using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace WeArchive.Core.Query;

/// <summary>
/// Parses the documented string forms of query filter values.
/// docs/CLI.md <c>message list</c>.
/// <para>
/// It lives in the application layer, not in a transport, because the interpretation rules are part
/// of the query contract: a value with an explicit UTC designator or offset keeps that instant,
/// while a value without one is interpreted in the machine's local offset — the same offset the
/// archive renders canonical timestamps with. A transport that parsed dates itself would encode a
/// second, silently different rule.
/// <para>
/// Accepted forms are documented in docs/CLI.md: an ISO-8601 date, or a date-time with a <c>T</c>
/// or space separator, with or without an explicit offset or <c>Z</c>.
/// </para>
/// <para>
/// Parsing is deliberately explicit rather than <c>DateTimeOffset.TryParse</c>: that API assumes
/// the machine's local timezone for an offset-less value, which would make the same filter mean
/// different instants on different machines.
/// </para>
/// </summary>
public static class FilterValues
{
    private static readonly string[] OffsetFormats =
    [
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd'T'HH:mmzzz",
        "yyyy-MM-dd HH:mm:sszzz",
        "yyyy-MM-dd HH:mmzzz",
    ];

    private static readonly string[] LocalFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd",
    ];

    /// <summary>
    /// Parses one date/date-time filter value.
    /// </summary>
    /// <param name="text">The caller-supplied value.</param>
    /// <param name="localOffset">Offset applied to a value that carries no explicit offset.</param>
    /// <param name="value">The parsed instant.</param>
    /// <param name="error">A caller-facing explanation when parsing failed.</param>
    public static bool TryParseInstant(
        string? text,
        TimeSpan localOffset,
        out DateTimeOffset value,
        [NotNullWhen(false)] out string? error)
    {
        error = null;
        value = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "a date or date-time value is required.";
            return false;
        }

        // 'Z' is a UTC designator, not a literal: normalize it to an explicit offset so the
        // accepted format set stays small and the instant stays exactly what the caller wrote.
        var normalized = text.EndsWith('Z') || text.EndsWith('z')
            ? string.Concat(text.AsSpan(0, text.Length - 1), "+00:00")
            : text;

        if (DateTimeOffset.TryParseExact(
                normalized,
                OffsetFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out value))
        {
            return true;
        }

        if (DateTime.TryParseExact(
                text,
                LocalFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var local))
        {
            value = new DateTimeOffset(local, localOffset);
            return true;
        }

        error = "expected an ISO-8601 date or date-time value, for example 2026-09-01 or 2026-09-01T09:30:00+08:00.";
        return false;
    }
}