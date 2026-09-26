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
            () => harness.Store.ListIngestCheckpointsAsync(cancelled.Token));

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
    public async Task IngestCheckpointEnumerationIsNeutralAndDeterministic()
    {
        using var harness = await SeededAsync();
        await harness.Store.SetIngestCheckpointAsync(Checkpoint(
            "ingest_b",
            "some_future_scope",
            "z",
            """{"version":1,"generation_id":"gen_z"}""",
            ArchiveQueryHarness.At(4, 2)), CancellationToken.None);
        await harness.Store.SetIngestCheckpointAsync(Checkpoint(
            "ingest_a",
            "conversation",
            ArchiveQueryHarness.GroupConversationId,
            """{"version":9,"payload":"this build does not know it"}""",
            ArchiveQueryHarness.At(4, 1)), CancellationToken.None);

        var rows = await harness.Store.ListIngestCheckpointsAsync(CancellationToken.None);

        // The store reports the rows it owns verbatim — scope kinds, payload and times — and does
        // not interpret any of them: interpreting them is the cursor owner's job.
        Assert.Equal(2, rows.Count);
        Assert.Equal(
            [ArchiveQueryHarness.GroupConversationId, "z"],
            rows.Select(row => row.ScopeId));
        Assert.Equal(
            ["conversation", "some_future_scope"],
            rows.Select(row => row.ScopeKind));
        Assert.Equal(
            [ArchiveQueryHarness.At(4, 1), ArchiveQueryHarness.At(4, 2)],
            rows.Select(row => row.UpdatedAt));
        Assert.Equal(
            ["""{"version":9,"payload":"this build does not know it"}""", """{"version":1,"generation_id":"gen_z"}"""],
            rows.Select(row => row.CheckpointJson));
        Assert.All(rows, row => Assert.Equal(ArchiveQueryHarness.AccountId, row.AccountId));
    }

    [Fact]
    public async Task EmptyArchiveHasNoIngestCheckpoints()
    {
        var harness = new ArchiveQueryHarness();
        try
        {
            Assert.Empty(await harness.Store.ListIngestCheckpointsAsync(CancellationToken.None));
        }
        finally
        {
            harness.Dispose();
        }
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