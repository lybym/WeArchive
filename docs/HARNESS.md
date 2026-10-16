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

This contract is now shipped for interactive retrieval. `ArchiveQueryService`, `wearchive message
list` and `wearchive context` are implemented (Issue #27 / M3a) and are read-only over the
canonical archive. The derived FTS index and the search service contract are implemented
(Issue #102 / M3b-1), and the `wearchive search` CLI transport is implemented (issue #103 /
M3b-2). The read-only `wearchive status` freshness report and the citation-ready evidence
references are implemented (issue #107 / M4c). The MCP transport is not implemented yet.

**Post-v0.6.0 implementation order:** M2 message-semantic correctness and
provenance first, then M3b rebuildable SQLite FTS5/`wearchive search`
(including measured Chinese/mixed-language recall), then remaining M4
Collection-scope search/export, freshness-status CLI and bounded evidence
references. The M3b index slice and the CLI transport are both delivered, as
is Collection-scope search/export (issues #103–#106, including export
time-range selection). Existing M3a query/context
and Collection sync already ship; do not reimplement them. The V2-only
forward decision affects preservation/capture, not this canonical query
boundary (ADR 0012).

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
search is delivered by the M3b slices (Issues #102/#103) through the same service. Statistics and
activity summaries are delivered by the M3c slice (issue #104) as
`ArchiveQueryService.GetStatisticsAsync` — one bounded, read-only report of scope totals, the
canonical type distribution and day/month/conversation breakdown buckets, with unknown, partial
and unattributed records reported explicitly. Archive freshness is wired to the CLI by the M4c
slice (issue #107) as the read-only `wearchive status` command.

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

wearchive stats `
  --bucket month `
  --since 2026-01-01 `
  --limit 100 `
  --json

wearchive status `
  --json
```

`message list`, `context` and `search` are **shipped** and normative in [`CLI.md`](CLI.md) —
including the `items` / `next_cursor` / `has_more` page shape, the context DTO that separates the
target message from the messages before and after it, the `--participant` / `--type` filters and
the time grammar. `search` runs over the FTS index of section 7 and returns the same canonical
message DTO, so the workflow below is the shipped retrieval pattern. `stats` is **shipped** by the
M3c slice (issue #104) and normative in [`CLI.md`](CLI.md): one read-only document naming its
scope and basis, with unknown/partial/unattributed records as explicit totals. `status` is
**shipped** by the M4c slice (issue #107) and normative in [`CLI.md`](CLI.md): one read-only
document of the three independent freshness stages of section 10.

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

The M3b-0 specification ([SEARCH_SPEC.md](SEARCH_SPEC.md), ADR 0013) fixes this contract: the
searchable-field inventory, a deterministic CJK bigram projection as the index
input (so 2-character Chinese queries such as `模型` and mixed terms such as `AI玩具` match),
term/phrase/prefix matching with implicit AND, `bm25` ranking with the canonical timeline order as
tie-break, conversation/Collection/time/participant/type filters, opaque cursor pagination, and a
derived R0 index lifecycle (forward migration, same-transaction maintenance, deterministic rebuild
from canonical rows only). The index and the service contract are implemented by the M3b index
slice (Issue #102); the `wearchive search` CLI transport is implemented by the M3b CLI slice
(issue #103) and is normative in [`CLI.md`](CLI.md).

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
wearchive message list --collection ai-toy
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
delivered by Issue #26. Collection-scoped `search` is delivered by issue #103
(`wearchive search --collection`), Collection-scoped canonical message listing by issue #105
(`wearchive message list --collection`), and Collection-scoped export with time-range refinement
by issue #106 (`wearchive export --collection ai-toy [--since <time>] [--until <time>]`); all of
them resolve the same authoritative catalog, so a Collection is one scope across sync, listing,
search, statistics and export. An export reports its resolved scope and unarchived members in the
CLI result and in the package manifest instead of silently reading a partially archived Collection
as complete.

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
           account-wide generation scan, without exposing the checkpoint payload or table layout;
           projected by the Raw Vault ingest component that owns the cursor encoding, behind the
           Core IIngestProgressSource contract
canonical  archive counts plus the newest archived message instant, as of the moment of the read
```

They are deliberately never inferred from one another: captured evidence may not be ingested yet,
and an ingest may be older than the newest capture. An account that was captured but never ingested
is reported with capture progress and null ingest progress.

**Shipped by issue #107 (M4c).** `wearchive status` (normative in [`CLI.md`](CLI.md)) is the
read-only CLI projection of this report: one JSON document with `basis` (which archive, as of
`queried_at`), `canonical` (counts plus the newest archived message instant), `capture` (the latest
published generation per known account, with a stage-level `unavailable_reason` when the
preservation store cannot be enumerated at all) and `ingest` (committed canonical publication
progress per account). It reports the stages exactly as they were observed and never derives a
combined verdict: whether a capture is "stale" or an ingest is "behind" is the caller's judgement
from independent facts, not a product assertion — a synthesized fresh/stale label would be exactly
the inference this section forbids.

A stage that cannot be read degrades instead of failing the report: an unreadable preservation
store yields a null generation plus an explicit reason, so the canonical status stays available and
a real defect remains diagnosable rather than presenting as an unavailable archive. That reason is
the failure's exception type and message — engineering diagnostics a caller reports, not an input it
parses, and never artifact contents, a fingerprint, a checksum or message content.

## 11. Evidence references (citations)

A Harness that quotes archived messages downstream needs a durable, verifiable way to say *which*
evidence it cited. An evidence reference is that citation: a stable, source-neutral address of
exactly one archived canonical message together with the context window it was cited from
(issue #107, docs/PRD.md FR-17).

**Canonical form.**

```text
wearchive://message/<m_id>?conversation=<conversation-id>&before=<n>&after=<n>
```

The reference names only stable canonical identities and the cited window:

- `<m_id>` — the stable canonical message ID (`m_<16 hex>`), the citation target;
- `conversation` — the stable conversation ID (`g_<16 hex>` / `u_<16 hex>`) the message was cited
  from; resolution verifies the archive agrees;
- `before` / `after` — the window counts (0–100) the citation covers, so re-resolution reproduces
  exactly the window the agent cited.

It never contains a Raw Vault path, artifact hash, partition fingerprint, checkpoint payload or any
source-internal identifier — a citation outlives storage layout changes precisely because it does
not name them. The grammar is strict and fail-closed: the exact lowercase scheme, exactly those
three query parameters, no duplicates, no percent-encoding, stable IDs in their canonical lowercase
form. Anything else is rejected (`usage_error`), because a citation that silently parsed into a
different window would resolve to evidence the agent did not cite.

**Construction.** Every `wearchive context` result carries the citation-ready `reference` string for
the window it returned — the returned window's own counts, so copying that field and resolving it
later reproduces the same window. For a citation built from `message list` or `search` items, use
the item's `id` and `conversation_id` and the window you then retrieve with `context`.

A complete citation record is the reference plus the cited message's canonical envelope as returned
by the CLI — in particular its `occurred_at`, `type`, `text` and the `source` provenance block.
The provenance block is retained as engineering traceability, not as part of the address: it is
never an identity input and never appears inside the reference.

**Resolution.** `wearchive context <reference>` resolves a reference through the official CLI:

```powershell
wearchive context "wearchive://message/m_x?conversation=g_y&before=5&after=5" --json
```

Resolution returns exactly the cited canonical message and its cited window, or fails explicitly:

- the message is not in the archive → `message_not_found` (exit 1) — the explicit "unavailable"
  outcome of a stale or wrong-archive citation;
- the reference names a conversation the message does not belong to → `reference_unresolvable`
  (exit 1) — the citation cannot be exactly true, so it never degrades into a nearby match;
- the reference does not parse → `usage_error` (exit 2).

An explicit `--before`/`--after` overrides the cited window (the echoed `reference` then reports the
window actually requested). A message ID that exists is returned verbatim with all its canonical
semantics — unknown, partial and unresolved-sender records are cited as they are archived, never
normalized away.

## 12. A bounded Harness analysis flow

The minimal bounded flow for a Harness analysis over one Collection — every step through the
official CLI, every step read-only except a sync the caller explicitly requested
(issue #107, [`CLI.md`](CLI.md)):

```text
1. resolve Collection      wearchive collection show <name>          (read-only)
2. optional sync           wearchive sync --collection <name>        (write; ONLY on explicit request)
3. status                  wearchive status --json                   (read-only)
4. search                  wearchive search "query" --collection <name> --json
5. context                 wearchive context <message-id> --before 20 --after 20 --json
6. citations               the result's reference field / section 11
7. status disclosure       wearchive status --json again; cite the basis and queried_at
```

The shape of this flow is normative for Harness integrations:

- **Status before and after.** Read `wearchive status` before analysis (rule 10 below: never assume
  a recent live message is already captured and ingested) and disclose the status basis with the
  analysis (the `basis.archive_path` and `basis.queried_at` of the run).
- **Sync is never implicit.** No query command syncs, and no analysis step may trigger a sync by
  itself; refreshing canonical state is always the caller's explicit decision at step 2. Analysis
  over a stale archive is a disclosed fact, not an error — the status stages make it visible.
- **Everything else is read-only (R0).** Steps 1 and 3–7 mutate nothing: no canonical data, no Raw
  Vault evidence, no checkpoint. There is no background scheduler and no second query stack; a
  future MCP transport would expose the same service, not replace the flow.
- **Scope through Collection.** `--collection` resolves the same authoritative catalog in search,
  listing, statistics and export, so one named scope covers the whole analysis.

## 13. Future MCP transport

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

## 14. Recommended Harness rules

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

## 15. Why JSONL remains

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
