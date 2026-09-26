# WeArchive CLI contract

This document records the additive command, JSON and exit-code contract for the M0.5 CLI
product-surface migration. It covers the read-only discovery commands (Issue #7), the
`sync`/`export` commands (Issue #8), the Raw Vault capture/ingest/rebuild family (Issues #22–#25),
the Collection scope (Issue #26) and the query family (Issue #27). It refines the high-level CLI
contract in [`PRD.md`](PRD.md) (FR-22), [`ARCHITECTURE.md`](ARCHITECTURE.md) section 3.1.1 and
[ADR 0006](adr/0006-cli-first-product-surface.md). The CLI is the primary product surface;
this file is normative for the command shapes and machine-readable output described here.

Field names in every JSON result are pinned by `[JsonPropertyName]` attributes in
`src/WeArchive.Cli/Output/Dto/`, so a C# property rename cannot silently change the wire
contract.

## Entry point

During the CLI migration the entry-point project is `src/WeArchive.Cli` (assembly
`WeArchive.Cli`). The historical WPF project (`src/WeArchive.App`, assembly `WeArchive`) still
exists, so the CLI is built as a distinct assembly to avoid a name collision while both surfaces
coexist. Once the WPF presentation layer is retired, this project becomes the shipped
`wearchive` / `WeArchive.exe` (see ADR 0006 transition rule). The composition root
(`Program.cs`) reuses `AddWeArchiveCore` + `AddWeChatWindowsSource` — there is no second
composition model.

## Global options

Global options are recognized before and after the command name by `CommandLineParser`:

```text
--json          Exactly one JSON document on stdout (machine contract). No progress, ANSI
                decoration, prompts or localized prose on stdout. Also suppresses progress on
                stderr so machine output is never interleaved with it.
--quiet         Suppress non-essential progress on stderr. Errors are never suppressed.
--no-input      Never prompt. Missing required input is a deterministic failure, not a prompt.
-v, --version   Print the product version and exit.
-h, --help      Show usage and exit.
```

`--account <id>` is **command-specific** to the conversation commands (see below), not a global
option, so global option parsing stays generic.

## Exit codes

```text
0     The requested operation completed under its documented semantics.
1     Operation/runtime failure (including source-unavailable and listing/describe failures
      for the list/show commands). In --json mode stdout still carries one error document.
2     Usage / configuration validation failure.
130   Cancellation / user interrupt (cooperative; observed when a command honors the
      cancellation token).
```

## stdout / stderr separation

- **stdout**: the result — human text, or exactly one JSON document in `--json`. In `--json`
  mode a non-zero exit still writes exactly one JSON error document on stdout, never prose and
  never an empty stream.
- **stderr**: progress and human diagnostics (e.g. `Listing accounts…` in human mode),
  suppressed by `--json` and `--quiet`. Errors in human mode are written to stderr as
  `error: <message>` and are never suppressed.

Machine-readable stdout never contains progress, prompts or ANSI decoration (FR-22).

## Commands (discovery family — Issue #7 / M0.5)

These commands are read-only (R0): they never mutate WeChat source data, never persist
recovery state, and never prompt regardless of `--no-input`. They are thin adapters over
`SourceCatalogService` and contain no source-format logic.

### `wearchive doctor`

Reports platform/source/archive readiness. An unavailable source or archive is a valid
diagnostic **result**, not a failure: `doctor` exits `0` even when the source is unavailable,
because reporting unavailability is its purpose (FR-02). Provided by PR #13.

JSON shape (exit 0):

```json
{
  "ready": true,
  "source": {
    "available": true,
    "adapter": "wechat-windows",
    "adapter_version": "0.1.0",
    "source_version": "4.1.13.12",
    "source_product": "WeChat for Windows",
    "unavailable_reason": null
  },
  "archive": {
    "available": true,
    "path": "C:\\...\\wearchive.db",
    "account_count": 0,
    "conversation_count": 0,
    "participant_count": 0,
    "message_count": 0,
    "unavailable_reason": null
  }
}
```

### `wearchive account list`

Enumerates locally available source profiles (logged-in accounts) with their stable account
identifiers. Exits `0` when the source is available (an empty list is a valid result). Exits `1`
with `error.code = "source_unavailable"` when the source cannot be reached.

JSON shape: a JSON array of:

```json
{
  "source_profile_id": "wxid_...",
  "stable_id": "a_<16-hex>",
  "display_name": null,
  "is_current": true,
  "last_active_at": "2026-01-15T09:00:00+08:00",
  "data_root_path": null
}
```

`stable_id` is `StableIds.Account(adapter_name, source_profile_id)` ([DATA_MODEL.md](DATA_MODEL.md)
section 16), identical before and after import. An empty list serializes as `[]`.

### `wearchive conversation list [--account <id>]`

Enumerates conversations for a source profile with stable conversation identifiers and
source-neutral metadata.

**Account resolution** (never prompts, safe under `--no-input`): `--account <id>` selects
explicitly by the account's stable id (`a_...`) **or** its source profile id; otherwise the
current account (`is_current`) is used, falling back to the first account.

Exits `1` with `source_unavailable`, `no_accounts`, `account_not_found` or
`conversation_list_failed` on the corresponding failure.

JSON shape: a JSON array of:

```json
{
  "stable_id": "g_<16-hex>",
  "source_id": "100200300@chatroom",
  "kind": "group",
  "title": "华东产品创新中心工作群",
  "peer_source_user_id": null,
  "last_message_at": "2026-02-24T09:00:00+08:00",
  "message_count_hint": null
}
```

`kind` is a stable wire name (`direct`, `group`, `official`, `system`, `unknown`), never a raw
numeric source type code. `stable_id` mirrors `StableIds.Conversation` exactly (the same
derivation `ImportService` uses), so a caller may refer to a conversation by the same identifier
before and after import: group conversations are `g_<16-hex>`; all other kinds are the peer's
`u_<16-hex>` identity, where the peer is `peer_source_user_id` falling back to `source_id` when
no explicit peer is known.

### `wearchive conversation show <id-or-alias> [--account <id>]`

Resolves a conversation and returns its source-neutral metadata. The supported identifiers are:

- the stable source identifier (`source_id`); and
- the stable archive id (`stable_id`, `g_...` / `u_...`).

No second alias store is introduced: `stable_id` is the documented deterministic derivation
([DATA_MODEL.md](DATA_MODEL.md) section 16). Resolution by the export-catalog `alias`
([EXPORT_PRD.md](EXPORT_PRD.md)) is a forward refinement and is not implemented here.

Because the stable archive id is derived (not stored), `show` resolves by listing the profile's
conversations and matching by `stable_id` or `source_id`, then describes the matched conversation.
Account resolution is identical to `conversation list`.

Exits `1` with `conversation_not_found`, `source_unavailable`, `conversation_list_failed` or
`conversation_describe_failed` on the corresponding failure; `2` if no identifier is supplied.

JSON shape (exit 0):

```json
{
  "stable_id": "g_<16-hex>",
  "source_id": "100200300@chatroom",
  "kind": "group",
  "title": "华东产品创新中心工作群",
  "peer_source_user_id": null,
  "message_count": 24,
  "first_message_at": "2026-01-15T09:00:00+08:00",
  "last_message_at": "2026-02-24T09:00:00+08:00",
  "participant_count": 3
}
```

## Commands (sync/export family — Issue #8 / M0.5)

These commands publish to the archive or to a derived dataset. They are thin transport
adapters over `ImportService` (`sync`) and `ArchiveWorkflow` (`export`); they contain no
source-format, normalization or transaction logic. Both reuse the documented R2 import and
R1 export reliability levels — no R3+ crash-recovery machinery is introduced (see
[DEVELOPMENT.md](DEVELOPMENT.md) “Reliability Levels”).

### `wearchive sync --conversation <id-or-alias>`

Imports one conversation from the local source into the SQLite archive (FR-04/FR-08/FR-09,
FR-14). Resolution is identical to `conversation show`: the `<id-or-alias>` matches the
canonical stable archive id (`g_…`/`u_…`) **or** the upstream `source_id`; the current source
account is auto-selected (no prompt, safe under `--no-input`). `StableIds.Conversation` is
the single derivation shared with `ImportService`, so a caller may refer to the same id before
and after import.

Options:

```text
--conversation <id-or-alias>   Required. Stable archive id (g_/u_) or upstream source id.
```

The same command also accepts `--collection <name>`, which synchronizes every conversation a named
Collection scopes; the two selectors are mutually exclusive (see “Collection scope” below).

Reliability — **R2** (Import): a normal success commits under the existing conversation
transaction. A Fatal source-coverage failure rolls back the entire conversation transaction,
so no partial conversation is published; the CLI surfaces it as exit 1. A cooperative
cancellation keeps the already-read records (they are committed by stable id and a later full
re-read completes the conversation without duplicates) and exits 130. SQLite is the system of
record.

Exits `1` with `failure` (Fatal import did not complete), `source_unavailable`,
`no_accounts`, `conversation_list_failed`, `conversation_not_found` or
`conversation_describe_failed` on the corresponding failure; `2` on a usage error; `130` on
cancellation.

JSON shape (exit 0):

```json
{
  "conversation_id": "g_<16-hex>",
  "account_id": "a_<16-hex>",
  "source_profile_id": "wxid_...",
  "source_conversation_id": "100200300@chatroom",
  "records_scanned": 24,
  "counters": {
    "inserted": 18,
    "updated": 0,
    "unchanged": 0,
    "unknown": 1,
    "partial": 0
  },
  "first_message_at": "2026-01-15T09:00:00+08:00",
  "last_message_at": "2026-02-04T09:00:00+08:00",
  "diagnostics": [
    {
      "severity": "warning",
      "code": "unknown_message_type",
      "message": "...",
      "count": 1,
      "source_type": "1000007",
      "source_subtype": null
    }
  ]
}
```

`conversation_id` mirrors `StableIds.Conversation` exactly. `counters` describes committed
state only (a rolled-back Fatal run produces an error document, not this result). A repeated
sync is idempotent by stable id (`unchanged` grows on the second run).

### `wearchive export --conversation <id-or-alias> [--output <dir>]`

Publishes one conversation's JSONL dataset (FR-12, [EXPORT_PRD.md](EXPORT_PRD.md)). Resolution
is identical to `sync`. The command delegates the whole source → archive → dataset operation
to `ArchiveWorkflow`, which re-imports idempotently (by stable id) and then exports from the
SQLite archive, so the manifest carries ingested diagnostics.

Options:

```text
--conversation <id-or-alias>   Required. Stable archive id (g_/u_) or upstream source id.
-o, --output <dir>             Optional. Output directory. Defaults to a per-conversation
                               subdirectory of the per-user application data exports/
                               directory when omitted.
```

`--output` is always a **single-conversation package root**: the exporter rebuilds
`manifest.json`, `conversations.yaml` and `identities.yaml` from the conversation(s) named by
that invocation, and `conversations.yaml`/`identities.yaml` keep only the entries belonging to
them (plus any user-maintained entries, per [EXPORT_PRD.md](EXPORT_PRD.md) sections 3.2, 5.4 and
15). Pointing two different conversations at one explicit `--output` root therefore leaves the
second export's catalogs describing only the second conversation — its JSONL partitions remain
on disk but are no longer indexed. **The default destination avoids that**: when `--output` is
omitted, each conversation is published to its own root, `<exports>/<stable-id>/`, so the
default destination is always a self-consistent, standalone package and exporting one
conversation can never de-index another. Pass `--output` explicitly only when the whole package
at that root is meant to describe the conversation(s) of that single invocation.

Reliability — **R1** (Export): a normal success publishes the complete documented output per
[EXPORT_PRD.md](EXPORT_PRD.md) section 3. A caught cancellation or I/O failure attempts
in-process restoration of the prior package where possible; process crash and OS/power loss
are **not** guaranteed recovery classes. SQLite remains the system of record and re-export is
the recovery path. No commit marker, journal or rollback ledger is persisted.

Exits `1` with `failure` (export operation failure, an exporter that reported
`succeeded: false`, or a Fatal source-coverage failure during the re-import phase),
`source_unavailable`, `no_accounts`, `conversation_list_failed` or `conversation_not_found` on
the corresponding failure; `2` on a usage error; `130` on cancellation. A failed export writes
the failure document, never the result document above; the exporter's own `failure_reason`, when
it supplies one, is carried in `error.message`.

JSON shape (exit 0):

```json
{
  "succeeded": true,
  "output_directory": "C:\\...\\exports\\...",
  "conversation_ids": ["g_<16-hex>"],
  "record_count": 24,
  "unknown_count": 1,
  "partial_count": 0,
  "time_range": {
    "first_message_at": "2026-01-15T09:00:00+08:00",
    "last_message_at": "2026-02-04T09:00:00+08:00"
  },
  "files": [
    { "path": "identities.yaml", "kind": "identities", "record_count": 4 },
    { "path": "conversations.yaml", "kind": "conversations", "record_count": 1 },
    { "path": "collections.yaml", "kind": "collections", "record_count": 0 },
    { "path": "chats/groups/g_<16-hex>/2026/2026-01.jsonl", "kind": "timeline", "record_count": 22 }
  ],
  "conversation_paths": ["chats/groups/g_<16-hex>"],
  "diagnostics": [
    { "severity": "warning", "code": "unknown_message_type", "message": "...", "count": 1 }
  ]
}
```

`files[].kind` is a stable wire name (`identities`, `conversations`, `collections`,
`timeline`). `manifest.json` is published last and is described by the manifest itself, so it
is not listed in `files[]`.

Re-export is deterministic except for the explicitly generated metadata: given the same archive
state, export configuration and exporter version, every field of the package is byte-identical
between two successful exports **except** `manifest.json`'s `created_at` (and
`exporter_version` across builds), matching [EXPORT_PRD.md](EXPORT_PRD.md) section 15. A
repeated export never rewrites or corrupts a timeline partition, so re-export from the SQLite
archive remains the recovery path.

## Commands (capture family — Issue #22 / M1.5)

### `wearchive capture [--account <id>]`

Captures a supported local WeChat account into a durable, versioned, immutable Raw Vault
generation that remains readable without the original WeChat database key (FR-04/FR-05/FR-06/
FR-13/FR-20, [RAW_VAULT.md](RAW_VAULT.md)). The command delegates the entire snapshot -&gt;
publish operation to `CaptureService`; it contains no key-acquisition, SQLCipher or manifest
publication logic.

After a complete version-2 baseline, a later run verifies partition fingerprints and reuses
unchanged preserved artifacts. If incremental safety cannot be proven it performs a full
consistent capture. `--json` reports `mode`, `generation_id`, `completeness`, `coverage` and
`coverage_summary` (`expected`, `captured`, `reused`, `unavailable`, `unsupported`) plus
diagnostics. The output never contains source keys or fingerprint contents. A no-change run
publishes a new generation only after checking the current source partitions, and reports reused
coverage explicitly.

Account resolution (never prompts, safe under `--no-input`): `--account <id>` selects explicitly
by the account's stable id (`a_...`) **or** its source profile id; otherwise the current account
(`is_current`) is used, falling back to the first account.

Options:

```text
--account <id>   Optional. Stable account id (a_) or source profile id.
```

Reliability — **R1** (Raw Vault publication): a normal success publishes exactly one complete
generation with a validated manifest and checksums. A Fatal source/coverage failure or caught
cancellation discards the staged material and publishes nothing — no incomplete generation is
ever published as complete. No journal, commit marker or rollback ledger is persisted. See
[DEVELOPMENT.md](DEVELOPMENT.md) Reliability Levels and
[ADR 0008](adr/0008-raw-vault-storage-and-snapshot.md).

Exits `1` with `failure` (capture did not complete), `source_unavailable`, `no_accounts` or
`account_not_found` on the corresponding failure; `2` on a usage error; `130` on cancellation.

JSON shape (exit 0):

```json
{
  "generation_id": "gen_<16-hex>",
  "account_id": "a_<16-hex>",
  "source_profile_id": "wxid_...",
  "capture_time": "2026-03-01T12:00:00+08:00",
  "completeness": "complete",
  "mode": "baseline",
  "capture_adapter_family": "wechat-windows",
  "capture_adapter_version": "0.1.0",
  "artifact_count": 5,
  "previous_generation_id": null,
  "diagnostics": [],
  "coverage": [
    { "partition_id": "db_storage/session/session.db", "status": "captured", "diagnostic": null },
    { "partition_id": "db_storage/message/message_0.db", "status": "reused", "diagnostic": null },
    {
      "partition_id": "db_storage/voice/voice.db",
      "status": "unavailable",
      "diagnostic": "Partition 'db_storage/voice/voice.db' could not be read."
    }
  ],
  "coverage_summary": {
    "expected": 3,
    "captured": 1,
    "reused": 1,
    "unavailable": 1,
    "unsupported": 0
  }
}
```

`completeness` is `complete` or `partial`. `mode` is `baseline` for a full consistent snapshot
and `incremental` when unchanged partitions reused already-published evidence. `expected` is the
number of partitions this run accounted for; `captured + reused + unavailable + unsupported`
equals `expected`. `previous_generation_id` links to the immediately preceding published
generation for the same account, forming an append-only chain.

`coverage` reports each partition's `captured`/`reused`/`unavailable`/`unsupported` status, and
`diagnostic` gives the engineering reason for a partition that was not captured, so a consumer
can attribute a coverage gap without parsing `diagnostics[].message`. Both deliberately omit the
source fingerprint and artifact checksum: the CLI contract exposes a verifiable completeness
statement without publishing the evidence used to prove incremental safety, and never exposes
source keys. When incremental safety cannot be proven the run reads the whole source and reports a
`capture_full_fallback` diagnostic instead of claiming complete incremental coverage.

`mode: incremental` means evidence was not reacquired, not that the run was cheap: every capture
still re-fingerprints each expected partition (database file plus committed WAL bytes) before and
after the snapshot, and re-verifies the predecessor generation's artifacts before reusing any of
them. Plan for a cost proportional to source size even when nothing changed.

## Commands (Collection scope — Issue #26 / M4 foundation)

`Collection` is the one reusable named-conversation scope (docs/PRD.md FR-23, docs/HARNESS.md
section 8). Its authoritative configuration is a single user-maintained file at
`%LOCALAPPDATA%\WeArchive\collections.yaml`, reusing the documented `collections.yaml` shape and
versioned by its own top-level `schema_version`:

```yaml
schema_version: 1.0
collections:
  ai-toy:
    conversations:
      - g_0123456789abcdef
      - u_1123456789abcdef
```

Membership values are **stable conversation IDs** (`g_<16 hex>` for groups, `u_<16 hex>` otherwise)
— never display names or upstream source ids. Ownership, rebuild-survival semantics and membership
rules are recorded in [ADR 0009](adr/0009-collection-configuration-ownership.md). The
`collections.yaml` inside an export package is derived output and is never authoritative here.

Absent configuration is an empty catalog. Configuration that cannot be read or parsed, an
unsupported `schema_version`, a missing `collections` mapping or an empty Collection name is a
deterministic configuration failure (`collection_config_invalid`, exit `2`); the file is diagnosed
and never rewritten. Invalid or duplicated membership entries are reported per Collection, and
resolved membership is the valid, first-seen, de-duplicated set.

### `wearchive collection list`

Lists the available Collections. Exit `0` with an empty array when no configuration exists.

JSON shape: a JSON array of:

```json
{
  "name": "ai-toy",
  "conversation_count": 2,
  "invalid_member_count": 0,
  "duplicate_member_count": 0
}
```

### `wearchive collection show <name>`

Returns one Collection's stable conversation membership.

JSON shape (exit 0):

```json
{
  "name": "ai-toy",
  "conversation_ids": ["g_0123456789abcdef", "u_1123456789abcdef"],
  "invalid_conversation_ids": [],
  "duplicate_conversation_ids": []
}
```

Exits `1` with `collection_not_found` when the name is not defined; `2` when no `<name>` is supplied
or the configuration is invalid.

### `wearchive sync --collection <name>`

Synchronizes a Collection: resolves its stable conversation IDs, captures required live-source
evidence once through the shared `CaptureService`, then ingests each conversation from the Raw Vault.
It reuses `sync --conversation`'s resolution semantics and the Raw Vault ingest path — no second
scope abstraction, no JSONL scanning and no separate source parser.

Options:

```text
--collection <name>            Required (unless --conversation is given). Collection name.
```

Account resolution matches `capture`: the current source account is auto-selected (no prompt, safe
under `--no-input`). Capture is mandatory and account-scoped, so a capture that publishes nothing is
an operation failure (`capture_failed`, exit `1`) rather than a fabricated per-conversation failure.

Reliability — unchanged by this command. Capture is **R1** and each conversation's import is **R2**;
the run is **multi-scope, not one transaction**, so each conversation's canonical writes and ingest
checkpoint commit in that conversation's own SQLite transaction. A conversation failure never rolls
back another conversation's committed progress. No journal, commit marker or new transaction protocol
is introduced.

Per-conversation `status` is a stable wire name:

```text
succeeded    evidence changed and this conversation's canonical publication committed
no_change    the conversation was verified and nothing changed; its checkpoint kept its value
failed       a capture/ingest failure rolled this conversation back (its `error` explains it)
unresolved   the declared member matched no conversation in the captured evidence
```

JSON shape (one document on every path, including a partially successful run):

```json
{
  "collection": "ai-toy",
  "succeeded": false,
  "account_id": "a_<16-hex>",
  "source_profile_id": "wxid_...",
  "generation_id": "gen_<16-hex>",
  "capture_mode": "incremental",
  "conversations": [
    { "conversation_id": "g_<16-hex>", "status": "succeeded", "conversations_ingested": 1, "error": null },
    { "conversation_id": "u_<16-hex>", "status": "no_change", "conversations_ingested": 0, "error": null },
    { "conversation_id": "u_<16-hex>", "status": "failed", "conversations_ingested": 0, "error": "..." },
    { "conversation_id": "wxid_typo", "status": "unresolved", "conversations_ingested": 0, "error": "..." }
  ],
  "summary": { "requested": 4, "succeeded": 1, "no_change": 1, "failed": 2 },
  "invalid_conversation_ids": ["wxid_typo"],
  "duplicate_conversation_ids": []
}
```

`succeeded` is true only when every requested member succeeded or was verified unchanged, so a
partially successful run is never described as total success. This is the documented multi-scope
refinement of the CLI process contract (docs/ARCHITECTURE.md section 3.1.1): stdout carries the
structured per-scope result on every path, and the **exit status remains authoritative**:

```text
0    every requested conversation succeeded or was unchanged
1    at least one requested conversation failed or was unresolved, or capture failed
2    usage error, or invalid Collection configuration
130  cancellation; completed conversations keep their progress
```

`--json` still emits exactly one JSON document on every one of those paths.

## Commands (query family — Issue #27 / M3a)

`message list` and `context` are the shipped minimum retrieval slice of M3a
([`HARNESS.md`](HARNESS.md) sections 3–5, [`PRD.md`](PRD.md) FR-16/FR-17/FR-30).

They are read-only (**R0**): they never mutate canonical data, never read the Raw Vault, never open
live WeChat, never acquire a database key and never scan exported JSONL. They are thin adapters
over `ArchiveQueryService`, which reads the canonical SQLite archive through its existing
timeline index `(conversation_id, occurred_utc, source_order_key, id)`. No FTS table, no second
search engine and no schema migration is introduced. Retrieval therefore works while live WeChat is
unavailable, as long as canonical data exists.

Query field names are pinned by `[JsonPropertyName]` in
`src/WeArchive.Cli/Output/Dto/MessageResultDto.cs`, and the canonical message DTO mirrors the
documented envelope of [`MESSAGE_SCHEMA.md`](MESSAGE_SCHEMA.md) sections 3–6. It never exposes a
SQLite column name, a numeric upstream message type code or a WeChat table name.

### `wearchive message list --conversation <stable-id>`

Lists one conversation's canonical messages in deterministic timeline order.

Options:

```text
--conversation <stable-id>   Required. Stable conversation id (g_<16 hex> / u_<16 hex>).
                             The upstream source id and export-catalog aliases are not accepted here.
--since <time>               Optional. Inclusive lower bound.
--until <time>               Optional. Inclusive upper bound.
--participant <stable-id>    Optional. Canonical participant id (u_<16 hex>) of the sender.
--type <canonical-type>      Optional. Canonical message type wire name (text, image, …, unknown).
--limit <n>                  Optional. Page size, 1–500. Default 100.
--cursor <opaque-cursor>     Optional. Resume from a previous next_cursor.
```

**Time values.** `--since` and `--until` accept an ISO-8601 date (`2026-09-01`) or date-time
(`2026-09-01T09:30:00`, `2026-09-01T09:30:00+08:00`, `2026-09-01T01:30:00Z`); the space-separated
form (`2026-09-01 09:30:00`) is accepted as the same value. Both bounds are **inclusive instants**;
a date-only or offset-less value denotes that instant in the machine's local offset, which is the
same offset the archive renders canonical timestamps with (a `Z` or explicit offset always wins). A
date-only `--until 2026-09-01` therefore means `2026-09-01T00:00:00`, not "all of 1 September". An
unparseable value is a usage failure, never a silently widened query.

Bounds are compared at the archive's one-second resolution, because that is how the canonical
timeline stores message instants: a sub-second component is truncated before comparison, so
`--until 2026-09-01T09:30:00.500+08:00` still includes a message at `09:30:00`.

An unknown option, a repeated option or an option without a value is a usage failure. A bare `--`
ends option parsing, so an identifier that begins with `-` stays addressable.

**Ordering and paging.** Order is `occurred_utc`, then `source_order_key` (empty when the source
supplied none), then the stable message id, so messages that share an instant are neither repeated
nor skipped. Pagination is keyset-based, not offset-based: `next_cursor` is an opaque token bound
to this conversation, date range, participant and type filter set — changing `--limit` between
pages is allowed, changing a filter is not (that is `cursor_invalid`). `next_cursor` is null
exactly when `has_more` is false.

**Filters.** `--participant` addresses the canonical stable participant id stored as the message's
sender; an unresolved sender is never fabricated into one. `--type` is the canonical semantic type,
so `unknown` records defined by [`MESSAGE_SCHEMA.md`](MESSAGE_SCHEMA.md) are returned normally like
any other type.

JSON shape (exit 0):

```json
{
  "items": [
    {
      "id": "m_<16-hex>",
      "conversation_id": "g_<16-hex>",
      "sender_id": "u_<16-hex>",
      "occurred_at": "2026-01-15T09:00:00+08:00",
      "type": "text",
      "text": "下午三点开会。",
      "payload": null,
      "reply_to": {
        "message_id": "m_<16-hex>",
        "sender_id": "u_<16-hex>",
        "sender_name": "Alice",
        "text": "会议改到四点",
        "time": "2026-01-15T08:59:00+08:00"
      },
      "is_partial": false,
      "source": {
        "source_message_id": "s:1234",
        "source_type": "1",
        "source_subtype": null,
        "source_partition": "message_0",
        "source_order_key": "1234"
      }
    }
  ],
  "next_cursor": "opaque-cursor-or-null",
  "has_more": false
}
```

`payload` is the canonical type-specific structure and is null when the type carries none.
`reply_to` is null when the message is not a reply; its `message_id` is the resolved canonical
target and is null while the target is not archived, in which case the locally available snapshot
fields are still present. `source` is the documented canonical provenance block
([`MESSAGE_SCHEMA.md`](MESSAGE_SCHEMA.md) section 3.4, [`PRD.md`](PRD.md) FR-19) — it is
engineering traceability and is never an identity input.

Human output prints one line per message, led by the stable message id, followed by the UTC
timestamp, the sender, the canonical type and the semantic text, then the `next cursor:` line when
the result is resumable.

Exits `1` with `conversation_not_found` or `archive_unavailable`; `2` with `usage_error`
(missing/unknown option, invalid value) or `cursor_invalid`; `130` on cancellation.

### `wearchive context <message-id> [--before <n>] [--after <n>]`

Returns the bounded canonical window around one stable message id. The message's own conversation
scopes both sides, so a window never crosses into another conversation's timeline.

Options:

```text
<message-id>     Required. Stable canonical message id (m_<16 hex>).
--before <n>     Optional. Preceding messages, 0–100. Default 20.
--after <n>      Optional. Following messages, 0–100. Default 20.
```

The target is a distinct field from the two arrays, so a caller never infers the anchor from a list
position.

JSON shape (exit 0):

```json
{
  "message_id": "m_<16-hex>",
  "conversation_id": "g_<16-hex>",
  "before": [ { "id": "m_<16-hex>", "…": "…" } ],
  "message": { "id": "m_<16-hex>", "…": "…" },
  "after": [ { "id": "m_<16-hex>", "…": "…" } ]
}
```

`before` and `after` contain the same item shape as `message list`. Fewer records are returned at
the first and last edges of the timeline instead of failing; `--before 0 --after 0` returns only the
target.

Exits `1` with `message_not_found` or `archive_unavailable`; `2` with `usage_error`; `130` on
cancellation.

## Failure document (`--json`)

In `--json` mode a non-zero exit still writes exactly one JSON document to stdout so callers can
parse the cause from a stable field. The exit code distinguishes success from failure.

```json
{ "error": { "code": "source_unavailable", "message": "..." } }
```

Stable `error.code` values:

| code | exit | meaning |
|---|---|---|
| `usage_error` | 2 | Usage/configuration validation failure |
| `failure` | 1 | Generic runtime/operation failure |
| `cancelled` | 130 | User interrupt/cancellation |
| `source_unavailable` | 1 | The source could not be reached (account listing failed) |
| `no_accounts` | 1 | Source available but no profiles exist, so a conversation profile cannot be resolved |
| `account_not_found` | 1 | The `--account` selector matched no profile |
| `conversation_list_failed` | 1 | Enumerating conversations for a profile failed |
| `conversation_not_found` | 1 | The conversation identifier resolved to nothing |
| `conversation_describe_failed` | 1 | Describing a resolved conversation failed |
| `collection_not_found` | 1 | The Collection name is not defined by the authoritative configuration |
| `collection_config_invalid` | 2 | The user-maintained Collection configuration exists but is invalid |
| `capture_failed` | 1 | `sync --collection` could not capture usable live-source evidence |
| `message_not_found` | 1 | A stable message id resolved to no archived message |
| `cursor_invalid` | 2 | A pagination cursor is malformed, unsupported or belongs to a different query |
| `archive_unavailable` | 1 | The canonical archive could not be read (a missing archive file is created as an empty archive, exactly as `doctor` reports it) |

## `wearchive rebuild`

Rebuilds every captured account's canonical data from each account's latest published complete Raw Vault
generation. This command does not inspect or contact live WeChat, does not acquire a database key,
and does not consume JSONL exports. Supported generations must contain decrypted WeChat 4.x SQLite
evidence whose manifest and artifact checksums validate.

The command initializes a fresh canonical database through normal migrations, replays source
records through the existing parser, normalizer and import transaction, preserves matching
user-maintained participant display-name overrides, validates the completed SQLite file, then
selects it as the active archive. Any failure before replacement leaves the selected archive
untouched. Raw Vault generations and exports are not modified.

```text
wearchive rebuild [--json] [--no-input] [--quiet]
```

JSON success emits one object with `succeeded`, `archive_path`, `account_count`,
`participant_count`, `conversation_count` and `message_count`. Failures use the standard JSON
error envelope; cancellation exits `130`.

The rebuild publication guarantee is limited to normal completion and caught in-process errors.
Process crash, OS/filesystem crash and power loss during replacement are not guaranteed recovery
classes. No persistent journal or rollback protocol is used.

## `wearchive ingest`

Ingests verified Raw Vault generations into the existing canonical archive. It reads preserved
evidence only and does not contact live WeChat. By default it processes all conversations for the
selected Raw Vault account; `--conversation` limits the operation to one conversation, selected by
its stable conversation id (`g_…`/`u_…`) or its upstream source conversation id. Each conversation's
canonical writes and generation checkpoint commit in one SQLite
transaction. Repeated scoped runs use that conversation's committed generation lineage to skip
covered generations before opening their artifacts; account-wide runs also use a complete-scan
cursor to skip generations already examined for every conversation. Changed/new generations and
conversations are discovered by validating the changed generation; older evidence for a
conversation missing from a later generation remains retained. A caught cancellation rolls back
the current conversation and leaves its checkpoint unchanged. `--replay` deliberately
reprocesses preserved generations for parser repair without recapturing live source evidence. A
reader-version change also invalidates existing ingest cursors.

```text
wearchive ingest --account <raw-vault-account-id> [--conversation <stable-or-source-conversation-id>] [--replay] [--json] [--no-input] [--quiet]
```

JSON success emits one object with `succeeded` and `conversations_ingested`. Failures use the
standard JSON error envelope; cancellation exits `130`.

## Not yet implemented

The `--conversation <id-or-alias>` selector resolves by the canonical stable archive id
(`g_…`/`u_…`) or the upstream `source_id`; resolution by the export-catalog `alias` and
time-range selection is a forward refinement layered on the same export engine
([EXPORT_PRD.md](EXPORT_PRD.md) section 7) and is not implemented in M0.5. See
[ROADMAP.md](ROADMAP.md) M0.5/M1.

Collection *resolution* and Collection-scoped `sync` are implemented (Issue #26). Collection-scoped
query/search, Collection-scoped export selection, time-range selection and an interactive Collection
editor are not.

`message list` and `context` are implemented (Issue #27 / M3a). Keyword/full-text search
(`wearchive search`), SQLite FTS indexing, statistics/activity timelines beyond archive freshness,
Collection-scoped query filters and MCP transport are not: `ArchiveQueryService` already exposes
capture/ingest/canonical freshness, but no CLI status command is wired to it yet. Keyword search is
M3b work and no FTS index exists.

`--collection` is not accepted by `message list`; Collection-scoped query filtering is follow-up
work layered on the same `ArchiveQueryService` and the same Collection catalog.

`wearchive collection` has no `--account` option: stable conversation IDs are account-scoped by
construction, and the capture account is auto-selected exactly as `capture` does. A member that
belongs to a different account is reported as `unresolved`, never silently remapped.
