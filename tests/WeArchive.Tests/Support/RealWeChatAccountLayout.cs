namespace WeArchive.Tests.Support;

/// <summary>
/// The source-partition layout observed on the real WeChat 4.x account that exposed Issue #37:
/// 25 databases under <c>db_storage</c>, of which 24 were captureable and
/// <c>migrate/unspportmsg.db</c> had no verifiable key.
/// <para>
/// Only relative partition paths are recorded here. No chat content, account identifier or
/// database key is part of this fixture data.
/// </para>
/// </summary>
internal static class RealWeChatAccountLayout
{
    /// <summary>The 25 <c>db_storage</c>-relative partition ids, in sorted order.</summary>
    public static readonly string[] Partitions =
    [
        "bizchat/bizchat.db",
        "chatbot/chatbot_message.db",
        "contact/contact_fts.db",
        "contact/contact.db",
        "emoticon/emoticon.db",
        "favorite/favorite_fts.db",
        "favorite/favorite.db",
        "general/general.db",
        "hardlink/hardlink.db",
        "head_image/head_image.db",
        "message/biz_message_0.db",
        "message/biz_message_1.db",
        "message/media_0.db",
        "message/media_1.db",
        "message/message_0.db",
        "message/message_1.db",
        "message/message_2.db",
        "message/message_fts.db",
        "message/message_resource.db",
        "message/weclaw.db",
        "migrate/unspportmsg.db",
        "session/session.db",
        "sns/sns.db",
        "solitaire/solitaire.db",
        "third_app_icon/third_app_icon.db",
    ];
}