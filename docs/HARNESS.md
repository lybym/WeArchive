# WeArchive Harness / Agent Integration Contract

## 1. Purpose

This document defines how Harnesses, AI agents and automation should access WeArchive data.

The default access path is:

```text
Harness / Agent
      │
      ▼
CLI --json / future MCP
      │
      ▼
ArchiveQueryService
      │
      ▼
archive/wearchive.db
```

Harnesses MUST NOT normally:

- query the Raw Vault directly;
- execute ad-hoc SQL against `wearchive.db` as a product contract;
- recursively scan JSONL exports for interactive retrieval when a query API exists;
- depend on WeChat table names, numeric message types or parser-specific source structures.

This contract is now partially shipped. `ArchiveQueryService`, `wearchive message list` and
`wearchive context` are implemented (Issue #27 / M3a) and are read-only over the canonical archive.
Keyword/full-text search, a CLI archive-status command and MCP transport are not implemented yet.

## 2. Layer responsibilities

### Raw Vault

Preservation and rebuild source. Internal to capture/rebuild/diagnostics. Not a Harness query surface.

### Canonical archive (`wearchive.db`)

Operational runtime dataset containing stable normalized semantics, provenance and indexes. Harness access is mediated by application services.

### ArchiveQueryService

The stable application API for retrieval. CLI and future MCP are transport adapters over the same service.

### JSONL/YAML/JSON export

Portable interchange/offline dataset. Appropriate for transfer, audit, offline batch processing and external tools; not the preferred interactive query path.

## 3. Query-service responsibilities

`ArchiveQueryService` should provide source-independent operations such as:

- list/get accounts;
- list/get conversations;
- list messages by conversation/date/person/type;
- keyword/full-text search;
- context-window retrieval around a message;
- archive status/freshness metadata;
- collection resolution;
- statistics/activity summaries.

It must return canonical DTOs and stable IDs, not SQLite implementation details.

**Shipped by Issue #27 (M3a minimum retrieval):** listing messages by stable conversation, date
range, canonical sender and canonical type with opaque cursor pagination; bounded context-window
retrieval around a stable message id; and capture/ingest/canonical freshness. Keyword/full-text
search and statistics/activity summaries remain target behavior, and no CLI status command is wired
to the freshness API yet.

## 4. CLI query surface

Target commands include:

```powershell
wearchive message list `
  --conversation g_xxx `
  --since 2026-09-01 `
  --until 2026-09-17 `
  --limit 200 `
  --json

wearchive search "AI 玩具" `
  --conversation g_xxx `
  --since 2026-01-01 `
  --limit 100 `
  --json

wearchive context m_xxx `
  --before 20 `
  --after 20 `
  --json
```

`message list` and `context` are **shipped** and normative in [`CLI.md`](CLI.md) — including the
`items` / `next_cursor` / `has_more` page shape, the context DTO that separates the target message
from the messages before and after it, the `--participant` / `--type` filters and the time grammar.
`search` remains target behavior: it needs the FTS index of section 7, which does not exist.

## 5. Pagination

Large query results MUST be paginated/cursor-based rather than forcing a Harness to load an entire archive.

Conceptual response:

```json
{
  "items": [],
  "next_cursor": "opaque-cursor-or-null",
  "has_more": false
}
```

Cursors are opaque product contracts; callers must not derive meaning from their internal representation.

## 6. Search and context workflow

Preferred agent pattern:

```text
resolve conversation / collection
        ↓
search canonical FTS
        ↓
small candidate set
        ↓
get context window for relevant message IDs
        ↓
provide only selected canonical messages to the model
```

The intended optimization is to avoid:

```text
scan many monthly JSONL files
→ parse every line
→ filter client-side
→ reload surrounding messages
```

## 7. FTS

SQLite FTS5 is a derived index over canonical archive data.

Initial searchable material should include at least:

- `semantic_text`;
- selected structured payload fields such as filename, link title and description.

FTS is rebuildable from `wearchive.db` and is not an archival source of truth.

## 8. Collections as a shared scope abstraction

A Collection is one named reusable set of stable conversation IDs.

The same Collection should be usable as a scope for:

```text
sync
query/search
Harness analysis
export
```

Example:

```yaml
collections:
  ai-toy:
    conversations:
      - g_0123456789abcdef
      - u_1123456789abcdef
      - u_2123456789abcdef
```

Target commands:

```text
wearchive collection list
wearchive collection show ai-toy
wearchive sync --collection ai-toy
wearchive search "报价" --collection ai-toy
wearchive export --collection ai-toy
```

Do not introduce separate concepts such as `watch-list`, `sync-group` or `harness-dataset` when Collection already expresses the required conversation scope.

**Authoritative configuration.** The one authoritative Collection configuration is the
user-maintained application-level file `%LOCALAPPDATA%\WeArchive\collections.yaml`, using the shape
above. The `collections.yaml` inside an export package is derived output and is never authoritative
for scope resolution. Membership values are stable conversation IDs; invalid or duplicated entries
are reported deterministically and configuration is diagnosed rather than silently rewritten. See
[ADR 0009](adr/0009-collection-configuration-ownership.md).

**Shipped status.** `collection list`, `collection show <name>` and `sync --collection <name>` are
delivered by Issue #26. Collection-scoped `search`/`export` remain target behavior and are not yet
implemented.

## 9. Incremental synchronization for Harness workflows

Recurring Harness workflows should not require full source rescans.

Target behavior:

```text
Collection ai-toy
    ├─ conversation A -> ingest checkpoint A
    ├─ conversation B -> ingest checkpoint B
    └─ conversation C -> ingest checkpoint C
```

Each conversation advances independently after its own successful canonical publication. One failed conversation must not cause unrelated conversations to lose their successful progress.

`sync --collection <name>` implements this today: it resolves the Collection's stable conversation
IDs, captures required live-source evidence once through the shared capture service, then ingests
each conversation from the Raw Vault, returning structured per-conversation
success/no-change/failed/unresolved state. A partially successful run is never reported as total
success, and the process exit status is non-zero when any requested conversation failed.

Capture progress from live WeChat is separately tracked from ingest progress from the Raw Vault; see `RAW_VAULT.md`.

## 10. Freshness

Agents need to distinguish:

- latest successfully captured Raw Vault generation;
- latest successfully ingested generation/scope;
- canonical archive query freshness;
- optional export generation time.

A future `get_archive_status` query/MCP tool should expose these values without requiring the Harness to inspect internal database tables.

**Shipped by Issue #27.** `ArchiveQueryService.GetFreshnessAsync` already reports them from three
independent sources, so the distinction is available to the eventual status command/MCP tool:

```text
capture    latest published Raw Vault generation per known account, through the Core
           IRawVaultStore contract, without partition fingerprints, checksums or artifacts
ingest     most recently committed conversation-scope canonical publication and last completed
           account-wide generation scan, without exposing the checkpoint payload or table layout
canonical  archive counts plus the newest archived message instant, as of the moment of the read
```

They are deliberately never inferred from one another: captured evidence may not be ingested yet,
and an ingest may be older than the newest capture. An account that was captured but never ingested
is reported with capture progress and null ingest progress. No CLI status command is wired to this
yet, so a Harness currently reaches it through the application service rather than through
`--json`.

## 11. Future MCP transport

A future stdio MCP server may expose the same `ArchiveQueryService` operations.

Suggested initial tools:

```text
list_accounts
list_conversations
get_conversation
list_messages
search_messages
get_message_context
list_collections
get_archive_status
```

Later write/sync tools may include:

```text
sync_conversation
sync_collection
```

MCP MUST NOT implement a second query engine or directly parse Raw Vault artifacts. It is a transport adapter over application services.

## 12. Recommended Harness rules

A Harness integration should follow these rules:

1. Resolve human names to stable conversation/participant IDs before repeated queries.
2. Prefer stable IDs over mutable display names.
3. Use `search` for candidate discovery and `context` for surrounding timeline retrieval.
4. Use date/conversation/participant filters as early as possible.
5. Page through large results; do not request the entire archive by default.
6. Do not scan exported JSONL recursively unless the user explicitly requests offline/export analysis.
7. Do not read Raw Vault as a normal query source.
8. Do not depend on raw SQLite schema from an agent prompt or external automation.
9. Treat unknown canonical messages as retained evidence, not as permission to inspect arbitrary upstream structures automatically.
10. Use archive freshness/status before assuming a recent live-WeChat message has already been captured and ingested.

## 13. Why JSONL remains

JSONL remains a first-class interchange format for:

- portable datasets;
- external analysis tools;
- offline/batch LLM processing;
- audit/inspection;
- long-lived export independent of WeArchive internals.

Its role is not replaced by QueryService. The distinction is:

```text
Raw Vault      = recovery/preservation format
wearchive.db   = runtime canonical format
QueryService   = interactive access API
JSONL          = interchange/offline export format
```
