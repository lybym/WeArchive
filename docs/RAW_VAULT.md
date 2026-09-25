# WeArchive Raw Vault

The Raw Vault is a preservation layer that captures a source-faithful snapshot of a supported
WeChat account *before* normalization. It is separate from the canonical SQLite archive
(`archive/wearchive.db`) and has its own format version, manifest and reliability contract.

Normative decisions are in [ADR 0008](adr/0008-raw-vault-storage-and-snapshot.md).

## 1. Purpose

- preserve source-faithful database/schema evidence (every table, column and row) before
  normalization, including fields the current parser cannot interpret;
- keep captured evidence readable when the live WeChat client or its database key are no longer
  available;
- never persist the upstream WeChat database key;
- publish immutable, versioned generations with verifiable checksums and provenance.

The Raw Vault is **not** the canonical archive. Canonical messages live in `wearchive.db`; the
Raw Vault holds the raw source snapshot that a future parser can re-process.

`wearchive rebuild` selects a reader by the preserved capture family, source version and
artifact format metadata. The initial reader supports decrypted WeChat for Windows 4.x SQLite
images from a generation marked `complete`; partial generations cannot establish complete
canonical coverage and are rejected. It reads those images directly from the verified generation and has no live-source or
database-key fallback. It creates the canonical target using the ordinary migration sequence,
checks SQLite integrity, and only then replaces the selected canonical file. A failed read,
normalization or validation leaves the selected archive untouched. Rebuild never writes into
Raw Vault generations; exports remain separate derived outputs.

`wearchive ingest` incrementally imports verified Raw Vault generations into an existing
canonical archive. It publishes one conversation at a time through the normal SQLite import
transaction and commits that conversation's ingest checkpoint in the same transaction. An
account-scope scan cursor separately records generation identities only after a complete
account-wide scan, so retries can discover conversations left unimported and skip generations
already fully examined. `--conversation` scopes the operation to one source conversation and
does not advance the account scan cursor; `--replay` reprocesses preserved generations after a
reader repair and invalidates prior scan coverage until the replay catches up.

## 2. Entities

Conceptual entities (independent of the canonical archive schema):

```text
RawVaultAccount  — one captured source profile
RawGeneration    — one published snapshot; logically immutable
RawArtifact      — one content object (e.g. a decrypted source database image)
CaptureRun       — provenance: adapter family/version, capture time, completeness, diagnostics
```

The Raw Vault format version is independent of the canonical SQLite, message-schema and
export-schema versions.

## 3. Physical layout

```text
<vault-root>/accounts/<account-id>/generations/<generation-id>/
  manifest.json
  artifacts/<sha256><ext>
```

- `<vault-root>` defaults to `%LOCALAPPDATA%\WeArchive\rawvault`.
- `<account-id>` is the stable account id (`a_<16-hex>`, [DATA_MODEL.md](DATA_MODEL.md) section 16).
- `<generation-id>` is `gen_<16-hex>`, derived from `StableIds.Generation(accountId, captureTime,
  adapterFamily, adapterVersion)`.
- Artifacts are named by their SHA-256 hex digest plus the original file extension, so identical
  content is stored once and content-addressable.

## 4. Manifest

`manifest.json` is a versioned JSON document. Field names are stable snake_case.

```json
{
  "manifest_version": 1,
  "vault_format_version": 1,
  "generation_id": "gen_...",
  "account_id": "a_...",
  "source_profile_id": "wxid_...",
  "source": {
    "adapter_name": "wechat-windows",
    "adapter_version": "0.1.0",
    "source_product_name": "WeChat for Windows",
    "source_version": "4.1.13.12"
  },
  "capture": {
    "capture_time": "2026-03-01T12:00:00+08:00",
    "capture_adapter_family": "wechat-windows",
    "capture_adapter_version": "0.1.0",
    "mode": "baseline",
    "completeness": "complete",
    "artifact_count": 5
  },
  "artifacts": [
    {
      "role": "source-database",
      "name": "message_0.db",
      "content_ref": "artifacts/<sha256>.db",
      "sha256": "<hex>",
      "size": 1048576,
      "source_format": "sqlite",
      "is_decrypted": true,
      "metadata": {
        "page_count": "256",
        "wal_frames_applied": "3",
        "wal_frames_rejected": "0",
        "was_plaintext": "false",
        "source_relative_path": "db_storage/message/message_0.db"
      }
    }
  ],
  "diagnostics": [],
  "previous_generation_id": null
}
```

### 4.1 Versioning

- `manifest_version` — the manifest's own structure version. Currently `1`.
- `vault_format_version` — the physical artifact layout version. Currently `1`.

These are independent of each other and of the canonical SQLite, message-schema and
export-schema versions.

### 4.2 Artifact roles

Artifact `role` values are source-neutral strings so the Raw Vault does not leak WeChat table
names into Core/CLI:

- `source-database` — a decrypted source database image (SQLite).

Future roles may include `schema-evidence`, `source-config` or derived dumps; they are additive
and do not change existing roles.

## 5. Consistent snapshot strategy

WeChat 4.x keeps its databases open in SQLCipher/WAL mode. An ordinary file copy of the `.db`
file may be a checkpoint older than the client's state, and the `.db` + `.wal` pair is encrypted
and unreadable without the key.

The WeChat capture adapter reuses `SqlCipherDatabaseCache`, which:

1. opens source files with shared read access (read-only);
2. replays only committed, HMAC-verified WAL frames up to the last commit marker;
3. materializes each encrypted database as a decrypted, ordinary SQLite image in a transient
   scratch directory.

The capture adapter copies each plaintext image into the Raw Vault as an artifact. The upstream
key is held only in memory and is never persisted. When the capture finishes, the scratch cache
is disposed, deleting the key and decrypted scratch material.

The resulting artifacts are **decrypted SQLite images**: source-faithful and readable without
the original WeChat key.

## 6. Publication and immutability

Publication is **publish-last**:

1. `BeginGenerationAsync` creates a `.staging` directory.
2. `WriteArtifactAsync` writes each artifact to the staging area and returns its descriptor
   with a verified SHA-256.
3. `PublishAsync` writes `manifest.json` to the staging directory and atomically renames it to
   its final location (`Directory.Move` on the same volume).

A generation is only discoverable after step 3. A failed or cancelled capture discards the
staging directory and publishes nothing — no incomplete generation can be mistaken for a
complete one.

Published generations are **immutable**: the store refuses to overwrite an existing generation
directory. Later captures create new generations linked by `previous_generation_id`, forming an
append-only chain. Later source deletion does not delete or rewrite earlier generations.

## 7. Completeness and failure modes

| Verdict | Meaning |
|---|---|
| `complete` | All required artifacts captured and verified; no Fatal diagnostic |
| `partial` | Optional evidence missing (e.g. some auxiliary database unreadable) but required evidence present; still a valid generation |
| `incomplete` | Required evidence missing or snapshot inconsistent; the generation is discarded, not published |

Fatal examples: required partition unavailable, inconsistent snapshot/WAL state, checksum
mismatch, source identity unavailable, key unavailable before capture, artifact publication
failure.

Caught cancellation/runtime/I/O failure discards the staging material best-effort and fails
explicitly. Process crash and OS/power loss are not guaranteed recovery classes. No persistent
recovery journal, rollback ledger or complex R3+ state machine is added.

## 8. Reliability level — R1

Raw Vault capture follows [R1](DEVELOPMENT.md#10.3-r1--in-process-replace-with-best-effort-restoration):

- **Success (A)**: one complete generation published with validated manifest and checksums.
- **Caught cancellation (B)**: staging discarded best-effort; nothing published.
- **Caught I/O error (C)**: same as B; failure reported.
- **Process crash (D)**: not guaranteed; a stale `.staging` directory is never discoverable.
- **OS/power loss (E)**: not guaranteed.

No journal, commit marker or rollback ledger is persisted.

## 9. Generation discovery and validation

- `ListGenerationsAsync(accountId)` — published generations ordered by their manifest
  `previous_generation_id` lineage; capture time orders independent roots and breaks ties.
- `GetLatestGenerationAsync(accountId)` — the last generation in publication-lineage order.
- `OpenGenerationAsync(accountId, generationId)` — opens a generation for read-only inspection,
  verifying every artifact's SHA-256. A tampered or corrupted generation is rejected (returns
  null) rather than trusted.

## 10. Key non-persistence

The upstream WeChat database key:

- is recovered read-only from the running client's memory (ADR 0005);
- is verified against real database pages before use;
- exists only in memory (`WeChatKeySet`) for the duration of the capture;
- is never written to the Raw Vault, the canonical SQLite, logs or CLI output;
- is deleted when the scratch cache is disposed.

Captured artifacts are **decrypted** content; they are readable without the key. Tests verify
that no key material appears in any manifest, artifact metadata or CLI output.

## 11. Layer separation

- **Core** (`WeArchive.Core`): `RawManifest`, `RawArtifactDescriptor`, `IRawVaultStore`,
  `ISourceCaptureAdapter`, `CaptureService`. No WeChat schema details.
- **Infrastructure/RawVault**: `RawVaultStore` (filesystem persistence),
  `RawManifestSerializer`. Treats artifacts as opaque content and orders them by manifest lineage.
- **Infrastructure**: `RawVaultIngestService` coordinates verified generations, generic source
  adapter reads, conversation publication and scoped checkpoints.
- **Infrastructure/WeChat**: `WeChatCaptureAdapter` — the only place WeChat database layout,
  key acquisition and SQLCipher decryption live; the captured WeChat reader remains behind this
  boundary.

## 12. CLI

```text
wearchive capture [--account <id>]
wearchive ingest --account <id> [--conversation <source-id>] [--replay]
```

`capture` is a thin adapter over `CaptureService`; `ingest` uses the Raw Vault as the read-only
source and writes canonical conversations transactionally. See [CLI.md](CLI.md) for the full
contract.
