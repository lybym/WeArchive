using System.Diagnostics;
using WeArchive.Core.Collections;
using WeArchive.Core.Domain;
using WeArchive.Core.Query;
using WeArchive.Core.Services;
using WeArchive.Infrastructure;
using WeArchive.Infrastructure.Archive;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// M3c representative-archive performance acceptance (issue #104): computing one statistics report
/// over a representative archive through the shipped store completes within a generous budget and
/// the breakdown stays a bounded page.
/// <para>
/// The corpus is small enough for CI (6,000 messages through the real store write path) while
/// proving the budget that matters: statistics are a constant number of linear passes over the
/// counted rows — no per-message materialization, no unbounded result. The derived FTS index is
/// irrelevant to this budget and is neither read nor maintained here.
/// </para>
/// </summary>
public sealed class ArchiveStatisticsPerformanceTests
{
    private const int ConversationCount = 6;
    private const int MessagesPerConversation = 1_000;
    private const int TotalMessages = ConversationCount * MessagesPerConversation;

    /// <summary>Generous wall-clock budget for one statistics read on shared CI hardware.</summary>
    private static readonly TimeSpan StatisticsBudget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RepresentativeArchiveStatisticsCompleteWithinTheBudgetAndStayBounded()
    {
        using var temp = new TempDirectory();
        var clock = new FixedClock();
        var store = new SqliteArchiveStore(temp.Combine("archive", "wearchive.db"), clock);
        var messages = await SeedAsync(store);

        var service = new ArchiveQueryService(
            store,
            new FakeRawVaultStore(),
            new RawVaultIngestService(new ThrowingRawVaultStore(), store, clock),
            new CollectionCatalogService(new StubCollectionCatalogSource()),
            clock);

        var watch = Stopwatch.StartNew();
        var statistics = await service.GetStatisticsAsync(new StatisticsRequest(), CancellationToken.None);
        watch.Stop();

        // The totals describe exactly what was written — the counts are correct on a corpus large
        // enough that a wrong filter or an accidental materialization would show.
        Assert.Equal(TotalMessages, statistics.Totals.MessageCount);
        Assert.Equal(ConversationCount, statistics.Totals.ConversationCount);
        Assert.Equal(
            messages.Count(message => message.Type == CanonicalMessageType.Text),
            statistics.ByType.Single(type => type.Type == "text").Count);
        Assert.Equal(messages.Count(message => message.IsPartial), statistics.Totals.PartialCount);
        Assert.Equal(messages.Count(message => message.SenderId is null), statistics.Totals.UnattributedCount);

        // The breakdown is a bounded page, not a materialized corpus.
        Assert.True(statistics.BreakdownItems.Count <= ArchiveQueryService.MaxLimit);

        Assert.True(
            watch.Elapsed < StatisticsBudget,
            $"statistics over {TotalMessages} messages took {watch.Elapsed.TotalMilliseconds:F0} ms; budget is {StatisticsBudget.TotalSeconds:F0} s.");
    }

    /// <summary>
    /// Seeds a deterministic corpus through the real store write path: several conversations,
    /// senders, canonical types, partial records, unattributed records and a 30-day spread.
    /// </summary>
    private static async Task<IReadOnlyList<CanonicalMessage>> SeedAsync(SqliteArchiveStore store)
    {
        var accountId = "a_00000000000000p1";
        await store.UpsertAccountAsync(new ArchiveAccount
        {
            Id = accountId,
            SourceProfileId = "wxid_perf_fixture",
            AdapterName = "wechat-windows",
            AdapterVersion = "0.1.0",
            SourceVersion = "4.1.13.12",
        }, CancellationToken.None);

        var conversations = Enumerable.Range(0, ConversationCount)
            .Select(index => new ArchiveConversation
            {
                Id = $"g_00000000000000{index:x2}",
                AccountId = accountId,
                SourceConversationId = $"{index}@perf",
                Kind = ConversationKind.Group,
                Title = $"Perf {index}",
            })
            .ToArray();
        await store.UpsertConversationsAsync(conversations, CancellationToken.None);

        var types = new[]
        {
            CanonicalMessageType.Text,
            CanonicalMessageType.Image,
            CanonicalMessageType.Voice,
            CanonicalMessageType.Link,
            CanonicalMessageType.File,
        };
        var senders = new[]
        {
            "u_00000000000000a1",
            "u_00000000000000b2",
            "u_00000000000000c3",
        };

        var messages = new List<CanonicalMessage>(TotalMessages);
        for (var index = 0; index < TotalMessages; index++)
        {
            var conversation = conversations[index % ConversationCount];
            var day = index % 30;
            messages.Add(new CanonicalMessage
            {
                Id = $"m_{index:X16}",
                ConversationId = conversation.Id,
                SenderId = index % 50 == 49 ? null : senders[index % senders.Length],
                OccurredAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    .AddDays(day)
                    .AddSeconds(index / 30),
                Type = types[index % types.Length],
                Text = $"perf message {index}",
                IsPartial = index % 97 == 0,
                Source = new SourceProvenance
                {
                    SourceProfileId = "wxid_perf_fixture",
                    SourceConversationId = conversation.SourceConversationId,
                    SourceMessageId = $"s:{index}",
                    SourceType = "1",
                    SourcePartition = "message_0",
                    SourceOrderKey = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    AdapterName = "wechat-windows",
                    AdapterVersion = "0.1.0",
                    SourceVersion = "4.1.13.12",
                },
            });
        }

        foreach (var batch in messages.Chunk(500))
        {
            await store.UpsertMessagesAsync(batch, CancellationToken.None);
        }

        return messages;
    }
}
