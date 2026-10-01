using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.Services;
using WeArchive.Infrastructure.Archive;
using WeArchive.Infrastructure.Export;
using WeArchive.Infrastructure.Fixtures;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// Verifies that a source adapter which cannot provide complete coverage for a
/// conversation (a missing or unreadable message shard) surfaces a typed Fatal
/// diagnostic and a Failed run instead of a silent empty import that exports an
/// apparently valid empty dataset. docs/PRD.md FR-14, docs/ARCHITECTURE.md section 11.
/// </summary>
public sealed class SourceCoverageTests
{
    /// <summary>
    /// An otherwise valid adapter that emits one message without an upstream or composite
    /// source id. Such a record cannot be represented safely in the canonical model.
    /// </summary>
    private sealed class MissingMessageIdAdapter : ISourceAdapter
    {
        private readonly FixtureSourceAdapter _fixture = new();

        public string AdapterName => _fixture.AdapterName;
        public string AdapterVersion => _fixture.AdapterVersion;

        public Task<SourceDescriptor> DescribeSourceAsync(CancellationToken cancellationToken) =>
            _fixture.DescribeSourceAsync(cancellationToken);

        public Task<IReadOnlyList<SourceAccount>> ListAccountsAsync(CancellationToken cancellationToken) =>
            _fixture.ListAccountsAsync(cancellationToken);

        public Task<IReadOnlyList<SourceConversation>> ListConversationsAsync(
            string sourceProfileId, CancellationToken cancellationToken) =>
            _fixture.ListConversationsAsync(sourceProfileId, cancellationToken);

        public Task<SourceConversationDetail> DescribeConversationAsync(
            string sourceProfileId, string sourceConversationId, CancellationToken cancellationToken) =>
            _fixture.DescribeConversationAsync(sourceProfileId, sourceConversationId, cancellationToken);

        public Task<IReadOnlyList<SourceParticipant>> ListParticipantsAsync(
            string sourceProfileId, CancellationToken cancellationToken) =>
            _fixture.ListParticipantsAsync(sourceProfileId, cancellationToken);

        public async IAsyncEnumerable<SourceMessage> ReadMessagesAsync(
            string sourceProfileId,
            string sourceConversationId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new SourceMessage
            {
                SourceConversationId = sourceConversationId,
                SenderSourceUserId = FixtureSourceAdapter.Alice,
                OccurredAt = FixtureSourceAdapter.Base,
                SourcePartition = "fixture_0",
                SourceMessageId = "l:fixture_0:1",
                SourceOrderKey = "1",
                Content = SourceMessageContent.PlainText("valid record"),
            };

            yield return new SourceMessage
            {
                SourceConversationId = sourceConversationId,
                SenderSourceUserId = FixtureSourceAdapter.Bob,
                OccurredAt = FixtureSourceAdapter.Base.AddMinutes(1),
                SourcePartition = "fixture_0",
                SourceOrderKey = "2",
                Content = SourceMessageContent.PlainText("record without identity"),
            };

            await Task.Yield();
        }
    }

    /// <summary>
    /// An adapter whose message shard is unavailable. Its <see cref="ReadMessagesAsync"/>
    /// throws <see cref="SourceCoverageException"/> instead of yielding an empty stream,
    /// matching how the WeChat adapter now behaves when a conversation's shard is missing
    /// or unreadable.
    /// </summary>
    private sealed class UnreadableShardAdapter : ISourceAdapter
    {
        public const string ProfileId = "stub_account";
        public const string ConversationId = "stub_conv";

        public string AdapterName => "stub";
        public string AdapterVersion => "1.0.0";

        public Task<SourceDescriptor> DescribeSourceAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SourceDescriptor
            {
                AdapterName = AdapterName,
                AdapterVersion = AdapterVersion,
                SourceVersion = "stub-1",
                SourceProductName = "stub source",
                IsAvailable = true,
            });

        public Task<IReadOnlyList<SourceAccount>> ListAccountsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SourceAccount>>(
            [
                new SourceAccount
                {
                    SourceProfileId = ProfileId,
                    DisplayName = "stub account",
                    IsCurrent = true,
                    LastActiveAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                },
            ]);

        public Task<IReadOnlyList<SourceConversation>> ListConversationsAsync(
            string sourceProfileId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SourceConversation>>(
            [
                new SourceConversation
                {
                    SourceConversationId = ConversationId,
                    Kind = ConversationKind.Group,
                    Title = "stub",
                },
            ]);

        public Task<SourceConversationDetail> DescribeConversationAsync(
            string sourceProfileId, string sourceConversationId, CancellationToken cancellationToken) =>
            Task.FromResult(new SourceConversationDetail
            {
                SourceConversationId = sourceConversationId,
                MessageCount = 5,
                ParticipantCount = 1,
            });

        public Task<IReadOnlyList<SourceParticipant>> ListParticipantsAsync(
            string sourceProfileId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SourceParticipant>>(
            [
                new SourceParticipant { SourceUserId = ProfileId, Nickname = "stub" },
            ]);

        // The whole point of this stub: the shard is unavailable, so reading throws a
        // typed coverage exception instead of yielding an empty stream.
        public IAsyncEnumerable<SourceMessage> ReadMessagesAsync(
            string sourceProfileId, string sourceConversationId, CancellationToken cancellationToken)
        {
            throw new SourceCoverageException(
                DiagnosticCodes.PartitionUnreadable,
                $"stub shard unreadable for conversation '{sourceConversationId}'");
        }
    }

    /// <summary>How a long synthetic source stream ends.</summary>
    private enum StreamFault
    {
        /// <summary>The stream ends normally after its valid records.</summary>
        None,

        /// <summary>A final record with no upstream and no composite identity.</summary>
        MissingIdentity,

        /// <summary>A typed source-coverage failure after records were already read.</summary>
        CoverageException,

        /// <summary>An unexpected adapter failure after records were already read.</summary>
        UnexpectedException,
    }

    /// <summary>
    /// Emits a stream long enough to cross <c>ImportService</c>'s 2,048-record batch boundary
    /// before it faults, which is the state the shorter stubs above cannot reach. The records
    /// reuse the fixture's composite identity scheme, so the same ids can be re-imported with
    /// different text. All content is synthetic.
    /// </summary>
    private sealed class LongStreamAdapter(int validRecords, StreamFault fault, string textPrefix)
        : ISourceAdapter
    {
        private readonly FixtureSourceAdapter _fixture = new();

        public string AdapterName => _fixture.AdapterName;

        public string AdapterVersion => _fixture.AdapterVersion;

        public Task<SourceDescriptor> DescribeSourceAsync(CancellationToken cancellationToken) =>
            _fixture.DescribeSourceAsync(cancellationToken);

        public Task<IReadOnlyList<SourceAccount>> ListAccountsAsync(CancellationToken cancellationToken) =>
            _fixture.ListAccountsAsync(cancellationToken);

        public Task<IReadOnlyList<SourceConversation>> ListConversationsAsync(
            string sourceProfileId, CancellationToken cancellationToken) =>
            _fixture.ListConversationsAsync(sourceProfileId, cancellationToken);

        public Task<SourceConversationDetail> DescribeConversationAsync(
            string sourceProfileId, string sourceConversationId, CancellationToken cancellationToken) =>
            _fixture.DescribeConversationAsync(sourceProfileId, sourceConversationId, cancellationToken);

        public Task<IReadOnlyList<SourceParticipant>> ListParticipantsAsync(
            string sourceProfileId, CancellationToken cancellationToken) =>
            _fixture.ListParticipantsAsync(sourceProfileId, cancellationToken);

        public async IAsyncEnumerable<SourceMessage> ReadMessagesAsync(
            string sourceProfileId,
            string sourceConversationId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var index = 1; index <= validRecords; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Record(index);
                await Task.Yield();
            }

            switch (fault)
            {
                case StreamFault.MissingIdentity:
                    yield return Record(validRecords + 1) with { SourceMessageId = null };
                    break;
                case StreamFault.CoverageException:
                    throw new SourceCoverageException(
                        DiagnosticCodes.PartitionUnreadable,
                        "The message shard became unreadable after earlier records were read.");
                case StreamFault.UnexpectedException:
                    throw new InvalidOperationException("The message shard failed mid-read.");
                case StreamFault.None:
                default:
                    break;
            }
        }

        private SourceMessage Record(int index) => new()
        {
            SourceConversationId = FixtureSourceAdapter.GroupConversation,
            SenderSourceUserId = index % 2 == 0 ? FixtureSourceAdapter.Bob : FixtureSourceAdapter.Alice,
            OccurredAt = FixtureSourceAdapter.Base.AddSeconds(index),
            SourceType = "1",
            SourcePartition = "fixture_0",
            SourceMessageId = $"l:fixture_0:{index}",
            SourceOrderKey = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Content = SourceMessageContent.PlainText($"{textPrefix} {index}"),
        };
    }

    /// <summary>Cancels the import as soon as the first batch has been written.</summary>
    private sealed class CancelOnArchiving(CancellationTokenSource cts) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value)
        {
            if (value.Stage == OperationStages.Archiving)
            {
                cts.Cancel();
            }
        }
    }

    private static ImportRequest GroupImportRequest() => new()
    {
        SourceProfileId = FixtureSourceAdapter.FixtureAccountId,
        SourceConversationId = FixtureSourceAdapter.GroupConversation,
        Kind = ConversationKind.Group,
        ConversationTitle = "华东产品创新中心工作群",
    };

    /// <summary>
    /// The archive must hold no trace of a conversation whose import did not publish: no
    /// messages, no conversation row, and no aggregates that count records it does not have.
    /// </summary>
    private static async Task AssertNothingWasPublishedAsync(
        SqliteArchiveStore store,
        string conversationId)
    {
        Assert.Empty(await store.ReadMessagesAsync(conversationId, CancellationToken.None));
        Assert.Null(await store.GetConversationAsync(conversationId, CancellationToken.None));

        var stats = await store.GetArchiveStatsAsync(CancellationToken.None);
        Assert.Equal(0, stats.ConversationCount);
        Assert.Equal(0, stats.MessageCount);
    }

    [Fact]
    public async Task AnUnreadableShardSurfacesAFatalDiagnosticAndAFailedRunNotAnEmptyImport()
    {
        using var temp = new TempDirectory();
        var adapter = new UnreadableShardAdapter();
        var clock = new FixedClock();
        var store = new SqliteArchiveStore(temp.Combine("archive.db"), clock);
        var importer = new ImportService(adapter, store, clock);

        var outcome = await importer.ImportConversationAsync(
            new ImportRequest
            {
                SourceProfileId = UnreadableShardAdapter.ProfileId,
                SourceConversationId = UnreadableShardAdapter.ConversationId,
                Kind = ConversationKind.Group,
            },
            null, CancellationToken.None);

        // The run must not be reported as a successful empty conversation.
        Assert.Equal(ImportRunStatus.Failed, outcome.Run.Status);

        var fatal = Assert.Single(outcome.Diagnostics, d => d.Severity == DiagnosticSeverity.Fatal);
        Assert.Equal(DiagnosticCodes.PartitionUnreadable, fatal.Code);
        Assert.Contains(UnreadableShardAdapter.ConversationId, fatal.Message, StringComparison.Ordinal);

        // A failed run must not be relabelled as a benign "no new records" summary.
        Assert.DoesNotContain(outcome.Diagnostics, d => d.Code == DiagnosticCodes.NoNewRecords);
    }

    [Fact]
    public async Task AnUnreadableShardDoesNotProduceAnEmptyExportDataset()
    {
        using var temp = new TempDirectory();
        var adapter = new UnreadableShardAdapter();
        var clock = new FixedClock();
        var store = new SqliteArchiveStore(temp.Combine("archive.db"), clock);
        var exporter = new JsonlDatasetExporter(store);
        var importer = new ImportService(adapter, store, clock);
        var workflow = new ArchiveWorkflow(exporter, store, clock);

        var outcome = await importer.ImportConversationAsync(
            new ImportRequest
            {
                SourceProfileId = UnreadableShardAdapter.ProfileId,
                SourceConversationId = UnreadableShardAdapter.ConversationId,
                Kind = ConversationKind.Group,
            },
            null, CancellationToken.None);

        Assert.Equal(ImportRunStatus.Failed, outcome.Run.Status);

        var output = temp.Combine("export");

        // The failed run published nothing, so export — a read-only derivation from the canonical
        // archive (Issue #66) — has no conversation to derive and must not produce an empty package.
        var ex = await Assert.ThrowsAsync<ConversationNotArchivedException>(() =>
            workflow.ExportConversationAsync(
                new ExportConversationRequest
                {
                    ConversationId = StableIds.Conversation(
                        StableIds.Account(adapter.AdapterName, UnreadableShardAdapter.ProfileId),
                        ConversationKind.Group,
                        UnreadableShardAdapter.ConversationId,
                        null),
                    OutputDirectory = output,
                },
                null, CancellationToken.None));

        Assert.Equal(
            StableIds.Conversation(
                StableIds.Account(adapter.AdapterName, UnreadableShardAdapter.ProfileId),
                ConversationKind.Group,
                UnreadableShardAdapter.ConversationId,
                null),
            ex.ConversationId);

        Assert.False(Directory.Exists(output),
            "an empty export package must not be produced for an unreadable shard");
    }

    [Fact]
    public async Task ARecordWithoutStableIdFailsImportAndCannotExportAReducedDataset()
    {
        using var temp = new TempDirectory();
        var adapter = new MissingMessageIdAdapter();
        var clock = new FixedClock();
        var store = new SqliteArchiveStore(temp.Combine("archive.db"), clock);
        var exporter = new JsonlDatasetExporter(store);
        var importer = new ImportService(adapter, store, clock);
        var workflow = new ArchiveWorkflow(exporter, store, clock);

        var outcome = await importer.ImportConversationAsync(
            new ImportRequest
            {
                SourceProfileId = FixtureSourceAdapter.FixtureAccountId,
                SourceConversationId = FixtureSourceAdapter.GroupConversation,
                Kind = ConversationKind.Group,
            },
            null, CancellationToken.None);

        Assert.Equal(ImportRunStatus.Failed, outcome.Run.Status);
        Assert.Contains(
            outcome.Diagnostics,
            d => d.Code == DiagnosticCodes.SourceMessageIdUnavailable
              && d.Message.Contains("stable SourceMessageId", StringComparison.Ordinal));

        var output = temp.Combine("export");

        // A source record without an identity must not yield a silently reduced export: the run
        // published no conversation, so the canonical-only export cannot derive a dataset.
        await Assert.ThrowsAsync<ConversationNotArchivedException>(() =>
            workflow.ExportConversationAsync(
                new ExportConversationRequest
                {
                    ConversationId = StableIds.Conversation(
                        StableIds.Account(adapter.AdapterName, FixtureSourceAdapter.FixtureAccountId),
                        ConversationKind.Group,
                        FixtureSourceAdapter.GroupConversation,
                        null),
                    OutputDirectory = output,
                },
                null,
                CancellationToken.None));

        Assert.False(Directory.Exists(output),
            "a source record without an identity must not yield a silently reduced export");
    }

    [Fact]
    public async Task ARunFailingAfterAFlushedBatchPublishesNoPartialConversation()
    {
        // 3,000 valid records means at least one full 2,048-record batch is written before the
        // record without identity fails the run, which is the case the smaller stub cannot
        // reach.
        const int validRecords = 3_000;

        using var temp = new TempDirectory();
        var clock = new FixedClock();
        var archivePath = temp.Combine("archive.db");
        var store = new SqliteArchiveStore(archivePath, clock);
        var importer = new ImportService(
            new LongStreamAdapter(validRecords, StreamFault.MissingIdentity, "batch record"),
            store,
            clock);

        var outcome = await importer.ImportConversationAsync(
            GroupImportRequest(),
            null,
            CancellationToken.None);

        Assert.Equal(ImportRunStatus.Failed, outcome.Run.Status);
        var fatal = Assert.Single(outcome.Diagnostics, d => d.Severity == DiagnosticSeverity.Fatal);
        Assert.Equal(DiagnosticCodes.SourceMessageIdUnavailable, fatal.Code);

        // The run read those records; it published none of them, so it must not claim them.
        // The record that failed normalization is not counted as scanned — it is reported
        // through the Fatal diagnostic instead.
        Assert.Equal(validRecords, outcome.Run.RecordsScanned);
        Assert.Equal(0, outcome.Run.RecordsInserted);
        Assert.Equal(0, outcome.Run.RecordsUpdated);
        Assert.Equal(0, outcome.Run.RecordsSkipped);

        await AssertNothingWasPublishedAsync(store, outcome.ConversationId);

        // The rollback must be durable, not merely invisible inside the live store.
        var reopened = new SqliteArchiveStore(archivePath, clock);
        await AssertNothingWasPublishedAsync(reopened, outcome.ConversationId);
    }

    [Fact]
    public async Task AShardFailingAfterAFlushedBatchPublishesNoPartialConversation()
    {
        const int validRecords = 2_500;

        using var temp = new TempDirectory();
        var clock = new FixedClock();
        var store = new SqliteArchiveStore(temp.Combine("archive.db"), clock);
        var importer = new ImportService(
            new LongStreamAdapter(validRecords, StreamFault.CoverageException, "batch record"),
            store,
            clock);

        var outcome = await importer.ImportConversationAsync(
            GroupImportRequest(),
            null,
            CancellationToken.None);

        Assert.Equal(ImportRunStatus.Failed, outcome.Run.Status);
        var fatal = Assert.Single(outcome.Diagnostics, d => d.Severity == DiagnosticSeverity.Fatal);
        Assert.Equal(DiagnosticCodes.PartitionUnreadable, fatal.Code);
        Assert.Equal(0, outcome.Run.RecordsInserted);

        await AssertNothingWasPublishedAsync(store, outcome.ConversationId);
    }

    [Fact]
    public async Task AFailedReImportLeavesThePreviouslyArchivedConversationUntouched()
    {
        using var temp = new TempDirectory();
        var clock = new FixedClock();
        var store = new SqliteArchiveStore(temp.Combine("archive.db"), clock);

        // A first, complete import archives the fixture conversation.
        var first = await new ImportService(new FixtureSourceAdapter(), store, clock)
            .ImportConversationAsync(GroupImportRequest(), null, CancellationToken.None);
        Assert.Equal(ImportRunStatus.Completed, first.Run.Status);

        var beforeMessages = await store.ReadMessagesAsync(first.ConversationId, CancellationToken.None);
        var beforeConversation = await store.GetConversationAsync(first.ConversationId, CancellationToken.None);
        Assert.NotEmpty(beforeMessages);
        Assert.NotNull(beforeConversation);

        // A later run re-reads those records with different text and then fails: the updates it
        // staged must be rolled back rather than left half-applied over good archived data.
        var failing = new ImportService(
            new LongStreamAdapter(2_500, StreamFault.UnexpectedException, "rewritten record"),
            store,
            clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failing.ImportConversationAsync(GroupImportRequest(), null, CancellationToken.None));

        var afterMessages = await store.ReadMessagesAsync(first.ConversationId, CancellationToken.None);
        Assert.Equal(
            beforeMessages.Select(m => (m.Id, m.Text)),
            afterMessages.Select(m => (m.Id, m.Text)));
        Assert.Equal(
            beforeConversation,
            await store.GetConversationAsync(first.ConversationId, CancellationToken.None));
    }

    [Fact]
    public async Task ACancelledImportKeepsTheRecordsItAlreadyRead()
    {
        // Cancellation is the one deliberate exception to "an incomplete read publishes
        // nothing": the user stopped the run, and the UI promises that already-written archive
        // records are kept because archive writes are idempotent by stable ID.
        using var temp = new TempDirectory();
        var clock = new FixedClock();
        var store = new SqliteArchiveStore(temp.Combine("archive.db"), clock);
        var importer = new ImportService(
            new LongStreamAdapter(3_000, StreamFault.None, "batch record"),
            store,
            clock);

        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            importer.ImportConversationAsync(GroupImportRequest(), new CancelOnArchiving(cts), cts.Token));

        var conversationId = StableIds.GroupConversation(
            StableIds.Account(new FixtureSourceAdapter().AdapterName, FixtureSourceAdapter.FixtureAccountId),
            FixtureSourceAdapter.GroupConversation);

        var conversation = await store.GetConversationAsync(conversationId, CancellationToken.None);
        Assert.NotNull(conversation);

        var messages = await store.ReadMessagesAsync(conversationId, CancellationToken.None);
        Assert.Equal(2048, messages.Count);
        Assert.Equal(messages.Count, conversation.MessageCount);
    }
}
