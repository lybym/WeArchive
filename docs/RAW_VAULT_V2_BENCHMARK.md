# Raw Vault v2 storage benchmark evidence

Issue: #77

Date: 2026-10-03

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
index. The authoritative persisted-format overhead is now frozen in
[RAW_VAULT_V2_FORMAT.md](RAW_VAULT_V2_FORMAT.md); Issue #79 must reproduce the decision using the
actual storage engine and separately report the derived-index allocated bytes.

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

## 6. Decision

The Issue #77 provisional writer default is:

    block_size = 4096
    codec preference = zstd level 1, raw fallback

Rationale:

- 4 KiB has the lowest measured post-baseline incremental retained bytes;
- it has the lowest T365 sensitivity result for the accepted hourly workload;
- its prototype reconstruction throughput remains adequate for an archival path;
- the descriptor still records block size, so the architecture is not permanently coupled to
  SQLite's current 4096-byte source page size.

This default is **provisional for the first RC**, not a format-global constant.

The v2 reader accepts 4096/8192/16384/32768/65536-byte block sizes.

## 7. Reproduction gate in Issue #79

Issue #77 is docs/format-first and therefore does not implement the production storage engine.

Issue #79 MUST reproduce the decision after the actual:

- pack framing from [RAW_VAULT_V2_FORMAT.md](RAW_VAULT_V2_FORMAT.md);
- persistent map;
- Zstd/raw selection;
- derived SQLite lookup index;
- filesystem allocated-byte accounting

exist in product code.

A material divergence is a hard stop. Do not silently change the writer default inside #79. Instead:

1. publish the new measurements;
2. update Issue #77-derived docs/decision with rationale;
3. obtain review of the changed default;
4. then continue implementation.

## 8. Stable gate

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

If the real hourly trace materially reverses the provisional default, adjust the writer policy and
issue a new RC. The manifest-v3/vault-v2 architecture does not need to change because block size is
an artifact descriptor property.
