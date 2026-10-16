using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.Query;
using WeArchive.Core.Search;

namespace WeArchive.Infrastructure.Archive;

/// <summary>
/// SQLite archive store. This is the system of record for normalized data.
/// docs/ARCHITECTURE.md section 3.6, docs/DATA_MODEL.md sections 8 and 13.
/// <para>
/// Writes are idempotent upserts keyed by canonical IDs. A message is only counted as
/// changed when its semantic content hash changes, so re-running an import over the
/// same range yields all-unchanged counters and no duplicate logical records.
/// </para>
/// </summary>
public sealed class SqliteArchiveStore : IArchiveStore
{
    private const string SchemaVersionKey = "user_version";

    /// <summary>
    /// Canonical message column order. Every message read uses this list so a new column cannot
    /// silently shift the positions <see cref="ReadMessage"/> relies on.
    /// </summary>
    private const string MessageColumns = """
        id, conversation_id, sender_id, occurred_at, type, semantic_text, payload_json,
        reply_to_message_id, reply_snapshot_json, source_profile_id, source_conversation_id,
        source_message_id, source_type, source_subtype, source_partition, source_order_key,
        adapter_name, adapter_version, source_version, import_run_id, is_partial,
        reply_source_message_id
        """;

    /// <summary>Canonical timeline order, served by <c>ix_messages_timeline</c>. docs/DATA_MODEL.md section 8.1.</summary>
    private const string MessageOrderAscending = "ASC";

    /// <summary>
    /// The canonical timeline <c>ORDER BY</c> for one direction. The direction is applied to every
    /// ordering column: SQL applies a trailing <c>DESC</c> to the last expression alone, which would
    /// otherwise return the oldest rather than the newest records of a preceding window.
    /// </summary>
    private static string MessageOrderBy(string direction) =>
        $"occurred_utc {direction}, COALESCE(source_order_key, '') {direction}, id {direction}";

    private readonly string _connectionString;
    private readonly IClock _clock;

    public SqliteArchiveStore(string archivePath, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArchivePath = Path.GetFullPath(archivePath);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        var directory = Path.GetDirectoryName(ArchivePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = ArchivePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
    }

    public string ArchivePath { get; }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        using var connection = Open();
        ApplyMigrations(connection);
        return Task.CompletedTask;
    }

    public Task<ImportRun> BeginImportRunAsync(
        string accountId,
        SourceDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var run = new ImportRun
        {
            Id = "run_" + Guid.NewGuid().ToString("n", CultureInfo.InvariantCulture)[..16],
            AccountId = accountId,
            AdapterName = descriptor.AdapterName,
            AdapterVersion = descriptor.AdapterVersion,
            SourceVersion = descriptor.SourceVersion,
            StartedAt = _clock.UtcNow,
            Status = ImportRunStatus.Running,
        };

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO import_runs (id, account_id, adapter_name, adapter_version, source_version,
                                     started_at, status)
            VALUES ($id, $account, $adapter, $adapterVersion, $sourceVersion, $started, $status);
            """;
        command.Parameters.AddWithValue("$id", run.Id);
        command.Parameters.AddWithValue("$account", run.AccountId);
        command.Parameters.AddWithValue("$adapter", run.AdapterName);
        command.Parameters.AddWithValue("$adapterVersion", (object?)run.AdapterVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceVersion", (object?)run.SourceVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$started", Format(run.StartedAt));
        command.Parameters.AddWithValue("$status", run.Status.ToString());
        command.ExecuteNonQuery();

        return Task.FromResult(run);
    }

    public Task CompleteImportRunAsync(ImportRun run, CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE import_runs SET
                finished_at = $finished, status = $status,
                records_scanned = $scanned, records_inserted = $inserted,
                records_updated = $updated, records_skipped = $skipped,
                unknown_count = $unknown, partial_count = $partial,
                warning_count = $warnings, error_count = $errors,
                diagnostics_json = $diagnostics
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", run.Id);
        command.Parameters.AddWithValue("$finished", run.FinishedAt is { } f ? Format(f) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$status", run.Status.ToString());
        command.Parameters.AddWithValue("$scanned", run.RecordsScanned);
        command.Parameters.AddWithValue("$inserted", run.RecordsInserted);
        command.Parameters.AddWithValue("$updated", run.RecordsUpdated);
        command.Parameters.AddWithValue("$skipped", run.RecordsSkipped);
        command.Parameters.AddWithValue("$unknown", run.UnknownCount);
        command.Parameters.AddWithValue("$partial", run.PartialCount);
        command.Parameters.AddWithValue("$warnings", run.WarningCount);
        command.Parameters.AddWithValue("$errors", run.ErrorCount);
        command.Parameters.AddWithValue("$diagnostics", SerializeDiagnostics(run.Diagnostics));
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task UpsertAccountAsync(ArchiveAccount account, CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO accounts (id, source_profile_id, adapter_name, adapter_version, source_version,
                                  display_name, data_root_path)
            VALUES ($id, $profile, $adapter, $adapterVersion, $sourceVersion, $display, $root)
            ON CONFLICT(id) DO UPDATE SET
                adapter_version = excluded.adapter_version,
                source_version  = excluded.source_version,
                display_name    = COALESCE(excluded.display_name, accounts.display_name),
                data_root_path  = COALESCE(excluded.data_root_path, accounts.data_root_path);
            """;
        command.Parameters.AddWithValue("$id", account.Id);
        command.Parameters.AddWithValue("$profile", account.SourceProfileId);
        command.Parameters.AddWithValue("$adapter", account.AdapterName);
        command.Parameters.AddWithValue("$adapterVersion", (object?)account.AdapterVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceVersion", (object?)account.SourceVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$display", (object?)account.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("$root", (object?)account.DataRootPath ?? DBNull.Value);
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task UpsertParticipantsAsync(
        IEnumerable<ArchiveParticipant> participants,
        CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO participants (id, account_id, source_participant_id, latest_remark, nickname, alias, user_display_name)
            VALUES ($id, $account, $source, $remark, $nickname, $alias, $display)
            ON CONFLICT(id) DO UPDATE SET
                latest_remark = COALESCE(excluded.latest_remark, participants.latest_remark),
                nickname      = COALESCE(excluded.nickname, participants.nickname),
                alias         = COALESCE(excluded.alias, participants.alias),
                user_display_name = COALESCE(excluded.user_display_name, participants.user_display_name);
            """;
        var pId = command.Parameters.Add("$id", SqliteType.Text);
        var pAccount = command.Parameters.Add("$account", SqliteType.Text);
        var pSource = command.Parameters.Add("$source", SqliteType.Text);
        var pRemark = command.Parameters.Add("$remark", SqliteType.Text);
        var pNick = command.Parameters.Add("$nickname", SqliteType.Text);
        var pAlias = command.Parameters.Add("$alias", SqliteType.Text);
        var pDisplay = command.Parameters.Add("$display", SqliteType.Text);

        foreach (var participant in participants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pId.Value = participant.Id;
            pAccount.Value = participant.AccountId;
            pSource.Value = participant.SourceParticipantId;
            pRemark.Value = (object?)participant.LatestRemark ?? DBNull.Value;
            pNick.Value = (object?)participant.Nickname ?? DBNull.Value;
            pAlias.Value = (object?)participant.Alias ?? DBNull.Value;
            pDisplay.Value = (object?)participant.UserDisplayName ?? DBNull.Value;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task UpsertConversationsAsync(
        IEnumerable<ArchiveConversation> conversations,
        CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        UpsertConversationsCore(connection, transaction, conversations, cancellationToken);
        transaction.Commit();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Inserts or refreshes conversation rows on a caller-owned transaction so that a staged
    /// import can create its conversation inside the import transaction.
    /// </summary>
    private static void UpsertConversationsCore(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<ArchiveConversation> conversations,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Conversation titles are mutable metadata: they may be refreshed, but they never
        // participate in identity. Message aggregates are recomputed from the archive
        // rather than trusted from the caller.
        command.CommandText =
            """
            INSERT INTO conversations (id, account_id, source_conversation_id, kind, title,
                                       peer_participant_id, owner_participant_id,
                                       first_message_at, last_message_at, message_count)
            VALUES ($id, $account, $source, $kind, $title, $peer, $owner, $first, $last, 0)
            ON CONFLICT(id) DO UPDATE SET
                title                = COALESCE(excluded.title, conversations.title),
                peer_participant_id  = COALESCE(excluded.peer_participant_id, conversations.peer_participant_id),
                owner_participant_id = COALESCE(excluded.owner_participant_id, conversations.owner_participant_id);
            """;
        var pId = command.Parameters.Add("$id", SqliteType.Text);
        var pAccount = command.Parameters.Add("$account", SqliteType.Text);
        var pSource = command.Parameters.Add("$source", SqliteType.Text);
        var pKind = command.Parameters.Add("$kind", SqliteType.Text);
        var pTitle = command.Parameters.Add("$title", SqliteType.Text);
        var pPeer = command.Parameters.Add("$peer", SqliteType.Text);
        var pOwner = command.Parameters.Add("$owner", SqliteType.Text);
        var pFirst = command.Parameters.Add("$first", SqliteType.Text);
        var pLast = command.Parameters.Add("$last", SqliteType.Text);

        foreach (var conversation in conversations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pId.Value = conversation.Id;
            pAccount.Value = conversation.AccountId;
            pSource.Value = conversation.SourceConversationId;
            pKind.Value = conversation.Kind.ToWireName();
            pTitle.Value = (object?)conversation.Title ?? DBNull.Value;
            pPeer.Value = (object?)conversation.PeerParticipantId ?? DBNull.Value;
            pOwner.Value = (object?)conversation.OwnerParticipantId ?? DBNull.Value;
            pFirst.Value = (object?)FormatOrNull(conversation.FirstMessageAt) ?? DBNull.Value;
            pLast.Value = (object?)FormatOrNull(conversation.LastMessageAt) ?? DBNull.Value;
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Opens the archive transaction that stages one conversation import. The conversation row
    /// is created inside it and only becomes visible when the session commits, so a run that
    /// cannot read the source completely never publishes a partial conversation.
    /// docs/ARCHITECTURE.md sections 3.2.1 and 11.
    /// </summary>
    public Task<IConversationImportSession> BeginConversationImportAsync(
        ArchiveConversation conversation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var connection = Open();
        try
        {
            var transaction = connection.BeginTransaction();
            UpsertConversationsCore(connection, transaction, [conversation], cancellationToken);
            IConversationImportSession session =
                new ConversationImportSession(connection, transaction, conversation.Id);
            return Task.FromResult(session);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public Task<UpsertCounters> UpsertMessagesAsync(
        IReadOnlyList<CanonicalMessage> messages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
        {
            return Task.FromResult(new UpsertCounters());
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        var counters = UpsertMessagesCore(connection, transaction, messages, cancellationToken);

        RefreshConversationAggregates(
            connection,
            transaction,
            messages.Select(message => message.ConversationId));

        transaction.Commit();
        return Task.FromResult(counters);
    }

    /// <summary>
    /// The idempotent message upsert itself, on a caller-owned transaction and without
    /// publishing conversation aggregates, so a staged import transaction reuses exactly the
    /// same write path. docs/DATA_MODEL.md section 8.2.
    /// <para>
    /// The derived FTS index is maintained by the same transaction
    /// (docs/SEARCH_SPEC.md section 10.2): a newly inserted message gains its index row, a
    /// message whose semantic hash changed has its index row replaced, and an unchanged replay
    /// writes nothing. There is no staleness window: a conversation is fully indexed when its
    /// import transaction commits, and an index-write failure rolls the whole transaction back so
    /// no unindexed canonical row is ever published.
    /// </para>
    /// </summary>
    private static UpsertCounters UpsertMessagesCore(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<CanonicalMessage> messages,
        CancellationToken cancellationToken)
    {
        var inserted = 0;
        var updated = 0;
        var unchanged = 0;
        var unknown = 0;
        var partial = 0;
        var indexInserts = new List<KeyValuePair<string, string>>();
        var indexReplacements = new List<string>();

        using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT content_hash FROM messages WHERE id = $id;";
        var eId = exists.Parameters.Add("$id", SqliteType.Text);

        using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText =
            """
            INSERT INTO messages (
                id, conversation_id, sender_id, occurred_at, occurred_utc, type, semantic_text,
                payload_json, reply_to_message_id, reply_source_message_id, reply_snapshot_json,
                source_profile_id, source_conversation_id, source_message_id, source_type,
                source_subtype, source_partition, source_order_key, adapter_name, adapter_version,
                source_version, import_run_id, content_hash, is_partial)
            VALUES (
                $id, $conversation, $sender, $occurred, $occurredUtc, $type, $text,
                $payload, NULL, $replySource, $replySnapshot,
                $profile, $sourceConversation, $sourceMessage, $sourceType,
                $sourceSubtype, $partition, $orderKey, $adapter, $adapterVersion,
                $sourceVersion, $importRun, $hash, $partial)
            ON CONFLICT(id) DO UPDATE SET
                sender_id       = excluded.sender_id,
                occurred_at     = excluded.occurred_at,
                occurred_utc    = excluded.occurred_utc,
                type            = excluded.type,
                semantic_text   = excluded.semantic_text,
                payload_json    = excluded.payload_json,
                reply_source_message_id = excluded.reply_source_message_id,
                -- The resolved canonical target is only valid for the upstream id it was
                -- resolved from. If the upstream target changed (the message was edited to
                -- quote a different record), the previously-resolved target is stale and
                -- would fabricate an incorrect relationship: clear it so the backfill pass
                -- re-resolves. The comparison is NULL-safe so a source id that changed to or
                -- from NULL is also detected. docs/DATA_MODEL.md section 8.7.
                reply_to_message_id = CASE
                    WHEN reply_source_message_id IS NOT excluded.reply_source_message_id
                        THEN NULL
                    ELSE reply_to_message_id
                END,
                reply_snapshot_json = excluded.reply_snapshot_json,
                import_run_id   = excluded.import_run_id,
                content_hash    = excluded.content_hash,
                is_partial      = excluded.is_partial;
            """;
        var p = new Dictionary<string, SqliteParameter>(StringComparer.Ordinal)
        {
            ["$id"] = upsert.Parameters.Add("$id", SqliteType.Text),
            ["$conversation"] = upsert.Parameters.Add("$conversation", SqliteType.Text),
            ["$sender"] = upsert.Parameters.Add("$sender", SqliteType.Text),
            ["$occurred"] = upsert.Parameters.Add("$occurred", SqliteType.Text),
            ["$occurredUtc"] = upsert.Parameters.Add("$occurredUtc", SqliteType.Integer),
            ["$type"] = upsert.Parameters.Add("$type", SqliteType.Text),
            ["$text"] = upsert.Parameters.Add("$text", SqliteType.Text),
            ["$payload"] = upsert.Parameters.Add("$payload", SqliteType.Text),
            ["$replySource"] = upsert.Parameters.Add("$replySource", SqliteType.Text),
            ["$replySnapshot"] = upsert.Parameters.Add("$replySnapshot", SqliteType.Text),
            ["$profile"] = upsert.Parameters.Add("$profile", SqliteType.Text),
            ["$sourceConversation"] = upsert.Parameters.Add("$sourceConversation", SqliteType.Text),
            ["$sourceMessage"] = upsert.Parameters.Add("$sourceMessage", SqliteType.Text),
            ["$sourceType"] = upsert.Parameters.Add("$sourceType", SqliteType.Text),
            ["$sourceSubtype"] = upsert.Parameters.Add("$sourceSubtype", SqliteType.Text),
            ["$partition"] = upsert.Parameters.Add("$partition", SqliteType.Text),
            ["$orderKey"] = upsert.Parameters.Add("$orderKey", SqliteType.Text),
            ["$adapter"] = upsert.Parameters.Add("$adapter", SqliteType.Text),
            ["$adapterVersion"] = upsert.Parameters.Add("$adapterVersion", SqliteType.Text),
            ["$sourceVersion"] = upsert.Parameters.Add("$sourceVersion", SqliteType.Text),
            ["$importRun"] = upsert.Parameters.Add("$importRun", SqliteType.Text),
            ["$hash"] = upsert.Parameters.Add("$hash", SqliteType.Text),
            ["$partial"] = upsert.Parameters.Add("$partial", SqliteType.Integer),
        };

        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hash = ContentHash(message);
            eId.Value = message.Id;
            var existing = exists.ExecuteScalar() as string;

            p["$id"].Value = message.Id;
            p["$conversation"].Value = message.ConversationId;
            p["$sender"].Value = (object?)message.SenderId ?? DBNull.Value;
            p["$occurred"].Value = Format(message.OccurredAt);
            p["$occurredUtc"].Value = message.OccurredAt.ToUnixTimeSeconds();
            p["$type"].Value = message.Type.ToWireName();
            p["$text"].Value = message.Text;
            p["$payload"].Value = message.Payload?.ToJsonString() ?? (object)DBNull.Value;
            p["$replySource"].Value = (object?)message.ReplyTo?.SourceMessageId ?? DBNull.Value;
            p["$replySnapshot"].Value = message.ReplyTo is null
                ? DBNull.Value
                : JsonSerializer.Serialize(new ReplySnapshotDto
                {
                    SenderId = message.ReplyTo.SenderId,
                    SenderName = message.ReplyTo.SenderName,
                    Text = message.ReplyTo.Text,
                    Time = FormatOrNull(message.ReplyTo.OccurredAt),
                });
            p["$profile"].Value = message.Source.SourceProfileId;
            p["$sourceConversation"].Value = message.Source.SourceConversationId;
            p["$sourceMessage"].Value = message.Source.SourceMessageId ?? string.Empty;
            p["$sourceType"].Value = (object?)message.Source.SourceType ?? DBNull.Value;
            p["$sourceSubtype"].Value = (object?)message.Source.SourceSubtype ?? DBNull.Value;
            p["$partition"].Value = (object?)message.Source.SourcePartition ?? DBNull.Value;
            p["$orderKey"].Value = (object?)message.Source.SourceOrderKey ?? DBNull.Value;
            p["$adapter"].Value = message.Source.AdapterName;
            p["$adapterVersion"].Value = (object?)message.Source.AdapterVersion ?? DBNull.Value;
            p["$sourceVersion"].Value = (object?)message.Source.SourceVersion ?? DBNull.Value;
            p["$importRun"].Value = (object?)message.Source.ImportRunId ?? DBNull.Value;
            p["$hash"].Value = hash;
            p["$partial"].Value = message.IsPartial ? 1 : 0;

            upsert.ExecuteNonQuery();

            if (existing is null)
            {
                inserted++;
                indexInserts.Add(new(message.Id, SearchableMessageText.Build(message)));
            }
            else if (!string.Equals(existing, hash, StringComparison.Ordinal))
            {
                updated++;
                // The projected body depends on semantic content, so a real update replaces the
                // index row; an unchanged replay touches nothing.
                indexReplacements.Add(message.Id);
                indexInserts.Add(new(message.Id, SearchableMessageText.Build(message)));
            }
            else
            {
                unchanged++;
            }

            if (message.Type == CanonicalMessageType.Unknown)
            {
                unknown++;
            }

            if (message.IsPartial)
            {
                partial++;
            }
        }

        // One batched replacement scan pays for every changed message of this transaction; the
        // delete is content-agnostic, so it is correct for any stored index row.
        ArchiveSearchIndex.DeleteMessageRows(connection, transaction, indexReplacements);
        ArchiveSearchIndex.InsertBodies(connection, transaction, indexInserts);

        return new UpsertCounters
        {
            Inserted = inserted,
            Updated = updated,
            Unchanged = unchanged,
            Unknown = unknown,
            Partial = partial,
        };
    }

    /// <summary>
    /// Recomputes first/last message timestamps and the message count from the archive itself
    /// rather than trusting a caller, inside the caller's transaction.
    /// docs/DATA_MODEL.md section 8.1.
    /// </summary>
    private static void RefreshConversationAggregates(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<string> conversationIds)
    {
        var ids = conversationIds
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (ids.Length == 0)
        {
            return;
        }

        using var aggregate = connection.CreateCommand();
        aggregate.Transaction = transaction;
        aggregate.CommandText =
            """
            UPDATE conversations SET
                first_message_at = (SELECT m.occurred_at FROM messages m WHERE m.conversation_id = conversations.id ORDER BY m.occurred_utc ASC LIMIT 1),
                last_message_at  = (SELECT m.occurred_at FROM messages m WHERE m.conversation_id = conversations.id ORDER BY m.occurred_utc DESC LIMIT 1),
                message_count    = (SELECT COUNT(*) FROM messages m WHERE m.conversation_id = conversations.id)
            WHERE id IN (SELECT value FROM json_each($ids));
            """;
        aggregate.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids));
        aggregate.ExecuteNonQuery();
    }

    /// <summary>
    /// One conversation import's staged transaction. Disposing a session that was neither
    /// committed nor rolled back discards it, so an abandoned import cannot leave partial
    /// archive records behind.
    /// </summary>
    private sealed class ConversationImportSession(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string conversationId) : IConversationImportSession
    {
        private bool _finished;

        public Task<UpsertCounters> UpsertMessagesAsync(
            IReadOnlyList<CanonicalMessage> messages,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(messages);
            EnsureStaged();

            return Task.FromResult(messages.Count == 0
                ? new UpsertCounters()
                : UpsertMessagesCore(connection, transaction, messages, cancellationToken));
        }

        public Task SetIngestCheckpointAsync(IngestCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            EnsureStaged();
            cancellationToken.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO ingest_checkpoints (id, account_id, adapter_family, scope_kind, scope_id, checkpoint_json, updated_at)
                VALUES ($id, $account, $family, $kind, $scope, $json, $updated)
                ON CONFLICT(account_id, adapter_family, scope_kind, scope_id) DO UPDATE SET
                    checkpoint_json=excluded.checkpoint_json, updated_at=excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$id", checkpoint.Id);
            command.Parameters.AddWithValue("$account", checkpoint.AccountId);
            command.Parameters.AddWithValue("$family", checkpoint.AdapterFamily);
            command.Parameters.AddWithValue("$kind", checkpoint.ScopeKind);
            command.Parameters.AddWithValue("$scope", checkpoint.ScopeId);
            command.Parameters.AddWithValue("$json", checkpoint.CheckpointJson);
            command.Parameters.AddWithValue("$updated", Format(checkpoint.UpdatedAt));
            command.ExecuteNonQuery();
            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            EnsureStaged();
            cancellationToken.ThrowIfCancellationRequested();

            // The aggregates are published together with the records they describe, so a
            // committed conversation can never advertise a message count the archive does not
            // hold.
            RefreshConversationAggregates(connection, transaction, [conversationId]);
            transaction.Commit();
            _finished = true;
            return Task.CompletedTask;
        }

        public Task RollbackAsync()
        {
            if (!_finished)
            {
                _finished = true;
                transaction.Rollback();
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!_finished)
            {
                _finished = true;
                try
                {
                    transaction.Rollback();
                }
                catch (InvalidOperationException)
                {
                    // The provider already finished the transaction; there is nothing to undo.
                }
            }

            transaction.Dispose();
            connection.Dispose();
            return ValueTask.CompletedTask;
        }

        private void EnsureStaged()
        {
            if (_finished)
            {
                throw new InvalidOperationException(
                    "The conversation import session was already committed or rolled back.");
            }
        }
    }

    public Task<IReadOnlyList<ArchiveAccount>> ListAccountsAsync(CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, source_profile_id, adapter_name, adapter_version, source_version, display_name, data_root_path FROM accounts ORDER BY source_profile_id;";
        using var reader = command.ExecuteReader();
        var result = new List<ArchiveAccount>();
        while (reader.Read())
        {
            result.Add(new ArchiveAccount
            {
                Id = reader.GetString(0),
                SourceProfileId = reader.GetString(1),
                AdapterName = reader.GetString(2),
                AdapterVersion = reader.IsDBNull(3) ? null : reader.GetString(3),
                SourceVersion = reader.IsDBNull(4) ? null : reader.GetString(4),
                DisplayName = reader.IsDBNull(5) ? null : reader.GetString(5),
                DataRootPath = reader.IsDBNull(6) ? null : reader.GetString(6),
            });
        }

        return Task.FromResult<IReadOnlyList<ArchiveAccount>>(result);
    }

    public Task<IReadOnlyList<ArchiveConversation>> ListConversationsAsync(
        string accountId,
        CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, account_id, source_conversation_id, kind, title, peer_participant_id,
                   owner_participant_id, first_message_at, last_message_at, message_count
            FROM conversations WHERE account_id = $account
            ORDER BY COALESCE(last_message_at, '') DESC, id;
            """;
        command.Parameters.AddWithValue("$account", accountId);
        using var reader = command.ExecuteReader();
        var result = new List<ArchiveConversation>();
        while (reader.Read())
        {
            result.Add(ReadConversation(reader));
        }

        return Task.FromResult<IReadOnlyList<ArchiveConversation>>(result);
    }

    public Task<ArchiveConversation?> GetConversationAsync(
        string conversationId,
        CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, account_id, source_conversation_id, kind, title, peer_participant_id,
                   owner_participant_id, first_message_at, last_message_at, message_count
            FROM conversations WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", conversationId);
        using var reader = command.ExecuteReader();
        ArchiveConversation? conversation = reader.Read() ? ReadConversation(reader) : null;
        return Task.FromResult(conversation);
    }

    /// <summary>
    /// Projects the persisted ingest diagnostics that belong to one conversation's canonical
    /// records. Only audit rows an import run already wrote are read
    /// (<c>import_runs.diagnostics_json</c> joined through <c>messages.import_run_id</c>), in a
    /// deterministic run-id order, so export can populate <c>manifest.diagnostics</c> without any
    /// live-source read (Issue #66, Gap D, docs/DATA_MODEL.md section 13).
    /// </summary>
    public Task<IReadOnlyList<ImportDiagnostic>> ReadConversationDiagnosticsAsync(
        string conversationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.diagnostics_json
            FROM import_runs r
            WHERE r.diagnostics_json IS NOT NULL
              AND r.id IN (
                  SELECT DISTINCT import_run_id
                  FROM messages
                  WHERE conversation_id = $conversation AND import_run_id IS NOT NULL)
            ORDER BY r.id;
            """;
        command.Parameters.AddWithValue("$conversation", conversationId);

        var diagnostics = new List<ImportDiagnostic>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            diagnostics.AddRange(DeserializeDiagnostics(reader.GetString(0)));
        }

        return Task.FromResult<IReadOnlyList<ImportDiagnostic>>(diagnostics);
    }

    public Task<IReadOnlyList<ArchiveParticipant>> ListParticipantsAsync(
        string accountId,
        CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, account_id, source_participant_id, latest_remark, nickname, alias, user_display_name
            FROM participants WHERE account_id = $account ORDER BY id;
            """;
        command.Parameters.AddWithValue("$account", accountId);
        using var reader = command.ExecuteReader();
        var result = new List<ArchiveParticipant>();
        while (reader.Read())
        {
            result.Add(new ArchiveParticipant
            {
                Id = reader.GetString(0),
                AccountId = reader.GetString(1),
                SourceParticipantId = reader.GetString(2),
                LatestRemark = reader.IsDBNull(3) ? null : reader.GetString(3),
                Nickname = reader.IsDBNull(4) ? null : reader.GetString(4),
                Alias = reader.IsDBNull(5) ? null : reader.GetString(5),
                UserDisplayName = reader.IsDBNull(6) ? null : reader.GetString(6),
            });
        }

        return Task.FromResult<IReadOnlyList<ArchiveParticipant>>(result);
    }

    public Task<IReadOnlyList<CanonicalMessage>> ReadMessagesAsync(
        string conversationId,
        CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {MessageColumns}
            FROM messages WHERE conversation_id = $id
            ORDER BY {MessageOrderBy(MessageOrderAscending)};
            """;
        command.Parameters.AddWithValue("$id", conversationId);

        using var reader = command.ExecuteReader();
        var result = new List<CanonicalMessage>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(ReadMessage(reader));
        }

        return Task.FromResult<IReadOnlyList<CanonicalMessage>>(result);
    }

    /// <summary>
    /// One bounded keyset page of a conversation's canonical timeline, or of the union of a
    /// resolved Collection's member conversations in the same canonical timeline order across all
    /// of them. docs/DATA_MODEL.md section 23.
    /// <para>
    /// The scope is exactly one of <see cref="ArchiveMessageQuery.ConversationId"/> or
    /// <see cref="ArchiveMessageQuery.ConversationIds"/> — the member list uses the same
    /// <c>json_each</c> member filter the search and statistics scopes use. The cursor position is
    /// compared in the same order the query sorts by, and one extra row is read so "there are more
    /// records" is a fact about the archive rather than an inference from a full page. Equal
    /// timestamps are separated by <c>source_order_key</c> and then by the stable message id,
    /// which is globally unique, so the order is a total order across conversations too and a
    /// resume neither repeats nor skips a record.
    /// </para>
    /// </summary>
    public Task<ArchiveMessagePage> QueryMessagesAsync(
        ArchiveMessageQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        // Exactly one scope: one conversation, or the union of a resolved Collection's membership.
        // An unscoped or double-scoped query would silently change what a page covers, so it is a
        // caller-contract violation rather than a result shape.
        if (query.ConversationId is null && query.ConversationIds is null)
            throw new ArgumentException(
                "a message query must scope one conversation or a conversation member list.",
                nameof(query));
        if (query.ConversationId is not null && query.ConversationIds is not null)
            throw new ArgumentException(
                "a message query must scope either one conversation or a conversation member list, not both.",
                nameof(query));

        using var connection = Open();
        using var command = connection.CreateCommand();

        var sql = new StringBuilder();
        sql.Append("SELECT ").Append(MessageColumns).Append(" FROM messages WHERE ");
        if (query.ConversationId is not null)
        {
            sql.Append("conversation_id = $conversation");
            command.Parameters.AddWithValue("$conversation", query.ConversationId);
        }
        else
        {
            sql.Append("conversation_id IN (SELECT value FROM json_each($conversations))");
            command.Parameters.AddWithValue("$conversations", JsonSerializer.Serialize(query.ConversationIds));
        }

        if (query.Since is not null)
        {
            sql.Append(" AND occurred_utc >= $since");
            command.Parameters.AddWithValue("$since", query.Since.Value.ToUnixTimeSeconds());
        }

        if (query.Until is not null)
        {
            sql.Append(" AND occurred_utc <= $until");
            command.Parameters.AddWithValue("$until", query.Until.Value.ToUnixTimeSeconds());
        }

        if (query.ParticipantId is not null)
        {
            sql.Append(" AND sender_id = $sender");
            command.Parameters.AddWithValue("$sender", query.ParticipantId);
        }

        if (query.Type is not null)
        {
            sql.Append(" AND type = $type");
            command.Parameters.AddWithValue("$type", query.Type.Value.ToWireName());
        }

        if (query.After is not null)
        {
            sql.Append(" AND (").Append(PositionPredicate(">", "$afterUtc", "$afterOrder", "$afterId")).Append(')');
            command.Parameters.AddWithValue("$afterUtc", query.After.OccurredUtc);
            command.Parameters.AddWithValue("$afterOrder", query.After.SourceOrderKey);
            command.Parameters.AddWithValue("$afterId", query.After.MessageId);
        }

        sql.Append(" ORDER BY ").Append(MessageOrderBy(MessageOrderAscending)).Append(" LIMIT $limit;");
        command.Parameters.AddWithValue("$limit", query.Limit + 1);
        command.CommandText = sql.ToString();

        using var reader = command.ExecuteReader();
        var items = new List<CanonicalMessage>(Math.Min(query.Limit, 128));
        var hasMore = false;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (items.Count == query.Limit)
            {
                hasMore = true;
                break;
            }

            items.Add(ReadMessage(reader));
        }

        return Task.FromResult(new ArchiveMessagePage { Items = items, HasMore = hasMore });
    }

    /// <summary>
    /// The bounded canonical window around one stable message ID, or null when the archive holds
    /// no such message. The target's own conversation scopes both sides, so a context window never
    /// crosses into another conversation's timeline.
    /// </summary>
    public Task<ArchiveMessageContext?> ReadMessageContextAsync(
        string messageId,
        int before,
        int after,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = Open();

        CanonicalMessage? target;
        using (var targetCommand = connection.CreateCommand())
        {
            targetCommand.CommandText = $"SELECT {MessageColumns} FROM messages WHERE id = $id;";
            targetCommand.Parameters.AddWithValue("$id", messageId);
            using var reader = targetCommand.ExecuteReader();
            target = reader.Read() ? ReadMessage(reader) : null;
        }

        if (target is null)
            return Task.FromResult<ArchiveMessageContext?>(null);

        var position = new MessageOrderKey
        {
            OccurredUtc = target.OccurredAt.ToUnixTimeSeconds(),
            SourceOrderKey = target.Source.SourceOrderKey ?? string.Empty,
            MessageId = target.Id,
        };

        return Task.FromResult<ArchiveMessageContext?>(new ArchiveMessageContext
        {
            Target = target,
            Before = ReadWindow(connection, target.ConversationId, position, before, forward: false, cancellationToken),
            After = ReadWindow(connection, target.ConversationId, position, after, forward: true, cancellationToken),
        });
    }

    /// <summary>
    /// Reads up to <paramref name="count"/> records strictly before or after a timeline position.
    /// A "before" window is read newest-first and reversed so both windows come back in timeline
    /// order, and the caller never has to correct the archive's ordering.
    /// </summary>
    private static IReadOnlyList<CanonicalMessage> ReadWindow(
        SqliteConnection connection,
        string conversationId,
        MessageOrderKey position,
        int count,
        bool forward,
        CancellationToken cancellationToken)
    {
        if (count == 0)
            return [];

        using var command = connection.CreateCommand();
        var direction = forward ? "ASC" : "DESC";
        command.CommandText =
            $"""
            SELECT {MessageColumns} FROM messages
            WHERE conversation_id = $conversation
              AND ({PositionPredicate(forward ? ">" : "<", "$positionUtc", "$positionOrder", "$positionId")})
            ORDER BY {MessageOrderBy(direction)}
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$positionUtc", position.OccurredUtc);
        command.Parameters.AddWithValue("$positionOrder", position.SourceOrderKey);
        command.Parameters.AddWithValue("$positionId", position.MessageId);
        command.Parameters.AddWithValue("$limit", count);

        using var reader = command.ExecuteReader();
        var items = new List<CanonicalMessage>(count);
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(ReadMessage(reader));
        }

        if (!forward)
            items.Reverse();

        return items;
    }

    /// <summary>
    /// The keyset predicate for "strictly after (or before) this timeline position", written
    /// against the same <c>COALESCE(source_order_key, '')</c> expression the reads order by, so a
    /// resume continues exactly where the previous page stopped.
    /// </summary>
    private static string PositionPredicate(
        string comparison,
        string utcParameter,
        string orderParameter,
        string idParameter) =>
        $"""
        occurred_utc {comparison} {utcParameter}
             OR (occurred_utc = {utcParameter}
                 AND (COALESCE(source_order_key, '') {comparison} {orderParameter}
                      OR (COALESCE(source_order_key, '') = {orderParameter} AND id {comparison} {idParameter})))
        """;

    /// <summary>
    /// Enumerates every persisted ingest checkpoint row in a deterministic order.
    /// <para>
    /// This is a neutral read of rows the archive owns: it deliberately does not interpret the
    /// opaque <c>checkpoint_json</c> payload and does not classify scope kinds, because both are
    /// owned by the component that writes the cursor. Interpreting them here would make the
    /// canonical persistence boundary depend on the Raw Vault ingest implementation's vocabulary.
    /// Ingest progress is projected by <c>RawVaultIngestService</c> through
    /// <see cref="IIngestProgressSource"/>.
    /// </para>
    /// <para>
    /// The order covers every column of the table's uniqueness key
    /// (<c>account_id, adapter_family, scope_kind, scope_id</c>), so it is a total order and a
    /// caller that breaks ties by row position still gets a deterministic answer.
    /// </para>
    /// </summary>
    public Task<IReadOnlyList<IngestCheckpoint>> ListIngestCheckpointsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, account_id, adapter_family, scope_kind, scope_id, checkpoint_json, updated_at
            FROM ingest_checkpoints
            ORDER BY account_id, adapter_family, scope_kind, scope_id;
            """;

        using var reader = command.ExecuteReader();
        var rows = new List<IngestCheckpoint>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(new IngestCheckpoint
            {
                Id = reader.GetString(0),
                AccountId = reader.GetString(1),
                AdapterFamily = reader.GetString(2),
                ScopeKind = reader.GetString(3),
                ScopeId = reader.GetString(4),
                CheckpointJson = reader.GetString(5),
                UpdatedAt = ParseTimestamp(reader.GetString(6)),
            });
        }

        return Task.FromResult<IReadOnlyList<IngestCheckpoint>>(rows);
    }

    public Task<int> ResolveReplyTargetsAsync(string conversationId, CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE messages
            SET reply_to_message_id = (
                SELECT r.id FROM messages r
                WHERE r.conversation_id = messages.conversation_id
                  AND r.source_message_id = messages.reply_source_message_id
                LIMIT 1)
            WHERE conversation_id = $id
              AND reply_to_message_id IS NULL
              AND reply_source_message_id IS NOT NULL
              AND EXISTS (
                SELECT 1 FROM messages r
                WHERE r.conversation_id = messages.conversation_id
                  AND r.source_message_id = messages.reply_source_message_id);
            """;
        command.Parameters.AddWithValue("$id", conversationId);
        return Task.FromResult(command.ExecuteNonQuery());
    }

    public Task<ConversationStats> GetConversationStatsAsync(
        string conversationId,
        CancellationToken cancellationToken)
    {
        using var connection = Open();

        using var head = connection.CreateCommand();
        head.CommandText =
            """
            SELECT COUNT(*),
                   SUM(CASE WHEN type = 'unknown' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN is_partial = 1 THEN 1 ELSE 0 END),
                   (SELECT m.occurred_at FROM messages m WHERE m.conversation_id = $id ORDER BY m.occurred_utc ASC LIMIT 1),
                   (SELECT m.occurred_at FROM messages m WHERE m.conversation_id = $id ORDER BY m.occurred_utc DESC LIMIT 1)
            FROM messages WHERE conversation_id = $id;
            """;
        head.Parameters.AddWithValue("$id", conversationId);
        using var reader = head.ExecuteReader();
        var count = 0;
        var unknowns = 0;
        var partials = 0;
        DateTimeOffset? first = null;
        DateTimeOffset? last = null;
        if (reader.Read())
        {
            count = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            unknowns = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            partials = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            first = reader.IsDBNull(3) ? null : ParseTimestamp(reader.GetString(3));
            last = reader.IsDBNull(4) ? null : ParseTimestamp(reader.GetString(4));
        }

        reader.Close();

        using var types = connection.CreateCommand();
        types.CommandText =
            "SELECT type, COUNT(*) FROM messages WHERE conversation_id = $id GROUP BY type;";
        types.Parameters.AddWithValue("$id", conversationId);
        using var typeReader = types.ExecuteReader();
        var counts = new Dictionary<CanonicalMessageType, int>();
        while (typeReader.Read())
        {
            CanonicalMessageTypes.TryParse(typeReader.GetString(0), out var type);
            counts[type] = typeReader.GetInt32(1);
        }

        return Task.FromResult(new ConversationStats
        {
            ConversationId = conversationId,
            MessageCount = count,
            UnknownCount = unknowns,
            PartialCount = partials,
            FirstMessageAt = first,
            LastMessageAt = last,
            TypeCounts = counts,
        });
    }

    public Task<ArchiveStats> GetArchiveStatsAsync(CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT (SELECT COUNT(*) FROM accounts),
                   (SELECT COUNT(*) FROM conversations),
                   (SELECT COUNT(*) FROM participants),
                   (SELECT COUNT(*) FROM messages),
                   (SELECT m.occurred_at FROM messages m ORDER BY m.occurred_utc DESC LIMIT 1);
            """;
        using var reader = command.ExecuteReader();
        var stats = new ArchiveStats { ArchivePath = ArchivePath };
        if (reader.Read())
        {
            stats = stats with
            {
                AccountCount = reader.GetInt32(0),
                ConversationCount = reader.GetInt32(1),
                ParticipantCount = reader.GetInt32(2),
                MessageCount = reader.GetInt32(3),
                LastMessageAt = reader.IsDBNull(4) ? null : ParseTimestamp(reader.GetString(4)),
            };
        }

        return Task.FromResult(stats);
    }

    /// <summary>
    /// One read-only statistics aggregate over the canonical <c>messages</c> rows
    /// (docs/DATA_MODEL.md section 24). Every section is derived at query time from the same
    /// scope/range filter — no derived statistics state exists to read or write — and unknown,
    /// partial and unattributed records are counted wherever they fall instead of being dropped.
    /// </summary>
    public Task<ArchiveStatisticsResult> GetArchiveStatisticsAsync(
        ArchiveStatisticsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = Open();

        // One scope/range filter, shared verbatim by the totals, the type distribution, the
        // first/last subselects and the breakdown page, so the sections of one result can never
        // disagree about what was counted. The alias is substituted so the same fragment serves
        // both the outer queries (m) and the totals' first/last subselects (m2).
        string Filter(string alias)
        {
            var conditions = new List<string>(3);
            if (query.ConversationIds is not null)
            {
                conditions.Add(
                    $"{alias}.conversation_id IN (SELECT value FROM json_each($conversations))");
            }

            if (query.Since is not null)
            {
                conditions.Add($"{alias}.occurred_utc >= $since");
            }

            if (query.Until is not null)
            {
                conditions.Add($"{alias}.occurred_utc <= $until");
            }

            return conditions.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", conditions);
        }

        void BindScope(SqliteCommand command)
        {
            if (query.ConversationIds is not null)
            {
                command.Parameters.AddWithValue(
                    "$conversations", JsonSerializer.Serialize(query.ConversationIds));
            }

            if (query.Since is not null)
            {
                command.Parameters.AddWithValue("$since", query.Since.Value.ToUnixTimeSeconds());
            }

            if (query.Until is not null)
            {
                command.Parameters.AddWithValue("$until", query.Until.Value.ToUnixTimeSeconds());
            }
        }

        var scope = Filter("m");

        // First/last observed instants in canonical timeline order (section 8.1), so a shared
        // instant is placed by the same rule that orders a listing. Both are facts about the
        // counted rows; an empty scope reports null rather than an invented instant.
        using var totals = connection.CreateCommand();
        totals.CommandText =
            $"""
            SELECT COUNT(*),
                   COUNT(DISTINCT m.conversation_id),
                   COUNT(DISTINCT m.sender_id),
                   SUM(m.sender_id IS NULL),
                   SUM(m.type = 'unknown'),
                   SUM(m.is_partial = 1),
                   (SELECT m2.occurred_at FROM messages m2{Filter("m2")}
                    ORDER BY m2.occurred_utc ASC, COALESCE(m2.source_order_key, '') ASC, m2.id ASC LIMIT 1),
                   (SELECT m2.occurred_at FROM messages m2{Filter("m2")}
                    ORDER BY m2.occurred_utc DESC, COALESCE(m2.source_order_key, '') DESC, m2.id DESC LIMIT 1)
            FROM messages m{scope};
            """;
        BindScope(totals);

        StatisticsTotals totalsResult;
        using (var reader = totals.ExecuteReader())
        {
            reader.Read();
            totalsResult = new StatisticsTotals
            {
                MessageCount = reader.GetInt32(0),
                ConversationCount = reader.GetInt32(1),
                SenderCount = reader.GetInt32(2),
                UnattributedCount = reader.IsDBNull(3) ? 0 : (int)reader.GetInt64(3),
                UnknownCount = reader.IsDBNull(4) ? 0 : (int)reader.GetInt64(4),
                PartialCount = reader.IsDBNull(5) ? 0 : (int)reader.GetInt64(5),
                FirstMessageAt = reader.IsDBNull(6) ? null : ParseTimestamp(reader.GetString(6)),
                LastMessageAt = reader.IsDBNull(7) ? null : ParseTimestamp(reader.GetString(7)),
            };
        }

        // The canonical type inventory is closed (MESSAGE_SCHEMA section 4), so this listing
        // is inherently bounded. Only observed types appear — zero rows are never invented.
        var byType = new List<StatisticsTypeCount>();
        using (var types = connection.CreateCommand())
        {
            types.CommandText =
                $"""
                SELECT m.type, COUNT(*) FROM messages m{scope} GROUP BY m.type ORDER BY m.type ASC;
                """;
            BindScope(types);
            using var typeReader = types.ExecuteReader();
            while (typeReader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                byType.Add(new StatisticsTypeCount
                {
                    Type = typeReader.GetString(0),
                    Count = typeReader.GetInt32(1),
                });
            }
        }

        var (rows, hasMore) = query.Breakdown switch
        {
            StatisticsBreakdownKind.Day => ReadBucketBreakdown(
                connection, query, cancellationToken,
                bucketExpression: "(m.occurred_utc / 86400) * 86400",
                afterExpression: "(m.occurred_utc / 86400) * 86400",
                afterValue: ParseDayBucketAfter(query.BreakdownAfter),
                bindScope: BindScope,
                isMonthBucket: false),
            StatisticsBreakdownKind.Month => ReadBucketBreakdown(
                connection, query, cancellationToken,
                bucketExpression: "strftime('%Y-%m', m.occurred_utc, 'unixepoch')",
                afterExpression: "strftime('%Y-%m', m.occurred_utc, 'unixepoch')",
                afterValue: query.BreakdownAfter,
                bindScope: BindScope,
                isMonthBucket: true),
            _ => ReadConversationBreakdown(connection, query, cancellationToken, BindScope),
        };

        return Task.FromResult(new ArchiveStatisticsResult
        {
            Totals = totalsResult,
            ByType = byType,
            BreakdownItems = rows,
            BreakdownHasMore = hasMore,
        });
    }

    /// <summary>
    /// Reads one bounded keyset page of the day/month bucket breakdown. The page reads one bucket
    /// beyond its limit, so <c>has_more</c> is a fact about the archive rather than an inference
    /// from a full page (docs/DATA_MODEL.md section 24). The bucket key arrives as a bound
    /// parameter; caller input never becomes SQL syntax.
    /// </summary>
    private static (IReadOnlyList<StatisticsBreakdownRow> Rows, bool HasMore) ReadBucketBreakdown(
        SqliteConnection connection,
        ArchiveStatisticsQuery query,
        CancellationToken cancellationToken,
        string bucketExpression,
        string afterExpression,
        object? afterValue,
        Action<SqliteCommand> bindScope,
        bool isMonthBucket)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {bucketExpression} AS bucket_key,
                   COUNT(*),
                   SUM(m.type = 'unknown'),
                   SUM(m.is_partial = 1)
            FROM messages m
            {FilterFor(query, afterValue is null ? null : $"{afterExpression} > $after")}
            GROUP BY {bucketExpression}
            ORDER BY bucket_key ASC
            LIMIT $limit;
            """;
        bindScope(command);
        if (afterValue is not null)
        {
            command.Parameters.AddWithValue("$after", afterValue);
        }

        command.Parameters.AddWithValue("$limit", query.BreakdownLimit + 1);

        var rows = new List<StatisticsBreakdownRow>(Math.Min(query.BreakdownLimit, 128));
        var hasMore = false;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rows.Count == query.BreakdownLimit)
            {
                hasMore = true;
                break;
            }

            var messageCount = reader.GetInt64(1);
            var unknownCount = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
            var partialCount = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
            rows.Add(isMonthBucket
                ? MonthRow(reader.GetString(0), messageCount, unknownCount, partialCount)
                : DayRow(reader.GetInt64(0), messageCount, unknownCount, partialCount));
        }

        return (rows, hasMore);

        static StatisticsBreakdownRow DayRow(long epoch, long messageCount, long unknownCount, long partialCount) => new()
        {
            BucketStart = DateTimeOffset.FromUnixTimeSeconds(epoch),
            MessageCount = (int)messageCount,
            UnknownCount = (int)unknownCount,
            PartialCount = (int)partialCount,
        };

        static StatisticsBreakdownRow MonthRow(string monthKey, long messageCount, long unknownCount, long partialCount)
        {
            var year = int.Parse(monthKey.AsSpan(0, 4), CultureInfo.InvariantCulture);
            var month = int.Parse(monthKey.AsSpan(5, 2), CultureInfo.InvariantCulture);
            return new StatisticsBreakdownRow
            {
                BucketStart = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero),
                MessageCount = (int)messageCount,
                UnknownCount = (int)unknownCount,
                PartialCount = (int)partialCount,
            };
        }
    }

    /// <summary>
    /// Reads one bounded keyset page of the per-conversation breakdown, keyed by the stable
    /// conversation ID in ordinal order so every page is deterministic.
    /// </summary>
    private static (IReadOnlyList<StatisticsBreakdownRow> Rows, bool HasMore) ReadConversationBreakdown(
        SqliteConnection connection,
        ArchiveStatisticsQuery query,
        CancellationToken cancellationToken,
        Action<SqliteCommand> bindScope)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT m.conversation_id AS bucket_key,
                   COUNT(*),
                   SUM(m.type = 'unknown'),
                   SUM(m.is_partial = 1)
            FROM messages m
            {FilterFor(query, query.BreakdownAfter is null ? null : "m.conversation_id > $after")}
            GROUP BY m.conversation_id
            ORDER BY bucket_key ASC
            LIMIT $limit;
            """;
        bindScope(command);
        if (query.BreakdownAfter is not null)
        {
            command.Parameters.AddWithValue("$after", query.BreakdownAfter);
        }

        command.Parameters.AddWithValue("$limit", query.BreakdownLimit + 1);

        var rows = new List<StatisticsBreakdownRow>(Math.Min(query.BreakdownLimit, 128));
        var hasMore = false;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rows.Count == query.BreakdownLimit)
            {
                hasMore = true;
                break;
            }

            rows.Add(new StatisticsBreakdownRow
            {
                ConversationId = reader.GetString(0),
                MessageCount = (int)reader.GetInt64(1),
                UnknownCount = reader.IsDBNull(2) ? 0 : (int)reader.GetInt64(2),
                PartialCount = reader.IsDBNull(3) ? 0 : (int)reader.GetInt64(3),
            });
        }

        return (rows, hasMore);
    }

    /// <summary>
    /// Builds the shared scope/range <c>WHERE</c> fragment for the statistics queries, optionally
    /// joined with the breakdown's keyset condition so the page filter is part of one clause no
    /// matter which filters the caller supplied.
    /// </summary>
    private static string FilterFor(ArchiveStatisticsQuery query, string? extraCondition = null)
    {
        var conditions = new List<string>(4);
        if (query.ConversationIds is not null)
        {
            conditions.Add("m.conversation_id IN (SELECT value FROM json_each($conversations))");
        }

        if (query.Since is not null)
        {
            conditions.Add("m.occurred_utc >= $since");
        }

        if (query.Until is not null)
        {
            conditions.Add("m.occurred_utc <= $until");
        }

        if (extraCondition is not null)
        {
            conditions.Add(extraCondition);
        }

        return conditions.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", conditions);
    }

    /// <summary>
    /// The exclusive day-bucket keyset position. The service validated the spelling; a value that
    /// is not a non-negative multiple of a day fails closed instead of silently starting mid-bucket.
    /// </summary>
    private static long? ParseDayBucketAfter(string? after)
    {
        if (after is null)
        {
            return null;
        }

        if (!long.TryParse(after, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
            || epoch < 0
            || epoch % 86400 != 0)
        {
            throw ArchiveQueryException.InvalidCursor("the cursor is not a valid pagination cursor.");
        }

        return epoch;
    }

    public Task<IngestCheckpoint?> GetIngestCheckpointAsync(
        string accountId, string adapterFamily, string scopeKind, string scopeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, account_id, adapter_family, scope_kind, scope_id, checkpoint_json, updated_at FROM ingest_checkpoints WHERE account_id=$account AND adapter_family=$family AND scope_kind=$kind AND scope_id=$scope;";
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$family", adapterFamily);
        command.Parameters.AddWithValue("$kind", scopeKind);
        command.Parameters.AddWithValue("$scope", scopeId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return Task.FromResult<IngestCheckpoint?>(null);
        return Task.FromResult<IngestCheckpoint?>(new IngestCheckpoint
        {
            Id = reader.GetString(0), AccountId = reader.GetString(1), AdapterFamily = reader.GetString(2),
            ScopeKind = reader.GetString(3), ScopeId = reader.GetString(4), CheckpointJson = reader.GetString(5),
            UpdatedAt = ParseTimestamp(reader.GetString(6)),
        });
    }

    public Task SetIngestCheckpointAsync(IngestCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ingest_checkpoints (id, account_id, adapter_family, scope_kind, scope_id, checkpoint_json, updated_at)
            VALUES ($id, $account, $family, $kind, $scope, $json, $updated)
            ON CONFLICT(account_id, adapter_family, scope_kind, scope_id) DO UPDATE SET
                checkpoint_json=excluded.checkpoint_json, updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", checkpoint.Id);
        command.Parameters.AddWithValue("$account", checkpoint.AccountId);
        command.Parameters.AddWithValue("$family", checkpoint.AdapterFamily);
        command.Parameters.AddWithValue("$kind", checkpoint.ScopeKind);
        command.Parameters.AddWithValue("$scope", checkpoint.ScopeId);
        command.Parameters.AddWithValue("$json", checkpoint.CheckpointJson);
        command.Parameters.AddWithValue("$updated", Format(checkpoint.UpdatedAt));
        command.ExecuteNonQuery();
        transaction.Commit();
        return Task.CompletedTask;
    }

    /// <summary>
    /// One bounded page of the canonical full-text search, over the derived FTS index this store
    /// maintains. docs/SEARCH_SPEC.md sections 5–8, docs/DATA_MODEL.md section 19.
    /// <para>
    /// Search fails closed: a missing or inconsistent index is
    /// <see cref="WeArchive.Core.Query.ArchiveQueryErrorCodes.SearchIndexUnavailable"/>, never a
    /// silently partial result set, and the index is never repaired by a query (R0). The guard is
    /// the cheap part of the section 10.4 verification — index existence plus row-count agreement
    /// — while the FTS5 integrity-check remains an explicit maintenance operation.
    /// </para>
    /// <para>
    /// Ordering is the search total order: FTS5 bm25 rank ascending, then the canonical timeline
    /// tail. The keyset predicate compares the same expressions the order by uses, so resuming a
    /// page neither repeats nor skips a row of equal rank (docs/SEARCH_SPEC.md section 6).
    /// </para>
    /// </summary>
    public Task<ArchiveSearchPage> SearchMessagesAsync(
        ArchiveSearchQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = Open();

        // Fail-closed guard: the derived index must exist and agree with the canonical rows it
        // derives from. Any deeper corruption is diagnosed by VerifySearchIndexAsync and repaired
        // by RebuildSearchIndexAsync, never here.
        if (!ArchiveSearchIndex.TableExists(connection))
        {
            throw ArchiveQueryException.IndexUnavailable(
                "the FTS search index does not exist in this archive; run the archive maintenance index rebuild to restore search.");
        }

        long messageCount;
        long indexCount;
        using (var counts = connection.CreateCommand())
        {
            counts.CommandText = $"SELECT (SELECT COUNT(*) FROM messages), (SELECT COUNT(*) FROM {ArchiveSearchIndex.TableName});";
            using var reader = counts.ExecuteReader();
            reader.Read();
            messageCount = reader.GetInt64(0);
            indexCount = reader.GetInt64(1);
        }

        if (messageCount != indexCount)
        {
            throw ArchiveQueryException.IndexUnavailable(
                $"the FTS search index does not agree with the canonical archive ({indexCount} index rows for {messageCount} messages); run the archive maintenance index rebuild to restore search.");
        }

        var sql = new StringBuilder();
        sql.Append("SELECT ").Append(MessageColumns).Append(", f.rank")
           .Append(" FROM messages m")
           .Append(" JOIN ").Append(ArchiveSearchIndex.TableName).Append(" f ON f.message_id = m.id")
           .Append(" WHERE f.messages_fts MATCH $expr");
        var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$expr", query.MatchExpression);

        if (query.ConversationId is not null)
        {
            sql.Append(" AND m.conversation_id = $conversation");
            command.Parameters.AddWithValue("$conversation", query.ConversationId);
        }

        if (query.ConversationIds is not null)
        {
            sql.Append(" AND m.conversation_id IN (SELECT value FROM json_each($conversations))");
            command.Parameters.AddWithValue("$conversations", JsonSerializer.Serialize(query.ConversationIds));
        }

        if (query.Since is not null)
        {
            sql.Append(" AND m.occurred_utc >= $since");
            command.Parameters.AddWithValue("$since", query.Since.Value.ToUnixTimeSeconds());
        }

        if (query.Until is not null)
        {
            sql.Append(" AND m.occurred_utc <= $until");
            command.Parameters.AddWithValue("$until", query.Until.Value.ToUnixTimeSeconds());
        }

        if (query.ParticipantId is not null)
        {
            sql.Append(" AND m.sender_id = $sender");
            command.Parameters.AddWithValue("$sender", query.ParticipantId);
        }

        if (query.Type is not null)
        {
            sql.Append(" AND m.type = $type");
            command.Parameters.AddWithValue("$type", query.Type.Value.ToWireName());
        }

        if (query.After is not null)
        {
            sql.Append(
                """
                 AND (f.rank > $afterRank
                      OR (f.rank = $afterRank
                          AND (m.occurred_utc > $afterUtc
                               OR (m.occurred_utc = $afterUtc
                                   AND (COALESCE(m.source_order_key, '') > $afterOrder
                                        OR (COALESCE(m.source_order_key, '') = $afterOrder AND m.id > $afterId))))))
                """);
            command.Parameters.AddWithValue("$afterRank", query.After.Rank);
            command.Parameters.AddWithValue("$afterUtc", query.After.OccurredUtc);
            command.Parameters.AddWithValue("$afterOrder", query.After.SourceOrderKey);
            command.Parameters.AddWithValue("$afterId", query.After.MessageId);
        }

        sql.Append(" ORDER BY f.rank ASC, m.occurred_utc ASC, COALESCE(m.source_order_key, '') ASC, m.id ASC LIMIT $limit;");
        command.Parameters.AddWithValue("$limit", query.Limit + 1);
        command.CommandText = sql.ToString();

        var hits = new List<ArchiveSearchHit>(Math.Min(query.Limit, 128));
        var hasMore = false;
        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (hits.Count == query.Limit)
                {
                    hasMore = true;
                    break;
                }

                hits.Add(new ArchiveSearchHit
                {
                    Message = ReadMessage(reader),
                    Rank = reader.GetDouble(22),
                });
            }
        }
        catch (SqliteException ex)
        {
            // A corrupt index must not leak as a raw SQLite failure or as a partial result. The
            // reason carries only the error number — never query content or index content
            // (docs/SEARCH_SPEC.md sections 3 and 12).
            throw ArchiveQueryException.IndexUnavailable(
                $"the FTS search index could not serve the query (SQLite error {ex.SqliteErrorCode}); run the archive maintenance index verification for a diagnosis.");
        }

        return Task.FromResult(new ArchiveSearchPage { Hits = hits, HasMore = hasMore });
    }

    /// <summary>
    /// Archive-maintenance verification of the derived search index
    /// (docs/SEARCH_SPEC.md section 10.4): FTS5 integrity-check plus a messages-vs-index
    /// row-count agreement check. Diagnoses only — it never repairs.
    /// </summary>
    public Task<SearchIndexVerification> VerifySearchIndexAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();

        if (!ArchiveSearchIndex.TableExists(connection))
        {
            return Task.FromResult(new SearchIndexVerification
            {
                IsHealthy = false,
                Reason = "the FTS search index does not exist in this archive.",
            });
        }

        long messageCount;
        long indexCount;
        using (var counts = connection.CreateCommand())
        {
            counts.CommandText = $"SELECT (SELECT COUNT(*) FROM messages), (SELECT COUNT(*) FROM {ArchiveSearchIndex.TableName});";
            using var reader = counts.ExecuteReader();
            reader.Read();
            messageCount = reader.GetInt64(0);
            indexCount = reader.GetInt64(1);
        }

        if (messageCount != indexCount)
        {
            return Task.FromResult(new SearchIndexVerification
            {
                IsHealthy = false,
                Reason = $"the index row count does not agree with the canonical archive ({indexCount} index rows for {messageCount} messages).",
            });
        }

        try
        {
            using var check = connection.CreateCommand();
            check.CommandText =
                $"INSERT INTO {ArchiveSearchIndex.TableName}({ArchiveSearchIndex.TableName}) VALUES ('integrity-check');";
            check.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            return Task.FromResult(new SearchIndexVerification
            {
                IsHealthy = false,
                Reason = $"FTS5 integrity-check failed (SQLite error {ex.SqliteErrorCode}); the index is inconsistent with its stored content.",
            });
        }

        return Task.FromResult(new SearchIndexVerification { IsHealthy = true });
    }

    /// <summary>
    /// Deterministically rebuilds the derived search index from canonical rows only, in one
    /// transaction (docs/SEARCH_SPEC.md section 10.3). Canonical data is never modified.
    /// </summary>
    public Task RebuildSearchIndexAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        ArchiveSearchIndex.Rebuild(connection, transaction, cancellationToken);
        transaction.Commit();
        return Task.CompletedTask;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        ApplyMigrations(connection);
        return connection;
    }

    private static void ApplyMigrations(SqliteConnection connection)
    {
        using (var create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, description TEXT NOT NULL, applied_at TEXT NOT NULL);";
            create.ExecuteNonQuery();
        }

        var applied = new HashSet<int>();
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT version FROM schema_migrations;";
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                applied.Add(reader.GetInt32(0));
            }
        }

        foreach (var migration in ArchiveMigrations.All)
        {
            if (applied.Contains(migration.Version))
            {
                continue;
            }

            using var transaction = connection.BeginTransaction();
            foreach (var statement in migration.Statements)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }

            migration.Backfill?.Invoke(connection, transaction);

            using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText =
                    "INSERT INTO schema_migrations (version, description, applied_at) VALUES ($v, $d, $t);";
                record.Parameters.AddWithValue("$v", migration.Version);
                record.Parameters.AddWithValue("$d", migration.Description);
                record.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                record.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        // user_version mirrors the current schema version. It is written only when it differs:
        // opening a connection must stay a read-only operation on an up-to-date archive, because
        // the canonical search reads through regular connections while an import transaction may
        // hold the write lock (R0 read-only query, docs/ARCHITECTURE.md section 3.9).
        var userVersion = Convert.ToInt32(
            new SqliteCommand($"PRAGMA {SchemaVersionKey};", connection).ExecuteScalar(),
            CultureInfo.InvariantCulture);
        if (userVersion != ArchiveMigrations.CurrentVersion)
        {
            using var version = connection.CreateCommand();
            version.CommandText = $"PRAGMA {SchemaVersionKey} = {ArchiveMigrations.CurrentVersion};";
            version.ExecuteNonQuery();
        }
    }

    private static ArchiveConversation ReadConversation(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        AccountId = reader.GetString(1),
        SourceConversationId = reader.GetString(2),
        Kind = ConversationKinds.TryParse(reader.GetString(3), out var kind) ? kind : ConversationKind.Unknown,
        Title = reader.IsDBNull(4) ? null : reader.GetString(4),
        PeerParticipantId = reader.IsDBNull(5) ? null : reader.GetString(5),
        OwnerParticipantId = reader.IsDBNull(6) ? null : reader.GetString(6),
        FirstMessageAt = reader.IsDBNull(7) ? null : ParseTimestamp(reader.GetString(7)),
        LastMessageAt = reader.IsDBNull(8) ? null : ParseTimestamp(reader.GetString(8)),
        MessageCount = reader.GetInt32(9),
    };

    private static CanonicalMessage ReadMessage(SqliteDataReader reader)
    {
        CanonicalMessageTypes.TryParse(reader.GetString(4), out var type);

        ReplyReference? reply = null;
        var replySnapshot = reader.IsDBNull(8) ? null : reader.GetString(8);
        var replyTargetId = reader.IsDBNull(7) ? null : reader.GetString(7);
        if (replySnapshot is not null || replyTargetId is not null)
        {
            var dto = replySnapshot is null
                ? new ReplySnapshotDto()
                : JsonSerializer.Deserialize<ReplySnapshotDto>(replySnapshot) ?? new ReplySnapshotDto();
            reply = new ReplyReference
            {
                MessageId = replyTargetId,
                SourceMessageId = reader.IsDBNull(21) ? null : reader.GetString(21),
                SenderId = dto.SenderId,
                SenderName = dto.SenderName,
                Text = dto.Text,
                OccurredAt = dto.Time is null ? null : ParseTimestamp(dto.Time),
            };
        }

        return new CanonicalMessage
        {
            Id = reader.GetString(0),
            ConversationId = reader.GetString(1),
            SenderId = reader.IsDBNull(2) ? null : reader.GetString(2),
            OccurredAt = ParseTimestamp(reader.GetString(3)),
            Type = type,
            Text = reader.GetString(5),
            Payload = reader.IsDBNull(6) ? null : JsonNode.Parse(reader.GetString(6)) as JsonObject,
            ReplyTo = reply,
            Source = new SourceProvenance
            {
                SourceProfileId = reader.GetString(9),
                SourceConversationId = reader.GetString(10),
                SourceMessageId = reader.GetString(11),
                SourceType = reader.IsDBNull(12) ? null : reader.GetString(12),
                SourceSubtype = reader.IsDBNull(13) ? null : reader.GetString(13),
                SourcePartition = reader.IsDBNull(14) ? null : reader.GetString(14),
                SourceOrderKey = reader.IsDBNull(15) ? null : reader.GetString(15),
                AdapterName = reader.GetString(16),
                AdapterVersion = reader.IsDBNull(17) ? null : reader.GetString(17),
                SourceVersion = reader.IsDBNull(18) ? null : reader.GetString(18),
                ImportRunId = reader.IsDBNull(19) ? null : reader.GetString(19),
            },
            IsPartial = !reader.IsDBNull(20) && reader.GetInt32(20) != 0,
        };
    }

    /// <summary>
    /// Semantic content fingerprint. Only fields that change the meaning of an archived
    /// record participate, so metadata-only refreshes do not count as updates.
    /// </summary>
    private static string ContentHash(CanonicalMessage message)
    {
        var builder = new StringBuilder();
        builder.Append(message.Type.ToWireName()).Append('\u001f');
        builder.Append(message.SenderId ?? string.Empty).Append('\u001f');
        builder.Append(message.OccurredAt.ToUnixTimeSeconds()).Append('\u001f');
        builder.Append(message.Text).Append('\u001f');
        builder.Append(message.Payload?.ToJsonString() ?? string.Empty).Append('\u001f');
        // The referenced upstream id (not the resolved canonical id) participates: the
        // resolved id is produced by the archive itself, so including it would make a
        // re-import of unchanged source data look like an update.
        builder.Append(message.ReplyTo?.SourceMessageId ?? string.Empty).Append('\u001f');
        builder.Append(message.ReplyTo?.SenderId ?? string.Empty).Append('\u001f');
        builder.Append(message.ReplyTo?.Text ?? string.Empty).Append('\u001f');
        builder.Append(message.IsPartial ? '1' : '0');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static string SerializeDiagnostics(IReadOnlyList<ImportDiagnostic> diagnostics) =>
        JsonSerializer.Serialize(diagnostics.Select(d => new
        {
            severity = d.Severity.ToString(),
            code = d.Code,
            message = d.Message,
            count = d.Count,
            source_type = d.SourceType,
            source_subtype = d.SourceSubtype,
        }));

    /// <summary>
    /// Reads back the projection written by <see cref="SerializeDiagnostics"/>. A malformed or
    /// non-array payload cannot be interpreted and yields no diagnostics rather than inventing
    /// entries; the canonical records themselves remain the export's source of truth.
    /// </summary>
    private static IReadOnlyList<ImportDiagnostic> DeserializeDiagnostics(string json)
    {
        var diagnostics = new List<ImportDiagnostic>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return diagnostics;
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return diagnostics;
        }

        foreach (var item in document.RootElement.EnumerateArray())
        {
            diagnostics.Add(new ImportDiagnostic
            {
                Severity = ParseSeverity(ReadString(item, "severity")),
                Code = ReadString(item, "code") ?? string.Empty,
                Message = ReadString(item, "message") ?? string.Empty,
                Count = item.TryGetProperty("count", out var count) && count.ValueKind == JsonValueKind.Number
                    ? count.GetInt32()
                    : 1,
                SourceType = ReadString(item, "source_type"),
                SourceSubtype = ReadString(item, "source_subtype"),
            });
        }

        return diagnostics;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DiagnosticSeverity ParseSeverity(string? value) =>
        Enum.TryParse<DiagnosticSeverity>(value, ignoreCase: true, out var severity)
            ? severity
            : DiagnosticSeverity.Info;

    private static string Format(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static string? FormatOrNull(DateTimeOffset? value) =>
        value is null ? null : Format(value.Value);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record ReplySnapshotDto
    {
        [System.Text.Json.Serialization.JsonPropertyName("sender_id")]
        public string? SenderId { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("sender_name")]
        public string? SenderName { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("text")]
        public string? Text { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("time")]
        public string? Time { get; init; }
    }
}
