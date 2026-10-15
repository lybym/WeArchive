# WeArchive Canonical Search Specification (M3b-0)

Status: normative specification. Sections 3–6 and 9–10 are **implemented** by the M3b index slice
([Issue #102](https://github.com/lybym/WeArchive/issues/102)); the `wearchive search` CLI transport
(sections 7–9 at the CLI layer and 12's exit-code mapping) is the follow-up slice (issue #103) and
is not implemented yet. This document is the deliverable of
[Issue #101](https://github.com/lybym/WeArchive/issues/101) (Roadmap M3b-0, PRD FR-15/16/17,
HARNESS.md sections 6–7). It fixes the searchable-field inventory, the normalized index input, the
matching/filter/cursor contract and the derived-index lifecycle for canonical full-text search, and
records the benchmark evidence that selects the FTS5 tokenization strategy (ADR 0013).

Until the CLI slice ships, `ArchiveQueryService` implements search (section 9.1) but no
`wearchive search` command exists. Shipped-status sections of other documents must not claim the
CLI command until it is delivered and tested.

## 1. Requirements trace

```text
PRD      FR-15 (local FTS over canonical semantic text and selected structured payload fields),
         FR-16 (paginated retrieval by stable identifiers), FR-17 (all search behind
         ArchiveQueryService), G6 (efficient machine retrieval), NFR-06/08/13
Roadmap  M3b — SQLite FTS5 index, Chinese/mixed-language retrieval evaluation, wearchive search,
         deterministic canonical provenance
HARNESS  sections 6–7: search canonical FTS -> small candidate set -> context windows; FTS is a
         derived, rebuildable index over canonical data, never an archival source of truth
ADR 0008 query access layer; ADR 0013 tokenization strategy selection (this specification)
```

Constraints carried over unchanged: search reads **only** the canonical SQLite archive through
`ArchiveQueryService` (R0 read-only query); it never opens live WeChat, acquires a key, reads Raw
Vault artifacts, scans exported JSONL, or introduces a new query service, a model-specific chunk
store, or an LLM-specific derived dataset (PRD section 5 non-goals).

## 2. Definitions

- **Canonical archive** — `wearchive.db` per `DATA_MODEL.md`; the only search input.
- **Searchable text** — the deterministic per-message concatenation defined in section 4.1.
- **Projected token stream** — the output of the CJK bigram projection (section 4.2) applied to
  searchable text; the exact token sequence a unicode61 FTS5 index stores.
- **Matching contract** — the query semantics of section 5. Benchmark ground truth is defined by
  this contract, not by any individual strategy's output.

## 3. Searchable-field inventory (normative)

One FTS row exists per canonical message. Its input is the message's own canonical data only:
`messages.semantic_text` plus the selected structured payload fields below
(PRD FR-15 "semantic text plus selected structured payload fields").

| Canonical type | Indexed fields |
| --- | --- |
| every type | `semantic_text` (always) |
| `file` | `payload.filename` |
| `link` | `payload.title`, `payload.description`, `payload.source_app` |
| `app_share` | `payload.title`, `payload.description`, `payload.source_app` |
| `mini_program` | `payload.app_name`, `payload.title` |
| `location` | `payload.label`, `payload.address` |
| `contact_card` | `payload.display_name` |
| `forward_bundle` | `payload.title`, `payload.items[].text` (in stored order) |

Excluded from the index input, per type:

- **Provenance and raw-only artifacts** — `source_*` columns, `reply_source_message_id`,
  unknown-payload `source_type`/`source_subtype`/`raw_summary`, `app_id`, `page_path`.
  These are engineering traceability, not message semantics (MESSAGE_SCHEMA section 18).
- **Structured measurements** — `amount`, `currency`, `status`, `latitude`, `longitude`,
  `duration_seconds`, `size_bytes`, `extension`. They have product meaning as structured data, not
  as free-text tokens; a future structured filter, not FTS, would query them.
- **Mutable names** — participant remarks/nicknames, conversation titles, forward-bundle
  `items[].sender_name`, and the reply snapshot text (`reply_snapshot_json`). Stable IDs, not
  mutable names, address entities (AGENTS.md); name→ID resolution belongs to the discovery surface
  (`conversation list` / `identities.yaml`), not to message FTS. The reply snapshot is excluded
  because it duplicates the target message's text whenever the target is archived; indexing it
  later is an additive contract change that requires its own documentation.
- **Secrets and undecoded binary** — none exist as canonical fields; Phase 1 preserves no binary
  payloads (MESSAGE_SCHEMA section 7). The rule is stated so a future field cannot silently enter
  the index: a field not listed above is excluded by default.

URLs inside `semantic_text` (link texts per MESSAGE_SCHEMA section 9, app-share texts per section
10) are canonical
message text and are therefore indexed as ordinary tokens; they arrive through the canonical text,
not through the excluded `original_url`/`fallback_url` payload fields.

The index is stored inside the archive file it derives from, so it carries exactly the archive's
local sensitivity envelope. No index content, query text or diagnostic may leave the machine or
appear in logs (NFR-08; diagnostics never echo query content).

## 4. Normalized index input (normative)

### 4.1 Searchable text construction

For one message, in field order (absent fields contribute nothing, no separators are emitted for
them):

```text
searchable_text =
    semantic_text
    \n payload fields from the section 3 table, in the listed order, one per line
```

Forward-bundle nested `items[].text` values are appended in stored item order. The construction is
a pure function of canonical row content: same rows → byte-identical searchable text.

### 4.2 CJK bigram projection

unicode61 treats consecutive CJK ideographs as one long token, so a 2-character query such as
`模型` can never match inside `大模型上线了`. The selected strategy (ADR 0013, benchmark section 11)
rewrites CJK runs into overlapping bigrams before indexing and applies the **same** projection to
query terms, which turns unicode61 token adjacency into exact CJK substring semantics.

Algorithm (deterministic; operates on UTF-16 code units):

1. A character is a **CJK ideograph** iff it is in U+3400–U+4DBF, U+4E00–U+9FFF or U+F900–U+FAFF.
2. Each maximal run of CJK ideographs of length ≥ 2 is replaced by its overlapping bigrams in
   order, separated by a space: `大模型上线` → `大模 模型 型上 上线`; `模型评估流程` →
   `模型 型评 评估 估流 流程`. A run of length 1 is copied verbatim.
3. A single separating space is inserted at every CJK↔non-CJK boundary, because unicode61 treats
   ASCII letters/digits and CJK ideographs alike as token characters: without it, `汇报V8` or
   `200.00元` would glue into one token. The space is itself a unicode61 separator, so it never
   merges tokens and never changes within-class tokenization.
4. All other characters are copied verbatim.

The projection is applied to the searchable text at index time and to each query term at query
time (section 5.2). It is **not** applied to canonical storage: `semantic_text`, payloads and every
exported artifact remain exactly as defined by MESSAGE_SCHEMA; the projection exists only inside
the derived index. Any change to the algorithm changes match behavior and therefore requires a
specification update plus a re-run of the pinned benchmark tests; the follow-up index slice must
rebuild the derived index when the projection version changes.

Known limitation (documented, not fixed in v1): a single-CJK-character query (`看`) cannot match
inside longer CJK runs — that would require a unigram index whose size and precision costs are not
justified by any current requirement. A single character still matches when it stands alone as a
run (for example `看` in `看 一下`). Supplementary-plane ideographs (outside the BMP ranges above)
are left to default unicode61 behavior and are not bigram-augmented in v1.

## 5. Matching semantics (normative)

### 5.1 Query grammar

A query string is a sequence of whitespace-separated **terms**:

- An unquoted term is matched as an ordered token sequence (phrase) over the projected token
  stream — for a multi-character CJK run this is exactly substring semantics; for a Latin word it
  is whole-word semantics (unicode61 splits terms like `GPT-5` into `gpt` + `5` and requires them
  adjacent, so `200.00` matches `1,200.00` as a substring would).
- A term wrapped in double quotes (`"三点开会"`) is one phrase term; quotes allow spaces inside a
  term. A quote spans whitespace until its closing quote or the end of the query; an unbalanced
  quote is an ordinary character of the term it appeared in.
- A term with one trailing `*` (`model*`) is a prefix term: the last projected token matches any
  token with that prefix (`model*` matches `model` and `models`).
- Multiple terms combine with implicit AND: every term must match the same message.
- Matching is case-insensitive for ASCII (unicode61 case folding) and punctuation-transparent
  inside phrases: token positions do not advance over separators, so `开会。讨论` contains the
  adjacent pair `开会`/`讨论` just like `开会 讨论` does.

There is no OR/NOT/NEAR, no field-scoped syntax, no fuzzy or pinyin matching and no score syntax in
v1 (section 13).

### 5.2 Query normalization

Every term is projected with the section 4.2 algorithm before it reaches FTS5, identically to the
indexed text. The CLI/Service query compiler then quotes every term (doubling embedded quotes), so
caller input can never become FTS5 query syntax: an input like `模型") OR (1=1) --` is a literal
token sequence that simply matches nothing.

### 5.3 Invalid queries

A query is invalid — a deterministic usage failure (`query_invalid`, exit 2, section 12) — when it
contains no terms, or a term contains no letter, digit or CJK ideograph (for example `*`, `!!!`,
or a term made only of quote characters). Validation happens in the compiler before any FTS5 call;
FTS5 parse errors are never leaked to callers. Error messages must not echo query content.

### 5.4 Empty and no-match results

Zero matches is a **successful** result: exit 0 with an empty `items` array, `has_more: false` and
a null `next_cursor`. An empty archive behaves the same way. Search is never a failure because it
found nothing.

## 6. Ranking and ordering (normative)

Result order is the FTS5 `bm25` rank of the projected body (lower is better), with a deterministic
total order tie-break:

```text
(bm25 rank ascending, occurred_utc ascending, COALESCE(source_order_key, '') ascending, id ascending)
```

This is the same canonical timeline tail the M3a bounded retrieval uses (DATA_MODEL section 8.1),
so ties never shuffle between pages and the order is stable for a given archive state. The JSON
contract exposes no score value: bm25 numbers are implementation-specific and would pin the
contract to SQLite internals; only the order is part of the contract.

## 7. Filters and scoping (normative)

All filters reuse the M3a `message list` semantics and validation (`CLI.md`):

- `--conversation <stable-id>` restricts results to one canonical conversation
  (`g_<16 hex>` / `u_<16 hex>`).
- `--collection <name>` restricts results to the union of the Collection's member conversations,
  resolved through the one authoritative Collection catalog (ADR 0009). Unknown name →
  `collection_not_found` (exit 1); invalid configuration → `collection_config_invalid` (exit 2);
  a member that resolves to no archived conversation contributes no results and never errors by
  itself.
- `--since` / `--until` — inclusive instants, identical time grammar and one-second resolution as
  `message list` (DATA_MODEL section 23.1).
- `--participant <stable-id>` — canonical sender participant ID, same as `message list`.
- `--type <canonical-type>` — canonical semantic type wire name (MESSAGE_SCHEMA section 4).

`--conversation` and `--collection` are mutually exclusive scopes in v1; supplying both is a usage
error. Filters narrow the candidate set; they never rewrite the query terms.

## 8. Pagination (normative)

- Page size `--limit <n>`, 1–500, default 100 — identical bounds to `message list`.
- The response envelope is exactly the M3a shape: `items`, `next_cursor`, `has_more`; a page reads
  one record beyond its limit, so `has_more` is a fact about the archive; `next_cursor` is null
  exactly when `has_more` is false.
- Cursors are **opaque API tokens, not persistent archive records** (DATA_MODEL section 23.2): an
  encoding-versioned token carrying the exclusive position of the last returned item in the
  section 6 total order plus a fingerprint of the normalized query + filter set. Page size is
  excluded, so a caller may change `--limit` between pages. An empty, malformed,
  unsupported-version or mismatched cursor is `cursor_invalid` (exit 2), never a silently
  different page.
- Execution is page-bounded: the service fetches at most limit+1 rows per page. No query materializes
  an unbounded result set in memory (Issue #101 acceptance criterion 3).

## 9. Result contract (proposed — implemented by the follow-up slices)

### 9.1 Core service extension

Search is an **extension of the existing `ArchiveQueryService`** — no second query service, no
model-specific chunk store, no new persistent entity (implemented by the index slice,
[Issue #102](https://github.com/lybym/WeArchive/issues/102)):

```csharp
public sealed record SearchRequest
{
    public required string Query { get; init; }          // section 5 grammar
    public string? ConversationId { get; init; }         // section 7 (mutually exclusive scope)
    public string? CollectionName { get; init; }
    public string? Since { get; init; }
    public string? Until { get; init; }
    public string? ParticipantId { get; init; }
    public string? Type { get; init; }
    public int? Limit { get; init; }
    public string? Cursor { get; init; }
}

public sealed record SearchPage
{
    public required IReadOnlyList<CanonicalMessage> Items { get; init; }  // same DTO as message list
    public required bool HasMore { get; init; }
    public string? NextCursor { get; init; }
}
```

Hits return the same canonical message DTO the M3a commands return — canonical envelope, payload,
resolved reply and the full provenance block (MESSAGE_SCHEMA section 3.4) — so a Harness can chain
`search` → `context` with stable IDs without a second representation. The service reads only the
canonical archive through `IArchiveStore`; validation, filter resolution and cursor handling follow
the M3a service patterns (R0: no write path, no checkpoint, no recovery state).

### 9.2 CLI transport (proposed `wearchive search`; implemented by the follow-up CLI slice)

```text
wearchive search <query> [--conversation <stable-id>] [--collection <name>]
                  [--since <time>] [--until <time>] [--participant <stable-id>]
                  [--type <canonical-type>] [--limit <n>] [--cursor <opaque>]
                  [--json] [--no-input] [--quiet]
```

`--json` stdout (exit 0): one document with the `items` / `next_cursor` / `has_more` envelope where
every item is byte-shaped like the `message list` item DTO. Human output prints one line per hit
led by the stable message id, then the `next cursor:` line when resumable. New stable error codes
added to the failure-document table: `query_invalid` (exit 2, section 5.3) and
`search_index_unavailable` (exit 1, section 10.4); all other paths reuse the existing codes
(`conversation_not_found`, `collection_not_found`, `collection_config_invalid`, `archive_unavailable`,
`cursor_invalid`, `usage_error`, `cancelled`).

## 10. Index lifecycle, migration and integrity (normative approach)

### 10.1 Derived state classification

The FTS index is **R0 derived state** (DEVELOPMENT.md section 10.2): rebuildable from the
canonical archive, never authoritative, never a second system of record. No journal, commit
marker, transaction protocol or recovery state is added (AGENTS.md hard-stop rule; the strongest
mechanism below is an ordinary SQLite transaction).

### 10.2 Creation and sync

- A numbered forward-only migration creates the FTS5 virtual table; `schema_migrations` and
  `user_version` advance accordingly (DATA_MODEL section 18). The migration also backfills the
  index from existing canonical rows, so an upgraded archive is immediately searchable.
  The concrete DDL (contentless-delete vs plain table with an UNINDEXED stable message-id column)
  is the index slice's implementation decision within the constraints: stable message-id ↔ row
  mapping, no canonical column changes, deterministic rebuild.
- Index rows are maintained **inside the same SQLite transaction** as the message upserts of one
  conversation import. This adds no new transaction protocol — it is the documented R2
  conversation-import boundary with one more write inside it — and it means a published
  conversation is always fully indexed; no staleness window exists on any product path.
- `wearchive rebuild` recreates the index together with the canonical database (fresh migrations +
  import), so rebuild output is searchable without a separate step.

### 10.3 Deterministic rebuild from canonical data only

A full index rebuild is a pure re-derivation over section 4 input: re-running it against the same
canonical rows yields a functionally identical index (the pinned benchmark asserts identical match
sets and byte size). Rebuild is available as an explicit maintenance operation of the index slice
and runs implicitly for a freshly migrated or rebuilt archive. The index slice must add a
migration test (previous schema version → index created + backfilled) and a rebuild-determinism
test (DATA_MODEL section 18; DEVELOPMENT.md section 7 migration-test rule).

### 10.4 Integrity and failure behavior

- Integrity verification uses SQLite's FTS5 `integrity-check` plus a messages-vs-index row-count
  agreement check, exposed as an archive-maintenance verification, not a per-query tax.
- Because maintenance is transactional (section 10.2), staleness cannot arise through product
  paths; external corruption or manual tampering is the remaining failure class. Search treats a
  missing or failed-verification index as fail-closed: `search_index_unavailable` (exit 1) with a
  diagnostic, never a silently partial result set, and no automatic repair (R0 read-only query).
  Recovery is the explicit rebuild of section 10.3.

## 11. Benchmark evidence (Issue #101 acceptance criterion 1)

### 11.1 Method

Three locally-supported, deterministic FTS5 strategies were measured over the same fixture corpus:

```text
unicode61           FTS5 default tokenizer over the raw searchable text
trigram             FTS5 trigram tokenizer over the raw searchable text
bigram-projection   unicode61 over the section 4.2 projected searchable text  <- selected (ADR 0013)
```

- Native Chinese tokenizers (ICU, jieba) are not available in the bundled
  SQLite (`e_sqlite3` 3.53.3, FTS5 enabled; no ICU), and a managed segmentation library would add
  a dependency plus dictionary-version nondeterminism, violating the dependency policy
  (DEVELOPMENT.md section 14). Bigram projection is the "locally-supported deterministic
  tokenization" option the Issue allows.
- **Corpus.** `tests/WeArchive.Tests/Support/SearchBenchmarkCorpus.cs`: 31 deterministic synthetic
  seed documents covering the Issue's probe classes — CJK 2-char words inside longer runs
  (`模型`, `玩具`, `开会`), CJK ≥3-char substrings (`人工智能`, `项目汇报`), mixed Chinese/Latin
  (`AI玩具`), abbreviations (`API`, `OKR`), punctuation-bearing terms (`GPT-5`, `200.00`),
  partial terms inside filenames/payload fields (`汇报`, `pptx`, `华东`), quoted phrases,
  multi-term AND, prefix, URL/identifier exclusions and no-match controls. All content is
  synthetic placeholder material (docs/DEVELOPMENT.md section 8).
- **Probe set.** 36 probe queries (50 relevant document/probe pairs) whose ground truth is defined
  by the section 5 matching contract — not by any strategy's output. Zero-relevance probes
  (exclusions, no-match) contribute false positives only.
- **Metric definition.** Retrieval is folded per seed document (a seed counts as retrieved when
  any of its scale replicas matches), so recall/precision are independent of the replica
  multiplier: recall = TP/(TP+FN) and precision = TP/(TP+FP), micro-aggregated.
- **Runner.** `tests/WeArchive.Tests/FtsSearchBenchmarkTests.cs`. Quality pins and the decisive
  match-set matrix run in CI at seed scale; `WEARCHIVE_SEARCH_BENCHMARK_SCALE=<docs-per-seed>`
  runs the scale measurement that produces the tables below. Every index passes the FTS5
  `integrity-check`; rebuild determinism (identical match sets and byte size) is pinned.

### 11.2 Quality results (seed corpus; pinned by `FtsSearchBenchmarkTests`)

Overall (micro over all 36 probes):

| Strategy | Recall | Precision |
| --- | --- | --- |
| unicode61 | 21/50 = 0.420 | 1.000 |
| trigram | 28/50 = 0.560 | 28/29 = 0.965 |
| bigram-projection | 50/50 = 1.000 | 1.000 |

Recall by probe category (relevant pairs matched / total):

| Category | unicode61 | trigram | bigram-projection |
| --- | --- | --- | --- |
| CJK 2-char substring (11) | 1/11 | 0/11 | 11/11 |
| CJK ≥3-char substring (9) | 0/9 | 9/9 | 9/9 |
| Mixed Chinese/Latin (4) | 2/4 | 1/4 | 4/4 |
| Latin whole-word (8) | 7/8 | 6/8 | 8/8 |
| Punctuation/abbreviations (8) | 7/8 | 8/8 | 8/8 |
| Partial terms in filenames/fields (6) | 2/6 | 1/6 | 6/6 |
| Quoted phrase (1) | 0/1 | 1/1 | 1/1 |
| Multi-term AND (1) | 0/1 | 0/1 | 1/1 |
| Prefix (2) | 2/2 | 2/2 | 2/2 |

Decisive measured behaviors (pinned per probe):

- **unicode61** treats a whole CJK run as one token: `模型` never matches inside `大模型上线了`,
  `AI玩具` is unfindable as `AI`+`玩具`, and CJK↔Latin glue even defeats Latin words (`确认Q4`
  hides `q4`; `200.00元` hides `00`). Its one category hit (`张三` standing alone) shows the
  failure is the tokenizer, not the fixtures.
- **trigram** answers every ≥3-character substring but returns nothing for 2-character queries —
  `模型`, `玩具`, `开会`, `周报`, `张三`, `AI`, `Q4`, `价格` all miss — and its substring semantics
  cost precision: `search` matches `research` (the one false positive in the matrix).
- **bigram-projection** satisfies the whole matching contract, including `模型` ⊂ `大模型上线了`,
  `AI玩具` with adjacency, `华东`/`汇报` inside `华东中心项目汇报V8.pptx`, `周五` inside a
  forwarded bundle item, the `三点开会` phrase, `开会 预算` as AND, and `model*`.

### 11.3 Cost results (realistic corpus scale)

Measured offline on file-backed temporary databases, one ingest transaction per index, default
journal mode, every index verified with FTS5 `integrity-check`.

Environment: Windows 11 (NT 10.0.26300), AMD Ryzen 7 8845H, .NET 10 SDK 10.0.401,
Microsoft.Data.Sqlite 10.0.12 (bundled `e_sqlite3`, SQLite 3.53.3, FTS5 enabled). Absolute
latency/ingest numbers are machine-relative; the pinned invariants (quality matrix, size ordering,
rebuild determinism) are the decision evidence, and the table's relative ordering is what matters.

| Corpus | Strategy | Index bytes | Bytes/doc | Ingest wall | Median query |
| --- | --- | --- | --- | --- | --- |
| 10,354 docs | unicode61 | 933,888 | 90 | 0.06 s | 0.069 ms |
| 10,354 docs | trigram | 1,355,776 | 131 | 0.05 s | 0.280 ms |
| 10,354 docs | bigram-projection | 1,331,200 | 129 | 0.04 s | 0.295 ms |
| 103,354 docs | unicode61 | 9,261,056 | 90 | 0.35 s | 0.055 ms |
| 103,354 docs | trigram | 15,097,856 | 146 | 0.38 s | 2.104 ms |
| 103,354 docs | bigram-projection | 13,287,424 | 129 | 0.30 s | 2.054 ms |

Scale rationale: 10k messages ≈ a light personal archive; 100k messages ≈ a heavy multi-year
archive, far above today's canonical stores. Full rebuild of the selected strategy at 100k
documents (ingest + integrity check) measured 0.43 s wall — rebuild cost is negligible against
canonical rebuild, which already reparses all evidence.

Reading of the costs:

- Storage: bigram-projection costs +43% bytes/doc over unicode61 (129 vs 90) and 12% less than
  trigram at 100k. On a 100k-message archive the whole index is ~13 MB — small against the
  canonical database itself.
- Latency: unicode61 is ~37× faster per query at 100k (0.055 ms vs 2.05 ms median) because
  projected queries carry more terms — but both strategies answer in single-digit milliseconds,
  orders of magnitude under any interactive budget, and unicode61's speed buys a 0.42 recall.
- Rebuild: 0.43 s at 100k docs; linear scaling from the 10k/100k pair.

### 11.4 Selection (recorded in ADR 0013)

**bigram-projection is selected.** It is the only measured strategy that satisfies the matching
contract (recall 1.000, precision 1.000 on the probe set), it costs ~13 MB per 100k messages and
single-digit-millisecond queries, its projection is a small, fully specified, dependency-free
algorithm (section 4.2), and it keeps the entire stack on the bundled SQLite unicode61 tokenizer.
unicode61-only fails the product's core Chinese substring requirement; trigram-only fails every
2-character query and loses Latin word precision; a double-index hybrid (unicode61 + trigram)
would pay for two indexes (~236 bytes/doc) and two query paths to reach capabilities the projection
already delivers. The residual limitations (single-CJK-character queries, supplementary-plane
ideographs, punctuation-transparent phrases) are documented in sections 4.2/5.1 rather than hidden.

## 12. Failure modes (normative)

| Condition | Behavior |
| --- | --- |
| Empty/whitespace query, or a term without any letter/digit/CJK character | `query_invalid`, exit 2, before any FTS5 call; message never echoes query content |
| FTS5 operator-looking input (`模型") OR (1=1) --`) | Literal token sequence; matches nothing; exit 0 with empty results (no injection surface) |
| No matches / empty archive | Success: empty `items`, `has_more` false, null cursor, exit 0 |
| Unknown conversation ID | `conversation_not_found`, exit 1 |
| Malformed participant ID (not `u_<16 hex>`) | `usage_error`, exit 2 |
| Unknown but well-formed participant ID | Not an error — same as `message list`: the filter matches nothing and returns an empty successful page (exit 0) |
| Unknown Collection name / invalid Collection config | `collection_not_found` exit 1 / `collection_config_invalid` exit 2 |
| Missing, corrupt or failed-verification index | `search_index_unavailable`, exit 1, structured diagnostic; no automatic repair, no silent partial results |
| Unreadable archive | `archive_unavailable`, exit 1 |
| Malformed/mismatched cursor | `cursor_invalid`, exit 2 |
| Cancellation | exit 130; no state mutated (R0) |

## 13. Non-goals

- No boolean operators (OR/NOT/NEAR), field-scoped queries, fuzzy/pinyin matching, regex or
  similarity search in v1.
- No score/snippet/highlight exposure in the v1 JSON contract (additive future work).
- No search over Raw Vault artifacts, exported JSONL or any non-canonical store; no embedding,
  vector or LLM chunk dataset (PRD section 5).
- No automatic index repair; no new reliability machinery (section 10.1).
- No change to canonical schema semantics, MESSAGE_SCHEMA output, export behavior or any R1/R2
  boundary.

## 14. Governance

- Documentation-led: this specification, ADR 0013 and the cross-references in
  `PRD.md`/`ROADMAP.md`/`ARCHITECTURE.md`/`DATA_MODEL.md`/`HARNESS.md`/`CLI.md` land together;
  the benchmark pins live in `tests/WeArchive.Tests/FtsSearchBenchmarkTests.cs` and must match
  section 11 at all times — a failing pin invalidates the evidence and forces re-evaluation.
- The index slice (FTS5 table, migration, migration test, rebuild, integrity, `ArchiveQueryService`
  extension) is delivered by [Issue #102](https://github.com/lybym/WeArchive/issues/102); the CLI
  slice (`wearchive search` transport, JSON/error contract, CLI contract tests) is the follow-up
  Issue #103; each must reference this specification and keep its PR within its own scope
  (DEVELOPMENT.md section 6).
- Shipped-status language anywhere in `docs/` must continue to distinguish implemented (the index
  slice) from the not-yet-implemented CLI transport (issue #103).
