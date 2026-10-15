using WeArchive.Core.Domain;

namespace WeArchive.Tests.Support;

/// <summary>One raw WeChat 4.x wire record in the M2a semantic baseline corpus.</summary>
/// <param name="RecordId">Stable fixture id (also used as the upstream record identity).</param>
/// <param name="SourceConversationId">Synthetic upstream conversation id.</param>
/// <param name="SenderSourceUserId">
/// Upstream sender the shard reader would report (peer / real_sender_id), before the
/// parser's group-prefix hint is considered. Null when the record has no resolvable sender.
/// </param>
/// <param name="Type">Upstream <c>local_type</c> low 32 bits (classic type code).</param>
/// <param name="SubType">Upstream <c>local_type</c> high bits (app subtype).</param>
/// <param name="Content">Raw message content exactly as stored in the shard column.</param>
public sealed record SemanticBaselineRecord(
    string RecordId,
    string SourceConversationId,
    string? SenderSourceUserId,
    int Type,
    int SubType,
    string? Content);

/// <summary>
/// Deterministic, privacy-safe synthetic WeChat 4.x wire corpus for the M2a semantic
/// quality baseline (Issue #97, Roadmap M2a, PRD G3/G5, FR-07/19/24).
/// </summary>
/// <remarks>
/// <para>
/// Every record is written at the wire level (numeric type codes and XML payload exactly
/// as the message shard stores them) so the corpus measures the shipped parser/normalizer
/// rather than mirroring it. Positive fixtures cover every canonical MESSAGE_SCHEMA
/// section 4 type the parser supports; negative fixtures cover the representative
/// unknown/unsupported and field-unavailable outcomes that must stay explicit.
/// </para>
/// <para>
/// Privacy: all ids, names, texts, file names and URLs are synthetic placeholders
/// (<c>example.com</c>, 张三/李四-style role names already used by the repository docs
/// and fixtures). No real conversation content, archive, export, key or machine-private
/// path is committed (docs/DEVELOPMENT.md section 8, Issue #97 governance). The corpus is
/// deterministic: the same corpus version always yields the same baseline numbers.
/// </para>
/// <para>
/// Source-version sampling: the corpus is a single synthetic sample tagged with
/// <see cref="CorpusSourceVersion"/>; real-data profiling must group by the adapter-reported
/// upstream client version and emit aggregate metrics only (docs/SEMANTIC_QUALITY_BASELINE.md).
/// </para>
/// </remarks>
public static class SemanticBaselineCorpus
{
    public const string CorpusSourceVersion = "synthetic-wechat4x-m2a-1";
    public const string AdapterName = "semantic-baseline-corpus";
    public const string AdapterVersion = "1.0.0";
    public const string SourceProfileId = "baseline_account";

    public const string DirectConversation = "wxid_bob";
    public const string GroupConversation = "100200300@chatroom";
    public const string OfficialConversation = "gh_example_news";
    public const string SystemConversation = "filehelper";

    /// <summary>Participants the parser may strip a group prefix for.</summary>
    public static readonly string[] KnownUsers = ["wxid_alice", "wxid_bob", "wxid_carol"];

    public static IReadOnlyList<SemanticBaselineRecord> Records { get; } =
    [
        // ---- direct conversation (wxid_bob) ------------------------------------------
        new("text_direct", DirectConversation, "wxid_bob", 1, 0, "下午三点开会。"),
        new("voice_direct_timed", DirectConversation, "wxid_bob", 34, 0,
            """<msg><voicemsg voicelength="5722" voiceformat="4" fromusername="wxid_bob" /></msg>"""),
        new("voice_direct_untimed", DirectConversation, "wxid_bob", 34, 0,
            """<msg><voicemsg voiceformat="4" /></msg>"""),
        new("image_direct", DirectConversation, "wxid_bob", 3, 0, null),
        new("video_direct", DirectConversation, "wxid_bob", 43, 0, null),
        new("emoji_direct", DirectConversation, "wxid_bob", 47, 0, null),
        new("file_direct_named", DirectConversation, "wxid_bob", 49, 6,
            """<msg><appmsg appid="wx0000000000000001" sdkver="0"><title>项目季度汇报模板.docx</title><type>6</type><appattach><totallen>18120</totallen><fileext>docx</fileext></appattach></appmsg></msg>"""),
        new("file_direct_unnamed", DirectConversation, "wxid_bob", 49, 6,
            """<msg><appmsg><type>6</type><appattach><totallen>500</totallen><fileext>pdf</fileext></appattach></appmsg></msg>"""),
        new("link_direct_original", DirectConversation, "wxid_bob", 49, 5,
            """<msg><appmsg><title>人工智能产业发展报告</title><des>可获得的摘要</des><type>5</type><url>https://example.com/article/123</url></appmsg></msg>"""),
        new("link_direct_wrapper_only", DirectConversation, "wxid_bob", 49, 5,
            """<msg><appmsg><title>仅跳转页</title><type>5</type><url>https://support.weixin.qq.com/redirect?x=1</url></appmsg></msg>"""),
        new("appshare_unmodeled_subtype", DirectConversation, "wxid_bob", 49, 87,
            """<msg><appmsg><title>未建模分享卡片</title><des>卡片描述</des><type>87</type><url>https://example.com/card</url></appmsg></msg>"""),
        new("miniprogram_direct", DirectConversation, "wxid_bob", 49, 33,
            """<msg><appmsg><title>商品详情</title><des>XX商城</des><weappinfo><pagepath>pages/product?id=123</pagepath><appid>wx1234567890abcd</appid></weappinfo><sourcedisplayname>XX商城</sourcedisplayname><type>33</type></appmsg></msg>"""),
        new("quote_direct", DirectConversation, "wxid_bob", 49, 57,
            """<msg><appmsg><title>我同意这个方案</title><refermsg><svrid>954009715970562000</svrid><fromusr>wxid_bob</fromusr><displayname>张三</displayname><content>下午三点开会。</content><createtime>1776678109</createtime><type>1</type></refermsg><type>57</type></appmsg></msg>"""),
        new("location_direct", DirectConversation, "wxid_bob", 48, 0,
            """<msg><location poiname="南京南站" label="南京市雨花台区" x="31.968" y="118.796" address="南京市雨花台区示例路1号" /></msg>"""),
        new("contactcard_direct", DirectConversation, "wxid_bob", 42, 0,
            """<msg nickname="张三" alias="zs" username="wxid_new_contact" />"""),
        new("voip_direct", DirectConversation, "wxid_bob", 50, 0,
            """<msg><voipmsg><diaplay_content>语音通话 05:32</diaplay_content></voipmsg></msg>"""),
        new("redpacket_direct", DirectConversation, "wxid_bob", 49, 2000,
            """<msg><appmsg><title>收到一个红包</title><type>2000</type><wcpayinfo><paysubtype>1</paysubtype><feedesc>66.00</feedesc></wcpayinfo></appmsg></msg>"""),
        new("transfer_unclassified", DirectConversation, "wxid_bob", 49, 2001,
            """<msg><appmsg><title>转账给李四</title><type>2001</type><wcpayinfo><paysubtype>3</paysubtype><feedesc>200.00</feedesc></wcpayinfo></appmsg></msg>"""),
        new("unknown_classic_type", DirectConversation, "wxid_bob", 1000007, 0, "opaque-payload"),
        new("app_without_appmsg", DirectConversation, "wxid_bob", 49, 101,
            """<msg><title>占位信封</title></msg>"""),
        new("app_malformed_xml", DirectConversation, "wxid_bob", 49, 5,
            """<msg><appmsg><title>broken"""),

        // ---- group conversation (100200300@chatroom) ----------------------------------
        // Group prefixes use explicit \n escapes (not multi-line literals) so the corpus is
        // byte-identical under any checkout line-ending normalization.
        new("text_group_known_sender", GroupConversation, "wxid_dave", 1, 0, "wxid_alice:\n下午三点开会。"),
        new("text_group_unmatched_prefix", GroupConversation, "wxid_dave", 1, 0, "注意:\n下午三点开会。"),
        new("bundle_group_structured", GroupConversation, "wxid_alice", 49, 19,
            """<msg><appmsg><title>项目讨论聊天记录</title><des>张三: 第一版价格是不是太高了</des><type>19</type><recorditem><![CDATA[<recordinfo><title>项目讨论聊天记录</title><datalist count="3"><dataitem datatype="1" dataid="1"><datadesc>第一版价格是不是太高了？</datadesc><sourcename>张三</sourcename></dataitem><dataitem datatype="2" dataid="2"><datadesc>[图片]</datadesc><sourcename>李四</sourcename></dataitem><dataitem datatype="9" dataid="3"><datadesc>不支持类型的嵌套条目</datadesc><sourcename>王五</sourcename></dataitem></datalist></recordinfo>]]></recorditem></appmsg></msg>"""),
        new("bundle_group_description_fallback", GroupConversation, "wxid_alice", 49, 19,
            "<msg><appmsg><title>仅摘要转发的聊天记录</title><des>张三: 第一版价格是不是太高了\n李四: 收到</des><type>19</type><recorditem><![CDATA[recordlist-not-xml]]></recorditem></appmsg></msg>"),
        new("system_group_template", GroupConversation, null, 10000, 0,
            """<sysmsg type="sysmsgtemplate"><sysmsgtemplate><content_template><![CDATA["$username$"邀请"$invitee$"加入了群聊]]></content_template><link_list><link name="username"><title>张三</title></link><link name="invitee"><title>李四</title></link></link_list></sysmsgtemplate></sysmsg>"""),
        new("revoke_group", GroupConversation, null, 10000, 0,
            """<sysmsg type="revokemsg"><revokemsg session="wxid_alice"><newmsgid>954009715970562000</newmsgid><content>你撤回了一条消息</content><revoketime>1776678109</revoketime></revokemsg></sysmsg>"""),
        new("voice_group_untimed", GroupConversation, "wxid_carol", 34, 0, "wxid_carol:\n<msg><voicemsg voiceformat=\"4\" /></msg>"),

        // ---- official-account conversation (gh_example_news) --------------------------
        new("text_official_push", OfficialConversation, null, 1, 0, "Example 新闻推送占位文本"),
        new("link_official_article", OfficialConversation, null, 49, 5,
            """<msg><appmsg><title>Example 专栏文章</title><des>文章摘要</des><type>5</type><url>https://example.com/news/2026/10</url></appmsg></msg>"""),

        // ---- system conversation (filehelper) -----------------------------------------
        new("text_system_helper", SystemConversation, null, 1, 0, "文件传输助手占位消息"),
        new("system_plain_event", SystemConversation, null, 10002, 0,
            """<sysmsg type="pat"><text>戳了戳你</text></sysmsg>"""),
        new("unknown_classic_system", SystemConversation, null, 1050001, 0, "opaque-system-payload"),
    ];
}
