using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;
using WeArchive.Infrastructure.WeChat;
using WeArchive.Infrastructure.WeChat.Compatibility;

namespace WeArchive.Tests.Support;

/// <summary>
/// One conversation the synthetic capture source exposes, and how its preserved evidence looks.
/// <para>
/// Defaults model a healthy conversation. <see cref="MessageTablePresent"/> = false models a
/// conversation whose message shard is missing while the session row still exists: under the
/// Issue #37 rule that is a Fatal source-coverage condition only when the generation cannot prove
/// its Required message evidence is complete, which is what
/// <see cref="MessageShardUnreadable"/> additionally models.
/// </para>
/// </summary>
internal sealed class SyntheticCaptureConversation
{
    public required string SourceConversationId { get; init; }

    public string Text { get; set; } = "synthetic message";

    /// <summary>Number of message rows written into this conversation's message shard.</summary>
    public int MessageCount { get; set; } = 1;

    /// <summary>When false, the conversation's message shard has no message table.</summary>
    public bool MessageTablePresent { get; set; } = true;

    /// <summary>
    /// When true, this conversation's table is written into a message shard that cannot be read as
    /// SQLite. That keeps the Fatal source-coverage condition available to tests now that a
    /// conversation provably absent from every successfully indexed shard is legitimately empty
    /// (Issue #37): a conversation must never be published as complete while required evidence
    /// could actually be missing.
    /// </summary>
    public bool MessageShardUnreadable { get; set; }
}

/// <summary>
/// The synthetic live source behind a Collection sync test. It reports one account and nothing
/// else: driving <c>CaptureService</c> is its only purpose, and the conversation surface of a live
/// source is deliberately not simulated here (the captured evidence is what the ingest path reads).
/// </summary>
internal sealed class SyntheticWeChatSourceAdapter : ISourceAdapter
{
    public const string ProfileId = "wxid_synthetic_account";

    public string AdapterName => WeChatWindowsSourceAdapter.Name;

    public string AdapterVersion => "0.1.0";

    /// <summary>When false, capture reports an unavailable source instead of publishing a generation.</summary>
    public bool IsAvailable { get; set; } = true;

    public Task<SourceDescriptor> DescribeSourceAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SourceDescriptor
        {
            AdapterName = AdapterName,
            AdapterVersion = AdapterVersion,
            SourceVersion = "4.1.13.12",
            SourceProductName = "WeChat for Windows",
            IsAvailable = IsAvailable,
            UnavailableReason = IsAvailable ? null : "The synthetic source is not running.",
        });

    public Task<IReadOnlyList<SourceAccount>> ListAccountsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SourceAccount>>(
        [
            new SourceAccount
            {
                SourceProfileId = ProfileId,
                DisplayName = ProfileId,
                IsCurrent = true,
            },
        ]);

    public Task<IReadOnlyList<SourceConversation>> ListConversationsAsync(
        string sourceProfileId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SourceConversation>>([]);

    public Task<IReadOnlyList<SourceParticipant>> ListParticipantsAsync(
        string sourceProfileId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SourceParticipant>>([]);

    public Task<SourceConversationDetail> DescribeConversationAsync(
        string sourceProfileId,
        string sourceConversationId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "The synthetic source drives capture only; the captured evidence is what ingest reads.");

    public IAsyncEnumerable<SourceMessage> ReadMessagesAsync(
        string sourceProfileId,
        string sourceConversationId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "The synthetic source drives capture only; the captured evidence is what ingest reads.");
}

/// <summary>
/// A synthetic capture adapter that materializes the conversations under test as decrypted,
/// WeChat-4.x-shaped SQLite images inside a Raw Vault generation.
/// <para>
/// It declares the shipped WeChat capture adapter family/version and a 4.x source version so the
/// captured-source reader accepts the published generation (docs/ARCHITECTURE.md section 3.6.1).
/// Evidence is built from <see cref="Conversations"/>, which a test may mutate between runs to
/// model changed and unchanged source content and to model a later recovery of a failed member.
/// </para>
/// </summary>
internal sealed class SyntheticWeChatCaptureAdapter : ISourceCaptureAdapter, IDisposable
{
    private const string SourceDatabaseRole = "source-database";

    /// <summary>Sentinel SQL text marking an artifact that is deliberately not a database.</summary>
    private const string NotADatabaseMarker = "\0not-a-database\0";

    private readonly string _scratchRoot;
    private bool _disposed;

    public SyntheticWeChatCaptureAdapter(IEnumerable<SyntheticCaptureConversation> conversations)
    {
        Conversations = [.. conversations];
        _scratchRoot = Path.Combine(
            Path.GetTempPath(),
            "WeArchive-collection-capture",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_scratchRoot);
    }

    public List<SyntheticCaptureConversation> Conversations { get; }

    /// <summary>When set, capture fails instead of publishing evidence.</summary>
    public Exception? Failure { get; set; }

    public string CaptureAdapterFamily => WeChatCaptureAdapter.Family;

    public string CaptureAdapterVersion => WeChatCaptureAdapter.Version;

    public async Task<SourceCaptureResult> CaptureAsync(
        string sourceProfileId,
        IRawGenerationSession session,
        IProgress<CaptureProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Failure is not null)
        {
            throw Failure;
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new CaptureProgress { Stage = CaptureStages.Snapshotting, Total = 3 });

        var readableShardSql = BuildMessageSql(Conversations.Where(c => !c.MessageShardUnreadable).ToList());
        var databases = new List<(string Name, string RelativePath, string Sql)>
        {
            ("session.db", "session/session.db", BuildSessionSql()),
            ("contact.db", "contact/contact.db", BuildContactSql()),
            ("message_0.db", "message/message_0.db", readableShardSql),
        };

        // A conversation whose shard cannot be read is preserved as an artifact that is not a
        // readable SQLite database, so the reader reports it as an unreadable shard instead of
        // silently treating the conversation as empty (see SyntheticCaptureConversation).
        var hasUnreadableShard = Conversations.Any(c => c.MessageShardUnreadable);
        if (hasUnreadableShard)
        {
            databases.Add(("message_1.db", "message/message_1.db", NotADatabaseMarker));
        }

        var artifacts = new List<RawArtifactDescriptor>();
        var coverage = new List<RawPartitionCoverage>();
        for (var i = 0; i < databases.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (name, relativePath, sql) = databases[i];
            var path = Path.Combine(_scratchRoot, name);
            if (sql == NotADatabaseMarker)
            {
                await File.WriteAllBytesAsync(path, "this is not a sqlite database"u8.ToArray(), cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                BuildDatabase(path, sql);
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var descriptor = await session
                .WriteArtifactAsync(
                    SourceDatabaseRole,
                    name,
                    stream,
                    sourceFormat: "sqlite",
                    isDecrypted: true,
                    metadata: new Dictionary<string, string>
                    {
                        ["synthetic"] = "true",
                        ["source_relative_path"] = relativePath,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            artifacts.Add(descriptor);
            coverage.Add(new RawPartitionCoverage
            {
                PartitionId = relativePath,
                Status = RawPartitionStatus.Captured,
                SourceFingerprint = descriptor.Sha256,
                ArtifactSha256 = descriptor.Sha256,
            });

            progress?.Report(new CaptureProgress
            {
                Stage = CaptureStages.Snapshotting,
                Processed = i + 1,
                Total = databases.Count,
            });
        }

        return new SourceCaptureResult
        {
            Artifacts = artifacts,
            Coverage = coverage,
            Completeness = RawGenerationCompleteness.Complete,
            SourceProductName = "WeChat for Windows",
            SourceVersion = "4.1.13.12",
        };
    }

    private string BuildSessionSql()
    {
        var rows = string.Join(Environment.NewLine, Conversations.Select((conversation, index) =>
            $"INSERT INTO {WeChat4Schema.SessionTable} VALUES ('{conversation.SourceConversationId}', {1737244800 + index}, {1737244800 + index}, 1, 0, NULL);"));
        return $"""
            CREATE TABLE {WeChat4Schema.SessionTable} (
                username TEXT PRIMARY KEY,
                sort_timestamp INTEGER,
                last_timestamp INTEGER,
                last_msg_type INTEGER,
                last_msg_sub_type INTEGER,
                summary TEXT);
            {rows}
            """;
    }

    private string BuildContactSql()
    {
        var rows = string.Join(Environment.NewLine, Conversations.Select(conversation =>
            $"INSERT INTO {WeChat4Schema.ContactTable} VALUES ('{conversation.SourceConversationId}', 'Remark {conversation.SourceConversationId}', 'Nick', NULL, 1);"));
        return $"""
            CREATE TABLE {WeChat4Schema.ContactTable} (
                username TEXT PRIMARY KEY,
                remark TEXT,
                nick_name TEXT,
                alias TEXT,
                local_type INTEGER);
            {rows}
            CREATE TABLE stranger (username TEXT PRIMARY KEY, nick_name TEXT);
            """;
    }

    private string BuildMessageSql(IReadOnlyList<SyntheticCaptureConversation> shardConversations)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"CREATE TABLE {WeChat4Schema.Name2IdTable} (rowid INTEGER PRIMARY KEY, user_name TEXT);");

        for (var i = 0; i < shardConversations.Count; i++)
        {
            var conversation = shardConversations[i];
            builder.AppendLine(
                $"INSERT INTO {WeChat4Schema.Name2IdTable} VALUES ({i + 1}, '{conversation.SourceConversationId}');");
        }

        foreach (var conversation in shardConversations)
        {
            if (!conversation.MessageTablePresent)
            {
                // The session row exists but this shard does not hold the conversation's table: a
                // Fatal source-coverage condition unless the generation proves otherwise.
                continue;
            }

            var table = WeChat4Schema.MessageTableName(conversation.SourceConversationId);
            builder.AppendLine($"""
                CREATE TABLE "{table}" (
                    local_id INTEGER PRIMARY KEY,
                    server_id INTEGER,
                    local_type INTEGER,
                    real_sender_id INTEGER,
                    create_time INTEGER,
                    message_content BLOB,
                    WCDB_CT_message_content INTEGER,
                    compress_content BLOB);
                """);

            var rows = Enumerable.Range(1, Math.Max(conversation.MessageCount, 1)).Select(id =>
                $"INSERT INTO \"{table}\" VALUES ({id}, {7000 + id}, 1, 1, {1736907600 + id}, '{conversation.Text} {id}', 0, NULL);");
            foreach (var row in rows)
            {
                builder.AppendLine(row);
            }
        }

        return builder.ToString();
    }

    private static void BuildDatabase(string path, string sql)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (Directory.Exists(_scratchRoot))
            {
                Directory.Delete(_scratchRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}

/// <summary>Helpers the Collection tests share for naming synthetic conversations.</summary>
internal static class SyntheticWeChatNaming
{
    /// <summary>A direct (one-to-one) conversation's upstream id.</summary>
    public static string DirectId(string suffix) => "wxid_" + suffix;

    /// <summary>A group conversation's upstream room id.</summary>
    public static string GroupId(string suffix) => suffix + "@chatroom";

    /// <summary>
    /// The stable conversation id the source surface reports for an upstream id, mirroring
    /// <c>WeChatWindowsSourceAdapter.ListConversationsAsync</c> (a direct conversation's peer is its
    /// own upstream id; a group conversation has no peer).
    /// </summary>
    public static string StableConversationId(string accountId, string sourceConversationId)
    {
        var kind = WeChat4Schema.ClassifyConversation(sourceConversationId);
        return StableIds.Conversation(
            accountId,
            kind,
            sourceConversationId,
            kind == ConversationKind.Direct ? sourceConversationId : null);
    }

    /// <summary>The WeChat 4.x message-shard table name for a conversation.</summary>
    public static string MessageTableName(string sourceConversationId) =>
        WeChat4Schema.MessageTableName(sourceConversationId);

    /// <summary>A SHA-256 hex digest, used by tests that compare preserved file content.</summary>
    public static string Sha256Hex(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}