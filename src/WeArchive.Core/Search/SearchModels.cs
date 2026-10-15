using WeArchive.Core.Domain;

namespace WeArchive.Core.Search;

/// <summary>
/// A caller-facing canonical search request. Filter values arrive as the canonical wire forms the
/// CLI documents, so the service — not the transport — owns query compilation and filter
/// validation (docs/SEARCH_SPEC.md sections 5 and 7).
/// </summary>
public sealed record SearchRequest
{
    /// <summary>The query text, in the grammar of docs/SEARCH_SPEC.md section 5.1. Required.</summary>
    public required string Query { get; init; }

    /// <summary>
    /// Restricts results to one canonical conversation (<c>g_...</c> / <c>u_...</c>). Mutually
    /// exclusive with <see cref="CollectionName"/>.
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>
    /// Restricts results to the union of the Collection's member conversations, resolved through
    /// the one authoritative Collection catalog (docs/SEARCH_SPEC.md section 7). Mutually
    /// exclusive with <see cref="ConversationId"/>.
    /// </summary>
    public string? CollectionName { get; init; }

    /// <summary>Inclusive lower bound on the message instant, as the caller spelled it.</summary>
    public string? Since { get; init; }

    /// <summary>Inclusive upper bound on the message instant, as the caller spelled it.</summary>
    public string? Until { get; init; }

    /// <summary>Canonical stable participant ID (<c>u_&lt;16 hex&gt;</c>) of the sender.</summary>
    public string? ParticipantId { get; init; }

    /// <summary>Canonical semantic message type wire name, e.g. <c>text</c> or <c>unknown</c>.</summary>
    public string? Type { get; init; }

    /// <summary>Requested page size (1–500). Null selects the service default.</summary>
    public int? Limit { get; init; }

    /// <summary>Opaque cursor previously returned as <c>next_cursor</c>.</summary>
    public string? Cursor { get; init; }
}

/// <summary>
/// One page of canonical search results plus the opaque cursor that continues the same search.
/// <para>
/// Hits carry the same canonical message DTO the M3a commands return — canonical envelope, payload,
/// resolved reply and the full provenance block — so a Harness can chain search → context with
/// stable IDs without a second representation (docs/SEARCH_SPEC.md section 9.1). Zero matches is a
/// successful result: empty items, <see cref="HasMore"/> false and a null cursor. The contract
/// exposes no score value; only the order is part of the contract (docs/SEARCH_SPEC.md section 6).
/// </para>
/// </summary>
public sealed record SearchPage
{
    public required IReadOnlyList<CanonicalMessage> Items { get; init; }

    public required bool HasMore { get; init; }

    public string? NextCursor { get; init; }
}
