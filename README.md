# agentic-pr-review

`agentic-pr-review` is a GitHub-first, stateful code review agent and
deterministic publishing runtime.

The selected [CR1/CR2 direction](docs/90_roadmap/cr1-cr2-plan.md) moves generic agent execution to SolusAgent and distribution to a framework-dependent managed .NET 10 DLL, then consolidates product behavior in the .NET CLI behind a thin composite Action. The model proposes findings; deterministic APR Host code validates and performs every GitHub side effect. The development head now uses SolusAgent and the managed publish tree; CR2 CLI/Action consolidation remains pending.

This repository is experimental public tooling. The pre-1.0 line does not
provide a stable public API.

## Development Head Status

The current nested Node 24 Action implements ordinary exact release-map resolution and the verified managed-tree launcher, with seven raw inputs and fourteen accounting outputs. It invokes the explicit C# `run` command; C# owns input parsing and defaults, while the current H2 result remains until its CR2 replacement. Its checked payload map is explicitly null: no payload has been authorized, so the entry fails clearly before network access or launch. Private prepared-proof bundles remain separate repository capabilities. No experimental release or supported downstream installation is available yet; the [R7 plan](docs/90_roadmap/r7-plan.md) retains separate binding, qualification and publication gates. Formal public/default graduation requires a later maintainer decision. See [distribution](docs/20_architecture/distribution.md) and [credential-free generated Action fixtures](tests/distribution/README.md).

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
git submodule update --init --recursive
npm ci
npm run check
npm run dist:check
npm run runtime:integration
```

`npm run dist:check` validates the exact seven-input/fourteen-output metadata, Action map/source inventory, package and lockfile ownership, generated-wrapper input and external-import inventory, byte-for-byte bundle reproducibility, and retired root-alias/local-runner/workflow-invocation drift. The command is read-only; use `npm run build:action` to regenerate the checked bundle intentionally.

`npm run runtime:integration` publishes the framework-dependent C# runtime
outside the repository workspace and exercises the retained Input/Result/Trace
CLI boundary directly. This deterministic CLI smoke does not establish Solus review qualification; actual managed archive qualification runs on the trusted Ubuntu 24.04 x64 builder. Wrapper lifecycle and privacy behavior is covered separately
by the ActionHost framework verifier. The complete runtime validation command is:

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
- CR1 has migrated generic execution to SolusAgent and retired the old generic loop, provider, SESSION and production AOT paths. The [exact-source acceptance record](docs/90_roadmap/cr1-cr2-plan.md#final-cr1-technical-acceptance) binds the final production tree to actual installed-package, archive and Action evidence; it is not release or live-quality approval.
- CR2 makes the .NET CLI the sole product entry and simplifies the Action, with explicit artifact-bridge disposition.
- Remaining R7 first-candidate preparation, qualification, publication and adoption follow CR1/CR2 and affected contract refinement. R7 targets public versioned experimental distribution, exact automatic payload selection and bounded public/private/test adoption. Formal public/default graduation remains deferred.

R1-R6 are implementation/evidence history. [CR1/CR2](docs/90_roadmap/cr1-cr2-plan.md) governs the changed target and sequence; completed evidence and R7 issue identities remain. The [R7 plan](docs/90_roadmap/r7-plan.md) records the original approved budgets, trusted configuration and two-tag release identity. Implemented accounting and ordinary resolution do not grant release authority. The Action map remains unbound; no installation command or release version is available.

## Project Documentation

- [`docs/00_project/project-context.md`](docs/00_project/project-context.md)
  defines the product role and source-of-truth model.
- [`docs/90_roadmap/cr1-cr2-plan.md`](docs/90_roadmap/cr1-cr2-plan.md) owns the selected migration direction, acceptance boundaries and remaining R7 sequence.
- [`docs/20_architecture/agent-runtime-rebaseline.md`](docs/20_architecture/agent-runtime-rebaseline.md) records the earlier R-series architecture and surviving implementation contracts.
- [`docs/20_architecture/security-boundary.md`](docs/20_architecture/security-boundary.md)
  defines credential, tool, state, and side-effect boundaries.
- [`docs/20_architecture/distribution.md`](docs/20_architecture/distribution.md)
  defines the target distribution model and transitional state.
- [`docs/20_architecture/r4-migration-cutover-handoff.md`](docs/20_architecture/r4-migration-cutover-handoff.md)
  records the closed R4 migration inventory and the exact-tree E1 handoff gate.
- [`docs/90_roadmap/roadmap-seed.md`](docs/90_roadmap/roadmap-seed.md) defines
  the historical R0-R7 sequence and current CR1/CR2 dependency path.
- [`docs/90_roadmap/r7-plan.md`](docs/90_roadmap/r7-plan.md) defines the approved experimental distribution and adoption contract, with current implementation distinguished from its targets.
- [`docs/50_ai/agent-context.md`](docs/50_ai/agent-context.md) supplies APR's agent context.
- [Solus Book adoption](docs/50_ai/handbook-adoption.md) records the pinned shared handbook, initialization, skill routing, and retained APR requirements. Common engineering standards and skills live in the `docs/shared/` Git submodule; APR owns its product documentation.

Historical M1-M4 contract documents remain implementation and migration evidence.
They do not describe current source ownership, re-enable the retired legacy Action
surface, or override the R0-R7 roadmap and R4 cutover handoff.
