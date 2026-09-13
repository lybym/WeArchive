# WeArchive Product Requirements Document

## 1. Product definition

WeArchive is a local-first personal archive tool for organizing, preserving, searching and exporting the user's own WeChat desktop data.

The product is designed around one core idea: **the archive is the product; collection is only an input stage**. The system should build a durable, normalized, queryable personal message archive that remains useful even when upstream client versions change.

## 2. Target user

Initial target user:

- A technically capable Windows user.
- Uses WeChat desktop regularly.
- Wants to preserve and organize their own conversations over long periods.
- Wants structured exports for LLM/Harness analysis, scripting, search, statistics or downstream automation.

## 3. Product goals

### G1. Reliable local archive

Import supported local conversation data into a stable archive database with repeatable, idempotent runs.

### G2. Preserve provenance

Every normalized record should retain enough source metadata to answer where it came from, when it was imported and whether parsing was complete.

### G3. Incremental operation

After the initial import, routine refreshes should process only new or changed data whenever possible.

### G4. Machine-oriented export

Users shall be able to export selected direct/group chats or named collections as stable, plain-text, machine-readable datasets optimized for LLM/Harness analysis and secondary processing.

Phase 1 export behavior is defined by [EXPORT_PRD.md](EXPORT_PRD.md).

### G5. Stable message semantics

Upstream message types shall be normalized into a stable semantic message contract independent of WeChat database/XML internals.

Canonical message behavior is defined by [MESSAGE_SCHEMA.md](MESSAGE_SCHEMA.md).

### G6. Long-term maintainability

Source-specific logic must be isolated behind adapters so that upstream client changes do not force a rewrite of the archive, search or export layers.

### G7. Local-first privacy

Conversation content remains local by default. Any future external AI integration must be opt-in and separable from the core archive/export pipeline.

## 4. Non-goals for initial milestones

The following are explicitly out of scope for the first phases:

- Sending messages or automating user interactions.
- Remote account access.
- Multi-user hosted service.
- Cloud synchronization as a required dependency.
- Social/CRM features.
- Full graphical desktop application.
- Human-oriented chat rendering as a core requirement.
- Exporting or preserving binary image/audio/video/file payloads as part of the Phase 1 product.
- OCR over images.
- ASR over voice messages.
- LLM-specific derived/chunk datasets.
- Remote crawling of shared webpages as part of canonical export.

## 5. Primary user journeys

### Journey A — First archive

1. Install WeArchive.
2. Run diagnostics.
3. Select an available local account/source.
4. Start an import.
5. Observe progress and warnings.
6. Inspect archive statistics.
7. Export selected conversations.

Success condition: the user obtains a durable local archive without modifying source data.

### Journey B — Incremental refresh

1. Run `wearchive sync`.
2. System identifies previously imported state.
3. Only new/changed records are processed where supported.
4. Archive integrity checks run.
5. User receives a concise summary of additions, updates, unknown records and warnings.

### Journey C — Search and retrieval

1. Search for a keyword, participant, group or date range.
2. Retrieve matching normalized messages.
3. Select relevant conversations/time ranges for downstream processing.

### Journey D — LLM / Harness analysis

1. Select one or more direct/group conversations or a named collection.
2. Export monthly JSONL timelines plus identity/conversation/collection catalogs.
3. Point Harness/LLM workflows at stable conversation folders and date partitions.
4. Resolve stable IDs through `identities.yaml`, `conversations.yaml` and `collections.yaml`.

## 6. Functional requirements

### FR-01 Source discovery

The system shall expose available local source profiles through a common adapter interface.

### FR-02 Diagnostics

The system shall provide a `doctor` command that reports platform support, configuration, archive availability and adapter readiness without changing source data.

### FR-03 Conversation discovery

The system shall enumerate supported conversations with stable source identifiers and display metadata where available.

### FR-04 Message ingestion

The system shall ingest messages through an adapter and convert them into the canonical semantic message model.

### FR-05 Participant normalization

The system shall preserve stable participant identifiers separately from mutable display names.

### FR-06 Non-text event normalization

Image, voice, video, emoji, file and other non-text messages shall remain visible as semantic timeline events even when no binary content is preserved.

For file messages, the original filename shall be retained when locally available.

### FR-07 Archive persistence

The system shall store normalized data in a local SQLite archive with schema migrations.

### FR-08 Idempotency

Re-running an import over the same source range shall not create duplicate logical records.

### FR-09 Import runs

Every ingestion operation shall produce an import-run record containing start/end time, adapter version, source version metadata, counters and warnings.

### FR-10 Checkpoints

The system shall store per-source checkpoints to enable incremental refresh.

### FR-11 Search

The system shall support local full-text search over canonical semantic text and selected structured fields.

### FR-12 Selective export

The system shall support selective machine-oriented export by conversation stable ID, alias or named collection.

Phase 1 export shall use stable conversation directories and monthly JSONL timeline files with separate identity/conversation/collection mapping files.

Detailed behavior is defined by [EXPORT_PRD.md](EXPORT_PRD.md).

### FR-13 Provenance

Every archived message shall retain source account, conversation, source record ID, source type/subtype where available, source partition/shard if relevant and import run.

### FR-14 Integrity reporting

The system shall surface partial-read, stale-source, unknown-type and parsing warnings instead of silently dropping uncertain data.

### FR-15 Link/app-share normalization

When a message contains a link, third-party shared content or mini-program/card content, the system shall preserve all locally obtainable semantic metadata and expose the best locally obtainable original URL when available.

A wrapper/tracking URL must not be mislabeled as a confirmed original URL.

Missing link metadata must not cause the message itself to be dropped.

### FR-16 Identity mapping

The export shall include a user-maintainable stable-ID mapping.

Default display-name rule:

```text
latest remark exists -> display_name = latest remark
no latest remark      -> display_name = ""
```

Nickname is retained as metadata but does not automatically replace a missing remark.

### FR-17 Conversation catalog and collections

The export shall include a conversation catalog and support user-maintainable aliases and reusable collections of direct/group conversations.

### FR-18 Canonical message envelope

Every normalized/exported timeline record shall conform to [MESSAGE_SCHEMA.md](MESSAGE_SCHEMA.md) and expose the conceptual layers:

```text
common envelope + text + payload + reply_to + source
```

The `text` field is the default LLM/search representation. Type-specific detail belongs in `payload`.

### FR-19 Reply/quote relationship

Replies/quotes shall be represented structurally rather than flattened only into text.

When the referenced archived message resolves, retain its canonical message ID. When only a local quote snapshot is available, retain the snapshot without inventing an ID.

### FR-20 Forwarded chat bundles

Where locally parsable, merged/forwarded chat records shall preserve nested textual items, sender labels, times and normalized types.

Nested identities shall be mapped to stable IDs only when resolution is sufficiently reliable.

### FR-21 Unknown messages

Unsupported or unrecognized source records shall be exported/archived as `unknown` semantic events with source type/subtype metadata where available.

Unknown records must be counted in diagnostics and must never be silently discarded.

## 7. Non-functional requirements

### NFR-01 Local-first

Core archive, search and export functions must work offline.

### NFR-02 Read-only source boundary

Initial source adapters must not intentionally modify upstream application data.

### NFR-03 Determinism

Given the same source snapshot, export configuration and exporter version, normalized output should be reproducible except for explicitly documented generated metadata.

### NFR-04 Observability

Long-running operations must emit structured progress, counters and warnings.

### NFR-05 Testability

Archive/export layers must be testable using fixtures without requiring a running WeChat client.

### NFR-06 Version isolation

Client-version-specific assumptions must remain inside source adapters or compatibility modules.

### NFR-07 Privacy

Secrets, raw private datasets and generated personal archives must never be committed to the repository by default.

### NFR-08 Recoverability

Interrupted imports must be resumable or safely repeatable.

### NFR-09 Stable references

Mutable human names shall not determine physical export paths. Harness workflows must be able to refer to stable conversation paths across remark/group-name changes.

### NFR-10 No fabricated semantics

The system must not invent unavailable identities, URLs, amounts, timestamps, filenames or message content merely to avoid null/unknown states.

## 8. Normalized domain model

Core entities:

- `Account`
- `Conversation`
- `Participant`
- `ConversationParticipant`
- `Message`
- `ImportRun`
- `SourceCheckpoint`
- optional source/provenance artifacts

Phase 1 does not require a binary-media archive entity. Media/file semantics are represented through canonical messages and structured payload metadata.

See [DATA_MODEL.md](DATA_MODEL.md).

## 9. CLI surface — target

```text
wearchive doctor
wearchive sources
wearchive conversations
wearchive sync
wearchive stats
wearchive search <query>
wearchive export --conversation <stable-id-or-alias>
wearchive export --collection <collection-name>
```

Exact syntax may evolve, but product behavior must remain consistent with the docs.

## 10. Milestone acceptance criteria

### M0 — Foundation

- Project installs cleanly on Windows.
- CLI and configuration work.
- Canonical domain/message models are defined and tested.
- SQLite archive schema and migrations exist.
- A fixture/mock adapter can complete an end-to-end import.
- Canonical Phase 1 JSONL export works from fixture/archive data.
- Stable conversation paths, identity mappings and conversation catalog behavior are covered by tests.
- Unknown message fixtures remain present and counted.

### M1 — First real local source adapter

- Supported account/source can be discovered.
- Conversations and text messages can be imported.
- Multiple source partitions are merged into one stable logical timeline.
- Incremental refresh works for supported records.
- File/image/voice/video events are normalized without binary preservation.
- Link/app-share/mini-program records preserve obtainable semantic metadata and original URLs.
- Reply relationships are preserved where locally resolvable.
- Latest available remarks populate default identity display names according to the export PRD.
- Missing partitions or unsupported records produce explicit diagnostics.

### M2 — Semantic completeness

- Forwarded bundles, system/revoke events and additional card/special-message types are normalized where feasible.
- Unsupported/partial records are represented and counted rather than silently dropped.
- Conversation and identity metadata refresh safely without changing stable paths.

### M3 — Retrieval

- Full-text search works.
- Conversation/date/person filters work.
- Archive statistics and timelines are available.

### M4 — Harness workflows

- Harness workflows can reliably select conversations/collections and date partitions from canonical export files.
- Core archive/export remains fully functional without any AI provider.
- No separate LLM-derived/chunk dataset is required.

## 11. Product governance

All implementation work must trace to documentation under `docs/`.

Before code implementing a new capability is merged, at least one of the following must already describe the intended behavior:

- `PRD.md`
- specialized PRDs such as `EXPORT_PRD.md`
- `MESSAGE_SCHEMA.md`
- `ARCHITECTURE.md`
- `DATA_MODEL.md`
- `ROADMAP.md`
- an ADR under `docs/adr/`

If code changes intended product behavior or schema semantics, update documentation in the same pull request.
