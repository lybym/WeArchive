# WeArchive Data Model

## 1. Purpose

The normalized data model decouples the long-lived personal archive from any single upstream client format.

Source adapters may change often. The archive schema should change only when product semantics change.

The canonical message semantics are defined by [MESSAGE_SCHEMA.md](MESSAGE_SCHEMA.md). Phase 1 export behavior is defined by [EXPORT_PRD.md](EXPORT_PRD.md).

## 2. Core principles

1. Stable IDs and display names are separate concepts.
2. Source provenance is first-class.
3. Import runs are auditable.
4. Re-import must be idempotent.
5. Message semantics are source-independent.
6. Binary media is not required for the Phase 1 export product.
7. Source-specific fields belong in provenance/metadata, not in core domain columns unless they have product meaning.
8. Unknown records are retained rather than silently discarded.
9. An import publishes atomically: one conversation's records become part of the archive in a single transaction, and a run that could not read the source completely leaves the archive unchanged instead of storing the records it happened to read.

## 3. Entity relationship overview

This is the shipped archive schema (SQLite, migrations 1–2). Column names and types are normative.

```mermaid
erDiagram
    ACCOUNT ||--o{ CONVERSATION : owns
    CONVERSATION ||--o{ MESSAGE : contains
    PARTICIPANT ||--o{ MESSAGE : sends
    MESSAGE ||--o| MESSAGE : replies_to
    ACCOUNT ||--o{ PARTICIPANT : contains
    ACCOUNT ||--o{ SOURCE_CHECKPOINT : tracks
    ACCOUNT ||--o{ INGEST_CHECKPOINT : tracks

    ACCOUNT {
        TEXT id PK
        TEXT source_profile_id
        TEXT adapter_name
        TEXT adapter_version
        TEXT source_version
        TEXT display_name
        TEXT data_root_path
    }

    PARTICIPANT {
        TEXT id PK
        TEXT account_id FK
        TEXT source_participant_id
        TEXT latest_remark
        TEXT nickname
        TEXT alias
        TEXT user_display_name
    }

    CONVERSATION {
        TEXT id PK
        TEXT account_id FK
        TEXT source_conversation_id
        TEXT kind
        TEXT title
        TEXT peer_participant_id
        TEXT owner_participant_id
        TEXT first_message_at
        TEXT last_message_at
        INTEGER message_count
    }

    MESSAGE {
        TEXT id PK
        TEXT conversation_id FK
        TEXT sender_id
        TEXT occurred_at
        INTEGER occurred_utc
        TEXT type
        TEXT semantic_text
        TEXT payload_json
        TEXT reply_to_message_id
        TEXT reply_source_message_id
        TEXT reply_snapshot_json
        TEXT source_profile_id
        TEXT source_conversation_id
        TEXT source_message_id
        TEXT source_type
        TEXT source_subtype
        TEXT source_partition
        TEXT source_order_key
        TEXT adapter_name
        TEXT adapter_version
        TEXT source_version
        TEXT import_run_id
        TEXT content_hash
        INTEGER is_partial
    }

    IMPORT_RUN {
        TEXT id PK
        TEXT account_id
        TEXT adapter_name
        TEXT adapter_version
        TEXT source_version
        TEXT started_at
        TEXT finished_at
        TEXT status
        INTEGER records_scanned
        INTEGER records_inserted
        INTEGER records_updated
        INTEGER records_skipped
        INTEGER unknown_count
        INTEGER partial_count
        INTEGER warning_count
        INTEGER error_count
        TEXT diagnostics_json
    }

    SOURCE_CHECKPOINT {
        TEXT id PK
        TEXT account_id FK
        TEXT adapter_name
        TEXT adapter_version
        TEXT checkpoint_json
        TEXT updated_at
    }

    INGEST_CHECKPOINT {
        TEXT id PK
        TEXT account_id FK
        TEXT adapter_family
        TEXT scope_kind
        TEXT scope_id
        TEXT checkpoint_json
        TEXT updated_at
    }
```

`ConversationParticipant` has no dedicated table in migration 1: a conversation's peer and
owner are referenced directly from `conversations`, and per-participant metadata lives on
`participants`. A full membership relation remains a future schema change.

Indexes:

```text
accounts            unique (adapter_name, source_profile_id)
participants        unique (account_id, source_participant_id)
conversations       unique (account_id, source_conversation_id)
messages            (conversation_id, occurred_utc, source_order_key, id)   -- canonical timeline order
messages            (conversation_id, source_message_id)
messages            (conversation_id, type)
source_checkpoints  unique (account_id, adapter_name)
ingest_checkpoints  unique (account_id, adapter_family, scope_kind, scope_id)
```

## 4. Account

Represents one logical source profile imported into an archive.

Required concepts:

- internal archive `id`;
- stable `source_profile_id` from adapter;
- adapter identity;
- source/client version metadata;
- optional display label.

An Account is not an authentication credential record.

## 5. Conversation

Represents a private chat, group chat or other supported logical conversation.

Fields:

- stable archive ID;
- account ID;
- stable source conversation ID;
- `kind`: `direct`, `group`, `official`, `system`, `unknown`;
- current title/display name;
- `peer_participant_id` for a direct conversation and `owner_participant_id` where known;
- `message_count`;
- optional user-maintained alias in export configuration;
- optional first/last known timestamps.

Conversation titles are mutable and must never be used as identity keys or physical export paths.

## 6. Participant

Represents a stable logical sender/contact identity when available.

Fields:

- archive ID;
- account ID;
- stable source participant ID;
- latest available remark;
- latest nickname;
- `alias`;
- `user_display_name`.

Phase 1 export identity rules:

```text
latest remark exists -> default display_name = latest remark
no latest remark      -> default display_name = ""
```

Nickname is retained as metadata but does not automatically fill a missing remark. The
user-maintained `display_name_override` in the exported `identities.yaml` is a separate,
export-level hook and wins over the generated default; it is not an archive column.

Display names can change and collide. Stable participant IDs must remain the canonical identity.

## 7. ConversationParticipant

Many-to-many relation between conversations and participants.

Migration 1 does not create a `conversation_participants` table. Today the conversation-to-participant relation is expressed by `conversations.peer_participant_id` and `conversations.owner_participant_id`, and participant metadata is stored on `participants`. A future migration may add a real membership table carrying:

- group nickname;
- role;
- join/leave timing where available.

Group-specific names never replace the canonical participant ID.

## 8. Message

`Message` is the canonical archived semantic event.

Required semantics:

- archive message ID;
- conversation ID;
- sender ID when resolvable;
- canonical timestamp;
- normalized semantic `type`;
- LLM/search-oriented semantic `text`;
- optional type-specific structured `payload`;
- optional reply/reference relationship;
- source provenance;
- import run;
- a semantic `content_hash` for idempotent upserts.

### 8.1 Timestamps and ordering

A message stores its time twice:

- `occurred_at` — ISO-8601 text **with the source's timezone offset** (`yyyy-MM-dd'T'HH:mm:sszzz`), which is what the export publishes and what a human or LLM reads;
- `occurred_utc` — the same instant as epoch seconds (`INTEGER`), which is what the archive orders by and range-queries on.

Storing both avoids re-parsing text on every range query while keeping the exported timestamp
unambiguous. Canonical timeline order is `(occurred_utc, source_order_key, id)`, which is the
index `ix_messages_timeline`. Conversation first/last aggregates and stats are computed from
`occurred_utc` (the epoch) and retain the corresponding rendered `occurred_at`; they never
take `MIN`/`MAX` over the offset-bearing text, which would mis-order records across different
timezone offsets.

### 8.2 Idempotent upsert and `content_hash`

`content_hash` is a SHA-256 over the **semantic** fields only: canonical type, sender, epoch
time, semantic text, payload JSON, the reply's upstream source id, the reply's sender and
text, and the partial flag.

Fields that only carry metadata — for example the refreshed adapter version, source version
or import run id — are deliberately excluded. A re-import of unchanged source data is
therefore recognised as unchanged and does not count as an update, which is what makes
repeated imports idempotent in practice rather than only in primary-key terms.

### 8.3 Common semantic model

The conceptual structure is:

```text
Message
├─ common envelope
│  ├─ id
│  ├─ conversation_id
│  ├─ sender_id
│  ├─ time
│  └─ type
├─ semantic_text
├─ payload
├─ reply_to
└─ source provenance
```

The archive stores the type-specific structure in the `payload_json` column while exports use the canonical shape defined in `MESSAGE_SCHEMA.md`.

### 8.4 Canonical message types

Phase 1 normalized types:

- `text`
- `image`
- `voice`
- `video`
- `file`
- `link`
- `app_share`
- `mini_program`
- `forward_bundle`
- `location`
- `contact_card`
- `system`
- `revoke`
- `red_packet`
- `transfer`
- `emoji`
- `unknown`

`quote` is not a standalone content type. A quoted/replied message uses the underlying content type plus a `reply_to` relationship.

Unknown source records must map to `unknown` with diagnostics and source type metadata; they must not be silently discarded.

### 8.5 Semantic text

Every message should have a compact semantic text representation useful to LLMs and full-text search.

Examples:

```text
text      -> 下午三点开会。
image     -> [图片]
voice     -> [语音]
video     -> [视频]
file      -> [文件] 华东中心项目汇报V8.pptx
link      -> [链接] 标题\nhttps://...
app_share -> [APP分享][来源] 标题\nhttps://...
unknown   -> [未识别消息]
```

Semantic text must not contain unnecessary upstream XML or implementation details.

### 8.6 Structured payload

`payload` contains product-meaningful type-specific semantics, for example:

- file name / extension / size;
- link title / description / original URL;
- app source / app ID / page path;
- voice duration;
- location fields;
- transfer fields;
- forwarded-chat nested items.

Missing fields must not be guessed.

### 8.7 Reply relationship

Replies/quotes are represented structurally. Three columns work together:

- `reply_source_message_id` — the **upstream** id of the referenced record, addressed with the
  composite strategy of section 16.2. It is stored exactly as the adapter supplied it and is
  what makes resolution possible, but it is provenance and is never exported.
- `reply_to_message_id` — the resolved canonical `m_...` id, filled in by the archive when the
  referenced record is present in the same archive (resolved on insert and by a backfill pass
  for records imported later). It is null while the target does not resolve. The archive never
  fabricates a target id. When a re-import changes `reply_source_message_id` (the upstream
  message was edited to quote a different record), the previously-resolved
  `reply_to_message_id` is cleared so the backfill pass re-resolves it against the new target;
  a target that no longer resolves is left null rather than retaining the stale id.
- `reply_snapshot_json` — the locally available quoted sender/text metadata, kept even when no
  canonical target resolves.

This allows reply graph analysis both when the target is archived and when the source only
stored a quote snapshot, or when the target is missing from the local source entirely.

## 9. Non-text events and binary policy

The archive may retain local source metadata needed for diagnostics, but Phase 1 export does not require copying image/audio/video/file binaries.

Product semantics retained include:

- image: event existence;
- video: event existence;
- voice: event existence and duration where reliably available;
- file: original filename and optional metadata;
- emoji/sticker: event existence.

No OCR/ASR/visual derived layer is part of Phase 1.

## 10. Link and app-share semantics

Links and third-party shared content are first-class message semantics.

The normalized payload should preserve, where locally available:

- title;
- description;
- source application;
- original URL;
- wrapper/fallback URL;
- app ID;
- page path.

The system should prefer a confirmed underlying/original URL over a wrapper/tracking URL when that distinction can be determined from local source metadata.

Phase 1 canonical export does not require remote crawling of target webpages.

## 11. Forwarded bundle semantics

Merged/forwarded chat records may contain nested textual events.

The archive should preserve:

- bundle title;
- item count;
- nested sender name;
- nested sender ID only when reliably resolvable;
- nested timestamp;
- nested normalized type;
- nested semantic text.

An unresolvable nested sender must not be assigned a fabricated canonical identity.

## 12. Unknown records

Unknown records are first-class retained events.

Minimum useful information:

- `type = unknown`;
- semantic text `[未识别消息]`;
- upstream type/subtype where available;
- optional compact raw summary;
- normal source provenance.

Diagnostics and manifests must count unknown/partial records.

## 13. ImportRun

Every sync/import invocation creates one ImportRun.

Required fields:

- run ID;
- start/end time;
- account ID;
- adapter name/version;
- source version;
- status;
- records scanned;
- records inserted;
- records updated;
- records skipped;
- warnings count;
- errors count;
- unknown message count;
- partial count;
- structured diagnostic summary (`diagnostics_json`).

This entity is required for operational trust.

`records_scanned` counts the records the run read; the inserted/updated/skipped counters describe
what the run published. A run that was rolled back because source coverage failed publishes
nothing, so it reports zero inserted/updated/skipped while `records_scanned` still shows how far
it read and its Fatal diagnostic says why. Section 2 principle 9, docs/ARCHITECTURE.md section 3.2.1.

Every conversation a run publishes is committed in one transaction together with its messages, so
the conversation row and its aggregates never describe records the archive does not hold.

## 14. SourceCheckpoint

Stores adapter-owned incremental state.

Schema fields:

- `id`;
- `account_id`;
- `adapter_name` / `adapter_version`;
- `checkpoint_json` — the opaque, versioned payload;
- `updated_at`.

Rules:

- payload is versioned;
- payload is opaque to the generic importer;
- checkpoint update occurs only after durable archive commit;
- adapter version changes may invalidate prior checkpoints;
- uniqueness is `(account_id, adapter_name)`.

**Status:** the legacy table exists in migration 1 and remains unchanged. Raw Vault ingestion does not reinterpret it; it uses the separate `IngestCheckpoint` table introduced by migration 2.

### 14.1 IngestCheckpoint

Migration 2 records Raw Vault to canonical progress independently for each account, adapter family and scope. Conversation content rows use `scope_kind=conversation`; `scope_id` is the stable canonical conversation ID. Their opaque, versioned cursor contains the last generation whose conversation evidence changed and was published, a fingerprint of that conversation's source metadata and messages, and the executing reader version. It contains no source database key or message content. A reader upgrade invalidates older cursors; `wearchive ingest --replay` also allows parser repair to replay preserved generations without live recapture.

Scoped scans also use `scope_kind=conversation_coverage` with the same stable conversation ID. This cursor records newer generations that were fully verified but contained unchanged conversation evidence. It is committed in a conversation transaction without a second message import pass or a new import-run audit row. Keeping coverage separate allows repeat scoped imports to skip verified generations while the content cursor advances only when evidence changes. A reader upgrade invalidates older cursors.

Account scope rows use `scope_kind=account` and the stable account ID as `scope_id`. This cursor records the set of generation identities fully scanned by an account-wide ingest, after every conversation in each generation was either published or found unchanged. A scoped `--conversation` ingest never advances it. This lets a later account-wide run discover conversations not yet imported and lets completed generations be skipped even when one unchanged conversation's publication checkpoint remains at an older generation. Generations are processed in manifest publication-lineage order, not inferred from capture timestamps.

Conversation content and coverage checkpoints are upserted inside SQLite transactions with the corresponding canonical conversation state. Fatal source/identity/coverage errors and caught cancellation roll back an in-flight conversation publication. The account scan cursor is written only after the generation scan completes; replay invalidates prior account coverage and ignores per-conversation coverage so preserved generations can be re-read. Interruption before a coverage or generation-scan cursor commits causes a safe retry. Replaying a generation remains idempotent by canonical stable message ID and content hash. Captured participant metadata is refreshed from each opened generation even when conversation message evidence is unchanged. Migration 2 does not backfill from `source_checkpoints`.

## 15. SourceArtifact / provenance

Minimum provenance fields for Message where available:

```text
source_profile_id
source_conversation_id
source_message_id
source_type
source_subtype
source_partition
source_order_key
adapter_name
adapter_version
source_version
import_run_id
```

All of these exist as columns on `messages`. Provenance is not the primary downstream analysis interface but must support reprocessing and diagnostics.

## 16. Identity strategy

Archive IDs are deterministic. Every stable ID is a namespace prefix plus the first **16 hex
characters (64 bits)** of `SHA-256` over the length-prefixed, concatenated namespace parts.

```text
a_<16 hex>   account      SHA-256("account", adapter_name, source_profile_id)
u_<16 hex>   participant  SHA-256("user", account_id, source_user_id)
g_<16 hex>   group        SHA-256("group", account_id, source_room_id)
m_<16 hex>   message      SHA-256("message", conversation_id, source_message_id)
```

A direct conversation's stable ID **is** its peer's `u_...` ID — the same peer in the same
account always yields the same ID whether it is addressed as a conversation or as a person.

Mutable human names (remarks, nicknames, group titles) are never inputs, which is what makes
physical export paths survive renames.

### 16.1 Why 16 hex characters, not 8

Earlier illustrative examples in these documents showed 8-character prefixes. The
implementation uses 16 because a 32-bit prefix collides measurably at archive scale: the
birthday bound puts a 50% collision probability at roughly 77,000 ids, which a single large
message archive exceeds. 64 bits moves that bound far beyond any personal archive while
keeping IDs short enough to read and to use as folder names.

### 16.2 Composite upstream message identity

When upstream has no stable message id, the adapter supplies a documented composite instead of
a content hash, because repeated identical messages are valid data:

```text
server id present -> s:<server_id>
otherwise         -> l:<partition>:<local_id>
```

`partition` identifies the upstream shard the record came from, so the composite stays unique
across a multi-shard timeline.

A record for which the adapter can provide neither form of identity is a source-coverage
failure. The importer records a Fatal diagnostic and the workflow does not export a reduced
dataset; it never skips that record or invents an identity, and the run publishes nothing to the
archive (section 2 principle 9).

## 17. Deduplication

Deduplication prefers stable source identity over content heuristics.

The archive's idempotency mechanism is the primary key together with `content_hash`: an upsert
whose semantic hash is unchanged is a no-op, while a real semantic change is recorded as an
update. Fallback content-based deduplication is not used, because repeated identical messages
are valid data.

## 18. Schema evolution

- Use numbered database migrations.
- Every applied migration is recorded in `schema_migrations (version, description, applied_at)`.
- The current schema version is also mirrored into SQLite's `user_version` pragma.
- Migrations are forward-only and idempotent: a re-open of an up-to-date archive applies nothing.
- Never mutate production archives without migration records.
- Backward-incompatible archive changes require a documented migration path.
- Export and message schemas are independently versioned in `manifest.json`.

Migration 1 creates the initial canonical schema. Migration 2 adds scoped `ingest_checkpoints`; it leaves migration-1 `source_checkpoints` and all existing archive rows untouched.

## 19. Search indexing

Full-text search is a derived index, not the canonical store.

The FTS index should be rebuildable from normalized `semantic_text` and selected structured payload fields such as filenames, link titles and descriptions.

No FTS index exists in migration 1; search is M3 work.

## 20. Raw source retention

Raw source records are optional and should not be required for normal archive/export workflows.

If retained for debugging/reproducibility:

- store separately from canonical semantic columns;
- mark source format/version;
- allow disabling retention;
- avoid placing large opaque blobs in the core Message table.

## 21. Raw Vault data model

The Raw Vault is a separate persistence layer from the canonical SQLite archive. It has its own
independently versioned format (`manifest_version` currently `2`, `vault_format_version` `1`),
and no canonical SQLite migration is required to introduce it (Issue #22, migration-1
`source_checkpoints` remains untouched).

Conceptual entities:

```text
RawVaultAccount  — one captured source profile (identified by the same stable account id
                   used by the canonical archive, a_<16-hex>)
RawGeneration    — one published snapshot; logically immutable; identified by gen_<16-hex>
RawArtifact      — one content object (e.g. a decrypted source database image)
CaptureRun       — provenance embedded in the manifest: adapter family/version, capture
                   time, completeness, diagnostics
```

### 21.1 Generation identity

`gen_<16-hex>` is `SHA-256("generation", account_id, capture_time_utc_iso8601,
capture_adapter_family, capture_adapter_version)` truncated to 16 hex characters (64 bits),
matching the canonical archive's identity-derivation rules (section 16). Two captures of the
same account at different times yield different generation ids.

### 21.2 Physical layout

```text
<vault-root>/accounts/<account-id>/generations/<generation-id>/
  manifest.json
  artifacts/<sha256><ext>
```

### 21.3 Manifest

The manifest is a versioned JSON document recording source product/version, source profile,
capture adapter family/version, capture time, mode (`baseline` or `incremental`), completeness,
every artifact's role/name/content-ref/SHA-256/size, per-partition coverage, the optional capture
checkpoint, diagnostics and the previous generation id (append-only chain). See
[RAW_VAULT.md](RAW_VAULT.md) for the full manifest shape.

The manifest format version is independent of the canonical SQLite schema version
(section 18), the message-schema version and the export-schema version.

### 21.4 Artifact roles and checksums

Artifact `role` values are source-neutral strings (e.g. `source-database`) so the Raw Vault does
not leak WeChat table names into Core/CLI. Every artifact has a verifiable SHA-256 checksum;
opening a generation re-verifies every checksum and rejects tampered or corrupted artifacts.

### 21.5 Capture checkpoint and partition coverage

Manifest version 2 adds two persistent structures to the generation manifest. Both live inside
the publish-last manifest, so neither can advance separately from a published generation:

```text
coverage[]           — one entry per source partition this generation accounts for:
                       partition_id, status (captured | reused | unavailable | unsupported),
                       source_fingerprint, artifact_sha256, diagnostic
capture_checkpoint   — version, generation_id, capture_adapter_family,
                       capture_adapter_version, partition_fingerprints{partition_id -> fingerprint}
```

The checkpoint is the Raw Vault-side capture cursor. It is written only for a `complete`
generation whose every coverage entry is `captured` or `reused` with a fingerprint and a
checksum that resolves to an artifact of the same generation; a `partial` generation records
coverage but no checkpoint, and a discarded generation publishes nothing at all. It is a record
distinct from SQLite `ingest_checkpoints`: capture freshness and canonical ingest freshness can
legitimately differ, and neither is ever inferred from the other. A version-1 manifest has no
checkpoint, so the next live capture widens to a full snapshot. No canonical SQLite migration is
involved.

`unavailable` is a report, never a deletion instruction: a partition that disappears from the
live source cannot remove an earlier generation or any artifact it requires.

### 21.6 Raw Vault format-version tests

Raw Vault format-version tests must verify that `manifest_version = 1` can be reopened
independently of the capture process — i.e. a new `RawVaultStore` instance pointing at the same
root can discover and validate a published generation without the capture adapter being alive.
They must also verify that a version-2 generation round-trips its coverage and checkpoint, and
that a coverage/checkpoint pair which disagrees (fingerprint, partition set, adapter identity or
a checksum that names no artifact of the generation) is rejected rather than trusted.

### 21.7 Rebuild and canonical migration

The Raw Vault format introduces no canonical SQLite migration. Rebuild creates a new canonical
database by applying the normal forward migration sequence to an empty file; it does not copy
an older canonical database. Stable account, participant, conversation and message identities
continue to use section 16 derivation. Existing `user_display_name` overrides are carried into
the rebuilt database when a matching canonical participant remains present. The Raw Vault
format version is tracked in each manifest, not in `schema_migrations`.

## 22. Collection configuration (not canonical SQLite)

`Collection` is the shared reusable scope for a named set of conversations (`docs/PRD.md` FR-23).
Its membership keys are stable conversation IDs (section 16): `g_<16 hex>` for group conversations
and `u_<16 hex>` for every other kind, which is exactly the derivation
`StableIds.Conversation` performs.

Collection definitions are **user-maintained durable configuration, not canonical archive data**.
They are therefore deliberately absent from the SQLite schema above and require no migration:

```text
%LOCALAPPDATA%\WeArchive\collections.yaml
  schema_version: "1.0"
  collections:
    <name>:
      conversations: [ <stable conversation id>, ... ]
```

Ownership, rebuild-survival semantics and stable-ID membership rules are recorded in
[ADR 0009](adr/0009-collection-configuration-ownership.md). Summary of the data-model consequences:

- the file reuses the documented `collections.yaml` semantic shape and is versioned by its own
  top-level `schema_version`, independent of the SQLite schema version (section 18), the message
  schema version and the export schema version;
- canonical SQLite is rebuildable from the Raw Vault, so it is intentionally **not** the only copy of
  a user-maintained Collection definition; rebuild needs no Collection-preservation rule and does not
  carry Collections into a rebuilt archive;
- the `collections.yaml` inside a generated export package remains derived output under
  `EXPORT_PRD.md` section 8 and is never authoritative for sync/query/export scope resolution;
- an absent file is an empty catalog; invalid configuration is diagnosed rather than silently
  rewritten; invalid or duplicated membership entries are reported per Collection and the resolved
  membership is the valid, first-seen, de-duplicated set;
- no `sync-group`, `watch-list` or `harness-dataset` table, file or model is introduced, because a
  second concept for the same conversation scope would duplicate this one.

Moving Collection definitions into canonical SQLite would be a persistent schema change and would
require a documented migration, a migration test, and an explicit rebuild-preservation rule that does
not exist today (section 18, `docs/PRD.md` FR-11).

## 23. Query model (M3a)

`ArchiveQueryService` (`src/WeArchive.Core/Services`) is the single source-independent retrieval
boundary over the canonical archive; CLI `--json` and a future MCP transport are adapters over it
(`docs/HARNESS.md` sections 2–5, `docs/ARCHITECTURE.md` section 3.9).

This slice adds **no** canonical schema change: bounded retrieval orders and filters with the
existing migration-1 timeline index `(conversation_id, occurred_utc, source_order_key, id)`, and
section 19 still holds — there is no FTS table, virtual table or second search engine.

### 23.1 Bounded retrieval

Canonical timeline order remains `occurred_utc`, then `COALESCE(source_order_key, '')`, then the
stable message id (section 8.1). A bounded page reads one record beyond its page size, so
`has_more` is a fact about the archive rather than an inference from a full page.

Filters map onto canonical columns only:

```text
since / until   -> occurred_utc range, both bounds inclusive
participant     -> messages.sender_id, a stable u_<16 hex> participant id
type            -> messages.type, a canonical wire name (section 8.4)
```

A date-only or offset-less bound denotes that instant in the machine's local offset — the same
offset the archive renders canonical timestamps with (section 8.1) — so a range never depends on the
timezone of whichever process parses it.

### 23.2 Pagination cursors

Cursors are **API tokens, not persistent archive records**: nothing is written to SQLite, no
migration is involved, and no recovery semantics apply. A cursor carries the exclusive keyset
position of the last returned record (epoch instant, normalized order key, stable message id) plus a
fingerprint of the normalized filter set (conversation, date range, participant, type). Page size is
deliberately excluded so a caller may change `--limit` between pages of the same listing.

A cursor is opaque to callers, and an empty, malformed, unsupported-version or filter-mismatched
cursor is a deterministic validation failure (`docs/CLI.md`), never a silently different page.
Because resumption is keyset-based rather than offset-based, records that share an instant are
neither repeated nor skipped.

### 23.3 Freshness

Freshness is reported from three independent sources and is never inferred across them
(`docs/HARNESS.md` section 10):

- **capture** — the latest published generation per known account, read through the Core
  `IRawVaultStore` contract and projected without partition fingerprints, artifact checksums or
  artifact contents (section 21.3);
- **ingest** — the most recently committed conversation-scope canonical publication and the last
  completed account-wide generation scan, projected from `ingest_checkpoints` (section 14.1)
  without exposing the cursor payload, its encoding or its scope vocabulary;
- **canonical** — archive counts plus the newest archived message instant, which is what says how
  current the queryable state is.

A null generation means "no valid published generation is discoverable for this account"; it never
means a generation matched. An account captured but not yet ingested is reported with capture
progress and null ingest progress, which is the distinction section 21.5 requires.

### 23.4 Read-only guarantee

Query is **R0**: it adds no table, index, checkpoint, journal, transaction protocol or recovery
state, and no query failure — validation, not-found or unreadable-archive — mutates canonical data,
Raw Vault evidence, the capture checkpoint or an ingest checkpoint.
