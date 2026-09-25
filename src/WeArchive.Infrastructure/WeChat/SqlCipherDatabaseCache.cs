using System.Buffers.Binary;
using System.Runtime.Versioning;
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
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SqlCipherDatabaseCache : IDisposable
{
    private readonly WeChatKeySet? _keys;
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

    private SqlCipherDatabaseCache()
    {
    }

    /// <summary>Opens preserved plaintext database images without any source key path.</summary>
    public static SqlCipherDatabaseCache ForCapturedPlaintext() => new();

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
        if (existing is not null)
        {
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
            // Not encrypted: this happens for a few auxiliary files.
            return new DecryptionOutcome(path, header.Length / SqlCipherPageCipher.PageSize, 0, 0, true);
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
        var mainPages = main.Length / SqlCipherPageCipher.PageSize;
        var image = new byte[mainPages * SqlCipherPageCipher.PageSize];
        var macKey = SqlCipherPageCipher.DeriveMacKey(key, main.AsSpan(0, SqlCipherPageCipher.SaltSize));

        for (var i = 0; i < mainPages; i++)
        {
            var page = main.AsSpan(i * SqlCipherPageCipher.PageSize, SqlCipherPageCipher.PageSize);
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
        var frameSize = SqlCipherPageCipher.PageSize + 24;
        if (wal.Length < 32 + frameSize)
        {
            return (0, 0);
        }

        var pageSize = (int)BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(8, 4));
        if (pageSize != SqlCipherPageCipher.PageSize)
        {
            return (0, 0);
        }

        var salt1 = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(16, 4));
        var salt2 = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(20, 4));

        var frameCount = (wal.Length - 32) / frameSize;
        uint committedPageCount = 0;
        var lastCommitFrame = -1;

        for (var frame = 0; frame < frameCount; frame++)
        {
            var offset = 32 + (frame * frameSize);
            if (BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(offset + 8, 4)) != salt1
                || BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(offset + 12, 4)) != salt2)
            {
                // The ring buffer still holds older generations beyond this point.
                break;
            }

            var databaseSize = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(offset + 4, 4));
            if (databaseSize != 0)
            {
                committedPageCount = databaseSize;
                lastCommitFrame = frame;
            }
        }

        if (lastCommitFrame < 0)
        {
            return (0, 0);
        }

        if (committedPageCount > 0)
        {
            var required = (long)committedPageCount * SqlCipherPageCipher.PageSize;
            if (required != image.LongLength)
            {
                Array.Resize(ref image, (int)required);
            }
        }

        var applied = 0;
        var rejected = 0;
        for (var frame = 0; frame <= lastCommitFrame; frame++)
        {
            var offset = 32 + (frame * frameSize);
            var pageNumber = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(offset, 4));
            if (pageNumber == 0)
            {
                break;
            }

            var page = wal.AsSpan(offset + 24, SqlCipherPageCipher.PageSize);
            if (!SqlCipherPageCipher.VerifyPage(macKey, page, pageNumber))
            {
                rejected++;
                continue;
            }

            var destination = (long)(pageNumber - 1) * SqlCipherPageCipher.PageSize;
            if (destination + SqlCipherPageCipher.PageSize > image.LongLength)
            {
                rejected++;
                continue;
            }

            SqlCipherPageCipher.DecryptPage(key, page, pageNumber).CopyTo(image, destination);
            applied++;
        }

        return (applied, rejected);
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
}
