using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WeArchive.Cli.Commands;
using WeArchive.Cli.CommandLine;
using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;
using WeArchive.Core.Services;
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
    public async Task OfficialAccountConversationsCapturedInTheBizMessageShardAreRebuildable()
    {
        // The real-account failure Issue #37 exposed: an official-account (gh_) conversation was
        // listed from session.db but its message table lives in message/biz_message_0.db, which a
        // message_ prefix filter skipped, so the whole rebuild aborted with an unreadable
        // conversation. A captured generation that preserves that shard must be fully readable.
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        await PublishGenerationAsync(
            vault, temp.Path, profile, accountId, "official account text", "0.1.0", CapturedAt,
            conversationId: "gh_synthetic_official",
            messageShardRelativePath: "message/biz_message_0.db");
        var archivePath = temp.Combine("archive", "wearchive.db");

        var result = await new RebuildService(vault, archivePath, new FixedClock())
            .RebuildAsync(null, CancellationToken.None);

        Assert.Equal(1, result.Stats.ConversationCount);
        Assert.Equal(1, result.Stats.MessageCount);
        Assert.Empty(result.SkippedAccounts);
        var archive = new WeArchive.Infrastructure.Archive.SqliteArchiveStore(archivePath, new FixedClock());
        var conversation = Assert.Single(await archive.ListConversationsAsync(accountId, CancellationToken.None));
        var message = Assert.Single(await archive.ReadMessagesAsync(conversation.Id, CancellationToken.None));
        Assert.Equal("official account text", message.Text);
    }

    [Fact]
    public async Task ConversationWithoutAMessageTableIsProvablyEmptyWhenRequiredEvidenceIsComplete()
    {
        // WeChat creates a conversation's Msg_ table only once it has records, so a session row
        // with no table is legitimately empty -- but only when the evidence set proves every
        // Required message partition was captured (Issue #37 authorized scope).
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        await PublishGenerationAsync(
            vault, temp.Path, profile, accountId, "with data", "0.1.0", CapturedAt,
            additionalConversationId: "wxid_never_messaged",
            additionalHasMessageTable: false,
            publishCoverage: true);
        var archivePath = temp.Combine("archive", "wearchive.db");

        var result = await new RebuildService(vault, archivePath, new FixedClock())
            .RebuildAsync(null, CancellationToken.None);

        Assert.Equal(1, result.Stats.AccountCount);
        Assert.Equal(2, result.Stats.ConversationCount);
        Assert.Equal(1, result.Stats.MessageCount);
        Assert.Empty(result.SkippedAccounts);

        var archive = new WeArchive.Infrastructure.Archive.SqliteArchiveStore(archivePath, new FixedClock());
        var messageCounts = new List<int>();
        foreach (var conversation in await archive.ListConversationsAsync(accountId, CancellationToken.None))
        {
            messageCounts.Add((await archive.ReadMessagesAsync(conversation.Id, CancellationToken.None)).Count);
        }

        // One conversation carries its record; the table-less one is archived as empty instead of
        // aborting the whole rebuild.
        Assert.Equal(new[] { 0, 1 }, messageCounts.OrderBy(count => count));
    }

    [Fact]
    public async Task ProvablyEmptyConversationCompletesWithTheNoNewRecordsDiagnostic()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        var generation = await PublishGenerationAsync(
            vault, temp.Path, profile, accountId, "with data", "0.1.0", CapturedAt,
            additionalConversationId: "wxid_never_messaged",
            additionalHasMessageTable: false,
            publishCoverage: true);

        using var adapter = CapturedWeChatSourceAdapter.Create(generation);
        var archive = new WeArchive.Infrastructure.Archive.SqliteArchiveStore(
            temp.Combine("archive", "wearchive.db"), new FixedClock());
        var outcome = await new ImportService(adapter, archive, new FixedClock())
            .ImportConversationAsync(
                new ImportRequest
                {
                    SourceProfileId = profile,
                    SourceConversationId = "wxid_never_messaged",
                    Kind = ConversationKind.Direct,
                    PeerSourceUserId = "wxid_never_messaged",
                },
                null,
                CancellationToken.None);

        // Not a silent empty import: the run completes and says why it read nothing.
        Assert.Equal(ImportRunStatus.Completed, outcome.Run.Status);
        var diagnostic = Assert.Single(outcome.Diagnostics, d => d.Code == DiagnosticCodes.NoNewRecords);
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Equal(0, outcome.Run.RecordsScanned);
    }

    [Fact]
    public async Task ConversationWithoutAMessageTableStaysFatalWhenRequiredEvidenceIsNotProven()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);

        // Same shape, but the generation's coverage does not name the required message shard, so
        // this evidence set cannot prove required message coverage is complete.
        var generation = await PublishGenerationAsync(
            vault, temp.Path, profile, accountId, "with data", "0.1.0", CapturedAt,
            additionalConversationId: "wxid_never_messaged",
            additionalHasMessageTable: false,
            publishCoverage: true,
            omitMessageShardFromCoverage: true);

        using (var adapter = CapturedWeChatSourceAdapter.Create(generation))
        {
            var error = await Assert.ThrowsAsync<SourceCoverageException>(async () =>
            {
                await foreach (var _ in adapter.ReadMessagesAsync(
                    profile, "wxid_never_messaged", CancellationToken.None))
                {
                }
            });
            Assert.Equal(DiagnosticCodes.PartitionMissing, error.Code);
        }

        // The rebuild therefore still fails closed rather than publishing a partial conversation.
        var archivePath = temp.Combine("archive", "wearchive.db");
        var thrown = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new RebuildService(vault, archivePath, new FixedClock()).RebuildAsync(null, CancellationToken.None));
        Assert.Contains("wxid_never_messaged", thrown.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(archivePath));
    }

    [Fact]
    public async Task CompleteCoverageWithoutACaptureCheckpointDoesNotProveRequiredMessageEvidence()
    {
        // Issue #39 hardening: coverage alone is not proof. A complete version-2 shape that names
        // every Required message shard but carries no capture checkpoint cannot be cross-validated
        // against artifacts, so it is not accepted as evidence and the table-less conversation
        // stays Fatal instead of being published as legitimately empty.
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        var generation = await PublishGenerationAsync(
            vault, temp.Path, profile, accountId, "with data", "0.1.0", CapturedAt,
            additionalConversationId: "wxid_never_messaged",
            additionalHasMessageTable: false,
            publishCoverage: true,
            publishCheckpoint: false);

        using var adapter = CapturedWeChatSourceAdapter.Create(generation);
        var error = await Assert.ThrowsAsync<SourceCoverageException>(async () =>
        {
            await foreach (var _ in adapter.ReadMessagesAsync(
                profile, "wxid_never_messaged", CancellationToken.None))
            {
            }
        });
        Assert.Equal(DiagnosticCodes.PartitionMissing, error.Code);
    }

    [Fact]
    public async Task CheckpointEvidenceDisagreeingWithCoverageDoesNotProveRequiredMessageEvidence()
    {
        // Issue #39 hardening: every counted shard must appear in the checkpoint with the same
        // source fingerprint its coverage entry records. A checkpoint that names a different
        // source state proves nothing about this generation's coverage, so the captured reader
        // keeps the Fatal source-coverage semantics for a table-less conversation.
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        var generation = await PublishGenerationAsync(
            vault, temp.Path, profile, accountId, "with data", "0.1.0", CapturedAt,
            additionalConversationId: "wxid_never_messaged",
            additionalHasMessageTable: false,
            publishCoverage: true,
            checkpointFingerprintOverride: "0000000000000000000000000000000000000000000000000000000000000000");

        using var adapter = CapturedWeChatSourceAdapter.Create(generation);
        var error = await Assert.ThrowsAsync<SourceCoverageException>(async () =>
        {
            await foreach (var _ in adapter.ReadMessagesAsync(
                profile, "wxid_never_messaged", CancellationToken.None))
            {
            }
        });
        Assert.Equal(DiagnosticCodes.PartitionMissing, error.Code);
    }

    [Fact]
    public async Task ReusedRequiredMessageShardStillProvesRequiredMessageEvidence()
    {
        // Issue #47 (PR #44 test-gap hardening): an incremental run re-verifies partition
        // fingerprints and reuses unchanged preserved artifacts, so in the shipped shape the
        // Required message shard of every generation after the baseline is recorded as `reused`,
        // not `captured`. The rebuild must accept that evidence — otherwise every incremental
        // generation would lose the provably-empty verdict and regress to Fatal — because
        // RawVaultGenerationSession.ReuseArtifactAsync materializes the reused bytes inside the new
        // generation, so only the coverage status differs from a captured shard.
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        await PublishGenerationAsync(
            vault, temp.Path, profile, accountId, "with data", "0.1.0", CapturedAt,
            additionalConversationId: "wxid_never_messaged",
            additionalHasMessageTable: false,
            publishCoverage: true,
            messageShardCoverageStatus: RawPartitionStatus.Reused);
        var archivePath = temp.Combine("archive", "wearchive.db");

        var result = await new RebuildService(vault, archivePath, new FixedClock())
            .RebuildAsync(null, CancellationToken.None);

        Assert.Equal(2, result.Stats.ConversationCount);
        Assert.Equal(1, result.Stats.MessageCount);
        Assert.Empty(result.SkippedAccounts);
    }

    [Fact]
    public async Task ReusedRequiredMessageShardWithoutCheckpointEvidenceDoesNotProveRequiredMessageEvidence()
    {
        // Issue #47 (PR #44 test-gap hardening): `reused` is accepted by the predicate exactly as
        // `captured` is, so it must fail closed the same way. A reused Required message shard whose
        // fingerprint is absent from the checkpoint's captured/reused set proves nothing about this
        // generation, and the table-less conversation keeps the Fatal semantics rather than being
        // published as legitimately empty.
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);

        await AssertEvidenceNotProvenAsync(
            temp, vault, profile, accountId,
            omitMessageShardFromCheckpoint: true,
            messageShardCoverageStatus: RawPartitionStatus.Reused);
    }

    [Fact]
    public async Task CheckpointOfAnotherVersionDoesNotProveRequiredMessageEvidence()
    {
        // Issue #47 (PR #44 test-gap hardening): the predicate only understands checkpoint
        // version 1. A future or hand-built version is not "close enough" — its field meanings are
        // not defined for this reader, so it proves nothing (docs/RAW_VAULT.md section 7).
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);

        await AssertEvidenceNotProvenAsync(temp, vault, profile, accountId, checkpointVersion: 2);
    }

    [Fact]
    public async Task CheckpointOfAnotherGenerationDoesNotProveRequiredMessageEvidence()
    {
        // Issue #47 (PR #44 test-gap hardening): the checkpoint is this generation's capture
        // cursor. A checkpoint naming a different generation could only prove evidence for that
        // other generation, so coverage cross-checked against it is rejected.
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);

        await AssertEvidenceNotProvenAsync(
            temp, vault, profile, accountId, checkpointGenerationId: "gen_" + new string('0', 16));
    }

    [Fact]
    public async Task CheckpointOfAnotherCaptureAdapterFamilyDoesNotProveRequiredMessageEvidence()
    {
        // Issue #47 (PR #44 test-gap hardening): mirroring WeChatCaptureAdapter.BuildPriorMap, a
        // checkpoint written by a different capture adapter family is not evidence for this
        // generation's partitions, even though its coverage names the Required message shards.
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);

        await AssertEvidenceNotProvenAsync(
            temp, vault, profile, accountId, checkpointCaptureAdapterFamily: "other-adapter");
    }

    [Fact]
    public async Task CheckpointOfAnotherCaptureAdapterVersionDoesNotProveRequiredMessageEvidence()
    {
        // Issue #47 (PR #44 test-gap hardening): the capture adapter version is part of the
        // checkpoint identity; a disagreement means the fingerprint semantics counted by coverage
        // are not the ones the checkpoint recorded, so the generation fails closed.
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);

        await AssertEvidenceNotProvenAsync(
            temp, vault, profile, accountId, checkpointCaptureAdapterVersion: "0.2.0");
    }

    [Fact]
    public async Task AccountDirectoryWithoutAPublishedGenerationIsReportedAndSkipped()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "captured text", "0.1.0", CapturedAt);

        // A failed or cancelled capture leaves the account scaffold behind: an account directory
        // whose generations directory is empty. It must not abort every other account's rebuild.
        var emptyAccountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, "wxid_unpublished");
        Directory.CreateDirectory(Path.Combine(vault.VaultRoot, "accounts", emptyAccountId, "generations"));
        var archivePath = temp.Combine("archive", "wearchive.db");

        var result = await new RebuildService(vault, archivePath, new FixedClock())
            .RebuildAsync(null, CancellationToken.None);

        Assert.Equal(1, result.Stats.AccountCount);
        Assert.Equal(1, result.Stats.ConversationCount);
        Assert.Equal(1, result.Stats.MessageCount);
        var skipped = Assert.Single(result.SkippedAccounts);
        Assert.Equal(emptyAccountId, skipped.AccountId);
        Assert.False(string.IsNullOrWhiteSpace(skipped.Reason));
        Assert.True(File.Exists(archivePath));
    }

    [Fact]
    public async Task RebuildFailsWhenNoAccountHasAPublishedGeneration()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var emptyAccountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, "wxid_unpublished");
        Directory.CreateDirectory(Path.Combine(vault.VaultRoot, "accounts", emptyAccountId, "generations"));
        var archivePath = temp.Combine("archive", "wearchive.db");

        var thrown = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new RebuildService(vault, archivePath, new FixedClock()).RebuildAsync(null, CancellationToken.None));

        // Publishing an empty archive over a usable one would be worse than failing explicitly.
        Assert.Contains(emptyAccountId, thrown.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(archivePath));
    }

    [Fact]
    public async Task RebuildJsonReportsSkippedAccountDirectories()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "captured text", "0.1.0", CapturedAt);
        var emptyAccountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, "wxid_unpublished");
        Directory.CreateDirectory(Path.Combine(vault.VaultRoot, "accounts", emptyAccountId, "generations"));
        var archivePath = temp.Combine("archive", "wearchive.db");

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await new RebuildCommand(new RebuildService(vault, archivePath, new FixedClock()))
            .ExecuteAsync(new CliContext(stdout, stderr, new GlobalOptions { Json = true, NoInput = true }), [], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        Assert.Empty(stderr.ToString());
        using var json = System.Text.Json.JsonDocument.Parse(stdout.ToString());
        Assert.True(json.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.Equal(1, json.RootElement.GetProperty("account_count").GetInt32());
        var skipped = json.RootElement.GetProperty("skipped_accounts").EnumerateArray().ToArray();
        var entry = Assert.Single(skipped);
        Assert.Equal(emptyAccountId, entry.GetProperty("account_id").GetString());
        Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("reason").GetString()));
    }

    [Fact]
    public async Task ReadingPreservedEvidenceLeavesTheGenerationDirectoryUntouched()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        // The message shard is a WAL-mode image, which is what a real captured artifact is. Opening
        // it in place without immutable semantics made SQLite create -wal/-shm sidecars inside the
        // published generation directory (and once deleted the artifact itself).
        var generation = await PublishGenerationAsync(
            vault, temp.Path, profile, accountId, "captured text", "0.1.0", CapturedAt,
            walMode: true, publishCoverage: true);
        var before = DescribeGenerationDirectory(generation);
        var archivePath = temp.Combine("archive", "wearchive.db");

        await new RebuildService(vault, archivePath, new FixedClock()).RebuildAsync(null, CancellationToken.None);

        Assert.Equal(before, DescribeGenerationDirectory(generation));
    }

    /// <summary>The exact file set and per-file hashes of one published generation.</summary>
    private static string DescribeGenerationDirectory(RawGeneration generation)
    {
        var entries = Directory.EnumerateFileSystemEntries(generation.GenerationDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(generation.GenerationDirectory, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path =>
            {
                var full = Path.Combine(generation.GenerationDirectory, path);
                return Directory.Exists(full)
                    ? path + "/"
                    : path + "=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))).ToLowerInvariant();
            });
        return string.Join("\n", entries);
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
    public async Task RawVaultScopedIngestAdvancesUnchangedConversationCoverageAndSkipsOnRepeat()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        const string profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "A first", "reader-1", CapturedAt,
            conversationId: "wxid_a", additionalConversationId: "wxid_b", additionalText: "B unchanged");
        var archive = new WeArchive.Infrastructure.Archive.SqliteArchiveStore(temp.Combine("archive", "wearchive.db"), new FixedClock());
        var ingester = new RawVaultIngestService(vault, archive, new FixedClock());
        Assert.Equal(1, await ingester.IngestAsync(accountId, "wxid_b", null, CancellationToken.None));
        var conversationB = Assert.Single(await archive.ListConversationsAsync(accountId, CancellationToken.None));
        var firstCheckpoint = await archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
            "conversation", conversationB.Id, CancellationToken.None);
        Assert.NotNull(firstCheckpoint);
        long ImportRunCount()
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = archive.ArchivePath,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM import_runs;";
            return (long)command.ExecuteScalar()!;
        }
        var runsBeforeNewGeneration = ImportRunCount();

        var newer = await PublishGenerationAsync(vault, temp.Path, profile, accountId, "A changed", "reader-1", CapturedAt.AddHours(1),
            conversationId: "wxid_a", additionalConversationId: "wxid_b", additionalText: "B unchanged");
        Assert.Equal(0, await ingester.IngestAsync(accountId, "wxid_b", null, CancellationToken.None));
        var unchangedCheckpoint = await archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
            "conversation", conversationB.Id, CancellationToken.None);
        Assert.NotNull(unchangedCheckpoint);
        Assert.Equal(firstCheckpoint.CheckpointJson, unchangedCheckpoint.CheckpointJson);
        var coverageCheckpoint = await archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
            "conversation_coverage", conversationB.Id, CancellationToken.None);
        Assert.NotNull(coverageCheckpoint);
        Assert.Contains(newer.GenerationId, coverageCheckpoint.CheckpointJson, StringComparison.Ordinal);
        Assert.Equal(runsBeforeNewGeneration, ImportRunCount());

        var changedArtifact = Path.Combine(newer.GenerationDirectory, newer.Manifest.Artifacts[0].ContentRef);
        await using (var stream = new FileStream(changedArtifact, FileMode.Append, FileAccess.Write, FileShare.Read))
            await stream.WriteAsync(new byte[] { 0x01 });

        Assert.Equal(0, await ingester.IngestAsync(accountId, "wxid_b", null, CancellationToken.None));
        Assert.Equal(unchangedCheckpoint.CheckpointJson,
            (await archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
                "conversation", conversationB.Id, CancellationToken.None))?.CheckpointJson);
        Assert.Equal("B unchanged", Assert.Single(await archive.ReadMessagesAsync(conversationB.Id, CancellationToken.None)).Text);
        Assert.Equal(runsBeforeNewGeneration, ImportRunCount());
    }

    [Fact]
    public async Task RawVaultScopedReplayCancellationInvalidatesNewerCoverageBeforeRetry()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        const string profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        var first = await PublishGenerationAsync(vault, temp.Path, profile, accountId, "A", "reader-1", CapturedAt,
            conversationId: "wxid_bob");
        var second = await PublishGenerationAsync(vault, temp.Path, profile, accountId, "B", "reader-1", CapturedAt.AddHours(1),
            conversationId: "wxid_bob");
        var third = await PublishGenerationAsync(vault, temp.Path, profile, accountId, "B", "reader-1", CapturedAt.AddHours(2),
            conversationId: "wxid_bob");
        var archive = new WeArchive.Infrastructure.Archive.SqliteArchiveStore(temp.Combine("archive", "wearchive.db"), new FixedClock());
        var ingester = new RawVaultIngestService(vault, archive, new FixedClock());

        Assert.Equal(2, await ingester.IngestAsync(accountId, "wxid_bob", null, CancellationToken.None));
        var conversation = Assert.Single(await archive.ListConversationsAsync(accountId, CancellationToken.None));
        Assert.Equal("B", Assert.Single(await archive.ReadMessagesAsync(conversation.Id, CancellationToken.None)).Text);
        var coverage = await archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
            "conversation_coverage", conversation.Id, CancellationToken.None);
        Assert.NotNull(coverage);
        Assert.Contains(third.GenerationId, coverage.CheckpointJson, StringComparison.Ordinal);

        using var cancellation = new CancellationTokenSource();
        var progress = new CallbackProgress<string>(message =>
        {
            if (message == $"Ingested {conversation.Id} from generation {first.GenerationId}")
                cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ingester.IngestAsync(accountId, "wxid_bob", progress, cancellation.Token, replay: true));

        Assert.Equal("A", Assert.Single(await archive.ReadMessagesAsync(conversation.Id, CancellationToken.None)).Text);
        var invalidatedCoverage = await archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
            "conversation_coverage", conversation.Id, CancellationToken.None);
        Assert.NotNull(invalidatedCoverage);
        Assert.Contains("coverage_invalidated", invalidatedCoverage.CheckpointJson, StringComparison.Ordinal);

        Assert.Equal(1, await ingester.IngestAsync(accountId, "wxid_bob", null, CancellationToken.None));
        Assert.Equal("B", Assert.Single(await archive.ReadMessagesAsync(conversation.Id, CancellationToken.None)).Text);
        var contentCheckpoint = await archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
            "conversation", conversation.Id, CancellationToken.None);
        Assert.NotNull(contentCheckpoint);
        Assert.Contains(second.GenerationId, contentCheckpoint.CheckpointJson, StringComparison.Ordinal);
        var retriedCoverage = await archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
            "conversation_coverage", conversation.Id, CancellationToken.None);
        Assert.NotNull(retriedCoverage);
        Assert.Contains(third.GenerationId, retriedCoverage.CheckpointJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawVaultIngestRefreshesParticipantMetadataFromContactOnlyGeneration()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        const string profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "same messages", "reader-1", CapturedAt,
            conversationId: "wxid_bob", participantRemark: "Old remark");
        var archive = new WeArchive.Infrastructure.Archive.SqliteArchiveStore(temp.Combine("archive", "wearchive.db"), new FixedClock());
        var ingester = new RawVaultIngestService(vault, archive, new FixedClock());
        Assert.Equal(1, await ingester.IngestAsync(accountId, null, null, CancellationToken.None));
        var participantId = StableIds.Participant(accountId, "wxid_bob");
        Assert.Equal("Old remark", Assert.Single(await archive.ListParticipantsAsync(accountId, CancellationToken.None),
            participant => participant.Id == participantId).LatestRemark);

        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "same messages", "reader-1", CapturedAt.AddHours(1),
            conversationId: "wxid_bob", participantRemark: "New remark");

        Assert.Equal(1, await ingester.IngestAsync(accountId, null, null, CancellationToken.None));
        Assert.Equal("New remark", Assert.Single(await archive.ListParticipantsAsync(accountId, CancellationToken.None),
            participant => participant.Id == participantId).LatestRemark);
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

    /// <summary>
    /// A scoped ingest accepts the stable conversation id as well as the upstream source
    /// conversation id, so a Collection (whose membership keys are stable conversation ids) can
    /// reuse this ingest path instead of introducing a second enumerator
    /// (docs/DATA_MODEL.md section 16, docs/PRD.md FR-23).
    /// </summary>
    [Fact]
    public async Task RawVaultScopedIngestAcceptsTheStableConversationIdOrTheSourceConversationId()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        const string profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "stable id evidence", "reader-1", CapturedAt,
            conversationId: "wxid_bob");
        var archive = new WeArchive.Infrastructure.Archive.SqliteArchiveStore(temp.Combine("archive", "wearchive.db"), new FixedClock());
        var ingester = new RawVaultIngestService(vault, archive, new FixedClock());
        var stableId = StableIds.Conversation(accountId, ConversationKind.Direct, "wxid_bob", "wxid_bob");

        Assert.Equal(1, await ingester.IngestConversationAsync(accountId, stableId, null, CancellationToken.None));

        var conversation = Assert.Single(await archive.ListConversationsAsync(accountId, CancellationToken.None));
        Assert.Equal(stableId, conversation.Id);
        Assert.Equal("stable id evidence", Assert.Single(await archive.ReadMessagesAsync(conversation.Id, CancellationToken.None)).Text);

        // The upstream source conversation id keeps selecting the same conversation and is a
        // no-change repeat rather than a duplicate publication.
        Assert.Equal(0, await ingester.IngestConversationAsync(accountId, "wxid_bob", null, CancellationToken.None));
        Assert.Single(await archive.ListConversationsAsync(accountId, CancellationToken.None));
    }

    /// <summary>
    /// A selector that matches no preserved conversation is a typed, distinguishable outcome: a
    /// multi-scope caller must report an unresolved Collection member differently from a failure
    /// that was attempted and rolled back (docs/PRD.md FR-23).
    /// </summary>
    [Fact]
    public async Task RawVaultScopedIngestReportsAnUnresolvedConversationWithATypedFailure()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        const string profile = "wxid_alice";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        await PublishGenerationAsync(vault, temp.Path, profile, accountId, "present", "reader-1", CapturedAt,
            conversationId: "wxid_bob");
        var archive = new WeArchive.Infrastructure.Archive.SqliteArchiveStore(temp.Combine("archive", "wearchive.db"), new FixedClock());
        var ingester = new RawVaultIngestService(vault, archive, new FixedClock());
        var absent = StableIds.Conversation(accountId, ConversationKind.Direct, "wxid_absent", "wxid_absent");

        var error = await Assert.ThrowsAsync<ConversationNotInRawVaultException>(
            () => ingester.IngestConversationAsync(accountId, absent, null, CancellationToken.None));

        Assert.Equal(absent, error.ConversationSelector);
        Assert.Equal(accountId, error.AccountId);
        Assert.Empty(await archive.ListConversationsAsync(accountId, CancellationToken.None));
    }

    /// <summary>
    /// Publishes a complete generation whose coverage names every Required message shard, applies
    /// the given checkpoint defect to it, and asserts that the captured reader refuses to treat
    /// that evidence as proof: reading the table-less conversation keeps the existing Fatal
    /// <c>partition_missing</c> outcome instead of publishing it as legitimately empty
    /// (Issue #47, PR #44 fail-closed follow-ups). Such manifests are not reachable through the
    /// shipped capture path and are rejected by `RawManifestSerializer` on read; the predicate is
    /// the second line of defence for hand-built or future manifests.
    /// </summary>
    private static async Task AssertEvidenceNotProvenAsync(
        TempDirectory temp,
        RawVaultStore vault,
        string profile,
        string accountId,
        int checkpointVersion = 1,
        string? checkpointGenerationId = null,
        string? checkpointCaptureAdapterFamily = null,
        string? checkpointCaptureAdapterVersion = null,
        bool omitMessageShardFromCheckpoint = false,
        RawPartitionStatus messageShardCoverageStatus = RawPartitionStatus.Captured)
    {
        var generation = await PublishGenerationAsync(
            vault, temp.Path, profile, accountId, "with data", "0.1.0", CapturedAt,
            additionalConversationId: "wxid_never_messaged",
            additionalHasMessageTable: false,
            publishCoverage: true,
            checkpointVersion: checkpointVersion,
            checkpointGenerationId: checkpointGenerationId,
            checkpointCaptureAdapterFamily: checkpointCaptureAdapterFamily,
            checkpointCaptureAdapterVersion: checkpointCaptureAdapterVersion,
            omitMessageShardFromCheckpoint: omitMessageShardFromCheckpoint,
            messageShardCoverageStatus: messageShardCoverageStatus);

        using var adapter = CapturedWeChatSourceAdapter.Create(generation);
        var error = await Assert.ThrowsAsync<SourceCoverageException>(async () =>
        {
            await foreach (var _ in adapter.ReadMessagesAsync(
                profile, "wxid_never_messaged", CancellationToken.None))
            {
            }
        });
        Assert.Equal(DiagnosticCodes.PartitionMissing, error.Code);
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
        int messageCount = 1,
        string participantRemark = "Bob",
        string messageShardRelativePath = "message/message_0.db",
        bool walMode = false,
        bool publishCoverage = false,
        bool omitMessageShardFromCoverage = false,
        bool publishCheckpoint = true,
        string? checkpointFingerprintOverride = null,
        int checkpointVersion = 1,
        string? checkpointGenerationId = null,
        string? checkpointCaptureAdapterFamily = null,
        string? checkpointCaptureAdapterVersion = null,
        bool omitMessageShardFromCheckpoint = false,
        RawPartitionStatus messageShardCoverageStatus = RawPartitionStatus.Captured)
    {
        var dbRoot = Path.Combine(scratch, "db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dbRoot);
        var messageShardFileName = Path.GetFileName(messageShardRelativePath);
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
            INSERT INTO contact VALUES ('{conversationId}', '{participantRemark}', 'Bob', NULL, 1);
            {(additionalConversationId is null ? string.Empty : $"INSERT INTO contact VALUES ('{additionalConversationId}', '{participantRemark}', 'Bob', NULL, 1);")}
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
        BuildDb(Path.Combine(dbRoot, messageShardFileName), (walMode ? "PRAGMA journal_mode=WAL;" : string.Empty) + messageSql);

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
        foreach (var (name, relative, path) in new[]
        {
            ("session.db", "session/session.db", Path.Combine(dbRoot, "session.db")),
            ("contact.db", "contact/contact.db", Path.Combine(dbRoot, "contact.db")),
            (messageShardFileName, messageShardRelativePath, Path.Combine(dbRoot, messageShardFileName)),
        })
        {
            await using var stream = File.OpenRead(path);
            artifacts.Add(await session.WriteArtifactAsync("source-database", name, stream, "sqlite", true,
                new Dictionary<string, string> { ["source_relative_path"] = relative }, CancellationToken.None));
        }
        var coverage = new List<RawPartitionCoverage>();
        RawCaptureCheckpoint? checkpoint = null;
        if (publishCoverage)
        {
            coverage =
            [
                .. artifacts
                    .Where(a => !omitMessageShardFromCoverage ||
                        a.Metadata!["source_relative_path"] != messageShardRelativePath)
                    .Select(a => new RawPartitionCoverage
                    {
                        PartitionId = a.Metadata!["source_relative_path"],
                        Status = a.Metadata!["source_relative_path"] == messageShardRelativePath
                            ? messageShardCoverageStatus
                            : RawPartitionStatus.Captured,
                        SourceFingerprint = a.Sha256,
                        ArtifactSha256 = a.Sha256,
                    }),
            ];
            var fingerprints = coverage.ToDictionary(
                c => c.PartitionId,
                c => checkpointFingerprintOverride ?? c.SourceFingerprint!,
                StringComparer.Ordinal);
            if (omitMessageShardFromCheckpoint)
            {
                fingerprints.Remove(messageShardRelativePath);
            }

            checkpoint = new RawCaptureCheckpoint
            {
                Version = checkpointVersion,
                GenerationId = checkpointGenerationId ?? session.GenerationId,
                CaptureAdapterFamily = checkpointCaptureAdapterFamily ?? WeChatCaptureAdapter.Family,
                CaptureAdapterVersion = checkpointCaptureAdapterVersion ?? readerVersion,
                PartitionFingerprints = fingerprints,
            };
            if (!publishCheckpoint)
            {
                checkpoint = null;
            }
        }

        var manifest = new RawManifest
        {
            GenerationId = session.GenerationId,
            AccountId = accountId,
            SourceProfileId = profile,
            Source = new RawManifestSource { AdapterName = WeChatWindowsSourceAdapter.Name, AdapterVersion = readerVersion, SourceProductName = "WeChat for Windows", SourceVersion = "4.1.13.12" },
            Capture = new RawManifestCapture { CaptureTime = capturedAt, CaptureAdapterFamily = WeChatCaptureAdapter.Family, CaptureAdapterVersion = readerVersion, Mode = RawCaptureMode.Baseline, Completeness = completeness, ArtifactCount = artifacts.Count },
            Artifacts = artifacts,
            Coverage = coverage,
            CaptureCheckpoint = checkpoint,
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
