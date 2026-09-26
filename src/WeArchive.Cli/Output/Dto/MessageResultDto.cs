using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WeArchive.Core.Domain;

namespace WeArchive.Cli.Output.Dto;

/// <summary>
/// One canonical message as the CLI publishes it. Field names are pinned by
/// <c>[JsonPropertyName]</c> so a C# rename can never change the machine contract
/// (docs/CLI.md, docs/PRD.md FR-22).
/// <para>
/// The shape mirrors the documented canonical message envelope
/// (docs/MESSAGE_SCHEMA.md sections 3–6): stable canonical IDs, the canonical semantic type and
/// text, the structured payload, the reply relationship and the documented <c>source</c>
/// provenance. It never exposes SQLite column names, a numeric upstream type code or a WeChat
/// table.
/// </para>
/// </summary>
public sealed record MessageDto
{
    /// <summary>Stable canonical message ID (<c>m_&lt;16 hex&gt;</c>).</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("conversation_id")]
    public required string ConversationId { get; init; }

    /// <summary>Stable participant ID when the sender resolved, otherwise null. Never invented.</summary>
    [JsonPropertyName("sender_id")]
    public string? SenderId { get; init; }

    /// <summary>Canonical timestamp with the source's offset.</summary>
    [JsonPropertyName("occurred_at")]
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Canonical semantic type wire name (<c>text</c>, <c>unknown</c>, ...).</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    /// <summary>Type-specific structured semantics, or null when the type carries none.</summary>
    [JsonPropertyName("payload")]
    public JsonObject? Payload { get; init; }

    [JsonPropertyName("reply_to")]
    public MessageReplyDto? ReplyTo { get; init; }

    /// <summary>True when the source record could not be fully parsed into its semantic type.</summary>
    [JsonPropertyName("is_partial")]
    public bool IsPartial { get; init; }

    [JsonPropertyName("source")]
    public required MessageSourceDto Source { get; init; }

    public static MessageDto From(CanonicalMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new MessageDto
        {
            Id = message.Id,
            ConversationId = message.ConversationId,
            SenderId = message.SenderId,
            OccurredAt = message.OccurredAt,
            Type = message.Type.ToWireName(),
            Text = message.Text,
            Payload = message.Payload,
            ReplyTo = message.ReplyTo is null ? null : MessageReplyDto.From(message.ReplyTo),
            IsPartial = message.IsPartial,
            Source = new MessageSourceDto
            {
                SourceMessageId = message.Source.SourceMessageId,
                SourceType = message.Source.SourceType,
                SourceSubtype = message.Source.SourceSubtype,
                SourcePartition = message.Source.SourcePartition,
                SourceOrderKey = message.Source.SourceOrderKey,
            },
        };
    }
}

/// <summary>The structural reply/quote relationship (docs/MESSAGE_SCHEMA.md section 6).</summary>
public sealed record MessageReplyDto
{
    /// <summary>Resolved canonical target ID, or null while the target is not archived.</summary>
    [JsonPropertyName("message_id")]
    public string? MessageId { get; init; }

    [JsonPropertyName("sender_id")]
    public string? SenderId { get; init; }

    [JsonPropertyName("sender_name")]
    public string? SenderName { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("time")]
    public DateTimeOffset? OccurredAt { get; init; }

    public static MessageReplyDto From(ReplyReference reply) => new()
    {
        MessageId = reply.MessageId,
        SenderId = reply.SenderId,
        SenderName = reply.SenderName,
        Text = reply.Text,
        OccurredAt = reply.OccurredAt,
    };
}

/// <summary>
/// Canonical provenance of a message (docs/MESSAGE_SCHEMA.md section 3.4, docs/PRD.md FR-19).
/// It is engineering traceability, not message semantics, and is never used for identity.
/// </summary>
public sealed record MessageSourceDto
{
    [JsonPropertyName("source_message_id")]
    public string? SourceMessageId { get; init; }

    [JsonPropertyName("source_type")]
    public string? SourceType { get; init; }

    [JsonPropertyName("source_subtype")]
    public string? SourceSubtype { get; init; }

    [JsonPropertyName("source_partition")]
    public string? SourcePartition { get; init; }

    [JsonPropertyName("source_order_key")]
    public string? SourceOrderKey { get; init; }
}

/// <summary>
/// <c>message list --json</c> result. The three field names are pinned by the Issue and by
/// docs/CLI.md; <c>next_cursor</c> is null exactly when <c>has_more</c> is false.
/// </summary>
public sealed record MessageListResultDto
{
    [JsonPropertyName("items")]
    public required IReadOnlyList<MessageDto> Items { get; init; }

    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; init; }

    [JsonPropertyName("has_more")]
    public required bool HasMore { get; init; }
}

/// <summary>
/// <c>context --json</c> result: the target message is a distinct field from the bounded arrays
/// before and after it, so a caller never has to infer the anchor from a position in a list.
/// </summary>
public sealed record MessageContextResultDto
{
    [JsonPropertyName("message_id")]
    public required string MessageId { get; init; }

    [JsonPropertyName("conversation_id")]
    public required string ConversationId { get; init; }

    [JsonPropertyName("before")]
    public required IReadOnlyList<MessageDto> Before { get; init; }

    [JsonPropertyName("message")]
    public required MessageDto Message { get; init; }

    [JsonPropertyName("after")]
    public required IReadOnlyList<MessageDto> After { get; init; }
}