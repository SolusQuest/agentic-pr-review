# Project Context

`agentic-pr-review` is a GitHub-native PR review action and runtime project.

The product goal is to provide policy-driven, structured-first PR review automation that can:

- read GitHub PR metadata, changed files, and bounded patch context;
- apply repository review policy and context documents;
- produce structured findings;
- preserve cost-efficient cross-workflow review memory;
- avoid duplicate comments;
- support deterministic fixtures and replayable validation;
- publish safe PR feedback through deterministic adapter code.

## Runtime Product Constraints

Beyond the review capabilities above, the project-owned runtime has three product-level constraints that shape its architecture:

1. **Runtime replacement**: the self-developed runtime is the long-term live review path. `claude-code-cli` is the current live provider baseline and remains in a compatibility and maintenance role; new runtime capabilities target the project-owned runtime, not the Claude Code CLI integration.
2. **Cross-session context recovery**: the project-owned runtime must resume review context across separate GitHub Actions runs without depending on Claude Code's session mechanism.
3. **Cache-efficient session continuation**: for supported prefix-cache providers, the runtime must reconstruct a strict, stable cacheable request prefix across resumed sessions and make cache effectiveness and normalized input cost measurable. Stable construction is a runtime contract; an individual provider-reported cache hit is an observed outcome.

See `docs/20_architecture/architecture.md` for the runtime replacement direction, session continuity, and provider request prefix contract.
The project is not trying to become a generic coding agent, a general agent framework, or a hosted review service in the initial scope.

## Engineering Goals

This repository is also an intentional production-style agent runtime engineering project. The selected implementation direction is a C# runtime with Native AOT as its distribution target, even though a TypeScript-only or Go implementation could reduce cross-language complexity.

The engineering goals are to:

- build a review-specific agent runtime in C# behind a language-neutral JSON protocol;
- exercise deterministic cross-language contracts, provider orchestration, durable session state, and replay;
- keep runtime dependencies and serialization choices compatible with Native AOT from the first CLI milestone;
- publish pinned, verifiable, self-contained runtime binaries once behavior and compatibility are stable;
- make the added build, versioning, and distribution complexity visible and testable rather than incidental.

C# and Native AOT are architecture and engineering commitments, not product success criteria. Review quality, safety, resumability, cache economics, and operational reliability determine whether the runtime succeeds.

## Current Position

The existing implementation is a TypeScript GitHub Action with structured review output, sticky comment publishing, optional inline comments, state artifacts, deterministic test fixtures, and a live `claude-code-cli` runtime provider.

The next architectural direction is to evolve the review runtime into a clearer product boundary:

- TypeScript remains the GitHub Action host, adapter, and publisher.
- The selected project-owned C# runtime core owns review-domain reasoning, contract validation, provider orchestration, and trace generation.
- The boundary between them should be explicit, schema-first, and testable.

Native AOT feasibility is validated early so incompatible dependency choices do not accumulate. Production release assets, checksums, and compatibility matrices remain later distribution work.

`claude-code-cli` remains the current live provider baseline. It may receive bug fixes, security fixes, provider-version compatibility fixes, and CI/live-smoke maintenance, but new runtime product capabilities should target the project-owned runtime path rather than the Claude Code CLI integration.

## Source Of Truth

Use repository files as the durable source of truth:

- `README.md`: user-facing action usage and current public API.
- `docs/00_project/`: project role and source-of-truth rules.
- `docs/10_workflow/`: issue, PR, and release workflow rules.
- `docs/20_architecture/`: architecture, runtime protocol, security boundary, and distribution direction.
- `docs/50_ai/`: agent context and cross-agent procedures.
- `docs/90_roadmap/`: roadmap, near-term milestones, and issue planning.

GitHub issues track executable work. PR descriptions record implementation changes and validation. Chat discussions, local notes, and task prompts are not durable project truth until summarized into repository docs, issues, or PRs.
