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
- The historical WPF project may exist only during migration; it is not a second first-class product surface.
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

Current priority is **M0.5 — CLI product-surface migration**.

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

Current priority is **M1.5 — Raw Vault baseline capture** (Issue #22), then M1 incremental checkpoints and full partition-coverage reporting, then continue M2/M3/M4.

The foundation must not be weakened: generic adapter contract, canonical message schema, normalized models, SQLite archive, stable identity/export rules, provenance, diagnostics, fixture-driven import and deterministic JSONL export.
