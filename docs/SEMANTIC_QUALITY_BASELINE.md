# M2a semantic quality baseline: parser classification and unknown-type evidence

This document is the baseline report required by
[Issue #97](https://github.com/lybym/WeArchive/issues/97) (Roadmap M2a, PRD G3/G5,
FR-07/19/24). It establishes a reproducible, privacy-safe measurement of what the
shipped WeChat parser/normalizer actually classifies today, pins that measurement in
CI-rerunnable tests, and derives the prioritized M2b/M2c/M2d defect matrix that the
next milestone slices must work against. It deliberately measures before changing any
special-message logic (Roadmap M2 sequence, item 1).

Measured at: `main` commit `4c933a4d6512f8e2eec9ea52fd5f3ee29dc47bbb`
(v0.6.0 Stable + #96/#108), parser version `WeArchive.Infrastructure` as of that commit.

## 1. Method, data and privacy

- **Corpus.** `tests/WeArchive.Tests/Support/SemanticBaselineCorpus.cs` is a deterministic,
  synthetic WeChat 4.x **wire-level** corpus: 33 records written as numeric `local_type`
  codes plus message XML exactly as the message shards store them (extended additively to
  37 records by the M2b slice, Issue #98 — see section 10 — and to 44 records by the M2c
  slice, Issue #99 — see section 11). Because the corpus
  lives at the wire level, the measurement exercises the shipped parser
  (`WeChatContentParser`) and normalizer (`MessageNormalizer`) rather than mirroring them.
- **Privacy.** Every id, name, text, file name and URL is a synthetic placeholder
  (`example.com`, 张三/李四-style role names already used by the repository docs). No real
  conversation content, archive, export, key or machine-private path is committed
  (docs/DEVELOPMENT.md section 8, Issue #97 governance red line).
- **Measurement harness.** `tests/WeArchive.Tests/SemanticBaselineTests.cs` runs every
  corpus record through the shipped parser → normalizer boundary and additionally through
  the real `ImportService` → `SqliteArchiveStore` → `JsonlDatasetExporter` pipeline in
  temporary directories. No live WeChat client, no database key, no network. CI runs it
  as part of the standard test suite.
- **Real-data profiling rule.** Any future measurement over real archives must emit
  **aggregate metrics only** — never messages, ids, names or payloads — and must group by
  the adapter-reported upstream client version (`SourceDescriptor.SourceVersion`). This PR
  commits no real-data profiling.
- **Source-version sampling.** The corpus carries one synthetic sample version,
  `synthetic-wechat4x-m2a-1`, which flows into canonical provenance
  (`SourceProvenance.SourceVersion`) and into the export manifest (`source_version`).
  Multi-version sampling requires real captures and is out of scope here; the metric
  definitions below already carry the version dimension so real profiling can reuse them.

## 2. Metric definitions

All metrics are computed over the same closed set of records; nothing is excluded from
the denominator.

| Metric | Definition |
| --- | --- |
| Denominator | Total corpus records measured through the shipped parser → normalizer boundary (33 at M2a; 37 after the additive M2b extension, section 10; 44 after the additive M2c extension, section 11). |
| Classification numerator | Number of records whose normalized canonical type equals type T. |
| Unknown numerator | Records normalized to `unknown` (all of them keep verbatim upstream type codes in `payload.source_type`/`source_subtype`). |
| Partial numerator | Records whose canonical envelope carries `IsPartial = true` (only part of the upstream record could be interpreted). |
| Diagnostic numerator | Records that receive an ingest-level partial diagnostic per the documented `ImportService` rule: unknown → `unknown_message_type`; other partial → `partial_app_message`; reply without a locally available quote snapshot → `unresolved_reply_target`. |
| Coverage exclusions | Scenario families the corpus intentionally does not sample (section 5); they affect interpretation, never the denominator. |
| Source version | The adapter-reported upstream client version; `synthetic-wechat4x-m2a-1` for this corpus. |

Preservation invariants asserted alongside every measurement (they are not metrics but
gate the baseline): no record is dropped (emitted count == corpus count); unknown stays
explicit with preserved codes; unavailable fields stay null (no fabricated values,
MESSAGE_SCHEMA section 20); the offline import → archive → export run is
byte-deterministic across two independent fresh archives.

## 3. Supported-type catalog against MESSAGE_SCHEMA

Canonical list is normative in docs/MESSAGE_SCHEMA.md section 4 (17 types; `quote` is a
relationship, not a content type). The table maps each canonical type to the wire
producers the shipped parser recognizes, with the corpus as evidence.

| Canonical type | Wire producers recognized today | Corpus evidence | Notes |
| --- | --- | --- | --- |
| `text` | type 1; type 49 app subtype 57/1 (quote envelope keeps its own text) | `text_direct`, `text_group_known_sender`, `text_group_unmatched_prefix`, `quote_direct`, `text_official_push`, `text_system_helper` | group prefix stripped only for known participants |
| `image` | type 3; app subtype 2 | `image_direct` | payload null; no binary in Phase 1 |
| `voice` | type 34 | `voice_direct_timed`, `voice_direct_untimed`, `voice_group_untimed` | duration only when the client recorded one |
| `video` | type 43 | `video_direct` | payload null |
| `file` | app subtype 6 | `file_direct_named`, `file_direct_unnamed` | name/extension/size only when locally present |
| `link` | app subtypes 3/4/5/24 | `link_direct_original`, `link_direct_wrapper_only`, `link_official_article` | wrapper URLs land in `fallback_url` (MESSAGE_SCHEMA section 9 rule 2) |
| `app_share` | type 49 with recognized-but-unmodeled app payloads, including every `wcpayinfo` shape outside D1's bounded transfer/red-packet rules | `appshare_unmodeled_subtype` (subtype 87), `groupcollect_unsampled_shape` (subtype 2003 — pinned by review round 1 P1-1) | explicitly partial; every obtainable field kept |
| `mini_program` | app subtypes 33/36 or weappinfo present | `miniprogram_direct` | appid/pagepath without URL guessing |
| `forward_bundle` | app subtype 19, or `recorditem` present | `bundle_group_structured`, `bundle_group_description_fallback` | nested items preserved incl. unknown item types |
| `location` | type 48; app subtype 17 | `location_direct` | poinname/label/address/lat-long only when present |
| `contact_card` | type 42 | `contactcard_direct` | display name + source user id only |
| `system` | types 10000/10002 (non-revoke), type 50 VoIP | `system_group_template`, `system_plain_event`, `voip_direct` | template links resolved; unresolved templates stay partial |
| `revoke` | type 10000 with `revokemsg` | `revoke_group` | operator/newmsgid only when locally present |
| `red_packet` | type 49 + `wcpayinfo` + title containing 红包 | `redpacket_direct`, `redpacket_without_amount` | amount recorded only when the local fee description is a plain decimal (D2); otherwise the shipped null payload |
| `transfer` | type 49 app subtype 2001 + `wcpayinfo` without a red-packet title (D1, adopted in #98; gated to the sampled subtype by review round 1 P1-1) | `transfer_unclassified`, `transfer_without_amount` | amount/currency/status only when reliably readable; unreadable amount stays partial |
| `emoji` | type 47; app subtype 8 | `emoji_direct` | payload null |
| `unknown` | every other classic type; type 49 without a usable app payload; `appmsg` with no title/description/url (incl. title-less pay cards); unparseable XML | `unknown_classic_type`, `app_without_appmsg`, `app_malformed_xml`, `unknown_classic_system`, `paycard_titleless_unknown` | never dropped; codes preserved |

## 4. Measured baseline (pinned by `SemanticBaselineTests`)

### 4.1 Classification by canonical type

Updated by the M2b slice (Issue #98) and the M2c slice (Issue #99); the deltas are recorded in
sections 10 and 11.

| Canonical type | Count |
| --- | --- |
| text | 12 |
| image | 1 |
| voice | 3 |
| video | 1 |
| emoji | 1 |
| file | 2 |
| link | 3 |
| app_share | 2 |
| mini_program | 1 |
| forward_bundle | 2 |
| location | 1 |
| contact_card | 1 |
| system | 3 |
| revoke | 2 |
| red_packet | 2 |
| transfer | 2 |
| unknown | 5 |
| **Total (denominator)** | **44** |

### 4.2 By conversation category and source version

| Conversation category (WeChat4Schema.ClassifyConversation) | Records |
| --- | --- |
| Direct (`wxid_bob`) | 31 |
| Group (`100200300@chatroom`) | 8 |
| Official (`gh_example_news`) | 2 |
| System (`filehelper`) | 3 |

All 44 records carry `source_version = synthetic-wechat4x-m2a-1` in provenance; the
sample is single-version by design (section 1).

### 4.3 By upstream source type (type/subtype)

23 distinct upstream `(type, subtype)` pairs in the corpus; the per-record verbatim codes
are pinned by `EveryCorpusRecordReachesTheCanonicalBoundaryWithItsCodesPreserved`.

| Upstream (type, subtype) | Records | Canonical outcome(s) |
| --- | --- | --- |
| (1, 0) text | 7 | text ×7 |
| (34, 0) voice | 3 | voice ×3 (2 partial: duration unavailable) |
| (3, 0) image | 1 | image |
| (43, 0) video | 1 | video |
| (47, 0) emoji | 1 | emoji |
| (49, 5) app/url | 4 | link ×3, unknown ×1 (malformed XML, U3) |
| (49, 6) app/file | 2 | file ×2 (1 partial: name unavailable) |
| (49, 19) app/forward bundle | 2 | forward_bundle ×2 (1 partial: description fallback) |
| (49, 33) app/mini program | 1 | mini_program |
| (49, 57) app/quote | 5 | text + reply snapshot ×5 (1 resolved by the archive after the M2c slice, section 11) |
| (49, 87) app/unmodeled | 1 | app_share partial (D3) |
| (49, 101) app envelope w/o appmsg | 1 | unknown partial (U2) |
| (49, 2000) app/red packet | 2 | red_packet ×2 (amount only when readable, D2) |
| (49, 2001) app/transfer | 2 | transfer ×2 (1 partial: amount unavailable, D1) |
| (49, 2003) app/group collection (unsampled pay shape) | 1 | app_share partial (D1 boundary, pinned by review round 1 P1-1) |
| (49, 2005) title-less pay card | 1 | unknown partial (U4) |
| (42, 0) contact card | 1 | contact_card |
| (48, 0) location | 1 | location |
| (50, 0) VoIP | 1 | system (event voip) |
| (10000, 0) system | 3 | system (template), revoke ×2 (1 without a locally recorded newmsgid) |
| (10002, 0) system extended | 1 | system |
| (1000007, 0) unmodeled classic | 1 | unknown (U1) |
| (1050001, 0) unmodeled classic | 1 | unknown (U1) |

Five wire outcomes land in `unknown` (U1 ×2, U2, U3, U4); none is dropped.

### 4.4 Partial and diagnostics

| Metric | Count |
| --- | --- |
| Partial records | 12 (`voice_direct_untimed`, `file_direct_unnamed`, `appshare_unmodeled_subtype`, `transfer_without_amount`, `groupcollect_unsampled_shape`, `unknown_classic_type`, `app_without_appmsg`, `app_malformed_xml`, `bundle_group_description_fallback`, `voice_group_untimed`, `unknown_classic_system`, `paycard_titleless_unknown`) |
| Complete records | 32 |
| `unknown_message_type` diagnostics | 5 (upstream codes preserved per record: 1000007/0, 49/101, 49/5, 1050001/0, 49/2005) |
| `partial_app_message` diagnostics | 7 (34/0 ×2, 49/6, 49/87, 49/2001, 49/19, 49/2003) |
| `unresolved_reply_target` diagnostics | 0 (every corpus quote carries a locally available snapshot) |
| `reply_snapshot_only` diagnostics (Info) | 1 (the M2c corpus quote whose target is archived under a server id resolves; section 11) |

After M2b the partial set differs from the M2a baseline only by additive records and the
D1 reclassification: `transfer_unclassified` left the partial set (now a complete
`transfer`, D1); the additive `transfer_without_amount`, `groupcollect_unsampled_shape`
(D1 boundary: non-2001 pay card) and `paycard_titleless_unknown` (U4) entered it. The
partial 49/2001 record is `transfer_without_amount`. The M2c additive records introduce no
new partial or unknown outcomes.

### 4.5 Export-manifest counters (real pipeline run)

The corpus is imported through `ImportService` and exported through
`JsonlDatasetExporter` into a fresh SQLite archive; the manifests aggregate to:
`record_count` 44, `unknown_count` 5, `partial_count` 12, `unsupported_count` 5
(`unsupported_count` mirrors `unknown_count` by exporter contract). Two independent
archive-and-export runs produce byte-identical JSONL datasets and identical counters.

### 4.6 Nested text preservation (baseline behaviors that must not regress)

- Quote/reply: `reply_to.sender_name` (张三), `reply_to.text` (下午三点开会。), resolved
  canonical `sender_id` and the quoted record's `createtime` are preserved;
  `reply_to.message_id` stays null at normalization and is filled only by the archive
  backfill pass when the target is present (docs/MESSAGE_SCHEMA.md section 6). Since the
  M2c slice (Issue #99) the adapter composes the upstream `svrid` into the documented
  `s:<server_id>` identity space so the backfill pass can actually match an archived target.
- Forward bundles: structured `recorditem` items are preserved with sender display names;
  a nested item of an unsupported datatype stays `unknown` inside the preserved bundle;
  the description-only fallback is explicitly partial; per-item `time` stays null because
  WeChat 4.x does not persist it (MESSAGE_SCHEMA section 12).
- System templates: `content_template` link lists resolve to the human-readable sentence
  without inventing identities; unresolved templates stay partial with the template text.
- Group prefix: stripped only when it names a known participant; otherwise the text is
  preserved verbatim while the sender still resolves from the shard's real sender id.

## 5. Coverage exclusions

The corpus intentionally does not sample the following; conclusions from section 4 do not
extend to them.

1. **Binary media payloads** — Phase 1 exports no binaries (MESSAGE_SCHEMA section 7);
   the corpus samples the event semantics only.
2. **Multi-version sampling** — one synthetic version tag; real client-version deltas
   require real captures (aggregate-only rule, section 1).
3. **Vault formats** — the measurement is parser/normalizer-level; Raw Vault v1/v2
   storage is out of scope (V2-only policy, ADR 0012).
4. **Financial semantics beyond presence** — red-packet/transfer amounts, currencies and
   statuses are not extracted yet (D2/D1); nothing is measured about their correctness.
5. **Long-tail wire variants** — one representative sample per scenario (VoIP, location,
   contact card, official push, revoke, system template); real data carries more
   variants that M2b must sample separately before changing them.
6. **Rich official-account article bodies** — sampled as link metadata only.
7. **Raw-data profiling** — none in this PR; future real-data runs must follow
   section 1 and never commit messages.

## 6. Unknown-type evidence matrix

Every path that produces `unknown` today, with its evidence:

| # | Unknown path | Upstream shape | Explicit preservation | Evidence |
| --- | --- | --- | --- | --- |
| U1 | Unmodeled classic type | `local_type` outside the recognized set (e.g. 1000007, 1050001) | canonical `unknown`, payload keeps `source_type`/`source_subtype`, ingest diagnostic `unknown_message_type` | parser `WeChatContentParser.Parse` default branch; tests `EveryCorpusRecordReachesTheCanonicalBoundaryWithItsCodesPreserved`, `OfflineImportArchiveExportIsByteDeterministicAndCountsUnknowns` |
| U2 | Type 49 without `<appmsg>` | app envelope missing/foreign (e.g. subtype 101) | canonical `unknown`, parser diagnostic `partial_app_message`, ingest diagnostic `unknown_message_type` | parser `ParseAppMessage` no-appmsg branch; corpus `app_without_appmsg` |
| U3 | Unparseable message XML | truncated/malformed payload | canonical `unknown` via `SourceMessageContent.Unparsed`, never an exception | parser `ParseAppMessage` TryParse failure; corpus `app_malformed_xml`; existing test `MalformedXmlYieldsAPartialRecordNotAnException` |
| U4 | Unmodeled app payload without fields | `appmsg` with no title/description/url (incl. pay cards whose `wcpayinfo` shape is outside D1's bounded rules) | canonical `unknown` (nothing to keep in app_share) | parser `ParseAppMessage` default branch; corpus `paycard_titleless_unknown`; MESSAGE_SCHEMA section 18 |
| U5 | Nested forward item of unknown datatype | `dataitem datatype ∉ {1,2}` without a placeholder text | item type `unknown` inside the preserved bundle; bundle itself not dropped | parser `MapForwardItemType`; test `ForwardBundlesPreserveNestedTextIncludingUnknownItems` |

No hidden drops exist on the measured surface: the no-drop invariant (emitted == corpus)
is asserted at the canonical boundary and at the exported dataset boundary.

## 7. Prioritized defect matrix for M2b/M2c/M2d

Priorities: **P1** = directly misrepresents available local semantics; **P2** = loses
obtainable field value while staying explicit; **P3** = documentation/diagnostic
polish. "Bounded requirement" states what the slice may change; anything beyond needs a
new approved Issue. Unless stated otherwise: no canonical schema change, no fabricated
values, unknown stays explicit (Issue #97 governance; AGENTS.md mandatory rules).

Status after M2b (Issue #98, review round 1 applied): **D1, D2 and D4 are adopted**
(section 10 records the before/after evidence; D1's classification is gated to the sampled
49/2001 subtype so that every other wcpayinfo shape keeps its documented fallback, exactly
as this matrix requires); **D3 stays the documented `app_share` fallback** — no concrete
subtype was modeled with MESSAGE_SCHEMA backing; **D5 stays unadopted** — no real
VoIP/system variants were sampled, so no code change is authorized.

Status after M2c (Issue #99): **E1 is adopted** and **E2/E3 are documented and pinned**
without behavior change — section 11 records the before/after evidence.

### M2b — special/unknown classification

| ID | P | Finding | Evidence | Bounded requirement |
| --- | --- | --- | --- | --- |
| D1 | P1 | **Transfers are not classified.** A type 49/2001 `wcpayinfo` record lands in `app_share` (partial) instead of `transfer`, so the canonical type is wrong even though money semantics are locally present. | corpus `transfer_unclassified`; parser `ParseAppMessage` default branch; test `UnavailableFieldsStayNullAndAreFlaggedPartial`; baseline `transfer` count 0 | Classify as `transfer` only when the local record carries transfer semantics (wcpayinfo without red-packet title), preserving obtainable amount/currency/status fields; every other wcpayinfo shape stays `app_share` partial. Reuses the existing `transfer` canonical type and payload shape (MESSAGE_SCHEMA section 15) — no schema change. |
| D2 | P2 | **Red-packet financial fields are not extracted** (`feedesc` etc. stay unread; payload null). | corpus `redpacket_direct`; parser wcpayinfo branch; MESSAGE_SCHEMA section 15 | Optional: extract only reliably-understood fields into the existing red_packet/transfer payload shapes; never estimate; if pursued, extend MESSAGE_SCHEMA section 15 notes in the same PR. |
| D3 | P2 | **Channels/Live and other unmodeled app subtypes land in `app_share` partial.** Semantics stay available but the type is generic. | corpus `appshare_unmodeled_subtype` (subtype 87); parser default branch; test `EveryCorpusRecordReachesTheCanonicalBoundaryWithItsCodesPreserved` | Keep `app_share` partial as the documented fallback unless a slice models a concrete subtype with MESSAGE_SCHEMA backing; do not guess types from titles. |
| D4 | P3 | **Description-fallback forward bundles carry no parser-level diagnostic code** (partial flag only; ingest still emits `partial_app_message`). | corpus `bundle_group_description_fallback`; parser `ParseForwardBundle` fallback return | Optional dedicated diagnostic code; requires docs/MESSAGE_SCHEMA diagnostic note in the same PR. Behavior must not change otherwise. |
| D5 | P3 | **VoIP/system event variants beyond the sampled shapes are unmeasured.** | section 5 exclusions | M2b must sample real variants (aggregate-only) before modeling; no code change authorized by this report. |

Working-as-documented unavailability outcomes (not defects; listed so they are not
"fixed" into guesses): voice without duration (`voice_duration_unavailable`), file
without name, wrapper-only URLs, nested forward items without timestamps, revoke without
revoked text, transfer without a plain-decimal fee description (explicitly partial
transfer, #98 — decorated forms stay unparsed rather than guessed).

### M2c — reply/quote/forward relations

| ID | P | Finding | Evidence | Bounded requirement |
| --- | --- | --- | --- | --- |
| E1 | P1 | **`reply_to.message_id` is always null at normalization** and depends on the archive backfill pass to resolve; the corpus quote's upstream `svrid` does not match any archived composite source id, so the snapshot-only path is the measured baseline. | normalizer `BuildReply`; archive backfill (`SqliteArchiveStore` reply-resolution pass); tests `QuoteSnapshotsArePreservedWithoutFabricatingATargetId`, `SqliteArchiveStoreTests.ReplyTargetIsResolvedAgainstTheArchiveOnly` | M2c must prove resolution against real evidence; never fabricate a target id; snapshot retention unchanged. |
| E2 | P2 | **Revoke target linkage is unverified in real data.** `revoked_message_id` is derived as `StableIds.Message(conversation, upstream newmsgid)`, which only matches the archive when the shard reader uses the upstream newmsgid as its source message identity. | corpus `revoke_group`; normalizer `BuildPayload` (Revoke) | M2c verifies the newmsgid ↔ archived-identity linkage on real evidence and either aligns it or documents the null-fallback; no guessing. |
| E3 | P3 | **Forward-bundle nested item timestamps are always null** (WeChat 4.x does not persist them). | corpus bundles; MESSAGE_SCHEMA section 12; parser `ParseForwardBundle` (`OccurredAt = null` comment) | Keep null; treat any future source that persists item times as a new corpus scenario, not a data change. |

### M2d — metadata refresh/merge

| ID | P | Finding | Evidence | Bounded requirement |
| --- | --- | --- | --- | --- |
| F1 | P2 | **Group sender identity degrades gracefully but unevenly**: known-participant prefixes resolve through the prefix hint; unmatched prefixes still resolve through the shard's real sender id; participants without contact metadata export as stable ids with empty display data. | corpus `text_group_known_sender` vs `text_group_unmatched_prefix`; test `GroupPrefixAndSystemTemplatesPreserveTheHumanReadableText`; exporter identities ledger | M2d refresh/merge must keep stable ids stable across remark/nickname changes (Roadmap M2d); baseline pins the id-only resolution. |
| F2 | P3 | **Official-account pushes have no resolvable canonical sender** (`sender_id` null) — explicit, not fabricated. | corpus `text_official_push`, `link_official_article` | Optional M2d participant modeling for official accounts; must not invent identities. |

## 8. Reproducibility and update contract

- Re-run the measurement: `dotnet test WeArchive.sln --filter SemanticBaselineTests`
  (CI runs the full suite on every PR).
- The pinned values live in `SemanticBaselineTests.CorpusClassificationMatchesThePinnedBaselineReport`
  and `OfflineImportArchiveExportIsByteDeterministicAndCountsUnknowns`; they must match
  section 4 of this document at all times.
- M2b/M2c/M2d changes that intentionally alter classification must, in the same PR:
  (1) extend the corpus additively (append records; never mutate or repurpose an existing
  record id), (2) update the pinned values and this document's tables, (3) record the
  before/after numbers with the defect-ID above, and (4) keep every preservation
  invariant green. A measurement that weakens an invariant fails the build by design.
- Failure semantics of the harness itself: pure in-process measurement (no persistence);
  pipeline runs use temporary directories discarded per test — no reliability-level
  impact (tests only).

## 9. Schema and governance statement

- **No canonical schema change**: no DATA_MODEL.md update, no migration, no migration
  test (Issue acceptance criterion 5; persistence policy in docs/DEVELOPMENT.md section 12).
- **No canonical message-schema change**: MESSAGE_SCHEMA.md is referenced, not modified;
  every measured behavior is the already-shipped contract.
- **No architecture change**: the measurement consumes existing public boundaries
  (`WeChatContentParser`, `MessageNormalizer`, `ImportService`, `JsonlDatasetExporter`);
  the corpus adapter is test-only and adds no product surface.
- **No user-visible behavior change**: product code is untouched by this baseline.
  (Superseded by the M2b update in section 10, which does change classification and
  payload behavior for adopted defect cases.)
- Reliability levels unchanged (R1 capture / R2 ingest / R0 read-only query); this PR
  adds tests and documentation only.

## 10. M2b update: adopted defect cases (Issue #98)

The M2b slice ([Issue #98](https://github.com/lybym/WeArchive/issues/98), Roadmap M2b,
PRD G3/G5, FR-07/20/24) adopts D1, D2 and D4 from the section 7 matrix and leaves D3/D5
untouched. All changes keep the section 2 preservation invariants green: no record is
dropped, unknown stays explicit with preserved codes, unavailable fields stay null, and
the offline import → archive → export run stays byte-deterministic.

### 10.1 D1 (P1) — transfers are classified (adopted; bounded to the sampled subtype)

- Before: the 49/2001 `wcpayinfo` record `transfer_unclassified` normalized to
  `app_share` (partial); the canonical `transfer` count was 0.
- After: the sampled 49/2001 record with a local pay-card payload and a title that does
  not carry red-packet semantics normalizes to `transfer`. `transfer_unclassified`
  → `transfer`, complete, with `payload.amount = "200.00"` read from the locally recorded
  fee description; `currency`/`status` stay null (no sampled evidence carries them).
- Scope (review round 1, P1-1): the classification is gated to
  `appType == 2001` — the one shape the M2a corpus samples. The shipped red-packet rule
  (`wcpayinfo` + red-packet title) is unchanged and keeps precedence. Every other
  `wcpayinfo` shape keeps its pre-existing documented fallback: a pay card with
  title/description/url stays `app_share` partial (corpus
  `groupcollect_unsampled_shape`, subtype 2003), and a pay card without any of them stays
  the explicit `unknown` fallback (corpus `paycard_titleless_unknown`, subtype 2005,
  U4). This section and the section 7 D1 bounded requirement are now consistent:
  "every other wcpayinfo shape stays `app_share` partial" (or unknown when nothing is
  keepable) is the implemented and pinned behavior.
- Delta: `transfer` 0 → 2 (`transfer_unclassified` complete, additive
  `transfer_without_amount` partial); `app_share` 2 → 3 (additive
  `groupcollect_unsampled_shape` joins the original 87-subtype record); total records
  33 → 37; partial records 10 → 12 (`transfer_unclassified` left the partial set; the
  additive `transfer_without_amount`, `groupcollect_unsampled_shape` and
  `paycard_titleless_unknown` entered it); `partial_app_message` diagnostics 6 → 7
  (additive 49/2003 record).
- Boundary: the amount is recorded only when the local fee description is a plain unsigned
  decimal with a non-empty integer part. Fragmentary forms (`.50`, `200.`) and decorated
  forms (currency symbols, thousands separators) are not accepted and stay null with the
  record explicitly partial — no blanket claim for unsampled variants (MESSAGE_SCHEMA
  section 15; acceptance criterion 5). Classifying other pay-card subtypes (e.g. group
  collection) would require a new approved Issue with its own sampled evidence.

### 10.2 D2 (P2) — red-packet amount (adopted, bounded)

- Before: `redpacket_direct` carried `payload: null`.
- After: `redpacket_direct` exposes `payload = { "amount": "66.00", "currency": null,
  "status": null }` under the same plain-decimal rule as D1. The shipped red-packet shape
  is otherwise unchanged: the additive `redpacket_without_amount` record keeps
  `payload: null`, is not flagged partial, and red-packet classification (text `[红包]`)
  is unchanged.
- Delta: `red_packet` 1 → 2 (additive record); classification precision unchanged; no
  partial/diagnostic delta.

### 10.3 D4 (P3) — description-fallback diagnostic (adopted, parser-level)

- Before: description-fallback forward bundles carried only the partial flag.
- After: the parser marks the fallback with the dedicated code
  `forward_bundle_description_fallback` (Core `DiagnosticCodes`, MESSAGE_SCHEMA section
  12). The ingest-level diagnostic remains `partial_app_message` by the documented
  `ImportService` rule; classification, partial flags and export counters are unchanged.
- Delta: none in sections 4.1–4.5 (the code is parser-level and test-asserted; the
  ingest contract is unchanged).

### 10.4 Not adopted

- **D3** — unmodeled app subtypes stay `app_share` partial as documented; no concrete
  subtype was modeled with MESSAGE_SCHEMA backing, and types are not guessed from titles.
- **D5** — VoIP/system variants beyond the sampled shapes remain unmeasured; modeling
  them without real samples is explicitly out of scope for M2b.

### 10.5 Verification

- The pinned values live in
  `SemanticBaselineTests.CorpusClassificationMatchesThePinnedBaselineReport` and
  `OfflineImportArchiveExportIsByteDeterministicAndCountsUnknowns`; after the additive M2b
  corpus extension they matched this document's tables (37 records at the time of that
  slice; 44 after the M2c extension in section 11). The four new M2b
  records are appended, no existing record id was mutated or repurposed.
- New/updated unit coverage: `WeChatContentParserTests` (transfer with/without readable
  amount; fragmentary and decorated fee descriptions rejected; transfer classification
  bounded to the sampled subtype incl. the app_share/unknown fallbacks for other pay-card
  shapes; red-packet title precedence over the 2001 subtype; red packet with/without
  amount; both forward-bundle description-fallback paths) and
  `SemanticBaselineTests.UnavailableFieldsStayNullAndAreFlaggedPartial` (transfer/red-packet
  payload semantics and the pinned pay-card fallbacks at the canonical boundary).
- No persisted-schema change: the archive stores payloads as JSON text
  (`docs/DATA_MODEL.md` section 8.6); no migration or DATA_MODEL update is required.
- No architecture change: decoding stays in `src/WeArchive.Infrastructure/WeChat/Parsers`,
  domain semantics in `WeArchive.Core/Normalization`; reliability levels unchanged
  (R1 capture / R2 conversation ingest / R0 read-only query).

## 11. M2c update: reply/quote/forward reference resolution (Issue #99)

The M2c slice ([Issue #99](https://github.com/lybym/WeArchive/issues/99), Roadmap M2c,
PRD FR-24/25) adopts E1 and documents E2/E3 from the section 7 matrix. All changes keep
the section 2 preservation invariants green: no record is dropped, unknown stays explicit
with preserved codes, unavailable fields stay null, and the offline import → archive →
export run stays byte-deterministic.

### 11.1 E1 (P1) — reply references resolve against the archive (adopted)

- Before: `ParseReply` returned the raw upstream `<refermsg><svrid>` and every adapter kept
  it verbatim in `reply_source_message_id`, while target rows are archived under the
  documented composite identities (`s:<server_id>` / `l:<partition>:<local_id>`,
  DATA_MODEL section 16.2). The two identity spaces can never intersect, so the archive's
  resolution pass (`ResolveReplyTargetsAsync`) could not match anything for WeChat data:
  the measured baseline was `reply_to.message_id` = null for every quote, and the
  `WeChatWindowsSourceAdapter.BuildReplySourceMessageId` helper that documents the intended
  composition was dead code.
- After: `WeChatWindowsSourceAdapter.ComposeReplyReference` composes the parser's raw svrid
  verbatim into the `s:<server_id>` form at the adapter boundary (both the live and the
  captured-reader path flow through `ReadMessagesAsync`). The reference is now in the same
  identity space as the archived target, so the existing conversation-scoped resolution
  pass resolves it. No numeric interpretation, lookup or content matching is added; a
  reference that exists is wrapped, a missing one stays null.
- Corpus evidence (additive; no existing record id was mutated or repurposed): seven new
  records form the positive/negative reference matrix —
  `m2c_quote_target_text` + `m2c_quote_resolved` (resolved pair; the target carries a
  server id, the quote's svrid matches it), `m2c_quote_target_absent` (svrid names no
  archived record), `m2c_quote_target_unaddressable` + `m2c_quote_unaddressable_target`
  (a record with the exact quoted text exists but carries no server id, proving the
  resolver does not fall back to content matching), `m2c_quote_without_svrid` (malformed
  reference; snapshot retained), and `m2c_revoke_without_newmsgid` (E2). The corpus adapter
  now archives server-id-carrying records under `s:<server_id>` exactly like the shipped
  adapter; all other records keep their pre-existing `sb:<record_id>` test identity.
- Quantified delta: total records 37 → 44 (text 6 → 12 — the two addressable-target text
  records plus four more 49/57 quotes that normalize to text, all additive; revoke 1 → 2;
  all other type counts unchanged); partial records 12 → 12; `unknown` 5 → 5; every
  `unresolved_reply_target` diagnostic count 0 → 0 (all sampled quotes keep snapshots);
  resolved reply targets in the offline pipeline run 0 → 1 (`reply_snapshot_only` Info
  diagnostic 0 → 1). The M2a-pinned canonical outcomes of every pre-existing record are
  unchanged; only the pinned upstream reference *form* changed from raw svrid to the
  composed `s:` form (asserted by `QuoteSnapshotsArePreservedWithoutFabricatingATargetId`).
- Rollout: `WeChatWindowsSourceAdapter.Version` 0.2.0 → 0.3.0 (the documented load-bearing
  bump). The next ingest re-scans conversations and re-imports them idempotently: the
  upsert stores the composed reference, the NULL-safe CASE clears any stale resolved
  target when the reference changes (DATA_MODEL section 8.7), and the backfill pass
  re-resolves. Outcomes are deterministic under incremental ingest and offline rebuild.

### 11.2 E2 (P2) — revoke target linkage (documented, pinned; no behavior change)

The sampled evidence does not establish that a revokemsg `<newmsgid>` equals the server id
(or any archived identity) of the revoked record, and the bounded requirement forbids
guessing. The shipped behavior therefore stays exactly as measured and is now documented
in MESSAGE_SCHEMA section 14: `revoked_message_id` is the deterministic derivation over
the locally recorded `newmsgid` (`StableIds.Message(conversation, newmsgid)`), it stays
`null` when the record carries no `<newmsgid>`, and the archive deliberately runs no
resolution pass over revoke payloads. Consumers must treat a derived value that matches no
archived message as unresolved rather than following it as a canonical link. Pinned by
`RevokeTargetFollowsTheDocumentedNullFallback`, `RevokeMessagesBecomeRevokeEvents`,
`RevokeWithANewmsgidDerivesTheDeterministicTargetValue` and
`RevokeKeepsOperatorAndNoInventedContent`. Aligning the linkage to real evidence remains
future work that needs real captured samples.

### 11.3 E3 (P3) — forwarded-bundle nested timestamps stay null

No change, exactly as the matrix requires: WeChat 4.x does not persist per-item
timestamps, the parser keeps `items[].time` null (MESSAGE_SCHEMA section 12), and the
round-trip/export test asserts the nulls survive export (`ForwardBundlesPreserveNestedTextIncludingUnknownItems`,
the bundle assertions in `ReplyTargetsResolveInTheFullPipelineWithoutCrossIdentityGuessing`).
A future source that persists item times is a new corpus scenario, not a data change.

### 11.4 Verification and governance

- Pinned values live in `SemanticBaselineTests.CorpusClassificationMatchesThePinnedBaselineReport`
  and `OfflineImportArchiveExportIsByteDeterministicAndCountsUnknowns`; they match
  sections 4.1–4.5 after the additive corpus extension (44 records).
- New/updated coverage beyond the corpus pins: `WeChatContentParserTests` (refermsg
  without svrid; verbatim non-numeric svrid; revoke with/without newmsgid),
  `WeChatMessageShardTests.ReplyReferencesAreComposedIntoTheServerIdIdentitySpace`
  (adapter-level composition across a rotation-window boundary, live and captured path),
  `SqliteArchiveStoreTests.ReplyTargetResolvesAcrossShardPartitionsWithinOneConversation`
  and `ReplyResolutionNeverLinksAcrossConversations` (no cross-conversation wrong-target
  linking), and the full-pipeline matrix in `SemanticBaselineTests`.
- No persisted-schema change: `reply_source_message_id` already exists; only the values
  stored for newly imported WeChat records change form, covered by the version-bump
  re-import path. No migration or migration test is required.
- No canonical message-schema change: the exported envelope is unchanged (`message_id`
  semantics were already "null while unresolved"); MESSAGE_SCHEMA sections 6 and 14
  document the resolution identity and the revoke fallback in the same PR.
- No architecture change: the composition lives in the WeChat adapter boundary
  (`src/WeArchive.Infrastructure/WeChat`); the archive, normalizer and exporter are
  source-neutral and unchanged. Reliability levels unchanged (R1 capture / R2 conversation
  ingest / R0 read-only query); the version bump reuses the existing documented
  checkpoint-invalidation mechanism, adding no journal, marker or recovery state.
