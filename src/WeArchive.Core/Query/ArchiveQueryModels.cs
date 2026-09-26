using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;

namespace WeArchive.Core.Query;

/// <summary>
/// A caller-facing message listing request. Filter values arrive as the canonical wire forms the
/// CLI documents, so the service — not the transport — owns filter validation.
/// </summary>
public sealed record MessageListRequest
{
    /// <summary>Stable conversation ID (<c>g_...</c> / <c>u_...</c>). Required.</summary>
    public required string ConversationId { get; init; }

    /// <summary>Inclusive lower bound on the message instant, as the caller spelled it.</summary>
    public string? Since { get; init; }

    /// <summary>Inclusive upper bound on the message instant, as the caller spelled it.</summary>
    public string? Until { get; init; }

    /// <summary>Canonical stable participant ID (<c>u_&lt;16 hex&gt;</c>) of the sender.</summary>
    public string? ParticipantId { get; init; }

    /// <summary>Canonical semantic message type wire name, e.g. <c>text</c> or <c>unknown</c>.</summary>
    public string? Type { get; init; }

    /// <summary>Requested page size. Null selects <see cref="ArchiveQueryService.DefaultLimit"/>.</summary>
    public int? Limit { get; init; }

    /// <summary>Opaque cursor previously returned as <c>next_cursor</c>.</summary>
    public string? Cursor { get; init; }
}

/// <summary>
/// One page of canonical messages plus the opaque cursor that continues the same query.
/// <see cref="NextCursor"/> is null exactly when <see cref="HasMore"/> is false, so a caller
/// cannot mistake an exhausted result for a resumable one.
/// </summary>
public sealed record MessageQueryPage
{
    public required IReadOnlyList<CanonicalMessage> Items { get; init; }

    public required bool HasMore { get; init; }

    public string? NextCursor { get; init; }
}

/// <summary>Bounded context around one canonical message, addressed by stable message ID.</summary>
public sealed record MessageContextWindow
{
    public required CanonicalMessage Message { get; init; }

    public required IReadOnlyList<CanonicalMessage> Before { get; init; }

    public required IReadOnlyList<CanonicalMessage> After { get; init; }
}

/// <summary>
/// Archive freshness, expressed so that capture progress, canonical ingest progress and canonical
/// query state are distinguishable without reading checkpoint tables (docs/HARNESS.md section 10).
/// They legitimately differ: captured evidence may not be ingested yet, and an ingest may be
/// older than the newest capture.
/// </summary>
public sealed record ArchiveFreshness
{
    public required CanonicalFreshness Canonical { get; init; }

    /// <summary>
    /// Reason the preservation store could not be enumerated at all, or null when it was.
    /// <para>
    /// When this is set, <see cref="Capture"/> and <see cref="Ingest"/> list only the accounts the
    /// canonical archive knows, so a vault-only account is visibly unknown rather than silently
    /// absent. Reading canonical status never fails because the preservation store did.
    /// </para>
    /// </summary>
    public string? CaptureUnavailableReason { get; init; }

    /// <summary>Latest successfully published Raw Vault generation per known account.</summary>
    public required IReadOnlyList<CaptureFreshness> Capture { get; init; }

    /// <summary>Latest successfully ingested scope/time per account with ingest progress.</summary>
    public required IReadOnlyList<IngestFreshness> Ingest { get; init; }
}

/// <summary>The canonical archive's own queryable state.</summary>
public sealed record CanonicalFreshness
{
    public required string ArchivePath { get; init; }

    public required int AccountCount { get; init; }

    public required int ConversationCount { get; init; }

    public required int ParticipantCount { get; init; }

    public required int MessageCount { get; init; }

    /// <summary>Instant of the newest archived message, or null for an empty archive.</summary>
    public DateTimeOffset? LastMessageAt { get; init; }

    /// <summary>When this status was read. Query freshness is always "as of" this instant.</summary>
    public required DateTimeOffset QueriedAt { get; init; }
}

/// <summary>
/// The latest published Raw Vault generation for one account, without the manifest fields that
/// are internal evidence (partition fingerprints, artifact checksums, artifact contents).
/// </summary>
public sealed record CaptureFreshness
{
    public required string AccountId { get; init; }

    /// <summary>
    /// Generation ID of the latest valid published generation, or null when none can be reported —
    /// because no generation exists yet, or because the preservation store could not be read
    /// (see <see cref="UnavailableReason"/>). Null always means "no generation can be reported",
    /// never "a generation was found and matched".
    /// </summary>
    public string? GenerationId { get; init; }

    public DateTimeOffset? CaptureTime { get; init; }

    public RawGenerationCompleteness? Completeness { get; init; }

    public int ArtifactCount { get; init; }

    public string? PreviousGenerationId { get; init; }

    /// <summary>
    /// Short engineering reason when this account's capture state could not be read, or null when it
    /// was read — including when the account simply has no generation yet. It carries an exception
    /// type and message only: never artifact contents, a partition fingerprint, a checksum or
    /// message content.
    /// </summary>
    public string? UnavailableReason { get; init; }
}