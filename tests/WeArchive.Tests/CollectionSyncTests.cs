using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using WeArchive.Core.Collections;
using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;
using WeArchive.Core.Services;
using WeArchive.Infrastructure;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// Integration tests for Collection-scoped synchronization (docs/PRD.md FR-23/FR-29, G4/G8,
/// docs/HARNESS.md section 9, docs/ARCHITECTURE.md section 3.8).
/// <para>
/// Every case runs the production path — authoritative configuration file, catalog resolution,
/// <c>CaptureService</c>, Raw Vault publication and the real Raw Vault ingest — with the synthetic
/// WeChat capture source. The defining property under test is that Collection execution is
/// multi-scope: each conversation advances its own ingest checkpoint, and one conversation's Fatal
/// failure never reverts another conversation's committed progress.
/// </para>
/// </summary>
public sealed class CollectionSyncTests
{
    [Fact]
    public async Task CollectionSyncPublishesEveryMemberIndependently()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A message" },
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("b"), Text = "B message" },
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.GroupId("100200300"), Text = "C message" });

        harness.WriteCollection("project-x",
            harness.StableId(WeChatSyncHarness.DirectId("a")),
            harness.StableId(WeChatSyncHarness.DirectId("b")),
            harness.StableId(WeChatSyncHarness.GroupId("100200300")));

        var result = await harness.Sync.SyncAsync(
            new CollectionSyncRequest { CollectionName = "project-x" }, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Items.Count);
        Assert.All(result.Items, item => Assert.Equal(CollectionSyncItemStatus.Succeeded, item.Status));
        Assert.Equal(3, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);
        Assert.False(string.IsNullOrEmpty(result.AccountId));
        Assert.False(string.IsNullOrEmpty(result.GenerationId));
        Assert.Empty(result.InvalidConversationIds);
        Assert.Empty(result.DuplicateConversationIds);

        // Every member is addressable in the archive by the same stable id the Collection declared,
        // and each advanced its own ingest checkpoint inside its own conversation transaction.
        var conversations = await harness.ConversationsAsync();
        Assert.Equal(3, conversations.Count);
        foreach (var item in result.Items)
        {
            var conversation = Assert.Single(conversations, c => c.Id == item.ConversationId);
            Assert.NotEmpty(await harness.MessagesAsync(conversation.Id));
            Assert.NotNull(await harness.ConversationCheckpointAsync(item.ConversationId));
        }
    }

    [Fact]
    public async Task OneFatalConversationDoesNotRevertSuccessfulConversations()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A committed" },
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("b"), Text = "B committed" },
            new SyntheticCaptureConversation
            {
                SourceConversationId = WeChatSyncHarness.GroupId("100200300"),
                Text = "C unreadable",
                MessageTablePresent = false,
                MessageShardUnreadable = true,
            });

        var idA = harness.StableId(WeChatSyncHarness.DirectId("a"));
        var idB = harness.StableId(WeChatSyncHarness.DirectId("b"));
        var idC = harness.StableId(WeChatSyncHarness.GroupId("100200300"));
        harness.WriteCollection("project-x", idA, idB, idC);

        var result = await harness.Sync.SyncAsync(
            new CollectionSyncRequest { CollectionName = "project-x" }, null, CancellationToken.None);

        // A partially successful Collection run must never be described as total success...
        Assert.False(result.Succeeded);
        Assert.Equal(3, result.Items.Count);

        // ...while the successful members stay visible with their own committed state.
        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(0, result.NoChangeCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(CollectionSyncItemStatus.Succeeded, Assert.Single(result.Items, i => i.ConversationId == idA).Status);
        Assert.Equal(CollectionSyncItemStatus.Succeeded, Assert.Single(result.Items, i => i.ConversationId == idB).Status);
        var failed = Assert.Single(result.Items, i => i.ConversationId == idC);
        Assert.Equal(CollectionSyncItemStatus.Failed, failed.Status);
        Assert.False(string.IsNullOrWhiteSpace(failed.Error));

        var conversations = await harness.ConversationsAsync();
        Assert.Equal(2, conversations.Count);
        Assert.Equal("A committed 1", Assert.Single(await harness.MessagesAsync(idA)).Text);
        Assert.Equal("B committed 1", Assert.Single(await harness.MessagesAsync(idB)).Text);
        Assert.Null(await harness.FindConversationAsync(WeChatSyncHarness.GroupId("100200300")));

        // The successful conversations kept their committed checkpoints; the failed one has none,
        // so nothing about it can be mistaken for progress.
        Assert.NotNull(await harness.ConversationCheckpointAsync(idA));
        Assert.NotNull(await harness.ConversationCheckpointAsync(idB));
        Assert.Null(await harness.ConversationCheckpointAsync(idC));
    }

    [Fact]
    public async Task ChangedConversationAdvancesWhileAnUnchangedConversationReportsNoChange()
    {
        using var temp = new TempDirectory();
        var changed = new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A first" };
        var stable = new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("b"), Text = "B stable" };
        using var harness = WeChatSyncHarness.Create(temp, changed, stable);

        var idA = harness.StableId(WeChatSyncHarness.DirectId("a"));
        var idB = harness.StableId(WeChatSyncHarness.DirectId("b"));
        harness.WriteCollection("project-x", idA, idB);

        var first = await harness.Sync.SyncAsync(
            new CollectionSyncRequest { CollectionName = "project-x" }, null, CancellationToken.None);
        Assert.True(first.Succeeded);
        Assert.Equal(2, first.SucceededCount);
        var checkpointBAfterFirst = await harness.ConversationCheckpointAsync(idB);

        // Only conversation A's preserved evidence changes.
        changed.Text = "A second";
        harness.AdvanceClock();

        var second = await harness.Sync.SyncAsync(
            new CollectionSyncRequest { CollectionName = "project-x" }, null, CancellationToken.None);

        Assert.True(second.Succeeded);
        Assert.Equal(CollectionSyncItemStatus.Succeeded, Assert.Single(second.Items, i => i.ConversationId == idA).Status);
        var noChange = Assert.Single(second.Items, i => i.ConversationId == idB);
        Assert.Equal(CollectionSyncItemStatus.NoChange, noChange.Status);
        Assert.Equal(0, noChange.ConversationsIngested);
        Assert.Equal(1, second.SucceededCount);
        Assert.Equal(1, second.NoChangeCount);
        Assert.Equal(0, second.FailedCount);

        // The unchanged conversation kept its checkpoint value; the changed one advanced.
        Assert.Equal(checkpointBAfterFirst!.CheckpointJson, (await harness.ConversationCheckpointAsync(idB))!.CheckpointJson);
        Assert.Contains("A second", Assert.Single(await harness.MessagesAsync(idA)).Text, StringComparison.Ordinal);
        Assert.Contains("B stable", Assert.Single(await harness.MessagesAsync(idB)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetryingAfterAFatalMemberPreservesSuccessfulProgressAndReattemptsTheFailedMember()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A committed" },
            new SyntheticCaptureConversation
            {
                SourceConversationId = WeChatSyncHarness.GroupId("100200300"),
                Text = "C unreadable",
                MessageTablePresent = false,
                MessageShardUnreadable = true,
            });

        var idA = harness.StableId(WeChatSyncHarness.DirectId("a"));
        var idC = harness.StableId(WeChatSyncHarness.GroupId("100200300"));
        harness.WriteCollection("project-x", idA, idC);

        var first = await harness.Sync.SyncAsync(
            new CollectionSyncRequest { CollectionName = "project-x" }, null, CancellationToken.None);
        Assert.False(first.Succeeded);
        var checkpointAAfterFirst = await harness.ConversationCheckpointAsync(idA);
        Assert.NotNull(checkpointAAfterFirst);

        // A later capture publishes new evidence; the preserved generation for C is still
        // incomplete, so a retry re-attempts C instead of pretending it is done.
        harness.AdvanceClock();

        var second = await harness.Sync.SyncAsync(
            new CollectionSyncRequest { CollectionName = "project-x" }, null, CancellationToken.None);

        Assert.False(second.Succeeded);
        // The earlier success is neither re-published nor regressed: its checkpoint is unchanged.
        var unchanged = Assert.Single(second.Items, i => i.ConversationId == idA);
        Assert.Equal(CollectionSyncItemStatus.NoChange, unchanged.Status);
        Assert.Equal(checkpointAAfterFirst!.CheckpointJson, (await harness.ConversationCheckpointAsync(idA))!.CheckpointJson);
        Assert.Equal("A committed 1", Assert.Single(await harness.MessagesAsync(idA)).Text);

        // The failed member is retried, reported again, and still holds no progress.
        var retried = Assert.Single(second.Items, i => i.ConversationId == idC);
        Assert.Equal(CollectionSyncItemStatus.Failed, retried.Status);
        Assert.False(string.IsNullOrWhiteSpace(retried.Error));
        Assert.Null(await harness.ConversationCheckpointAsync(idC));
        Assert.Null(await harness.FindConversationAsync(WeChatSyncHarness.GroupId("100200300")));
    }

    [Fact]
    public async Task RetryingAfterCancellationCompletesTheInterruptedConversationAndKeepsEarlierProgress()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A committed" },
            new SyntheticCaptureConversation
            {
                SourceConversationId = WeChatSyncHarness.DirectId("b"),
                Text = "B interrupted",
                MessageCount = 300,
            },
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("c"), Text = "C committed" });

        var idA = harness.StableId(WeChatSyncHarness.DirectId("a"));
        var idB = harness.StableId(WeChatSyncHarness.DirectId("b"));
        var idC = harness.StableId(WeChatSyncHarness.DirectId("c"));
        harness.WriteCollection("project-x", idA, idB, idC);

        using (var cancellation = new CancellationTokenSource())
        {
            var progress = new SyncProgress(message =>
            {
                if (message.StartsWith($"Ingesting {idB} ", StringComparison.Ordinal))
                {
                    cancellation.Cancel();
                }
            });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                harness.Sync.SyncAsync(
                    new CollectionSyncRequest { CollectionName = "project-x" }, progress, cancellation.Token));
        }

        var checkpointAAfterCancellation = await harness.ConversationCheckpointAsync(idA);
        Assert.NotNull(checkpointAAfterCancellation);

        harness.AdvanceClock();

        // The retry resumes with the same preserved evidence: A is untouched, B and C complete.
        var retry = await harness.Sync.SyncAsync(
            new CollectionSyncRequest { CollectionName = "project-x" }, null, CancellationToken.None);

        Assert.True(retry.Succeeded);
        Assert.Equal(CollectionSyncItemStatus.NoChange, Assert.Single(retry.Items, i => i.ConversationId == idA).Status);
        Assert.Equal(CollectionSyncItemStatus.Succeeded, Assert.Single(retry.Items, i => i.ConversationId == idB).Status);
        Assert.Equal(CollectionSyncItemStatus.Succeeded, Assert.Single(retry.Items, i => i.ConversationId == idC).Status);
        Assert.Equal(checkpointAAfterCancellation!.CheckpointJson, (await harness.ConversationCheckpointAsync(idA))!.CheckpointJson);
        Assert.NotEmpty(await harness.MessagesAsync(idB));
        Assert.NotEmpty(await harness.MessagesAsync(idC));
    }

    [Fact]
    public async Task CancellationDuringOneConversationKeepsEarlierCommitsAndRollsBackTheCurrentOne()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A committed" },
            new SyntheticCaptureConversation
            {
                SourceConversationId = WeChatSyncHarness.DirectId("b"),
                Text = "B interrupted",
                MessageCount = 300,
            },
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("c"), Text = "C untouched" });

        var idA = harness.StableId(WeChatSyncHarness.DirectId("a"));
        var idB = harness.StableId(WeChatSyncHarness.DirectId("b"));
        var idC = harness.StableId(WeChatSyncHarness.DirectId("c"));
        harness.WriteCollection("project-x", idA, idB, idC);

        using var cancellation = new CancellationTokenSource();
        var progress = new SyncProgress(message =>
        {
            // Cancel while conversation B is being ingested, after A has already committed.
            if (message.StartsWith($"Ingesting {idB} ", StringComparison.Ordinal))
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Sync.SyncAsync(
                new CollectionSyncRequest { CollectionName = "project-x" }, progress, cancellation.Token));

        // Completed conversations keep their progress...
        var conversations = await harness.ConversationsAsync();
        Assert.Equal(idA, Assert.Single(conversations).Id);
        Assert.Equal("A committed 1", Assert.Single(await harness.MessagesAsync(idA)).Text);
        Assert.NotNull(await harness.ConversationCheckpointAsync(idA));

        // ...the in-flight conversation rolled back entirely...
        Assert.Null(await harness.FindConversationAsync(WeChatSyncHarness.DirectId("b")));
        Assert.Null(await harness.ConversationCheckpointAsync(idB));

        // ...and the run stopped instead of continuing to the next member.
        Assert.Null(await harness.FindConversationAsync(WeChatSyncHarness.DirectId("c")));
    }

    [Fact]
    public async Task InvalidMembershipEntriesAreReportedAsUnresolvedMembers()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A message" });

        var idA = harness.StableId(WeChatSyncHarness.DirectId("a"));
        harness.WriteCollection("project-x", idA, "wxid_not_a_stable_id");

        var result = await harness.Sync.SyncAsync(
            new CollectionSyncRequest { CollectionName = "project-x" }, null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(["wxid_not_a_stable_id"], result.InvalidConversationIds);

        var unresolved = Assert.Single(result.Items, i => i.ConversationId == "wxid_not_a_stable_id");
        Assert.Equal(CollectionSyncItemStatus.Unresolved, unresolved.Status);
        Assert.False(string.IsNullOrWhiteSpace(unresolved.Error));
        Assert.Equal(CollectionSyncItemStatus.Succeeded, Assert.Single(result.Items, i => i.ConversationId == idA).Status);

        // The resolvable member still advanced: an unusable declaration does not block the scope.
        Assert.NotNull(await harness.ConversationCheckpointAsync(idA));
    }

    [Fact]
    public async Task DuplicateMembershipIsReportedAndSynchronizedOnce()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A message" });

        var idA = harness.StableId(WeChatSyncHarness.DirectId("a"));
        harness.WriteCollection("project-x", idA, idA);

        var result = await harness.Sync.SyncAsync(
            new CollectionSyncRequest { CollectionName = "project-x" }, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal([idA], result.DuplicateConversationIds);
        var item = Assert.Single(result.Items);
        Assert.Equal(idA, item.ConversationId);
        Assert.Equal(CollectionSyncItemStatus.Succeeded, item.Status);
        Assert.Single(await harness.ConversationsAsync());
    }

    [Fact]
    public async Task AMemberThatNamesNoCapturedConversationIsReportedAsUnresolved()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A message" });

        // A well-formed stable id that belongs to another account resolves to nothing in this
        // account's preserved evidence.
        var foreign = StableIds.Conversation("a_0000000000000000", ConversationKind.Direct, "wxid_elsewhere", "wxid_elsewhere");
        var idA = harness.StableId(WeChatSyncHarness.DirectId("a"));
        harness.WriteCollection("project-x", idA, foreign);

        var result = await harness.Sync.SyncAsync(
            new CollectionSyncRequest { CollectionName = "project-x" }, null, CancellationToken.None);

        Assert.False(result.Succeeded);
        var unresolved = Assert.Single(result.Items, i => i.ConversationId == foreign);
        Assert.Equal(CollectionSyncItemStatus.Unresolved, unresolved.Status);
        Assert.Contains("was not found in Raw Vault account", unresolved.Error!, StringComparison.Ordinal);
        Assert.Equal(CollectionSyncItemStatus.Succeeded, Assert.Single(result.Items, i => i.ConversationId == idA).Status);
    }

    [Fact]
    public async Task AnUnknownCollectionNameIsADeterministicError()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp);
        harness.WriteCollection("project-x", harness.StableId(WeChatSyncHarness.DirectId("a")));

        var error = await Assert.ThrowsAsync<CollectionNotFoundException>(() =>
            harness.Sync.SyncAsync(new CollectionSyncRequest { CollectionName = "missing" }, null, CancellationToken.None));

        Assert.Equal("missing", error.Name);

        // Nothing was captured for a name that does not exist.
        Assert.Empty(await harness.Vault.ListGenerationsAsync(harness.AccountId, CancellationToken.None));
    }

    [Fact]
    public async Task AnEmptyCollectionSynchronizesWithoutCapturing()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp);
        harness.WriteCollection("empty");

        var result = await harness.Sync.SyncAsync(
            new CollectionSyncRequest { CollectionName = "empty" }, null, CancellationToken.None);

        // An empty scope is a valid, complete result; a live capture would acquire evidence no
        // caller asked for.
        Assert.True(result.Succeeded);
        Assert.Empty(result.Items);
        Assert.Null(result.GenerationId);
        Assert.Empty(await harness.Vault.ListGenerationsAsync(harness.AccountId, CancellationToken.None));
    }

    [Fact]
    public async Task AnUnavailableSourceFailsTheOperationRatherThanFabricatingPerMemberFailures()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A message" });
        harness.WriteCollection("project-x", harness.StableId(WeChatSyncHarness.DirectId("a")));
        harness.Source.IsAvailable = false;

        var error = await Assert.ThrowsAsync<CollectionCaptureException>(() =>
            harness.Sync.SyncAsync(new CollectionSyncRequest { CollectionName = "project-x" }, null, CancellationToken.None));

        Assert.Equal("project-x", error.CollectionName);
        Assert.Contains("not running", error.Reason, StringComparison.Ordinal);
        Assert.Empty(await harness.ConversationsAsync());
    }

    [Fact]
    public async Task ACaptureThatThrowsFailsTheOperationAndPublishesNothing()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A message" });
        harness.WriteCollection("project-x", harness.StableId(WeChatSyncHarness.DirectId("a")));
        harness.Capture.Failure = new IOException("the snapshot could not be read");

        var error = await Assert.ThrowsAsync<CollectionCaptureException>(() =>
            harness.Sync.SyncAsync(new CollectionSyncRequest { CollectionName = "project-x" }, null, CancellationToken.None));

        Assert.Contains("could not be read", error.Reason, StringComparison.Ordinal);
        Assert.Empty(await harness.Vault.ListGenerationsAsync(harness.AccountId, CancellationToken.None));
        Assert.Empty(await harness.ConversationsAsync());
    }

    // ---- Issue #96: the #63 historical-Partial regression, V2-only, Collection-scoped --------

    // docs/ROADMAP.md "Independent, bounded V2-only follow-up", Issue #96 scope 1: a live
    // Collection sync must follow the current complete evidence of an isolated V2 fixture vault in
    // which a historical Partial generation precedes the current complete generation, while direct
    // historical ingest keeps failing closed and the partial evidence stays byte-identical. This
    // is the Collection-scoped counterpart of the conversation-sync regression in
    // ConversationSyncTests, and adds no product change: the shipped generation-pinned ingest is
    // what makes ordinary sync follow current evidence.
    [Fact]
    public async Task CollectionSyncFollowsTheCurrentCompleteGenerationAndLeavesHistoricalPartialEvidenceUntouched()
    {
        using var temp = new TempDirectory();
        var conversationA = new SyntheticCaptureConversation
        {
            SourceConversationId = WeChatSyncHarness.DirectId("a"),
            Text = "A historical",
            MessageCount = 2,
        };
        using var harness = WeChatSyncHarness.Create(temp,
            conversationA,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("b"), Text = "B" });

        var idA = harness.StableId(WeChatSyncHarness.DirectId("a"));
        var idB = harness.StableId(WeChatSyncHarness.DirectId("b"));
        harness.WriteCollection("project-x", idA, idB);

        // A historical Partial generation in the #63 shape — an expected partition that could not
        // be read (a migration partition) downgraded the baseline capture — is published into the
        // same isolated V2 vault before the current complete evidence exists.
        const string partialReason = "Source partition 'migrate/unspportmsg.db' could not be read " +
            "during the historical baseline capture.";
        harness.Capture.ExtraCoverage.Add(new RawPartitionCoverage
        {
            PartitionId = "migrate/unspportmsg.db",
            Status = RawPartitionStatus.Unavailable,
            Diagnostic = partialReason,
        });
        harness.Capture.ExtraDiagnostics.Add(
            RawManifestDiagnostic.Partial(DiagnosticCodes.PartitionUnreadable, partialReason));
        harness.Capture.CompletenessOverride = RawGenerationCompleteness.Partial;
        var captureService = harness.Provider.GetRequiredService<CaptureService>();
        var historicalPartial = await captureService.CaptureAccountAsync(
            new CaptureRequest { SourceProfileId = WeChatSyncHarness.ProfileId }, null, CancellationToken.None);
        Assert.True(historicalPartial.Succeeded);

        // The fixture is V2-only (ADR 0012): the historical partial generation is manifest 3 over
        // vault format 2, and the vault retains it untouched from here on.
        var openedPartial = await harness.Vault.OpenGenerationAsync(
            harness.AccountId, historicalPartial.GenerationId, CancellationToken.None);
        Assert.NotNull(openedPartial);
        Assert.Equal(3, openedPartial!.Manifest.ManifestVersion);
        Assert.Equal(2, openedPartial.Manifest.VaultFormatVersion);
        Assert.Equal(RawGenerationCompleteness.Partial, openedPartial.Manifest.Capture.Completeness);
        var partialManifestPath = Path.Combine(openedPartial.GenerationDirectory, "manifest.json");
        var partialManifestHash = SHA256.HashData(await File.ReadAllBytesAsync(partialManifestPath));

        // Direct historical ingest of the partial evidence fails closed before any canonical
        // write: an explicit generation-pinned ingest opens the partial generation and refuses it
        // with the source-neutral coverage rollup (docs/PRD.md FR-20).
        var ingester = harness.Provider.GetRequiredService<RawVaultIngestService>();
        var pinnedError = await Assert.ThrowsAsync<IncompleteCanonicalCoverageException>(() =>
            ingester.IngestConversationFromGenerationAsync(
                harness.AccountId, idA, historicalPartial.GenerationId, null, CancellationToken.None));
        Assert.Equal(CanonicalCoverageVerdict.Incomplete, pinnedError.Coverage.Verdict);
        Assert.Equal(1, pinnedError.Coverage.Unavailable);
        Assert.Equal(0, pinnedError.Coverage.Unclassified);
        Assert.Empty(await harness.ConversationsAsync());
        Assert.Null(await harness.ConversationCheckpointAsync(idA));

        // The source moves on and the historical capture inputs are reset; the live sync then
        // publishes its own complete generation on top of the partial one.
        harness.Capture.ExtraCoverage.Clear();
        harness.Capture.ExtraDiagnostics.Clear();
        harness.Capture.CompletenessOverride = null;
        conversationA.Text = "A current";
        harness.AdvanceClock();

        var result = await harness.Sync.SyncAsync(
            new CollectionSyncRequest { CollectionName = "project-x" }, null, CancellationToken.None);

        // The live sync succeeds from its own complete generation; the historical partial
        // generation neither blocks the scope nor contributes to it.
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, item => Assert.Equal(CollectionSyncItemStatus.Succeeded, item.Status));
        Assert.NotEqual(historicalPartial.GenerationId, result.GenerationId);
        // A successful live sync always names the generation it published.
        Assert.False(string.IsNullOrEmpty(result.GenerationId));

        var published = await harness.Vault.OpenGenerationAsync(
            harness.AccountId, result.GenerationId!, CancellationToken.None);
        Assert.NotNull(published);
        Assert.Equal(RawGenerationCompleteness.Complete, published!.Manifest.Capture.Completeness);
        Assert.Equal(historicalPartial.GenerationId, published.Manifest.PreviousGenerationId);
        Assert.Equal(3, published.Manifest.ManifestVersion);
        Assert.Equal(2, published.Manifest.VaultFormatVersion);

        // Both members follow the current complete evidence: the content the ingest published is
        // the current source content, and each member's content checkpoint names the generation
        // the sync published — not the older partial one.
        Assert.Equal(new[] { "A current 1", "A current 2" }, (await harness.MessagesAsync(idA)).Select(m => m.Text));
        Assert.Equal(new[] { "B 1" }, (await harness.MessagesAsync(idB)).Select(m => m.Text));
        Assert.Equal(result.GenerationId, GenerationOf(await harness.ConversationCheckpointAsync(idA)));
        Assert.Equal(result.GenerationId, GenerationOf(await harness.ConversationCheckpointAsync(idB)));

        // Explicit historical traversal still fails closed after the live sync: the account-wide
        // ingest has no coverage of the partial chain root yet, opens it and refuses it before
        // publishing anything.
        var traversalError = await Assert.ThrowsAsync<IncompleteCanonicalCoverageException>(() =>
            ingester.IngestAsync(harness.AccountId, null, null, CancellationToken.None));
        Assert.Equal(CanonicalCoverageVerdict.Incomplete, traversalError.Coverage.Verdict);
        Assert.Equal(1, traversalError.Coverage.Unavailable);

        // The historical partial evidence is byte-identical after all of it: the sync neither
        // rewrote, reclassified nor deleted the partial generation to make the scope pass.
        Assert.Equal(partialManifestHash, SHA256.HashData(await File.ReadAllBytesAsync(partialManifestPath)));
    }

    private sealed class SyncProgress(Action<string> callback) : IProgress<string>
    {
        public void Report(string value) => callback(value);
    }

    /// <summary>Reads the generation cursor a checkpoint records, or null when the row is absent.</summary>
    private static string? GenerationOf(IngestCheckpoint? checkpoint)
    {
        if (checkpoint is null)
        {
            return null;
        }

        using var document = System.Text.Json.JsonDocument.Parse(checkpoint.CheckpointJson);
        var generationId = document.RootElement.GetProperty("generation_id").GetString();
        return string.IsNullOrWhiteSpace(generationId) ? null : generationId;
    }
}
