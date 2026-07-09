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

The project is not trying to become a generic coding agent, a general agent framework, or a hosted review service in the initial scope.

## Current Position

The existing implementation is a TypeScript GitHub Action with structured review output, sticky comment publishing, optional inline comments, state artifacts, deterministic test fixtures, and a live `claude-code-cli` runtime provider.

The next architectural direction is to evolve the review runtime into a clearer product boundary:

- TypeScript remains the GitHub Action host, adapter, and publisher.
- A future C# runtime core owns review-domain reasoning, contract validation, provider orchestration, and trace generation.
- The boundary between them should be explicit, schema-first, and testable.

## Source Of Truth

Use repository files as the durable source of truth:

- `README.md`: user-facing action usage and current public API.
- `docs/00_project/`: project role and source-of-truth rules.
- `docs/10_workflow/`: issue, PR, and release workflow rules.
- `docs/20_architecture/`: architecture, runtime protocol, security boundary, and distribution direction.
- `docs/50_ai/`: agent context and cross-agent procedures.
- `docs/90_roadmap/`: public roadmap, near-term milestones, and issue planning.

GitHub issues track executable work. PR descriptions record implementation changes and validation. Chat discussions, local notes, and task prompts are not durable project truth until summarized into public repository docs, issues, or PRs.

## Public Context Boundary

All public repository content must be understandable without private context. If a task depends on private context, the private context must first be converted into public-safe requirements, constraints, or acceptance criteria.
