# WeArchive Product Requirements Document

## 1. Product definition

WeArchive is a local-first personal archive tool for organizing, preserving, searching and exporting the user's own WeChat desktop data.

The product is designed around one core idea: **the archive is the product; collection is only an input stage**. The system should build a durable, normalized, queryable personal message archive that remains useful even when upstream client versions change.

## 2. Target user

Initial target user:

- A technically capable Windows user.
- Uses WeChat desktop regularly.
- Wants to preserve and organize their own conversations over long periods.
- Wants structured exports for notes, project records, personal knowledge management or later analysis.

## 3. Product goals

### G1. Reliable local archive

Import supported local conversation data into a stable archive database with repeatable, idempotent runs.

### G2. Preserve provenance

Every normalized record should retain enough source metadata to answer: where did this item come from, when was it imported, and whether it has changed.

### G3. Incremental operation

After the initial import, routine refreshes should process only new or changed data whenever possible.

### G4. Human-readable export

Users should be able to export selected conversations and time ranges to JSON, Markdown and HTML.

### G5. Long-term maintainability

Source-specific logic must be isolated behind adapters so that upstream client changes do not force a rewrite of the archive, search or export layers.

### G6. Local-first privacy

Conversation content remains local by default. Any future external AI integration must be opt-in, explicit about the payload, and separable from the core archive pipeline.

## 4. Non-goals for initial milestones

The following are explicitly out of scope for M0/M1:

- Sending messages or automating user interactions.
- Remote account access.
- Multi-user hosted service.
- Cloud synchronization as a required dependency.
- Social/CRM features.
- Full graphical desktop application.

## 5. Primary user journeys

### Journey A — First archive

1. Install WeArchive.
2. Run diagnostics.
3. Select an available local account/source.
4. Start an import.
5. Observe import progress and warnings.
6. Open archive statistics.
7. Export a selected conversation.

Success condition: the user obtains a durable local archive without modifying source data.

### Journey B — Incremental refresh

1. Run `wearchive sync`.
2. System identifies previously imported state.
3. Only new/changed records are processed where supported.
4. Archive integrity checks run.
5. User receives a concise summary of additions, updates and warnings.

### Journey C — Search and retrieval

1. Search for a keyword, participant, group or date range.
2. Review matching normalized messages.
3. Export the selected result set or surrounding conversation context.

### Journey D — Long-term project knowledge

1. Select one or more conversations.
2. Export normalized Markdown/JSON.
3. Feed selected material into the user's own note-taking or analysis workflow.

## 6. Functional requirements

### FR-01 Source discovery

The system shall expose available local source profiles through a common adapter interface.

### FR-02 Diagnostics

The system shall provide a `doctor` command that reports platform support, configuration, archive availability and adapter readiness without changing source data.

### FR-03 Conversation discovery

The system shall enumerate supported conversations with stable source identifiers and display metadata where available.

### FR-04 Message ingestion

The system shall ingest messages through an adapter and convert them into a normalized internal message model.

### FR-05 Participant normalization

The system shall preserve stable participant identifiers separately from mutable display names.

### FR-06 Attachment metadata

The system shall support normalized attachment metadata even before full media extraction is implemented.

### FR-07 Archive persistence

The system shall store normalized data in a local SQLite archive with schema migrations.

### FR-08 Idempotency

Re-running an import over the same source range shall not create duplicate logical records.

### FR-09 Import runs

Every ingestion operation shall produce an import-run record containing start/end time, adapter version, source version metadata, counters and warnings.

### FR-10 Checkpoints

The system shall store per-source checkpoints to enable incremental refresh.

### FR-11 Search

The system shall support local full-text search over normalized text content.

### FR-12 Export

The system shall support JSON, Markdown and HTML exports by conversation and time range.

### FR-13 Provenance

Every archived message shall retain source account, conversation, source record ID, source partition/shard if relevant, and import run.

### FR-14 Integrity reporting

The system shall surface partial-read, stale-source, unsupported-type and parsing warnings instead of silently dropping uncertain data.

### FR-15 Media catalog

The system shall maintain a media catalog with source references, logical type, local availability state and normalized archive path.

## 7. Non-functional requirements

### NFR-01 Local-first

Core archive, search and export functions must work offline.

### NFR-02 Read-only source boundary

Initial source adapters must not intentionally modify upstream application data.

### NFR-03 Determinism

Given the same source snapshot and adapter version, normalized output should be reproducible.

### NFR-04 Observability

Long-running operations must emit structured progress, counters and warnings.

### NFR-05 Testability

Archive and export layers must be testable using fixtures without requiring a running WeChat client.

### NFR-06 Version isolation

Client-version-specific assumptions must remain inside source adapters or compatibility modules.

### NFR-07 Privacy

Secrets, raw private datasets and generated personal archives must never be committed to the repository by default.

### NFR-08 Recoverability

Interrupted imports must be resumable or safely repeatable.

## 8. Normalized domain model

Core entities:

- `Account`
- `Conversation`
- `Participant`
- `ConversationParticipant`
- `Message`
- `Attachment`
- `ImportRun`
- `SourceCheckpoint`
- `SourceArtifact`

The normalized model is source-independent. Source-specific fields belong in provenance metadata, not in core business logic.

## 9. CLI surface — target

```text
wearchive doctor
wearchive sources
wearchive conversations
wearchive sync
wearchive stats
wearchive search <query>
wearchive export <conversation> --format markdown
wearchive export <conversation> --format json
wearchive export <conversation> --format html
```

Command names may evolve, but the capabilities above define the intended product surface.

## 10. Milestone acceptance criteria

### M0 — Foundation

- Project installs cleanly on Windows.
- CLI and configuration work.
- Normalized models are defined and tested.
- SQLite archive schema and migrations exist.
- A fixture/mock adapter can complete an end-to-end import.
- JSON and Markdown export work from the archive.

### M1 — First real local source adapter

- Supported account/source can be discovered.
- Conversations and text messages can be imported.
- Multiple source partitions are merged into a stable logical timeline where necessary.
- Incremental refresh works for supported records.
- Import diagnostics clearly report missing/unsupported records.

### M2 — Media and completeness

- Attachment catalog is populated.
- Supported locally available media can be archived.
- Media provenance and integrity are tracked.
- HTML export can render archived media.

### M3 — Retrieval

- Full-text search works.
- Conversation/date/person filters work.
- Archive statistics and timelines are available.

### M4 — Analysis-ready workflows

- LLM-ready export package.
- Optional local analysis hooks.
- Explicit external-provider boundary if remote models are used.

## 11. Product governance

All implementation work must trace to documentation under `docs/`.

Before code implementing a new capability is merged, at least one of the following must already describe the intended behavior:

- PRD requirement
- Architecture section
- Data model document
- Roadmap milestone
- Architecture Decision Record (ADR)

If code changes the intended product behavior, update documentation in the same pull request.
