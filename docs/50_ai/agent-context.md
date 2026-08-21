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

R1 is complete: the legacy mixed Action, TypeScript coordinator, and Claude Code CLI execution path are gone. R4 now includes a nested generated Node 24 wrapper for repository-controlled prepared-payload proof, but it is not a supported downstream Action before R7. W3-W15 removed the superseded TypeScript invocation, state, publisher, protocol, prefix, provider-metadata, canonicalization, and root shared-module families after their C# replacements and assertion dispositions were pinned. The direct-runtime schemas and fixtures remain live C# contracts, and the S2 artifact-provenance vectors remain the sole retained negative migration evidence. R4-W14 retired the TypeScript canonical-json family; C# Canonical remains current and the prefix corpus remains immutable evidence. Read [`r4-migration-cutover-handoff.md`](../20_architecture/r4-migration-cutover-handoff.md) for the closed inventory and exact-tree E1 gate, and `r1-legacy-removal-handoff.md` before changing historical deletion evidence.

`README.md` and current-position documents describe the live repository boundary. Older contracts that mention deleted surfaces are historical or migration evidence unless the R1 handoff assigns them a retained current consumer; they do not by themselves describe current implementation behavior. Historical roadmap files do not override the R0-R7 sequence.

## Default Validation

For code and docs changes:

```bash
npm run check
```

Packaging, workflow, README, and distribution changes also run:

```bash
npm run dist:check
```

This command verifies the nested R4 metadata, generated bundle reproducibility and inventory, package ownership, and retired-surface drift.
