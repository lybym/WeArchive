# ADR 0008: Separate Raw Vault, Canonical Archive and Query Access Layers

- Status: Accepted
- Date: 2026-09-17

## Context

WeArchive originally treated normalized SQLite as the durable archive and JSONL/YAML exports as rebuildable derived data. That design correctly isolated query/export behavior from WeChat-specific schemas, but it left one preservation gap: once source records were normalized, source fields that the current parser did not understand could be lost permanently if the live WeChat databases later changed, were deleted or became unreadable.

At the same time, direct JSONL scanning is a poor default interface for interactive Harness/Agent retrieval at archive scale. Agents need a stable query contract without depending on SQLite internals or WeChat schemas.

The architecture therefore needs to satisfy three different concerns without conflating them:

1. preserve source evidence with high fidelity;
2. provide stable source-independent semantics and efficient query/indexing;
3. provide portable exports and agent-friendly access APIs.

## Decision

WeArchive adopts four distinct data/access layers:

```text
Live WeChat source
      │ capture
      ▼
Raw Vault
      │ reader / parser / normalizer
      ▼
Canonical Archive (archive/wearchive.db)
      │ ArchiveQueryService
      ├─ CLI --json
      └─ future MCP
      │
      └─ JSONL/YAML/JSON export
```

### Raw Vault

The Raw Vault is the **archival source of truth**.

It stores immutable, versioned, source-faithful captured evidence sufficient to re-run future readers/parsers without requiring the live WeChat source or original WeChat database key.

Upstream deletion does not imply Raw Vault deletion.

### Canonical SQLite archive

`archive/wearchive.db` is the **operational system of record** for product behavior.

It contains stable normalized entities, canonical messages, provenance and query indexes. It is allowed to omit source-specific details that remain preserved in the Raw Vault.

It MUST be rebuildable from the Raw Vault under the rebuild contract.

### Query access

Harnesses, agents and automation access canonical data through `ArchiveQueryService` exposed by CLI JSON and, later, MCP.

Normal callers do not directly query Raw Vault or rely on raw SQLite schema.

### Export

JSONL/YAML/JSON remain portable, auditable, rebuildable interchange formats. They are not the default interactive Harness query interface.

## Synchronization decision

Preservation and canonical ingestion are separate operations with separate progress state:

- capture checkpoint: live source -> Raw Vault;
- ingest checkpoint: Raw Vault -> canonical archive.

The ordinary `sync` workflow may orchestrate both, but a parser failure must not require already-captured data to be recollected from WeChat.

Collection is the shared reusable conversation scope for sync, query/Harness and export.

## Rebuild decision

A target `wearchive rebuild` operation recreates the canonical archive using the Raw Vault only.

Rebuild must not require:

- a running WeChat client;
- live-source discovery;
- reacquisition of the original WeChat database key.

Stable IDs must remain deterministic across rebuilds and parser upgrades for the same preserved source identities.

## Security decision

WeChat database keys are never persisted.

The Raw Vault must be independently recoverable from WeChat key-acquisition changes. If Raw Vault data is encrypted at rest, it uses WeArchive/user-owned key management independent of WeChat keys.

## Consequences

### Positive

- historical source evidence survives upstream deletion/schema changes;
- parser bugs and new message semantics can be corrected by rebuilding;
- WeChat compatibility remains isolated from QueryService/Harness integrations;
- interactive retrieval can use indexes/FTS rather than scanning JSONL;
- JSONL remains simple and portable without becoming a runtime database;
- canonical database migrations can be recovered from preserved evidence when required.

### Costs

- more local storage and lifecycle metadata;
- a new capture subsystem and generation/manifest contract;
- explicit capture and ingest checkpoints;
- additional security responsibility because preserved source data is durable;
- rebuild testing across reader/parser versions becomes mandatory.

## Rejected alternatives

### Make `wearchive.db` mirror WeChat's schema

Rejected because it would make query/export/Harness behavior depend on upstream version-specific tables/types and undermine the existing compatibility boundary.

### Let Harness query Raw Vault directly

Rejected because it would expose WeChat schema/version complexity to callers and make agent integrations unstable.

### Keep only canonical SQLite

Rejected as the long-term preservation model because unknown/unparsed source fields could become unrecoverable after the upstream source disappears.

### Make JSONL the primary Harness database

Rejected because recursive file discovery, parsing, filtering and context-window retrieval are inefficient and duplicate query/index responsibilities already suited to SQLite.

## Status and implementation note

This ADR is the accepted target architecture. It does not claim that Raw Vault, rebuild, QueryService, FTS or MCP are already shipped in 0.2.x. `ROADMAP.md` defines delivery order and acceptance criteria.