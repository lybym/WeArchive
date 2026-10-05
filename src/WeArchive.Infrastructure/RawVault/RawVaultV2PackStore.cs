using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using WeArchive.Core.RawVault;

namespace WeArchive.Infrastructure.RawVault;

/// <summary>
/// Account-local immutable pack store with a disposable SQLite location index. The index can
/// always be reconstructed from sealed packs and is never preservation authority.
/// </summary>
internal sealed class RawVaultV2PackStore
{
    private const int TargetPackBytes = 64 * 1024 * 1024;
    private readonly string _root;
    private readonly string _packs;
    private readonly string _index;
    private readonly string _lockPath;
    private readonly bool _compressObjects;
    private readonly int _targetPackBytes;
    private readonly Action? _beforePackPublish;
    internal RawCaptureStorageCounters Counters { get; private set; } = new();

    internal RawVaultV2PackStore(string accountDirectory, bool compressObjects = true, int targetPackBytes = TargetPackBytes,
        Action? beforePackPublish = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountDirectory);
        if (targetPackBytes < 72 + 48) throw new ArgumentOutOfRangeException(nameof(targetPackBytes));
        _root = Path.Combine(accountDirectory, "objects"); _packs = Path.Combine(_root, "packs");
        _index = Path.Combine(_root, "lookup.sqlite"); _lockPath = Path.Combine(_root, "writer.lock");
        _compressObjects = compressObjects;
        _targetPackBytes = targetPackBytes;
        _beforePackPublish = beforePackPublish;
        Directory.CreateDirectory(_packs);
        EnsureIndex();
    }

    internal void Put(IEnumerable<RawVaultV2Format.StoredObject> values, CancellationToken cancellationToken,
        Action? afterPackPublished = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        EnsureIndex();
        using var writerLock = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var pendingPack = new List<(RawVaultV2Format.StoredObject Value, byte[] Bytes)>();
        var pendingKeys = new HashSet<string>(StringComparer.Ordinal);
        var pendingBytes = 16;
        foreach (var batch in values.Chunk(400))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = FindExisting(batch);
            var missing = batch.Where(value => !existing.Contains(Key(value)) && !pendingKeys.Contains(Key(value)))
                .GroupBy(Key, StringComparer.Ordinal).Select(group => group.First()).ToArray();
            if (missing.Length == 0) continue;
            var records = missing.Select(value => (Value: value, Bytes: RawVaultV2Format.EncodeRecord(value, _compressObjects))).ToArray();
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pendingPack.Count > 0 && pendingBytes + record.Bytes.Length + 56 > _targetPackBytes)
                {
                    FlushPack();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                pendingPack.Add(record);
                pendingKeys.Add(Key(record.Value));
                pendingBytes = checked(pendingBytes + record.Bytes.Length);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        FlushPack();

        void FlushPack()
        {
            if (pendingPack.Count == 0) return;
            PublishPack(pendingPack.ToArray(), cancellationToken);
            afterPackPublished?.Invoke();
            pendingPack.Clear();
            pendingBytes = 16;
        }
    }

    internal RawVaultV2Format.Artifact PutArtifact(ReadOnlySpan<byte> content, int blockSize, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var artifact = RawVaultV2Format.BuildArtifact(content, blockSize);
        Put(artifact.Objects, cancellationToken);
        return artifact;
    }

    /// <summary>Builds and stores one artifact using a single fixed-size input buffer.</summary>
    internal RawVaultV2Format.Artifact PutArtifact(Stream input, long size, int blockSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead) throw new ArgumentException("Raw Vault v2 artifact input must be readable.", nameof(input));
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
        if (!RawVaultV2Format.SupportedBlockSizes.Contains(blockSize)) throw new ArgumentOutOfRangeException(nameof(blockSize));

        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var root = Array.Empty<byte>();
        ulong blockCount = 0;
        Put(CreateObjects(), cancellationToken);
        return new RawVaultV2Format.Artifact(root, size, blockCount,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), [], blockSize);

        IEnumerable<RawVaultV2Format.StoredObject> CreateObjects()
        {
            var leaves = new List<RawVaultV2Format.MapEntry>(RawVaultV2Format.Fanout);
            var leafBytes = 0UL;
            var level = new List<MapReference>();
            var remaining = size;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = checked((int)Math.Min(blockSize, remaining));
                var block = new byte[length];
                input.ReadExactly(block);
                hash.AppendData(block);
                var digest = RawVaultV2Format.DataDigest(block);
                leaves.Add(new(digest, checked((uint)length)));
                leafBytes = checked(leafBytes + (ulong)length);
                blockCount = checked(blockCount + 1);
                remaining -= length;
                yield return new(1, digest, block);

                if (leaves.Count == RawVaultV2Format.Fanout)
                {
                    var mapObject = MakeLeaf(leaves, leafBytes, level);
                    yield return mapObject;
                    leaves.Clear(); leafBytes = 0;
                }
            }
            if (input.ReadByte() != -1) throw new InvalidDataException("Raw Vault v2 artifact input exceeds its declared size.");
            if (leaves.Count > 0 || level.Count == 0)
                yield return MakeLeaf(leaves, leafBytes, level);

            while (level.Count > 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var next = new List<MapReference>((level.Count + RawVaultV2Format.Fanout - 1) / RawVaultV2Format.Fanout);
                foreach (var children in level.Chunk(RawVaultV2Format.Fanout))
                {
                    var nodeLevel = checked((byte)(children[0].Level + 1));
                    if (nodeLevel > 13 || children.Any(child => child.Level + 1 != nodeLevel))
                        throw new InvalidDataException("Raw Vault v2 map tree exceeds its supported depth or is unbalanced.");
                    var childEntries = children.Select(child => new RawVaultV2Format.ChildEntry(child.Digest, child.Blocks, child.Bytes)).ToArray();
                    var bytes = RawVaultV2Format.EncodeInternal(nodeLevel, childEntries);
                    var digest = RawVaultV2Format.MapDigest(bytes);
                    next.Add(new(digest, childEntries.Aggregate<RawVaultV2Format.ChildEntry, ulong>(0, (n, child) => checked(n + child.Blocks)),
                        childEntries.Aggregate<RawVaultV2Format.ChildEntry, ulong>(0, (n, child) => checked(n + child.Bytes)), nodeLevel));
                    yield return new(2, digest, bytes);
                }
                level = next;
            }
            root = level[0].Digest;
        }

        static RawVaultV2Format.StoredObject MakeLeaf(IReadOnlyList<RawVaultV2Format.MapEntry> entries, ulong bytes, List<MapReference> level)
        {
            var canonical = RawVaultV2Format.EncodeLeaf(entries);
            var digest = RawVaultV2Format.MapDigest(canonical);
            level.Add(new(digest, checked((ulong)entries.Count), bytes, 0));
            return new(2, digest, canonical);
        }
    }

    /// <summary>Streams a verified artifact to the caller without buffering the reconstructed file.</summary>
    internal void MaterializeTo(RawVaultV2Format.Artifact artifact, Stream output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(output);
        if (!RawVaultV2Format.SupportedBlockSizes.Contains(artifact.BlockSize) || artifact.Size < 0 ||
            artifact.Root.Length != 32 || artifact.Sha256.Length != 64 || artifact.Sha256.Any(c => !Uri.IsHexDigit(c)) ||
            artifact.Sha256 != artifact.Sha256.ToLowerInvariant() ||
            artifact.BlockCount != (artifact.Size == 0 ? 0UL : checked((ulong)((artifact.Size + artifact.BlockSize - 1) / artifact.BlockSize))))
            throw new InvalidDataException("Invalid Raw Vault v2 artifact descriptor.");

        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var path = new HashSet<string>(StringComparer.Ordinal);
        long written = 0;
        Visit(artifact.Root, expectedLevel: null, artifact.BlockCount, checked((ulong)artifact.Size));
        if (written != artifact.Size || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != artifact.Sha256)
            throw new InvalidDataException("Raw Vault v2 reconstructed artifact integrity check failed.");

        void Visit(byte[] digest, byte? expectedLevel, ulong expectedBlocks, ulong expectedBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Convert.ToHexString(digest);
            if (!path.Add(key)) throw new InvalidDataException("Cycle in Raw Vault v2 map tree.");
            var node = RawVaultV2Format.DecodeNode(Get(2, digest).Bytes);
            if (node.Level > 13 || node.Level != expectedLevel && expectedLevel is not null ||
                node.Blocks != expectedBlocks || node.Bytes != expectedBytes)
                throw new InvalidDataException("Raw Vault v2 map hierarchy is inconsistent.");
            if (node.Type == 0)
            {
                var blocks = GetMany(1, node.Leaves.Select(entry => entry.Digest).ToArray());
                for (var i = 0; i < node.Leaves.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = node.Leaves[i];
                    var block = blocks[i].Bytes;
                    var blockIndex = checked((ulong)(written / artifact.BlockSize));
                    var expectedLength = blockIndex + 1 == artifact.BlockCount
                        ? artifact.Size - checked((long)(blockIndex * (ulong)artifact.BlockSize))
                        : artifact.BlockSize;
                    if (block.Length != entry.Length || block.Length != expectedLength)
                        throw new InvalidDataException("Raw Vault v2 block length disagrees with logical position.");
                    output.Write(block);
                    hash.AppendData(block);
                    written = checked(written + block.Length);
                }
            }
            else
            {
                foreach (var child in node.Children)
                    Visit(child.Digest, checked((byte)(node.Level - 1)), child.Blocks, child.Bytes);
            }
            path.Remove(key);
        }
    }

    internal RawVaultV2Format.StoredObject Get(byte kind, byte[] digest)
    {
        return GetMany(kind, [digest])[0];
    }

    private IReadOnlyList<RawVaultV2Format.StoredObject> GetMany(byte kind, IReadOnlyList<byte[]> digests)
    {
        if (digests.Count == 0) return [];
        try
        {
            if (TryReadMany(kind, digests, out var indexedValues)) return indexedValues;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException or IOException)
        {
            /* Rebuild derived locations from authoritative packs before the final lookup. */
            _ = ex;
        }
        RebuildIndex();
        if (TryReadMany(kind, digests, out var values)) return values;
        throw new InvalidDataException("One or more Raw Vault v2 objects are not present in any sealed pack.");
    }

    private void PublishPack((RawVaultV2Format.StoredObject Value, byte[] Bytes)[] records, CancellationToken cancellationToken)
    {
        var packId = Guid.NewGuid().ToString("N"); var fileName = packId + ".rvpk";
        var temp = Path.Combine(_packs, packId + ".staging"); var final = Path.Combine(_packs, fileName);
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Write("RVPK0001"u8); stream.Write([1, 0, 0, 0, 0, 0, 0, 0]);
            foreach (var record in records) stream.Write(record.Bytes);
            stream.Flush();
            var contentLength = checked((ulong)stream.Length); stream.Position = 0;
            var checksum = System.Security.Cryptography.SHA256.HashData(stream);
            stream.Position = checked((long)contentLength); stream.Write("RVPKEND1"u8);
            Span<byte> footer = stackalloc byte[48]; BinaryPrimitives.WriteUInt64LittleEndian(footer, checked((ulong)records.Length));
            BinaryPrimitives.WriteUInt64LittleEndian(footer[8..], contentLength); checksum.CopyTo(footer[16..]); stream.Write(footer);
        }
        _ = RawVaultV2Format.ReadPack(File.ReadAllBytes(temp));
        _beforePackPublish?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(temp, final);
        Counters = Counters with
        {
            NewDataBlocks = Counters.NewDataBlocks + records.LongCount(r => r.Value.Kind == 1),
            NewDataBytes = Counters.NewDataBytes + records.Where(r => r.Value.Kind == 1).Sum(r => (long)r.Value.Bytes.Length),
            NewMapNodes = Counters.NewMapNodes + records.LongCount(r => r.Value.Kind == 2),
            NewPacks = Counters.NewPacks + 1,
            NewPackBytes = Counters.NewPackBytes + new FileInfo(final).Length,
        };
        using var connection = OpenIndex(); using var transaction = connection.BeginTransaction();
        long offset = 16;
        foreach (var record in records)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO objects(kind,digest,pack_file,record_offset,record_length) VALUES($kind,$digest,$pack,$offset,$length)";
            command.Parameters.AddWithValue("$kind", record.Value.Kind); command.Parameters.AddWithValue("$digest", record.Value.Digest);
            command.Parameters.AddWithValue("$pack", fileName); command.Parameters.AddWithValue("$offset", offset); command.Parameters.AddWithValue("$length", record.Bytes.Length);
            command.ExecuteNonQuery(); offset = checked(offset + record.Bytes.Length);
        }
        transaction.Commit();
    }

    private HashSet<string> FindExisting(IReadOnlyList<RawVaultV2Format.StoredObject> values)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        using var connection = OpenIndex();
        using var command = connection.CreateCommand();
        var predicates = new List<string>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            predicates.Add($"(kind=$kind{i} AND digest=$digest{i})");
            command.Parameters.AddWithValue($"$kind{i}", values[i].Kind);
            command.Parameters.AddWithValue($"$digest{i}", values[i].Digest);
        }
        command.CommandText = $"SELECT kind,digest FROM objects WHERE {string.Join(" OR ", predicates)}";
        using var reader = command.ExecuteReader();
        while (reader.Read()) found.Add(reader.GetByte(0) + ":" + Convert.ToHexString((byte[])reader[1]));
        return found;
    }

    private bool TryReadMany(byte kind, IReadOnlyList<byte[]> digests, out IReadOnlyList<RawVaultV2Format.StoredObject> values)
    {
        values = [];
        var uniqueDigests = digests.GroupBy(Convert.ToHexString, StringComparer.Ordinal).Select(group => group.First()).ToArray();
        var locators = new Dictionary<string, (string File, long Offset, int Length)>(StringComparer.Ordinal);
        using (var connection = OpenIndex())
        using (var command = connection.CreateCommand())
        {
            var predicates = new List<string>(uniqueDigests.Length);
            for (var i = 0; i < uniqueDigests.Length; i++)
            {
                predicates.Add($"(kind=$kind{i} AND digest=$digest{i})");
                command.Parameters.AddWithValue($"$kind{i}", kind);
                command.Parameters.AddWithValue($"$digest{i}", uniqueDigests[i]);
            }
            command.CommandText = $"SELECT digest,pack_file,record_offset,record_length FROM objects WHERE {string.Join(" OR ", predicates)}";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var digest = Convert.ToHexString((byte[])reader[0]);
                var file = reader.GetString(1); var offset = reader.GetInt64(2); var length = reader.GetInt32(3);
                if (Path.GetFileName(file) != file || offset < 16 || length < 48)
                    throw new InvalidDataException("Invalid Raw Vault v2 index locator.");
                locators[digest] = (file, offset, length);
            }
        }
        if (locators.Count != uniqueDigests.Length) return false;

        var decodedByDigest = new Dictionary<string, RawVaultV2Format.StoredObject>(StringComparer.Ordinal);
        var streams = new Dictionary<string, FileStream>(StringComparer.Ordinal);
        try
        {
            foreach (var digest in uniqueDigests)
            {
                var key = Convert.ToHexString(digest);
                var locator = locators[key];
                if (!streams.TryGetValue(locator.File, out var stream))
                {
                    stream = new FileStream(Path.Combine(_packs, locator.File), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    streams.Add(locator.File, stream);
                }
                if (locator.Offset > stream.Length - 56 - locator.Length)
                    throw new InvalidDataException("Raw Vault v2 indexed record is outside its pack bounds.");
                stream.Position = locator.Offset;
                var record = new byte[locator.Length]; stream.ReadExactly(record);
                var decoded = RawVaultV2Format.DecodeRecord(record);
                if (decoded.Kind != kind || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(decoded.Digest, digest))
                    throw new InvalidDataException("Raw Vault v2 index entry points to a different object.");
                decodedByDigest.Add(key, decoded);
            }
        }
        finally
        {
            foreach (var stream in streams.Values) stream.Dispose();
        }
        values = digests.Select(digest => decodedByDigest[Convert.ToHexString(digest)]).ToArray();
        return true;
    }

    private void EnsureIndex()
    {
        var existed = File.Exists(_index);
        try
        {
            bool healthy;
            using (var connection = OpenIndex())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA quick_check";
                healthy = string.Equals(command.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase);
            }
            if (!healthy || !existed) RebuildIndex();
            else { using var connection = OpenIndex(); CreateSchema(connection); }
        }
        catch (SqliteException) { RebuildIndex(); }
    }

    private void RebuildIndex()
    {
        using var writerLock = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(_index)) File.Delete(_index); // Derived index only; packs remain authoritative.
        using var connection = OpenIndex(); CreateSchema(connection);
        using var transaction = connection.BeginTransaction();
        foreach (var path in Directory.EnumerateFiles(_packs, "*.rvpk", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path); var bytes = File.ReadAllBytes(path);
            var objects = RawVaultV2Format.ReadPack(bytes); var offset = 16;
            foreach (var value in objects)
            {
                // Existing compression is representation-only; scan offsets from record framing, not by re-encoding.
                var storedLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 12));
                var recordLength = checked(48 + (int)storedLength);
                AddIndexRow(connection, transaction, value, name, offset, recordLength); offset = checked(offset + recordLength);
            }
        }
        transaction.Commit();
    }

    private static void AddIndexRow(SqliteConnection connection, SqliteTransaction transaction,
        RawVaultV2Format.StoredObject value, string pack, long offset, int length)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO objects(kind,digest,pack_file,record_offset,record_length) VALUES($kind,$digest,$pack,$offset,$length)";
        command.Parameters.AddWithValue("$kind", value.Kind); command.Parameters.AddWithValue("$digest", value.Digest);
        command.Parameters.AddWithValue("$pack", pack); command.Parameters.AddWithValue("$offset", offset); command.Parameters.AddWithValue("$length", length);
        command.ExecuteNonQuery();
    }

    private static void CreateSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS objects(kind INTEGER NOT NULL,digest BLOB NOT NULL,pack_file TEXT NOT NULL,record_offset INTEGER NOT NULL,record_length INTEGER NOT NULL,PRIMARY KEY(kind,digest)) WITHOUT ROWID";
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenIndex()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _index, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private, Pooling = false }.ToString());
        connection.Open(); return connection;
    }

    private static string Key(RawVaultV2Format.StoredObject value) => value.Kind + ":" + Convert.ToHexString(value.Digest);

    private sealed record MapReference(byte[] Digest, ulong Blocks, ulong Bytes, byte Level);
}
