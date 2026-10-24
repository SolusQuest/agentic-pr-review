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

The APR review product has four runtime constraints:

1. **Product-owned authority**: APR owns review policy, evidence acceptance, encrypted storage, lineage/reset and deterministic side effects. The selected managed production composition uses SolusAgent for generic execution; CR1-R7 removes the obsolete APR-owned loop while retaining APR domain and Host authority. The Claude Code CLI path and legacy TypeScript coordinator have been removed; historical tags preserve historical behavior.
2. **Cross-run session recovery**: a completed review session can be restored and continued across separate GitHub Actions runs without relying on a third-party CLI session mechanism.
3. **Cache-efficient continuation**: supported providers receive a stable, runtime-owned cacheable prefix reconstructed from canonical logical session state. Prefix stability is enforceable; a provider cache hit is an observed outcome.
4. **Deterministic side effects**: the model and Agent propose findings, while trusted Host code validates and performs all GitHub writes.

See [`docs/20_architecture/agent-runtime-rebaseline.md`](../20_architecture/agent-runtime-rebaseline.md) for the earlier architecture and current implementation contract. [CR1/CR2](../90_roadmap/cr1-cr2-plan.md) governs the selected migration target and sequence.

## Engineering Goals

C# remains the product language. CR1 implements SolusAgent and framework-dependent managed .NET 10 distribution; CR2 implements a single .NET CLI product entry and thin composite Action. The [CR2 technical acceptance](../20_architecture/cr2-technical-acceptance.md) records the final source, retirement owners and separate original-archive/Runner/hosted evidence.

The engineering goals are to:

- build a review-specific C# product over bounded shared-runtime execution;
- implement a small, safe read-only repository tool set;
- keep GitHub credentials and capabilities out of provider-visible, model-visible, durable, and published channels through explicit data-flow and capability boundaries;
- keep reasoning, repository tool results, and provider continuation material out of repository-visible plaintext state, with authenticated scope binding and production transport controls that prevent fork/untrusted workflows from accessing, decrypting, substituting, replaying, or publishing restricted sessions;
- keep APR state/acceptance authority while Solus owns generic runtime context and provider mechanics;
- validate review quality, resumability, safety, and cache economics with representative execution;
- publish pinned, verifiable managed archives containing the complete publish tree with an explicit compatible runtime prerequisite;
- keep compatibility machinery proportional to actual independently released or durable boundaries.

Cross-language contract implementation is no longer an objective by itself. TypeScript remains only in the thin wrapper and official artifact bridge, their tests, the permanent S2 artifact-provenance vectors, and migration/conformance guards. The direct-runtime JSON schemas and fixtures are embedded and consumed by C#; business decisions, state, protocol handling, sticky publication, and inline publication belong to the C# Host and runtime.

The selected language, shared runtime and distribution model serve the product goals. Review quality, grounded evidence, safety, resumability, cache economics, and operational reliability decide whether the runtime succeeds.

## Current Position

The current implementation contains:

- one nested composite with internal Node 24 preparation/post and launch Actions, ordinary exact managed release-map resolution and trusted runtime/tree descriptor launch; seven raw inputs and fourteen outputs remain, while the checked map is null and no supported downstream installation exists;
- one C# `run` entry owning input/default/configuration, review, results/accounting and Host authorization/state/publication; no legacy TypeScript coordinator or Claude Code CLI route;
- APR-owned review policy, six bounded read-only tools (`list_changed_files`, `read_diff`, `list_files`, `search_text`, `read_file`, `finish_review`) and findings acceptance over the five exact Solus `0.1.0-internal.4` packages;
- compatible restricted Solus context format 2 / compatibility 1 under the bounded `cr1-solus-internal4-dev` profile, authenticated encrypted state, independent Host lineage, fail-closed selected-current admission and explicit authorized reset;
- C# artifact REST discovery/readback/deletion and a private upload-only official SDK subprocess, with transaction-interleaved readback and confirmed process/channel closure before release;
- actual original managed-archive/final composite qualification and separately scoped successful hosted artifact upload/restore/delete proof; historical Native AOT, APR-loop/provider/SESSION evidence retains its original source and supporting consumers;
- must-find, must-not-find, invalid-tool, tamper, replay, scope, continuation, and secret-canary evaluation;
- a protected, default-branch, no-publication live-provider proof at the final R3 commit;
- a closed TypeScript migration record: W3-W15 removed the invocation, StateV2, state-acceptance, ledger, publisher, protocol, prefix, provider-metadata, canonicalization, and root shared-module families only after their C# replacements or reviewed-obsolete dispositions were checked. No current TypeScript state reader, publisher, protocol adapter, compatibility surface, or canonicalizer remains. The direct-runtime schemas and C# models remain live independently of the ActionHost replacements, and the S2 vectors remain permanent negative conformance evidence. R4-W5 retired StateV2; no current reader or compatibility surface. R4-W14 retired the TypeScript canonical-json family; C# Canonical remains current and the prefix corpus remains immutable evidence.

R3 is complete. R4 restored the bounded wrapper and its official artifact bridge over an explicitly prepared payload, completed the TypeScript business/state/publisher/protocol cutover, and closed the migration inventory, E1/E2 handoffs, and the maintainer-authorized trusted two-run proof; see the [`r4-migration-cutover-handoff.md`](../20_architecture/r4-migration-cutover-handoff.md) record and completed [#181](https://github.com/SolusQuest/agentic-pr-review/issues/181) proof. The development head is still not a supported downstream Action. After CR1/CR2 and affected contract refinement, the retained [R7 plan](../90_roadmap/r7-plan.md) targets public experimental release payload delivery and bounded maintainer adoption; formal public/default graduation is deferred to a later explicit maintainer decision.

R4's Host, state, publication and TypeScript cutover work is an implementation record. R5 engineering evaluation is delivered, while its earlier [live model-quality result](../90_roadmap/r5-evaluation-results.md) remains inconclusive. The [R6 final acceptance](https://github.com/SolusQuest/agentic-pr-review/issues/271#issuecomment-5928401721) accepts bounded engineering/evaluation closeout after complete descriptive A/B histories, independently AI-adjudicated bounded V5 quality/safety and reduced-capacity reset evidence. It supersedes the earlier [insufficient-evidence report](../90_roadmap/r6-economics-results.md) at that bounded scope without rewriting it. Native-history equivalence, live segmented-prefix association, formal regression, actual billing and default-capacity adequacy remain unknown or inconclusive; they supply no graduation credit or automatic new R7 obligations.

R7 has delivered parts of its runtime/configuration/accounting and ordinary-resolution surface; its map remains unbound. CR1/CR2 replaces the future generic-runtime, AOT and permanent Node-entry assumptions. Affected unfinished contracts require re-refinement before execution; remaining first-candidate work follows both CR batches. Release, deployment, paid calls and closeout retain separate authority. Host credential/state/publication ownership and selected-current fail-closed behavior remain unchanged.

A separate Agent process is deferred until fault, resource, extension, or trust evidence justifies the additional protocol and distribution surface.

## Source Of Truth

Use repository files as the durable source of truth:

- `README.md`: current user-facing action usage and public API;
- `docs/00_project/`: project role and source-of-truth rules;
- `docs/10_workflow/`: issue, PR, and release workflow rules;
- `docs/90_roadmap/cr1-cr2-plan.md`: selected shared-runtime, managed distribution and CLI/Action change direction;
- `docs/20_architecture/agent-runtime-rebaseline.md`: earlier R-series architecture and surviving implementation contracts;
- `docs/20_architecture/architecture.md`: concise architecture direction;
- `docs/20_architecture/security-boundary.md`: trust, credential, tool, and artifact boundaries;
- `docs/20_architecture/`: current implementation contracts and architecture details;
- `docs/shared/`: pinned Solus Book engineering standards and common skills;
- `docs/50_ai/`: APR agent context, handbook adoption, and local procedure additions;
- `docs/90_roadmap/`: current sequencing and issue-planning direction.

Current implementation contract documents remain authoritative for current code until migration removes their surfaces. When they conflict with the selected long-term direction, the CR1/CR2 document controls the changed target and sequencing for new design work.

Use [APR source-of-truth rules](source-of-truth.md) with the shared model for GitHub work records, durable decisions, and historical evidence.
