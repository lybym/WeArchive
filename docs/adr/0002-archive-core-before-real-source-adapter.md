# ADR 0002 — Stabilize the archive core before the real source adapter

Status: accepted  
Date: 2026-09-13

## Context

The easiest way to begin this project would be to implement a source-specific extractor first and then shape the rest of the product around whatever data it exposes. That would couple the archive, CLI and exports to one client version and make future compatibility work expensive.

## Decision

M0 will stabilize the generic archive core before M1 implements the first real Windows WeChat source adapter.

M0 must provide:

- normalized domain models
- source-adapter protocol
- fixture/mock adapter
- import orchestration
- provenance
- idempotent archive persistence
- migrations
- diagnostics
- JSON/Markdown export
- automated tests

Only after those boundaries are working end to end should M1 connect real local source data.

## Alternatives considered

### Implement real source access first

Rejected because source-specific fields, ordering rules and edge cases would leak into the domain model and become difficult to undo.

### Build GUI first

Rejected because UI would stabilize workflows around an immature archive model and slow architectural iteration.

### Export directly from source

Rejected because it prevents durable normalization, incremental synchronization, provenance and later search/analysis.

## Consequences

Positive:

- Source compatibility work remains isolated.
- New adapters can reuse the same archive/search/export engine.
- Tests can run without a live client.
- Archive schema is shaped by product semantics rather than upstream tables.

Costs:

- The first visible end-user result arrives later than a one-off extractor.
- Fixture design must anticipate important source edge cases.

## References

- `docs/PRD.md`
- `docs/ARCHITECTURE.md`
- `docs/DATA_MODEL.md`
- `docs/ROADMAP.md`
