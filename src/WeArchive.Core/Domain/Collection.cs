namespace WeArchive.Core.Domain;

/// <summary>A user-maintained reusable set of stable conversation IDs.</summary>
public sealed record Collection
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> ConversationIds { get; init; }
}
