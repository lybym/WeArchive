# AGENTS.md

This repository is documentation-led.

Before making any non-trivial code change, read the following documents in order:

1. `docs/PRD.md`
2. `docs/RAW_VAULT.md` when the task touches capture, preservation, source snapshots, rebuild, checkpoints, source deletion, decryption persistence or source-version readers
3. `docs/HARNESS.md` when the task touches Harness/Agent access, query/search/context, pagination, collections or MCP
4. `docs/EXPORT_PRD.md` when the task touches export, identities, conversation selection, collections or dataset layout
5. `docs/MESSAGE_SCHEMA.md` when the task touches message parsing, normalization, search, export or source adapters
6. `docs/ARCHITECTURE.md`
7. `docs/DATA_MODEL.md`
8. `docs/ROADMAP.md`
9. `docs/DEVELOPMENT.md`
10. `docs/CLI.md` when the task touches CLI command syntax, options, JSON output or exit codes
11. relevant files under `docs/adr/`, especially ADR 0008 for preservation/query-layer changes

## Stack and product surface

The target product is a Windows command-line application for humans, scripts and agents:

- **C# 14** on **.NET 10 LTS** (`net10.0` for `WeArchive.Core`; `net10.0-windows` for Windows infrastructure, CLI and tests), `win-x64`, self-contained.
- **`gh`-style command CLI** is the only shipped first-class product surface. Do not build a full-screen TUI, conversational shell, embedded LLM or second GUI surface unless docs/ADR explicitly authorize it.
- A future stdio MCP server is an allowed transport target only after `ArchiveQueryService` exists. MCP must wrap the same application services; it must not become a second parser/query engine or a second product data model.
- The shipped artifact is `WeArchive-win-x64.zip` (self-contained, `WeArchive.exe` plus a `wearchive.cmd` shim). There is no installer and no auto-updater; do not reintroduce one without a new ADR/product decision (`docs/adr/0007-cli-self-contained-distribution.md`).
- **xUnit v2 on VSTest** is the test runner.
- Dependencies are centrally pinned; `WeArchive.Core` must remain free of presentation and source-specific implementation concerns.

Layout and dependency direction are normative in `docs/ARCHITECTURE.md`, `docs/adr/0006-cli-first-product-surface.md` and `docs/adr/0008-raw-vault-canonical-query-layers.md`.

## Shipped vs target architecture

Do not confuse architecture documented for the next feature line with capabilities already shipped in 0.2.x.

Current shipped 0.2.x behavior still imports directly from the live WeChat source into the canonical SQLite archive and ships only the documented M0.5 CLI command family.

The target architecture is:

```text
Live WeChat
    │ capture
    ▼
Raw Vault / Source Archive        archival source of truth
    │ read / parse / normalize
    ▼
archive/wearchive.db              rebuildable canonical operational store
    │
    ▼
ArchiveQueryService
    ├─ CLI --json
    └─ future MCP
          │
          ▼
       Harness / Agent

archive/wearchive.db
    └─ JSONL/YAML/JSON export     portable interchange/offline dataset
```

A task/PR must state whether it changes shipped behavior or only advances target architecture. Do not write tests, docs or release notes that claim `capture`, `rebuild`, full query commands, Raw Vault persistence or MCP are already shipped unless the implementation actually exists.

## Mandatory repository rules

- Documentation under `docs/` is the source of truth.
- Do not invent product requirements from existing code.
- Do not change architecture implicitly through implementation.
- Every feature must map to a PRD requirement and Roadmap milestone.
- **Every non-trivial PR must have a pre-existing GitHub Issue created before the PR is opened.** The Issue must define the goal/scope/acceptance criteria, and the PR must reference it with `Closes #N` or `Refs #N` as appropriate.
- **Creating an Issue after a PR has already been opened does not satisfy the Issue-first rule.** If that happens, close the invalid PR, establish the Issue, then open a replacement PR referencing it.
- The PR must stay within the predecessor Issue scope. Materially new product/reliability/architecture scope requires updating/approving the Issue or creating another Issue before implementation expands.
- Source/client-specific behavior must remain behind capture/source-reader/adapter/compatibility boundaries.
- Normalizer output must follow `docs/MESSAGE_SCHEMA.md`.
- Export behavior and physical dataset layout must follow `docs/EXPORT_PRD.md`.
- CLI commands must be thin adapters over application services; command parsing/output must not duplicate capture/import/query/export business rules.
- In `--json` mode, stdout is a machine contract: exactly one final JSON document, with no progress bars, ANSI decoration, prompts or localized explanatory prose.
- Progress/human diagnostics belong on stderr. `--no-input` must never prompt.
- Stable IDs, not mutable names, determine canonical identity and physical export paths.
- Unknown or unsupported canonical source records must be preserved as explicit `unknown`/diagnostic states; do not silently drop them.
- Do not fabricate unavailable identities, URLs, amounts, filenames, timestamps or message content merely to avoid null/unknown states.
- Phase 1 does not imply binary media preservation, OCR, ASR or an LLM-specific derived/chunk dataset.
- Persistent schema changes require `docs/DATA_MODEL.md`, a migration and tests.
- Canonical message-schema changes require `docs/MESSAGE_SCHEMA.md` and schema-version consideration.
- Product/architecture changes require documentation changes in the same PR.
- New major architectural choices require an ADR.
- Preserve the local-first and read-only-live-source principles unless documentation explicitly changes them.
- Do not commit real personal chat data, real Raw Vault generations, real canonical archives, exports, secrets or machine-private datasets to Git.

## Preservation-layer rules — Raw Vault

These rules apply to work that implements or changes `docs/RAW_VAULT.md`.

### Truth and lifecycle

- The Raw Vault / Source Archive is the **archival source of truth** for preserved source evidence.
- `archive/wearchive.db` is the **canonical operational system of record**, optimized for stable semantics and runtime access; it must be rebuildable from the Raw Vault once M1.5 is delivered.
- FTS/search indexes, statistics caches and exports are derived state. They must never become the only copy of source evidence.
- Raw Vault synchronization is preservation, not mirroring. A record disappearing from live WeChat MUST NOT automatically delete an earlier preserved generation or source record.
- Published Raw Vault generations are logically immutable. Physical deduplication is allowed, but later captures must not rewrite history in place.

### Capture before interpretation

- Capture should preserve source data with the smallest practical semantic transformation.
- Do not reduce source evidence to a narrow `RawMessage` model before preservation if doing so drops unknown/source-specific fields.
- Current parser ignorance is not permission to discard source fields. Future readers/parsers must be able to reinterpret preserved evidence.
- Capture completeness and source-version evidence must be explicit and auditable.
- Unsafe ordinary copying of a live SQLite/SQLCipher DB and WAL is not an acceptable consistent-snapshot strategy when it can produce mismatched state.

### Encryption and source-key independence

- WeChat key acquisition, SQLCipher decryption and compatibility/parsing code must remain inside `src/WeArchive.Infrastructure/WeChat` or a documented successor boundary.
- **No WeChat database key may be persisted, logged or exported.**
- Temporary scratch/decryption work products that are not published Raw Vault artifacts remain transient and must be cleaned up according to their operation contract.
- A successfully validated and intentionally published Raw Vault snapshot is different from transient scratch data: it is persistent archive data and must remain readable without reacquiring the original WeChat key.
- If Raw Vault encryption at rest is introduced later, it must use WeArchive/user-owned key management independent of the upstream WeChat database key and requires explicit security/product documentation.

### Capture and ingest checkpoints

Do not conflate source preservation progress with canonical parsing/ingest progress.

```text
Live WeChat
   │ CaptureCheckpoint
   ▼
Raw Vault
   │ IngestCheckpoint
   ▼
wearchive.db
```

- Capture checkpoint: what source evidence has safely entered the Raw Vault.
- Ingest checkpoint: what preserved evidence has successfully entered the canonical archive.
- A parser failure must not force already-preserved data to be recollected from live WeChat.
- Conversation-scoped ingest progress should allow one conversation to advance without coupling unrelated conversations.
- The shipped migration-1 `source_checkpoints` table is an earlier generic design; do not treat its current `(account_id, adapter_name)` uniqueness as a permanent product contract if the documented capture/ingest model requires a migration.

### Rebuild invariant

A target `wearchive rebuild` implementation must:

- build a fresh canonical archive using Raw Vault evidence only;
- not require a running WeChat client;
- not reacquire/read the original WeChat database key;
- not require the original external live-source files to remain present;
- reproduce deterministic stable IDs for unchanged preserved source identities;
- select compatible source readers/parsers from preserved source-version evidence;
- allow FTS and other derived indexes to be regenerated after canonical reconstruction.

Reader/parser implementation versions are metadata. They must not silently create new stable-ID namespaces for the same logical source family.

## Canonical archive and query rules

- `wearchive.db` is the authoritative runtime dataset for canonical query, FTS, timeline/range retrieval, statistics, collections, Harness access and export generation.
- Source-specific behavior must not leak into query/export/CLI/MCP logic.
- `ArchiveQueryService` is the target stable interactive retrieval API. CLI and future MCP are transports over that service.
- Harness/Agent integrations MUST NOT normally query Raw Vault directly.
- Harness/Agent integrations MUST NOT treat ad-hoc SQL against `wearchive.db` as the public product contract.
- Harness/Agent integrations MUST NOT recursively scan JSONL exports for normal interactive retrieval once the corresponding query API exists.
- Query results must expose canonical DTOs/stable IDs and use pagination/cursors for potentially large result sets.
- FTS is derived from canonical data and must be rebuildable from `wearchive.db`.
- JSONL/YAML/JSON remains a first-class portable interchange/offline export format; do not remove it merely because QueryService/MCP exists.

Preferred Harness path:

```text
Harness / Agent
    ↓
CLI --json / future MCP
    ↓
ArchiveQueryService
    ↓
archive/wearchive.db
```

## Collection rule

Collection is the shared reusable scope abstraction for a named set of conversations.

Where the requirement is equivalent to "this stable set of conversations", prefer extending Collection for:

```text
sync
query/search
Harness analysis
export
```

Do not invent parallel concepts such as `watch-list`, `sync-group` or `harness-dataset` unless the requirement cannot be represented by Collection and that difference is documented.

## Reliability: mandatory hard-stop rule

**This is a hard stop, not a suggestion.**

When fixing a review finding would require introducing any of the following:

- a new persistent journal;
- a commit marker or recovery marker;
- a new transaction protocol outside an already documented transaction/publication boundary;
- persistent rollback/recovery state;
- distributed or multi-process coordination;
- a cross-file/package commit protocol;
- a complex persistent state machine whose purpose is crash/restart recovery;

**STOP CODING FIRST.**

Before implementing that mechanism, verify that the reliability property it is trying to satisfy is explicitly required by the current `PRD`, specialized PRD, `RAW_VAULT.md`, GitHub Issue acceptance criteria or Roadmap milestone.

If there is no explicit requirement:

1. do **not** silently upgrade the implementation's reliability level;
2. do **not** treat a reviewer phrase such as “safe”, “atomic”, “durable” or “recoverable” as authorization for a stronger protocol;
3. report the mismatch and identify the currently documented Reliability Level in `docs/DEVELOPMENT.md`;
4. update/approve the requirement or Issue before implementing R3+ recovery machinery.

This rule applies equally to export publication, canonical archive import, Raw Vault generation publication, capture checkpoints and rebuild/swap behavior.

### Current reliability anchors

- **Phase 1 Export = R1**: normal success publishes complete new output; caught cancellation/I/O failures attempt in-process restoration where documented; process crash and OS/power loss are not implicitly guaranteed recovery classes.
- **Current Conversation Import = R2**: a Fatal source-coverage failure must not publish a partial conversation that can be mistaken for a complete one.
- **Raw Vault capture/rebuild reliability** must be implemented only to the explicitly documented failure classes for the relevant Issue/milestone. The architectural requirement for immutable published generations does not, by itself, authorize an undocumented R3+ journal/state machine.
- **R3+** crash-recovery protocols require explicit product authorization.

Read `docs/DEVELOPMENT.md` section “Reliability Levels” before changing file/database publication behavior.

## When docs and code disagree

Treat current docs as authoritative while respecting their explicit shipped-vs-target status.

Do not silently alter implementation direction to preserve provisional code. Either:

1. align code to the documented shipped/target contract, or
2. propose/document a deliberate change to PRD/ADR/Issue before implementing it.

Do not “fix” code to a target command that docs explicitly mark as not yet shipped unless the predecessor Issue is the implementation Issue for that target capability.

## Task workflow

For each non-trivial task:

1. Identify PRD requirement(s).
2. Identify Roadmap milestone and whether the requirement is shipped behavior or target architecture.
3. Read `RAW_VAULT.md` and/or `HARNESS.md` when the task crosses those boundaries.
4. **Create or select the GitHub Issue before opening a PR.** Confirm goal, scope and acceptance criteria.
5. Create the branch / make changes only within that Issue scope.
6. Check `EXPORT_PRD.md` / `MESSAGE_SCHEMA.md` when relevant.
7. Check architecture/data-model/checkpoint implications.
8. Identify the applicable Reliability Level when persistence/files/transactions are touched.
9. Implement the smallest compliant change.
10. Add/update tests, including failure/rebuild/stable-ID semantics where relevant.
11. Update docs when behavior, CLI contract, reliability, preservation or architecture changes.
12. Verify diagnostics/provenance and preserved-source fidelity are not weakened.
13. Verify the implementation did not silently add stronger recovery guarantees than required.
14. Verify Harness-facing code does not bypass QueryService into Raw Vault/source-specific structures.
15. Open the PR only after the predecessor Issue exists; reference it with `Closes #N` / `Refs #N`.

If an agent discovers that a non-trivial PR has already been opened without a predecessor Issue, it must not paper over the violation by creating an Issue afterward and merely editing the PR body. Close that PR and replace it after the Issue exists.

## Current priority

**M0.5 — CLI product-surface migration** is complete. The CLI is the only shipped first-class product surface, and the historical WPF application has been removed.

Shipped 0.2.x command family:

```text
wearchive doctor
wearchive account list
wearchive conversation list
wearchive conversation show <id-or-alias>
wearchive sync --conversation <id-or-alias>
wearchive export --conversation <id-or-alias>
```

The next implementation priorities are defined by `docs/ROADMAP.md`:

1. **P0 — Raw Vault baseline + rebuild (M1.5).**
2. **P0 — conversation-scoped incremental capture/ingest and Collection sync foundation.**
3. **P0 — minimum `ArchiveQueryService` + message/date/conversation/context retrieval + Harness CLI contract.**
4. **P1 — SQLite FTS5 + search/context workflows.**
5. **P1 — Collection as the shared sync/query/export/Harness scope.**
6. **P1 — stdio MCP only after QueryService is stable.**
7. Continue M2 semantic depth in parallel where it does not displace the preservation/query foundations.

Do not add a second query engine, direct Harness-to-Raw-Vault path, JSONL-as-runtime-database workaround, GUI, installer or auto-updater to bypass these priorities.

The foundation must not be weakened:

```text
Raw Vault      = archival source of truth / preserved evidence
wearchive.db   = rebuildable canonical operational store
QueryService   = interactive retrieval API
CLI / MCP      = transport adapters
JSONL/YAML     = portable interchange/offline export
```
