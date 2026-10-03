# AGENTS.md

This repository is documentation-led.

Before making any non-trivial code change, read the following documents in order:

1. `docs/PRD.md`
2. `docs/EXPORT_PRD.md` when the task touches export, identities, conversation selection, collections or dataset layout
3. `docs/MESSAGE_SCHEMA.md` when the task touches message parsing, normalization, search, export or source adapters
4. `docs/ARCHITECTURE.md`
5. `docs/DATA_MODEL.md`
6. `docs/ROADMAP.md`
7. `docs/DEVELOPMENT.md`
8. `docs/CLI.md` when the task touches CLI command syntax, options, JSON output or exit codes
9. relevant files under `docs/adr/`

## Stack

The target product is a Windows command-line application for humans, scripts and agents:

- **C# 14** on **.NET 10 LTS** (`net10.0` for `WeArchive.Core`; `net10.0-windows` for Windows infrastructure, CLI and tests), `win-x64`, self-contained.
- **`gh`-style command CLI** is the primary product surface. Do not build a full-screen TUI, conversational shell, embedded LLM or second GUI surface unless docs/ADR explicitly authorize it.
- The historical WPF project (`src/WeArchive.App`) was removed by Issue #9; the CLI is the only product surface, and no second presentation surface may be added without a new product decision.
- **xUnit v2 on VSTest** is the test runner.
- Dependencies are centrally pinned; `WeArchive.Core` must remain free of presentation and source-specific implementation concerns.

Layout and dependency direction are normative in `docs/ARCHITECTURE.md` and `docs/adr/0006-cli-first-product-surface.md`.

## Mandatory rules

- Documentation under `docs/` is the source of truth.
- Do not invent product requirements from existing code.
- Do not change architecture implicitly through implementation.
- Every feature must map to a PRD requirement and Roadmap milestone.
- **Every non-trivial PR must have a pre-existing GitHub Issue created before the PR is opened.** The Issue must define the goal/scope/acceptance criteria, and the PR must reference it with `Closes #N` or `Refs #N` as appropriate.
- **Creating an Issue after a PR has already been opened does not satisfy the Issue-first rule.** If that happens, close the invalid PR, establish the Issue, then open a replacement PR referencing it.
- **A Release PR must not introduce product feature code.** Feature PRs must be merged to `main` first; `release/vX.Y.Z` is branched from `main` and its Release PR only converges version numbers, release notes and packaging metadata (see `docs/DEVELOPMENT.md` §6.1). A merged release branch is closed history and is never the next development baseline.
- The PR must stay within the predecessor Issue scope. Materially new product/reliability/architecture scope requires updating/approving the Issue or creating another Issue before implementation expands.
- Source/client-specific behavior must remain behind the adapter boundary.
- Normalizer output must follow `docs/MESSAGE_SCHEMA.md`.
- Export behavior and physical dataset layout must follow `docs/EXPORT_PRD.md`.
- Export, search and archive layers must depend on normalized domain models, not source-specific structures.
- CLI commands must be thin adapters over application services; command parsing/output must not duplicate archive/import/export business rules.
- In `--json` mode, stdout is a machine contract: exactly one final JSON document, with no progress bars, ANSI decoration, prompts or localized explanatory prose.
- Progress/human diagnostics belong on stderr. `--no-input` must never prompt.
- Stable IDs, not mutable names, determine canonical identity and physical export paths.
- Unknown or unsupported source records must be preserved as explicit `unknown`/diagnostic states; do not silently drop them.
- Do not fabricate unavailable identities, URLs, amounts, filenames, timestamps or message content merely to avoid null/unknown states.
- Phase 1 does not require binary media preservation, OCR, ASR or an LLM-specific derived/chunk dataset.
- Persistent schema changes require `docs/DATA_MODEL.md`, a migration and tests.
- Canonical message-schema changes require `docs/MESSAGE_SCHEMA.md` and schema-version consideration.
- Product/architecture changes require documentation changes in the same PR.
- New major architectural choices require an ADR.
- Preserve the local-first and read-only-source principles unless documentation explicitly changes them.
- Do not commit real personal chat data, real archives, exports, secrets or machine-private datasets.
- WeChat key acquisition, SQLCipher decryption and compatibility/parsing code must stay inside `src/WeArchive.Infrastructure/WeChat`.
- No database key may be persisted, logged or exported. Decrypted source material must remain transient according to the source-adapter contract.

## Reliability: mandatory hard-stop rule

**This is a hard stop, not a suggestion.**

When fixing a review finding would require introducing any of the following:

- a new persistent journal;
- a commit marker or recovery marker;
- a new transaction protocol outside the already documented SQLite transaction boundary;
- persistent rollback/recovery state;
- distributed or multi-process coordination;
- a cross-file/package commit protocol;
- a complex persistent state machine whose purpose is crash/restart recovery;

**STOP CODING FIRST.**

Before implementing that mechanism, verify that the reliability property it is trying to satisfy is explicitly required by the current `PRD`, specialized PRD, GitHub Issue acceptance criteria or Roadmap milestone.

If there is no explicit requirement:

1. do **not** silently upgrade the implementation's reliability level;
2. do **not** treat a reviewer phrase such as “safe”, “atomic”, “durable” or “recoverable” as authorization for a stronger protocol;
3. report the mismatch and identify the currently documented Reliability Level in `docs/DEVELOPMENT.md`;
4. ask for / propose a requirement or Issue change before implementing R3+ recovery machinery.

The goal is to prevent review-driven reliability scope creep from turning a bounded feature into an undocumented journal/transaction/state-machine project.

### Current reliability anchors

- **Phase 1 Export = R1**: normal success publishes complete new output; caught cancellation/I/O failures attempt in-process restoration where possible; process crash and OS/power loss are not guaranteed recovery classes. SQLite is the system of record and export can be regenerated.
- **Conversation Import = R2**: a Fatal source-coverage failure must roll back the entire conversation transaction. Do not publish a partial conversation that can be mistaken for a complete one.
- **R3+** crash-recovery protocols require explicit product authorization.

Read `docs/DEVELOPMENT.md` section “Reliability Levels” before changing file/database publication behavior.

## When docs and code disagree

Treat current docs as authoritative.

Do not silently alter implementation direction to preserve provisional code. Either:

1. align code to docs, or
2. propose/document a deliberate change to docs/ADR/Issue before implementing it.

## Task workflow

For each non-trivial task:

1. Identify PRD requirement(s).
2. Identify Roadmap milestone.
3. **Create or select the GitHub Issue before opening a PR.** Confirm goal, scope and acceptance criteria.
4. Create the branch / make changes only within that Issue scope.
5. Check `EXPORT_PRD.md` / `MESSAGE_SCHEMA.md` when relevant.
6. Check architecture/data-model implications.
7. Identify the applicable Reliability Level when persistence/files/transactions are touched.
8. Implement the smallest compliant change.
9. Add/update tests, including failure-semantics tests where relevant.
10. Update docs when behavior, CLI contract, reliability or architecture changes.
11. Verify that diagnostics/provenance are not weakened.
12. Verify the implementation did not silently add stronger recovery guarantees than required.
13. Open the PR only after the predecessor Issue exists; reference it with `Closes #N` / `Refs #N`.

If an agent discovers that a non-trivial PR has already been opened without a predecessor Issue, it must not paper over the violation by creating an Issue afterward and merely editing the PR body. Close that PR and replace it after the Issue exists.

## Current priority

**M0.5 — CLI product-surface migration** is complete. The CLI is the only product surface, and the historical WPF application has been removed.

Required initial command family:

```text
wearchive doctor
wearchive account list
wearchive conversation list
wearchive conversation show <id-or-alias>
wearchive sync --conversation <id-or-alias>
wearchive export --conversation <id-or-alias>
wearchive capture [--account <id>]
```

Do not add TUI/chat/embedded-agent/MCP scope to this migration.

**v0.5.1 is the current Stable baseline** (Release Issue #73, tag `v0.5.1`, Release PR #74). It carries the delivered chain through Stable `v0.4.0` — the `v0.3.0` M1.5 capture slice, the `0.3.1` hardening batch, and the completed M1 Windows local-source adapter (Issues #49/#51) — plus Stable `v0.5.0` (canonical archive-only offline export) and the WeChat multi-shard rotation ingest fix (Issue #71, delivered by PR #72). The upgrade chain was verified in the real environment at `v0.5.1-rc.1` (existing checkpoints reused as no-op, rotated-shard conversations fully read, full 2610/2610 session re-sync with zero duplicates). The post-`v0.3.0` cleanup batch is delivered and closed history, not an active priority:

- **Issue #40 — CI credibility** (closed): Actions artifact retention lowered and the portable-ZIP upload made non-blocking, so `main` does not run red on convenience-artifact failures.
- **Issue #39 — post-`v0.3.0` deferred hardening** (closed): the first batch of `0.3.1` work, including the `capture --account` stable-id defect surfaced by Issue #37.

**M1 — Windows local-source adapter is complete on `main`.** Its release slice was delivered by two non-overlapping capability Issues, both now closed/completed:

1. **Issue #49 — M1a canonical incremental second-sync orchestration**: converged `sync --conversation` on the shared `CaptureService` → Raw Vault → conversation-scoped ingest path already shipped for Collection sync, and owned the Roadmap acceptance criterion *"a second sync imports only new/changed records where supported"*. Delivered by PR #53.
2. **Issue #51 — M1b canonical source-neutral partition/evidence coverage reporting**: added the canonical coverage rollup over the stable sync-result boundary handed to it by Issue #49, and owned the Roadmap acceptance criterion *"canonical source coverage reporting is explicit enough to distinguish complete and incomplete reads"*. The `canonical_coverage` rollup rides on `sync --conversation` (and the shared ingest refusal path), and an incomplete read fails closed as a structured `incomplete_coverage` document. Delivered by PR #54.

```text
#48 docs state alignment → #49 M1a canonical second-sync → #51 M1b canonical coverage reporting → real-environment acceptance PASS → M1 complete (recorded by Issue #57)
```

The two Issues did not own overlapping acceptance criteria: Issue #49 does not define the canonical coverage contract and does not complete M1 alone, and Issue #51 must not re-open the sync orchestration owned by Issue #49. Issue #49's real-environment acceptance for the combined #49 + #51 chain passed on a supported Windows/WeChat 4.x installation, and the milestone completion was recorded afterwards through a separate documentation/governance step (Issue #57). Do not re-open the delivered M1 capability Issues, and do not treat this as marking M1.5 complete.

The delivered M1 code landed on `main` **after** Stable `v0.3.1`, so `v0.3.1` does not contain it. It has since shipped in Stable `v0.4.0`.

The active product **P0 is the bounded M1.5 Raw Vault v2 storage slice** authorized by Issue #77 and ADR 0011. The accepted operating target is at least hourly capture while retaining logical generation history; the shipped vault-format-v1 whole-artifact-per-generation representation does not scale to that cadence.

The mandatory order is documentation-led:

```text
#77 docs / ADR / format / benchmark gate
    ↓
v2 storage engine / dual-format read path / v2 writer / storage verification
    ↓
official-RC hourly real-environment acceptance
    ↓
Stable
```

**Issue #79 MUST NOT begin product-code implementation until #77 is closed with the v2 contract merged to `main`.** Issue #78 snapshot-integrity hardening may run in parallel; it blocks formal v2 capture acceptance and Stable, not storage-engine/read-path investigation.

**M2 — message semantics / semantic depth remains the next semantic product milestone after this bounded storage/correctness slice.** It is deferred, not cancelled or absorbed into M1.5.

Raw Vault encryption-at-rest remains separate deferred M1.5 protection work. The v2 storage slice does not authorize scheduler scope, GC/compaction, destructive historical migration, cross-account deduplication or R3+ recovery machinery. M1.5 remains incomplete.

**Issue #47** (the profile-id selector case-sensitivity asymmetry between `capture` and `conversation`, plus the PR #44 fail-closed test-gap items) is completed/closed post-`v0.3.1` **P2 hardening history**, delivered by PR #52. It is not active product work and does not change the current Issue #77 Raw Vault v2 P0 priority.

With M1 complete, follow `docs/ROADMAP.md`: first finish the explicitly authorized Raw Vault v2 M1.5 storage/correctness slice, then resume **M2 message semantics**, followed by M3 retrieval and remaining M4 Harness workflows. FTS/search, Collection export/query, MCP and a scheduler stay outside the active storage slice; Raw Vault encryption-at-rest remains separate deferred work.

Issue #24 delivers conversation-scoped Raw Vault ingest checkpoints; Issue #25 delivers incremental live-source capture checkpoints and explicit **capture-side** partition-coverage reporting (the canonical coverage rollup over that capture-side reporting is delivered by Issue #51). Issue #26 delivers the M4 Collection sync foundation: one authoritative application-level `collections.yaml` (ADR 0009), `collection list`/`collection show`, and `sync --collection`. Issue #27 delivers the M3a minimum structured retrieval slice: `ArchiveQueryService` plus the `message list` / `context` CLI contract, read-only over the canonical archive with no FTS index and no schema migration. Issue #37 delivers the WeChat source-partition support policy (Required / Supported auxiliary / Known unsupported / Unknown) and the real-environment verification of the capture and Raw-Vault-only rebuild chain. Continue from the completed M1 baseline to M2/M3/M4.

Collection remains the one scope abstraction: do not add a `sync-group`, `watch-list` or `harness-dataset` model, and do not make the derived export-package `collections.yaml` authoritative for product behavior.

The foundation must not be weakened: generic adapter contract, canonical message schema, normalized models, SQLite archive, stable identity/export rules, provenance, diagnostics, fixture-driven import and deterministic JSONL export.
