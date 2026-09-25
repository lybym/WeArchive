using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;

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
            }
            else if (!string.Equals(existing, hash, StringComparison.Ordinal))
            {
                updated++;
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
            """
            SELECT id, conversation_id, sender_id, occurred_at, type, semantic_text, payload_json,
                   reply_to_message_id, reply_snapshot_json, source_profile_id, source_conversation_id,
                   source_message_id, source_type, source_subtype, source_partition, source_order_key,
                   adapter_name, adapter_version, source_version, import_run_id, is_partial,
                   reply_source_message_id
            FROM messages WHERE conversation_id = $id
            ORDER BY occurred_utc, COALESCE(source_order_key, ''), id;
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
                   (SELECT COUNT(*) FROM messages);
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
            };
        }

        return Task.FromResult(stats);
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

        using (var version = connection.CreateCommand())
        {
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
