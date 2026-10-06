# APR Validation

Use the shared [validation standard](../shared/standards/validation.md) and [test-validation skill](../shared/skills/test-validation/SKILL.md) for applicable inputs, process handling, and honest evidence. APR owns the commands and qualification requirements below.

## Default Checks

Install the checked dependencies with `npm ci`. For code and docs changes, run:

```bash
npm run check
```

This runs formatting, TypeScript checking, and the source tests. Packaging, workflow, README, and distribution changes also run:

```bash
npm run dist:check
```

The distribution check is read-only. It verifies nested Node 24 Action metadata, the exact release-map/source inventory, package and lockfile ownership, generated bundle reproducibility, and retired-surface drift. Use `npm run build:action` only when intentionally regenerating the checked bundle.

The pinned handbook has its own formatting conventions and source history. APR does not reformat or edit it during ordinary project work. Validate changed APR references, the selected source identity, and the shared resources affected by an adoption update.

## Runtime And Workflow Changes

For affected direct-runtime integration, run `npm run runtime:integration`. The complete runtime command is `bash runtime/scripts/verify-runtime.sh all`. Follow [local runtime validation](local-runtime-validation.md) and [parallel runtime CI](runtime-ci-parallel-validation.md) for the actual platform and qualification requirements. A source or documentation check does not establish those runtime proofs.

Follow [APR repository policy](repository-policy.md) for CI, synthetic fixtures, live-provider authorization, and event security restrictions. The shared validation standard owns document-consumer checks and evidence reporting.
