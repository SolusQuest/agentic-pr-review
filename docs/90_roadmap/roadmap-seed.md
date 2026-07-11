# Roadmap

This roadmap records the current direction for `agentic-pr-review`. It replaces the earlier
seed-only roadmap with a re-baselined view of what already exists, what the project is optimizing for,
and which near-term milestones are ready to track in issues.

## Long-Term Goal

`agentic-pr-review` should become a GitHub-first, stateful, policy-driven PR review runtime that
produces structured, low-noise, replayable code review findings through a deterministic, safe
publishing pipeline while preserving cost-efficient session continuation.

In short:

```text
PR review runtime, not a general coding agent.
```

The project optimizes for:

- low-noise review;
- stateful memory;
- structured output;
- safe publishing;
- replayability;
- eval-driven improvement;
- measurable quality and cache economics;
- GitHub-first practicality;
- a clear future runtime boundary.

The project-owned runtime has three product-level constraints:

1. **Runtime replacement** - the self-developed runtime is the long-term live review path; `claude-code-cli` is the current baseline kept in a compatibility and maintenance role.
2. **Cross-session context recovery** - the runtime resumes review context across separate GitHub Actions runs without depending on Claude Code's session mechanism.
3. **Cache-efficient session continuation** - for supported prefix-cache providers, the runtime reconstructs a strict, stable cacheable prefix across resumed sessions and demonstrates measurable cache effectiveness and normalized input cost. Stable construction is a contract; an individual provider-reported cache hit is an observed outcome.

## Engineering Direction

The project intentionally uses a TypeScript host and a project-owned C# runtime, with Native AOT as the runtime distribution target. This cross-language shape is an explicit engineering objective: it creates a clear process and protocol boundary and develops production-style experience with C# agent runtimes, deterministic serialization, provider orchestration, and verifiable binary distribution.

The project accepts the additional build, versioning, and release complexity. C# and Native AOT remain architecture commitments rather than product success criteria; quality, safety, resumability, cache economics, and reliability still determine whether the runtime path graduates.

## Non-Goals

The project is not trying to become:

- a general coding agent;
- a replacement for IDE coding agents or autonomous code editing systems;
- an arbitrary shell or code-writing agent;
- a system that lets models own GitHub write tools;
- a hosted SaaS, billing platform, or dashboard-first product in the near term;
- a multi-platform review framework before GitHub PR review is strong.

The runtime core must not receive `GITHUB_TOKEN`, call GitHub write APIs, or directly mutate platform
state. Deterministic host or publisher code owns side effects.

## Current Baseline

The current implementation is a TypeScript GitHub Action with:

- structured model output validation;
- sticky PR summary publishing;
- optional inline comments with line mapping and duplicate suppression;
- GitHub-native state artifacts and PR diff snapshots;
- deterministic test fixtures;
- a live `claude-code-cli` provider;
- token and artifact safety gates;
- local synthetic validation.

The TypeScript-to-runtime boundary is now established. The `ReviewInputV1` / `ReviewResultV1` /
`ReviewTraceV1` protocol, a deterministic C# runtime CLI, and recurring framework-dependent and
`linux-x64` Native AOT CI coverage all exist. The next architectural work is TypeScript-side
runtime invocation (Phase 3) and a cache-efficient runtime provider foundation (Phase 4).

## Target Architecture

Selected long-term shape:

```text
GitHub Action first; future GitHub App only if productization needs justify it
  -> TypeScript host / adapter / publisher
  -> schema-first JSON protocol
  -> C# review runtime, distributed as Native AOT once behavior and compatibility are proven
  -> provider and repo-local tool orchestration
  -> structured findings, sanitized traces, fixtures, and replay
```

TypeScript remains responsible for:

- GitHub Action inputs and outputs;
- GitHub event parsing;
- GitHub API reads and writes;
- sticky and inline publishing;
- artifact upload;
- runtime binary resolution and invocation;
- final side-effect safety checks.

The project-owned C# runtime core is responsible for:

- reading sanitized review input;
- validating protocol version;
- packing review context;
- running deterministic or live providers through project-owned interfaces;
- orchestrating bounded repo-local read, grep, glob, and patch-aware tools when enabled;
- proposing structured findings;
- emitting sanitized usage and trace metadata by default;
- writing structured review output.

Restricted raw diagnostics remain explicit trusted debug behavior, not normal runtime output.

## Phased Roadmap

### Phase 0: Public Planning And Validation Baseline

Goal: make docs and local checks reflect the current project state.

Done when:

- `npm run check` passes on CI for `main`.
- `npm run check` is documented as the default local validation command.
- roadmap docs describe current capabilities without presenting completed work as future work.
- near-term milestone and issue plans are self-contained.

### Phase 1: Runtime Protocol Contract

Goal: prove the TypeScript-to-runtime boundary without replacing the current action path.

Done when:

- `ReviewInputV1`, `ReviewResultV1`, and minimal `ReviewTraceV1` are defined by schema files or
  equivalent strict contract definitions.
- `ReviewInputV1` includes sanitized target metadata, changed file and bounded patch context,
  policy/context inputs, runtime options, minimal previous-state summary, and minimal existing
  comment or duplicate-evidence summary when available.
- `ReviewResultV1` includes summary, findings, optional publish hints, usage, warnings, diagnostics,
  and an explicit mapping into current structured review handling.
- `ReviewTraceV1` contains sanitized trace metadata only.
- contract fixtures cover bootstrap, incremental, skipped/empty, invalid output, incompatible
  protocol version, path safety, and privacy cases.
- TypeScript can construct sanitized `review-input.json` fixtures from existing action state and
  validate `review-result.json` fixtures.
- GitHub credentials, absolute paths, `..` paths, protocol-looking paths, and write-only platform
  metadata are rejected or excluded.
- incompatible protocol versions fail closed.

### Phase 2: Deterministic C# Runtime CLI

Goal: introduce a standalone deterministic C# runtime CLI behind the file protocol and prove early Native AOT feasibility.

Done when:

- a runtime CLI supports `review --input <path> --output <path> --trace <path>`;
- the CLI reads `review-input.json`, validates protocol version, and writes `review-result.json`;
- invalid input produces no partial successful result;
- documented exit codes distinguish success, contract errors, runtime errors, and provider errors;
- deterministic provider behavior is stable across repeated runs and supports CI;
- trace output is sanitized by default and contains no raw provider bodies or secrets.
- the selected dependencies and CLI entrypoint can publish and run as an initial `linux-x64` Native AOT feasibility artifact without requiring production release packaging.

### Phase 3: TypeScript Runtime Integration

Goal: let the action invoke the runtime behind a guarded path while keeping publishing deterministic.

Done when:

- TypeScript writes runtime input, invokes the runtime, validates output, and renders the sticky
  summary from the result.
- the existing TypeScript action path remains the default until the runtime path has fixture and CI
  coverage equivalent to the current path.
- the runtime path is guarded behind an explicit input or test-only mode.
- no GitHub write behavior changes in the first integration PR.
- inline publishing remains disabled or sticky-only for the new runtime path until line mapping and
  duplicate suppression contracts are covered by fixtures.
- failure modes fail closed without publishing invalid findings or uploading unsafe artifacts.
- generated action bundle changes, if any, are validated with `npm run dist:check`.

### Phase 4: Cache-Efficient Runtime Provider Foundation

Goal: establish cache-efficient cross-session live-provider execution behind project-owned C# runtime interfaces.

Done when:

- the session ledger and provider request prefix design is completed as the first Phase 4 gate;
- a schema-versioned ledger and canonical prefix materializer have contract fixtures;
- restored sessions reproduce the expected `prefixSha256` for the same versioned inputs;
- the ledger is restored from and persisted to an approved durable state artifact across separate workflow runs;
- missing, incompatible, corrupt, or unsafe ledger state falls back to bootstrap behavior without unsafe reuse;
- the runtime provider interface supports deterministic and live providers.
- live provider execution is available only in explicit trusted modes.
- cache-read, cache-write, uncached-input, output usage, timeouts, malformed output, and provider failures are normalized when the provider exposes them;
- representative resumed-session evaluations compare normalized input cost with a documented stateless baseline;
- provider secrets stay out of files, logs, normal artifacts, traces, and structured outputs.
- TypeScript still preserves fail-closed enforcement for budgets and publishing where needed.
- `claude-code-cli` remains a compatibility and maintenance path while the project-owned live provider path is established as the intended long-term default.

### Phase 5: Stateful Memory And Safe Publisher Contracts

Goal: formalize long-lived state and publisher decisions as contracts.

Done when:

- state contracts cover sticky metadata, state artifact manifest, previous review snapshot, lineage,
  and finding fingerprints.
- publisher contracts cover publish plan/result, inline eligibility, sticky fallback, duplicate
  suppression, failed inline target behavior, and comment URL metadata.
- corrupt sticky metadata falls back safely.
- missing or incompatible state triggers bootstrap behavior.
- duplicate inline candidates are suppressed.
- non-commentable lines remain sticky-only.
- publisher code never trusts runtime-provided GitHub metadata without validation.

- long-lived ledger compatibility, invalidation, and schema migration behavior are defined;
- cache effectiveness and normalized input cost are measurable without making a single provider cache miss a publisher failure.

### Phase 6: Evaluation, Replay, And Shadow Validation

Goal: make review quality, cache economics, and runtime graduation evidence measurable and regressible.

Done when:

- fixture categories exist for must-find, must-not-find, safety gates, stateful review, publisher
  behavior, provider failures, and malformed structured output.
- at least one fixture exists for each required category.
- eval exits non-zero on regression and produces deterministic CI-friendly output.
- replay can consume a versioned replay bundle or manifest without GitHub credentials or live GitHub state; the bundle contains or content-addresses sanitized review input, runtime/provider identity, deterministic provider and tool fixture material, trace evidence, and actual or expected result data.
- reports distinguish quality failures from infrastructure failures.
- reports include true-positive, false-positive, duplicate, line mapping, incremental correctness,
  token usage, cache-read ratio, normalized input cost, latency, and reproducibility signals.
- a no-publish shadow mode can compare the project-owned runtime with the maintained baseline on the same sanitized review inputs.
- documented promotion thresholds distinguish isolated provider cache misses from sustained prefix instability or cost regression.

### Phase 7: Runtime Distribution And Live Graduation

Goal: ship the runtime as a pinned, verifiable release artifact after the runtime has stable protocol
and useful provider behavior, then make an evidence-based live-provider transition decision.

Done when:

- Native AOT release assets exist for an initial documented platform, likely `linux-x64`.
- release assets include checksums.
- checksum verification failure fails closed.
- the action maps default runtime version to action version and never downloads implicit `latest`.
- `runtime_path` bypasses download and checksum but still validates runtime and protocol version.
- release docs describe action/runtime compatibility.
- quality, safety, cache economics, and operational thresholds for preferring the project-owned live path are satisfied.
- the `claude-code-cli` compatibility, fallback, migration, and possible deprecation policy is decided from observed runtime evidence rather than calendar dates.

### Phase 8: Optional Language Intelligence

Goal: add deeper review capability only after core runtime quality is proven.

Candidate later work:

- optional C#/.NET syntax helper tools;
- analyzer diagnostics and project graph helpers;
- public API shape analysis;
- source generator or nullable annotation helpers.

This remains optional and must not become a required dependency of the default Native AOT runtime.

### Phase 9: Productization Options

Goal: consider broader product surfaces only after GitHub-first review quality, eval/replay,
publisher safety, and runtime invocation/distribution are stable.

Candidate later work:

- optional GitHub App token mode;
- hosted GitHub App;
- org-level policy;
- cross-repo analytics;
- GitLab or Azure DevOps adapters.

## Near-Term Milestones

Create GitHub milestones for executable near-term work only:

1. [`M0: Validation and roadmap baseline`](https://github.com/SolusQuest/agentic-pr-review/milestone/1)
2. [`M1: Runtime protocol contract`](https://github.com/SolusQuest/agentic-pr-review/milestone/3)
3. [`M2: Deterministic C# runtime CLI`](https://github.com/SolusQuest/agentic-pr-review/milestone/2)
4. [`M3: TypeScript runtime integration`](https://github.com/SolusQuest/agentic-pr-review/milestone/5)
5. [`M4: Cache-efficient runtime provider foundation`](https://github.com/SolusQuest/agentic-pr-review/milestone/4)

Keep M5 and M6 as roadmap-only phases until M3 integration and the M4 ledger/prefix design make their issue boundaries stable.

The initial issue plan is maintained in
[`initial-issue-plan.md`](./initial-issue-plan.md).

M3-M6 sequencing and refinement-ready issue outlines are maintained in
[`m3-m6-plan.md`](./m3-m6-plan.md).

## Post-M2 Planning Status

The following issues are not part of the initial M0-M2 issue plan.

### M4 Foundation: Cache-efficient session ledger and provider request prefix contract

Issue [#29](https://github.com/SolusQuest/agentic-pr-review/issues/29) defines the first Phase 4 design gate: a canonical session ledger, stable provider request prefix, normalized cache usage, and cost non-regression model for resumed sessions.

It does not block M2 or M3, but it blocks project-owned live provider implementation. Its design deliverable must create or identify the implementation issues needed to complete M4.

### Post-M4 Candidate: Runtime context and tool orchestration

Full repo-local read, grep, glob, patch-aware access, bounded tool-result handling, and tool-loop policy do not block M4. They form a separate future roadmap track after the provider/session foundation is stable.

This candidate must define tool contracts, stable/dynamic prefix placement, budgets, fixtures, and security boundaries before receiving a milestone or implementation issues.

### Deferred Phase 7 Candidate: Runtime replacement and Claude Code compatibility policy

Issue [#30](https://github.com/SolusQuest/agentic-pr-review/issues/30) is deferred until the project-owned live provider has Phase 6 quality, cache economics, safety, and operational evidence.

The issue should remain closed and unmilestoned while deferred. Reopen it for Phase 7 live graduation rather than assigning it to M4.
