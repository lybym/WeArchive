using System.Text.Json;
using WeArchive.Core.Domain;
using WeArchive.Core.Query;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// Unit tests for the opaque pagination cursor and the documented filter-value grammar
/// (docs/CLI.md <c>message list</c>, docs/HARNESS.md section 5).
/// </summary>
public sealed class ArchiveQueryCursorTests
{
    private const string ConversationId = "g_00000000000000c1";

    private static MessageOrderKey Position() => new()
    {
        OccurredUtc = 1767225600,
        SourceOrderKey = "0002",
        MessageId = "m_0000000000000002",
    };

    [Fact]
    public void EncodeThenDecodeRoundTripsTheExactTimelinePosition()
    {
        var fingerprint = MessageCursor.Fingerprint(ConversationId, null, null, null, null);

        var cursor = MessageCursor.Encode(fingerprint, Position());
        var decoded = MessageCursor.Decode(fingerprint, cursor);

        Assert.Equal(Position().OccurredUtc, decoded.OccurredUtc);
        Assert.Equal(Position().SourceOrderKey, decoded.SourceOrderKey);
        Assert.Equal(Position().MessageId, decoded.MessageId);
    }

    [Fact]
    public void CursorIsOpaqueAndCarriesNoReadableQueryText()
    {
        var cursor = MessageCursor.Encode(
            MessageCursor.Fingerprint(ConversationId, null, null, null, null),
            Position());

        Assert.DoesNotContain(ConversationId, cursor, StringComparison.Ordinal);
        Assert.DoesNotContain("occurred", cursor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('=', cursor);
        Assert.DoesNotContain('+', cursor);
        Assert.DoesNotContain('/', cursor);
    }

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("!!!!")]
    [InlineData("YWJj")] // valid base64url, but not a cursor document
    public void MalformedCursorIsRejectedAsValidationFailure(string cursor)
    {
        var exception = Assert.Throws<ArchiveQueryException>(() => MessageCursor.Decode("fingerprint", cursor));

        Assert.Equal(ArchiveQueryErrorCodes.CursorInvalid, exception.Code);
        Assert.Equal(ArchiveQueryFailureKind.Validation, exception.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyCursorIsRejected(string cursor)
    {
        var exception = Assert.Throws<ArchiveQueryException>(() => MessageCursor.Decode("fingerprint", cursor));

        Assert.Equal(ArchiveQueryErrorCodes.CursorInvalid, exception.Code);
    }

    [Fact]
    public void CursorOfAnotherConversationIsRejected()
    {
        var cursor = MessageCursor.Encode(
            MessageCursor.Fingerprint("g_00000000000000ff", null, null, null, null),
            Position());

        var exception = Assert.Throws<ArchiveQueryException>(
            () => MessageCursor.Decode(MessageCursor.Fingerprint(ConversationId, null, null, null, null), cursor));

        Assert.Equal(ArchiveQueryErrorCodes.CursorInvalid, exception.Code);
    }

    [Fact]
    public void CursorOfAnotherDateRangeIsRejected()
    {
        var cursor = MessageCursor.Encode(
            MessageCursor.Fingerprint(ConversationId, ArchiveQueryHarness.At(1, 1), null, null, null),
            Position());

        var exception = Assert.Throws<ArchiveQueryException>(
            () => MessageCursor.Decode(MessageCursor.Fingerprint(ConversationId, ArchiveQueryHarness.At(1, 2), null, null, null), cursor));

        Assert.Equal(ArchiveQueryErrorCodes.CursorInvalid, exception.Code);
    }

    [Fact]
    public void CursorOfAnotherParticipantOrTypeIsRejected()
    {
        var participantCursor = MessageCursor.Encode(
            MessageCursor.Fingerprint(ConversationId, null, null, ArchiveQueryHarness.AliceId, null),
            Position());

        Assert.Equal(
            ArchiveQueryErrorCodes.CursorInvalid,
            Assert.Throws<ArchiveQueryException>(
                () => MessageCursor.Decode(MessageCursor.Fingerprint(ConversationId, null, null, ArchiveQueryHarness.BobId, null), participantCursor)).Code);

        var typeCursor = MessageCursor.Encode(
            MessageCursor.Fingerprint(ConversationId, null, null, null, "text"),
            Position());

        Assert.Equal(
            ArchiveQueryErrorCodes.CursorInvalid,
            Assert.Throws<ArchiveQueryException>(
                () => MessageCursor.Decode(MessageCursor.Fingerprint(ConversationId, null, null, null, "image"), typeCursor)).Code);
    }

    [Fact]
    public void CursorVersionFromAnotherBuildIsRejectedRatherThanGuessed()
    {
        var cursor = EncodeRaw(new
        {
            v = 2,
            f = MessageCursor.Fingerprint(ConversationId, null, null, null, null),
            t = 1767225600,
            o = "0002",
            m = "m_0000000000000002",
        });

        var exception = Assert.Throws<ArchiveQueryException>(
            () => MessageCursor.Decode(MessageCursor.Fingerprint(ConversationId, null, null, null, null), cursor));

        Assert.Equal(ArchiveQueryErrorCodes.CursorInvalid, exception.Code);
        Assert.Contains("version", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FingerprintIgnoresTheOffsetSpellingOfTheSameInstant()
    {
        var utc = MessageCursor.Fingerprint(
            ConversationId,
            new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero),
            null,
            null,
            null);
        var local = MessageCursor.Fingerprint(
            ConversationId,
            new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.FromHours(8)),
            null,
            null,
            null);

        Assert.Equal(utc, local);
    }

    [Fact]
    public void FingerprintDistinguishesEveryFilterDimension()
    {
        var baseline = MessageCursor.Fingerprint(ConversationId, null, null, null, null);

        Assert.NotEqual(baseline, MessageCursor.Fingerprint("g_other", null, null, null, null));
        Assert.NotEqual(baseline, MessageCursor.Fingerprint(ConversationId, ArchiveQueryHarness.At(1, 1), null, null, null));
        Assert.NotEqual(baseline, MessageCursor.Fingerprint(ConversationId, null, ArchiveQueryHarness.At(1, 1), null, null));
        Assert.NotEqual(baseline, MessageCursor.Fingerprint(ConversationId, null, null, ArchiveQueryHarness.AliceId, null));
        Assert.NotEqual(baseline, MessageCursor.Fingerprint(ConversationId, null, null, null, "text"));
    }

    [Fact]
    public void OversizedCursorIsRejectedWithoutBeingParsed()
    {
        var exception = Assert.Throws<ArchiveQueryException>(
            () => MessageCursor.Decode("fingerprint", new string('a', 5000)));

        Assert.Equal(ArchiveQueryErrorCodes.CursorInvalid, exception.Code);
    }

    private static string EncodeRaw<T>(T payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

/// <summary>
/// Unit tests for the documented date/date-time filter grammar (docs/CLI.md <c>message list</c>).
/// The offset rules matter: an offset-less value must be interpreted in the machine's local
/// offset rather than in the ambient timezone of whichever process parses it.
/// </summary>
public sealed class FilterValuesTests
{
    private static readonly TimeSpan LocalOffset = TimeSpan.FromHours(8);

    [Theory]
    [InlineData("2026-09-01", 2026, 9, 1, 0, 0, 8)]
    [InlineData("2026-09-01T09:30:00", 2026, 9, 1, 9, 30, 8)]
    [InlineData("2026-09-01T09:30", 2026, 9, 1, 9, 30, 8)]
    [InlineData("2026-09-01 09:30:00", 2026, 9, 1, 9, 30, 8)]
    [InlineData("2026-09-01T09:30:00+08:00", 2026, 9, 1, 9, 30, 8)]
    [InlineData("2026-09-01T01:30:00+00:00", 2026, 9, 1, 1, 30, 0)]
    [InlineData("2026-09-01T01:30:00Z", 2026, 9, 1, 1, 30, 0)]
    public void DocumentedFormsAreAccepted(
        string text,
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int offsetHours)
    {
        Assert.True(FilterValues.TryParseInstant(text, LocalOffset, out var value, out var error));

        Assert.Null(error);
        Assert.Equal(new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromHours(offsetHours)), value);
    }

    [Fact]
    public void OffsetLessValueUsesTheSuppliedLocalOffsetNotTheAmbientTimezone()
    {
        Assert.True(FilterValues.TryParseInstant("2026-09-01T09:30:00", TimeSpan.FromHours(-5), out var value, out _));

        Assert.Equal(TimeSpan.FromHours(-5), value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 9, 30, 0, TimeSpan.FromHours(-5)), value);
    }

    [Theory]
    [InlineData("next tuesday")]
    [InlineData("2026-13-01")]
    [InlineData("20260901")]
    [InlineData("")]
    [InlineData("   ")]
    public void UnparseableValueIsRejected(string text)
    {
        Assert.False(FilterValues.TryParseInstant(text, LocalOffset, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void ErrorMessageDescribesTheAcceptedFormsWithoutEchoingInternalState()
    {
        FilterValues.TryParseInstant("next tuesday", LocalOffset, out _, out var error);

        Assert.Contains("ISO-8601", error, StringComparison.Ordinal);
        Assert.Contains("2026-09-01", error, StringComparison.Ordinal);
    }

    [Fact]
    public void UtcDesignatorIsAnInstantNotALiteralSuffix()
    {
        Assert.True(FilterValues.TryParseInstant("2026-09-01T00:00:00Z", LocalOffset, out var value, out _));

        Assert.Equal(TimeSpan.Zero, value.Offset);
        Assert.Equal("+00:00", value.ToString("zzz"));
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.FromHours(8)), value);
    }
}