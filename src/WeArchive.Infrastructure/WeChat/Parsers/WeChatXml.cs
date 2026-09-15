using System.Xml;
using System.Xml.Linq;

namespace WeArchive.Infrastructure.WeChat.Parsers;

/// <summary>
/// Tolerant helpers for WeChat's message XML.
/// <para>
/// WeChat payloads are usually well-formed XML, but they are produced by many client
/// versions and occasionally contain control characters. Parsing is therefore attempted
/// first, then retried after sanitizing, and finally reported as unparsed so the caller can
/// emit an <c>unknown</c> record instead of dropping it.
/// </para>
/// </summary>
internal static class WeChatXml
{
    public static bool TryParse(string? xml, out XDocument document)
    {
        document = new XDocument();
        if (string.IsNullOrWhiteSpace(xml))
        {
            return false;
        }

        if (TryParseExact(xml, out document))
        {
            return true;
        }

        var sanitized = Sanitize(xml);
        return !ReferenceEquals(sanitized, xml) && TryParseExact(sanitized, out document);
    }

    private static bool TryParseExact(string xml, out XDocument document)
    {
        try
        {
            document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            return true;
        }
        catch (XmlException)
        {
            document = new XDocument();
            return false;
        }
    }

    /// <summary>Removes characters that are illegal in XML 1.0.</summary>
    private static string Sanitize(string xml)
    {
        var builder = new System.Text.StringBuilder(xml.Length);
        var changed = false;
        foreach (var c in xml)
        {
            if (c == 0x9 || c == 0xA || c == 0xD
                || (c >= 0x20 && c <= 0xD7FF)
                || (c >= 0xE000 && c <= 0xFFFD))
            {
                builder.Append(c);
            }
            else
            {
                changed = true;
            }
        }

        return changed ? builder.ToString() : xml;
    }

    /// <summary>First descendant with the given local name, ignoring namespaces.</summary>
    public static XElement? Element(XContainer? parent, string localName) =>
        parent?.Descendants().FirstOrDefault(e => LocalNameEquals(e, localName));

    public static string? Value(XContainer? parent, string localName)
    {
        var element = Element(parent, localName);
        return element is null ? null : Normalize(element.Value);
    }

    public static string? Attribute(XElement? element, string name)
    {
        if (element is null)
        {
            return null;
        }

        foreach (var attribute in element.Attributes())
        {
            if (string.Equals(attribute.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
            {
                return Normalize(attribute.Value);
            }
        }

        return null;
    }

    /// <summary>Attribute lookup starting at a container, searching itself and its descendants.</summary>
    public static string? FindAttribute(XContainer? parent, string localName, string attributeName) =>
        Attribute(Element(parent, localName), attributeName);

    /// <summary>Value of <paramref name="childName"/> inside the first descendant named <paramref name="containerName"/>.</summary>
    public static string? NestedValue(XContainer? parent, string containerName, string childName)
    {
        var container = Element(parent, containerName);
        return container is null ? null : Value(container, childName);
    }

    /// <summary>Attribute of <paramref name="childName"/> inside the first descendant named <paramref name="containerName"/>.</summary>
    public static string? NestedAttribute(XContainer? parent, string containerName, string childName, string attributeName)
    {
        var container = Element(parent, containerName);
        return container is null ? null : Attribute(Element(container, childName), attributeName);
    }

    public static bool LocalNameEquals(XElement element, string localName) =>
        string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase);

    private static string? Normalize(string? value) =>
        string.IsNullOrEmpty(value) ? null : value.Trim();

    public static long? ParseLong(string? value) =>
        long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    public static double? ParseDouble(string? value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    public static DateTimeOffset? ParseUnixSeconds(string? value)
    {
        var seconds = ParseLong(value);
        return seconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;
    }
}
