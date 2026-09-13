# WeArchive Roadmap

## Guiding rule

Requirements and architecture lead implementation. Code must not become the de facto specification.

The roadmap is milestone-based rather than date-based. A milestone is complete only when its acceptance criteria are met and its documentation matches the shipped behavior.

## M0 — Product foundation

**Goal:** establish a stable archive and export contract before integrating a real upstream source.

Deliverables:

- Python package and CLI.
- Configuration model.
- Normalized domain models.
- Canonical message envelope and message-type semantics.
- Source adapter protocol.
- Fixture/mock adapter.
- SQLite archive schema and migrations.
- Import-run and diagnostic models.
- Idempotent importer.
- Stable-ID identity/conversation catalogs.
- Monthly JSONL machine export.
- Unit/integration tests using fixtures.

Acceptance criteria:

- A fixture dataset can be imported end to end.
- Re-importing the same fixture creates no logical duplicates.
- Every fixture message follows `docs/MESSAGE_SCHEMA.md`.
- Unknown message types are retained and counted.
- Stable conversation paths do not depend on mutable names.
- Selective export by conversation works.
- Export results are deterministic.
- Import provenance can be traced from archive row to source fixture.
- Interrupted imports are safe to retry.

## M1 — Windows local-source adapter

**Goal:** support the first real Windows WeChat 4.x local data workflow while preserving the generic architecture.

Deliverables:

- Windows source-profile discovery.
- Source/client version reporting.
- Conversation enumeration.
- Participant/contact mapping.
- Latest-remark identity mapping.
- Text-message import.
- Non-text event normalization.
- File-name extraction for file messages where available.
- Link/app-share/mini-program metadata normalization.
- Best locally obtainable original URL extraction.
- Reply/quote relationship normalization.
- Source partition/shard abstraction.
- Timeline merge and ordering.
- Completeness/freshness diagnostics.
- Incremental checkpoints.

Acceptance criteria:

- At least one locally logged-in account can be represented as a source profile.
- Direct/group conversations import into the normalized schema.
- Messages from multiple underlying partitions appear in one logical ordered timeline.
- A second sync imports only new/changed records where supported.
- Missing partitions or unsupported record types produce explicit warnings.
- File/image/voice/video events can be represented without binary export.
- Links and app-share records preserve locally obtainable titles/descriptions/original URLs.
- Latest available remarks populate the default identity display name, while missing remarks remain blank.

## M2 — Message semantics and completeness

**Goal:** improve semantic coverage without changing the canonical export contract.

Deliverables:

- Forwarded chat bundle normalization.
- System event normalization.
- Revoke-event normalization.
- Location/contact-card normalization.
- Red-packet/transfer normalization when semantics are reliably understood.
- Better unknown-type diagnostics.
- Reply-target resolution improvements.
- Conversation/identity metadata refresh and merge rules.

Acceptance criteria:

- Important non-text/source-card events remain visible in machine exports.
- Forwarded bundles retain available nested textual content.
- Unknown records are never silently discarded.
- Metadata refresh does not change stable paths or stable IDs.
- Re-export remains deterministic.

## M3 — Search and retrieval

**Goal:** make the archive efficient for targeted machine analysis.

Deliverables:

- SQLite FTS index.
- Keyword search.
- Conversation filters.
- Participant filters.
- Date-range filters.
- Context-window retrieval.
- Archive statistics.
- Conversation activity timeline.

Acceptance criteria:

- Search results map back to canonical archived messages.
- Rebuilding the search index is deterministic.
- Large archives remain usable from CLI.
- Search indexes can be rebuilt entirely from canonical archive data.

## M4 — Harness-oriented workflows

**Goal:** make repeated LLM/Harness analysis easy without introducing a separate derived-data layer.

Deliverables:

- Named collections for recurring analysis scopes.
- Selective export by collection and time range.
- Manifest completeness/counter reporting.
- Stable prompt/reference conventions for exported folders.
- Optional redaction/selective-field controls.
- Optional local helper commands for locating relevant JSONL partitions.

Acceptance criteria:

- A Harness can determine what files to load from manifest/catalog files.
- Users can repeatedly refer to stable conversation IDs/aliases/collections.
- Core archive/export remains fully functional without any AI provider.
- No LLM-specific chunk files are required.

## M5 — Product experience

**Goal:** improve usability after the archive/message/export contracts are stable.

Candidate work:

- Local web UI or desktop UI for configuration/health only.
- Browse conversation metadata.
- Search UI.
- Import history/health dashboard.
- Export-selection wizard.
- Scheduled local sync.

A GUI is not a prerequisite for the archive engine and human-oriented chat rendering is not a core goal.

## Deferred / exploratory

These items are intentionally not committed to a milestone:

- macOS support.
- Linux support.
- Multi-machine archive reconciliation.
- Encrypted archive-at-rest option.
- Plugin system for additional chat sources.
- Direct integration with note systems.
- Binary media preservation.
- OCR/ASR pipelines.
- Remote crawling of linked webpages.
- LLM-specific derived/chunk datasets.

## Release discipline

Suggested version mapping:

- `0.1.x` — M0 foundation
- `0.2.x` — M1 real source adapter
- `0.3.x` — M2 semantic completeness
- `0.4.x` — M3 search
- `0.5.x` — M4 Harness workflows
- `1.0.0` — stable archive/message/export schemas and supported upgrade path

## Current priority

The current priority is **M0 before M1**.

Do not optimize upstream compatibility before the canonical archive, message schema, export contract, provenance, diagnostics and fixture-based tests are stable.
