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

**Current Stable baseline: `v0.6.0`** (published 2026-10-14 from main commit
`d25b61655f262de3d13f548d579b6b8e03ee8935`). CI and Release both passed.
The bounded M1.5 **Raw Vault v2 storage/correctness slice is complete**: ADR 0011,
#77–#84, #85/PR #91, real official-RC acceptance #86, acceptance harness #92 and
script repairs #93. The official RC produced 170 executed hourly capture points,
81 successful authoritative verifications, validated 4096-byte/Zstd1 defaults and
offline canonical rebuild. Historical observations and limitations are recorded in
[Issue #86](https://github.com/lybym/WeArchive/issues/86#issuecomment-6159259365).

**V2-only forward support policy (ADR 0012):** all new capture/sync and acceptance
use vault format 2 / manifest 3. The owner explicitly retires dedicated v1-only
and mixed v1/v2 *real-environment* acceptance work; those paths were not exercised
in #86 and MUST NOT be represented as tested. Do not add new v1-only operations.
Do not delete or mutate v1 evidence, silently migrate vaults or remove the
already-shipped legacy reader. Any breaking legacy-reader removal requires a new
approved Issue/compatibility decision. Raw Vault encryption-at-rest is deferred.

**Next product milestone: M2 — message semantics and completeness.**
Sequence: (1) establish a privacy-safe semantic/coverage measurement baseline;
(2) address unknown/special-message classification and explicit diagnostics;
(3) resolve reply/quote target linkages within supported evidence;
(4) validate contact/remark metadata refresh, stable IDs and deterministic
rebuild/export. Scope must be backed by MESSAGE_SCHEMA, test fixtures and bounded
real-world evidence. Do not fabricate missing semantics.

**Then M3b — canonical full-text retrieval**, after existing M3a:
- M3a delivered by Issue #27: `ArchiveQueryService`, `message list`, `context`,
  structured pagination and freshness from independent capture/ingest/canonical sources.
- M3b targets a rebuildable SQLite FTS5 index, Chinese/mixed-language retrieval
  evaluation, `wearchive search` CLI and deterministic canonical provenance.
- Query remains read-only (R0); do not query Raw Vault or walk exported JSONL
  for interactive retrieval.

**Then remaining M4 — Harness/Collection workflows:**
- Issue #26 already delivered authoritative `collections.yaml`,
  `collection list|show` and `sync --collection`.
- The remaining work is Collection-scoped canonical query/search and export,
  selected time ranges, read-only archive status, bounded references and
  machine-readable Harness workflows. Any future MCP is an optional transport,
  not a replacement query stack, and requires its own scope decision.

**Post-v0.6.0 reliability follow-up (complete, Issue #96):** the historical
Partial scenario (#63) is confirmed and guarded by isolated V2-only synthetic
fixtures, and the #86 source-partition write-race failures are quantified in
`docs/CAPTURE_WRITE_RACE_EVIDENCE.md`: fail-closed capture never published a
partial generation, the impact is availability-only, and no product retry/backoff
was added — any bounded retry stays a documented proposal for a future Issue.
Never weaken WAL/snapshot integrity or introduce R3+
recovery machinery without explicit requirements. No 7-day re-acceptance and
no v1-only/mixed-history scenario are required.

Historical foundations are closed: M0.5 CLI-only migration (#9); M1 second-sync
(#49) and canonical coverage (#51); Raw Vault capture/ingest (#22–#25);
v0.5.0 canonical-only offline export (#66); multi-shard fix (#71).
Do not reopen or rebuild these delivered capabilities.

Follow `docs/ROADMAP.md`, `docs/PRD.md`, `docs/HARNESS.md`, `docs/adr/0012-v2-only-forward-operations.md`
and Issue-first governance before creating a feature PR. The CLI remains the only
first-class presentation surface; no implicit TUI, GUI, embedded model or MCP work.
Collection remains one authoritative scope abstraction; do not invent
`watch-list`, `sync-group` or `harness-dataset`.

Do not weaken generic adapters, canonical message semantics, SQLite system of
record, stable IDs, provenance, complete/partial diagnostics, R1 capture,
R2 conversation ingest or deterministic export.
