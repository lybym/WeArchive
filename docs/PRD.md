# WeArchive Product Requirements Document

## 1. Product definition

WeArchive is a local-first personal archive tool for preserving, normalizing, searching and exporting the user's own WeChat desktop data.

The product is built around two complementary truths:

- the **Raw Vault** is the archival source of truth: high-fidelity preserved source evidence that should remain usable even if the live WeChat data later disappears or becomes unreadable;
- `archive/wearchive.db` is the canonical operational system of record: a stable, source-independent, queryable interpretation of that evidence.

The canonical database is intentionally rebuildable from the Raw Vault. Query/search/Harness/export behavior operates on the canonical layer rather than on Raw Vault internals.

The target product surface is a Windows `gh`-style command-line application for WeChat for Windows 4.x. It is designed for humans, scripts, Harness workflows and AI agents. CLI is the only shipped first-class presentation surface. A future MCP server may be added as another transport over shared application/query services; it must not become a second business-logic implementation.

This architecture decision is recorded in [ADR 0008](adr/0008-raw-vault-canonical-query-layers.md). Preservation/rebuild semantics are defined by [RAW_VAULT.md](RAW_VAULT.md); Harness/query rules are defined by [HARNESS.md](HARNESS.md).

## 2. Current versus target status

The 0.2.x implementation already provides:

- live WeChat 4.x discovery/read-only access;
- normalization into canonical SQLite;
- deterministic stable IDs and provenance;
- CLI discovery/sync/export commands;
- JSONL/YAML/JSON machine export.

The following are accepted target requirements but are not yet shipped in 0.2.x:

- the incremental canonical (second-sync) path over the live adapter, and canonical
  partition-coverage rollup;
- FTS/keyword search and a CLI archive-status command (`message list` and `context` ship as the
  M3a structured-retrieval slice; the FTS index and full-text search remain open);
- Collection-scoped query/search and export selection;
- MCP transport.

Delivered since the 0.2.x list was written: incremental Raw Vault capture with explicit partition
coverage (Issue #25), conversation-scoped Raw Vault ingest checkpoints (Issue #24), and the
application-level Collection catalog plus Collection-scoped `sync` foundation (Issue #26), and the
M3a minimum structured retrieval slice: `ArchiveQueryService` with the `message list` / `context`
CLI contract (Issue #27).

Documentation may specify target behavior before implementation, but shipped-status sections must not claim these capabilities until delivered and tested.

## 3. Target users and callers

Initial users/callers:

- Windows users of WeChat for Windows 4.x who want durable personal preservation;
- scripts/local automation needing deterministic archive/query operations;
- Harness/LLM workflows and AI agents needing stable machine-readable retrieval;
- technical users who prefer explicit CLI contracts over GUI automation.

The product must not require an AI provider. Humans and automation use the same underlying application services.

## 4. Product goals

### G1. Durable source preservation

Preserve supported local WeChat source evidence in a versioned Raw Vault before source evolution/deletion can make that evidence unrecoverable.

### G2. Rebuildable canonical archive

Maintain a normalized SQLite database whose stable identities and canonical semantics can be recreated from preserved Raw Vault evidence without accessing live WeChat.

### G3. Preserve provenance and unknown evidence

Canonical records retain provenance, while source fields not understood by the current parser remain preserved in the Raw Vault for future reinterpretation.

### G4. Incremental operation

After initial baseline capture/ingest, routine refreshes should process only new/changed source material and only affected conversations/partitions whenever this can be done safely.

### G5. Stable message semantics

Upstream message types are normalized into a source-independent semantic contract defined by [MESSAGE_SCHEMA.md](MESSAGE_SCHEMA.md).

### G6. Efficient machine retrieval

Interactive retrieval should use canonical SQLite indexes/FTS through `ArchiveQueryService`, not recursive scanning of large JSONL exports.

### G7. Machine-oriented export

Selected conversations/collections/time ranges can be exported as stable portable JSONL/YAML/JSON datasets. Export is derived/interchange state and must be regenerable from the canonical archive.

### G8. Agent- and script-friendly interfaces

Automation-relevant operations have deterministic command syntax, stable exit semantics, pagination where appropriate and machine-readable JSON. Future MCP exposes the same application/query services rather than duplicating logic.

### G9. Long-term maintainability

WeChat-version-specific schemas, cryptography and parsing remain behind source/capture-reader compatibility boundaries. Query/export/Harness callers must not depend on upstream schema details.

### G10. Local-first privacy

Preservation, archive, query and export work locally/offline after required capture. External AI/provider integration is optional and separable.

## 5. Non-goals / separately scoped work

Unless a later requirement explicitly changes scope, the following are not core requirements of the current milestones:

- sending messages or automating WeChat user interactions;
- remote account access or hosted multi-user service;
- cloud synchronization as a required dependency;
- GUI/full-screen TUI/conversational shell as first-class product surfaces;
- embedding an LLM provider inside the core archive pipeline;
- OCR/ASR/visual-derived datasets;
- remote crawling of shared webpages as canonical semantics;
- LLM-specific chunk/vector datasets as the canonical archive;
- automatic preservation of every image/audio/video/file binary merely because Raw Vault exists.

Binary media preservation requires its own Media Vault/product contract.

## 6. Data-layer contract

```text
L0 Live Source
   WeChat
       │ capture
       ▼
L1 Preservation Layer
   Raw Vault
       │ reader/parser/normalizer
       ▼
L2 Semantic Layer
   archive/wearchive.db
       │
       ▼
L3 Access Layer
   ArchiveQueryService -> CLI / future MCP
       │
       ▼
L4 Interchange Layer
   JSONL / YAML / JSON
```

Normal Harness/Agent operations consume L3 backed by L2. Raw Vault is not a general query surface.

## 7. Primary user journeys

### Journey A — Diagnose and discover

1. Run `wearchive doctor`.
2. Discover source accounts/conversations when live source is available.
3. Inspect canonical archive/Raw Vault freshness when those features are implemented.
4. Automation uses the same operations with `--json --no-input`.

Success: caller can determine what source/archive data is available and address conversations using stable IDs.

### Journey B — First preservation and archive

Target behavior:

1. capture supported WeChat source evidence into Raw Vault baseline generation;
2. parse/normalize preserved evidence into canonical SQLite;
3. publish only under documented completeness/reliability rules;
4. optionally export portable datasets.

`wearchive sync` may orchestrate capture + ingest for ordinary usage.

Success: canonical data is queryable and the preserved evidence remains available for future rebuild even if live WeChat later changes.

### Journey C — Incremental refresh

1. determine required live-source capture delta using capture checkpoint/evidence;
2. publish a new immutable Raw Vault generation;
3. determine affected canonical scopes using ingest checkpoints;
4. ingest only new/changed records where supported;
5. advance each successful conversation/scope independently.

Source disappearance must not delete earlier Raw Vault generations or canonical history automatically.

### Journey D — Rebuild after parser/source-access change

1. start from preserved Raw Vault generations;
2. select compatible/new readers for each source format/version;
3. rebuild a fresh canonical SQLite database;
4. rebuild derived FTS/index/statistics state;
5. preserve deterministic stable IDs.

Success: rebuild does not access live WeChat or reacquire its original database key.

### Journey E — Search and retrieval

1. resolve account/conversation/collection to stable IDs;
2. query by keyword/person/date/type through `ArchiveQueryService`;
3. retrieve context windows around selected messages;
4. page through large result sets.

Harness callers should not recursively scan JSONL for this interactive workflow.

### Journey F — Offline/export analysis

1. select conversations/collections/time ranges;
2. export JSONL plus catalogs/manifests;
3. use the dataset for transfer, audit or batch/offline processing.

JSONL remains first-class interchange output without becoming the primary runtime query database.

## 8. Functional requirements

### FR-01 Source discovery

Expose available local source profiles through source/capture adapters and CLI commands.

### FR-02 Diagnostics

Report platform, source, Raw Vault, canonical archive and (when implemented) freshness/coverage status without modifying source data.

### FR-03 Conversation discovery

Enumerate supported conversations with stable source identifiers and source-neutral metadata.

### FR-04 Raw Vault capture

Capture supported source evidence into an independently recoverable, versioned preservation layer.

Capture must not intentionally modify WeChat data. A published generation records source version, artifact identity/checksums and completeness diagnostics.

After a complete baseline, supported partitions may reuse verified preserved evidence when
versioned source fingerprints prove they are unchanged. A capture that cannot establish safe
incremental coverage falls back to a full consistent snapshot. Each generation reports explicit
partition coverage; capture progress advances only with successful publication and remains
independent of canonical ingest progress.

Each source adapter defines an explicit partition-support policy for the observed source/version;
filesystem discovery alone does not define product support. The policy distinguishes Required,
Supported auxiliary, Known unsupported and Unknown/unclassified source partitions. Known
unsupported evidence stays visible in coverage but does not by itself prevent `Complete`; an
unknown/unclassified discovered partition cannot be silently ignored or reported as fully covered.
A `Complete` generation means the adapter's required supported evidence is captured/reused and
verified under that policy, not that every physical database file in the source tree was decryptable.

### FR-05 Immutable preservation

Published Raw Vault generations are logically immutable. Source deletion/absence MUST NOT automatically delete preserved history.

### FR-06 Key-independent recoverability

A successful Raw Vault capture must remain readable without reacquiring the original WeChat database key. WeChat keys are never persisted.

### FR-07 Message ingestion and normalization

Readers/adapters convert preserved/live source records into canonical semantic messages through the normalizer.

### FR-08 Participant normalization

Stable participant identifiers are separate from mutable display names/remarks/nicknames.

### FR-09 Non-text event normalization

Image, voice, video, emoji, file and other non-text messages remain visible as semantic timeline events even when binary payloads are not preserved.

### FR-10 Canonical archive persistence

Store normalized data in local SQLite with migrations. The canonical database is the operational system of record for query/export/application behavior.

### FR-11 Canonical rebuild

A fresh canonical archive can be rebuilt from Raw Vault only, without live WeChat/source-key access.

Stable IDs for unchanged preserved source identities must remain unchanged across rebuild/parser upgrades.

### FR-12 Idempotency

Replay/re-ingest over the same preserved source evidence must not create duplicate logical records.

### FR-13 Capture and import audit

Capture and ingest operations record enough metadata/counters/diagnostics to establish what evidence was acquired and what canonical records were published.

### FR-14 Separate checkpoints

Preservation progress and canonical-ingest progress are independent.

Target model:

- capture checkpoint: live source -> Raw Vault;
- ingest checkpoint: Raw Vault -> canonical archive, scoped to conversation/partition where appropriate.

The shipped migration-1 generic `source_checkpoints` table is an earlier implementation and is not reinterpreted as ingest state. Raw Vault ingestion uses the migration-2 `ingest_checkpoints` table.

### FR-15 Search

Support local FTS over canonical semantic text and selected structured payload fields.

### FR-16 Structured retrieval

Expose message listing/filtering and context-window retrieval by stable conversation/person/date/message identifiers.

Results are paginated/cursor-based for large result sets.

Shipped by Issue #27 (M3a minimum retrieval) through `ArchiveQueryService` and the
`message list` / `context` CLI commands ([`CLI.md`](CLI.md)): listing is bounded and resumed with an
opaque keyset cursor, `since`/`until` bounds are inclusive instants, participant and canonical type
filters use stable canonical semantics, and `context` returns a bounded before/target/after window
around a stable message ID. Keyword search and archive statistics remain M3b/FR-15 work.

### FR-17 ArchiveQueryService

All interactive query/search/context operations are implemented behind a source-independent application service. CLI and future MCP are transports over this service.

Harnesses must not be required to know SQLite table layouts.

Shipped by Issue #27 for structured retrieval, context windows and capture/ingest/canonical
freshness. It reads only the canonical archive: it never opens live WeChat, acquires a key, reads
Raw Vault artifacts or uses exported JSONL as a runtime store, and it returns canonical DTOs and
stable IDs rather than SQLite rows or WeChat structures. Keyword search is not implemented yet, so
FR-17 is not yet complete for search.

### FR-18 Selective export

Support machine-oriented export by conversation stable ID, alias or Collection, with time-range refinement where defined.

Export consumes canonical archive data; it does not parse Raw Vault directly.

Detailed packaging remains defined by [EXPORT_PRD.md](EXPORT_PRD.md).

### FR-19 Provenance

Canonical messages retain source account/conversation/message identity, type/subtype where available, source partition/order and reader/adapter/source-version metadata sufficient for traceability.

### FR-20 Integrity and publication

Fatal source/capture/coverage failures must not be silently converted into complete results. Unknown semantics are retained explicitly rather than dropped. Source discovery follows the same rule: known-unsupported partitions remain explicit diagnostics/coverage, while newly discovered unclassified partitions prevent a false `Complete` verdict until their support semantics are decided.

Reliability guarantees are operation-specific and documented before implementation.

### FR-21 Link/app-share normalization

Preserve all locally obtainable semantic metadata without fabricating original URLs or other unavailable values.

### FR-22 Identity mapping

Exports include user-maintainable stable-ID display mappings. Mutable names do not define identity or physical paths.

### FR-23 Collections

Collection is the single reusable abstraction for a named set of conversations and may be used as a scope for:

```text
sync
query/search
Harness analysis
export
```

Do not introduce overlapping `sync-group`, `watch-list` or `harness-dataset` concepts unless they represent materially different semantics.

Shipped by Issue #26 (P0 Collection sync foundation): one authoritative application-level
`collections.yaml`, `collection list` / `collection show <name>` resolution, and
`sync --collection <name>` as a scope over the shared capture + Raw Vault ingest path. Membership
keys are stable conversation IDs; unknown names, invalid configuration and invalid/duplicate
membership entries are deterministic outcomes. Shipped status per intended use:

```text
sync                 -> shipped in this Issue
query/search         -> follow-up (uses the same Collection catalog)
Harness analysis     -> follow-up (CLI JSON resolution is shipped)
export               -> follow-up (Collection-scoped export selection not yet wired)
```

Ownership and rebuild-survival semantics are recorded in
[ADR 0009](adr/0009-collection-configuration-ownership.md). This does not authorize Collection
keyword/query filtering, an interactive Collection editor, or MCP/scheduler synchronization.

### FR-24 Canonical message envelope

Every canonical/exported timeline event follows [MESSAGE_SCHEMA.md](MESSAGE_SCHEMA.md):

```text
common envelope + text + payload + reply_to + source
```

### FR-25 Reply/quote relationship

Replies/quotes are represented structurally rather than only flattened into text.

### FR-26 Forwarded bundles

Where locally parsable, forwarded/merged chat records preserve nested available sender/time/type/text semantics.

### FR-27 Unknown source records

Unsupported source records remain preserved in Raw Vault. Canonical ingestion emits `unknown` semantic events with available provenance/diagnostics instead of silently discarding them.

### FR-28 CLI process contract

The CLI remains a first-class machine API:

- concise default human output;
- `--json`: exactly one final JSON document on stdout;
- progress/human diagnostics on stderr;
- `--quiet` suppresses non-essential progress;
- `--no-input` never prompts;
- deterministic exit semantics: `0` success, `1` runtime/operation failure, `2` usage/config validation failure, `130` cancellation.

The exact shipped command/JSON contract is versioned in [CLI.md](CLI.md).

### FR-29 Target preservation commands

Planned additive commands include:

```text
wearchive capture
wearchive ingest --account <raw-vault-account-id> [--conversation <source-conversation-id>]
wearchive sync --collection <name>
wearchive rebuild
```

`rebuild` must never implicitly fall back to live WeChat.

Shipped status: `capture`, `ingest`, `rebuild` and `sync --collection <name>` are implemented;
`sync --collection` scopes capture + Raw Vault ingest over the application-level Collection catalog
(Issue #26) and reuses `sync --conversation`'s resolution semantics rather than adding a second
scope abstraction.

### FR-30 Target query commands

Planned query commands include message listing, keyword search and message-context retrieval with stable JSON pagination. Exact syntax becomes normative in `CLI.md` only when implemented.

Shipped status: `wearchive message list` and `wearchive context` are implemented (Issue #27) and
normative in [`CLI.md`](CLI.md), including their `--json` DTOs and exit/error codes. Keyword search
(`wearchive search`) and a CLI archive-status command are not implemented.

### FR-31 Future MCP transport

A future stdio MCP server may expose `ArchiveQueryService` operations. It must not implement independent Raw Vault parsing or a second query engine.

## 9. Non-functional requirements

### NFR-01 Local-first

Preservation, rebuild, canonical query and export work locally; rebuild/query/export do not require network access.

### NFR-02 Read-only upstream boundary

Source capture/access must not intentionally mutate WeChat data.

### NFR-03 Preservation fidelity

Capture minimizes semantic transformation before preservation and does not discard source fields merely because current parsers do not understand them. This fidelity rule applies within the adapter's documented supported evidence contract; it does not imply that every physical database file discovered in a source tree is automatically a supported partition.

### NFR-04 Determinism

Given the same preserved evidence, reader/normalizer version and configuration, canonical rebuild/export should be reproducible except for documented generated metadata.

### NFR-05 Observability

Long-running capture/ingest/rebuild/query/export operations emit structured progress, counters and diagnostics without polluting machine-readable stdout.

### NFR-06 Testability

Capture/rebuild/query layers must be testable with fixtures without a live WeChat client. Parser-upgrade rebuild scenarios must be covered.

### NFR-07 Version isolation

Client-version-specific table/column/type/crypto assumptions remain within capture/reader compatibility modules.

### NFR-08 Privacy

Secrets and private Raw Vault/archive/export content are never committed to the repository by default. WeChat DB keys are never persisted.

### NFR-09 Recoverability

Recoverability guarantees are stated by operation/failure class. Unqualified claims such as "safe" or "atomic" are prohibited.

### NFR-10 Stable references

Parser/reader upgrades and canonical rebuilds do not change deterministic stable IDs solely because implementation versions changed.

### NFR-11 No fabricated semantics

Unavailable identities, URLs, amounts, timestamps, filenames or message content are not invented to avoid null/unknown states.

### NFR-12 Composability

CLI commands remain pipeable and automation-safe; query results are paginated where unbounded output would be unsafe/inefficient.

### NFR-13 Raw Vault isolation

Normal Harness/Agent/query/export workflows do not depend on Raw Vault physical schemas or file layouts.

## 10. Canonical domain model

Canonical entities include:

- `Account`
- `Conversation`
- `Participant`
- `ConversationParticipant` (conceptual; current migration uses narrower relations)
- `Message`
- `ImportRun`
- checkpoint state
- source provenance

Raw Vault generations/manifests belong to the preservation model, not to the canonical `Message` schema.

See [DATA_MODEL.md](DATA_MODEL.md).

## 11. Product/access surfaces

### 11.1 CLI

The CLI remains the shipped first-class product surface and thin transport over application services.

### 11.2 ArchiveQueryService

This is the intended stable internal product API for retrieval/search/context. It shields callers from persistence/schema changes.

### 11.3 Future MCP

MCP is an optional transport over shared services. Its introduction does not change the CLI-first product decision and does not embed an AI provider.

### 11.4 JSONL export

JSONL remains portable interchange/offline data rather than the default interactive agent query surface.

## 12. Milestone acceptance summary

### M0 — Archive foundation (met)

Canonical models, migration-1 SQLite, fixture imports, stable IDs and JSONL export are delivered.

### M0.5 — CLI product-surface migration (met)

CLI-only product surface, JSON contract, stdout/stderr/exit behavior and portable packaging are delivered.

### M1 — Live WeChat source adapter (partial)

Core local-source discovery/parsing exists. Conversation-scoped Raw Vault ingest checkpoints are delivered by Issue #24, and incremental live-source capture with explicit partition-coverage reporting is delivered by Issue #25. The canonical second-sync path and the remaining M1 milestones stay open.

### M1.5 — Preservation/rebuild foundation (partial delivery)

Delivered by Issues #22 and #23: Raw Vault baseline capture, immutable generations/manifests,
source-independent evidence retention, and a validated Raw-Vault-only canonical rebuild with
stable-ID preservation.

Delivered by Issue #25: incremental capture checkpoints, explicit expected/captured/reused/
unavailable/unsupported partition coverage, and an automatic full-snapshot fallback whenever
incremental safety cannot be proven.

Still outstanding: physical cross-generation storage dedup, Raw Vault encryption-at-rest, and
real-environment verification on a supported Windows/WeChat 4.x installation.

### M2 — Semantic completeness

Improve parser depth/unknown handling and use rebuildability to reprocess preserved historical evidence.

### M3 — Query/Harness foundation

- `ArchiveQueryService`;
- message/date/conversation retrieval;
- FTS/search/context;
- Collection-backed scopes;
- `docs/HARNESS.md` contract and CLI JSON query surface.

### M4 — MCP and advanced Harness workflows

Optional MCP over QueryService, richer collection/query/export automation and status/freshness tooling.

Issue #26 delivers the M4 Collection *sync* foundation (authoritative Collection catalog,
`collection list`/`show`, `sync --collection`); Collection-scoped query/search and export selection
remain M3/M4 follow-up scope.

## 13. Product governance

All implementation work must trace to documentation under `docs/`.

Architecture-changing work must update the relevant PRD/architecture/data-model/specialized specification/ADR before or with implementation.

Shipped-status language must remain distinct from accepted target behavior. Documentation-first does not permit documentation to falsely claim unimplemented capabilities are available.
