using System.Buffers.Binary;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using WeArchive.Infrastructure.WeChat.Crypto;
using WeArchive.Infrastructure.WeChat.KeyAcquisition;

namespace WeArchive.Infrastructure.WeChat;

internal sealed class WeChatKeyUnavailableException(string message) : Exception(message);

/// <summary>Outcome of materializing one encrypted database as readable SQLite.</summary>
internal sealed record DecryptionOutcome(
    string PlaintextPath,
    int PageCount,
    int WalFramesApplied,
    int WalFramesRejected,
    bool WasPlaintext);

/// <summary>
/// Materializes WeChat's SQLCipher databases as ordinary SQLite files that
/// <c>Microsoft.Data.Sqlite</c> can query.
/// <para>
/// WeChat keeps the source databases open in WAL mode, so the main file alone may be a
/// checkpoint older than the client's state. Frames are therefore replayed from the
/// write-ahead log, but only those belonging to the log's current generation and only up
/// to the last committed transaction, exactly as SQLite itself would recover them. Frames
/// are verified against the page HMAC before use, so a stale or torn frame is rejected
/// rather than silently corrupting the image.
/// </para>
/// <para>
/// Plaintext copies are written to a per-run scratch directory under the user's local app
/// data and are deleted when the cache is disposed. WeChat's own files are never modified.
/// </para>
/// <para>
/// A path that is already plaintext — a preserved Raw Vault artifact, or an unencrypted source
/// file — is used in place and is never deleted by this cache, even when it is materialized again
/// after its fingerprint changed.
/// </para>
/// <para>
/// The captured-plaintext cache additionally treats every image it sees as immutable evidence and
/// opens it with SQLite's <c>immutable=1</c> semantics, so reading a preserved image neither locks
/// it nor creates <c>-wal</c>/<c>-shm</c> sidecars inside a published generation directory. A
/// preserved image is a self-contained checkpointed image whose committed state is entirely in the
/// main file, so ignoring any sidecar next to it cannot lose evidence (Issue #37).
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SqlCipherDatabaseCache : IDisposable
{
    private readonly WeChatKeySet? _keys;
    private readonly bool _preservedImagesAreImmutable;
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _scratchRoot;
    private bool _disposed;

    public SqlCipherDatabaseCache(WeChatKeySet keys)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _scratchRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WeArchive",
            "scratch",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_scratchRoot);
    }

    private SqlCipherDatabaseCache(bool preservedImagesAreImmutable)
    {
        _preservedImagesAreImmutable = preservedImagesAreImmutable;
    }

    /// <summary>Opens preserved plaintext database images without any source key path.</summary>
    public static SqlCipherDatabaseCache ForCapturedPlaintext() => new(preservedImagesAreImmutable: true);

    /// <summary>
    /// Opens one database read-only for querying, materializing it first when it is still encrypted.
    /// A preserved image is opened with immutable semantics so the read path cannot mutate the
    /// published generation it came from (see the type remarks).
    /// </summary>
    public SqliteConnection OpenReadOnly(string encryptedPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var plaintext = GetPlaintext(encryptedPath);
        var dataSource = plaintext.WasPlaintext && _preservedImagesAreImmutable
            ? ImmutableDataSource(plaintext.PlaintextPath)
            : plaintext.PlaintextPath;

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dataSource,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>A SQLite URI that makes the immutable-by-contract image readable without sidecars.</summary>
    private static string ImmutableDataSource(string path)
    {
        string uri;
        try
        {
            uri = new Uri(Path.GetFullPath(path)).AbsoluteUri;
        }
        catch (UriFormatException)
        {
            // A path the URI parser rejects is still usable in the plain form SQLite accepts.
            uri = "file:" + Path.GetFullPath(path).Replace('\\', '/');
        }

        return uri + "?immutable=1";
    }

    public DecryptionOutcome GetPlaintext(string encryptedPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var full = Path.GetFullPath(encryptedPath);
        var fingerprint = Fingerprint(full);

        if (_cache.TryGetValue(full, out var existing) && existing.Fingerprint == fingerprint)
        {
            return existing.Outcome;
        }

        var outcome = Materialize(full);
        if (existing is not null && !existing.Outcome.WasPlaintext)
        {
            // Only an image this cache materialized itself may be deleted. A plaintext image is
            // the caller's own file — a Raw Vault artifact on the captured path, or an
            // already-plain source file on the capture path — so the cache must never delete it.
            // SQLite opening such an image in place creates -wal/-shm sidecars, which changes the
            // fingerprint and used to make this line destroy preserved evidence (and could destroy
            // a source file), which is exactly what Issue #37's real-environment rebuild exposed.
            TryDelete(existing.Outcome.PlaintextPath);
        }

        _cache[full] = new CacheEntry(fingerprint, outcome);
        return outcome;
    }

    private DecryptionOutcome Materialize(string path)
    {
        var header = ReadRange(path, 0, SqlCipherPageCipher.PageSize);
        if (header.Length < SqlCipherPageCipher.PageSize)
        {
            throw new WeChatKeyUnavailableException($"'{Path.GetFileName(path)}' is too small to be a database.");
        }

        if (header.AsSpan(0, 15).SequenceEqual("SQLite format 3"u8))
        {
            // Not encrypted: this happens for a few auxiliary files. Use SQLite's own
            // read-only WAL recovery and backup implementation so committed plaintext WAL
            // evidence is part of the captured image without ever opening the source writable.
            // SQLite backup alone is not proof that the source WAL was fully understood: SQLite
            // may silently fall back to the main database after malformed or torn WAL evidence.
            var pageSize = ReadSqlitePageSize(header);
            var mainLength = new FileInfo(path).Length;
            if (mainLength == 0 || mainLength % pageSize != 0)
            {
                throw new WeChatKeyUnavailableException(
                    $"'{Path.GetFileName(path)}' has an incomplete SQLite page at the end of the source file.");
            }

            if (_scratchRoot is null)
            {
                return new DecryptionOutcome(path, checked((int)(mainLength / pageSize)), 0, 0, true);
            }

            var wal = ReadPlaintextWal(path, pageSize, checked((uint)(mainLength / pageSize)));
            if (wal.Rejected > 0)
            {
                throw new WeChatKeyUnavailableException(
                    $"'{Path.GetFileName(path)}' has invalid or incomplete plaintext SQLite WAL evidence.");
            }

            var snapshotPath = Path.Combine(_scratchRoot, Guid.NewGuid().ToString("n") + ".db");
            try
            {
                using var source = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString());
                source.Open();
                using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = snapshotPath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false,
                }.ToString());
                destination.Open();
                source.BackupDatabase(destination);
                ValidateImage(snapshotPath);
                var size = new FileInfo(snapshotPath).Length;
                return new DecryptionOutcome(snapshotPath, checked((int)(size / pageSize)), 0, 0, true);
            }
            catch (SqliteException ex)
            {
                TryDelete(snapshotPath);
                TryDelete(snapshotPath + "-wal");
                TryDelete(snapshotPath + "-shm");
                throw new WeChatKeyUnavailableException(
                    $"'{Path.GetFileName(path)}' could not be read as a consistent plaintext SQLite/WAL snapshot: {ex.Message}");
            }
        }

        if (_keys is null || !_keys.TryResolve(path, out var key))
        {
            throw new WeChatKeyUnavailableException(
                $"No verified database key is available for '{Path.GetFileName(path)}'.");
        }

        var target = Path.Combine(_scratchRoot!, Guid.NewGuid().ToString("n") + ".db");
        var pageCount = 0;
        var applied = 0;
        var rejected = 0;

        var main = ReadAll(path);
        if (main.Length == 0 || main.Length % SqlCipherPageCipher.PageSize != 0)
        {
            throw new WeChatKeyUnavailableException(
                $"'{Path.GetFileName(path)}' has an incomplete SQLCipher page at the end of the source file.");
        }

        var mainPages = main.Length / SqlCipherPageCipher.PageSize;
        var image = new byte[mainPages * SqlCipherPageCipher.PageSize];
        var macKey = SqlCipherPageCipher.DeriveMacKey(key, main.AsSpan(0, SqlCipherPageCipher.SaltSize));

        for (var i = 0; i < mainPages; i++)
        {
            var page = main.AsSpan(i * SqlCipherPageCipher.PageSize, SqlCipherPageCipher.PageSize);
            if (!SqlCipherPageCipher.VerifyPage(macKey, page, (uint)(i + 1)))
            {
                throw new WeChatKeyUnavailableException(
                    $"'{Path.GetFileName(path)}' page {i + 1} failed SQLCipher authentication.");
            }

            var plain = SqlCipherPageCipher.DecryptPage(key, page, (uint)(i + 1));
            plain.CopyTo(image, i * SqlCipherPageCipher.PageSize);
            pageCount++;
        }

        var (framesApplied, framesRejected) = ApplyWriteAheadLog(path, key, macKey, ref image);
        applied = framesApplied;
        rejected = framesRejected;

        File.WriteAllBytes(target, image);
        return new DecryptionOutcome(target, image.Length / SqlCipherPageCipher.PageSize, applied, rejected, false);
    }

    /// <summary>
    /// Replays committed frames from the write-ahead log. Only frames whose salt matches
    /// the log header (i.e. the current generation) and that appear before the last commit
    /// marker are applied.
    /// </summary>
    private static (int Applied, int Rejected) ApplyWriteAheadLog(
        string databasePath,
        byte[] key,
        byte[] macKey,
        ref byte[] image)
    {
        var walPath = databasePath + "-wal";
        if (!File.Exists(walPath))
        {
            return (0, 0);
        }

        var wal = ReadAll(walPath);
        var scan = ScanWriteAheadLog(
            wal,
            SqlCipherPageCipher.PageSize,
            checked((uint)(image.Length / SqlCipherPageCipher.PageSize)),
            (pageNumber, pageOffset) => SqlCipherPageCipher.VerifyPage(
                macKey, wal.AsSpan(pageOffset, SqlCipherPageCipher.PageSize), pageNumber));

        if (scan.LastCommitFrame < 0)
        {
            return (0, scan.Rejected);
        }

        var applied = 0;
        var transactionStart = 0;
        for (var commit = 0; commit <= scan.LastCommitFrame; commit++)
        {
            var databaseSize = scan.Frames[commit].DatabaseSize;
            if (databaseSize == 0)
            {
                continue;
            }

            // Resize at each validated commit: a truncate discards its removed pages before a
            // later growth can expose them again. The scan proves that every newly exposed page
            // has fresh evidence in this transaction, so resize cannot invent page content.
            Array.Resize(ref image, checked((int)((long)databaseSize * SqlCipherPageCipher.PageSize)));
            for (var frame = transactionStart; frame <= commit; frame++)
            {
                var (offset, pageNumber, _) = scan.Frames[frame];
                var page = wal.AsSpan(offset + 24, SqlCipherPageCipher.PageSize);
                var destination = (long)(pageNumber - 1) * SqlCipherPageCipher.PageSize;
                SqlCipherPageCipher.DecryptPage(key, page, pageNumber).CopyTo(image, destination);
                applied++;
            }

            transactionStart = commit + 1;
        }

        return (applied, scan.Rejected);
    }

    private static WalScanResult ReadPlaintextWal(string databasePath, int pageSize, uint mainPageCount)
    {
        var walPath = databasePath + "-wal";
        return File.Exists(walPath)
            ? ScanWriteAheadLog(ReadAll(walPath), pageSize, mainPageCount)
            : WalScanResult.Empty;
    }

    /// <summary>
    /// Validates the shared SQLite WAL framing/checksum/transaction protocol. The optional page
    /// validator adds SQLCipher page authentication; plaintext WALs use the same protocol checks
    /// without a page-authentication layer. SQLite backup remains responsible for materialization.
    /// </summary>
    private static WalScanResult ScanWriteAheadLog(
        byte[] wal,
        int expectedPageSize,
        uint mainPageCount,
        Func<uint, int, bool>? validatePage = null)
    {
        const int headerSize = 32;
        if (wal.Length == 0)
        {
            return WalScanResult.Empty;
        }

        if (wal.Length < headerSize)
        {
            return WalScanResult.Invalid;
        }

        var magic = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(0, 4));
        if (magic is not (0x377F0682 or 0x377F0683) ||
            BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(4, 4)) != 3_007_000 ||
            BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(8, 4)) != expectedPageSize)
        {
            return WalScanResult.Invalid;
        }

        var checksumBigEndian = magic == 0x377F0683;
        if (!TryWalChecksum(wal.AsSpan(0, 24), checksumBigEndian, 0, 0, out var checksum0, out var checksum1) ||
            checksum0 != BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(24, 4)) ||
            checksum1 != BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(28, 4)))
        {
            return WalScanResult.Invalid;
        }

        var frameSize = expectedPageSize + 24;
        var salt1 = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(16, 4));
        var salt2 = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(20, 4));
        var hasIncompleteTail = (wal.Length - headerSize) % frameSize != 0;
        var frameCount = (wal.Length - headerSize) / frameSize;
        var rollingChecksum0 = checksum0;
        var rollingChecksum1 = checksum1;
        var committedPageCount = mainPageCount;
        uint transactionMaxPage = 0;
        var transactionNewPages = new HashSet<uint>();
        var lastCommitFrame = -1;
        var rejected = 0;
        var reachedOlderGeneration = false;
        var frames = new List<(int Offset, uint PageNumber, uint DatabaseSize)>();

        for (var frame = 0; frame < frameCount; frame++)
        {
            var offset = headerSize + (frame * frameSize);
            if (BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(offset + 8, 4)) != salt1 ||
                BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(offset + 12, 4)) != salt2)
            {
                // SQLite reuses WAL files without truncating them. The first stale-salt frame is
                // the logical end of this generation; bytes after it are old storage, not a tail.
                reachedOlderGeneration = true;
                break;
            }

            if (!TryWalChecksum(
                    wal.AsSpan(offset, 8), checksumBigEndian, rollingChecksum0, rollingChecksum1,
                    out rollingChecksum0, out rollingChecksum1) ||
                !TryWalChecksum(
                    wal.AsSpan(offset + 24, expectedPageSize), checksumBigEndian,
                    rollingChecksum0, rollingChecksum1, out rollingChecksum0, out rollingChecksum1) ||
                rollingChecksum0 != BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(offset + 16, 4)) ||
                rollingChecksum1 != BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(offset + 20, 4)))
            {
                rejected++;
                break;
            }

            var pageNumber = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(offset, 4));
            if (pageNumber == 0 || (validatePage is not null && !validatePage(pageNumber, offset + 24)))
            {
                rejected++;
                break;
            }

            var databaseSize = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(offset + 4, 4));
            transactionMaxPage = Math.Max(transactionMaxPage, pageNumber);
            if (pageNumber > committedPageCount)
            {
                transactionNewPages.Add(pageNumber);
            }

            if (databaseSize != 0)
            {
                // A valid commit must explain every page added since the previous logical size.
                // Distinct page evidence matters: repeated writes cannot fill a missing page,
                // and pages discarded by an earlier truncate cannot support later regrowth.
                if (transactionMaxPage > databaseSize ||
                    (databaseSize > committedPageCount &&
                     databaseSize - committedPageCount != (uint)transactionNewPages.Count))
                {
                    rejected++;
                    break;
                }

                committedPageCount = databaseSize;
                lastCommitFrame = frame;
                transactionMaxPage = 0;
                transactionNewPages.Clear();
            }

            frames.Add((offset, pageNumber, databaseSize));
        }

        // A short physical tail is ambiguous only while it follows the current generation. Once
        // a full stale-salt frame marks logical EOF, short leftovers belong to the prior generation.
        if (hasIncompleteTail && !reachedOlderGeneration)
        {
            rejected++;
        }

        return new WalScanResult(frames, lastCommitFrame, committedPageCount, rejected);
    }

    private static int ReadSqlitePageSize(ReadOnlySpan<byte> header)
    {
        var encoded = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(header.Slice(16, 2));
        var pageSize = encoded == 1 ? 65_536 : encoded;
        if (pageSize is < 512 or > 65_536 || (pageSize & (pageSize - 1)) != 0)
        {
            throw new WeChatKeyUnavailableException("Plaintext SQLite source has an invalid page size.");
        }

        return pageSize;
    }

    private static void ValidateImage(string path)
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            using var reader = command.ExecuteReader();
            var foundResult = false;
            while (reader.Read())
            {
                foundResult = true;
                if (!string.Equals(reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new WeChatKeyUnavailableException(
                        $"Materialized SQLite image '{Path.GetFileName(path)}' failed structural quick-check.");
                }
            }

            if (!foundResult)
            {
                throw new WeChatKeyUnavailableException(
                    $"Materialized SQLite image '{Path.GetFileName(path)}' returned no structural quick-check result.");
            }
        }
        catch (SqliteException ex)
        {
            throw new WeChatKeyUnavailableException(
                $"Materialized SQLite image '{Path.GetFileName(path)}' failed structural quick-check: {ex.Message}");
        }
    }

    private static bool TryWalChecksum(
        ReadOnlySpan<byte> bytes,
        bool bigEndian,
        uint input0,
        uint input1,
        out uint output0,
        out uint output1)
    {
        output0 = input0;
        output1 = input1;
        if (bytes.Length % 8 != 0)
        {
            return false;
        }

        for (var i = 0; i < bytes.Length; i += 8)
        {
            var first = bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(i, 4))
                : BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(i, 4));
            var second = bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(i + 4, 4))
                : BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(i + 4, 4));
            output0 = unchecked(output0 + first + output1);
            output1 = unchecked(output1 + second + output0);
        }

        return true;
    }

    private static string Fingerprint(string path)
    {
        var main = new FileInfo(path);
        var wal = new FileInfo(path + "-wal");
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{main.Length}:{main.LastWriteTimeUtc.Ticks}:{(wal.Exists ? wal.Length : 0)}:{(wal.Exists ? wal.LastWriteTimeUtc.Ticks : 0)}");
    }

    private static byte[] ReadAll(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[stream.Length];
        stream.ReadExactly(buffer, 0, buffer.Length);
        return buffer;
    }

    private static byte[] ReadRange(string path, long offset, int length)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var available = (int)Math.Min(length, Math.Max(0, stream.Length - offset));
        var buffer = new byte[Math.Max(available, 0)];
        if (available > 0)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            stream.ReadExactly(buffer, 0, available);
        }

        return buffer;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Scratch cleanup is best effort.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cache.Clear();

        try
        {
            if (_scratchRoot is not null && Directory.Exists(_scratchRoot))
            {
                Directory.Delete(_scratchRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Leaving an empty scratch directory behind is harmless.
        }
    }

    private sealed record CacheEntry(string Fingerprint, DecryptionOutcome Outcome);

    private sealed record WalScanResult(
        IReadOnlyList<(int Offset, uint PageNumber, uint DatabaseSize)> Frames,
        int LastCommitFrame,
        uint CommittedPageCount,
        int Rejected)
    {
        public static WalScanResult Empty { get; } = new([], -1, 0, 0);

        public static WalScanResult Invalid { get; } = new([], -1, 0, 1);
    }
}
