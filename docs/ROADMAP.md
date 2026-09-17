# WeArchive Roadmap

## Guiding rule

Requirements and architecture lead implementation. Code must not become the de facto specification.

The roadmap is milestone-based rather than date-based. A milestone is complete only when its acceptance criteria are met and its documentation matches shipped behavior.

WeArchive is CLI-first: a `gh`-style command interface is the primary product surface for humans, scripts and agents. See [ADR 0006](adr/0006-cli-first-product-surface.md). The CLI is distributed as a self-contained portable `win-x64` ZIP; see [ADR 0007](adr/0007-cli-self-contained-distribution.md).

The preservation/query architecture was refined on 2026-09-17. Target data flow is:

```text
Live WeChat
    │ capture
    ▼
Raw Vault / Source Archive        archival source of truth
    │ read / parse / normalize
    ▼
archive/wearchive.db              rebuildable canonical operational store
    │
    ▼
ArchiveQueryService
    ├─ CLI --json
    └─ future MCP
          │
          ▼
       Harness / Agent

archive/wearchive.db
    └─ JSONL/YAML/JSON export     portable interchange/offline dataset
```

See [RAW_VAULT.md](RAW_VAULT.md), [HARNESS.md](HARNESS.md) and [ADR 0008](adr/0008-raw-vault-canonical-query-layers.md).

**Status boundary:** the 0.2.x implementation still imports directly from the live WeChat source into the canonical SQLite archive. Raw Vault, `capture`, `rebuild`, the full query command family and MCP are target architecture, not shipped behavior.

## M0 — Archive foundation (complete)

**Goal:** establish stable canonical archive and export contracts before integrating a real upstream source.

Delivered:

- C#/.NET solution foundation;
- configuration/settings model;
- normalized domain models;
- canonical message envelope and message-type semantics;
- source adapter contract;
- fixture/mock adapter;
- SQLite archive schema and migrations;
- import-run and diagnostic models;
- idempotent importer;
- stable-ID identity/conversation catalogs;
- monthly JSONL machine export;
- unit/integration tests using fixtures.

Acceptance criteria — met:

- fixture data imports end to end;
- re-import creates no logical duplicates;
- every fixture message follows `MESSAGE_SCHEMA.md`;
- unknown types are retained and counted;
- stable conversation paths do not depend on mutable names;
- selective export by conversation works in the export engine;
- export results are deterministic;
- import provenance is traceable;
- archive operations can be retried under their documented reliability semantics.

The historical WPF shell was an implementation vehicle and is not part of the enduring M0 product contract.

## M0.5 — CLI product-surface migration (delivered)

**Goal:** replace the WPF-first product surface with a small `gh`-style command CLI while preserving the existing archive/source/export engine.

Shipped command family:

```text
wearchive doctor
wearchive account list
wearchive conversation list
wearchive conversation show <id-or-alias>
wearchive sync --conversation <id-or-alias>
wearchive export --conversation <id-or-alias>
```

CLI contract:

- default concise human output;
- `--json` emits exactly one JSON document on stdout;
- progress/human diagnostics go to stderr;
- `--quiet` suppresses non-essential progress;
- `--no-input` never prompts;
- exit codes: `0` success, `1` operation failure, `2` usage/config failure, `130` cancellation;
- JSON field names/exit behavior covered by contract tests.

Reliability constraints:

- Phase 1 Export remains `R1` from `DEVELOPMENT.md`;
- current conversation import remains `R2` for Fatal source-coverage failures;
- stronger crash-recovery protocols require explicit requirements rather than review-driven scope growth.

Acceptance criteria — met:

- `WeArchive.Cli` is the primary executable;
- all required commands call application services rather than duplicating business logic;
- human and JSON output paths are tested;
- stdout/stderr and exit semantics are tested;
- `--no-input` is automation-safe;
- WPF-specific product code is removed;
- a self-contained `win-x64` portable release artifact is produced and smoke-tested.

## M1 — Windows local-source adapter (MVP slice delivered; completion work remains)

**Goal:** support Windows WeChat local-data collection while keeping source-specific behavior isolated.

Delivered:

- Windows source-profile discovery and source/client version reporting;
- direct/group/official/system conversation enumeration;
- participant/contact and latest-remark mapping;
- text and non-text semantic import;
- links/app-share/mini-program metadata;
- reply/quote and forwarded-bundle normalization;
- source partition/shard abstraction and merged timeline ordering;
- completeness diagnostics and unknown accounting;
- read-only SQLCipher access with verified local key acquisition.

Still required:

- explicit complete/incomplete source partition coverage reporting;
- incremental acquisition semantics that can evolve into the Raw Vault capture checkpoint model;
- conversation-scoped ingest progress so unrelated conversations advance independently.

The migration-1 `source_checkpoints` table is an earlier generic checkpoint design. It must not be treated as the final capture/ingest checkpoint contract merely because it already exists.

## M1.5 — Raw Vault preservation and rebuild (next architecture foundation)

**Goal:** make preserved source evidence independent of live WeChat availability and make the canonical SQLite archive rebuildable.

Deliverables:

- Raw Vault / Source Archive storage with immutable logical generations;
- first complete supported capture followed by safe incremental capture where supported;
- source-version metadata, checksums, coverage/completeness diagnostics and generation manifests;
- capture that preserves source-faithful fields rather than reducing unknown records before preservation;
- explicit separation of capture checkpoints from canonical ingest checkpoints;
- source deletion/absence never implicitly deletes earlier preserved generations;
- `wearchive capture` target workflow: live WeChat -> Raw Vault only;
- `wearchive rebuild` target workflow: Raw Vault -> fresh canonical archive, with no live-WeChat access;
- reader/parser selection by preserved source version;
- deterministic stable IDs reproduced across rebuilds;
- rebuildable FTS/derived indexes after canonical reconstruction.

Security/recovery invariants:

- Raw Vault must remain readable without reacquiring the original WeChat database key;
- WeChat database keys are never persisted;
- transient scratch copies are cleaned up, while successfully published Raw Vault snapshots are intentional persistent archive data;
- a Raw Vault generation must use a documented consistent snapshot mechanism rather than unsafe live DB/WAL file copying;
- binary media preservation is not implied by this milestone; see `RAW_VAULT.md` section 13.

Acceptance criteria are defined normatively in [RAW_VAULT.md](RAW_VAULT.md).

## M2 — Message semantics and completeness

**Goal:** improve canonical semantic depth without weakening source preservation or stable IDs.

Remaining emphasis:

- more reliable special-message semantics;
- better unknown-type diagnostics;
- reply-target resolution improvements;
- metadata refresh/merge correctness;
- parser improvements that can be replayed against Raw Vault generations once M1.5 is delivered.

Acceptance criteria:

- important source events remain visible;
- forwarded bundles retain available nested text;
- unknown canonical records are never silently discarded;
- unknown source fields are not discarded by the preservation layer merely because the current parser does not understand them;
- metadata refresh/parser upgrades do not change stable IDs/paths;
- re-export remains deterministic.

## M3 — ArchiveQueryService and retrieval

**Goal:** make the canonical archive the efficient runtime query surface for humans, scripts and agents.

The stable product API is `ArchiveQueryService`. CLI and future MCP are transport adapters; Harnesses must not depend directly on SQLite schema, Raw Vault files or recursive JSONL scans for normal interactive retrieval.

### M3a — Minimum structured retrieval (high priority)

Deliverables:

- `ArchiveQueryService` abstraction over canonical archive models;
- message listing by conversation and date range;
- participant/type filters where supported;
- cursor-based pagination;
- context-window retrieval around a stable message ID;
- archive/capture/ingest freshness status;
- CLI `--json` query commands with stable DTOs;
- `docs/HARNESS.md` as the Agent integration contract.

Acceptance criteria:

- large result sets are paginated;
- query results expose stable canonical IDs, not SQLite/source internals;
- ordinary query/export operations can work when live WeChat is unavailable once the required canonical data already exists;
- Harness workflows do not need to scan exported JSONL by default.

### M3b — Full-text search and derived indexes

Deliverables:

- SQLite FTS5 index over canonical semantic content;
- keyword search;
- conversation/participant/date filters;
- context-window integration;
- archive statistics and activity timeline.

Acceptance criteria:

- search results map to canonical archived messages;
- indexes are deterministic and rebuildable entirely from `wearchive.db`;
- FTS is treated as derived state, not archival evidence.

## M4 — Harness and collection workflows

**Goal:** make repeated Harness/Agent analysis efficient without introducing a second data model.

Deliverables:

- named Collection as the shared scope abstraction for sync/query/search/export;
- collection-scoped incremental ingest with per-conversation progress;
- selective export by collection and time range;
- stable Harness reference conventions;
- optional redaction/selective-field controls;
- future stdio MCP transport over `ArchiveQueryService` after the query contract is stable.

Target examples:

```text
wearchive collection list
wearchive collection show ai-toy
wearchive sync --collection ai-toy
wearchive search "报价" --collection ai-toy
wearchive export --collection ai-toy
```

Rules:

- do not invent parallel scope concepts such as `watch-list`, `sync-group` or `harness-dataset` when Collection already represents the conversation set;
- MCP must not implement another parser/query engine or read Raw Vault directly;
- JSONL remains a first-class interchange/offline dataset, not the default interactive Harness query surface.

## M5 — Optional human convenience

**Goal:** add convenience only after preservation and query contracts are stable.

Candidate work must be separately justified. Possible examples:

- richer human-readable CLI reports;
- optional interactive helpers;
- scheduled local capture/sync integration.

A GUI or full-screen TUI is not assumed by this milestone and requires a new product decision if proposed.

## Deferred / exploratory

These items are intentionally not committed unless a later requirement promotes them:

- macOS source support;
- Linux source support;
- multi-machine Raw Vault/archive reconciliation;
- Raw Vault encryption-at-rest/key-management design;
- plugin system for additional chat sources;
- direct integration with note systems;
- binary media preservation / Media Vault;
- OCR/ASR pipelines;
- remote crawling of linked webpages;
- LLM-specific derived/chunk datasets;
- embedded conversational agent.

MCP is no longer an architectural non-goal: it is an optional future transport under M4, but only after `ArchiveQueryService` exists and must not become a second business/query layer.

## Packaging and release

The shipped artifact remains a self-contained `win-x64` CLI published as `WeArchive-win-x64.zip`, containing `WeArchive.exe` plus a `wearchive.cmd` PATH shim. There is no installer and no auto-updater. Packaging/distribution changes require their own product/ADR decision.

Code signing remains separate work unless explicitly scheduled.

## Release discipline

Provisional mapping:

- `0.1.x` — historical WPF MVP + archive/source foundation;
- `0.2.x` — CLI-first product surface and current live-source -> canonical archive behavior;
- next feature lines — preservation/rebuild, incremental sync, minimum query/Harness contract, FTS and collection workflows;
- `1.0.0` — stable preservation/canonical/query/export/CLI contracts and supported upgrade/deprecation path.

Exact version numbers are chosen by release work; roadmap order is normative, version arithmetic is not.

## Current priority

The current implementation order is driven by the real 0.2.x usage bottlenecks and the preservation architecture:

1. **P0 — Raw Vault baseline + rebuild contract implementation (M1.5).** Preserve source-faithful evidence independently of live WeChat and prove a fresh `wearchive.db` can be rebuilt without live source/key reacquisition.
2. **P0 — Conversation-scoped incremental capture/ingest + Collection sync foundation (M1/M1.5/M4).** Separate capture and ingest checkpoints; avoid full rescans; let conversations advance independently.
3. **P0 — Minimum `ArchiveQueryService` + message/date/conversation/context retrieval + Harness CLI contract (M3a).** Harness/Agent must stop depending on recursive JSONL scanning for normal interactive queries.
4. **P1 — SQLite FTS5 + `search`/context workflows (M3b).** Use canonical SQLite/indexes for targeted candidate retrieval.
5. **P1 — Collection as one scope for sync/query/export/Harness (M4).** Do not create duplicate scope concepts.
6. **P1 — stdio MCP transport after QueryService is stable (M4).** MCP wraps the same application service; it does not parse Raw Vault or implement a second query engine.
7. Continue M2 semantic depth in parallel where it does not displace the preservation/query foundations above.

The foundation that must not be weakened is now explicit:

```text
Raw Vault      = archival source of truth / preserved evidence
wearchive.db   = rebuildable canonical operational store
QueryService   = interactive retrieval API
CLI / MCP      = transport adapters
JSONL/YAML     = portable interchange/offline export
```
