using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.Query;
using WeArchive.Core.RawVault;
using WeArchive.Core.Services;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// Behaviour tests for <see cref="ArchiveQueryService"/> against a synthetic canonical archive:
/// deterministic timeline ordering, inclusive date bounds, canonical filters, cursor paging
/// without duplicates or omissions, bounded context windows and freshness reporting.
/// docs/PRD.md FR-16/FR-17, docs/HARNESS.md sections 3–5 and 10.
/// </summary>
public sealed class ArchiveQueryServiceTests
{
    private static async Task<ArchiveQueryHarness> SeededAsync()
    {
        var harness = new ArchiveQueryHarness();
        await harness.SeedAsync();
        return harness;
    }

    private static MessageListRequest Request(
        string conversationId = ArchiveQueryHarness.GroupConversationId,
        string? since = null,
        string? until = null,
        string? participant = null,
        string? type = null,
        int? limit = null,
        string? cursor = null) => new()
        {
            ConversationId = conversationId,
            Since = since,
            Until = until,
            ParticipantId = participant,
            Type = type,
            Limit = limit,
            Cursor = cursor,
        };

    // ---- listing and ordering ----------------------------------------------

    [Fact]
    public async Task ListingReturnsTheWholeTimelineInCanonicalOrder()
    {
        using var harness = await SeededAsync();

        var page = await harness.Service.ListMessagesAsync(Request(), CancellationToken.None);

        Assert.Equal(
            ArchiveQueryHarness.CanonicalOrder(harness.GroupMessages).Select(message => message.Id),
            page.Items.Select(message => message.Id));
        Assert.False(page.HasMore);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task ListingAgreesWithTheUnboundedArchiveRead()
    {
        using var harness = await SeededAsync();

        var page = await harness.Service.ListMessagesAsync(Request(limit: 500), CancellationToken.None);
        var unbounded = await harness.ReadGroupMessagesAsync();

        Assert.Equal(unbounded.Select(message => message.Id), page.Items.Select(message => message.Id));
    }

    [Fact]
    public async Task ListingIsScopedToOneConversation()
    {
        using var harness = await SeededAsync();

        var page = await harness.Service.ListMessagesAsync(
            Request(ArchiveQueryHarness.SecondConversationId),
            CancellationToken.None);

        Assert.Equal(3, page.Items.Count);
        Assert.All(page.Items, message => Assert.Equal(ArchiveQueryHarness.SecondConversationId, message.ConversationId));
    }

    [Fact]
    public async Task EmptyConversationIsAnEmptyPageNotAnError()
    {
        using var harness = await SeededAsync();
        var emptyConversationId = StableIds.GroupConversation(ArchiveQueryHarness.AccountId, "999@chatroom");
        await harness.Store.UpsertConversationsAsync(
        [
            new ArchiveConversation
            {
                Id = emptyConversationId,
                AccountId = ArchiveQueryHarness.AccountId,
                SourceConversationId = "999@chatroom",
                Kind = ConversationKind.Group,
                Title = "Empty",
            },
        ], CancellationToken.None);

        var page = await harness.Service.ListMessagesAsync(Request(emptyConversationId), CancellationToken.None);

        Assert.Empty(page.Items);
        Assert.False(page.HasMore);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task UnknownConversationIsADeterministicNotFoundResult()
    {
        using var harness = await SeededAsync();

        var exception = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.ListMessagesAsync(Request("g_0000000000000fff"), CancellationToken.None));

        Assert.Equal(ArchiveQueryErrorCodes.ConversationNotFound, exception.Code);
        Assert.Equal(ArchiveQueryFailureKind.NotFound, exception.Kind);
    }

    // ---- filters ------------------------------------------------------------

    [Fact]
    public async Task SinceAndUntilAreInclusiveInstants()
    {
        using var harness = await SeededAsync();

        var page = await harness.Service.ListMessagesAsync(
            Request(
                since: "2026-01-02T00:00:00+08:00",
                until: "2026-01-04T09:00:00+08:00"),
            CancellationToken.None);

        Assert.Equal(
            [
                ArchiveQueryHarness.ImageId,
                ArchiveQueryHarness.PartialId,
                ArchiveQueryHarness.SystemId,
            ],
            page.Items.Select(message => message.Id));
    }

    [Fact]
    public async Task DateOnlyBoundDenotesMidnightOfThatDay()
    {
        using var harness = await SeededAsync();

        var page = await harness.Service.ListMessagesAsync(Request(until: "2026-01-03"), CancellationToken.None);

        Assert.Equal(
            [
                ArchiveQueryHarness.FirstId,
                ArchiveQueryHarness.SecondId,
                ArchiveQueryHarness.UnknownId,
                ArchiveQueryHarness.ImageId,
            ],
            page.Items.Select(message => message.Id));

        var fromMidnight = await harness.Service.ListMessagesAsync(
            Request(since: "2026-01-03", until: "2026-01-03"),
            CancellationToken.None);
        Assert.Empty(fromMidnight.Items);
    }

    [Fact]
    public async Task ParticipantFilterUsesTheStableSenderIdentity()
    {
        using var harness = await SeededAsync();

        var alice = await harness.Service.ListMessagesAsync(
            Request(participant: ArchiveQueryHarness.AliceId),
            CancellationToken.None);

        Assert.Equal(
            [
                ArchiveQueryHarness.FirstId,
                ArchiveQueryHarness.UnknownId,
                ArchiveQueryHarness.PartialId,
                ArchiveQueryHarness.NullOrderKeyId,
                ArchiveQueryHarness.EqualTimestampFirstId,
            ],
            alice.Items.Select(message => message.Id));

        // The unresolved sender of the system record is never fabricated into a participant.
        var bob = await harness.Service.ListMessagesAsync(
            Request(participant: ArchiveQueryHarness.BobId),
            CancellationToken.None);
        Assert.Equal(
            [
                ArchiveQueryHarness.SecondId,
                ArchiveQueryHarness.ImageId,
                ArchiveQueryHarness.EqualTimestampSecondId,
            ],
            bob.Items.Select(message => message.Id));
    }

    [Fact]
    public async Task TypeFilterUsesCanonicalSemanticsAndReturnsUnknownRecords()
    {
        using var harness = await SeededAsync();

        var unknown = await harness.Service.ListMessagesAsync(Request(type: "unknown"), CancellationToken.None);
        Assert.Equal([ArchiveQueryHarness.UnknownId], unknown.Items.Select(message => message.Id));
        Assert.Equal(CanonicalMessageType.Unknown, unknown.Items[0].Type);

        var text = await harness.Service.ListMessagesAsync(Request(type: "text"), CancellationToken.None);
        Assert.Equal(
            [
                ArchiveQueryHarness.FirstId,
                ArchiveQueryHarness.SecondId,
                ArchiveQueryHarness.PartialId,
                ArchiveQueryHarness.NullOrderKeyId,
                ArchiveQueryHarness.EqualTimestampSecondId,
            ],
            text.Items.Select(message => message.Id));
    }

    [Fact]
    public async Task FiltersCompose()
    {
        using var harness = await SeededAsync();

        var page = await harness.Service.ListMessagesAsync(
            Request(
                participant: ArchiveQueryHarness.AliceId,
                type: "text",
                since: "2026-01-05",
                until: "2026-01-05T23:59:59+08:00"),
            CancellationToken.None);

        Assert.Equal([ArchiveQueryHarness.NullOrderKeyId], page.Items.Select(message => message.Id));
    }

    // ---- pagination ---------------------------------------------------------

    [Fact]
    public async Task CursorPagingCoversTheTimelineWithoutDuplicatesOrOmissions()
    {
        using var harness = await SeededAsync();
        var expected = ArchiveQueryHarness.CanonicalOrder(harness.GroupMessages).Select(m => m.Id).ToList();

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;

        while (true)
        {
            var page = await harness.Service.ListMessagesAsync(Request(limit: 2, cursor: cursor), CancellationToken.None);
            pages++;

            Assert.True(page.Items.Count <= 2);
            if (page.HasMore)
            {
                Assert.NotNull(page.NextCursor);
            }
            else
            {
                Assert.Null(page.NextCursor);
            }

            seen.AddRange(page.Items.Select(message => message.Id));

            if (!page.HasMore)
                break;

            cursor = page.NextCursor;
            Assert.True(pages < 20, "paging did not terminate");
        }

        // Nine records at two per page, including three messages that share an instant and two
        // that share both an instant and an order key.
        Assert.Equal(5, pages);
        Assert.Equal(expected, seen);
        Assert.Equal(seen.Count, seen.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task ResumeAcceptsADifferentPageSize()
    {
        using var harness = await SeededAsync();

        var first = await harness.Service.ListMessagesAsync(Request(limit: 2), CancellationToken.None);
        Assert.True(first.HasMore);

        var rest = await harness.Service.ListMessagesAsync(Request(limit: 7, cursor: first.NextCursor), CancellationToken.None);

        Assert.Equal(
            ArchiveQueryHarness.CanonicalOrder(harness.GroupMessages).Select(m => m.Id).Skip(2),
            rest.Items.Select(m => m.Id));
        Assert.False(rest.HasMore);
    }

    [Fact]
    public async Task CursorFromADifferentQueryIsRejectedRatherThanMisapplied()
    {
        using var harness = await SeededAsync();

        var page = await harness.Service.ListMessagesAsync(Request(limit: 2), CancellationToken.None);
        Assert.NotNull(page.NextCursor);

        var exception = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.ListMessagesAsync(
                Request(ArchiveQueryHarness.SecondConversationId, limit: 2, cursor: page.NextCursor),
                CancellationToken.None));

        Assert.Equal(ArchiveQueryErrorCodes.CursorInvalid, exception.Code);
        Assert.Equal(ArchiveQueryFailureKind.Validation, exception.Kind);
    }

    [Fact]
    public async Task MalformedCursorIsRejected()
    {
        using var harness = await SeededAsync();

        var exception = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.ListMessagesAsync(Request(cursor: "opaque-garbage"), CancellationToken.None));

        Assert.Equal(ArchiveQueryErrorCodes.CursorInvalid, exception.Code);
    }

    [Fact]
    public async Task PagingIsBoundedByTheRequestedLimit()
    {
        using var harness = await SeededAsync();

        var page = await harness.Service.ListMessagesAsync(Request(limit: 1), CancellationToken.None);

        Assert.Single(page.Items);
        Assert.True(page.HasMore);
        Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public async Task RepeatedListingIsStableAndDoesNotConsumeTheCursor()
    {
        using var harness = await SeededAsync();

        var first = await harness.Service.ListMessagesAsync(Request(limit: 3), CancellationToken.None);
        var repeat = await harness.Service.ListMessagesAsync(Request(limit: 3), CancellationToken.None);

        Assert.Equal(first.Items.Select(message => message.Id), repeat.Items.Select(message => message.Id));
        Assert.Equal(first.NextCursor, repeat.NextCursor);
        Assert.Equal(first.HasMore, repeat.HasMore);
    }

    [Fact]
    public async Task CancellationBetweenPagesLeavesTheListingResumable()
    {
        using var harness = await SeededAsync();

        var page = await harness.Service.ListMessagesAsync(Request(limit: 4), CancellationToken.None);
        Assert.True(page.HasMore);

        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => harness.Service.ListMessagesAsync(Request(limit: 4, cursor: page.NextCursor), cancelled.Token));
        }

        // A cursor is caller-held, not server-side state: the interrupted page resumes unchanged.
        var resumed = await harness.Service.ListMessagesAsync(Request(limit: 8, cursor: page.NextCursor), CancellationToken.None);

        Assert.Equal(
            ArchiveQueryHarness.CanonicalOrder(harness.GroupMessages).Select(m => m.Id).Skip(4),
            resumed.Items.Select(message => message.Id));
    }

    [Fact]
    public async Task CursorPositionSurvivesTheDisappearanceOfItsOwnMessage()
    {
        using var harness = await SeededAsync();
        var fingerprint = MessageCursor.Fingerprint(
            ArchiveQueryHarness.GroupConversationId,
            null,
            null,
            null,
            null);

        // A position that names no archived record — the cursor denotes a place in the timeline,
        // not a row that must still exist, so resuming stays deterministic.
        var cursor = MessageCursor.Encode(
            fingerprint,
            new MessageOrderKey
            {
                OccurredUtc = ArchiveQueryHarness.At(1, 4).ToUnixTimeSeconds() + 1,
                SourceOrderKey = string.Empty,
                MessageId = "m_0000000000000fff",
            });

        var page = await harness.Service.ListMessagesAsync(Request(cursor: cursor), CancellationToken.None);

        Assert.Equal(3, page.Items.Count);
        Assert.Equal(ArchiveQueryHarness.At(1, 5).ToUnixTimeSeconds(), page.Items[0].OccurredAt.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task ResumeAcrossARecordWithNoUpstreamOrderKeyStaysExact()
    {
        using var harness = await SeededAsync();
        var expected = ArchiveQueryHarness.CanonicalOrder(harness.GroupMessages).Select(m => m.Id).ToList();

        // Canonical order places the record with no order key first among the equal-instant group,
        // so a page boundary lands exactly on the record whose key is NULL rather than ''.
        var boundary = expected.IndexOf(ArchiveQueryHarness.NullOrderKeyId);
        Assert.True(boundary > 0);

        var first = await harness.Service.ListMessagesAsync(
            Request(limit: boundary + 1),
            CancellationToken.None);
        Assert.Equal(expected.Take(boundary + 1), first.Items.Select(message => message.Id));
        Assert.True(first.HasMore);
        Assert.NotNull(first.NextCursor);

        var rest = await harness.Service.ListMessagesAsync(
            Request(limit: 20, cursor: first.NextCursor),
            CancellationToken.None);

        Assert.Equal(expected.Skip(boundary + 1), rest.Items.Select(message => message.Id));
        Assert.False(rest.HasMore);
    }

    [Fact]
    public async Task PagingStaysExactWhenTheArchiveIsWrittenBetweenPages()
    {
        using var harness = await SeededAsync();
        var expected = ArchiveQueryHarness.CanonicalOrder(harness.GroupMessages).Select(m => m.Id).ToList();

        var first = await harness.Service.ListMessagesAsync(Request(limit: 4), CancellationToken.None);
        Assert.Equal(expected.Take(4), first.Items.Select(message => message.Id));
        Assert.NotNull(first.NextCursor);

        // Another run publishes a record that belongs in the second page's range.
        var insertedId = "m_0000000000000200";
        await harness.Store.UpsertMessagesAsync(
        [
            ArchiveQueryHarness.Message(
                insertedId,
                ArchiveQueryHarness.GroupConversationId,
                ArchiveQueryHarness.At(1, 2, 12),
                ArchiveQueryHarness.AliceId,
                CanonicalMessageType.Text,
                "written between pages",
                "0001",
                "l:message_0:200"),
        ], CancellationToken.None);

        var seen = new List<string>(first.Items.Select(message => message.Id));
        var cursor = first.NextCursor;
        var pages = 0;
        while (cursor is not null)
        {
            var page = await harness.Service.ListMessagesAsync(
                Request(limit: 4, cursor: cursor),
                CancellationToken.None);
            seen.AddRange(page.Items.Select(message => message.Id));
            cursor = page.NextCursor;
            Assert.True(++pages < 10, "paging did not terminate");
        }

        // Every record that existed before the write is seen exactly once, the record written
        // between pages is picked up in timeline position, and nothing is repeated.
        Assert.Equal(seen.Count, seen.Distinct(StringComparer.Ordinal).Count());
        Assert.All(expected, id => Assert.Contains(id, seen));
        Assert.Equal(
            ArchiveQueryHarness.CanonicalOrder(
                harness.GroupMessages.Concat(
                [
                    ArchiveQueryHarness.Message(
                        insertedId,
                        ArchiveQueryHarness.GroupConversationId,
                        ArchiveQueryHarness.At(1, 2, 12),
                        ArchiveQueryHarness.AliceId,
                        CanonicalMessageType.Text,
                        "written between pages",
                        "0001",
                        "l:message_0:200"),
                ])).Select(message => message.Id),
            seen);
    }

    [Fact]
    public async Task SubSecondBoundsAreComparedAtTheArchiveResolution()
    {
        using var harness = await SeededAsync();

        // The archive stores whole seconds, so a sub-second component is truncated before
        // comparison: both bounds still include the record at that second (docs/CLI.md).
        var page = await harness.Service.ListMessagesAsync(
            Request(
                since: "2026-01-02T09:00:00.500+08:00",
                until: "2026-01-02T09:00:00.500+08:00"),
            CancellationToken.None);

        Assert.Equal([ArchiveQueryHarness.ImageId], page.Items.Select(message => message.Id));
    }

    // ---- request validation -------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ArchiveQueryService.MaxLimit + 1)]
    [InlineData(int.MaxValue)]
    public async Task OutOfRangeLimitIsAValidationFailure(int limit)
    {
        using var harness = await SeededAsync();

        var exception = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.ListMessagesAsync(Request(limit: limit), CancellationToken.None));

        Assert.Equal(ArchiveQueryErrorCodes.InvalidRequest, exception.Code);
        Assert.Equal(ArchiveQueryFailureKind.Validation, exception.Kind);
    }

    [Fact]
    public async Task MaximumLimitIsAccepted()
    {
        using var harness = await SeededAsync();

        var page = await harness.Service.ListMessagesAsync(
            Request(limit: ArchiveQueryService.MaxLimit),
            CancellationToken.None);

        Assert.Equal(harness.GroupMessages.Count, page.Items.Count);
    }

    [Fact]
    public async Task MissingConversationIdIsAValidationFailure()
    {
        using var harness = await SeededAsync();

        var exception = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.ListMessagesAsync(Request(conversationId: "  "), CancellationToken.None));

        Assert.Equal(ArchiveQueryErrorCodes.InvalidRequest, exception.Code);
    }

    [Fact]
    public async Task ReversedRangeIsAValidationFailure()
    {
        using var harness = await SeededAsync();

        var exception = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.ListMessagesAsync(
                Request(since: "2026-02-01", until: "2026-01-01"),
                CancellationToken.None));

        Assert.Equal(ArchiveQueryErrorCodes.InvalidRequest, exception.Code);
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("2026-13-01")]
    [InlineData("01/02/2026")]
    public async Task UnparseableDateBoundIsAValidationFailureNotASilentlyWidenedQuery(string value)
    {
        using var harness = await SeededAsync();

        var exception = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.ListMessagesAsync(Request(since: value), CancellationToken.None));

        Assert.Equal(ArchiveQueryErrorCodes.InvalidRequest, exception.Code);
        Assert.Contains("since", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("u_00000000000000A1")]
    [InlineData("u_0000")]
    [InlineData("g_00000000000000c1")]
    [InlineData("u_00000000000000g1")]
    public async Task ParticipantFilterMustBeACanonicalStableId(string participant)
    {
        using var harness = await SeededAsync();

        var exception = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.ListMessagesAsync(Request(participant: participant), CancellationToken.None));

        Assert.Equal(ArchiveQueryErrorCodes.InvalidRequest, exception.Code);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("TEXT")]
    [InlineData("")]
    [InlineData("quote")]
    public async Task TypeFilterMustBeACanonicalWireName(string type)
    {
        using var harness = await SeededAsync();

        var exception = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.ListMessagesAsync(Request(type: type), CancellationToken.None));

        Assert.Equal(ArchiveQueryErrorCodes.InvalidRequest, exception.Code);
    }

    [Theory]
    [InlineData("image")]
    [InlineData("system")]
    [InlineData("forward_bundle")]
    [InlineData("unknown")]
    public async Task EveryCanonicalTypeWireNameIsAccepted(string type)
    {
        using var harness = await SeededAsync();

        var page = await harness.Service.ListMessagesAsync(Request(type: type), CancellationToken.None);

        Assert.All(page.Items, message => Assert.Equal(type, message.Type.ToWireName()));
    }

    // ---- context window ----------------------------------------------------

    [Fact]
    public async Task ContextReturnsBoundedCanonicalNeighboursAroundTheTarget()
    {
        using var harness = await SeededAsync();

        var window = await harness.Service.GetContextAsync(ArchiveQueryHarness.ImageId, 2, 2, CancellationToken.None);

        Assert.Equal(ArchiveQueryHarness.ImageId, window.Message.Id);
        Assert.Equal(
            [ArchiveQueryHarness.SecondId, ArchiveQueryHarness.UnknownId],
            window.Before.Select(message => message.Id));
        Assert.Equal(
            [ArchiveQueryHarness.PartialId, ArchiveQueryHarness.SystemId],
            window.After.Select(message => message.Id));
        Assert.All(window.Before, message => Assert.Equal(ArchiveQueryHarness.GroupConversationId, message.ConversationId));
    }

    [Fact]
    public async Task ContextAtTheFirstAndLastTimelineEdgesIsClampedNotFailed()
    {
        using var harness = await SeededAsync();

        var first = await harness.Service.GetContextAsync(ArchiveQueryHarness.FirstId, 5, 5, CancellationToken.None);
        Assert.Empty(first.Before);
        Assert.Equal(
            [
                ArchiveQueryHarness.SecondId,
                ArchiveQueryHarness.UnknownId,
                ArchiveQueryHarness.ImageId,
                ArchiveQueryHarness.PartialId,
                ArchiveQueryHarness.SystemId,
            ],
            first.After.Select(message => message.Id));

        var last = await harness.Service.GetContextAsync(
            ArchiveQueryHarness.EqualTimestampSecondId,
            9,
            9,
            CancellationToken.None);
        Assert.Empty(last.After);
        Assert.Equal(8, last.Before.Count);
    }

    [Fact]
    public async Task ContextWithZeroBoundsReturnsOnlyTheTarget()
    {
        using var harness = await SeededAsync();

        var window = await harness.Service.GetContextAsync(ArchiveQueryHarness.PartialId, 0, 0, CancellationToken.None);

        Assert.Empty(window.Before);
        Assert.Empty(window.After);
        Assert.Equal(ArchiveQueryHarness.PartialId, window.Message.Id);
    }

    [Fact]
    public async Task ContextSeparatesEqualTimestampNeighboursDeterministically()
    {
        using var harness = await SeededAsync();

        var window = await harness.Service.GetContextAsync(
            ArchiveQueryHarness.EqualTimestampFirstId,
            1,
            1,
            CancellationToken.None);

        // Same instant as the target, ordered only by source order key: the missing-key record is
        // before it and the id tie-break decides the following record.
        Assert.Equal([ArchiveQueryHarness.NullOrderKeyId], window.Before.Select(message => message.Id));
        Assert.Equal([ArchiveQueryHarness.EqualTimestampSecondId], window.After.Select(message => message.Id));
    }

    [Fact]
    public async Task ContextNeverCrossesIntoAnotherConversation()
    {
        using var harness = await SeededAsync();

        var window = await harness.Service.GetContextAsync(
            ArchiveQueryHarness.OtherConversationId,
            10,
            10,
            CancellationToken.None);

        Assert.Empty(window.Before);
        Assert.Equal(
            ["m_0000000000000102", "m_0000000000000103"],
            window.After.Select(message => message.Id));
        Assert.All(
            window.After,
            message => Assert.Equal(ArchiveQueryHarness.SecondConversationId, message.ConversationId));
    }

    [Fact]
    public async Task UnknownMessageIsADeterministicNotFoundResult()
    {
        using var harness = await SeededAsync();

        var exception = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.GetContextAsync("m_0000000000000fff", 1, 1, CancellationToken.None));

        Assert.Equal(ArchiveQueryErrorCodes.MessageNotFound, exception.Code);
        Assert.Equal(ArchiveQueryFailureKind.NotFound, exception.Kind);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(ArchiveQueryService.MaxContextMessages + 1, 0)]
    [InlineData(0, ArchiveQueryService.MaxContextMessages + 1)]
    public async Task ContextBoundsOutsideTheDocumentedRangeAreValidationFailures(int before, int after)
    {
        using var harness = await SeededAsync();

        var exception = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.GetContextAsync(ArchiveQueryHarness.ImageId, before, after, CancellationToken.None));

        Assert.Equal(ArchiveQueryErrorCodes.InvalidRequest, exception.Code);
    }

    [Fact]
    public async Task MaximumContextWindowIsAcceptedAndClampedByTheTimeline()
    {
        using var harness = await SeededAsync();

        var window = await harness.Service.GetContextAsync(
            ArchiveQueryHarness.ImageId,
            ArchiveQueryService.MaxContextMessages,
            ArchiveQueryService.MaxContextMessages,
            CancellationToken.None);

        Assert.Equal(3, window.Before.Count);
        Assert.Equal(5, window.After.Count);
    }

    [Fact]
    public async Task EmptyMessageIdIsAValidationFailure()
    {
        using var harness = await SeededAsync();

        var exception = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.GetContextAsync(" ", 1, 1, CancellationToken.None));

        Assert.Equal(ArchiveQueryErrorCodes.InvalidRequest, exception.Code);
    }

    // ---- source and preservation independence -------------------------------

    [Fact]
    public async Task QuerySucceedsWhenThePreservationStoreIsUnusable()
    {
        using var harness = await SeededAsync();
        var service = new ArchiveQueryService(
            harness.Store,
            new ThrowingRawVaultStore(),
            harness.IngestProgress,
            harness.Clock);

        var page = await service.ListMessagesAsync(Request(), CancellationToken.None);
        var window = await service.GetContextAsync(ArchiveQueryHarness.ImageId, 1, 1, CancellationToken.None);

        Assert.Equal(harness.GroupMessages.Count, page.Items.Count);
        Assert.Equal(ArchiveQueryHarness.ImageId, window.Message.Id);
    }

    [Fact]
    public async Task QueryIsReadOnlyAndNeverMutatesCanonicalState()
    {
        using var harness = await SeededAsync();
        var before = await harness.ReadGroupMessagesAsync();
        var statsBefore = await harness.Store.GetArchiveStatsAsync(CancellationToken.None);

        await harness.Service.ListMessagesAsync(Request(limit: 2), CancellationToken.None);

        // A cursor failure must leave the archive exactly as it was, not partially paged state.
        await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.ListMessagesAsync(Request(cursor: "garbage"), CancellationToken.None));

        var after = await harness.ReadGroupMessagesAsync();
        var statsAfter = await harness.Store.GetArchiveStatsAsync(CancellationToken.None);

        Assert.Equal(
            before.Select(message => (message.Id, message.Text, message.Type)),
            after.Select(message => (message.Id, message.Text, message.Type)));
        Assert.Equal(statsBefore.MessageCount, statsAfter.MessageCount);
        Assert.Equal(statsBefore.ConversationCount, statsAfter.ConversationCount);
    }

    // ---- freshness ----------------------------------------------------------

    [Fact]
    public async Task FreshnessDistinguishesCaptureFromCanonicalIngestProgress()
    {
        using var harness = await SeededAsync();
        var generationId = "gen_00000000000000ff";
        harness.Vault.AccountIds = [ArchiveQueryHarness.AccountId];
        harness.Vault.Latest[ArchiveQueryHarness.AccountId] = new RawGenerationSummary
        {
            GenerationId = generationId,
            AccountId = ArchiveQueryHarness.AccountId,
            CaptureTime = ArchiveQueryHarness.At(3, 1, 12),
            Completeness = RawGenerationCompleteness.Complete,
            ArtifactCount = 3,
            EvidenceFingerprint = "fingerprint-that-must-not-be-published",
        };

        var freshness = await harness.Service.GetFreshnessAsync(CancellationToken.None);

        var capture = Assert.Single(freshness.Capture);
        Assert.Equal(generationId, capture.GenerationId);
        Assert.Equal(RawGenerationCompleteness.Complete, capture.Completeness);
        Assert.Equal(3, capture.ArtifactCount);
        Assert.Equal(ArchiveQueryHarness.At(3, 1, 12), capture.CaptureTime);

        // Captured but never ingested: the ingest row exists and says so, instead of being absent.
        var ingest = Assert.Single(freshness.Ingest);
        Assert.Equal(ArchiveQueryHarness.AccountId, ingest.AccountId);
        Assert.Null(ingest.LastIngestAt);
        Assert.Null(ingest.LatestIngestedGenerationId);
        Assert.Null(ingest.LastAccountScanAt);

        Assert.Equal(harness.ArchivePath, freshness.Canonical.ArchivePath);
        Assert.Equal(1, freshness.Canonical.AccountCount);
        Assert.Equal(2, freshness.Canonical.ConversationCount);
        Assert.Equal(harness.GroupMessages.Count + harness.OtherMessages.Count, freshness.Canonical.MessageCount);
        Assert.Equal(ArchiveQueryHarness.At(2, 2), freshness.Canonical.LastMessageAt);
        Assert.Equal(harness.Clock.UtcNow, freshness.Canonical.QueriedAt);

        // Freshness must not open artifacts or enumerate every generation.
        Assert.DoesNotContain(nameof(FakeRawVaultStore.ListGenerationsAsync), harness.Vault.Calls);
    }

    [Fact]
    public async Task FreshnessReportsCommittedIngestProgressWithoutExposingCursorLayout()
    {
        using var harness = await SeededAsync();
        await harness.Store.SetIngestCheckpointAsync(new IngestCheckpoint
        {
            Id = "ingest_1",
            AccountId = ArchiveQueryHarness.AccountId,
            AdapterFamily = "wechat-windows",
            ScopeKind = "conversation",
            ScopeId = ArchiveQueryHarness.GroupConversationId,
            CheckpointJson = """{"version":1,"reader_version":"0.1.0","generation_id":"gen_00000000000000aa","evidence_fingerprint":"secret"}""",
            UpdatedAt = ArchiveQueryHarness.At(3, 1, 12),
        }, CancellationToken.None);

        await harness.Store.SetIngestCheckpointAsync(new IngestCheckpoint
        {
            Id = "ingest_2",
            AccountId = ArchiveQueryHarness.AccountId,
            AdapterFamily = "wechat-windows",
            ScopeKind = "account",
            ScopeId = ArchiveQueryHarness.AccountId,
            CheckpointJson = """{"version":1,"reader_version":"0.1.0","covered_generation_ids":["gen_00000000000000aa"]}""",
            UpdatedAt = ArchiveQueryHarness.At(3, 1, 13),
        }, CancellationToken.None);

        var freshness = await harness.Service.GetFreshnessAsync(CancellationToken.None);

        var ingest = Assert.Single(freshness.Ingest);
        Assert.Equal(ArchiveQueryHarness.At(3, 1, 12), ingest.LastIngestAt);
        Assert.Equal("gen_00000000000000aa", ingest.LatestIngestedGenerationId);
        Assert.Equal(ArchiveQueryHarness.GroupConversationId, ingest.LatestIngestedConversationId);
        Assert.Equal(ArchiveQueryHarness.At(3, 1, 13), ingest.LastAccountScanAt);
    }

    [Fact]
    public async Task FreshnessCoversACapturedAccountThatTheCanonicalArchiveDoesNotKnow()
    {
        using var harness = await SeededAsync();
        harness.Vault.AccountIds = ["a_00000000000000b9"];

        var freshness = await harness.Service.GetFreshnessAsync(CancellationToken.None);

        Assert.Equal(
            [ArchiveQueryHarness.AccountId, "a_00000000000000b9"],
            freshness.Capture.Select(capture => capture.AccountId));
        Assert.Equal(
            [ArchiveQueryHarness.AccountId, "a_00000000000000b9"],
            freshness.Ingest.Select(ingest => ingest.AccountId));
        Assert.All(freshness.Capture, capture => Assert.Null(capture.GenerationId));
        // The canonical counts stay a statement about the archive, not about the vault.
        Assert.Equal(1, freshness.Canonical.AccountCount);
    }

    [Theory]
    [InlineData("InvalidDataException")]
    [InlineData("IOException")]
    [InlineData("UnauthorizedAccessException")]
    [InlineData("ArgumentException")]
    [InlineData("NotSupportedException")]
    public async Task AnyPreservationStoreFailureDegradesCaptureFreshnessInsteadOfTheWholeReport(
        string exceptionType)
    {
        using var harness = await SeededAsync();
        harness.Vault.AccountIds = [ArchiveQueryHarness.AccountId];
        harness.Vault.Failure = CreateFailure(exceptionType, "the preservation store is unreadable");

        var freshness = await harness.Service.GetFreshnessAsync(CancellationToken.None);

        // The canonical half must survive a preservation-store failure: a status projection that
        // disappears when one stage is unreadable is useless for diagnosing that stage.
        Assert.Equal(1, freshness.Canonical.AccountCount);
        Assert.Equal(harness.GroupMessages.Count + harness.OtherMessages.Count, freshness.Canonical.MessageCount);

        // The vault listing failed, so only the canonically known account is listed, and the report
        // says why rather than silently narrowing the account set.
        var capture = Assert.Single(freshness.Capture);
        Assert.Equal(ArchiveQueryHarness.AccountId, capture.AccountId);
        Assert.Null(capture.GenerationId);
        Assert.Null(capture.CaptureTime);
        Assert.Null(capture.Completeness);
        Assert.NotNull(freshness.CaptureUnavailableReason);
        Assert.Contains(exceptionType, freshness.CaptureUnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VaultListingFailureNeverRemovesCanonicallyKnownAccounts()
    {
        using var harness = await SeededAsync();
        // The archive knows the account even though the vault cannot be enumerated at all.
        harness.Vault.Failure = new IOException("vault root disappeared");

        var freshness = await harness.Service.GetFreshnessAsync(CancellationToken.None);

        Assert.Equal([ArchiveQueryHarness.AccountId], freshness.Capture.Select(capture => capture.AccountId));
        Assert.Equal([ArchiveQueryHarness.AccountId], freshness.Ingest.Select(ingest => ingest.AccountId));
        Assert.NotNull(freshness.CaptureUnavailableReason);
    }

    [Fact]
    public async Task GenerationReadFailureIsReportedPerAccountWithItsReason()
    {
        using var harness = await SeededAsync();
        harness.Vault.AccountIds = [ArchiveQueryHarness.AccountId];
        harness.Vault.AccountFailures[ArchiveQueryHarness.AccountId] =
            new ArgumentException("duplicate generation identity");

        var freshness = await harness.Service.GetFreshnessAsync(CancellationToken.None);

        // The listing itself succeeded, so only this account's capture state is unknown.
        Assert.Null(freshness.CaptureUnavailableReason);
        var capture = Assert.Single(freshness.Capture);
        Assert.Null(capture.GenerationId);
        Assert.NotNull(capture.UnavailableReason);
        Assert.Contains("ArgumentException", capture.UnavailableReason, StringComparison.Ordinal);
        Assert.Contains("duplicate generation identity", capture.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReadableAccountStillReportsItsGenerationWhenAnotherAccountIsUnreadable()
    {
        using var harness = await SeededAsync();
        var otherAccountId = "a_00000000000000b9";
        harness.Vault.AccountIds = [ArchiveQueryHarness.AccountId, otherAccountId];
        harness.Vault.Latest[ArchiveQueryHarness.AccountId] = new RawGenerationSummary
        {
            GenerationId = "gen_00000000000000aa",
            AccountId = ArchiveQueryHarness.AccountId,
            CaptureTime = ArchiveQueryHarness.At(3, 1, 12),
            Completeness = RawGenerationCompleteness.Complete,
            ArtifactCount = 2,
            EvidenceFingerprint = "fingerprint",
        };
        harness.Vault.AccountFailures[otherAccountId] = new InvalidDataException("invalid manifest");

        var freshness = await harness.Service.GetFreshnessAsync(CancellationToken.None);

        // One unreadable account degrades only itself; the readable account is still reported and
        // the report as a whole stays available.
        Assert.Null(freshness.CaptureUnavailableReason);
        Assert.Equal(2, freshness.Capture.Count);
        var readable = freshness.Capture.Single(capture => capture.AccountId == ArchiveQueryHarness.AccountId);
        Assert.Equal("gen_00000000000000aa", readable.GenerationId);
        Assert.Null(readable.UnavailableReason);
        var unreadable = freshness.Capture.Single(capture => capture.AccountId == otherAccountId);
        Assert.Null(unreadable.GenerationId);
        Assert.NotNull(unreadable.UnavailableReason);
    }

    private static Exception CreateFailure(string exceptionType, string message) => exceptionType switch
    {
        "InvalidDataException" => new InvalidDataException(message),
        "IOException" => new IOException(message),
        "UnauthorizedAccessException" => new UnauthorizedAccessException(message),
        "ArgumentException" => new ArgumentException(message),
        _ => new NotSupportedException(message),
    };

    [Fact]
    public async Task FreshnessOnAnEmptyArchiveIsAConsistentEmptyReport()
    {
        var harness = new ArchiveQueryHarness();
        try
        {
            var freshness = await harness.Service.GetFreshnessAsync(CancellationToken.None);

            Assert.Empty(freshness.Capture);
            Assert.Empty(freshness.Ingest);
            Assert.Equal(0, freshness.Canonical.MessageCount);
            Assert.Null(freshness.Canonical.LastMessageAt);
            Assert.Equal(harness.Clock.UtcNow, freshness.Canonical.QueriedAt);
        }
        finally
        {
            harness.Dispose();
        }
    }
}