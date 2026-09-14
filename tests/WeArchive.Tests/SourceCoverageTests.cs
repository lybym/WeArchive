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
        var catalog = new SourceCatalogService(adapter);
        var importer = new ImportService(adapter, store, clock);
        var workflow = new ArchiveWorkflow(catalog, importer, exporter, store, clock);

        var output = temp.Combine("export");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.ExportConversationAsync(
                new ExportConversationRequest
                {
                    SourceProfileId = UnreadableShardAdapter.ProfileId,
                    SourceConversationId = UnreadableShardAdapter.ConversationId,
                    Kind = ConversationKind.Group,
                    OutputDirectory = output,
                },
                null, CancellationToken.None));

        Assert.Contains(UnreadableShardAdapter.ConversationId, ex.Message, StringComparison.Ordinal);

        // No dataset package may be produced for a conversation whose source is
        // unavailable: the exporter is never reached.
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
        var workflow = new ArchiveWorkflow(
            new SourceCatalogService(adapter),
            new ImportService(adapter, store, clock),
            exporter,
            store,
            clock);
        var output = temp.Combine("export");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.ExportConversationAsync(
                new ExportConversationRequest
                {
                    SourceProfileId = FixtureSourceAdapter.FixtureAccountId,
                    SourceConversationId = FixtureSourceAdapter.GroupConversation,
                    Kind = ConversationKind.Group,
                    OutputDirectory = output,
                },
                null,
                CancellationToken.None));

        Assert.Contains("stable SourceMessageId", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output),
            "a source record without an identity must not yield a silently reduced export");
    }
}
