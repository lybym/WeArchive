# Raw Vault v2 storage benchmark evidence

Issues: #77, #79

Date: 2026-10-04

Purpose: record the privacy-safe aggregate evidence used to choose the **provisional first-RC writer
default** for Raw Vault v2.

This document does not contain personal chat content, source paths, database names from the private
profile, keys or artifact bytes.

## 1. Dataset

Read-only production Raw Vault sample:

- 8 published Complete generations;
- 264 logical artifact references (33 per generation);
- 10,606.375 MB total logical artifact bytes across generations;
- all sampled SQLite artifacts used 4096-byte source pages;
- existing vault representation: manifest version 2 / vault format 1;
- no cross-generation hard links.

The sample intervals were not uniformly one hour. Therefore yearly figures below are capacity
**scenarios**, not observed annual forecasts.

## 2. Candidate storage blocks

The same artifacts were simulated using fixed blocks of:

- 4 KiB;
- 8 KiB;
- 16 KiB;
- 32 KiB;
- 64 KiB.

Each candidate was measured raw and with Zstd level 1. Content identity was based on uncompressed
block bytes. A fanout-32 structurally shared ordered map was used for the metadata comparison.

The temporary analysis only read production Raw Vault files. Simulated payloads and reconstructed
files were written outside the production vault and removed after measurement.

## 3. Capacity results

Decimal MB:

| Block | Unique raw payload | Zstd1 payload | Data objects | Object/index metadata model | Map content + record/index model |
|---|---:|---:|---:|---:|---:|
| 4 KiB | 1,334.321 | 824.571 | 325,762 | 31.273 | 13.019 |
| 8 KiB | 1,341.907 | 812.580 | 163,825 | 15.727 | 6.834 |
| 16 KiB | 1,352.032 | 799.930 | 82,550 | 7.925 | 3.656 |
| 32 KiB | 1,369.334 | 804.270 | 41,826 | 4.015 | 2.014 |
| 64 KiB | 1,396.367 | 804.980 | 21,340 | 2.049 | 1.114 |

The object/index values above came from the prototype accounting model, not the final product SQLite
index. The actual-engine results below supersede the prototype decision figures.

Total retained bytes at the observed 8-generation point:

| Block | Raw total MB | Zstd1 total MB |
|---|---:|---:|
| 4 KiB | 1,378.885 | 869.135 |
| 8 KiB | 1,364.740 | 835.413 |
| 16 KiB | 1,363.885 | 811.783 |
| 32 KiB | 1,375.636 | 810.572 |
| 64 KiB | 1,399.802 | 808.415 |

Post-baseline incremental totals (G2-G8):

| Block | Raw MB | Zstd1 MB |
|---|---:|---:|
| 4 KiB | 13.840 | 6.426 |
| 8 KiB | 19.700 | 8.614 |
| 16 KiB | 29.022 | 12.331 |
| 32 KiB | 45.778 | 19.699 |
| 64 KiB | 72.620 | 31.084 |

The observed snapshot-total minimum and the long-running incremental minimum are therefore not the
same:

- raw total at this point: 16 KiB is smallest;
- Zstd1 total at this point: 64 KiB is smallest;
- **post-baseline incremental retained cost: 4 KiB is smallest**.

The accepted product workload is long-running hourly capture, so the incremental cost is the primary
selection signal.

## 4. Performance results

Single-run warm-cache prototype measurements:

| Block | Hash CPU | Zstd1 CPU | Latest materialization MiB/s raw / Zstd1 | Random historical MiB/s raw / Zstd1 |
|---|---:|---:|---:|---:|
| 4 KiB | 18.55 s | 7.69 s | 205.3 / 157.0 | 209.4 / 160.5 |
| 8 KiB | 16.05 s | 6.47 s | 209.4 / 182.6 | 242.8 / 182.1 |
| 16 KiB | 14.39 s | 5.86 s | 273.1 / 203.0 | 306.0 / 215.3 |
| 32 KiB | 13.23 s | 4.88 s | 343.6 / 248.5 | 364.8 / 252.4 |
| 64 KiB | 11.53 s | 4.48 s | 260.1 / 195.2 | 399.1 / 278.9 |

Random materialization used a fixed random seed and the same 24 historical artifacts, about
1.241 GB logical data.

Limitations:

- prototype implementation, not the final .NET engine;
- warm OS cache;
- one measurement pass;
- does not include live source fingerprint, key acquisition or SQLCipher decryption;
- final pack segmentation and SQLite index query behavior were not implemented;
- these numbers are design evidence, not a release SLA.

## 5. Provisional hourly capacity model

Until a real hourly trace exists:

    T365(B) = baseline_retained_bytes(B)
            + 8760 * mean_incremental_retained_bytes_per_capture(B)

Using the first retained generation as the baseline and the seven observed transitions to estimate
mean incremental bytes per capture:

| Block | Baseline retained MB | Mean incremental MB/capture | T365 scenario retained GB |
|---|---:|---:|---:|
| 4 KiB | 862.709 | 0.918 | 8.904 |
| 8 KiB | 826.799 | 1.231 | 11.607 |
| 16 KiB | 799.452 | 1.762 | 16.231 |
| 32 KiB | 790.873 | 2.814 | 25.443 |
| 64 KiB | 777.331 | 4.441 | 39.677 |

The baseline is observed retained Zstd1 bytes for generation 1 (eight-generation total minus the
seven post-baseline transitions). T365 applies the Issue #77 formula in decimal GB. It means: "if
every future hourly capture introduced the same mean amount of previously unseen unique content as
the seven measured transitions." It is not an empirical one-year forecast. Annualized incremental
bytes alone are 8.042, 10.780, 15.432, 24.652 and 38.900 GB respectively; those values exclude the
baseline and are not T365.

## 6. Correctness gate status

The Issue #77/#79 benchmark selection rule requires every candidate to pass correctness checks before
its storage cost is considered. The implementation test run passed the candidate matrix for all five
block sizes with both raw and Zstd1/raw-fallback storage. It verifies byte-identical reconstruction
and full SHA-256, empty and partial-tail artifacts, no-change reuse, append, in-place update,
truncate, rewrite, golden encodings, unknown-version/corruption rejection, and immutable pack/index
recovery cases. The focused suite result was 29 passed and 1 environment-gated benchmark skipped;
the full non-live-adapter suite result is recorded in the Issue #79 PR.

| Required check | Recorded result in this analysis |
|---|---|
| Byte-identical reconstruction for each block size and codec | PASS; every block size and both codec paths covered |
| Unknown-field preservation | PASS; opaque bytes in the source stream survive byte-identical reconstruction |
| No-change root/object reuse | PASS; roots and existing objects are reused |
| Append | PASS |
| In-place update | PASS |
| Truncate | PASS |
| Rewrite | PASS |

The run used .NET SDK 10.0.401 on Windows. Private source bytes were not emitted or retained.

The golden vector remains a compact set of encoding examples. Multi-level fanout-32 maps,
descriptor-tail reconstruction, empty-artifact reconstruction and corruption rejection are covered
by the implementation tests in addition to the golden fixture.

## 7. Provisional hourly capacity model

The v2 reader accepts 4096/8192/16384/32768/65536-byte block sizes. The actual-engine default decision
is recorded in section 10 below.

## 8. Reproduction gate in Issue #79

Issue #77 is docs/format-first and therefore does not implement the production storage engine. Issue
#79 has now recorded the correctness matrix above and reproduced the storage-cost comparison with
the actual implementation.

Issue #79 MUST run the correctness gate, reproduce the cost comparison, and select the first-RC
default after the actual:

- pack framing from [RAW_VAULT_V2_FORMAT.md](RAW_VAULT_V2_FORMAT.md);
- persistent map;
- Zstd/raw selection;
- derived SQLite lookup index;
- filesystem allocated-byte accounting

exist in product code.

A material divergence is a hard stop. Do not silently select or change the writer default inside
#79. Instead:

1. publish the new measurements;
2. update Issue #77-derived docs/decision with rationale;
3. obtain review of the changed default;
4. then continue implementation.

## 9. Stable gate

The official RC real-environment acceptance must replace this scenario model with a continuous
hourly trace before Stable.

That acceptance must report at least:

- successful/failed scheduled capture points;
- logical generation bytes;
- new payload bytes;
- new metadata bytes;
- allocated pack/index bytes;
- no-change captures;
- actual capture duration;
- materialization/rebuild behavior without live WeChat/key;
- any workload event such as VACUUM/rewrite that legitimately causes large historical deltas.

If the real hourly trace materially reverses the selected default, adjust the writer policy and
issue a new RC. The manifest-v3/vault-v2 architecture does not need to change because block size is
an artifact descriptor property.

## 10. Issue #79 actual-engine results and writer default

The final engine benchmark ran on Windows with .NET SDK 10.0.401 against the read-only sample above
(8 complete generations, 264 artifacts). Workload fingerprint:
`d86eb08685c249df2ff9e0d904aca3ea052117a44135fefe80e03861589c2b98` (SHA-256 over the ordered
manifest artifact SHA-256 values). It exercised the real pack writer, fanout-32 map, Zstd1/raw
fallback, derived SQLite index, Windows allocated-byte accounting, and artifact materializer. The
full matrix completed successfully in 20 minutes. Each materialized artifact was compared through
the descriptor's full SHA-256 check. Test output retained aggregate metrics only.

T365 uses the allocated pack plus allocated derived-index bytes after the first generation as the
baseline, followed by the mean of the seven measured generation transitions. Values use decimal GB.

| Block | Codec | Baseline allocated bytes | Mean incremental allocated bytes/capture | T365 GB | Latest materialization MiB/s | Historical 24 materialization MiB/s |
|---|---|---:|---:|---:|---:|---:|
| 4 KiB | none | 1,382,735,096 | 2,003,518 | 18.934 | 42.00 | 41.46 |
| 4 KiB | Zstd1/raw fallback | 880,269,310 | 937,510 | **9.093** | 37.86 | 37.48 |
| 8 KiB | none | 1,353,839,812 | 2,831,282 | 26.156 | 66.38 | 70.72 |
| 8 KiB | Zstd1/raw fallback | 835,528,249 | 1,240,555 | 11.703 | 67.89 | 69.00 |
| 16 KiB | none | 1,339,280,184 | 4,145,297 | 37.652 | 101.40 | 128.46 |
| 16 KiB | Zstd1/raw fallback | 803,877,829 | 1,754,203 | 16.171 | 100.36 | 113.93 |
| 32 KiB | none | 1,332,037,160 | 6,528,040 | 58.518 | 167.56 | 198.10 |
| 32 KiB | Zstd1/raw fallback | 793,038,387 | 2,803,496 | 25.352 | 148.42 | 162.30 |
| 64 KiB | none | 1,328,276,000 | 10,367,677 | 92.149 | 227.34 | 250.45 |
| 64 KiB | Zstd1/raw fallback | 778,418,625 | 4,421,843 | 39.514 | 186.43 | 206.25 |

The derived-index allocated bytes after eight generations were 32,485,376; 16,343,040;
8,286,208; 4,190,208; and 2,154,496 for Zstd1/raw-fallback at 4/8/16/32/64 KiB respectively.
The corresponding pack allocated bytes were 854,346,503; 827,869,091; 807,871,043; 808,472,651;
and 807,217,027. The lower block-size candidate retains more index metadata, while its lower
incremental data cost still produces the smallest T365.

**Decision: provisional first-RC writer default is 4096 bytes with Zstd level 1 and raw fallback.**
The actual engine confirms the 4 KiB candidate remains the T365 cost leader from the prototype
comparison. The actual value is 9.093 GB versus the prototype scenario's 8.904 GB; this small change
does not reverse the ranking or require a format/default review. Readers continue to accept all five
block sizes, and reused artifacts preserve their descriptor block size and root.

These are single-run warm-cache measurements, and the yearly figure remains a scenario derived from
seven observed transitions rather than a continuous hourly trace. The final RC must still replace
this projection with the real hourly acceptance trace described in section 9.
