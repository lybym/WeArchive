using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeArchive.Core.Domain;

namespace WeArchive.Core.Query;

/// <summary>
/// Encodes and decodes the opaque pagination cursor of a message listing.
/// docs/CLI.md <c>message list</c>, docs/HARNESS.md section 5.
/// <para>
/// A cursor is an API token, not a persistent archive record and not an offset. It carries the
/// exclusive canonical timeline position of the last returned record plus a fingerprint of the
/// filters it was produced for, so resuming is exact and a cursor cannot silently be replayed
/// against a different query.
/// </para>
/// <para>
/// Callers must treat the value as opaque: the encoding is an implementation detail and may change
/// without notice, while the contract is only "pass the returned <c>next_cursor</c> back".
/// </para>
/// </summary>
public static class MessageCursor
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
    public static string Encode(string fingerprint, MessageOrderKey position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentNullException.ThrowIfNull(position);

        var payload = new CursorPayload
        {
            Version = CurrentVersion,
            Fingerprint = fingerprint,
            OccurredUtc = position.OccurredUtc,
            SourceOrderKey = position.SourceOrderKey,
            MessageId = position.MessageId,
        };

        return ToBase64Url(JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions));
    }

    /// <summary>
    /// Reads a cursor back into the timeline position it denotes.
    /// </summary>
    /// <param name="fingerprint">
    /// The fingerprint of the normalized filter set the cursor is expected to belong to.
    /// </param>
    /// <param name="cursor">The caller-supplied token.</param>
    /// <exception cref="ArchiveQueryException">
    /// The value is empty, malformed, of an unsupported version, or was produced for a different
    /// filter set. The message never echoes the token's contents.
    /// </exception>
    public static MessageOrderKey Decode(string fingerprint, string cursor)
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
                "the cursor version is not supported by this build; restart the listing.");
        }

        if (string.IsNullOrEmpty(payload.MessageId) || payload.SourceOrderKey is null)
            throw ArchiveQueryException.InvalidCursor("the cursor is not a valid pagination cursor.");

        if (!string.Equals(payload.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw ArchiveQueryException.InvalidCursor(
                "the cursor belongs to a different query; resume a listing with its own cursor.");
        }

        return new MessageOrderKey
        {
            OccurredUtc = payload.OccurredUtc,
            SourceOrderKey = payload.SourceOrderKey,
            MessageId = payload.MessageId,
        };
    }

    /// <summary>
    /// Fingerprint of the filters a cursor is bound to: conversation, date range, participant and
    /// type. The page size is deliberately excluded — a caller may legitimately change
    /// <c>--limit</c> between pages of the same listing.
    /// </summary>
    /// <param name="conversationId">Stable conversation ID.</param>
    /// <param name="since">Parsed inclusive lower bound, or null.</param>
    /// <param name="until">Parsed inclusive upper bound, or null.</param>
    /// <param name="participantId">Canonical participant filter, or null.</param>
    /// <param name="type">Canonical message type wire name, or null.</param>
    public static string Fingerprint(
        string conversationId,
        DateTimeOffset? since,
        DateTimeOffset? until,
        string? participantId,
        string? type)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        var builder = new StringBuilder();
        builder.Append(conversationId).Append('\u001f');
        builder.Append(Instant(since)).Append('\u001f');
        builder.Append(Instant(until)).Append('\u001f');
        builder.Append(participantId ?? string.Empty).Append('\u001f');
        builder.Append(type ?? string.Empty);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant();
    }

    /// <summary>
    /// Renders an instant as epoch seconds so a cursor is independent of the offset spelling the
    /// caller used for the same range.
    /// </summary>
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

        [JsonPropertyName("t")]
        public long OccurredUtc { get; init; }

        [JsonPropertyName("o")]
        public string? SourceOrderKey { get; init; }

        [JsonPropertyName("m")]
        public string? MessageId { get; init; }
    }
}