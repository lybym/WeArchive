using System.Security.Cryptography;
using WeArchive.Core.RawVault;
using WeArchive.Infrastructure.RawVault;

namespace WeArchive.Tests;

public sealed class RawVaultArtifactProviderTests
{
    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(2, 1, true)]
    [InlineData(3, 2, true)]
    [InlineData(1, 2, false)]
    [InlineData(2, 2, false)]
    [InlineData(3, 1, false)]
    [InlineData(4, 2, false)]
    public void ManifestAndVaultVersionTuplesDispatchExplicitly(int manifestVersion, int vaultFormatVersion, bool supported)
    {
        var content = "format tuple"u8.ToArray();
        var isV2 = vaultFormatVersion == 2;
        var artifact = new RawArtifactDescriptor
        {
            Role = "source-database", Name = "fixture.db",
            Sha256 = Convert.ToHexString(SHA256.HashData(isV2 ? [] : content)).ToLowerInvariant(),
            Size = isV2 ? 0 : content.Length,
            ContentRef = isV2 ? null! : "artifacts/fixture.db",
            Storage = isV2 ? new RawArtifactStorage
            {
                Kind = "fixed-block-map-v1", BlockSize = 4096, BlockCount = 0, Root = new string('0', 64),
            } : null,
        };
        var manifest = Manifest("gen_3333333333333333", "a_0123456789abcdef", DateTimeOffset.UtcNow,
            [artifact], manifestVersion, vaultFormatVersion, null);

        Assert.Equal(supported, RawManifestSerializer.TryDeserialize(RawManifestSerializer.Serialize(manifest)) is not null);
    }

    [Fact]
    public async Task V2GenerationReconstructsVerifiedArtifactAndRebuildsDeletedIndex()
    {
        var fixture = CreateV2Generation();
        try
        {
            var index = Path.Combine(fixture.AccountDirectory, "objects", "lookup.sqlite");
            File.Delete(index);

            var store = new RawVaultStore(fixture.Root);
            var opened = await store.OpenGenerationAsync(fixture.AccountId, fixture.GenerationId, CancellationToken.None);
            Assert.NotNull(opened);
            Assert.True(File.Exists(index));

            string materialized;
            using (var provider = RawVaultArtifactProvider.Create(opened!))
            {
                materialized = provider.GetVerifiedPath(opened!.Manifest.Artifacts[0]);
                Assert.Equal(fixture.Content, await File.ReadAllBytesAsync(materialized));
                Assert.Equal(fixture.Content.LongLength, new FileInfo(materialized).Length);
                Assert.Equal(Convert.ToHexString(SHA256.HashData(fixture.Content)).ToLowerInvariant(),
                    Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(materialized))).ToLowerInvariant());
                Assert.True(File.Exists(materialized));
            }

            Assert.False(File.Exists(materialized));
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    [Fact]
    public async Task ExistingMaterializationCannotMaskMissingAuthoritativeV2Pack()
    {
        var fixture = CreateV2Generation();
        try
        {
            var store = new RawVaultStore(fixture.Root);
            var opened = await store.OpenGenerationAsync(fixture.AccountId, fixture.GenerationId, CancellationToken.None);
            Assert.NotNull(opened);
            var packs = Directory.GetFiles(Path.Combine(fixture.AccountDirectory, "objects", "packs"), "*.rvpk");
            Assert.NotEmpty(packs);

            using (var prior = RawVaultArtifactProvider.Create(opened!))
                Assert.True(File.Exists(prior.GetVerifiedPath(opened!.Manifest.Artifacts[0])));

            foreach (var pack in packs) File.Delete(pack);
            using var fresh = RawVaultArtifactProvider.Create(opened!);
            Assert.Throws<InvalidDataException>(() => fresh.GetVerifiedPath(opened!.Manifest.Artifacts[0]));
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    [Fact]
    public async Task V1AndV2GenerationsInOneLineageRemainReadable()
    {
        var root = Path.Combine(Path.GetTempPath(), "wearchive-artifacts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var accountId = "a_0123456789abcdef";
            var vault = new RawVaultStore(root);
            var v1Content = "legacy artifact"u8.ToArray();
            var v1Context = Context(accountId, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
            await using var session = await vault.BeginGenerationAsync(v1Context, CancellationToken.None);
            await using var input = new MemoryStream(v1Content, writable: false);
            var v1Artifact = await session.WriteArtifactAsync("source-database", "session.db", input, "sqlite", true, null, CancellationToken.None);
            var v1Manifest = Manifest(session.GenerationId, accountId, v1Context.CaptureTime, [v1Artifact], 1, 1, null);
            var v1 = await session.PublishAsync(v1Manifest, CancellationToken.None);

            var accountDirectory = Path.Combine(root, "accounts", accountId);
            var v2Store = new RawVaultV2PackStore(accountDirectory);
            var v2Content = Enumerable.Range(0, 9000).Select(i => (byte)(i * 19)).ToArray();
            var artifact = v2Store.PutArtifact(v2Content, 4096, CancellationToken.None);
            var storage = new RawArtifactStorage
            {
                Kind = "fixed-block-map-v1", BlockSize = artifact.BlockSize,
                BlockCount = artifact.BlockCount, Root = Convert.ToHexString(artifact.Root).ToLowerInvariant(),
            };
            var descriptor = new RawArtifactDescriptor
            {
                Role = "source-database", Name = "session.db", Sha256 = artifact.Sha256,
                Size = artifact.Size, SourceFormat = "sqlite", IsDecrypted = true, Storage = storage,
                Metadata = new Dictionary<string, string> { ["source_relative_path"] = "db_storage/session/session.db" },
            };
            var v2Id = "gen_2222222222222222";
            var v2Time = v1Context.CaptureTime.AddHours(1);
            var v2Directory = Path.Combine(accountDirectory, "generations", v2Id);
            Directory.CreateDirectory(v2Directory);
            var v2Manifest = Manifest(v2Id, accountId, v2Time, [descriptor], 3, 2, v1.GenerationId);
            await File.WriteAllTextAsync(Path.Combine(v2Directory, "manifest.json"), RawManifestSerializer.Serialize(v2Manifest));

            var generations = await vault.ListGenerationsAsync(accountId, CancellationToken.None);
            Assert.Equal(2, generations.Count);
            var openedV1 = await vault.OpenGenerationAsync(accountId, v1.GenerationId, CancellationToken.None);
            var openedV2 = await vault.OpenGenerationAsync(accountId, v2Id, CancellationToken.None);
            Assert.NotNull(openedV1);
            Assert.NotNull(openedV2);
            using (var provider = RawVaultArtifactProvider.Create(openedV1!))
                Assert.Equal(v1Content, await File.ReadAllBytesAsync(provider.GetVerifiedPath(openedV1!.Manifest.Artifacts[0])));
            using var v2Provider = RawVaultArtifactProvider.Create(openedV2!);
            Assert.Equal(v2Content, await File.ReadAllBytesAsync(v2Provider.GetVerifiedPath(descriptor)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V2MaterializerRejectsDescriptorSizeOrChecksumMismatch(bool changeSize)
    {
        var fixture = CreateV2Generation();
        try
        {
            var store = new RawVaultStore(fixture.Root);
            var opened = await store.OpenGenerationAsync(fixture.AccountId, fixture.GenerationId, CancellationToken.None);
            Assert.NotNull(opened);

            var artifact = opened!.Manifest.Artifacts[0];
            var invalid = changeSize
                ? artifact with { Size = artifact.Size + 1 }
                : artifact with { Sha256 = new string('0', 64) };
            var altered = opened with { Manifest = opened.Manifest with { Artifacts = [invalid] } };
            using var provider = RawVaultArtifactProvider.Create(altered);

            Assert.Throws<InvalidDataException>(() => provider.GetVerifiedPath(invalid));
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    private static (string Root, string AccountDirectory, string AccountId, string GenerationId, byte[] Content) CreateV2Generation()
    {
        var root = Path.Combine(Path.GetTempPath(), "wearchive-artifacts-" + Guid.NewGuid().ToString("N"));
        var accountId = "a_0123456789abcdef";
        var generationId = "gen_1111111111111111";
        var accountDirectory = Path.Combine(root, "accounts", accountId);
        var generationDirectory = Path.Combine(accountDirectory, "generations", generationId);
        Directory.CreateDirectory(generationDirectory);
        var content = Enumerable.Range(0, 12001).Select(i => (byte)(i * 31)).ToArray();
        var v2Store = new RawVaultV2PackStore(accountDirectory);
        var artifact = v2Store.PutArtifact(content, 4096, CancellationToken.None);
        var descriptor = new RawArtifactDescriptor
        {
            Role = "source-database", Name = "session.db", Sha256 = artifact.Sha256,
            Size = artifact.Size, SourceFormat = "sqlite", IsDecrypted = true,
            Storage = new RawArtifactStorage
            {
                Kind = "fixed-block-map-v1", BlockSize = artifact.BlockSize,
                BlockCount = artifact.BlockCount, Root = Convert.ToHexString(artifact.Root).ToLowerInvariant(),
            },
        };
        var manifest = Manifest(generationId, accountId, new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero), [descriptor], 3, 2, null);
        File.WriteAllText(Path.Combine(generationDirectory, "manifest.json"), RawManifestSerializer.Serialize(manifest));
        return (root, accountDirectory, accountId, generationId, content);
    }

    private static RawGenerationContext Context(string accountId, DateTimeOffset captureTime) => new()
    {
        AccountId = accountId, SourceProfileId = "wxid_test", CaptureTime = captureTime,
        CaptureAdapterFamily = "fixture", CaptureAdapterVersion = "1.0.0",
    };

    private static RawManifest Manifest(string generationId, string accountId, DateTimeOffset captureTime,
        IReadOnlyList<RawArtifactDescriptor> artifacts, int manifestVersion, int vaultFormatVersion, string? previous) => new()
    {
        ManifestVersion = manifestVersion, VaultFormatVersion = vaultFormatVersion,
        GenerationId = generationId, AccountId = accountId, SourceProfileId = "wxid_test",
        Source = new RawManifestSource { AdapterName = "fixture", AdapterVersion = "1.0.0", SourceVersion = "4.0" },
        Capture = new RawManifestCapture
        {
            CaptureTime = captureTime, CaptureAdapterFamily = "fixture", CaptureAdapterVersion = "1.0.0",
            Mode = RawCaptureMode.Baseline, Completeness = RawGenerationCompleteness.Complete, ArtifactCount = artifacts.Count,
        },
        Artifacts = artifacts, PreviousGenerationId = previous,
    };
}
