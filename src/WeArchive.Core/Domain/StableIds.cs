using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WeArchive.Core.Domain;

/// <summary>
/// Deterministic stable-ID derivation. docs/DATA_MODEL.md section 16.
/// <para>
/// IDs are derived <em>only</em> from stable upstream identifiers plus a namespace
/// scope. Mutable human names (remarks, nicknames, group titles) are never inputs,
/// which is what makes physical export paths survive renames.
/// </para>
/// <para>
/// The digest prefix is 16 hex characters (64 bits) rather than the 8 characters
/// shown in the illustrative docs examples: a 32-bit prefix would suffer a
/// measurable rate of accidental collisions across a large message archive.
/// See docs/DATA_MODEL.md section 16.1.
/// </para>
/// </summary>
public static class StableIds
{
    private const int HexLength = 16;

    public static string Account(string adapterName, string sourceProfileId) =>
        "a_" + Digest("account", adapterName, sourceProfileId);

    /// <summary>Stable identity for a person (friend or group member).</summary>
    public static string Participant(string accountId, string sourceUserId) =>
        "u_" + Digest("user", accountId, sourceUserId);

    /// <summary>A direct conversation's stable ID is the peer's stable identity.</summary>
    public static string DirectConversation(string accountId, string peerSourceUserId) =>
        Participant(accountId, peerSourceUserId);

    public static string GroupConversation(string accountId, string sourceRoomId) =>
        "g_" + Digest("group", accountId, sourceRoomId);

    /// <summary>
    /// Stable message ID.
    /// <paramref name="sourceMessageId"/> must be stable for the upstream record;
    /// when upstream has no stable ID the adapter supplies a documented composite
    /// (partition + local ordering key) instead of a content hash, because repeated
    /// identical messages are valid data. docs/DATA_MODEL.md section 17.
    /// </summary>
    public static string Message(string conversationId, string sourceMessageId) =>
        "m_" + Digest("message", conversationId, sourceMessageId);

    private static string Digest(params string[] parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            builder.Append(part.Length.ToString(CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(part);
            builder.Append('|');
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes.AsSpan(0, HexLength / 2)).ToLowerInvariant();
    }
}
