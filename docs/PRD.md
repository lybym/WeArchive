# WeArchive Product Requirements Document

## 1. Product definition

WeArchive is a local-first personal archive tool for organizing, preserving, searching and exporting the user's own WeChat desktop data.

The product is designed around one core idea: **the archive is the product; collection is only an input stage**. The system builds a durable, normalized, queryable personal message archive that remains useful even when upstream client versions change.

The target product surface is a **Windows `gh`-style command-line application** for WeChat for Windows 4.x. It is designed to be called directly by humans, scripts, Harness workflows and AI agents. The CLI is an explicit command interface, not a conversational shell, TUI or embedded agent runtime. See [adr/0006-cli-first-product-surface.md](adr/0006-cli-first-product-surface.md).

The repository currently contains the historical WPF MVP while the CLI migration is in progress and not yet shipped. That WPF surface is transitional and must not be expanded as a second first-class product surface.

## 2. Target user and caller

Initial users/callers:

- A Windows user of WeChat for Windows 4.x.
- Scripts and local automation that need deterministic access to the user's own archive.
- Harness/LLM workflows and AI agents that need stable machine-readable commands and outputs.
- Technical users who prefer concise commands over a desktop workflow.

The product must not require an agent or AI provider. Humans and automation call the same underlying operations.

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

### G6. Agent- and script-friendly CLI

All automation-relevant product operations shall have deterministic command syntax, stable exit semantics and a machine-readable JSON mode. Agents should call commands, not automate a GUI.

### G7. Long-term maintainability

Source-specific logic must be isolated behind adapters so that upstream client changes do not force a rewrite of the archive, search, export or CLI layers.

### G8. Local-first privacy

Conversation content remains local by default. Any future external AI integration must be opt-in and separable from the core archive/export pipeline.

## 4. Non-goals for initial milestones

The following are explicitly out of scope for the first phases:

- Sending messages or automating WeChat user interactions.
- Remote account access.
- Multi-user hosted service.
- Cloud synchronization as a required dependency.
- Social/CRM features.
- A WPF/WinUI/Avalonia desktop GUI as a first-class product surface.
- A full-screen TUI or interactive terminal menu.
- A Claude Code-style conversational shell or embedded LLM/agent runtime.
- MCP/server mode as part of the current CLI migration.
- Human-oriented chat rendering as a core requirement.
- Exporting or preserving binary image/audio/video/file payloads as part of the Phase 1 product.
- OCR over images.
- ASR over voice messages.
- LLM-specific derived/chunk datasets.
- Remote crawling of shared webpages as part of canonical export.

## 5. Primary user journeys

### Journey A — Diagnose and discover

1. Start WeChat and sign in.
2. Run `wearchive doctor` to inspect platform/source/archive readiness.
3. Run `wearchive account list` when multiple local profiles exist.
4. Run `wearchive conversation list` to obtain stable conversation identifiers and metadata.
5. Automation uses the same commands with `--json --no-input`.

Success condition: the caller can determine readiness and address a conversation without GUI state.

### Journey B — First archive/export

1. Resolve a conversation ID or alias.
2. Run `wearchive sync --conversation <id-or-alias>`.
3. Run `wearchive export --conversation <id-or-alias>`.
4. Observe progress/diagnostics on stderr or consume the final JSON result from stdout.
5. Inspect or process the generated machine-readable dataset.

Success condition: the caller obtains a durable local archive and deterministic export without modifying source data.

### Journey C — Incremental refresh

1. Run sync for the same conversation again.
2. Re-running the import over the same source range creates no duplicate logical records.
3. Archive integrity checks run.
4. The command returns concise counters and diagnostics.

Only-new-record incremental refresh requires per-source checkpoints (FR-10) and is not yet delivered; the current importer performs a full, idempotent re-read.

### Journey D — Search and retrieval

1. Search for a keyword, participant, group or date range.
2. Retrieve matching normalized messages.
3. Select relevant conversations/time ranges for downstream processing.

Archive full-text search (FR-11 / M3) is not yet delivered. It must be exposed through CLI when implemented rather than through a separate GUI-only path.

### Journey E — LLM / Harness analysis

1. Resolve one or more direct/group conversations or a named collection.
2. Export monthly JSONL timelines plus identity/conversation/collection catalogs.
3. Point Harness/LLM workflows at stable conversation folders and date partitions.
4. Resolve stable IDs through `identities.yaml`, `conversations.yaml` and `collections.yaml`.

## 6. Functional requirements

### FR-01 Source discovery

The system shall expose available local source profiles through a common adapter interface and CLI commands.

### FR-02 Diagnostics

The system shall report platform support, configuration, archive availability and adapter readiness without changing source data. `wearchive doctor` is the primary product command for this requirement.

### FR-03 Conversation discovery

The system shall enumerate supported conversations with stable source identifiers and display metadata where available. CLI enumeration must support machine-readable output.

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

The `source_checkpoints` table and its lifecycle rules are defined by [DATA_MODEL.md](DATA_MODEL.md) section 14, but the current importer does **not** yet read or advance checkpoints. This requirement is met at M1 completion.

### FR-11 Search

The system shall support local full-text search over canonical semantic text and selected structured fields.

Not yet delivered (M3).

### FR-12 Selective export

The system shall support selective machine-oriented export by conversation stable ID, alias or named collection.

Phase 1 export shall use stable conversation directories and monthly JSONL timeline files with separate identity/conversation/collection mapping files.

Detailed behavior is defined by [EXPORT_PRD.md](EXPORT_PRD.md).

### FR-13 Provenance

Every archived message shall retain source account, conversation, source record ID, source type/subtype where available, source partition/shard if relevant and import run.

### FR-14 Integrity reporting and import publication

The system shall surface partial-read, stale-source, unknown-type and parsing warnings instead of silently dropping uncertain data.

A **Fatal source-coverage failure must roll back the entire conversation import transaction**. No records from that failed conversation run may become usable archive state. Cancellation is not automatically equivalent to a source-coverage failure; its exact publication behavior follows the documented import reliability level in `DEVELOPMENT.md` and the architecture transaction contract.

### FR-15 Link/app-share normalization

When a message contains a link, third-party shared content or mini-program/card content, the system shall preserve all locally obtainable semantic metadata and expose the best locally obtainable original URL when available.

A wrapper/tracking URL must not be mislabeled as a confirmed original URL. Missing link metadata must not cause the message itself to be dropped.

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

Every normalized/exported timeline record shall conform to [MESSAGE_SCHEMA.md](MESSAGE_SCHEMA.md) and expose:

```text
common envelope + text + payload + reply_to + source
```

### FR-19 Reply/quote relationship

Replies/quotes shall be represented structurally rather than flattened only into text.

### FR-20 Forwarded chat bundles

Where locally parsable, merged/forwarded chat records shall preserve nested textual items, sender labels, times and normalized types.

### FR-21 Unknown messages

Unsupported or unrecognized source records shall be exported/archived as `unknown` semantic events with source type/subtype metadata where available. Unknown records must be counted and never silently discarded.

### FR-22 CLI command contract

The CLI is a first-class product API.

Initial required command family:

```text
wearchive doctor
wearchive account list
wearchive conversation list
wearchive conversation show <id-or-alias>
wearchive sync --conversation <id-or-alias>
wearchive export --conversation <id-or-alias>
```

Every automation-relevant command shall support:

- default concise human-readable output;
- `--json`: one final JSON document on stdout, with no ANSI decoration or explanatory prose;
- progress/human diagnostics on stderr;
- `--quiet`: suppress non-essential progress;
- `--no-input`: never prompt; fail when required input is missing;
- deterministic exit semantics: `0` success, `1` runtime/operation failure, `2` usage/configuration validation failure, `130` user cancellation/interrupt.

Exact JSON schemas are versioned implementation contracts and must be covered by contract tests. A command may return exit `0` with Partial diagnostics only when the corresponding product requirement explicitly permits a valid partial result.

## 7. Non-functional requirements

### NFR-01 Local-first

Core archive, search and export functions must work offline.

### NFR-02 Read-only source boundary

Initial source adapters must not intentionally modify upstream application data.

### NFR-03 Determinism

Given the same source snapshot, export configuration and exporter version, normalized output should be reproducible except for explicitly documented generated metadata.

### NFR-04 Observability

Long-running operations must emit structured progress, counters and warnings. In CLI mode, progress must not corrupt machine-readable stdout.

### NFR-05 Testability

Archive/export layers must be testable using fixtures without requiring a running WeChat client. CLI parsing, stdout/stderr separation, JSON schemas and exit codes must also be testable without GUI automation.

### NFR-06 Version isolation

Client-version-specific assumptions must remain inside source adapters or compatibility modules.

### NFR-07 Privacy

Secrets, raw private datasets and generated personal archives must never be committed to the repository by default.

### NFR-08 Recoverability

Recoverability guarantees must be stated by operation and failure class, not described with unqualified words such as "safe", "durable" or "atomic". The normative reliability levels are defined in `docs/DEVELOPMENT.md`.

### NFR-09 Stable references

Mutable human names shall not determine physical export paths. Harness workflows must be able to refer to stable conversation paths across remark/group-name changes.

### NFR-10 No fabricated semantics

The system must not invent unavailable identities, URLs, amounts, timestamps, filenames or message content merely to avoid null/unknown states.

### NFR-11 Composability

CLI commands must be pipeable and automation-safe. Machine-readable stdout must not contain progress bars, prompts or localization-dependent prose.

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

### 9.1 Target surface — command CLI

The primary product surface is a single executable with `gh`-style subcommands. It must remain thin over application services and must not contain source-format logic.

Human example:

```text
wearchive doctor
wearchive conversation list
wearchive export --conversation g_01fd893a7b21c054
```

Agent/script example:

```text
wearchive doctor --json --no-input
wearchive conversation list --json --no-input
wearchive export --conversation g_01fd893a7b21c054 --json --no-input
```

The CLI is not a TUI and does not host an LLM.

### 9.2 Transitional surface — historical WPF MVP

The repository currently contains the WPF MVP described by superseded ADR 0003. It exists only until the CLI migration reaches the acceptance criteria tracked in GitHub Issues. New product features must target the CLI/application-service path rather than extend WPF-specific behavior.

## 10. Milestone acceptance criteria

### M0 — Archive foundation (met)

- Canonical domain/message models are defined and tested.
- SQLite archive schema and migrations exist.
- A fixture/mock adapter can complete an end-to-end import.
- Canonical Phase 1 JSONL export works from fixture/archive data.
- Stable conversation paths, identity mappings and conversation catalog behavior are covered by tests.
- Unknown message fixtures remain present and counted.

The historical WPF shell was an implementation vehicle, not part of the enduring M0 contract.

### M0.5 — CLI product-surface migration

- `WeArchive.Cli` (or equivalent console entry point) is the primary executable.
- Required FR-22 commands are implemented over existing application services.
- Human and `--json` output modes have contract tests.
- stdout/stderr separation and exit codes are tested.
- `--no-input` never prompts.
- Required sync/export behavior retains the existing archive/export semantics and reliability levels.
- WPF-specific product code is removed after parity; the project does not carry two first-class presentation layers.
- Portable self-contained `win-x64` release is produced and smoke-tested from GitHub Actions/Release artifacts.

### M1 — First real local source adapter (MVP slice met; milestone incomplete)

Met:

- A supported account/source can be discovered for a locally signed-in WeChat 4.x installation, together with the client version.
- Conversations and text messages can be imported.
- Multiple source partitions are merged into one stable logical timeline.
- File/image/voice/video events are normalized without binary preservation.
- Link/app-share/mini-program records preserve obtainable semantic metadata and original URLs.
- Reply relationships are preserved where locally resolvable.
- Latest available remarks populate default identity display names.
- Unsupported records produce explicit diagnostics.

Not yet met:

- Incremental refresh works for supported records.
- Missing partitions produce explicit, complete coverage reporting.

### M2 — Semantic completeness

Remaining work is depth rather than coverage: more reliable special-message semantics, better unknown-type diagnostics and reply-target resolution improvements.

### M3 — Retrieval

- Full-text search works.
- Conversation/date/person filters work.
- Archive statistics and timelines are available.
- Retrieval is exposed through machine-readable CLI commands.

### M4 — Harness workflows

- Harness workflows can reliably select conversations/collections and date partitions from canonical export files and/or CLI JSON results.
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

If code changes intended product behavior, CLI contract, schema semantics or reliability level, update documentation in the same pull request.
