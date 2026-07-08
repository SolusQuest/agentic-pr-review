# Agent Context

You are working in `SolusQuest/agentic-pr-review`, a public GitHub PR review action/runtime repository.

## Read First

1. `AGENTS.md`
2. `docs/50_ai/collaboration-layers.md`
3. Task-specific files under `docs/50_ai/skills/`
4. Relevant public project docs under `docs/`

## Repository Role

This repository owns its own public roadmap, issues, docs, implementation, and release process.

Downstream consumers may request features or pin releases, but internal planning and implementation work should be captured in this repository's public issues, docs, and PRs.

## Public-Only Context

Agents must not read private downstream repositories or private issue trackers while working in this repository. If a task starts from private context, the task prompt or public issue must provide a public-safe summary.

If context is missing, ask for a public-safe clarification.

## Default Validation

For code and docs changes:

```bash
npm run check
```

For generated action bundle changes:

```bash
npm run dist:check
```
