# Capture fail-closed evidence: source-write races in the #86 official RC

This document is the quantitative write-race report required by Issue #96 (the
Roadmap's "Independent, bounded V2-only follow-up"). It measures the capture
failures recorded during the #86 official-RC real-environment acceptance run of
`v0.6.0-rc.1`, separates safety-correct fail-closed behavior from availability
impact, and states — without implementing — whether a bounded retry/backoff is
justified by the data.

## 1. Data sources and method

- **Accepted record.** [#86 acceptance evidence](https://github.com/lybym/WeArchive/issues/86#issuecomment-6159259365)
  (owner-accepted PASS for `v0.6.0-rc.1`, commit `84128794baa4d2c5f8d452731d4b8a03f8eece64`).
- **Trace.** The run's classified hourly trace (`evidence/hourly-trace.jsonl`,
  203 JSONL records, one per planned point). The trace is privacy-safe by
  construction: acceptance paths are stored as `<acceptance-root>` placeholders
  and no chat content, identifiers or keys are recorded. Only aggregate
  statistics derived from the trace are committed here; the trace itself and any
  private acceptance roots stay on the operator's machine and out of the
  repository (Issue #96 privacy red line).
- **Method.** Mechanical re-aggregation of the trace by exact classification of
  the recorded CLI error lines, cross-checked against the accepted record.
  Counts below are labeled by source. Nothing is extrapolated or fabricated;
  where the two sources differ, both are shown (section 2).

## 2. Denominator and failure classification

Per-point ledger of the run (both sources agree):

| Quantity | Value |
| --- | --- |
| Planned hourly points | 204 (extended from 168 under the operator-approved gap-and-extend strategy) |
| Recorded | 203 (unaccounted: 0) |
| `missed_no_execution` (machine off; no capture attempt recorded) | 33 |
| Executed points (denominator) | **170** |
| `success` | 81 — every one passed authoritative `vault verify`, all `complete` |
| `failed_capture` | 89 |
| `success_partial` | **0** (the #63 contingent scenario never occurred naturally) |
| Zero-new-data hours (`new_data_bytes = 0` and `new_map_nodes = 0`) | 18 (points 52–54, 75–78, 99–101, 123, 125, 146–149, 172–173) |

Failure classification of the 89 `failed_capture` points:

| Class | Accepted record (#86 comment) | Mechanical text classification of the same trace |
| --- | --- | --- |
| Source-write race ("Source partition '…' changed during capture") | **69** | **60** |
| Client state ("WeChat is not running" / "No database key could be recovered") | **20** | 22 + 4 = **26** |
| Harness-side assembly-load failure (RC process failed before capture logic; run-local environment, not source or product logic) | not listed separately | **3** (points 66–68) |

Method note on the 69/20 versus 60/26/3 difference: the accepted classification
was produced by the operator with run-journal context (boot/shutdown windows,
the mid-run acceptance-root migration) that a text matcher cannot see; the
mechanical split uses only the recorded error text. This report keeps the
accepted record as the denominator of reference and uses the mechanical split
only where it adds structure (streaks, durations). Every safety-relevant
property is identical under both classifications: no failed point published
anything, and no partial generation was ever released (section 4).

## 3. Observed source and client state

- **Source (write-race class).** The 60 mechanically-identified write-race
  failures name 10 distinct source partitions:
  `message/biz_message_0.db` 19, `contact/contact.db` 14,
  `message/message_0.db` 13, `general/general.db` 3, `hardlink/hardlink.db` 3,
  `session/session.db` 3, `head_image/head_image.db` 2, `message/message_1.db` 1,
  `message/message_resource.db` 1, `sns/sns.db` 1. Message shards and the
  contact database — the partitions WeChat writes while the client is actively
  used — dominate.
- **Client state.** By definition, every write-race failure occurred while the
  client was running and actively writing. The client-state class splits into
  "not running" (22) and "key not recoverable" (4); per the accepted record, the
  19 pre-migration "not running" points were caused by the §D1 environment
  defect (a low-integrity-label acceptance binary could not see the running
  client), while the late-run points (e.g. 165: key unavailable right after a
  boot; 189: "not running" immediately post-boot) were accurate fail-closed
  reports of true states.

## 4. Safety: fail-closed behavior was correct at every failure

- All 89 failed points recorded `generation_id = null`, no storage counters and
  no verify — i.e. **nothing was published**; no partial generation ever entered
  the vault (docs/PRD.md FR-20, capture R1).
- All 81 successful points published exactly one `complete` generation, each of
  which passed authoritative `vault verify` (81/81). The WAL/SQLCipher
  consistency and snapshot-integrity proof of #86 is untouched by this report.
- No `success_partial` point occurred in 170 executed points: the historical
  #63 contingent scenario (an older partial generation blocking a live sync)
  was never naturally triggered. That is precisely why the synthetic V2 fixture
  regression added by Issue #96 (conversation- and Collection-scoped
  Partial→Complete tests, with direct historical ingest still failing closed)
  is the durable guard for this scenario.
- Availability impact of a failed point is bounded and recovered: the unchanged
  partitions are fingerprint-reused by the next capture, so a lost hourly slot
  loses cadence granularity, not data. The 18 zero-new-data hours additionally
  demonstrate the no-change growth guarantee in the real environment.

## 5. Streaks and recovery latency (mechanical, trace-derived)

- The 89 failed points form 29 consecutive failed streaks; the long ones
  (lengths 14, 7 and 5) are client-state/environment periods, not write races.
- 26 streaks involve at least one write-race failure; most are length 1–4.
- Recovery gap from the end of a write-race-involving failed streak to the next
  executed capture: **1 planned hour in 22 of 26 streaks**, 2 hours in 1,
  3 hours in 2 and 5 hours in 1 (the longer gaps include machine-off
  `missed_no_execution` hours). After every failed streak a successful capture
  eventually followed on the same lineage; the accepted record summarizes this
  as "the next hourly point succeeded in every streak".
- Intra-point retries were rare and never recovered a write race: only 3 of 203
  points carry recorded retry attempts, and the single recorded intra-point
  write-race retry (point 35, retry spacing ≈ 548 s ≈ 9.1 min) failed
  identically. No point shows a write-race attempt followed by an intra-point
  success.

## 6. Capture duration and cost of a failed attempt

Wall-clock duration of the capture process per recorded final outcome
(`duration_ms`, trace-derived):

| Outcome | n | min | p50 | p90 | max | mean |
| --- | --- | --- | --- | --- | --- | --- |
| `success` | 81 | 67.0 s | 109.8 s | 181.6 s | 331.9 s | 123.7 s |
| `failed_capture` — write race | 60 | 67.6 s | 113.8 s | 244.6 s | 2434.3 s | 189.0 s |
| `failed_capture` — client state | 26 | 1.2 s | 1.9 s | 62.4 s | 88.6 s | 19.0 s |

A write-race failure therefore costs roughly a full capture attempt of work
(p50 ≈ 114 s) before failing closed, with one outlier at ≈ 40.6 min. Client-state
failures exit fast (pre-flight checks), and the assembly-load failures are
harness-side.

## 7. Availability impact

- Write-race failures consumed 60–69 of 170 executed hourly slots
  (≈ 35.3–40.6% depending on classification), i.e. ≈ 29.4–33.8% of the 204
  planned points never produced a generation in their hour.
- No data loss is attributable to these failures: every failed slot published
  nothing, and the next successful capture republished a complete, verified
  snapshot (section 4). The impact is cadence availability only — one lost
  hourly increment per failed slot, plus the wasted capture work of section 6.

## 8. Bounded retry/backoff assessment — proposal only, no product change

The measured data does **not** justify adding retry/backoff to the product
capture path now:

1. The one observed intra-point retry (≈ 9.1 minutes after the failure) failed
   identically; observed recovery latency is dominated by the hourly cadence
   (median 1 hour), so a short in-capture backoff would likely re-pay a full
   capture cost (p50 ≈ 114 s) for no measured benefit (0 observed intra-hour
   recoveries).
2. The decisive quantity — the actual duration of a WeChat write burst on a
   given partition — was never measured; an hourly cadence cannot see sub-hour
   structure. Designing a delay without that measurement would be guesswork.
3. Fail-closed is the safety-correct behavior (FR-20): the error the capture
   already reports is literally "retry for a stable snapshot", and the next
   hourly point did recover every streak. The status quo already bounds the
   availability cost to one slot per occurrence.

**Proposed (not implemented) design, for a future Issue only:** a bounded
retry of at most 1–2 extra capture attempts per occurrence, with a minutes-scale
delay calibrated by a dedicated short-interval probe that first measures real
write-burst durations; the retry must re-enter the existing `CaptureService`
path, must never publish a partial generation, must add no journal, commit
marker, recovery state or R3+ machinery (docs/DEVELOPMENT.md section 10,
AGENTS.md hard-stop rule), and must preserve the no-change growth guarantees and
the SQLCipher/WAL consistency proof. Until such a probe justifies it, the
fail-closed behavior stands unchanged.

## 9. Limitations

- Single machine, single client version (WeChat for Windows 4.1.15.13), hourly
  cadence, one run; no sub-hour visibility and no cross-client variance.
- The 20-vs-69 published classification was made with operator run-journal
  context that the mechanical re-aggregation cannot reproduce; both are reported
  and neither is silently corrected.
- One harness-side assembly-load failure class (3 points) has no accepted-record
  counterpart; it is environment-local and excluded from source/client
  conclusions.
- The historical Partial scenario was not observed naturally during the run
  (`success_partial` = 0), so the fixture regression — not field data — is the
  evidence that live sync follows current complete evidence (Issue #96 scope 1).
