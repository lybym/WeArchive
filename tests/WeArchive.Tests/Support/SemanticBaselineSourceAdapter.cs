using System.Runtime.CompilerServices;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Infrastructure.WeChat.Compatibility;
using WeArchive.Infrastructure.WeChat.Parsers;

namespace WeArchive.Tests.Support;

/// <summary>
/// A deterministic, source-shaped adapter that streams the M2a semantic baseline corpus
/// (Issue #97) through the real WeChat wire parser.
/// <para>
/// Unlike <see cref="WeArchive.Infrastructure.Fixtures.FixtureSourceAdapter"/>, which
/// builds source-neutral content directly, this adapter carries raw WeChat 4.x wire
/// records (numeric type codes + XML payloads) and runs every record through
/// <see cref="WeChatContentParser"/> — the shipped classification boundary the baseline
/// measures. It enables a full offline import → archive → export determinism run in CI
/// without a live WeChat client or a database key.
/// </para>
/// </summary>
public sealed class SemanticBaselineSourceAdapter : ISourceAdapter
{
    public string AdapterName => SemanticBaselineCorpus.AdapterName;

    public string AdapterVersion => SemanticBaselineCorpus.AdapterVersion;

    public Task<SourceDescriptor> DescribeSourceAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SourceDescriptor
        {
            AdapterName = AdapterName,
            AdapterVersion = AdapterVersion,
            SourceVersion = SemanticBaselineCorpus.CorpusSourceVersion,
            SourceProductName = "WeArchive M2a semantic baseline corpus",
            IsAvailable = true,
        });

    public Task<IReadOnlyList<SourceAccount>> ListAccountsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SourceAccount>>(
        [
            new SourceAccount
            {
                SourceProfileId = SemanticBaselineCorpus.SourceProfileId,
                DisplayName = "Semantic baseline corpus account",
                IsCurrent = true,
            },
        ]);

    public Task<IReadOnlyList<SourceConversation>> ListConversationsAsync(
        string sourceProfileId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SourceConversation>>(
        [
            new SourceConversation
            {
                SourceConversationId = SemanticBaselineCorpus.DirectConversation,
                Kind = WeChat4Schema.ClassifyConversation(SemanticBaselineCorpus.DirectConversation),
                Title = "李四（语料占位）",
                PeerSourceUserId = SemanticBaselineCorpus.DirectConversation,
            },
            new SourceConversation
            {
                SourceConversationId = SemanticBaselineCorpus.GroupConversation,
                Kind = WeChat4Schema.ClassifyConversation(SemanticBaselineCorpus.GroupConversation),
                Title = "语料占位群",
            },
            new SourceConversation
            {
                SourceConversationId = SemanticBaselineCorpus.OfficialConversation,
                Kind = WeChat4Schema.ClassifyConversation(SemanticBaselineCorpus.OfficialConversation),
                Title = "Example 新闻（语料占位）",
            },
            new SourceConversation
            {
                SourceConversationId = SemanticBaselineCorpus.SystemConversation,
                Kind = WeChat4Schema.ClassifyConversation(SemanticBaselineCorpus.SystemConversation),
                Title = "文件传输助手（语料占位）",
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
            ParticipantCount = SemanticBaselineCorpus.KnownUsers.Length,
        });
    }

    public Task<IReadOnlyList<SourceParticipant>> ListParticipantsAsync(
        string sourceProfileId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SourceParticipant>>(
        [
            new SourceParticipant { SourceUserId = "wxid_alice", Nickname = "张三（占位）" },
            new SourceParticipant { SourceUserId = "wxid_bob", Nickname = "李四（占位）" },
            new SourceParticipant { SourceUserId = "wxid_carol", Nickname = "王五（占位）" },
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

    /// <summary>
    /// Converts the corpus records of one conversation into source messages by running the
    /// shipped wire parser. The sender is the parser's group-prefix hint when present and
    /// the corpus sender otherwise; neither is invented.
    /// </summary>
    public static IReadOnlyList<SourceMessage> BuildMessages(string sourceConversationId)
    {
        var isGroup = string.Equals(
            sourceConversationId,
            SemanticBaselineCorpus.GroupConversation,
            StringComparison.Ordinal);

        var messages = new List<SourceMessage>();
        var index = 0;
        foreach (var record in SemanticBaselineCorpus.Records)
        {
            if (!string.Equals(record.SourceConversationId, sourceConversationId, StringComparison.Ordinal))
            {
                continue;
            }

            index++;
            var parsed = WeChatContentParser.Parse(
                record.Type,
                record.SubType,
                record.Content,
                isGroup,
                SemanticBaselineCorpus.KnownUsers.Contains);

            messages.Add(new SourceMessage
            {
                SourceConversationId = sourceConversationId,
                SenderSourceUserId = parsed.SenderHint ?? record.SenderSourceUserId,
                OccurredAt = Base.AddSeconds(index),
                SourceType = record.Type.ToString(System.Globalization.CultureInfo.InvariantCulture),
                SourceSubtype = record.SubType.ToString(System.Globalization.CultureInfo.InvariantCulture),
                SourcePartition = "message_0",
                SourceMessageId = $"sb:{record.RecordId}",
                SourceOrderKey = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Content = parsed.Content,
                Reply = parsed.Reply,
            });
        }

        return messages;
    }

    /// <summary>Fixed epoch so every derived timestamp — and therefore every export — is reproducible.</summary>
    public static readonly DateTimeOffset Base = new(2026, 9, 13, 14, 0, 0, TimeSpan.FromHours(8));
}
