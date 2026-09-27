using System.Text.RegularExpressions;

namespace WeArchive.Infrastructure.WeChat;

/// <summary>
/// How the WeChat 4.x capture adapter accounts for one discovered source partition.
/// <para>
/// The classes and their completeness consequences are specified by docs/RAW_VAULT.md
/// ("Source-partition support policy") and docs/PRD.md FR-20; Issue #37 defines them.
/// </para>
/// </summary>
internal enum WeChatSourcePartitionClass
{
    /// <summary>
    /// Evidence required by the currently supported canonical/rebuild contract. A missing or
    /// unreadable Required partition prevents the generation from being <c>complete</c>.
    /// </summary>
    Required,

    /// <summary>
    /// Evidence this adapter supports preserving in addition to the required canonical minimum.
    /// If such evidence is present but cannot be captured the generation is <c>partial</c>.
    /// </summary>
    SupportedAuxiliary,

    /// <summary>
    /// A known, explicitly classified partition outside the adapter's current support contract.
    /// It stays visible as <c>unsupported</c> coverage and is not part of the capture checkpoint,
    /// but its presence does not by itself downgrade an otherwise complete generation.
    /// </summary>
    KnownUnsupported,

    /// <summary>
    /// Newly discovered evidence for which this adapter version has no approved support decision.
    /// It is recorded as <c>unsupported</c> coverage with a partial diagnostic and prevents the
    /// generation from being reported <c>complete</c> until the partition is classified.
    /// </summary>
    Unknown,
}

/// <summary>
/// The explicit WeChat 4.x source-partition support policy.
/// <para>
/// Filesystem discovery and product support are deliberately separate: an adapter may discover
/// physical source files that are not part of the evidence contract it supports, so the presence
/// of a <c>*.db</c> file alone never makes that partition required evidence. Classification is an
/// explicit allowlist — the <see cref="WeChatSourcePartitionClass.Unknown"/> class is reachable
/// for any discovered partition the policy cannot positively classify, and is never silently
/// treated as Known unsupported.
/// </para>
/// <para>
/// This type stays inside the WeChat infrastructure boundary: partition ids are source-relative
/// paths such as <c>message/message_0.db</c>, never Core/CLI concepts.
/// </para>
/// </summary>
internal static class WeChatSourcePartitionPolicy
{
    /// <summary>
    /// The one documented Known-unsupported WeChat 4.x partition for the current release line.
    /// The current canonical rebuild reader does not consume it, so capture accounts for its
    /// presence as <c>unsupported</c> rather than requiring a database key and a materialized
    /// artifact (Issue #37).
    /// </summary>
    public const string UnsupportedMessagePartition = "migrate/unspportmsg.db";

    /// <summary>Required evidence, matched exactly (case-insensitively, after normalization).</summary>
    private static readonly HashSet<string> RequiredExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "session/session.db",
        "contact/contact.db",
    };

    /// <summary>
    /// Known WeChat 4.x partitions the adapter supports preserving but that are not the canonical
    /// minimum consumed by the current rebuild reader. This is the observed 4.x layout; an
    /// unlisted directory or file name is deliberately <em>not</em> blessed by this set.
    /// </summary>
    private static readonly HashSet<string> SupportedAuxiliaryExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "bizchat/bizchat.db",
        "chatbot/chatbot_message.db",
        "contact/contact_fts.db",
        "emoticon/emoticon.db",
        "favorite/favorite.db",
        "favorite/favorite_fts.db",
        "general/general.db",
        "hardlink/hardlink.db",
        "head_image/head_image.db",
        "message/message_fts.db",
        "message/message_resource.db",
        "message/weclaw.db",
        "sns/sns.db",
        "solitaire/solitaire.db",
        "third_app_icon/third_app_icon.db",
    };

    /// <summary>Explicitly classified partitions outside the current support contract.</summary>
    private static readonly HashSet<string> KnownUnsupportedExact = new(StringComparer.OrdinalIgnoreCase)
    {
        UnsupportedMessagePartition,
    };

    /// <summary>The conversation message shards the canonical rebuild contract requires.</summary>
    private static readonly Regex RequiredShardPattern = new(
        @"\Amessage/message_[0-9]+\.db\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Numbered message-shard siblings the adapter preserves as auxiliary evidence.</summary>
    private static readonly Regex AuxiliaryShardPattern = new(
        @"\Amessage/(?:biz_message|media)_[0-9]+\.db\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether a discovered source-relative id names a database partition at all.
    /// <para>
    /// A database's <c>-wal</c> and <c>-shm</c> siblings are fingerprint inputs and WAL-index
    /// artefacts, not partitions: they are never classified, fingerprinted as partitions or
    /// recorded in coverage.
    /// </para>
    /// </summary>
    public static bool IsPartitionFile(string sourceRelativeId)
    {
        ArgumentNullException.ThrowIfNull(sourceRelativeId);
        return Normalize(sourceRelativeId).EndsWith(".db", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Classifies one discovered source partition, identified by its <c>db_storage</c>-relative,
    /// <c>/</c>-separated id (e.g. <c>message/message_0.db</c>). Backslashes are accepted as the
    /// same separator and matching is case-insensitive. Anything the allowlist cannot positively
    /// classify is <see cref="WeChatSourcePartitionClass.Unknown"/>.
    /// </summary>
    public static WeChatSourcePartitionClass Classify(string sourceRelativeId)
    {
        ArgumentNullException.ThrowIfNull(sourceRelativeId);
        var id = Normalize(sourceRelativeId);

        if (RequiredExact.Contains(id) || RequiredShardPattern.IsMatch(id))
        {
            return WeChatSourcePartitionClass.Required;
        }

        if (SupportedAuxiliaryExact.Contains(id) || AuxiliaryShardPattern.IsMatch(id))
        {
            return WeChatSourcePartitionClass.SupportedAuxiliary;
        }

        if (KnownUnsupportedExact.Contains(id))
        {
            return WeChatSourcePartitionClass.KnownUnsupported;
        }

        // A genuine `migrate/` partition other than the documented one is not blessed either:
        // it is unknown until a product decision classifies it.
        return WeChatSourcePartitionClass.Unknown;
    }

    /// <summary>True when the partition is part of the adapter's supported evidence contract.</summary>
    public static bool IsSupportedEvidence(WeChatSourcePartitionClass partitionClass) =>
        partitionClass is WeChatSourcePartitionClass.Required
            or WeChatSourcePartitionClass.SupportedAuxiliary;

    private static string Normalize(string sourceRelativeId) => sourceRelativeId.Replace('\\', '/');
}