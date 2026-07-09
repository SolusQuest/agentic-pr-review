# PR Publishing

Use this procedure when preparing a PR in this repository.

Follow `docs/10_workflow/pr-workflow.md` for PR body requirements, validation rules, and agent behavior constraints.

## Before Opening A PR

1. Confirm the diff contains only intended changes.
2. Run required validation.
3. Write a self-contained PR body.

## Validation

Default:

```bash
npm run check
```

If action bundle output changes:

```bash
npm run dist:check
```

If validation cannot run, state why in the PR body.

## PR Rules

- Open a PR; do not merge it.
- Keep the PR body technically self-contained.
- Link issues when available.
- Do not mutate labels, milestones, Projects, repository settings, branch protection, or secrets unless explicitly authorized.
