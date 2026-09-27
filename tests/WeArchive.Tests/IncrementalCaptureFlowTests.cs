using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;
using WeArchive.Core.Services;
using WeArchive.Infrastructure.Fixtures;
using WeArchive.Infrastructure.RawVault;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// Issue #25 acceptance criteria: after a complete baseline a later capture decides supported
/// unchanged vs new/changed partitions, only new/changed evidence is physically reacquired,
/// an unprovable incremental condition widens to a full consistent snapshot, every generation
/// records explicit partition coverage, and the capture checkpoint advances only with a
/// successfully published complete generation — independently of canonical ingest progress.
/// </summary>
public sealed class IncrementalCaptureFlowTests
{
    [Fact]
    public async Task SourceSequencePreservesHistoryAndAdvancesOnlyCompleteCheckpoint()
    {
        using var temp = new TempDirectory();
        var (service, vault, adapter, clock) = CreateHarness(temp.Combine("vault"));

        adapter.Partitions["A"] = "a";
        adapter.Partitions["B"] = "b";
        adapter.Partitions["C"] = "c";
        adapter.Partitions["D"] = "d";

        var first = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(first.Succeeded);
        Assert.Equal(RawCaptureMode.Baseline, first.Mode);
        Assert.Equal(RawGenerationCompleteness.Complete, first.Completeness);
        var generation1 = await vault.OpenGenerationAsync(first.AccountId, first.GenerationId, CancellationToken.None);
        Assert.NotNull(generation1);
        Assert.NotNull(generation1!.Manifest.CaptureCheckpoint);

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        adapter.Partitions["E"] = "e";
        var second = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(second.Succeeded);
        Assert.Equal(RawCaptureMode.Incremental, second.Mode);
        Assert.Equal(4, second.Coverage.Count(c => c.Status == RawPartitionStatus.Reused));
        Assert.Single(second.Coverage, c => c.PartitionId == "E" && c.Status == RawPartitionStatus.Captured);

        // T3: A D E. B and C disappear from the live source; the earlier generations that still
        // require them must remain intact and readable.
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        adapter.Partitions.Remove("B");
        adapter.Partitions.Remove("C");
        var third = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(third.Succeeded);
        Assert.Equal(RawGenerationCompleteness.Partial, third.Completeness);
        Assert.Equal(2, third.Coverage.Count(c => c.Status == RawPartitionStatus.Unavailable));
        var generation3 = await vault.OpenGenerationAsync(third.AccountId, third.GenerationId, CancellationToken.None);
        Assert.NotNull(generation3);
        Assert.Null(generation3!.Manifest.CaptureCheckpoint);
        Assert.Contains(generation3.Manifest.Artifacts, a => a.Name == "E.db");

        var generation2 = await vault.OpenGenerationAsync(first.AccountId, second.GenerationId, CancellationToken.None);
        Assert.NotNull(generation2);
        Assert.Contains(generation2!.Manifest.Artifacts, a => a.Name == "B.db");
        Assert.Contains(generation2.Manifest.Artifacts, a => a.Name == "C.db");
        Assert.NotNull(await vault.OpenGenerationAsync(first.AccountId, first.GenerationId, CancellationToken.None));
    }

    [Fact]
    public async Task NoChangeCapturePublishesNewGenerationThatReusesVerifiedEvidence()
    {
        using var temp = new TempDirectory();
        var (service, vault, adapter, clock) = CreateHarness(temp.Combine("vault"));

        adapter.Partitions["A"] = "a";
        adapter.Partitions["B"] = "b";

        var first = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(first.Succeeded);
        var generation1 = await vault.OpenGenerationAsync(first.AccountId, first.GenerationId, CancellationToken.None);
        Assert.NotNull(generation1);

        clock.UtcNow = clock.UtcNow.AddMinutes(5);
        var second = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(second.Succeeded);
        Assert.Equal(RawCaptureMode.Incremental, second.Mode);
        Assert.Equal(RawGenerationCompleteness.Complete, second.Completeness);
        Assert.All(second.Coverage, c => Assert.Equal(RawPartitionStatus.Reused, c.Status));

        var generation2 = await vault.OpenGenerationAsync(first.AccountId, second.GenerationId, CancellationToken.None);
        Assert.NotNull(generation2);
        // The checkpoint advances to the newly published generation and keeps the same
        // fingerprint evidence, because the source partitions were verified as unchanged.
        Assert.Equal(second.GenerationId, generation2!.Manifest.CaptureCheckpoint!.GenerationId);
        Assert.Equal(
            Fingerprints(generation1!.Manifest.CaptureCheckpoint!),
            Fingerprints(generation2.Manifest.CaptureCheckpoint!));

        // Earlier generations are never edited by a later capture.
        var reopened = await vault.OpenGenerationAsync(first.AccountId, first.GenerationId, CancellationToken.None);
        Assert.NotNull(reopened);
        Assert.Equal(first.GenerationId, reopened!.Manifest.CaptureCheckpoint!.GenerationId);
        Assert.Equal(first.GenerationId, reopened.Manifest.CaptureCheckpoint.GenerationId);
    }

    [Fact]
    public async Task ChangedPartitionIsReacquiredInsteadOfReused()
    {
        using var temp = new TempDirectory();
        var (service, _, adapter, clock) = CreateHarness(temp.Combine("vault"));

        adapter.Partitions["A"] = "a";
        adapter.Partitions["B"] = "b";
        var first = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(first.Succeeded);

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        adapter.Partitions["B"] = "b-changed";
        var second = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(second.Succeeded);
        Assert.Equal(RawCaptureMode.Incremental, second.Mode);
        Assert.Single(second.Coverage, c => c.PartitionId == "A" && c.Status == RawPartitionStatus.Reused);
        Assert.Single(second.Coverage, c => c.PartitionId == "B" && c.Status == RawPartitionStatus.Captured);
    }

    [Fact]
    public async Task AdapterVersionChangeInvalidatesCheckpointAndWidensToFullCapture()
    {
        using var temp = new TempDirectory();
        var (service, _, adapter, clock) = CreateHarness(temp.Combine("vault"));

        adapter.Partitions["A"] = "a";
        var first = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(first.Succeeded);
        Assert.Null(first.PreviousGenerationId);

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        adapter.CaptureAdapterVersion = "2";
        var second = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(second.Succeeded);
        Assert.Equal(RawCaptureMode.Baseline, second.Mode);
        Assert.All(second.Coverage, c => Assert.Equal(RawPartitionStatus.Captured, c.Status));
        Assert.Contains(second.Diagnostics, d => d.Code == DiagnosticCodes.CaptureFullFallback);
    }

    [Fact]
    public async Task GenerationWithoutCaptureCheckpointWidensToFullCapture()
    {
        using var temp = new TempDirectory();
        var (service, vault, adapter, clock) = CreateHarness(temp.Combine("vault"));

        adapter.Partitions["A"] = "a";
        var first = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(first.Succeeded);
        var generation1 = await vault.OpenGenerationAsync(first.AccountId, first.GenerationId, CancellationToken.None);
        Assert.NotNull(generation1);

        // Republish the generation as the pre-#25 version-1 shape: readable, but it carries no
        // coverage and no capture checkpoint, so it cannot prove incremental safety.
        var legacyManifest = RawManifestSerializer.Serialize(generation1!.Manifest with
        {
            ManifestVersion = 1,
            Coverage = [],
            CaptureCheckpoint = null,
        });
        await File.WriteAllTextAsync(
            Path.Combine(generation1.GenerationDirectory, "manifest.json"), legacyManifest);

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var second = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(second.Succeeded);
        Assert.Equal(RawCaptureMode.Baseline, second.Mode);
        Assert.Contains(second.Diagnostics, d => d.Code == DiagnosticCodes.CaptureFullFallback);

        var generation2 = await vault.OpenGenerationAsync(first.AccountId, second.GenerationId, CancellationToken.None);
        Assert.NotNull(generation2);
        Assert.NotNull(generation2!.Manifest.CaptureCheckpoint);
    }

    [Fact]
    public async Task UnreadableAndUnsupportedPartitionsAreReportedWithoutAdvancingCheckpoint()
    {
        using var temp = new TempDirectory();
        var (service, vault, adapter, clock) = CreateHarness(temp.Combine("vault"));

        adapter.Partitions["A"] = "a";
        adapter.Partitions["R"] = "r";
        adapter.Partitions["Z"] = "z";
        adapter.Unreadable.Add("R");
        adapter.Unsupported.Add("Z");

        var first = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(first.Succeeded);
        Assert.Equal(RawGenerationCompleteness.Partial, first.Completeness);
        Assert.Single(first.Coverage, c => c.PartitionId == "A" && c.Status == RawPartitionStatus.Captured);
        Assert.Single(first.Coverage, c => c.PartitionId == "R" && c.Status == RawPartitionStatus.Unavailable);
        Assert.Single(first.Coverage, c => c.PartitionId == "Z" && c.Status == RawPartitionStatus.Unsupported);
        Assert.All(
            first.Coverage.Where(c => c.Status != RawPartitionStatus.Captured),
            c => Assert.False(string.IsNullOrWhiteSpace(c.Diagnostic)));

        var generation1 = await vault.OpenGenerationAsync(first.AccountId, first.GenerationId, CancellationToken.None);
        Assert.NotNull(generation1);
        Assert.Null(generation1!.Manifest.CaptureCheckpoint);

        // Without a checkpoint the following capture must read the whole source again.
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        adapter.Unreadable.Clear();
        adapter.Unsupported.Clear();
        var second = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(second.Succeeded);
        Assert.Equal(RawCaptureMode.Baseline, second.Mode);
        Assert.Equal(RawGenerationCompleteness.Complete, second.Completeness);
        Assert.Contains(second.Diagnostics, d => d.Code == DiagnosticCodes.CaptureFullFallback);
    }

    [Fact]
    public async Task KnownUnsupportedCoverageDoesNotDowngradeCompletenessOrBlockTheCheckpoint()
    {
        using var temp = new TempDirectory();
        var (service, vault, adapter, clock) = CreateHarness(temp.Combine("vault"));

        adapter.Partitions["A"] = "a";
        adapter.Partitions["B"] = "b";
        adapter.Partitions["Q"] = "q";
        adapter.Unsupported.Add("Q");

        var first = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);

        // A complete generation may legitimately carry `unsupported` coverage for a
        // discovered-but-unsupported partition (Issue #37).
        Assert.True(first.Succeeded);
        Assert.Equal(RawGenerationCompleteness.Complete, first.Completeness);
        Assert.Equal(2, first.Coverage.Count(c => c.Status == RawPartitionStatus.Captured));
        Assert.Equal(
            RawPartitionStatus.Unsupported,
            first.Coverage.Single(c => c.PartitionId == "Q").Status);

        var generation1 = await vault.OpenGenerationAsync(first.AccountId, first.GenerationId, CancellationToken.None);
        Assert.NotNull(generation1);
        var checkpoint = generation1!.Manifest.CaptureCheckpoint;
        Assert.NotNull(checkpoint);

        // The checkpoint covers exactly the captured/reused set: the unsupported entry stays in
        // coverage but is never checkpoint evidence.
        Assert.Equal(3, generation1.Manifest.Coverage.Count);
        Assert.Equal(2, checkpoint!.PartitionFingerprints.Count);
        Assert.DoesNotContain("Q", checkpoint.PartitionFingerprints.Keys);
        Assert.Equal(2, generation1.Manifest.Capture.ArtifactCount);

        // The next run still reuses from that checkpoint and keeps the unsupported entry visible.
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var second = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(second.Succeeded);
        Assert.Equal(RawCaptureMode.Incremental, second.Mode);
        Assert.Equal(RawGenerationCompleteness.Complete, second.Completeness);
        Assert.Equal(2, second.Coverage.Count(c => c.Status == RawPartitionStatus.Reused));
        Assert.Equal(
            RawPartitionStatus.Unsupported,
            second.Coverage.Single(c => c.PartitionId == "Q").Status);
        Assert.DoesNotContain(second.Diagnostics, d => d.Code == DiagnosticCodes.CaptureFullFallback);

        var generation2 = await vault.OpenGenerationAsync(first.AccountId, second.GenerationId, CancellationToken.None);
        Assert.NotNull(generation2);
        Assert.Equal(second.GenerationId, generation2!.Manifest.CaptureCheckpoint!.GenerationId);
        Assert.Equal(2, generation2.Manifest.CaptureCheckpoint.PartitionFingerprints.Count);
    }

    [Fact]
    public async Task UnclassifiedCoverageForcesPartialAndPublishesNoCheckpoint()
    {
        using var temp = new TempDirectory();
        var (service, vault, adapter, clock) = CreateHarness(temp.Combine("vault"));

        adapter.Partitions["A"] = "a";
        adapter.Partitions["N"] = "n";
        adapter.Unclassified.Add("N");

        var first = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);

        // Unknown/unclassified evidence can never yield a complete verdict, and stays explicit.
        Assert.True(first.Succeeded);
        Assert.Equal(RawGenerationCompleteness.Partial, first.Completeness);
        Assert.Equal(
            RawPartitionStatus.Unsupported,
            first.Coverage.Single(c => c.PartitionId == "N").Status);
        Assert.Contains(first.Diagnostics, d => d.Code == DiagnosticCodes.PartitionUnclassified);

        var generation1 = await vault.OpenGenerationAsync(first.AccountId, first.GenerationId, CancellationToken.None);
        Assert.NotNull(generation1);
        Assert.Null(generation1!.Manifest.CaptureCheckpoint);

        // Without a checkpoint the following capture widens to a full baseline.
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var second = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(second.Succeeded);
        Assert.Equal(RawCaptureMode.Baseline, second.Mode);
        Assert.Contains(second.Diagnostics, d => d.Code == DiagnosticCodes.CaptureFullFallback);
    }

    [Fact]
    public async Task ACompleteVerdictContradictedByCoverageOrDiagnosticsIsDowngraded()
    {
        using var temp = new TempDirectory();
        var (service, vault, adapter, clock) = CreateHarness(temp.Combine("vault"));

        adapter.Partitions["A"] = "a";
        adapter.Partitions["R"] = "r";
        adapter.Unreadable.Add("R");

        // The fixture reports Partial for unreadable evidence, so force the contradictory shape
        // directly: a complete verdict that still carries an unavailable coverage entry. FR-20
        // requires the service to refuse to publish that as complete.
        adapter.ReportCompleteDespiteCoverageGaps = true;
        var first = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.Equal(RawGenerationCompleteness.Partial, first.Completeness);
        Assert.Contains(first.Diagnostics, d => d.Code == DiagnosticCodes.CaptureCompletenessDowngraded);

        var generation1 = await vault.OpenGenerationAsync(first.AccountId, first.GenerationId, CancellationToken.None);
        Assert.NotNull(generation1);
        Assert.Equal(RawGenerationCompleteness.Partial, generation1!.Manifest.Capture.Completeness);
        Assert.Null(generation1.Manifest.CaptureCheckpoint);

        // The contradictory evidence is still published as a partial generation, never dropped.
        Assert.Contains(
            generation1.Manifest.Coverage,
            c => c.PartitionId == "R" && c.Status == RawPartitionStatus.Unavailable);

        // The next run widens because the predecessor is not a complete checkpoint generation.
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        adapter.ReportCompleteDespiteCoverageGaps = false;
        adapter.Unreadable.Clear();
        var second = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(second.Succeeded);
        Assert.Equal(RawCaptureMode.Baseline, second.Mode);
        Assert.Equal(RawGenerationCompleteness.Complete, second.Completeness);
    }

    [Fact]
    public async Task CaughtAdapterFailureBeforePublicationLeavesPreviousCheckpointUnchanged()
    {
        using var temp = new TempDirectory();
        var (service, vault, adapter, clock) = CreateHarness(temp.Combine("vault"));

        adapter.Partitions["A"] = "a";
        var first = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(first.Succeeded);

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        adapter.Partitions["A"] = "a-changed";

        // Caught cancellation propagates and publishes nothing.
        adapter.Failure = new OperationCanceledException("synthetic cancellation");
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None));
        await AssertLatestCheckpointUnchangedAsync(vault, first);

        // Caught source I/O failure is reported explicitly and also publishes nothing.
        adapter.Failure = new IOException("synthetic source I/O failure");
        var failed = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.False(failed.Succeeded);
        Assert.Equal("synthetic source I/O failure", failed.FailureMessage);
        await AssertLatestCheckpointUnchangedAsync(vault, first);
    }

    [Fact]
    public async Task PublicationFailureLeavesPreviousCheckpointUnchanged()
    {
        using var temp = new TempDirectory();
        var (service, vault, adapter, clock) = CreateHarness(temp.Combine("vault"));

        adapter.Partitions["A"] = "a";
        var first = await service.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.True(first.Succeeded);

        // The real store serves every read and write; only the publish step fails, so this
        // exercises a Fatal publication failure rather than a weakened immutability guard.
        var failingService = new CaptureService(
            new FixtureSourceAdapter(), adapter, new FailingPublishVaultStore(vault), clock);

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        adapter.Partitions["A"] = "a-changed";
        var second = await failingService.CaptureAccountAsync(new CaptureRequest(), null, CancellationToken.None);
        Assert.False(second.Succeeded);
        Assert.Equal("synthetic generation publication failure", second.FailureMessage);

        await AssertLatestCheckpointUnchangedAsync(vault, first);
    }

    /// <summary>
    /// The previously published generation is still the latest and still carries the checkpoint
    /// that was published with it: a failed run never produces a newer cursor.
    /// </summary>
    private static async Task AssertLatestCheckpointUnchangedAsync(RawVaultStore vault, CaptureResult expected)
    {
        var latest = await vault.GetLatestGenerationAsync(expected.AccountId, CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal(expected.GenerationId, latest!.GenerationId);

        var reopened = await vault.OpenGenerationAsync(expected.AccountId, expected.GenerationId, CancellationToken.None);
        Assert.NotNull(reopened);
        Assert.Equal(expected.GenerationId, reopened!.Manifest.CaptureCheckpoint!.GenerationId);
    }

    private static (
        CaptureService Service,
        RawVaultStore Vault,
        SyntheticCaptureAdapter Adapter,
        FixedClock Clock) CreateHarness(string vaultRoot)
    {
        var vault = new RawVaultStore(vaultRoot);
        var clock = new FixedClock();
        var adapter = new SyntheticCaptureAdapter();
        var service = new CaptureService(new FixtureSourceAdapter(), adapter, vault, clock);
        return (service, vault, adapter, clock);
    }

    /// <summary>Order-independent rendering of the checkpoint's partition fingerprints.</summary>
    private static string Fingerprints(RawCaptureCheckpoint checkpoint) =>
        string.Join(
            ";",
            checkpoint.PartitionFingerprints
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));

    /// <summary>Delegates everything to a real store but always fails generation publication.</summary>
    private sealed class FailingPublishVaultStore(IRawVaultStore inner) : IRawVaultStore
    {
        public string VaultRoot => inner.VaultRoot;

        public Task<IReadOnlyList<string>> ListAccountIdsAsync(CancellationToken cancellationToken) =>
            inner.ListAccountIdsAsync(cancellationToken);

        public async Task<IRawGenerationSession> BeginGenerationAsync(
            RawGenerationContext context,
            CancellationToken cancellationToken) =>
            new FailingPublishSession(
                await inner.BeginGenerationAsync(context, cancellationToken).ConfigureAwait(false));

        public Task<IReadOnlyList<RawGenerationSummary>> ListGenerationsAsync(
            string accountId,
            CancellationToken cancellationToken) =>
            inner.ListGenerationsAsync(accountId, cancellationToken);

        public Task<RawGenerationSummary?> GetLatestGenerationAsync(
            string accountId,
            CancellationToken cancellationToken) =>
            inner.GetLatestGenerationAsync(accountId, cancellationToken);

        public Task<RawGeneration?> OpenGenerationAsync(
            string accountId,
            string generationId,
            CancellationToken cancellationToken) =>
            inner.OpenGenerationAsync(accountId, generationId, cancellationToken);
    }

    private sealed class FailingPublishSession(IRawGenerationSession inner) : IRawGenerationSession
    {
        public string GenerationId => inner.GenerationId;

        public string StagingDirectory => inner.StagingDirectory;

        public Task<RawArtifactDescriptor> WriteArtifactAsync(
            string role,
            string name,
            Stream content,
            string? sourceFormat,
            bool isDecrypted,
            IReadOnlyDictionary<string, string>? metadata,
            CancellationToken cancellationToken) =>
            inner.WriteArtifactAsync(role, name, content, sourceFormat, isDecrypted, metadata, cancellationToken);

        public Task<RawArtifactDescriptor> ReuseArtifactAsync(
            RawGeneration previous,
            RawArtifactDescriptor artifact,
            CancellationToken cancellationToken) =>
            inner.ReuseArtifactAsync(previous, artifact, cancellationToken);

        public Task<RawGeneration> PublishAsync(RawManifest manifest, CancellationToken cancellationToken) =>
            throw new IOException("synthetic generation publication failure");

        public Task DiscardAsync() => inner.DiscardAsync();

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}