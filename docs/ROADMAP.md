# WeArchive Roadmap

## Guiding rule

Requirements and architecture lead implementation. Code must not become the de facto specification.

The roadmap is milestone-based rather than date-based. A milestone is complete only when its acceptance criteria are met and its documentation matches the shipped behavior.

## M0 — Product foundation

**Goal:** establish a stable archive core before integrating a real upstream source.

Deliverables:

- Python package and CLI.
- Configuration model.
- Normalized domain models.
- Source adapter protocol.
- Fixture/mock adapter.
- SQLite archive schema and migrations.
- Import-run and diagnostic models.
- Idempotent importer.
- JSON and Markdown export.
- Unit/integration tests using fixtures.

Acceptance criteria:

- A fixture dataset can be imported end to end.
- Re-importing the same fixture creates no logical duplicates.
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
- Text-message import.
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

## M2 — Attachment and media archive

**Goal:** preserve local attachment metadata and supported locally available media.

Deliverables:

- Attachment catalog.
- Image metadata.
- Voice metadata.
- Video/file metadata.
- Media availability state.
- Archive copy/link strategy.
- Digest/integrity tracking.
- HTML exporter with local media rendering.

Acceptance criteria:

- Message-to-attachment relationships survive re-import.
- Missing local media is represented as unavailable rather than silently omitted.
- Media archive paths are stable.
- HTML exports remain portable within an export bundle.

## M3 — Search and retrieval

**Goal:** make the archive genuinely useful as a personal information system.

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

- Search results map back to the canonical archived message.
- Rebuilding the search index is deterministic.
- Large archives remain usable from CLI.

## M4 — Analysis-ready workflows

**Goal:** prepare selected archive material for downstream personal knowledge and AI workflows.

Deliverables:

- Structured analysis export package.
- Conversation chunking with stable message references.
- Optional local summarization interface.
- External-provider boundary with explicit opt-in.
- Redaction/selective-export hooks.

Acceptance criteria:

- Users can see exactly what data is selected before external processing.
- Core archive remains fully functional without any AI provider.
- Analysis outputs retain references to source archived messages.

## M5 — Product experience

**Goal:** improve usability after the underlying archive model is stable.

Candidate work:

- Local web UI or desktop UI.
- Browse conversations.
- Search UI.
- Import history/health dashboard.
- Export wizard.
- Scheduled local sync.

A GUI should not become a prerequisite for the archive engine.

## Deferred / exploratory

These items are intentionally not committed to a milestone yet:

- macOS support.
- Linux support.
- Multi-machine archive reconciliation.
- Encrypted archive-at-rest option.
- Plugin system for additional chat sources.
- Direct integration with note systems.

## Release discipline

Suggested version mapping:

- `0.1.x` — M0 foundation
- `0.2.x` — M1 real source adapter
- `0.3.x` — M2 media
- `0.4.x` — M3 search
- `0.5.x` — M4 analysis workflows
- `1.0.0` — stable archive schema and supported upgrade path

## Current priority

The current priority is **M0 before M1**.

Do not optimize upstream compatibility before the normalized archive, provenance, diagnostics and fixture-based tests are stable. This prevents source-specific implementation details from defining the entire product architecture.
