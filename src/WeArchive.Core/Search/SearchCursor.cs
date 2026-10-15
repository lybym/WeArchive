using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeArchive.Core.Domain;
using WeArchive.Core.Query;

namespace WeArchive.Core.Search;

/// <summary>
/// Encodes and decodes the opaque pagination cursor of a canonical search.
/// docs/SEARCH_SPEC.md section 8, docs/DATA_MODEL.md section 23.2.
/// <para>
/// A cursor is an API token, not a persistent archive record and not an offset. It carries the
/// exclusive position of the last returned item in the search total order — FTS5 <c>bm25</c> rank
/// first, then the canonical timeline keys — plus a fingerprint of the normalized query and filter
/// set it was produced for. The page size is deliberately excluded, so a caller may change the
/// requested limit between pages of the same search.
/// </para>
/// <para>
/// Callers must treat the value as opaque: the encoding is an implementation detail and may change
/// without notice, while the contract is only "pass the returned <c>next_cursor</c> back". The
/// rank is an implementation-specific number and never crosses the service boundary as a result
/// value (docs/SEARCH_SPEC.md section 6); it travels only inside this token.
/// </para>
/// </summary>
public static class SearchCursor
{
    /// <summary>Cursor encoding version. An unknown version is rejected, never guessed.</summary>
    private const int CurrentVersion = 1;

    /// <summary>Longest accepted cursor. A caller-supplied token is bounded input, not free storage.</summary>
    private const int MaxCursorLength = 4096;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Builds the cursor that resumes immediately after <paramref name="position"/>.
    /// </summary>
    public static string Encode(string fingerprint, SearchOrderPosition position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentNullException.ThrowIfNull(position);

        var payload = new CursorPayload
        {
            Version = CurrentVersion,
            Fingerprint = fingerprint,
            Rank = position.Rank,
            OccurredUtc = position.OccurredUtc,
            SourceOrderKey = position.SourceOrderKey,
            MessageId = position.MessageId,
        };

        return ToBase64Url(JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions));
    }

    /// <summary>
    /// Reads a cursor back into the search-order position it denotes.
    /// </summary>
    /// <param name="fingerprint">
    /// The fingerprint of the normalized query and filter set the cursor is expected to belong to.
    /// </param>
    /// <param name="cursor">The caller-supplied token.</param>
    /// <exception cref="WeArchive.Core.Query.ArchiveQueryException">
    /// The value is empty, malformed, of an unsupported version, or was produced for a different
    /// query or filter set (<c>cursor_invalid</c>). The message never echoes the token's contents.
    /// </exception>
    public static SearchOrderPosition Decode(string fingerprint, string cursor)
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
                "the cursor version is not supported by this build; restart the search.");
        }

        if (string.IsNullOrEmpty(payload.MessageId) || payload.SourceOrderKey is null)
            throw ArchiveQueryException.InvalidCursor("the cursor is not a valid pagination cursor.");

        if (!string.Equals(payload.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw ArchiveQueryException.InvalidCursor(
                "the cursor belongs to a different query; resume a search with its own cursor.");
        }

        return new SearchOrderPosition
        {
            Rank = payload.Rank,
            OccurredUtc = payload.OccurredUtc,
            SourceOrderKey = payload.SourceOrderKey,
            MessageId = payload.MessageId,
        };
    }

    /// <summary>
    /// Fingerprint of the normalized query and filter set a cursor is bound to
    /// (docs/SEARCH_SPEC.md section 8): the parsed term structure (so two spellings of the same
    /// query share a cursor), the resolved scope, the inclusive instant bounds as epoch seconds,
    /// the participant and the canonical type. The page size is excluded.
    /// </summary>
    public static string Fingerprint(
        IReadOnlyList<SearchQueryTerm> terms,
        string? conversationId,
        IReadOnlyList<string> collectionConversationIds,
        long? sinceUtc,
        long? untilUtc,
        string? participantId,
        string? type)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(collectionConversationIds);

        var builder = new StringBuilder();
        foreach (var term in terms)
        {
            builder.Append(term.Content).Append('\u001e')
                .Append(term.IsPhrase ? 'p' : '-').Append(term.HasPrefixStar ? '*' : '-').Append('\u001f');
        }

        builder.Append('\u001f');
        builder.Append(conversationId ?? string.Empty).Append('\u001f');
        foreach (var id in collectionConversationIds)
            builder.Append(id).Append('\u001e');
        builder.Append('\u001f');
        builder.Append(sinceUtc?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append('\u001f');
        builder.Append(untilUtc?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append('\u001f');
        builder.Append(participantId ?? string.Empty).Append('\u001f');
        builder.Append(type ?? string.Empty);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant();
    }

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

        [JsonPropertyName("r")]
        public double Rank { get; init; }

        [JsonPropertyName("t")]
        public long OccurredUtc { get; init; }

        [JsonPropertyName("o")]
        public string? SourceOrderKey { get; init; }

        [JsonPropertyName("m")]
        public string? MessageId { get; init; }
    }
}
