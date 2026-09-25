# WeArchive CLI contract

This document records the additive command, JSON and exit-code contract for the M0.5 CLI
product-surface migration. It covers the read-only discovery commands (Issue #7) and the
`sync`/`export` commands (Issue #8). It refines the high-level CLI contract in
[`PRD.md`](PRD.md) (FR-22), [`ARCHITECTURE.md`](ARCHITECTURE.md) section 3.1.1 and
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
    { "partition_id": "db_storage/session/session.db", "status": "captured" },
    { "partition_id": "db_storage/message/message_0.db", "status": "reused" }
  ],
  "coverage_summary": {
    "expected": 2,
    "captured": 1,
    "reused": 1,
    "unavailable": 0,
    "unsupported": 0
  }
}
```

`completeness` is `complete` or `partial`. `mode` is `baseline` for a full consistent snapshot
and `incremental` when unchanged partitions reused already-published evidence. `expected` is the
number of partitions this run accounted for; `captured + reused + unavailable + unsupported`
equals `expected`. `previous_generation_id` links to the immediately preceding published
generation for the same account, forming an append-only chain.

`coverage` reports each partition's `captured`/`reused`/`unavailable`/`unsupported` status. It
deliberately omits the source fingerprint and artifact checksum: the CLI contract exposes a
verifiable completeness statement without publishing the evidence used to prove incremental
safety, and never exposes source keys. When incremental safety cannot be proven the run reads the
whole source and reports a `capture_full_fallback` diagnostic instead of claiming complete
incremental coverage.

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
selected Raw Vault account; `--conversation` limits the operation to one upstream conversation
ID. Each conversation's canonical writes and generation checkpoint commit in one SQLite
transaction. Repeated scoped runs use that conversation's committed generation lineage to skip
covered generations before opening their artifacts; account-wide runs also use a complete-scan
cursor to skip generations already examined for every conversation. Changed/new generations and
conversations are discovered by validating the changed generation; older evidence for a
conversation missing from a later generation remains retained. A caught cancellation rolls back
the current conversation and leaves its checkpoint unchanged. `--replay` deliberately
reprocesses preserved generations for parser repair without recapturing live source evidence. A
reader-version change also invalidates existing ingest cursors.

```text
wearchive ingest --account <raw-vault-account-id> [--conversation <source-conversation-id>] [--replay] [--json] [--no-input] [--quiet]
```

JSON success emits one object with `succeeded` and `conversations_ingested`. Failures use the
standard JSON error envelope; cancellation exits `130`.

## Not yet implemented

The `--conversation <id-or-alias>` selector resolves by the canonical stable archive id
(`g_…`/`u_…`) or the upstream `source_id`; resolution by the export-catalog `alias` and
collection/time-range selection is a forward refinement layered on the same export engine
([EXPORT_PRD.md](EXPORT_PRD.md) section 7) and is not implemented in M0.5. See
[ROADMAP.md](ROADMAP.md) M0.5/M1.
