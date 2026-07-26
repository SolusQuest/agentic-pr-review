# Project Context

`agentic-pr-review` is a GitHub-native, stateful code review agent and deterministic publishing action.

The product goal is to:

- review one immutable pull request snapshot;
- retrieve additional repository context through bounded read-only tools;
- apply repository review policy and context documents;
- produce structured, grounded, low-noise findings;
- resume useful review context across separate GitHub Actions runs;
- preserve cache-efficient session continuation where providers support prefix caching;
- avoid duplicate comments and stale publication;
- support deterministic fixtures, quality evaluation, and replay;
- publish safe PR feedback only through trusted deterministic host code.

The project is not a generic coding agent, code editor, shell agent, general agent framework, or hosted review service in its initial scope.

## Runtime Product Constraints

The project-owned runtime has four product-level constraints:

1. **Project-owned execution**: the new development head uses the project-owned runtime. The Claude Code CLI path and legacy TypeScript coordinator have been removed; historical tags preserve historical behavior.
2. **Cross-run session recovery**: a completed review session can be restored and continued across separate GitHub Actions runs without relying on a third-party CLI session mechanism.
3. **Cache-efficient continuation**: supported providers receive a stable, runtime-owned cacheable prefix reconstructed from canonical logical session state. Prefix stability is enforceable; a provider cache hit is an observed outcome.
4. **Deterministic side effects**: the model and Agent propose findings, while trusted Host code validates and performs all GitHub writes.

See [`docs/20_architecture/agent-runtime-rebaseline.md`](../20_architecture/agent-runtime-rebaseline.md) for the detailed selected architecture.

## Engineering Goals

C# and Native AOT remain the selected product-runtime and distribution direction.

The engineering goals are to:

- build a review-specific C# agent with a bounded multi-turn loop;
- implement a small, safe read-only repository tool set;
- keep GitHub credentials and capabilities out of provider-visible, model-visible, durable, and published channels through explicit data-flow and capability boundaries;
- keep reasoning, repository tool results, and provider continuation material out of repository-visible plaintext state, with authenticated scope binding and production transport controls that prevent fork/untrusted workflows from accessing, decrypting, substituting, replaying, or publishing restricted sessions;
- own canonical session state and provider request materialization;
- validate review quality, resumability, safety, and cache economics with representative execution;
- publish pinned, verifiable, self-contained runtime payloads;
- keep compatibility machinery proportional to actual independently released or durable boundaries.

Cross-language contract implementation is no longer an objective by itself. During the R1 no-public-Action interval, TypeScript remains only as narrow runtime, state, publisher, migration, and conformance evidence. R4 may reintroduce a thin wrapper and artifact bridge after the Host is selected and proven.

C# and Native AOT are architecture commitments, not product success criteria. Review quality, grounded evidence, safety, resumability, cache economics, and operational reliability decide whether the runtime succeeds.

## Current Position

The current implementation contains:

- no public GitHub Action and no legacy TypeScript coordinator or Claude Code CLI execution path;
- deterministic and ledger-oriented C# runtime paths;
- a project-owned DeepSeek provider adapter;
- structured review output and optional inline comments;
- retained internal TypeScript state selection, acceptance, publisher, and prefix-contract evidence for R2-R4 migration;
- extensive TypeScript/C# conformance fixtures;
- framework-dependent and Native AOT validation.

This is a reliable stateful provider pipeline, but it is not yet the target code review agent. The core missing product slice is:

- provider-requested repository tools;
- multi-turn tool execution;
- tool results returned to the provider;
- canonical agent conversation state containing bounded tool results;
- findings bound to observed tool evidence;
- must-find and must-not-find quality evaluation.

The selected next direction is:

- remove the Claude Code CLI and migration-only public runtime selectors from the new head;
- prove a C# agent loop and read-only tools before another broad contract program;
- let R2 compare a pinned `Microsoft.Extensions.AI.Abstractions` dependency with project-minimal exchange types and select the smaller Native AOT-proven surface;
- keep the agent loop, durable state, budgets, provider capabilities, and prefix semantics project-owned;
- migrate GitHub business logic into the trusted Host module of one C# executable;
- reduce TypeScript to a thin Action wrapper and artifact bridge;
- delete duplicate TypeScript business validators after equivalent host behavior is proven;
- defer broad cost-graduation work until real resumed agent traffic exists.

A separate Agent process is deferred until fault, resource, extension, or trust evidence justifies the additional protocol and distribution surface.

## Source Of Truth

Use repository files as the durable source of truth:

- `README.md`: current user-facing action usage and public API;
- `docs/00_project/`: project role and source-of-truth rules;
- `docs/10_workflow/`: issue, PR, and release workflow rules;
- `docs/20_architecture/agent-runtime-rebaseline.md`: selected target agent architecture and migration;
- `docs/20_architecture/architecture.md`: concise architecture direction;
- `docs/20_architecture/security-boundary.md`: trust, credential, tool, and artifact boundaries;
- `docs/20_architecture/`: current implementation contracts and architecture details;
- `docs/50_ai/`: agent context and cross-agent procedures;
- `docs/90_roadmap/`: current sequencing and issue-planning direction.

Current implementation contract documents remain authoritative for current code until migration removes their surfaces. When they conflict with the selected long-term direction, the rebaseline document controls new design work.

GitHub issues track actionable design, refinement, and implementation work. Pull requests record accepted project decisions, implementation history, and validation. Chat discussions, local notes, and task prompts are not durable project truth until summarized into repository docs, issues, or PRs.
