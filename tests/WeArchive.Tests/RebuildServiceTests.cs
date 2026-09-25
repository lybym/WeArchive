using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WeArchive.Cli.Commands;
using WeArchive.Cli.CommandLine;
using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;
using WeArchive.Infrastructure;
using WeArchive.Infrastructure.RawVault;
using WeArchive.Infrastructure.WeChat;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

public sealed class RebuildServiceTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 3, 1, 12, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task RebuildUsesOnlyVerifiedCapturedDatabasesAndReplacesAfterValidation()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        var generation = await PublishGenerationAsync(vault, temp.Path, profile, accountId, "captured text", "0.1.0", CapturedAt);
        var before = await HashArtifactsAsync(generation);
        var archivePath = temp.Combine("archive", "wearchive.db");
        var priorArchive = new WeArchive.Infrastructure.Archive.SqliteArchiveStore(archivePath, new FixedClock());
        await priorArchive.InitializeAsync(CancellationToken.None);
        await priorArchive.UpsertAccountAsync(new ArchiveAccount
        {
            Id = accountId,
            SourceProfileId = profile,
            AdapterName = WeChatWindowsSourceAdapter.Name,
        }, CancellationToken.None);
        await priorArchive.UpsertParticipantsAsync([new ArchiveParticipant
        {
            Id = StableIds.Participant(accountId, "wxid_bob"),
            AccountId = accountId,
            SourceParticipantId = "wxid_bob",
            UserDisplayName = "Robert",
        }], CancellationToken.None);
        var rebuild = new RebuildService(vault, archivePath, new FixedClock());

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var command = new RebuildCommand(rebuild);
        var exit = await command.ExecuteAsync(new CliContext(stdout, stderr, new GlobalOptions { Json = true, NoInput = true }), [], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        using var json = System.Text.Json.JsonDocument.Parse(stdout.ToString());
        Assert.Equal(1, json.RootElement.GetProperty("account_count").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("conversation_count").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("message_count").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("participant_count").GetInt32());
        Assert.Empty(stderr.ToString());
        Assert.Equal(before, await HashArtifactsAsync(generation));

        var archive = new WeArchive.Infrastructure.Archive.SqliteArchiveStore(archivePath, new FixedClock());
        var conversations = await archive.ListConversationsAsync(accountId, CancellationToken.None);
        var message = Assert.Single(await archive.ReadMessagesAsync(Assert.Single(conversations).Id, CancellationToken.None));
        Assert.Equal("captured text", message.Text);
        Assert.Equal(StableIds.Message(conversations[0].Id, "s:7001"), message.Id);
        Assert.Equal("Robert", Assert.Single(await archive.ListParticipantsAsync(accountId, CancellationToken.None)).UserDisplayName);

        await rebuild.RebuildAsync(null, CancellationToken.None);
        var repeated = Assert.Single(await archive.ReadMessagesAsync(conversations[0].Id, CancellationToken.None));
        Assert.Equal(message.Id, repeated.Id);
        Assert.Equal(message.Text, repeated.Text);
    }

    [Fact]
    public async Task ReaderVersionAndParserSemanticsDoNotChangeStableMessageIdentity()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        var first = await PublishGenerationAsync(vault, temp.Path, profile, accountId, "original", "reader-1", CapturedAt);
        var firstArchivePath = temp.Combine("first", "wearchive.db");
        await new RebuildService(vault, firstArchivePath, new FixedClock()).RebuildAsync(null, CancellationToken.None);
        var firstArchive = new WeArchive.Infrastructure.Archive.SqliteArchiveStore(firstArchivePath, new FixedClock());
        var conversationId = Assert.Single(await firstArchive.ListConversationsAsync(accountId, CancellationToken.None)).Id;
        var firstMessage = Assert.Single(await firstArchive.ReadMessagesAsync(conversationId, CancellationToken.None));

        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "corrected semantics", "reader-2", CapturedAt.AddHours(1));
        var secondArchivePath = temp.Combine("second", "wearchive.db");
        await new RebuildService(vault, secondArchivePath, new FixedClock()).RebuildAsync(null, CancellationToken.None);
        var secondArchive = new WeArchive.Infrastructure.Archive.SqliteArchiveStore(secondArchivePath, new FixedClock());
        var secondConversation = Assert.Single(await secondArchive.ListConversationsAsync(accountId, CancellationToken.None));
        var secondMessage = Assert.Single(await secondArchive.ReadMessagesAsync(secondConversation.Id, CancellationToken.None));

        Assert.Equal(firstMessage.Id, secondMessage.Id);
        Assert.NotEqual(firstMessage.Text, secondMessage.Text);
        Assert.Equal(firstMessage.ConversationId, secondMessage.ConversationId);
    }

    [Fact]
    public async Task FailedRebuildLeavesSelectedArchiveUntouched()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "active data", "reader-1", CapturedAt);
        var archivePath = temp.Combine("archive", "wearchive.db");
        await new RebuildService(vault, archivePath, new FixedClock()).RebuildAsync(null, CancellationToken.None);
        var before = await FileHashAsync(archivePath);

        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "unreadable", "reader-2", CapturedAt.AddHours(1), hasMessageTable: false);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new RebuildService(vault, archivePath, new FixedClock()).RebuildAsync(null, CancellationToken.None));

        Assert.Equal(before, await FileHashAsync(archivePath));
    }

    [Fact]
    public async Task UnsupportedCapturedRecordsRemainCanonicalUnknownMessages()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "opaque body", "reader-1", CapturedAt, sourceType: 9999);
        var archivePath = temp.Combine("archive", "wearchive.db");

        await new RebuildService(vault, archivePath, new FixedClock()).RebuildAsync(null, CancellationToken.None);

        var archive = new WeArchive.Infrastructure.Archive.SqliteArchiveStore(archivePath, new FixedClock());
        var conversation = Assert.Single(await archive.ListConversationsAsync(accountId, CancellationToken.None));
        var message = Assert.Single(await archive.ReadMessagesAsync(conversation.Id, CancellationToken.None));
        Assert.Equal(CanonicalMessageType.Unknown, message.Type);
    }

    [Fact]
    public async Task PartialCaptureIsRejectedWithoutReplacingTheSelectedArchive()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "known data", "reader-1", CapturedAt);
        var archivePath = temp.Combine("archive", "wearchive.db");
        await new RebuildService(vault, archivePath, new FixedClock()).RebuildAsync(null, CancellationToken.None);
        var before = await FileHashAsync(archivePath);
        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "partial data", "reader-2", CapturedAt.AddHours(1), completeness: RawGenerationCompleteness.Partial);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new RebuildService(vault, archivePath, new FixedClock()).RebuildAsync(null, CancellationToken.None));

        Assert.Equal(before, await FileHashAsync(archivePath));
    }

    [Fact]
    public async Task RawVaultIngestTracksConversationGenerationAndSkipsAnUnchangedSecondRun()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        var first = await PublishGenerationAsync(vault, temp.Path, profile, accountId, "first evidence", "reader-1", CapturedAt);
        var second = await PublishGenerationAsync(vault, temp.Path, profile, accountId, "second evidence", "reader-1", CapturedAt.AddHours(1));
        var repaired = await PublishGenerationAsync(vault, temp.Path, profile, accountId, "reparsed evidence", "reader-2", CapturedAt.AddHours(2));
        var archive = new WeArchive.Infrastructure.Archive.SqliteArchiveStore(temp.Combine("archive", "wearchive.db"), new FixedClock());
        var ingester = new RawVaultIngestService(vault, archive, new FixedClock());

        Assert.Equal(3, await ingester.IngestAsync(accountId, null, null, CancellationToken.None));
        Assert.Equal(0, await ingester.IngestAsync(accountId, null, null, CancellationToken.None));
        Assert.Equal(3, await ingester.IngestAsync(accountId, null, null, CancellationToken.None, replay: true));
        var conversation = Assert.Single(await archive.ListConversationsAsync(accountId, CancellationToken.None));
        Assert.Equal("reparsed evidence", Assert.Single(await archive.ReadMessagesAsync(conversation.Id, CancellationToken.None)).Text);
        var checkpoint = await archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family, "conversation", conversation.Id, CancellationToken.None);
        Assert.NotNull(checkpoint);
        Assert.Contains(repaired.GenerationId, checkpoint!.CheckpointJson, StringComparison.Ordinal);
        Assert.NotEqual(first.GenerationId, second.GenerationId);
    }

    [Fact]
    public async Task RawVaultIngestKeepsOlderConversationAndDiscoversANewConversationInLaterGeneration()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        const string profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        var older = await PublishGenerationAsync(vault, temp.Path, profile, accountId, "older only evidence", "reader-1", CapturedAt, conversationId: "wxid_bob");
        var archive = new WeArchive.Infrastructure.Archive.SqliteArchiveStore(temp.Combine("archive", "wearchive.db"), new FixedClock());
        var ingester = new RawVaultIngestService(vault, archive, new FixedClock());
        Assert.Equal(1, await ingester.IngestAsync(accountId, null, null, CancellationToken.None));

        var oldArtifact = Path.Combine(older.GenerationDirectory, older.Manifest.Artifacts[0].ContentRef);
        await using (var stream = new FileStream(oldArtifact, FileMode.Append, FileAccess.Write, FileShare.Read))
            await stream.WriteAsync(new byte[] { 0x01 });

        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "new conversation evidence", "reader-1", CapturedAt.AddHours(1), conversationId: "wxid_carol");
        Assert.Equal(1, await ingester.IngestAsync(accountId, null, null, CancellationToken.None));

        var conversations = await archive.ListConversationsAsync(accountId, CancellationToken.None);
        Assert.Equal(2, conversations.Count);
        var messages = await Task.WhenAll(conversations.Select(c => archive.ReadMessagesAsync(c.Id, CancellationToken.None)));
        Assert.Contains(messages.SelectMany(m => m), m => m.Text == "older only evidence");
        Assert.Contains(messages.SelectMany(m => m), m => m.Text == "new conversation evidence");
    }

    [Fact]
    public async Task RawVaultIngestDoesNotRegressCoveredConversationsOrAdvanceUnchangedConversationCheckpoints()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        const string profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "A old", "reader-1", CapturedAt,
            conversationId: "wxid_a", additionalConversationId: "wxid_b", additionalText: "B stable");
        var archive = new WeArchive.Infrastructure.Archive.SqliteArchiveStore(temp.Combine("archive", "wearchive.db"), new FixedClock());
        var ingester = new RawVaultIngestService(vault, archive, new FixedClock());
        Assert.Equal(2, await ingester.IngestAsync(accountId, null, null, CancellationToken.None));

        var initialConversations = await archive.ListConversationsAsync(accountId, CancellationToken.None);
        var conversationA = Assert.Single(initialConversations, c => c.SourceConversationId == "wxid_a");
        var conversationB = Assert.Single(initialConversations, c => c.SourceConversationId == "wxid_b");
        var checkpointBBefore = await archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
            "conversation", conversationB.Id, CancellationToken.None);

        var middle = await PublishGenerationAsync(vault, temp.Path, profile, accountId, "A changed", "reader-1", CapturedAt.AddHours(1),
            conversationId: "wxid_a", additionalConversationId: "wxid_b", additionalText: "B stable");
        Assert.Equal(1, await ingester.IngestAsync(accountId, null, null, CancellationToken.None));

        var checkpointBAfter = await archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
            "conversation", conversationB.Id, CancellationToken.None);
        Assert.Equal(checkpointBBefore!.CheckpointJson, checkpointBAfter!.CheckpointJson);
        Assert.Equal("A changed", Assert.Single(await archive.ReadMessagesAsync(conversationA.Id, CancellationToken.None)).Text);
        Assert.Equal("B stable", Assert.Single(await archive.ReadMessagesAsync(conversationB.Id, CancellationToken.None)).Text);

        var middleArtifact = Path.Combine(middle.GenerationDirectory, middle.Manifest.Artifacts[0].ContentRef);
        await using (var stream = new FileStream(middleArtifact, FileMode.Append, FileAccess.Write, FileShare.Read))
            await stream.WriteAsync(new byte[] { 0x01 });
        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "A newest", "reader-1", CapturedAt.AddHours(2),
            conversationId: "wxid_a", additionalConversationId: "wxid_b", additionalText: "B stable");
        Assert.Equal(1, await ingester.IngestAsync(accountId, null, null, CancellationToken.None));
        Assert.Equal("A newest", Assert.Single(await archive.ReadMessagesAsync(conversationA.Id, CancellationToken.None)).Text);
    }

    [Fact]
    public async Task RawVaultAccountIngestDiscoversUnimportedConversationAfterScopedIngest()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        const string profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "A scoped", "reader-1", CapturedAt,
            conversationId: "wxid_a", additionalConversationId: "wxid_b", additionalText: "B pending");
        var archive = new WeArchive.Infrastructure.Archive.SqliteArchiveStore(temp.Combine("archive", "wearchive.db"), new FixedClock());
        var ingester = new RawVaultIngestService(vault, archive, new FixedClock());

        Assert.Equal(1, await ingester.IngestAsync(accountId, "wxid_a", null, CancellationToken.None));
        Assert.Equal(1, await ingester.IngestAsync(accountId, null, null, CancellationToken.None));
        var conversations = await archive.ListConversationsAsync(accountId, CancellationToken.None);
        Assert.Equal(2, conversations.Count);
        var messageSets = await Task.WhenAll(conversations.Select(c => archive.ReadMessagesAsync(c.Id, CancellationToken.None)));
        Assert.Contains(messageSets.SelectMany(m => m), m => m.Text == "A scoped");
        Assert.Contains(messageSets.SelectMany(m => m), m => m.Text == "B pending");
        Assert.Equal(0, await ingester.IngestAsync(accountId, "wxid_a", null, CancellationToken.None));
    }

    [Fact]
    public async Task RawVaultIngestFollowsPublicationLineageWhenCaptureTimeMovesBackward()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        const string profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "first capture", "reader-1", CapturedAt);
        var archive = new WeArchive.Infrastructure.Archive.SqliteArchiveStore(temp.Combine("archive", "wearchive.db"), new FixedClock());
        var ingester = new RawVaultIngestService(vault, archive, new FixedClock());
        Assert.Equal(1, await ingester.IngestAsync(accountId, null, null, CancellationToken.None));

        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "clock-corrected capture", "reader-1", CapturedAt.AddHours(-1));
        Assert.Equal(1, await ingester.IngestAsync(accountId, null, null, CancellationToken.None));
        var conversation = Assert.Single(await archive.ListConversationsAsync(accountId, CancellationToken.None));
        Assert.Equal("clock-corrected capture", Assert.Single(await archive.ReadMessagesAsync(conversation.Id, CancellationToken.None)).Text);
    }

    [Fact]
    public async Task RawVaultIngestRetriesNormallyAfterReplayIsCancelledBetweenGenerations()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        const string profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "older evidence", "reader-1", CapturedAt);
        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "newer evidence", "reader-1", CapturedAt.AddHours(1));
        var archive = new WeArchive.Infrastructure.Archive.SqliteArchiveStore(temp.Combine("archive", "wearchive.db"), new FixedClock());
        var ingester = new RawVaultIngestService(vault, archive, new FixedClock());
        Assert.Equal(2, await ingester.IngestAsync(accountId, null, null, CancellationToken.None));

        using var cancellation = new CancellationTokenSource();
        var progress = new CallbackProgress<string>(message =>
        {
            if (message.Contains("generation", StringComparison.Ordinal)) cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ingester.IngestAsync(accountId, null, progress, cancellation.Token, replay: true));

        Assert.Equal(1, await ingester.IngestAsync(accountId, null, null, CancellationToken.None));
        var conversation = Assert.Single(await archive.ListConversationsAsync(accountId, CancellationToken.None));
        Assert.Equal("newer evidence", Assert.Single(await archive.ReadMessagesAsync(conversation.Id, CancellationToken.None)).Text);
    }

    [Fact]
    public async Task RawVaultScopedIngestSkipsCommittedHistoryBeforeOpeningArtifacts()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        const string profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        var older = await PublishGenerationAsync(vault, temp.Path, profile, accountId, "older scoped data", "reader-1", CapturedAt);
        var archive = new WeArchive.Infrastructure.Archive.SqliteArchiveStore(temp.Combine("archive", "wearchive.db"), new FixedClock());
        var ingester = new RawVaultIngestService(vault, archive, new FixedClock());

        Assert.Equal(1, await ingester.IngestAsync(accountId, "wxid_bob", null, CancellationToken.None));
        var oldArtifact = Path.Combine(older.GenerationDirectory, older.Manifest.Artifacts[0].ContentRef);
        await using (var stream = new FileStream(oldArtifact, FileMode.Append, FileAccess.Write, FileShare.Read))
            await stream.WriteAsync(new byte[] { 0x01 });

        Assert.Equal(0, await ingester.IngestAsync(accountId, "wxid_bob", null, CancellationToken.None));
        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "new scoped data", "reader-1", CapturedAt.AddHours(1));
        Assert.Equal(1, await ingester.IngestAsync(accountId, "wxid_bob", null, CancellationToken.None));
        var conversation = Assert.Single(await archive.ListConversationsAsync(accountId, CancellationToken.None));
        Assert.Equal("new scoped data", Assert.Single(await archive.ReadMessagesAsync(conversation.Id, CancellationToken.None)).Text);
    }

    [Fact]
    public async Task RawVaultIngestKeepsConversationAWhenBPublicationFailsAndRetriesB()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        const string profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "A committed", "reader-1", CapturedAt,
            conversationId: "wxid_a", additionalConversationId: "wxid_b", additionalText: "B retry");
        var sqlite = new WeArchive.Infrastructure.Archive.SqliteArchiveStore(temp.Combine("archive", "wearchive.db"), new FixedClock());
        await sqlite.InitializeAsync(CancellationToken.None);
        var conversationBId = StableIds.Conversation(accountId, ConversationKind.Direct, "wxid_b", "wxid_b");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sqlite.ArchivePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TRIGGER fail_b_checkpoint BEFORE INSERT ON ingest_checkpoints WHEN NEW.scope_id = '{conversationBId}' BEGIN SELECT RAISE(ABORT, 'simulated B checkpoint failure'); END;";
            command.ExecuteNonQuery();
        }
        var ingester = new RawVaultIngestService(vault, sqlite, new FixedClock());

        await Assert.ThrowsAsync<SqliteException>(() =>
            ingester.IngestAsync(accountId, null, null, CancellationToken.None));
        var onlyCommitted = Assert.Single(await sqlite.ListConversationsAsync(accountId, CancellationToken.None));
        Assert.Equal("wxid_a", onlyCommitted.SourceConversationId);
        Assert.Equal("A committed", Assert.Single(await sqlite.ReadMessagesAsync(onlyCommitted.Id, CancellationToken.None)).Text);
        Assert.NotNull(await sqlite.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
            "conversation", onlyCommitted.Id, CancellationToken.None));
        Assert.Null(await sqlite.GetConversationAsync(conversationBId, CancellationToken.None));

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sqlite.ArchivePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER fail_b_checkpoint;";
            command.ExecuteNonQuery();
        }
        Assert.Equal(1, await ingester.IngestAsync(accountId, null, null, CancellationToken.None));
        var afterRetry = await sqlite.ListConversationsAsync(accountId, CancellationToken.None);
        Assert.Equal(2, afterRetry.Count);
        var conversationB = Assert.Single(afterRetry, c => c.SourceConversationId == "wxid_b");
        Assert.Equal("B retry", Assert.Single(await sqlite.ReadMessagesAsync(conversationB.Id, CancellationToken.None)).Text);
        Assert.NotNull(await sqlite.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
            "conversation", conversationB.Id, CancellationToken.None));
    }

    [Fact]
    public async Task RawVaultIngestCancellationDuringConversationRollsBackRowsAndCheckpoint()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        const string profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "many messages", "reader-1", CapturedAt,
            conversationId: "wxid_a", messageCount: 512);
        var archive = new WeArchive.Infrastructure.Archive.SqliteArchiveStore(temp.Combine("archive", "wearchive.db"), new FixedClock());
        var ingester = new RawVaultIngestService(vault, archive, new FixedClock());
        using var cancellation = new CancellationTokenSource();
        var progress = new CallbackProgress<string>(message =>
        {
            if (message.Contains("reading_messages", StringComparison.Ordinal)) cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ingester.IngestAsync(accountId, null, progress, cancellation.Token));

        Assert.Empty(await archive.ListConversationsAsync(accountId, CancellationToken.None));
        Assert.Equal(0, (await archive.GetArchiveStatsAsync(CancellationToken.None)).MessageCount);
        var conversationId = StableIds.Conversation(accountId, ConversationKind.Direct, "wxid_a", "wxid_a");
        Assert.Null(await archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
            "conversation", conversationId, CancellationToken.None));
    }

    [Fact]
    public async Task RawVaultIngestFailsOnNewPublishedGenerationWithInvalidManifestWithoutAdvancingCheckpoints()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        const string profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        var first = await PublishGenerationAsync(vault, temp.Path, profile, accountId, "covered", "reader-1", CapturedAt);
        var archive = new WeArchive.Infrastructure.Archive.SqliteArchiveStore(temp.Combine("archive", "wearchive.db"), new FixedClock());
        var ingester = new RawVaultIngestService(vault, archive, new FixedClock());
        Assert.Equal(1, await ingester.IngestAsync(accountId, null, null, CancellationToken.None));
        var conversation = Assert.Single(await archive.ListConversationsAsync(accountId, CancellationToken.None));
        var priorConversationCheckpoint = await archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
            "conversation", conversation.Id, CancellationToken.None);
        var priorAccountCheckpoint = await archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
            "account", accountId, CancellationToken.None);
        Assert.NotNull(priorConversationCheckpoint);
        Assert.NotNull(priorAccountCheckpoint);

        var invalid = await PublishGenerationAsync(vault, temp.Path, profile, accountId, "new evidence", "reader-1", CapturedAt.AddHours(1));
        await File.WriteAllTextAsync(Path.Combine(invalid.GenerationDirectory, "manifest.json"), "{ malformed");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ingester.IngestAsync(accountId, null, null, CancellationToken.None));
        Assert.Contains(invalid.GenerationId, error.Message, StringComparison.Ordinal);
        Assert.Equal(priorConversationCheckpoint.CheckpointJson,
            (await archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
                "conversation", conversation.Id, CancellationToken.None))?.CheckpointJson);
        Assert.Equal(priorAccountCheckpoint.CheckpointJson,
            (await archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
                "account", accountId, CancellationToken.None))?.CheckpointJson);
        Assert.Equal("covered", Assert.Single(await archive.ReadMessagesAsync(conversation.Id, CancellationToken.None)).Text);
        Assert.Contains(first.GenerationId, priorConversationCheckpoint.CheckpointJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawVaultIngestPublishesEmptyConversationCheckpoint()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        const string profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "unused", "reader-1", CapturedAt,
            conversationId: "wxid_empty", messageCount: 0);
        var archive = new WeArchive.Infrastructure.Archive.SqliteArchiveStore(temp.Combine("archive", "wearchive.db"), new FixedClock());
        var ingester = new RawVaultIngestService(vault, archive, new FixedClock());

        Assert.Equal(1, await ingester.IngestAsync(accountId, null, null, CancellationToken.None));
        var conversation = Assert.Single(await archive.ListConversationsAsync(accountId, CancellationToken.None));
        Assert.Empty(await archive.ReadMessagesAsync(conversation.Id, CancellationToken.None));
        Assert.NotNull(await archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
            "conversation", conversation.Id, CancellationToken.None));
        Assert.Equal(0, await ingester.IngestAsync(accountId, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task RawVaultIngestRechecksCoverageWhenStoredReaderVersionIsOlder()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        const string profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "reader one", "reader-1", CapturedAt,
            conversationId: "wxid_versioned");
        var archive = new WeArchive.Infrastructure.Archive.SqliteArchiveStore(temp.Combine("archive", "wearchive.db"), new FixedClock());
        var ingester = new RawVaultIngestService(vault, archive, new FixedClock());
        Assert.Equal(1, await ingester.IngestAsync(accountId, null, null, CancellationToken.None));
        var conversation = Assert.Single(await archive.ListConversationsAsync(accountId, CancellationToken.None));
        var conversationCheckpoint = await archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
            "conversation", conversation.Id, CancellationToken.None);
        var accountCheckpoint = await archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
            "account", accountId, CancellationToken.None);
        Assert.NotNull(conversationCheckpoint);
        Assert.NotNull(accountCheckpoint);
        await archive.SetIngestCheckpointAsync(conversationCheckpoint with
        {
            CheckpointJson = conversationCheckpoint.CheckpointJson.Replace(
                $"\"reader_version\":\"{WeChatWindowsSourceAdapter.Version}\"", "\"reader_version\":\"0.0.0\"",
                StringComparison.Ordinal),
        }, CancellationToken.None);
        await archive.SetIngestCheckpointAsync(accountCheckpoint with
        {
            CheckpointJson = accountCheckpoint.CheckpointJson.Replace(
                $"\"reader_version\":\"{WeChatWindowsSourceAdapter.Version}\"", "\"reader_version\":\"0.0.0\"",
                StringComparison.Ordinal),
        }, CancellationToken.None);
        Assert.Equal(1, await ingester.IngestAsync(accountId, null, null, CancellationToken.None));
        Assert.Equal("reader one", Assert.Single(await archive.ReadMessagesAsync(conversation.Id, CancellationToken.None)).Text);
        var updatedCheckpoint = await archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
            "conversation", conversation.Id, CancellationToken.None);
        Assert.NotNull(updatedCheckpoint);
        Assert.Contains($"\"reader_version\":\"{WeChatWindowsSourceAdapter.Version}\"", updatedCheckpoint.CheckpointJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawVaultIngestReportsGenerationAndConversationWhenLaterCoverageFails()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        const string profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "prior complete data", "reader-1", CapturedAt);
        var archive = new WeArchive.Infrastructure.Archive.SqliteArchiveStore(temp.Combine("archive", "wearchive.db"), new FixedClock());
        var ingester = new RawVaultIngestService(vault, archive, new FixedClock());
        Assert.Equal(1, await ingester.IngestAsync(accountId, null, null, CancellationToken.None));
        var conversation = Assert.Single(await archive.ListConversationsAsync(accountId, CancellationToken.None));
        var priorConversationCheckpoint = await archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
            "conversation", conversation.Id, CancellationToken.None);
        var priorAccountCheckpoint = await archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
            "account", accountId, CancellationToken.None);
        Assert.NotNull(priorConversationCheckpoint);
        Assert.NotNull(priorAccountCheckpoint);

        var incomplete = await PublishGenerationAsync(vault, temp.Path, profile, accountId, "unavailable later shard",
            "reader-1", CapturedAt.AddHours(1), hasMessageTable: false);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await new IngestCommand(ingester).ExecuteAsync(
            new CliContext(stdout, stderr, new GlobalOptions { Json = true, NoInput = true }),
            ["--account", accountId], CancellationToken.None);
        Assert.Equal(ExitCode.Failure, exitCode);
        Assert.Contains(incomplete.GenerationId, stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("wxid_bob", stderr.ToString(), StringComparison.Ordinal);
        using (var errorDocument = System.Text.Json.JsonDocument.Parse(stdout.ToString()))
        {
            var errorMessage = errorDocument.RootElement.GetProperty("error").GetProperty("message").GetString();
            Assert.Contains(incomplete.GenerationId, errorMessage, StringComparison.Ordinal);
            Assert.Contains("wxid_bob", errorMessage, StringComparison.Ordinal);
        }
        Assert.Equal(priorConversationCheckpoint.CheckpointJson,
            (await archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
                "conversation", conversation.Id, CancellationToken.None))?.CheckpointJson);
        Assert.Equal(priorAccountCheckpoint.CheckpointJson,
            (await archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
                "account", accountId, CancellationToken.None))?.CheckpointJson);
        Assert.Equal("prior complete data", Assert.Single(await archive.ReadMessagesAsync(conversation.Id, CancellationToken.None)).Text);
    }

    private static async Task<RawGeneration> PublishGenerationAsync(
        RawVaultStore vault,
        string scratch,
        string profile,
        string accountId,
        string text,
        string readerVersion,
        DateTimeOffset capturedAt,
        bool hasMessageTable = true,
        long sourceType = 1,
        RawGenerationCompleteness completeness = RawGenerationCompleteness.Complete,
        string conversationId = "wxid_bob",
        string? additionalConversationId = null,
        string? additionalText = null,
        bool additionalHasMessageTable = true,
        int messageCount = 1)
    {
        var dbRoot = Path.Combine(scratch, "db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dbRoot);
        var table = "Msg_" + Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(conversationId))).ToLowerInvariant();
        var additionalTable = additionalConversationId is null ? null
            : "Msg_" + Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(additionalConversationId))).ToLowerInvariant();
        var messageRows = string.Join(Environment.NewLine, Enumerable.Range(1, messageCount)
            .Select(id => $"INSERT INTO \"{table}\" VALUES ({id}, {7000 + id}, {sourceType}, 1, {1736907600 + id}, '{text}', 0, NULL);"));
        BuildDb(Path.Combine(dbRoot, "session.db"), $"""
            CREATE TABLE SessionTable (username TEXT PRIMARY KEY, sort_timestamp INTEGER, last_timestamp INTEGER, last_msg_type INTEGER, last_msg_sub_type INTEGER, summary TEXT);
            INSERT INTO SessionTable VALUES ('{conversationId}', 1737244800, 1737244800, 1, 0, NULL);
            {(additionalConversationId is null ? string.Empty : $"INSERT INTO SessionTable VALUES ('{additionalConversationId}', 1737244700, 1737244700, 1, 0, NULL);")}
            """);
        BuildDb(Path.Combine(dbRoot, "contact.db"), $"""
            CREATE TABLE contact (username TEXT PRIMARY KEY, remark TEXT, nick_name TEXT, alias TEXT, local_type INTEGER);
            INSERT INTO contact VALUES ('{conversationId}', 'Bob', 'Bob', NULL, 1);
            {(additionalConversationId is null ? string.Empty : $"INSERT INTO contact VALUES ('{additionalConversationId}', 'Bob', 'Bob', NULL, 1);")}
            CREATE TABLE stranger (username TEXT PRIMARY KEY, nick_name TEXT);
            """);
        var messageSql = $"""
                CREATE TABLE Name2Id (rowid INTEGER PRIMARY KEY, user_name TEXT);
                INSERT INTO Name2Id VALUES (1, '{conversationId}');
                {(additionalConversationId is null ? string.Empty : $"INSERT INTO Name2Id VALUES (2, '{additionalConversationId}');")}
                """ + (hasMessageTable ? $"""
                CREATE TABLE "{table}" (local_id INTEGER PRIMARY KEY, server_id INTEGER, local_type INTEGER, real_sender_id INTEGER, create_time INTEGER, message_content BLOB, WCDB_CT_message_content INTEGER, compress_content BLOB);
                {messageRows}
                {(additionalTable is null || !additionalHasMessageTable ? string.Empty : $"CREATE TABLE \"{additionalTable}\" (local_id INTEGER PRIMARY KEY, server_id INTEGER, local_type INTEGER, real_sender_id INTEGER, create_time INTEGER, message_content BLOB, WCDB_CT_message_content INTEGER, compress_content BLOB); INSERT INTO \"{additionalTable}\" VALUES (1, 7002, {sourceType}, 1, 1736907600, '{additionalText ?? text}', 0, NULL);")}
                """ : string.Empty);
        BuildDb(Path.Combine(dbRoot, "message_0.db"), messageSql);

        var context = new RawGenerationContext
        {
            AccountId = accountId,
            SourceProfileId = profile,
            CaptureTime = capturedAt,
            CaptureAdapterFamily = WeChatCaptureAdapter.Family,
            CaptureAdapterVersion = readerVersion,
        };
        var previous = await vault.GetLatestGenerationAsync(accountId, CancellationToken.None);
        var session = await vault.BeginGenerationAsync(context, CancellationToken.None);
        var artifacts = new List<RawArtifactDescriptor>();
        foreach (var (name, path) in new[]
        {
            ("session.db", Path.Combine(dbRoot, "session.db")),
            ("contact.db", Path.Combine(dbRoot, "contact.db")),
            ("message_0.db", Path.Combine(dbRoot, "message_0.db")),
        })
        {
            await using var stream = File.OpenRead(path);
            var relative = name == "session.db" ? "session/session.db"
                : name == "contact.db" ? "contact/contact.db"
                : "message/message_0.db";
            artifacts.Add(await session.WriteArtifactAsync("source-database", name, stream, "sqlite", true,
                new Dictionary<string, string> { ["source_relative_path"] = relative }, CancellationToken.None));
        }
        var manifest = new RawManifest
        {
            GenerationId = session.GenerationId,
            AccountId = accountId,
            SourceProfileId = profile,
            Source = new RawManifestSource { AdapterName = WeChatWindowsSourceAdapter.Name, AdapterVersion = readerVersion, SourceProductName = "WeChat for Windows", SourceVersion = "4.1.13.12" },
            Capture = new RawManifestCapture { CaptureTime = capturedAt, CaptureAdapterFamily = WeChatCaptureAdapter.Family, CaptureAdapterVersion = readerVersion, Mode = RawCaptureMode.Baseline, Completeness = completeness, ArtifactCount = artifacts.Count },
            Artifacts = artifacts,
            PreviousGenerationId = previous?.GenerationId,
        };
        var result = await session.PublishAsync(manifest, CancellationToken.None);
        await session.DisposeAsync();
        Directory.Delete(dbRoot, recursive: true);
        return result;
    }

    private static void BuildDb(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static async Task<string> HashArtifactsAsync(RawGeneration generation)
    {
        var hashes = new List<string>();
        foreach (var artifact in generation.Manifest.Artifacts)
        {
            await using var stream = File.OpenRead(Path.Combine(generation.GenerationDirectory, artifact.ContentRef));
            hashes.Add(Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant());
        }
        return string.Join("|", hashes);
    }

    private static async Task<string> FileHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
