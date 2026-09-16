# WeArchive CLI contract

This document records the additive command, JSON and exit-code contract for the discovery
commands delivered by the M0.5 CLI product-surface migration (Issue #7). It refines the
high-level CLI contract in [`PRD.md`](PRD.md) (FR-22), [`ARCHITECTURE.md`](ARCHITECTURE.md)
section 3.1.1 and [ADR 0006](adr/0006-cli-first-product-surface.md). The CLI is the primary
product surface; this file is normative for the command shapes and machine-readable output
described here.

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

## Not yet implemented

`wearchive sync --conversation <id-or-alias>` and `wearchive export --conversation <id-or-alias>`
are part of the M0.5 command family but are delivered by separate issues; they retain the
existing R2 import and R1 export reliability levels. See [ROADMAP.md](ROADMAP.md) M0.5.
