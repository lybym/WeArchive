# WeArchive Development Governance

## 1. Rule: docs lead code

All product and implementation work must start from documentation under `docs/`.

The repository follows this precedence:

```text
PRD
  ↓
Architecture / Data Model / ADR
  ↓
Roadmap milestone
  ↓
GitHub Issue
  ↓
Branch / implementation / documentation change
  ↓
Pull Request
  ↓
Review / merge
```

Code is not the specification. When behavior changes, the documents that describe it change in the same pull request. When implementation and docs disagree, the discrepancy itself is a required task.

### 1.1 Mandatory Issue-first rule

Every **non-trivial Pull Request** must have a **pre-existing GitHub Issue** that was created before the PR is opened.

This applies to implementation, refactoring, architecture, reliability, packaging, documentation/governance changes and other non-trivial repository changes.

The Issue is the authorization and scope boundary; the PR is the implementation/review vehicle.

Required sequence:

```text
Requirement / problem identified
        ↓
Create or select GitHub Issue
        ↓
Issue has goal, scope and acceptance criteria
        ↓
Create branch / make changes
        ↓
Open PR referencing the Issue
```

Rules:

1. A non-trivial PR **must not be opened before its Issue exists**.
2. Creating an Issue after a PR has already been opened does **not** retroactively satisfy this rule.
3. If a PR was opened without a valid predecessor Issue, close the PR, establish the Issue, then open a replacement PR that references it.
4. Every non-trivial PR body must contain an explicit Issue reference, normally `Closes #N` when the PR fully satisfies it or `Refs #N` when it is only part of the Issue.
5. The PR scope must remain within the Issue scope. If review reveals materially new work, update/approve the Issue or create a new Issue before expanding implementation.
6. Review findings that are ordinary corrections inside the accepted Issue scope do not require a new Issue. New product requirements, reliability levels or architectural scope do.

Trivial exceptions are limited to changes with no meaningful reviewable scope, such as typo-only corrections or mechanical metadata fixes. When uncertain, create an Issue.

## 2. Definition of ready

A development task is ready only when:

- user-facing/system behavior is described in `docs/PRD.md` or another normative doc;
- architecture impact is understood;
- persistent model changes are reflected in `docs/DATA_MODEL.md`;
- the task belongs to a Roadmap milestone;
- a pre-existing GitHub Issue defines goal, scope and acceptance criteria;
- reliability expectations are explicit when the task touches persistence, files, transactions, recovery or publication.

## 3. Stack and toolchain

Target stack:

```text
C# 14 / .NET 10 LTS
  net10.0          WeArchive.Core
  net10.0-windows  WeArchive.Infrastructure, WeArchive.Cli, WeArchive.Tests
  win-x64, self-contained releases
  gh-style command CLI as the primary product surface
  xUnit v2 on the VSTest platform for tests
```

The historical `WeArchive.App` WPF project was removed by Issue #9. There is one first-class presentation layer: `src/WeArchive.Cli`, whose assembly is named `WeArchive` and which produces the shipped `WeArchive.exe`.

The SDK is pinned by `global.json`. Language level, nullable, analyzer level, warnings-as-errors and deterministic builds are centralized in `Directory.Build.props`; package versions are centrally pinned.

```powershell
dotnet restore WeArchive.sln
dotnet build   WeArchive.sln -c Release
dotnet test    WeArchive.sln
```

`TreatWarningsAsErrors` is on.

### 3.1 Test runner decision

xUnit **v2 on VSTest** remains the test platform. xUnit v3 with Microsoft.Testing.Platform was evaluated and rejected because `dotnet test` did not reliably discover this project's tests in the evaluated setup.

## 4. Pull request requirements

Every non-trivial PR must reference its pre-existing Issue and answer:

1. Which GitHub Issue does this implement, and does the PR use `Closes #N` or `Refs #N` appropriately?
2. Which PRD requirement/Roadmap item does it implement?
3. Does it change architecture?
4. Does it change persisted data/schema?
5. Does it change CLI/user-visible behavior?
6. What tests prove the acceptance criteria?
7. What diagnostics/failure modes were added or changed?
8. What Reliability Level applies to file/database/publication behavior?
9. Which documents were updated, and do they describe the shipped behavior?

If behavior changes, docs must change in the same PR.

A reviewer should reject or request correction for a non-trivial PR with no valid predecessor Issue.

## 5. Architecture Decision Records

Use ADRs for decisions that are expensive to reverse or affect multiple modules.

Path:

```text
docs/adr/NNNN-short-title.md
```

Template:

```markdown
# ADR NNNN — Title

Status: proposed | accepted | superseded | rejected
Date: YYYY-MM-DD

## Context
## Decision
## Alternatives considered
## Consequences
## References
```

Current key records include:

- `0001` docs are the source of truth;
- `0002` archive core before real source adapter;
- `0003` historical .NET/WPF MVP decision, presentation portion superseded;
- `0004` historical Velopack distribution decision, superseded;
- `0005` local key acquisition;
- `0006` CLI-first product surface;
- `0007` self-contained portable CLI distribution, supersedes `0004`.

## 6. Branching and change scope

Prefer small, Issue-aligned, milestone-aligned changes. The predecessor Issue must exist before the PR is opened; preferably create the branch from the Issue scope as well.

Examples:

```text
feat/cli-foundation
feat/cli-doctor
feat/cli-sync-export
feat/retire-wpf-cli-packaging
feat/m1-incremental-checkpoints
fix/import-coverage-rollback
docs/update-data-model
```

Avoid mixing unrelated refactors with product changes.

## 7. Testing policy

### Unit tests

For parsing, normalization, identity, CLI argument validation and pure archive functions.

### Fixture integration tests

For complete import/export flows without requiring a live upstream client:

```text
ExportPipelineTests      fixture import -> archive -> JSONL package
SqliteArchiveStoreTests  idempotency, ordering, provenance, transaction behavior
SourceCoverageTests      fatal coverage failure -> whole conversation rollback
```

### CLI contract tests

The CLI is a product API. Tests must cover at least:

- command/option parsing;
- `--json` emits exactly one JSON document to stdout;
- `--json` emits exactly one JSON document on the help path (`--help --json`, bare `wearchive --json`) and on every failure path (usage error, runtime failure, cancellation), including a stable `error.code`;
- JSON mode never emits progress/ANSI/localized prose to stdout;
- progress and human diagnostics go to stderr, including help text printed for an invalid command;
- `--quiet` suppresses non-essential progress;
- `--no-input` never prompts;
- documented exit codes (`0`, `1`, `2`, `130`);
- stable JSON field names for released command contracts;
- cancellation behavior;
- Fatal vs Partial diagnostic mapping;
- the released artifact shape: the CLI assembly is named `WeArchive`, the required command family is reachable, and the retired WPF/Velopack distribution surface is not reintroduced;
- the product version contract: `--version` and the `version` command agree, report a released version, and strip build metadata (see `docs/CLI.md` "Product version").

Tests must call CLI/application boundaries directly where practical; GUI automation is not part of the target test strategy.

### Release-artifact smoke tests

A release is verified by executing the published artifact, never a local development build, and
then by verifying the ZIP that actually ships:

```text
scripts/smoke-test-cli.ps1       --version --json (exact version), --help --json (required
                                 command family) and doctor --json --no-input, each asserting
                                 exit 0 and exactly one JSON document on stdout
scripts/smoke-test-package.ps1   asserts WeArchive.exe and wearchive.cmd sit at the ZIP root,
                                 extracts it into a clean directory, and runs the same contract
                                 against the extracted exe and through the wearchive.cmd shim
```

`doctor` needs no local WeChat client, so the smoke tests are environment-independent. CI builds
and verifies the artifact and its ZIP on every change (`cli-artifact` job); the release workflow
runs the same scripts with `-ExpectedVersion` before publishing.

Version-stamped builds must be covered whenever the version contract changes: CI publishes with
`-p:Version=0.0.0-ci` precisely so the artifact's reported version is asserted against a value
that differs from the `VersionPrefix` default, and `CliPackagingTests` pins the resolution rule
(informational version wins, build metadata stripped, numeric version as the fallback) against
synthetic assemblies.

### Adapter compatibility tests

Real-WeChat tests are isolated and skip gracefully when the required local environment is unavailable. They must never assert on or print private chat content.

### Migration tests

Every archive schema migration must be tested from the previous supported schema version.

### Export snapshot/determinism tests

Export fixtures remain deterministic. Generated metadata that is intentionally variable must be injectable/controlled for snapshot tests.

## 8. Fixture strategy

Use synthetic or sanitized fixtures. Do not commit:

- personal conversation content;
- real local archives;
- raw private client datasets;
- secrets, keys or credentials;
- generated exports containing private data;
- machine-specific private paths when avoidable.

`.probe/` and personal export outputs remain git-ignored.

## 9. Logging and diagnostics

Logging is not a substitute for structured diagnostics.

Import/export/CLI flows may produce:

- progress counters;
- structured warning/error codes;
- source/adapter version metadata;
- completeness indicators;
- final summary.

Chat content and database keys must never be logged. In CLI mode, logs/progress must never corrupt JSON stdout.

## 10. Reliability Levels

### 10.1 Why levels exist

Words such as **safe**, **durable**, **atomic**, **transactional** and **recoverable** are forbidden as standalone acceptance criteria for file/database behavior. They are too ambiguous unless the failure classes are named.

Every change that touches files, archive publication, checkpoints, temp files, replacement, cancellation or recovery must state its guarantee against these failure classes:

```text
A. normal successful completion
B. caught in-process cancellation
C. caught in-process I/O/runtime error
D. abrupt process termination/crash
E. OS/filesystem crash or sudden power loss
F. storage corruption/device failure (when relevant)
```

A PR may provide stronger guarantees than the baseline only when that stronger reliability property is explicitly required by the PRD/Issue/milestone.

### 10.2 R0 — Best effort / recreatable derived state

Use for caches, temporary data and other derived artifacts when loss is acceptable because authoritative state exists elsewhere.

Guarantee:

| Failure class | Required behavior |
|---|---|
| A. Success | Expected final output is complete |
| B. Caught cancellation | Best-effort cleanup; no guarantee of preserving previous derived output unless separately specified |
| C. Caught I/O/runtime error | Best-effort cleanup; report failure |
| D. Process crash | No recovery guarantee |
| E. OS/power loss | No recovery guarantee |
| F. Storage failure | No recovery guarantee |

### 10.3 R1 — In-process replace with best-effort restoration

This is the **Phase 1 Export reliability level**.

The SQLite archive is the system of record; export is derived and regenerable.

Required guarantee:

| Failure class | Required behavior |
|---|---|
| A. Success | A complete new export is published according to `EXPORT_PRD.md`; no stale duplicate partitions remain for the replaced conversation |
| B. Caught cancellation | The current process attempts to restore the previous export/output where the implementation has already displaced it; report cancellation. Restoration is best effort, not crash-safe |
| C. Caught I/O/runtime error | The current process attempts to restore the previous output where possible; report failure. Ambiguous leftovers may be retained for conservative cleanup/re-export |
| D. Process crash/kill | **Not guaranteed.** No journal/commit marker/persistent rollback protocol is required |
| E. OS/filesystem crash or power loss | **Not guaranteed.** No filesystem-durability/fsync contract is implied |
| F. Storage corruption/device failure | **Not guaranteed** |

Permitted techniques include sibling staging paths, temp files, backups and publish-last ordering **only to satisfy the in-process guarantee**. They do not imply a crash-recovery transaction.

Phase 1 Export must **not** introduce persistent journals, transaction ids, commit markers, recovery ledgers or a cross-process transaction protocol unless a later requirement explicitly upgrades the reliability level.

### 10.4 R2 — Database transaction publication

This is the baseline for one-conversation import publication.

SQLite is the system of record. Required semantics:

| Failure class | Required behavior |
|---|---|
| A. Success | The conversation transaction commits under the documented importer rules |
| B. Caught cancellation | Behavior must be explicitly defined by the Issue/PRD; cancellation is not automatically a source-coverage failure. Whatever is promised must be covered by tests |
| C. Caught runtime/source error | If it is a **Fatal source-coverage failure**, roll back the entire conversation transaction |
| D. Process crash | Rely on SQLite's documented transactional behavior for committed/uncommitted database transactions; do not add an application journal unless explicitly required |
| E. OS/power loss | Do not claim stronger guarantees than the configured SQLite/filesystem durability provides; if a stronger fsync/durability property becomes required, document and test it explicitly |
| F. Storage corruption/device failure | Outside the current product guarantee unless a requirement explicitly adds backup/recovery semantics |

**Hard rule:** a Fatal source-coverage failure must never publish a partial conversation that a later reader could mistake for a complete one.

Checkpoint advancement must occur only after the corresponding import publication boundary required by the checkpoint design.

### 10.5 R3+ — Explicit crash-recovery protocol

Any requirement that needs recovery across process crash/restart through persistent journals, commit markers, rollback state, multi-file transaction protocols or recovery state machines is **not a default engineering improvement**. It is a separate reliability feature and requires explicit PRD/Issue/milestone authorization before implementation.

Examples:

- persistent export transaction journal;
- commit marker read on next startup;
- write-ahead recovery ledger outside SQLite;
- distributed/multi-process coordination;
- cross-device atomic publication protocol;
- complex persistent state machine whose purpose is crash recovery.

If such behavior is requested during review but not documented, apply the hard-stop rule in `AGENTS.md` and escalate the requirement instead of coding it immediately.

### 10.6 Reliability review checklist

For any persistence/file PR, reviewer and implementer must answer:

1. Which reliability level applies?
2. What is the authoritative system of record?
3. What happens for A–F above?
4. Which guarantees are tested?
5. Does the implementation add recovery machinery stronger than the documented level?
6. If yes, where is the explicit PRD/Issue requirement authorizing that complexity?

## 11. Source adapter boundary

Adapters may know upstream formats/versions; generic layers may not.

Archive, search, export, Core domain models and CLI commands must not depend on WeChat table names or numeric source message codes.

WeChat key acquisition, SQLCipher decryption and compatibility/parsing code stay inside `src/WeArchive.Infrastructure/WeChat`.

No decrypted WeChat data may be intentionally left on disk after adapter disposal. Keys are never persisted, logged or exported.

## 12. Persistence policy

Any persistent archive schema change requires:

1. update to `docs/DATA_MODEL.md`;
2. migration;
3. migration test;
4. compatibility notes if existing archives are affected.

Migrations are numbered and forward-only.

## 13. CLI compatibility policy

The CLI is now the primary product surface.

Before 1.0, commands/JSON schemas may evolve, but every breaking change must be deliberate and documented. Once a command/JSON shape is included in a release used by automation, changes should prefer additive fields and deprecation over silent reinterpretation.

Normative rules:

- stdout JSON is machine contract, not log output;
- stderr is non-contractual human/progress output unless a specific diagnostic format is documented;
- exit codes are part of the command contract;
- `--no-input` must remain automation-safe;
- command parsing/formatting never changes archive semantics by itself.

## 14. Dependency policy

Prefer a small dependency surface. Add a dependency only when it materially improves correctness, maintainability or portability.

CLI frameworks are allowed only if they reduce parsing/help/validation complexity without leaking framework types into Core or Infrastructure.

`WeArchive.Core` remains dependency-minimal and free of presentation, SQLite and WeChat implementation concerns.

## 14.1 Packaging and release

The released artifact is a self-contained `win-x64` portable ZIP (`WeArchive-win-x64.zip`) produced by `scripts/pack-portable.ps1` from `src/WeArchive.Cli`.

Do not reintroduce an installer, auto-updater or update feed without a new product decision: the CLI is invoked ad hoc and exits, so there is no process in which an update check or in-app prompt could run. See [ADR 0007](adr/0007-cli-self-contained-distribution.md).

Do not replace the released-artifact smoke tests (`scripts/smoke-test-cli.ps1` and `scripts/smoke-test-package.ps1`) with a local development-build check.

`pack-portable.ps1` asserts the publish directory's executable reports the requested `-Version` before packaging, so `-SkipPublish` cannot silently package a stale or mis-stamped directory under a version its contents do not carry.

## 15. Security/privacy review

A PR needs explicit privacy review if it:

- adds network access;
- sends data outside the local machine;
- changes secret/config storage;
- adds telemetry;
- changes archive/export default locations;
- touches WeChat key acquisition, decryption or scratch-file handling.

Default posture remains local-only and offline-capable.

## 16. Issue template for implementation work

```markdown
## Requirement
PRD: FR-XX
Roadmap: MX
Reliability: R0 / R1 / R2 / R3+

## Goal

## Scope

## Non-goals

## Architecture impact

## Data model impact

## Failure semantics
- Success:
- Caught cancellation:
- Caught I/O/runtime failure:
- Process crash:
- OS/power loss:

## Acceptance criteria
- [ ] ...

## Tests

## Documentation changes
```

## 17. Completion rule

A feature is complete only when:

- its predecessor Issue existed before the PR was opened and the PR references it;
- acceptance criteria pass;
- tests cover behavior and documented failure semantics;
- diagnostics are adequate;
- docs match behavior;
- migration/provenance implications are handled;
- CLI contracts are tested when applicable;
- implementation stays within architectural boundaries;
- reliability complexity does not exceed documented requirements;
- no decrypted source data, key or private chat content leaks through logs/tests/repository artifacts.
