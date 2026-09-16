using System.Text.Json.Serialization;

namespace WeArchive.Cli.Output.Dto;

// CLI JSON DTOs for the discovery commands (Issue #7 / M0.5). They are presentation contracts
// (docs/ARCHITECTURE.md section 3.1.1): they wrap source-neutral Core domain results and expose
// only stable IDs and documented metadata, never raw WeChat table names or numeric type codes.
// Field names are pinned by [JsonPropertyName] so a C# rename cannot silently change the wire
// contract. See docs/CLI.md for the documented shapes.

/// <summary>One element of the <c>wearchive account list --json</c> array.</summary>
public sealed record AccountItemDto
{
    /// <summary>The source profile id (the upstream stable account identifier).</summary>
    [JsonPropertyName("source_profile_id")]
    public required string SourceProfileId { get; init; }

    /// <summary>The archive stable account id (<c>a_&lt;16 hex&gt;</c>), identical before and after import.</summary>
    [JsonPropertyName("stable_id")]
    public required string StableId { get; init; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    /// <summary>Whether this profile is the currently active source account.</summary>
    [JsonPropertyName("is_current")]
    public required bool IsCurrent { get; init; }

    [JsonPropertyName("last_active_at")]
    public DateTimeOffset? LastActiveAt { get; init; }

    /// <summary>Absolute path of the account data directory, for diagnostics only.</summary>
    [JsonPropertyName("data_root_path")]
    public string? DataRootPath { get; init; }
}

/// <summary>One element of the <c>wearchive conversation list --json</c> array.</summary>
public sealed record ConversationListItemDto
{
    /// <summary>
    /// The archive stable conversation id (<c>g_&lt;16 hex&gt;</c> for groups, <c>u_&lt;16 hex&gt;</c>
    /// for non-groups), mirroring <c>WeArchive.Core.Domain.StableIds.Conversation</c> exactly.
    /// </summary>
    [JsonPropertyName("stable_id")]
    public required string StableId { get; init; }

    /// <summary>The source conversation identifier (group room id or peer id).</summary>
    [JsonPropertyName("source_id")]
    public required string SourceId { get; init; }

    /// <summary>Stable wire name (<c>direct</c>, <c>group</c>, <c>official</c>, <c>system</c>, <c>unknown</c>).</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>For direct conversations: the peer's upstream user id, when known.</summary>
    [JsonPropertyName("peer_source_user_id")]
    public string? PeerSourceUserId { get; init; }

    [JsonPropertyName("last_message_at")]
    public DateTimeOffset? LastMessageAt { get; init; }

    /// <summary>Optional adapter-supplied record count hint, not a committed archive count.</summary>
    [JsonPropertyName("message_count_hint")]
    public int? MessageCountHint { get; init; }
}

/// <summary>Result of <c>wearchive conversation show &lt;id-or-alias&gt; --json</c>.</summary>
public sealed record ConversationDetailDto
{
    [JsonPropertyName("stable_id")]
    public required string StableId { get; init; }

    [JsonPropertyName("source_id")]
    public required string SourceId { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("peer_source_user_id")]
    public string? PeerSourceUserId { get; init; }

    /// <summary>Committed record count reported by describing the conversation.</summary>
    [JsonPropertyName("message_count")]
    public required int MessageCount { get; init; }

    [JsonPropertyName("first_message_at")]
    public DateTimeOffset? FirstMessageAt { get; init; }

    [JsonPropertyName("last_message_at")]
    public DateTimeOffset? LastMessageAt { get; init; }

    [JsonPropertyName("participant_count")]
    public required int ParticipantCount { get; init; }
}
