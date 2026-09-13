using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WeArchive.Core.Domain;
using WeArchive.Infrastructure.WeChat.Compatibility;

namespace WeArchive.Infrastructure.WeChat.Parsers;

/// <summary>Result of interpreting one upstream record.</summary>
internal sealed record ParsedWeChatContent(
    SourceMessageContent Content,
    SourceReplySnapshot? Reply,
    string? SenderHint,
    string? DiagnosticCode);

/// <summary>
/// Translates WeChat's wire formats (numeric codes, XML payload cards, compressed
/// content) into the source-neutral <see cref="SourceMessageContent"/> the normalizer
/// consumes.
/// <para>
/// Nothing here may invent a value. When a field cannot be read from the local record it
/// stays null and the record is flagged partial, which surfaces as a diagnostic and in the
/// export manifest rather than as fabricated data.
/// </para>
/// </summary>
internal static partial class WeChatContentParser
{
    /// <summary>URLs that are known indirection/landing pages rather than original content.</summary>
    private static readonly string[] WrapperUrlMarkers =
    [
        "support.weixin.qq.com/",
        "support.weixin.qq.com/update",
        "weixin.qq.com/redirect",
        "mp.weixin.qq.com/mp/waerrpage",
        "mp.weixin.qq.com/mp/readtemplate",
        "mp.weixin.qq.com/mp/verifycode",
        "weixin110.qq.com/",
        "security.weixin.qq.com/",
    ];

    public static ParsedWeChatContent Parse(
        int type,
        int subType,
        string? content,
        bool isGroup,
        Func<string, bool> isKnownUser)
    {
        var (text, senderHint) = ExtractGroupPrefix(content, isGroup, isKnownUser);

        switch (type)
        {
            case WeChat4Schema.TypeText:
                return new ParsedWeChatContent(SourceMessageContent.PlainText(text ?? string.Empty), null, senderHint, null);

            case WeChat4Schema.TypeImage:
                return Simple(SourceContentKind.Image, senderHint);

            case WeChat4Schema.TypeVideo:
                return Simple(SourceContentKind.Video, senderHint);

            case WeChat4Schema.TypeEmoji:
                return Simple(SourceContentKind.Emoji, senderHint);

            case WeChat4Schema.TypeVoice:
                return ParseVoice(text, senderHint);

            case WeChat4Schema.TypeLocation:
                return ParseLocation(text, senderHint);

            case WeChat4Schema.TypeContactCard:
                return ParseContactCard(text, senderHint);

            case WeChat4Schema.TypeVoip:
                return ParseVoip(text, senderHint);

            case WeChat4Schema.TypeApp:
                return ParseAppMessage(text, subType, senderHint);

            case WeChat4Schema.TypeSystem:
            case WeChat4Schema.TypeSystemExtended:
                return ParseSystemMessage(text, senderHint);

            default:
                return new ParsedWeChatContent(
                    SourceMessageContent.Unparsed(null),
                    null,
                    senderHint,
                    WeArchive.Core.Domain.DiagnosticCodes.UnknownMessageType);
        }
    }

    private static ParsedWeChatContent Simple(SourceContentKind kind, string? senderHint) =>
        new(new SourceMessageContent { Kind = kind }, null, senderHint, null);

    /// <summary>
    /// Group messages prefix the payload with <c>&lt;sender&gt;:\n</c>. The prefix is only
    /// removed when the captured name really is a known participant, so ordinary text that
    /// happens to contain a colon is never mangled.
    /// </summary>
    private static (string? Text, string? SenderHint) ExtractGroupPrefix(
        string? content,
        bool isGroup,
        Func<string, bool> isKnownUser)
    {
        if (content is null)
        {
            return (null, null);
        }

        if (!isGroup)
        {
            return (content, null);
        }

        var match = GroupPrefixRegex().Match(content);
        if (!match.Success)
        {
            return (content, null);
        }

        var candidate = match.Groups["user"].Value;
        if (!isKnownUser(candidate))
        {
            return (content, null);
        }

        return (content[match.Length..], candidate);
    }

    [GeneratedRegex(@"^(?<user>[A-Za-z0-9_@.\-]{3,64}):\r?\n", RegexOptions.CultureInvariant)]
    private static partial Regex GroupPrefixRegex();

    private static ParsedWeChatContent ParseVoice(string? content, string? senderHint)
    {
        if (!WeChatXml.TryParse(content, out var document))
        {
            return new ParsedWeChatContent(
                new SourceMessageContent { Kind = SourceContentKind.Voice, IsPartial = true },
                null,
                senderHint,
                DiagnosticCodes.VoiceDurationUnavailable);
        }

        var milliseconds = WeChatXml.ParseLong(WeChatXml.Attribute(WeChatXml.Element(document, "voicemsg"), "voicelength"));

        // WeChat reports voice length in milliseconds. The value is only used when the
        // client actually recorded one; it is never estimated.
        int? seconds = milliseconds is > 0
            ? (int)Math.Round(milliseconds.Value / 1000.0, MidpointRounding.AwayFromZero)
            : null;

        return new ParsedWeChatContent(
            new SourceMessageContent
            {
                Kind = SourceContentKind.Voice,
                DurationSeconds = seconds,
                IsPartial = seconds is null,
            },
            null,
            senderHint,
            seconds is null ? DiagnosticCodes.VoiceDurationUnavailable : null);
    }

    private static ParsedWeChatContent ParseLocation(string? content, string? senderHint)
    {
        if (!WeChatXml.TryParse(content, out var document))
        {
            return new ParsedWeChatContent(SourceMessageContent.Unparsed(null), null, senderHint, DiagnosticCodes.UnknownMessageType);
        }

        var location = WeChatXml.Element(document, "location");
        var pointName = WeChatXml.Attribute(location, "poiname");
        var label = WeChatXml.Attribute(location, "label");
        var address = WeChatXml.Attribute(location, "address");
        var latitude = WeChatXml.ParseDouble(WeChatXml.Attribute(location, "x"));
        var longitude = WeChatXml.ParseDouble(WeChatXml.Attribute(location, "y"));

        // The short point name is the useful label; the full label is the address text.
        var displayLabel = FirstNonEmpty(pointName, label);
        var displayAddress = FirstNonEmpty(address, label);

        return new ParsedWeChatContent(
            new SourceMessageContent
            {
                Kind = SourceContentKind.Location,
                Label = displayLabel,
                Address = ReferenceEquals(displayAddress, displayLabel) ? null : displayAddress,
                Latitude = latitude,
                Longitude = longitude,
                IsPartial = displayLabel is null,
            },
            null,
            senderHint,
            null);
    }

    private static ParsedWeChatContent ParseContactCard(string? content, string? senderHint)
    {
        if (!WeChatXml.TryParse(content, out var document))
        {
            return new ParsedWeChatContent(SourceMessageContent.Unparsed(null), null, senderHint, DiagnosticCodes.UnknownMessageType);
        }

        var msg = WeChatXml.Element(document, "msg");
        return new ParsedWeChatContent(
            new SourceMessageContent
            {
                Kind = SourceContentKind.ContactCard,
                CardDisplayName = FirstNonEmpty(
                    WeChatXml.Attribute(msg, "nickname"),
                    WeChatXml.Attribute(msg, "displayname")),
                CardSourceUserId = FirstNonEmpty(
                    WeChatXml.Attribute(msg, "username"),
                    WeChatXml.Attribute(msg, "encryptusername")),
            },
            null,
            senderHint,
            null);
    }

    private static ParsedWeChatContent ParseVoip(string? content, string? senderHint)
    {
        var text = "[语音通话]";
        if (WeChatXml.TryParse(content, out var document))
        {
            text = FirstNonEmpty(
                WeChatXml.Value(document, "diaplay_content"),
                WeChatXml.Value(document, "display_content"),
                text) ?? text;
        }

        return new ParsedWeChatContent(
            new SourceMessageContent
            {
                Kind = SourceContentKind.System,
                SystemText = text,
                SystemEvent = "voip",
            },
            null,
            senderHint,
            null);
    }

    private static ParsedWeChatContent ParseSystemMessage(string? content, string? senderHint)
    {
        if (!WeChatXml.TryParse(content, out var document))
        {
            return new ParsedWeChatContent(SourceMessageContent.Unparsed(null), null, senderHint, DiagnosticCodes.UnknownMessageType);
        }

        var sysMsg = WeChatXml.Element(document, "sysmsg");
        var sysType = WeChatXml.Attribute(sysMsg, "type");

        if (string.Equals(sysType, "revokemsg", StringComparison.OrdinalIgnoreCase))
        {
            var revoke = WeChatXml.Element(document, "revokemsg");
            var body = FirstNonEmpty(
                WeChatXml.Value(revoke, "content"),
                WeChatXml.Value(revoke, "replacemsg"));

            return new ParsedWeChatContent(
                new SourceMessageContent
                {
                    Kind = SourceContentKind.Revoke,
                    SystemText = body,
                    SystemEvent = "revokemsg",
                    RevokedSourceMessageId = NullIfEmpty(WeChatXml.Value(revoke, "newmsgid")),
                    OperatorSourceId = WeChatXml.Attribute(WeChatXml.Element(document, "revokemsg"), "session"),
                },
                null,
                senderHint,
                body is null ? DiagnosticCodes.ContentDecompressionFailed : null);
        }

        var (systemText, isPartial) = BuildSystemText(document);

        return new ParsedWeChatContent(
            new SourceMessageContent
            {
                Kind = SourceContentKind.System,
                SystemText = systemText,
                SystemEvent = sysType,
                IsPartial = isPartial,
            },
            null,
            senderHint,
            isPartial ? DiagnosticCodes.PartialAppMessage : null);
    }

    /// <summary>
    /// Group notices carry a template plus a link list that supplies the substituted names.
    /// Resolving the template yields the same sentence the client displays, without
    /// inventing any identity.
    /// </summary>
    private static (string? Text, bool IsPartial) BuildSystemText(XDocument document)
    {
        var direct = FirstNonEmpty(
            WeChatXml.Value(document, "text"),
            WeChatXml.Value(document, "content"));
        if (direct is not null)
        {
            return (direct, false);
        }

        var template = WeChatXml.Value(document, "content_template");
        if (template is null)
        {
            return (null, true);
        }

        var builder = new StringBuilder(template);
        foreach (var link in document.Descendants().Where(e => WeChatXml.LocalNameEquals(e, "link")))
        {
            var name = WeChatXml.Attribute(link, "name");
            var title = WeChatXml.Value(link, "title");
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(title))
            {
                builder.Replace("$" + name + "$", title);
            }
        }

        var resolved = builder.ToString().Trim();
        return resolved.Length == 0 ? (null, true) : (resolved, resolved.Contains('$', StringComparison.Ordinal));
    }

    private static ParsedWeChatContent ParseAppMessage(string? content, int subType, string? senderHint)
    {
        if (!WeChatXml.TryParse(content, out var document))
        {
            return new ParsedWeChatContent(
                SourceMessageContent.Unparsed(null),
                null,
                senderHint,
                DiagnosticCodes.UnknownMessageType);
        }

        var appMsg = WeChatXml.Element(document, "appmsg");
        if (appMsg is null)
        {
            // Some type-49 records wrap another message (for example a nested forward).
            return new ParsedWeChatContent(
                SourceMessageContent.Unparsed(null),
                null,
                senderHint,
                DiagnosticCodes.PartialAppMessage);
        }

        var declaredType = WeChatXml.ParseLong(WeChatXml.Value(appMsg, "type"));
        var appType = declaredType is >= 0 and <= int.MaxValue ? (int)declaredType.Value : subType;

        var title = WeChatXml.Value(appMsg, "title");
        var description = WeChatXml.Value(appMsg, "des");
        var url = FirstNonEmpty(WeChatXml.Value(appMsg, "url"), WeChatXml.Value(appMsg, "lowurl"));
        var reply = ParseReply(document);
        var sourceApp = FirstNonEmpty(
            WeChatXml.Value(appMsg, "sourcedisplayname"),
            WeChatXml.Value(appMsg, "appname"),
            TrimAppSuffix(WeChatXml.Value(appMsg, "sourceusername")));

        // In WeChat 4.x the mini-program envelope carries appid/pagepath as child elements of
        // <weappinfo>; older builds occasionally used attributes, so both are accepted.
        var appId = FirstNonEmpty(
            WeChatXml.NestedValue(appMsg, "weappinfo", "appid"),
            WeChatXml.Value(appMsg, "appid"),
            WeChatXml.NestedAttribute(appMsg, "weappinfo", "weappinfo", "appid"));
        var pagePath = FirstNonEmpty(
            WeChatXml.NestedValue(appMsg, "weappinfo", "pagepath"),
            WeChatXml.NestedAttribute(appMsg, "weappinfo", "weappinfo", "pagepath"));
        var isPartial = false;

        SourceMessageContent result;

        if (WeChatXml.Element(appMsg, "recorditem") is not null)
        {
            result = ParseForwardBundle(document, appMsg, title, description, appType);
        }
        else if (appType is WeChat4Schema.AppMiniProgram or WeChat4Schema.AppMiniProgramAlt
                 || (pagePath is not null && appId is not null))
        {
            result = new SourceMessageContent
            {
                Kind = SourceContentKind.MiniProgram,
                Title = title,
                Description = description,
                SourceApp = sourceApp,
                AppId = appId,
                PagePath = pagePath,
                OriginalUrl = IsWrapperUrl(url) ? null : url,
                FallbackUrl = IsWrapperUrl(url) ? url : null,
            };
        }
        else
        {
            switch (appType)
            {
                case WeChat4Schema.AppQuote:
                case WeChat4Schema.AppText:
                    // A quote's own text lives in <title>; the relationship is carried by
                    // <refermsg>. This is a text message with a reply, not a new type.
                    result = new SourceMessageContent { Kind = SourceContentKind.Text, Text = title ?? string.Empty };
                    break;

                case WeChat4Schema.AppImage:
                    result = new SourceMessageContent { Kind = SourceContentKind.Image };
                    break;

                case WeChat4Schema.AppEmoji:
                    result = new SourceMessageContent { Kind = SourceContentKind.Emoji };
                    break;

                case WeChat4Schema.AppFile:
                    result = ParseFile(appMsg, title);
                    break;

                case WeChat4Schema.AppLocation:
                    result = new SourceMessageContent
                    {
                        Kind = SourceContentKind.Location,
                        Label = title,
                        Address = description,
                    };
                    break;

                case WeChat4Schema.AppMusic:
                case WeChat4Schema.AppVideoLink:
                case WeChat4Schema.AppUrl:
                case WeChat4Schema.AppNote:
                    result = BuildLink(title, description, url);
                    break;

                case WeChat4Schema.AppForwardBundle:
                    result = ParseForwardBundle(document, appMsg, title, description, appType);
                    break;

                default:
                    if (WeChatXml.Element(appMsg, "wcpayinfo") is not null
                        && (title?.Contains("红包", StringComparison.Ordinal) ?? false))
                    {
                        result = new SourceMessageContent { Kind = SourceContentKind.RedPacket };
                    }
                    else if (title is not null || description is not null || url is not null)
                    {
                        // Recognised as an app/card payload but not a subtype we model
                        // precisely: keep every obtainable field rather than dropping it.
                        result = new SourceMessageContent
                        {
                            Kind = SourceContentKind.AppShare,
                            SourceApp = sourceApp,
                            AppId = appId,
                            PagePath = pagePath,
                            Title = title,
                            Description = description,
                            OriginalUrl = IsWrapperUrl(url) ? null : url,
                            FallbackUrl = IsWrapperUrl(url) ? url : null,
                            IsPartial = true,
                        };
                        isPartial = true;
                    }
                    else
                    {
                        return new ParsedWeChatContent(
                            SourceMessageContent.Unparsed(null),
                            reply,
                            senderHint,
                            DiagnosticCodes.UnknownMessageType);
                    }

                    break;
            }
        }

        return new ParsedWeChatContent(
            result with { IsPartial = result.IsPartial || isPartial },
            reply,
            senderHint,
            isPartial ? DiagnosticCodes.PartialAppMessage : null);
    }

    private static SourceMessageContent ParseFile(XElement appMsg, string? title)
    {
        var attach = WeChatXml.Element(appMsg, "appattach");
        var extension = FirstNonEmpty(
            WeChatXml.Value(attach, "fileext"),
            title is not null && title.Contains('.', StringComparison.Ordinal)
                ? Path.GetExtension(title)
                : null);
        var size = WeChatXml.ParseLong(WeChatXml.Value(attach, "totallen"));

        return new SourceMessageContent
        {
            Kind = SourceContentKind.File,
            FileName = title,
            FileExtension = extension,
            FileSizeBytes = size is > 0 ? size : null,
            IsPartial = title is null,
        };
    }

    private static SourceMessageContent BuildLink(string? title, string? description, string? url)
    {
        var wrapper = IsWrapperUrl(url);
        return new SourceMessageContent
        {
            Kind = SourceContentKind.Link,
            Title = title,
            Description = description,
            OriginalUrl = wrapper ? null : url,
            FallbackUrl = wrapper ? url : null,
        };
    }

    /// <summary>
    /// A merged/forwarded chat record. WeChat stores the human-readable form in
    /// <c>&lt;des&gt;</c> and the per-item list in <c>&lt;recorditem&gt;&lt;datalist&gt;</c>.
    /// Items that carry no resolvable identity keep their display name and no ID.
    /// </summary>
    private static SourceMessageContent ParseForwardBundle(
        XDocument document,
        XElement appMsg,
        string? title,
        string? description,
        int appType)
    {
        if (!WeChatXml.TryParse(WeChatXml.Value(appMsg, "recorditem"), out var record))
        {
            // The nested record may only exist as CDATA text; fall back to the summary.
            var items = ParseDescriptionItems(description);
            return new SourceMessageContent
            {
                Kind = SourceContentKind.ForwardBundle,
                Title = title,
                ForwardItemCount = items.Count,
                ForwardItems = items,
                IsPartial = true,
            };
        }

        var dataItems = record.Descendants()
            .Where(e => WeChatXml.LocalNameEquals(e, "dataitem"))
            .ToList();

        var parsed = new List<SourceForwardItem>(dataItems.Count);
        foreach (var item in dataItems)
        {
            var itemText = WeChatXml.Value(item, "datadesc");
            var dataType = WeChatXml.Attribute(item, "datatype");
            parsed.Add(new SourceForwardItem
            {
                SenderName = FirstNonEmpty(
                    WeChatXml.Value(item, "sourcename"),
                    WeChatXml.Value(item, "srcname"),
                    WeChatXml.Value(item, "fromname")),
                OccurredAt = null, // WeChat 4.x does not persist per-item timestamps here.
                Type = MapForwardItemType(dataType, itemText),
                Text = itemText ?? string.Empty,
            });
        }

        if (parsed.Count == 0)
        {
            parsed = ParseDescriptionItems(description);
        }

        var declaredCount = WeChatXml.ParseLong(WeChatXml.Attribute(
            record.Descendants().FirstOrDefault(e => WeChatXml.LocalNameEquals(e, "datalist")),
            "count"));

        return new SourceMessageContent
        {
            Kind = SourceContentKind.ForwardBundle,
            Title = title,
            ForwardItemCount = declaredCount is > 0 ? (int)declaredCount.Value : parsed.Count,
            ForwardItems = parsed,
            IsPartial = parsed.Count == 0 && appType == WeChat4Schema.AppForwardBundle,
        };
    }

    /// <summary>
    /// Falls back to the flattened <c>&lt;des&gt;</c> text, whose lines are
    /// <c>sender: text</c>. Only used when the structured list is unavailable.
    /// </summary>
    private static List<SourceForwardItem> ParseDescriptionItems(string? description)
    {
        var items = new List<SourceForwardItem>();
        if (string.IsNullOrWhiteSpace(description))
        {
            return items;
        }

        foreach (var rawLine in description.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(": ", StringComparison.Ordinal);
            if (separator > 0 && separator < 48)
            {
                items.Add(new SourceForwardItem
                {
                    SenderName = line[..separator],
                    Type = CanonicalMessageType.Text,
                    Text = line[(separator + 2)..],
                });
            }
            else
            {
                items.Add(new SourceForwardItem
                {
                    Type = CanonicalMessageType.Text,
                    Text = line,
                });
            }
        }

        return items;
    }

    /// <summary>
    /// Nested bundle items describe media only through a placeholder in their own text, so
    /// the placeholder is authoritative when present; otherwise only the data types that are
    /// actually understood are mapped and anything else stays <c>unknown</c> rather than
    /// being guessed.
    /// </summary>
    private static CanonicalMessageType MapForwardItemType(string? dataType, string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            if (text.StartsWith("[图片]", StringComparison.Ordinal))
            {
                return CanonicalMessageType.Image;
            }

            if (text.StartsWith("[视频]", StringComparison.Ordinal))
            {
                return CanonicalMessageType.Video;
            }

            if (text.StartsWith("[语音]", StringComparison.Ordinal))
            {
                return CanonicalMessageType.Voice;
            }

            if (text.StartsWith("[文件]", StringComparison.Ordinal))
            {
                return CanonicalMessageType.File;
            }

            if (text.StartsWith("[表情]", StringComparison.Ordinal))
            {
                return CanonicalMessageType.Emoji;
            }

            if (text.StartsWith("[链接]", StringComparison.Ordinal))
            {
                return CanonicalMessageType.Link;
            }
        }

        return dataType switch
        {
            "1" => CanonicalMessageType.Text,
            "2" => CanonicalMessageType.Image,
            _ => CanonicalMessageType.Unknown,
        };
    }

    private static SourceReplySnapshot? ParseReply(XDocument document)
    {
        var referMsg = WeChatXml.Element(document, "refermsg");
        if (referMsg is null)
        {
            return null;
        }

        var serverId = WeChatXml.Value(referMsg, "svrid");

        return new SourceReplySnapshot
        {
            SourceMessageId = NullIfEmpty(serverId),
            SenderSourceId = NullIfEmpty(WeChatXml.Value(referMsg, "fromusr")),
            SenderName = NullIfEmpty(WeChatXml.Value(referMsg, "displayname")),
            Text = NullIfEmpty(WeChatXml.Value(referMsg, "content")),
            OccurredAt = WeChatXml.ParseUnixSeconds(WeChatXml.Value(referMsg, "createtime")),
        };
    }

    /// <summary>
    /// Distinguishes an original content URL from a known indirection/landing URL.
    /// docs/MESSAGE_SCHEMA.md section 9 rule 2.
    /// </summary>
    public static bool IsWrapperUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        foreach (var marker in WrapperUrlMarkers)
        {
            if (url.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? TrimAppSuffix(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var index = value.IndexOf('@');
        return index > 0 ? value[..index] : value;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string DescribeType(int type, int subType) =>
        string.Create(CultureInfo.InvariantCulture, $"{type}");
}
