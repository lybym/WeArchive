using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WeArchive.Core.Domain;

namespace WeArchive.Infrastructure.WeChat.Compatibility;

/// <summary>
/// Every WeChat-4.x-specific table name, column name and numeric type code lives here.
/// <para>
/// This is the compatibility boundary required by docs/ARCHITECTURE.md section 3.3 and
/// docs/PRD.md NFR-06: the core domain, archive and exporter must never learn these
/// values, and a future client version only needs a sibling type in this namespace.
/// </para>
/// </summary>
internal static class WeChat4Schema
{
    // ---- message shard tables -------------------------------------------------------
    // One table per conversation, named after the MD5 of the upstream conversation id.
    public const string MessageTablePrefix = "Msg_";

    // ---- columns --------------------------------------------------------------------
    public const string LocalId = "local_id";
    public const string ServerId = "server_id";
    public const string LocalType = "local_type";
    public const string SortSeq = "sort_seq";
    public const string RealSenderId = "real_sender_id";
    public const string CreateTime = "create_time";
    public const string MessageContent = "message_content";
    public const string CompressContent = "compress_content";
    public const string ContentCompression = "WCDB_CT_message_content";
    public const string Source = "source";
    public const string SourceCompression = "WCDB_CT_source";

    /// <summary>Row-id to conversation/user-name map inside a message shard.</summary>
    public const string Name2IdTable = "Name2Id";

    // ---- contact database -----------------------------------------------------------
    public const string ContactTable = "contact";
    public const string ChatRoomTable = "chat_room";
    public const string ChatRoomMemberTable = "chatroom_member";

    // ---- session database -----------------------------------------------------------
    public const string SessionTable = "SessionTable";

    // ---- type packing ---------------------------------------------------------------
    /// <summary>
    /// <c>local_type</c> packs the classic WeChat type in the low 32 bits and the app
    /// subtype in the high bits: <c>(subtype &lt;&lt; 32) | type</c>.
    /// </summary>
    public static (int Type, int SubType) Unpack(long localType) =>
        ((int)(localType & 0xFFFFFFFFL), (int)(localType >> 32));

    // ---- classic message types ------------------------------------------------------
    public const int TypeText = 1;
    public const int TypeImage = 3;
    public const int TypeVoice = 34;
    public const int TypeContactCard = 42;
    public const int TypeVideo = 43;
    public const int TypeEmoji = 47;
    public const int TypeLocation = 48;
    public const int TypeApp = 49;
    public const int TypeVoip = 50;
    public const int TypeSystem = 10000;
    public const int TypeSystemExtended = 10002;

    // ---- app message (type 49) subtypes --------------------------------------------
    public const int AppText = 1;
    public const int AppImage = 2;
    public const int AppMusic = 3;
    public const int AppVideoLink = 4;
    public const int AppUrl = 5;
    public const int AppFile = 6;
    public const int AppEmoji = 8;
    public const int AppLocation = 17;
    public const int AppForwardBundle = 19;
    public const int AppNote = 24;
    public const int AppMiniProgram = 33;
    public const int AppMiniProgramAlt = 36;
    public const int AppQuote = 57;
    public const int AppChannels = 51;
    public const int AppLive = 63;
    public const int AppRedPacket = 2000;
    public const int AppTransfer = 2001;

    // ---- markers --------------------------------------------------------------------
    public const string WeChatRoomSuffix = "@chatroom";
    public const string OfficialAccountPrefix = "gh_";
    public const string OpenImSuffix = "@openim";
    public const string FinderSuffix = "@finder";
    public const string AppSuffix = "@app";

    /// <summary>Conversation shard table name for an upstream conversation id.</summary>
    public static string MessageTableName(string sourceConversationId) =>
        MessageTablePrefix + Md5Hex(sourceConversationId);

    public static string Md5Hex(string value) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>
    /// Classifies a conversation by its upstream user name. Names that are internal
    /// placeholders are reported as system conversations rather than being mislabelled
    /// as one-to-one chats.
    /// </summary>
    public static ConversationKind ClassifyConversation(string sourceConversationId)
    {
        if (sourceConversationId.EndsWith(WeChatRoomSuffix, StringComparison.Ordinal))
        {
            return ConversationKind.Group;
        }

        if (sourceConversationId.StartsWith(OfficialAccountPrefix, StringComparison.Ordinal)
            || sourceConversationId.EndsWith(AppSuffix, StringComparison.Ordinal))
        {
            return ConversationKind.Official;
        }

        if (sourceConversationId.Contains("sessionholder", StringComparison.Ordinal)
            || sourceConversationId.StartsWith("weixin", StringComparison.Ordinal)
            || sourceConversationId.StartsWith("floatbottle", StringComparison.Ordinal)
            || sourceConversationId.StartsWith("newsapp", StringComparison.Ordinal)
            || sourceConversationId.StartsWith("filehelper", StringComparison.Ordinal)
            || sourceConversationId.StartsWith("notifymessage", StringComparison.Ordinal)
            || sourceConversationId.StartsWith("masssendapp", StringComparison.Ordinal)
            || sourceConversationId.StartsWith("medianote", StringComparison.Ordinal)
            || sourceConversationId.EndsWith(FinderSuffix, StringComparison.Ordinal))
        {
            return ConversationKind.System;
        }

        return ConversationKind.Direct;
    }

    public static string FormatUnixTime(long seconds) =>
        DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("O", CultureInfo.InvariantCulture);
}

