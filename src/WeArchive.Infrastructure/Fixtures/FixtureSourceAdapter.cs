using System.Runtime.CompilerServices;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;

namespace WeArchive.Infrastructure.Fixtures;

/// <summary>
/// A deterministic synthetic source used to exercise the whole pipeline without a live
/// client. docs/ROADMAP.md M0 and docs/DEVELOPMENT.md section 7.
/// <para>
/// The fixture deliberately covers the edge cases the product must not lose: every
/// canonical message type, a group and a direct conversation, a reply whose target is
/// absent, repeated identical text, an unparsed record, a file with and without a name,
/// a wrapper-only URL, and messages spread across several months so monthly partitioning
/// is exercised.
/// </para>
/// <para>
/// All names, ids and messages are synthetic. No real conversation data is committed.
/// </para>
/// </summary>
public sealed class FixtureSourceAdapter : ISourceAdapter
{
    public const string FixtureAccountId = "fixture_account";

    public const string Alice = "wxid_alice";
    public const string Bob = "wxid_bob";
    public const string Carol = "wxid_carol";
    public const string GroupRoom = "100200300@chatroom";

    public const string DirectConversation = "wxid_alice";
    public const string GroupConversation = GroupRoom;

    /// <summary>Timestamps are fixed so that exports are byte-for-byte reproducible.</summary>
    public static readonly DateTimeOffset Base = new(2026, 1, 15, 9, 0, 0, TimeSpan.FromHours(8));

    public string AdapterName => "fixture";

    public string AdapterVersion => "1.0.0";

    public Task<SourceDescriptor> DescribeSourceAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SourceDescriptor
        {
            AdapterName = AdapterName,
            AdapterVersion = AdapterVersion,
            SourceVersion = "fixture-1",
            SourceProductName = "WeArchive fixture source",
            IsAvailable = true,
        });

    public Task<IReadOnlyList<SourceAccount>> ListAccountsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SourceAccount>>(
        [
            new SourceAccount
            {
                SourceProfileId = FixtureAccountId,
                DisplayName = "Fixture account",
                IsCurrent = true,
                LastActiveAt = Base,
            },
        ]);

    public Task<IReadOnlyList<SourceConversation>> ListConversationsAsync(
        string sourceProfileId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SourceConversation>>(
        [
            new SourceConversation
            {
                SourceConversationId = GroupConversation,
                Kind = ConversationKind.Group,
                Title = "华东产品创新中心工作群",
                LastMessageAt = Base.AddDays(40),
            },
            new SourceConversation
            {
                SourceConversationId = DirectConversation,
                Kind = ConversationKind.Direct,
                Title = "张三",
                PeerSourceUserId = Alice,
                LastMessageAt = Base.AddDays(5),
            },
        ]);

    public Task<SourceConversationDetail> DescribeConversationAsync(
        string sourceProfileId,
        string sourceConversationId,
        CancellationToken cancellationToken)
    {
        var messages = BuildMessages(sourceConversationId).ToList();
        return Task.FromResult(new SourceConversationDetail
        {
            SourceConversationId = sourceConversationId,
            MessageCount = messages.Count,
            FirstMessageAt = messages.Count == 0 ? null : messages.Min(m => m.OccurredAt),
            LastMessageAt = messages.Count == 0 ? null : messages.Max(m => m.OccurredAt),
            ParticipantCount = 3,
        });
    }

    public Task<IReadOnlyList<SourceParticipant>> ListParticipantsAsync(
        string sourceProfileId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SourceParticipant>>(
        [
            new SourceParticipant { SourceUserId = Alice, Remark = "张三", Nickname = "三哥", Alias = "zhang-san" },
            new SourceParticipant { SourceUserId = Bob, Remark = null, Nickname = "Kevin", Alias = null },
            new SourceParticipant { SourceUserId = Carol, Remark = "李四", Nickname = "四儿", Alias = null },
            new SourceParticipant { SourceUserId = FixtureAccountId, Remark = null, Nickname = "Me", Alias = null },
        ]);

    public async IAsyncEnumerable<SourceMessage> ReadMessagesAsync(
        string sourceProfileId,
        string sourceConversationId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var message in BuildMessages(sourceConversationId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return message;
            await Task.Yield();
        }
    }

    public static IReadOnlyList<SourceMessage> BuildMessages(string sourceConversationId) =>
        string.Equals(sourceConversationId, GroupConversation, StringComparison.Ordinal)
            ? BuildGroupMessages()
            : BuildDirectMessages();

    private static List<SourceMessage> BuildGroupMessages()
    {
        var messages = new List<SourceMessage>();
        var index = 0;

        SourceMessage Add(
            string sender,
            DateTimeOffset at,
            SourceMessageContent content,
            string? type,
            string? subType = null,
            SourceReplySnapshot? reply = null)
        {
            index++;
            messages.Add(new SourceMessage
            {
                SourceConversationId = GroupConversation,
                SenderSourceUserId = sender,
                OccurredAt = at,
                SourceType = type,
                SourceSubtype = subType,
                SourcePartition = "fixture_0",
                SourceMessageId = $"l:fixture_0:{index}",
                SourceOrderKey = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Content = content,
                Reply = reply,
            });
            return messages[^1];
        }

        // January 2026 -------------------------------------------------------------
        Add(Alice, Base, SourceMessageContent.PlainText("下午三点的评审会改到四点了。"), "1");
        Add(Bob, Base.AddMinutes(2), SourceMessageContent.PlainText("收到。"), "1");
        Add(Carol, Base.AddMinutes(4), SourceMessageContent.PlainText("收到。"), "1");
        Add(Bob, Base.AddMinutes(9), new SourceMessageContent { Kind = SourceContentKind.Image }, "3");
        Add(Alice, Base.AddMinutes(11), new SourceMessageContent
        {
            Kind = SourceContentKind.File,
            FileName = "华东中心项目汇报V8.pptx",
            FileExtension = "pptx",
            FileSizeBytes = 1_839_201,
        }, "49", "6");
        Add(Carol, Base.AddMinutes(13), new SourceMessageContent
        {
            Kind = SourceContentKind.File,
            FileName = null,
        }, "49", "6");
        Add(Bob, Base.AddHours(1), new SourceMessageContent
        {
            Kind = SourceContentKind.Link,
            Title = "人工智能产业发展报告",
            Description = "2026 年行业综述",
            OriginalUrl = "https://example.com/article/123",
        }, "49", "5");
        Add(Carol, Base.AddHours(2), new SourceMessageContent
        {
            Kind = SourceContentKind.Link,
            Title = "仅能取得跳转链接的分享",
            FallbackUrl = "https://support.weixin.qq.com/redirect?target=unknown",
        }, "49", "5");
        Add(Bob, Base.AddHours(3), new SourceMessageContent
        {
            Kind = SourceContentKind.AppShare,
            SourceApp = "小红书",
            Title = "上海周末去哪玩",
            Description = "周末出行清单",
            OriginalUrl = "https://example.com/note/9",
            FallbackUrl = "https://support.weixin.qq.com/redirect?k=9",
            AppId = "wx0000000000000001",
        }, "49", "5");
        Add(Carol, Base.AddHours(4), new SourceMessageContent
        {
            Kind = SourceContentKind.MiniProgram,
            SourceApp = "XX商城",
            Title = "商品详情",
            AppId = "wx1234567890abcd",
            PagePath = "pages/product?id=123",
            OriginalUrl = null,
        }, "49", "33");
        Add(Alice, Base.AddHours(5), new SourceMessageContent
        {
            Kind = SourceContentKind.Voice,
            DurationSeconds = 37,
        }, "34");
        Add(Bob, Base.AddHours(6), new SourceMessageContent { Kind = SourceContentKind.Video }, "43");
        Add(Carol, Base.AddHours(7), new SourceMessageContent { Kind = SourceContentKind.Emoji }, "47");
        Add(Alice, Base.AddHours(8), new SourceMessageContent
        {
            Kind = SourceContentKind.Location,
            Label = "南京南站",
            Address = "南京市雨花台区玉兰路 98 号",
            Latitude = 31.968,
            Longitude = 118.796,
        }, "48");
        Add(Bob, Base.AddHours(9), new SourceMessageContent
        {
            Kind = SourceContentKind.Transfer,
            Amount = "200.00",
            Currency = "CNY",
            TransferStatus = "accepted",
        }, "49", "2001");
        Add(Carol, Base.AddHours(10), new SourceMessageContent { Kind = SourceContentKind.RedPacket }, "49", "2000");
        Add(FixtureAccountId, Base.AddHours(11), new SourceMessageContent
        {
            Kind = SourceContentKind.System,
            SystemText = "张三邀请李四加入了群聊",
            SystemEvent = "member_join",
            ActorSourceIds = [Alice],
            TargetSourceIds = [Carol],
        }, "10000");

        // A reply whose quoted snapshot is present but whose target was never exported.
        Add(Alice, Base.AddHours(12), new SourceMessageContent
        {
            Kind = SourceContentKind.Text,
            Text = "我同意这个方案。",
        }, "49", "57", new SourceReplySnapshot
        {
            SourceMessageId = "s:9999999999999999999",
            SenderSourceId = Bob,
            SenderName = "Kevin",
            Text = "下午三点开会。",
            OccurredAt = Base.AddHours(11).AddMinutes(-30),
        });

        Add(Bob, Base.AddDays(1), new SourceMessageContent
        {
            Kind = SourceContentKind.Revoke,
            SystemText = "张三撤回了一条消息",
            SystemEvent = "revokemsg",
            OperatorSourceId = Alice,
        }, "10000");

        Add(Carol, Base.AddDays(1).AddMinutes(5), new SourceMessageContent
        {
            Kind = SourceContentKind.ForwardBundle,
            Title = "项目讨论聊天记录",
            ForwardItemCount = 2,
            ForwardItems =
            [
                new SourceForwardItem
                {
                    SenderName = "张三",
                    Type = CanonicalMessageType.Text,
                    Text = "第一版价格是不是太高了？",
                },
                new SourceForwardItem
                {
                    SenderName = "李四",
                    Type = CanonicalMessageType.Text,
                    Text = "再压一压供应商。",
                },
            ],
        }, "49", "19");

        Add(Bob, Base.AddDays(2), SourceMessageContent.Unparsed("opaque-payload"), "1000007");

        // February 2026 ------------------------------------------------------------
        Add(Alice, Base.AddDays(20), SourceMessageContent.PlainText("二月第一天的记录。"), "1");
        Add(Carol, Base.AddDays(21), SourceMessageContent.PlainText("二月第二天的记录。"), "1");

        return messages;
    }

    private static List<SourceMessage> BuildDirectMessages()
    {
        var messages = new List<SourceMessage>();
        var index = 0;

        void Add(string sender, DateTimeOffset at, SourceMessageContent content, string type)
        {
            index++;
            messages.Add(new SourceMessage
            {
                SourceConversationId = DirectConversation,
                SenderSourceUserId = sender,
                OccurredAt = at,
                SourceType = type,
                SourcePartition = "fixture_1",
                SourceMessageId = $"l:fixture_1:{index}",
                SourceOrderKey = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Content = content,
            });
        }

        Add(Alice, Base, SourceMessageContent.PlainText("这个卡在哪里？"), "1");
        Add(FixtureAccountId, Base.AddMinutes(1), SourceMessageContent.PlainText("在客户现场。"), "1");
        Add(Alice, Base.AddMinutes(2), new SourceMessageContent { Kind = SourceContentKind.Voice, DurationSeconds = 6 }, "34");
        Add(Alice, Base.AddDays(3), SourceMessageContent.PlainText("三月见。"), "1");

        return messages;
    }
}
