# WeArchive Development Governance

## 1. Rule: docs lead code

All product and implementation work must start from documentation under `docs/`.

The repository follows this precedence:

```text
PRD
  ↓
Architecture / Data Model / ADR
  ↓
Roadmap milestone
  ↓
Issue / task
  ↓
Code + tests
```

Code is not the specification.

## 2. Definition of ready

A development task is ready only when:

- the user-facing or system behavior is described in `docs/PRD.md`, or the task is an implementation of an existing documented requirement;
- architecture impact is understood;
- persistent model changes are reflected in `docs/DATA_MODEL.md`;
- the task belongs to a Roadmap milestone;
- acceptance criteria are explicit.

## 3. Pull request requirements

Every non-trivial PR should answer:

1. Which PRD requirement or Roadmap item does this implement?
2. Does it change architecture?
3. Does it change persisted data/schema?
4. Does it change CLI/user-visible behavior?
5. What tests prove the acceptance criteria?
6. What diagnostics/failure modes were added or changed?

If behavior changes, docs must change in the same PR.

## 4. Architecture Decision Records

Use ADRs for decisions that are expensive to reverse or affect multiple modules.

Path:

```text
docs/adr/NNNN-short-title.md
```

Template:

```markdown
# ADR NNNN — Title

Status: proposed | accepted | superseded | rejected
Date: YYYY-MM-DD

## Context

## Decision

## Alternatives considered

## Consequences

## References
```

## 5. Branching and change scope

Prefer small, milestone-aligned changes.

Suggested branch names:

```text
feat/m0-archive-store
feat/m0-fixture-adapter
feat/m1-source-discovery
fix/import-idempotency
docs/update-data-model
```

Avoid mixing unrelated refactors with product changes.

## 6. Testing policy

Testing layers:

### Unit tests

For pure parsing, normalization, identity and archive functions.

### Fixture integration tests

For complete import flows without requiring a live upstream client.

### Adapter compatibility tests

For source-specific behavior. These should be isolated and skipped gracefully when the required local environment is unavailable.

### Migration tests

Every archive schema migration must be tested from the previous supported schema version.

### Export snapshot tests

JSON/Markdown/HTML exporters should have deterministic fixtures or snapshots.

## 7. Fixture strategy

The repository should use synthetic or sanitized fixtures.

Do not commit:

- personal conversation content
- real local archives
- raw private client datasets
- secrets or credentials
- machine-specific private paths when avoidable

Fixtures should model edge cases deliberately:

- duplicate display names
- group/private conversation differences
- repeated identical messages
- missing sender metadata
- unsupported message type
- missing attachment
- out-of-order source records
- multi-partition timeline

## 8. Logging and diagnostics

Do not treat logging as a substitute for structured diagnostics.

Import flows should produce:

- progress counters
- structured warning/error codes
- source/adapter version metadata
- completeness indicators
- final summary

Sensitive message content should not be logged by default.

## 9. Source adapter boundary

Adapters are allowed to know about upstream formats and versions.

Generic layers are not.

The following must not depend on source-specific table names, file names or message-type codes:

- archive store
- search
- exporters
- core domain models
- high-level CLI commands

## 10. Persistence policy

Any persistent schema change requires:

1. update to `docs/DATA_MODEL.md`
2. migration
3. migration test
4. compatibility notes if existing archives are affected

## 11. CLI compatibility

Until 1.0, CLI commands may evolve, but breaking changes should be documented.

After 1.0, breaking CLI or archive-schema changes require an explicit migration/deprecation plan.

## 12. Dependency policy

Prefer a small dependency surface.

Add a dependency only when it materially improves correctness, maintainability or portability.

For each substantial dependency, record:

- purpose
- why the standard library is insufficient
- platform implications
- licensing implications

## 13. Security/privacy review

A PR needs explicit privacy review if it:

- adds network access
- sends data outside the local machine
- changes secret/config storage
- adds telemetry
- changes archive/export default locations

Default posture remains local-only and offline-capable.

## 14. Issue template for implementation work

Recommended issue body:

```markdown
## Requirement
PRD: FR-XX
Roadmap: MX

## Goal

## Scope

## Non-goals

## Architecture impact

## Data model impact

## Acceptance criteria
- [ ] ...

## Tests

## Documentation changes
```

## 15. Completion rule

A feature is not complete when the code merely runs.

It is complete when:

- acceptance criteria pass
- tests cover the behavior
- diagnostics are adequate
- docs match behavior
- migration/provenance implications are handled
- the implementation remains within the documented architectural boundaries
