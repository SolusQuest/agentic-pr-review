# CLAUDE.md

This file provides operating guidance for Claude Code working in `SolusQuest/agentic-pr-review`.

## Operating rules

Read and follow `AGENTS.md` for general agent operating rules, context boundaries, CI rules, PR rules, and issue rules.

## Code conventions

- TypeScript with strict mode.
- ES modules (`"type": "module"` in package.json).
- Use `vitest` for testing.
- Use `prettier` for formatting.

## Text hygiene

Do not wrap commit messages, PR bodies, issue bodies, or comments in decorative `@...@` markers.

## Validation

After making changes, run:

```bash
npm run check
```

This runs format checking, type checking, and tests.

## Safety

- Do not commit secrets, API keys, or credentials.
- Do not use `secrets: inherit` in CI workflows.
- Do not use `pull_request_target` without explicit security review.
- If a task requires provider access, it must be explicitly defined with synthetic fixtures or test-only mode.
