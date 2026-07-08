# Source Of Truth

This repository uses a lightweight source-of-truth model.

## Durable Knowledge

- Public user-facing behavior lives in `README.md`.
- Long-lived project rules and architecture live under `docs/`.
- Agent entrypoints are thin and route to durable docs.
- GitHub issues track specific work.
- Pull requests record implementation history and validation.

## Non-Durable Inputs

These are useful while working but are not durable source of truth:

- chat transcripts;
- local scratch notes;
- task prompts;
- private planning context;
- CI logs after they expire;
- unpublished design discussion.

If a conclusion matters after a task is complete, move it into a public issue, public doc, or PR description.

## GitHub Project And Metadata

Repository metadata such as labels, milestones, and Projects can help organize work, but they are not enough for execution-critical context. An agent should be able to understand the task from the issue body, linked public docs, and PR description.

Agents must not mutate repository metadata unless the task explicitly authorizes that operation.
