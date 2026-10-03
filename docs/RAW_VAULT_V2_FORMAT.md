# Raw Vault v2 persisted-format specification

Status: proposed by Issue #77 / ADR 0011

This document freezes the **persisted representation contract** needed by the Raw Vault v2 storage
engine. It is intentionally source-neutral. SQLite, SQLCipher, WAL and WeChat partition semantics do
not appear in this format.

The shipped product remains vault format 1 until the downstream implementation Issues are merged
and released.

## 1. Version tuple

The first v2 writer uses:

- `manifest_version = 3`
- `vault_format_version = 2`
- storage kind `fixed-block-map-v1`
- pack format `RVPK0001`
- map-node format version `1`

A reader MUST dispatch explicitly by supported versions. Unknown versions or unsupported
manifest/vault-format combinations fail closed.

## 2. Manifest-v3 artifact descriptor

A vault-v2 artifact no longer uses a generation-relative `content_ref` as its storage location.
The logical artifact fields remain source-neutral:

```json
{
  "role": "source-database",
  "name": "message_0.db",
  "sha256": "<lowercase full-artifact sha256>",
  "size": 1048576,
  "source_format": "sqlite",
  "is_decrypted": true,
  "storage": {
    "kind": "fixed-block-map-v1",
    "block_size": 4096,
    "block_count": 256,
    "root": "<lowercase map-object sha256>"
  },
  "metadata": {
    "source_relative_path": "db_storage/message/message_0.db"
  }
}
```

Rules:

- `sha256` is SHA-256 over the exact reconstructed artifact bytes, without domain separation, to
  preserve the historical full-artifact checksum meaning.
- `size` is the exact logical byte length.
- `block_size` MUST be one of 4096, 8192, 16384, 32768 or 65536.
- `block_count = 0` iff `size = 0`; otherwise
  `block_count = ceil(size / block_size)`.
- Every block except the final block has exactly `block_size` uncompressed bytes.
- The final block length is `size - block_size * (block_count - 1)`.
- `root` is a lowercase 64-hex digest of a typed map object.
- A generation may contain artifacts with different supported block sizes.
- Writer default block size is a writer policy, not a vault-format-global constant.
- When an existing v2 artifact is reused unchanged, the writer preserves its existing block size
  and root. It MUST NOT silently rechunk because a newer writer default changed.

Manifest-v1/v2 + vault-v1 keep their historical `content_ref` path semantics.

## 3. Typed object identity

All integer fields in binary canonical encodings are unsigned little-endian.

### 3.1 Data object

A data object contains one uncompressed artifact block.

Its digest is:

```text
SHA256(
  UTF8("wearchive/raw-vault/data/v1\0")
  || U64LE(uncompressed_length)
  || uncompressed_bytes
)
```

The nominal artifact block size, logical position, page number, generation id and account id do not
enter the digest.

Therefore equal block bytes with equal length may be reused across logical positions/generations in
the same account store.

### 3.2 Map object

A map object's digest is:

```text
SHA256(
  UTF8("wearchive/raw-vault/map/v1\0")
  || canonical_map_node_bytes
)
```

Data and map objects therefore occupy distinct hash domains even if their payload bytes are equal.

## 4. Persistent ordered block map

The first format uses fixed fanout **32**.

The tree is deterministic:

1. split the artifact into its ordered fixed-size data blocks;
2. group consecutive data references into leaf nodes of at most 32 entries;
3. group consecutive child-map references into internal nodes of at most 32 entries;
4. repeat until exactly one node remains;
5. an empty artifact uses one canonical empty leaf as its root.

No balancing, promotion or alternative packing strategy is permitted for map format v1. This keeps
the same logical block sequence deterministically mapped to the same root.

### 4.1 Node header

Every canonical map node starts with exactly 28 bytes:

| Offset | Size | Field | Value |
|---:|---:|---|---|
| 0 | 4 | magic | ASCII `RVMP` |
| 4 | 1 | format_version | `1` |
| 5 | 1 | node_type | `0 = leaf`, `1 = internal` |
| 6 | 1 | level | `0` for leaf; parent level = child level + 1 |
| 7 | 1 | flags | `0` |
| 8 | 2 | entry_count | U16LE, 0..32 |
| 10 | 2 | reserved | zero |
| 12 | 8 | subtree_block_count | U64LE |
| 20 | 8 | subtree_logical_bytes | U64LE |

Reserved/flags values other than zero are unsupported in map format v1.

### 4.2 Leaf entries

A leaf entry is exactly 36 bytes:

| Size | Field |
|---:|---|
| 32 | raw data-object digest bytes |
| 4 | U32LE uncompressed block length |

The header's `subtree_block_count` equals `entry_count`.
The header's `subtree_logical_bytes` equals the sum of entry lengths.

A non-final logical block whose entry length differs from the artifact descriptor's
`block_size` is invalid. The final logical block MUST have the exact descriptor-derived tail
length.

### 4.3 Internal entries

An internal entry is exactly 48 bytes:

| Size | Field |
|---:|---|
| 32 | raw child map-object digest bytes |
| 8 | U64LE child subtree block count |
| 8 | U64LE child subtree logical bytes |

The parent header totals MUST equal the sums of its child-entry totals.

All children of one internal node MUST have level exactly `parent.level - 1`.

### 4.4 Root validation

Before an artifact is accepted:

- root object kind must be map;
- root total blocks must equal descriptor `block_count`;
- root total logical bytes must equal descriptor `size`;
- traversed leaf order must produce exactly the descriptor's block sequence;
- every referenced data object must validate its typed digest and uncompressed length;
- reconstructed complete bytes must hash to descriptor `sha256`.

Map-root validation does not replace full artifact SHA-256 verification.

## 5. Pack format

Published objects are stored in immutable account-local pack files.

A pack is private while being appended. Once sealed and published it is never appended or modified.

### 5.1 Pack header

Every pack starts with 16 bytes:

| Offset | Size | Field | Value |
|---:|---:|---|---|
| 0 | 8 | magic | ASCII `RVPK0001` |
| 8 | 2 | pack_format_version | U16LE `1` |
| 10 | 2 | flags | zero |
| 12 | 4 | reserved | zero |

### 5.2 Object record

Each record begins at the current byte offset and uses a 48-byte header:

| Offset | Size | Field |
|---:|---:|---|
| 0 | 4 | ASCII `ROBJ` |
| 4 | 1 | record_version = 1 |
| 5 | 1 | object_kind: 1=data, 2=map |
| 6 | 1 | codec: 0=none, 1=zstd |
| 7 | 1 | flags = 0 |
| 8 | 4 | U32LE uncompressed_length |
| 12 | 4 | U32LE stored_length |
| 16 | 32 | raw typed-object digest |

The next `stored_length` bytes are the stored payload.

For `codec = none`, stored payload equals canonical uncompressed object bytes.

For `codec = zstd`, decompression MUST produce exactly `uncompressed_length` bytes before typed
digest verification. The encoder's Zstd compression level is not part of identity and is not stored
as object semantics.

The writer SHOULD use Zstd level 1 initially and MUST fall back to `none` when compression is not
beneficial after record overhead.

Unknown record versions, object kinds, codecs or nonzero flags fail closed.

### 5.3 Pack footer

A sealed pack ends with exactly 56 bytes:

| Offset | Size | Field |
|---:|---:|---|
| 0 | 8 | magic = ASCII `RVPKEND1` |
| 8 | 8 | U64LE record_count |
| 16 | 8 | U64LE content_length |
| 24 | 32 | SHA-256(pack bytes before footer) |

`content_length` is the byte length from the first byte of the pack header through the final byte
of the final object record; it excludes the footer.

Pack filenames are physical locators, not object identity. Repack/recompression may move an object
to another pack without changing any generation manifest or map root.

## 6. Derived SQLite lookup index

The object-location index is derived R0 state. Its exact on-disk SQLite schema is **not part of the
vault-format compatibility contract** because it may be discarded and rebuilt.

Its required logical mapping is:

```text
(object_kind, digest)
  -> pack_file, record_offset, codec,
     uncompressed_length, stored_length
```

The index MUST NOT be the only place that records enough information to validate a pack record.

On index version mismatch/corruption, the supported recovery is to discard/rebuild the derived
index by scanning sealed packs, not to rewrite generation manifests.

## 7. Publication and authority

For a successful v2 capture:

```text
private staging
  -> candidate objects/map nodes
  -> candidate pack(s)
  -> seal + validate complete pack(s)
  -> publish immutable pack(s)
  -> validate all generation roots + full artifact SHA-256
  -> write manifest in generation staging
  -> publish generation last
```

Authority:

- generation manifest: logical snapshot/provenance/coverage/checkpoint;
- sealed packs reachable from manifest roots: authoritative physical artifact content;
- object-location SQLite index: derived/rebuildable;
- materialized SQLite files/cache: derived/rebuildable.

An object/pack that exists but is unreachable from every retained generation may remain after an R1
caught failure. Its presence is a space leak, not evidence publication.

GC is not part of format-v2 core publication.

## 8. Compatibility dispatch

Required first-release read matrix:

| Manifest | Vault format | Behavior |
|---:|---:|---|
| 1 | 1 | read with legacy rules; no incremental checkpoint proof |
| 2 | 1 | read with shipped coverage/checkpoint rules |
| 3 | 2 | read through v2 artifact provider/materializer |
| other | other | fail closed unless a later format explicitly adds support |

A lineage may contain both format-1 and format-2 generations.

No preserved-read failure may silently fall back to live WeChat/key acquisition.

## 9. Benchmark decision for first writer default

Issue #77 compared block sizes 4/8/16/32/64 KiB over the same real dataset:
8 generations, 264 artifact references and 10.606 GB logical bytes.

Prototype results showed:

| Block | Zstd1 retained total MB | Post-baseline incremental total MB | Scenario T365 GB |
|---|---:|---:|---:|
| 4 KiB | 869.135 | 6.426 | 8.042 |
| 8 KiB | 835.413 | 8.614 | 10.780 |
| 16 KiB | 811.783 | 12.331 | 15.432 |
| 32 KiB | 810.572 | 19.699 | 24.652 |
| 64 KiB | 808.415 | 31.084 | 38.900 |

The total retained baseline/history at the observed eight-generation point is not the same metric as
long-term hourly incremental cost. The first writer therefore uses **4096 bytes as the provisional
default** because the accepted workload is long-running hourly capture and the scenario model
favored 4 KiB strongly.

This is not a claim that SQLite pages require 4 KiB storage blocks.

The storage-engine implementation Issue must reproduce the decision with the actual pack/index/map
implementation. A material divergence is a hard stop: update Issue #77/docs and approve the changed
default rather than silently changing the persisted behavior during implementation.

The official RC real-environment gate then validates the default against a continuous hourly trace.

## 10. Golden vectors

Machine-readable vectors are stored at:

`docs/fixtures/raw-vault-v2/golden-v1.json`

Implementations MUST reproduce those bytes/digests exactly before writing production v2 content.
