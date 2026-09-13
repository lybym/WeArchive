namespace WeArchive.Core.Domain;

/// <summary>
/// Canonical Phase 1 semantic message types.
/// Normative list: docs/MESSAGE_SCHEMA.md section 4.
/// <c>quote</c> is intentionally absent: replying/quoting is a relationship
/// expressed through <see cref="CanonicalMessage.ReplyTo"/>, not a content type.
/// </summary>
public enum CanonicalMessageType
{
    Text,
    Image,
    Voice,
    Video,
    File,
    Link,
    AppShare,
    MiniProgram,
    ForwardBundle,
    Location,
    ContactCard,
    System,
    Revoke,
    RedPacket,
    Transfer,
    Emoji,
    Unknown,
}

public static class CanonicalMessageTypes
{
    private static readonly Dictionary<CanonicalMessageType, string> WireNames = new()
    {
        [CanonicalMessageType.Text] = "text",
        [CanonicalMessageType.Image] = "image",
        [CanonicalMessageType.Voice] = "voice",
        [CanonicalMessageType.Video] = "video",
        [CanonicalMessageType.File] = "file",
        [CanonicalMessageType.Link] = "link",
        [CanonicalMessageType.AppShare] = "app_share",
        [CanonicalMessageType.MiniProgram] = "mini_program",
        [CanonicalMessageType.ForwardBundle] = "forward_bundle",
        [CanonicalMessageType.Location] = "location",
        [CanonicalMessageType.ContactCard] = "contact_card",
        [CanonicalMessageType.System] = "system",
        [CanonicalMessageType.Revoke] = "revoke",
        [CanonicalMessageType.RedPacket] = "red_packet",
        [CanonicalMessageType.Transfer] = "transfer",
        [CanonicalMessageType.Emoji] = "emoji",
        [CanonicalMessageType.Unknown] = "unknown",
    };

    /// <summary>The stable wire value used in JSONL and the archive.</summary>
    public static string ToWireName(this CanonicalMessageType type) =>
        WireNames.TryGetValue(type, out var name) ? name : "unknown";

    public static bool TryParse(string? wireName, out CanonicalMessageType type)
    {
        foreach (var (key, value) in WireNames)
        {
            if (string.Equals(value, wireName, StringComparison.Ordinal))
            {
                type = key;
                return true;
            }
        }

        type = CanonicalMessageType.Unknown;
        return false;
    }
}
