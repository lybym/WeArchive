using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
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
/// One readable shard that holds a conversation's message table, together with the
/// partition the rows are reported under and that shard's own <c>Name2Id</c> sender map.
/// </summary>
internal sealed record WeChatMessageShard(
    string ShardPath,
    string Table,
    string Partition,
    IReadOnlyDictionary<long, string> Name2Id);

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
    IReadOnlyDictionary<string, string>? capturedPartitions = null,
    bool requiredMessageEvidenceComplete = false)
{
    private readonly WeChatAccountLocation _account = account;
    private readonly SqlCipherDatabaseCache _cache = cache;
    private readonly string? _capturedSessionPath = capturedSessionPath;
    private readonly string? _capturedContactPath = capturedContactPath;
    private readonly IReadOnlyList<string>? _capturedMessagePaths = capturedMessagePaths;
    private readonly IReadOnlyDictionary<string, string>? _capturedPartitions = capturedPartitions;
    // True only when the evidence set behind this reader proves that every Required message-bearing
    // partition was captured/reused and indexed (a verified complete Raw Vault generation). Only
    // then may a conversation with no message table be reported as legitimately empty; the caller
    // owns that WeChat-specific judgement, this reader only consumes the fact (Issue #37).
    private readonly bool _requiredMessageEvidenceComplete = requiredMessageEvidenceComplete;
    private Dictionary<string, WeChatContactRow>? _contacts;
    private List<WeChatSessionRow>? _sessions;
    private Dictionary<string, List<string>>? _messageShardByTable;
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
    /// Locates every readable shard holding a conversation's message table, oldest rotation
    /// window first. WeChat 4.x rotates a conversation's <c>Msg_</c> table across the
    /// <c>message_N.db</c> family: on rotation the client starts writing new records into a
    /// different shard while the older shards keep the history they already hold, so a
    /// long-lived conversation exists in several shards at once (Issue #71).
    /// </summary>
    public IReadOnlyList<WeChatMessageShard> FindMessageShards(string sourceConversationId)
    {
        var shards = _messageShardByTable ??= BuildShardIndex();
        var table = WeChat4Schema.MessageTableName(sourceConversationId);
        if (!shards.TryGetValue(table, out var shardPaths))
        {
            return [];
        }

        var result = new List<WeChatMessageShard>(shardPaths.Count);
        foreach (var shardPath in shardPaths)
        {
            if (!_name2Id.TryGetValue(shardPath, out var map))
            {
                map = ReadName2Id(shardPath);
                _name2Id[shardPath] = map;
            }

            result.Add(new WeChatMessageShard(
                shardPath,
                table,
                ResolvePartition(shardPath),
                map));
        }

        return result;
    }

    private string ResolvePartition(string shardPath) =>
        _capturedPartitions is not null
            && _capturedPartitions.TryGetValue(shardPath, out var capturedPartition)
                ? capturedPartition
                : Path.GetFileNameWithoutExtension(shardPath);

    private Dictionary<string, List<string>> BuildShardIndex()
    {
        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
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
                    var table = reader.GetString(0);
                    if (!index.TryGetValue(table, out var holders))
                    {
                        holders = [];
                        index[table] = holders;
                    }

                    holders.Add(shard);
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

        // Oldest rotation window first: WeChat keeps the current window in the lowest-numbered
        // shard file, so ordering the numeric suffix descending orders the rotated windows
        // oldest first and the whole stream stays ascending in time across them (Issue #71).
        foreach (var holders in index.Values)
        {
            holders.Sort(CompareShardsOldestFirst);
        }

        return index;
    }

    private static readonly Regex TrailingShardNumber = new(@"(\d+)$", RegexOptions.Compiled);

    private static int CompareShardsOldestFirst(string left, string right)
    {
        var byWindow = ShardWindowNumber(right).CompareTo(ShardWindowNumber(left));
        return byWindow != 0 ? byWindow : string.CompareOrdinal(left, right);
    }

    private static int ShardWindowNumber(string shardPath)
    {
        var match = TrailingShardNumber.Match(Path.GetFileNameWithoutExtension(shardPath));
        return match.Success && long.TryParse(match.Groups[1].Value, out var number) && number <= int.MaxValue
            ? (int)number
            : -1;
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

    /// <summary>
    /// Aggregate counters for a conversation without reading message bodies. The counters
    /// cover every rotation window that holds the conversation's table (Issue #71).
    /// </summary>
    public SourceConversationDetail Describe(string sourceConversationId)
    {
        var shards = FindMessageShards(sourceConversationId);
        long count = 0;
        long? first = null;
        long? last = null;
        foreach (var shard in shards)
        {
            using var connection = Open(shard.ShardPath);
            using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                 SELECT COUNT(*), MIN(create_time), MAX(create_time)
                 FROM "{shard.Table}";
                 """;
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                continue;
            }

            count += reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
            var shardFirst = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
            var shardLast = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
            if (shardFirst is not null && (first is null || shardFirst.Value < first.Value))
            {
                first = shardFirst;
            }

            if (shardLast is not null && (last is null || shardLast.Value > last.Value))
            {
                last = shardLast;
            }
        }

        return new SourceConversationDetail
        {
            SourceConversationId = sourceConversationId,
            MessageCount = (int)Math.Min(count, int.MaxValue),
            FirstMessageAt = first is null ? null : ToLocalTime(first.Value),
            LastMessageAt = last is null ? null : ToLocalTime(last.Value),
        };
    }

    /// <summary>
    /// Streams a conversation's records in ascending time order across every rotation window
    /// that holds its table, oldest window first (Issue #71).
    /// </summary>
    public IEnumerable<WeChatMessageRow> ReadMessages(string sourceConversationId, CancellationToken cancellationToken)
    {
        var shards = FindMessageShards(sourceConversationId);
        if (shards.Count == 0)
        {
            // No readable shard holds this conversation's table. If any shard failed to index, the
            // table may live in one of those, so coverage is genuinely partial and this stays
            // Fatal: a conversation must never be published as complete while required evidence
            // could actually be missing (FR-14/FR-20).
            if (_unreadableShards.Count > 0)
            {
                throw new SourceCoverageException(
                    DiagnosticCodes.PartitionUnreadable,
                    $"The message shard for conversation '{sourceConversationId}' could not be read. " +
                    $"{_unreadableShards.Count} shard(s) failed during indexing, so source coverage is " +
                    "incomplete and no records were archived for this conversation.");
            }

            // Every message shard indexed successfully. When the evidence set behind this reader
            // proves its Required message partitions are all present, the absence of a table is
            // source truth rather than unknown coverage: WeChat only creates a conversation's table
            // once it has records, so the conversation is legitimately empty. The import then
            // completes with the framework's `no_new_records` info diagnostic instead of a silent
            // empty dataset (Issue #37).
            if (_requiredMessageEvidenceComplete)
            {
                yield break;
            }

            throw new SourceCoverageException(
                DiagnosticCodes.PartitionMissing,
                $"No message shard was found for conversation '{sourceConversationId}'. " +
                "The source provided no readable records for this conversation.");
        }

        foreach (var shard in shards)
        {
            using var connection = Open(shard.ShardPath);
            using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                 SELECT {WeChat4Schema.LocalId}, COALESCE({WeChat4Schema.ServerId}, 0),
                        {WeChat4Schema.LocalType}, COALESCE({WeChat4Schema.RealSenderId}, 0),
                        {WeChat4Schema.CreateTime}, {WeChat4Schema.MessageContent},
                        COALESCE({WeChat4Schema.ContentCompression}, 0), {WeChat4Schema.CompressContent}
                 FROM "{shard.Table}"
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
                    shard.Partition,
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    content,
                    (int)reader.GetInt64(6));
            }
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

    private SqliteConnection Open(string encryptedPath) => _cache.OpenReadOnly(encryptedPath);

    public static string Describe(WeChatContactRow contact) => string.Create(
        CultureInfo.InvariantCulture,
        $"{contact.UserName}");
}
