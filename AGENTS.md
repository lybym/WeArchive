# AGENTS.md

This repository is documentation-led.

Before making any non-trivial code change, read the following documents in order:

1. `docs/PRD.md`
2. `docs/ARCHITECTURE.md`
3. `docs/DATA_MODEL.md`
4. `docs/ROADMAP.md`
5. `docs/DEVELOPMENT.md`
6. relevant files under `docs/adr/`

## Mandatory rules

- Documentation under `docs/` is the source of truth.
- Do not invent product requirements from existing code.
- Do not change architecture implicitly through implementation.
- Every feature must map to a PRD requirement and Roadmap milestone.
- Source/client-specific behavior must remain behind the adapter boundary.
- Export, search and archive layers must depend on normalized domain models, not source-specific structures.
- Persistent schema changes require an update to `docs/DATA_MODEL.md`, a migration and tests.
- Product/architecture changes require documentation changes in the same PR.
- New major architectural choices require an ADR.
- Preserve the local-first and read-only-source principles unless documentation explicitly changes them.
- Do not commit real personal chat data, real archives, secrets or machine-private datasets.

## When docs and code disagree

During early development, treat the current docs as authoritative.

Do not silently alter the implementation direction to preserve old provisional code. Either:

1. align code to docs, or
2. propose and document a deliberate change to the docs/ADR before implementing it.

## Task workflow

For each implementation task:

1. Identify the PRD requirement(s).
2. Identify the Roadmap milestone.
3. Check architecture/data-model implications.
4. Define acceptance criteria.
5. Implement the smallest compliant change.
6. Add/update tests.
7. Update docs when behavior or architecture changes.
8. Verify that diagnostics/provenance are not weakened.

## Current priority

Current priority is M0: Product foundation.

Do not skip directly to a real client-specific implementation before the generic adapter protocol, normalized models, archive persistence, provenance, diagnostics, fixture-driven import and deterministic exports are stable enough to support it.
