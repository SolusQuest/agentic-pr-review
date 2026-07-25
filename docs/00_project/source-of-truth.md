# Source Of Truth

This repository uses a lightweight source-of-truth model.

## Durable Knowledge

- Current user-facing behavior lives in `README.md`.
- Long-lived project rules and architecture live under `docs/`.
- Agent entrypoints are thin and route to durable docs.
- GitHub issues track specific work.
- Pull requests record implementation history and validation.

## Non-Durable Inputs

These are useful while working but are not durable source of truth:

- chat transcripts;
- local scratch notes;
- task prompts;
- CI logs after they expire;
- unpublished design discussion.

If a conclusion matters after a task is complete, move it into an issue, doc, or PR description.

## Current Implementation And Selected Target

- `README.md` and current implementation-contract documents describe behavior that exists on the development head.
- `docs/20_architecture/agent-runtime-rebaseline.md`, `docs/20_architecture/architecture.md`, and `docs/90_roadmap/roadmap-seed.md` govern new runtime design and migration sequencing after the breaking reset is activated.
- Historical roadmap documents remain evidence only when marked superseded.
- Until migration removes a current surface, its implementation contract remains authoritative for operating or validating that surface. It does not override the selected target for new design work.

When the current implementation and selected target differ, state which one is being discussed rather than merging them into a fictional intermediate architecture.

## GitHub Project And Metadata

Repository metadata such as labels, milestones, and Projects can help organize work, but they are not enough for execution-critical context. An agent should be able to understand the task from the issue body, linked docs, and PR description.

Agents must not mutate repository metadata unless the task explicitly authorizes that operation.
