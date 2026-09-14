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
Issue / task
  ↓
Code + tests
```

Code is not the specification. Because the shipped implementation once ran ahead of its
documents, the reverse direction is now mandatory too: when behaviour changes, the documents
that describe it change in the same pull request, and when the implementation is found to
differ from the documents, correcting the documentation is itself a required task rather than
an optional cleanup.

## 2. Definition of ready

A development task is ready only when:

- the user-facing or system behavior is described in `docs/PRD.md`, or the task is an implementation of an existing documented requirement;
- architecture impact is understood;
- persistent model changes are reflected in `docs/DATA_MODEL.md`;
- the task belongs to a Roadmap milestone;
- acceptance criteria are explicit.

## 3. Stack and toolchain

```text
C# 14 / .NET 10 LTS
  net10.0          WeArchive.Core
  net10.0-windows  WeArchive.Infrastructure, WeArchive.App, WeArchive.Tests
  win-x64, self-contained releases
  WPF (MVVM) for the desktop UI
  xUnit v2 on the VSTest platform for tests
```

The SDK is pinned by `global.json` (10.0.401, `rollForward: latestFeature`). Language level,
nullable, analyzer level, warnings-as-errors and deterministic builds are set once in
`Directory.Build.props`; every package version is pinned centrally in
`Directory.Packages.props` with `ManagePackageVersionsCentrally`.

The solution file is `WeArchive.sln`.

```powershell
dotnet restore WeArchive.sln
dotnet build   WeArchive.sln -c Release
dotnet test    WeArchive.sln
```

`TreatWarningsAsErrors` is on. A build with a warning is a failing build.

### 3.1 Test runner decision

xUnit **v2 on VSTest** is the test platform. xUnit v3 with Microsoft.Testing.Platform was
evaluated and rejected: on the .NET 10 SDK its MTP mode of `dotnet test` discovered zero tests
for this project while the same assembly ran all tests when launched directly. A reliable
`dotnet test` locally and in CI is worth more than the newer runner.

The test project is Windows-targeted (`net10.0-windows`) because the infrastructure layer is.

## 4. Pull request requirements

Every non-trivial PR should answer:

1. Which PRD requirement or Roadmap item does this implement?
2. Does it change architecture?
3. Does it change persisted data/schema?
4. Does it change user-visible behavior?
5. What tests prove the acceptance criteria?
6. What diagnostics/failure modes were added or changed?
7. Which documents were updated, and do they still describe the shipped behaviour?

If behavior changes, docs must change in the same PR.

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

Existing records: `0001` docs are the source of truth, `0002` archive core before real source
adapter, `0003` .NET/WPF stack, `0004` Velopack distribution, `0005` local key acquisition.

## 6. Branching and change scope

Prefer small, milestone-aligned changes.

Suggested branch names:

```text
feat/m0-archive-store
feat/m0-fixture-adapter
feat/m1-source-discovery
fix/import-idempotency
docs/update-data-model
```

Avoid mixing unrelated refactors with product changes.

## 7. Testing policy

Testing layers:

### Unit tests

For pure parsing, normalization, identity and archive functions.

```text
StableIdTests            stable-ID derivation, namespace separation, prefix length
MessageNormalizerTests   upstream evidence -> canonical types, payloads, reply handling
WeChatContentParserTests WeChat wire payloads -> source-neutral content
```

### Fixture integration tests

For complete import and export flows without requiring a live upstream client. The synthetic
`FixtureSourceAdapter` in `src/WeArchive.Infrastructure/Fixtures` is the source for these:

```text
ExportPipelineTests      fixture import -> archive -> JSONL package, manifest and catalogs
SqliteArchiveStoreTests  upsert idempotency, timeline ordering, reply resolution, provenance
SourceCoverageTests      source-coverage failure -> failing run, and no partial archive publication
```

### Adapter compatibility tests

For source-specific behavior:

```text
SqlCipherTests               SQLCipher page format, WAL replay, key verification
WeChatAdapterIntegrationTests source discovery, conversation enumeration, record reading
```

Compatibility tests must be isolated and **skipped gracefully when the required local
environment is unavailable**. `WeChatEnvironmentFactAttribute` skips a real-WeChat test when no
usable WeChat installation is present, so the suite is green on a machine without WeChat.
Integration tests must never assert on message content — only on structure and counts — so no
private data can leak into test output.

### Migration tests

Every archive schema migration must be tested from the previous supported schema version.

### Export snapshot/determinism tests

The JSONL/YAML exporters have deterministic fixtures and snapshots. `ExportRequest.CreatedAt`
is injectable precisely so that export output can be byte-compared; it is the only
non-reproducible field in an export package.

Current status: 101 tests, of which 96 pass and five are real-WeChat integration tests that skip
when no usable WeChat installation is present.

## 8. Fixture strategy

The repository uses synthetic or sanitized fixtures.

Do not commit:

- personal conversation content
- real local archives
- raw private client datasets
- secrets, keys or credentials
- machine-specific private paths when avoidable

**Real personal chat data must never be committed**, in any form — not as a fixture, not as a
test expectation, not as a diagnostic message, not as a screenshot, and not as a probe output.

`.probe/` (reverse-engineering scratch work, including decrypted or plaintext database copies
and recovered keys) and export outputs are git-ignored and must stay that way. If a probe
produces something that a test needs, it must be reduced to a synthetic fixture before it is
committed.

Fixtures should model edge cases deliberately:

- duplicate display names
- group/private conversation differences
- repeated identical messages
- missing sender metadata
- unsupported message type
- missing attachment
- out-of-order source records
- multi-partition timeline

## 9. Logging and diagnostics

Do not treat logging as a substitute for structured diagnostics.

Import and export flows produce:

- progress counters
- structured warning/error codes
- source/adapter version metadata
- completeness indicators
- final summary

`Microsoft.Extensions.Logging` is wired for the application. **Chat content is never logged**,
and neither is any database key. Diagnostics carry identifiers, upstream type codes and short
engineering explanations only.

## 10. Source adapter boundary

Adapters are allowed to know about upstream formats and versions.

Generic layers are not.

The following must not depend on source-specific table names, file names or message-type codes:

- archive store
- search
- exporters
- core domain models
- WPF view models and application commands

WeChat key acquisition, SQLCipher decryption and WeChat schema/parser compatibility code must
stay inside `src/WeArchive.Infrastructure/WeChat` (`Compatibility`, `Parsers`, `Crypto`,
`KeyAcquisition`). Nothing outside that boundary may know that WeChat is encrypted, which
databases exist, or which upstream type codes mean what.

No decrypted WeChat data may be left on disk: plaintext scratch copies live only under
`%LOCALAPPDATA%\WeArchive\scratch` for the duration of a read and are deleted when the adapter
is disposed.

## 11. Persistence policy

Any persistent schema change requires:

1. update to `docs/DATA_MODEL.md`
2. migration
3. migration test
4. compatibility notes if existing archives are affected

Migrations are numbered and forward-only, recorded in `schema_migrations`, with the current
version mirrored into SQLite's `user_version`.

## 12. User-visible and command-line compatibility

The shipped surface is the WPF application. Until 1.0, its user-visible behaviour and layout
may evolve, but breaking changes should be documented.

A command-line surface is a documented long-term target, not a current deliverable. When it is
introduced, its commands may evolve until 1.0; after 1.0, breaking CLI or archive-schema
changes require an explicit migration/deprecation plan.

## 13. Dependency policy

Prefer a small dependency surface.

Add a dependency only when it materially improves correctness, maintainability or portability.

For each substantial dependency, record:

- purpose
- why the standard library or an in-box framework assembly is insufficient
- platform implications
- licensing implications

Current dependencies and why they are there:

| Dependency | Where | Purpose |
|---|---|---|
| `Microsoft.Data.Sqlite` | Infrastructure | The archive system of record. ADO.NET provider for SQLite; no ORM is used or wanted. |
| `YamlDotNet` | Infrastructure | Reading and writing `identities.yaml`, `conversations.yaml` and `collections.yaml`, which must stay hand-editable. |
| `ZstdSharp.Port` | Infrastructure | Decompressing WeChat payloads whose compression marker is set; managed, so no native dependency. |
| `Microsoft.Extensions.DependencyInjection` (+ `.Abstractions`) | App, Infrastructure | Composition root and constructor injection. |
| `Microsoft.Extensions.Logging` (+ `.Abstractions`, `.Debug`) | App, Infrastructure | Application logging. |
| `Velopack` | App | Installer and update packaging/checking. |
| `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio` | Tests | Test platform and runner. |

`WeArchive.Core` has **no** package and no project references by design, which is what keeps the
canonical model free of WPF, SQLite, file-system and WeChat dependencies. SQLite, JSON, crypto
and cancellation are otherwise in-box (`Microsoft.Data.Sqlite`, `System.Text.Json`,
`System.Security.Cryptography`).

## 14. Security/privacy review

A PR needs explicit privacy review if it:

- adds network access
- sends data outside the local machine
- changes secret/config storage
- adds telemetry
- changes archive/export default locations
- touches WeChat key acquisition, decryption or scratch-file handling

Default posture remains local-only and offline-capable. The only network access in the MVP is
the on-demand update check against the project's GitHub Releases.

## 15. Issue template for implementation work

Recommended issue body:

```markdown
## Requirement
PRD: FR-XX
Roadmap: MX

## Goal

## Scope

## Non-goals

## Architecture impact

## Data model impact

## Acceptance criteria
- [ ] ...

## Tests

## Documentation changes
```

## 16. Completion rule

A feature is not complete when the code merely runs.

It is complete when:

- acceptance criteria pass
- tests cover the behavior
- diagnostics are adequate
- docs match behavior
- migration/provenance implications are handled
- the implementation remains within the documented architectural boundaries
- no decrypted source data, key or chat content is persisted, logged or exported
