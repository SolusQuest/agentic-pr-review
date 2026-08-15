# agentic-pr-review

`agentic-pr-review` is a GitHub-first, stateful code review agent and
deterministic publishing runtime.

The project is being rebuilt around one Native AOT C# application containing a
trusted Host, an in-process review Agent, bounded read-only repository tools, and
provider adapters. The model proposes findings; deterministic Host code validates
and performs every GitHub side effect.

This repository is experimental public tooling. The pre-1.0 line does not
provide a stable public API.

## Development Head Status

The current development head contains the nested R4 Action metadata and generated Node 24 wrapper only for repository-controlled proof with an explicitly prepared payload. It is not a supported downstream Action: there is no release payload selection, automatic download, root Action alias, or stable output surface. R7 owns release assets, checksums, automatic payload resolution, and promotion of a supported public default. See [`r4-actionhost-wrapper-plan.md`](docs/20_architecture/r4-actionhost-wrapper-plan.md).

Do not reference `main` or another moving development-head commit as an Action.
There is no compatibility wrapper or supported downstream bootstrap/reset path during this transition.

The immutable [`v0.1.0`](https://github.com/SolusQuest/agentic-pr-review/tree/v0.1.0)
tag remains the historical, unmaintained legacy implementation. Consumers pinned
to that tag or another old immutable commit continue resolving that historical
tree, but no new legacy snapshot will be published. Persisted legacy or M4 state
has no migration guarantee into the future Agent state.

Legacy agent-specific variables and secrets referenced exclusively by the
removed Claude, DeepSeek-live, and M4 workflows are no longer consumed by the
development head. Their values are not printed or migrated.

## Development Validation

Install dependencies and run the source checks:

```bash
npm ci
npm run check
npm run dist:check
npm run runtime:integration
```

`npm run dist:check` validates the exact seven-input/no-output metadata, package and lockfile ownership, generated-wrapper input and external-import inventory, byte-for-byte bundle reproducibility, and retired root-alias/local-runner/workflow-invocation drift. The command is read-only; use `npm run build:action` to regenerate the checked bundle intentionally.

`npm run runtime:integration` publishes the framework-dependent C# runtime and
test fixture outside the repository workspace, then exercises the direct
source-host/process boundary. On Linux it also publishes and executes the
`linux-x64` Native AOT path. The complete runtime validation command is:

```bash
bash runtime/scripts/verify-runtime.sh all
```

Pull-request and push CI use synthetic fixtures and direct validation without
provider secrets. Live provider validation is trusted and manually gated when a
roadmap phase explicitly introduces it.

## Migration Sequence

- R1 removes the mixed public Action and the Claude Code CLI coordinator.
- R2 proves the minimal C# Agent loop, `read_file`, `search_text`,
  `finish_review`, secure session restore, and the AI-abstraction decision.
- R3 completed the trusted no-publish live-provider route and initial six-tool read-only profile.
- R4 introduces the replacement thin Node wrapper, C# `ActionHost`, public
  Action surface, downstream-owned encrypted artifact state, and integrated two-run proof.
- R7 owns release assets, checksums, exact automatic payload selection, and the
  public default.

Do not infer future Action inputs, outputs, or distribution behavior before the
owning roadmap phase records them.

## Project Documentation

- [`docs/00_project/project-context.md`](docs/00_project/project-context.md)
  defines the product role and source-of-truth model.
- [`docs/20_architecture/agent-runtime-rebaseline.md`](docs/20_architecture/agent-runtime-rebaseline.md)
  records the selected architecture and migration sequence.
- [`docs/20_architecture/security-boundary.md`](docs/20_architecture/security-boundary.md)
  defines credential, tool, state, and side-effect boundaries.
- [`docs/20_architecture/distribution.md`](docs/20_architecture/distribution.md)
  defines the target distribution model and transitional state.
- [`docs/90_roadmap/roadmap-seed.md`](docs/90_roadmap/roadmap-seed.md) defines
  the R0-R7 critical path.
- [`docs/50_ai/agent-context.md`](docs/50_ai/agent-context.md) is the shared
  agent entrypoint.

Historical M1-M4 contract documents remain implementation and migration evidence
for the source still present on the development head. They do not re-enable the
retired legacy Action surface or override the R0-R7 roadmap.
