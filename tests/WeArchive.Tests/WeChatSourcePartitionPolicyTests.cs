using WeArchive.Infrastructure.WeChat;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// Issue #37 acceptance criteria: the WeChat 4.x source-partition policy is explicit and
/// allowlist-based. Filesystem discovery alone never makes a partition required evidence; the
/// four classes are reachable and the Unknown class is never silently blessed as Known
/// unsupported.
/// </summary>
public sealed class WeChatSourcePartitionPolicyTests
{
    /// <summary>
    /// The exact 25 partitions observed on the real WeChat 4.x account that exposed Issue #37
    /// (24 captureable + <c>migrate/unspportmsg.db</c>).
    /// </summary>
    private static readonly string[] ObservedRealAccountPartitions = RealWeChatAccountLayout.Partitions;

    [Theory]
    [InlineData("session/session.db")]
    [InlineData("contact/contact.db")]
    [InlineData("message/message_0.db")]
    [InlineData("message/message_1.db")]
    [InlineData("message/message_12345.db")]
    // Official-account (gh_) conversations live in the biz_message_ family, and the canonical
    // rebuild reader cannot read them without it, so it is Required evidence (Issue #37).
    [InlineData("message/biz_message_0.db")]
    [InlineData("message/biz_message_99.db")]
    public void RequiredEvidenceIsClassifiedRequired(string partitionId) =>
        Assert.Equal(WeChatSourcePartitionClass.Required, WeChatSourcePartitionPolicy.Classify(partitionId));

    [Theory]
    [InlineData("SESSION/SESSION.DB")]
    [InlineData(@"session\session.db")]
    [InlineData("Contact/Contact.Db")]
    [InlineData(@"MESSAGE\Message_7.DB")]
    [InlineData(@"Message\Biz_Message_3.DB")]
    public void ClassificationIgnoresCaseAndAcceptsBackslashSeparators(string partitionId) =>
        Assert.Equal(WeChatSourcePartitionClass.Required, WeChatSourcePartitionPolicy.Classify(partitionId));

    [Theory]
    [InlineData("bizchat/bizchat.db")]
    [InlineData("chatbot/chatbot_message.db")]
    [InlineData("contact/contact_fts.db")]
    [InlineData("emoticon/emoticon.db")]
    [InlineData("favorite/favorite.db")]
    [InlineData("favorite/favorite_fts.db")]
    [InlineData("general/general.db")]
    [InlineData("hardlink/hardlink.db")]
    [InlineData("head_image/head_image.db")]
    [InlineData("message/media_0.db")]
    [InlineData("message/media_12.db")]
    [InlineData("message/message_fts.db")]
    [InlineData("message/message_resource.db")]
    [InlineData("message/weclaw.db")]
    [InlineData("sns/sns.db")]
    [InlineData("solitaire/solitaire.db")]
    [InlineData("third_app_icon/third_app_icon.db")]
    public void ObservedWeChat4xAuxiliaryPartitionsAreClassifiedSupportedAuxiliary(string partitionId) =>
        Assert.Equal(
            WeChatSourcePartitionClass.SupportedAuxiliary,
            WeChatSourcePartitionPolicy.Classify(partitionId));

    [Theory]
    [InlineData("migrate/unspportmsg.db")]
    [InlineData(@"migrate\unspportmsg.db")]
    [InlineData("MIGRATE/UnspportMsg.DB")]
    public void TheDocumentedPartitionIsClassifiedKnownUnsupported(string partitionId) =>
        Assert.Equal(
            WeChatSourcePartitionClass.KnownUnsupported,
            WeChatSourcePartitionPolicy.Classify(partitionId));

    [Theory]
    // A genuine migrate/ partition other than the documented one must not be silently blessed.
    [InlineData("migrate/other_message.db")]
    [InlineData("migrate/migrate.db")]
    // The documented name is matched exactly: WeChat's own misspelling is load-bearing, so the
    // corrected spelling is genuinely unclassified evidence rather than a silent alias.
    [InlineData("migrate/unsupportmsg.db")]
    // A new directory or a new sibling in a known directory is unclassified evidence.
    [InlineData("newpart/new.db")]
    [InlineData("session/session_extra.db")]
    [InlineData("contact/contact_extra.db")]
    [InlineData("message/message_fts2.db")]
    [InlineData("message/message_0_backup.db")]
    [InlineData("message/message_.db")]
    [InlineData("message/message_0x.db")]
    [InlineData("message/biz_message.db")]
    [InlineData("message/biz_message_0x.db")]
    [InlineData("message/message_0.db-wal")]
    [InlineData("message/message_0.db-shm")]
    public void UnclassifiablePartitionsAreNeverBlessed(string partitionId) =>
        Assert.Equal(WeChatSourcePartitionClass.Unknown, WeChatSourcePartitionPolicy.Classify(partitionId));

    [Theory]
    [InlineData("message/message_0.db")]
    [InlineData(@"message\message_0.db")]
    [InlineData("MESSAGE/MESSAGE_0.DB")]
    public void DatabasePartitionsAreRecognisedAsPartitionFiles(string partitionId) =>
        Assert.True(WeChatSourcePartitionPolicy.IsPartitionFile(partitionId));

    [Theory]
    [InlineData("message/message_0.db-wal")]
    [InlineData("message/message_0.db-shm")]
    [InlineData(@"session\session.db-wal")]
    public void WalAndShmSiblingsAreNotPartitions(string partitionId) =>
        Assert.False(WeChatSourcePartitionPolicy.IsPartitionFile(partitionId));

    [Fact]
    public void RealObservedAccountClassifiesIntoExactlyOneClassPerPartition()
    {
        var classified = ObservedRealAccountPartitions
            .Select(partitionId => (Id: partitionId, Class: WeChatSourcePartitionPolicy.Classify(partitionId)))
            .ToArray();

        Assert.Equal(25, classified.Length);
        Assert.Equal(7, classified.Count(c => c.Class == WeChatSourcePartitionClass.Required));
        Assert.Equal(17, classified.Count(c => c.Class == WeChatSourcePartitionClass.SupportedAuxiliary));
        Assert.Equal(1, classified.Count(c => c.Class == WeChatSourcePartitionClass.KnownUnsupported));
        Assert.Equal(0, classified.Count(c => c.Class == WeChatSourcePartitionClass.Unknown));

        Assert.Equal(
            new[] { WeChatSourcePartitionPolicy.UnsupportedMessagePartition },
            classified.Where(c => c.Class == WeChatSourcePartitionClass.KnownUnsupported).Select(c => c.Id));
        Assert.Equal(
            new[]
            {
                "contact/contact.db",
                "message/biz_message_0.db",
                "message/biz_message_1.db",
                "message/message_0.db",
                "message/message_1.db",
                "message/message_2.db",
                "session/session.db",
            },
            classified.Where(c => c.Class == WeChatSourcePartitionClass.Required)
                .Select(c => c.Id)
                .OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void OnlyRequiredAndSupportedAuxiliaryCountAsSupportedEvidence()
    {
        Assert.True(WeChatSourcePartitionPolicy.IsSupportedEvidence(WeChatSourcePartitionClass.Required));
        Assert.True(WeChatSourcePartitionPolicy.IsSupportedEvidence(WeChatSourcePartitionClass.SupportedAuxiliary));
        Assert.False(WeChatSourcePartitionPolicy.IsSupportedEvidence(WeChatSourcePartitionClass.KnownUnsupported));
        Assert.False(WeChatSourcePartitionPolicy.IsSupportedEvidence(WeChatSourcePartitionClass.Unknown));
    }
}