using Microsoft.Data.Sqlite;
using WeArchive.Core.Domain;
using WeArchive.Infrastructure.WeChat;
using WeArchive.Infrastructure.WeChat.Compatibility;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// Issue #37 acceptance criterion "the capture-required set and rebuild-required set are
/// consistent for the supported WeChat 4.x canonical rebuild contract".
/// <para>
/// WeChat 4.x splits conversation tables across the <c>message_&lt;n&gt;.db</c> family and the
/// <c>biz_message_&lt;n&gt;.db</c> family, which is where official-account (<c>gh_</c>)
/// conversations live. Both the live locator and the captured-source reader must recognise both
/// families, otherwise a complete capture still cannot be read back.
/// </para>
/// </summary>
public sealed class WeChatMessageShardTests
{
    [Theory]
    [InlineData("message_0.db", true)]
    [InlineData("message_12.db", true)]
    [InlineData("MESSAGE_0.DB", true)]
    [InlineData("biz_message_0.db", true)]
    [InlineData("Biz_Message_3.DB", true)]
    // Kept by the broad message_ prefix that discovery already accepted; these hold no
    // conversation tables, so they only contribute none.
    [InlineData("message_fts.db", true)]
    [InlineData("message_resource.db", true)]
    // Preserved as evidence, but never a message shard.
    [InlineData("media_0.db", false)]
    [InlineData("weclaw.db", false)]
    public void TheMessageShardDefinitionCoversBothConversationShardFamilies(string fileName, bool expected) =>
        Assert.Equal(expected, WeChatDataLocator.IsMessageShardFileName(fileName));

    [Fact]
    public void MessageShardDiscoveryCoversTheBizMessageFamily()
    {
        using var temp = new TempDirectory();
        var storage = temp.Combine("db_storage");
        var messageDirectory = Path.Combine(storage, "message");
        Directory.CreateDirectory(messageDirectory);
        foreach (var name in new[]
        {
            "message_0.db", "message_1.db", "biz_message_0.db", "biz_message_1.db",
            "media_0.db", "message_fts.db", "message_resource.db", "weclaw.db",
        })
        {
            File.WriteAllText(Path.Combine(messageDirectory, name), name);
        }

        var account = new WeChatAccountLocation("wxid_test", temp.Path, storage, null);

        Assert.Equal(
            new[]
            {
                "biz_message_0.db", "biz_message_1.db", "message_0.db", "message_1.db",
                "message_fts.db", "message_resource.db",
            },
            WeChatDataLocator.MessageDatabases(account).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void LiveReaderReadsAnOfficialAccountConversationFromTheBizMessageShard()
    {
        using var temp = new TempDirectory();
        var storage = temp.Combine("db_storage");
        Directory.CreateDirectory(Path.Combine(storage, "session"));
        Directory.CreateDirectory(Path.Combine(storage, "contact"));
        Directory.CreateDirectory(Path.Combine(storage, "message"));

        // A synthetic official-account conversation: its only message table lives in the
        // biz_message_ family, so a reader that ignores that family reports no shard at all.
        const string conversationId = "gh_synthetic_official";
        var table = WeChat4Schema.MessageTableName(conversationId);
        Build(Path.Combine(storage, "session", "session.db"), $"""
            CREATE TABLE {WeChat4Schema.SessionTable} (username TEXT PRIMARY KEY, sort_timestamp INTEGER, last_timestamp INTEGER, last_msg_type INTEGER, last_msg_sub_type INTEGER, summary TEXT);
            INSERT INTO {WeChat4Schema.SessionTable} VALUES ('{conversationId}', 1737244800, 1737244800, 1, 0, NULL);
            """);
        Build(Path.Combine(storage, "contact", "contact.db"), $"""
            CREATE TABLE {WeChat4Schema.ContactTable} (username TEXT PRIMARY KEY, remark TEXT, nick_name TEXT, alias TEXT, local_type INTEGER);
            INSERT INTO {WeChat4Schema.ContactTable} VALUES ('{conversationId}', NULL, 'Official', NULL, 1);
            """);
        Build(Path.Combine(storage, "message", "biz_message_0.db"), $"""
            CREATE TABLE {WeChat4Schema.Name2IdTable} (rowid INTEGER PRIMARY KEY, user_name TEXT);
            INSERT INTO {WeChat4Schema.Name2IdTable} VALUES (1, '{conversationId}');
            CREATE TABLE "{table}" (local_id INTEGER PRIMARY KEY, server_id INTEGER, local_type INTEGER, real_sender_id INTEGER, create_time INTEGER, message_content BLOB, WCDB_CT_message_content INTEGER, compress_content BLOB);
            INSERT INTO "{table}" VALUES (1, 7001, 1, 1, 1736907601, 'official account text', 0, NULL);
            """);

        var account = new WeChatAccountLocation("wxid_test", temp.Path, storage, null);
        using var cache = SqlCipherDatabaseCache.ForCapturedPlaintext();
        var reader = new WeChatAccountReader(account, cache);

        var rows = reader.ReadMessages(conversationId, CancellationToken.None).ToList();

        var row = Assert.Single(rows);
        Assert.Equal(1L, row.LocalId);
        Assert.Equal("official account text", System.Text.Encoding.UTF8.GetString(row.Content!));
    }

    [Fact]
    public void LivePathKeepsTheFatalPartitionMissingOutcomeForATableLessConversation()
    {
        // Issue #39: the live-path vs captured-rebuild asymmetry is deliberate and must not be
        // "aligned" away. A live source has no generation manifest from which Required
        // message-evidence completeness could be proven, so the live reader — constructed exactly
        // as WeChatWindowsSourceAdapter.GetReader does, with no completeness flag — keeps the
        // Fatal partition_missing semantics for a conversation whose table is absent from every
        // successfully indexed shard. Only a captured generation may publish such a conversation
        // as legitimately empty (docs/RAW_VAULT.md section 1).
        using var temp = new TempDirectory();
        var storage = temp.Combine("db_storage");
        Directory.CreateDirectory(Path.Combine(storage, "session"));
        Directory.CreateDirectory(Path.Combine(storage, "contact"));
        Directory.CreateDirectory(Path.Combine(storage, "message"));

        const string messaged = "wxid_has_messages";
        const string tableLess = "wxid_never_messaged";
        var messagedTable = WeChat4Schema.MessageTableName(messaged);
        Build(Path.Combine(storage, "session", "session.db"), $"""
            CREATE TABLE {WeChat4Schema.SessionTable} (username TEXT PRIMARY KEY, sort_timestamp INTEGER, last_timestamp INTEGER, last_msg_type INTEGER, last_msg_sub_type INTEGER, summary TEXT);
            INSERT INTO {WeChat4Schema.SessionTable} VALUES ('{messaged}', 1737244800, 1737244800, 1, 0, NULL);
            INSERT INTO {WeChat4Schema.SessionTable} VALUES ('{tableLess}', 1737244700, 1737244700, 1, 0, NULL);
            """);
        Build(Path.Combine(storage, "contact", "contact.db"), $"""
            CREATE TABLE {WeChat4Schema.ContactTable} (username TEXT PRIMARY KEY, remark TEXT, nick_name TEXT, alias TEXT, local_type INTEGER);
            INSERT INTO {WeChat4Schema.ContactTable} VALUES ('{messaged}', NULL, 'Messaged', NULL, 1);
            INSERT INTO {WeChat4Schema.ContactTable} VALUES ('{tableLess}', NULL, 'Quiet', NULL, 1);
            """);
        // The one message shard indexes successfully; it simply holds no table for the second
        // conversation. This is the "provably empty" shape minus the generation-level proof a
        // live source can never provide.
        Build(Path.Combine(storage, "message", "message_0.db"), $"""
            CREATE TABLE {WeChat4Schema.Name2IdTable} (rowid INTEGER PRIMARY KEY, user_name TEXT);
            INSERT INTO {WeChat4Schema.Name2IdTable} VALUES (1, '{messaged}');
            CREATE TABLE "{messagedTable}" (local_id INTEGER PRIMARY KEY, server_id INTEGER, local_type INTEGER, real_sender_id INTEGER, create_time INTEGER, message_content BLOB, WCDB_CT_message_content INTEGER, compress_content BLOB);
            INSERT INTO "{messagedTable}" VALUES (1, 7001, 1, 1, 1736907601, 'live text', 0, NULL);
            """);

        var account = new WeChatAccountLocation("wxid_test", temp.Path, storage, null);
        using var cache = SqlCipherDatabaseCache.ForCapturedPlaintext();
        var reader = new WeChatAccountReader(account, cache);

        var error = Assert.Throws<SourceCoverageException>(
            () => reader.ReadMessages(tableLess, CancellationToken.None).ToList());
        Assert.Equal(DiagnosticCodes.PartitionMissing, error.Code);
    }

    private static void Build(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}