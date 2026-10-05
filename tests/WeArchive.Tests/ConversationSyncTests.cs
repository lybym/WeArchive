using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;
using WeArchive.Core.Services;
using WeArchive.Infrastructure;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// Integration tests for conversation-scoped preservation-first synchronization
/// (<c>sync --conversation</c>): capture -&gt; immutable Raw Vault generation -&gt;
/// conversation-scoped incremental ingest -&gt; canonical SQLite + ingest checkpoint.
/// docs/PRD.md G2/G4/FR-12/FR-14/FR-20/FR-29, docs/ARCHITECTURE.md sections 3.2/10,
/// docs/DEVELOPMENT.md sections 10.3.1/10.4, Issue #49 acceptance criteria.
/// <para>
/// Every case runs the production path through the shipped composition root — capture service,
/// Raw Vault store, real ingest implementation and the real checkpoints — over a synthetic WeChat
/// source, so the T1–T4 incrementality scenarios and the failure/cancellation semantics are proven
/// without a live client or a database key.
/// </para>
/// </summary>
public sealed class ConversationSyncTests
{
    // ---- T1: first sync establishes capture and ingest progress ---------------

    [Fact]
    public async Task FirstConversationSyncCapturesPublishesAndAdvancesOnlyThatConversation()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A", MessageCount = 3 },
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("b"), Text = "B" });

        var idA = harness.StableId(WeChatSyncHarness.DirectId("a"));
        var idB = harness.StableId(WeChatSyncHarness.DirectId("b"));

        var result = await SyncAsync(harness, WeChatSyncHarness.DirectId("a"));

        // Step 2-4: exactly one generation was captured and the selected conversation was ingested
        // from it.
        Assert.Equal(SyncPublicationStatus.Succeeded, result.Status);
        Assert.Equal(1, result.ConversationsIngested);
        Assert.Equal(RawCaptureMode.Baseline, result.CaptureMode);
        Assert.Null(result.PreviousGenerationId);
        Assert.Equal(harness.AccountId, result.AccountId);
        Assert.Equal(idA, result.ConversationId);
        var generation = Assert.Single(await harness.GenerationsAsync());
        Assert.Equal(result.GenerationId, generation.GenerationId);

        // Step 5: the selected conversation advanced its own checkpoint; the other conversation
        // was never touched by a conversation-scoped sync.
        Assert.Equal(3, (await harness.MessagesAsync(idA)).Count);
        Assert.NotNull(await harness.FindConversationAsync(WeChatSyncHarness.DirectId("a")));
        Assert.Null(await harness.FindConversationAsync(WeChatSyncHarness.DirectId("b")));
        Assert.Null(await harness.ConversationCheckpointAsync(idB));
        Assert.Equal(result.GenerationId, GenerationOf(await harness.ConversationCheckpointAsync(idA)));
    }

    // ---- T2: unchanged second sync ------------------------------------------

    [Fact]
    public async Task UnchangedSecondSyncReusesVerifiedEvidenceAndReportsNoChange()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A", MessageCount = 3 });

        var idA = harness.StableId(WeChatSyncHarness.DirectId("a"));

        var first = await SyncAsync(harness, WeChatSyncHarness.DirectId("a"));
        Assert.Equal(SyncPublicationStatus.Succeeded, first.Status);
        var committed = await harness.MessagesAsync(idA);
        var contentCheckpointAfterFirst = await harness.ConversationCheckpointAsync(idA);

        // Nothing about the live source changed.
        harness.AdvanceClock();
        var second = await SyncAsync(harness, WeChatSyncHarness.DirectId("a"));

        // Deterministic no_change: the conversation was verified against newer evidence and
        // nothing was republished.
        Assert.Equal(SyncPublicationStatus.NoChange, second.Status);
        Assert.Equal(0, second.ConversationsIngested);
        Assert.Equal(first.GenerationId, second.PreviousGenerationId);
        Assert.NotEqual(first.GenerationId, second.GenerationId);

        // The capture itself was incremental: every unchanged source partition was reused from the
        // verified predecessor instead of being reacquired (Issue #25).
        Assert.Equal(RawCaptureMode.Incremental, second.CaptureMode);
        var reopened = await harness.Vault.OpenGenerationAsync(harness.AccountId, second.GenerationId, CancellationToken.None);
        Assert.NotNull(reopened);
        Assert.All(reopened!.Manifest.Coverage, entry => Assert.Equal(RawPartitionStatus.Reused, entry.Status));

        // The unchanged conversation keeps its content checkpoint; only the transactional coverage
        // cursor moves to the newer generation.
        Assert.Equal(contentCheckpointAfterFirst!.CheckpointJson, (await harness.ConversationCheckpointAsync(idA))!.CheckpointJson);
        Assert.Equal(second.GenerationId, GenerationOf(await harness.ConversationCoverageCheckpointAsync(idA)));

        // No duplicate canonical records: the identical evidence republished nothing.
        var afterSecond = await harness.MessagesAsync(idA);
        Assert.Equal(committed.Select(m => m.Id), afterSecond.Select(m => m.Id));
    }

    // ---- T3: new evidence for the selected conversation ---------------------

    [Fact]
    public async Task NewEvidenceIsIngestedIncrementallyAndAdvancesTheSelectedCheckpoint()
    {
        using var temp = new TempDirectory();
        var conversation = new SyntheticCaptureConversation
        {
            SourceConversationId = WeChatSyncHarness.DirectId("a"),
            Text = "A",
            MessageCount = 1,
        };
        using var harness = WeChatSyncHarness.Create(temp, conversation);

        var idA = harness.StableId(WeChatSyncHarness.DirectId("a"));
        var first = await SyncAsync(harness, WeChatSyncHarness.DirectId("a"));
        Assert.Single(await harness.MessagesAsync(idA));

        // Conversation A gains one message; the other source partitions are unchanged.
        conversation.MessageCount = 2;
        harness.AdvanceClock();

        var second = await SyncAsync(harness, WeChatSyncHarness.DirectId("a"));

        Assert.Equal(SyncPublicationStatus.Succeeded, second.Status);
        Assert.Equal(1, second.ConversationsIngested);
        Assert.Equal(RawCaptureMode.Incremental, second.CaptureMode);
        Assert.Equal(first.GenerationId, second.PreviousGenerationId);
        Assert.Equal(second.GenerationId, GenerationOf(await harness.ConversationCheckpointAsync(idA)));
        Assert.NotEqual(first.GenerationId, GenerationOf(await harness.ConversationCheckpointAsync(idA)));

        // Only the changed shard was reacquired; unchanged partitions were reused.
        var reopened = await harness.Vault.OpenGenerationAsync(harness.AccountId, second.GenerationId, CancellationToken.None);
        Assert.NotNull(reopened);
        Assert.Contains(reopened!.Manifest.Coverage, entry => entry.Status == RawPartitionStatus.Captured);
        Assert.Contains(reopened.Manifest.Coverage, entry => entry.Status == RawPartitionStatus.Reused);

        // The new record was canonically added exactly once.
        var messages = await harness.MessagesAsync(idA);
        Assert.Equal(2, messages.Count);
        Assert.Equal("A 1", messages[0].Text);
        Assert.Equal("A 2", messages[1].Text);
    }

    // ---- T4: another conversation changed -----------------------------------

    [Fact]
    public async Task AChangeInAnotherConversationLeavesTheSelectedConversationUnchanged()
    {
        using var temp = new TempDirectory();
        var selected = new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A" };
        var other = new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("b"), Text = "B" };
        using var harness = WeChatSyncHarness.Create(temp, selected, other);

        var idA = harness.StableId(WeChatSyncHarness.DirectId("a"));
        var first = await SyncAsync(harness, WeChatSyncHarness.DirectId("a"));
        var checkpointAfterFirst = await harness.ConversationCheckpointAsync(idA);
        var committed = await harness.MessagesAsync(idA);

        other.Text = "B changed";
        harness.AdvanceClock();

        var second = await SyncAsync(harness, WeChatSyncHarness.DirectId("a"));

        Assert.Equal(SyncPublicationStatus.NoChange, second.Status);
        Assert.Equal(0, second.ConversationsIngested);
        Assert.Equal(second.GenerationId, GenerationOf(await harness.ConversationCoverageCheckpointAsync(idA)));

        // A keeps its content checkpoint and its canonical rows; B was never part of this scope.
        Assert.Equal(checkpointAfterFirst!.CheckpointJson, (await harness.ConversationCheckpointAsync(idA))!.CheckpointJson);
        Assert.Equal(committed.Select(m => m.Id), (await harness.MessagesAsync(idA)).Select(m => m.Id));
        Assert.Null(await harness.FindConversationAsync(WeChatSyncHarness.DirectId("b")));
        Assert.Equal(2, (await harness.GenerationsAsync()).Count);
    }

    [Fact]
    public async Task LiveSyncUsesItsCompleteGenerationWithoutRewritingOrDependingOnHistoricalPartialEvidence()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation
            {
                SourceConversationId = WeChatSyncHarness.DirectId("a"),
                Text = "current evidence",
                MessageCount = 2,
            });
        var idA = harness.StableId(WeChatSyncHarness.DirectId("a"));
        var captureService = harness.Provider.GetRequiredService<CaptureService>();

        harness.Capture.CompletenessOverride = RawGenerationCompleteness.Partial;
        var historicalPartial = await captureService.CaptureAccountAsync(
            new CaptureRequest { SourceProfileId = WeChatSyncHarness.ProfileId }, null, CancellationToken.None);
        Assert.True(historicalPartial.Succeeded);
        var openedHistoricalPartial = await harness.Vault.OpenGenerationAsync(
            harness.AccountId, historicalPartial.GenerationId, CancellationToken.None);
        Assert.NotNull(openedHistoricalPartial);
        var historicalPath = Path.Combine(openedHistoricalPartial!.GenerationDirectory, "manifest.json");
        var historicalManifestHash = SHA256.HashData(await File.ReadAllBytesAsync(historicalPath));

        harness.Capture.CompletenessOverride = null;
        harness.AdvanceClock();
        var completeEvidence = await captureService.CaptureAccountAsync(
            new CaptureRequest { SourceProfileId = WeChatSyncHarness.ProfileId }, null, CancellationToken.None);
        Assert.True(completeEvidence.Succeeded);
        Assert.Equal(RawGenerationCompleteness.Complete, completeEvidence.Completeness);

        // The historical, explicit ingest/replay contracts still visit history and fail closed
        // before publishing any canonical rows or checkpoint when the oldest generation is partial.
        var ingester = harness.Provider.GetRequiredService<RawVaultIngestService>();
        await Assert.ThrowsAsync<IncompleteCanonicalCoverageException>(() => ingester.IngestAsync(
            harness.AccountId, WeChatSyncHarness.DirectId("a"), null, CancellationToken.None));
        Assert.Empty(await harness.ConversationsAsync());
        Assert.Null(await harness.ConversationCheckpointAsync(idA));
        await Assert.ThrowsAsync<IncompleteCanonicalCoverageException>(() => ingester.IngestAsync(
            harness.AccountId, WeChatSyncHarness.DirectId("a"), null, CancellationToken.None, replay: true));
        Assert.Empty(await harness.ConversationsAsync());
        Assert.Null(await harness.ConversationCheckpointAsync(idA));

        // A live sync publishes another complete, self-contained snapshot and consumes only that
        // exact generation; unrelated partial history cannot block the first canonical result.
        harness.AdvanceClock();
        var result = await SyncAsync(harness, WeChatSyncHarness.DirectId("a"));
        Assert.Equal(SyncPublicationStatus.Succeeded, result.Status);
        Assert.Equal(CanonicalCoverageVerdict.Complete, result.Coverage.Verdict);
        Assert.Equal(0, result.Coverage.Unavailable);
        Assert.Equal(0, result.Coverage.Unclassified);
        Assert.Equal(result.GenerationId, GenerationOf(await harness.ConversationCheckpointAsync(idA)));
        Assert.Equal(new[] { "current evidence 1", "current evidence 2" },
            (await harness.MessagesAsync(idA)).Select(message => message.Text));
        Assert.Equal(historicalManifestHash,
            SHA256.HashData(await File.ReadAllBytesAsync(historicalPath)));

        // Rebuild uses the latest complete generation and leaves historical partial evidence alone.
        var rebuilt = await new RebuildService(harness.Vault, harness.ArchivePath, harness.Clock)
            .RebuildAsync(null, CancellationToken.None);
        Assert.Equal(2, rebuilt.Stats.MessageCount);
        Assert.Equal(historicalManifestHash,
            SHA256.HashData(await File.ReadAllBytesAsync(historicalPath)));
    }

    // ---- failure semantics --------------------------------------------------

    [Fact]
    public async Task FatalIngestFailureAfterPublishedCaptureKeepsCanonicalStateAndCheckpointUnchanged()
    {
        using var temp = new TempDirectory();
        var conversation = new SyntheticCaptureConversation
        {
            SourceConversationId = WeChatSyncHarness.DirectId("a"),
            Text = "A",
            MessageCount = 2,
        };
        using var harness = WeChatSyncHarness.Create(temp, conversation);

        var idA = harness.StableId(WeChatSyncHarness.DirectId("a"));
        await SyncAsync(harness, WeChatSyncHarness.DirectId("a"));
        var checkpointAfterFirst = await harness.ConversationCheckpointAsync(idA);
        Assert.NotNull(checkpointAfterFirst);

        // The conversation's message shard becomes unreadable while its session row survives: a
        // Fatal source-coverage condition for the in-flight publication.
        conversation.MessageShardUnreadable = true;
        harness.AdvanceClock();

        await Assert.ThrowsAnyAsync<InvalidDataException>(
            () => SyncAsync(harness, WeChatSyncHarness.DirectId("a")));

        // R2: the in-flight conversation neither published nor advanced its checkpoint...
        Assert.Equal(checkpointAfterFirst!.CheckpointJson, (await harness.ConversationCheckpointAsync(idA))!.CheckpointJson);
        Assert.Equal(2, (await harness.MessagesAsync(idA)).Count);

        // ...while the successfully published Raw Vault generation is retained, not rolled back.
        var generations = await harness.GenerationsAsync();
        Assert.Equal(2, generations.Count);
        var retained = await harness.Vault.OpenGenerationAsync(
            generations[^1].AccountId, generations[^1].GenerationId, default);
        Assert.NotNull(retained);
        Assert.Equal(3, retained.Manifest.ManifestVersion);
        Assert.Equal(2, retained.Manifest.VaultFormatVersion);
    }

    [Fact]
    public async Task RetryAfterAFatalIngestFailureStaysFailClosedAndIdempotent()
    {
        using var temp = new TempDirectory();
        var conversation = new SyntheticCaptureConversation
        {
            SourceConversationId = WeChatSyncHarness.DirectId("a"),
            Text = "A",
            MessageCount = 2,
        };
        using var harness = WeChatSyncHarness.Create(temp, conversation);

        var idA = harness.StableId(WeChatSyncHarness.DirectId("a"));
        await SyncAsync(harness, WeChatSyncHarness.DirectId("a"));
        var checkpointAfterFirst = await harness.ConversationCheckpointAsync(idA);
        var committed = await harness.MessagesAsync(idA);

        conversation.MessageShardUnreadable = true;
        harness.AdvanceClock();
        await Assert.ThrowsAnyAsync<InvalidDataException>(
            () => SyncAsync(harness, WeChatSyncHarness.DirectId("a")));

        // The published generation whose required message evidence cannot be read is still
        // uncovered for this conversation, so a retry fails closed rather than skipping evidence
        // it cannot prove (docs/DEVELOPMENT.md section 10.4, the documented Fatal hard rule).
        // Idempotency is what matters: nothing is duplicated, and no bogus progress is recorded.
        harness.AdvanceClock();
        await Assert.ThrowsAnyAsync<InvalidDataException>(
            () => SyncAsync(harness, WeChatSyncHarness.DirectId("a")));

        Assert.Equal(checkpointAfterFirst!.CheckpointJson, (await harness.ConversationCheckpointAsync(idA))!.CheckpointJson);
        Assert.Equal(committed.Select(m => m.Id), (await harness.MessagesAsync(idA)).Select(m => m.Id));
    }

    [Fact]
    public async Task CancellationDuringIngestRollsBackTheInFlightConversationAndRetainsTheGeneration()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation
            {
                SourceConversationId = WeChatSyncHarness.DirectId("a"),
                Text = "A",
                MessageCount = 300,
            });

        var idA = harness.StableId(WeChatSyncHarness.DirectId("a"));

        using (var cancellation = new CancellationTokenSource())
        {
            var progress = new SyncProgress(message =>
            {
                if (message.StartsWith($"Ingesting {idA} ", StringComparison.Ordinal))
                {
                    cancellation.Cancel();
                }
            });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => SyncAsync(harness, WeChatSyncHarness.DirectId("a"), progress, cancellation.Token));
        }

        // The capture published before the ingest phase started, and that generation is retained.
        var generation = Assert.Single(await harness.GenerationsAsync());

        // The in-flight conversation and its checkpoint were rolled back together.
        Assert.Null(await harness.FindConversationAsync(WeChatSyncHarness.DirectId("a")));
        Assert.Null(await harness.ConversationCheckpointAsync(idA));
        Assert.Empty(await harness.MessagesAsync(idA));

        // A later retry completes the conversation from the same preserved evidence.
        harness.AdvanceClock();
        var retry = await SyncAsync(harness, WeChatSyncHarness.DirectId("a"));
        Assert.Equal(SyncPublicationStatus.Succeeded, retry.Status);
        Assert.Equal(300, (await harness.MessagesAsync(idA)).Count);
        Assert.NotNull(await harness.ConversationCheckpointAsync(idA));
        Assert.Equal(2, (await harness.GenerationsAsync()).Count);
    }

    [Fact]
    public async Task APreCancelledRunPublishesNothing()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A" });

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SyncAsync(harness, WeChatSyncHarness.DirectId("a"), progress: null, cancellation.Token));

        Assert.Empty(await harness.GenerationsAsync());
        Assert.Empty(await harness.ConversationsAsync());
    }

    [Fact]
    public async Task CaptureFailurePublishesNothingAndFailsTheOperation()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A" });
        harness.Source.IsAvailable = false;

        var error = await Assert.ThrowsAsync<SyncCaptureException>(
            () => SyncAsync(harness, WeChatSyncHarness.DirectId("a")));

        Assert.Contains("not running", error.Reason, StringComparison.Ordinal);
        Assert.Empty(await harness.GenerationsAsync());
        Assert.Empty(await harness.ConversationsAsync());
    }

    [Fact]
    public async Task AConversationAbsentFromTheCapturedEvidenceIsReportedAsNotInRawVault()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A" });

        // A well-formed stable id that belongs to another account resolves to nothing in this
        // account's captured evidence.
        var foreign = StableIds.Conversation(
            "a_0000000000000000", ConversationKind.Direct, "wxid_elsewhere", "wxid_elsewhere");

        var error = await Assert.ThrowsAsync<ConversationNotInRawVaultException>(() =>
            harness.ConversationSync.SyncAsync(
                new ConversationSyncRequest
                {
                    SourceProfileId = WeChatSyncHarness.ProfileId,
                    ConversationId = foreign,
                },
                null,
                CancellationToken.None));

        Assert.Equal(foreign, error.ConversationSelector);

        // The capture still published; only the canonical publication was skipped.
        Assert.Single(await harness.GenerationsAsync());
        Assert.Empty(await harness.ConversationsAsync());
    }

    // ---- canonical coverage (Issue #51) --------------------------------------

    // The Issue #51 acceptance cases, over synthetic Raw Vault generations: the canonical
    // coverage rollup distinguishes complete from incomplete reads, keeps known-unsupported
    // evidence explicit, stays conservative for unclassified evidence, and never confuses the
    // ingest-progress conversation_coverage cursor with the completeness model.

    [Fact]
    public async Task CaseAKnownUnsupportedEvidenceIsExplicitButComplete()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A", MessageCount = 2 });

        const string reason = "Source partition 'migrate/unspportmsg.db' is outside this adapter " +
            "version's supported evidence contract; it is recorded as unsupported and is not " +
            "required for a complete capture.";
        harness.Capture.ExtraCoverage.Add(new RawPartitionCoverage
        {
            PartitionId = "migrate/unspportmsg.db",
            Status = RawPartitionStatus.Unsupported,
            Diagnostic = reason,
        });
        harness.Capture.ExtraDiagnostics.Add(
            RawManifestDiagnostic.Info(DiagnosticCodes.PartitionUnsupported, reason));

        var result = await SyncAsync(harness, WeChatSyncHarness.DirectId("a"));

        Assert.Equal(SyncPublicationStatus.Succeeded, result.Status);
        Assert.Equal(CanonicalCoverageVerdict.Complete, result.Coverage.Verdict);
        Assert.Equal(4, result.Coverage.Expected);
        Assert.Equal(3, result.Coverage.Available);
        Assert.Equal(0, result.Coverage.Unavailable);
        Assert.Equal(1, result.Coverage.KnownUnsupported);
        Assert.Equal(0, result.Coverage.Unclassified);

        // Deterministic: the rollup the sync reported is exactly the rollup of the persisted
        // manifest for the same generation and reader policy.
        var reopened = await harness.Vault
            .OpenGenerationAsync(harness.AccountId, result.GenerationId, CancellationToken.None);
        Assert.NotNull(reopened);
        Assert.Equal(CanonicalCoverage.From(reopened!.Manifest), result.Coverage);
    }

    [Fact]
    public async Task CaseBUnavailableRequiredEvidenceFailsClosedWithIncompleteCoverage()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A" });

        const string reason = "The message shard could not be read.";
        harness.Capture.ExtraCoverage.Add(new RawPartitionCoverage
        {
            PartitionId = "message/message_1.db",
            Status = RawPartitionStatus.Unavailable,
            Diagnostic = reason,
        });
        harness.Capture.ExtraDiagnostics.Add(
            RawManifestDiagnostic.Partial(DiagnosticCodes.PartitionUnreadable, reason));
        harness.Capture.CompletenessOverride = RawGenerationCompleteness.Partial;

        var error = await Assert.ThrowsAsync<IncompleteCanonicalCoverageException>(
            () => SyncAsync(harness, WeChatSyncHarness.DirectId("a")));

        // The partial generation was published by capture (R1) but refused by the ingest (R2):
        // the refusal carries the incomplete coverage and never a complete verdict.
        Assert.Equal(CanonicalCoverageVerdict.Incomplete, error.Coverage.Verdict);
        Assert.Equal(4, error.Coverage.Expected);
        Assert.Equal(3, error.Coverage.Available);
        Assert.Equal(1, error.Coverage.Unavailable);
        Assert.Equal(0, error.Coverage.KnownUnsupported);

        // Nothing was published canonically; the published generation itself is retained.
        Assert.Empty(await harness.ConversationsAsync());
        Assert.Single(await harness.GenerationsAsync());

        // A retry stays fail-closed while the evidence is incomplete.
        harness.AdvanceClock();
        await Assert.ThrowsAsync<IncompleteCanonicalCoverageException>(
            () => SyncAsync(harness, WeChatSyncHarness.DirectId("a")));
        Assert.Empty(await harness.ConversationsAsync());
    }

    [Fact]
    public async Task CaseCUnclassifiedEvidenceFailsConservativelyAsIncomplete()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A" });

        const string reason = "Source partition 'general/newdb.db' has no approved support " +
            "classification for this adapter version; the generation cannot be complete until " +
            "that partition is classified.";
        harness.Capture.ExtraCoverage.Add(new RawPartitionCoverage
        {
            PartitionId = "general/newdb.db",
            Status = RawPartitionStatus.Unsupported,
            Diagnostic = reason,
        });
        harness.Capture.ExtraDiagnostics.Add(
            RawManifestDiagnostic.Partial(DiagnosticCodes.PartitionUnclassified, reason));
        harness.Capture.CompletenessOverride = RawGenerationCompleteness.Partial;

        var error = await Assert.ThrowsAsync<IncompleteCanonicalCoverageException>(
            () => SyncAsync(harness, WeChatSyncHarness.DirectId("a")));

        Assert.Equal(CanonicalCoverageVerdict.Incomplete, error.Coverage.Verdict);
        Assert.Equal(1, error.Coverage.Unclassified);
        Assert.Equal(0, error.Coverage.KnownUnsupported);
        Assert.Equal(0, error.Coverage.Unavailable);

        // Unknown/unclassified evidence never silently produced a complete canonical result.
        Assert.Empty(await harness.ConversationsAsync());
    }

    [Fact]
    public async Task CaseDUnchangedSecondSyncReportsCompleteCoverageWithoutConfusingTheCursor()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A" });

        const string reason = "Source partition 'migrate/unspportmsg.db' is outside this adapter " +
            "version's supported evidence contract; it is recorded as unsupported and is not " +
            "required for a complete capture.";
        harness.Capture.ExtraCoverage.Add(new RawPartitionCoverage
        {
            PartitionId = "migrate/unspportmsg.db",
            Status = RawPartitionStatus.Unsupported,
            Diagnostic = reason,
        });
        harness.Capture.ExtraDiagnostics.Add(
            RawManifestDiagnostic.Info(DiagnosticCodes.PartitionUnsupported, reason));

        var idA = harness.StableId(WeChatSyncHarness.DirectId("a"));
        var first = await SyncAsync(harness, WeChatSyncHarness.DirectId("a"));
        Assert.Equal(SyncPublicationStatus.Succeeded, first.Status);

        harness.AdvanceClock();
        var second = await SyncAsync(harness, WeChatSyncHarness.DirectId("a"));

        // No_change still reports the canonical completeness derived from the verified evidence
        // of the newer generation — the conversation_coverage cursor is ingest progress, not a
        // completeness statement (docs/DATA_MODEL.md section 14.1, Issue #51).
        Assert.Equal(SyncPublicationStatus.NoChange, second.Status);
        Assert.Equal(CanonicalCoverageVerdict.Complete, second.Coverage.Verdict);
        Assert.Equal(4, second.Coverage.Expected);
        Assert.Equal(3, second.Coverage.Available);
        Assert.Equal(1, second.Coverage.KnownUnsupported);

        // Progress and completeness stay distinct concepts: the content checkpoint kept its
        // generation while the transactional coverage cursor moved to the newer one.
        Assert.Equal(first.GenerationId, GenerationOf(await harness.ConversationCheckpointAsync(idA)));
        Assert.Equal(second.GenerationId, GenerationOf(await harness.ConversationCoverageCheckpointAsync(idA)));
    }

    // ---- no R3+ recovery machinery ------------------------------------------

    [Fact]
    public async Task ConversationSyncAddsNoPersistentRecoveryState()
    {
        // The hard-stop rule forbids journals, commit markers, recovery ledgers or cross-file
        // transaction protocols beyond SQLite's own documented boundary. After a first sync, an
        // unchanged second sync and an incremental third sync, only SQLite's own files and the
        // documented Raw Vault generation layout may exist.
        using var temp = new TempDirectory();
        var conversation = new SyntheticCaptureConversation
        {
            SourceConversationId = WeChatSyncHarness.DirectId("a"),
            Text = "A",
        };
        using var harness = WeChatSyncHarness.Create(temp, conversation);

        await SyncAsync(harness, WeChatSyncHarness.DirectId("a"));
        harness.AdvanceClock();
        await SyncAsync(harness, WeChatSyncHarness.DirectId("a"));

        // The archive directory contains only SQLite's own files.
        var archiveDir = Path.GetDirectoryName(harness.ArchivePath)!;
        foreach (var file in Directory.EnumerateFiles(archiveDir))
        {
            var name = Path.GetFileName(file);
            Assert.True(
                name.EndsWith(".db", StringComparison.Ordinal)
                || name.EndsWith(".db-wal", StringComparison.Ordinal)
                || name.EndsWith(".db-shm", StringComparison.Ordinal),
                $"unexpected archive file '{name}' — no application recovery state is permitted");
        }

        // The Raw Vault carries only published generations (manifest.json plus artifacts) and the
        // documented generation chain record; a sync leaves no journal/commit marker behind.
        foreach (var entry in Directory.EnumerateFileSystemEntries(
            temp.Combine("rawvault"), "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(entry);
            Assert.False(
                name.Contains("journal", StringComparison.OrdinalIgnoreCase)
                || name.Contains("commit-marker", StringComparison.OrdinalIgnoreCase)
                || name.Contains("recovery", StringComparison.OrdinalIgnoreCase)
                || name.Contains("ledger", StringComparison.OrdinalIgnoreCase)
                || name.Contains("transaction", StringComparison.OrdinalIgnoreCase)
                || name.Contains("staging", StringComparison.OrdinalIgnoreCase),
                $"unexpected Raw Vault state '{name}' — no application recovery state is permitted");
        }
    }

    // ---- helpers ------------------------------------------------------------

    private static Task<ConversationSyncResult> SyncAsync(
        WeChatSyncHarness harness,
        string sourceConversationId,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        harness.ConversationSync.SyncAsync(
            new ConversationSyncRequest
            {
                // Mirrors the CLI: the profile the selector resolved against is pinned, and the
                // canonical stable conversation id is what gets ingested.
                SourceProfileId = WeChatSyncHarness.ProfileId,
                ConversationId = harness.StableId(sourceConversationId),
            },
            progress,
            cancellationToken);

    /// <summary>Reads the generation cursor a checkpoint records, or null when the row is absent.</summary>
    private static string? GenerationOf(IngestCheckpoint? checkpoint)
    {
        if (checkpoint is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(checkpoint.CheckpointJson);
        var generationId = document.RootElement.GetProperty("generation_id").GetString();
        return string.IsNullOrWhiteSpace(generationId) ? null : generationId;
    }

    private sealed class SyncProgress(Action<string> callback) : IProgress<string>
    {
        public void Report(string value) => callback(value);
    }
}
