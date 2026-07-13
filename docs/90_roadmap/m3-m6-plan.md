# M3-M6 Planning

This document records the next roadmap steps after the runtime protocol and deterministic C# CLI. It gives future refinement enough context without pretending unresolved implementation decisions are agent-ready.

## Planning Policy

- M3 and M4 are active GitHub milestones because their boundaries and dependencies are stable enough to track.
- M3 issues may start as refinement-ready outlines. They must identify open decisions explicitly and must be refined to the repository's agent-ready criteria before implementation.
- M4 starts with the session ledger and prefix contract design in #29. That issue creates or identifies the implementation follow-ups needed to complete M4.
- M5 and M6 remain roadmap-only phases. Do not create their milestones or placeholder issues until M3 integration and the M4 design make their contracts stable.
- #30 remains deferred, closed, and unmilestoned until Phase 6 evidence supports a Phase 7 live-runtime graduation decision.

## M3: TypeScript Runtime Integration

Goal: let the TypeScript action invoke the deterministic C# runtime behind a guarded path while preserving host-owned publishing and fail-closed behavior.

Entry conditions:

- M1 protocol construction and result mapping are complete.
- M2 CLI invocation, protocol validation, exit codes, and deterministic fixtures are stable enough to consume.

Exit conditions:

- the TypeScript host can construct runtime input, invoke the CLI, validate result and trace output, and map the result into current structured review handling;
- the path is explicit and guarded, while the current action path remains the default;
- the first integration path is sticky-only and does not change GitHub write behavior;
- invalid, partial, incompatible, or unsafe runtime output fails closed;
- deterministic end-to-end integration coverage runs in CI without provider secrets.

### [Feature: Add TypeScript runtime invocation adapter](https://github.com/SolusQuest/agentic-pr-review/issues/33)

Refinement status: not agent-ready. The objective and boundaries are stable; process lifecycle and binary-resolution decisions remain open.

Objective: add a host-side adapter that can materialize protocol files, invoke the deterministic C# CLI, and return validated result and trace data without wiring it into normal action execution.

Dependencies: #18, #19, #20, and #21.

In scope:

- isolated adapter/module boundary;
- input, result, and trace file ownership;
- CLI arguments, exit-code mapping, bounded output, and cleanup behavior;
- protocol/version validation before returning data to the action;
- unit or process-level tests against deterministic fixtures.

Out of scope:

- changing the default action path;
- publishing comments;
- live provider execution;
- runtime download or production release resolution.

Open decisions for refinement:

- runtime executable/path resolution for local, CI, and later bundled/downloaded modes;
- timeout, cancellation, and process-output limits;
- temporary directory ownership and cleanup on partial failure;
- whether trace output is mandatory for deterministic integration tests or optional as in the protocol.

Related paths:

- `src/main.ts`
- `src/config.ts`
- `src/protocol/`
- planned `runtime/` C# CLI project
- `docs/20_architecture/runtime-protocol.md`
- `docs/20_architecture/security-boundary.md`

### [Feature: Add guarded C# runtime execution path to the action](https://github.com/SolusQuest/agentic-pr-review/issues/34)

Refinement status: agent-ready; refined in issue #34 before implementation.

Objective: wire the invocation adapter into an explicit guarded action path and map validated runtime results into the current host-owned structured review and sticky publishing pipeline.

Dependencies: #18, #19, #20, #21, and #33.

In scope:

- explicit default-off `runtime_backend=deterministic-csharp` path;
- mapping validated `ReviewResultV1` into current structured review handling;
- host-owned phase, SHA, usage-budget, lineage, fingerprint, and publishing metadata;
- sticky-only behavior for the first integrated runtime path;
- fail-closed handling before any comment or artifact side effect;
- backend-aware state identity, trusted command resolution, deterministic trace policy, and bounded outputs.

Out of scope:

- making the C# runtime the default live path;
- inline publishing for the new runtime path;
- live provider or session ledger support;
- changing the model/runtime ownership of GitHub side effects.

Refined decisions are recorded in issue #34. The first path is public experimental but default-off,
uses `runtime_provider=test`, is sticky-only, and excludes trace artifact upload; #35 owns real
cross-language process fixtures and CI orchestration.

Related paths:

- `src/main.ts`
- `src/config.ts`
- `src/structured.ts`
- `src/state.ts`
- `src/comments.ts`
- `src/inline-comments.ts`
- `.github/actions/agentic-pr-review/action.yml`

### [Task: Add deterministic C# runtime integration fixtures and CI coverage](https://github.com/SolusQuest/agentic-pr-review/issues/35)

Refinement status: not agent-ready. Required scenarios are known; fixture ownership and CI command shape depend on the first two M3 issues.

Objective: prove the TypeScript host and deterministic C# runtime work together across success, compatibility, failure, and privacy paths without provider secrets.

Dependencies: #21, #33, and #34.

In scope:

- a local end-to-end integration command;
- success, no-findings, invalid input, incompatible protocol, non-zero exit, partial output, unsafe path, and sanitized trace scenarios;
- stable assertions over normalized output rather than environment-specific paths or timestamps;
- CI execution without provider credentials;
- documentation for local reproduction.

Out of scope:

- live provider smoke tests;
- comment publishing assertions beyond proving no side effect occurs before validation;
- runtime release downloads;
- cache/session ledger behavior.

Open decisions for refinement:

- whether the test invokes a framework-dependent CLI, the M2 AOT feasibility artifact, or both;
- fixture process orchestration and cross-platform CI scope;
- whether trace artifact upload is tested locally, in CI, or in a later state/publisher phase.

Related paths:

- `.github/workflows/ci.yml`
- `protocol/fixtures/v1/`
- `src/protocol/fixtures.test.ts`
- planned `runtime/` C# tests

## M4: Cache-Efficient Runtime Provider Foundation

Goal: establish project-owned live-provider execution with deterministic, cache-efficient cross-session recovery.

The first issue is #29, which defines the ledger, canonical prefix, provider capability, normalized usage, and cost non-regression contracts. #29 does not block deterministic M2 or M3 work, but project-owned live provider implementation must not proceed without its design.

Expected implementation follow-ups after #29:

- `ProviderSessionLedgerV1` schema, validation, and unsafe/corrupt fixtures;
- canonical prefix materialization and `prefixSha256` restore/append fixtures;
- provider capability and normalized cache usage model;
- minimal AOT-compatible live provider adapter with bounded failures and telemetry;
- ledger artifact restore/upload integration across separate workflow runs, including safe bootstrap fallback for missing, incompatible, corrupt, or unsafe state;
- representative resumed-session cache and normalized-cost validation.

M4 is complete only when the contract is implemented and validated. Closing #29 alone does not complete the milestone; #29 must create or identify the implementation follow-ups before it closes.

## Post-M4 Candidate: Runtime Context And Tool Orchestration

Full repo-local tool orchestration is not an M4 exit condition. Keep it as a separate roadmap candidate until the live-provider/session foundation is stable.

Candidate scope:

- bounded read, grep, glob, and patch-aware tools;
- tool request/result contracts and deterministic fixtures;
- stable-prefix tool-definition placement and dynamic tool-result placement;
- tool-call budgets, timeout/cancellation, path safety, and output bounds;
- provider/tool loop orchestration without GitHub write capabilities.

Do not create a milestone or implementation issues for this candidate until its contracts and dependency on M4 are refined.

## Phase 5: Stateful Memory And Safe Publisher Contracts

Keep Phase 5 roadmap-only until M3 result mapping and M4 ledger ownership are stable.

Medium-term targets:

- sticky metadata, state manifest, review snapshot, lineage, ledger, and finding identity compatibility;
- explicit bootstrap, invalidation, corruption, and schema-migration behavior;
- publish plan/result contracts and runtime-to-host trust validation;
- duplicate suppression, inline eligibility, sticky fallback, and failed target behavior;
- idempotent state and publisher behavior under retries or repeated workflow runs.

Phase 5 does not decide whether `claude-code-cli` is removed. That policy requires Phase 6 evidence and belongs to Phase 7 graduation.

## Phase 6: Evaluation, Replay, And Shadow Validation

Keep Phase 6 roadmap-only until M4 defines provider usage telemetry and Phase 5 defines state/publisher contracts.

Quality and correctness signals:

- must-find and must-not-find outcomes;
- false-positive, duplicate, and line-mapping rates;
- incremental review and state-recovery correctness;
- replay reproducibility and infrastructure failure classification;
- latency and provider/runtime failure rates.

Replay uses a versioned replay bundle or manifest, not `ReviewTraceV1` alone. The bundle contains or content-addresses sanitized review input, runtime/provider identity, deterministic provider and tool fixture material, optional approved ledger state for stateful cases, trace evidence, actual/expected result, and versioned content hashes without requiring GitHub credentials or live GitHub state.

Cache and cost signals:

- total, uncached, cache-read, cache-write, and output tokens when exposed by the provider;
- cache-read ratio and prefix-hash continuity across restored sessions;
- normalized input cost calculated from explicit evaluation pricing ratios rather than hard-coded protocol prices;
- resumed-session cost ratio against a documented stateless baseline;
- isolated cache miss versus sustained prefix instability or cost regression.

Phase 6 should include a no-publish shadow mode that compares the project-owned runtime with the maintained baseline on the same sanitized review inputs. Its promotion thresholds become inputs to the later runtime distribution and live-graduation policy.
