# ADR 0004 — Packaging and update distribution via Velopack

Status: accepted
Date: 2026-03-01

## Context

The MVP must produce a Windows program that a non-developer can run. `docs/PRD.md` keeps the
product local-first and offline-capable, so the build must not depend on a machine having a
.NET runtime installed.

Two distribution forms are required:

1. a portable ZIP that runs after extraction;
2. an installer (`Setup.exe`) with a working update path.

The installer must also not paint the project into a corner: code signing is explicitly not
an MVP prerequisite, but the packaging design must not prevent adding Authenticode later.

## Decision

1. Publishing is **self-contained**: `dotnet publish -c Release -r win-x64 --self-contained true`.
   A framework-dependent build is not a supported release artifact.
2. The **portable** artifact is `WeArchive-win-x64.zip`, produced by `scripts/pack-portable.ps1`
   from the publish directory.
3. The **installer** is produced by **Velopack** (current stable 1.2.0 at the time of this
   decision) via `scripts/pack-velopack.ps1`:
   `vpk pack -u WeArchive -v <version> -p <publishDir> -e WeArchive.exe`.
   `vpk` is used as a pinned .NET tool (`dotnet tool install -g vpk --version 1.2.0`) so the
   packaging tool always matches the `Velopack` package referenced by the application.
4. Update distribution is **GitHub Releases** consumed by Velopack's GitHub source:
   `new UpdateManager(new GithubSource("https://github.com/lybym/WeArchive", null, false))`.
5. The application only needs to **check** for updates. `UpdateService` reports "up to date",
   "new version available" (directing the user to Releases), or a neutral message when the
   release feed does not exist yet or the machine is offline. Running unpackaged during
   development is reported as such rather than as an error, so an absent release feed can
   never break the app.
6. The application calls `VelopackApp.Build().Run()` before WPF initialises so the installer
   and updater hooks are handled; it is a no-op in an unpackaged build.
7. Signing is optional and additive: the packaging scripts accept an optional certificate
   thumbprint, and `vpk pack` supports signing parameters, so Authenticode can be added
   without changing the artifact layout.

## Alternatives considered

### Framework-dependent publish

Rejected: it makes a .NET 10 runtime a user prerequisite, which contradicts the product's
"runs on a normal Windows machine" goal.

### MSI / WiX Toolset

Rejected for the MVP. It adds a large toolchain and a hand-maintained installer definition,
and it does not solve updates. Velopack produces an installer and an update feed from one
command and one NuGet package.

### ClickOnce

Rejected: it requires a trust configuration and a hosting model that conflicts with plain
GitHub Releases, and its update story is bound to the deployment manifest rather than to
versioned release assets.

### Squirrel.Windows

Rejected: it is effectively superseded by Velopack, which the project would then be migrating
away from later anyway.

### Porting an updater by hand

Rejected: update logic is security-sensitive and easy to get subtly wrong (delta packages,
staged rollouts, rollback). Using a maintained library is the lower-risk choice.

## Consequences

Positive:

- One command produces both the installer and the update assets, and the same assets feed the
  in-app update check.
- Users need no .NET runtime.
- Authenticode can be layered on later without repackaging design changes.

Costs:

- The release pipeline depends on the `vpk` tool; its version is pinned to the application's
  `Velopack` package.
- Velopack's asset set (`releases.*.json`, `*.nupkg`, `Setup.exe`, `Portable.zip`) must be
  published to the same GitHub Release for updates to work.
- Unsigned installers will raise a SmartScreen warning until signing is added; this is
  accepted for the MVP and recorded in the README limitations.

## References

- Velopack C# quick start: <https://docs.velopack.io/getting-started/csharp>
- Velopack GitHub Actions guide: <https://docs.velopack.io/distributing/github-actions>
- Velopack self-hosting / `vpk pack` reference: <https://docs.velopack.io/distributing/self-hosting>
- `scripts/pack-portable.ps1`, `scripts/pack-velopack.ps1`
- `.github/workflows/release.yml`
- `docs/DEVELOPMENT.md`
