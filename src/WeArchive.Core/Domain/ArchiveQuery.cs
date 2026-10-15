namespace WeArchive.Core.Domain;

/// <summary>
/// One bounded message query at the archive persistence boundary.
/// docs/DATA_MODEL.md section 23, docs/ARCHITECTURE.md section 3.6.
/// <para>
/// The archive answers in canonical timeline order — <c>(occurred_utc, source_order_key, id)</c>
/// — so a page never repeats or omits a record with an equal timestamp. Ordering and filtering
/// use the existing canonical timeline index; no FTS table or second search engine is involved.
/// </para>
/// <para>
/// <see cref="After"/> is an exclusive keyset position, not an offset: paging stays correct while
/// the archive is being written by another run, and it is the reason a cursor can be resumed
/// without duplicates.
/// </para>
/// </summary>
public sealed record ArchiveMessageQuery
{
    /// <summary>Stable conversation ID (<c>g_...</c> / <c>u_...</c>).</summary>
    public required string ConversationId { get; init; }

    /// <summary>Inclusive lower bound on the message instant.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Inclusive upper bound on the message instant.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>Canonical stable participant ID (<c>u_...</c>) of the sender.</summary>
    public string? ParticipantId { get; init; }

    /// <summary>Canonical semantic message type.</summary>
    public CanonicalMessageType? Type { get; init; }

    /// <summary>Maximum number of records the page may contain.</summary>
    public required int Limit { get; init; }

    /// <summary>Exclusive canonical timeline position to resume from.</summary>
    public MessageOrderKey? After { get; init; }
}

/// <summary>
/// A position in the canonical timeline order. It is produced from an already-returned record, so
/// the archive never has to trust a caller-supplied row identity.
/// </summary>
public sealed record MessageOrderKey
{
    /// <summary>The record instant as epoch seconds — the archive's ordering and range column.</summary>
    public required long OccurredUtc { get; init; }

    /// <summary>The source ordering evidence, normalized to <c>""</c> when the source supplied none.</summary>
    public required string SourceOrderKey { get; init; }

    /// <summary>Stable canonical message ID, the final tie-breaker.</summary>
    public required string MessageId { get; init; }
}

/// <summary>One bounded page of canonical messages in timeline order.</summary>
public sealed record ArchiveMessagePage
{
    public required IReadOnlyList<CanonicalMessage> Items { get; init; }

    /// <summary>True when the archive held at least one further record after this page.</summary>
    public required bool HasMore { get; init; }
}

/// <summary>
/// A position in the search result order: the FTS5 <c>bm25</c> rank of the projected body, then
/// the canonical timeline tail <c>(occurred_utc, source_order_key, id)</c> — the deterministic
/// total order of docs/SEARCH_SPEC.md section 6. It is produced from an already-returned row and
/// travels only inside the opaque search cursor token, never as a result value.
/// </summary>
public sealed record SearchOrderPosition
{
    /// <summary>The row's bm25 rank; numerically lower ranks sort first.</summary>
    public required double Rank { get; init; }

    /// <summary>The record instant as epoch seconds — the timeline tie-break column.</summary>
    public required long OccurredUtc { get; init; }

    /// <summary>The source ordering evidence, normalized to <c>""</c> when the source supplied none.</summary>
    public required string SourceOrderKey { get; init; }

    /// <summary>Stable canonical message ID, the final tie-breaker.</summary>
    public required string MessageId { get; init; }
}

/// <summary>
/// A bounded context window around one archived message: the target plus the canonical
/// messages immediately before and after it in the same conversation.
/// </summary>
public sealed record ArchiveMessageContext
{
    public required CanonicalMessage Target { get; init; }

    /// <summary>Up to <c>before</c> records preceding the target, in timeline order.</summary>
    public required IReadOnlyList<CanonicalMessage> Before { get; init; }

    /// <summary>Up to <c>after</c> records following the target, in timeline order.</summary>
    public required IReadOnlyList<CanonicalMessage> After { get; init; }
}

/// <summary>
/// One bounded full-text query against the archive's derived FTS index
/// (docs/SEARCH_SPEC.md sections 5–8, M3b index slice).
/// <para>
/// <see cref="MatchExpression"/> is the caller-compiled MATCH expression: the Core search compiler
/// has already projected every term with the CJK bigram projection and quoted it, so the archive
/// executes exactly the documented matching contract and never invents query semantics of its own.
/// Filters narrow the candidate rows; they never rewrite the query terms.
/// </para>
/// <para>
/// Results are ordered by the search total order — bm25 rank ascending, then the canonical
/// timeline tail <c>(occurred_utc, source_order_key, id)</c> ascending — and the implementation
/// returns at most <see cref="Limit"/> rows, reporting whether further rows matched. No search
/// materializes an unbounded result set.
/// </para>
/// </summary>
public sealed record ArchiveSearchQuery
{
    /// <summary>The compiled FTS5 MATCH expression (projected and quoted by the Core compiler).</summary>
    public required string MatchExpression { get; init; }

    /// <summary>Restricts results to one conversation. Null when the search is unscoped.</summary>
    public string? ConversationId { get; init; }

    /// <summary>
    /// Restricts results to the union of these conversations (a resolved Collection's membership).
    /// Null when the search is not collection-scoped; empty matches nothing.
    /// </summary>
    public IReadOnlyList<string>? ConversationIds { get; init; }

    /// <summary>Inclusive lower bound on the message instant.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Inclusive upper bound on the message instant.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>Canonical stable participant ID of the sender.</summary>
    public string? ParticipantId { get; init; }

    /// <summary>Canonical semantic message type.</summary>
    public CanonicalMessageType? Type { get; init; }

    /// <summary>Maximum number of rows the page may contain.</summary>
    public required int Limit { get; init; }

    /// <summary>Exclusive search-order position to resume from.</summary>
    public SearchOrderPosition? After { get; init; }
}

/// <summary>
/// One search hit: the canonical message DTO plus the bm25 rank the archive ordered it by. The
/// rank is an ordering input for the next page's keyset position and never crosses the application
/// service boundary as a result value (docs/SEARCH_SPEC.md section 6).
/// </summary>
public sealed record ArchiveSearchHit
{
    public required CanonicalMessage Message { get; init; }

    public required double Rank { get; init; }
}

/// <summary>One bounded page of canonical search results in search-order.</summary>
public sealed record ArchiveSearchPage
{
    public required IReadOnlyList<ArchiveSearchHit> Hits { get; init; }

    /// <summary>True when the archive held at least one further matching row after this page.</summary>
    public required bool HasMore { get; init; }
}

/// <summary>
/// Outcome of the archive's search-index verification
/// (docs/SEARCH_SPEC.md section 10.4): FTS5 <c>integrity-check</c> plus a messages-vs-index
/// row-count agreement check, exposed as an archive-maintenance operation — never a per-query tax.
/// <para>
/// A verification failure is a diagnosis, not a repair: recovery is the explicit index rebuild.
/// The reason is a short engineering statement and never carries message or index content.
/// </para>
/// </summary>
public sealed record SearchIndexVerification
{
    /// <summary>True when the index exists, agrees with the canonical rows and passes integrity-check.</summary>
    public required bool IsHealthy { get; init; }

    /// <summary>Short reason the index is not healthy, or null when it is.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Source-neutral projection of the archive's committed ingest progress for one account.
/// docs/DATA_MODEL.md section 14.1, docs/HARNESS.md section 10.
/// <para>
/// It deliberately carries no checkpoint payload, scope-kind vocabulary or table shape: a caller
/// learns <em>what</em> was ingested and <em>when</em>, never how the cursor is encoded.
/// </para>
/// </summary>
public sealed record IngestFreshness
{
    public required string AccountId { get; init; }

    /// <summary>
    /// When this account's most recent conversation-scope canonical publication committed, or null
    /// when no conversation cursor exists yet.
    /// </summary>
    public DateTimeOffset? LastIngestAt { get; init; }

    /// <summary>
    /// Raw Vault generation of the most recently committed conversation ingest, or null. A
    /// conversation where newer evidence was verified unchanged keeps its older generation id,
    /// because nothing was republished.
    /// </summary>
    public string? LatestIngestedGenerationId { get; init; }

    /// <summary>The stable conversation ID that <see cref="LatestIngestedGenerationId"/> belongs to.</summary>
    public string? LatestIngestedConversationId { get; init; }

    /// <summary>
    /// When a complete account-wide generation scan last finished, or null. This is separate from
    /// conversation content progress: a scoped ingest never advances it.
    /// </summary>
    public DateTimeOffset? LastAccountScanAt { get; init; }
}