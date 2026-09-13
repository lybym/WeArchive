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
8. relevant files under `docs/adr/`

## Stack

The implementation is a Windows desktop application, not a script collection:

- **C# 14** on **.NET 10 LTS** (`net10.0` for `WeArchive.Core`; `net10.0-windows` for `WeArchive.Infrastructure`, `WeArchive.App` and `tests/WeArchive.Tests`), `win-x64`, self-contained.
- **WPF** (MVVM) is the only presentation technology.
- **xUnit v2 on the VSTest platform** is the test runner. xUnit v3 / Microsoft.Testing.Platform was evaluated and rejected: on the .NET 10 SDK its `dotnet test` mode discovered zero tests.
- Dependencies are centrally pinned in `Directory.Packages.props`; `WeArchive.Core` has no package and no project references at all.

Layout and dependency direction are normative in `docs/ARCHITECTURE.md` and `docs/adr/0003-dotnet-wpf-mvp.md`.

## Mandatory rules

- Documentation under `docs/` is the source of truth.
- Do not invent product requirements from existing code.
- Do not change architecture implicitly through implementation.
- Every feature must map to a PRD requirement and Roadmap milestone.
- Source/client-specific behavior must remain behind the adapter boundary.
- Normalizer output must follow `docs/MESSAGE_SCHEMA.md`.
- Export behavior and physical dataset layout must follow `docs/EXPORT_PRD.md`.
- Export, search and archive layers must depend on normalized domain models, not source-specific structures.
- Stable IDs, not mutable names, determine canonical identity and physical export paths.
- Unknown or unsupported source records must be preserved as explicit `unknown`/diagnostic states; do not silently drop them.
- Do not fabricate unavailable identities, URLs, amounts, filenames or message content merely to avoid null/unknown states.
- Phase 1 does not require binary image/audio/video/file preservation, OCR, ASR or an LLM-specific derived/chunk dataset.
- Persistent schema changes require an update to `docs/DATA_MODEL.md`, a migration and tests.
- Canonical message-schema changes require an update to `docs/MESSAGE_SCHEMA.md` and schema-version consideration.
- Product/architecture changes require documentation changes in the same PR.
- New major architectural choices require an ADR.
- Preserve the local-first and read-only-source principles unless documentation explicitly changes them.
- Do not commit real personal chat data, real archives, exports, secrets or machine-private datasets.
- WeChat key acquisition, SQLCipher decryption and the WeChat schema/parser compatibility code must stay inside `src/WeArchive.Infrastructure/WeChat` (`Compatibility`, `Schema`, `Parsers`, `Crypto`, `KeyAcquisition`). Nothing outside that boundary may know that WeChat is encrypted, which databases exist, or which upstream type codes mean what.
- No decrypted WeChat data may be left on disk. Plaintext scratch copies live only under `%LOCALAPPDATA%\WeArchive\scratch` for the duration of a read and are deleted when the adapter is disposed. Keys are never persisted, logged or exported.

## When docs and code disagree

During early development, treat the current docs as authoritative.

Do not silently alter implementation direction to preserve old provisional code. Either:

1. align code to docs, or
2. propose and document a deliberate change to the docs/ADR before implementing it.

## Task workflow

For each implementation task:

1. Identify the PRD requirement(s).
2. Identify the Roadmap milestone.
3. Check `EXPORT_PRD.md` / `MESSAGE_SCHEMA.md` when relevant.
4. Check architecture/data-model implications.
5. Define acceptance criteria.
6. Implement the smallest compliant change.
7. Add/update tests.
8. Update docs when behavior or architecture changes.
9. Verify that diagnostics/provenance are not weakened.

## Current priority

Current priority: **M0 is implemented, and the MVP slice of M1 is implemented.** The next work is the rest of M1 (incremental checkpoints, full partition-coverage reporting) and then M2.

The foundation this milestone was gated on now exists and must not be weakened: the generic adapter contract, the canonical message schema, normalized models, archive persistence, stable identity/export rules, provenance, diagnostics, fixture-driven import and deterministic JSONL exports.

Because the implementation shipped ahead of its documentation, **docs under `docs/`, `README.md` and this file must be corrected in the same change as any further behavior change** — a feature is not done until the documents that describe it agree with what the program does.
