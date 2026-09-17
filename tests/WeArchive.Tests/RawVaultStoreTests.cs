using Microsoft.Data.Sqlite;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;
using WeArchive.Infrastructure.RawVault;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// Raw Vault store tests: artifact checksum verification, generation ordering/identity,
/// published-generation immutability guards, and invalid/incomplete manifest rejection.
/// Issue #22 acceptance criteria.
/// </summary>
public sealed class RawVaultStoreTests
{
    private static readonly DateTimeOffset T1 = new(2026, 3, 1, 12, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset T2 = T1.AddHours(1);
    private static readonly DateTimeOffset T3 = T2.AddHours(1);

    private static RawGenerationContext Context(string accountId, DateTimeOffset captureTime) => new()
    {
        AccountId = accountId,
        SourceProfileId = "wxid_test",
        CaptureTime = captureTime,
        CaptureAdapterFamily = "fixture",
        CaptureAdapterVersion = "1.0.0",
    };

    private static RawManifest BuildManifest(
        string generationId,
        string accountId,
        DateTimeOffset captureTime,
        IReadOnlyList<RawArtifactDescriptor> artifacts,
        string? previous = null) => new()
    {
        GenerationId = generationId,
        AccountId = accountId,
        SourceProfileId = "wxid_test",
        Source = new RawManifestSource
        {
            AdapterName = "fixture",
            AdapterVersion = "1.0.0",
        },
        Capture = new RawManifestCapture
        {
            CaptureTime = captureTime,
            CaptureAdapterFamily = "fixture",
            CaptureAdapterVersion = "1.0.0",
            Mode = RawCaptureMode.Baseline,
            Completeness = RawGenerationCompleteness.Complete,
            ArtifactCount = artifacts.Count,
        },
        Artifacts = artifacts,
        PreviousGenerationId = previous,
    };

    private static async Task<RawArtifactDescriptor> WriteArtifactAsync(
        IRawGenerationSession session,
        string name,
        byte[] content)
    {
        await using var stream = new System.IO.MemoryStream(content);
        return await session.WriteArtifactAsync(
            "source-database", name, stream, "sqlite", true, null, CancellationToken.None);
    }

    [Fact]
    public async Task ArtifactHasVerifiableSha256()
    {
        using var temp = new TempDirectory();
        var store = new RawVaultStore(temp.Combine("vault"));
        var content = "hello raw vault"u8.ToArray();
        var expectedHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();

        var session = await store.BeginGenerationAsync(Context("a_acct", T1), CancellationToken.None);
        var descriptor = await WriteArtifactAsync(session, "test.db", content);

        Assert.Equal(expectedHash, descriptor.Sha256);
        Assert.Equal(content.Length, descriptor.Size);
        Assert.True(descriptor.IsDecrypted);
        Assert.Equal("sqlite", descriptor.SourceFormat);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task PublishedGenerationIsOpenableWithVerifiedChecksums()
    {
        using var temp = new TempDirectory();
        var store = new RawVaultStore(temp.Combine("vault"));
        var accountId = "a_acct";

        var session = await store.BeginGenerationAsync(Context(accountId, T1), CancellationToken.None);
        var descriptor = await WriteArtifactAsync(session, "test.db", "data"u8.ToArray());
        var manifest = BuildManifest(session.GenerationId, accountId, T1, [descriptor]);
        await session.PublishAsync(manifest, CancellationToken.None);
        await session.DisposeAsync();

        var generation = await store.OpenGenerationAsync(accountId, session.GenerationId, CancellationToken.None);
        Assert.NotNull(generation);
        Assert.Equal(session.GenerationId, generation!.GenerationId);
        Assert.Single(generation.Manifest.Artifacts);
    }

    [Fact]
    public async Task TamperedArtifactIsRejected()
    {
        using var temp = new TempDirectory();
        var store = new RawVaultStore(temp.Combine("vault"));
        var accountId = "a_acct";

        var session = await store.BeginGenerationAsync(Context(accountId, T1), CancellationToken.None);
        var descriptor = await WriteArtifactAsync(session, "test.db", "original"u8.ToArray());
        var manifest = BuildManifest(session.GenerationId, accountId, T1, [descriptor]);
        await session.PublishAsync(manifest, CancellationToken.None);
        await session.DisposeAsync();

        // Tamper with the artifact file on disk.
        var artifactPath = System.IO.Path.Combine(
            generationDir(store, accountId, session.GenerationId), descriptor.ContentRef);
        await System.IO.File.WriteAllTextAsync(artifactPath, "tampered");

        var reopened = await store.OpenGenerationAsync(accountId, session.GenerationId, CancellationToken.None);
        Assert.Null(reopened);
    }

    [Fact]
    public async Task ImmutableGenerationCannotBeOverwritten()
    {
        using var temp = new TempDirectory();
        var store = new RawVaultStore(temp.Combine("vault"));
        var accountId = "a_acct";

        // Publish generation 1.
        var session1 = await store.BeginGenerationAsync(Context(accountId, T1), CancellationToken.None);
        var desc1 = await WriteArtifactAsync(session1, "a.db", "one"u8.ToArray());
        await session1.PublishAsync(BuildManifest(session1.GenerationId, accountId, T1, [desc1]), CancellationToken.None);
        await session1.DisposeAsync();

        // A second capture at the exact same instant derives the same generation id; publishing
        // must be refused because the generation already exists (immutability guard).
        var session2 = await store.BeginGenerationAsync(Context(accountId, T1), CancellationToken.None);
        Assert.Equal(session1.GenerationId, session2.GenerationId);
        var desc2 = await WriteArtifactAsync(session2, "a.db", "two"u8.ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session2.PublishAsync(BuildManifest(session2.GenerationId, accountId, T1, [desc2]), CancellationToken.None));
        await session2.DisposeAsync();
    }

    [Fact]
    public async Task LaterCaptureDoesNotEditEarlierGeneration()
    {
        using var temp = new TempDirectory();
        var store = new RawVaultStore(temp.Combine("vault"));
        var accountId = "a_acct";

        // Generation 1.
        var session1 = await store.BeginGenerationAsync(Context(accountId, T1), CancellationToken.None);
        var desc1 = await WriteArtifactAsync(session1, "a.db", "gen1"u8.ToArray());
        var gen1Manifest = BuildManifest(session1.GenerationId, accountId, T1, [desc1]);
        await session1.PublishAsync(gen1Manifest, CancellationToken.None);
        await session1.DisposeAsync();

        // Generation 2 (later capture time).
        var session2 = await store.BeginGenerationAsync(Context(accountId, T2), CancellationToken.None);
        var desc2 = await WriteArtifactAsync(session2, "a.db", "gen2"u8.ToArray());
        var gen2Manifest = BuildManifest(session2.GenerationId, accountId, T2, [desc2], session1.GenerationId);
        await session2.PublishAsync(gen2Manifest, CancellationToken.None);
        await session2.DisposeAsync();

        // Generation 1 must be untouched: same checksum, same manifest.
        var gen1 = await store.OpenGenerationAsync(accountId, session1.GenerationId, CancellationToken.None);
        Assert.NotNull(gen1);
        Assert.Equal("gen1"u8.ToArray().Length, gen1!.Manifest.Artifacts[0].Size);
        Assert.Equal(desc1.Sha256, gen1.Manifest.Artifacts[0].Sha256);
        Assert.Null(gen1.Manifest.PreviousGenerationId);

        // Generation 2 links to generation 1.
        var gen2 = await store.OpenGenerationAsync(accountId, session2.GenerationId, CancellationToken.None);
        Assert.NotNull(gen2);
        Assert.Equal(session1.GenerationId, gen2!.Manifest.PreviousGenerationId);
    }

    [Fact]
    public async Task GenerationsAreOrderedByCaptureTime()
    {
        using var temp = new TempDirectory();
        var store = new RawVaultStore(temp.Combine("vault"));
        var accountId = "a_acct";

        // Publish out of order: T3, T1, T2.
        foreach (var time in new[] { T3, T1, T2 })
        {
            var session = await store.BeginGenerationAsync(Context(accountId, time), CancellationToken.None);
            var desc = await WriteArtifactAsync(session, "a.db", time.Ticks.ToString().GetBytes());
            await session.PublishAsync(BuildManifest(session.GenerationId, accountId, time, [desc]), CancellationToken.None);
            await session.DisposeAsync();
        }

        var list = await store.ListGenerationsAsync(accountId, CancellationToken.None);
        Assert.Equal(3, list.Count);
        Assert.Equal(T1, list[0].CaptureTime);
        Assert.Equal(T2, list[1].CaptureTime);
        Assert.Equal(T3, list[2].CaptureTime);

        var latest = await store.GetLatestGenerationAsync(accountId, CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal(T3, latest!.CaptureTime);
    }

    [Fact]
    public async Task DiscardedStagingLeavesNoDiscoverableGeneration()
    {
        using var temp = new TempDirectory();
        var store = new RawVaultStore(temp.Combine("vault"));
        var accountId = "a_acct";

        var session = await store.BeginGenerationAsync(Context(accountId, T1), CancellationToken.None);
        await WriteArtifactAsync(session, "a.db", "data"u8.ToArray());
        await session.DiscardAsync();
        await session.DisposeAsync();

        var list = await store.ListGenerationsAsync(accountId, CancellationToken.None);
        Assert.Empty(list);
    }

    [Fact]
    public async Task KeyNonPersistence_ManifestContainsNoSecrets()
    {
        using var temp = new TempDirectory();
        var store = new RawVaultStore(temp.Combine("vault"));
        var accountId = "a_acct";

        var session = await store.BeginGenerationAsync(Context(accountId, T1), CancellationToken.None);
        var desc = await WriteArtifactAsync(session, "test.db", "plaintext-data"u8.ToArray());
        var manifest = BuildManifest(session.GenerationId, accountId, T1, [desc]);
        await session.PublishAsync(manifest, CancellationToken.None);
        await session.DisposeAsync();

        var gen = await store.OpenGenerationAsync(accountId, session.GenerationId, CancellationToken.None);
        Assert.NotNull(gen);

        var manifestJson = System.IO.File.ReadAllText(
            System.IO.Path.Combine(generationDir(store, accountId, session.GenerationId), "manifest.json"));

        // No key material may appear anywhere in the manifest or artifact metadata.
        Assert.DoesNotContain("key", manifestJson, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("x'", manifestJson);
        Assert.True(gen!.Manifest.Artifacts[0].IsDecrypted);
    }

    [Fact]
    public async Task UnpublishedGenerationWithoutManifestIsNotListed()
    {
        using var temp = new TempDirectory();
        var store = new RawVaultStore(temp.Combine("vault"));
        var accountId = "a_acct";

        // Begin but never publish; dispose to simulate a crash.
        var session = await store.BeginGenerationAsync(Context(accountId, T1), CancellationToken.None);
        await WriteArtifactAsync(session, "a.db", "data"u8.ToArray());
        await session.DisposeAsync();

        var list = await store.ListGenerationsAsync(accountId, CancellationToken.None);
        Assert.Empty(list);
    }

    private static string generationDir(IRawVaultStore store, string accountId, string generationId) =>
        System.IO.Path.Combine(
            store.VaultRoot, "accounts", accountId, "generations", generationId);
}

internal static class StringByteArrayExtensions
{
    public static byte[] GetBytes(this string s) => System.Text.Encoding.UTF8.GetBytes(s);
}
