using WeArchive.Core.RawVault;

namespace WeArchive.Core.Services;

/// <summary>
/// Request for one conversation-scoped preservation-first synchronization
/// (<c>wearchive sync --conversation</c>). docs/PRD.md FR-04/FR-09/FR-14/FR-29, Issue #49.
/// </summary>
public sealed record ConversationSyncRequest
{
    /// <summary>
    /// The stable conversation id (<c>g_…</c>/<c>u_…</c>) to ingest from the captured evidence.
    /// The caller resolves the documented <c>&lt;id-or-alias&gt;</c> selector first; canonical
    /// identity is the stable id (docs/DATA_MODEL.md section 16).
    /// </summary>
    public required string ConversationId { get; init; }

    /// <summary>
    /// The source profile to capture. When null the current account is auto-selected, which never
    /// prompts and is therefore safe under <c>--no-input</c>.
    /// </summary>
    public string? SourceProfileId { get; init; }
}

/// <summary>
/// The stable machine-readable result of one conversation-scoped sync. It is the application-level
/// boundary handed to the CLI and to the future canonical coverage rollup: it fixes
/// <c>succeeded</c>/<c>no_change</c> semantics without exposing Raw Vault internals
/// (docs/CLI.md, docs/ARCHITECTURE.md section 3.1.1).
/// </summary>
public sealed record ConversationSyncResult
{
    /// <summary>The stable account id the evidence was captured from.</summary>
    public required string AccountId { get; init; }

    /// <summary>The source profile the evidence was captured from.</summary>
    public required string SourceProfileId { get; init; }

    /// <summary>The stable conversation id this run published, or verified unchanged.</summary>
    public required string ConversationId { get; init; }

    public required SyncPublicationStatus Status { get; init; }

    /// <summary>Conversations published by this run (0 or 1).</summary>
    public required int ConversationsIngested { get; init; }

    /// <summary>The Raw Vault generation the conversation was ingested from.</summary>
    public required string GenerationId { get; init; }

    /// <summary>Whether the capture reused verified evidence or read the whole supported source.</summary>
    public required RawCaptureMode CaptureMode { get; init; }

    /// <summary>The generation this one extends; null for the first published generation.</summary>
    public string? PreviousGenerationId { get; init; }

    /// <summary>
    /// Diagnostics the capture publication recorded (for example a full-snapshot fallback or a
    /// completeness downgrade). They are capture-side findings and are deliberately not a
    /// canonical partition/evidence coverage contract.
    /// </summary>
    public IReadOnlyList<RawManifestDiagnostic> CaptureDiagnostics { get; init; } = [];
}
