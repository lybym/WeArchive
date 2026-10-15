# ADR 0013 — Canonical FTS5 search uses unicode61 with a deterministic CJK bigram projection

Status: accepted

Date: 2026-10-15

Issue: [#101](https://github.com/lybym/WeArchive/issues/101)

Specification: [SEARCH_SPEC.md](../SEARCH_SPEC.md) (normative algorithm, matching contract, index
lifecycle and benchmark evidence)

## Context

M3b needs local full-text search over canonical Chinese/mixed-language archive data (PRD FR-15/16/17,
Roadmap M3b, HARNESS.md sections 6–7). Before building the FTS5 index (follow-up Issue) the
tokenization strategy had to be selected from the options SQLite actually supports in the shipped
binary. The bundled `e_sqlite3` (via Microsoft.Data.Sqlite 10.0.12, SQLite 3.53.3) compiles FTS5
but no ICU tokenizer, and the dependency policy (DEVELOPMENT.md section 14) rules out a managed
segmentation library or a native tokenizer extension. Chinese search has a specific hard
requirement: 2-character words (`模型`, `玩具`, `开会`) inside longer CJK runs, mixed Latin/CJK
runs (`AI玩具`, `确认Q4`), and punctuation-bearing terms (`GPT-5`, `200.00`) must match.

[Issue #101](https://github.com/lybym/WeArchive/issues/101) benchmarked three locally-supported,
deterministic strategies over a privacy-safe fixture corpus with a matching-contract ground truth
(docs/SEARCH_SPEC.md section 11): plain `unicode61`, the `trigram` tokenizer, and `unicode61` over
a deterministic CJK bigram projection (CJK runs of ≥2 characters rewritten to overlapping bigrams,
with boundary separators so CJK and Latin token characters never glue).

## Decision

Canonical search indexes the **bigram-projected searchable text** with the FTS5 `unicode61`
tokenizer, and applies the same projection to every query term:

1. Searchable input per message = `semantic_text` plus the selected structured payload fields
   (SEARCH_SPEC.md section 3), constructed deterministically.
2. The CJK bigram projection (SEARCH_SPEC.md section 4.2) is applied at index time and query time,
   turning unicode61 token adjacency into exact CJK substring semantics. It never touches canonical
   storage, exports or MESSAGE_SCHEMA output.
3. Matching semantics, filters, ranking, cursor pagination, index lifecycle and failure modes are
   normative in SEARCH_SPEC.md sections 5–10 and 12.
4. The index is R0 derived state: created by a forward migration, maintained inside the existing
   R2 conversation-import transaction, rebuildable from canonical rows only, verified by FTS5
   integrity-check, and never repaired implicitly by a query.

## Alternatives considered

- **Plain `unicode61`.** Measured recall 0.420: whole CJK runs become single tokens, so 2-char
  Chinese substring queries (`模型` ⊄ `大模型上线了`), `AI玩具`, and even Latin words glued to CJK
  (`确认Q4`, `200.00元`) fail. Rejected: fails the product's core Chinese retrieval requirement.
- **`trigram`.** Measured recall 0.560, precision 0.965: every query shorter than 3 characters
  returns nothing (most 2-character Chinese words), and substring semantics false-positives Latin
  words (`search` matches `research`). Its index is also the largest (146 bytes/doc at 100k).
  Rejected: cannot serve the dominant Chinese query length.
- **Double index (`unicode61` + `trigram`) with query routing.** Reaches most capabilities of the
  projection but pays two indexes (~236 bytes/doc combined) and two query paths plus a routing
  rule; the projection delivers the same contract with one index. Rejected on storage/complexity.
- **Native Chinese tokenizer (ICU/jieba) or managed segmentation library.** Not available in the
  bundled SQLite build; a library adds a dependency and dictionary-version nondeterminism against
  the repository's dependency-minimal, deterministic-build policy. Rejected.

## Consequences

- The selected strategy measured recall 1.000 / precision 1.000 on the probe set, a ~13 MB index
  per 100k messages (+43% over plain unicode61, 12% below trigram) and single-digit-millisecond
  median queries at 100k documents; a 100k-document rebuild measured 0.43 s.
- The bigram projection becomes a stable specification contract: changing it changes match
  behavior and requires a SEARCH_SPEC.md update, a re-run of the pinned benchmark tests, and an
  index rebuild (R0 derived state).
- Documented v1 limitations: single-CJK-character queries do not match inside longer CJK runs;
  supplementary-plane ideographs are not bigram-augmented; phrase matching is
  punctuation-transparent. No boolean operators, fuzzy/pinyin matching or score exposure in v1.
- No reliability upgrade: the FTS index adds one more derived write inside the already documented
  SQLite transaction boundary (R2 ingest unchanged, R0 query unchanged); no journal, commit
  marker or recovery protocol exists.
- Implementation is split into a follow-up index slice (FTS5 table, migration + migration test,
  rebuild, integrity, `ArchiveQueryService` extension) and a CLI slice (`wearchive search`
  transport), each Issue-first and referenced to SEARCH_SPEC.md. Until those slices ship, no
  product search exists and shipped-status language in docs must not claim it.

## References

- [SEARCH_SPEC.md](../SEARCH_SPEC.md) — canonical search specification and benchmark
- [Issue #101](https://github.com/lybym/WeArchive/issues/101) — M3b-0 specification/benchmark issue
- [PRD.md](../PRD.md) FR-15/16/17, [ROADMAP.md](../ROADMAP.md) M3b,
  [HARNESS.md](../HARNESS.md) sections 6–7
- [ADR 0008](0008-raw-vault-canonical-query-layers.md) — query access layers
- Benchmark pins: `tests/WeArchive.Tests/FtsSearchBenchmarkTests.cs`
