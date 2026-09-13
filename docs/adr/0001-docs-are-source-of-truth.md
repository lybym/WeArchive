# ADR 0001 — Documentation is the source of truth

Status: accepted  
Date: 2026-09-13

## Context

WeArchive will evolve across multiple milestones and must adapt to upstream client changes. If implementation becomes the implicit specification, product behavior and architectural boundaries will drift quickly, especially when development is delegated to coding agents.

## Decision

Documentation under `docs/` is the authoritative specification for product behavior and architecture.

The precedence is:

```text
PRD
  ↓
Architecture / Data Model / ADR
  ↓
Roadmap
  ↓
Issue
  ↓
Code and tests
```

When implementation conflicts with current documentation during early development, documentation takes precedence until the discrepancy is explicitly resolved.

Any code change that intentionally changes product behavior or architecture must update the corresponding documentation in the same pull request.

## Alternatives considered

### Code as specification

Rejected because upstream-specific implementation details would quickly define product behavior accidentally.

### Issue-driven specification only

Rejected because issues are task-oriented and do not provide a stable system-level view.

### Wiki/external documentation

Rejected for now because documentation should version together with the codebase and be reviewable in the same pull requests.

## Consequences

Positive:

- Coding agents have a clear design baseline.
- Architecture drift becomes easier to detect.
- Product decisions remain reviewable over time.
- Issues can reference stable requirements.

Costs:

- Documentation changes are required before or alongside implementation changes.
- Some experiments may need ADRs before becoming production architecture.

## References

- `docs/PRD.md`
- `docs/ARCHITECTURE.md`
- `docs/DATA_MODEL.md`
- `docs/ROADMAP.md`
- `docs/DEVELOPMENT.md`
