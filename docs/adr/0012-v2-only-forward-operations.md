# ADR 0012 — V2-only forward Raw Vault operations and acceptance

Status: accepted

Date: 2026-10-15

Issue: #94

Supersedes: no persisted-format specification; narrows the **future product support and acceptance policy** in ADR 0011 without altering its historical format contract.

## Context

WeArchive `v0.6.0` Stable shipped on 2026-10-14. Issue #86 accepted the official-RC Raw Vault v2 hourly capture trace and offline rebuild; Issue #93 repaired acceptance scripts before Stable. New capture already writes manifest version 3 / vault format 2. The repository also contains an implemented read path for historical vault-format-1 and mixed lineages.

The #86 real-environment run started from a fresh acceptance root. It verified v2 captures, integrity, offline rebuild and storage economics, but did **not** directly exercise v1-only or mixed v1/v2 real-world history. This coverage gap is disclosed, not quietly reclassified as tested.

The owner has decided to stop investing in format-v1 workflows: only **V2 for all future capture/sync and post-v0.6.0 acceptance**. Keeping a v1-only/mixed real-environment gate would spend effort on a retired operational mode.

## Decision

1. **V2-only forward path.** New operational capture/sync baselines and acceptance fixtures target v2 generations (manifest v3 / vault format 2) and the existing canonical ingest/query contract. Do not create new v1 generations or expand v1 writer behavior.
2. **Retire v1-only / mixed v1-v2 *new acceptance work*.** No dedicated follow-up real-environment v1-only/mixed-history runs are needed for the v0.6.0 acceptance closure or subsequent forward-only milestones. Record that #86 did not directly exercise them; do not claim they passed.
3. **No destruction or implicit migration.** This decision does not delete, rewrite, compact, force-migrate or automatically invalidate any existing v1 history. The already implemented dual-format reader is historical shipped functionality, not an obligation to add new legacy scenarios. Removal of reader capability, loss of access to old records, or a breaking persisted-format change needs separate explicit requirements, a migration/deprecation policy and an Issue before coding.
4. **No source-of-truth change.** Raw Vault preserved evidence remains the preservation truth; canonical SQLite remains the query/export/Harness system of record. R1 capture, R2 conversation ingest and offline reconstructability for supported forward-v2 evidence are unchanged.
5. **No v1-driven release blockers.** Further v2 work is gated on v2 correctness, source snapshot integrity, canonical semantics and provenance, not retroactive v1-only/mixed live reenactments. Raw Vault encryption-at-rest remains separately deferred.

## Consequences

- Roadmap may mark the **v2 storage/correctness slice** complete after v0.6.0, while avoiding the false statement that all historical compatibility test paths were executed.
- Future non-destructive verification may inspect retained history under the existing `vault verify` contract, but this is not a mandate for dedicated new v1 regression programs.
- The next product order is M2 semantic completeness, followed by M3b canonical FTS/search and the remaining M4 Harness workflows; delivered M3a and Collection sync are not to be rebuilt.
- No product code, CLI contract, persisted schema or historical evidence is changed by this ADR.

## Verification / governance

Implementation requires its own Issue-first PR. The documentation-only PR for #94 records the policy and shipped status; it does not assert a v1 reader deletion, automatic conversion or new compatibility guarantee.

References: [v0.6.0 Stable](https://github.com/lybym/WeArchive/releases/tag/v0.6.0), [#86 acceptance evidence](https://github.com/lybym/WeArchive/issues/86#issuecomment-6159259365), [#93](https://github.com/lybym/WeArchive/pull/93), [ADR 0011](0011-raw-vault-v2-content-addressed-storage.md).
