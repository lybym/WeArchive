using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.Normalization;

namespace WeArchive.Core.Services;

public sealed record ImportRequest
{
    public required string SourceProfileId { get; init; }

    public required string SourceConversationId { get; init; }

    public required ConversationKind Kind { get; init; }

    /// <summary>For direct conversations: the peer's upstream user id.</summary>
    public string? PeerSourceUserId { get; init; }

    public string? ConversationTitle { get; init; }

    /// <summary>Expected record count, used only for progress reporting.</summary>
    public int? TotalHint { get; init; }
}

public sealed record ImportOutcome
{
    public required string ConversationId { get; init; }

    public required ImportRun Run { get; init; }

    public required UpsertCounters Counters { get; init; }

    public IReadOnlyList<ImportDiagnostic> Diagnostics { get; init; } = [];

    public DateTimeOffset? FirstMessageAt { get; init; }

    public DateTimeOffset? LastMessageAt { get; init; }
}

/// <summary>
/// Orchestrates source -&gt; normalizer -&gt; archive for one conversation.
/// docs/ARCHITECTURE.md section 3.2 and section 4.
/// <para>
/// The importer never bypasses the canonical model and never writes exports.
/// Records it cannot interpret are normalized to <c>unknown</c> and counted; they
/// are never skipped.
/// </para>
/// </summary>
public sealed class ImportService(ISourceAdapter adapter, IArchiveStore archive, IClock clock)
{
    private const int BatchSize = 2048;

    private readonly ISourceAdapter _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    private readonly IArchiveStore _archive = archive ?? throw new ArgumentNullException(nameof(archive));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public async Task<ImportOutcome> ImportConversationAsync(
        ImportRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _archive.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var descriptor = await _adapter.DescribeSourceAsync(cancellationToken).ConfigureAwait(false);
        if (!descriptor.IsAvailable)
        {
            throw new InvalidOperationException(
                descriptor.UnavailableReason ?? "The source is not available.");
        }

        var accountId = StableIds.Account(_adapter.AdapterName, request.SourceProfileId);
        var conversationId = ResolveConversationId(accountId, request);

        await _archive.UpsertAccountAsync(new ArchiveAccount
        {
            Id = accountId,
            SourceProfileId = request.SourceProfileId,
            AdapterName = _adapter.AdapterName,
            AdapterVersion = _adapter.AdapterVersion,
            SourceVersion = descriptor.SourceVersion,
            DisplayName = request.SourceProfileId,
        }, cancellationToken).ConfigureAwait(false);

        var participants = await _adapter
            .ListParticipantsAsync(request.SourceProfileId, cancellationToken)
            .ConfigureAwait(false);

        await _archive.UpsertParticipantsAsync(
            participants.Select(p => new ArchiveParticipant
            {
                Id = StableIds.Participant(accountId, p.SourceUserId),
                AccountId = accountId,
                SourceParticipantId = p.SourceUserId,
                LatestRemark = p.Remark,
                Nickname = p.Nickname,
                Alias = p.Alias,
            }),
            cancellationToken).ConfigureAwait(false);

        var run = await _archive.BeginImportRunAsync(accountId, descriptor, cancellationToken)
            .ConfigureAwait(false);

        var diagnostics = new DiagnosticBag();
        var counters = new UpsertCounters();
        var total = request.TotalHint ?? 0;
        var processed = 0;
        DateTimeOffset? first = null;
        DateTimeOffset? last = null;

        var context = new NormalizationContext
        {
            AccountId = accountId,
            ConversationId = conversationId,
            SourceProfileId = request.SourceProfileId,
            AdapterName = _adapter.AdapterName,
            AdapterVersion = _adapter.AdapterVersion,
            SourceVersion = descriptor.SourceVersion,
            ImportRunId = run.Id,
            ResolveParticipantId = sourceUserId => StableIds.Participant(accountId, sourceUserId),
        };

        var batch = new List<CanonicalMessage>(BatchSize);
        var status = ImportRunStatus.Completed;
        var published = false;

        // The conversation and its records are staged in one archive transaction
        // (docs/ARCHITECTURE.md section 3.2.1). The conversation row is created inside it and is
        // only published on commit: the archive is the system of record, so a run that cannot
        // read the source completely must leave the archive exactly as it found it instead of
        // publishing a partial conversation that looks complete (FR-14,
        // docs/ARCHITECTURE.md section 11).
        var session = await _archive.BeginConversationImportAsync(
            new ArchiveConversation
            {
                Id = conversationId,
                AccountId = accountId,
                SourceConversationId = request.SourceConversationId,
                Kind = request.Kind,
                Title = request.ConversationTitle,
                PeerParticipantId = request.Kind == ConversationKind.Direct && request.PeerSourceUserId is { Length: > 0 } peer
                    ? StableIds.Participant(accountId, peer)
                    : null,
            },
            cancellationToken).ConfigureAwait(false);

        try
        {
            try
            {
                await foreach (var source in _adapter
                    .ReadMessagesAsync(request.SourceProfileId, request.SourceConversationId, cancellationToken)
                    .WithCancellation(cancellationToken)
                    .ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    CanonicalMessage message;
                    try
                    {
                        message = MessageNormalizer.Normalize(source, context);
                    }
                    catch (ArgumentException ex)
                    {
                        // A record without a stable identity cannot be represented as an
                        // unknown canonical message: doing so would fabricate an id, while
                        // skipping it would make a reduced source look complete. Treat the
                        // adapter-contract breach as source-coverage failure so the workflow
                        // records a Fatal diagnostic and does not export a partial dataset.
                        throw new SourceCoverageException(
                            DiagnosticCodes.SourceMessageIdUnavailable,
                            ex.Message);
                    }

                    if (message.Type == CanonicalMessageType.Unknown)
                    {
                        diagnostics.Partial(
                            DiagnosticCodes.UnknownMessageType,
                            "Source record could not be normalized to a canonical type and was archived as 'unknown'.",
                            source.SourceType,
                            source.SourceSubtype);
                    }
                    else if (message.IsPartial)
                    {
                        diagnostics.Partial(
                            DiagnosticCodes.PartialAppMessage,
                            "Source record was only partially parsed; missing fields were left null.",
                            source.SourceType,
                            source.SourceSubtype);
                    }

                    if (message.ReplyTo is not null && message.ReplyTo.Text is null)
                    {
                        diagnostics.Partial(
                            DiagnosticCodes.UnresolvedReplyTarget,
                            "Reply target has no locally available quote snapshot.",
                            source.SourceType,
                            source.SourceSubtype);
                    }

                    batch.Add(message);
                    processed++;
                    first = first is null || message.OccurredAt < first ? message.OccurredAt : first;
                    last = last is null || message.OccurredAt > last ? message.OccurredAt : last;

                    if (batch.Count >= BatchSize)
                    {
                        counters = Add(counters, await FlushAsync(session, batch, cancellationToken).ConfigureAwait(false));
                        Report(progress, OperationStages.Archiving, processed, total, conversationId);
                    }
                    else if (processed % 256 == 0)
                    {
                        Report(progress, OperationStages.ReadingMessages, processed, total, conversationId);
                    }
                }

                if (batch.Count > 0)
                {
                    counters = Add(counters, await FlushAsync(session, batch, cancellationToken).ConfigureAwait(false));
                }

                // The source was read to the end, so the staged conversation may become the
                // archive's record of it.
                await session.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                published = true;
            }
            catch (OperationCanceledException)
            {
                // A deliberate stop keeps what was already read: archive writes are idempotent
                // by stable ID, so a later full re-read completes the conversation without
                // producing duplicates. That promise is what the UI shows on cancellation.
                status = ImportRunStatus.Cancelled;
                await session.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                published = true;
                throw;
            }
            catch (SourceCoverageException ex)
            {
                // The source could not provide complete coverage for this conversation (a
                // missing or unreadable message shard, or a record without identity). Surface a
                // typed Fatal diagnostic instead of completing as a misleading empty import
                // (FR-14). Nothing this run read is published, so the archive keeps no partial
                // conversation.
                status = ImportRunStatus.Failed;
                diagnostics.Fatal(ex.Code, ex.Message);
                await session.RollbackAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                status = ImportRunStatus.Failed;
                await session.RollbackAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            // The session must release the database before the import-run audit row is written.
            await session.DisposeAsync().ConfigureAwait(false);

            if (status == ImportRunStatus.Completed)
            {
                var resolved = await _archive
                    .ResolveReplyTargetsAsync(conversationId, CancellationToken.None)
                    .ConfigureAwait(false);

                if (resolved > 0)
                {
                    diagnostics.Info(
                        DiagnosticCodes.ReplySnapshotOnly,
                        $"Resolved {resolved} reply target(s) against the archive.");
                }
            }

            // "No new records" is only an honest Info summary when the import genuinely
            // completed and read nothing. A failed run (e.g. an unreadable shard) must not
            // be relabelled as a successful empty conversation.
            if (status == ImportRunStatus.Completed && processed == 0)
            {
                diagnostics.Info(DiagnosticCodes.NoNewRecords, "The conversation produced no records.");
            }

            var rolled = diagnostics.Rollup();
            var finished = run with
            {
                FinishedAt = _clock.UtcNow,
                Status = status,
                RecordsScanned = processed,
                // The counters describe what this run left in the archive. A rolled-back run
                // published nothing, so it must not claim inserted or updated records; what it
                // read is still visible through RecordsScanned and the Fatal diagnostic.
                RecordsInserted = published ? counters.Inserted : 0,
                RecordsUpdated = published ? counters.Updated : 0,
                RecordsSkipped = published ? counters.Unchanged : 0,
                UnknownCount = counters.Unknown,
                PartialCount = counters.Partial,
                WarningCount = rolled.Count(d => d.Severity == DiagnosticSeverity.Partial),
                ErrorCount = rolled.Count(d => d.Severity == DiagnosticSeverity.Fatal),
                Diagnostics = rolled,
            };

            await _archive.CompleteImportRunAsync(finished, CancellationToken.None).ConfigureAwait(false);
            run = finished;
        }

        return new ImportOutcome
        {
            ConversationId = conversationId,
            Run = run,
            Counters = counters,
            Diagnostics = run.Diagnostics,
            FirstMessageAt = first,
            LastMessageAt = last,
        };
    }

    // Delegates to StableIds.Conversation so the importer and the CLI discovery surface share
    // one derivation (docs/DATA_MODEL.md section 16): a conversation's stable id is the same
    // before and after import.
    private static string ResolveConversationId(string accountId, ImportRequest request) =>
        StableIds.Conversation(accountId, request.Kind, request.SourceConversationId, request.PeerSourceUserId);

    private static async Task<UpsertCounters> FlushAsync(
        IConversationImportSession session,
        List<CanonicalMessage> batch,
        CancellationToken cancellationToken)
    {
        var result = await session.UpsertMessagesAsync(batch, cancellationToken).ConfigureAwait(false);
        batch.Clear();
        return result;
    }

    private static UpsertCounters Add(UpsertCounters a, UpsertCounters b) => new()
    {
        Inserted = a.Inserted + b.Inserted,
        Updated = a.Updated + b.Updated,
        Unchanged = a.Unchanged + b.Unchanged,
        Unknown = a.Unknown + b.Unknown,
        Partial = a.Partial + b.Partial,
    };

    private static void Report(
        IProgress<OperationProgress>? progress,
        string stage,
        int processed,
        int total,
        string conversationId) =>
        progress?.Report(new OperationProgress
        {
            Stage = stage,
            Processed = processed,
            Total = total,
            Detail = conversationId,
        });
}
