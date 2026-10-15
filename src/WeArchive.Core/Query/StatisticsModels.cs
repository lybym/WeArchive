using WeArchive.Core.Domain;

namespace WeArchive.Core.Query;

/// <summary>
/// A caller-facing statistics request. Filter values arrive as the canonical wire forms the CLI
/// documents, so the service — not the transport — owns filter and scope validation.
/// </summary>
public sealed record StatisticsRequest
{
    /// <summary>
    /// Optional stable conversation ID (<c>g_...</c> / <c>u_...</c>) narrowing every count to one
    /// conversation. Mutually exclusive with <see cref="CollectionName"/>.
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>
    /// Optional Collection name narrowing every count to the union of the Collection's archived
    /// member conversations. Mutually exclusive with <see cref="ConversationId"/>.
    /// </summary>
    public string? CollectionName { get; init; }

    /// <summary>Inclusive lower bound on the counted message instant, as the caller spelled it.</summary>
    public string? Since { get; init; }

    /// <summary>Inclusive upper bound on the counted message instant, as the caller spelled it.</summary>
    public string? Until { get; init; }

    /// <summary>
    /// Breakdown wire name — <c>day</c> (default), <c>month</c> or <c>conversation</c>.
    /// </summary>
    public string? Bucket { get; init; }

    /// <summary>Requested breakdown page size. Null selects <see cref="ArchiveQueryService.DefaultLimit"/>.</summary>
    public int? Limit { get; init; }

    /// <summary>Opaque cursor previously returned as <c>next_cursor</c>.</summary>
    public string? Cursor { get; init; }
}

/// <summary>
/// The service-level statistics result: what was counted (scope), when and against which archive
/// (basis), the totals, the type distribution and one bounded page of the requested breakdown.
/// <see cref="NextCursor"/> is null exactly when <see cref="HasMore"/> is false, so a caller
/// cannot mistake an exhausted breakdown for a resumable one.
/// </summary>
public sealed record ArchiveStatistics
{
    /// <summary><c>archive</c>, <c>conversation</c> or <c>collection</c>.</summary>
    public required string ScopeKind { get; init; }

    /// <summary>The requested stable conversation ID (conversation scope only).</summary>
    public string? ConversationId { get; init; }

    /// <summary>The requested Collection name (collection scope only).</summary>
    public string? CollectionName { get; init; }

    /// <summary>The resolved inclusive lower bound, or null when unbounded.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>The resolved inclusive upper bound, or null when unbounded.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>Canonical archive the counts were read from. It names the basis, never a source claim.</summary>
    public required string ArchivePath { get; init; }

    /// <summary>When the counts were observed. Every number is "as of" this instant.</summary>
    public required DateTimeOffset QueriedAt { get; init; }

    public required StatisticsTotals Totals { get; init; }

    public required IReadOnlyList<StatisticsTypeCount> ByType { get; init; }

    public required StatisticsBreakdownKind Breakdown { get; init; }

    public required IReadOnlyList<StatisticsBreakdownRow> BreakdownItems { get; init; }

    public required bool HasMore { get; init; }

    public string? NextCursor { get; init; }
}
