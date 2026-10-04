using System.Security.Cryptography;
using System.Text.Json;
using WeArchive.Infrastructure.RawVault;

namespace WeArchive.Tests;

public sealed class RawVaultV2FormatTests
{
    [Fact]
    public void ProvisionalWriterDefaultIsTheSupportedFourKiBBlockSize()
    {
        Assert.Contains(RawVaultV2Format.ProvisionalWriterDefaultBlockSize, RawVaultV2Format.SupportedBlockSizes);
        Assert.Equal(4096, RawVaultV2Format.ProvisionalWriterDefaultBlockSize);
    }

    [Fact]
    public void ReproducesPublishedGoldenDataAndMapVectors()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "docs", "fixtures", "raw-vault-v2", "golden-v1.json")));
        var fixture = json.RootElement;
        var bytes = Convert.FromHexString(fixture.GetProperty("payload_hex").GetString()!);
        Assert.Equal(fixture.GetProperty("data_object").GetProperty("digest").GetString(), Convert.ToHexString(RawVaultV2Format.DataDigest(bytes)).ToLowerInvariant());
        var empty = RawVaultV2Format.EncodeLeaf([]);
        Assert.Equal(fixture.GetProperty("empty_leaf_map").GetProperty("canonical_node_hex").GetString(), Convert.ToHexString(empty).ToLowerInvariant());
        var entry = new RawVaultV2Format.MapEntry(RawVaultV2Format.DataDigest(bytes), (uint)bytes.Length);
        var leaf = RawVaultV2Format.EncodeLeaf([entry]);
        Assert.Equal(fixture.GetProperty("one_block_leaf_map").GetProperty("canonical_node_hex").GetString(), Convert.ToHexString(leaf).ToLowerInvariant());
        var packObject = new RawVaultV2Format.StoredObject(1, RawVaultV2Format.DataDigest(bytes), bytes);
        var pack = RawVaultV2Format.BuildPack([packObject], compress: false);
        Assert.Equal(fixture.GetProperty("raw_single_record_pack").GetProperty("full_pack_hex").GetString(), Convert.ToHexString(pack).ToLowerInvariant());
        Assert.Equal(bytes, RawVaultV2Format.ReadPack(pack)[0].Bytes);
    }

    [Fact]
    public void MapTreesPreserveLeafOrderAndTotals()
    {
        const int blockSize = 4096;
        var entries = Enumerable.Range(0, 1057).Select(i => new RawVaultV2Format.MapEntry(
            SHA256.HashData(BitConverter.GetBytes(i)), (uint)(i == 1056 ? 71 : blockSize))).ToArray();
        var nodes = RawVaultV2Format.BuildTree(entries, out var root);
        var byDigest = nodes.ToDictionary(o => Convert.ToHexString(o.Digest), o => o.Bytes);
        var traversed = new List<RawVaultV2Format.MapEntry>();
        Walk(root);
        Assert.Equal(entries.Select(e => (Convert.ToHexString(e.Digest), e.Length)), traversed.Select(e => (Convert.ToHexString(e.Digest), e.Length)));
        Assert.Equal(entries.Aggregate<RawVaultV2Format.MapEntry, ulong>(0, (n, e) => n + e.Length), RawVaultV2Format.DecodeNode(byDigest[Convert.ToHexString(root)]).Bytes);

        void Walk(byte[] digest)
        {
            var node = RawVaultV2Format.DecodeNode(byDigest[Convert.ToHexString(digest)]);
            if (node.Type == 0) traversed.AddRange(node.Leaves);
            else foreach (var child in node.Children) Walk(child.Digest);
        }
    }

    [Fact]
    public void MaterializerRejectsChildTotalsThatDisagreeWithTheChildNode()
    {
        const int blockSize = 4096;
        var artifact = RawVaultV2Format.BuildArtifact(new byte[blockSize * 34], blockSize);
        var objects = artifact.Objects.ToDictionary(
            value => value.Kind + ":" + Convert.ToHexString(value.Digest), value => value.Bytes, StringComparer.Ordinal);
        var rootNode = RawVaultV2Format.DecodeNode(objects["2:" + Convert.ToHexString(artifact.Root)]);
        Assert.Equal(1, rootNode.Type);
        var children = rootNode.Children.ToArray();
        children[0] = children[0] with { Blocks = children[0].Blocks + 1, Bytes = children[0].Bytes + blockSize };
        children[1] = children[1] with { Blocks = children[1].Blocks - 1, Bytes = children[1].Bytes - blockSize };
        var badRoot = RawVaultV2Format.EncodeInternal(rootNode.Level, children);
        var badRootDigest = RawVaultV2Format.MapDigest(badRoot);
        objects["2:" + Convert.ToHexString(badRootDigest)] = badRoot;
        var badArtifact = artifact with { Root = badRootDigest };

        Assert.Throws<InvalidDataException>(() => RawVaultV2Format.Materialize(badArtifact, objects));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TypedObjectRecordRoundTripsAndRejectsMutation(bool compress)
    {
        var bytes = Enumerable.Repeat((byte)0x41, 16384).ToArray();
        var obj = new RawVaultV2Format.StoredObject(1, RawVaultV2Format.DataDigest(bytes), bytes);
        var record = RawVaultV2Format.EncodeRecord(obj, compress);
        Assert.Equal(bytes, RawVaultV2Format.DecodeRecord(record).Bytes);
        record[^1] ^= 1;
        Assert.ThrowsAny<Exception>(() => RawVaultV2Format.DecodeRecord(record));
    }

    [Fact]
    public void NonBeneficialCompressionFallsBackToRawAndUnknownVersionsFailClosed()
    {
        var bytes = new byte[4096]; RandomNumberGenerator.Fill(bytes);
        var value = new RawVaultV2Format.StoredObject(1, RawVaultV2Format.DataDigest(bytes), bytes);
        var rawFallback = RawVaultV2Format.EncodeRecord(value, compress: true);
        Assert.Equal(0, rawFallback[6]);
        Assert.Throws<InvalidDataException>(() => RawVaultV2Format.DecodeRecord(rawFallback[..4].Concat(new byte[] { 2 }).Concat(rawFallback[5..]).ToArray()));
        var unknownCodec = rawFallback.ToArray(); unknownCodec[6] = 2;
        Assert.Throws<InvalidDataException>(() => RawVaultV2Format.DecodeRecord(unknownCodec));

        var map = RawVaultV2Format.EncodeLeaf([new(value.Digest, (uint)bytes.Length)]);
        map[4] = 2;
        Assert.Throws<InvalidDataException>(() => RawVaultV2Format.DecodeNode(map));
    }

    [Theory]
    [InlineData(4096)] [InlineData(8192)] [InlineData(16384)] [InlineData(32768)] [InlineData(65536)]
    public void CorrectnessMatrixRoundTripsEveryBlockSizeAndCodecAndRetainsPriorRoots(int blockSize)
    {
        // Opaque bytes represent unknown source fields. Storage must preserve them verbatim.
        var baseline = new byte[blockSize * 2 + 19]; RandomNumberGenerator.Fill(baseline);
        var append = baseline.Concat(new byte[] { 0x00, 0xff, 0x80 }).ToArray();
        var update = baseline.ToArray(); update[blockSize + 7] ^= 0xff;
        var truncate = baseline[..(blockSize + 4)];
        var rewrite = Enumerable.Range(0, baseline.Length).Select(i => (byte)(i * 31)).ToArray();
        foreach (var candidate in new[] { baseline, append, update, truncate, rewrite, Array.Empty<byte>() })
        {
            var artifact = RawVaultV2Format.BuildArtifact(candidate, blockSize);
            foreach (var useCompression in new[] { false, true })
            {
                var decoded = artifact.Objects.ToDictionary(
                    value => value.Kind + ":" + Convert.ToHexString(value.Digest),
                    value => RawVaultV2Format.DecodeRecord(RawVaultV2Format.EncodeRecord(value, useCompression)).Bytes,
                    StringComparer.Ordinal);
                var rebuilt = RawVaultV2Format.Materialize(artifact, decoded);
                Assert.Equal(candidate, rebuilt);
                Assert.Equal(SHA256.HashData(candidate), SHA256.HashData(rebuilt));
            }
        }

        var unchangedA = RawVaultV2Format.BuildArtifact(baseline, blockSize);
        var unchangedB = RawVaultV2Format.BuildArtifact(baseline, blockSize);
        Assert.Equal(unchangedA.Root, unchangedB.Root);
        Assert.Equal(unchangedA.Objects.Select(x => x.Kind + ":" + Convert.ToHexString(x.Digest)).Order(),
            unchangedB.Objects.Select(x => x.Kind + ":" + Convert.ToHexString(x.Digest)).Order());
        var changed = RawVaultV2Format.BuildArtifact(append, blockSize);
        Assert.NotEqual(unchangedA.Root, changed.Root);
        var oldObjects = unchangedA.Objects.ToDictionary(x => x.Kind + ":" + Convert.ToHexString(x.Digest), x => x.Bytes, StringComparer.Ordinal);
        Assert.Equal(baseline, RawVaultV2Format.Materialize(unchangedA, oldObjects));
    }
}
