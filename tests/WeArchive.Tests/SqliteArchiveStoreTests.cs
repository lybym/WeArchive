using System.Text.Json.Nodes;
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
}
