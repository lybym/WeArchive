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

## M1.5 — Raw Vault preservation (incremental capture delivered; milestone incomplete)

**Goal:** capture a supported local WeChat account into a durable, versioned, immutable Raw Vault
generation that remains readable without the original WeChat database key, and keep that capture
current without reacquiring unchanged evidence.

Delivered:

- source-independent Core contracts for Raw Vault capture and generation metadata;
- Raw Vault persistence under Infrastructure (immutable generations, publish-last, SHA-256 checksums);
- WeChat-specific capture adapter (consistent snapshot via `SqlCipherDatabaseCache`, transient key, decrypted artifacts);
- `wearchive capture` CLI command as a thin adapter over `CaptureService`;
- generation discovery/validation (list, latest, open-with-checksum-verification);
- key non-persistence (no upstream key in vault, canonical SQLite, logs or CLI output);
- unknown/source-specific fields preserved in captured evidence;
- versioned capture checkpoints published inside the generation manifest (Issue #25);
- per-partition change detection over source database + committed WAL content (Issue #25);
- explicit expected/captured/reused/unavailable/unsupported partition coverage (Issue #25);
- automatic widening to a full consistent snapshot whenever incremental safety cannot be proven (Issue #25).

Still missing (non-goals of the rebuild slice, and open/deferred rather than prerequisites for the
delivered M1 canonical sync loop):

- physical cross-generation storage-dedup optimization;
- Raw Vault encryption-at-rest.

Real-environment verification on a supported Windows/WeChat 4.x installation was performed under
Issue #37 and passed. On the real account the baseline capture reached `complete` with expected 25 /
captured 24 / unsupported 1 (`migrate/unspportmsg.db` recorded as `unsupported` with a
`partition_unsupported` info diagnostic and excluded from the 24-fingerprint checkpoint), two
incremental captures reused 18-24 unchanged partitions with no full fallback, and the isolated
Raw-Vault-only rebuild succeeded with 1 account, 1382 conversations and 216662 messages while
reporting the account directory with no published generation through `skipped_accounts`. All 9
generations x 24 artifacts re-hashed with 0 missing and 0 changed, and the pre-existing canonical
archive was restored byte-identical. The first `v0.3.0-rc.1` real-account run exposed Issue #37
(recursive `*.db` discovery treated `migrate/unspportmsg.db` as required even though the canonical
rebuild reader does not consume it); the source-partition support policy is now documented and
implemented, so `v0.3.0` Stable is no longer blocked by it.

Issue #23 delivers the bounded Raw-Vault-only canonical rebuild foundation and `wearchive rebuild`.
Issue #24 adds conversation-scoped Raw Vault ingestion/checkpoints. Issue #25 adds incremental
live-source capture with partition coverage; it does not authorize collection orchestration.

Acceptance criteria — met for the capture slice:

- a supported WeChat account can be captured into a versioned Raw Vault generation;
- a successful capture produces a complete, parseable, versioned manifest;
- every captured artifact referenced by the manifest has a verifiable SHA-256 checksum;
- manifest records source product/version, source profile, capture adapter family/version, capture time and completeness;
- published generations are logically immutable; later capture never edits an earlier generation;
- captured evidence remains readable after live WeChat access/original DB key are unavailable;
- no WeChat DB key is written to Raw Vault, canonical SQLite, logs or CLI output;
- no persistent recovery journal, rollback ledger or complex R3+ state machine is added;
- after a complete baseline a later capture distinguishes unchanged from new/changed partitions and reacquires only the latter (Issue #25);
- the capture checkpoint advances only with a successfully published complete generation, and a Fatal publication leaves it unchanged (Issue #25);
- a disappeared source partition is reported and never deletes an earlier generation or its evidence (Issue #25).

Do not mark M1.5 complete in this Issue.

## M2 — Message semantics and completeness

**Goal:** improve semantic depth without changing the canonical export contract.

Remaining emphasis:

- more reliable special-message semantics;
- better unknown-type diagnostics;
- reply-target resolution improvements;
- metadata refresh/merge correctness.

Acceptance criteria:

- important source events remain visible;
- forwarded bundles retain available nested text;
- unknown records are never silently discarded;
- metadata refresh does not change stable IDs/paths;
- re-export remains deterministic.

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

- SQLite FTS5 index and `wearchive search` keyword retrieval;
- archive statistics and activity timeline beyond basic freshness;
- a CLI status command and MCP transport over the query service;
- Collection-scoped query filtering.

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

The Stable baseline is **`v0.5.1`** (Release Issue #73, Release PR #74). It carries the delivered
chain through Stable `v0.4.0` (M1 Windows local-source adapter) and `v0.5.0` (canonical archive-only
offline export) plus the WeChat multi-shard rotation ingest fix (Issue #71, PR #72). The post-`v0.3.0` cleanup batch is delivered
and closed — the CI credibility work (Issue #40) and the deferred hardening that included the
`capture --account` stable-id defect (Issue #39) — so it is historical context, not an active
priority. `AGENTS.md` "Current priority" states the same order.

**M1 — Windows local-source adapter is complete on `main` and has moved into delivered/completed
history.** Its release slice was delivered by **Issue #49 (M1a)**, which converged the canonical
incremental second-sync path over the live workflow — `sync --conversation` on the shared
`CaptureService` → Raw Vault → conversation-scoped ingest orchestration already shipped for
Collection sync — and by **Issue #51 (M1b)**, which added the canonical source-neutral
partition/evidence coverage reporting on the stable sync-result boundary Issue #49 handed over:
`canonical_coverage` on `sync --conversation`, the shared coverage model for direct Raw Vault ingest,
and the structured `incomplete_coverage` failure. Issue #49's real-environment acceptance of the
combined #49 + #51 chain passed
([evidence](https://github.com/lybym/WeArchive/issues/49#issuecomment-5887650417)), so both M1
acceptance criteria are met. The delivered M1 code landed on `main` **after** Stable `v0.3.1` and is
therefore not part of that release; it has since shipped in Stable `v0.4.0`.

1. **The next product P0 is M2 — message semantics / semantic depth** (docs/ROADMAP.md M2):
   more reliable special-message semantics, better unknown-type diagnostics, reply-target resolution
   improvements and metadata refresh/merge correctness, without changing the canonical export
   contract.
2. The remaining M1.5 items — physical cross-generation storage deduplication and Raw Vault
   encryption-at-rest — stay open/deferred optimization and protection work. They are **not**
   completed by M1 completion, they are **not** prerequisites for the delivered M1 canonical sync
   loop, and M1.5 itself remains incomplete. The delivered M1.5 capture slice (Issues #22/#25/#37) is
   verified in the real environment: versioned immutable generations, safe incremental capture and
   explicit partition coverage.
3. Build M3 retrieval and the remaining M4 Harness workflows on the CLI contract.

FTS/search, Collection export/query, MCP, a scheduler, deduplication and encryption-at-rest all
remain outside the delivered M1 scope; M1 completion does not cover them.

**Issue #47** (profile-id selector case-sensitivity across `capture`/`conversation`, plus the PR #44
fail-closed test-gap hardening) is completed/closed post-`v0.3.1` **P2 hardening history**, delivered by PR #52.
It is not active product work and does not change the M2 P0 priority.

Issue #24 delivered conversation-scoped Raw Vault ingest checkpoints and Issue #25 delivered
incremental live-source capture with explicit capture-side partition coverage; both are shipped and
are the foundation Issue #49 built on rather than work to repeat. The capture-side coverage of
Issue #25 and the source-partition support policy of Issue #37 are likewise the evidence source
Issue #51 mapped into canonical coverage semantics — without redefining either of them.

Issue #26 delivered the M4 Collection sync foundation (Collection catalog, `collection list`/`show`,
`sync --collection`); Collection-scoped query/search and export selection remain follow-up work.

Issue #27 delivered the M3a minimum retrieval slice (`ArchiveQueryService`, `message list`,
`context`); the FTS index, keyword search, statistics/activity timelines, a CLI status command and
MCP transport remain open.

Issue #37 delivered the WeChat source-partition support policy and the real-environment verification
of the capture and Raw-Vault-only rebuild chain.

The migration must preserve the foundation already delivered: generic adapter contract, canonical message schema, normalized models, SQLite system of record, stable identity/export rules, provenance, diagnostics, fixture-driven import and deterministic export.
