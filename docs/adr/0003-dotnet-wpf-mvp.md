# ADR 0003 — .NET 10 / C# 14 / WPF is the implementation stack

Status: accepted
Date: 2026-03-01

## Context

The repository was founded as a documentation-led project with a CLI-first Python
implementation sketch. The formal requirements in `docs/PRD.md`, the canonical message
contract in `docs/MESSAGE_SCHEMA.md` and the export contract in `docs/EXPORT_PRD.md` are
language-independent, but every architectural document described Python modules
(`src/wearchive/...`), a Python `SourceAdapter` protocol, and "Phase 1 is CLI-first".

The first runnable milestone has now been delivered and it is a Windows desktop
application:

- the highest-value local source is Windows WeChat 4.x, which stores its data in
  SQLCipher-encrypted SQLite databases that must be read out of a running, logged-in client;
- the users this milestone targets need a real, runnable Windows program that lets them pick
  a conversation and get a dataset out, not a library;
- the target runtime is C# 14 on .NET 10 LTS (`net10.0-windows`), with WPF as the desktop UI.

Leaving the Python documents untouched while shipping C# would violate the project's own
first rule: documentation is the source of truth (`docs/adr/0001`).

## Decision

1. The implementation stack is **C# 14 / .NET 10 LTS**, target frameworks `net10.0` for the
   domain layer and `net10.0-windows` for infrastructure, presentation and tests.
2. The desktop UI is **WPF**. Windows 10/11 `win-x64` is the only supported platform for
   this milestone. macOS, Linux, Web, MAUI, WinUI, Electron and Avalonia are out of scope.
3. The solution is split into four projects with enforced dependency direction:
   `WeArchive.Core` (domain, contracts, normalizer — no WPF, no SQLite, no WeChat),
   `WeArchive.Infrastructure` (source adapter, compatibility, SQLCipher, archive, export,
   settings), `WeArchive.App` (WPF presentation, MVVM) and `tests/WeArchive.Tests`.
4. The early Python skeleton (`src/wearchive/`, `pyproject.toml`, `tests/test_model.py`) is
   **deleted**. It never defined product behaviour; `docs/` did. Keeping a parallel
   implementation would only create two competing sources of truth.
5. The generic contracts stay exactly as documented: `ISourceAdapter` (describe source, list
   accounts, list conversations, read messages, list participants, describe one
   conversation), canonical message envelope, SQLite archive as the system of record, and
   machine-oriented export. These are now C# interfaces rather than Python protocols, but
   the boundaries are unchanged.
6. xUnit is used for tests. **xUnit v2 on the VSTest platform** was chosen over xUnit v3: on
   the .NET 10 SDK, xUnit v3's Microsoft.Testing.Platform `dotnet test` mode discovered zero
   tests for this project even though the same assembly ran all tests when invoked directly.
   A green `dotnet test` locally and in CI matters more than the newer runner.

## Alternatives considered

### Keep Python and add a Python desktop UI

Rejected. The source of truth is a Windows-only, memory-resident client whose databases are
encrypted by a native Windows library; the ecosystem the users need is a signed, self-contained
Windows executable. A Python UI would also add a runtime prerequisite the product explicitly
does not want.

### Keep a Python core behind a C# UI

Rejected. It would require shipping and interop-ing a second runtime for no functional gain,
and it would keep two implementations of the canonical normalizer alive.

### Avalonia / MAUI / WinUI instead of WPF

Rejected for this milestone. The product is Windows-only, WPF is in-box with .NET 10 and
requires no extra dependency, and the UI here is a technical-verification surface rather than
a design-led product.

### Keep the Python CLI as a second entry point

Rejected. `docs/PRD.md` describes one product surface per milestone. A second entry point
would need its own tests, packaging and support, and the CLI surface in the PRD is a
long-term goal, not a current deliverable.

## Consequences

Positive:

- One implementation, one source of truth; the docs now describe what actually runs.
- Self-contained `win-x64` publishing gives users a program with no .NET prerequisite.
- The `Core` project has zero package and zero project references, so the canonical model
  cannot accidentally acquire a WPF, SQLite or WeChat dependency.
- SQLite, JSON and crypto are all in-box (`Microsoft.Data.Sqlite`, `System.Text.Json`,
  `System.Security.Cryptography`), keeping the dependency surface small.

Costs:

- The documents that described Python module layout had to be rewritten in the same change.
- Contributors expecting a CLI must use the WPF application; the CLI surface in the PRD is
  now explicitly future work.
- The test project is Windows-targeted because the infrastructure layer is
  (`net10.0-windows`).

## References

- `docs/PRD.md`
- `docs/ARCHITECTURE.md`
- `docs/DEVELOPMENT.md`
- `docs/adr/0001-docs-are-source-of-truth.md`
- `docs/adr/0002-archive-core-before-real-source-adapter.md`
- `docs/adr/0004-distribution-velopack.md`
- `docs/adr/0005-wechat-local-key-acquisition.md`
