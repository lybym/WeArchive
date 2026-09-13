# WeArchive Roadmap

## Guiding rule

Requirements and architecture lead implementation. Code must not become the de facto specification.

The roadmap is milestone-based rather than date-based. A milestone is complete only when its acceptance criteria are met and its documentation matches the shipped behavior.

## M0 — Product foundation (complete)

**Goal:** establish a stable archive and export contract before integrating a real upstream source.

Deliverables:

- .NET solution and WPF application shell.
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

Acceptance criteria — all met:

- A fixture dataset can be imported end to end.
- Re-importing the same fixture creates no logical duplicates.
- Every fixture message follows `docs/MESSAGE_SCHEMA.md`.
- Unknown message types are retained and counted.
- Stable conversation paths do not depend on mutable names.
- Selective export by conversation works.
- Export results are deterministic.
- Import provenance can be traced from archive row to source fixture.
- Interrupted imports are safe to retry.

The original deliverable list named a Python package and CLI. That skeleton was deleted in
favour of the C#/.NET desktop implementation; see [adr/0003-dotnet-wpf-mvp.md](adr/0003-dotnet-wpf-mvp.md).
The foundation it was meant to establish is unchanged and is what shipped.

## M1 — Windows local-source adapter (MVP slice delivered; milestone not complete)

**Goal:** support the first real Windows WeChat 4.x local data workflow while preserving the generic architecture.

### Delivered in this MVP

- Windows source-profile discovery, including the data root, account directories, account login names and the installed client version.
- Source/client version reporting.
- Conversation enumeration for direct, group, official and system conversations.
- Participant/contact mapping.
- Latest-remark identity mapping.
- Text-message import.
- Non-text event normalization for image, voice, video, emoji, file and location.
- File-name extraction for file messages where available.
- Link/app-share/mini-program metadata normalization.
- Best locally obtainable original URL extraction, with wrapper/tracking URLs never relabelled as originals.
- Reply/quote relationship normalization, including resolution of the upstream target to a canonical archive message id.
- Forwarded-bundle, system and revoke event normalization.
- Source partition/shard abstraction across the message databases.
- Multi-shard timeline merge and canonical ordering.
- Completeness diagnostics and unknown accounting, rolled up by code and source type/subtype.
- Read-only access to encrypted SQLCipher 4 databases, with keys recovered from the running client and cryptographically verified.
- Reliable `win-x64` publish plus portable ZIP and Velopack installer packaging.

### Still missing for M1

- **Incremental checkpoints.** The `source_checkpoints` table and its rules exist, but the importer neither reads nor advances them; every import re-reads the whole conversation and relies on content-hash idempotency.
- **Full partition-coverage reporting.** A missing or unreadable partition produces a diagnostic, but there is no report of which partitions exist, which were read and which were not.

### Acceptance criteria

- At least one locally logged-in account can be represented as a source profile. (met)
- Direct/group conversations import into the normalized schema. (met)
- Messages from multiple underlying partitions appear in one logical ordered timeline. (met)
- A second sync imports only new/changed records where supported. (not met — see above)
- Missing partitions or unsupported record types produce explicit warnings. (met for unsupported records; partial for partition coverage)
- File/image/voice/video events can be represented without binary export. (met)
- Links and app-share records preserve locally obtainable titles/descriptions/original URLs. (met)
- Latest available remarks populate the default identity display name, while missing remarks remain blank. (met)

## M2 — Message semantics and completeness

**Goal:** improve semantic coverage without changing the canonical export contract.

Deliverables:

- Forwarded chat bundle normalization. (delivered early)
- System event normalization. (delivered early)
- Revoke-event normalization. (delivered early)
- Location/contact-card normalization. (delivered early)
- Red-packet/transfer normalization when semantics are reliably understood. (partly delivered: modelled and tested, but a red packet is only recognised when the local record explicitly identifies one)
- Better unknown-type diagnostics. (partly delivered: counted rollups by code and type/subtype exist)
- Reply-target resolution improvements. (partly delivered)
- Conversation/identity metadata refresh and merge rules. (delivered early)

Acceptance criteria:

- Important non-text/source-card events remain visible in machine exports.
- Forwarded bundles retain available nested textual content.
- Unknown records are never silently discarded.
- Metadata refresh does not change stable paths or stable IDs.
- Re-export remains deterministic.

## M3 — Search and retrieval

## M3 — Search and retrieval

**Goal:** make the archive efficient for targeted machine analysis.

Deliverables:

- SQLite FTS index.
- Keyword search.
- Conversation filters. (delivered early, over the source list rather than the archive: title/id search plus a direct/group filter)
- Participant filters.
- Date-range filters.
- Context-window retrieval.
- Archive statistics. (partly delivered: conversation and message counts)
- Conversation activity timeline.

Acceptance criteria:

- Search results map back to canonical archived messages.
- Rebuilding the search index is deterministic.
- Large archives remain usable from the application.
- Search indexes can be rebuilt entirely from canonical archive data.

## M4 — Harness-oriented workflows

**Goal:** make repeated LLM/Harness analysis easy without introducing a separate derived-data layer.

Deliverables:

- Named collections for recurring analysis scopes. (the file format ships; selection does not)
- Selective export by collection and time range.
- Manifest completeness/counter reporting. (partly delivered: counts and per-file inventory ship)
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

- Richer desktop UI for configuration/health.
- Browse conversation metadata. (partly delivered: the conversation list and preview ship)
- Search UI.
- Import history/health dashboard.
- Export-selection wizard, including multi-conversation and collection export.
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

## Packaging and release

Delivered alongside the MVP:

- Self-contained `win-x64` publish (`dotnet publish -c Release -r win-x64 --self-contained true`) producing `WeArchive.exe`.
- A portable ZIP and a Velopack `Setup.exe` (`vpk pack -u WeArchive -v <version> -p <publishDir> -e WeArchive.exe`).
- GitHub Releases as the update feed, with an in-app "check for updates" that degrades to a normal message when the feed is absent or the machine is offline.
- CI that restores, builds and tests on push and pull request, and a release workflow triggered by a `v*` tag or manual dispatch.

Not done: code signing. Releases are unsigned for the MVP, so SmartScreen may warn on first
run; the packaging scripts keep an optional certificate hook so Authenticode can be added
without changing the artifact layout.

See [adr/0004-distribution-velopack.md](adr/0004-distribution-velopack.md).

## Release discipline

Version mapping:

- `0.1.x` — M0 foundation **and the delivered MVP slice of M1** (current line)
- `0.2.x` — M1 complete (incremental checkpoints, partition-coverage reporting)
- `0.3.x` — M2 semantic completeness
- `0.4.x` — M3 search
- `0.5.x` — M4 Harness workflows
- `1.0.0` — stable archive/message/export schemas and supported upgrade path

## Current priority

The current priority is to **finish M1 on top of the delivered foundation**: incremental
checkpoints and full partition-coverage reporting, then the remaining M2 semantic depth.

The foundation that once gated M1 now exists (canonical archive, message schema, export
contract, provenance, diagnostics and fixture-based tests) and must not be weakened while M1
is completed. In parallel, documentation must keep matching shipped behaviour: this roadmap is
the record of what a milestone actually delivered, including partial deliveries.
