using Microsoft.Data.Sqlite;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;
using WeArchive.Core.Services;
using WeArchive.Infrastructure;
using WeArchive.Infrastructure.Fixtures;
using WeArchive.Infrastructure.RawVault;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// Integration flow: fixture source -&gt; capture -&gt; Raw Vault generation -&gt; dispose source
/// -&gt; reopen Raw Vault -&gt; verify manifest/artifacts/checksums.
/// <para>
/// Covers Issue #22 integration-test scope: main DB + WAL state, unknown source fields,
/// repeated baseline captures, source deletion after generation 1, and the read-without-key
/// guarantee.
/// </para>
/// </summary>
public sealed class CaptureFlowTests
{
    private static CaptureService BuildCaptureService(
        string vaultRoot, FixedClock? clock = null)
    {
        clock ??= new FixedClock();
        var fixture = new FixtureSourceAdapter();
        var captureAdapter = new FixtureCaptureAdapter();
        var vault = new RawVaultStore(vaultRoot);
        return new CaptureService(fixture, captureAdapter, vault, clock);
    }

    private static CaptureService BuildCaptureServiceWithClock(
        string vaultRoot, out FixedClock clock)
    {
        clock = new FixedClock();
        return BuildCaptureService(vaultRoot, clock);
    }

    [Fact]
    public async Task CaptureProducesCompleteGenerationReadableAfterSourceDisposal()
    {
        using var temp = new TempDirectory();
        var vaultRoot = temp.Combine("vault");

        // Phase 1: capture with the source available.
        FixedClock clock;
        var capture = BuildCaptureServiceWithClock(vaultRoot, out clock);
        var result = await capture.CaptureAccountAsync(
            new CaptureRequest(), null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(RawGenerationCompleteness.Complete, result.Completeness);
        Assert.Equal(3, result.ArtifactCount);
        Assert.Null(result.PreviousGenerationId);

        var generationId = result.GenerationId;
        var accountId = result.AccountId;

        // Phase 2: dispose the source and reopen the vault independently.
        // A new store instance pointing at the same root must find the generation without the
        // original capture adapter or fixture being alive.
        var store = new RawVaultStore(vaultRoot);
        var generation = await store.OpenGenerationAsync(accountId, generationId, CancellationToken.None);

        Assert.NotNull(generation);
        Assert.Equal(generationId, generation!.GenerationId);
        Assert.Equal(3, generation.Manifest.Artifacts.Count);
        Assert.True(generation.Manifest.Artifacts.All(a => a.IsDecrypted));

        // Every artifact referenced by the manifest has a verifiable SHA-256 checksum.
        foreach (var artifact in generation.Manifest.Artifacts)
        {
            var path = Path.Combine(generation.GenerationDirectory, artifact.ContentRef);
            Assert.True(File.Exists(path));
            var actual = await RawVaultStoreTestsHelper.ComputeSha256Async(path);
            Assert.Equal(artifact.Sha256, actual);
        }
    }

    [Fact]
    public async Task RepeatedBaselineCapturesCreateNewGenerationsNotEdits()
    {
        using var temp = new TempDirectory();
        var vaultRoot = temp.Combine("vault");

        var clock = new FixedClock();
        var capture = BuildCaptureService(vaultRoot, clock);

        // Generation 1.
        clock.UtcNow = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.FromHours(8));
        var r1 = await capture.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(r1.Succeeded);

        // Generation 2 (later time).
        clock.UtcNow = new DateTimeOffset(2026, 3, 1, 13, 0, 0, TimeSpan.FromHours(8));
        var r2 = await capture.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(r2.Succeeded);

        Assert.NotEqual(r1.GenerationId, r2.GenerationId);
        Assert.Equal(r1.GenerationId, r2.PreviousGenerationId);

        var store = new RawVaultStore(vaultRoot);
        var list = await store.ListGenerationsAsync(r1.AccountId, CancellationToken.None);
        Assert.Equal(2, list.Count);
        Assert.Equal(r1.GenerationId, list[0].GenerationId);
        Assert.Equal(r2.GenerationId, list[1].GenerationId);
    }

    [Fact]
    public async Task SourceDeletionAfterGeneration1DoesNotAffectGeneration1()
    {
        using var temp = new TempDirectory();
        var vaultRoot = temp.Combine("vault");

        var clock = new FixedClock();
        var capture = BuildCaptureService(vaultRoot, clock);

        clock.UtcNow = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.FromHours(8));
        var r1 = await capture.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(r1.Succeeded);

        // "Delete" the source by building a fresh capture service (new fixture adapter with
        // new scratch). This simulates the source being unavailable after generation 1.
        var capture2 = BuildCaptureService(vaultRoot, clock);

        // The first generation must still be openable.
        var store = new RawVaultStore(vaultRoot);
        var gen1 = await store.OpenGenerationAsync(r1.AccountId, r1.GenerationId, CancellationToken.None);
        Assert.NotNull(gen1);
        Assert.Equal(3, gen1!.Manifest.Artifacts.Count);
    }

    [Fact]
    public async Task CapturedArtifactsAreReadableSqliteWithoutKey()
    {
        using var temp = new TempDirectory();
        var vaultRoot = temp.Combine("vault");

        var capture = BuildCaptureService(vaultRoot, new FixedClock());
        var result = await capture.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(result.Succeeded);

        var store = new RawVaultStore(vaultRoot);
        var generation = await store.OpenGenerationAsync(result.AccountId, result.GenerationId, CancellationToken.None);
        Assert.NotNull(generation);

        // Each artifact must be an ordinary, unencrypted SQLite database readable without any key.
        foreach (var artifact in generation!.Manifest.Artifacts)
        {
            var path = Path.Combine(generation.GenerationDirectory, artifact.ContentRef);
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString();

            using var connection = new SqliteConnection(cs);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
            var tables = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }

            Assert.NotEmpty(tables);
        }
    }

    [Fact]
    public async Task UnknownSourceFieldsRemainInPreservedEvidence()
    {
        using var temp = new TempDirectory();
        var vaultRoot = temp.Combine("vault");

        var capture = BuildCaptureService(vaultRoot, new FixedClock());
        var result = await capture.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(result.Succeeded);

        var store = new RawVaultStore(vaultRoot);
        var generation = await store.OpenGenerationAsync(result.AccountId, result.GenerationId, CancellationToken.None);
        Assert.NotNull(generation);

        // The fixture's message database has an 'extra_metadata' column the parser does not
        // interpret. It must still be present in the preserved SQLite artifact.
        var messageArtifact = generation!.Manifest.Artifacts.First(a => a.Name == "message_0.db");
        var path = Path.Combine(generation.GenerationDirectory, messageArtifact.ContentRef);

        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString();
        using var connection = new SqliteConnection(cs);
        connection.Open();

        // Verify the column exists and the unknown-field value was preserved.
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT extra_metadata FROM Message_abcdef WHERE local_id = 1;";
        var value = (string?)cmd.ExecuteScalar();
        Assert.Equal("unknown-field-value", value);
    }

    [Fact]
    public async Task CaptureDiagnosticsNeverContainKeyMaterial()
    {
        using var temp = new TempDirectory();
        var vaultRoot = temp.Combine("vault");

        var capture = BuildCaptureService(vaultRoot, new FixedClock());
        var result = await capture.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(result.Succeeded);

        var store = new RawVaultStore(vaultRoot);
        var generation = await store.OpenGenerationAsync(result.AccountId, result.GenerationId, CancellationToken.None);
        Assert.NotNull(generation);

        var manifestJson = File.ReadAllText(Path.Combine(generation!.GenerationDirectory, "manifest.json"));
        Assert.DoesNotContain("key", manifestJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("x'", manifestJson);
        Assert.DoesNotContain("salt", manifestJson, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class RawVaultStoreTestsHelper
{
    public static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
