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

/// <summary>
/// The bounded breakdown dimension of one statistics read (M3c,
/// docs/DATA_MODEL.md section 24, docs/CLI.md <c>wearchive stats</c>).
/// </summary>
public enum StatisticsBreakdownKind
{
    /// <summary>One UTC calendar day per bucket, derived from the canonical instant.</summary>
    Day,

    /// <summary>One UTC calendar month per bucket, derived from the canonical instant.</summary>
    Month,

    /// <summary>One archived conversation per bucket, keyed by the stable conversation ID.</summary>
    Conversation,
}

public static class StatisticsBreakdownKinds
{
    /// <summary>The stable wire value documented for the <c>--bucket</c> option.</summary>
    public static string ToWireName(this StatisticsBreakdownKind kind) => kind switch
    {
        StatisticsBreakdownKind.Day => "day",
        StatisticsBreakdownKind.Month => "month",
        StatisticsBreakdownKind.Conversation => "conversation",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// Parses one documented <c>--bucket</c> wire value. Anything else is rejected by the caller —
    /// an unsupported breakdown is a usage failure, never a silently different bucketing.
    /// </summary>
    public static bool TryParse(string? wireName, out StatisticsBreakdownKind kind)
    {
        switch (wireName)
        {
            case "day":
                kind = StatisticsBreakdownKind.Day;
                return true;
            case "month":
                kind = StatisticsBreakdownKind.Month;
                return true;
            case "conversation":
                kind = StatisticsBreakdownKind.Conversation;
                return true;
            default:
                kind = StatisticsBreakdownKind.Day;
                return false;
        }
    }
}

/// <summary>
/// Totals of one statistics read. Every number is an observation over the archived canonical
/// records inside the requested scope and range — never a claim about the source's unarchived
/// remainder (docs/PRD.md FR-16, docs/DATA_MODEL.md section 24).
/// </summary>
public sealed record StatisticsTotals
{
    /// <summary>Counted archived messages, including unknown and partial records.</summary>
    public required int MessageCount { get; init; }

    /// <summary>Distinct archived conversations that contributed at least one counted message.</summary>
    public required int ConversationCount { get; init; }

    /// <summary>Distinct stable participant IDs observed as senders of counted messages.</summary>
    public required int SenderCount { get; init; }

    /// <summary>
    /// Counted messages whose sender could not be resolved to a stable participant ID. They are
    /// reported explicitly instead of being dropped or folded into <see cref="SenderCount"/>.
    /// </summary>
    public required int UnattributedCount { get; init; }

    /// <summary>Counted messages whose canonical type is <c>unknown</c> (MESSAGE_SCHEMA section 5).</summary>
    public required int UnknownCount { get; init; }

    /// <summary>Counted messages flagged <c>is_partial</c> — the record was not fully parsed.</summary>
    public required int PartialCount { get; init; }

    /// <summary>Instant of the first counted message in canonical timeline order, or null when none.</summary>
    public DateTimeOffset? FirstMessageAt { get; init; }

    /// <summary>Instant of the last counted message in canonical timeline order, or null when none.</summary>
    public DateTimeOffset? LastMessageAt { get; init; }
}

/// <summary>One canonical message type's share of the counted messages.</summary>
public sealed record StatisticsTypeCount
{
    /// <summary>Canonical message type wire name (<c>text</c>, …, <c>unknown</c>).</summary>
    public required string Type { get; init; }

    public required int Count { get; init; }
}

/// <summary>
/// One breakdown bucket. For the <c>day</c>/<c>month</c> breakdowns only <see cref="BucketStart"/>
/// is set; for the <c>conversation</c> breakdown only <see cref="ConversationId"/> is. Both shapes
/// carry the same counts, and unknown/partial records are counted inside their bucket instead of
/// being silently dropped.
/// </summary>
public sealed record StatisticsBreakdownRow
{
    /// <summary>UTC start instant of the day/month bucket (day/month breakdown only).</summary>
    public DateTimeOffset? BucketStart { get; init; }

    /// <summary>Stable conversation ID of the bucket (conversation breakdown only).</summary>
    public string? ConversationId { get; init; }

    public required int MessageCount { get; init; }

    public required int UnknownCount { get; init; }

    public required int PartialCount { get; init; }
}

/// <summary>
/// One read-only statistics query at the archive persistence boundary (M3c,
/// docs/DATA_MODEL.md section 24). Scope arrives as archived conversation IDs — null counts the
/// whole archive — and the caller owns scope resolution and keyset-position validation.
/// </summary>
public sealed record ArchiveStatisticsQuery
{
    /// <summary>Archived conversation IDs to count, or null for the whole archive.</summary>
    public IReadOnlyList<string>? ConversationIds { get; init; }

    /// <summary>Inclusive lower bound on the counted message instant.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Inclusive upper bound on the counted message instant.</summary>
    public DateTimeOffset? Until { get; init; }

    public StatisticsBreakdownKind Breakdown { get; init; } = StatisticsBreakdownKind.Day;

    /// <summary>
    /// Exclusive keyset position after which the breakdown page starts, or null for the first
    /// page. Its spelling is breakdown-kind-specific (day epoch, <c>YYYY-MM</c> month key, stable
    /// conversation ID) and is validated by the caller.
    /// </summary>
    public string? BreakdownAfter { get; init; }

    /// <summary>Maximum number of breakdown rows the page may contain.</summary>
    public required int BreakdownLimit { get; init; }
}

/// <summary>
/// The archive's statistics answer: scope totals, the full canonical type distribution and one
/// bounded breakdown page, all derived from canonical rows at query time.
/// </summary>
public sealed record ArchiveStatisticsResult
{
    public required StatisticsTotals Totals { get; init; }

    public required IReadOnlyList<StatisticsTypeCount> ByType { get; init; }

    public required IReadOnlyList<StatisticsBreakdownRow> BreakdownItems { get; init; }

    /// <summary>True when the archive held at least one further matching bucket after this page.</summary>
    public required bool BreakdownHasMore { get; init; }
}