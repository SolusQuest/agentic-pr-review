# Agent Entry

This repository is public. Agents work from this repository, the current task prompt, and public context.

## Startup Reading Order

1. `docs/50_ai/agent-context.md`
2. `docs/50_ai/collaboration-layers.md`
3. Task-specific procedures under `docs/50_ai/skills/`
4. Relevant project, workflow, architecture, or roadmap docs under `docs/`

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
