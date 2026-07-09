# Architecture Direction

The project should evolve toward a narrow, review-specific architecture:

1. TypeScript GitHub Action host and publisher.
2. C# review runtime core.
3. Schema-first JSON protocol between them.

This direction is a product architecture target, not a requirement to rewrite the current implementation immediately.

The schema-first protocol is defined as JSON Schema files under `protocol/schemas/`; see `docs/20_architecture/runtime-protocol.md` for contract details.

## TypeScript Responsibilities

TypeScript remains the best layer for GitHub Action integration:

- action inputs and outputs;
- GitHub event parsing;
- GitHub API calls;
- PR metadata and changed file loading;
- sticky PR comment state;
- existing comment scanning;
- inline comment publishing;
- line mapping and inline eligibility, at least initially;
- artifact upload;
- step summary and failure reporting;
- runtime binary resolution and invocation when an external runtime exists.

The TypeScript publisher owns side effects.

## Runtime Core Responsibilities

A future C# runtime core should be platform-neutral and review-specific:

- read sanitized review input;
- validate protocol version;
- perform context packing;
- run deterministic and live providers through a project-owned provider interface;
- orchestrate read/grep/glob-style repo-local tools when enabled;
- generate structured findings;
- generate finding fingerprints or fingerprint inputs;
- produce usage and trace data;
- write structured review result output.

The runtime core proposes findings. The publisher decides what can be posted.

## Non-Goals

Do not build these in the initial architecture:

- a general coding agent;
- an autonomous code-writing agent;
- a hosted GitHub App;
- a multi-platform tool beyond GitHub PR review;
- a Semantic Kernel or Microsoft Agent Framework dependency;
- deep Roslyn/MSBuild semantic analysis;
- model-callable GitHub write tools;
- runtime-owned GitHub comment posting.
