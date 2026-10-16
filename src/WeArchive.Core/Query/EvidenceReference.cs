using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using WeArchive.Core.Services;

namespace WeArchive.Core.Query;

/// <summary>
/// A citation-ready evidence reference: one stable, source-neutral address of exactly one archived
/// canonical message together with the context window a Harness cited it from
/// (docs/HARNESS.md section 11, docs/PRD.md FR-17, issue #107).
/// <para>
/// The canonical form is
/// <c>wearchive://message/&lt;m_id&gt;?conversation=&lt;conversation-id&gt;&amp;before=&lt;n&gt;&amp;after=&lt;n&gt;</c>.
/// It names only stable canonical identities — the message ID, its conversation and the window
/// counts — never a Raw Vault path, artifact hash or source-internal key, so a citation stays
/// resolvable for as long as the canonical archive holds the message. The canonical provenance
/// block of the cited message (<c>source</c> in the message DTO) is retained by the citer as the
/// traceability half of a citation; it is engineering traceability and never an identity input, so
/// it deliberately stays out of the address.
/// </para>
/// <para>
/// Parsing is strict and fail-closed: the exact lowercase scheme, exactly the three query
/// parameters, no duplicates, no percent-encoding, stable IDs in their canonical lowercase form and
/// window bounds inside the documented context range. Anything else is a deterministic validation
/// failure, because a citation that silently parses into a different window would resolve to
/// evidence the agent did not cite.
/// </para>
/// </summary>
public sealed record EvidenceReference
{
    /// <summary>The exact canonical scheme-and-host prefix of every evidence reference.</summary>
    public const string UriPrefix = "wearchive://message/";

    /// <summary>Stable stable-ID digest length (docs/DATA_MODEL.md section 16): 16 lowercase hex.</summary>
    private const int StableIdHexLength = 16;

    private const string MessageIdPrefix = "m_";
    private const string GroupConversationPrefix = "g_";
    private const string DirectConversationPrefix = "u_";

    private EvidenceReference(string messageId, string conversationId, int before, int after)
    {
        MessageId = messageId;
        ConversationId = conversationId;
        Before = before;
        After = after;
    }

    /// <summary>Stable canonical message ID (<c>m_&lt;16 hex&gt;</c>) the citation addresses.</summary>
    public string MessageId { get; }

    /// <summary>Stable conversation ID (<c>g_&lt;16 hex&gt;</c> / <c>u_&lt;16 hex&gt;</c>) the cited
    /// message was cited from. Resolution verifies the archive agrees.</summary>
    public string ConversationId { get; }

    /// <summary>Cited number of preceding messages, as <c>wearchive context</c> counts them.</summary>
    public int Before { get; }

    /// <summary>Cited number of following messages, as <c>wearchive context</c> counts them.</summary>
    public int After { get; }

    /// <summary>
    /// Creates a reference from validated parts. The message ID and conversation must be the
    /// canonical lowercase stable forms and the window must sit inside the documented context
    /// range, so every instance renders to a resolvable canonical form.
    /// </summary>
    /// <exception cref="ArgumentException">A part is outside the documented canonical form.</exception>
    public static EvidenceReference Create(string messageId, string conversationId, int before, int after)
    {
        if (!IsStableId(messageId, MessageIdPrefix))
            throw new ArgumentException(
                "the message id must be a stable canonical message id (m_<16 hex>).", nameof(messageId));
        if (!IsStableId(conversationId, GroupConversationPrefix) && !IsStableId(conversationId, DirectConversationPrefix))
            throw new ArgumentException(
                "the conversation id must be a stable canonical conversation id (g_<16 hex> / u_<16 hex>).",
                nameof(conversationId));
        if (before < 0 || before > ArchiveQueryService.MaxContextMessages)
            throw new ArgumentOutOfRangeException(
                nameof(before), before, $"before must be between 0 and {ArchiveQueryService.MaxContextMessages}.");
        if (after < 0 || after > ArchiveQueryService.MaxContextMessages)
            throw new ArgumentOutOfRangeException(
                nameof(after), after, $"after must be between 0 and {ArchiveQueryService.MaxContextMessages}.");

        return new EvidenceReference(messageId, conversationId, before, after);
    }

    /// <summary>
    /// Parses one canonical reference. Every deviation from the documented form — a different
    /// scheme, an unknown or repeated parameter, a non-canonical identifier, an out-of-range window
    /// — fails with a reason instead of parsing into something else.
    /// </summary>
    public static bool TryParse(
        string? text,
        [NotNullWhen(true)] out EvidenceReference? reference,
        [NotNullWhen(false)] out string? error)
    {
        reference = null;
        if (text is null || text.Length == 0)
        {
            error = "an evidence reference is required.";
            return false;
        }

        if (!text.StartsWith(UriPrefix, StringComparison.Ordinal))
        {
            error = $"an evidence reference must start with '{UriPrefix}'.";
            return false;
        }

        var rest = text[UriPrefix.Length..];
        var queryStart = rest.IndexOf('?', StringComparison.Ordinal);
        if (queryStart < 0)
        {
            error = "an evidence reference must carry a '?conversation=…&before=…&after=…' query.";
            return false;
        }

        var messageId = rest[..queryStart];
        if (!IsStableId(messageId, MessageIdPrefix))
        {
            error = "an evidence reference must address a stable canonical message id (m_<16 hex>).";
            return false;
        }

        string? conversationId = null;
        int? before = null;
        int? after = null;
        foreach (var parameter in rest[(queryStart + 1)..].Split('&'))
        {
            var separator = parameter.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                error = $"an evidence reference query parameter must be written as '<name>=<value>', found '{parameter}'.";
                return false;
            }

            var name = parameter[..separator];
            var value = parameter[(separator + 1)..];
            if (value.Length == 0)
            {
                error = $"an evidence reference query parameter '{name}' must not be empty.";
                return false;
            }

            switch (name)
            {
                case "conversation" when conversationId is not null:
                case "before" when before is not null:
                case "after" when after is not null:
                    error = $"an evidence reference query parameter '{name}' is repeated.";
                    return false;
                case "conversation":
                    if (!IsStableId(value, GroupConversationPrefix) && !IsStableId(value, DirectConversationPrefix))
                    {
                        error = "an evidence reference 'conversation' must be a stable canonical conversation id (g_<16 hex> / u_<16 hex>).";
                        return false;
                    }

                    conversationId = value;
                    break;
                case "before":
                case "after":
                    if (!TryParseWindowCount(value, name, out var count, out var countError))
                    {
                        error = countError;
                        return false;
                    }

                    if (name == "before")
                        before = count;
                    else
                        after = count;

                    break;
                default:
                    error = $"an evidence reference query parameter '{name}' is not part of the documented form.";
                    return false;
            }
        }

        if (conversationId is null || before is null || after is null)
        {
            error = "an evidence reference must carry exactly the query parameters 'conversation', 'before' and 'after'.";
            return false;
        }

        reference = new EvidenceReference(messageId, conversationId, before.Value, after.Value);
        error = null;
        return true;
    }

    /// <summary>The canonical form a citation records and a resolution command accepts.</summary>
    public string ToCanonicalForm() =>
        $"{UriPrefix}{MessageId}?conversation={ConversationId}&before={Before.ToString(CultureInfo.InvariantCulture)}&after={After.ToString(CultureInfo.InvariantCulture)}";

    public override string ToString() => ToCanonicalForm();

    private static bool TryParseWindowCount(string value, string name, out int count, [NotNullWhen(false)] out string? error)
    {
        count = 0;
        error = null;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out count))
        {
            error = $"an evidence reference '{name}' must be a whole number between 0 and {ArchiveQueryService.MaxContextMessages}.";
            return false;
        }

        if (count <= ArchiveQueryService.MaxContextMessages)
            return true;

        error = $"an evidence reference '{name}' must be a whole number between 0 and {ArchiveQueryService.MaxContextMessages}.";
        return false;
    }

    private static bool IsStableId(string? value, string prefix)
    {
        if (value is null || value.Length != prefix.Length + StableIdHexLength || !value.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        foreach (var character in value[prefix.Length..])
        {
            var isLowerHex = (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f');
            if (!isLowerHex)
                return false;
        }

        return true;
    }
}
