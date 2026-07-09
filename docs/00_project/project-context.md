# Project Context

`agentic-pr-review` is a GitHub-native PR review action and runtime project.

The project goal is to provide policy-driven, structured-first PR review automation that can:

- read GitHub PR metadata, changed files, and bounded patch context;
- apply repository review policy and context documents;
- produce structured findings;
- preserve cross-workflow review memory;
- avoid duplicate comments;
- support deterministic fixtures and replayable validation;
- publish safe PR feedback through deterministic adapter code.

## Runtime Product Constraints

Beyond the review capabilities above, the project-owned runtime has three product-level constraints that shape its architecture:

1. **Runtime replacement**: the self-developed runtime is the long-term live review path. `claude-code-cli` is the current live provider baseline and remains in a compatibility and maintenance role; new runtime capabilities target the project-owned runtime, not the Claude Code CLI integration.
2. **Cross-session context recovery**: the project-owned runtime must resume review context across separate GitHub Actions runs without depending on Claude Code's session mechanism.
3. **Stable provider request prefix**: the runtime must construct LLM API requests with a strict, stable cacheable prefix so that prefix-cache reuse is possible across resumed sessions.

See `docs/20_architecture/architecture.md` for the runtime replacement direction, session continuity, and provider request prefix contract.
The project is not trying to become a generic coding agent, a general agent framework, or a hosted review service in the initial scope.

## Current Position

The existing implementation is a TypeScript GitHub Action with structured review output, sticky comment publishing, optional inline comments, state artifacts, deterministic test fixtures, and a live `claude-code-cli` runtime provider.

The next architectural direction is to evolve the review runtime into a clearer product boundary:

- TypeScript remains the GitHub Action host, adapter, and publisher.
- A future C# runtime core owns review-domain reasoning, contract validation, provider orchestration, and trace generation.
- The boundary between them should be explicit, schema-first, and testable.

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
