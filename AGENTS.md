# Agent Entry

This repository is public. Agents working here must use only this repository, the current task prompt, and public context.

## Startup Reading Order

1. `docs/50_ai/agent-context.md`
2. `docs/50_ai/collaboration-layers.md`
3. Task-specific procedures under `docs/50_ai/skills/`
4. Relevant project, workflow, architecture, or roadmap docs under `docs/`

## Context Boundary

- Do not read or depend on private repositories, private issue trackers, private workflow logs, private prompts, transcripts, credentials, or secrets.
- If required information is missing, ask for a public-safe clarification instead of inferring from private context.
- Do not include private issue links, private repository details, private file paths, prompts, transcripts, workflow logs, credentials, secrets, or private-only rationale in files, commits, PR bodies, comments, CI logs, or artifacts.
- PR bodies must be technically self-contained and understandable without private context.

## Safety Rules

- Do not merge PRs.
- Do not mutate repository settings, labels, milestones, Projects, branch protection, or secrets unless a task explicitly authorizes that metadata operation.
- `pull_request` and `push` CI must run without provider secrets.
- Do not use `pull_request_target` without explicit security review.
- Use synthetic fixtures or test-only modes unless a task explicitly defines live provider validation.

## Validation

Run the relevant checks documented in `CLAUDE.md` and task-specific docs. For code changes, the default local validation is:

```bash
npm run check
```
