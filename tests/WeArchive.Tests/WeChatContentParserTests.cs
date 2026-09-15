using WeArchive.Core.Domain;
using WeArchive.Infrastructure.WeChat;
using WeArchive.Infrastructure.WeChat.Compatibility;
using WeArchive.Infrastructure.WeChat.Parsers;

namespace WeArchive.Tests;

/// <summary>
/// WeChat payload parsing: wire formats stay inside the adapter, and everything the local
/// record does not contain stays null. docs/MESSAGE_SCHEMA.md sections 8-18.
/// </summary>
public sealed class WeChatContentParserTests
{
    private static readonly string[] KnownUsers = ["wxid_alice", "wxid_bob", "100200300@chatroom"];

    private static ParsedWeChatContent Parse(int type, int subType, string? content, bool isGroup = true) =>
        WeChatContentParser.Parse(type, subType, content, isGroup, KnownUsers.Contains);

    [Fact]
    public void LocalTypeIsUnpackedIntoTypeAndSubType()
    {
        Assert.Equal((49, 57), WeChat4Schema.Unpack(57L << 32 | 49));
        Assert.Equal((1, 0), WeChat4Schema.Unpack(1));
        Assert.Equal((3, 0), WeChat4Schema.Unpack(3));
        Assert.Equal((49, 51), WeChat4Schema.Unpack(219043332145L));
    }

    [Fact]
    public void ConversationsAreClassifiedFromTheirUpstreamId()
    {
        Assert.Equal(ConversationKind.Group, WeChat4Schema.ClassifyConversation("100200300@chatroom"));
        Assert.Equal(ConversationKind.Official, WeChat4Schema.ClassifyConversation("gh_2c9f5861e119"));
        Assert.Equal(ConversationKind.Direct, WeChat4Schema.ClassifyConversation("wxid_alice"));
        Assert.Equal(ConversationKind.System, WeChat4Schema.ClassifyConversation("brandsessionholder"));
    }

    [Fact]
    public void GroupPrefixIsStrippedOnlyForKnownSenders()
    {
        var known = Parse(WeChat4Schema.TypeText, 0, "wxid_alice:\n下午三点开会。");
        Assert.Equal("wxid_alice", known.SenderHint);
        Assert.Equal("下午三点开会。", known.Content.Text);

        // Not a real participant: the text must be preserved verbatim.
        var unknown = Parse(WeChat4Schema.TypeText, 0, "注意:\n下午三点开会。");
        Assert.Null(unknown.SenderHint);
        Assert.Equal("注意:\n下午三点开会。", unknown.Content.Text);

        // Direct conversations never carry the prefix.
        var direct = Parse(WeChat4Schema.TypeText, 0, "wxid_alice:\nhello", isGroup: false);
        Assert.Null(direct.SenderHint);
    }

    [Fact]
    public void VoiceDurationIsConvertedFromMilliseconds()
    {
        var parsed = Parse(
            WeChat4Schema.TypeVoice,
            0,
            """<msg><voicemsg voicelength="5722" voiceformat="4" fromusername="wxid_alice" /></msg>""");

        Assert.Equal(SourceContentKind.Voice, parsed.Content.Kind);
        Assert.Equal(6, parsed.Content.DurationSeconds);
    }

    [Fact]
    public void VoiceWithoutALengthStaysUnknownInsteadOfBeingEstimated()
    {
        var parsed = Parse(WeChat4Schema.TypeVoice, 0, """<msg><voicemsg voiceformat="4" /></msg>""");

        Assert.Null(parsed.Content.DurationSeconds);
        Assert.True(parsed.Content.IsPartial);
    }

    [Fact]
    public void FileMessageKeepsTheOriginalName()
    {
        var parsed = Parse(
            WeChat4Schema.TypeApp,
            WeChat4Schema.AppFile,
            """
            <msg><appmsg appid="wx6618f1cfc6c132f8" sdkver="0"><title>2026年千里眼APaaS研发规划.docx</title>
            <type>6</type><appattach><totallen>18120</totallen><fileext>docx</fileext></appattach></appmsg></msg>
            """);

        Assert.Equal(SourceContentKind.File, parsed.Content.Kind);
        Assert.Equal("2026年千里眼APaaS研发规划.docx", parsed.Content.FileName);
        Assert.Equal("docx", parsed.Content.FileExtension);
        Assert.Equal(18120, parsed.Content.FileSizeBytes);
    }

    [Fact]
    public void LinkKeepsTitleDescriptionAndOriginalUrl()
    {
        var parsed = Parse(
            WeChat4Schema.TypeApp,
            WeChat4Schema.AppUrl,
            """
            <msg><appmsg><title>Goyard Hardy太适合出差</title><des>日本价格也好</des>
            <type>5</type><url>https://example.com/article/123</url></appmsg></msg>
            """);

        Assert.Equal(SourceContentKind.Link, parsed.Content.Kind);
        Assert.Equal("https://example.com/article/123", parsed.Content.OriginalUrl);
        Assert.Null(parsed.Content.FallbackUrl);
    }

    [Theory]
    [InlineData("https://support.weixin.qq.com/cgi-bin/mmsupport-bin/readtemplate?t=page/favorite_record")]
    [InlineData("https://mp.weixin.qq.com/mp/waerrpage?appid=wx9c96d694a75e4de1&type=upgrade")]
    [InlineData("https://weixin.qq.com/redirect?k=9")]
    public void WrapperUrlsAreRecognised(string url)
    {
        Assert.True(WeChatContentParser.IsWrapperUrl(url));
    }

    [Fact]
    public void WrapperOnlyAppShareNeverClaimsAnOriginalUrl()
    {
        var parsed = Parse(
            WeChat4Schema.TypeApp,
            WeChat4Schema.AppUrl,
            """
            <msg><appmsg><title>仅跳转</title><type>5</type>
            <url>https://support.weixin.qq.com/redirect?x=1</url></appmsg></msg>
            """);

        Assert.Null(parsed.Content.OriginalUrl);
        Assert.Equal("https://support.weixin.qq.com/redirect?x=1", parsed.Content.FallbackUrl);
    }

    [Fact]
    public void MiniProgramIsDetectedFromItsAppInfo()
    {
        var parsed = Parse(
            WeChat4Schema.TypeApp,
            WeChat4Schema.AppMiniProgram,
            """
            <msg><appmsg><title>商品详情</title><des>XX商城</des>
            <weappinfo><pagepath>pages/product?id=123</pagepath><appid>wx123456</appid>
            <username>gh_846bc6fe28be@app</username></weappinfo>
            <sourcedisplayname>XX商城</sourcedisplayname><type>33</type></appmsg></msg>
            """);

        Assert.Equal(SourceContentKind.MiniProgram, parsed.Content.Kind);
        Assert.Equal("pages/product?id=123", parsed.Content.PagePath);
        Assert.Equal("wx123456", parsed.Content.AppId);
        Assert.Equal("XX商城", parsed.Content.SourceApp);
    }

    [Fact]
    public void QuoteBecomesATextMessageWithAStructuredReply()
    {
        var parsed = Parse(
            WeChat4Schema.TypeApp,
            WeChat4Schema.AppQuote,
            """
            <msg><appmsg><title>我同意这个方案</title><refermsg>
            <svrid>954009715970562098</svrid><fromusr>wxid_bob</fromusr>
            <displayname>Kevin</displayname><content>下午三点开会。</content>
            <createtime>1776678109</createtime><type>1</type></refermsg>
            <type>57</type></appmsg></msg>
            """);

        Assert.Equal(SourceContentKind.Text, parsed.Content.Kind);
        Assert.Equal("我同意这个方案", parsed.Content.Text);

        Assert.NotNull(parsed.Reply);
        Assert.Equal("954009715970562098", parsed.Reply!.SourceMessageId);
        Assert.Equal("wxid_bob", parsed.Reply.SenderSourceId);
        Assert.Equal("Kevin", parsed.Reply.SenderName);
        Assert.Equal("下午三点开会。", parsed.Reply.Text);
    }

    [Fact]
    public void ForwardBundleKeepsNestedItemsWithoutInventingSenders()
    {
        var parsed = Parse(
            WeChat4Schema.TypeApp,
            WeChat4Schema.AppForwardBundle,
            """
            <msg><appmsg><title>项目讨论聊天记录</title><des>张三: 第一版价格太高</des>
            <type>19</type>
            <recorditem><![CDATA[<recordinfo><title>项目讨论聊天记录</title>
            <datalist count="2">
            <dataitem datatype="1" dataid="1"><datadesc>第一版价格是不是太高了？</datadesc><sourcename>张三</sourcename></dataitem>
            <dataitem datatype="2" dataid="2"><datadesc>[图片]</datadesc><sourcename>李四</sourcename></dataitem>
            </datalist></recordinfo>]]></recorditem>
            </appmsg></msg>
            """);

        Assert.Equal(SourceContentKind.ForwardBundle, parsed.Content.Kind);
        Assert.Equal(2, parsed.Content.ForwardItemCount);
        Assert.Equal(2, parsed.Content.ForwardItems!.Count);
        Assert.Equal("张三", parsed.Content.ForwardItems[0].SenderName);
        Assert.Null(parsed.Content.ForwardItems[0].SenderId);
        Assert.Equal(CanonicalMessageType.Text, parsed.Content.ForwardItems[0].Type);
        Assert.Equal(CanonicalMessageType.Image, parsed.Content.ForwardItems[1].Type);
    }

    [Fact]
    public void RevokeMessagesBecomeRevokeEvents()
    {
        var parsed = Parse(
            WeChat4Schema.TypeSystem,
            0,
            """
            <?xml version="1.0"?><sysmsg type="revokemsg"><revokemsg>
            <content>你撤回了一条消息</content><revoketime>1757323696</revoketime></revokemsg></sysmsg>
            """);

        Assert.Equal(SourceContentKind.Revoke, parsed.Content.Kind);
        Assert.Equal("你撤回了一条消息", parsed.Content.SystemText);
    }

    [Fact]
    public void GroupNoticesResolveTheirNameTemplate()
    {
        var parsed = Parse(
            WeChat4Schema.TypeSystem,
            0,
            """
            <sysmsg type="sysmsgtemplate"><sysmsgtemplate>
            <content_template><![CDATA["$username$"邀请"$invitee$"加入了群聊]]></content_template>
            <link_list>
            <link name="username"><title>张三</title></link>
            <link name="invitee"><title>李四</title></link>
            </link_list></sysmsgtemplate></sysmsg>
            """);

        Assert.Equal(SourceContentKind.System, parsed.Content.Kind);
        Assert.Equal("\"张三\"邀请\"李四\"加入了群聊", parsed.Content.SystemText);
    }

    [Fact]
    public void AnUnrecognisedAppMessageBecomesUnknownWithItsCodesPreserved()
    {
        var parsed = Parse(WeChat4Schema.TypeApp, 87, "<msg><appmsg><type>87</type></appmsg></msg>");

        Assert.Equal(SourceContentKind.Unknown, parsed.Content.Kind);
        Assert.True(parsed.Content.IsPartial);
    }

    [Fact]
    public void AnUnrecognisedClassicTypeBecomesUnknownRatherThanBeingDropped()
    {
        var parsed = Parse(1000007, 0, "opaque-payload");

        Assert.Equal(SourceContentKind.Unknown, parsed.Content.Kind);
        Assert.Equal(DiagnosticCodes.UnknownMessageType, parsed.DiagnosticCode);
    }

    [Fact]
    public void MalformedXmlYieldsAPartialRecordNotAnException()
    {
        var parsed = Parse(WeChat4Schema.TypeApp, WeChat4Schema.AppUrl, "<msg><appmsg><title>broken");

        Assert.Equal(SourceContentKind.Unknown, parsed.Content.Kind);
        Assert.True(parsed.Content.IsPartial);
    }

    [Fact]
    public void CompressedPayloadsAreDecodedAndFailuresAreReported()
    {
        var raw = System.Text.Encoding.UTF8.GetBytes("下午三点开会。");
        using var compressor = new ZstdSharp.Compressor();
        var compressed = compressor.Wrap(raw).ToArray();

        Assert.True(WeChatContentDecoder.TryDecode(compressed, 4, out var text, out var failure));
        Assert.Null(failure);
        Assert.Equal("下午三点开会。", text);

        Assert.False(WeChatContentDecoder.TryDecode([0x01, 0x02, 0x03], 4, out _, out var badFailure));
        Assert.Equal(DiagnosticCodes.ContentDecompressionFailed, badFailure);

        Assert.True(WeChatContentDecoder.TryDecode(raw, 0, out var plain, out _));
        Assert.Equal("下午三点开会。", plain);
    }
}
