# ADR 0009: Collection Configuration Ownership

- Status: Accepted
- Date: 2026-09-18

## Context

`Collection` is already the documented shared scope abstraction for a named set of conversations
(`docs/PRD.md` FR-23, `docs/HARNESS.md` section 8, `docs/EXPORT_PRD.md` section 8). Its durable
configuration shape is documented too:

```yaml
schema_version: 1.0
collections:
  ai-toy:
    conversations:
      - g_0123456789abcdef
      - u_1123456789abcdef
```

What the documentation did not settle is **which** durable store is authoritative for
user-maintained Collection definitions once Collections become a sync scope rather than only an
export artifact:

- `EXPORT_PRD.md` section 3 places `collections.yaml` inside each generated export package and
  section 8 says an export writes the empty shape only when the file does not exist. That makes the
  package copy *derived output*: any export package, at any path, may contain a stale or empty copy,
  and nothing distinguishes an intentional Collection set from an artifact a previous export
  happened to create.
- Canonical SQLite (`archive/wearchive.db`) is explicitly **rebuildable** from the Raw Vault
  (`docs/PRD.md` G2/FR-11, ADR 0008). Storing the only copy of a user-maintained Collection
  definition there means a rebuild — which creates a fresh migrated database — silently discards
  them unless rebuild is given an explicit preservation rule for Collection rows.

Issue #26 requires an authoritative application-level source plus an ADR for its ownership, and
explicitly forbids making an arbitrary generated export package authoritative and prefers not to
make rebuildable canonical data the only copy.

## Decision

### The authoritative store

The authoritative Collection configuration is **one user-maintained application-level YAML file**:

```text
%LOCALAPPDATA%\WeArchive\collections.yaml
```

It reuses the documented `collections.yaml` semantic shape, including its top-level
`schema_version`, so the durable format is shared rather than reinvented and no second
configuration vocabulary appears.

Only this file is authoritative for product behavior. The file at the same path inside a generated
export package is derived output that remains subject to the existing export rules
(`docs/EXPORT_PRD.md` sections 3.2, 8 and 15): an export writes the empty shape only when it is
absent and never overwrites it, and the application **never reads** it to resolve a Collection.
`sync`, `collection list` and `collection show` all resolve through the one authoritative file.

### Membership identity

Collection membership keys are **stable conversation IDs** (`g_<16 hex>` / `u_<16 hex>`,
`docs/DATA_MODEL.md` section 16) and nothing else. A direct conversation's stable ID is its peer's
`u_…` identity, so those two prefixes cover every conversation a Collection can name.

Declared entries are validated exactly against that form. A value that is not a stable conversation
ID — an upstream `source_id`, a display name, a truncated or uppercase ID, a message or account ID
— is reported as an invalid entry and never coerced into a different conversation. Entries that
repeat an earlier valid entry are reported as duplicates. The application never rewrites the file to
"fix" either case.

### Validation and failure semantics

- absent path or absent file → empty catalog; `collection list` reports no Collections and
  resolving a name is a deterministic `collection_not_found`;
- unreadable file, invalid YAML, an unsupported `schema_version`, a missing `collections` mapping
  or an empty Collection name → a deterministic configuration failure
  (`collection_config_invalid`, CLI exit 2). The file is diagnosed, never regenerated or silently
  treated as empty;
- invalid or duplicated membership entries → reported per Collection; the resolved membership is the
  valid, first-seen, de-duplicated set.

`CollectionCatalogService` owns these semantics in `WeArchive.Core`; the Infrastructure layer only
reads the file and deserializes the documented shape, so Core performs no file I/O and takes no YAML
dependency.

### No second Collection concept

This decision adds no `sync-group`, `watch-list` or `harness-dataset` model or persistence. A scope
either consumes `Collection` or it does not exist.

## Consequences

### Positive

- Collection definitions are readable, diffable and hand-maintainable, and a user can back them up
  without exporting anything;
- they survive `wearchive rebuild` and any canonical migration because they are not stored in the
  rebuildable database at all, so rebuild needs no Collection-preservation rule;
- one authoritative location removes the ambiguity between "the Collections I maintain" and "the
  Collections a generated package happens to contain";
- invalid configuration is visible instead of silently disabling a user's Collections.

### Costs

- Collections are local configuration, not queryable canonical data: a future Collection
  query/export integration must resolve through this catalog rather than join against SQLite;
- a Collection file cannot be edited through an interactive CLI editor in this Issue, so
  maintenance is a text edit;
- because stable conversation IDs embed the account id, membership is account-scoped. A Collection
  member that belongs to a different account than the resolved capture account is reported as an
  unresolved member rather than silently remapped. Phase 1 archives one account, so this is a
  reporting rule rather than a limitation today.

## Rejected alternatives

### Make the export package `collections.yaml` authoritative

Rejected: an export package is arbitrary generated output at a user-chosen `--output` path. Treating
it as product configuration would make sync behavior depend on which package the user last exported
and would let a re-export with an empty shape silently disable a Collection set.

### Store Collections only in canonical SQLite

Rejected for this Issue: canonical SQLite is rebuilt from the Raw Vault, and no rebuild-preservation
semantics for user-maintained Collection rows exist. Making rebuildable data the only copy would put
user-maintained definitions at risk of silent loss. A later migration to SQLite is possible, but only
together with an explicit rebuild-preservation rule.

### Introduce a Collection-specific SQLite database or a second configuration file

Rejected: it would create a second configuration store next to the documented
`collections.yaml` semantics for no benefit, and duplicate ownership is exactly the ambiguity this
ADR removes.

### Normalize membership entries on read (case folding, prefix repair, source-id resolution)

Rejected: silently rewriting or coercing membership would mean the effective scope differs from what
the user wrote, and a typo would resolve to a conversation the user did not name.

## Status and implementation note

Issue #26 implements this decision: `collection list`, `collection show <name>` and
`sync --collection <name>` resolve from this file, `sync --collection` captures live-source evidence
through the shared `CaptureService` and ingests each member through the Raw Vault ingest path, and
each conversation advances its own ingest checkpoint independently.

This ADR does **not** authorize Collection keyword/query filtering, Collection-scoped export
selection, an interactive Collection editor, an MCP/scheduler surface or a second Collection concept.
Those remain follow-up scope. It also changes no reliability level: no journal, commit marker or new
transaction protocol is introduced.