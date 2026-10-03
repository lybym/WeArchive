# ADR 0011 — Raw Vault v2 fixed-block content-addressed artifact storage

Status: proposed

Date: 2026-10-03

Issue: #77

## Context

ADR 0010 established the Raw Vault preservation boundary: a capture produces a source-faithful,
key-independent logical generation before normalization; published generations are logically
immutable; capture is R1; and storage remains source-neutral.

The shipped vault-format-v1 representation stores each artifact as a complete file inside every
published generation. Incremental capture can reuse previously acquired evidence when source
fingerprints prove that a partition is unchanged, but v1 reuse still creates a new physical file
copy for the later generation.

That distinction was acceptable for the original capture milestone because physical deduplication
was explicitly deferred. It is not acceptable for the new operating target: at least one Raw Vault
capture per hour while retaining logical history long-term.

Read-only analysis of a real v0.5.1 vault with 8 generations, 264 artifact references and about
10.606 GB of logical artifact bytes showed that whole-file reuse is insufficient. A small change
inside a large SQLite artifact changes the artifact checksum and makes whole-file storage pay for
the complete file again. Fixed-size sub-file content reuse reduced the measured post-baseline
physical payload by orders of magnitude on that workload.

This ADR changes the physical preservation representation. It does not change what a logical
generation means.

## Decision

### 1. Logical generations remain the preservation contract

A RawGeneration remains a complete logical snapshot. Later capture never edits an earlier
generation. Coverage, capture checkpoints, lineage, source provenance and artifact-level SHA-256
remain part of the logical evidence contract.

Logical immutability does not require duplicate physical bytes.

### 2. Vault format 2 uses an account-local fixed-block content-addressed artifact store

The target representation is:

    logical artifact byte stream
        -> fixed-size blocks
        -> typed content-addressed data objects
        -> persistent ordered block map
        -> immutable sealed packs

The content-addressed store is account-local for the first v2 release. Objects may be shared
between generations and between artifacts of the same Raw Vault account. Cross-account
deduplication is not authorized by this ADR.

This storage engine is specific to Raw Vault artifact preservation. It is not a general-purpose
CAS library or object database product.

### 3. Storage blocks are source-neutral

SQLite, SQLCipher and WAL semantics remain inside Infrastructure/WeChat.

Infrastructure/RawVault receives a logical artifact byte stream and understands only:

- artifact logical length;
- a descriptor-selected fixed block size;
- ordered block identity;
- typed data/map objects;
- pack records and codecs;
- reconstruction and integrity verification.

The storage block size is not defined by SQLite page size.

Manifest-v3 readers support these block sizes:

- 4096 bytes;
- 8192 bytes;
- 16384 bytes;
- 32768 bytes;
- 65536 bytes.

One artifact uses one fixed block size. The final block may be shorter. A generation may contain
artifacts using different supported block sizes.

The first writer default is selected by the Issue #77 benchmark gate. The current provisional
candidate is 4096 bytes with Zstd level 1. Changing the writer default within the already-supported
set does not by itself require a new vault-format version.

An existing v2 artifact should retain its predecessor block size when reused. A deliberate
rechunk is a representation change with measurable cost and must not happen silently.

### 4. Artifact identity remains independent of physical representation

Every artifact retains:

- source-neutral role/name/provenance;
- logical size;
- full SHA-256 over the reconstructed artifact bytes;
- source format/decryption metadata where applicable;
- a storage descriptor naming the v2 representation.

The persistent map root is not a replacement for the artifact SHA-256.

Physical repack, recompression, lookup-index rebuild or a future explicit rechunk must not change
the logical artifact identity when reconstructed bytes are unchanged.

Data-object identity does not include generation id, page number or logical offset. Equal
uncompressed object bytes under the same typed-object identity rules may be physically reused.

Map-node identity is domain-separated from data-object identity and is computed from a canonical,
versioned node encoding.

### 5. Persistent ordered block maps use structural sharing

A v2 artifact descriptor points to an immutable ordered block-map root rather than embedding a
complete per-generation block-hash list.

The map is a fixed-fanout persistent tree. The canonical fanout and node encoding are frozen by
Issue #77 together with golden vectors. A no-change artifact reuses the same root and writes no new
map nodes. Appends or in-place changes create only the required new path/nodes while prior roots
remain readable.

There is no generation delta replay chain. Any one artifact root must describe enough information
to reconstruct that complete artifact from the object store.

### 6. Physical payload uses immutable segmented packs

Objects are stored in account-local pack files.

A pack is created in staging, appended while private, sealed, validated and then published. A
published pack is immutable and is never reopened for append.

Pack target size, materialization buffer size and compression level are implementation defaults,
not logical generation semantics, unless a future format revision explicitly makes them so.

The persisted record format identifies object kind, object digest, uncompressed length, stored
length, codec and format version. Unknown record or codec versions fail closed.

Compression is representation only:

- object identity is calculated over the canonical uncompressed typed object;
- supported first-release codecs are none and zstd;
- the initial writer uses Zstd level 1 when beneficial and otherwise stores the record raw;
- compression level is not object identity.

### 7. The lookup index is derived state

Each account may have a SQLite lookup index mapping object identity to pack/offset/length/codec.

The lookup index is R0 derived state:

- it is not evidence authority;
- losing it cannot make preserved evidence unrecoverable;
- it can be rebuilt by scanning authoritative sealed packs;
- a lookup hit never overrides object checksum/type/length validation.

The canonical archive database is not used for this index and receives no migration because of
Raw Vault v2.

### 8. Manifest and vault versions advance independently

The target v2 writer uses:

- manifest_version = 3;
- vault_format_version = 2.

Manifest version 3 is required because a RawArtifactDescriptor no longer means
"generation-relative ContentRef path". It carries a source-neutral storage descriptor such as:

    storage.kind        = fixed-block-map-v1
    storage.block_size  = <supported size>
    storage.block_count = <count>
    storage.root        = <typed map root>

The exact JSON field spelling and required/optional rules are frozen by Issue #77 format vectors.

Vault-format-v2 readers reconstruct artifact bytes through the storage engine. Vault-format-v1
readers continue to use generation-local files.

A new binary must support:

- existing readable manifest-v1/vault-v1 generations under their historical rules;
- manifest-v2/vault-v1 generations;
- manifest-v3/vault-v2 generations;
- lineages containing both vault-format 1 and vault-format 2 generations.

Unknown or unsupported combinations fail closed. There is no live-source fallback when a
preserved generation cannot be read.

Writing v2 does not require converting existing v1 generations. "Read old + write new" is the
default upgrade path.

### 9. Publication remains R1

Raw Vault capture remains R1.

For v2, publication order is constrained so a discoverable generation cannot name unpublished
objects:

1. create private staging;
2. write candidate objects/map nodes and candidate packs;
3. seal and validate new packs;
4. publish complete immutable packs;
5. validate the generation's reachable object closure and artifact checksums;
6. write the generation manifest in staging;
7. publish the generation last.

Caught failure or cancellation before generation publication must not publish a new Complete
generation or advance its capture checkpoint.

Unreferenced complete objects/packs may remain as orphaned physical space. That is an allowed R1
space leak, not evidence corruption.

This ADR does not authorize a persistent intent log, commit marker, rollback ledger,
multi-process transaction protocol or other R3+ recovery machinery.

Only one mutating Raw Vault operation may write a vault at a time. The first implementation may use
an OS-lifetime single-writer guard, but must not introduce persistent coordination state.

### 10. Source-side and storage-side increment metrics are distinct

For the WeChat adapter, source_changed_page_bytes may continue to use its known 4096-byte SQLite
page boundary as a source-side observation metric.

Raw Vault storage reports storage-block and physical metrics separately, including:

- logical_generation_bytes;
- storage_changed_block_bytes where measurable;
- rawvault_new_payload_bytes;
- rawvault_new_metadata_bytes;
- rawvault_unique_payload_bytes_total;
- rawvault_reachable_payload_bytes;
- rawvault_orphan_payload_bytes;
- rawvault_index_bytes;
- rawvault_pack_allocated_bytes;
- materialized_cache_bytes and scratch_peak_bytes when those facilities exist.

"Source directory net growth", "changed source pages" and "new Raw Vault physical bytes" are not
interchangeable quantities.

## Benchmark gate for the first writer default

Issue #77 selects the provisional writer default from the supported block sizes using a reproducible
benchmark with the candidate pack record, persistent map and derived lookup index.

For each candidate B:

    T365(B) = baseline_retained_bytes(B)
            + 8760 * mean_incremental_retained_bytes_per_capture(B)

T365 is a workload model until a real hourly trace exists. It must be labeled as estimated when
the source captures were not actually one hour apart.

All candidates must first prove byte-identical reconstruction and correct no-change, append,
in-place update, truncate and rewrite behavior.

Choose the smallest T365. If alternatives are within 5 percent, prefer the lower object count only
when latest/random artifact materialization p95 is no more than 10 percent worse than the
lowest-cost candidate.

The selected value is provisional for the first RC. The official RC real-environment gate must
validate it against a continuous hourly trace before Stable.

## Compatibility and migration

Existing v1 generations remain authoritative and immutable.

The first v2 release does not require:

- in-place rewrite of historical manifests;
- deletion of v1 artifacts;
- automatic migration;
- generation retention/pruning.

A future explicit v1-to-v2 optimize operation may copy and verify history into a separate v2 target,
but that is separately scoped work.

GC/pack compaction is also separate work. The v2 core must not depend on deleting old generations
to make hourly storage viable.

## Alternatives considered

### Whole-file hard links

Useful as a temporary mitigation for unchanged v1 artifacts, but they do not solve a large artifact
that changes by only a few blocks. They also add NTFS-specific link-count and shared-file mutation
risks. They are not the long-term architecture.

### Whole-file CAS

Eliminates identical complete files but has the same changed-large-artifact write amplification.

### Filesystem block cloning / ReFS

Can provide physical sharing on supported filesystems, but would make the preservation model depend
on filesystem capabilities that are not guaranteed on the normal NTFS installation.

### WAL archival or generation delta chains

Rejected as the v2 preservation representation. They introduce history-chain and source-lifecycle
requirements that are harder to make independently reconstructable and do not preserve the simple
"one generation/root describes one complete artifact" contract.

### One file per block

Rejected because a real baseline already implies hundreds of thousands of small objects.

## Consequences

- Logical snapshot semantics and physical storage are explicitly decoupled.
- Routine hourly capture can retain long history with physical growth dominated by new unique
  blocks and small shared-map metadata instead of whole changed database files.
- A damaged shared object can affect multiple generations; full artifact SHA verification remains
  mandatory when establishing artifact integrity.
- Reader complexity increases because v1 and v2 must coexist.
- Existing v1 bytes are not reclaimed automatically; the first benefit is a reduced future growth
  slope.
- GC, historical optimize, persistent materialized caches, direct SQLCipher-to-block streaming,
  WAL-assisted skip logic and encryption-at-rest remain separate future work.

## References

- Issue #77 — v2 docs/format/benchmark gate
- Issue #78 — snapshot-integrity hardening
- Issue #79 — v2 storage-engine implementation (blocked by #77)
- ADR 0008 — Raw Vault / canonical / query layer separation
- ADR 0010 — original Raw Vault storage and consistent snapshot
- docs/RAW_VAULT.md
- docs/DATA_MODEL.md
- docs/ARCHITECTURE.md
- docs/DEVELOPMENT.md
