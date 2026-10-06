# Source Of Truth

Use the shared [source-of-truth model](../shared/standards/source-of-truth.md) for durable decisions, current behavior, selected requirements, and historical evidence. APR retains the following owning documents.

## Current Implementation And Selected Target

- `README.md` and current implementation-contract documents describe behavior that exists on the development head.
- `docs/20_architecture/agent-runtime-rebaseline.md`, `docs/20_architecture/architecture.md`, and `docs/90_roadmap/roadmap-seed.md` govern new runtime design and migration sequencing after the breaking reset is activated.
- Historical roadmap documents remain evidence only when marked superseded.
- Until migration removes a current surface, its implementation contract remains authoritative for operating or validating that surface. It does not override the selected target for new design work.

When the current implementation and selected target differ, state which one is being discussed rather than merging them into a fictional intermediate architecture.

For tracker context, use the shared [issue workflow](../shared/standards/issue-workflow.md). [APR repository policy](../10_workflow/repository-policy.md) owns local metadata restrictions.
