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

Cross-language contract implementation is no longer an objective by itself. TypeScript remains as narrow state, protocol, migration, and conformance evidence plus the R4 thin wrapper and official artifact bridge. Business decisions, sticky publication, and inline publication remain in the C# Host.

C# and Native AOT are architecture commitments, not product success criteria. Review quality, grounded evidence, safety, resumability, cache economics, and operational reliability decide whether the runtime succeeds.

## Current Position

The current implementation contains:

- one nested, generated Node 24 Action wrapper for repository-controlled prepared-payload proof, with no downstream release/default claim or stable outputs;
- no legacy TypeScript coordinator, Claude Code CLI, or superseded single-request C# product route;
- one project-owned C# Agent with the six bounded read-only tools `list_changed_files`, `read_diff`, `list_files`, `search_text`, `read_file`, and `finish_review`;
- a bounded multi-turn tool loop whose validated tool results become canonical SESSION history and whose terminal findings are grounded in admitted evidence;
- a project-owned DeepSeek thinking adapter with exact continuation reconstruction across two fresh Host processes;
- authenticated encrypted local STATE, independent Host lineage, same-head and verified-ahead admission, and framework-dependent plus Linux x64 Native AOT validation;
- must-find, must-not-find, invalid-tool, tamper, replay, scope, continuation, and secret-canary evaluation;
- a protected, default-branch, no-publication live-provider proof at the final R3 commit;
- retained internal TypeScript state only as named R4 replacement evidence; W3 removed the invocation families and obsolete live-context schema, W5 removed the StateV2 family, schema, opaque sidecar fixtures, and generators after S5/S6/P5/P6/E1 disposition, W6 removed the Git-ref state-acceptance family and its dedicated schemas after S1-S6/P5/P6/E1 evidence, W8 removed the sticky publisher after P1/P2/P5/P6/E1 evidence, W9 independently removed the inline/target publisher after H2/H5/P3/P4/P6/E1 evidence, W10 removed the duplicate TypeScript review protocol/structured-result family while preserving the embedded Input/Result/Trace schemas and complete C#-owned fixture corpus, W11 removed the prefix family and generator after C# ownership, immutable-corpus, and E1 continuation proof, W12 removed the provider-metadata TypeScript surface, schema resource, and dedicated fixture corpus, and W15 removed the now-consumerless root review DTO and utility modules after their 52-symbol retained/superseded/obsolete disposition and W7-W10 import provenance were checked. R4-W5 retired StateV2; no current reader or compatibility surface. The direct-runtime schemas and C# models remain live independently of the ActionHost replacements.

R3 is complete. R4 has restored the bounded wrapper and its official artifact bridge over an explicitly prepared payload. The development head is still not a supported downstream Action and does not yet provide the complete trusted two-run proof, release payload delivery, or R7 public default.

The selected next direction is R4:

- keep the completed R3 Agent, tools, SESSION, grounding, DeepSeek continuation, and capability boundaries as the product core;
- add trusted GitHub event/configuration authorization and exact-SHA snapshot materialization in C# ActionHost;
- add downstream-repository-owned encrypted artifact state behind the internal asynchronous state-store seam;
- move deterministic sticky/inline publication and transaction recovery into C# Host code;
- integrate the thin Node Action wrapper and private artifact bridge with the remaining trusted Host proof work;
- delete retained TypeScript business families only through named replacement gates;
- keep downstream release/payload delivery and broad cost graduation in their later milestones.

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
