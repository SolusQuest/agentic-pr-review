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

Cross-language contract implementation is no longer an objective by itself. TypeScript remains only in the thin wrapper and official artifact bridge, their tests, the permanent S2 artifact-provenance vectors, and migration/conformance guards. The direct-runtime JSON schemas and fixtures are embedded and consumed by C#; business decisions, state, protocol handling, sticky publication, and inline publication belong to the C# Host and runtime.

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
- a closed TypeScript migration record: W3-W15 removed the invocation, StateV2, state-acceptance, ledger, publisher, protocol, prefix, provider-metadata, canonicalization, and root shared-module families only after their C# replacements or reviewed-obsolete dispositions were checked. No current TypeScript state reader, publisher, protocol adapter, compatibility surface, or canonicalizer remains. The direct-runtime schemas and C# models remain live independently of the ActionHost replacements, and the S2 vectors remain permanent negative conformance evidence. R4-W5 retired StateV2; no current reader or compatibility surface. R4-W14 retired the TypeScript canonical-json family; C# Canonical remains current and the prefix corpus remains immutable evidence.

R3 is complete. R4 restored the bounded wrapper and its official artifact bridge over an explicitly prepared payload, completed the TypeScript business/state/publisher/protocol cutover, and closed the migration inventory, E1/E2 handoffs, and the maintainer-authorized trusted two-run proof; see the [`r4-migration-cutover-handoff.md`](../20_architecture/r4-migration-cutover-handoff.md) record and completed [#181](https://github.com/SolusQuest/agentic-pr-review/issues/181) proof. The development head is still not a supported downstream Action. The approved [R7 plan](../90_roadmap/r7-plan.md) targets public experimental release payload delivery and bounded maintainer adoption; formal public/default graduation is deferred to a later explicit maintainer decision.

R4's Host, state, publication and TypeScript cutover work is an implementation record. R5 engineering evaluation is delivered, while its earlier [live model-quality result](../90_roadmap/r5-evaluation-results.md) remains inconclusive. The [R6 final acceptance](https://github.com/SolusQuest/agentic-pr-review/issues/271#issuecomment-5928401721) accepts bounded engineering/evaluation closeout after complete descriptive A/B histories, independently AI-adjudicated bounded V5 quality/safety and reduced-capacity reset evidence. It supersedes the earlier [insufficient-evidence report](../90_roadmap/r6-economics-results.md) at that bounded scope without rewriting it. Native-history equivalence, live segmented-prefix association, formal regression, actual billing and default-capacity adequacy remain unknown or inconclusive; they supply no graduation credit or automatic new R7 obligations.

R7's budgets, trusted review configuration, accounting outputs, exact downloadable payload and SDK-free templates are approved targets, not features delivered by this documentation activation. Dependent implementation waits for the C1 docs merge and native issue dependencies; release, deployment, paid calls and closeout retain separate authority. Host credential/state/publication ownership and selected-current fail-closed behavior remain unchanged.

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
- `docs/shared/`: pinned Solus Book engineering standards and common skills;
- `docs/50_ai/`: APR agent context, handbook adoption, and local procedure additions;
- `docs/90_roadmap/`: current sequencing and issue-planning direction.

Current implementation contract documents remain authoritative for current code until migration removes their surfaces. When they conflict with the selected long-term direction, the rebaseline document controls new design work.

Use [APR source-of-truth rules](source-of-truth.md) with the shared model for GitHub work records, durable decisions, and historical evidence.
