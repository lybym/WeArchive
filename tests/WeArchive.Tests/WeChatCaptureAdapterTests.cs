using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;
using WeArchive.Core.Services;
using WeArchive.Infrastructure.Fixtures;
using WeArchive.Infrastructure.RawVault;
using WeArchive.Infrastructure.WeChat;
using WeArchive.Infrastructure.WeChat.KeyAcquisition;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// Issue #25 acceptance criteria exercised against the shipped WeChat capture adapter through its
/// environment seam. The incremental decision — partition fingerprinting, predecessor mapping,
/// artifact reuse, key gating and the post-snapshot consistency recheck — is covered without a
/// live WeChat client or a real database key, which is what docs/PRD.md NFR-06 requires of the
/// capture layer. The real-environment run remains tracked as an outstanding M1.5 item.
/// </summary>
public sealed class WeChatCaptureAdapterTests
{
    [Fact]
    public void DefaultConstructionUsesTheProductionEnvironment()
    {
        using var adapter = new WeChatCaptureAdapter();
        Assert.Equal(WeChatCaptureAdapter.Family, adapter.CaptureAdapterFamily);
        Assert.Equal(WeChatCaptureAdapter.Version, adapter.CaptureAdapterVersion);
    }

    [Fact]
    public async Task UnchangedPartitionsAreReusedWithoutAcquiringAKeyOrDecrypting()
    {
        using var source = new TempDirectory();
        using var vaultDirectory = new TempDirectory();
        var session = CreateSourceFile(source, "session/session.db", "session-1");
        var message = CreateSourceFile(source, "message/message_0.db", "message-1");
        var environment = CreateEnvironment(source, session, message);
        var keys = new RecordingKeyAcquirer();
        using var adapter = new WeChatCaptureAdapter(keys, environment);
        var (service, vault, clock) = CreateService(vaultDirectory, adapter);

        var baseline = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(baseline.Succeeded);
        Assert.Equal(RawCaptureMode.Baseline, baseline.Mode);
        Assert.Equal(2, baseline.Coverage.Count(c => c.Status == RawPartitionStatus.Captured));
        Assert.Equal(1, keys.AcquireCount);
        Assert.Equal(1, environment.CreateMaterializerCalls);
        Assert.Equal(2, environment.GetPlaintextCalls);

        var sourceBefore = await File.ReadAllBytesAsync(session);
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var incremental = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);

        Assert.True(incremental.Succeeded);
        Assert.Equal(RawCaptureMode.Incremental, incremental.Mode);
        Assert.Equal(RawGenerationCompleteness.Complete, incremental.Completeness);
        Assert.Equal(2, incremental.Coverage.Count(c => c.Status == RawPartitionStatus.Reused));
        Assert.DoesNotContain(incremental.Diagnostics, d => d.Code == DiagnosticCodes.CaptureFullFallback);

        // Reuse is proven from the source, not assumed: no key was acquired, nothing was
        // decrypted again, and the live source is byte-for-byte unchanged (read-only source).
        Assert.Equal(1, keys.AcquireCount);
        Assert.Equal(1, environment.CreateMaterializerCalls);
        Assert.Equal(2, environment.GetPlaintextCalls);
        Assert.True(sourceBefore.SequenceEqual(await File.ReadAllBytesAsync(session)));

        // The checkpoint advanced to the new generation; the previous generation is untouched.
        var generation1 = await vault.OpenGenerationAsync(baseline.AccountId, baseline.GenerationId, CancellationToken.None);
        Assert.NotNull(generation1);
        Assert.Equal(baseline.GenerationId, generation1!.Manifest.CaptureCheckpoint!.GenerationId);
        var generation2 = await vault.OpenGenerationAsync(baseline.AccountId, incremental.GenerationId, CancellationToken.None);
        Assert.NotNull(generation2);
        Assert.Equal(incremental.GenerationId, generation2!.Manifest.CaptureCheckpoint!.GenerationId);
    }

    [Fact]
    public async Task ChangedDatabaseIsReacquiredWhileTheUnchangedOneIsReused()
    {
        using var source = new TempDirectory();
        using var vaultDirectory = new TempDirectory();
        var session = CreateSourceFile(source, "session/session.db", "session-1");
        var message = CreateSourceFile(source, "message/message_0.db", "message-1");
        var environment = CreateEnvironment(source, session, message);
        var keys = new RecordingKeyAcquirer();
        using var adapter = new WeChatCaptureAdapter(keys, environment);
        var (service, _, clock) = CreateService(vaultDirectory, adapter);

        var baseline = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(baseline.Succeeded);

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        await File.WriteAllTextAsync(message, "message-2");
        var incremental = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);

        Assert.True(incremental.Succeeded);
        Assert.Equal(RawCaptureMode.Incremental, incremental.Mode);
        Assert.Single(incremental.Coverage, c =>
            c.PartitionId == "session/session.db" && c.Status == RawPartitionStatus.Reused);
        Assert.Single(incremental.Coverage, c =>
            c.PartitionId == "message/message_0.db" && c.Status == RawPartitionStatus.Captured);
        Assert.Equal(2, keys.AcquireCount);
        Assert.Equal(3, environment.GetPlaintextCalls);
    }

    [Fact]
    public async Task WalOnlyChangeForcesReacquisitionButShmChangeDoesNot()
    {
        using var source = new TempDirectory();
        using var vaultDirectory = new TempDirectory();
        var database = CreateSourceFile(source, "message/message_0.db", "message-1");
        var wal = database + "-wal";
        await File.WriteAllTextAsync(wal, "wal-1");
        var environment = CreateEnvironment(source, database);
        var keys = new RecordingKeyAcquirer();
        using var adapter = new WeChatCaptureAdapter(keys, environment);
        var (service, _, clock) = CreateService(vaultDirectory, adapter);

        var baseline = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(baseline.Succeeded);

        // The volatile -shm index changes without any content change and must not force a
        // reacquisition.
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        await File.WriteAllTextAsync(database + "-shm", "shm-1");
        var afterShm = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(afterShm.Succeeded);
        Assert.Single(afterShm.Coverage, c => c.Status == RawPartitionStatus.Reused);

        // Committed WAL bytes are source evidence: a WAL-only change is a real change.
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        await File.WriteAllTextAsync(wal, "wal-2");
        var afterWal = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(afterWal.Succeeded);
        Assert.Single(afterWal.Coverage, c => c.Status == RawPartitionStatus.Captured);
        Assert.Equal(2, keys.AcquireCount);
    }

    [Fact]
    public async Task UnmappablePredecessorEvidenceWidensToFullCapture()
    {
        using var source = new TempDirectory();
        using var vaultDirectory = new TempDirectory();
        var session = CreateSourceFile(source, "session/session.db", "session-1");
        var environment = CreateEnvironment(source, session);
        var keys = new RecordingKeyAcquirer();
        using var adapter = new WeChatCaptureAdapter(keys, environment);
        var (service, vault, clock) = CreateService(vaultDirectory, adapter);

        var baseline = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(baseline.Succeeded);

        // Keep the predecessor a valid, complete, checkpoint-carrying generation, but remove the
        // evidence that maps an artifact to the live partition it preserves. The adapter must not
        // reuse it: the generation is still accepted by the service, so this exercises the
        // adapter's own precondition rather than the service's.
        await RewriteManifestAsync(vault, baseline.AccountId, baseline.GenerationId, manifest => manifest with
        {
            Artifacts = [.. manifest.Artifacts.Select(a => a with { Metadata = null })],
        });

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var second = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);

        Assert.True(second.Succeeded);
        Assert.Equal(RawCaptureMode.Baseline, second.Mode);
        Assert.Contains(second.Diagnostics, d => d.Code == DiagnosticCodes.CaptureFullFallback);
        Assert.Single(second.Coverage, c => c.Status == RawPartitionStatus.Captured);
        Assert.Equal(2, keys.AcquireCount);
    }

    [Fact]
    public async Task DisappearedPartitionIsReportedPartialWithoutAdvancingTheCheckpoint()
    {
        using var source = new TempDirectory();
        using var vaultDirectory = new TempDirectory();
        var session = CreateSourceFile(source, "session/session.db", "session-1");
        var message = CreateSourceFile(source, "message/message_0.db", "message-1");
        var environment = CreateEnvironment(source, session, message);
        var keys = new RecordingKeyAcquirer();
        using var adapter = new WeChatCaptureAdapter(keys, environment);
        var (service, vault, clock) = CreateService(vaultDirectory, adapter);

        var baseline = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(baseline.Succeeded);

        // The live source loses a partition. Its evidence must never be deleted, and the run must
        // not publish a checkpoint that claims complete coverage.
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        File.Delete(message);
        environment.Databases.Remove(message);
        var second = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);

        Assert.True(second.Succeeded);
        Assert.Equal(RawGenerationCompleteness.Partial, second.Completeness);
        Assert.Single(second.Coverage, c => c.PartitionId == "session/session.db" && c.Status == RawPartitionStatus.Reused);
        Assert.Single(second.Coverage, c =>
            c.PartitionId == "message/message_0.db"
            && c.Status == RawPartitionStatus.Unavailable
            && !string.IsNullOrWhiteSpace(c.Diagnostic));

        var generation2 = await vault.OpenGenerationAsync(baseline.AccountId, second.GenerationId, CancellationToken.None);
        Assert.NotNull(generation2);
        Assert.Null(generation2!.Manifest.CaptureCheckpoint);
        // The earlier generation, and the artifact it still requires, remain intact.
        var generation1 = await vault.OpenGenerationAsync(baseline.AccountId, baseline.GenerationId, CancellationToken.None);
        Assert.NotNull(generation1);
        Assert.Contains(generation1!.Manifest.Artifacts, a => a.Name == "message_0.db");
    }

    [Fact]
    public async Task SourceChangeDuringCaptureIsFatalAndPublishesNothing()
    {
        using var source = new TempDirectory();
        using var vaultDirectory = new TempDirectory();
        var database = CreateSourceFile(source, "message/message_0.db", "message-1");
        var environment = CreateEnvironment(source, database);
        var keys = new RecordingKeyAcquirer();
        using var adapter = new WeChatCaptureAdapter(keys, environment);
        var (service, vault, clock) = CreateService(vaultDirectory, adapter);

        var baseline = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(baseline.Succeeded);

        // Mutate the source after its evidence was snapshotted but before the consistency recheck,
        // so neither the copied predecessor artifact nor the new image can be claimed as current.
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var mutated = false;
        var progress = new InlineProgress<CaptureProgress>(report =>
        {
            if (!mutated && report.Total > 0 && report.Processed == report.Total)
            {
                mutated = true;
                File.WriteAllText(database, "changed-during-capture");
            }
        });

        var second = await service.CaptureAccountAsync(new CaptureRequest(), progress, CancellationToken.None);

        Assert.True(mutated);
        Assert.False(second.Succeeded);
        await AssertLatestCheckpointUnchangedAsync(vault, baseline);
    }

    [Fact]
    public async Task RequiredChangeWithClientNotRunningIsFatalAndPublishesNothing()
    {
        using var source = new TempDirectory();
        using var vaultDirectory = new TempDirectory();
        var database = CreateSourceFile(source, "message/message_0.db", "message-1");
        var environment = CreateEnvironment(source, database);
        var keys = new RecordingKeyAcquirer();
        using var adapter = new WeChatCaptureAdapter(keys, environment);
        var (service, vault, clock) = CreateService(vaultDirectory, adapter);

        var baseline = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(baseline.Succeeded);
        Assert.Equal(1, keys.AcquireCount);

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        await File.WriteAllTextAsync(database, "message-2");
        environment.ClientRunning = false;
        var second = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);

        Assert.False(second.Succeeded);
        // No key is even attempted without a running client, and nothing is published.
        Assert.Equal(1, keys.AcquireCount);
        Assert.Contains(second.Diagnostics, d => d.Code == DiagnosticCodes.KeyAcquisitionFailed);
        await AssertLatestCheckpointUnchangedAsync(vault, baseline);
    }

    [Fact]
    public async Task KeyAcquisitionFailureIsFatalAndPublishesNothing()
    {
        using var source = new TempDirectory();
        using var vaultDirectory = new TempDirectory();
        var database = CreateSourceFile(source, "message/message_0.db", "message-1");
        var environment = CreateEnvironment(source, database);
        var keys = new RecordingKeyAcquirer { FailureMessage = "synthetic key failure" };
        using var adapter = new WeChatCaptureAdapter(keys, environment);
        var (service, vault, _) = CreateService(vaultDirectory, adapter);

        var result = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("synthetic key failure", result.FailureMessage);
        var latest = await vault.GetLatestGenerationAsync(result.AccountId, CancellationToken.None);
        Assert.Null(latest);
    }

    [Fact]
    public async Task RejectedWalFramesMakeTheRunPartialWithoutAdvancingTheCheckpoint()
    {
        using var source = new TempDirectory();
        using var vaultDirectory = new TempDirectory();
        var database = CreateSourceFile(source, "message/message_0.db", "message-1");
        var environment = CreateEnvironment(source, database);
        var keys = new RecordingKeyAcquirer();
        using var adapter = new WeChatCaptureAdapter(keys, environment);
        var (service, vault, clock) = CreateService(vaultDirectory, adapter);

        var baseline = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(baseline.Succeeded);

        // A torn/stale WAL frame leaves a snapshot that is consistent only up to the last committed
        // transaction, so the run is partial and must not advance the capture checkpoint.
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        await File.WriteAllTextAsync(database, "message-2");
        environment.WalFramesRejected = 3;
        var partial = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);

        Assert.True(partial.Succeeded);
        Assert.Equal(RawGenerationCompleteness.Partial, partial.Completeness);
        Assert.Contains(partial.Diagnostics, d => d.Code == DiagnosticCodes.WalFramesRejected);
        var partialGeneration = await vault.OpenGenerationAsync(baseline.AccountId, partial.GenerationId, CancellationToken.None);
        Assert.NotNull(partialGeneration);
        Assert.Null(partialGeneration!.Manifest.CaptureCheckpoint);

        // With no checkpoint the next run reads the whole source again and can publish one.
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        environment.WalFramesRejected = 0;
        var recovered = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(recovered.Succeeded);
        Assert.Equal(RawCaptureMode.Baseline, recovered.Mode);
        Assert.Equal(RawGenerationCompleteness.Complete, recovered.Completeness);
        Assert.Contains(recovered.Diagnostics, d => d.Code == DiagnosticCodes.CaptureFullFallback);
    }

    [Fact]
    public void PriorMapRejectsEveryUnsafePredecessorShape()
    {
        var valid = Predecessor();
        var prior = WeChatCaptureAdapter.BuildPriorMap(valid);
        Assert.NotNull(prior);
        Assert.Single(prior!);
        Assert.Contains("p1", prior!.Keys);

        Assert.Null(WeChatCaptureAdapter.BuildPriorMap(null));
        Assert.Null(WeChatCaptureAdapter.BuildPriorMap(WithManifest(valid, m => m with { ManifestVersion = 1 })));
        Assert.Null(WeChatCaptureAdapter.BuildPriorMap(WithManifest(valid, m => m with { CaptureCheckpoint = null })));
        Assert.Null(WeChatCaptureAdapter.BuildPriorMap(WithManifest(valid, m => m with
        {
            CaptureCheckpoint = m.CaptureCheckpoint! with { Version = 2 },
        })));
        Assert.Null(WeChatCaptureAdapter.BuildPriorMap(WithManifest(valid, m => m with
        {
            CaptureCheckpoint = m.CaptureCheckpoint! with { GenerationId = "gen_other" },
        })));
        Assert.Null(WeChatCaptureAdapter.BuildPriorMap(WithManifest(valid, m => m with
        {
            CaptureCheckpoint = m.CaptureCheckpoint! with { CaptureAdapterFamily = "other" },
        })));
        Assert.Null(WeChatCaptureAdapter.BuildPriorMap(WithManifest(valid, m => m with
        {
            CaptureCheckpoint = m.CaptureCheckpoint! with { CaptureAdapterVersion = "9.9.9" },
        })));
        // A partial predecessor cannot establish that its coverage is still current.
        Assert.Null(WeChatCaptureAdapter.BuildPriorMap(WithManifest(valid, m => m with
        {
            Capture = m.Capture with { Completeness = RawGenerationCompleteness.Partial },
        })));
        // Evidence that cannot be mapped unambiguously is never reused.
        Assert.Null(WeChatCaptureAdapter.BuildPriorMap(Predecessor(
            artifacts: [Artifact("p1", "sha1") with { Metadata = null }])));
        Assert.Null(WeChatCaptureAdapter.BuildPriorMap(Predecessor(
            artifacts: [Artifact("p1", "sha1") with { Metadata = EmptyMetadata(" ") }])));
        Assert.Null(WeChatCaptureAdapter.BuildPriorMap(Predecessor(
            artifacts: [Artifact("same/path.db", "sha1"), Artifact("same/path.db", "sha2")],
            coverage: [Covered("p1", "fp1", "sha1"), Covered("p2", "fp2", "sha2")],
            fingerprints: new Dictionary<string, string> { ["p1"] = "fp1", ["p2"] = "fp2" })));
        // Coverage that disagrees with the checkpoint or with the artifact it names.
        Assert.Null(WeChatCaptureAdapter.BuildPriorMap(Predecessor(
            fingerprints: new Dictionary<string, string> { ["p1"] = "fp-other" })));
        Assert.Null(WeChatCaptureAdapter.BuildPriorMap(Predecessor(
            coverage: [Covered("p1", "fp1", "sha-other")])));
        Assert.Null(WeChatCaptureAdapter.BuildPriorMap(Predecessor(
            coverage: [Covered("p1", "fp1", "sha1") with { Status = RawPartitionStatus.Unavailable }])));
        Assert.Null(WeChatCaptureAdapter.BuildPriorMap(Predecessor(
            coverage: [Covered("p1", "fp1", "sha1") with { SourceFingerprint = null }])));
        // An artifact count that does not match the recorded coverage is ambiguous.
        Assert.Null(WeChatCaptureAdapter.BuildPriorMap(Predecessor(
            artifacts: [Artifact("p1", "sha1"), Artifact("p2", "sha2")])));
    }

    private static (CaptureService Service, RawVaultStore Vault, FixedClock Clock) CreateService(
        TempDirectory vaultDirectory,
        WeChatCaptureAdapter adapter)
    {
        var vault = new RawVaultStore(vaultDirectory.Combine("vault"));
        var clock = new FixedClock();
        var service = new CaptureService(new FixtureSourceAdapter(), adapter, vault, clock);
        return (service, vault, clock);
    }

    private static FixtureWeChatEnvironment CreateEnvironment(TempDirectory source, params string[] databases)
    {
        var environment = new FixtureWeChatEnvironment
        {
            AccountDirectory = source.Path,
            DatabaseDirectory = source.Path,
        };
        environment.Databases.AddRange(databases);
        return environment;
    }

    private static string CreateSourceFile(TempDirectory source, string relativePath, string content)
    {
        var path = source.Combine(relativePath.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Republishes a generation's manifest, leaving the artifacts on disk untouched.</summary>
    private static async Task RewriteManifestAsync(
        RawVaultStore vault,
        string accountId,
        string generationId,
        Func<RawManifest, RawManifest> mutate)
    {
        var generation = await vault.OpenGenerationAsync(accountId, generationId, CancellationToken.None);
        if (generation is null)
        {
            throw new InvalidOperationException("The generation under test must be published and readable.");
        }

        var json = RawManifestSerializer.Serialize(mutate(generation.Manifest));
        await File.WriteAllTextAsync(Path.Combine(generation.GenerationDirectory, "manifest.json"), json);
    }

    /// <summary>The latest generation is still the previous one and still carries its checkpoint.</summary>
    private static async Task AssertLatestCheckpointUnchangedAsync(RawVaultStore vault, CaptureResult expected)
    {
        var latest = await vault.GetLatestGenerationAsync(expected.AccountId, CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal(expected.GenerationId, latest!.GenerationId);

        var reopened = await vault.OpenGenerationAsync(expected.AccountId, expected.GenerationId, CancellationToken.None);
        Assert.NotNull(reopened);
        Assert.Equal(expected.GenerationId, reopened!.Manifest.CaptureCheckpoint!.GenerationId);
    }

    private static RawGeneration WithManifest(RawGeneration generation, Func<RawManifest, RawManifest> mutate) =>
        generation with { Manifest = mutate(generation.Manifest) };

    private static RawArtifactDescriptor Artifact(string relativePath, string sha256) => new()
    {
        Role = "source-database",
        Name = Path.GetFileName(relativePath),
        ContentRef = $"artifacts/{sha256}.db",
        Sha256 = sha256,
        Size = 16,
        SourceFormat = "sqlite",
        IsDecrypted = true,
        Metadata = new Dictionary<string, string> { ["source_relative_path"] = relativePath },
    };

    private static IReadOnlyDictionary<string, string> EmptyMetadata(string value) =>
        new Dictionary<string, string> { ["source_relative_path"] = value };

    private static RawPartitionCoverage Covered(string partitionId, string fingerprint, string sha256) => new()
    {
        PartitionId = partitionId,
        Status = RawPartitionStatus.Captured,
        SourceFingerprint = fingerprint,
        ArtifactSha256 = sha256,
    };

    private static RawGeneration Predecessor(
        IReadOnlyList<RawArtifactDescriptor>? artifacts = null,
        IReadOnlyList<RawPartitionCoverage>? coverage = null,
        IReadOnlyDictionary<string, string>? fingerprints = null) => new()
    {
        GenerationId = "gen_test",
        AccountId = "a_test",
        GenerationDirectory = Path.Combine(Path.GetTempPath(), "wechat-predecessor-not-read"),
        Manifest = new RawManifest
        {
            GenerationId = "gen_test",
            AccountId = "a_test",
            SourceProfileId = "wxid_test",
            Source = new RawManifestSource
            {
                AdapterName = "wechat-windows",
                AdapterVersion = "0.1.0",
                SourceProductName = "WeChat for Windows",
                SourceVersion = "4.1.13.12",
            },
            Capture = new RawManifestCapture
            {
                CaptureTime = FixedClock.Default,
                CaptureAdapterFamily = WeChatCaptureAdapter.Family,
                CaptureAdapterVersion = WeChatCaptureAdapter.Version,
                Mode = RawCaptureMode.Incremental,
                Completeness = RawGenerationCompleteness.Complete,
                ArtifactCount = 1,
            },
            Artifacts = artifacts ?? [Artifact("p1", "sha1")],
            Coverage = coverage ?? [Covered("p1", "fp1", "sha1")],
            CaptureCheckpoint = new RawCaptureCheckpoint
            {
                GenerationId = "gen_test",
                CaptureAdapterFamily = WeChatCaptureAdapter.Family,
                CaptureAdapterVersion = WeChatCaptureAdapter.Version,
                PartitionFingerprints = fingerprints ?? new Dictionary<string, string> { ["p1"] = "fp1" },
            },
        },
    };

    /// <summary>Reports progress inline so a test can act between two adapter steps.</summary>
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private sealed class RecordingKeyAcquirer : IWeChatDatabaseKeyAcquirer
    {
        public int AcquireCount { get; private set; }

        public string? FailureMessage { get; set; }

        public WeChatDatabaseKeyAcquisitionResult Acquire(IReadOnlyList<string> databasePaths)
        {
            AcquireCount++;
            return FailureMessage is null
                ? WeChatDatabaseKeyAcquisitionResult.Success(new WeChatKeySet([]), "synthetic key set")
                : WeChatDatabaseKeyAcquisitionResult.Failure(FailureMessage);
        }
    }

    private sealed class FixtureWeChatEnvironment : IWeChatCaptureEnvironment
    {
        public List<string> Databases { get; } = [];

        public string AccountDirectory { get; init; } = string.Empty;

        public string DatabaseDirectory { get; init; } = string.Empty;

        public string SourceProfileId { get; init; } = "wxid_test";

        public string? ClientVersion { get; set; } = "4.1.13.12";

        public bool ClientRunning { get; set; } = true;

        public int WalFramesRejected { get; set; }

        public int CreateMaterializerCalls { get; private set; }

        public int GetPlaintextCalls { get; private set; }

        public IReadOnlyList<WeChatAccountLocation> DiscoverAccounts() =>
            [new WeChatAccountLocation(SourceProfileId, AccountDirectory, DatabaseDirectory, null)];

        public IReadOnlyList<string> EnumerateDatabases(WeChatAccountLocation account)
        {
            Assert.Equal(SourceProfileId, account.SourceProfileId);
            return Databases;
        }

        public string? DetectClientVersion() => ClientVersion;

        public bool IsClientRunning() => ClientRunning;

        /// <summary>The key set the adapter asked this environment to materialize with.</summary>
        public WeChatKeySet? LastKeySet { get; private set; }

        public IWeChatSourceMaterializer CreateMaterializer(WeChatKeySet keys)
        {
            CreateMaterializerCalls++;
            LastKeySet = keys;
            return new FixtureMaterializer(this);
        }

        private sealed class FixtureMaterializer(FixtureWeChatEnvironment owner) : IWeChatSourceMaterializer
        {
            public DecryptionOutcome GetPlaintext(string databasePath)
            {
                owner.GetPlaintextCalls++;

                // The fixture source is already readable plaintext, so the "materialized" image is
                // the source file itself; only the WAL evidence is synthetic.
                return new DecryptionOutcome(databasePath, 4, 0, owner.WalFramesRejected, true);
            }

            public void Dispose()
            {
            }
        }
    }
}