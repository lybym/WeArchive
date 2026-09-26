# ADR 0009: Application-level Collection configuration ownership

- Status: Accepted
- Date: 2026-09-26

## Context

The export PRD defines a reusable `collections.yaml` shape, but a generated export package is
rebuildable interchange data. Sync configuration must remain available when exports are absent,
regenerated, moved, or rebuilt from Raw Vault.

## Decision

The authoritative product Collection catalog is `%LOCALAPPDATA%/WeArchive/collections.yaml`,
with `schema_version: 1.0` and the documented `collections -> name -> conversations` shape.
Each member is a stable canonical conversation ID (`g_<16-hex>` or `u_<16-hex>`). The file is
outside canonical SQLite and Raw Vault, is user-maintained, and is never silently rewritten.
Missing configuration means an empty catalog. Invalid schema/YAML/IDs and duplicate membership
are deterministic errors.

An export package's `collections.yaml` remains a user-maintained interchange copy. It is not
read as sync configuration and export does not overwrite an existing package file. Canonical
rebuild does not touch the application catalog. Each Collection member is captured through the
shared `CaptureService` and ingested through the existing per-conversation Raw Vault transaction
and checkpoint. No separate sync-group model or checkpoint is introduced.

## Consequences

- Collection configuration survives archive rebuild and does not couple product behavior to an
  arbitrary export folder.
- Users edit the app-level catalog directly; no interactive editor is introduced here.
- Collection list/show and sync ship now; Collection query/search/export selection remains a
  follow-up.
- Existing conversation checkpoint semantics independently preserve successful member progress.
