using System.Globalization;
using System.Text.Json.Nodes;
using WeArchive.Core.Domain;

namespace WeArchive.Core.Normalization;

/// <summary>
/// Everything the normalizer needs to turn a source record into a canonical record.
/// Supplied by the import orchestration layer so that the normalizer itself stays
/// pure and unit-testable without any source, archive or filesystem access.
/// </summary>
public sealed record NormalizationContext
{
    public required string AccountId { get; init; }

    /// <summary>Stable conversation ID of the conversation being imported.</summary>
    public required string ConversationId { get; init; }

    public required string SourceProfileId { get; init; }

    public required string AdapterName { get; init; }

    public required string AdapterVersion { get; init; }

    public string? SourceVersion { get; init; }

    public string? ImportRunId { get; init; }

    /// <summary>Maps an upstream user id to a stable <c>u_...</c> identity.</summary>
    public required Func<string, string> ResolveParticipantId { get; init; }
}

/// <summary>
/// The canonical boundary. Converts adapter output into the stable semantic model
/// defined by docs/MESSAGE_SCHEMA.md.
/// <para>
/// Upstream type codes are preserved verbatim in <see cref="SourceProvenance"/> for
/// later parser improvements, but they never influence downstream export shape.
/// Unrecognized records become <see cref="CanonicalMessageType.Unknown"/> and are
/// always emitted: this type is never allowed to drop a record.
/// </para>
/// </summary>
public static class MessageNormalizer
{
    public static CanonicalMessage Normalize(SourceMessage source, NormalizationContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        var sourceMessageId = source.SourceMessageId;
        if (string.IsNullOrWhiteSpace(sourceMessageId))
        {
            throw new ArgumentException(
                "A source message must carry a stable SourceMessageId. Adapters are responsible for a " +
                "documented composite identity (for example partition + local ordering key) when upstream " +
                "has no stable id. See docs/DATA_MODEL.md section 16.",
                nameof(source));
        }

        var content = source.Content;
        var type = MapType(content.Kind);
        var text = SemanticText.Build(type, content);

        return new CanonicalMessage
        {
            Id = StableIds.Message(context.ConversationId, sourceMessageId),
            ConversationId = context.ConversationId,
            SenderId = ResolveSender(source.SenderSourceUserId, context),
            OccurredAt = source.OccurredAt,
            Type = type,
            Text = text,
            Payload = BuildPayload(type, content, source, context),
            ReplyTo = BuildReply(source.Reply, context),
            Source = new SourceProvenance
            {
                SourceProfileId = context.SourceProfileId,
                SourceConversationId = source.SourceConversationId,
                SourceMessageId = sourceMessageId,
                SourceType = source.SourceType,
                SourceSubtype = source.SourceSubtype,
                SourcePartition = source.SourcePartition,
                SourceOrderKey = source.SourceOrderKey,
                AdapterName = context.AdapterName,
                AdapterVersion = context.AdapterVersion,
                SourceVersion = context.SourceVersion,
                ImportRunId = context.ImportRunId,
            },
            IsPartial = content.IsPartial,
        };
    }

    public static CanonicalMessageType MapType(SourceContentKind kind) => kind switch
    {
        SourceContentKind.Text => CanonicalMessageType.Text,
        SourceContentKind.Image => CanonicalMessageType.Image,
        SourceContentKind.Voice => CanonicalMessageType.Voice,
        SourceContentKind.Video => CanonicalMessageType.Video,
        SourceContentKind.File => CanonicalMessageType.File,
        SourceContentKind.Link => CanonicalMessageType.Link,
        SourceContentKind.AppShare => CanonicalMessageType.AppShare,
        SourceContentKind.MiniProgram => CanonicalMessageType.MiniProgram,
        SourceContentKind.ForwardBundle => CanonicalMessageType.ForwardBundle,
        SourceContentKind.Location => CanonicalMessageType.Location,
        SourceContentKind.ContactCard => CanonicalMessageType.ContactCard,
        SourceContentKind.System => CanonicalMessageType.System,
        SourceContentKind.Revoke => CanonicalMessageType.Revoke,
        SourceContentKind.RedPacket => CanonicalMessageType.RedPacket,
        SourceContentKind.Transfer => CanonicalMessageType.Transfer,
        SourceContentKind.Emoji => CanonicalMessageType.Emoji,
        _ => CanonicalMessageType.Unknown,
    };

    private static string? ResolveSender(string? senderSourceUserId, NormalizationContext context)
    {
        if (string.IsNullOrWhiteSpace(senderSourceUserId))
        {
            return null;
        }

        // A synthetic marker used by adapters when the record is generated by the
        // service itself rather than by a person.
        return senderSourceUserId == SourceSenderIds.System
            ? null
            : context.ResolveParticipantId(senderSourceUserId);
    }

    private static ReplyReference? BuildReply(SourceReplySnapshot? reply, NormalizationContext context)
    {
        if (reply is null)
        {
            return null;
        }

        return new ReplyReference
        {
            // Resolved later against the archive; never fabricated here.
            MessageId = null,
            SourceMessageId = reply.SourceMessageId,
            SenderId = string.IsNullOrWhiteSpace(reply.SenderSourceId)
                ? null
                : context.ResolveParticipantId(reply.SenderSourceId),
            SenderName = reply.SenderName,
            Text = reply.Text,
            OccurredAt = reply.OccurredAt,
        };
    }

    private static JsonObject? BuildPayload(
        CanonicalMessageType type,
        SourceMessageContent content,
        SourceMessage source,
        NormalizationContext context)
    {
        switch (type)
        {
            case CanonicalMessageType.Text:
            case CanonicalMessageType.Image:
            case CanonicalMessageType.Video:
            case CanonicalMessageType.Emoji:
            case CanonicalMessageType.RedPacket:
                return null;

            case CanonicalMessageType.Voice:
                return new JsonObject { ["duration_seconds"] = content.DurationSeconds };

            case CanonicalMessageType.File:
                return new JsonObject
                {
                    ["filename"] = content.FileName,
                    ["extension"] = content.FileExtension,
                    ["size_bytes"] = content.FileSizeBytes,
                };

            case CanonicalMessageType.Link:
                return new JsonObject
                {
                    ["title"] = content.Title,
                    ["description"] = content.Description,
                    ["original_url"] = content.OriginalUrl,
                    ["fallback_url"] = content.FallbackUrl,
                    ["source_app"] = content.SourceApp,
                };

            case CanonicalMessageType.AppShare:
                return new JsonObject
                {
                    ["source_app"] = content.SourceApp,
                    ["app_id"] = content.AppId,
                    ["title"] = content.Title,
                    ["description"] = content.Description,
                    ["original_url"] = content.OriginalUrl,
                    ["fallback_url"] = content.FallbackUrl,
                    ["page_path"] = content.PagePath,
                };

            case CanonicalMessageType.MiniProgram:
                return new JsonObject
                {
                    ["app_name"] = content.SourceApp,
                    ["app_id"] = content.AppId,
                    ["title"] = content.Title,
                    ["page_path"] = content.PagePath,
                    ["original_url"] = content.OriginalUrl,
                };

            case CanonicalMessageType.ForwardBundle:
                return new JsonObject
                {
                    ["title"] = content.Title,
                    ["item_count"] = content.ForwardItemCount ?? content.ForwardItems?.Count,
                    ["items"] = BuildForwardItems(content.ForwardItems),
                };

            case CanonicalMessageType.Location:
                return new JsonObject
                {
                    ["label"] = content.Label,
                    ["address"] = content.Address,
                    ["latitude"] = content.Latitude,
                    ["longitude"] = content.Longitude,
                };

            case CanonicalMessageType.ContactCard:
                return new JsonObject
                {
                    ["display_name"] = content.CardDisplayName,
                    ["source_user_id"] = content.CardSourceUserId,
                };

            case CanonicalMessageType.System:
                return new JsonObject
                {
                    ["event"] = content.SystemEvent,
                    ["actor_ids"] = BuildIdArray(content.ActorSourceIds, context),
                    ["target_ids"] = BuildIdArray(content.TargetSourceIds, context),
                };

            case CanonicalMessageType.Revoke:
                return new JsonObject
                {
                    ["operator_id"] = string.IsNullOrWhiteSpace(content.OperatorSourceId)
                        ? null
                        : context.ResolveParticipantId(content.OperatorSourceId),
                    ["revoked_message_id"] = content.RevokedSourceMessageId is { Length: > 0 } revoked
                        ? StableIds.Message(context.ConversationId, revoked)
                        : null,
                    ["revoked_text"] = content.RevokedText,
                };

            case CanonicalMessageType.Transfer:
                return new JsonObject
                {
                    ["amount"] = content.Amount,
                    ["currency"] = content.Currency,
                    ["status"] = content.TransferStatus,
                };

            default:
                return new JsonObject
                {
                    ["source_type"] = source.SourceType,
                    ["source_subtype"] = source.SourceSubtype,
                    ["raw_summary"] = content.RawSummary,
                };
        }
    }

    private static JsonArray? BuildForwardItems(IReadOnlyList<SourceForwardItem>? items)
    {
        if (items is null || items.Count == 0)
        {
            return null;
        }

        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add(new JsonObject
            {
                ["sender_name"] = item.SenderName,
                ["sender_id"] = item.SenderId,
                ["time"] = item.OccurredAt?.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                ["type"] = item.Type.ToWireName(),
                ["text"] = item.Text,
            });
        }

        return array;
    }

    private static JsonArray? BuildIdArray(IReadOnlyList<string>? sourceIds, NormalizationContext context)
    {
        if (sourceIds is null || sourceIds.Count == 0)
        {
            return null;
        }

        var array = new JsonArray();
        foreach (var id in sourceIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                array.Add(context.ResolveParticipantId(id));
            }
        }

        return array;
    }
}

/// <summary>Sentinel sender ids understood by the normalizer.</summary>
public static class SourceSenderIds
{
    /// <summary>
    /// Used by adapters for records the service itself generated (group notices,
    /// recalls, system events). Rendered as a null <c>sender_id</c> rather than a
    /// fabricated identity. docs/MESSAGE_SCHEMA.md section 3.2.
    /// </summary>
    public const string System = "\u0000system";
}
