# ADR 0008 — Raw Vault storage and consistent snapshot

Status: proposed

Date: 2026-03-01

## Context

WeArchive M1.5 (Issue #22) introduces a preservation layer that exists *before* normalization:
the **Raw Vault**. The canonical SQLite archive stores normalized, source-independent messages,
but a source-faithful snapshot — every table, column and row exactly as the upstream client
wrote it — must also survive so that:

1. future parser improvements can re-process evidence the current parser cannot interpret;
2. unknown/source-specific fields remain available even when no canonical type maps to them;
3. a captured account remains readable when the live WeChat client or its database key are no
   longer available (the source is transient; the vault is durable).

The Raw Vault is separate from the canonical archive (`archive/wearchive.db`). It has its own
format version, its own manifest, and its own reliability contract.

### Design constraints from the Issue

- Source acquisition/decryption/snapshot logic must remain within the WeChat Infrastructure
  boundary; Raw Vault storage must not leak WeChat schema details into Core, CLI, query or
  export layers.
- The upstream WeChat database key must never be persisted, logged or exported.
- A consistent snapshot must be captured rather than unsafe ordinary DB/WAL file copying.
- Published generations are logically immutable; later capture never edits an earlier
  generation in place.
- No persistent recovery journal, rollback ledger or complex R3+ state machine may be added.
- The first implementation is baseline/full capture only.

## Decision

### 1. Persisted representation

A Raw Vault **generation** is a directory containing one `manifest.json` and an `artifacts/`
subdirectory:

```text
<vault-root>/accounts/<account-id>/generations/<generation-id>/
  manifest.json
  artifacts/<sha256><ext>
```

- **manifest.json** — a versioned JSON document (`manifest_version = 1`) recording source
  product/version, source profile, capture adapter family/version, capture time, completeness,
  every artifact's role/name/content-ref/SHA-256/size, diagnostics, and the previous
  generation id (append-only chain).
- **artifacts/** — opaque content objects. The store records the SHA-256 and size of each but
  never inspects artifact internals. WeChat schema details (table names, column names, message
  type codes) live *inside* the artifacts, not in the manifest's Core-visible fields.

The manifest format version is independent of the canonical SQLite schema version, the message
schema version, and the export schema version (docs/DATA_MODEL.md section 18).

### 2. Consistent snapshot strategy (WeChat 4.x)

The WeChat capture adapter reuses the existing `SqlCipherDatabaseCache`, which:

- opens source files with shared read access (read-only, NFR-02);
- replays only committed, HMAC-verified WAL frames up to the last commit marker (exactly as
  SQLite itself would recover them), so the snapshot is consistent rather than a torn copy;
- materializes each encrypted SQLCipher database as a decrypted, ordinary SQLite image in a
  transient scratch directory.

The capture adapter copies each plaintext image into the Raw Vault as an artifact. The upstream
key (`WeChatKeySet`) is held only in memory for the duration of the capture and is never written
to the vault, the canonical SQLite, logs or CLI output. When the capture finishes, the scratch
cache is disposed, deleting the key and the decrypted scratch material.

The resulting artifacts are **decrypted SQLite images**: source-faithful (every table, column
and row preserved, including fields the current parser cannot interpret) and readable without
the original WeChat key.

### 3. Publication and immutability

Publication is **publish-last**: artifacts are staged in a `.staging` directory and the manifest
is written only when the capture is complete. The staging directory is then atomically renamed
to its final location (`Directory.Move` on the same volume). A generation is only discoverable
after this rename — a failed or cancelled capture leaves no manifest and therefore no
discoverable generation.

A published generation is never overwritten: the store refuses to rename into an existing
generation directory (immutability guard). Later source deletion does not delete or rewrite
earlier generations.

### 4. Reliability level — R1

Raw Vault capture follows R1 (in-process publication):

| Failure class | Required behavior |
|---|---|
| A. Success | A complete generation is published with a validated manifest and checksums |
| B. Caught cancellation | The staging directory is discarded best-effort; no generation is published |
| C. Caught I/O/runtime error | Same as B; the failure is reported, not published as a generation |
| D. Process crash | Not guaranteed. A stale `.staging` directory may remain but is never discoverable; later runs may remove it |
| E. OS/power loss | Not guaranteed |
| F. Storage failure | Not guaranteed |

No persistent journal, commit marker, recovery ledger or cross-process transaction protocol is
introduced. This is deliberately bounded: process crash and OS/power loss are not guaranteed
recovery classes, matching the documented R1 level.

### 5. Completeness and failure modes

A generation is published as `complete` only when every required artifact validates and no Fatal
diagnostic was recorded. A Fatal source/WAL consistency or required-artifact coverage failure
causes the `CaptureService` to discard the staging directory and return a failure — the
generation is never published as complete. Partial diagnostics (e.g. rejected WAL frames,
optional database unreadable) may produce a `partial` generation that is still valid evidence,
but Fatal coverage never degrades to `complete`.

### 6. Layer separation

- **Core** (`WeArchive.Core`): source-independent contracts — `RawManifest`, `RawArtifactDescriptor`,
  `IRawVaultStore`, `ISourceCaptureAdapter`, `CaptureService`. No WeChat table names, column
  names or message type codes appear here.
- **Infrastructure/RawVault**: the filesystem store (`RawVaultStore`) and manifest serializer.
  Treats artifacts as opaque content.
- **Infrastructure/WeChat**: the `WeChatCaptureAdapter`, which knows WeChat's database layout,
  key acquisition and SQLCipher decryption. This is the only place WeChat schema details live.

## Alternatives considered

- **Copy the raw encrypted .db + .wal files.** Rejected: the files are SQLCipher-encrypted and
  unreadable without the key, which must not be persisted. The vault's purpose is to remain
  readable *without* the key.
- **Store a logical JSON/SQL dump instead of a decrypted SQLite image.** Rejected for the
  baseline: a decrypted SQLite image is maximally source-faithful (preserves exact schema,
  types, rowids and unknown columns) and is already produced by the existing `SqlCipherDatabaseCache`.
  A logical dump can be derived later from a captured generation without re-acquiring the key.
- **In-process transaction with a persistent commit marker.** Rejected: it would introduce R3+
  recovery machinery explicitly excluded by the Issue (non-goals) and by the AGENTS.md
  hard-stop rule. The publish-last rename satisfies the in-process R1 guarantee without a journal.

## Consequences

- The Raw Vault introduces an independently versioned persistent format with `manifest_version`
  and `vault_format_version`, separate from the canonical SQLite, message and export versions.
- Future parser improvements can reprocess evidence from captured generations without a running
  WeChat client or key.
- The vault root is a per-user application data directory (`%LOCALAPPDATA%\WeArchive\rawvault`).
- `wearchive capture` is a thin CLI adapter over `CaptureService`.
- This ADR does not authorize incremental capture, storage-dedup optimization,
  encryption-at-rest, or any R3+ crash-recovery protocol. The bounded Raw-Vault-only
  canonical rebuild is separately scoped and authorized by Issue #23; it adds no persistent
  crash-recovery protocol.

## References

- Issue #22 — Raw Vault baseline capture and immutable generation publication
- Issue #23 — Raw-Vault-only canonical rebuild
- `docs/RAW_VAULT.md` — Raw Vault specification
- `docs/ARCHITECTURE.md` — Raw Vault section
- `docs/DATA_MODEL.md` — Raw Vault entities section
- ADR 0005 — local key acquisition
- `docs/DEVELOPMENT.md` — Reliability Levels (R1)
