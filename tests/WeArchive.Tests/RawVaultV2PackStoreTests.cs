using System.Security.Cryptography;
using WeArchive.Infrastructure.RawVault;

namespace WeArchive.Tests;

public sealed class RawVaultV2PackStoreTests
{
    [Theory]
    [InlineData(4096)] [InlineData(8192)] [InlineData(16384)] [InlineData(32768)] [InlineData(65536)]
    public void ArtifactRoundTripsThroughSealedPacksAndPreservesPriorRoots(int blockSize)
    {
        var account = Path.Combine(Path.GetTempPath(), "wearchive-v2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(account);
        try
        {
            var store = new RawVaultV2PackStore(account);
            var original = new byte[blockSize * 3 + 29];
            Random.Shared.NextBytes(original);
            var first = store.PutArtifact(original, blockSize, CancellationToken.None);
            var firstPackNames = Directory.GetFiles(Path.Combine(account, "objects", "packs"), "*.rvpk");
            var unchanged = store.PutArtifact(original, blockSize, CancellationToken.None);
            Assert.Equal(first.Root, unchanged.Root);
            Assert.Equal(firstPackNames, Directory.GetFiles(Path.Combine(account, "objects", "packs"), "*.rvpk"));
            var firstPackHashes = firstPackNames.ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));

            foreach (var nextBytes in new[]
            {
                original.Concat(new byte[] { 0xff }).ToArray(),
                ChangeByte(original, blockSize + 7),
                original[..(blockSize + 3)],
                Enumerable.Range(0, original.Length).Select(i => (byte)(i * 31)).ToArray(),
                original.ToArray()
            })
            {
                var next = store.PutArtifact(nextBytes, blockSize, CancellationToken.None);
                using var output = new MemoryStream();
                store.MaterializeTo(next, output, CancellationToken.None);
                Assert.Equal(nextBytes, output.ToArray());
            }

            using var priorOutput = new MemoryStream();
            store.MaterializeTo(first, priorOutput, CancellationToken.None);
            Assert.Equal(original, priorOutput.ToArray());
            Assert.All(firstPackHashes, pair => Assert.Equal(pair.Value, SHA256.HashData(File.ReadAllBytes(pair.Key))));
        }
        finally
        {
            Directory.Delete(account, recursive: true);
        }
    }

    [Fact]
    public void SecondWriterIsRejectedWithoutChangingPublishedPacks()
    {
        var account = Path.Combine(Path.GetTempPath(), "wearchive-v2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(account);
        try
        {
            var store = new RawVaultV2PackStore(account);
            var initial = new RawVaultV2Format.StoredObject(1, RawVaultV2Format.DataDigest([1]), [1]);
            store.Put([initial], CancellationToken.None);
            var packs = Directory.GetFiles(Path.Combine(account, "objects", "packs"), "*.rvpk");
            using var heldLock = new FileStream(Path.Combine(account, "objects", "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var next = new RawVaultV2Format.StoredObject(1, RawVaultV2Format.DataDigest([2]), [2]);
            Assert.Throws<IOException>(() => store.Put([next], CancellationToken.None));
            Assert.Equal(packs, Directory.GetFiles(Path.Combine(account, "objects", "packs"), "*.rvpk"));
        }
        finally
        {
            Directory.Delete(account, recursive: true);
        }
    }

    [Fact]
    public void SealedPacksDeduplicateObjectsAndRebuildTheDerivedIndex()
    {
        var account = Path.Combine(Path.GetTempPath(), "wearchive-v2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(account);
        try
        {
            var bytes = Enumerable.Repeat((byte)0x5a, 4096).ToArray();
            var data = new RawVaultV2Format.StoredObject(1, RawVaultV2Format.DataDigest(bytes), bytes);
            var mapBytes = RawVaultV2Format.EncodeLeaf([new(data.Digest, (uint)bytes.Length)]);
            var map = new RawVaultV2Format.StoredObject(2, RawVaultV2Format.MapDigest(mapBytes), mapBytes);
            var store = new RawVaultV2PackStore(account);
            store.Put([data, map], CancellationToken.None);
            var packsPath = Path.Combine(account, "objects", "packs");
            var firstPacks = Directory.GetFiles(packsPath, "*.rvpk");
            Assert.Single(firstPacks);
            Assert.Equal(bytes, store.Get(1, data.Digest).Bytes);
            Assert.Equal(mapBytes, store.Get(2, map.Digest).Bytes);

            store.Put([data, map], CancellationToken.None);
            Assert.Equal(firstPacks, Directory.GetFiles(packsPath, "*.rvpk"));

            File.Delete(Path.Combine(account, "objects", "lookup.sqlite"));
            var rebuilt = new RawVaultV2PackStore(account);
            Assert.Equal(bytes, rebuilt.Get(1, data.Digest).Bytes);
            Assert.Equal(mapBytes, rebuilt.Get(2, map.Digest).Bytes);

            File.WriteAllBytes(Path.Combine(account, "objects", "lookup.sqlite"), [0xff, 0, 0xff]);
            var rebuiltAgain = new RawVaultV2PackStore(account);
            Assert.Equal(bytes, rebuiltAgain.Get(1, data.Digest).Bytes);
        }
        finally
        {
            Directory.Delete(account, recursive: true);
        }
    }

    [Fact]
    public void CorruptSealedPackFailsClosed()
    {
        var account = Path.Combine(Path.GetTempPath(), "wearchive-v2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(account);
        try
        {
            var bytes = new byte[] { 1, 2, 3 };
            var data = new RawVaultV2Format.StoredObject(1, RawVaultV2Format.DataDigest(bytes), bytes);
            var store = new RawVaultV2PackStore(account);
            store.Put([data], CancellationToken.None);
            var pack = Directory.GetFiles(Path.Combine(account, "objects", "packs"), "*.rvpk").Single();
            File.WriteAllBytes(pack, File.ReadAllBytes(pack)[..^1]);
            Assert.Throws<InvalidDataException>(() => store.Get(1, data.Digest));
        }
        finally
        {
            Directory.Delete(account, recursive: true);
        }
    }

    [Fact]
    public void RepeatedBlockContentAtDifferentOffsetsUsesOneObjectAndReconstructsInOrder()
    {
        var account = Path.Combine(Path.GetTempPath(), "wearchive-v2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(account);
        try
        {
            var block = Enumerable.Repeat((byte)0x37, 4096).ToArray();
            var artifactBytes = block.Concat(block).Concat(Enumerable.Repeat((byte)0x11, 4096)).ToArray();
            var store = new RawVaultV2PackStore(account);
            var artifact = store.PutArtifact(artifactBytes, 4096, CancellationToken.None);
            using var output = new MemoryStream();
            store.MaterializeTo(artifact, output, CancellationToken.None);
            Assert.Equal(artifactBytes, output.ToArray());
            Assert.Equal(1, artifact.Objects.Count(value => value.Kind == 1 && value.Digest.SequenceEqual(RawVaultV2Format.DataDigest(block))));
        }
        finally
        {
            Directory.Delete(account, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(4096)] [InlineData(8193)]
    public void StreamingArtifactWriterRoundTripsWithoutRetainingDataObjects(int length)
    {
        var account = Path.Combine(Path.GetTempPath(), "wearchive-v2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(account);
        try
        {
            var bytes = new byte[length]; Random.Shared.NextBytes(bytes);
            var store = new RawVaultV2PackStore(account);
            using var input = new MemoryStream(bytes, writable: false);
            var artifact = store.PutArtifact(input, bytes.LongLength, 4096, CancellationToken.None);
            Assert.Empty(artifact.Objects);
            using var output = new MemoryStream();
            store.MaterializeTo(artifact, output, CancellationToken.None);
            Assert.Equal(bytes, output.ToArray());
        }
        finally
        {
            Directory.Delete(account, recursive: true);
        }
    }

    [Fact]
    public void PackRolloverCreatesNewSealedFilesAndHonorsCancellationBeforePublication()
    {
        var account = Path.Combine(Path.GetTempPath(), "wearchive-v2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(account);
        try
        {
            var store = new RawVaultV2PackStore(account, compressObjects: false, targetPackBytes: 175);
            var objects = Enumerable.Range(1, 3).Select(i =>
            {
                var bytes = new[] { (byte)i };
                return new RawVaultV2Format.StoredObject(1, RawVaultV2Format.DataDigest(bytes), bytes);
            }).ToArray();
            store.Put(objects, CancellationToken.None);
            var packs = Directory.GetFiles(Path.Combine(account, "objects", "packs"), "*.rvpk");
            Assert.Equal(2, packs.Length);
            var checksums = packs.ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));

            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Assert.Throws<OperationCanceledException>(() => store.Put([
                new RawVaultV2Format.StoredObject(1, RawVaultV2Format.DataDigest([9]), [9])
            ], canceled.Token));
            Assert.All(checksums, pair => Assert.Equal(pair.Value, SHA256.HashData(File.ReadAllBytes(pair.Key))));
            Assert.Equal(packs, Directory.GetFiles(Path.Combine(account, "objects", "packs"), "*.rvpk"));
        }
        finally
        {
            Directory.Delete(account, recursive: true);
        }
    }

    [Fact]
    public void PacksRemainBufferedAcrossLookupBatches()
    {
        var account = Path.Combine(Path.GetTempPath(), "wearchive-v2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(account);
        try
        {
            var store = new RawVaultV2PackStore(account, compressObjects: false);
            var objects = Enumerable.Range(0, 500).Select(i =>
            {
                var bytes = BitConverter.GetBytes(i);
                return new RawVaultV2Format.StoredObject(1, RawVaultV2Format.DataDigest(bytes), bytes);
            }).ToArray();
            store.Put(objects, CancellationToken.None);
            Assert.Single(Directory.GetFiles(Path.Combine(account, "objects", "packs"), "*.rvpk"));
            Assert.Equal(objects.Length, objects.Count(value => store.Get(1, value.Digest).Bytes.SequenceEqual(value.Bytes)));
        }
        finally
        {
            Directory.Delete(account, recursive: true);
        }
    }

    [Fact]
    public void RepeatedContentAcrossLookupBatchesIsStoredOnceAndReconstructsByteForByte()
    {
        var account = Path.Combine(Path.GetTempPath(), "wearchive-v2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(account);
        try
        {
            const int blockSize = 4096;
            var blocks = Enumerable.Range(0, 400).Select(i =>
            {
                var seed = SHA256.HashData(BitConverter.GetBytes(i));
                var block = Enumerable.Range(0, blockSize).Select(index => seed[index % seed.Length]).ToArray();
                return block;
            }).ToList();
            blocks.Add(blocks[0].ToArray());
            var bytes = blocks.SelectMany(block => block).ToArray();
            var store = new RawVaultV2PackStore(account, compressObjects: false);
            var artifact = store.PutArtifact(bytes, blockSize, CancellationToken.None);

            var repeatedDigest = RawVaultV2Format.DataDigest(blocks[0]);
            var storedCopies = Directory.GetFiles(Path.Combine(account, "objects", "packs"), "*.rvpk")
                .SelectMany(path => RawVaultV2Format.ReadPack(File.ReadAllBytes(path)))
                .Count(value => value.Kind == 1 && value.Digest.SequenceEqual(repeatedDigest));
            Assert.Equal(1, storedCopies);

            using var output = new MemoryStream();
            store.MaterializeTo(artifact, output, CancellationToken.None);
            Assert.Equal(bytes, output.ToArray());
        }
        finally
        {
            Directory.Delete(account, recursive: true);
        }
    }

    [Fact]
    public void CancellationAfterASealedPackLeavesOnlyCompleteUnreachableObjects()
    {
        var account = Path.Combine(Path.GetTempPath(), "wearchive-v2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(account);
        try
        {
            using var cancellation = new CancellationTokenSource();
            var store = new RawVaultV2PackStore(account, compressObjects: false, targetPackBytes: 175);
            var objects = Enumerable.Range(1, 3).Select(i =>
            {
                var bytes = new[] { (byte)i };
                return new RawVaultV2Format.StoredObject(1, RawVaultV2Format.DataDigest(bytes), bytes);
            }).ToArray();
            Assert.Throws<OperationCanceledException>(() => store.Put(objects, cancellation.Token, cancellation.Cancel));

            var packs = Directory.GetFiles(Path.Combine(account, "objects", "packs"), "*.rvpk");
            var pack = Assert.Single(packs);
            Assert.Equal(2, RawVaultV2Format.ReadPack(File.ReadAllBytes(pack)).Count);
            Assert.Empty(Directory.GetFiles(Path.Combine(account, "objects", "packs"), "*.staging"));
        }
        finally
        {
            Directory.Delete(account, recursive: true);
        }
    }

    [Fact]
    public void FailureBeforePackPublicationLeavesPriorPacksUntouchedAndOnlyASealedStage()
    {
        var account = Path.Combine(Path.GetTempPath(), "wearchive-v2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(account);
        try
        {
            var bytes = new byte[] { 1, 2, 3 };
            var value = new RawVaultV2Format.StoredObject(1, RawVaultV2Format.DataDigest(bytes), bytes);
            var failed = new RawVaultV2PackStore(account, compressObjects: false,
                beforePackPublish: () => throw new IOException("Injected pre-publication failure."));
            Assert.Throws<IOException>(() => failed.Put([value], CancellationToken.None));
            Assert.Empty(Directory.GetFiles(Path.Combine(account, "objects", "packs"), "*.rvpk"));
            var staged = Assert.Single(Directory.GetFiles(Path.Combine(account, "objects", "packs"), "*.staging"));
            Assert.Single(RawVaultV2Format.ReadPack(File.ReadAllBytes(staged)));

            var retry = new RawVaultV2PackStore(account, compressObjects: false);
            retry.Put([value], CancellationToken.None);
            Assert.Equal(bytes, retry.Get(1, value.Digest).Bytes);
        }
        finally
        {
            Directory.Delete(account, recursive: true);
        }
    }

    private static byte[] ChangeByte(byte[] input, int position)
    {
        var changed = input.ToArray();
        changed[position] ^= 0xff;
        return changed;
    }
}
