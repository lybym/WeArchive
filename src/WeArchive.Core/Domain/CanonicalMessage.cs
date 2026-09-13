using System.Text.Json.Nodes;

namespace WeArchive.Core.Domain;

/// <summary>
/// The canonical archived semantic event.
/// Normative shape: docs/MESSAGE_SCHEMA.md section 2 and docs/DATA_MODEL.md section 8.
/// </summary>
public sealed record CanonicalMessage
{
    /// <summary>Stable WeArchive message ID (<c>m_...</c>).</summary>
    public required string Id { get; init; }

    /// <summary>Stable direct/group conversation ID (<c>u_...</c> or <c>g_...</c>).</summary>
    public required string ConversationId { get; init; }

    /// <summary>Canonical sender identity when resolvable, otherwise null. Never invented.</summary>
    public string? SenderId { get; init; }

    /// <summary>Canonical timestamp with offset.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    public required CanonicalMessageType Type { get; init; }

    /// <summary>LLM/search-first semantic representation (plain UTF-8 text).</summary>
    public required string Text { get; init; }

    /// <summary>Type-specific structured semantics; null when the type needs no payload.</summary>
    public JsonObject? Payload { get; init; }

    /// <summary>Structural reply/quote relationship plus the locally available snapshot.</summary>
    public ReplyReference? ReplyTo { get; init; }

    public required SourceProvenance Source { get; init; }

    /// <summary>True when the record could not be fully parsed into its semantic type.</summary>
    public bool IsPartial { get; init; }
}

/// <summary>
/// docs/MESSAGE_SCHEMA.md section 6. A missing target ID is represented as null;
/// the system never fabricates a referenced message ID.
/// </summary>
public sealed record ReplyReference
{
    /// <summary>
    /// Canonical ID of the referenced archived message. Null until the target has been
    /// resolved against the archive; it is never guessed.
    /// </summary>
    public string? MessageId { get; init; }

    /// <summary>
    /// Upstream identifier of the referenced record. Provenance only: it lets the archive
    /// resolve <see cref="MessageId"/> when the target is present, and is never exported.
    /// </summary>
    public string? SourceMessageId { get; init; }

    public string? SenderId { get; init; }

    public string? SenderName { get; init; }

    public string? Text { get; init; }

    public DateTimeOffset? OccurredAt { get; init; }
}
