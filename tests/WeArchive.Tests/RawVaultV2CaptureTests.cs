using WeArchive.Core.Abstractions;
using WeArchive.Core.RawVault;
using WeArchive.Core.Services;
using WeArchive.Infrastructure.Fixtures;
using WeArchive.Infrastructure.RawVault;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

public sealed class RawVaultV2CaptureTests
{
    [Fact]
    public async Task ChangedSourceFingerprintWithIdenticalPlaintextAdvancesProofWithoutNewObjects()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var adapter = new SyntheticCaptureAdapter();
        adapter.Partitions["A"] = "before";
        adapter.ContentOverrides["A"] = "identical plaintext"u8.ToArray();
        var clock = new FixedClock();
        var capture = new CaptureService(new FixtureSourceAdapter(), adapter, vault, clock);
        var first = await capture.CaptureAccountAsync(new(), null, default);
        clock.UtcNow = clock.UtcNow.AddHours(1);
        adapter.Partitions["A"] = "after";
        var second = await capture.CaptureAccountAsync(new(), null, default);
        Assert.True(second.Succeeded);
        Assert.Equal(RawPartitionStatus.Captured, Assert.Single(second.Coverage).Status);
        Assert.Equal(0, second.StorageCounters.NewDataBytes);
        Assert.Equal(0, second.StorageCounters.NewMapNodes);
        var old = (await vault.OpenGenerationAsync(first.AccountId, first.GenerationId, default))!;
        var current = (await vault.OpenGenerationAsync(second.AccountId, second.GenerationId, default))!;
        Assert.Equal(old.Manifest.Artifacts[0].Storage, current.Manifest.Artifacts[0].Storage);
        Assert.Equal("after", current.Manifest.CaptureCheckpoint!.PartitionFingerprints["A"]);
        Assert.Equal("before", old.Manifest.CaptureCheckpoint!.PartitionFingerprints["A"]);
    }

    [Fact]
    public async Task ExistingNondefaultBlockSizeAndRootSurviveFreshAndReusedCapture()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var context = Context(DateTimeOffset.UtcNow);
        await using var seed = await vault.BeginGenerationAsync(context, default);
        var bytes = new byte[8192 * 35 + 11];
        new Random(83).NextBytes(bytes);
        var engine = new RawVaultV2PackStore(Path.Combine(vault.VaultRoot, "accounts", context.AccountId));
        var stored = engine.PutArtifact(bytes, 8192, default);
        var descriptor = new RawArtifactDescriptor { Role = "source-database", Name = "fixture.db",
            Sha256 = stored.Sha256, Size = bytes.Length, Storage = new() { Kind = "fixed-block-map-v1",
                BlockSize = 8192, BlockCount = stored.BlockCount, Root = Convert.ToHexString(stored.Root).ToLowerInvariant() } };
        var generation = await seed.PublishAsync(Manifest(seed, context, descriptor), default);
        context = context with { CaptureTime = context.CaptureTime.AddHours(1) };
        await using var next = await vault.BeginGenerationAsync(context, default);
        var fresh = await Write(next, bytes);
        var reused = await next.ReuseArtifactAsync(generation, descriptor, default);
        Assert.Equal(descriptor.Storage, fresh.Storage);
        Assert.Equal(descriptor.Storage, reused.Storage);
        Assert.Equal(0, next.StorageCounters.NewDataBlocks);
        Assert.Equal(0, next.StorageCounters.NewMapNodes);
        await next.PublishAsync(Manifest(next, context, fresh, seed.GenerationId), default);
    }

    private static RawGenerationContext Context(DateTimeOffset time) => new()
    {
        AccountId = "a_fixture", SourceProfileId = "fixture", CaptureTime = time,
        CaptureAdapterFamily = "fixture", CaptureAdapterVersion = "1",
    };

    private static RawManifest Manifest(IRawGenerationSession session, RawGenerationContext context,
        RawArtifactDescriptor artifact, string? previous = null) => new()
    {
        GenerationId = session.GenerationId, AccountId = context.AccountId, SourceProfileId = context.SourceProfileId,
        Source = new() { AdapterName = "fixture", AdapterVersion = "1" },
        Capture = new() { CaptureTime = context.CaptureTime, CaptureAdapterFamily = "fixture",
            CaptureAdapterVersion = "1", Mode = RawCaptureMode.Baseline,
            Completeness = RawGenerationCompleteness.Complete, ArtifactCount = 1 },
        Artifacts = [artifact], PreviousGenerationId = previous,
    };

    private static Task<RawArtifactDescriptor> Write(IRawGenerationSession session, byte[] bytes) =>
        session.WriteArtifactAsync("source-database", "fixture.db", new MemoryStream(bytes), "sqlite", true, null, default);

    [Fact]
    public async Task LegacyPredecessorImportsWithoutRewritingHistoryAndLaterReusesRoot()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("vault");
        var context = Context(DateTimeOffset.UtcNow);
        var legacy = new RawVaultStore(root, writeLegacy: true);
        await using var first = await legacy.BeginGenerationAsync(context, default);
        var descriptor = await Write(first, "legacy evidence"u8.ToArray());
        var generation = await first.PublishAsync(Manifest(first, context, descriptor), default);
        var oldManifest = File.ReadAllBytes(Path.Combine(generation.GenerationDirectory, "manifest.json"));
        var vault = new RawVaultStore(root);
        context = context with { CaptureTime = context.CaptureTime.AddHours(1) };
        await using var second = await vault.BeginGenerationAsync(context, default);
        var imported = await second.ReuseArtifactAsync(generation, descriptor, default);
        var v2 = await second.PublishAsync(Manifest(second, context, imported, first.GenerationId), default);
        Assert.Equal((3, 2), (v2.Manifest.ManifestVersion, v2.Manifest.VaultFormatVersion));
        Assert.Null(imported.ContentRef);
        Assert.NotNull(imported.Storage);
        Assert.Equal(descriptor.Sha256, imported.Sha256);
        Assert.True(second.StorageCounters.NewDataBytes > 0);
        context = context with { CaptureTime = context.CaptureTime.AddHours(1) };
        await using var third = await vault.BeginGenerationAsync(context, default);
        var reused = await third.ReuseArtifactAsync(v2, imported, default);
        await third.PublishAsync(Manifest(third, context, reused, second.GenerationId), default);
        Assert.Equal(imported.Storage, reused.Storage);
        Assert.Equal(0, third.StorageCounters.NewDataBytes);
        Assert.Equal(0, third.StorageCounters.NewMapNodes);
        Assert.Equal(0, third.StorageCounters.NewPacks);
        Assert.Equal(oldManifest, File.ReadAllBytes(Path.Combine(generation.GenerationDirectory, "manifest.json")));
        Assert.NotNull(await vault.OpenGenerationAsync(context.AccountId, first.GenerationId, default));
        Assert.Equal(3, (await vault.ListGenerationsAsync(context.AccountId, default)).Count);
    }

    [Theory]
    [InlineData("append")]
    [InlineData("update")]
    [InlineData("truncate")]
    [InlineData("rewrite")]
    public async Task ChangesStoreOnlyNewUniqueBlocksAndLeavePriorBytesReadable(string change)
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var context = Context(DateTimeOffset.UtcNow);
        var bytes = new byte[4096 * 64];
        new Random(83).NextBytes(bytes);
        await using var first = await vault.BeginGenerationAsync(context, default);
        var artifact = await Write(first, bytes);
        var prior = await first.PublishAsync(Manifest(first, context, artifact), default);
        var changed = (byte[])bytes.Clone();
        if (change == "append") changed = [.. bytes, .. "tail"u8.ToArray()];
        if (change == "update") changed[4096 * 20 + 5] ^= 1;
        if (change == "truncate") changed = bytes[..^4096];
        if (change == "rewrite") new Random(84).NextBytes(changed);
        context = context with { CaptureTime = context.CaptureTime.AddHours(1) };
        await using var second = await vault.BeginGenerationAsync(context, default);
        var current = await Write(second, changed);
        var published = await second.PublishAsync(Manifest(second, context, current, first.GenerationId), default);
        Assert.Equal(change == "rewrite" ? 64 : change == "truncate" ? 0 : 1, second.StorageCounters.NewDataBlocks);
        Assert.True(second.StorageCounters.NewMapNodes <= 3);
        using var oldProvider = RawVaultArtifactProvider.Create(prior);
        using var newProvider = RawVaultArtifactProvider.Create(published);
        Assert.Equal(bytes, File.ReadAllBytes(oldProvider.GetVerifiedPath(artifact)));
        Assert.Equal(changed, File.ReadAllBytes(newProvider.GetVerifiedPath(current)));
    }

    [Theory]
    [InlineData("pack-sealed", false)]
    [InlineData("objects-published", false)]
    [InlineData("before-verification", false)]
    [InlineData("roots-verified", false)]
    [InlineData("manifest-written", false)]
    [InlineData("pack-sealed", true)]
    [InlineData("objects-published", true)]
    [InlineData("before-verification", true)]
    [InlineData("roots-verified", true)]
    [InlineData("manifest-written", true)]
    public async Task PublicationFailureOrCancellationLeavesPriorCheckpointUnchanged(string stage, bool cancel)
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("vault");
        var source = new FixtureSourceAdapter();
        var adapter = new SyntheticCaptureAdapter();
        adapter.Partitions["A"] = "first";
        var clock = new FixedClock();
        var vault = new RawVaultStore(root);
        var service = new CaptureService(source, adapter, vault, clock);
        var first = await service.CaptureAccountAsync(new(), null, default);
        Assert.True(first.Succeeded);
        var prior = (await vault.OpenGenerationAsync(first.AccountId, first.GenerationId, default))!;
        var oldManifest = File.ReadAllBytes(Path.Combine(prior.GenerationDirectory, "manifest.json"));
        clock.UtcNow = clock.UtcNow.AddHours(1);
        adapter.Partitions["A"] = "second";
        using var cts = new CancellationTokenSource();
        var faultVault = new RawVaultStore(root, false, current =>
        {
            if (current != stage) return;
            Assert.Single(vault.ListGenerationsAsync(first.AccountId, default).GetAwaiter().GetResult());
            if (cancel) { cts.Cancel(); return; }
            throw new IOException("injected publication failure");
        });
        var faultService = new CaptureService(source, adapter, faultVault, clock);
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => faultService.CaptureAccountAsync(new(), null, cts.Token));
        else
            Assert.False((await faultService.CaptureAccountAsync(new(), null, default)).Succeeded);
        Assert.Single(await vault.ListGenerationsAsync(first.AccountId, default));
        Assert.Equal(oldManifest, File.ReadAllBytes(Path.Combine(prior.GenerationDirectory, "manifest.json")));
        Assert.Equal(first.GenerationId, (await vault.OpenGenerationAsync(first.AccountId, first.GenerationId, default))!
            .Manifest.CaptureCheckpoint!.GenerationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidChecksumOrRootCannotPublishGeneration(bool badRoot)
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var context = Context(DateTimeOffset.UtcNow);
        await using var session = await vault.BeginGenerationAsync(context, default);
        var artifact = await Write(session, "evidence"u8.ToArray());
        artifact = badRoot ? artifact with { Storage = artifact.Storage! with { Root = new string('0', 64) } }
            : artifact with { Sha256 = new string('0', 64) };
        await Assert.ThrowsAsync<InvalidDataException>(() => session.PublishAsync(Manifest(session, context, artifact), default));
        Assert.Empty(await vault.ListGenerationsAsync(context.AccountId, default));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(vault.VaultRoot, "accounts", context.AccountId, "objects", "packs"), "*.rvpk"));
    }
}
