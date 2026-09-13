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

Every normalized record should retain enough source metadata to answer: where did this item come from, when was it imported, and whether it has changed.

### G3. Incremental operation

After the initial import, routine refreshes should process only new or changed data whenever possible.

### G4. Machine-oriented export

Users should be able to export selected direct chats and group chats as stable, plain-text, machine-readable datasets optimized for LLM/Harness analysis and secondary processing.

Phase 1 export behavior is defined normatively by [EXPORT_PRD.md](EXPORT_PRD.md).

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
- Human-oriented chat rendering as a core requirement.
- Exporting binary image/audio/video/file payloads as part of the Phase 1 canonical export.
- LLM-specific derived/chunk files in Phase 1.

## 5. Primary user journeys

### Journey A — First archive

1. Install WeArchive.
2. Run diagnostics.
3. Select an available local account/source.
4. Start an import.
5. Observe import progress and warnings.
6. Open archive statistics.
7. Export selected conversations.

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
3. Select relevant conversations and time ranges for downstream processing.

### Journey D — LLM / Harness analysis

1. Select one or more direct/group conversations or a named collection.
2. Export stable-ID-based JSONL timelines plus identity/conversation mapping files.
3. Point Harness/LLM workflows at the required conversation folders and date partitions.
4. Use `identities.yaml`, `conversations.yaml` and `collections.yaml` to resolve stable IDs into user-maintained semantic names.

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

### FR-06 Attachment/event metadata

The system shall normalize non-text events such as image, voice, video and file messages even when binary payloads are intentionally not exported.

For file messages, the original file name shall be retained when locally available.

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

The system shall support selective machine-oriented export by conversation, alias or collection.

Phase 1 export shall use stable conversation directories and monthly JSONL timeline files, with separate identity/conversation/collection mapping files.

The detailed normative behavior is defined in [EXPORT_PRD.md](EXPORT_PRD.md).

### FR-13 Provenance

Every archived message shall retain source account, conversation, source record ID, source partition/shard if relevant, and import run.

### FR-14 Integrity reporting

The system shall surface partial-read, stale-source, unsupported-type and parsing warnings instead of silently dropping uncertain data.

### FR-15 Link/app-share normalization

When a message contains a link or forwarded/shared application content, the system shall preserve all locally obtainable semantic metadata and expose the best locally obtainable original URL when available.

Missing link metadata must not cause the message itself to be dropped.

### FR-16 Identity mapping

The export shall include a user-maintainable stable-ID mapping. Default display names shall use the latest available remark; when no remark exists, the default display name remains blank rather than falling back automatically to nickname.

### FR-17 Conversation catalog and collections

The export shall include a conversation catalog and support user-maintainable aliases and reusable collections of direct/group conversations.

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

Archive and export layers must be testable using fixtures without requiring a running WeChat client.

### NFR-06 Version isolation

Client-version-specific assumptions must remain inside source adapters or compatibility modules.

### NFR-07 Privacy

Secrets, raw private datasets and generated personal archives must never be committed to the repository by default.

### NFR-08 Recoverability

Interrupted imports must be resumable or safely repeatable.

### NFR-09 Stable references

Mutable human names shall not determine physical export paths. LLM/Harness workflows must be able to refer to stable conversation paths across remark/group-name changes.

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
wearchive export --conversation <stable-id-or-alias>
wearchive export --collection <collection-name>
```

The exact CLI syntax may evolve, but Phase 1 export behavior must remain consistent with `docs/EXPORT_PRD.md`.

## 10. Milestone acceptance criteria

### M0 — Foundation

- Project installs cleanly on Windows.
- CLI and configuration work.
- Normalized models are defined and tested.
- SQLite archive schema and migrations exist.
- A fixture/mock adapter can complete an end-to-end import.
- Phase 1 JSONL export works from fixture/archive data.
- Stable conversation paths, identity mappings and conversation catalog behavior are covered by tests.

### M1 — First real local source adapter

- Supported account/source can be discovered.
- Conversations and text messages can be imported.
- Multiple source partitions are merged into a stable logical timeline where necessary.
- Incremental refresh works for supported records.
- Import diagnostics clearly report missing/unsupported records.
- File/image/voice/video events are normalized without requiring binary export.
- Link and app-share messages preserve obtainable titles/descriptions/original URLs.
- Latest available remarks populate default identity display names according to the export PRD.

### M2 — Completeness and message semantics

- Additional message/card/system-event types are normalized.
- Quote/reply relationships are preserved when resolvable.
- Unsupported/partial records are represented and counted rather than silently dropped.
- Conversation and identity metadata refresh safely without changing stable paths.

### M3 — Retrieval

- Full-text search works.
- Conversation/date/person filters work.
- Archive statistics and timelines are available.
- Optional large-scale machine export formats may be evaluated without replacing the Phase 1 canonical JSONL contract.

### M4 — Analysis workflows

- LLM/Harness workflows can reliably select conversations/collections and date partitions from canonical export files.
- Optional local analysis hooks may be introduced.
- Any external-provider boundary remains explicit and opt-in.

## 11. Product governance

All implementation work must trace to documentation under `docs/`.

Before code implementing a new capability is merged, at least one of the following must already describe the intended behavior:

- PRD requirement
- Specialized PRD such as `EXPORT_PRD.md`
- Architecture section
- Data model document
- Roadmap milestone
- Architecture Decision Record (ADR)

If code changes the intended product behavior, update documentation in the same pull request.
