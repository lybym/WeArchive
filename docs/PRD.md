# WeArchive Product Requirements Document

## 1. Product definition

WeArchive is a local-first personal archive tool for organizing, preserving, searching and exporting the user's own WeChat desktop data.

The product is designed around one core idea: **the archive is the product; collection is only an input stage**. The system should build a durable, normalized, queryable personal message archive that remains useful even when upstream client versions change.

The shipped product form is a **Windows desktop (WPF) application** for WeChat for Windows 4.x. The Python CLI sketch was deleted; see [adr/0003-dotnet-wpf-mvp.md](adr/0003-dotnet-wpf-mvp.md). A command-line surface remains a long-term goal (section 9), not a current deliverable.

## 2. Target user

Initial target user:

- A Windows user of WeChat for Windows 4.x.
- Uses WeChat desktop regularly.
- Wants to preserve and organize their own conversations over long periods.
- Wants structured exports for LLM/Harness analysis, scripting, search, statistics or downstream automation.
- Is willing to run a desktop application; is not required to write code or drive a command line.

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
- A cross-platform or design-led desktop experience. The MVP desktop application exists (section 9), but it is a technical surface for verification and export, not a chat client.
- Human-oriented chat rendering as a core requirement.
- Exporting or preserving binary image/audio/video/file payloads as part of the Phase 1 product.
- OCR over images.
- ASR over voice messages.
- LLM-specific derived/chunk datasets.
- Remote crawling of shared webpages as part of canonical export.

## 5. Primary user journeys

### Journey A — First archive

1. Install WeArchive (or extract the portable build).
2. Launch it and review the environment panel: detected WeChat version, source availability and archive location.
3. Select an available local account/source.
4. Narrow the conversation list with search and the direct/group filter.
5. Start an export for the selected conversation.
6. Observe progress and warnings.
7. Inspect the result summary and the exported folder.

Success condition: the user obtains a durable local archive without modifying source data.

### Journey B — Incremental refresh

1. Export the same conversation again.
2. Re-running the import over the same source range creates no duplicate logical records; existing rows are updated in place.
3. Archive integrity checks run.
4. User receives a concise summary of records, unknown records and warnings.

Only-new-record incremental refresh requires per-source checkpoints (FR-10) and is not yet delivered; the current behaviour is a full, idempotent re-read.

### Journey C — Search and retrieval

1. Search for a keyword, participant, group or date range.
2. Retrieve matching normalized messages.
3. Select relevant conversations/time ranges for downstream processing.

Archive full-text search (FR-11 / M3) is not yet delivered. The MVP provides conversation search and a per-conversation preview of message count and available time range.

### Journey D — LLM / Harness analysis

1. Select one or more direct/group conversations or a named collection.
2. Export monthly JSONL timelines plus identity/conversation/collection catalogs.
3. Point Harness/LLM workflows at stable conversation folders and date partitions.
4. Resolve stable IDs through `identities.yaml`, `conversations.yaml` and `collections.yaml`.

## 6. Functional requirements

### FR-01 Source discovery

The system shall expose available local source profiles through a common adapter interface.

### FR-02 Diagnostics

The system shall report platform support, configuration, archive availability and adapter readiness without changing source data.

In the shipped application this is the environment panel, which is refreshed on demand and needs no separate command.

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

The `source_checkpoints` table and its lifecycle rules are defined by [DATA_MODEL.md](DATA_MODEL.md) section 14, but the MVP importer does **not** yet read or advance checkpoints. This requirement is met at M1 completion, not by the current MVP slice.

### FR-11 Search

The system shall support local full-text search over canonical semantic text and selected structured fields.

Not yet delivered (M3).

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

The user-maintained hook is the `display_name_override` key in `identities.yaml`; when it is non-empty it wins over the generated default. See [EXPORT_PRD.md](EXPORT_PRD.md) section 5.

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

The archive implements `Account`, `Conversation`, `Participant`, `Message`, `ImportRun` and `SourceCheckpoint` as tables. `ConversationParticipant` is modelled today through the conversation's peer/owner participant references and per-conversation participant metadata; a full membership relation is not yet a table.

See [DATA_MODEL.md](DATA_MODEL.md).

## 9. Product surface

### 9.1 Shipped surface — WPF desktop application

The MVP product surface is a single-window Windows desktop application. It provides, in order of the workflow:

- **Environment panel** — detected WeChat version, source availability (with the reason when unavailable), the resolved data source diagnostics, and the current archive path with conversation/message counters.
- **Account selection** — the locally available source profiles, with the last used profile remembered across runs.
- **Conversation list** — direct and group conversations with their display titles, a text search over title and upstream id, and an all/direct/group filter.
- **Conversation preview** — for the selected conversation, its message count and its available time range, read cheaply without loading message bodies.
- **Export** — a chosen output directory (remembered), single-conversation export, live progress with counters, and cancellation. Cancelling is safe: archive writes are idempotent by stable ID.
- **Result summary** — record count, conversation id, unknown and partial counts, the exported time range, and a link that opens the exported conversation folder.
- **Diagnostics list** — the structured diagnostics produced by the export, with their severity and message.
- **Update check** — an on-demand check against the GitHub Releases feed that degrades to a normal message when the feed is absent or the machine is offline.

The application must never contain source-format logic; it drives application services and renders their progress and diagnostics.

### 9.2 Archived CLI surface — target

A command-line surface is not promised by the current milestone and does not exist in this implementation. It remains a documented long-term target, to be delivered only when a milestone accepts it:

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

### M0 — Foundation (met)

- Project builds and runs cleanly on Windows. (met: `dotnet build` and `dotnet test` on the .NET 10 SDK; the shipped surface is a WPF application rather than a CLI/configuration surface.)
- Canonical domain/message models are defined and tested. (met)
- SQLite archive schema and migrations exist. (met: migration 1 with a `schema_migrations` record)
- A fixture/mock adapter can complete an end-to-end import. (met: `FixtureSourceAdapter`)
- Canonical Phase 1 JSONL export works from fixture/archive data. (met: `JsonlDatasetExporter` reads the archive only)
- Stable conversation paths, identity mappings and conversation catalog behavior are covered by tests. (met)
- Unknown message fixtures remain present and counted. (met)

### M1 — First real local source adapter (MVP slice met)

Met:

- A supported account/source can be discovered for a locally signed-in WeChat 4.x installation, together with the client version. (met)
- Conversations and text messages can be imported. (met)
- Multiple source partitions are merged into one stable logical timeline. (met: message databases are merged and ordered by canonical time, upstream order key and stable id)
- File/image/voice/video events are normalized without binary preservation. (met)
- Link/app-share/mini-program records preserve obtainable semantic metadata and original URLs. (met; wrapper/tracking URLs are never mislabeled as original)
- Reply relationships are preserved where locally resolvable. (met: the archive resolves the upstream reply target to a canonical message id)
- Latest available remarks populate default identity display names according to the export PRD. (met)
- Unsupported records produce explicit diagnostics. (met, with counted rollups by code and source type/subtype)

Not yet met:

- Incremental refresh works for supported records. (not met: checkpoints are defined in the schema but the importer always reads the full conversation)
- Missing partitions produce explicit, complete coverage reporting. (partially met: an unreadable or missing partition is diagnosed, but there is no full partition-coverage report)

### M2 — Semantic completeness (partly delivered ahead of the milestone)

- Forwarded bundles, system/revoke events and additional card/special-message types are normalized where feasible. (delivered early: `forward_bundle`, `system`, `revoke`, `location`, `contact_card`, `red_packet` and `transfer` are all normalized and tested)
- Unsupported/partial records are represented and counted rather than silently dropped. (delivered early)
- Conversation and identity metadata refresh safely without changing stable paths. (delivered early: re-export refreshes metadata and preserves user-maintained fields, and stable paths are unaffected)

Remaining M2 work is depth rather than coverage: more reliable special-message semantics (for example red-packet recognition), better unknown-type diagnostics and reply-target resolution improvements.

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
