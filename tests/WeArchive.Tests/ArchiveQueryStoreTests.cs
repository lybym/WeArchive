using Microsoft.Data.Sqlite;
using WeArchive.Core.Domain;
using WeArchive.Core.Query;
using WeArchive.Core.Services;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// Persistence-boundary tests for the bounded query methods: page bounding, keyset resumption,
/// checkpoint projection and the guarantee that the query slice adds no index, migration or search
/// engine (docs/DATA_MODEL.md sections 8.1, 14.1, 18, 19 and 23).
/// </summary>
public sealed class ArchiveQueryStoreTests
{
    private static async Task<ArchiveQueryHarness> SeededAsync()
    {
        var harness = new ArchiveQueryHarness();
        await harness.SeedAsync();
        return harness;
    }

    [Fact]
    public async Task StorePageIsBoundedByTheLimitAndReportsFurtherRecords()
    {
        using var harness = await SeededAsync();

        var page = await harness.Store.QueryMessagesAsync(
            new ArchiveMessageQuery { ConversationId = ArchiveQueryHarness.GroupConversationId, Limit = 2 },
            CancellationToken.None);

        Assert.Equal(2, page.Items.Count);
        Assert.True(page.HasMore);
        Assert.Equal(
            [ArchiveQueryHarness.FirstId, ArchiveQueryHarness.SecondId],
            page.Items.Select(message => message.Id));
    }

    [Fact]
    public async Task StorePageAtTheExactEndReportsNoFurtherRecords()
    {
        using var harness = await SeededAsync();

        var page = await harness.Store.QueryMessagesAsync(
            new ArchiveMessageQuery
            {
                ConversationId = ArchiveQueryHarness.GroupConversationId,
                Limit = harness.GroupMessages.Count,
            },
            CancellationToken.None);

        Assert.Equal(harness.GroupMessages.Count, page.Items.Count);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task ResumeFromTheLastPositionOfTheTimelineReturnsNothing()
    {
        using var harness = await SeededAsync();
        var ordered = ArchiveQueryHarness.CanonicalOrder(harness.GroupMessages);
        var last = ordered[^1];

        var page = await harness.Store.QueryMessagesAsync(
            new ArchiveMessageQuery
            {
                ConversationId = ArchiveQueryHarness.GroupConversationId,
                Limit = 5,
                After = new MessageOrderKey
                {
                    OccurredUtc = last.OccurredAt.ToUnixTimeSeconds(),
                    SourceOrderKey = last.Source.SourceOrderKey ?? string.Empty,
                    MessageId = last.Id,
                },
            },
            CancellationToken.None);

        Assert.Empty(page.Items);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task ResumeUsesTheOrderKeyBeforeTheIdWhenTimestampsAreEqual()
    {
        using var harness = await SeededAsync();
        var ordered = ArchiveQueryHarness.CanonicalOrder(harness.GroupMessages);
        var anchor = ordered.First(message => message.Id == ArchiveQueryHarness.UnknownId);

        var page = await harness.Store.QueryMessagesAsync(
            new ArchiveMessageQuery
            {
                ConversationId = ArchiveQueryHarness.GroupConversationId,
                Limit = 10,
                After = new MessageOrderKey
                {
                    OccurredUtc = anchor.OccurredAt.ToUnixTimeSeconds(),
                    SourceOrderKey = anchor.Source.SourceOrderKey!,
                    MessageId = anchor.Id,
                },
            },
            CancellationToken.None);

        // The two records that share the anchor's instant but carry a higher order key follow it,
        // in order-key then id order; nothing else from that instant reappears.
        Assert.Equal(
            [ArchiveQueryHarness.ImageId, ArchiveQueryHarness.PartialId, ArchiveQueryHarness.SystemId,
             ArchiveQueryHarness.NullOrderKeyId, ArchiveQueryHarness.EqualTimestampFirstId,
             ArchiveQueryHarness.EqualTimestampSecondId],
            page.Items.Select(message => message.Id));
    }

    [Fact]
    public async Task ContextLookupReturnsNullForAnUnknownMessage()
    {
        using var harness = await SeededAsync();

        var context = await harness.Store.ReadMessageContextAsync("m_0000000000000fff", 5, 5, CancellationToken.None);

        Assert.Null(context);
    }

    [Fact]
    public async Task EveryReadPathHonoursCancellation()
    {
        using var harness = await SeededAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Store.QueryMessagesAsync(
                new ArchiveMessageQuery { ConversationId = ArchiveQueryHarness.GroupConversationId, Limit = 10 },
                cancelled.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Store.ReadMessageContextAsync(ArchiveQueryHarness.ImageId, 1, 1, cancelled.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Store.ListIngestFreshnessAsync(cancelled.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Service.ListMessagesAsync(
                new MessageListRequest { ConversationId = ArchiveQueryHarness.GroupConversationId },
                cancelled.Token));
    }

    [Fact]
    public async Task ArchiveStatsExposeTheNewestMessageInstant()
    {
        using var harness = await SeededAsync();

        var stats = await harness.Store.GetArchiveStatsAsync(CancellationToken.None);

        Assert.Equal(harness.GroupMessages.Count + harness.OtherMessages.Count, stats.MessageCount);
        Assert.Equal(ArchiveQueryHarness.At(2, 2), stats.LastMessageAt);
    }

    [Fact]
    public async Task EmptyArchiveReportsNoNewestMessageInstant()
    {
        var harness = new ArchiveQueryHarness();
        try
        {
            var stats = await harness.Store.GetArchiveStatsAsync(CancellationToken.None);

            Assert.Equal(0, stats.MessageCount);
            Assert.Null(stats.LastMessageAt);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [Fact]
    public async Task IngestFreshnessProjectsOnlyCanonicalPublicationScopes()
    {
        using var harness = await SeededAsync();

        // A coverage cursor says "verified unchanged"; it must not be reported as a publication.
        await harness.Store.SetIngestCheckpointAsync(Checkpoint(
            "ingest_coverage",
            "conversation_coverage",
            ArchiveQueryHarness.GroupConversationId,
            """{"version":1,"generation_id":"gen_coverage"}""",
            ArchiveQueryHarness.At(4, 1)), CancellationToken.None);

        // An unrecognised scope kind is not this projection's business and must not be guessed at.
        await harness.Store.SetIngestCheckpointAsync(Checkpoint(
            "ingest_other",
            "some_future_scope",
            "x",
            """{"version":1,"generation_id":"gen_other"}""",
            ArchiveQueryHarness.At(4, 2)), CancellationToken.None);

        var freshness = Assert.Single(await harness.Store.ListIngestFreshnessAsync(CancellationToken.None));

        Assert.Equal(ArchiveQueryHarness.AccountId, freshness.AccountId);
        Assert.Null(freshness.LastIngestAt);
        Assert.Null(freshness.LatestIngestedGenerationId);
        Assert.Null(freshness.LastAccountScanAt);
    }

    [Fact]
    public async Task IngestFreshnessReportsTheMostRecentlyCommittedConversationPublication()
    {
        using var harness = await SeededAsync();

        await harness.Store.SetIngestCheckpointAsync(Checkpoint(
            "ingest_old",
            "conversation",
            ArchiveQueryHarness.GroupConversationId,
            """{"version":1,"generation_id":"gen_old"}""",
            ArchiveQueryHarness.At(4, 1)), CancellationToken.None);

        await harness.Store.SetIngestCheckpointAsync(Checkpoint(
            "ingest_new",
            "conversation",
            ArchiveQueryHarness.SecondConversationId,
            """{"version":1,"generation_id":"gen_new"}""",
            ArchiveQueryHarness.At(4, 2)), CancellationToken.None);

        var freshness = Assert.Single(await harness.Store.ListIngestFreshnessAsync(CancellationToken.None));

        Assert.Equal(ArchiveQueryHarness.At(4, 2), freshness.LastIngestAt);
        Assert.Equal("gen_new", freshness.LatestIngestedGenerationId);
        Assert.Equal(ArchiveQueryHarness.SecondConversationId, freshness.LatestIngestedConversationId);
    }

    [Fact]
    public async Task IngestFreshnessIsDeterministicWhenTwoPublicationsShareAnInstant()
    {
        using var harness = await SeededAsync();

        await harness.Store.SetIngestCheckpointAsync(Checkpoint(
            "ingest_a",
            "conversation",
            ArchiveQueryHarness.GroupConversationId,
            """{"version":1,"generation_id":"gen_a"}""",
            ArchiveQueryHarness.At(4, 1)), CancellationToken.None);

        await harness.Store.SetIngestCheckpointAsync(Checkpoint(
            "ingest_b",
            "conversation",
            ArchiveQueryHarness.SecondConversationId,
            """{"version":1,"generation_id":"gen_b"}""",
            ArchiveQueryHarness.At(4, 1)), CancellationToken.None);

        var freshness = Assert.Single(await harness.Store.ListIngestFreshnessAsync(CancellationToken.None));

        // Ties break on the stable scope id, so the reported "latest" never depends on row order.
        Assert.Equal(ArchiveQueryHarness.SecondConversationId, freshness.LatestIngestedConversationId);
        Assert.Equal("gen_b", freshness.LatestIngestedGenerationId);
    }

    [Fact]
    public async Task UnreadableCursorPayloadContributesNoGenerationRatherThanFailingTheRead()
    {
        using var harness = await SeededAsync();
        await harness.Store.SetIngestCheckpointAsync(Checkpoint(
            "ingest_broken",
            "conversation",
            ArchiveQueryHarness.GroupConversationId,
            "this is not json",
            ArchiveQueryHarness.At(4, 1)), CancellationToken.None);

        var freshness = Assert.Single(await harness.Store.ListIngestFreshnessAsync(CancellationToken.None));

        Assert.Equal(ArchiveQueryHarness.At(4, 1), freshness.LastIngestAt);
        Assert.Null(freshness.LatestIngestedGenerationId);
    }

    [Fact]
    public async Task QuerySliceAddsNoFtsIndexAndNoSchemaMigration()
    {
        using var harness = await SeededAsync();

        using var connection = new SqliteConnection($"Data Source={harness.ArchivePath}");
        connection.Open();

        using (var fts = connection.CreateCommand())
        {
            fts.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE '%fts%' OR sql LIKE '%fts5%';";
            Assert.Equal(0, Convert.ToInt32(fts.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        }

        using (var migrations = connection.CreateCommand())
        {
            migrations.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
            Assert.Equal(2, Convert.ToInt32(migrations.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        }

        using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            Assert.Equal(2, Convert.ToInt32(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        }

        using (var timeline = connection.CreateCommand())
        {
            // The documented timeline index is what bounded retrieval orders by; it still exists.
            timeline.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_messages_timeline';";
            Assert.Equal(1, Convert.ToInt32(timeline.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        }

        await Task.CompletedTask;
    }

    [Fact]
    public async Task UnreadableArchiveIsReportedAsAnUnavailableArchive()
    {
        var harness = new ArchiveQueryHarness();
        try
        {
            await File.WriteAllTextAsync(harness.ArchivePath, "this is not a SQLite database");

            var exception = await Assert.ThrowsAsync<ArchiveQueryException>(
                () => harness.Service.ListMessagesAsync(
                    new MessageListRequest { ConversationId = ArchiveQueryHarness.GroupConversationId },
                    CancellationToken.None));

            Assert.Equal(ArchiveQueryErrorCodes.ArchiveUnavailable, exception.Code);
            Assert.Equal(ArchiveQueryFailureKind.Unavailable, exception.Kind);
        }
        finally
        {
            harness.Dispose();
        }
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