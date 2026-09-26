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