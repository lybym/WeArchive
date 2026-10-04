using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ZstdSharp;

namespace WeArchive.Infrastructure.RawVault;

/// <summary>Canonical encodings for the fixed-block Raw Vault v2 object format.</summary>
internal static class RawVaultV2Format
{
    internal const int Fanout = 32;
    internal const int HeaderSize = 28;
    internal const int LeafEntrySize = 36;
    internal const int InternalEntrySize = 48;
    internal const int ProvisionalWriterDefaultBlockSize = 4096;
    internal static readonly int[] SupportedBlockSizes = [4096, 8192, 16384, 32768, 65536];
    private static readonly byte[] DataDomain = Encoding.UTF8.GetBytes("wearchive/raw-vault/data/v1\0");
    private static readonly byte[] MapDomain = Encoding.UTF8.GetBytes("wearchive/raw-vault/map/v1\0");

    internal sealed record StoredObject(byte Kind, byte[] Digest, byte[] Bytes);
    internal sealed record MapEntry(byte[] Digest, uint Length);
    internal sealed record ChildEntry(byte[] Digest, ulong Blocks, ulong Bytes);
    internal sealed record Node(byte Type, byte Level, ulong Blocks, ulong Bytes,
        IReadOnlyList<MapEntry> Leaves, IReadOnlyList<ChildEntry> Children);
    internal sealed record Artifact(byte[] Root, long Size, ulong BlockCount, string Sha256,
        IReadOnlyList<StoredObject> Objects, int BlockSize);

    internal static Artifact BuildArtifact(ReadOnlySpan<byte> content, int blockSize, bool compress = true)
    {
        if (!SupportedBlockSizes.Contains(blockSize)) throw new ArgumentOutOfRangeException(nameof(blockSize), "Unsupported Raw Vault v2 block size.");
        var objects = new List<StoredObject>(); var blocks = new List<MapEntry>();
        for (var offset = 0; offset < content.Length; offset += blockSize)
        {
            var length = Math.Min(blockSize, content.Length - offset);
            var bytes = content.Slice(offset, length).ToArray(); var digest = DataDigest(bytes);
            objects.Add(new(1, digest, bytes)); blocks.Add(new(digest, checked((uint)length)));
        }
        objects.AddRange(BuildTree(blocks, out var root));
        _ = compress; // Compression is selected while records are written; identity remains uncompressed.
        var unique = objects.GroupBy(o => o.Kind + ":" + Convert.ToHexString(o.Digest), StringComparer.Ordinal).Select(group => group.First()).ToArray();
        return new(root, content.Length, checked((ulong)blocks.Count), Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), unique, blockSize);
    }

    internal static byte[] Materialize(Artifact artifact, IReadOnlyDictionary<string, byte[]> objectBytes)
    {
        using var output = new MemoryStream();
        MaterializeTo(artifact, objectBytes, output);
        return output.ToArray();
    }

    internal static void MaterializeTo(Artifact artifact, IReadOnlyDictionary<string, byte[]> objectBytes, Stream output)
    {
        ArgumentNullException.ThrowIfNull(artifact); ArgumentNullException.ThrowIfNull(objectBytes); ArgumentNullException.ThrowIfNull(output);
        if (!SupportedBlockSizes.Contains(artifact.BlockSize) || artifact.Size < 0 || artifact.BlockCount != (artifact.Size == 0 ? 0UL : checked((ulong)((artifact.Size + artifact.BlockSize - 1) / artifact.BlockSize))))
            throw new InvalidDataException("Invalid Raw Vault v2 artifact descriptor.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (artifact.Root.Length != 32 || artifact.Sha256.Length != 64 || artifact.Sha256.Any(c => !Uri.IsHexDigit(c)) || artifact.Sha256 != artifact.Sha256.ToLowerInvariant())
            throw new InvalidDataException("Invalid Raw Vault v2 artifact digest descriptor.");
        var visiting = new HashSet<string>(StringComparer.Ordinal); long written = 0;
        Visit(artifact.Root, expectedLevel: null, artifact.BlockCount, checked((ulong)artifact.Size));
        if (written != artifact.Size || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != artifact.Sha256)
            throw new InvalidDataException("Raw Vault v2 reconstructed artifact integrity check failed.");

        void Visit(byte[] digest, byte? expectedLevel, ulong expectedBlocks, ulong expectedBytes)
        {
            var key = Convert.ToHexString(digest);
            if (!visiting.Add(key)) throw new InvalidDataException("Cycle in Raw Vault v2 map tree.");
            if (!objectBytes.TryGetValue("2:" + key, out var mapBytes) || !CryptographicOperations.FixedTimeEquals(MapDigest(mapBytes), digest))
                throw new InvalidDataException("Missing or invalid Raw Vault v2 map object.");
            var node = DecodeNode(mapBytes);
            if (node.Level > 13 || node.Blocks != expectedBlocks || node.Bytes != expectedBytes)
                throw new InvalidDataException("Raw Vault v2 child totals disagree with the parent map entry.");
            if (expectedLevel is not null && node.Level != expectedLevel) throw new InvalidDataException("Invalid Raw Vault v2 child level.");
            if (key == Convert.ToHexString(artifact.Root) && (node.Blocks != artifact.BlockCount || node.Bytes != checked((ulong)artifact.Size)))
                throw new InvalidDataException("Raw Vault v2 root totals disagree with the artifact descriptor.");
            if (node.Type == 0)
            {
                foreach (var entry in node.Leaves)
                {
                    var blockKey = Convert.ToHexString(entry.Digest);
                    if (!objectBytes.TryGetValue("1:" + blockKey, out var block) || !CryptographicOperations.FixedTimeEquals(DataDigest(block), entry.Digest) || block.Length != entry.Length)
                        throw new InvalidDataException("Missing or invalid Raw Vault v2 data object.");
                    var blockIndex = checked((ulong)(written / artifact.BlockSize));
                    var expectedLength = blockIndex + 1 == artifact.BlockCount ? artifact.Size - checked((long)(blockIndex * (ulong)artifact.BlockSize)) : artifact.BlockSize;
                    if (block.Length != expectedLength) throw new InvalidDataException("Raw Vault v2 block length disagrees with logical position.");
                    output.Write(block); hash.AppendData(block); written = checked(written + block.Length);
                }
            }
            else foreach (var child in node.Children) Visit(child.Digest, checked((byte)(node.Level - 1)), child.Blocks, child.Bytes);
            visiting.Remove(key);
        }
    }

    internal static byte[] DataDigest(ReadOnlySpan<byte> data)
    {
        var canonical = new byte[DataDomain.Length + 8 + data.Length];
        DataDomain.CopyTo(canonical, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(canonical.AsSpan(DataDomain.Length), checked((ulong)data.Length));
        data.CopyTo(canonical.AsSpan(DataDomain.Length + 8));
        return SHA256.HashData(canonical);
    }

    internal static byte[] MapDigest(ReadOnlySpan<byte> node)
    {
        var canonical = new byte[MapDomain.Length + node.Length];
        MapDomain.CopyTo(canonical, 0);
        node.CopyTo(canonical.AsSpan(MapDomain.Length));
        return SHA256.HashData(canonical);
    }

    internal static byte[] EncodeLeaf(IReadOnlyList<MapEntry> entries)
    {
        if (entries.Count > Fanout) throw new InvalidDataException("Raw Vault v2 map fanout exceeded.");
        var result = new byte[HeaderSize + entries.Count * LeafEntrySize];
        WriteHeader(result, 0, checked((ulong)entries.Count), entries.Aggregate<MapEntry, ulong>(0, (n, e) => checked(n + e.Length)));
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Digest.Length != 32 || entries[i].Length == 0) throw new InvalidDataException("Invalid Raw Vault v2 leaf entry.");
            var offset = HeaderSize + i * LeafEntrySize;
            entries[i].Digest.CopyTo(result, offset);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset + 32), entries[i].Length);
        }
        return result;
    }

    internal static byte[] EncodeInternal(byte level, IReadOnlyList<ChildEntry> children)
    {
        if (level == 0 || children.Count is < 1 or > Fanout) throw new InvalidDataException("Invalid Raw Vault v2 internal node.");
        var result = new byte[HeaderSize + children.Count * InternalEntrySize];
        var blocks = children.Aggregate<ChildEntry, ulong>(0, (n, e) => checked(n + e.Blocks));
        var bytes = children.Aggregate<ChildEntry, ulong>(0, (n, e) => checked(n + e.Bytes));
        WriteHeader(result, level, blocks, bytes);
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child.Digest.Length != 32 || child.Blocks == 0) throw new InvalidDataException("Invalid Raw Vault v2 child entry.");
            var offset = HeaderSize + i * InternalEntrySize;
            child.Digest.CopyTo(result, offset);
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(offset + 32), child.Blocks);
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(offset + 40), child.Bytes);
        }
        return result;
    }

    internal static Node DecodeNode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize || !bytes[..4].SequenceEqual("RVMP"u8) || bytes[4] != 1 || bytes[5] > 1 || bytes[7] != 0 || BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]) != 0)
            throw new InvalidDataException("Invalid or unsupported Raw Vault v2 map node header.");
        var type = bytes[5]; var level = bytes[6]; var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
        var blocks = BinaryPrimitives.ReadUInt64LittleEndian(bytes[12..]); var logicalBytes = BinaryPrimitives.ReadUInt64LittleEndian(bytes[20..]);
        if (count > Fanout || (type == 0) != (level == 0) || (type == 0 && bytes.Length != HeaderSize + count * LeafEntrySize) || (type == 1 && (count == 0 || bytes.Length != HeaderSize + count * InternalEntrySize)))
            throw new InvalidDataException("Invalid Raw Vault v2 map node shape.");
        if (type == 0)
        {
            var entries = new List<MapEntry>(count); ulong sum = 0;
            for (var i = 0; i < count; i++) { var at = HeaderSize + i * LeafEntrySize; var len = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 32)..]); if (len == 0) throw new InvalidDataException("Empty block in Raw Vault v2 map."); entries.Add(new(bytes.Slice(at, 32).ToArray(), len)); sum = checked(sum + len); }
            if (blocks != count || logicalBytes != sum) throw new InvalidDataException("Raw Vault v2 leaf totals are inconsistent.");
            return new(type, level, blocks, logicalBytes, entries, []);
        }
        var children = new List<ChildEntry>(count); ulong childBlocks = 0, childBytes = 0;
        for (var i = 0; i < count; i++) { var at = HeaderSize + i * InternalEntrySize; var n = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(at + 32)..]); var b = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(at + 40)..]); if (n == 0) throw new InvalidDataException("Empty child in Raw Vault v2 map."); children.Add(new(bytes.Slice(at, 32).ToArray(), n, b)); childBlocks = checked(childBlocks + n); childBytes = checked(childBytes + b); }
        if (blocks != childBlocks || logicalBytes != childBytes) throw new InvalidDataException("Raw Vault v2 internal totals are inconsistent.");
        return new(type, level, blocks, logicalBytes, [], children);
    }

    internal static IReadOnlyList<StoredObject> BuildTree(IReadOnlyList<MapEntry> blocks, out byte[] rootDigest)
    {
        var objects = new List<StoredObject>();
        var nodes = new List<(byte[] Digest, ulong Blocks, ulong Bytes, byte Level)>();
        foreach (var chunk in blocks.Chunk(Fanout))
        {
            var bytes = EncodeLeaf(chunk); var digest = MapDigest(bytes); objects.Add(new(2, digest, bytes));
            nodes.Add((digest, (ulong)chunk.Length, chunk.Aggregate<MapEntry, ulong>(0, (n, x) => checked(n + x.Length)), 0));
        }
        if (nodes.Count == 0)
        {
            var bytes = EncodeLeaf([]); rootDigest = MapDigest(bytes); objects.Add(new(2, rootDigest, bytes)); return objects;
        }
        while (nodes.Count > 1)
        {
            var next = new List<(byte[] Digest, ulong Blocks, ulong Bytes, byte Level)>();
            foreach (var chunk in nodes.Chunk(Fanout))
            {
                var level = checked((byte)(chunk[0].Level + 1));
                if (chunk.Any(n => n.Level + 1 != level)) throw new InvalidDataException("Unbalanced Raw Vault v2 map tree.");
                var entries = chunk.Select(n => new ChildEntry(n.Digest, n.Blocks, n.Bytes)).ToArray();
                var bytes = EncodeInternal(level, entries); var digest = MapDigest(bytes); objects.Add(new(2, digest, bytes));
                next.Add((digest, entries.Aggregate<ChildEntry, ulong>(0, (n, x) => checked(n + x.Blocks)), entries.Aggregate<ChildEntry, ulong>(0, (n, x) => checked(n + x.Bytes)), level));
            }
            nodes = next;
        }
        rootDigest = nodes[0].Digest;
        return objects;
    }

    internal static byte[] EncodeRecord(StoredObject value, bool compress = true)
    {
        if (value.Kind is not (1 or 2) || value.Digest.Length != 32)
            throw new InvalidDataException("Invalid Raw Vault v2 object identity.");
        var actualDigest = value.Kind == 1 ? DataDigest(value.Bytes) : MapDigest(value.Bytes);
        if (!CryptographicOperations.FixedTimeEquals(actualDigest, value.Digest))
            throw new InvalidDataException("Raw Vault v2 object bytes do not match their identity.");
        if (value.Kind == 2) _ = DecodeNode(value.Bytes);
        var payload = value.Bytes; byte codec = 0;
        if (compress && payload.Length > 0)
        {
            using var compressor = new Compressor(1); var candidate = compressor.Wrap(payload).ToArray();
            if (candidate.Length < payload.Length) { payload = candidate; codec = 1; }
        }
        var record = new byte[48 + payload.Length]; "ROBJ"u8.CopyTo(record); record[4] = 1; record[5] = value.Kind; record[6] = codec;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8), checked((uint)value.Bytes.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(12), checked((uint)payload.Length)); value.Digest.CopyTo(record, 16); payload.CopyTo(record, 48); return record;
    }

    internal static StoredObject DecodeRecord(ReadOnlySpan<byte> record)
    {
        if (record.Length < 48 || !record[..4].SequenceEqual("ROBJ"u8) || record[4] != 1 || record[5] is < 1 or > 2 || record[6] > 1 || record[7] != 0) throw new InvalidDataException("Invalid or unsupported Raw Vault v2 object record.");
        var uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(record[8..]); var stored = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
        if (stored != record.Length - 48) throw new InvalidDataException("Raw Vault v2 record length mismatch.");
        var maximumObjectSize = record[5] == 1 ? 65536U : HeaderSize + Fanout * InternalEntrySize;
        if (uncompressed > maximumObjectSize) throw new InvalidDataException("Raw Vault v2 object exceeds its format size bound.");
        byte[] bytes;
        if (record[6] == 0) { if (stored != uncompressed) throw new InvalidDataException("Raw Raw Vault v2 record length mismatch."); bytes = record[48..].ToArray(); }
        else { using var decompressor = new Decompressor(); bytes = decompressor.Unwrap(record[48..].ToArray()).ToArray(); if (bytes.Length != uncompressed) throw new InvalidDataException("Raw Vault v2 decompressed length mismatch."); }
        var digest = record.Slice(16, 32).ToArray(); var actual = record[5] == 1 ? DataDigest(bytes) : MapDigest(bytes);
        if (!CryptographicOperations.FixedTimeEquals(actual, digest)) throw new InvalidDataException("Raw Vault v2 object digest mismatch.");
        if (record[5] == 2) _ = DecodeNode(bytes);
        return new(record[5], digest, bytes);
    }

    internal static byte[] BuildPack(IReadOnlyList<StoredObject> objects, bool compress = true)
    {
        using var body = new MemoryStream(); body.Write("RVPK0001"u8); body.Write([1, 0, 0, 0, 0, 0, 0, 0]);
        foreach (var value in objects) body.Write(EncodeRecord(value, compress));
        var content = body.ToArray(); var digest = SHA256.HashData(content);
        using var pack = new MemoryStream(content.Length + 56); pack.Write(content); pack.Write("RVPKEND1"u8);
        Span<byte> numbers = stackalloc byte[16]; BinaryPrimitives.WriteUInt64LittleEndian(numbers, checked((ulong)objects.Count));
        BinaryPrimitives.WriteUInt64LittleEndian(numbers[8..], checked((ulong)content.Length)); pack.Write(numbers); pack.Write(digest);
        return pack.ToArray();
    }

    internal static IReadOnlyList<StoredObject> ReadPack(ReadOnlySpan<byte> pack)
    {
        if (pack.Length < 72 || !pack[..8].SequenceEqual("RVPK0001"u8) || BinaryPrimitives.ReadUInt16LittleEndian(pack[8..]) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(pack[10..]) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(pack[12..]) != 0)
            throw new InvalidDataException("Invalid or unsupported Raw Vault v2 pack header.");
        var footer = pack[^56..];
        if (!footer[..8].SequenceEqual("RVPKEND1"u8)) throw new InvalidDataException("Missing Raw Vault v2 pack footer.");
        var count = BinaryPrimitives.ReadUInt64LittleEndian(footer[8..]); var contentLength = BinaryPrimitives.ReadUInt64LittleEndian(footer[16..]);
        if (contentLength != checked((ulong)(pack.Length - 56)) || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(pack[..^56]), footer[24..]))
            throw new InvalidDataException("Raw Vault v2 pack length or checksum mismatch.");
        var objects = new List<StoredObject>(); var offset = 16;
        while (offset < checked((int)contentLength))
        {
            if (checked((int)contentLength) - offset < 48) throw new InvalidDataException("Truncated Raw Vault v2 object record.");
            var storedLength = BinaryPrimitives.ReadUInt32LittleEndian(pack[(offset + 12)..]);
            if (storedLength > checked((uint)(checked((int)contentLength) - offset - 48))) throw new InvalidDataException("Raw Vault v2 object record exceeds pack bounds.");
            var recordLength = checked(48 + (int)storedLength); objects.Add(DecodeRecord(pack.Slice(offset, recordLength))); offset += recordLength;
        }
        if (offset != checked((int)contentLength) || checked((ulong)objects.Count) != count) throw new InvalidDataException("Raw Vault v2 pack record count mismatch.");
        return objects;
    }

    private static void WriteHeader(Span<byte> result, byte level, ulong blocks, ulong bytes)
    {
        "RVMP"u8.CopyTo(result); result[4] = 1; result[5] = level == 0 ? (byte)0 : (byte)1; result[6] = level;
        BinaryPrimitives.WriteUInt16LittleEndian(result[8..], checked((ushort)(result.Length == HeaderSize ? 0 : (result.Length - HeaderSize) / (level == 0 ? LeafEntrySize : InternalEntrySize))));
        BinaryPrimitives.WriteUInt64LittleEndian(result[12..], blocks); BinaryPrimitives.WriteUInt64LittleEndian(result[20..], bytes);
    }
}
