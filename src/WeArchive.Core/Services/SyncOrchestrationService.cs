using WeArchive.Core.Abstractions;
using WeArchive.Core.RawVault;

namespace WeArchive.Core.Services;

/// <summary>
/// The single application-level boundary for preservation-first scope synchronization: live
/// evidence is acquired once through the shared <see cref="CaptureService"/>, and canonical
/// publication happens through the conversation-scoped Raw Vault ingest path
/// (<see cref="IConversationIngestService"/>).
/// <para>
/// Every scope that synchronizes live source content — one conversation
/// (<see cref="ConversationSyncService"/>) or a named Collection
/// (<see cref="CollectionSyncService"/>) — depends on this boundary instead of assembling its own
/// capture/ingest pipeline. That is what keeps the shipped workflow single-sourced
/// (docs/ARCHITECTURE.md sections 3.2 and 3.8, docs/PRD.md G4/FR-14/FR-29, Issue #49):
/// </para>
/// <code>
/// live source -> CaptureService -> immutable Raw Vault generation
///             -> conversation-scoped incremental ingest -> canonical SQLite + ingest checkpoint
/// </code>
/// <para>
/// The boundary owns only the two shared decisions. Capture is account-scoped and mandatory: a
/// capture that publishes nothing is an operation-level failure
/// (<see cref="SyncCaptureException"/>), never a fabricated per-conversation outcome. An ingest
/// that publishes nothing is a verified unchanged scope (<see cref="SyncPublicationStatus.NoChange"/>),
/// not a failure, because the Raw Vault ingest path only reports zero after it has compared the
/// conversation's evidence fingerprint and committed its transactional coverage cursor.
/// </para>
/// <para>
/// Reliability is unchanged. Capture remains R1 and one conversation's ingest remains R2: this
/// service sequences the documented components and adds no journal, commit marker, recovery state
/// or new transaction protocol (docs/DEVELOPMENT.md section 10).
/// </para>
/// </summary>
public sealed class SyncOrchestrationService
{
    private readonly CaptureService _captureService;
    private readonly IConversationIngestService _ingest;

    public SyncOrchestrationService(CaptureService captureService, IConversationIngestService ingest)
    {
        _captureService = captureService ?? throw new ArgumentNullException(nameof(captureService));
        _ingest = ingest ?? throw new ArgumentNullException(nameof(ingest));
    }

    /// <summary>
    /// Acquires live-source evidence for one account into an immutable Raw Vault generation.
    /// </summary>
    /// <param name="sourceProfileId">
    /// The source profile to capture. When null the current account is auto-selected, which never
    /// prompts and is therefore safe under <c>--no-input</c>.
    /// </param>
    /// <param name="progress">Optional human progress; never machine-readable stdout.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>The successfully published generation.</returns>
    /// <exception cref="SyncCaptureException">
    /// No usable generation was published. A failed or cancelled capture publishes nothing and
    /// leaves the previously verified generation chain untouched
    /// (docs/DEVELOPMENT.md section 10.3.1).
    /// </exception>
    /// <exception cref="OperationCanceledException">The capture was cancelled; nothing was published.</exception>
    public async Task<CaptureResult> CaptureAsync(
        string? sourceProfileId,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var capture = await _captureService.CaptureAccountAsync(
            new CaptureRequest { SourceProfileId = sourceProfileId },
            progress is null ? null : new SyncCaptureProgress(progress),
            cancellationToken).ConfigureAwait(false);

        if (!capture.Succeeded)
        {
            throw new SyncCaptureException(
                capture.FailureMessage ?? "the capture did not publish a usable Raw Vault generation.");
        }

        return capture;
    }

    /// <summary>
    /// Publishes one conversation's already-captured Raw Vault evidence into the canonical archive.
    /// </summary>
    /// <param name="accountId">The stable account id whose published evidence is read.</param>
    /// <param name="conversationSelector">
    /// The stable conversation id (<c>g_…</c>/<c>u_…</c>) or the upstream source conversation id.
    /// </param>
    /// <param name="progress">Optional human progress; never machine-readable stdout.</param>
    /// <param name="cancellationToken">
    /// Cooperative cancellation. The in-flight conversation's canonical writes and its ingest
    /// checkpoint are rolled back together; an earlier published Raw Vault generation is retained.
    /// </param>
    /// <exception cref="Domain.ConversationNotInRawVaultException">
    /// The selector matched no conversation in any published generation for the account.
    /// </exception>
    public async Task<SyncIngestOutcome> IngestConversationAsync(
        string accountId,
        string conversationSelector,
        string generationId,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        var published = await _ingest
            .IngestConversationFromGenerationAsync(
                accountId, conversationSelector, generationId, progress, cancellationToken)
            .ConfigureAwait(false);

        return new SyncIngestOutcome
        {
            ConversationsPublished = published,
            // Zero published conversations means the conversation was verified against newer
            // evidence and found unchanged: a success with no new canonical records, never a
            // silent failure.
            Status = published > 0 ? SyncPublicationStatus.Succeeded : SyncPublicationStatus.NoChange,
        };
    }

    /// <summary>
    /// Forwards capture progress as a human-readable line. It reports synchronously (unlike
    /// <see cref="Progress{T}"/>), so progress order is deterministic for a caller that records it.
    /// </summary>
    private sealed class SyncCaptureProgress(IProgress<string> target) : IProgress<CaptureProgress>
    {
        public void Report(CaptureProgress value) => target.Report(
            value.Total > 0
                ? $"Capturing: {value.Stage} {value.Processed}/{value.Total}"
                : $"Capturing: {value.Stage}");
    }
}

/// <summary>
/// A capture that did not publish a usable Raw Vault generation, so the requested scope could not
/// be ingested. It is the shared operation-level failure of every preservation-first sync scope; a
/// scope-specific failure type may derive from it to add scope detail.
/// </summary>
public class SyncCaptureException : Exception
{
    public SyncCaptureException(string reason)
        : this($"Capturing live-source evidence failed: {reason}", reason)
    {
    }

    protected SyncCaptureException(string message, string reason)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>The capture failure reason without the shared scope prefix.</summary>
    public string Reason { get; }
}

/// <summary>
/// Outcome of publishing one conversation's preserved evidence into the canonical archive.
/// </summary>
public enum SyncPublicationStatus
{
    /// <summary>New or changed evidence was published and the conversation's checkpoint advanced.</summary>
    Succeeded,

    /// <summary>
    /// The conversation's evidence was verified unchanged; nothing was republished and its
    /// content checkpoint kept its value.
    /// </summary>
    NoChange,
}

/// <summary>What one conversation-scoped ingest call published.</summary>
public sealed record SyncIngestOutcome
{
    /// <summary>Conversations published by this call (0 or 1).</summary>
    public required int ConversationsPublished { get; init; }

    public required SyncPublicationStatus Status { get; init; }
}
