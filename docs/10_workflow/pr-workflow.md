# PR Workflow

PRs must be public-safe, reviewable, and validated.

## PR Body

Every PR should include:

- summary;
- changes;
- validation;
- sanitization review;
- issue or tracking reference when available;
- breaking changes if any.

Do not paste task prompts, private planning notes, private links, logs, transcripts, or source locations into PR descriptions.

## Agent Behavior

Agents may open and update PRs when authorized. Agents must not merge PRs.

Agents must not mutate repository settings, labels, milestones, Projects, branch protection, or secrets unless the task explicitly authorizes that metadata operation.

## Validation

Default validation for repository changes:

```bash
npm run check
```

If the generated action bundle is changed, also run:

```bash
npm run dist:check
```

For docs-only changes, run the available formatting checks and inspect the rendered Markdown in the diff.

## Sanitization Review

Before opening or updating a PR, inspect the diff for:

- private issue numbers or private PR links;
- private workflow logs, prompts, transcripts, or diffs;
- secret names, credential values, private endpoints, or private environment values;
- private repository-specific paths or source locations;
- claims that require private context to understand.

The PR body should list checked categories, not private source locations.
