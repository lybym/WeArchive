# WeArchive Roadmap

## Guiding rule

Requirements and architecture lead implementation. Code must not become the de facto specification.

The roadmap is milestone-based rather than date-based. A milestone is complete only when its acceptance criteria are met and its documentation matches shipped behavior.

The product-surface decision changed on 2026-09-15: WeArchive is now **CLI-first**, with a `gh`-style command interface designed for humans, scripts and agents. See [ADR 0006](adr/0006-cli-first-product-surface.md).

## M0 — Archive foundation (complete)

**Goal:** establish a stable archive and export contract before integrating a real upstream source.

Delivered:

- C#/.NET solution foundation.
- Configuration/settings model.
- Normalized domain models.
- Canonical message envelope and message-type semantics.
- Source adapter contract.
- Fixture/mock adapter.
- SQLite archive schema and migrations.
- Import-run and diagnostic models.
- Idempotent importer.
- Stable-ID identity/conversation catalogs.
- Monthly JSONL machine export.
- Unit/integration tests using fixtures.

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

### Required commands

```text
wearchive doctor
wearchive account list
wearchive conversation list
wearchive conversation show <id-or-alias>
wearchive sync --conversation <id-or-alias>
wearchive export --conversation <id-or-alias>
```

### CLI contract

- default concise human output;
- `--json` emits exactly one JSON document on stdout;
- progress/human diagnostics go to stderr;
- `--quiet` suppresses non-essential progress;
- `--no-input` never prompts;
- exit codes: `0` success, `1` operation failure, `2` usage/config failure, `130` cancellation;
- JSON field names/exit behavior covered by contract tests.

### Reliability constraints

- Phase 1 Export remains `R1` from `DEVELOPMENT.md`: complete output on normal success; caught cancellation/I/O failure attempts in-process restoration; process crash and OS/power loss are not guaranteed recovery classes.
- Conversation import remains `R2`: Fatal source-coverage failure rolls back the entire conversation transaction.
- CLI migration must not introduce R3+ crash-recovery machinery unless a separate requirement explicitly authorizes it.

### Acceptance criteria — met

- `WeArchive.Cli` is the primary executable (assembly `WeArchive`, producing `WeArchive.exe`);
- all required commands call existing application services rather than duplicating business logic;
- human and JSON output paths are tested;
- stdout/stderr and exit semantics are tested;
- `--no-input` is automation-safe;
- sync/export retain existing archive/export semantics;
- the WPF presentation layer is removed;
- the repository does not maintain two first-class presentation layers;
- self-contained `win-x64` portable release artifact is produced and smoke-tested from GitHub Actions/Release;
- docs/README/AGENTS reflect the CLI product.

### Explicit non-goals

- full-screen TUI;
- conversational/Claude Code-style shell;
- embedded LLM/agent provider;
- MCP/server mode;
- adding new archive/search semantics only to justify the migration.

## M1 — Windows local-source adapter (complete)

**Goal:** support the first real Windows WeChat 4.x local-data workflow while preserving generic architecture.

Delivered:

- Windows source-profile discovery and source/client version reporting;
- direct/group/official/system conversation enumeration;
- participant/contact and latest-remark mapping;
- text and non-text semantic import;
- links/app-share/mini-program metadata;
- reply/quote and forwarded-bundle normalization;
- system/revoke and other documented semantic types;
- source partition/shard abstraction and merged timeline ordering;
- completeness diagnostics and unknown accounting;
- read-only SQLCipher access with verified local key acquisition;
- **conversation-scoped canonical second sync** (`sync --conversation` converged on the shared
  `CaptureService` → Raw Vault → conversation-scoped incremental ingest workflow, Issue #49):
  an unchanged repeat reuses verified capture evidence, publishes nothing and reports
  `no_change`, and new/changed evidence advances only the selected conversation's ingest
  checkpoint;
- **canonical source-neutral coverage reporting** (Issue #51): `sync --conversation` results carry
  the `canonical_coverage` rollup of the verified generation — expected/available/unavailable and
  known-unsupported vs unclassified evidence with an overall complete/incomplete verdict — and an
  incomplete read fails closed as a structured `incomplete_coverage` document instead of ever
  being reported as a successful complete sync (docs/RAW_VAULT.md section 4.4, docs/CLI.md).

Real-environment acceptance — passed:

- the canonical second-sync + coverage chain required by Issues #49 and #51 was accepted in the
  real environment on Windows 11 25H2 with WeChat for Windows 4.1.15.13, covering baseline sync,
  unchanged incremental sync (`no_change`), a real source change, changed incremental sync,
  ingest-checkpoint advance, `canonical_coverage` ↔ Raw Vault manifest cross-check, and
  idempotency/stable-ID/Raw Vault immutability checks;
- evidence: [Issue #49 real-environment acceptance](https://github.com/lybym/WeArchive/issues/49#issuecomment-5887650417).

Acceptance criteria — met:

- `a second sync imports only new/changed records where supported` — implemented and tested by
  **Issue #49** (M1a canonical incremental second-sync orchestration, delivered by PR #53) and
  covered by the real-environment acceptance above;
- `canonical source coverage reporting is explicit enough to distinguish complete and incomplete
  reads` — implemented and tested by **Issue #51** (M1b canonical source-neutral partition/evidence
  coverage reporting, delivered by PR #54) and covered by the real-environment acceptance above.

The two Issues' acceptance criteria are non-overlapping. **M1 is complete on `main`.** The delivered
M1 code landed after Stable `v0.3.1`, so it is not part of that release; it has since shipped in
Stable `v0.4.0`.

## M1.5 — Raw Vault preservation (V2 storage/correctness slice complete)

**Goal:** retain immutable, key-independent Raw Vault logical history, with efficient
incremental physical storage under an hourly capture operating target.

**Delivered**: source-neutral capture/ingest and offline rebuild (Issues #22–#25,
#37); Raw Vault V2 specification and real-data block benchmark (#77); SQLCipher
and plaintext WAL snapshot proof (#78); 4096-byte fixed-block account-local CAS
(#79); historical dual-format reader (#82); manifest v3/vault format 2 writer
(#83); authoritative `vault stats` and `vault verify` (#84); official RC and
release gate (#85/#86); acceptance harness and repairs (#92/#93).

**Shipped in Stable v0.6.0** on 2026-10-14. [Issue #86 evidence](https://github.com/lybym/WeArchive/issues/86#issuecomment-6159259365)
accounts for 204 planned points with 170 actually executed (81 successful
captures, each authoritative verification green); 18 points had zero new data
payload and zero map nodes. Offline rebuild passed without source/key and
the provisional 4KiB + Zstd1 default was validated. R1 capture/R2 ingest
boundaries remain unchanged; this does not claim R3+ recovery.

**Coverage/decision:** The acceptance used a fresh vault; v1-only and mixed
v1/v2 *real-environment* paths were not exercised. Per
[ADR 0012](adr/0012-v2-only-forward-operations.md), the owner retires
dedicated new acceptance of those legacy scenarios and operates future
capture/sync on V2 only. Preserve old v1 evidence and existing shipped
reader; no deletion, implicit migration or reader removal is authorized.
Historical Partial (#63) was not directly triggered; a bounded isolated V2
fixture can supply the remaining regression evidence.

**Outside the completed V2 slice:** encryption-at-rest remains separately
deferred, as do GC/compaction, scheduled capture, cross-account dedup,
media and R3+ recovery. They are not v0.6.0 release blockers.

## M2 — Message semantics and completeness (next active milestone)

**Goal:** improve canonical semantic fidelity without weakening provenance,
source completeness, stable IDs or deterministic export.

Ordered proposed slices (each owns a separate Issue):
1. **M2a quality baseline (delivered by Issue #97):** privacy-safe classification of
   unknown messages, representative fixtures, measured semantics/coverage and
   reproducible regression acceptance criteria. The measured baseline, the synthetic
   wire-level corpus and the pinned regression tests live in
   `docs/SEMANTIC_QUALITY_BASELINE.md` and `tests/WeArchive.Tests/SemanticBaselineTests.cs`.
2. **M2b special/unknown messages:** improve high-impact supported
   classifications, retain unsupported evidence and explicit diagnostics.
3. **M2c reply/quote/forward relations:** resolve supported targets and
   nested text, preserving null/unknown for unavailable source truth.
4. **M2d metadata refresh/merge (delivered by Issue #100):** maintain stable identity and
   export paths through contact/remark changes; regression through offline rebuild/export.

Acceptance: positive and negative MESSAGE_SCHEMA fixtures, before/after
quality measures, unchanged source/Raw Vault, no invented fields, R1/R2
safety gates, and deterministic re-export. No claim that all unknowns can
be resolved.

## M3 — Search and retrieval

**Goal:** make the archive efficient for targeted machine analysis.

Deliverables:

- SQLite FTS index;
- keyword search;
- conversation filters;
- participant filters;
- date-range filters;
- context-window retrieval;
- archive statistics;
- activity timeline;
- CLI commands exposing retrieval in human and `--json` modes.

Acceptance criteria:

- search results map to canonical archived messages;
- index rebuild is deterministic;
- indexes are rebuildable entirely from canonical archive data;
- large archives remain usable from CLI workflows.

### M3a — Minimum structured retrieval (delivered by Issue #27)

**Goal:** let humans, scripts and Harnesses retrieve bounded canonical message ranges and context
windows directly from `wearchive.db` through a stable source-independent API and CLI JSON contract.

Delivered:

- `ArchiveQueryService` (Core) as the single source-independent retrieval boundary, returning
  canonical DTOs and stable IDs rather than SQLite rows or WeChat structures;
- message listing by stable conversation with inclusive `since`/`until` bounds, canonical
  participant and canonical type filters, bounded page size and opaque keyset cursor pagination;
- context-window retrieval around a stable message ID (bounded before/target/after);
- `wearchive message list` and `wearchive context` as thin CLI transports with a pinned
  `--json` contract (`items`, `next_cursor`, `has_more`; before/target/after) and documented
  exit/error semantics;
- capture/ingest/canonical freshness exposed by the service without revealing checkpoint tables or
  Raw Vault layout;
- no new index, no schema migration and no FTS table: bounded retrieval uses the migration-1
  timeline index.

Not delivered by M3a (still open in M3):

- archive statistics and activity timeline beyond basic freshness;
- a CLI status command and MCP transport over the query service;
- Collection-scoped query filtering.

### M3b — Canonical full-text retrieval (planned)

- SQLite FTS5 index and `wearchive search` keyword retrieval.

## M4 — Harness-oriented workflows

**Goal:** make repeated LLM/Harness analysis easy without introducing a separate derived-data layer.

Deliverables:

- named collections for recurring analysis scopes;
- selective export by collection and time range;
- manifest completeness/counter reporting;
- stable prompt/reference conventions for exported folders;
- optional redaction/selective-field controls;
- helper CLI commands for resolving conversations/partitions.

Delivered by Issue #26 (M4 foundation, P0 — Collection catalog and collection-scoped sync):

- one authoritative application-level Collection configuration
  (`%LOCALAPPDATA%\WeArchive\collections.yaml`) reusing the documented `collections.yaml` shape,
  owned per [ADR 0009](adr/0009-collection-configuration-ownership.md);
- `collection list` and `collection show <name>` with stable-conversation-ID membership and
  deterministic reporting of unknown names, invalid configuration and invalid/duplicate entries;
- `sync --collection <name>` resolving a Collection as a scope over the shared `CaptureService` and
  Raw Vault ingest path, with per-conversation independent checkpoints, structured
  success/no-change/failed/unresolved results and a non-zero exit status for a partially successful
  run;
- no `sync-group`/`watch-list`/`harness-dataset` concept and no canonical SQLite migration.

Still open in M4: Collection-scoped export and time-range selection, richer query/Harness automation
and status/freshness tooling.

Acceptance criteria:

- a Harness can determine what data to load from CLI JSON and/or manifest/catalog files;
- stable IDs/aliases/collections remain reusable;
- core archive/export remains functional without any AI provider;
- no LLM-specific chunk files are required.

## M5 — Optional human convenience

**Goal:** add convenience only after the command/data contracts are stable.

Candidate work must be separately justified. Possible examples:

- richer human-readable CLI reports;
- optional interactive helpers;
- scheduled local sync integration.

A GUI or full-screen TUI is **not** assumed by this milestone and requires a new product decision if proposed.

## Deferred / exploratory

These items are intentionally not committed:

- macOS support;
- Linux support;
- multi-machine archive reconciliation;
- encrypted archive-at-rest option;
- plugin system for additional chat sources;
- direct integration with note systems;
- binary media preservation;
- OCR/ASR pipelines;
- remote crawling of linked webpages;
- LLM-specific derived/chunk datasets;
- MCP/server interface;
- embedded conversational agent.

## Packaging and release

Historical WPF MVP releases used self-contained `win-x64` publishing plus portable ZIP and Velopack installer/update assets. That line is retired.

M0.5 delivers a simpler product requirement: a **self-contained `win-x64` CLI artifact** suitable for direct invocation by humans and agents, shipped as a portable ZIP with no installer and no auto-updater ([ADR 0007](adr/0007-cli-self-contained-distribution.md) supersedes the Velopack distribution decision of [ADR 0004](adr/0004-distribution-velopack.md)).

GitHub Actions/Release remains the source of test artifacts and release artifacts. Code signing remains separate work unless explicitly scheduled.

## Release discipline

Provisional mapping:

- `0.1.x` — historical WPF MVP + archive/source foundation;
- next minor line — CLI product-surface migration;
- subsequent lines — M1 completion, M2 semantics, M3 retrieval, M4 Harness workflows;
- `1.0.0` — stable archive/message/export/CLI contracts and supported upgrade/deprecation path.

Exact version numbers are chosen by release work; roadmap order is normative, version arithmetic is not.

## Current priority

**Stable baseline: `v0.6.0`** (2026-10-14, #86 hourly V2 acceptance,
#93 merged, Release/CI passed). M1.5 V2 storage/correctness is **complete**,
not a pending P0. Encryption-at-rest remains an independent deferred
protection item.

**V2-only forward policy:** only V2 (manifest v3/vault format 2) for new
capture/sync and future real-environment acceptance. No v1-only/mixed
acceptance work; #86 did not test those historical paths. Existing v1
files/readers remain untouched pending any separate breaking-change
authorization (ADR 0012).

**Next implementation priorities in dependency order:**
1. M2a semantic evidence/quality baseline → M2b special/unknown semantics
   → M2c reply/forward targets → M2d metadata refresh/merge correctness
   (PRD FR-07/19/24/25, MESSAGE_SCHEMA).
2. M3b FTS5 over canonical SQLite (PRD FR-15/16/17): evaluate Chinese/
   mixed-language tokenization and performance; implement reproducible
   rebuildable index and `wearchive search` with bounded JSON results,
   stable IDs, provenance and context. M3a `ArchiveQueryService`,
   `message list` and `context` are already shipped (#27).
3. Remaining M4 Harness workflows (FR-18/23, HARNESS.md): reuse the
   authoritative Collection (#26) for scoped query/search and export;
   add read-only CLI freshness/status, scoped time-range selection and
   evidence references for bounded analysis. MCP is optional, separately
   scoped, and not a prerequisite.

**Independent, bounded V2-only follow-up (delivered, Issue #96):** the
historical Partial (#63) path is guarded by isolated synthetic V2 fixture
regressions — conversation- and Collection-scoped live sync follows the current
complete generation while direct historical ingest stays fail-closed — and the
#86 capture failures caused by source-write races are quantified in
`docs/CAPTURE_WRITE_RACE_EVIDENCE.md`: fail-closed at every point, no partial
generation published, availability-only impact, and no product retry added; any
bounded retry/backoff remains a proposal for a future Issue. Preserve snapshot
integrity. No new seven-day run or v1-only/mixed test gate is required.

M0/M0.5/M1, #66 canonical-only offline export, #71 multi-shard support,
#26 Collection sync and #27 minimum structured retrieval are delivered
history, not new development. Create Issue-first work with documented
reliability levels; do not silently start R3+, background scheduler or
an embedded AI provider.
