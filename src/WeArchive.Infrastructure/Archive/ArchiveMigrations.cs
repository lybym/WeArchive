namespace WeArchive.Infrastructure.Archive;

/// <summary>
/// Numbered, forward-only archive migrations.
/// docs/DATA_MODEL.md section 18: never mutate an archive without a migration record,
/// and every migration must be reflected in docs/DATA_MODEL.md.
/// </summary>
internal static class ArchiveMigrations
{
    public const int CurrentVersion = 1;

    public static IReadOnlyList<Migration> All { get; } =
    [
        new Migration(1, "initial canonical archive schema",
        [
            """
            CREATE TABLE accounts (
                id                TEXT PRIMARY KEY,
                source_profile_id TEXT NOT NULL,
                adapter_name      TEXT NOT NULL,
                adapter_version   TEXT,
                source_version    TEXT,
                display_name      TEXT,
                data_root_path    TEXT
            );
            """,
            "CREATE UNIQUE INDEX ix_accounts_source ON accounts(adapter_name, source_profile_id);",

            """
            CREATE TABLE participants (
                id                  TEXT PRIMARY KEY,
                account_id          TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                source_participant_id TEXT NOT NULL,
                latest_remark       TEXT,
                nickname            TEXT,
                alias               TEXT,
                user_display_name   TEXT
            );
            """,
            "CREATE UNIQUE INDEX ix_participants_source ON participants(account_id, source_participant_id);",

            """
            CREATE TABLE conversations (
                id                     TEXT PRIMARY KEY,
                account_id             TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                source_conversation_id TEXT NOT NULL,
                kind                   TEXT NOT NULL,
                title                  TEXT,
                peer_participant_id    TEXT,
                owner_participant_id   TEXT,
                first_message_at       TEXT,
                last_message_at        TEXT,
                message_count          INTEGER NOT NULL DEFAULT 0
            );
            """,
            "CREATE UNIQUE INDEX ix_conversations_source ON conversations(account_id, source_conversation_id);",

            """
            CREATE TABLE messages (
                id                    TEXT PRIMARY KEY,
                conversation_id       TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                sender_id             TEXT,
                occurred_at           TEXT NOT NULL,
                occurred_utc          INTEGER NOT NULL,
                type                  TEXT NOT NULL,
                semantic_text         TEXT NOT NULL,
                payload_json          TEXT,
                reply_to_message_id   TEXT,
                reply_source_message_id TEXT,
                reply_snapshot_json   TEXT,
                source_profile_id     TEXT NOT NULL,
                source_conversation_id TEXT NOT NULL,
                source_message_id     TEXT NOT NULL,
                source_type           TEXT,
                source_subtype        TEXT,
                source_partition      TEXT,
                source_order_key      TEXT,
                adapter_name          TEXT NOT NULL,
                adapter_version       TEXT,
                source_version        TEXT,
                import_run_id         TEXT,
                content_hash          TEXT NOT NULL,
                is_partial            INTEGER NOT NULL DEFAULT 0
            );
            """,
            "CREATE INDEX ix_messages_timeline ON messages(conversation_id, occurred_utc, source_order_key, id);",
            "CREATE INDEX ix_messages_source ON messages(conversation_id, source_message_id);",
            "CREATE INDEX ix_messages_type ON messages(conversation_id, type);",

            """
            CREATE TABLE import_runs (
                id                TEXT PRIMARY KEY,
                account_id        TEXT NOT NULL,
                adapter_name      TEXT NOT NULL,
                adapter_version   TEXT,
                source_version    TEXT,
                started_at        TEXT NOT NULL,
                finished_at       TEXT,
                status            TEXT NOT NULL,
                records_scanned   INTEGER NOT NULL DEFAULT 0,
                records_inserted  INTEGER NOT NULL DEFAULT 0,
                records_updated   INTEGER NOT NULL DEFAULT 0,
                records_skipped   INTEGER NOT NULL DEFAULT 0,
                unknown_count     INTEGER NOT NULL DEFAULT 0,
                partial_count     INTEGER NOT NULL DEFAULT 0,
                warning_count     INTEGER NOT NULL DEFAULT 0,
                error_count       INTEGER NOT NULL DEFAULT 0,
                diagnostics_json  TEXT
            );
            """,

            """
            CREATE TABLE source_checkpoints (
                id               TEXT PRIMARY KEY,
                account_id       TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                adapter_name     TEXT NOT NULL,
                adapter_version  TEXT,
                checkpoint_json  TEXT NOT NULL,
                updated_at       TEXT NOT NULL
            );
            """,
            "CREATE UNIQUE INDEX ix_checkpoints_source ON source_checkpoints(account_id, adapter_name);",
        ]),
    ];
}

internal sealed record Migration(int Version, string Description, IReadOnlyList<string> Statements);
