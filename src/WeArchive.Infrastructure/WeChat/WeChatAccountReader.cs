using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using WeArchive.Core.Domain;
using WeArchive.Infrastructure.WeChat.Compatibility;

namespace WeArchive.Infrastructure.WeChat;

internal sealed record WeChatSessionRow(
    string UserName,
    long SortTimestamp,
    long LastTimestamp,
    int LastMessageType,
    int LastMessageSubType,
    string? Summary);

internal sealed record WeChatContactRow(
    string UserName,
    string? Remark,
    string? NickName,
    string? Alias,
    int LocalType);

/// <summary>One upstream message row, still in WeChat's wire shape.</summary>
internal sealed record WeChatMessageRow(
    string Partition,
    long LocalId,
    long ServerId,
    long LocalType,
    long RealSenderId,
    long CreateTime,
    byte[]? Content,
    int ContentCompression);

/// <summary>
/// Read-only access to one account's decrypted databases. All WeChat column and table
/// names are confined to this class and <see cref="WeChat4Schema"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WeChatAccountReader(
    WeChatAccountLocation account,
    SqlCipherDatabaseCache cache,
    string? capturedSessionPath = null,
    string? capturedContactPath = null,
    IReadOnlyList<string>? capturedMessagePaths = null,
    IReadOnlyDictionary<string, string>? capturedPartitions = null)
{
    private readonly WeChatAccountLocation _account = account;
    private readonly SqlCipherDatabaseCache _cache = cache;
    private readonly string? _capturedSessionPath = capturedSessionPath;
    private readonly string? _capturedContactPath = capturedContactPath;
    private readonly IReadOnlyList<string>? _capturedMessagePaths = capturedMessagePaths;
    private readonly IReadOnlyDictionary<string, string>? _capturedPartitions = capturedPartitions;
    private Dictionary<string, WeChatContactRow>? _contacts;
    private List<WeChatSessionRow>? _sessions;
    private Dictionary<string, string>? _messageShardByTable;
    private readonly Dictionary<string, Dictionary<long, string>> _name2Id = new(StringComparer.OrdinalIgnoreCase);
    // Shards that could not be opened/indexed, recorded (not swallowed) so a conversation
    // whose only table lives in one of them surfaces a partial-coverage diagnostic instead
    // of a silent empty import. FR-14.
    private readonly List<string> _unreadableShards = [];

    public WeChatAccountLocation Account => _account;

    /// <summary>Reads the cached session list (most recently used first).</summary>
    public IReadOnlyList<WeChatSessionRow> ReadSessions()
    {
        if (_sessions is not null)
        {
            return _sessions;
        }

        var path = _capturedSessionPath ?? WeChatDataLocator.SessionDatabase(_account);
        _sessions = [];
        if (!File.Exists(path))
        {
            return _sessions;
        }

        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT username, COALESCE(sort_timestamp, 0), COALESCE(last_timestamp, 0),
                    COALESCE(last_msg_type, 0), COALESCE(last_msg_sub_type, 0), summary
             FROM {WeChat4Schema.SessionTable}
             ORDER BY COALESCE(sort_timestamp, last_timestamp, 0) DESC;
             """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            _sessions.Add(new WeChatSessionRow(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                (int)reader.GetInt64(3),
                (int)reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return _sessions;
    }

    /// <summary>Reads the account's contact catalog (friends, group rooms and strangers).</summary>
    public IReadOnlyDictionary<string, WeChatContactRow> ReadContacts()
    {
        if (_contacts is not null)
        {
            return _contacts;
        }

        _contacts = new Dictionary<string, WeChatContactRow>(StringComparer.Ordinal);
        var path = _capturedContactPath ?? WeChatDataLocator.ContactDatabase(_account);
        if (!File.Exists(path))
        {
            return _contacts;
        }

        using var connection = Open(path);
        foreach (var table in new[] { WeChat4Schema.ContactTable, "stranger" })
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                 SELECT username, remark, nick_name, alias, COALESCE(local_type, 0)
                 FROM {table} WHERE username IS NOT NULL;
                 """;
            try
            {
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var userName = reader.GetString(0);
                    if (string.IsNullOrWhiteSpace(userName))
                    {
                        continue;
                    }

                    _contacts.TryAdd(userName, new WeChatContactRow(
                        userName,
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        (int)reader.GetInt64(4)));
                }
            }
            catch (SqliteException)
            {
                // An older schema without the table simply contributes no contacts.
            }
        }

        return _contacts;
    }

    /// <summary>
    /// Locates the message shard holding a conversation's table and its sender-id map.
    /// WeChat 4.x splits conversations across several <c>message_N.db</c> files.
    /// </summary>
    public (string ShardPath, string Table, IReadOnlyDictionary<long, string> Name2Id)? FindMessageShard(
        string sourceConversationId)
    {
        var shards = _messageShardByTable ??= BuildShardIndex();
        var table = WeChat4Schema.MessageTableName(sourceConversationId);
        if (!shards.TryGetValue(table, out var shardPath))
        {
            return null;
        }

        if (!_name2Id.TryGetValue(shardPath, out var map))
        {
            map = ReadName2Id(shardPath);
            _name2Id[shardPath] = map;
        }

        return (shardPath, table, map);
    }

    private Dictionary<string, string> BuildShardIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var shard in _capturedMessagePaths ?? WeChatDataLocator.MessageDatabases(_account))
        {
            try
            {
                using var connection = Open(shard);
                using var command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE '{WeChat4Schema.MessageTablePrefix}%';";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    index[reader.GetString(0)] = shard;
                }
            }
            catch (SqliteException)
            {
                // Recorded, not swallowed: a conversation whose table lives only in this
                // shard must surface a partial-coverage diagnostic, not a silent empty
                // import. See ReadMessages.
                _unreadableShards.Add(shard);
            }
            catch (WeChatKeyUnavailableException)
            {
                // The key for this shard could not be recovered; the shard contributes no
                // tables and any conversation whose data lives here is partial coverage.
                _unreadableShards.Add(shard);
            }
        }

        return index;
    }

    private Dictionary<long, string> ReadName2Id(string shardPath)
    {
        var map = new Dictionary<long, string>();
        using var connection = Open(shardPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT rowid, user_name FROM {WeChat4Schema.Name2IdTable};";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1))
            {
                map[reader.GetInt64(0)] = reader.GetString(1);
            }
        }

        return map;
    }

    /// <summary>Aggregate counters for a conversation without reading message bodies.</summary>
    public SourceConversationDetail Describe(string sourceConversationId)
    {
        var shard = FindMessageShard(sourceConversationId);
        if (shard is null)
        {
            return new SourceConversationDetail { SourceConversationId = sourceConversationId };
        }

        using var connection = Open(shard.Value.ShardPath);
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT COUNT(*), MIN(create_time), MAX(create_time)
             FROM "{shard.Value.Table}";
             """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new SourceConversationDetail { SourceConversationId = sourceConversationId };
        }

        var count = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
        var first = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
        var last = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);

        return new SourceConversationDetail
        {
            SourceConversationId = sourceConversationId,
            MessageCount = count,
            FirstMessageAt = first is null ? null : ToLocalTime(first.Value),
            LastMessageAt = last is null ? null : ToLocalTime(last.Value),
        };
    }

    /// <summary>Streams a conversation's records in ascending time order.</summary>
    public IEnumerable<WeChatMessageRow> ReadMessages(string sourceConversationId, CancellationToken cancellationToken)
    {
        var shard = FindMessageShard(sourceConversationId);
        if (shard is null)
        {
            // No readable shard holds this conversation's table. If any shard failed to
            // index, the table may live in one of those, so coverage is genuinely partial;
            // otherwise the source simply has no table for this conversation. Either way
            // this must not become a silent empty import that exports an apparently valid
            // empty dataset (FR-14).
            throw _unreadableShards.Count > 0
                ? new SourceCoverageException(
                    DiagnosticCodes.PartitionUnreadable,
                    $"The message shard for conversation '{sourceConversationId}' could not be read. " +
                    $"{_unreadableShards.Count} shard(s) failed during indexing, so source coverage is " +
                    "incomplete and no records were archived for this conversation.")
                : new SourceCoverageException(
                    DiagnosticCodes.PartitionMissing,
                    $"No message shard was found for conversation '{sourceConversationId}'. " +
                    "The source provided no readable records for this conversation.");
        }

        var partition = _capturedPartitions is not null
            && _capturedPartitions.TryGetValue(shard.Value.ShardPath, out var capturedPartition)
                ? capturedPartition
                : Path.GetFileNameWithoutExtension(shard.Value.ShardPath);
        using var connection = Open(shard.Value.ShardPath);
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT {WeChat4Schema.LocalId}, COALESCE({WeChat4Schema.ServerId}, 0),
                    {WeChat4Schema.LocalType}, COALESCE({WeChat4Schema.RealSenderId}, 0),
                    {WeChat4Schema.CreateTime}, {WeChat4Schema.MessageContent},
                    COALESCE({WeChat4Schema.ContentCompression}, 0), {WeChat4Schema.CompressContent}
             FROM "{shard.Value.Table}"
             ORDER BY {WeChat4Schema.CreateTime}, {WeChat4Schema.LocalId};
             """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // message_content is TEXT for plain records and a BLOB when WCDB compressed it.
            // compress_content is the older location of the compressed payload.
            var content = ReadBinary(reader, 5) ?? ReadBinary(reader, 7);
            yield return new WeChatMessageRow(
                partition,
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                content,
                (int)reader.GetInt64(6));
        }
    }

    private static byte[]? ReadBinary(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return reader.GetValue(ordinal) switch
        {
            byte[] bytes => bytes,
            string text => System.Text.Encoding.UTF8.GetBytes(text),
            _ => null,
        };
    }

    public static DateTimeOffset ToLocalTime(long unixSeconds)
    {
        var instant = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        return instant.ToOffset(TimeZoneInfo.Local.GetUtcOffset(instant.UtcDateTime));
    }

    private SqliteConnection Open(string encryptedPath)
    {
        var plaintext = _cache.GetPlaintext(encryptedPath);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = plaintext.PlaintextPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    public static string Describe(WeChatContactRow contact) => string.Create(
        CultureInfo.InvariantCulture,
        $"{contact.UserName}");
}
