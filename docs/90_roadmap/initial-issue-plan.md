# Initial Issue Plan

This file records the initial issue breakdown for the near-term roadmap. It is intended
to seed GitHub milestones and issues for M0 through M2 only. Later roadmap phases remain candidates
until their dependencies are clearer.

## Milestones To Create Now

### [M0: Validation and roadmap baseline](https://github.com/SolusQuest/agentic-pr-review/milestone/1)

Purpose: make the roadmap and validation baseline match the current repository state.

Exit criteria:

- `npm run check` is reliable as the documented default validation command.
- roadmap docs describe the current TypeScript action baseline without presenting completed work as
  future work.
- near-term runtime protocol work is split into self-contained issues.

### [M1: Runtime protocol contract](https://github.com/SolusQuest/agentic-pr-review/milestone/3)

Purpose: define and test the schema-first file protocol between the TypeScript host and selected C#
runtime without replacing the current action path.

Exit criteria:

- `ReviewInputV1`, `ReviewResultV1`, and minimal `ReviewTraceV1` are defined by schema or strict
  equivalent contract definitions.
- TypeScript constructs sanitized `review-input.json` fixtures from existing action state.
- TypeScript validates `review-result.json` fixtures and maps results into current structured review
  handling.
- fixtures cover bootstrap, incremental, skipped/empty, invalid output, incompatible protocol
  version, path safety, and privacy cases.
- GitHub credentials and write-only platform metadata are excluded from runtime input.

### [M2: Deterministic C# runtime CLI](https://github.com/SolusQuest/agentic-pr-review/milestone/2)

Purpose: prove a standalone deterministic C# runtime CLI can consume the protocol, emit sanitized
results/traces, and pass an early Native AOT feasibility check in local and CI validation.

Exit criteria:

- runtime CLI supports `review --input <path> --output <path> --trace <path>`.
- runtime validates protocol and runtime version.
- documented exit codes distinguish success, contract errors, runtime errors, and provider errors.
- invalid input produces no partial successful result.
- deterministic provider output is stable across repeated runs and CI.
- trace output is sanitized by default.
- an initial `linux-x64` Native AOT publish executes a deterministic fixture without production
  release packaging.
- CI continuously publishes and executes the AOT binary against at least one deterministic fixture.

M2 sequencing:

- refine #20's stable exit taxonomy before #19 finalizes externally observable failure behavior;
- #19 establishes the CLI, deterministic protocol path, minimal fail-closed plumbing, and initial AOT feasibility;
- #20 owns the stable version, exit-code, and sanitized error contract;
- #21 runs after #19 and #20 and continuously validates both framework-dependent and Native AOT paths in CI.

## Issues

### [Task: Rebaseline public roadmap and near-term issue plan](https://github.com/SolusQuest/agentic-pr-review/issues/13)

Milestone: `M0: Validation and roadmap baseline`

Objective: update the roadmap so it reflects the current repository baseline and the next
executable runtime-protocol work.

Context: the current action already has structured output validation, sticky comments, optional inline
comments, state artifacts, PR diff snapshots, deterministic fixtures, and a live provider path. The
previous roadmap seed read like early planning material and did not clearly distinguish completed
baseline capabilities from next architectural work.

In scope:

- Update `docs/90_roadmap/roadmap-seed.md` into a current roadmap.
- Document the long-term goal, non-goals, target architecture, phased roadmap, and near-term
  milestones.
- Add an issue plan for M0 through M2.
- Keep later work as candidate roadmap material until protocol and CLI decisions land.

Out of scope:

- runtime implementation changes;
- action input/output behavior changes;
- GitHub App, hosted service, GitLab, or Azure DevOps design;

Acceptance criteria:

- Roadmap docs describe current capabilities without presenting completed work as future work.
- M0 through M2 are self-contained.
- Later phases are documented as candidates rather than active commitments.
- `npm run check` or docs-appropriate validation is run.

Related docs:

- `docs/00_project/project-context.md`
- `docs/20_architecture/architecture.md`
- `docs/20_architecture/runtime-protocol.md`
- `docs/90_roadmap/roadmap-seed.md`

### [Task: Define ReviewInputV1 protocol contract](https://github.com/SolusQuest/agentic-pr-review/issues/14)

Milestone: `M1: Runtime protocol contract`

Objective: define the initial sanitized runtime input contract used by the TypeScript host to hand
review context to a future runtime.

Context: the runtime boundary is intended to be protocol-first and file-based. The TypeScript host
should write `review-input.json`, and the runtime should read it without receiving GitHub write
credentials.

In scope:

- Define `ReviewInputV1` using JSON Schema or an equivalent strict contract source.
- Include protocol version, requested runtime version, target metadata, PR metadata, phase/review
  mode, changed files, bounded patch context, optional incremental diff snapshot summary, context
  documents, policy text, runtime options, minimal previous state summary, and minimal existing
  comment/duplicate evidence summary.
- Specify which fields are trusted host metadata versus untrusted review subject data.
- Exclude `GITHUB_TOKEN`, provider API keys, raw auth headers, private runner paths, and write-only
  GitHub metadata.
- Add representative valid and invalid input examples.

Out of scope:

- implementing the runtime CLI;
- changing current publishing behavior;
- adding live provider behavior to the runtime.

Acceptance criteria:

- Contract rejects incompatible or missing protocol version.
- Contract excludes credentials and secrets by design.
- Path fields are repo-relative and reject absolute, drive-qualified, protocol-looking, current-dir
  only, and `..` paths.
- Existing TypeScript action state can be mapped to the contract.
- Tests or fixture validation cover valid bootstrap and incremental inputs.

Related docs and code:

- `docs/20_architecture/runtime-protocol.md`
- `docs/20_architecture/security-boundary.md`
- `src/types.ts`
- `src/target.ts`
- `src/prompt.ts`
- `src/state.ts`

### [Task: Define ReviewResultV1 protocol contract](https://github.com/SolusQuest/agentic-pr-review/issues/15)

Milestone: `M1: Runtime protocol contract`

Objective: define the initial runtime result contract that carries proposed structured findings back
to the TypeScript publisher.

Context: runtime output is already structured at the action boundary today through
`ModelReviewContentV1` and `StructuredReviewEnvelopeV1`. The protocol result should preserve that
structured-first behavior while making the runtime boundary explicit and versioned.

In scope:

- Define `ReviewResultV1` using JSON Schema or an equivalent strict contract source.
- Include protocol version, runtime version, summary, findings, usage, warnings, diagnostics, and
  optional trace reference.
- Define finding fields for severity, confidence, category, title, body, evidence, repo-relative
  path, proposed line/range, optional suggested action, and optional publish hint.
- Document how `ReviewResultV1` maps into the current structured review normalization path.
- Add valid, no-finding, pathless finding, and invalid-result examples.

Out of scope:

- comment publishing changes;
- full publish-plan schema;
- runtime provider implementation.

Acceptance criteria:

- Invalid JSON or schema-invalid result fails closed.
- Low-confidence observations are omitted rather than represented.
- Unsafe paths and invalid line ranges are rejected.
- Runtime-provided workflow facts cannot override host-owned metadata such as phase, SHA, state key,
  runtime provider, usage budget status, or lineage totals.
- Mapping to current structured review behavior is covered by tests.

Related docs and code:

- `docs/20_architecture/runtime-protocol.md`
- `README.md`
- `src/structured.ts`
- `src/types.ts`

### [Task: Define minimal ReviewTraceV1 protocol contract](https://github.com/SolusQuest/agentic-pr-review/issues/16)

Milestone: `M1: Runtime protocol contract`

Objective: define the minimal sanitized trace contract needed for deterministic validation and as
evidence referenced by future replay bundles.

Context: trace data is important evidence for evaluation and future replay bundles, but a trace is not
a standalone replay artifact. Normal artifacts must not contain raw provider request or response
bodies, secrets, auth headers, raw prompts, or unbounded tool results.

In scope:

- Define minimal `ReviewTraceV1` schema or equivalent strict contract.
- Include protocol version, runtime version, input/result hashes, fixture or provider mode, sanitized
  tool-call summaries, usage summaries, warning/diagnostic summaries, and timestamps when needed.
- Define normal trace privacy constraints.
- Document how restricted raw diagnostics remain separate trusted debug behavior.

Out of scope:

- full replay implementation;
- raw provider diagnostic artifact changes;
- provider-specific trace adapters.

Acceptance criteria:

- Normal trace fixtures contain no raw provider bodies, secrets, auth headers, private runner paths,
  or unbounded tool output.
- Trace references can be associated with `ReviewResultV1`.
- Contract validation rejects unsafe trace payloads where feasible.
- Privacy constraints align with `docs/20_architecture/security-boundary.md`.

Related docs and code:

- `docs/20_architecture/security-boundary.md`
- `docs/20_architecture/runtime-protocol.md`
- `src/state.ts`
- `src/runtime.ts`

### [Task: Add protocol contract fixtures and safety cases](https://github.com/SolusQuest/agentic-pr-review/issues/17)

Milestone: `M1: Runtime protocol contract`

Objective: add deterministic contract fixtures that prove the runtime protocol handles normal review
scenarios, failure modes, and privacy boundaries.

Context: the project needs shared fixtures before TypeScript and a future runtime can evolve
independently. Fixtures should make schema drift and unsafe payloads visible in CI.

In scope:

- Add fixture files for bootstrap, incremental, skipped/empty, invalid result, incompatible protocol
  version, path safety, privacy/secret exclusion, and trace privacy.
- Add expected validation outcomes for each fixture.
- Document fixture naming and update rules.
- Keep fixtures synthetic.

Out of scope:

- live provider validation;
- real PR diffs or workflow logs;
- runtime CLI implementation.

Acceptance criteria:

- Fixtures are deterministic and reviewable in PRs.
- Invalid fixtures fail for the expected reason.
- Secret-like values, raw auth headers, absolute paths, and unsafe repo-relative paths are covered by
  negative cases.
- CI or local tests exercise the fixture set.

Related docs and code:

- `docs/20_architecture/runtime-protocol.md`
- `docs/20_architecture/security-boundary.md`
- `src/structured.test.ts`
- `src/state.test.ts`

### [Task: Add TypeScript protocol fixture construction and validation tests](https://github.com/SolusQuest/agentic-pr-review/issues/18)

Milestone: `M1: Runtime protocol contract`

Objective: prove the current TypeScript action state can construct sanitized protocol input fixtures
and validate runtime result fixtures without changing normal action behavior.

Context: the TypeScript action already resolves PR targets, context blocks, state artifacts,
incremental diff snapshots, and structured results. The protocol contract should reuse that knowledge
through explicit tests before a runtime CLI is introduced.

In scope:

- Add TypeScript helpers or test-only builders for `ReviewInputV1` fixture construction.
- Add validation tests for `ReviewResultV1` fixtures.
- Cover bootstrap and incremental PR modes, synthetic fixtures, path filtering, state summaries, and
  result mapping.
- Keep current action execution path unchanged unless a small helper extraction is needed.

Out of scope:

- invoking an external runtime;
- changing action inputs or outputs;
- publishing comments from protocol fixtures.

Acceptance criteria:

- Tests prove sanitized input can be generated from existing target/context/state data.
- Tests prove runtime result fixtures map into current structured review behavior.
- Credentials, provider secrets, and GitHub write metadata are not present in generated input.
- `npm run check` passes.

Related code:

- `src/main.ts`
- `src/target.ts`
- `src/context-blocks.ts`
- `src/state.ts`
- `src/structured.ts`
- `src/prompt.ts`

### [Feature: Add deterministic C# runtime CLI skeleton](https://github.com/SolusQuest/agentic-pr-review/issues/19)

Milestone: `M2: Deterministic C# runtime CLI`

Objective: add a standalone runtime CLI skeleton that can read protocol input and emit deterministic
protocol output without live provider secrets.

Context: the selected runtime direction is a C# review runtime distributed as Native AOT after behavior and compatibility are proven. The first CLI should prove the file protocol, deterministic behavior, and early AOT feasibility, not full provider orchestration or production packaging.

Coordination: #20 owns the stable exit-code taxonomy. #19 may establish minimal fail-closed process behavior, but it must not independently freeze externally observable exit classes before #20 is refined.

In scope:

- Add a runtime CLI project under a clear runtime directory.
- Implement `review --input <path> --output <path> --trace <path>`.
- Read and validate `ReviewInputV1`.
- Write deterministic `ReviewResultV1` and minimal `ReviewTraceV1`.
- Keep output stable for repeated runs.
- Document local execution commands.
- Prove the selected dependencies and CLI entrypoint can publish and run as an initial `linux-x64` Native AOT feasibility artifact.

Out of scope:

- production Native AOT release packaging, checksums, or platform matrix;
- live provider integration;
- GitHub API calls;
- comment publishing;
- Roslyn/MSBuild integration.

Acceptance criteria:

- CLI can run locally against protocol fixtures.
- CLI does not read `GITHUB_TOKEN` or provider secret environment variables.
- Output is deterministic for the same input.
- Invalid input does not write a successful result.
- An initial `linux-x64` Native AOT publish can execute a deterministic fixture without requiring the .NET SDK on the consuming path.
- Relevant local validation passes.

Related docs:

- `docs/20_architecture/architecture.md`
- `docs/20_architecture/runtime-protocol.md`
- `docs/20_architecture/distribution.md`

### [Task: Add runtime protocol version and exit-code handling](https://github.com/SolusQuest/agentic-pr-review/issues/20)

Milestone: `M2: Deterministic C# runtime CLI`

Objective: define and test runtime protocol/version checks and stable CLI exit codes.

Context: both sides of the runtime boundary should validate protocol version and fail closed on
incompatible contracts. Stable exit codes are needed before TypeScript can safely invoke a runtime
binary.

In scope:

- Document runtime CLI exit codes.
- Fail closed on unsupported protocol versions.
- Distinguish contract validation errors, runtime errors, provider errors, and trace/output write
  errors.
- Ensure errors are sanitized and do not print secrets or raw payloads.
- Add tests for success and failure exit paths.

Out of scope:

- full TypeScript runtime invocation;
- release asset verification;
- live provider retry policy.

Acceptance criteria:

- Unsupported protocol version exits with the documented contract-error code.
- Invalid input exits without writing a successful result.
- Error diagnostics are bounded and sanitized.
- Tests cover each documented exit class.

Related docs:

- `docs/20_architecture/runtime-protocol.md`
- `docs/20_architecture/security-boundary.md`

### [Task: Add deterministic and Native AOT runtime CI coverage](https://github.com/SolusQuest/agentic-pr-review/issues/21)

Milestone: `M2: Deterministic C# runtime CLI`

Objective: add CI coverage proving the framework-dependent and Native AOT runtime CLI paths can
validate fixtures and produce stable results.

Context: the runtime CLI should be exercised before TypeScript action integration. #19 establishes
initial AOT feasibility, while this issue continuously catches protocol drift, deterministic output
changes, and dependencies or serialization choices that break Native AOT.

Dependencies: #19 and #20.

In scope:

- Add a local script or test target that runs the deterministic runtime against protocol fixtures.
- Add CI coverage for the framework-dependent runtime fixture check.
- Publish the `linux-x64` Native AOT binary in CI and execute it against at least one deterministic
  fixture.
- Keep validation provider-secret-free.
- Document how to run the fixture check locally.

Out of scope:

- live provider smoke tests;
- production runtime release packaging, checksums, or download behavior;
- action-to-runtime integration.

Acceptance criteria:

- CI runs framework-dependent and Native AOT deterministic fixture checks without provider secrets.
- The AOT check executes the published binary rather than only proving that `dotnet publish`
  succeeds.
- Fixture output is deterministic or compared through stable normalized assertions.
- Failing fixture validation fails CI.
- Local validation instructions are documented.

Related docs and workflows:

- `.github/workflows/ci.yml`
- `docs/20_architecture/runtime-protocol.md`
- `docs/20_architecture/security-boundary.md`
