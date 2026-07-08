# PR Publishing

Use this procedure when preparing a PR in this repository.

## Before Opening A PR

1. Confirm the diff contains only intended changes.
2. Run required validation.
3. Perform a public-safety scan.
4. Write a self-contained PR body.

## Public-Safety Scan

Review the diff and PR body for:

- private issue numbers or links;
- private PR links;
- private workflow logs;
- prompts or transcripts;
- private diffs;
- secrets or credential values;
- private endpoint or environment values;
- private source paths or source locations.

The PR body should list checked categories, not private source locations.

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
- Keep the PR body public-safe and technically self-contained.
- Link public issues when available.
- Do not mutate labels, milestones, Projects, repository settings, branch protection, or secrets unless explicitly authorized.
