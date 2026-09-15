namespace WeArchive.Core.Domain;

/// <summary>
/// Cheap description of a source conversation, used to preview a selection before
/// committing to a full read.
/// </summary>
public sealed record SourceConversationDetail
{
    public required string SourceConversationId { get; init; }

    public int MessageCount { get; init; }

    public DateTimeOffset? FirstMessageAt { get; init; }

    public DateTimeOffset? LastMessageAt { get; init; }

    public int ParticipantCount { get; init; }
}
