using WeArchive.Core.Domain;

namespace WeArchive.Core.Normalization;

/// <summary>
/// Builds the LLM/search-first <c>text</c> representation for every canonical type.
/// Normative rules: docs/MESSAGE_SCHEMA.md sections 12 and 19.
/// <para>
/// The output must be deterministic, concise, plain UTF-8 and free of upstream
/// XML or implementation detail. Missing values are never substituted with guesses:
/// e.g. a file with no resolvable name renders as <c>[文件]</c>, not a made-up name.
/// </para>
/// </summary>
public static class SemanticText
{
    public static string Build(CanonicalMessageType type, SourceMessageContent content, string? systemText = null)
    {
        switch (type)
        {
            case CanonicalMessageType.Text:
                return content.Text ?? string.Empty;

            case CanonicalMessageType.Image:
                return "[图片]";

            case CanonicalMessageType.Video:
                return "[视频]";

            case CanonicalMessageType.Voice:
                return "[语音]";

            case CanonicalMessageType.Emoji:
                return "[表情]";

            case CanonicalMessageType.File:
                return string.IsNullOrWhiteSpace(content.FileName)
                    ? "[文件]"
                    : $"[文件] {content.FileName}";

            case CanonicalMessageType.Link:
                return Prefix("[链接]", content.Title, BestUrl(content.OriginalUrl, content.FallbackUrl), content.Description);

            case CanonicalMessageType.AppShare:
                return Prefix(
                    content.SourceApp is { Length: > 0 } app ? $"[APP分享][{app}]" : "[APP分享]",
                    content.Title,
                    BestUrl(content.OriginalUrl, content.FallbackUrl),
                    content.Description);

            case CanonicalMessageType.MiniProgram:
                return MiniProgram(content);

            case CanonicalMessageType.ForwardBundle:
                return ForwardBundle(content);

            case CanonicalMessageType.Location:
                return string.IsNullOrWhiteSpace(content.Label) ? "[位置]" : $"[位置] {content.Label}";

            case CanonicalMessageType.ContactCard:
                return string.IsNullOrWhiteSpace(content.CardDisplayName)
                    ? "[联系人名片]"
                    : $"[联系人名片] {content.CardDisplayName}";

            case CanonicalMessageType.System:
                return systemText ?? content.SystemText ?? "[系统消息]";

            case CanonicalMessageType.Revoke:
                return Revoke(content, systemText);

            case CanonicalMessageType.RedPacket:
                return "[红包]";

            case CanonicalMessageType.Transfer:
                return string.IsNullOrWhiteSpace(content.Amount) ? "[转账]" : $"[转账] {content.Amount} 元";

            default:
                return "[未识别消息]";
        }
    }

    /// <summary>
    /// URL precedence: confirmed original URL, then wrapper/fallback URL, then nothing.
    /// A wrapper URL is never relabelled as an original URL.
    /// docs/MESSAGE_SCHEMA.md sections 9 and 10.
    /// </summary>
    public static string? BestUrl(string? originalUrl, string? fallbackUrl) =>
        !string.IsNullOrWhiteSpace(originalUrl) ? originalUrl
        : !string.IsNullOrWhiteSpace(fallbackUrl) ? fallbackUrl
        : null;

    private static string Prefix(string prefix, string? title, string? url, string? description)
    {
        var head = string.IsNullOrWhiteSpace(title) ? prefix : $"{prefix} {title}";
        var lines = new List<string> { head };

        if (!string.IsNullOrWhiteSpace(url))
        {
            lines.Add(url);
        }
        else if (!string.IsNullOrWhiteSpace(description))
        {
            // With no URL at all, the description is the only semantic content worth keeping.
            lines.Add(description);
        }

        return string.Join('\n', lines);
    }

    private static string MiniProgram(SourceMessageContent content)
    {
        var name = content.SourceApp;
        var title = content.Title;

        var body = (name, title) switch
        {
            ({ Length: > 0 } n, { Length: > 0 } t) => $"{n} - {t}",
            ({ Length: > 0 } n, _) => n,
            (_, { Length: > 0 } t) => t,
            _ => string.Empty,
        };

        return body.Length == 0 ? "[小程序]" : $"[小程序] {body}";
    }

    private static string ForwardBundle(SourceMessageContent content)
    {
        var title = string.IsNullOrWhiteSpace(content.Title) ? string.Empty : content.Title;
        var count = content.ForwardItemCount ?? content.ForwardItems?.Count;

        if (count is null or 0)
        {
            return title.Length == 0 ? "[合并转发]" : $"[合并转发] {title}";
        }

        return title.Length == 0
            ? $"[合并转发] 共 {count} 条"
            : $"[合并转发] {title}，共 {count} 条";
    }

    private static string Revoke(SourceMessageContent content, string? systemText)
    {
        if (!string.IsNullOrWhiteSpace(systemText))
        {
            return $"[撤回消息] {systemText}";
        }

        if (!string.IsNullOrWhiteSpace(content.SystemText))
        {
            return $"[撤回消息] {content.SystemText}";
        }

        return "[撤回消息]";
    }
}
