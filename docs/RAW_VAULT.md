# WeArchive Raw Vault and Rebuild Contract

## 1. Purpose

The Raw Vault is WeArchive's preservation layer for the user's own local WeChat data.

Its job is different from the canonical SQLite archive:

- the **Raw Vault** answers: **what did the upstream source contain at capture time?**
- `archive/wearchive.db` answers: **what is WeArchive's current canonical interpretation of that preserved evidence?**

The Raw Vault is the **archival source of truth**. The canonical SQLite archive is the **operational system of record** and is rebuildable from the Raw Vault.

This document defines the target architecture. The 0.2.x implementation still imports directly from the live WeChat source and does not yet provide the Raw Vault/capture/rebuild workflow.

See also [ADR 0008](adr/0008-raw-vault-canonical-query-layers.md), [ARCHITECTURE.md](ARCHITECTURE.md), [DATA_MODEL.md](DATA_MODEL.md) and [ROADMAP.md](ROADMAP.md).

## 2. Data-layer model

```text
L0  Live Source
    WeChat local data
        │
        │ capture
        ▼
L1  Preservation Layer
    Raw Vault
    immutable generations + manifests
        │
        │ read / parse / normalize
        ▼
L2  Semantic Layer
    archive/wearchive.db
    canonical, indexed, queryable
        │
        ▼
L3  Access Layer
    ArchiveQueryService
    CLI / future MCP
        │
        ▼
L4  Interchange Layer
    JSONL / YAML / JSON exports
```

Only L1 is treated as non-reproducible preserved evidence. L2-L4 must be rebuildable from lower layers under their documented contracts.

## 3. Preservation principles

### 3.1 Capture before interpretation

Capture should preserve source data with the smallest practical semantic transformation. Parsing and normalization happen after capture.

A field that the current parser does not understand must not be discarded merely because it has no canonical meaning today. Future readers/parsers must be able to re-interpret preserved source records.

### 3.2 Upstream deletion is not archive deletion

Raw Vault synchronization is **preservation**, not mirroring.

If a record existed in an earlier successful generation and later disappears from the live WeChat source, that disappearance MUST NOT delete the preserved record or an earlier generation.

```text
source at T1: A B C D
source at T2: A D E

preserved history: A B C D + later E/changes
```

Deletion/purge of preserved evidence requires an explicit user operation; it is never inferred from source absence.

### 3.3 Immutable generations

Every successful capture publishes an immutable logical generation.

Example layout:

```text
source/
└─ a_<account-id>/
   ├─ catalog.db                  # implementation detail; optional
   └─ generations/
      ├─ 000001/
      │  └─ manifest.json
      ├─ 000002/
      │  └─ manifest.json
      └─ 000003/
         └─ manifest.json
```

A generation records what source artifacts and logical ranges were successfully captured at that point in time. A published generation is never modified in place.

Physical storage MAY deduplicate unchanged files/pages/chunks. Immutability is a logical contract, not a requirement to copy every byte on every run.

### 3.4 First full capture, later incremental capture

The target synchronization behavior is:

1. first successful capture establishes a complete baseline for the supported source scope;
2. later captures use capture checkpoints/source change evidence to acquire only new or changed material where safely supported;
3. when incremental safety cannot be established, fall back to a wider/full consistent capture rather than silently claiming completeness;
4. every generation records completeness and diagnostics explicitly.

Incremental optimization must never weaken the preservation contract.

## 4. Source fidelity

The preferred Raw Vault representation preserves a recoverable, source-faithful view of the WeChat databases and their source-version metadata.

The capture layer should avoid translating upstream records into a reduced `RawMessage` schema before preservation. Otherwise unknown fields can be lost before future parsers have a chance to understand them.

A Raw Vault generation should retain, where applicable:

- source product and source/client version;
- source profile/account identity;
- source database/partition logical role;
- source schema/version evidence;
- a consistent recoverable representation of database content;
- artifact hashes/checksums;
- capture time and capture-tool version;
- coverage/completeness diagnostics;
- enough metadata to select a compatible reader later.

## 5. Encryption and key independence

The Raw Vault MUST NOT depend on reacquiring the original WeChat database key in the future.

Therefore a successful capture must publish a representation that remains readable when:

- WeChat is uninstalled;
- the client cannot start;
- process-memory key acquisition changes;
- SQLCipher parameters change in later WeChat versions.

WeChat database keys MUST NOT be persisted in the Raw Vault, logs, manifests or canonical archive.

If Raw Vault data is encrypted at rest, that encryption must use WeArchive-owned/user-owned key management independent of the upstream WeChat key. The exact at-rest mechanism is a separate implementation/security decision.

## 6. SQLite/WAL consistency

Capture MUST NOT be implemented as an unsafe sequence of ordinary file copies from a live SQLite/SQLCipher database when that could produce a database/WAL mismatch.

A capture implementation must establish a documented consistent source snapshot using an approach appropriate to the supported WeChat version. WAL/partition completeness must be validated and surfaced in generation diagnostics.

A generation with a Fatal source-coverage failure is not published as a complete generation.

## 7. Capture manifest

A generation manifest is durable metadata. The exact JSON schema is versioned, but conceptually contains:

```json
{
  "manifest_version": 1,
  "generation": 42,
  "captured_at": "2026-09-17T14:00:00+08:00",
  "source_product": "WeChat for Windows",
  "source_version": "4.x",
  "source_profile_id": "wxid_...",
  "capture_adapter_family": "wechat-windows",
  "capture_adapter_version": "...",
  "completeness": "complete",
  "artifacts": [
    {
      "logical_role": "message_partition",
      "source_locator": "...",
      "content_ref": "...",
      "sha256": "..."
    }
  ],
  "diagnostics": []
}
```

`source_locator` is provenance, not a requirement that the original path still exist during rebuild.

## 8. Capture checkpoint vs ingest checkpoint

Preservation progress and canonical-ingest progress are different state and MUST be modeled separately.

### Capture checkpoint

Tracks what has safely entered the Raw Vault from the live source.

Conceptually:

```text
account
  └─ source/partition cursor(s)
```

### Ingest checkpoint

Tracks what Raw Vault evidence has been processed into the canonical archive.

Conceptually:

```text
account
  └─ conversation
      └─ generation / partition cursor(s)
```

A parser failure must not force already-preserved source data to be recollected from WeChat. After parser repair, ingest can resume/replay from the Raw Vault.

The shipped migration-1 `source_checkpoints` table is an earlier generic checkpoint design. A future migration may replace/refine it with explicitly scoped capture/ingest checkpoint storage. See [DATA_MODEL.md](DATA_MODEL.md).

## 9. Rebuild contract

`wearchive rebuild` is a target command whose defining contract is:

> Recreate the canonical archive from the Raw Vault without accessing the live WeChat source.

A rebuild may recreate:

- `archive/wearchive.db`;
- canonical accounts/participants/conversations/messages;
- reply relationships and provenance;
- FTS/search indexes;
- derived statistics/indexes.

Exports remain a separate derived operation and may be regenerated after rebuild.

A rebuild MUST NOT require:

- a running WeChat client;
- the original WeChat database key;
- the original source files still being present outside the Raw Vault.

## 10. Reader evolution

Raw Vault generations are versioned source evidence. Readers/parsers may evolve independently.

Example:

```text
Raw generations 1-80  (WeChat 4.x)
        │
        └─ WeChat4CapturedSourceReader

Raw generations 81+   (future WeChat schema)
        │
        └─ WeChatNextCapturedSourceReader

both
  ↓
Normalizer
  ↓
Canonical Archive
```

Reader implementation versions MUST NOT change stable identity namespaces. `wechat-windows` is a logical source/adapter family; parser implementation versions are metadata, not new identity domains.

## 11. Stable-ID rebuild invariant

Given preserved source identities, a rebuild MUST reproduce the same deterministic account, participant, conversation and message stable IDs.

This protects:

- aliases;
- collections;
- export paths;
- message references;
- Harness/MCP references;
- external automation state.

Changing a parser version is not sufficient reason to change a stable ID.

## 12. Canonical archive relationship

`archive/wearchive.db` is intentionally allowed to contain less source-specific detail than the Raw Vault. Its job is stable semantics and efficient operations.

It is the authoritative runtime dataset for:

- query;
- FTS;
- timeline/range retrieval;
- statistics;
- collections;
- Harness/Agent access through `ArchiveQueryService`;
- export generation.

Harnesses and normal product workflows MUST NOT read Raw Vault databases/files directly.

## 13. Media boundary

The initial Raw Vault milestone is scoped to the source database/data needed to reconstruct canonical message records and provenance.

It does **not** automatically imply preservation of every external image/audio/video/file binary referenced by WeChat. A future Media Vault/binary-preservation milestone must define those semantics separately.

Therefore "full backup" must be qualified:

- Raw Vault target: high-fidelity preservation of supported message/source database evidence;
- future media preservation: binary payload/assets when explicitly implemented.

## 14. Target CLI semantics

Planned command separation:

```text
wearchive capture                 # live WeChat -> Raw Vault only
wearchive sync --conversation ... # capture as needed + ingest selected scope
wearchive sync --collection ...   # capture as needed + ingest collection scope
wearchive rebuild                 # Raw Vault -> new canonical archive; no live source
```

`sync` is the ordinary convenience workflow; `capture` and `rebuild` are explicit preservation/recovery operations.

## 15. Acceptance criteria

The first Raw Vault milestone is complete only when:

1. a supported account can be captured into a versioned immutable generation;
2. capture does not intentionally modify WeChat data;
3. a later source deletion does not erase older preserved evidence;
4. the published generation has checksums, source-version metadata and completeness diagnostics;
5. a fresh canonical archive can be built using only the Raw Vault;
6. rebuild does not acquire/read a live WeChat key;
7. rebuild reproduces stable IDs for unchanged preserved source identities;
8. unknown/unparsed source fields are not discarded by the preservation layer merely because the current normalizer does not understand them;
9. capture and ingest progress are independently resumable/replayable under documented reliability rules;
10. automated tests cover at least one parser upgrade/rebuild scenario.