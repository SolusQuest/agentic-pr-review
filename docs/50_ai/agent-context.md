# Agent Context

You are working in `SolusQuest/agentic-pr-review`, a public GitHub PR review action/runtime repository.

## Read First

1. `docs/50_ai/collaboration-layers.md`
2. Task-specific files under `docs/50_ai/skills/`
3. Relevant project docs under `docs/`

## Repository Role

This repository owns its own roadmap, issues, docs, implementation, and release process.

Downstream consumers may request features or pin releases, but internal planning and implementation work should be captured in this repository's issues, docs, and PRs.

## Runtime Planning Baseline

For new runtime design and sequencing, read:

1. `docs/00_project/project-context.md`;
2. `docs/20_architecture/agent-runtime-rebaseline.md`;
3. `docs/20_architecture/security-boundary.md`;
4. `docs/90_roadmap/roadmap-seed.md`.

`README.md` and older contract documents continue to describe current implementation behavior until migration removes those surfaces. Historical roadmap files do not override the R0-R7 sequence.

## Default Validation

For code and docs changes:

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
