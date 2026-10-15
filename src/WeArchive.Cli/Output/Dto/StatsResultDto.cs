using System.Text.Json.Serialization;
using WeArchive.Core.Domain;
using WeArchive.Core.Query;

namespace WeArchive.Cli.Output.Dto;

/// <summary>
/// <c>wearchive stats --json</c> result (M3c, docs/CLI.md, docs/DATA_MODEL.md section 24).
/// Field names are pinned by <c>[JsonPropertyName]</c> so a C# rename can never change the machine
/// contract (docs/PRD.md FR-22).
/// <para>
/// Every number is an observation over archived canonical records inside the documented scope —
/// the document says what was counted (<c>scope</c>), when and against which archive
/// (<c>basis</c>), and reports unknown/partial/unattributed records explicitly instead of
/// silently dropping them. It never asserts unobserved messages or participants.
/// </para>
/// </summary>
public sealed record StatsResultDto
{
    [JsonPropertyName("scope")]
    public required StatsScopeDto Scope { get; init; }

    [JsonPropertyName("basis")]
    public required StatsBasisDto Basis { get; init; }

    [JsonPropertyName("totals")]
    public required StatsTotalsDto Totals { get; init; }

    [JsonPropertyName("by_type")]
    public required IReadOnlyList<StatsTypeCountDto> ByType { get; init; }

    [JsonPropertyName("breakdown")]
    public required StatsBreakdownDto Breakdown { get; init; }

    public static StatsResultDto From(ArchiveStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);

        return new StatsResultDto
        {
            Scope = new StatsScopeDto
            {
                Kind = statistics.ScopeKind,
                ConversationId = statistics.ConversationId,
                Collection = statistics.CollectionName,
                Since = statistics.Since,
                Until = statistics.Until,
            },
            Basis = new StatsBasisDto
            {
                Source = "canonical_archive",
                ArchivePath = statistics.ArchivePath,
                QueriedAt = statistics.QueriedAt,
            },
            Totals = StatsTotalsDto.From(statistics.Totals),
            ByType = [.. statistics.ByType.Select(StatsTypeCountDto.From)],
            Breakdown = new StatsBreakdownDto
            {
                Bucket = statistics.Breakdown.ToWireName(),
                Items = [.. statistics.BreakdownItems.Select(StatsBreakdownItemDto.From)],
                NextCursor = statistics.NextCursor,
                HasMore = statistics.HasMore,
            },
        };
    }
}

/// <summary>What the counts cover: the resolved statistics scope and its inclusive range.</summary>
public sealed record StatsScopeDto
{
    /// <summary><c>archive</c>, <c>conversation</c> or <c>collection</c>.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>The requested stable conversation ID (conversation scope only).</summary>
    [JsonPropertyName("conversation_id")]
    public string? ConversationId { get; init; }

    /// <summary>The requested Collection name (collection scope only).</summary>
    [JsonPropertyName("collection")]
    public string? Collection { get; init; }

    /// <summary>Resolved inclusive lower bound, or null when unbounded.</summary>
    [JsonPropertyName("since")]
    public DateTimeOffset? Since { get; init; }

    /// <summary>Resolved inclusive upper bound, or null when unbounded.</summary>
    [JsonPropertyName("until")]
    public DateTimeOffset? Until { get; init; }
}

/// <summary>When and against which archive the counts were observed. It names the basis of every
/// number — the canonical archive, as of <c>queried_at</c> — and never a source-coverage claim.</summary>
public sealed record StatsBasisDto
{
    /// <summary>Always <c>canonical_archive</c>; statistics never read the Raw Vault or a live source.</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("archive_path")]
    public required string ArchivePath { get; init; }

    /// <summary>When the counts were observed; every number is "as of" this instant.</summary>
    [JsonPropertyName("queried_at")]
    public required DateTimeOffset QueriedAt { get; init; }
}

/// <summary>Totals of the counted messages. Unknown, partial and unattributed records are explicit
/// first-class counters, never dropped or folded into another number.</summary>
public sealed record StatsTotalsDto
{
    [JsonPropertyName("message_count")]
    public required int MessageCount { get; init; }

    /// <summary>Distinct archived conversations that contributed at least one counted message.</summary>
    [JsonPropertyName("conversation_count")]
    public required int ConversationCount { get; init; }

    /// <summary>Distinct stable participant IDs observed as senders of counted messages.</summary>
    [JsonPropertyName("sender_participant_count")]
    public required int SenderCount { get; init; }

    /// <summary>Counted messages whose sender could not be resolved to a stable participant ID.</summary>
    [JsonPropertyName("unattributed_message_count")]
    public required int UnattributedCount { get; init; }

    /// <summary>Counted messages whose canonical type is <c>unknown</c>.</summary>
    [JsonPropertyName("unknown_count")]
    public required int UnknownCount { get; init; }

    /// <summary>Counted messages flagged <c>is_partial</c>.</summary>
    [JsonPropertyName("partial_count")]
    public required int PartialCount { get; init; }

    /// <summary>First counted message instant in canonical timeline order, or null when none.</summary>
    [JsonPropertyName("first_message_at")]
    public DateTimeOffset? FirstMessageAt { get; init; }

    /// <summary>Last counted message instant in canonical timeline order, or null when none.</summary>
    [JsonPropertyName("last_message_at")]
    public DateTimeOffset? LastMessageAt { get; init; }

    public static StatsTotalsDto From(StatisticsTotals totals)
    {
        ArgumentNullException.ThrowIfNull(totals);

        return new StatsTotalsDto
        {
            MessageCount = totals.MessageCount,
            ConversationCount = totals.ConversationCount,
            SenderCount = totals.SenderCount,
            UnattributedCount = totals.UnattributedCount,
            UnknownCount = totals.UnknownCount,
            PartialCount = totals.PartialCount,
            FirstMessageAt = totals.FirstMessageAt,
            LastMessageAt = totals.LastMessageAt,
        };
    }
}

/// <summary>One canonical message type's share of the counted messages, by stable wire name.</summary>
public sealed record StatsTypeCountDto
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }

    public static StatsTypeCountDto From(StatisticsTypeCount count)
    {
        ArgumentNullException.ThrowIfNull(count);

        return new StatsTypeCountDto { Type = count.Type, Count = count.Count };
    }
}

/// <summary>
/// One bounded page of the requested breakdown. <c>bucket_start</c> is set for the
/// <c>day</c>/<c>month</c> buckets and <c>conversation_id</c> for the <c>conversation</c> buckets;
/// the other key is null. <c>next_cursor</c> is null exactly when <c>has_more</c> is false.
/// </summary>
public sealed record StatsBreakdownDto
{
    /// <summary>The requested breakdown wire name (<c>day</c>, <c>month</c> or <c>conversation</c>).</summary>
    [JsonPropertyName("bucket")]
    public required string Bucket { get; init; }

    [JsonPropertyName("items")]
    public required IReadOnlyList<StatsBreakdownItemDto> Items { get; init; }

    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; init; }

    [JsonPropertyName("has_more")]
    public required bool HasMore { get; init; }
}

/// <summary>One breakdown bucket. Unknown/partial records are counted inside their bucket instead
/// of being silently dropped.</summary>
public sealed record StatsBreakdownItemDto
{
    /// <summary>UTC start instant of the day/month bucket (day/month breakdown only).</summary>
    [JsonPropertyName("bucket_start")]
    public DateTimeOffset? BucketStart { get; init; }

    /// <summary>Stable conversation ID of the bucket (conversation breakdown only).</summary>
    [JsonPropertyName("conversation_id")]
    public string? ConversationId { get; init; }

    [JsonPropertyName("message_count")]
    public required int MessageCount { get; init; }

    [JsonPropertyName("unknown_count")]
    public required int UnknownCount { get; init; }

    [JsonPropertyName("partial_count")]
    public required int PartialCount { get; init; }

    public static StatsBreakdownItemDto From(StatisticsBreakdownRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new StatsBreakdownItemDto
        {
            BucketStart = row.BucketStart,
            ConversationId = row.ConversationId,
            MessageCount = row.MessageCount,
            UnknownCount = row.UnknownCount,
            PartialCount = row.PartialCount,
        };
    }
}
