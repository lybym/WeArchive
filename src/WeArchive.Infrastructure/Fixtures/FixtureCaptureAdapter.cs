using Microsoft.Data.Sqlite;
using WeArchive.Core.Abstractions;
using WeArchive.Core.RawVault;

namespace WeArchive.Infrastructure.Fixtures;

/// <summary>
/// A synthetic capture adapter that produces small, deterministic, unencrypted SQLite
/// databases as Raw Vault artifacts. Used to exercise the full capture -&gt; store -&gt; reopen
/// flow without a running WeChat client (docs/DEVELOPMENT.md section 7 fixture strategy).
/// <para>
/// The fixture databases are source-faithful in shape: they preserve every table, column and
/// row, including columns the current parser does not interpret (an <c>extra_metadata</c>
/// column on the message table). This proves that unknown/source-specific fields remain
/// available in preserved evidence (Issue #22 unknown-field preservation).
/// </para>
/// </summary>
public sealed class FixtureCaptureAdapter : ISourceCaptureAdapter, IDisposable
{
    public const string Family = "fixture";
    public const string Version = "1.0.0";

    private const string SourceDatabaseRole = "source-database";
    private const string SourceFormat = "sqlite";

    private readonly string _scratchRoot;
    private bool _disposed;

    public FixtureCaptureAdapter()
    {
        _scratchRoot = Path.Combine(
            Path.GetTempPath(),
            "WeArchive-fixture-capture",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_scratchRoot);
    }

    public string CaptureAdapterFamily => Family;

    public string CaptureAdapterVersion => Version;

    public async Task<SourceCaptureResult> CaptureAsync(
        string sourceProfileId,
        IRawGenerationSession session,
        IProgress<CaptureProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(new CaptureProgress
        {
            Stage = CaptureStages.Snapshotting,
            Total = 3,
            Detail = sourceProfileId,
        });

        var artifacts = new List<RawArtifactDescriptor>();
        var databases = new[]
        {
            ("session.db", (Action<SqliteCommand>)BuildSessionSchema),
            ("contact.db", (Action<SqliteCommand>)BuildContactSchema),
            ("message_0.db", (Action<SqliteCommand>)BuildMessageSchema),
        };

        for (var i = 0; i < databases.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (name, schema) = databases[i];

            var path = Path.Combine(_scratchRoot, name);
            BuildDatabase(path, schema);

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var descriptor = await session
                .WriteArtifactAsync(
                    SourceDatabaseRole,
                    name,
                    stream,
                    sourceFormat: SourceFormat,
                    isDecrypted: true,
                    metadata: new Dictionary<string, string>
                    {
                        ["synthetic"] = "true",
                        ["source_relative_path"] = $"db_storage/{name[..^3]}/{name}",
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            artifacts.Add(descriptor);

            progress?.Report(new CaptureProgress
            {
                Stage = CaptureStages.Snapshotting,
                Processed = i + 1,
                Total = databases.Length,
                Detail = name,
            });
        }

        progress?.Report(new CaptureProgress { Stage = CaptureStages.Finalizing });

        return new SourceCaptureResult
        {
            Artifacts = artifacts,
            Completeness = RawGenerationCompleteness.Complete,
            Diagnostics = [],
            SourceProductName = "WeArchive fixture source",
            SourceVersion = "fixture-1",
        };
    }

    private static void BuildDatabase(string path, Action<SqliteCommand> applySchema)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        // Use the rollback journal rather than WAL so the entire database is in the main .db
        // file when the connection closes — the capture copies only the .db file, not a -wal
        // sidecar, and a checkpoint that has not flushed would make the artifact stale.
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=DELETE;";
        pragma.ExecuteNonQuery();
        using var command = connection.CreateCommand();
        applySchema(command);
        command.ExecuteNonQuery();
    }

    private static void BuildSessionSchema(SqliteCommand command)
    {
        command.CommandText =
            """
            CREATE TABLE session (
                username TEXT PRIMARY KEY,
                sort_timestamp INTEGER,
                last_timestamp INTEGER,
                last_msg_type INTEGER,
                last_msg_sub_type INTEGER,
                summary TEXT
            );

            INSERT INTO session (username, sort_timestamp, last_timestamp, last_msg_type, last_msg_sub_type, summary)
            VALUES
                ('wxid_alice', 1737244800, 1737244800, 1, 0, '收到。'),
                ('100200300@chatroom', 1737763200, 1737763200, 1, 0, '二月第二天的记录。');
            """;
    }

    private static void BuildContactSchema(SqliteCommand command)
    {
        command.CommandText =
            """
            CREATE TABLE friend (
                username TEXT PRIMARY KEY,
                remark TEXT,
                nick_name TEXT,
                alias TEXT,
                local_type INTEGER
            );

            CREATE TABLE stranger (
                username TEXT PRIMARY KEY,
                nick_name TEXT
            );

            INSERT INTO friend (username, remark, nick_name, alias, local_type) VALUES
                ('wxid_alice', '张三', '三哥', 'zhang-san', 1),
                ('wxid_bob', NULL, 'Kevin', NULL, 1),
                ('wxid_carol', '李四', '四儿', NULL, 1);

            INSERT INTO stranger (username, nick_name) VALUES
                ('wxid_stranger', '路人');
            """;
    }

    private static void BuildMessageSchema(SqliteCommand command)
    {
        command.CommandText =
            """
            CREATE TABLE Message_abcdef (local_id INTEGER PRIMARY KEY,
                                          server_id INTEGER,
                                          local_type INTEGER,
                                          real_sender_id INTEGER,
                                          create_time INTEGER,
                                          message_content BLOB,
                                          compress_content BLOB,
                                          content_compression INTEGER,
                                          extra_metadata TEXT);

            CREATE TABLE Name2Id (rowid INTEGER PRIMARY KEY, user_name TEXT);

            INSERT INTO Message_abcdef (local_id, server_id, local_type, real_sender_id, create_time, message_content, compress_content, content_compression, extra_metadata)
            VALUES
                (1, 1001, 1, 1, 1736907600, X'E4B88BE58D88E4B889E782B9E79A84E8AF84E5AEA1E4BC9AE694B9E588B0E59B9BE782B9E4BA86E38082', NULL, 0, 'unknown-field-value'),
                (2, 1002, 1, 2, 1736907720, X'E694B6E588B0E38082', NULL, 0, NULL),
                (3, 1003, 3, 3, 1736908140, NULL, NULL, 0, NULL);

            INSERT INTO Name2Id (rowid, user_name) VALUES
                (1, 'wxid_alice'),
                (2, 'wxid_bob'),
                (3, 'wxid_carol');
            """;
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
