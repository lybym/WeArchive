using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WeArchive.Core.Collections;
using WeArchive.Core.Domain;
using WeArchive.Core.Query;
using WeArchive.Core.RawVault;
using WeArchive.Core.Services;
using WeArchive.Infrastructure;
using WeArchive.Infrastructure.Archive;
using WeArchive.Infrastructure.RawVault;
using WeArchive.Infrastructure.WeChat;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// M3c rebuild acceptance (issue #104): the statistics of an archive rebuilt offline from
/// preserved Raw Vault evidence alone are identical to those of any other archive rebuilt from the
/// same evidence — statistics derive from canonical rows only, and canonical rebuild reproduces
/// those rows deterministically (docs/PRD.md FR-11, docs/DATA_MODEL.md section 24).
/// <para>
/// The fixture follows the established <c>RebuildServiceTests</c> pattern: a synthetic plaintext
/// WeChat shard set is captured into a real Raw Vault and rebuilt through the shipped
/// <see cref="RebuildService"/>. It contains no real chat data.
/// </para>
/// </summary>
public sealed class ArchiveStatisticsRebuildTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 3, 1, 12, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task StatisticsAreIdenticalAfterOfflineRebuild()
    {
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"), writeLegacy: true);
        await PublishGenerationAsync(vault, temp.Path, messageCount: 6);

        var firstPath = temp.Combine("first", "wearchive.db");
        var secondPath = temp.Combine("second", "wearchive.db");
        await new RebuildService(vault, firstPath, new FixedClock()).RebuildAsync(null, CancellationToken.None);
        await new RebuildService(vault, secondPath, new FixedClock()).RebuildAsync(null, CancellationToken.None);

        var first = await StatisticsAsync(firstPath);
        var second = await StatisticsAsync(secondPath);

        Assert.Equal(6, first.Totals.MessageCount);
        Assert.Equal(1, first.Totals.ConversationCount);
        Assert.Equal(0, first.Totals.UnknownCount);
        Assert.Equal(0, first.Totals.PartialCount);

        // Every statistics section is derived from canonical rows, and a deterministic rebuild
        // reproduces those rows — so the two reports agree field by field.
        Assert.Equal(first.Totals, second.Totals);
        Assert.Equal(
            first.ByType.Select(type => (type.Type, type.Count)).ToArray(),
            second.ByType.Select(type => (type.Type, type.Count)).ToArray());
        Assert.Equal(
            first.BreakdownItems.Select(row => (row.BucketStart, row.ConversationId, row.MessageCount, row.UnknownCount, row.PartialCount)).ToArray(),
            second.BreakdownItems.Select(row => (row.BucketStart, row.ConversationId, row.MessageCount, row.UnknownCount, row.PartialCount)).ToArray());

        // The rebuild fixture is single-conversation text over a few seconds: one day bucket.
        var bucket = Assert.Single(first.BreakdownItems);
        Assert.Equal(6, bucket.MessageCount);
    }

    private static async Task<ArchiveStatistics> StatisticsAsync(string archivePath)
    {
        var clock = new FixedClock();
        var store = new SqliteArchiveStore(archivePath, clock);
        var service = new ArchiveQueryService(
            store,
            new FakeRawVaultStore(),
            new RawVaultIngestService(new ThrowingRawVaultStore(), store, clock),
            new CollectionCatalogService(new StubCollectionCatalogSource()),
            clock);

        return await service.GetStatisticsAsync(new StatisticsRequest(), CancellationToken.None);
    }

    /// <summary>
    /// Publishes one synthetic WeChat generation with a direct conversation of plain-text
    /// messages — the minimal shard set the shipped rebuild reader requires.
    /// </summary>
    private static async Task<RawGeneration> PublishGenerationAsync(
        RawVaultStore vault,
        string scratch,
        int messageCount)
    {
        const string profile = "wxid_stats_fixture";
        const string conversationId = "wxid_peer";
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, profile);
        var dbRoot = Path.Combine(scratch, "db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dbRoot);
        var table = "Msg_" + Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(conversationId))).ToLowerInvariant();
        var messageRows = string.Join(Environment.NewLine, Enumerable.Range(1, messageCount)
            .Select(id => $"INSERT INTO \"{table}\" VALUES ({id}, {7000 + id}, 1, 1, {1736907600 + id}, 'stats rebuild fixture {id}', 0, NULL);"));

        BuildDb(Path.Combine(dbRoot, "session.db"), $"""
            CREATE TABLE SessionTable (username TEXT PRIMARY KEY, sort_timestamp INTEGER, last_timestamp INTEGER, last_msg_type INTEGER, last_msg_sub_type INTEGER, summary TEXT);
            INSERT INTO SessionTable VALUES ('{conversationId}', 1737244800, 1737244800, 1, 0, NULL);
            """);
        BuildDb(Path.Combine(dbRoot, "contact.db"), """
            CREATE TABLE contact (username TEXT PRIMARY KEY, remark TEXT, nick_name TEXT, alias TEXT, local_type INTEGER);
            INSERT INTO contact VALUES ('wxid_peer', 'Peer', 'Peer', NULL, 1);
            CREATE TABLE stranger (username TEXT PRIMARY KEY, nick_name TEXT);
            """);
        BuildDb(Path.Combine(dbRoot, "message_0.db"), $"""
            CREATE TABLE Name2Id (rowid INTEGER PRIMARY KEY, user_name TEXT);
            INSERT INTO Name2Id VALUES (1, '{conversationId}');
            CREATE TABLE "{table}" (local_id INTEGER PRIMARY KEY, server_id INTEGER, local_type INTEGER, real_sender_id INTEGER, create_time INTEGER, message_content BLOB, WCDB_CT_message_content INTEGER, compress_content BLOB);
            {messageRows}
            """);

        var context = new RawGenerationContext
        {
            AccountId = accountId,
            SourceProfileId = profile,
            CaptureTime = CapturedAt,
            CaptureAdapterFamily = WeChatCaptureAdapter.Family,
            CaptureAdapterVersion = "0.1.0",
        };
        var session = await vault.BeginGenerationAsync(context, CancellationToken.None);
        var artifacts = new List<RawArtifactDescriptor>();
        foreach (var (name, relative, path) in new[]
        {
            ("session.db", "session/session.db", Path.Combine(dbRoot, "session.db")),
            ("contact.db", "contact/contact.db", Path.Combine(dbRoot, "contact.db")),
            ("message_0.db", "message/message_0.db", Path.Combine(dbRoot, "message_0.db")),
        })
        {
            await using var stream = File.OpenRead(path);
            artifacts.Add(await session.WriteArtifactAsync("source-database", name, stream, "sqlite", true,
                new Dictionary<string, string> { ["source_relative_path"] = relative }, CancellationToken.None));
        }

        var manifest = new RawManifest
        {
            GenerationId = session.GenerationId,
            AccountId = accountId,
            SourceProfileId = profile,
            Source = new RawManifestSource
            {
                AdapterName = WeChatWindowsSourceAdapter.Name,
                AdapterVersion = "0.1.0",
                SourceProductName = "WeChat for Windows",
                SourceVersion = "4.1.13.12",
            },
            Capture = new RawManifestCapture
            {
                CaptureTime = CapturedAt,
                CaptureAdapterFamily = WeChatCaptureAdapter.Family,
                CaptureAdapterVersion = "0.1.0",
                Mode = RawCaptureMode.Baseline,
                Completeness = RawGenerationCompleteness.Complete,
                ArtifactCount = artifacts.Count,
            },
            Artifacts = artifacts,
            Coverage = [],
        };
        var result = await session.PublishAsync(manifest, CancellationToken.None);
        await session.DisposeAsync();
        Directory.Delete(dbRoot, recursive: true);
        return result;
    }

    private static void BuildDb(string path, string sql)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
