using WeArchive.Core.Collections;
using WeArchive.Core.Domain;
using WeArchive.Core.Query;
using WeArchive.Core.Services;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// Behaviour tests for <see cref="ArchiveQueryService.GetStatisticsAsync"/> against the synthetic
/// canonical archive (M3c, docs/DATA_MODEL.md section 24, docs/PRD.md FR-16/FR-17, issue #104):
/// deterministic totals, canonical type distribution, UTC day/month buckets and per-conversation
/// buckets, keyset pagination of the breakdown, scope resolution (archive / conversation /
/// Collection) and the explicit reporting of unknown, partial and unattributed records.
/// <para>
/// The fixture is built through the real <see cref="SqliteArchiveStore"/> write path, so the
/// counts exercise the shipped schema and timeline index exactly as a published archive does.
/// </para>
/// </summary>
public sealed class ArchiveStatisticsServiceTests
{
    // ---- archive-scope totals and breakdowns ---------------------------------

    [Fact]
    public async Task ArchiveScopeTotalsCountEveryArchivedRecordWithoutDroppingUnknowns()
    {
        using var harness = await SeededAsync();

        var statistics = await harness.Service.GetStatisticsAsync(
            new StatisticsRequest(), CancellationToken.None);

        Assert.Equal("archive", statistics.ScopeKind);
        Assert.Null(statistics.ConversationId);
        Assert.Null(statistics.CollectionName);
        Assert.Null(statistics.Since);
        Assert.Null(statistics.Until);
        Assert.Equal(harness.ArchivePath, statistics.ArchivePath);
        Assert.Equal(harness.Clock.UtcNow, statistics.QueriedAt);

        var totals = statistics.Totals;
        Assert.Equal(12, totals.MessageCount);
        Assert.Equal(2, totals.ConversationCount);
        Assert.Equal(2, totals.SenderCount);
        // The system message with an unresolvable sender stays visible as unattributed
        // instead of being dropped or folded into the sender count.
        Assert.Equal(1, totals.UnattributedCount);
        // Unknown and partial records are first-class totals (docs/DATA_MODEL.md section 12).
        Assert.Equal(1, totals.UnknownCount);
        Assert.Equal(1, totals.PartialCount);
        Assert.Equal(ArchiveQueryHarness.At(1, 1), totals.FirstMessageAt);
        Assert.Equal(ArchiveQueryHarness.At(2, 2), totals.LastMessageAt);
    }

    [Fact]
    public async Task ArchiveScopeTypeDistributionIsCompleteAndOrdered()
    {
        using var harness = await SeededAsync();

        var statistics = await harness.Service.GetStatisticsAsync(
            new StatisticsRequest(), CancellationToken.None);

        // Ordered by canonical wire name; `unknown` is an ordinary entry, never omitted.
        Assert.Equal(
            [
                ("file", 1),
                ("image", 1),
                ("link", 1),
                ("system", 1),
                ("text", 6),
                ("unknown", 1),
                ("voice", 1),
            ],
            statistics.ByType.Select(type => (type.Type, type.Count)).ToArray());
    }

    [Fact]
    public async Task DayBucketsUseUtcCanonicalDaysAndCountUnknownsInsideTheirBucket()
    {
        using var harness = await SeededAsync();

        var statistics = await harness.Service.GetStatisticsAsync(
            new StatisticsRequest { Bucket = "day" }, CancellationToken.None);

        Assert.Equal(StatisticsBreakdownKind.Day, statistics.Breakdown);
        Assert.False(statistics.HasMore);
        Assert.Null(statistics.NextCursor);

        // 09:00+08:00 is 01:00Z of the same UTC day, so every fixture message lands on the UTC
        // calendar day of its local date; buckets are deterministic across machines.
        Assert.Equal(
            [
                ("2026-01-01T00:00:00+00:00", 3, 1, 0),
                ("2026-01-02T00:00:00+00:00", 1, 0, 0),
                ("2026-01-03T00:00:00+00:00", 1, 0, 1),
                ("2026-01-04T00:00:00+00:00", 1, 0, 0),
                ("2026-01-05T00:00:00+00:00", 3, 0, 0),
                ("2026-02-01T00:00:00+00:00", 2, 0, 0),
                ("2026-02-02T00:00:00+00:00", 1, 0, 0),
            ],
            statistics.BreakdownItems.Select(row => (
                row.BucketStart!.Value.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
                row.MessageCount,
                row.UnknownCount,
                row.PartialCount)).ToArray());
        Assert.All(statistics.BreakdownItems, row => Assert.Null(row.ConversationId));
    }

    [Fact]
    public async Task MonthBucketsGroupByUtcCalendarMonth()
    {
        using var harness = await SeededAsync();

        var statistics = await harness.Service.GetStatisticsAsync(
            new StatisticsRequest { Bucket = "month" }, CancellationToken.None);

        Assert.Equal(StatisticsBreakdownKind.Month, statistics.Breakdown);
        Assert.Equal(
            [
                ("2026-01-01T00:00:00+00:00", 9, 1, 1),
                ("2026-02-01T00:00:00+00:00", 3, 0, 0),
            ],
            statistics.BreakdownItems.Select(row => (
                row.BucketStart!.Value.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
                row.MessageCount,
                row.UnknownCount,
                row.PartialCount)).ToArray());
    }

    [Fact]
    public async Task ConversationBucketsKeyByStableConversationIdAndStayInsideScope()
    {
        using var harness = await SeededAsync();

        var statistics = await harness.Service.GetStatisticsAsync(
            new StatisticsRequest { Bucket = "conversation" }, CancellationToken.None);

        Assert.Equal(StatisticsBreakdownKind.Conversation, statistics.Breakdown);
        Assert.Equal(
            [
                (ArchiveQueryHarness.GroupConversationId, 9, 1, 1),
                (ArchiveQueryHarness.SecondConversationId, 3, 0, 0),
            ],
            statistics.BreakdownItems.Select(row => (
                row.ConversationId,
                row.MessageCount,
                row.UnknownCount,
                row.PartialCount)).ToArray());
        Assert.All(statistics.BreakdownItems, row => Assert.Null(row.BucketStart));
    }

    [Fact]
    public async Task DateBoundsAreInclusiveInstantsAndNarrowEverySection()
    {
        using var harness = await SeededAsync();

        var statistics = await harness.Service.GetStatisticsAsync(
            new StatisticsRequest
            {
                Since = "2026-01-02T00:00:00+08:00",
                Until = "2026-01-04T09:00:00+08:00",
                Bucket = "day",
            },
            CancellationToken.None);

        Assert.Equal(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.FromHours(8)), statistics.Since);
        Assert.Equal(ArchiveQueryHarness.At(1, 4), statistics.Until);
        Assert.Equal(3, statistics.Totals.MessageCount);
        Assert.Equal(1, statistics.Totals.ConversationCount);
        Assert.Equal(2, statistics.Totals.SenderCount);
        Assert.Equal(1, statistics.Totals.UnattributedCount);
        Assert.Equal(0, statistics.Totals.UnknownCount);
        Assert.Equal(1, statistics.Totals.PartialCount);
        Assert.Equal(ArchiveQueryHarness.At(1, 2), statistics.Totals.FirstMessageAt);
        Assert.Equal(ArchiveQueryHarness.At(1, 4), statistics.Totals.LastMessageAt);
        Assert.Equal(
            ["2026-01-02T00:00:00+00:00", "2026-01-03T00:00:00+00:00", "2026-01-04T00:00:00+00:00"],
            statistics.BreakdownItems.Select(row => row.BucketStart!.Value.ToString("yyyy-MM-dd'T'HH:mm:sszzz")).ToArray());
    }

    [Fact]
    public async Task DefaultBucketIsTheDayTimelineAndDefaultLimitIsAccepted()
    {
        using var harness = await SeededAsync();

        var statistics = await harness.Service.GetStatisticsAsync(
            new StatisticsRequest(), CancellationToken.None);

        // The default page size is a bound, not a promise of rows: the fixture has seven day
        // buckets and they all fit, so the default read returns exactly them.
        Assert.Equal(StatisticsBreakdownKind.Day, statistics.Breakdown);
        Assert.Equal(7, statistics.BreakdownItems.Count);
        Assert.False(statistics.HasMore);
        Assert.Null(statistics.NextCursor);
    }

    // ---- scope resolution ----------------------------------------------------

    [Fact]
    public async Task ConversationScopeCountsOnlyThatConversation()
    {
        using var harness = await SeededAsync();

        var statistics = await harness.Service.GetStatisticsAsync(
            new StatisticsRequest
            {
                ConversationId = ArchiveQueryHarness.GroupConversationId,
                Bucket = "conversation",
            },
            CancellationToken.None);

        Assert.Equal("conversation", statistics.ScopeKind);
        Assert.Equal(ArchiveQueryHarness.GroupConversationId, statistics.ConversationId);
        Assert.Equal(9, statistics.Totals.MessageCount);
        Assert.Equal(1, statistics.Totals.ConversationCount);
        Assert.Equal(
            [(ArchiveQueryHarness.GroupConversationId, 9, 1, 1)],
            statistics.BreakdownItems.Select(row => (
                row.ConversationId, row.MessageCount, row.UnknownCount, row.PartialCount)).ToArray());
    }

    [Fact]
    public async Task ConversationScopeWithoutMessagesIsAZerosReportNotAnError()
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

        var statistics = await harness.Service.GetStatisticsAsync(
            new StatisticsRequest { ConversationId = emptyConversationId }, CancellationToken.None);

        Assert.Equal("conversation", statistics.ScopeKind);
        Assert.Equal(0, statistics.Totals.MessageCount);
        Assert.Equal(0, statistics.Totals.ConversationCount);
        Assert.Null(statistics.Totals.FirstMessageAt);
        Assert.Null(statistics.Totals.LastMessageAt);
        Assert.Empty(statistics.ByType);
        Assert.Empty(statistics.BreakdownItems);
        Assert.False(statistics.HasMore);
    }

    [Fact]
    public async Task CollectionScopeCountsTheArchivedMembershipAndIgnoresUnarchivedMembers()
    {
        using var harness = await SeededAsync();
        harness.CollectionSource.Document = new CollectionCatalogDocument
        {
            SchemaVersion = "1.0",
            Collections = new()
            {
                ["alpha"] = new CollectionEntryDocument
                {
                    Conversations =
                    [
                        ArchiveQueryHarness.GroupConversationId,
                        // A well-formed member that resolves to no archived conversation
                        // contributes no counts and never errors by itself.
                        "g_0000000000000fff",
                    ],
                },
            },
        };

        var statistics = await harness.Service.GetStatisticsAsync(
            new StatisticsRequest { CollectionName = "alpha" }, CancellationToken.None);

        Assert.Equal("collection", statistics.ScopeKind);
        Assert.Null(statistics.ConversationId);
        Assert.Equal("alpha", statistics.CollectionName);
        Assert.Equal(9, statistics.Totals.MessageCount);
        Assert.Equal(1, statistics.Totals.ConversationCount);
    }

    [Fact]
    public async Task UnknownConversationIsADeterministicNotFoundResult()
    {
        using var harness = await SeededAsync();

        var exception = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.GetStatisticsAsync(
                new StatisticsRequest { ConversationId = "g_0000000000000fff" },
                CancellationToken.None));

        Assert.Equal(ArchiveQueryErrorCodes.ConversationNotFound, exception.Code);
        Assert.Equal(ArchiveQueryFailureKind.NotFound, exception.Kind);
    }

    [Fact]
    public async Task UnknownCollectionIsADeterministicNotFoundResult()
    {
        using var harness = await SeededAsync();

        var exception = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.GetStatisticsAsync(
                new StatisticsRequest { CollectionName = "missing" }, CancellationToken.None));

        Assert.Equal(ArchiveQueryErrorCodes.CollectionNotFound, exception.Code);
        Assert.Equal(ArchiveQueryFailureKind.NotFound, exception.Kind);
    }

    [Fact]
    public async Task InvalidCollectionConfigurationIsADeterministicValidationFailure()
    {
        using var harness = await SeededAsync();
        harness.CollectionSource.Document = new CollectionCatalogDocument
        {
            SchemaVersion = "9.9",
            Collections = new(),
        };

        var exception = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.GetStatisticsAsync(
                new StatisticsRequest { CollectionName = "alpha" }, CancellationToken.None));

        Assert.Equal(ArchiveQueryErrorCodes.CollectionConfigInvalid, exception.Code);
        Assert.Equal(ArchiveQueryFailureKind.Validation, exception.Kind);
    }

    // ---- validation ------------------------------------------------------------

    [Fact]
    public async Task UnsupportedBucketIsAUsageFailureNotADifferentBucketing()
    {
        using var harness = await SeededAsync();

        var exception = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.GetStatisticsAsync(
                new StatisticsRequest { Bucket = "week" }, CancellationToken.None));

        Assert.Equal(ArchiveQueryErrorCodes.InvalidRequest, exception.Code);
        Assert.Equal(ArchiveQueryFailureKind.Validation, exception.Kind);
    }

    [Fact]
    public async Task MalformedBoundsAndInvertedRangeAreUsageFailures()
    {
        using var harness = await SeededAsync();

        var malformed = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.GetStatisticsAsync(
                new StatisticsRequest { Since = "not-a-date" }, CancellationToken.None));
        Assert.Equal(ArchiveQueryErrorCodes.InvalidRequest, malformed.Code);

        var inverted = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.GetStatisticsAsync(
                new StatisticsRequest
                {
                    Since = "2026-02-01",
                    Until = "2026-01-01",
                },
                CancellationToken.None));
        Assert.Equal(ArchiveQueryErrorCodes.InvalidRequest, inverted.Code);
    }

    [Fact]
    public async Task LimitOutsideTheDocumentedRangeIsAUsageFailure()
    {
        using var harness = await SeededAsync();

        foreach (var limit in new[] { 0, ArchiveQueryService.MaxLimit + 1 })
        {
            var exception = await Assert.ThrowsAsync<ArchiveQueryException>(
                () => harness.Service.GetStatisticsAsync(
                    new StatisticsRequest { Limit = limit }, CancellationToken.None));
            Assert.Equal(ArchiveQueryErrorCodes.InvalidRequest, exception.Code);
        }
    }

    [Fact]
    public async Task ConversationAndCollectionScopesAreMutuallyExclusive()
    {
        using var harness = await SeededAsync();

        var exception = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.GetStatisticsAsync(
                new StatisticsRequest
                {
                    ConversationId = ArchiveQueryHarness.GroupConversationId,
                    CollectionName = "alpha",
                },
                CancellationToken.None));

        Assert.Equal(ArchiveQueryErrorCodes.InvalidRequest, exception.Code);
    }

    // ---- breakdown pagination --------------------------------------------------

    [Fact]
    public async Task BreakdownPagesTileTheTimelineWithoutRepeatsOrOmissions()
    {
        using var harness = await SeededAsync();

        var unpaginated = await harness.Service.GetStatisticsAsync(
            new StatisticsRequest { Bucket = "day" }, CancellationToken.None);

        var expected = unpaginated.BreakdownItems.Select(row => (
            row.BucketStart, row.MessageCount, row.UnknownCount, row.PartialCount)).ToArray();

        var visited = new List<StatisticsBreakdownRow>();
        var cursor = (string?)null;
        var pages = 0;
        while (true)
        {
            var page = await harness.Service.GetStatisticsAsync(
                new StatisticsRequest { Bucket = "day", Limit = 2, Cursor = cursor },
                CancellationToken.None);
            pages++;

            visited.AddRange(page.BreakdownItems);
            if (!page.HasMore)
            {
                Assert.Null(page.NextCursor);
                break;
            }

            Assert.NotNull(page.NextCursor);
            Assert.True(page.BreakdownItems.Count == 2, "a resumable page must be full.");
            cursor = page.NextCursor;
        }

        Assert.Equal(4, pages);
        Assert.Equal(
            expected,
            visited.Select(row => (row.BucketStart, row.MessageCount, row.UnknownCount, row.PartialCount)).ToArray());
    }

    [Fact]
    public async Task TotalsAreScopeWideOnEveryBreakdownPage()
    {
        using var harness = await SeededAsync();

        var firstPage = await harness.Service.GetStatisticsAsync(
            new StatisticsRequest { Bucket = "day", Limit = 1 }, CancellationToken.None);
        Assert.True(firstPage.HasMore);
        Assert.Equal(12, firstPage.Totals.MessageCount);

        var lastPage = await harness.Service.GetStatisticsAsync(
            new StatisticsRequest
            {
                Bucket = "day",
                Limit = 1,
                Cursor = firstPage.NextCursor,
            },
            CancellationToken.None);
        Assert.Equal(12, lastPage.Totals.MessageCount);
        Assert.Equal(2, lastPage.Totals.ConversationCount);
        Assert.Equal(1, lastPage.Totals.UnknownCount);
    }

    [Fact]
    public async Task MonthBreakdownPaginatesByMonthKey()
    {
        using var harness = await SeededAsync();

        var firstPage = await harness.Service.GetStatisticsAsync(
            new StatisticsRequest { Bucket = "month", Limit = 1 }, CancellationToken.None);
        Assert.True(firstPage.HasMore);
        Assert.Equal(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            firstPage.BreakdownItems.Single().BucketStart);

        var lastPage = await harness.Service.GetStatisticsAsync(
            new StatisticsRequest { Bucket = "month", Limit = 1, Cursor = firstPage.NextCursor },
            CancellationToken.None);
        Assert.False(lastPage.HasMore);
        Assert.Null(lastPage.NextCursor);
        Assert.Equal(
            new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            lastPage.BreakdownItems.Single().BucketStart);
    }

    [Fact]
    public async Task ConversationBreakdownPaginatesByStableConversationId()
    {
        using var harness = await SeededAsync();

        var firstPage = await harness.Service.GetStatisticsAsync(
            new StatisticsRequest { Bucket = "conversation", Limit = 1 }, CancellationToken.None);
        Assert.True(firstPage.HasMore);
        Assert.Equal(
            ArchiveQueryHarness.GroupConversationId,
            firstPage.BreakdownItems.Single().ConversationId);

        var lastPage = await harness.Service.GetStatisticsAsync(
            new StatisticsRequest
            {
                Bucket = "conversation",
                Limit = 1,
                Cursor = firstPage.NextCursor,
            },
            CancellationToken.None);
        Assert.False(lastPage.HasMore);
        Assert.Equal(
            ArchiveQueryHarness.SecondConversationId,
            lastPage.BreakdownItems.Single().ConversationId);
    }

    [Fact]
    public async Task ACursorFromAnotherScopeOrBreakdownIsRejected()
    {
        using var harness = await SeededAsync();

        var dayPage = await harness.Service.GetStatisticsAsync(
            new StatisticsRequest { Bucket = "day", Limit = 1 }, CancellationToken.None);
        Assert.NotNull(dayPage.NextCursor);

        // Same token, different breakdown kind: rejected rather than silently re-bucketed.
        var monthFailure = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.GetStatisticsAsync(
                new StatisticsRequest { Bucket = "month", Cursor = dayPage.NextCursor },
                CancellationToken.None));
        Assert.Equal(ArchiveQueryErrorCodes.CursorInvalid, monthFailure.Code);

        // Same token, different scope: rejected rather than silently paged across scopes.
        var scopeFailure = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.GetStatisticsAsync(
                new StatisticsRequest
                {
                    ConversationId = ArchiveQueryHarness.GroupConversationId,
                    Bucket = "day",
                    Cursor = dayPage.NextCursor,
                },
                CancellationToken.None));
        Assert.Equal(ArchiveQueryErrorCodes.CursorInvalid, scopeFailure.Code);

        // A different date range is a different query.
        var rangeFailure = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.GetStatisticsAsync(
                new StatisticsRequest
                {
                    Bucket = "day",
                    Since = "2026-01-01",
                    Cursor = dayPage.NextCursor,
                },
                CancellationToken.None));
        Assert.Equal(ArchiveQueryErrorCodes.CursorInvalid, rangeFailure.Code);
    }

    [Fact]
    public async Task MalformedCursorIsADeterministicValidationFailure()
    {
        using var harness = await SeededAsync();

        var exception = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.GetStatisticsAsync(
                new StatisticsRequest { Cursor = "not-a-cursor" }, CancellationToken.None));

        Assert.Equal(ArchiveQueryErrorCodes.CursorInvalid, exception.Code);
        Assert.Equal(ArchiveQueryFailureKind.Validation, exception.Kind);
    }

    // ---- read-only guarantee ---------------------------------------------------

    [Fact]
    public async Task StatisticsReadNoRawVaultStateAndNeverMutateTheArchive()
    {
        using var harness = await SeededAsync();

        var before = await harness.Service.GetStatisticsAsync(
            new StatisticsRequest(), CancellationToken.None);
        Assert.Empty(harness.Vault.Calls);

        var after = await harness.Service.GetStatisticsAsync(
            new StatisticsRequest { Bucket = "month" }, CancellationToken.None);

        // The Raw Vault store was never enumerated: statistics read the canonical archive only
        // (docs/PRD.md NFR-13, docs/DATA_MODEL.md section 24).
        Assert.Empty(harness.Vault.Calls);

        // A repeated read is identical: no query path mutates canonical data (R0).
        Assert.Equal(before.Totals, after.Totals);
        Assert.Equal(
            before.ByType.Select(type => (type.Type, type.Count)).ToArray(),
            after.ByType.Select(type => (type.Type, type.Count)).ToArray());
        Assert.Equal(9, (await harness.Store.ReadMessagesAsync(
            ArchiveQueryHarness.GroupConversationId, CancellationToken.None)).Count);
        Assert.Equal(3, (await harness.Store.ReadMessagesAsync(
            ArchiveQueryHarness.SecondConversationId, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task EmptyArchiveIsAZerosReportNotAnError()
    {
        using var harness = new ArchiveQueryHarness();

        var statistics = await harness.Service.GetStatisticsAsync(
            new StatisticsRequest(), CancellationToken.None);

        Assert.Equal("archive", statistics.ScopeKind);
        Assert.Equal(0, statistics.Totals.MessageCount);
        Assert.Equal(0, statistics.Totals.ConversationCount);
        Assert.Equal(0, statistics.Totals.SenderCount);
        Assert.Equal(0, statistics.Totals.UnattributedCount);
        Assert.Equal(0, statistics.Totals.UnknownCount);
        Assert.Equal(0, statistics.Totals.PartialCount);
        Assert.Null(statistics.Totals.FirstMessageAt);
        Assert.Null(statistics.Totals.LastMessageAt);
        Assert.Empty(statistics.ByType);
        Assert.Empty(statistics.BreakdownItems);
        Assert.False(statistics.HasMore);
        Assert.Null(statistics.NextCursor);
    }

    [Fact]
    public async Task MonthBucketsSplitOnUtcMonthBoundariesNotLocalOnes()
    {
        using var harness = new ArchiveQueryHarness();
        await SeedPairAsync(
            harness,
            ("m_0000000000000031", new DateTimeOffset(2026, 1, 31, 23, 59, 59, TimeSpan.Zero)),
            ("m_0000000000000032", new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)));

        var statistics = await harness.Service.GetStatisticsAsync(
            new StatisticsRequest { Bucket = "month" }, CancellationToken.None);

        Assert.Equal(
            [
                ("2026-01-01T00:00:00+00:00", 1),
                ("2026-02-01T00:00:00+00:00", 1),
            ],
            statistics.BreakdownItems.Select(row => (
                row.BucketStart!.Value.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
                row.MessageCount)).ToArray());
    }

    [Fact]
    public async Task DayBucketsSplitOnUtcMidnightNotLocalMidnight()
    {
        using var harness = new ArchiveQueryHarness();
        await SeedPairAsync(
            harness,
            ("m_0000000000000033", new DateTimeOffset(2026, 3, 14, 23, 59, 59, TimeSpan.Zero)),
            ("m_0000000000000034", new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero)));

        var statistics = await harness.Service.GetStatisticsAsync(
            new StatisticsRequest { Bucket = "day" }, CancellationToken.None);

        Assert.Equal(
            [
                ("2026-03-14T00:00:00+00:00", 1),
                ("2026-03-15T00:00:00+00:00", 1),
            ],
            statistics.BreakdownItems.Select(row => (
                row.BucketStart!.Value.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
                row.MessageCount)).ToArray());
    }

    // ---- helpers ---------------------------------------------------------------

    private static async Task<ArchiveQueryHarness> SeededAsync()
    {
        var harness = new ArchiveQueryHarness();
        await harness.SeedAsync();
        return harness;
    }

    /// <summary>
    /// Seeds two boundary messages through the real store write path, so bucket boundaries are
    /// exercised exactly as a published archive would place them.
    /// </summary>
    private static async Task SeedPairAsync(
        ArchiveQueryHarness harness,
        (string Id, DateTimeOffset OccurredAt) first,
        (string Id, DateTimeOffset OccurredAt) second)
    {
        await harness.Store.UpsertAccountAsync(new ArchiveAccount
        {
            Id = ArchiveQueryHarness.AccountId,
            SourceProfileId = "wxid_fixture",
            AdapterName = "wechat-windows",
            AdapterVersion = "0.1.0",
            SourceVersion = "4.1.13.12",
        }, CancellationToken.None);

        await harness.Store.UpsertConversationsAsync(
        [
            new ArchiveConversation
            {
                Id = ArchiveQueryHarness.GroupConversationId,
                AccountId = ArchiveQueryHarness.AccountId,
                SourceConversationId = ArchiveQueryHarness.GroupSourceId,
                Kind = ConversationKind.Group,
                Title = "Boundary",
            },
        ], CancellationToken.None);

        await harness.Store.UpsertMessagesAsync(
        [
            ArchiveQueryHarness.Message(
                first.Id, ArchiveQueryHarness.GroupConversationId, first.OccurredAt,
                ArchiveQueryHarness.AliceId, CanonicalMessageType.Text, "first boundary", "0001", "l:boundary:1"),
            ArchiveQueryHarness.Message(
                second.Id, ArchiveQueryHarness.GroupConversationId, second.OccurredAt,
                ArchiveQueryHarness.AliceId, CanonicalMessageType.Text, "second boundary", "0002", "l:boundary:2"),
        ], CancellationToken.None);
    }
}
