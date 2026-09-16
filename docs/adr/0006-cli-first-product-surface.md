# ADR 0006 — CLI-first product surface

Status: accepted
Date: 2026-09-15

## Context

WeArchive's durable value is the normalized local archive and the stable machine-oriented
export/query contract. The primary downstream consumers are scripts, Harness/LLM workflows and
AI agents; human operators also need a direct way to inspect diagnostics and run the same
operations.

The current implementation reached an MVP through a WPF presentation layer. That decision made
sense as a technical-verification surface, but it makes the product harder to compose with agents:
an agent would have to automate windows, infer UI state and translate visible text back into
structured results even though the underlying Core and Infrastructure layers already expose
structured operations.

The existing architecture is already well isolated: source adapters, normalization, archive,
diagnostics and export do not require WPF. This allows the product surface to change without
rewriting the archive engine.

## Decision

1. **WeArchive is CLI-first.** The supported product surface for new development is a `gh`-style
   command-line application named `wearchive` / `WeArchive.exe`. Commands are explicit operations,
   not a conversational shell.
2. **C# 14 / .NET 10 LTS remains the implementation stack.** The real source adapter remains
   Windows-only (`win-x64`) because it integrates with WeChat for Windows 4.x. This ADR changes the
   presentation decision in ADR 0003, not the language/runtime or the Core/Infrastructure
   boundaries.
3. The target solution shape is:

   ```text
   WeArchive.Cli
       ↓
   WeArchive.Infrastructure
       ↓
   WeArchive.Core
   ```

   `tests/WeArchive.Tests` may reference all three for contract/integration testing.
4. **Do not maintain WPF and CLI as two first-class product surfaces.** During migration the WPF
   project may remain temporarily so `main` stays buildable, but the migration issue must remove
   the WPF-specific presentation code once CLI parity for the required MVP operations is reached.
5. **No TUI in this phase.** Full-screen terminal UI, interactive menus, chat-style prompts,
   embedded agents/models and MCP/server modes are out of scope. They require separate product
   justification later.
6. The initial command family is intentionally small:

   ```text
   wearchive doctor
   wearchive account list
   wearchive conversation list
   wearchive conversation show <id-or-alias>
   wearchive sync --conversation <id-or-alias>
   wearchive export --conversation <id-or-alias>
   ```

   Search, statistics, collections and additional selectors are added only when their existing
   PRD/Roadmap requirements are implemented.
7. **CLI output is a product API.** Every automation-relevant command must support a stable
   machine-readable mode. Initial contract:

   - default output: concise human-readable text;
   - `--json`: one final JSON document on stdout, with no ANSI decoration or explanatory prose;
   - progress and human diagnostics go to stderr so stdout remains pipeable;
   - `--quiet` suppresses non-essential progress;
   - `--no-input` forbids interactive prompting and fails instead when required input is missing;
   - exit `0`: requested operation completed under its documented semantics;
   - exit `1`: runtime/operation failure, including a Fatal diagnostic;
   - exit `2`: command-line usage or configuration validation failure;
   - exit `130`: user interrupt/cancellation.

   Partial diagnostics may accompany exit `0` when the relevant PRD explicitly allows the
   operation to publish a partial-but-valid result; the JSON result must expose those diagnostics.
8. Commands are thin adapters over application services. CLI parsing, formatting and process exit
   behavior must not contain WeChat schema logic, archive semantics or export business rules.
9. SQLite remains the system of record. Export files are derived and regenerable. The CLI does not
   introduce a second persistence model.

## Transition rule

At acceptance of this ADR, the repository still contains the historical WPF implementation. That
is a temporary implementation state, not the target product architecture. Migration is tracked by
GitHub Issues created from this decision and must preserve the current Core/Infrastructure behavior
and tests while replacing the presentation layer.

## Alternatives considered

### Keep WPF and add a CLI beside it

Rejected for the current product stage. Two first-class surfaces duplicate orchestration,
configuration, testing, packaging and support work. The primary use case does not justify that
cost.

### Build a Claude Code-style conversational terminal application

Rejected. WeArchive is a local archive capability, not an agent runtime. A conversational shell
would mix model/provider concerns into a deterministic data tool and make automation harder to
reason about.

### Build a full-screen TUI

Deferred. A TUI can improve manual browsing later, but it does not improve the machine contract
and is unnecessary for the current agent-first use case.

### Rewrite the product in another CLI-oriented language

Rejected. `WeArchive.Core` and `WeArchive.Infrastructure` already contain the difficult and
valuable behavior. C#/.NET remains appropriate for the Windows source adapter and self-contained
publishing; replacing the runtime would create migration risk without improving the CLI contract.

## Consequences

Positive:

- agents and scripts invoke deterministic commands instead of automating a GUI;
- stdout/JSON/exit-code contracts become directly testable;
- the WPF-specific state-management and UI test surface can be removed;
- packaging can become simpler because a portable self-contained executable is sufficient for the
  core product;
- Core/Infrastructure investment is preserved.

Costs:

- the WPF MVP must be deliberately retired rather than expanded;
- CLI compatibility becomes a product contract that requires tests and deprecation discipline;
- documentation and release workflows must distinguish the transitional WPF implementation from
  the CLI target until migration completes.

## Supersedes

This ADR supersedes the WPF/presentation-surface decision in
[ADR 0003](0003-dotnet-wpf-mvp.md). It **retains** ADR 0003's C# 14 / .NET 10 choice, Windows
source-adapter constraint, project-layering principles and xUnit decision.

ADR 0004 remains the historical distribution decision for the WPF line. CLI distribution changes
must be made explicitly by the corresponding migration/release issue rather than inferred here;
that has since been done by [ADR 0007](0007-cli-self-contained-distribution.md), which
supersedes ADR 0004 and ships the CLI as a self-contained portable ZIP.

## References

- `docs/PRD.md`
- `docs/ARCHITECTURE.md`
- `docs/ROADMAP.md`
- `docs/DEVELOPMENT.md`
- `AGENTS.md`
- `docs/adr/0001-docs-are-source-of-truth.md`
- `docs/adr/0003-dotnet-wpf-mvp.md`
