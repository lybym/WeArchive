using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;

namespace WeArchive.Core.Query;

/// <summary>
/// A caller-facing message listing request. Filter values arrive as the canonical wire forms the
/// CLI documents, so the service — not the transport — owns filter validation.
/// <para>
/// The scope is exactly one of <see cref="ConversationId"/> or <see cref="CollectionName"/>;
/// neither or both is a validation failure. A Collection resolves through the one authoritative
/// Collection catalog (docs/PRD.md FR-23, ADR 0009).
/// </para>
/// </summary>
public sealed record MessageListRequest
{
    /// <summary>Stable conversation ID (<c>g_...</c> / <c>u_...</c>), or null when the listing is
    /// Collection-scoped.</summary>
    public string? ConversationId { get; init; }

    /// <summary>Collection name to resolve as the listing scope, or null when the listing is
    /// conversation-scoped. Resolved through the one authoritative Collection catalog.</summary>
    public string? CollectionName { get; init; }

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
/// The resolved scope a message listing was answered for. It is reported so a caller can tell
/// what the page covers — never as a completeness claim about unarchived data:
/// <c>member_conversation_count</c> counts the Collection's resolved membership and
/// <c>archived_member_conversation_count</c> counts how many of those members the canonical
/// archive knows, so a collection whose canonical data is behind its configuration is visible as
/// such instead of silently reading as up to date (docs/PRD.md FR-23).
/// </summary>
public sealed record MessageQueryScope
{
    /// <summary><c>conversation</c> or <c>collection</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The requested stable conversation ID (conversation scope only).</summary>
    public string? ConversationId { get; init; }

    /// <summary>The requested Collection name (collection scope only).</summary>
    public string? CollectionName { get; init; }

    /// <summary>Resolved valid membership entries (collection scope only).</summary>
    public int? MemberConversationCount { get; init; }

    /// <summary>
    /// Resolved members the canonical archive knows (collection scope only). A member below this
    /// line contributes no messages and is never an error by itself, exactly like the search and
    /// statistics scopes.
    /// </summary>
    public int? ArchivedMemberConversationCount { get; init; }

    /// <summary>Declared membership entries that are not a valid stable conversation ID
    /// (collection scope only). Reported, never silently dropped.</summary>
    public int? InvalidMemberCount { get; init; }

    /// <summary>Declared membership entries that repeat an earlier valid entry (collection scope
    /// only). Reported, never silently dropped.</summary>
    public int? DuplicateMemberCount { get; init; }
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

    /// <summary>The resolved scope this page was answered for (docs/PRD.md FR-23).</summary>
    public required MessageQueryScope Scope { get; init; }
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
    /// was read — including when the account simply has no generation yet.
    /// <para>
    /// It is the failure's exception type and message, so a real defect stays diagnosable. It never
    /// carries artifact contents, a partition fingerprint, a checksum or message content. Like any
    /// local diagnostic it may mention a local filesystem path; it is a report about why a stage
    /// could not be read, never an input a caller parses to locate data.
    /// </para>
    /// </summary>
    public string? UnavailableReason { get; init; }
}