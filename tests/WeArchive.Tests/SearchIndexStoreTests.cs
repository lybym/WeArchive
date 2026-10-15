using System.Text.Json;
using Microsoft.Data.Sqlite;
using WeArchive.Core.Domain;
using WeArchive.Core.Query;
using WeArchive.Core.Search;
using WeArchive.Infrastructure;
using WeArchive.Infrastructure.Archive;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// The derived FTS search index at the archive persistence boundary ([Issue #102](
/// https://github.com/lybym/WeArchive/issues/102), M3b-1, docs/SEARCH_SPEC.md sections 3–4 and 10,
/// docs/DATA_MODEL.md section 19).
/// <para>
/// Covers the migration/backfill from the previous schema version, same-transaction maintenance
/// for insert/update/unchanged replays, deterministic rebuild from canonical rows only, the
/// maintenance verification, and the fail-closed failure injection: a missing or corrupted index is
/// diagnosed and repaired without ever damaging the canonical archive.
/// </para>
/// </summary>
public sealed class SearchIndexStoreTests
{
    private static DateTimeOffset When => FixedClock.Default;

    [Fact]
    public async Task FreshArchiveHasTheSearchIndexAndMigrationThreeIsRecorded()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("archive.db");
        var store = new SqliteArchiveStore(path, new FixedClock());
        await store.InitializeAsync(CancellationToken.None);

        AssertMigrationState(path, expectedMigrations: 3, expectedUserVersion: 3);
        using var connection = OpenArchive(path);
        Assert.True(ArchiveSearchIndex.TableExists(connection));
    }

    [Fact]
    public async Task MigrationTwoArchiveUpgradesAndBackfillsTheIndexFromCanonicalRows()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("archive.db");
        var store = await SearchIndexFixture.CreateSeededArchiveAsync(path);
        var before = await store.ReadMessagesAsync(SearchIndexFixture.GroupConversationId, CancellationToken.None);

        // Simulate a migration-2 archive: the index slice has not run on it yet.
        DemoteToSchemaVersionTwo(path);

        var upgraded = new SqliteArchiveStore(path, new FixedClock());
        await upgraded.InitializeAsync(CancellationToken.None);

        AssertMigrationState(path, expectedMigrations: 3, expectedUserVersion: 3);
        using (var connection = OpenArchive(path))
        {
            Assert.True(ArchiveSearchIndex.TableExists(connection));
            Assert.Equal(
                MessageCount(connection),
                ArchiveSearchIndex.CountRows(connection));
        }

        // The backfilled index is immediately searchable and matches the canonical rows it was
        // derived from, and no canonical row was touched by the migration.
        var page = await upgraded.SearchMessagesAsync(new ArchiveSearchQuery
        {
            MatchExpression = SearchQueryCompiler.CompileQuery("模型"),
            Limit = 100,
        }, CancellationToken.None);
        Assert.Contains(page.Hits, hit => hit.Message.Id == "text_model_launch");
        var after = await upgraded.ReadMessagesAsync(SearchIndexFixture.GroupConversationId, CancellationToken.None);
        AssertMessagesUnchanged(before, after);
    }

    [Fact]
    public async Task MigrationUpgradeIsIdempotent()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("archive.db");
        await SearchIndexFixture.CreateSeededArchiveAsync(path);
        DemoteToSchemaVersionTwo(path);

        var store = new SqliteArchiveStore(path, new FixedClock());
        await store.InitializeAsync(CancellationToken.None);
        await store.InitializeAsync(CancellationToken.None);

        AssertMigrationState(path, expectedMigrations: 3, expectedUserVersion: 3);
        using var connection = OpenArchive(path);
        Assert.Equal(
            MessageCount(connection),
            ArchiveSearchIndex.CountRows(connection));
    }

    [Fact]
    public async Task InsertAndUpdateMaintainTheIndexInTheSameTransaction()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("archive.db");
        var store = new SqliteArchiveStore(path, new FixedClock());
        await store.InitializeAsync(CancellationToken.None);
        await SeedAccountAsync(store);
        await store.UpsertConversationsAsync([Conversation("conversation-1")], CancellationToken.None);

        await store.UpsertMessagesAsync(
        [
            TextMessage("message-1", "conversation-1", "大模型上线了", orderKey: "0001"),
        ], CancellationToken.None);
        AssertSingleHit(store, "模型", "message-1");
        AssertIndexAgreesWithMessages(store, path);

        // A semantic update replaces the index row: the old terms stop matching, the new ones match.
        await store.UpsertMessagesAsync(
        [
            TextMessage("message-1", "conversation-1", "玩具收拾一下", orderKey: "0001"),
        ], CancellationToken.None);
        var page = await store.SearchMessagesAsync(new ArchiveSearchQuery
        {
            MatchExpression = SearchQueryCompiler.CompileQuery("模型"),
            ConversationId = "conversation-1",
            Limit = 100,
        }, CancellationToken.None);
        Assert.Empty(page.Hits);
        AssertSingleHit(store, "玩具", "message-1");
        AssertIndexAgreesWithMessages(store, path);

        // An unchanged replay writes nothing: the index still has exactly one row for the message.
        await store.UpsertMessagesAsync(
        [
            TextMessage("message-1", "conversation-1", "玩具收拾一下", orderKey: "0001"),
        ], CancellationToken.None);
        AssertIndexAgreesWithMessages(store, path);
        AssertSingleHit(store, "玩具", "message-1");
    }

    [Fact]
    public async Task StagedImportPublishesIndexRowsOnlyWithTheConversation()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("archive.db");
        var store = new SqliteArchiveStore(path, new FixedClock());
        await store.InitializeAsync(CancellationToken.None);
        await SeedAccountAsync(store);

        // A staged (uncommitted) conversation is invisible to search on another connection.
        await using (var session = await store.BeginConversationImportAsync(
            Conversation("conversation-staged"), CancellationToken.None))
        {
            await session.UpsertMessagesAsync(
            [
                TextMessage("staged-1", "conversation-staged", "下午三点开会", orderKey: "0001"),
            ], CancellationToken.None);
            var beforeCommit = await store.SearchMessagesAsync(new ArchiveSearchQuery
            {
                MatchExpression = SearchQueryCompiler.CompileQuery("开会"),
                Limit = 100,
            }, CancellationToken.None);
            Assert.Empty(beforeCommit.Hits);
            await session.CommitAsync(CancellationToken.None);
        }

        AssertSingleHit(store, "开会", "staged-1");
        AssertIndexAgreesWithMessages(store, path);

        // A rolled-back session publishes neither the canonical rows nor their index rows.
        await using (var session = await store.BeginConversationImportAsync(
            Conversation("conversation-rolled-back"), CancellationToken.None))
        {
            await session.UpsertMessagesAsync(
            [
                TextMessage("rolled-back-1", "conversation-rolled-back", "周五前给结论", orderKey: "0001"),
            ], CancellationToken.None);
            await session.RollbackAsync();
        }

        var afterRollback = await store.SearchMessagesAsync(new ArchiveSearchQuery
        {
            MatchExpression = SearchQueryCompiler.CompileQuery("周五"),
            Limit = 100,
        }, CancellationToken.None);
        Assert.Empty(afterRollback.Hits);
        AssertIndexAgreesWithMessages(store, path);
    }

    [Fact]
    public async Task RebuildIsDeterministicAndEquivalentToTheIncrementalIndex()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("archive.db");
        var store = await SearchIndexFixture.CreateSeededArchiveAsync(path);

        // Incrementally update one message after the initial ingest, so the live index reflects a
        // mix of inserts and an in-place replacement.
        var updated = SearchIndexFixture.CorpusMessages()
            .First(message => message.Id == "text_model_launch") with
        {
            Text = "大模型上线了，记得看周报和评审安排",
        };
        await store.UpsertMessagesAsync([updated], CancellationToken.None);

        var expected = await MatchSetsAsync(store);

        // A full rebuild from canonical rows yields the same match sets.
        await store.RebuildSearchIndexAsync(CancellationToken.None);
        Assert.Equal(expected, await MatchSetsAsync(store), MatchSetComparer.Instance);

        // Re-running the rebuild is deterministic.
        await store.RebuildSearchIndexAsync(CancellationToken.None);
        Assert.Equal(expected, await MatchSetsAsync(store), MatchSetComparer.Instance);

        // An archive built from scratch in one write yields the same match sets as the
        // incrementally maintained one (from-scratch rebuild vs incremental updates).
        using var scratchTemp = new TempDirectory();
        var scratch = new SqliteArchiveStore(scratchTemp.Combine("scratch.db"), new FixedClock());
        await scratch.InitializeAsync(CancellationToken.None);
        await SeedAccountAsync(scratch);
        await scratch.UpsertConversationsAsync(
        [
            Conversation(SearchIndexFixture.DirectConversationId, "1000001@chatroom", ConversationKind.Group),
            Conversation(SearchIndexFixture.GroupConversationId, "1000002@chatroom", ConversationKind.Group),
            Conversation(SearchIndexFixture.OfficialConversationId, "official-account-1", ConversationKind.Direct),
        ], CancellationToken.None);
        var scratchMessages = SearchIndexFixture.CorpusMessages()
            .Select(message => message.Id == "text_model_launch" ? updated : message)
            .ToList();
        await scratch.UpsertMessagesAsync(scratchMessages, CancellationToken.None);

        Assert.Equal(expected, await MatchSetsAsync(scratch), MatchSetComparer.Instance);

        // Every index shape passes the FTS5 integrity-check after maintenance and rebuilds.
        Assert.True((await store.VerifySearchIndexAsync(CancellationToken.None)).IsHealthy);
        Assert.True((await scratch.VerifySearchIndexAsync(CancellationToken.None)).IsHealthy);
    }

    [Fact]
    public async Task VerificationReportsHealthyAfterNormalOperationAndUnhealthyWhenTheIndexIsMissing()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("archive.db");
        var store = await SearchIndexFixture.CreateSeededArchiveAsync(path);

        var healthy = await store.VerifySearchIndexAsync(CancellationToken.None);
        Assert.True(healthy.IsHealthy);
        Assert.Null(healthy.Reason);

        using (var connection = OpenArchive(path))
        using (var drop = connection.CreateCommand())
        {
            drop.CommandText = "DROP TABLE messages_fts;";
            drop.ExecuteNonQuery();
        }

        var missing = await store.VerifySearchIndexAsync(CancellationToken.None);
        Assert.False(missing.IsHealthy);
        Assert.NotNull(missing.Reason);
    }

    [Fact]
    public async Task DeletedIndexRowsAreDiagnosedAndRebuiltWithoutCanonicalDamage()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("archive.db");
        var store = await SearchIndexFixture.CreateSeededArchiveAsync(path);
        var canonicalBefore = await AllCanonicalRowsAsync(store);
        var expected = await MatchSetsAsync(store);

        // Failure injection: the index loses rows while canonical data stays.
        using (var connection = OpenArchive(path))
        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM messages_fts;";
            delete.ExecuteNonQuery();
        }

        // Search fails closed instead of returning a partial result set.
        var failure = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => store.SearchMessagesAsync(new ArchiveSearchQuery
            {
                MatchExpression = SearchQueryCompiler.CompileQuery("模型"),
                Limit = 100,
            }, CancellationToken.None));
        Assert.Equal(ArchiveQueryErrorCodes.SearchIndexUnavailable, failure.Code);

        var verification = await store.VerifySearchIndexAsync(CancellationToken.None);
        Assert.False(verification.IsHealthy);
        Assert.NotNull(verification.Reason);

        // Recovery is the explicit rebuild; it restores the exact search behavior without
        // touching canonical rows.
        await store.RebuildSearchIndexAsync(CancellationToken.None);
        Assert.True((await store.VerifySearchIndexAsync(CancellationToken.None)).IsHealthy);
        Assert.Equal(expected, await MatchSetsAsync(store), MatchSetComparer.Instance);
        AssertMessagesUnchanged(canonicalBefore, await AllCanonicalRowsAsync(store));
    }

    [Fact]
    public async Task TamperedIndexContentIsDiagnosedByVerificationAndRebuildRepairsIt()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("archive.db");
        var store = await SearchIndexFixture.CreateSeededArchiveAsync(path);
        var canonicalBefore = await AllCanonicalRowsAsync(store);
        var expected = await MatchSetsAsync(store);

        // Failure injection: the stored index content is tampered without touching the term index,
        // so row counts still agree and matching still "works"; only the FTS5 integrity-check can
        // diagnose the inconsistency. The %_content shadow table stores the body in column c0 and
        // is keyed by the FTS rowid (rows are assigned 1..N in insertion order).
        using (var connection = OpenArchive(path))
        using (var tamper = connection.CreateCommand())
        {
            tamper.CommandText =
                "UPDATE messages_fts_content SET c0 = 'tampered placeholder body' WHERE id = 1;";
            tamper.ExecuteNonQuery();
        }

        var verification = await store.VerifySearchIndexAsync(CancellationToken.None);
        Assert.False(verification.IsHealthy);
        Assert.NotNull(verification.Reason);

        await store.RebuildSearchIndexAsync(CancellationToken.None);
        Assert.True((await store.VerifySearchIndexAsync(CancellationToken.None)).IsHealthy);
        Assert.Equal(expected, await MatchSetsAsync(store), MatchSetComparer.Instance);
        AssertMessagesUnchanged(canonicalBefore, await AllCanonicalRowsAsync(store));
    }

    [Fact]
    public async Task RebuildOnAnArchiveWithoutTheIndexRestoresIt()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("archive.db");
        var store = await SearchIndexFixture.CreateSeededArchiveAsync(path);
        var expected = await MatchSetsAsync(store);

        using (var connection = OpenArchive(path))
        using (var drop = connection.CreateCommand())
        {
            drop.CommandText = "DROP TABLE messages_fts;";
            drop.ExecuteNonQuery();
        }

        await store.RebuildSearchIndexAsync(CancellationToken.None);
        Assert.True((await store.VerifySearchIndexAsync(CancellationToken.None)).IsHealthy);
        Assert.Equal(expected, await MatchSetsAsync(store), MatchSetComparer.Instance);
    }

    [Fact]
    public async Task SearchNeverTouchesRawVaultOrSourceState()
    {
        // Structural guarantee: the search members exist on the store contract alone; they read
        // the archive file and nothing else. This test pins that a search round-trip performs no
        // write of any persistent state besides the derived index it may verify.
        using var temp = new TempDirectory();
        var path = temp.Combine("archive.db");
        var store = await SearchIndexFixture.CreateSeededArchiveAsync(path);

        var statsBefore = await store.GetArchiveStatsAsync(CancellationToken.None);
        await store.SearchMessagesAsync(new ArchiveSearchQuery
        {
            MatchExpression = SearchQueryCompiler.CompileQuery("模型"),
            Limit = 100,
        }, CancellationToken.None);
        var statsAfter = await store.GetArchiveStatsAsync(CancellationToken.None);

        Assert.Equal(statsBefore.MessageCount, statsAfter.MessageCount);
        Assert.Equal(statsBefore.ConversationCount, statsAfter.ConversationCount);
    }

    [Fact]
    public async Task RepairReplayAndMetadataRefreshPathsKeepTheIndexConsistent()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("archive.db");
        var store = new SqliteArchiveStore(path, new FixedClock());
        await store.InitializeAsync(CancellationToken.None);
        await SeedAccountAsync(store);

        var before = await store.SearchMessagesAsync(new ArchiveSearchQuery
        {
            MatchExpression = SearchQueryCompiler.CompileQuery("开会"),
            Limit = 100,
        }, CancellationToken.None);
        Assert.Empty(before.Hits);

        await using (var session = await store.BeginConversationImportAsync(
            Conversation("conversation-1"), CancellationToken.None))
        {
            await session.UpsertMessagesAsync(
            [
                TextMessage("message-1", "conversation-1", "下午三点开会", orderKey: "0001"),
                TextMessage("message-2", "conversation-1", "收到", orderKey: "0002",
                    replyToSourceId: "src:source:conversation-1:1"),
            ], CancellationToken.None);
            await session.CommitAsync(CancellationToken.None);
        }

        // The repair/replay path resolves reply targets by updating canonical rows; the resolved
        // id is not semantic content and is not indexed, so the index stays consistent.
        var resolved = await store.ResolveReplyTargetsAsync("conversation-1", CancellationToken.None);
        Assert.Equal(0, resolved); // the fixture's reply targets at source granularity do not resolve

        // A metadata refresh (participant display names) writes no message row at all.
        await store.UpsertParticipantsAsync(
        [
            new ArchiveParticipant
            {
                Id = SearchIndexFixture.AliceId,
                AccountId = SearchIndexFixture.AccountId,
                SourceParticipantId = "alice-source",
                UserDisplayName = "Alice (fixture)",
            },
        ], CancellationToken.None);

        Assert.True((await store.VerifySearchIndexAsync(CancellationToken.None)).IsHealthy);
        AssertIndexAgreesWithMessages(store, path);
        AssertSingleHit(store, "开会", "message-1");

        // A genuine replay of the same records (hash unchanged) leaves the search results intact.
        await using (var session = await store.BeginConversationImportAsync(
            Conversation("conversation-1"), CancellationToken.None))
        {
            await session.UpsertMessagesAsync(
            [
                TextMessage("message-1", "conversation-1", "下午三点开会", orderKey: "0001"),
                TextMessage("message-2", "conversation-1", "收到", orderKey: "0002",
                    replyToSourceId: "src:source:conversation-1:1"),
            ], CancellationToken.None);
            await session.CommitAsync(CancellationToken.None);
        }

        Assert.True((await store.VerifySearchIndexAsync(CancellationToken.None)).IsHealthy);
        AssertSingleHit(store, "开会", "message-1");
    }

    // ---- helpers ---------------------------------------------------------------

    private static ArchiveConversation Conversation(
        string id,
        string? sourceId = null,
        ConversationKind kind = ConversationKind.Group) => new()
    {
        Id = id,
        AccountId = SearchIndexFixture.AccountId,
        SourceConversationId = sourceId ?? $"source:{id}",
        Kind = kind,
    };

    /// <summary>Seeds the fixture account, so conversations can reference it.</summary>
    private static async Task SeedAccountAsync(SqliteArchiveStore store) =>
        await store.UpsertAccountAsync(new ArchiveAccount
        {
            Id = SearchIndexFixture.AccountId,
            SourceProfileId = "wxid_search_fixture",
            AdapterName = "wechat-windows",
            AdapterVersion = "0.1.0",
        }, CancellationToken.None);

    private static CanonicalMessage TextMessage(
        string id, string conversationId, string text, string orderKey, string? replyToSourceId = null) => new()
    {
        Id = id,
        ConversationId = conversationId,
        SenderId = SearchIndexFixture.AliceId,
        OccurredAt = When,
        Type = CanonicalMessageType.Text,
        Text = text,
        ReplyTo = replyToSourceId is null
            ? null
            : new ReplyReference { SourceMessageId = replyToSourceId, SenderId = SearchIndexFixture.AliceId, Text = "收到" },
        Source = new SourceProvenance
        {
            SourceProfileId = "wxid_fixture",
            SourceConversationId = $"source:{conversationId}",
            SourceMessageId = $"src:{id}",
            SourcePartition = "message_0",
            SourceOrderKey = orderKey,
            AdapterName = "wechat-windows",
            AdapterVersion = "0.1.0",
        },
    };

    private static SqliteConnection OpenArchive(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    private static long MessageCount(SqliteConnection connection) =>
        Convert.ToInt64(
            new SqliteCommand("SELECT COUNT(*) FROM messages;", connection).ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);

    private static void AssertMigrationState(string path, int expectedMigrations, int expectedUserVersion)
    {
        using var connection = OpenArchive(path);
        using var migrations = new SqliteCommand("SELECT COUNT(*) FROM schema_migrations;", connection);
        Assert.Equal(
            expectedMigrations,
            Convert.ToInt32(migrations.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        using var version = new SqliteCommand("PRAGMA user_version;", connection);
        Assert.Equal(
            expectedUserVersion,
            Convert.ToInt32(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void DemoteToSchemaVersionTwo(string path)
    {
        using var connection = OpenArchive(path);
        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM schema_migrations WHERE version=3; DROP TABLE messages_fts; PRAGMA user_version=2;";
        command.ExecuteNonQuery();
    }

    private static void AssertIndexAgreesWithMessages(SqliteArchiveStore store, string path)
    {
        var verification = store.VerifySearchIndexAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(verification.IsHealthy, verification.Reason ?? "index unhealthy");
        using var connection = OpenArchive(path);
        Assert.Equal(MessageCount(connection), ArchiveSearchIndex.CountRows(connection));
    }

    private static void AssertSingleHit(SqliteArchiveStore store, string query, string expectedMessageId)
    {
        var page = store.SearchMessagesAsync(new ArchiveSearchQuery
        {
            MatchExpression = SearchQueryCompiler.CompileQuery(query),
            Limit = 100,
        }, CancellationToken.None).GetAwaiter().GetResult();
        var hit = Assert.Single(page.Hits);
        Assert.Equal(expectedMessageId, hit.Message.Id);
    }

    /// <summary>
    /// The per-probe match sets of the benchmark corpus, run through the real archive search path.
    /// Message ids are corpus document ids, so the sets are directly comparable with the ground
    /// truth of <see cref="SearchBenchmarkCorpus"/>. Only probes with a non-empty relevant set are
    /// matched; zero-relevance probes assert emptiness.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, IReadOnlySet<string>>> MatchSetsAsync(SqliteArchiveStore store)
    {
        var result = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var probe in SearchBenchmarkCorpus.ProbeQueries)
        {
            var page = await store.SearchMessagesAsync(new ArchiveSearchQuery
            {
                MatchExpression = SearchQueryCompiler.CompileQuery(probe.Query),
                Limit = 500,
            }, CancellationToken.None);
            result[probe.QueryId] = page.Hits.Select(hit => hit.Message.Id).ToHashSet(StringComparer.Ordinal);
        }

        return result;
    }

    private static async Task<IReadOnlyList<CanonicalMessage>> AllCanonicalRowsAsync(SqliteArchiveStore store)
    {
        var rows = new List<CanonicalMessage>();
        foreach (var conversationId in SearchIndexFixture.ConversationIds.Values)
        {
            rows.AddRange(await store.ReadMessagesAsync(conversationId, CancellationToken.None));
        }

        return [.. rows.OrderBy(message => message.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Deep equality over the canonical rows, compared through their serialized canonical shape so
    /// a payload/semantic/provenance mutation cannot hide behind reference equality.
    /// </summary>
    private static void AssertMessagesUnchanged(IReadOnlyList<CanonicalMessage> before, IReadOnlyList<CanonicalMessage> after)
    {
        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(
                JsonSerializer.Serialize(before[i], CanonicalJsonOrder),
                JsonSerializer.Serialize(after[i], CanonicalJsonOrder));
        }
    }

    private static readonly JsonSerializerOptions CanonicalJsonOrder = new()
    {
        WriteIndented = false,
    };

    private sealed class MatchSetComparer : IEqualityComparer<IReadOnlyDictionary<string, IReadOnlySet<string>>>
    {
        public static readonly MatchSetComparer Instance = new();

        public bool Equals(
            IReadOnlyDictionary<string, IReadOnlySet<string>>? left,
            IReadOnlyDictionary<string, IReadOnlySet<string>>? right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left is null || right is null || left.Count != right.Count)
                return false;

            foreach (var (queryId, expected) in left)
            {
                if (!right.TryGetValue(queryId, out var actual) || !expected.SetEquals(actual))
                    return false;
            }

            return true;
        }

        public int GetHashCode(IReadOnlyDictionary<string, IReadOnlySet<string>> _) => 0;
    }
}
