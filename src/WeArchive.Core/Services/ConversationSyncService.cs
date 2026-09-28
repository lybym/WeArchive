namespace WeArchive.Core.Services;

/// <summary>
/// Conversation-scoped synchronization: acquire live evidence through the shared
/// <see cref="SyncOrchestrationService"/> (capture to an immutable Raw Vault generation) and then
/// publish only the selected conversation through the existing conversation-scoped incremental
/// ingest path. docs/PRD.md G2/G4/FR-04/FR-07/FR-12/FR-14/FR-20/FR-29, docs/ARCHITECTURE.md
/// sections 3.2 and 10, Issue #49.
/// <para>
/// This is an orchestration/contract convergence slice. It deliberately contains no capture
/// policy, no generation selection, no checkpoint logic, no normalization and no transaction
/// semantics of its own: it sequences the two documented boundaries so
/// <c>sync --conversation</c> and <c>sync --collection</c> share one preservation-first workflow
/// rather than maintaining a second incremental-ingest implementation.
/// </para>
/// <para>
/// Deterministic outcomes: a capture that publishes nothing fails the operation before any
/// canonical write (<see cref="SyncCaptureException"/>); an ingest that publishes nothing means the
/// conversation's evidence was verified unchanged and reports
/// <see cref="SyncPublicationStatus.NoChange"/>; anything else publishes the conversation and
/// advances only that conversation's ingest checkpoint.
/// </para>
/// <para>
/// Reliability is unchanged: capture is R1 and the conversation ingest is R2. The selected
/// conversation's canonical records and its ingest-checkpoint advancement commit together or
/// neither does, and a successfully published Raw Vault generation is never rolled back merely
/// because later canonical ingest fails or is cancelled (docs/DEVELOPMENT.md section 10).
/// </para>
/// </summary>
public sealed class ConversationSyncService(SyncOrchestrationService orchestration)
{
    private readonly SyncOrchestrationService _orchestration =
        orchestration ?? throw new ArgumentNullException(nameof(orchestration));

    /// <summary>
    /// Synchronizes one conversation from the live source: capture the account's evidence once,
    /// then ingest only the selected conversation from the published generation.
    /// </summary>
    /// <param name="request">The request; <see cref="ConversationSyncRequest.ConversationId"/> is a stable conversation id.</param>
    /// <param name="progress">Optional human progress; never machine-readable stdout.</param>
    /// <param name="cancellationToken">
    /// Cooperative cancellation. Cancellation before publication follows the capture (R1) or
    /// ingest (R2) semantics of the phase that was in flight: the in-flight conversation is rolled
    /// back with its checkpoint, and an already published Raw Vault generation is retained.
    /// </param>
    /// <exception cref="SyncCaptureException">No usable Raw Vault generation was published.</exception>
    /// <exception cref="Domain.ConversationNotInRawVaultException">
    /// The stable conversation id matched no conversation in the captured evidence.
    /// </exception>
    /// <exception cref="OperationCanceledException">The run was cancelled.</exception>
    public async Task<ConversationSyncResult> SyncAsync(
        ConversationSyncRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConversationId);

        var capture = await _orchestration
            .CaptureAsync(request.SourceProfileId, progress, cancellationToken)
            .ConfigureAwait(false);

        // The capture is account-scoped and succeeds before this point, so the ingest reads only
        // this run's verified evidence. Ingesting one conversation cannot advance another
        // conversation's checkpoint, and a change in another conversation is invisible here.
        var ingested = await _orchestration
            .IngestConversationAsync(capture.AccountId, request.ConversationId, progress, cancellationToken)
            .ConfigureAwait(false);

        return new ConversationSyncResult
        {
            AccountId = capture.AccountId,
            SourceProfileId = capture.SourceProfileId,
            ConversationId = request.ConversationId,
            Status = ingested.Status,
            ConversationsIngested = ingested.ConversationsPublished,
            GenerationId = capture.GenerationId,
            CaptureMode = capture.Mode,
            PreviousGenerationId = capture.PreviousGenerationId,
            CaptureDiagnostics = capture.Diagnostics,
        };
    }
}
