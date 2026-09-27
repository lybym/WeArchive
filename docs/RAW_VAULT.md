# WeArchive Raw Vault

The Raw Vault is a preservation layer that captures a source-faithful snapshot of a supported
WeChat account *before* normalization. It is separate from the canonical SQLite archive
(`archive/wearchive.db`) and has its own format version, manifest and reliability contract.

Normative decisions for the storage and snapshot design are in [ADR 0010](adr/0010-raw-vault-storage-and-snapshot.md) (currently `Status: proposed`); the implemented behaviour is specified in this document and in [DEVELOPMENT.md](DEVELOPMENT.md) section 10.3.1.

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

Conversation-level coverage follows the same supported-evidence contract. WeChat creates a
conversation's message table only once it has records, so a conversation listed by `session.db` may
have no table in any message shard. When the generation used for the rebuild proves its Required
message evidence is complete — every Required message-bearing partition is `captured`/`reused` and
every message shard indexed successfully — such a conversation is **legitimately empty** and is
published with the framework's `no_new_records` info diagnostic instead of being reported as a
source-coverage failure. In every other case the Fatal semantics are unchanged: a Required message
partition that is missing, unavailable, unsupported or unreadable, or a generation that is not
`complete`, still fails that conversation rather than publishing an empty one. A conversation is
therefore never reported as complete while required evidence could actually be missing.

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

Issue #25 adds a manifest-version-2 capture checkpoint. It is embedded in the newly published
generation manifest, so capture progress advances only with successful publish-last publication.
It is independent of the canonical SQLite ingest checkpoint. Version-1 generations remain
readable, but cannot prove incremental safety and cause a full capture fallback.

Each version-2 manifest records `coverage` entries keyed by opaque source partition ID. An entry
states `captured`, `reused`, `unavailable`, or `unsupported`, with a source fingerprint and artifact
checksum when present. A complete capture records a versioned `capture_checkpoint` with the
generation ID, adapter family/version, and the fingerprint of each covered partition. Partial
capture does not advance this checkpoint. Later capture verifies the previous generation and
checkpoint before reusing any artifact. If source fingerprints cannot establish safety, capture
widens to a full consistent snapshot. No-change capture still publishes a new immutable generation
after verifying every expected partition fingerprint; it reuses preserved artifacts and records
explicit coverage rather than claiming an unobserved source was checked. Disappeared partitions
are reported as unavailable and cannot delete prior generations.

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
  "manifest_version": 2,
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
  "coverage": [
    {
      "partition_id": "db_storage/message/message_0.db",
      "status": "captured",
      "source_fingerprint": "<opaque hex>",
      "artifact_sha256": "<hex>"
    },
    {
      "partition_id": "db_storage/session/session.db",
      "status": "reused",
      "source_fingerprint": "<opaque hex>",
      "artifact_sha256": "<hex>"
    }
  ],
  "capture_checkpoint": {
    "version": 1,
    "generation_id": "gen_...",
    "capture_adapter_family": "wechat-windows",
    "capture_adapter_version": "0.1.0",
    "partition_fingerprints": {
      "db_storage/message/message_0.db": "<opaque hex>",
      "db_storage/session/session.db": "<opaque hex>"
    }
  },
  "previous_generation_id": null
}
```

### 4.1 Versioning

- `manifest_version` — the manifest's own structure version. Current writes use `2`; version `1`
  remains readable.
- `vault_format_version` — the physical artifact layout version. Currently `1`.

These are independent of each other and of the canonical SQLite, message-schema and
export-schema versions.

### 4.2 Artifact roles

Artifact `role` values are source-neutral strings so the Raw Vault does not leak WeChat table
names into Core/CLI:

- `source-database` — a decrypted source database image (SQLite).

Future roles may include `schema-evidence`, `source-config` or derived dumps; they are additive
and do not change existing roles.

### 4.3 Coverage and the capture checkpoint

`coverage` records what this generation actually accounts for, keyed by an opaque
source-partition id (never a chat content value):

| `status` | Meaning |
|---|---|
| `captured` | The partition was read now and materialized into an artifact of this generation |
| `reused` | The partition fingerprint matched the previous complete generation, so its verified artifact was carried into this generation |
| `unavailable` | The partition was expected (or previously captured) but could not be read, or is absent from the live source |
| `unsupported` | The partition exists but is outside this adapter version's supported evidence contract (Known unsupported), or has no approved classification yet (Unknown/unclassified) |

#### Source-partition support policy

Filesystem discovery and product support are deliberately separate concepts. An adapter may discover
physical source files that are not part of the evidence contract it currently supports. A source
partition is classified by the source adapter for the observed source/version; the presence of a
`*.db` file alone never makes that partition required.

For the WeChat 4.x adapter, use these semantic classes:

- **Required** — evidence required by the currently supported canonical/rebuild contract. A missing or
  unreadable Required partition prevents the generation from being `complete`.
- **Supported auxiliary** — evidence the adapter supports preserving in addition to the required
  canonical minimum. If such evidence is present but cannot be captured, the generation is
  `partial`; absence is not invented as evidence.
- **Known unsupported** — a discovered partition outside the adapter's current support contract. It
  remains visible as `coverage.status = unsupported` with an explicit diagnostic, but does not by
  itself downgrade an otherwise complete generation and is not part of the capture checkpoint.
- **Unknown/unclassified** — newly discovered evidence for which the adapter has no approved support
  decision. It must be diagnosed explicitly and the generation must not be reported `complete`
  until the partition is classified; unknown evidence is never silently treated as Known
  unsupported.

Accordingly, `complete` means the adapter captured/reused and verified all evidence in its current
supported contract that is required for a complete snapshot; it does **not** mean every physical
`*.db` below the source data directory was decryptable. The capture-required evidence set and the
evidence required by the current Raw-Vault-only rebuild reader must remain consistent.

Classification is an explicit allowlist inside the WeChat infrastructure boundary
(`WeChatSourcePartitionPolicy`), never "everything that is not required is auxiliary". For the
current WeChat 4.x release line the concrete classification is:

| Class | WeChat 4.x partitions |
|---|---|
| Required | `session/session.db`, `contact/contact.db`, `message/message_<n>.db`, `message/biz_message_<n>.db` |
| Supported auxiliary | `bizchat/bizchat.db`, `chatbot/chatbot_message.db`, `contact/contact_fts.db`, `emoticon/emoticon.db`, `favorite/favorite.db`, `favorite/favorite_fts.db`, `general/general.db`, `hardlink/hardlink.db`, `head_image/head_image.db`, `message/media_<n>.db`, `message/message_fts.db`, `message/message_resource.db`, `message/weclaw.db`, `sns/sns.db`, `solitaire/solitaire.db`, `third_app_icon/third_app_icon.db` |
| Known unsupported | `migrate/unspportmsg.db` |
| Unknown | every other discovered partition, including any other `migrate/` database |

Matching ignores case and accepts `\` and `/` as the same separator. A database's `-wal`/`-shm`
siblings are fingerprint inputs, not partitions, and are never classified or recorded in coverage.

WeChat 4.x splits conversation tables across both the `message_<n>.db` family and the
`biz_message_<n>.db` family, which is where official-account (`gh_`) conversations live. The
canonical rebuild reader cannot read those conversations without the `biz_message_<n>.db`
partition, so it is Required evidence rather than auxiliary, and the live locator and the
captured-source reader share one definition of "message shard" so the capture-required set and the
rebuild-required set cannot drift apart again. `media_<n>.db` holds no conversation tables and
stays auxiliary.

For the current WeChat 4.x release line, `migrate/unspportmsg.db` is **Known unsupported**. The current
canonical reader/rebuild contract does not consume it, so capture must account for its presence as
unsupported rather than require a database key/materialized artifact. Reclassifying it or adding
canonical semantics for it requires a later approved product change.

A Known-unsupported partition is accounted for by a `coverage` entry with `status = unsupported` and
an informational `partition_unsupported` diagnostic, and is never materialized, never fingerprinted
and never part of the capture checkpoint. An Unknown/unclassified partition is also recorded as
`unsupported` coverage, but with a partial-severity `partition_unclassified` diagnostic naming that
partition, and it forces the generation to `partial` — so it can never be reported `complete` and
never publishes a checkpoint until its support semantics are classified. Unknown evidence is never
silently aggregated away or treated as Known unsupported.

`expected` in the CLI rollup is the number of coverage entries, i.e. every partition this run
accounted for — not a claim that every theoretical source partition was observed.

Each entry also carries an optional `diagnostic`: the engineering reason a partition was not
captured. It never contains a fingerprint, a checksum or source content, so it can be surfaced by
the CLI (see [CLI.md](CLI.md)) and audited later without weakening the no-secret contract.

`capture_checkpoint` is a versioned capture cursor, and **not** an ingest cursor:

- it is written only inside a manifest that is being published, so publish-last publication is
  the only way it can advance;
- it is only written for a generation whose completeness is `complete`; every Required/Supported
  partition in that complete generation is `captured` or `reused` with a recorded fingerprint and
  a checksum that names an artifact of that same generation. Known-unsupported coverage entries may
  remain `unsupported` and are excluded from checkpoint fingerprints/reuse;
- a `partial` generation records coverage but leaves `capture_checkpoint` absent, so the next run
  widens to a full snapshot instead of resuming from weaker evidence;
- a Fatal failure, caught cancellation or I/O error discards the staged generation, so the
  previously published checkpoint is unchanged and stays the latest;
- it can legitimately be fresher or staler than the canonical SQLite ingest checkpoint
  (`docs/DATA_MODEL.md` section 21). Neither is ever inferred from the other.

Incremental safety is proven per partition, from the source itself, by the capture adapter. For
WeChat 4.x this is a content fingerprint of the database file and its committed WAL bytes; the
volatile `-shm` index is deliberately excluded because it changes without any content change.
When a fingerprint cannot be recomputed, the previous generation cannot be mapped onto the live
partitions, the adapter version changed, or the previous generation is not a complete
version-2 generation with a matching checkpoint, capture widens to a full consistent snapshot
and reports the `capture_full_fallback` diagnostic. Widening is never silent.

A no-change capture has one deterministic behaviour: it still verifies every expected partition
fingerprint, still publishes a new immutable generation that reuses the already-verified
artifacts, and still advances the checkpoint to that new generation. It never claims that
unobserved source material was checked, and it never deletes or rewrites an earlier generation.

Incremental capture reduces **reacquisition**, not source I/O. Every run re-verifies the
predecessor generation's artifacts (opening a generation re-hashes all of them), fingerprints each
expected live partition before and after the snapshot, and re-hashes every reused copy into the new
generation. That work is what makes reuse trustworthy, so `mode = incremental` must not be read as
"cheap" for a multi-GB account.

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

Incremental capture reuses the same read-only boundary: before decrypting anything the adapter
computes a per-partition fingerprint over the source database file and its committed WAL bytes
(never `-shm`), then fingerprints every expected partition again after the snapshot is written.
A partition whose fingerprint is unchanged reuses the artifact already published by the previous
complete generation; a changed partition is materialized again; a partition that changes while
it is being read makes the generation Fatal, because neither the copied old artifact nor the new
image can be claimed as the current source state. The upstream WeChat files are only ever opened
for reading.

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

Reading preserved evidence never modifies or deletes it, and never leaves anything behind:

- the plaintext materialization cache only ever deletes an image it created itself in its own
  scratch directory — never a preserved artifact, and never an unencrypted source file;
- a preserved image is opened with SQLite's `immutable=1` semantics. A preserved image is a
  self-contained, checkpointed image whose committed state is entirely in the main file, so it can
  be read without locking it and without SQLite creating `-wal`/`-shm` sidecars inside the
  generation directory. A live source database is deliberately **not** opened this way, because its
  current state can live only in its write-ahead log.

Reading a generation therefore leaves its directory byte-for-byte as published.

## 7. Completeness and failure modes

| Verdict | Meaning |
|---|---|
| `complete` | All required artifacts captured and verified; no Fatal diagnostic |
| `partial` | Optional evidence missing (e.g. some auxiliary database unreadable) but required evidence present; still a valid generation |
| `incomplete` | Required evidence missing or snapshot inconsistent; the generation is discarded, not published |

Fatal examples: required partition unavailable, inconsistent snapshot/WAL state, checksum
mismatch, source identity unavailable, key unavailable before capture, artifact publication
failure, a partition that changes while it is being fingerprinted or read.

Issue #37 adds exactly three diagnostics to that model:

| Code | Severity | Meaning |
|---|---|---|
| `partition_unsupported` | `info` | A discovered partition is a known, explicitly classified partition outside the adapter's supported evidence contract. It is recorded as `unsupported` coverage and does not by itself downgrade `complete`. |
| `partition_unclassified` | `partial` | A discovered partition has no approved support classification for this adapter version. It is recorded as `unsupported` coverage, is named in the message, and forces `partial` so the generation cannot be reported `complete` and publishes no checkpoint. |
| `capture_completeness_downgraded` | `partial` | Defensive Core guard: a capture adapter reported complete coverage while the run carried an `unavailable` coverage entry or a partial-severity diagnostic, so the generation was recorded as `partial` instead. |

The `complete` verdict and the checkpoint are also enforced on the read side: a manifest that claims
`complete` while carrying `unavailable` coverage, or a checkpoint that omits a captured/reused entry,
addresses an `unsupported`/`unavailable` entry, or disagrees on a fingerprint or artifact, is rejected
rather than trusted ([DATA_MODEL.md](DATA_MODEL.md) section 21.6). `manifest_version` stays `2`: the
persisted structure is unchanged and only which coverage entries the checkpoint addresses is
clarified, so version-2 manifests written before this change remain readable.

Incremental capture adds exactly one diagnostic: `capture_full_fallback` (severity `info`). It
means incremental safety could not be proven and the run read the whole source instead. It is
informational, not a failure: the published generation is an ordinary complete or partial
generation, and the message states which precondition was missing.

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

An account directory can exist without any published generation: a capture that failed or was
cancelled leaves the account/generations scaffold behind. Such an account has no evidence, so
`wearchive rebuild` reports it explicitly and skips it rather than aborting the rebuild for every
other account. A generation that does exist but cannot be read, validated or normalized is still a
hard failure; and if no account has a published generation at all, rebuild refuses to publish an
empty archive rather than replacing a usable one.

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
