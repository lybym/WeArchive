# WeArchive Raw Vault

The Raw Vault is a preservation layer that captures a source-faithful snapshot of a supported
WeChat account *before* normalization. It is separate from the canonical SQLite archive
(`archive/wearchive.db`) and has its own format version, manifest and reliability contract.

Normative decisions for the shipped v1 snapshot/storage design are in [ADR 0010](adr/0010-raw-vault-storage-and-snapshot.md). The v2 physical-storage evolution is specified by [ADR 0011](adr/0011-raw-vault-v2-content-addressed-storage.md) and Issue #77. The dual-format reader supports manifest/vault versions 1/1, 2/1 and 3/2. Capture writes vault format 2 / manifest version 3 (Issue #83), without migrating historical v1 generations.

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
canonical coverage and are rejected. V1 images retain their generation-relative file semantics;
v2 images are reconstructed into temporary leased files and verified before SQLite opens them.
The reader has no live-source or database-key fallback. It creates the canonical target using the ordinary migration sequence,
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

This rule is deliberately asymmetric between the captured-rebuild path and the live source path.
A rebuild reads a published generation whose manifest — coverage plus the capture checkpoint — can
*prove* its Required message evidence is complete, so an absent table is source truth. The live
source path has no generation and no checkpoint: nothing distinguishes "WeChat never created this
table" from "the shard that holds it was not seen", so the live reader keeps the Fatal
`partition_missing` semantics for a table-less conversation. The direction must not be "aligned"
away: aligning the live path would publish conversations as empty without proof, and aligning the
rebuild path to the live semantics would discard a proof the manifest genuinely carries (Issue #39).
The completeness proof itself is cross-checked against the capture checkpoint (section 4.3), so
coverage entries alone are never accepted as evidence.

`wearchive ingest` incrementally imports verified Raw Vault generations into an existing
canonical archive. It publishes one conversation at a time through the normal SQLite import
transaction and commits that conversation's ingest checkpoint in the same transaction. An
account-scope scan cursor separately records generation identities only after a complete
account-wide scan, so retries can discover conversations left unimported and skip generations
already fully examined. `--conversation` scopes the operation to one source conversation and
does not advance the account scan cursor; `--replay` reprocesses preserved generations after a
reader repair and invalidates prior scan coverage until the replay catches up. Both explicit
`ingest` modes preserve historical traversal: an uncovered partial generation that the operation
reaches fails closed before that conversation's canonical publication or checkpoint can commit.

`wearchive sync --conversation` and `wearchive sync --collection` capture once through
`CaptureService`, then ingest each selected conversation from that run's exact published
generation. A complete generation is self-contained: unchanged evidence is referenced through verified immutable roots
into it, so an unrelated older partial generation is not part of the live sync's read. A partial
or otherwise incomplete current generation still fails closed. The live path advances only the
selected conversation checkpoint(s); it does not mark an account-wide historical scan complete.
This selection rule is intentionally different from explicit `wearchive ingest`, which traverses
uncovered history, and `wearchive ingest --replay`, which deliberately revisits that history.
`wearchive rebuild` reads only the latest published generation for each account and requires that
generation to prove complete coverage; it neither edits historical generations nor replays every
ancestor (docs/CLI.md, Issue #49 / #63).

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

### 3.1 Shipped vault format 1

```text
<vault-root>/accounts/<account-id>/generations/<generation-id>/
  manifest.json
  artifacts/<sha256><ext>
```

- `<vault-root>` defaults to `%LOCALAPPDATA%\WeArchive\rawvault`.
- `<account-id>` is the stable account id (`a_<16-hex>`, [DATA_MODEL.md](DATA_MODEL.md) section 16).
- `<generation-id>` is `gen_<16-hex>`, derived from `StableIds.Generation(accountId, captureTime,
  adapterFamily, adapterVersion)`.
- In vault format 1, each published generation owns its physical artifact files. Content identity
  prevents duplicate artifact files within one generation, but cross-generation `reused` evidence
  is still physically copied by the shipped implementation.

This distinction is intentional historical behavior: Issue #25 shipped incremental **acquisition**
reuse, not cross-generation incremental physical storage.

### 3.2 Vault format 2 — reader and capture writer supported

Issue #77 / ADR 0011 authorizes the following representation. The read path and exact persisted encoding are supported as specified in [RAW_VAULT_V2_FORMAT.md](RAW_VAULT_V2_FORMAT.md); [RAW_VAULT_V2_BENCHMARK.md](RAW_VAULT_V2_BENCHMARK.md) records prototype cost evidence and the correctness gate. The production capture writer is implemented by Issue #83:

```text
<vault-root>/
  accounts/<account-id>/
    generations/<generation-id>/
      manifest.json
    objects/
      packs/
        <pack-id>.rvpk
      lookup.sqlite         # derived / rebuildable
```

A manifest-v3 / vault-v2 artifact is a complete logical artifact described by:

- logical size;
- full artifact SHA-256;
- a supported fixed `block_size`;
- ordered block count;
- an immutable persistent-map root.

The map resolves through account-local typed content-addressed objects stored in immutable sealed
packs. The lookup SQLite database is derived state and may be rebuilt from the packs.

Capture retains the complete plaintext scratch image. Newly captured artifacts use the provisional
4096-byte writer default, while predecessor v2 artifacts keep their block size; identical logical
bytes retain the exact map root even when a source fingerprint changed. Valid v1 reuse goes through
the versioned artifact provider and imports the verified bytes into the block store, leaving all
historical files untouched. Packs are sealed, validated and published before every generation root
and full artifact checksum is verified; only then is the staging manifest written and the generation
published last. Cancellation/I/O failures publish no generation/checkpoint; unreachable packs may
remain as an R1 space leak. Capture success is retained if subsequent canonical ingest fails.

Capture results expose logical bytes and newly published data bytes/blocks, map nodes and pack
bytes/count. These metrics are in-memory observations and are never manifest/checkpoint authority.

The Raw Vault storage layer remains source-neutral. SQLite page size, SQLCipher and WAL behavior do
not enter the v2 storage contract; those remain WeChat-adapter concerns.

The v2 reader accepts fixed block sizes of 4096, 8192, 16384, 32768 and 65536
bytes. The writer default is selected by Issue #79 after its correctness matrix and actual-engine
benchmark. The actual-engine correctness gate passed and selected 4096 bytes + Zstd level 1 with raw fallback
as the provisional first-RC writer default. This is not a statement
that storage blocks must equal SQLite pages.

Writing v2 does not migrate or rewrite existing v1 generations. The intended upgrade path is
**read old + write new**.

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

- `manifest_version` — the manifest's own structure version. **Current writes use 3**;
  version 1 remains readable. The v2 storage format uses manifest version 3 because the
  artifact descriptor no longer means a generation-relative file path.
- `vault_format_version` — the physical artifact layout version. **Current writes use 2**.
  Historical generation-local artifacts use vault format 1 and remain readable.

These are independent of each other and of the canonical SQLite, message-schema and export-schema
versions. New readers must dispatch explicitly by supported manifest/vault-format combinations;
unknown combinations fail closed rather than being interpreted as a live-source fallback.

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

The classification rests on recorded structural evidence, not convenience (Issue #39). On the real
account that exposed the Issue #37 defect, `migrate/unspportmsg.db` is 8192 bytes — two database
pages, structurally incapable of holding the conversation tables the canonical contract reads — and
it admitted no verifiable database key. On the second real account observed on the same machine the
partition is entirely absent. Both facts are consistent with a migration-staging database that
never holds required evidence, so its presence can neither be required for a complete capture nor
silently ignored: it stays visible as unsupported coverage with an explicit diagnostic.

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
  (`docs/DATA_MODEL.md` section 21). Neither is ever inferred from the other;
- the captured-source rebuild reader cross-checks Required message-shard coverage against
  `capture_checkpoint` before it may treat a table-less conversation as legitimately empty: the
  counted shards must appear in the checkpoint with the source fingerprints their coverage entries
  record, so a complete version-2 coverage shape without a matching checkpoint — or with checkpoint
  evidence that disagrees with its own coverage — is not proof (Issue #39).

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

### 4.4 Canonical coverage interpretation (Issue #51)

The manifest's coverage entries are the evidence source for one source-neutral,
application-level rollup (`CanonicalCoverage`, `WeArchive.Core.RawVault`). It answers the
canonical question — *was the supported evidence required by the canonical result actually
available and complete?* — for humans, scripts and agents, without inspecting WeChat tables, Raw
Vault paths, manifest internals or checkpoint rows. It maps the persisted manifest as follows:

| Field | Meaning |
|---|---|
| `verdict` | `complete` only when the generation is `complete`, no `unavailable` evidence and no unclassified evidence remain; `incomplete` otherwise. Unknown/unclassified evidence can never silently produce a complete verdict, even if a manifest claimed one. |
| `expected` | Every coverage entry the generation accounts for — the same rule as the capture CLI's `expected`. |
| `available` | `captured` plus `reused` entries. The split is acquisition metadata and is deliberately merged: canonical coverage answers whether the evidence was available, not how it was reacquired. |
| `unavailable` | `unavailable` entries. |
| `known_unsupported` | `unsupported` entries accounted for by the informational `partition_unsupported` diagnostic. Explicit, and never by itself incomplete. |
| `unclassified` | `unsupported` entries accounted for by the partial `partition_unclassified` diagnostic, plus any unsupported entry the rollup cannot attribute to a policy diagnostic. Conservative. |

Rules the interpretation preserves:

- known-unsupported and unknown/unclassified evidence are distinguished by the diagnostic codes
  the source-partition policy already records (section 4.3), never by re-classifying source
  partitions outside the source/preservation boundary;
- the rollup is a pure function of the manifest, so the same verified generation and reader policy
  always produce the same coverage;
- it is distinct from the ingest-progress `conversation_coverage` checkpoint cursor
  ([DATA_MODEL.md](DATA_MODEL.md) section 14.1): the cursor records that a newer generation was
  verified unchanged so future ingest work can skip it; canonical coverage states whether the
  evidence behind a canonical result was complete. Neither is ever inferred from the other;
- the R2 ingest path refuses a generation whose coverage is not complete before any canonical
  write (`IncompleteCanonicalCoverageException`, carrying the coverage rollup), so a
  `succeeded`/`no_change` canonical result is always `complete` and an incomplete read surfaces as
  a structured failure (docs/CLI.md `incomplete_coverage`), never as a successful complete result;
- the rollup introduces no persistent state: it is a report over already-published evidence, not a
  checkpoint, journal or R3+ mechanism, and no coverage-reporting failure mutates canonical
  messages or checkpoints.

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

For WeChat Windows adapter `0.4.0`, encrypted source pages are authenticated before decryption;
main database and plaintext database lengths must end on a complete SQLite page. Both encrypted
and plaintext WALs are checked for the supported SQLite WAL magic/version/page size, header and
rolling frame checksums, generation salts, and transaction commit/database-size markers. Encrypted
WAL frames additionally require SQLCipher page HMACs. Plaintext sources are scanned before SQLite's
read-only backup so a backup that silently falls back to the main file cannot hide malformed or
incomplete WAL evidence. Only frames through the last valid commit are applied by the encrypted
materializer. Each commit is checked against the preceding logical database size: every newly
exposed page must have distinct valid frame evidence in that transaction. Repeated frames for one
page cannot fill a gap. A committed truncate discards removed pages; subsequent regrowth requires
fresh evidence for every removed page it exposes again, even if the main image or an earlier WAL
transaction held authenticated copies. Missing evidence rejects that transaction, retaining only
the last valid committed image with partial coverage and no checkpoint. Encrypted replay resizes
the image at each accepted commit, so the final `page_count` and artifact bytes describe exactly
that image. `wal_frames_applied` counts every frame replayed in accepted transactions, including
frames whose pages are removed by a later accepted truncate.
SQLite may reuse a WAL without truncating
it after a checkpoint; a frame whose salts differ from the current WAL header marks the end of the
current generation, and bytes beyond that logical boundary are ignored as leftovers. A short tail
before a stale-generation boundary, a malformed header/frame, failed page authentication, or
unmaterializable plaintext WAL is recorded as unavailable/partial coverage; it cannot produce a
complete generation or advance its capture checkpoint. Plaintext DB + WAL
snapshots are made through SQLite's read-only backup API so committed plaintext WAL rows are not
omitted and the source remains untouched. A mutation detected by the existing before/after
source fingerprints makes the capture incomplete and discards staging.

These checks establish authenticated pages and a committed SQLite snapshot under the supported
adapter contract; they are not a general logical-content proof. Plaintext DB/WAL backup images are
checked with SQLite `quick_check`. Encrypted SQLCipher images are validated at the page-HMAC and
WAL-protocol boundary; running ordinary SQLite structural validation over the decrypted image is
not reliable for the supported SQLCipher reserved-page layout, so no B-tree or application-level
consistency claim is made for that path. SQLite's generic `quick_check` also does not verify
external-content FTS index synchronization; an FTS-specific integrity command can report an index
mismatch even when `quick_check` returns `ok`. Tests assert this distinction and do not treat a
successful quick check as proof of FTS index consistency. Real WeChat 4.x FTS shards remain part of
the real environment acceptance gate.

The capture adapter version is bumped when these source-consistency semantics change. Existing
generation manifests remain immutable and readable, but checkpoints created by adapters `0.1.0`,
`0.2.0` or `0.3.0` are not reused by `0.4.0`; the next capture widens to a full materialization before recording a new
checkpoint.

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

## Read-only vault inspection (Issue #84)

`wearchive vault stats` and `wearchive vault verify` inspect **all retained generations** in
v1, v2 or mixed accounts, independently of live WeChat, keys or canonical SQLite. Both currently
perform a full observed scan: validate manifests/lineage and all sealed packs, traverse artifact
maps, and stream each artifact through its historical size/SHA-256 checks. `stats` does not
claim cheap directory-size estimates. `verify` is the explicit expensive integrity entry point.

`VaultInspectionService` owns these rules in Infrastructure/RawVault; the CLI only parses options
and renders results. Pack locations are derived in memory from authoritative sealed packs.
The on-disk lookup index is opened read-only for consistency diagnostics and never used to establish
artifact integrity. Missing, corrupt or inconsistent indexes are reported as rebuildable when
packs validate; inspection does not rebuild them on disk. No evidence, index, writer lock, cache,
capture checkpoint or canonical database is created or modified, including on caught failure or
cancellation. This adds no publication/recovery protocol or persisted schema.

Accounting deduplicates typed objects within each account, never across accounts. Logical generation
bytes sum artifact references, including shared artifacts in every retained generation. V1 whole
artifact bytes count distinct generation-relative files. Unique v2 payload and map metadata lengths
are uncompressed; separate stored byte metrics describe record payload representation. All physical
record payload bytes and duplicate record bytes remain visible. Unique stored byte values choose the
first record in ordinal pack-path order if duplicates have different representations. Pack logical
bytes include headers, framing, duplicate records and footers. Pack allocated bytes use Windows
`FILE_STANDARD_INFO.AllocationSize` and become unavailable if the filesystem/API cannot report them.

Reachability is the union of every retained artifact's map/data closure; unreferenced validated data
and map bytes are reported separately. Orphan sealed packs are scanned and validated too. Generation
and pack staging files are counted as temporary bytes, never evidence. There is no persistent
materialized cache; its metric is not applicable. Historical scratch peaks, source changed pages,
storage changed blocks and a write-amplification denominator cannot be reconstructed from a retained
vault and remain unavailable/not applicable. No zero-denominator ratio is fabricated.

Unsupported/corrupt authoritative content is an explicit failure with account, generation, artifact
and object/pack details where available. The first failure terminates the scan and **all aggregate
metrics are withheld**; partial counts are never presented as verified totals. Run inspection while
capture is idle: this is an observed read, not a concurrent-writer snapshot or multi-process protocol.
