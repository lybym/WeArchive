using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;
using WeArchive.Core.Domain;

namespace WeArchive.Core.Query;

/// <summary>
/// The exclusive keyset position a statistics breakdown page resumes after, as the raw
/// kind-dependent bucket key: epoch seconds of the day bucket, the <c>YYYY-MM</c> month key, or
/// the stable conversation ID (docs/DATA_MODEL.md section 24).
/// </summary>
public sealed record StatisticsBreakdownPosition
{
    public required string Value { get; init; }
}

/// <summary>
/// Encodes and decodes the opaque pagination cursor of a statistics breakdown.
/// docs/CLI.md <c>wearchive stats</c>.
/// <para>
/// Like the message and search cursors it is an API token, not a persistent archive record and
/// not an offset: it carries the exclusive keyset position of the last returned bucket plus a
/// fingerprint of the scope/filter set it was produced for, so a cursor cannot silently be
/// replayed against a different scope, range or breakdown kind. The page size is excluded, so a
/// caller may change <c>--limit</c> between pages.
/// </para>
/// <para>
/// Callers must treat the value as opaque: the encoding is an implementation detail and may
/// change without notice, while the contract is only "pass the returned <c>next_cursor</c> back".
/// </para>
/// </summary>
public static class StatisticsCursor
{
    /// <summary>Cursor encoding version. An unknown version is rejected, never guessed.</summary>
    private const int CurrentVersion = 1;

    /// <summary>Longest accepted cursor. A caller-supplied token is bounded input, not free storage.</summary>
    private const int MaxCursorLength = 4096;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Builds the cursor that resumes immediately after <paramref name="position"/>.</summary>
    public static string Encode(string fingerprint, StatisticsBreakdownPosition position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(position.Value);

        var payload = new CursorPayload
        {
            Version = CurrentVersion,
            Fingerprint = fingerprint,
            Position = position.Value,
        };

        return ToBase64Url(JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions));
    }

    /// <summary>
    /// Reads a cursor back into the breakdown position it denotes.
    /// </summary>
    /// <param name="fingerprint">
    /// The fingerprint of the normalized scope/filter set the cursor is expected to belong to.
    /// </param>
    /// <param name="cursor">The caller-supplied token.</param>
    /// <exception cref="ArchiveQueryException">
    /// The value is empty, malformed, of an unsupported version, or was produced for a different
    /// scope, range or breakdown kind. The message never echoes the token's contents.
    /// </exception>
    public static StatisticsBreakdownPosition Decode(string fingerprint, string cursor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        if (string.IsNullOrWhiteSpace(cursor))
            throw ArchiveQueryException.InvalidCursor("the cursor is empty.");

        if (cursor.Length > MaxCursorLength)
            throw ArchiveQueryException.InvalidCursor("the cursor is not a valid pagination cursor.");

        var bytes = TryFromBase64Url(cursor.Trim());
        if (bytes is null)
            throw ArchiveQueryException.InvalidCursor("the cursor is not a valid pagination cursor.");

        CursorPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CursorPayload>(bytes, SerializerOptions);
        }
        catch (JsonException)
        {
            throw ArchiveQueryException.InvalidCursor("the cursor is not a valid pagination cursor.");
        }

        if (payload is null || payload.Version != CurrentVersion)
        {
            throw ArchiveQueryException.InvalidCursor(
                "the cursor version is not supported by this build; restart the statistics read.");
        }

        if (string.IsNullOrEmpty(payload.Position))
            throw ArchiveQueryException.InvalidCursor("the cursor is not a valid pagination cursor.");

        if (!string.Equals(payload.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw ArchiveQueryException.InvalidCursor(
                "the cursor belongs to a different query; resume a statistics read with its own cursor.");
        }

        return new StatisticsBreakdownPosition { Value = payload.Position };
    }

    /// <summary>
    /// Fingerprint of the scope and filter set a statistics cursor is bound to: the scope kind and
    /// its resolved conversations (the requested conversation, or the Collection's archived member
    /// IDs, so a catalog change cannot silently repoint a cursor), the inclusive instant bounds as
    /// epoch seconds and the breakdown kind. The page size is deliberately excluded.
    /// </summary>
    public static string Fingerprint(
        string scopeKind,
        string? conversationId,
        IReadOnlyList<string>? collectionConversationIds,
        DateTimeOffset? since,
        DateTimeOffset? until,
        string breakdown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKind);

        var builder = new StringBuilder();
        builder.Append(scopeKind).Append('\u001f');
        builder.Append(conversationId ?? string.Empty).Append('\u001f');
        foreach (var id in collectionConversationIds ?? [])
            builder.Append(id).Append('\u001e');
        builder.Append('\u001f');
        builder.Append(Instant(since)).Append('\u001f');
        builder.Append(Instant(until)).Append('\u001f');
        builder.Append(breakdown);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant();
    }

    /// <summary>
    /// Validates one decoded position against the breakdown kind it will be applied to, so a
    /// structurally valid cursor of a foreign kind can never reach the store as a SQL literal
    /// (docs/DATA_MODEL.md section 24). The fingerprint already separates kinds; this keeps the
    /// store's keyset comparisons total.
    /// </summary>
    public static string RequirePosition(StatisticsBreakdownKind kind, StatisticsBreakdownPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);

        var value = position.Value;
        var valid = kind switch
        {
            StatisticsBreakdownKind.Day => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
                && epoch >= 0
                && epoch % 86400 == 0,
            StatisticsBreakdownKind.Month => value.Length == 7
                && value[4] == '-'
                && int.TryParse(value.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
                && int.TryParse(value.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var month)
                && year > 0
                && month is >= 1 and <= 12,
            StatisticsBreakdownKind.Conversation => value.Length > 0 && value.IndexOf('\u001e') < 0,
            _ => false,
        };

        if (!valid)
            throw ArchiveQueryException.InvalidCursor("the cursor is not a valid pagination cursor.");

        return value;
    }

    /// <summary>
    /// The exclusive position after a returned row: the row's own bucket key, in the kind's
    /// canonical spelling.
    /// </summary>
    public static StatisticsBreakdownPosition PositionOf(StatisticsBreakdownKind kind, StatisticsBreakdownRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return kind switch
        {
            StatisticsBreakdownKind.Day => new StatisticsBreakdownPosition
            {
                Value = (row.BucketStart?.ToUnixTimeSeconds()
                    ?? throw new ArgumentException("a day breakdown row must carry its bucket start.", nameof(row)))
                    .ToString(CultureInfo.InvariantCulture),
            },
            StatisticsBreakdownKind.Month => new StatisticsBreakdownPosition
            {
                Value = (row.BucketStart?.ToUniversalTime()
                    ?? throw new ArgumentException("a month breakdown row must carry its bucket start.", nameof(row)))
                    .ToString("yyyy-MM", CultureInfo.InvariantCulture),
            },
            StatisticsBreakdownKind.Conversation => new StatisticsBreakdownPosition
            {
                Value = row.ConversationId
                    ?? throw new ArgumentException("a conversation breakdown row must carry its conversation id.", nameof(row)),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static string Instant(DateTimeOffset? value) =>
        value is null ? string.Empty : value.Value.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? TryFromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            1 => string.Empty,
            _ => padded,
        };

        if (padded.Length == 0)
            return null;

        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The encoded cursor body. Short names keep the token small; the shape is private.</summary>
    private sealed record CursorPayload
    {
        [JsonPropertyName("v")]
        public int Version { get; init; }

        [JsonPropertyName("f")]
        public string? Fingerprint { get; init; }

        [JsonPropertyName("p")]
        public string? Position { get; init; }
    }
}
