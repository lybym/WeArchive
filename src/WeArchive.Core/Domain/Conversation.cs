namespace WeArchive.Core.Domain;

/// <summary>docs/DATA_MODEL.md section 5.</summary>
public enum ConversationKind
{
    Direct,
    Group,
    Official,
    System,
    Unknown,
}

public static class ConversationKinds
{
    public static string ToWireName(this ConversationKind kind) => kind switch
    {
        ConversationKind.Direct => "direct",
        ConversationKind.Group => "group",
        ConversationKind.Official => "official",
        ConversationKind.System => "system",
        _ => "unknown",
    };

    public static bool TryParse(string? wireName, out ConversationKind kind)
    {
        switch (wireName)
        {
            case "direct": kind = ConversationKind.Direct; return true;
            case "group": kind = ConversationKind.Group; return true;
            case "official": kind = ConversationKind.Official; return true;
            case "system": kind = ConversationKind.System; return true;
            case "unknown": kind = ConversationKind.Unknown; return true;
            default: kind = ConversationKind.Unknown; return false;
        }
    }
}

/// <summary>
/// A normalized conversation as stored in the archive.
/// <see cref="Title"/> is mutable metadata and must never define identity or a physical path.
/// </summary>
public sealed record ArchiveConversation
{
    /// <summary>Stable conversation ID: <c>u_...</c> for direct, <c>g_...</c> for groups.</summary>
    public required string Id { get; init; }

    public required string AccountId { get; init; }

    public required string SourceConversationId { get; init; }

    public required ConversationKind Kind { get; init; }

    public string? Title { get; init; }

    /// <summary>For direct conversations: the peer's stable participant ID.</summary>
    public string? PeerParticipantId { get; init; }

    public string? OwnerParticipantId { get; init; }

    public DateTimeOffset? FirstMessageAt { get; init; }

    public DateTimeOffset? LastMessageAt { get; init; }

    public int MessageCount { get; init; }
}

/// <summary>docs/DATA_MODEL.md section 6. Stable identity and human naming are separate.</summary>
public sealed record ArchiveParticipant
{
    /// <summary>Stable identity: <c>u_...</c>.</summary>
    public required string Id { get; init; }

    public required string AccountId { get; init; }

    public required string SourceParticipantId { get; init; }

    /// <summary>Latest available remark. Empty string when WeChat has no remark.</summary>
    public string? LatestRemark { get; init; }

    public string? Nickname { get; init; }

    public string? Alias { get; init; }

    /// <summary>
    /// User-maintained override. Regeneration must preserve this field.
    /// docs/EXPORT_PRD.md section 5.2.
    /// </summary>
    public string? UserDisplayName { get; init; }

    /// <summary>
    /// Default exported display name. Latest remark when non-empty, otherwise empty.
    /// Nickname must not fill a missing remark. docs/EXPORT_PRD.md section 5.1.
    /// </summary>
    public string ResolveDisplayName() =>
        !string.IsNullOrWhiteSpace(UserDisplayName) ? UserDisplayName!
        : !string.IsNullOrWhiteSpace(LatestRemark) ? LatestRemark!
        : string.Empty;
}

/// <summary>docs/DATA_MODEL.md section 4.</summary>
public sealed record ArchiveAccount
{
    public required string Id { get; init; }

    public required string SourceProfileId { get; init; }

    public required string AdapterName { get; init; }

    public string? AdapterVersion { get; init; }

    public string? SourceVersion { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>Absolute path of the discovered local data root, for diagnostics only.</summary>
    public string? DataRootPath { get; init; }
}
