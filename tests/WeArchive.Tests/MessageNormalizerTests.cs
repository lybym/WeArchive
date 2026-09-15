using System.Text.Json.Nodes;
using WeArchive.Core.Domain;
using WeArchive.Core.Normalization;

namespace WeArchive.Tests;

/// <summary>
/// Message normalization is normative in docs/MESSAGE_SCHEMA.md. These tests pin the
/// envelope, the semantic text and the type-specific payload for every canonical type.
/// </summary>
public sealed class MessageNormalizerTests
{
    private static readonly DateTimeOffset When = new(2026, 1, 15, 9, 0, 0, TimeSpan.FromHours(8));

    private static NormalizationContext Context() => new()
    {
        AccountId = "a_test",
        ConversationId = "g_test",
        SourceProfileId = "acct",
        AdapterName = "fixture",
        AdapterVersion = "1.0.0",
        SourceVersion = "fixture-1",
        ImportRunId = "run_1",
        ResolveParticipantId = sourceId => StableIds.Participant("a_test", sourceId),
    };

    private static CanonicalMessage Normalize(SourceMessageContent content, string? type = "1", string? subType = null) =>
        MessageNormalizer.Normalize(
            new SourceMessage
            {
                SourceConversationId = "100200300@chatroom",
                SenderSourceUserId = "wxid_alice",
                OccurredAt = When,
                SourceType = type,
                SourceSubtype = subType,
                SourcePartition = "message_0",
                SourceMessageId = "s:42",
                SourceOrderKey = "7",
                Content = content,
            },
            Context());

    [Fact]
    public void EnvelopeCarriesTheCanonicalLayers()
    {
        var message = Normalize(SourceMessageContent.PlainText("下午三点开会。"));

        Assert.Equal(CanonicalMessageType.Text, message.Type);
        Assert.Equal("下午三点开会。", message.Text);
        Assert.Equal(StableIds.Participant("a_test", "wxid_alice"), message.SenderId);
        Assert.Equal(When, message.OccurredAt);
        Assert.Null(message.Payload);
        Assert.Null(message.ReplyTo);
        Assert.Equal("s:42", message.Source.SourceMessageId);
        Assert.Equal("message_0", message.Source.SourcePartition);
        Assert.Equal("1", message.Source.SourceType);
        Assert.Equal("run_1", message.Source.ImportRunId);
    }

    [Fact]
    public void MessageIdIsStableForTheSameSourceRecord()
    {
        Assert.Equal(Normalize(SourceMessageContent.PlainText("x")).Id, Normalize(SourceMessageContent.PlainText("x")).Id);
    }

    [Theory]
    [InlineData(SourceContentKind.Image, "image", "[图片]")]
    [InlineData(SourceContentKind.Video, "video", "[视频]")]
    [InlineData(SourceContentKind.Emoji, "emoji", "[表情]")]
    [InlineData(SourceContentKind.RedPacket, "red_packet", "[红包]")]
    public void MediaEventsRenderAsTextWithoutBinaries(SourceContentKind kind, string wire, string expected)
    {
        var message = Normalize(new SourceMessageContent { Kind = kind });

        Assert.Equal(wire, message.Type.ToWireName());
        Assert.Equal(expected, message.Text);
        Assert.Null(message.Payload);
    }

    [Fact]
    public void VoiceKeepsDurationOnlyWhenReliable()
    {
        var withDuration = Normalize(new SourceMessageContent { Kind = SourceContentKind.Voice, DurationSeconds = 37 });
        Assert.Equal(37, withDuration.Payload!["duration_seconds"]!.GetValue<int>());

        var without = Normalize(new SourceMessageContent { Kind = SourceContentKind.Voice, IsPartial = true });
        Assert.True(without.Payload!["duration_seconds"] is null);
        Assert.True(without.IsPartial);
    }

    [Fact]
    public void FileKeepsTheOriginalNameAndNeverInventsOne()
    {
        var named = Normalize(new SourceMessageContent
        {
            Kind = SourceContentKind.File,
            FileName = "华东中心项目汇报V8.pptx",
            FileExtension = ".pptx",
            FileSizeBytes = 1_839_201,
        });

        Assert.Equal("file", named.Type.ToWireName());
        Assert.Equal("[文件] 华东中心项目汇报V8.pptx", named.Text);
        Assert.Equal("华东中心项目汇报V8.pptx", named.Payload!["filename"]!.GetValue<string>());

        var unnamed = Normalize(new SourceMessageContent { Kind = SourceContentKind.File });
        Assert.Equal("[文件]", unnamed.Text);
        Assert.True(unnamed.Payload!["filename"] is null);
    }

    [Fact]
    public void LinkExposesTitleDescriptionAndOriginalUrlStructurally()
    {
        var message = Normalize(new SourceMessageContent
        {
            Kind = SourceContentKind.Link,
            Title = "AI产业发展报告",
            Description = "可获得的摘要",
            OriginalUrl = "https://example.com/article",
        }, "49", "5");

        Assert.Equal(CanonicalMessageType.Link, message.Type);
        Assert.Equal("[链接] AI产业发展报告\nhttps://example.com/article", message.Text);
        Assert.Equal("https://example.com/article", message.Payload!["original_url"]!.GetValue<string>());
        Assert.True(message.Payload["fallback_url"] is null);
    }

    [Fact]
    public void WrapperOnlyLinkNeverBecomesAnOriginalUrl()
    {
        var message = Normalize(new SourceMessageContent
        {
            Kind = SourceContentKind.Link,
            Title = "仅跳转",
            FallbackUrl = "https://support.weixin.qq.com/redirect?x=1",
        }, "49", "5");

        Assert.True(message.Payload!["original_url"] is null);
        Assert.Equal("https://support.weixin.qq.com/redirect?x=1", message.Payload["fallback_url"]!.GetValue<string>());
    }

    [Fact]
    public void AppShareKeepsTheSourceApplication()
    {
        var message = Normalize(new SourceMessageContent
        {
            Kind = SourceContentKind.AppShare,
            SourceApp = "小红书",
            Title = "上海周末去哪玩",
            OriginalUrl = "https://example.com/note",
            AppId = "wx123",
        }, "49", "5");

        Assert.Equal(CanonicalMessageType.AppShare, message.Type);
        Assert.StartsWith("[APP分享][小红书] 上海周末去哪玩", message.Text, StringComparison.Ordinal);
        Assert.Equal("小红书", message.Payload!["source_app"]!.GetValue<string>());
        Assert.Equal("wx123", message.Payload["app_id"]!.GetValue<string>());
    }

    [Fact]
    public void MiniProgramIsModelledSeparatelyFromLinks()
    {
        var message = Normalize(new SourceMessageContent
        {
            Kind = SourceContentKind.MiniProgram,
            SourceApp = "XX商城",
            Title = "商品详情",
            AppId = "wx123456",
            PagePath = "pages/product?id=123",
        }, "49", "33");

        Assert.Equal(CanonicalMessageType.MiniProgram, message.Type);
        Assert.Equal("[小程序] XX商城 - 商品详情", message.Text);
        Assert.Equal("pages/product?id=123", message.Payload!["page_path"]!.GetValue<string>());
    }

    [Fact]
    public void ReplyKeepsRelationshipAndSnapshotWithoutInventingATargetId()
    {
        var message = MessageNormalizer.Normalize(
            new SourceMessage
            {
                SourceConversationId = "100200300@chatroom",
                SenderSourceUserId = "wxid_alice",
                OccurredAt = When,
                SourceType = "49",
                SourceMessageId = "s:100",
                Content = new SourceMessageContent { Kind = SourceContentKind.Text, Text = "我同意这个方案。" },
                Reply = new SourceReplySnapshot
                {
                    SourceMessageId = "999",
                    SenderSourceId = "wxid_bob",
                    SenderName = "Kevin",
                    Text = "下午三点开会。",
                },
            },
            Context());

        Assert.NotNull(message.ReplyTo);
        Assert.Null(message.ReplyTo!.MessageId);
        Assert.Equal(StableIds.Participant("a_test", "wxid_bob"), message.ReplyTo.SenderId);
        Assert.Equal("下午三点开会。", message.ReplyTo.Text);
        Assert.Equal("text", message.Type.ToWireName());
    }

    [Fact]
    public void ForwardBundleKeepsNestedItemsAndSenderNames()
    {
        var message = Normalize(new SourceMessageContent
        {
            Kind = SourceContentKind.ForwardBundle,
            Title = "项目讨论聊天记录",
            ForwardItemCount = 12,
            ForwardItems =
            [
                new SourceForwardItem
                {
                    SenderName = "张三",
                    Type = CanonicalMessageType.Text,
                    Text = "第一版价格是不是太高了？",
                    OccurredAt = When,
                },
            ],
        }, "49", "19");

        Assert.Equal(CanonicalMessageType.ForwardBundle, message.Type);
        Assert.Equal("[合并转发] 项目讨论聊天记录，共 12 条", message.Text);

        var items = Assert.IsType<JsonArray>(message.Payload!["items"]);
        Assert.Single(items);
        var item = Assert.IsType<JsonObject>(items[0]);
        Assert.Equal("张三", item["sender_name"]!.GetValue<string>());
        Assert.True(item["sender_id"] is null);
        Assert.Equal("第一版价格是不是太高了？", item["text"]!.GetValue<string>());
    }

    [Fact]
    public void UnknownRecordsArePreservedWithSourceTypeCodes()
    {
        var message = Normalize(SourceMessageContent.Unparsed(null), "1000007", "3");

        Assert.Equal(CanonicalMessageType.Unknown, message.Type);
        Assert.Equal("[未识别消息]", message.Text);
        Assert.Equal("1000007", message.Payload!["source_type"]!.GetValue<string>());
        Assert.Equal("3", message.Payload["source_subtype"]!.GetValue<string>());
        Assert.True(message.IsPartial);
    }

    [Fact]
    public void SystemEventKeepsEventAndParticipants()
    {
        var message = Normalize(new SourceMessageContent
        {
            Kind = SourceContentKind.System,
            SystemText = "张三邀请李四加入了群聊",
            SystemEvent = "member_join",
            ActorSourceIds = ["wxid_alice"],
            TargetSourceIds = ["wxid_carol"],
        }, "10000");

        Assert.Equal(CanonicalMessageType.System, message.Type);
        Assert.Equal("张三邀请李四加入了群聊", message.Text);
        Assert.Equal("member_join", message.Payload!["event"]!.GetValue<string>());
        Assert.Equal(StableIds.Participant("a_test", "wxid_alice"), message.Payload["actor_ids"]![0]!.GetValue<string>());
    }

    [Fact]
    public void RevokeKeepsOperatorAndNoInventedContent()
    {
        var message = Normalize(new SourceMessageContent
        {
            Kind = SourceContentKind.Revoke,
            SystemText = "张三撤回了一条消息",
            SystemEvent = "revokemsg",
            OperatorSourceId = "wxid_alice",
        }, "10000");

        Assert.Equal(CanonicalMessageType.Revoke, message.Type);
        Assert.Equal("[撤回消息] 张三撤回了一条消息", message.Text);
        Assert.Equal(StableIds.Participant("a_test", "wxid_alice"), message.Payload!["operator_id"]!.GetValue<string>());
        Assert.True(message.Payload["revoked_text"] is null);
    }

    [Fact]
    public void ARecordWithoutAStableSourceIdIsAContractViolation()
    {
        var source = new SourceMessage
        {
            SourceConversationId = "x",
            OccurredAt = When,
            Content = SourceMessageContent.PlainText("x"),
        };

        Assert.Throws<ArgumentException>(() => MessageNormalizer.Normalize(source, Context()));
    }

    [Fact]
    public void SystemSenderSentinelProducesNoFabricatedIdentity()
    {
        var message = MessageNormalizer.Normalize(
            new SourceMessage
            {
                SourceConversationId = "100200300@chatroom",
                SenderSourceUserId = SourceSenderIds.System,
                OccurredAt = When,
                SourceMessageId = "s:1",
                Content = SourceMessageContent.PlainText("x"),
            },
            Context());

        Assert.Null(message.SenderId);
    }
}
