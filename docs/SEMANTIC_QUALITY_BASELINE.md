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
  codes plus message XML exactly as the message shards store them. Because the corpus
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
| Denominator | Total corpus records measured through the shipped parser → normalizer boundary (33). |
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
| `app_share` | type 49 with recognized-but-unmodeled app payloads | `appshare_unmodeled_subtype` (subtype 87), `transfer_unclassified` (subtype 2001 — defect D1) | explicitly partial; every obtainable field kept |
| `mini_program` | app subtypes 33/36 or weappinfo present | `miniprogram_direct` | appid/pagepath without URL guessing |
| `forward_bundle` | app subtype 19, or `recorditem` present | `bundle_group_structured`, `bundle_group_description_fallback` | nested items preserved incl. unknown item types |
| `location` | type 48; app subtype 17 | `location_direct` | poinname/label/address/lat-long only when present |
| `contact_card` | type 42 | `contactcard_direct` | display name + source user id only |
| `system` | types 10000/10002 (non-revoke), type 50 VoIP | `system_group_template`, `system_plain_event`, `voip_direct` | template links resolved; unresolved templates stay partial |
| `revoke` | type 10000 with `revokemsg` | `revoke_group` | operator/newmsgid only when locally present |
| `red_packet` | type 49 + `wcpayinfo` + title containing 红包 | `redpacket_direct` | payload null; financial fields not extracted yet (D2) |
| `transfer` | — none — | (absent; denominator still counts the corpus transfer record under `app_share`) | defect D1 |
| `emoji` | type 47; app subtype 8 | `emoji_direct` | payload null |
| `unknown` | every other classic type; type 49 without a usable app payload; unparseable XML | `unknown_classic_type`, `app_without_appmsg`, `app_malformed_xml`, `unknown_classic_system` | never dropped; codes preserved |

## 4. Measured baseline (pinned by `SemanticBaselineTests`)

### 4.1 Classification by canonical type

| Canonical type | Count |
| --- | --- |
| text | 6 |
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
| revoke | 1 |
| red_packet | 1 |
| transfer | 0 (not produced — see D1) |
| unknown | 4 |
| **Total (denominator)** | **33** |

### 4.2 By conversation category and source version

| Conversation category (WeChat4Schema.ClassifyConversation) | Records |
| --- | --- |
| Direct (`wxid_bob`) | 21 |
| Group (`100200300@chatroom`) | 7 |
| Official (`gh_example_news`) | 2 |
| System (`filehelper`) | 3 |

All 33 records carry `source_version = synthetic-wechat4x-m2a-1` in provenance; the
sample is single-version by design (section 1).

### 4.3 By upstream source type (type/subtype)

21 distinct upstream `(type, subtype)` pairs in the corpus; the per-record verbatim codes
are pinned by `EveryCorpusRecordReachesTheCanonicalBoundaryWithItsCodesPreserved`.

| Upstream (type, subtype) | Records | Canonical outcome(s) |
| --- | --- | --- |
| (1, 0) text | 5 | text ×5 |
| (34, 0) voice | 3 | voice ×3 (2 partial: duration unavailable) |
| (3, 0) image | 1 | image |
| (43, 0) video | 1 | video |
| (47, 0) emoji | 1 | emoji |
| (49, 5) app/url | 4 | link ×3, unknown ×1 (malformed XML, U3) |
| (49, 6) app/file | 2 | file ×2 (1 partial: name unavailable) |
| (49, 19) app/forward bundle | 2 | forward_bundle ×2 (1 partial: description fallback) |
| (49, 33) app/mini program | 1 | mini_program |
| (49, 57) app/quote | 1 | text + reply snapshot |
| (49, 87) app/unmodeled | 1 | app_share partial (D3) |
| (49, 101) app envelope w/o appmsg | 1 | unknown partial (U2) |
| (49, 2000) app/red packet | 1 | red_packet |
| (49, 2001) app/transfer | 1 | app_share partial (D1) |
| (42, 0) contact card | 1 | contact_card |
| (48, 0) location | 1 | location |
| (50, 0) VoIP | 1 | system (event voip) |
| (10000, 0) system | 2 | system (template), revoke |
| (10002, 0) system extended | 1 | system |
| (1000007, 0) unmodeled classic | 1 | unknown (U1) |
| (1050001, 0) unmodeled classic | 1 | unknown (U1) |

Four wire outcomes land in `unknown` (U1 ×2, U2, U3); none is dropped.

### 4.4 Partial and diagnostics

| Metric | Count |
| --- | --- |
| Partial records | 10 (`voice_direct_untimed`, `file_direct_unnamed`, `appshare_unmodeled_subtype`, `transfer_unclassified`, `unknown_classic_type`, `app_without_appmsg`, `app_malformed_xml`, `bundle_group_description_fallback`, `voice_group_untimed`, `unknown_classic_system`) |
| Complete records | 23 |
| `unknown_message_type` diagnostics | 4 (upstream codes preserved per record: 1000007/0, 49/101, 49/5, 1050001/0) |
| `partial_app_message` diagnostics | 6 (34/0 ×2, 49/6, 49/87, 49/2001, 49/19) |
| `unresolved_reply_target` diagnostics | 0 (the corpus quote carries a locally available snapshot) |

### 4.5 Export-manifest counters (real pipeline run)

The corpus is imported through `ImportService` and exported through
`JsonlDatasetExporter` into a fresh SQLite archive; the manifests aggregate to:
`record_count` 33, `unknown_count` 4, `partial_count` 10, `unsupported_count` 4
(`unsupported_count` mirrors `unknown_count` by exporter contract). Two independent
archive-and-export runs produce byte-identical JSONL datasets and identical counters.

### 4.6 Nested text preservation (baseline behaviors that must not regress)

- Quote/reply: `reply_to.sender_name` (张三), `reply_to.text` (下午三点开会。), resolved
  canonical `sender_id` and the quoted record's `createtime` are preserved;
  `reply_to.message_id` stays null at normalization and is filled only by the archive
  backfill pass when the target is present (docs/MESSAGE_SCHEMA.md section 6).
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
| U4 | Unmodeled app payload without fields | `appmsg` with no title/description/url | canonical `unknown` (nothing to keep in app_share) | parser `ParseAppMessage` default branch; MESSAGE_SCHEMA section 18 |
| U5 | Nested forward item of unknown datatype | `dataitem datatype ∉ {1,2}` without a placeholder text | item type `unknown` inside the preserved bundle; bundle itself not dropped | parser `MapForwardItemType`; test `ForwardBundlesPreserveNestedTextIncludingUnknownItems` |

No hidden drops exist on the measured surface: the no-drop invariant (emitted == corpus)
is asserted at the canonical boundary and at the exported dataset boundary.

## 7. Prioritized defect matrix for M2b/M2c/M2d

Priorities: **P1** = directly misrepresents available local semantics; **P2** = loses
obtainable field value while staying explicit; **P3** = documentation/diagnostic
polish. "Bounded requirement" states what the slice may change; anything beyond needs a
new approved Issue. Unless stated otherwise: no canonical schema change, no fabricated
values, unknown stays explicit (Issue #97 governance; AGENTS.md mandatory rules).

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
revoked text.

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
- Reliability levels unchanged (R1 capture / R2 ingest / R0 read-only query); this PR
  adds tests and documentation only.
