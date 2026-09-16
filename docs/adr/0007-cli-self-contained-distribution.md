# ADR 0007 — Self-contained portable CLI distribution

Status: accepted
Date: 2026-09-16

## Context

[ADR 0004](0004-distribution-velopack.md) chose Velopack for the historical WPF MVP line:
a windowed `Setup.exe` installer plus a GitHub-Releases update feed, driven by an in-app
`UpdateService`. That decision assumed a desktop GUI user who installs a program once and
expects it to update itself.

[ADR 0006](0006-cli-first-product-surface.md) replaced the WPF presentation layer with a
`gh`-style command CLI, and Issue #9 retired the WPF surface and the WPF-only update
plumbing. The product surface is now an executable invoked by humans, scripts and agents.

That changes the distribution requirement:

- the primary consumer is a shell, a CI script or an agent harness, not a person clicking
  through an installer;
- the product must be usable without a .NET runtime on the machine (`docs/PRD.md`
  NFR-01 local-first, offline-capable);
- the executable must be reachable by name from `PATH` (`wearchive` / `WeArchive.exe`);
- an in-app updater requires a long-running GUI process to check and apply updates, which a
  short-lived CLI process is not.

`docs/ROADMAP.md` states the M0.5 requirement directly: a self-contained `win-x64` CLI
artifact suitable for direct invocation, and "Do not preserve Velopack merely because the
WPF line used it."

## Decision

1. Publishing remains **self-contained**:
   `dotnet publish src/WeArchive.Cli/WeArchive.Cli.csproj -c Release -r win-x64 --self-contained true`.
   A framework-dependent build is not a supported release artifact.
2. The release artifact is a **portable ZIP**: `WeArchive-win-x64.zip`, produced by
   `scripts/pack-portable.ps1` from the publish directory. The ZIP contains
   `WeArchive.exe` and a one-line `wearchive.cmd` shim so the documented lower-case
   `wearchive` invocation works from `PATH` after extraction. Windows also resolves
   `WeArchive.exe` case-insensitively.
3. **There is no installer and no auto-updater.** The `Velopack` package reference, the
   `vpk`-based `scripts/pack-velopack.ps1` and the WPF `UpdateService` are removed.
   Upgrading means extracting a newer ZIP over the previous one. This is a deliberate
   simplification, not an omission: a background update mechanism would add a
   security-sensitive network/self-modification surface that the product does not need.
4. Distribution is **GitHub Releases**: `.github/workflows/release.yml` publishes the
   portable ZIP as the release asset when a `v*` tag is pushed or the workflow is dispatched
   with an explicit version. The `AssemblyName` of `src/WeArchive.Cli` is `WeArchive`, so
   the produced executable is `WeArchive.exe`.
5. **Release verification runs against the published artifact and the shipped ZIP**, not a
   development build. `scripts/smoke-test-cli.ps1` executes the artifact and asserts
   `--version --json` (the exact release version), `--help --json` (required FR-22 command
   family) and a real fixture-safe command path (`doctor --json --no-input`) that must exit `0`
   and emit exactly one JSON document. `scripts/smoke-test-package.ps1` then asserts the ZIP's
   layout, extracts it into a clean directory and runs the same contract against the extracted
   executable and through the `wearchive.cmd` shim. `doctor` needs no local WeChat client, so the
   smoke tests are environment-independent.
6. **The reported product version is the release version, including a prerelease suffix.**
   `--version` resolves the CLI assembly's informational version with build metadata removed
   (`src/WeArchive.Cli/ProductVersion.cs`), because the SDK stamps `-p:Version=0.2.0-rc.1` as
   `AssemblyVersion=0.2.0.0` and `AssemblyInformationalVersion=0.2.0-rc.1`. Reporting the numeric
   assembly version would make a prerelease — a path this workflow deliberately publishes
   (`--prerelease`) — indistinguishable from its final release. `pack-portable.ps1` asserts the
   publish directory reports the requested version before packaging, so `-SkipPublish` cannot
   package a stale directory under a version its contents do not carry.
7. **Code signing remains separate, unscheduled work.** Nothing in this decision prevents
   adding Authenticode signing to the publish output later.
8. **Source-control signing of the CLI contract is unchanged.** `docs/CLI.md` remains
   normative for command syntax, JSON shapes and exit codes; this ADR changes only how the
   binary reaches a machine.

## Alternatives considered

### Keep Velopack for the CLI

Rejected. Velopack's value is a windowed installer plus a self-update feed for a GUI
application. A CLI is invoked ad hoc and exits; there is no process in which to run an
update check, no window to host an "update available" prompt, and no user expectation of
one. Keeping it would preserve a packaging toolchain, a pinned `vpk` tool and a network
update path for no product benefit.

### MSI / WiX Toolset

Rejected. It adds a large toolchain and a hand-maintained installer definition, and it
solves a machine-wide installation problem the product does not have. A portable folder
that a user or script extracts is composable and uninstallable by deletion.

### Framework-dependent publish

Rejected. It makes a .NET 10 runtime a user and CI prerequisite on every target machine,
which contradicts the product's local-first, offline, self-contained goal.

### A single-file self-contained executable

Considered. `PublishSingleFile` would remove the folder of runtime files, but it extracts
native dependencies at first run, which is slower to start and complicates the SQLite
native-library load path (`SQLitePCLRaw`/`e_sqlite3`). The folder-shaped publish plus a
`wearchive.cmd` shim already satisfies the "invoke by name from PATH" requirement with less
risk, so it is the chosen default. `PublishReadyToRun` stays off for a smaller artifact.

### Container or `dotnet tool` distribution

Rejected for this phase. `dotnet tool install` would require a .NET runtime, and the product
is Windows-only and self-contained by design.

## Consequences

Positive:

- one command produces the release artifact; the pipeline has no external packaging tool;
- the artifact has no installer, no update feed and no network dependency;
- extraction plus a `PATH` entry is the whole "installation" story, which suits scripts and
  agents;
- the release is smoke-tested as shipped, not as built;
- the dependency surface shrinks (the `Velopack` package is gone).

Costs:

- users must re-extract to upgrade; there is no automatic update;
- the artifact is a folder, not a single file, so it must be extracted rather than copied;
- unsigned executables will raise a SmartScreen warning until signing is scheduled; this is
  accepted and recorded in the README limitations.

## Supersedes

This ADR supersedes [ADR 0004](0004-distribution-velopack.md). It retains ADR 0004's
self-contained `win-x64` publishing decision and its `WeArchive-win-x64.zip` portable
artifact name.

## References

- `docs/PRD.md` (FR-22, NFR-01, NFR-05)
- `docs/ROADMAP.md` — M0.5 "Packaging and release"
- `docs/ARCHITECTURE.md` — section 3.1 presentation layer
- `docs/CLI.md` — entry point and invocation contract
- `docs/DEVELOPMENT.md` — stack, packaging and testing policy
- `docs/adr/0004-distribution-velopack.md` (superseded)
- `docs/adr/0006-cli-first-product-surface.md`
- `scripts/pack-portable.ps1`, `scripts/smoke-test-cli.ps1`, `scripts/smoke-test-package.ps1`
- `src/WeArchive.Cli/ProductVersion.cs`
- `.github/workflows/ci.yml`, `.github/workflows/release.yml`
