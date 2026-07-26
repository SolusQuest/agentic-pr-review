# PR Workflow

PRs must be reviewable and validated.

## PR Body

Every PR should include:

- summary;
- changes;
- validation;
- issue or tracking reference when available;
- breaking changes if any.

Keep PR descriptions self-contained. Do not paste raw task prompts, raw logs, transcripts, credentials, or secrets.

## Agent Behavior

Agents may open and update PRs when authorized. Agents must not merge PRs.

Agents must not mutate repository settings, labels, milestones, Projects, branch protection, or secrets unless the task explicitly authorizes that metadata operation.

## Validation

Default validation for repository changes:

```bash
npm run check
```

During the R1-R3 no-public-Action interval, packaging, workflow, README, and
distribution changes also run:

```bash
npm run dist:check
```

This command currently verifies that the retired Action surface remains absent.
R4 restores generated-wrapper reproducibility semantics.

For docs-only changes, run the available formatting checks and inspect the rendered Markdown in the diff.
