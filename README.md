# WeArchive

Local-first WeChat preservation, archive, query and export toolkit for personal data.

> **Product direction:** CLI-first (`gh`-style commands) for humans, scripts and AI agents.  
> **Platform:** Windows 10/11, `win-x64`, WeChat for Windows 4.x.  
> **Development rule:** documentation under `docs/` is the source of truth.

## Purpose

WeArchive is evolving from a "read WeChat and export files" tool into a layered personal-data archive:

```text
Live WeChat source
      │ capture
      ▼
Raw Vault                    archival source of truth
      │ reader/parser/normalizer
      ▼
archive/wearchive.db         canonical operational system of record
      │
      ▼
ArchiveQueryService
   ├─ CLI --json
   ├─ future MCP
   └─ JSONL/YAML/JSON export
```

The layers deliberately solve different problems:

- **Raw Vault** preserves source-faithful evidence so historical data can be re-parsed after WeChat schema/key-access changes or upstream deletion.
- **`wearchive.db`** contains stable normalized semantics, deterministic IDs and query indexes; normal product behavior reads this layer.
- **ArchiveQueryService** is the intended interactive API for CLI/Harness/MCP callers.
- **JSONL/YAML/JSON** remain portable, auditable interchange/offline export formats rather than the primary runtime query database.

The target layering is defined by [`docs/RAW_VAULT.md`](docs/RAW_VAULT.md) and [`docs/adr/0008-raw-vault-canonical-query-layers.md`](docs/adr/0008-raw-vault-canonical-query-layers.md).

## Current shipped CLI

The 0.2.x command family remains:

```text
wearchive doctor
wearchive account list
wearchive conversation list
wearchive conversation show <id-or-alias>
wearchive sync --conversation <id-or-alias>
wearchive export --conversation <id-or-alias>
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

The shipped CLI contract is documented in [`docs/CLI.md`](docs/CLI.md). The historical WPF MVP has been retired.

## Next architecture target

The accepted next architecture introduces preservation/rebuild and a first-class query layer. Planned command families include:

```text
wearchive capture
wearchive sync --conversation <id-or-alias>
wearchive sync --collection <name>
wearchive rebuild

wearchive message list ... --json
wearchive search ... --json
wearchive context <message-id> ... --json
```

`capture` preserves live-source evidence in the Raw Vault. `rebuild` recreates the canonical archive from the Raw Vault **without accessing live WeChat or reacquiring its database key**.

These commands are target requirements, not claims about 0.2.x implementation status. Delivery order is in [`docs/ROADMAP.md`](docs/ROADMAP.md).

## Harness / Agent access

Harnesses and agents should converge on:

```text
Harness / Agent
      ↓
CLI --json / future MCP
      ↓
ArchiveQueryService
      ↓
archive/wearchive.db
```

They should **not** normally:

- query Raw Vault files/databases directly;
- depend on WeChat table names or raw message type codes;
- execute ad-hoc SQL as a stable product contract;
- recursively scan large JSONL exports for interactive retrieval once query APIs are available.

See [`docs/HARNESS.md`](docs/HARNESS.md).

## Why both Raw Vault and `wearchive.db` exist

They are not duplicate databases.

```text
Raw Vault
= recovery / preservation format
= "what did the source contain?"
= non-reproducible preserved evidence

wearchive.db
= runtime canonical format
= "what does WeArchive understand it to mean?"
= rebuildable from Raw Vault

JSONL
= interchange / offline export format
= rebuildable from warchive.db
```

This lets future parser versions reinterpret old source records without forcing QueryService, Harness or export callers to understand historical WeChat schemas.

## Synchronization model

The target synchronization path separates two kinds of progress:

```text
WeChat
  │ capture checkpoint
  ▼
Raw Vault
  │ ingest checkpoint
  ▼
wearchive.db
```

Important invariants:

- first capture establishes a supported baseline; later capture is incremental where safely possible;
- Raw Vault generations are logically immutable;
- source disappearance does **not** delete previously preserved evidence;
- parser failure does not require already-preserved data to be recollected;
- stable IDs survive parser upgrades and full rebuilds;
- Collection is the shared conversation scope for sync, query/Harness and export.

The shipped migration-1 generic checkpoint exists, but the current importer does not yet consume/advance it. Explicit capture/ingest checkpointing is future work.

## Install and run

Download `WeArchive-win-x64.zip` from [Releases](https://github.com/lybym/WeArchive/releases), extract it and add the folder to `PATH`:

```powershell
wearchive --version
wearchive doctor
```

The package is self-contained: no .NET runtime is required. It contains `WeArchive.exe` and a `wearchive.cmd` shim. There is no installer and no auto-updater; upgrading means extracting a newer ZIP. See [`docs/adr/0007-cli-self-contained-distribution.md`](docs/adr/0007-cli-self-contained-distribution.md).

## Current implementation status

Already implemented:

- WeChat 4.x source discovery and read-only SQLCipher database access;
- local database-key acquisition from the running client with cryptographic verification;
- canonical normalization for text and documented non-text/event types;
- reply/quote, forwarded bundle, link/app-share and provenance handling;
- SQLite archive with migrations and idempotent import behavior;
- deterministic monthly JSONL plus YAML/JSON catalogs;
- structured Fatal/Partial/Info diagnostics;
- CLI-only product surface and portable self-contained `win-x64` packaging;
- fixture and real-environment integration tests.

Not yet implemented as of the architecture update:

- Raw Vault capture/generation storage;
- Raw-Vault-only canonical rebuild;
- explicit capture vs ingest checkpoints;
- conversation-scoped incremental sync;
- Collection-backed sync/query workflows;
- `ArchiveQueryService` retrieval surface;
- SQLite FTS5/query/context commands;
- MCP transport.

## Reliability model

Reliability guarantees are explicit. Terms such as "safe", "atomic" and "durable" are not accepted without failure classes.

Current shipped anchors:

- **Phase 1 Export = R1** — normal success publishes complete new output; caught cancellation/I/O failure attempts in-process restoration; process crash and OS/power loss are not guaranteed.
- **Conversation Import = R2** — a Fatal source-coverage failure rolls back the conversation transaction under the documented importer contract.

The Raw Vault architecture adds separate capture/publication and rebuild reliability requirements; those must be implemented and tested before being treated as shipped guarantees.

See [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md) and [`docs/RAW_VAULT.md`](docs/RAW_VAULT.md).

## Export structure

Current machine export remains:

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

Exports are derived/interchange state. Interactive Harness retrieval should move to QueryService/CLI/MCP rather than treating monthly JSONL as the primary database.

See [`docs/EXPORT_PRD.md`](docs/EXPORT_PRD.md) and [`docs/MESSAGE_SCHEMA.md`](docs/MESSAGE_SCHEMA.md).

## Architecture

Target shape:

```text
Human / Agent / Script
          │
          ▼
CLI / future MCP
          │
          ▼
ArchiveQueryService ────────────────┐
          │                         │
          ▼                         │
Canonical Archive                  │
archive/wearchive.db                │
          ▲                         │
          │ normalize               │ export
          │                         ▼
Raw Vault                    JSONL/YAML/JSON
          ▲
          │ capture
          │
Live WeChat source
```

Project layering remains:

| Project | Role |
|---|---|
| `src/WeArchive.Core` | Domain, contracts, normalization, orchestration and query-service abstractions |
| `src/WeArchive.Infrastructure` | WeChat compatibility/key acquisition/SQLCipher, Raw Vault implementation, SQLite archive/indexes, export |
| `src/WeArchive.Cli` | Command parsing, human/JSON rendering, stdout/stderr/exit-code contract |
| `tests/WeArchive.Tests` | Unit, integration, CLI, compatibility, capture/rebuild and query contract tests |

Raw Vault does **not** replace the canonical model. It sits upstream of it.

## Scope and limitations

Current scope:

- local Windows WeChat 4.x source;
- normalized text/metadata archive;
- stable identity/conversation/message IDs;
- selective machine export;
- an agent/script-friendly CLI as the only shipped product surface.

Target additions:

- durable source-preservation Raw Vault;
- rebuildable canonical database;
- conversation/collection incremental synchronization;
- indexed query/search/context retrieval;
- Harness integration contract and optional MCP transport.

Binary media preservation remains separate. A Raw Vault milestone that preserves message/source database evidence does not automatically mean every image/audio/video/file payload is archived. Future media preservation requires its own contract.

Other out-of-scope/deferred areas include hosted multi-user service, macOS/Linux source capture, embedded AI providers, GUI/full-screen TUI, installer and automatic updater.

## Build and develop

```powershell
dotnet restore WeArchive.sln
dotnet build WeArchive.sln -c Release
dotnet test WeArchive.sln
```

Build and smoke-test the current release artifact:

```powershell
./scripts/pack-portable.ps1 -Version 0.2.0
./scripts/smoke-test-cli.ps1
```

## Documentation first

Read before coding:

| Document | Purpose |
|---|---|
| [`docs/PRD.md`](docs/PRD.md) | Product definition and requirements |
| [`docs/RAW_VAULT.md`](docs/RAW_VAULT.md) | Preservation, capture, generations and rebuild contract |
| [`docs/HARNESS.md`](docs/HARNESS.md) | Harness/Agent query/access contract |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | Technical layering and boundaries |
| [`docs/DATA_MODEL.md`](docs/DATA_MODEL.md) | Canonical schema, stable IDs and target checkpoint model |
| [`docs/EXPORT_PRD.md`](docs/EXPORT_PRD.md) | Export layout and selection semantics |
| [`docs/MESSAGE_SCHEMA.md`](docs/MESSAGE_SCHEMA.md) | Canonical message semantics |
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | Milestones and priorities |
| [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md) | Development/reliability governance |
| [`docs/CLI.md`](docs/CLI.md) | Current shipped CLI command/JSON/exit contract |
| [`docs/adr/0008-raw-vault-canonical-query-layers.md`](docs/adr/0008-raw-vault-canonical-query-layers.md) | Accepted layer-separation decision |
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

WeArchive is local-first and read-only toward live WeChat.

- WeChat database keys are never persisted, logged or exported;
- no code injection/hooking/debugger attachment is required by the current source adapter;
- chat content is never written to application logs;
- real Raw Vaults, canonical archives, exports and private datasets must never be committed to Git;
- a future Raw Vault must be independently recoverable from WeChat keys; if encrypted at rest, it must use WeArchive/user-owned key management rather than persisting upstream keys.

Core archive/query/export workflows are designed to work offline. Any future external AI/provider integration remains optional and explicit.
