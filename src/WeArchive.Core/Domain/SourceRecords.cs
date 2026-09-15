namespace WeArchive.Core.Domain;

/// <summary>What an adapter can report about the local source it fronts.</summary>
public sealed record SourceDescriptor
{
    public required string AdapterName { get; init; }

    public required string AdapterVersion { get; init; }

    /// <summary>Upstream client version, e.g. <c>4.1.13.12</c>. Null when undetectable.</summary>
    public string? SourceVersion { get; init; }

    /// <summary>Human-readable product name, e.g. <c>WeChat for Windows</c>.</summary>
    public string? SourceProductName { get; init; }

    public required bool IsAvailable { get; init; }

    /// <summary>Why the source is unavailable; safe to display (no secrets, no chat content).</summary>
    public string? UnavailableReason { get; init; }

    /// <summary>Non-fatal observations gathered while probing the source.</summary>
    public IReadOnlyList<ImportDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>One locally available source profile (a logged-in local account).</summary>
public sealed record SourceAccount
{
    public required string SourceProfileId { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>Absolute path of the account data directory. Diagnostics only.</summary>
    public string? DataRootPath { get; init; }

    public DateTimeOffset? LastActiveAt { get; init; }

    public bool IsCurrent { get; init; }
}

/// <summary>A conversation as reported by an adapter before normalization.</summary>
public sealed record SourceConversation
{
    public required string SourceConversationId { get; init; }

    public required ConversationKind Kind { get; init; }

    public string? Title { get; init; }

    /// <summary>For direct conversations: the peer's upstream user id.</summary>
    public string? PeerSourceUserId { get; init; }

    public DateTimeOffset? LastMessageAt { get; init; }

    public int? MessageCountHint { get; init; }
}

/// <summary>A conversation participant as reported by an adapter.</summary>
public sealed record SourceParticipant
{
    public required string SourceUserId { get; init; }

    public string? Remark { get; init; }

    public string? Nickname { get; init; }

    public string? Alias { get; init; }
}

/// <summary>
/// One raw-ish upstream record, already translated out of the client wire format
/// by the adapter but not yet normalized into the canonical message envelope.
/// </summary>
public sealed record SourceMessage
{
    public required string SourceConversationId { get; init; }

    /// <summary>Upstream user id of the sender, when resolvable.</summary>
    public string? SenderSourceUserId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Verbatim upstream type code, kept for provenance and diagnostics.</summary>
    public string? SourceType { get; init; }

    /// <summary>Verbatim upstream subtype code, kept for provenance and diagnostics.</summary>
    public string? SourceSubtype { get; init; }

    /// <summary>Upstream partition (e.g. which message shard) the record came from.</summary>
    public string? SourcePartition { get; init; }

    /// <summary>
    /// Stable upstream record identifier. When upstream has no single id, the adapter
    /// supplies its documented composite identity instead.
    /// </summary>
    public string? SourceMessageId { get; init; }

    /// <summary>Upstream ordering key inside the partition.</summary>
    public string? SourceOrderKey { get; init; }

    public required SourceMessageContent Content { get; init; }

    public SourceReplySnapshot? Reply { get; init; }
}
