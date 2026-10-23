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

The distribution check is read-only. It verifies nested composite and internal Node 24 preparation/launch/post metadata, the exact release-map/source inventory, package and lockfile ownership, generated bundle reproducibility, and retired-surface drift. Use `npm run build:action` only when intentionally regenerating the checked bundle.

The pinned handbook has its own formatting conventions and source history. APR does not reformat or edit it during ordinary project work. Validate changed APR references, the selected source identity, and the shared resources affected by an adoption update.

The [CR1/CR2 direction](../90_roadmap/cr1-cr2-plan.md) changes future qualification requirements, not the checks for current code. Keep existing AOT/Node gates until their owners replace or retire those surfaces with evidence. Documentation checks and historical AOT proof do not establish managed publish-tree loading, Solus-backed execution or practical review capacity.

## Runtime And Workflow Changes

For macOS local development, use [native focused development tests](native-development-tests.md) for repeatable Node/.NET selections, temporary-path handling, runner counts and explicit Linux-only boundaries. These commands add local feedback; complete hosted Linux qualification remains required.

For affected direct-runtime integration, run `npm run runtime:integration`. The complete runtime command is `bash runtime/scripts/verify-runtime.sh all`. Follow [local runtime validation](local-runtime-validation.md) and [parallel runtime CI](runtime-ci-parallel-validation.md) for the actual platform and qualification requirements. A source or documentation check does not establish those runtime proofs.

The current `all` route runs test, framework and actual managed archive
qualification. `managed` aliases in the owning Agent, ActionHost, live-agent
verification commands invoke the same complete archive qualification. The
trusted-proof-v2 `managed` route first executes the actual V2 FDD
production/verifier and two-root preparation proof, then qualifies the archive.
Its default `framework` route runs that complete credential-free V2 proof. They require the reviewed-source Ubuntu 24.04 x64 builder and an
already verified package cache. No command automatically obtains credentials.
Ordinary PR/push CI calls explicit credential-free test/framework and
`deterministic` routes; the complete trusted `verify` workflow qualifies the
private Solus archive. A public FDD regression receipt cannot stand in for that
result. See [managed archive qualification](../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/README.md)
for the isolation, independent builds and bounded receipts.

Follow [APR repository policy](repository-policy.md) for CI, synthetic fixtures, live-provider authorization, and event security restrictions. The shared validation standard owns document-consumer checks and evidence reporting.
