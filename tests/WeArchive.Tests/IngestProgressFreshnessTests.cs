using WeArchive.Core.Domain;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// Tests for the ingest-progress projection. The projection belongs to the component that owns the
/// checkpoint encoding (`RawVaultIngestService`), so these tests exercise that owner rather than the
/// canonical archive store, which only enumerates rows verbatim
/// (docs/DATA_MODEL.md section 23.3, docs/HARNESS.md section 10).
/// </summary>
public sealed class IngestProgressFreshnessTests
{
    private static async Task<ArchiveQueryHarness> SeededAsync()
    {
        var harness = new ArchiveQueryHarness();
        await harness.SeedAsync();
        return harness;
    }

    [Fact]
    public async Task CoverageAndUnknownScopesAreNotReportedAsCanonicalPublications()
    {
        using var harness = await SeededAsync();

        // A coverage cursor says "verified unchanged"; it must not be reported as a publication.
        await harness.Store.SetIngestCheckpointAsync(Checkpoint(
            "ingest_coverage",
            "conversation_coverage",
            ArchiveQueryHarness.GroupConversationId,
            """{"version":1,"reader_version":"0.1.0","generation_id":"gen_coverage","evidence_fingerprint":"f"}""",
            ArchiveQueryHarness.At(4, 1)), CancellationToken.None);

        // An unrecognised scope kind is not this projection's business and must not be guessed at.
        await harness.Store.SetIngestCheckpointAsync(Checkpoint(
            "ingest_other",
            "some_future_scope",
            "x",
            """{"version":1,"reader_version":"0.1.0","generation_id":"gen_other","evidence_fingerprint":"f"}""",
            ArchiveQueryHarness.At(4, 2)), CancellationToken.None);

        var freshness = Assert.Single(await harness.IngestProgress.GetIngestFreshnessAsync(CancellationToken.None));

        Assert.Equal(ArchiveQueryHarness.AccountId, freshness.AccountId);
        Assert.Null(freshness.LastIngestAt);
        Assert.Null(freshness.LatestIngestedGenerationId);
        Assert.Null(freshness.LastAccountScanAt);
    }

    [Fact]
    public async Task MostRecentlyCommittedConversationPublicationIsReported()
    {
        using var harness = await SeededAsync();

        await harness.Store.SetIngestCheckpointAsync(Checkpoint(
            "ingest_old",
            "conversation",
            ArchiveQueryHarness.GroupConversationId,
            """{"version":1,"reader_version":"0.1.0","generation_id":"gen_old","evidence_fingerprint":"f"}""",
            ArchiveQueryHarness.At(4, 1)), CancellationToken.None);

        await harness.Store.SetIngestCheckpointAsync(Checkpoint(
            "ingest_new",
            "conversation",
            ArchiveQueryHarness.SecondConversationId,
            """{"version":1,"reader_version":"0.1.0","generation_id":"gen_new","evidence_fingerprint":"f"}""",
            ArchiveQueryHarness.At(4, 2)), CancellationToken.None);

        var freshness = Assert.Single(await harness.IngestProgress.GetIngestFreshnessAsync(CancellationToken.None));

        Assert.Equal(ArchiveQueryHarness.At(4, 2), freshness.LastIngestAt);
        Assert.Equal("gen_new", freshness.LatestIngestedGenerationId);
        Assert.Equal(ArchiveQueryHarness.SecondConversationId, freshness.LatestIngestedConversationId);
    }

    [Fact]
    public async Task TheReportedLatestPublicationIsDeterministicWhenInstantsTie()
    {
        using var harness = await SeededAsync();

        await harness.Store.SetIngestCheckpointAsync(Checkpoint(
            "ingest_a",
            "conversation",
            ArchiveQueryHarness.GroupConversationId,
            """{"version":1,"reader_version":"0.1.0","generation_id":"gen_a","evidence_fingerprint":"f"}""",
            ArchiveQueryHarness.At(4, 1)), CancellationToken.None);

        await harness.Store.SetIngestCheckpointAsync(Checkpoint(
            "ingest_b",
            "conversation",
            ArchiveQueryHarness.SecondConversationId,
            """{"version":1,"reader_version":"0.1.0","generation_id":"gen_b","evidence_fingerprint":"f"}""",
            ArchiveQueryHarness.At(4, 1)), CancellationToken.None);

        var freshness = Assert.Single(await harness.IngestProgress.GetIngestFreshnessAsync(CancellationToken.None));

        // Ties break on the stable scope id, so the reported "latest" never depends on row order.
        Assert.Equal(ArchiveQueryHarness.SecondConversationId, freshness.LatestIngestedConversationId);
        Assert.Equal("gen_b", freshness.LatestIngestedGenerationId);
    }

    [Fact]
    public async Task AccountScanCursorIsReportedByTimeAndNotAsAContentPublication()
    {
        using var harness = await SeededAsync();
        await harness.Store.SetIngestCheckpointAsync(Checkpoint(
            "ingest_scan",
            "account",
            ArchiveQueryHarness.AccountId,
            """{"version":1,"reader_version":"0.1.0","generation_id":"","evidence_fingerprint":"complete_account_scan","covered_generation_ids":["gen_00000000000000aa"]}""",
            ArchiveQueryHarness.At(4, 3)), CancellationToken.None);

        var freshness = Assert.Single(await harness.IngestProgress.GetIngestFreshnessAsync(CancellationToken.None));

        Assert.Equal(ArchiveQueryHarness.At(4, 3), freshness.LastAccountScanAt);
        // A scoped ingest never advances the scan cursor, and the scan cursor's empty generation
        // field means "no single generation" rather than a generation named "".
        Assert.Null(freshness.LastIngestAt);
        Assert.Null(freshness.LatestIngestedGenerationId);
        Assert.Null(freshness.LatestIngestedConversationId);
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("""{"version":9,"generation_id":"gen_future"}""")]
    [InlineData("""{"version":1,"reader_version":"0.1.0"}""")]
    [InlineData("""{"version":1,"reader_version":"0.1.0","generation_id":42,"evidence_fingerprint":"f"}""")]
    [InlineData("[]")]
    public async Task CursorThisBuildCannotInterpretContributesNoGenerationRatherThanFailing(
        string payload)
    {
        using var harness = await SeededAsync();
        await harness.Store.SetIngestCheckpointAsync(Checkpoint(
            "ingest_unreadable",
            "conversation",
            ArchiveQueryHarness.GroupConversationId,
            payload,
            ArchiveQueryHarness.At(4, 1)), CancellationToken.None);

        var freshness = Assert.Single(await harness.IngestProgress.GetIngestFreshnessAsync(CancellationToken.None));

        // The row is still reported by time; only the generation it cannot be read from is null.
        Assert.Equal(ArchiveQueryHarness.At(4, 1), freshness.LastIngestAt);
        Assert.Null(freshness.LatestIngestedGenerationId);
        Assert.Equal(ArchiveQueryHarness.GroupConversationId, freshness.LatestIngestedConversationId);
    }

    [Fact]
    public async Task ProjectionReadsNoPreservationState()
    {
        using var harness = await SeededAsync();
        await harness.Store.SetIngestCheckpointAsync(Checkpoint(
            "ingest_1",
            "conversation",
            ArchiveQueryHarness.GroupConversationId,
            """{"version":1,"reader_version":"0.1.0","generation_id":"gen_1","evidence_fingerprint":"f"}""",
            ArchiveQueryHarness.At(4, 1)), CancellationToken.None);

        // The harness builds this projection over a preservation store that throws on every call.
        var freshness = Assert.Single(await harness.IngestProgress.GetIngestFreshnessAsync(CancellationToken.None));

        Assert.Equal("gen_1", freshness.LatestIngestedGenerationId);
    }

    [Fact]
    public async Task ProjectionHonoursCancellation()
    {
        using var harness = await SeededAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.IngestProgress.GetIngestFreshnessAsync(cancelled.Token));
    }

    private static IngestCheckpoint Checkpoint(
        string id,
        string scopeKind,
        string scopeId,
        string json,
        DateTimeOffset updatedAt) => new()
        {
            Id = id,
            AccountId = ArchiveQueryHarness.AccountId,
            AdapterFamily = "wechat-windows",
            ScopeKind = scopeKind,
            ScopeId = scopeId,
            CheckpointJson = json,
            UpdatedAt = updatedAt,
        };
}