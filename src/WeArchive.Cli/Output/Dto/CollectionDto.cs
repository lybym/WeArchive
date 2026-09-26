using System.Text.Json.Serialization;
using WeArchive.Core.Collections;

namespace WeArchive.Cli.Output.Dto;

// CLI JSON DTOs for the Collection scope (docs/PRD.md FR-23/FR-29). They are presentation
// contracts (docs/ARCHITECTURE.md section 3.1.1): stable field names pinned by [JsonPropertyName],
// stable IDs only, and no internal configuration-object leakage. See docs/CLI.md.

/// <summary>One element of the <c>wearchive collection list --json</c> array.</summary>
public sealed record CollectionListItemDto
{
    /// <summary>The Collection name, exactly as declared in the authoritative configuration.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Resolved membership size: valid stable conversation IDs, de-duplicated.</summary>
    [JsonPropertyName("conversation_count")]
    public required int ConversationCount { get; init; }

    /// <summary>Declared entries that are not valid stable conversation IDs.</summary>
    [JsonPropertyName("invalid_member_count")]
    public required int InvalidMemberCount { get; init; }

    /// <summary>Declared entries that repeat an earlier membership entry.</summary>
    [JsonPropertyName("duplicate_member_count")]
    public required int DuplicateMemberCount { get; init; }
}

/// <summary>Result of <c>wearchive collection show &lt;name&gt; --json</c>.</summary>
public sealed record CollectionDetailDto
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Resolved stable conversation membership: valid IDs, first-declaration order, de-duplicated.</summary>
    [JsonPropertyName("conversation_ids")]
    public required IReadOnlyList<string> ConversationIds { get; init; }

    /// <summary>Declared entries that are not valid stable conversation IDs, in declaration order.</summary>
    [JsonPropertyName("invalid_conversation_ids")]
    public required IReadOnlyList<string> InvalidConversationIds { get; init; }

    /// <summary>Declared entries that repeat an earlier membership entry, in declaration order.</summary>
    [JsonPropertyName("duplicate_conversation_ids")]
    public required IReadOnlyList<string> DuplicateConversationIds { get; init; }
}

/// <summary>One element of the <c>wearchive sync --collection &lt;name&gt; --json</c> result.</summary>
public sealed record CollectionSyncItemDto
{
    /// <summary>The stable conversation ID the Collection declared.</summary>
    [JsonPropertyName("conversation_id")]
    public required string ConversationId { get; init; }

    /// <summary>Stable wire name: <c>succeeded</c>, <c>no_change</c>, <c>failed</c> or <c>unresolved</c>.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>Conversations published by this member's ingest call (0 or 1).</summary>
    [JsonPropertyName("conversations_ingested")]
    public required int ConversationsIngested { get; init; }

    /// <summary>The engineering reason this member did not succeed; null on success/no-change.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>Maps a <see cref="CollectionSyncItemStatus"/> to its stable wire name.</summary>
    public static string ToWireName(CollectionSyncItemStatus status) => status switch
    {
        CollectionSyncItemStatus.Succeeded => "succeeded",
        CollectionSyncItemStatus.NoChange => "no_change",
        CollectionSyncItemStatus.Failed => "failed",
        CollectionSyncItemStatus.Unresolved => "unresolved",
        _ => "failed",
    };

    public static CollectionSyncItemDto From(CollectionSyncItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new CollectionSyncItemDto
        {
            ConversationId = item.ConversationId,
            Status = ToWireName(item.Status),
            ConversationsIngested = item.ConversationsIngested,
            Error = item.Error,
        };
    }
}

/// <summary>Per-status counts of a Collection sync, so a partial run is never read as total success.</summary>
public sealed record CollectionSyncSummaryDto
{
    [JsonPropertyName("requested")]
    public required int Requested { get; init; }

    [JsonPropertyName("succeeded")]
    public required int Succeeded { get; init; }

    [JsonPropertyName("no_change")]
    public required int NoChange { get; init; }

    [JsonPropertyName("failed")]
    public required int Failed { get; init; }
}

/// <summary>Result of <c>wearchive sync --collection &lt;name&gt; --json</c>.</summary>
public sealed record CollectionSyncResultDto
{
    [JsonPropertyName("collection")]
    public required string Collection { get; init; }

    /// <summary>True only when every requested member succeeded or was verified unchanged.</summary>
    [JsonPropertyName("succeeded")]
    public required bool Succeeded { get; init; }

    /// <summary>Stable account ID the evidence was captured from; null when no capture ran.</summary>
    [JsonPropertyName("account_id")]
    public string? AccountId { get; init; }

    /// <summary>Source profile ID the evidence was captured from; null when no capture ran.</summary>
    [JsonPropertyName("source_profile_id")]
    public string? SourceProfileId { get; init; }

    /// <summary>The Raw Vault generation the members were ingested from; null when no capture ran.</summary>
    [JsonPropertyName("generation_id")]
    public string? GenerationId { get; init; }

    /// <summary><c>baseline</c> or <c>incremental</c>; null when no capture ran.</summary>
    [JsonPropertyName("capture_mode")]
    public string? CaptureMode { get; init; }

    [JsonPropertyName("conversations")]
    public required IReadOnlyList<CollectionSyncItemDto> Conversations { get; init; }

    [JsonPropertyName("summary")]
    public required CollectionSyncSummaryDto Summary { get; init; }

    /// <summary>Declared entries that are not valid stable conversation IDs.</summary>
    [JsonPropertyName("invalid_conversation_ids")]
    public required IReadOnlyList<string> InvalidConversationIds { get; init; }

    /// <summary>Declared entries that repeat an earlier membership entry.</summary>
    [JsonPropertyName("duplicate_conversation_ids")]
    public required IReadOnlyList<string> DuplicateConversationIds { get; init; }
}