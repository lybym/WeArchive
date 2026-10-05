# WeArchive official-RC real-environment acceptance harness (Issue #86)

This folder is the acceptance harness and runbook for Issue #86 —
`[P0][M1.5] validation: accept hourly v2 capture and offline recovery using the
official RC`. It is **validation infrastructure only**: it changes no product code,
no schema, no writer defaults and no CLI contract. The acceptance run itself is
executed on a real Windows/WeChat 4.x host by the run operator, never by CI.

The runbook fully operationalizes Issue #86. Where an issue phrase maps to a script
step, the mapping is stated explicitly so the posted evidence is auditable.

---

## 1. Prerequisites

- A supported Windows host with real, supported WeChat 4.x data (the issue's test
  environment requirement).
- **Run every script from a normal PowerShell console** on the acceptance host.
  Sandboxed/agent-execution environments may expose the invoking process only a
  restricted process view, in which the running WeChat client is invisible and
  capture correctly fails closed with `key_acquisition_failed`. The harness's own
  gates surface this immediately (init-root's source gate, the fixture scenario's
  client gate).
- PowerShell 7 (`pwsh`) on PATH.
- The SQLite command-line tools (`sqlite3.exe`) on PATH — only needed by the
  controlled fixture scenario (`fixture-scenario.ps1`).
- An **acceptance root**: a dedicated directory on a local drive, provided by the
  operator at run time. Never a repository path, never the production vault. All
  harness output — the redirected profile home, the RC binary, the evidence trace —
  lives under it.
- A **config file**: copy `config.example.json` outside the repository (the
  acceptance root is a good place), rename it to `config.json` and fill in the
  user-specific fields. The committed example contains only public release facts.
  A filled-in config is machine-private and must never be committed.

## 2. Isolation model (why the harness works without product changes)

The RC resolves its Raw Vault, canonical archive, key cache and scratch roots from
the per-user local application data folder. The harness redirects the RC process's
profile environment (`USERPROFILE` and the variables derived from it) into
`<acceptance-root>\home` when invoking the binary:

- The Windows known-folder API expands the per-user shell-folder registry values
  (`REG_EXPAND_SZ`, `%USERPROFILE%`-based) against the **process environment**, so
  `LocalApplicationData` — and with it every RC write path — resolves inside the
  acceptance home.
- The real user profile (`FOLDERID_Profile`) does not follow the override, so the
  profile-root candidate of WeChat source discovery still sees the real client data.
- When the WeChat data lives under the user's Documents folder instead, the source
  gate reports it unavailable and the operator creates one junction inside the
  acceptance home — `New-Item -ItemType Junction -Path <home>\Documents\xwechat_files
  -Target <real xwechat_files>` — which the redirected Documents candidate then
  resolves (init-root.ps1 prints exactly this remedy when needed).

This is not assumed: `init-root.ps1` **proves** the seam with the RC's own
`doctor --json` command (the `isolation gate`) before the run may start, and the
proof is persisted in `evidence/environment.json`. All evidence records carry the
resolved roots in privacy-safe form (relative to the acceptance root).

Privacy rules baked into the harness: source keys, fingerprints and absolute personal
paths are never recorded; paths are rendered relative to the acceptance root; the
repository never receives real data.

## 3. Exact RC binding (execution gate 1 — verify-rc.ps1)

The acceptance binds to the official RC produced by Issue #85. The very first
execution step is hash verification; nothing else may run until it passes.

```text
release tag:    v0.6.0-rc.1
commit:         84128794baa4d2c5f8d452731d4b8a03f8eece64
asset:          WeArchive-win-x64.zip
URL:            https://github.com/lybym/WeArchive/releases/download/v0.6.0-rc.1/WeArchive-win-x64.zip
SHA-256:        9BA9D50637B993555F6996C7A419422B7A213AD57E1A45CFA54402D3513CAFFB
self-reported:  {"version":"0.6.0-rc.1",...}   via `WeArchive.exe --version --json`
```

```powershell
./verify-rc.ps1 -Config <acceptance-root>\config.json   # add -Proxy http://127.0.0.1:7897 if needed
```

The script downloads the asset into the acceptance root, verifies SHA-256 against
`rc.asset_sha256` (hard gate; mismatch aborts), extracts it, and asserts
`--version --json` reports exactly `0.6.0-rc.1` — a dev build can never stand in for
the RC. Every later harness invocation re-resolves only this verified binary.

## 4. Environment recording (execution gate 2 — init-root.ps1)

```powershell
./init-root.ps1 -Config <acceptance-root>\config.json            # first run
./init-root.ps1 -Config <acceptance-root>\config.json -SeedVaultRoot <production vault root>
```

Creates the root layout (`home/`, `rc/`, `evidence/`, `logs/`), runs the isolation
gate, and records `evidence/environment.json` with the issue's required environment
fields: OS caption/version, filesystem and allocation unit where measurable (an
unavailable observation is recorded as null, never fabricated), drive capacity, RC
version/commit/asset hash, and the resolved vault/archive roots.

### v1 seed (compatibility scenarios)

`-SeedVaultRoot` copies (never moves) an existing vault — typically the production
Raw Vault's current v1 data — into the acceptance vault. The copy's generation
manifests are hashed into `evidence/v1-seed-hashes.json` for the immutability checks
(§9), and the seeded v1-only vault is authoritatively verified. The same account
lineage then produces, in order, the **v1-only read/rebuild** state, the **first
v1→v2 generation** (the first RC capture), **v2→v2 incremental** captures and the
**mixed v1/v2 lineage** — the issue's compatibility matrix on one account chain.

Record the acceptance account selector from the init output into `config.json`
(`capture.account_selector`); every capture selects the account explicitly.

## 5. The 7-day hourly procedure (hourly-capture.ps1)

Set `run.started_at_local` (ISO-8601 with offset) and `run.planned_hours: 168` in the
config, then either run the scheduled-task helper (§10) or invoke manually:

```powershell
./hourly-capture.ps1 -Config <acceptance-root>\config.json             # one scheduled cycle
./hourly-capture.ps1 -Config <acceptance-root>\config.json -SampleStats  # + read-only accounting sample
./hourly-capture.ps1 -Config <acceptance-root>\config.json -Summarize   # aggregate evidence summary
```

Each cycle, for the **current planned hourly point** (index =
`floor(now − started_at)`):

1. **Missed-point fill-in**: planned points that passed with no harness execution are
   recorded as `missed_no_execution` with an explanation — never silently skipped.
2. **Idempotency**: a point with an existing record is not re-executed; failed points
   can be re-attempted explicitly with `-Retry`.
3. **Capture**: `capture --account <selector> --json --no-input` against the isolated
   acceptance vault.
4. **Verify**: `vault verify --vault-root <acceptance vault> --json` — every
   successfully published generation must pass authoritative verification
   (acceptance criterion). `-SkipVerify` exists for operator emergencies only and is
   flagged in the record; the point then cannot count as a verified success.
5. **Evidence record**: exactly one JSON line appended to
   `evidence/hourly-trace.jsonl`.

### Per-capture evidence fields (Issue #86 storage-evidence mapping)

The trace record binds the exact RC (release tag, version, commit SHA, asset URL,
asset SHA-256) and carries:

| Issue field | Source |
|---|---|
| logical generation bytes | `capture.storage_counters.logical_bytes`; `vault stats` `logical_generation_bytes` when sampled |
| source-side changed-page bytes | `vault stats` `source_changed_page_bytes` — basis `unavailable` by design; never fabricated |
| storage changed-block bytes | `vault stats` `storage_changed_block_bytes` — basis `unavailable` by design |
| new authoritative payload bytes | `capture.storage_counters.new_data_bytes` / `new_data_blocks` |
| new map/metadata bytes | `capture.storage_counters.new_map_nodes`; `vault stats` `v2_map_metadata_stored_bytes` when sampled |
| allocated pack bytes | `capture.storage_counters.new_pack_bytes` / `new_packs`; `vault stats` `v2_pack_allocated_bytes` / `v2_pack_logical_bytes` when sampled |
| derived-index bytes | `vault stats` `derived_lookup_index_bytes` when sampled |
| reused payload bytes | `capture.coverage_summary.reused` (+ predecessor-generation reuse per coverage) |
| orphan/unreachable bytes | `vault stats` `v2_orphan_payload_uncompressed_bytes` / `v2_orphan_map_metadata_uncompressed_bytes` when sampled |
| capture duration | measured by the harness (`capture.duration_ms`) |
| materialization/rebuild timing | `offline-recovery.ps1` steps and scenario traces where sampled |
| no-change expectation | evaluated per point (below) |
| environment | `evidence/environment.json` + per-record `environment` block |
| start/end times | `planned_point.planned_at` / `planned_point.executed_at` |
| every scheduled capture result | `planned_point.outcome` + capture/verify sections |

`vault stats` sampling (`-SampleStats`) is optional per point because authoritative
verification already reads all packs; sample at least at the start, mid and end of
the run and after any unusual capture so the storage-evidence table can be filled.

### No-change hours

When every partition was reused, the record evaluates the issue's no-change
expectation (`no_change_expectation_met`): zero new data payload and zero new map
nodes — only bounded generation/provenance/checkpoint metadata. A violation is a
blocking anomaly listed by `-Summarize`.

### Evidence classification (every point is accounted for)

| Outcome | Meaning | Blocking? |
|---|---|---|
| `success` | capture exit 0, `complete`, authoritative verify passed | no |
| `success_partial` | generation published but `partial`; coverage gaps must be explained in `planned_point.explanation` | per gap |
| `failed_capture` | capture exited non-zero; error code + stderr recorded as explanation | yes if unexplained |
| `failed_verify` | capture published but verify failed | yes — stops the run until resolved |
| `missed_no_execution` | planned point passed with no harness execution | no (explained by definition) |

Unexplained failures block acceptance: the harness refuses to write a
`failed_capture`/`success_partial`/`missed_no_execution` record without an
explanation field, and `-Summarize` reports any point that is not accounted for.

## 6. Scenario matrix

### Hourly operation (inside the 7-day cadence)

- **No-change hours** — happen naturally; verified by `no_change_expectation_met`.
- **Normal chat/message append** — the live source's natural traffic.
- **Small in-place changes** — naturally observed; each `captured` partition in the
  coverage of the record attributes it.
- **Client restart** — restart WeChat at least once mid-run; the next capture points
  around the restart must classify normally.
- **WAL lifecycle/checkpoint behavior** — observed through partition fingerprint
  changes and re-captured partitions in the trace; explain any burst in the record.
- **Controlled truncate/rewrite fixture** — `fixture-scenario.ps1` (below).

### Compatibility (v1 seed + the hourly cadence)

- **v1-only read/rebuild** — init-root verifies the seeded v1-only vault
  authoritatively before any v2 capture exists.
- **First v1→v2 generation** — the first RC capture after seeding.
- **v2→v2 incremental** — every later hourly capture.
- **Mixed v1/v2 lineage** — the resulting account lineage; `vault stats` reports v1
  and v2 metrics separately when sampled.
- **Offline ingest/rebuild** — `offline-recovery.ps1` (§7).

### Controlled truncate/rewrite fixture (fixture-scenario.ps1)

```powershell
./fixture-scenario.ps1 -Config <acceptance-root>\config.json `
    -LiveAccountDirectory "<real xwechat_files\<wxid> directory>"
```

Builds an isolated sandbox under `<acceptance-root>\scenario-fixture` containing a
WeChat-4.x-shaped synthetic account discovered via the redirected Documents seam:
plaintext `SessionTable`/`contact` databases plus a plaintext synthetic message
shard, and one **read-only copy** of a real encrypted message shard as the key anchor
(transient key acquisition verifies against genuinely encrypted pages; the WeChat
client must be running and signed in — the script refuses otherwise and never
acquires keys itself). The fixture then captures baseline → append (rows added) →
**truncate/rewrite** (the shard file replaced in place), and records storage growth
attribution into `scenario-fixture/evidence/fixture-trace.jsonl`.

Verified harness properties of this scenario: the synthetic account is discovered
under the redirected Documents seam and resolves its profile id through
`all_users\login` (a marker directory the script creates); the adapter classifies
all fixture partitions as supported; and the read-only copy of the encrypted anchor
is never written. The capture → verify → attribution sequence itself runs only in
the operator's unrestricted console (see prerequisites).

Expected (Issue #86 storage evidence): the rewrite capture's physical growth is
attributable to the observed artifact rewrite, with **bounded** orphan/unreachable
bytes for the replaced blocks and no unexplained duplication; authoritative verify
passes; prior manifests stay byte-identical.

### Failure boundaries (failure-boundaries.ps1)

Every exercise is explicitly invoked, isolated from the main trace
(`scenario-boundaries/evidence/failure-boundaries-trace.jsonl`), and ends with an
authoritative verify of whatever it touched. Pause the schedule first
(`register-schedule.ps1 -Disable`).

| Issue boundary | Exercise |
|---|---|
| capture cancellation | `-IncludeKillCapture` kills a real capture mid-flight; lineage must be unchanged, verify green, leftover staging bounded (R1 fail-closed) |
| failure before manifest publication / published packs with manifest publication failure | the killed capture's staged-but-unpublished material: reported as orphan/staging bytes, never as evidence |
| ingest failure after successful capture | hourly retention semantics + `offline-recovery.ps1` (a successful generation survives without canonical ingest, and ingest/rebuild is later proven from authoritative evidence alone) |
| derived index deletion/corruption | on a copied vault: `missing_rebuildable` / `corrupt_rebuildable` via `vault stats`; authoritative verify must still pass |
| materialized temporary/cache deletion | scratch caches deleted; verify/stats unaffected |
| missing/corrupt authoritative object in a copied test vault | pack truncated/deleted in the copy; `vault verify` MUST fail closed |

```powershell
./failure-boundaries.ps1 -Config <acceptance-root>\config.json -IncludeKillCapture
```

### Regression coverage

- **#63** (historical Partial generations must not block the current successful
  sync): any `success_partial` point in the trace is followed by later normal
  captures on the same lineage — the cadence proves the non-blocking behavior; cite
  the point indexes in the evidence post.
- **#71** (rotated multi-shard message windows fully ingested): the offline
  rebuild's canonical counts (fresh archive, offline) must equal the pre-state
  baseline including rotated-shard conversations; `message list`/`export` on a
  rotated-shard conversation must show the full window.
- **#66** (canonical-only export remains offline): `offline-recovery.ps1
  -ExportConversation <id>` runs one canonical export while the source is offline.

## 7. Offline recovery acceptance (offline-recovery.ps1)

```powershell
./offline-recovery.ps1 -Config <acceptance-root>\config.json [-ExportConversation <id>]
```

Runs the issue's sequence and records each step: pre-state verify + canonical
baseline; source/key state (a failed capture with key acquisition unavailable is the
expected offline proof; a successful capture only counts when it reused all evidence
— zero new payload, no key acquisition attempted); derived v2 object index deletion;
disposable cache removal; **authoritative verify without derived state** (a valid
disposable cache must never compensate for missing authoritative CAS data); canonical
archive deletion and **fresh offline rebuild** with count comparison; generation-
manifest immutability before/after; v1 seed immutability when seeded; optional
offline export (#66).

## 8. Default block-size validation (4096 + Zstd level 1)

The first-RC writer default (Issue #79) is validated from the real trace, not
asserted:

1. Sample `vault stats` at the start, mid and end of the run (`-SampleStats`).
2. From the sampled metrics, derive per-period behavior: stored-vs-uncompressed
   payload ratio (`v2_unique_payload_stored_bytes` / `v2_unique_payload_uncompressed_bytes`),
   pack overhead (`v2_pack_logical_bytes` vs stored payload + map metadata),
   duplication (`duplicate_physical_record_bytes`), and per-capture growth
   (`new_data_bytes` for changed artifacts).
3. Compare small-file-dominated metadata periods against the #79 benchmark
   (`docs/RAW_VAULT_V2_BENCHMARK.md`) rather than absolute values: the decision
   reverses only if real hourly metadata/index/materialization behavior materially
   contradicts the benchmark basis.
4. If the trace materially reverses 4096 + Zstd1: do **not** accept Stable; open an
   issue-first product change, update docs/default policy with evidence, produce a
   new RC and rerun the affected gates. The manifest-v3/vault-v2 architecture does
   not require redesign for a writer-default change within the supported set.

## 9. Historical-evidence immutability

- `init-root.ps1` hashes the seeded v1 evidence (`evidence/v1-seed-hashes.json`).
- `offline-recovery.ps1` hashes every generation manifest before/after the offline
  phase and re-verifies the seed manifest — earlier published v1/v2 evidence must
  not be modified by later captures.
- The fixture and boundary traces additionally record the invariance of published
  manifests across later captures.
- "All retained successful-generation roots remain reconstructable" is covered by
  the per-point authoritative `vault verify` plus the offline fresh rebuild.

## 10. Hourly cadence scheduling (register-schedule.ps1)

```powershell
# Dry run (prints the exact task definition, registers nothing):
./register-schedule.ps1 -Config <acceptance-root>\config.json -WhatIf

# Register / pause / resume / remove:
./register-schedule.ps1 -Config <acceptance-root>\config.json
./register-schedule.ps1 -Disable
./register-schedule.ps1 -Enable
./register-schedule.ps1 -Remove
```

Hourly trigger (repeats for 8 days to cover the 7-day window plus margin),
`StartWhenAvailable` so a sleeping host catches up (the harness then classifies the
gap), no instance overlap, 2-hour execution limit. Registration happens only after
gates 1–2 passed. The task runs `hourly-capture.ps1` unprivileged; it never performs
a capture itself and never survives the acceptance window (remove it explicitly).

## 11. Finalization and evidence posting

```powershell
./hourly-capture.ps1 -Config <acceptance-root>\config.json -Summarize
```

`-Summarize` emits the aggregate: planned vs recorded points, classification
breakdown, unaccounted points (must be zero), no-change statistics and violations,
partial points and storage totals. Post the evidence on Issue #86 using the template
below, attaching the traces (`hourly-trace.jsonl`, scenario/boundary/recovery traces,
`environment.json`) — traces are privacy-safe by construction (paths relative to the
acceptance root, no keys, no fingerprints, no personal identifiers).

```markdown
## Issue #86 acceptance evidence

### Binding
- RC: v0.6.0-rc.1, commit 84128794baa4d2c5f8d452731d4b8a03f8eece64
- Asset: WeArchive-win-x64.zip, SHA-256 9BA9D50637B993555F6996C7A419422B7A213AD57E1A45CFA54402D3513CAFFB (verified by verify-rc.ps1)
- RC self-report: `--version --json` → {"version":"0.6.0-rc.1",...}

### Environment
- OS / version / filesystem / allocation unit / WeChat client version: <from environment.json>
- Acceptance roots: <privacy-safe form from environment.json>
- Run window: <started_at_local> … <end>, timezone <offset>

### Cadence
- Planned hourly points: 168 over >= 7 continuous days
- Recorded: <n> (success <n>, success_partial <n>, failed_capture <n>, failed_verify <n>, missed_no_execution <n>)
- Unaccounted points: 0
- Explanations for every partial/failed/missed point: <list>

### Storage evidence
- Per-period vault stats sampling: <reachable/orphan/duplicate/pack/index bytes>
- No-change hours: <n>, expectation violations: 0
- Physical growth attribution: <summary>

### Scenario results
- Compatibility: v1-only verify PASS; first v1→v2 <generation id>; v2→v2 incremental <n> points; mixed lineage verify PASS
- Fixture (truncate/rewrite): attributable growth, orphans bounded, verify PASS
- Failure boundaries: <per-exercise PASS lines from the boundary trace>
- Offline recovery: verify without derived index PASS; fresh offline rebuild counts match; disposable cache never required
- Regressions: #63 <evidence>, #71 <evidence>, #66 offline export PASS

### Default block-size validation
- 4096 + Zstd1: <validated / reversed with evidence>

### Immutability
- v1 seed unchanged; generation manifests unchanged across captures and offline phase

### Verdict
- <PASS / BLOCKED with reasons>
```

## 12. What this folder deliberately does not do

- No product code, schema, writer-default or CLI change (issue non-goal).
- No capture is started by the harness itself: every capture is an explicit script
  invocation or the registered hourly task.
- No scheduled task is registered by this repository's CI or by any script's import;
  `register-schedule.ps1` runs only when the operator invokes it.
- No real personal data, machine-private paths or filled-in configs are committed —
  `.gitignore` and the harness's privacy-safe rendering enforce this.
