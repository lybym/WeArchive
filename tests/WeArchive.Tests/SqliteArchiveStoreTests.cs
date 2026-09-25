using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Infrastructure.Archive;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// Archive persistence, idempotency and reply resolution.
/// docs/PRD.md FR-07/FR-08/FR-19, docs/DATA_MODEL.md sections 8 and 17.
/// </summary>
public sealed class SqliteArchiveStoreTests
{
    private static readonly DateTimeOffset When = new(2026, 1, 15, 9, 0, 0, TimeSpan.FromHours(8));

    private static async Task<SqliteArchiveStore> CreateStoreAsync(TempDirectory temp)
    {
        var store = new SqliteArchiveStore(temp.Combine("archive.db"), new FixedClock());
        await store.InitializeAsync(CancellationToken.None);
        return store;
    }

    private static async Task SeedAsync(SqliteArchiveStore store)
    {
        await store.UpsertAccountAsync(new ArchiveAccount
        {
            Id = "a_1",
            SourceProfileId = "acct",
            AdapterName = "fixture",
            AdapterVersion = "1.0.0",
        }, CancellationToken.None);

        await store.UpsertParticipantsAsync(
        [
            new ArchiveParticipant
            {
                Id = "u_alice",
                AccountId = "a_1",
                SourceParticipantId = "wxid_alice",
                LatestRemark = "张三",
                Nickname = "三哥",
            },
            new ArchiveParticipant
            {
                Id = "u_bob",
                AccountId = "a_1",
                SourceParticipantId = "wxid_bob",
                Nickname = "Kevin",
            },
        ], CancellationToken.None);

        await store.UpsertConversationsAsync(
        [
            new ArchiveConversation
            {
                Id = "g_1",
                AccountId = "a_1",
                SourceConversationId = "100200300@chatroom",
                Kind = ConversationKind.Group,
                Title = "华东产品创新中心工作群",
            },
        ], CancellationToken.None);
    }

    private static CanonicalMessage Message(
        string sourceId,
        string text,
        CanonicalMessageType type = CanonicalMessageType.Text,
        DateTimeOffset? at = null,
        string? orderKey = null,
        ReplyReference? reply = null,
        JsonObject? payload = null) => new()
        {
            Id = StableIds.Message("g_1", sourceId),
            ConversationId = "g_1",
            SenderId = "u_alice",
            OccurredAt = at ?? When,
            Type = type,
            Text = text,
            Payload = payload,
            ReplyTo = reply,
            Source = new SourceProvenance
            {
                SourceProfileId = "acct",
                SourceConversationId = "100200300@chatroom",
                SourceMessageId = sourceId,
                SourceType = "1",
                SourcePartition = "message_0",
                SourceOrderKey = orderKey ?? sourceId,
                AdapterName = "fixture",
                AdapterVersion = "1.0.0",
            },
        };

    [Fact]
    public async Task MessagesAreStoredAndReadBackWithTheirCanonicalFields()
    {
        using var temp = new TempDirectory();
        var store = await CreateStoreAsync(temp);
        await SeedAsync(store);

        var write = await store.UpsertMessagesAsync(
            [Message("s:1", "下午三点开会。")],
            CancellationToken.None);

        Assert.Equal(1, write.Inserted);
        Assert.Equal(0, write.Updated);

        var read = await store.ReadMessagesAsync("g_1", CancellationToken.None);
        var message = Assert.Single(read);
        Assert.Equal("下午三点开会。", message.Text);
        Assert.Equal(CanonicalMessageType.Text, message.Type);
        Assert.Equal("u_alice", message.SenderId);
        Assert.Equal(When, message.OccurredAt);
        Assert.Equal("message_0", message.Source.SourcePartition);
    }

    [Fact]
    public async Task ReimportingTheSameRecordsCreatesNoLogicalDuplicates()
    {
        using var temp = new TempDirectory();
        var store = await CreateStoreAsync(temp);
        await SeedAsync(store);

        var batch = new[] { Message("s:1", "同一句话"), Message("s:2", "同一句话") };

        var first = await store.UpsertMessagesAsync(batch, CancellationToken.None);
        var second = await store.UpsertMessagesAsync(batch, CancellationToken.None);
        var third = await store.UpsertMessagesAsync(batch, CancellationToken.None);

        Assert.Equal(2, first.Inserted);
        Assert.Equal(0, second.Inserted);
        Assert.Equal(2, second.Unchanged);
        Assert.Equal(0, third.Inserted);
        Assert.Equal(2, (await store.ReadMessagesAsync("g_1", CancellationToken.None)).Count);
    }

    [Fact]
    public async Task RepeatedIdenticalMessagesRemainDistinctRecords()
    {
        using var temp = new TempDirectory();
        var store = await CreateStoreAsync(temp);
        await SeedAsync(store);

        await store.UpsertMessagesAsync(
            [Message("s:1", "收到"), Message("s:2", "收到"), Message("s:3", "收到")],
            CancellationToken.None);

        var messages = await store.ReadMessagesAsync("g_1", CancellationToken.None);
        Assert.Equal(3, messages.Count);
    }

    [Fact]
    public async Task ChangedContentIsCountedAsAnUpdateNotANewRecord()
    {
        using var temp = new TempDirectory();
        var store = await CreateStoreAsync(temp);
        await SeedAsync(store);

        await store.UpsertMessagesAsync([Message("s:1", "旧文本")], CancellationToken.None);
        var update = await store.UpsertMessagesAsync([Message("s:1", "新文本")], CancellationToken.None);

        Assert.Equal(1, update.Updated);
        Assert.Equal(0, update.Inserted);
        Assert.Single(await store.ReadMessagesAsync("g_1", CancellationToken.None));
    }

    [Fact]
    public async Task TimelineIsOrderedByTimeWithAStableSecondaryKey()
    {
        using var temp = new TempDirectory();
        var store = await CreateStoreAsync(temp);
        await SeedAsync(store);

        await store.UpsertMessagesAsync(
        [
            Message("s:3", "c", orderKey: "3"),
            Message("s:1", "a", orderKey: "1"),
            Message("s:2", "b", orderKey: "2"),
        ], CancellationToken.None);

        var messages = await store.ReadMessagesAsync("g_1", CancellationToken.None);
        Assert.Equal(["a", "b", "c"], messages.Select(m => m.Text));
    }

    [Fact]
    public async Task ReplyTargetIsResolvedAgainstTheArchiveOnly()
    {
        using var temp = new TempDirectory();
        var store = await CreateStoreAsync(temp);
        await SeedAsync(store);

        // The quoted message is archived, so the relationship can be resolved.
        await store.UpsertMessagesAsync(
        [
            Message("s:1", "下午三点开会。"),
            Message(
                "s:2",
                "我同意这个方案。",
                reply: new ReplyReference { SourceMessageId = "s:1", SenderId = "u_bob", Text = "下午三点开会。" },
                orderKey: "9"),
        ], CancellationToken.None);

        var resolved = await store.ResolveReplyTargetsAsync("g_1", CancellationToken.None);
        Assert.Equal(1, resolved);

        var messages = await store.ReadMessagesAsync("g_1", CancellationToken.None);
        var reply = Assert.Single(messages, m => m.Text == "我同意这个方案。");
        Assert.Equal(StableIds.Message("g_1", "s:1"), reply.ReplyTo!.MessageId);
        Assert.Equal("下午三点开会。", reply.ReplyTo.Text);
    }

    [Fact]
    public async Task UnresolvableReplyKeepsItsSnapshotAndNoInventedTargetId()
    {
        using var temp = new TempDirectory();
        var store = await CreateStoreAsync(temp);
        await SeedAsync(store);

        // The quoted message was never archived (deleted, recalled or outside the export).
        await store.UpsertMessagesAsync(
        [
            Message(
                "s:2",
                "我同意这个方案。",
                reply: new ReplyReference { SourceMessageId = "s:999", SenderName = "Kevin", Text = "下午三点开会。" }),
        ], CancellationToken.None);

        var resolved = await store.ResolveReplyTargetsAsync("g_1", CancellationToken.None);
        Assert.Equal(0, resolved);

        var message = Assert.Single(await store.ReadMessagesAsync("g_1", CancellationToken.None));
        Assert.Null(message.ReplyTo!.MessageId);
        Assert.Equal("下午三点开会。", message.ReplyTo.Text);
        Assert.Equal("Kevin", message.ReplyTo.SenderName);
    }

    [Fact]
    public async Task ReplyResolutionSurvivesAReimport()
    {
        using var temp = new TempDirectory();
        var store = await CreateStoreAsync(temp);
        await SeedAsync(store);

        var batch = new[]
        {
            Message("s:1", "下午三点开会。"),
            Message(
                "s:2",
                "我同意这个方案。",
                reply: new ReplyReference { SourceMessageId = "s:1", SenderId = "u_bob", Text = "下午三点开会。" },
                orderKey: "9"),
        };

        await store.UpsertMessagesAsync(batch, CancellationToken.None);
        await store.ResolveReplyTargetsAsync("g_1", CancellationToken.None);

        // Re-importing the same source data must be counted as unchanged and must not lose
        // the relationship resolved on the previous run.
        var second = await store.UpsertMessagesAsync(batch, CancellationToken.None);
        Assert.Equal(0, second.Inserted);
        Assert.Equal(0, second.Updated);
        Assert.Equal(2, second.Unchanged);

        var reply = Assert.Single(
            await store.ReadMessagesAsync("g_1", CancellationToken.None),
            m => m.Text == "我同意这个方案。");
        Assert.Equal(StableIds.Message("g_1", "s:1"), reply.ReplyTo!.MessageId);
    }

    [Fact]
    public async Task ConversationAggregatesAreRecomputedFromTheArchive()
    {
        using var temp = new TempDirectory();
        var store = await CreateStoreAsync(temp);
        await SeedAsync(store);

        await store.UpsertMessagesAsync(
        [
            Message("s:1", "a", at: When),
            Message("s:2", "b", at: When.AddDays(2)),
            Message("s:3", "c", type: CanonicalMessageType.Unknown, at: When.AddDays(3)),
        ], CancellationToken.None);

        var conversation = await store.GetConversationAsync("g_1", CancellationToken.None);
        Assert.NotNull(conversation);
        Assert.Equal(3, conversation!.MessageCount);
        Assert.Equal(When, conversation.FirstMessageAt);
        Assert.Equal(When.AddDays(3), conversation.LastMessageAt);

        var stats = await store.GetConversationStatsAsync("g_1", CancellationToken.None);
        Assert.Equal(3, stats.MessageCount);
        Assert.Equal(1, stats.UnknownCount);
        Assert.Equal(1, stats.TypeCounts[CanonicalMessageType.Unknown]);
    }

    [Fact]
    public async Task AChangedReplyTargetIsReResolvedToTheNewCanonicalTarget()
    {
        using var temp = new TempDirectory();
        var store = await CreateStoreAsync(temp);
        await SeedAsync(store);

        var firstTarget = StableIds.Message("g_1", "s:1");
        var secondTarget = StableIds.Message("g_1", "s:3");

        // s:2 quotes s:1, which is archived, so the relationship resolves to s:1.
        await store.UpsertMessagesAsync(
        [
            Message("s:1", "下午三点开会。"),
            Message("s:2", "我同意这个方案。",
                reply: new ReplyReference { SourceMessageId = "s:1", SenderId = "u_bob", Text = "下午三点开会。" },
                orderKey: "9"),
        ], CancellationToken.None);
        await store.ResolveReplyTargetsAsync("g_1", CancellationToken.None);

        var reply = Assert.Single(await store.ReadMessagesAsync("g_1", CancellationToken.None), m => m.Text == "我同意这个方案。");
        Assert.Equal(firstTarget, reply.ReplyTo!.MessageId);

        // Upstream edits s:2 to quote a different archived record, s:3.
        await store.UpsertMessagesAsync(
        [
            Message("s:3", "另一个讨论。"),
            Message("s:2", "我同意这个方案。",
                reply: new ReplyReference { SourceMessageId = "s:3", SenderId = "u_bob", Text = "另一个讨论。" },
                orderKey: "9"),
        ], CancellationToken.None);
        await store.ResolveReplyTargetsAsync("g_1", CancellationToken.None);

        // The resolved target must follow the new upstream id, not retain the stale s:1
        // target that was resolved on the previous run.
        reply = Assert.Single(await store.ReadMessagesAsync("g_1", CancellationToken.None), m => m.Text == "我同意这个方案。");
        Assert.Equal(secondTarget, reply.ReplyTo!.MessageId);
        Assert.Equal("另一个讨论。", reply.ReplyTo.Text);
    }

    [Fact]
    public async Task AReplyTargetThatBecomesUnresolvableClearsTheStaleResolvedTarget()
    {
        using var temp = new TempDirectory();
        var store = await CreateStoreAsync(temp);
        await SeedAsync(store);

        // s:2 quotes s:1 (archived) and resolves to a canonical target.
        await store.UpsertMessagesAsync(
        [
            Message("s:1", "下午三点开会。"),
            Message("s:2", "我同意这个方案。",
                reply: new ReplyReference { SourceMessageId = "s:1", SenderId = "u_bob", Text = "下午三点开会。" },
                orderKey: "9"),
        ], CancellationToken.None);
        await store.ResolveReplyTargetsAsync("g_1", CancellationToken.None);

        var before = Assert.Single(await store.ReadMessagesAsync("g_1", CancellationToken.None), m => m.Text == "我同意这个方案。");
        Assert.NotNull(before.ReplyTo!.MessageId);

        // Upstream edits s:2 to quote a record that is not in the archive. The previously
        // resolved target must be cleared rather than retained (retaining it would
        // fabricate an incorrect relationship). docs/DATA_MODEL.md section 8.7.
        await store.UpsertMessagesAsync(
        [
            Message("s:2", "我同意这个方案。",
                reply: new ReplyReference { SourceMessageId = "s:999", SenderId = "u_bob", Text = "已删除的消息。" },
                orderKey: "9"),
        ], CancellationToken.None);
        await store.ResolveReplyTargetsAsync("g_1", CancellationToken.None);

        var after = Assert.Single(await store.ReadMessagesAsync("g_1", CancellationToken.None), m => m.Text == "我同意这个方案。");
        Assert.Null(after.ReplyTo!.MessageId);
        Assert.Equal("已删除的消息。", after.ReplyTo.Text);
    }

    [Fact]
    public async Task ConversationFirstAndLastAreAggregatedByEpochNotOffsetBearingText()
    {
        using var temp = new TempDirectory();
        var store = await CreateStoreAsync(temp);
        await SeedAsync(store);

        // Three instants whose offset-bearing ISO-8601 text does not sort chronologically:
        //   A 2026-01-15T02:00:00+00:00  -> 02:00 UTC  (earliest)
        //   B 2026-01-15T01:00:00-05:00  -> 06:00 UTC  (latest)
        //   C 2026-01-15T03:00:00+00:00  -> 03:00 UTC
        // MIN/MAX over the text would wrongly pick B (first) and C (last). The archive
        // must aggregate by the epoch column and retain the corresponding rendered time.
        var a = new DateTimeOffset(2026, 1, 15, 2, 0, 0, TimeSpan.Zero);
        var b = new DateTimeOffset(2026, 1, 15, 1, 0, 0, TimeSpan.FromHours(-5));
        var c = new DateTimeOffset(2026, 1, 15, 3, 0, 0, TimeSpan.Zero);

        await store.UpsertMessagesAsync(
        [
            Message("s:a", "a", at: a),
            Message("s:b", "b", at: b),
            Message("s:c", "c", at: c),
        ], CancellationToken.None);

        var conversation = await store.GetConversationAsync("g_1", CancellationToken.None);
        Assert.NotNull(conversation);
        Assert.Equal(a, conversation!.FirstMessageAt);
        Assert.Equal(b, conversation.LastMessageAt);

        var stats = await store.GetConversationStatsAsync("g_1", CancellationToken.None);
        Assert.Equal(a, stats.FirstMessageAt);
        Assert.Equal(b, stats.LastMessageAt);
    }

    [Fact]
    public async Task ImportRunsAreAudited()
    {
        using var temp = new TempDirectory();
        var store = await CreateStoreAsync(temp);
        await SeedAsync(store);

        var run = await store.BeginImportRunAsync(
            "a_1",
            new SourceDescriptor
            {
                AdapterName = "fixture",
                AdapterVersion = "1.0.0",
                SourceVersion = "fixture-1",
                IsAvailable = true,
            },
            CancellationToken.None);

        await store.CompleteImportRunAsync(run with
        {
            Status = ImportRunStatus.Completed,
            FinishedAt = When,
            RecordsScanned = 3,
            RecordsInserted = 3,
            UnknownCount = 1,
            Diagnostics = [ImportDiagnostic.Partial("unknown_message_type", "one record", "9999")],
        }, CancellationToken.None);

        var stats = await store.GetArchiveStatsAsync(CancellationToken.None);
        Assert.Equal(1, stats.AccountCount);
        Assert.Equal(1, stats.ConversationCount);
        Assert.Equal(2, stats.ParticipantCount);
    }

    [Fact]
    public async Task SchemaMigrationsAreRecordedAndIdempotent()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("archive.db");

        var first = new SqliteArchiveStore(path, new FixedClock());
        await first.InitializeAsync(CancellationToken.None);
        await first.InitializeAsync(CancellationToken.None);

        var second = new SqliteArchiveStore(path, new FixedClock());
        await second.InitializeAsync(CancellationToken.None);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task MigrationOneArchiveUpgradesWithoutChangingLegacySourceCheckpoint()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("archive.db");
        var store = new SqliteArchiveStore(path, new FixedClock());
        await store.InitializeAsync(CancellationToken.None);
        await store.UpsertAccountAsync(new ArchiveAccount
        {
            Id = "account-1", SourceProfileId = "profile-1", AdapterName = "fixture", AdapterVersion = "1",
        }, CancellationToken.None);
        await store.UpsertConversationsAsync([new ArchiveConversation
        {
            Id = "conversation-1", AccountId = "account-1", SourceConversationId = "source-1", Kind = ConversationKind.Direct,
        }], CancellationToken.None);
        await using (var session = await store.BeginConversationImportAsync(new ArchiveConversation
        {
            Id = "conversation-1", AccountId = "account-1", SourceConversationId = "source-1", Kind = ConversationKind.Direct,
        }, CancellationToken.None))
        {
            await session.UpsertMessagesAsync([new CanonicalMessage
            {
                Id = "message-1", ConversationId = "conversation-1", OccurredAt = When,
                Type = CanonicalMessageType.Text, Text = "preserved", Source = new SourceProvenance
                {
                    SourceProfileId = "profile-1", SourceConversationId = "source-1", SourceMessageId = "source-message-1",
                    AdapterName = "fixture",
                },
            }], CancellationToken.None);
            await session.CommitAsync(CancellationToken.None);
        }
        var run = await store.BeginImportRunAsync("account-1", new SourceDescriptor
        {
            AdapterName = "fixture", AdapterVersion = "1", IsAvailable = true,
        }, CancellationToken.None);
        await store.CompleteImportRunAsync(run with { Status = ImportRunStatus.Completed, FinishedAt = When }, CancellationToken.None);
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO source_checkpoints(id,account_id,adapter_name,adapter_version,checkpoint_json,updated_at) VALUES('legacy','account-1','fixture','old','{\"cursor\":7}','2026-01-01T00:00:00+00:00'); DELETE FROM schema_migrations WHERE version=2; DROP TABLE ingest_checkpoints; PRAGMA user_version=1;";
            command.ExecuteNonQuery();
        }

        await store.InitializeAsync(CancellationToken.None);
        var preserved = await store.GetArchiveStatsAsync(CancellationToken.None);
        Assert.Equal(1, preserved.AccountCount);
        Assert.Equal(1, preserved.ConversationCount);
        Assert.Equal(1, preserved.MessageCount);
        Assert.Equal("preserved", Assert.Single(await store.ReadMessagesAsync("conversation-1", CancellationToken.None)).Text);
        Assert.Null(await store.GetIngestCheckpointAsync("account-1", "fixture", "conversation", "conversation-1", CancellationToken.None));
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT checkpoint_json FROM source_checkpoints WHERE id='legacy'; SELECT user_version FROM pragma_user_version;";
            Assert.Equal("{\"cursor\":7}", command.ExecuteScalar());
            command.CommandText = "SELECT COUNT(*) FROM import_runs;";
            Assert.Equal(1L, command.ExecuteScalar());
            command.CommandText = "SELECT MAX(version) FROM schema_migrations;";
            Assert.Equal(2L, command.ExecuteScalar());
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_ingest_checkpoints_scope';";
            Assert.Equal(1L, command.ExecuteScalar());
        }
    }

    [Fact]
    public async Task IngestCheckpointCommitsAndRollsBackWithConversationPublication()
    {
        using var temp = new TempDirectory();
        var store = await CreateStoreAsync(temp);
        var account = new ArchiveAccount { Id = "account-1", SourceProfileId = "profile-1", AdapterName = "fixture" };
        await store.UpsertAccountAsync(account, CancellationToken.None);
        var checkpoint = new IngestCheckpoint
        {
            Id = "ingest-1", AccountId = account.Id, AdapterFamily = "fixture", ScopeKind = "conversation",
            ScopeId = "conversation-1", CheckpointJson = "{\"version\":1,\"generation_id\":\"gen-1\"}", UpdatedAt = When,
        };
        await using (var session = await store.BeginConversationImportAsync(new ArchiveConversation
        {
            Id = "conversation-1", AccountId = account.Id, SourceConversationId = "source-1", Kind = ConversationKind.Direct,
        }, CancellationToken.None))
        {
            await session.SetIngestCheckpointAsync(checkpoint, CancellationToken.None);
            await session.RollbackAsync();
        }
        Assert.Null(await store.GetIngestCheckpointAsync(account.Id, "fixture", "conversation", "conversation-1", CancellationToken.None));

        await using (var session = await store.BeginConversationImportAsync(new ArchiveConversation
        {
            Id = "conversation-1", AccountId = account.Id, SourceConversationId = "source-1", Kind = ConversationKind.Direct,
        }, CancellationToken.None))
        {
            await session.SetIngestCheckpointAsync(checkpoint, CancellationToken.None);
            await session.CommitAsync(CancellationToken.None);
        }
        Assert.Equal(checkpoint.CheckpointJson,
            (await store.GetIngestCheckpointAsync(account.Id, "fixture", "conversation", "conversation-1", CancellationToken.None))?.CheckpointJson);

        var independent = checkpoint with
        {
            Id = "ingest-2", ScopeId = "conversation-2", CheckpointJson = "{\"version\":1,\"generation_id\":\"gen-2\"}",
        };
        await using (var session = await store.BeginConversationImportAsync(new ArchiveConversation
        {
            Id = "conversation-2", AccountId = account.Id, SourceConversationId = "source-2", Kind = ConversationKind.Direct,
        }, CancellationToken.None))
        {
            await session.SetIngestCheckpointAsync(independent, CancellationToken.None);
            await session.RollbackAsync();
        }
        Assert.Equal(checkpoint.CheckpointJson,
            (await store.GetIngestCheckpointAsync(account.Id, "fixture", "conversation", "conversation-1", CancellationToken.None))?.CheckpointJson);
        Assert.Null(await store.GetIngestCheckpointAsync(account.Id, "fixture", "conversation", "conversation-2", CancellationToken.None));
    }
}
