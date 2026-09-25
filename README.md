# WeArchive

Local-first WeChat archive, parsing, export and analysis toolkit for personal data.

> **Product direction:** CLI-first (`gh`-style commands) for humans, scripts and AI agents.  
> **Platform:** Windows 10/11, `win-x64`, WeChat for Windows 4.x.  
> **Development rule:** documentation under `docs/` is the source of truth.

## Purpose

WeArchive turns the user's own local WeChat desktop data into a durable, normalized archive and machine-readable datasets.

The long-term product is not a chat viewer and not an extraction trick. Its value is the stable archive/message/export contract:

```text
Local WeChat source
    ↓
Read-only source adapter
    ↓
Canonical normalization + provenance
    ↓
SQLite archive (system of record)
    ↓
Query / export
    ↓
CLI + JSONL/YAML/JSON machine interfaces
```

The primary product surface is being migrated from the historical WPF MVP to a `gh`-style CLI. See [`docs/adr/0006-cli-first-product-surface.md`](docs/adr/0006-cli-first-product-surface.md).

## Target CLI

Initial command family:

```text
wearchive doctor
wearchive account list
wearchive conversation list
wearchive conversation show <id-or-alias>
wearchive sync --conversation <id-or-alias>
wearchive export --conversation <id-or-alias>
wearchive capture [--account <id>]
```

Automation contract:

```text
--json      exactly one JSON document on stdout
--quiet     suppress non-essential progress
--no-input  never prompt
stderr      progress and human diagnostics
exit 0      success
exit 1      operation/runtime failure
exit 2      usage/config validation failure
exit 130    cancellation/user interrupt
```

The CLI is intentionally **not** a full-screen TUI, conversational shell, embedded LLM or MCP server.

The discovery commands (`doctor`, `account list`, `conversation list`, `conversation show <id-or-alias>`) are thin adapters over the existing application services; their command/JSON/exit contract is documented in [`docs/CLI.md`](docs/CLI.md). The `sync` and `export` commands are delivered by separate M0.5 issues.

## Current implementation status

The difficult archive/source/export engine is already implemented:

- WeChat 4.x source discovery and read-only SQLCipher database access;
- local database-key acquisition from the running client with cryptographic verification;
- canonical normalization for text and documented non-text/event types;
- reply/quote, forwarded bundle, link/app-share and provenance handling;
- SQLite archive with migrations and idempotent import behavior;
- Raw Vault baseline capture — source-faithful, immutable generations with SHA-256 checksums, readable without the WeChat key ([RAW_VAULT.md](docs/RAW_VAULT.md));
- Raw-Vault-only canonical rebuild with a WeChat 4.x captured-source reader and validated archive replacement;
- deterministic machine export to monthly JSONL plus YAML/JSON catalogs;
- structured Fatal/Partial/Info diagnostics;
- fixture and real-environment integration tests.

The repository still contains the historical WPF application while **M0.5 CLI product-surface migration** is in progress. WPF is transitional and must not be expanded as a second first-class product surface.

## Reliability model

Reliability guarantees are explicit; terms such as “safe”, “atomic” and “durable” are not accepted without failure classes.

Current anchors:

- **Phase 1 Export = R1** — normal success publishes complete new output; caught cancellation/I/O failure attempts in-process restoration; process crash and OS/power loss are not guaranteed. SQLite is the system of record, so export can be regenerated.
- **Raw Vault capture = R1** — normal success publishes one complete generation; a Fatal failure or cancellation discards staged material and publishes nothing. No journal or commit marker is persisted.
- **Canonical rebuild** — builds and validates a fresh migrated archive before replacing the selected database; process-crash and power-loss recovery are not guaranteed and no persistent recovery journal is used.
- **Conversation Import = R2** — a Fatal source-coverage failure rolls back the entire conversation transaction.
- **R3+** — persistent journals, commit markers, recovery ledgers or complex crash-recovery state machines require an explicit product requirement before implementation.

See [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md).

## Export structure

```text
wechat-export/
├─ manifest.json
├─ identities.yaml
├─ conversations.yaml
├─ collections.yaml
└─ chats/
   ├─ direct/
   │  └─ u_<16-hex-stable-id>/<year>/<year>-<MM>.jsonl
   └─ groups/
      └─ g_<16-hex-stable-id>/<year>/<year>-<MM>.jsonl
```

Physical paths use stable IDs, never mutable display names. One JSONL line is one canonical logical message/event.

See [`docs/EXPORT_PRD.md`](docs/EXPORT_PRD.md) and [`docs/MESSAGE_SCHEMA.md`](docs/MESSAGE_SCHEMA.md).

## Architecture

Target shape:

```text
Human / Agent / Script
        ↓
   WeArchive.Cli
        ↓
Application Services
        ↓
Source Adapter → Normalizer → SQLite Archive → Query / Export
```

Target projects:

| Project | Role |
|---|---|
| `src/WeArchive.Core` | Domain, contracts, normalization, orchestration; presentation/source-format independent |
| `src/WeArchive.Infrastructure` | WeChat compatibility/key acquisition/SQLCipher, SQLite archive, Raw Vault store, export, settings |
| `src/WeArchive.Cli` | Command parsing, human/JSON rendering, stdout/stderr/exit-code contract |
| `tests/WeArchive.Tests` | Unit, integration, CLI contract and compatibility tests |

During migration, `src/WeArchive.App` may remain temporarily. It is not the target architecture.

## Current scope and limitations

In scope:

- local Windows WeChat 4.x source;
- normalized text/metadata archive;
- stable identity/conversation IDs;
- selective machine export;
- agent/script-friendly CLI migration.

Not yet complete:

- CLI product-surface migration itself;
- incremental checkpoints;
- complete partition-coverage reporting;
- full-text archive search;
- collection/time-range CLI workflows.

Out of current scope:

- binary media preservation;
- OCR/ASR;
- hosted/multi-user service;
- macOS/Linux source support;
- embedded AI provider;
- full-screen TUI/GUI as primary surface.

## Build and develop

```powershell
dotnet restore WeArchive.sln
dotnet build WeArchive.sln -c Release
dotnet test WeArchive.sln
```

The released product remains self-contained `win-x64`. M0.5 will simplify packaging around the CLI; the historical WPF/Velopack release path is not automatically preserved.

## Documentation first

Read before coding:

| Document | Purpose |
|---|---|
| [`docs/PRD.md`](docs/PRD.md) | Product definition and requirements |
| [`docs/EXPORT_PRD.md`](docs/EXPORT_PRD.md) | Export layout and selection semantics |
| [`docs/MESSAGE_SCHEMA.md`](docs/MESSAGE_SCHEMA.md) | Canonical message semantics |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | Technical boundaries and CLI target architecture |
| [`docs/DATA_MODEL.md`](docs/DATA_MODEL.md) | Archive model and schema evolution |
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | M0.5 CLI migration and later milestones |
| [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md) | Development/reliability/CLI governance |
| [`docs/CLI.md`](docs/CLI.md) | CLI command/JSON/exit contract |
| [`AGENTS.md`](AGENTS.md) | Mandatory agent rules and hard-stop conditions |

Development flow:

```text
PRD / ADR
   ↓
Roadmap milestone
   ↓
GitHub Issue + reliability level
   ↓
Implementation + tests
   ↓
Review against documented scope
```

## Privacy boundary

WeArchive is local-first and read-only toward WeChat.

- database keys are never persisted, logged or exported;
- no code injection/hooking/debugger attachment is required;
- decrypted copies are transient under `%LOCALAPPDATA%\WeArchive\scratch`;
- chat content is never written to application logs;
- real archives/exports/private datasets must not be committed to Git.

Core archive/export workflows work offline. Any future external AI/provider integration must remain optional and explicit.
