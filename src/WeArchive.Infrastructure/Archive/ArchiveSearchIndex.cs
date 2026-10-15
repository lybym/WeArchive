using System.Text.Json;
using Microsoft.Data.Sqlite;
using WeArchive.Core.Domain;
using WeArchive.Core.Search;

namespace WeArchive.Infrastructure.Archive;

/// <summary>
/// The derived canonical FTS5 search index over <c>messages</c>
/// (docs/SEARCH_SPEC.md sections 3–4 and 10, docs/DATA_MODEL.md section 19).
/// <para>
/// The index is <strong>R0 derived state</strong> (docs/DEVELOPMENT.md section 10.2): rebuildable
/// from canonical rows, never authoritative, never a second system of record. It is a plain FTS5
/// virtual table whose <c>body</c> column stores the CJK bigram-projected searchable text of one
/// canonical message and whose UNINDEXED <c>message_id</c> column is the stable message-id ↔ row
/// mapping. No canonical column changes and no journal, commit marker or recovery state exists;
/// every write below is an ordinary statement inside the caller's transaction
/// (docs/AGENTS.md reliability hard-stop; docs/SEARCH_SPEC.md section 10.1).
/// </para>
/// <para>
/// DDL decision (docs/SEARCH_SPEC.md section 10.2 leaves this to the index slice): a plain
/// (non-contentless, non-external-content) FTS5 table with the stable message id as an UNINDEXED
/// column. It keeps the stored body, so FTS5 <c>integrity-check</c> can actually verify
/// index↔content agreement — the property section 10.4's verification is defined on — and row
/// replacement uses ordinary DELETE/INSERT statements, which stay correct for any stored content.
/// </para>
/// </summary>
internal static class ArchiveSearchIndex
{
    public const string TableName = "messages_fts";

    /// <summary>
    /// The index DDL: unicode61 tokenization over the projected bigram body (ADR 0013), with the
    /// stable canonical message id as an UNINDEXED passthrough column.
    /// </summary>
    public const string CreateStatement =
        $"CREATE VIRTUAL TABLE {TableName} USING fts5(body, message_id UNINDEXED, tokenize='unicode61');";

    /// <summary>
    /// Projects one message's searchable text into its indexed body. The projection is applied only
    /// inside the derived index — canonical storage, exports and MESSAGE_SCHEMA output are never
    /// rewritten (docs/SEARCH_SPEC.md section 4.2).
    /// </summary>
    public static string BodyOf(string searchableText) => CjkBigramProjection.Project(searchableText);

    /// <summary>
    /// Reads every canonical row in stable id order and derives its index row. Returns the
    /// (message id, projected body) pairs the caller inserts. The id order is what makes a fresh
    /// migration backfill and a full rebuild deterministic.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> DeriveBodies(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var rows = new List<KeyValuePair<string, string>>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT id, type, semantic_text, payload_json FROM messages ORDER BY id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = reader.GetString(0);
                CanonicalMessageTypes.TryParse(reader.GetString(1), out var type);
                var text = reader.GetString(2);
                var payload = reader.IsDBNull(3)
                    ? null
                    : JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(reader.GetString(3));
                rows.Add(new(id, SearchableMessageText.Build(type, text, payload)));
            }
        }

        return rows;
    }

    /// <summary>
    /// Inserts one index row per (message id, searchable text) pair, projecting each body. Only
    /// rows whose message is new to the index may be passed in — this method never replaces.
    /// </summary>
    public static void InsertBodies(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<KeyValuePair<string, string>> messageBodies)
    {
        if (messageBodies.Count == 0)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {TableName}(body, message_id) VALUES ($body, $id);";
        var body = command.Parameters.Add("$body", SqliteType.Text);
        var id = command.Parameters.Add("$id", SqliteType.Text);
        command.Prepare();

        foreach (var (messageId, searchableText) in messageBodies)
        {
            body.Value = BodyOf(searchableText);
            id.Value = messageId;
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Deletes every index row of the given stable message ids, whatever their stored content.
    /// Deletion uses an ordinary DELETE over the UNINDEXED id column (a full index scan), so it is
    /// correct even when the stored body no longer matches what was indexed — the FTS5 special
    /// 'delete' command cannot be used on a plain table. Callers batch their replacements into one
    /// call to pay that scan once.
    /// </summary>
    public static void DeleteMessageRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> messageIds)
    {
        if (messageIds.Count == 0)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"DELETE FROM {TableName} WHERE message_id IN (SELECT value FROM json_each($ids));";
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(messageIds));
        command.ExecuteNonQuery();
    }

    /// <summary>Whether the FTS table exists at all (it is a virtual table recorded in sqlite_master).</summary>
    public static bool TableExists(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", TableName);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>The number of index rows, for the messages-vs-index agreement check.</summary>
    public static long CountRows(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {TableName};";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Recreates the FTS table and re-derives every row from canonical data, inside the caller's
    /// transaction. This is the deterministic rebuild of docs/SEARCH_SPEC.md section 10.3: the
    /// same canonical rows always produce the same index content.
    /// </summary>
    /// <returns>The number of canonical rows indexed.</returns>
    public static int Rebuild(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using (var drop = connection.CreateCommand())
        {
            drop.Transaction = transaction;
            drop.CommandText = $"DROP TABLE IF EXISTS {TableName};";
            drop.ExecuteNonQuery();
        }

        using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = CreateStatement;
            create.ExecuteNonQuery();
        }

        var bodies = DeriveBodies(connection, cancellationToken);
        InsertBodies(connection, transaction, bodies);
        return bodies.Count;
    }
}
