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

    [Fact]
    public void MultiShardRotationImportsEveryWindowOldestFirstWithoutDuplicates()
    {
        // Issue #71: WeChat 4.x rotates a conversation's Msg_ table across the message_N.db
        // family, so a long-lived conversation exists in several shards at once. A reader that
        // maps each table to exactly one shard silently truncates the history to a single
        // rotation window. The synthetic layout mirrors the measured account: message_2 holds
        // the oldest window, message_1 the middle one, message_0 the current one.
        using var temp = new TempDirectory();
        var storage = temp.Combine("db_storage");
        Directory.CreateDirectory(Path.Combine(storage, "message"));

        const string conversationId = "56894683949@chatroom";
        var table = WeChat4Schema.MessageTableName(conversationId);
        string ShardSql(string inserts) => $"""
            CREATE TABLE {WeChat4Schema.Name2IdTable} (rowid INTEGER PRIMARY KEY, user_name TEXT);
            CREATE TABLE "{table}" (local_id INTEGER PRIMARY KEY, server_id INTEGER, local_type INTEGER, real_sender_id INTEGER, create_time INTEGER, message_content BLOB, WCDB_CT_message_content INTEGER, compress_content BLOB);
            {inserts}
            """;
        Build(Path.Combine(storage, "message", "message_2.db"), ShardSql($"""
            INSERT INTO "{table}" VALUES (1, 8101, 1, 1, 1727769601, 'oldest a', 0, NULL);
            INSERT INTO "{table}" VALUES (2, 8102, 1, 2, 1727769602, 'oldest b', 0, NULL);
            """));
        Build(Path.Combine(storage, "message", "message_1.db"), ShardSql($"""
            INSERT INTO "{table}" VALUES (1, 8201, 1, 2, 1758643201, 'middle a', 0, NULL);
            INSERT INTO "{table}" VALUES (2, 8202, 1, 1, 1758643202, 'middle b', 0, NULL);
            """));
        Build(Path.Combine(storage, "message", "message_0.db"), ShardSql($"""
            INSERT INTO "{table}" VALUES (1, 8301, 1, 2, 1789996801, 'newest a', 0, NULL);
            """));

        var account = new WeChatAccountLocation("wxid_test", temp.Path, storage, null);
        using var cache = SqlCipherDatabaseCache.ForCapturedPlaintext();
        var reader = new WeChatAccountReader(account, cache);

        var rows = reader.ReadMessages(conversationId, CancellationToken.None).ToList();

        Assert.Equal(5, rows.Count);
        Assert.Equal(
            new[] { 1727769601L, 1727769602L, 1758643201L, 1758643202L, 1789996801L },
            rows.Select(row => row.CreateTime).ToArray());
        Assert.Equal(
            new[] { "message_2", "message_2", "message_1", "message_1", "message_0" },
            rows.Select(row => row.Partition).ToArray());
        Assert.Equal(5, rows.Select(row => row.ServerId).Distinct().Count());

        var detail = reader.Describe(conversationId);
        Assert.Equal(5, detail.MessageCount);
        Assert.Equal(WeChatAccountReader.ToLocalTime(1727769601), detail.FirstMessageAt);
        Assert.Equal(WeChatAccountReader.ToLocalTime(1789996801), detail.LastMessageAt);
    }

    [Fact]
    public async Task CapturedReaderResolvesEachRowsSenderThroughItsOwnShardsName2Id()
    {
        // Issue #71 acceptance criterion: sender resolution uses the Name2Id map of the shard
        // the row was read from. The same real_sender_id maps to a different user name in each
        // rotation window here, so a reader that resolved senders through one global map would
        // mislabel every row outside its chosen window.
        using var temp = new TempDirectory();
        var storage = temp.Combine("db_storage");
        Directory.CreateDirectory(Path.Combine(storage, "contact"));
        Directory.CreateDirectory(Path.Combine(storage, "message"));

        const string profileId = "a_synthetic";
        const string conversationId = "56894683949@chatroom";
        var table = WeChat4Schema.MessageTableName(conversationId);
        Build(Path.Combine(storage, "contact", "contact.db"), $"""
            CREATE TABLE {WeChat4Schema.ContactTable} (username TEXT PRIMARY KEY, remark TEXT, nick_name TEXT, alias TEXT, local_type INTEGER);
            INSERT INTO {WeChat4Schema.ContactTable} VALUES ('sender_rotated_out', NULL, 'Old', NULL, 1);
            INSERT INTO {WeChat4Schema.ContactTable} VALUES ('sender_middle', NULL, 'Mid', NULL, 1);
            INSERT INTO {WeChat4Schema.ContactTable} VALUES ('sender_current', NULL, 'New', NULL, 1);
            """);
        string ShardSql(string senderName, long createTime) => $"""
            CREATE TABLE {WeChat4Schema.Name2IdTable} (rowid INTEGER PRIMARY KEY, user_name TEXT);
            INSERT INTO {WeChat4Schema.Name2IdTable} VALUES (7, '{senderName}');
            CREATE TABLE "{table}" (local_id INTEGER PRIMARY KEY, server_id INTEGER, local_type INTEGER, real_sender_id INTEGER, create_time INTEGER, message_content BLOB, WCDB_CT_message_content INTEGER, compress_content BLOB);
            INSERT INTO "{table}" VALUES (1, 9{createTime % 1000}, 1, 7, {createTime}, 'window text', 0, NULL);
            """;
        var oldestPath = Path.Combine(storage, "message", "message_2.db");
        var middlePath = Path.Combine(storage, "message", "message_1.db");
        var newestPath = Path.Combine(storage, "message", "message_0.db");
        Build(oldestPath, ShardSql("sender_rotated_out", 1727769601));
        Build(middlePath, ShardSql("sender_middle", 1758643201));
        Build(newestPath, ShardSql("sender_current", 1789996801));

        var account = new WeChatAccountLocation("wxid_test", temp.Path, storage, null);
        using var cache = SqlCipherDatabaseCache.ForCapturedPlaintext();
        // The captured paths arrive in a deliberately unsorted order; the reader must still
        // read them oldest window first.
        var reader = new WeChatAccountReader(
            account,
            cache,
            capturedMessagePaths: [newestPath, oldestPath, middlePath],
            capturedPartitions: new Dictionary<string, string>
            {
                [oldestPath] = "message_2",
                [middlePath] = "message_1",
                [newestPath] = "message_0",
            });

        var descriptor = new SourceDescriptor
        {
            AdapterName = WeChatWindowsSourceAdapter.Name,
            AdapterVersion = WeChatWindowsSourceAdapter.Version,
            IsAvailable = true,
        };
        using var adapter = new WeChatWindowsSourceAdapter(
            new SourceAccount { SourceProfileId = profileId },
            descriptor,
            reader,
            cache);

        var messages = new List<SourceMessage>();
        await foreach (var message in adapter.ReadMessagesAsync(profileId, conversationId, CancellationToken.None))
        {
            messages.Add(message);
        }

        Assert.Equal(3, messages.Count);
        Assert.Equal(
            new[] { "message_2", "message_1", "message_0" },
            messages.Select(message => message.SourcePartition).ToArray());
        Assert.Equal(
            new[] { "sender_rotated_out", "sender_middle", "sender_current" },
            messages.Select(message => message.SenderSourceUserId).ToArray());
        Assert.Equal(
            new[] { 1727769601L, 1758643201L, 1789996801L },
            messages.Select(message => message.OccurredAt.ToUnixTimeSeconds()).ToArray());
    }

    [Fact]
    public void AReadableWindowIsStillImportedWhenAnotherWindowShardIsUnreadable()
    {
        // Issue #71 preserves the fail-closed semantics: an unreadable shard is recorded, and a
        // conversation whose table cannot be found in any readable shard stays fatal. But a
        // conversation whose table IS readable in one window must still import that window
        // rather than being refused as a whole.
        using var temp = new TempDirectory();
        var storage = temp.Combine("db_storage");
        Directory.CreateDirectory(Path.Combine(storage, "message"));

        const string conversationId = "56894683949@chatroom";
        var table = WeChat4Schema.MessageTableName(conversationId);
        Build(Path.Combine(storage, "message", "message_0.db"), $"""
            CREATE TABLE {WeChat4Schema.Name2IdTable} (rowid INTEGER PRIMARY KEY, user_name TEXT);
            CREATE TABLE "{table}" (local_id INTEGER PRIMARY KEY, server_id INTEGER, local_type INTEGER, real_sender_id INTEGER, create_time INTEGER, message_content BLOB, WCDB_CT_message_content INTEGER, compress_content BLOB);
            INSERT INTO "{table}" VALUES (1, 8301, 1, 2, 1789996801, 'newest window only', 0, NULL);
            """);
        // Not a SQLite file: indexing this shard fails and it is recorded as unreadable.
        File.WriteAllText(Path.Combine(storage, "message", "message_1.db"), "definitely not a database");

        var account = new WeChatAccountLocation("wxid_test", temp.Path, storage, null);
        using var cache = SqlCipherDatabaseCache.ForCapturedPlaintext();
        var reader = new WeChatAccountReader(account, cache);

        var rows = reader.ReadMessages(conversationId, CancellationToken.None).ToList();
        var row = Assert.Single(rows);
        Assert.Equal("message_0", row.Partition);

        // The unreadable shard keeps its fail-closed role for a conversation whose table is
        // found in no readable shard at all.
        var error = Assert.Throws<SourceCoverageException>(
            () => reader.ReadMessages("wxid_never_seen", CancellationToken.None).ToList());
        Assert.Equal(DiagnosticCodes.PartitionUnreadable, error.Code);
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