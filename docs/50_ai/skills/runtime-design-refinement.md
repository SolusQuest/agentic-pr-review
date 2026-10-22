# Runtime Design Refinement

Use shared [design refinement](../../shared/skills/design-refinement/SKILL.md) before implementing high-impact runtime or Action architecture changes. This document supplies APR's triggers, architecture constraints, and non-goals; the shared skill owns the decision procedure and output structure.

## Trigger This Procedure For

- action input or output changes;
- runtime provider selection changes;
- model backend configuration changes;
- JSON schema or protocol changes;
- C# runtime boundary changes;
- GitHub token or side-effect boundary changes;
- sticky state, lineage, or memory model changes;
- inline comment or publisher behavior changes;
- trace, artifact, or debug capture privacy changes;
- release, pinning, or runtime download policy changes.

## Current Architectural Defaults

- New runtime design follows `docs/90_roadmap/cr1-cr2-plan.md` and its scoped precedence over the earlier runtime rebaseline. Preserve current contracts until replacement and historical evidence at its original inputs.
- CR1 adapts the existing Node Action/official artifact bridge to managed launch. CR2 makes the .NET CLI the sole product entry and the Action a thin composite host with a narrow official SDK Node upload adapter. Follow the owning plan and [CR2 execution contract](../../20_architecture/cr2-cli-action-contract.md) for the single C# owner of product validation/defaults/results, upload/REST separation and preparation-to-launch revalidation; retain boundary protection without mirrored TypeScript product rules. Complete Node removal is not required, and the pure .NET feasibility probe is alternative evidence rather than production qualification. Do not preselect Python, root Action relocation or a state-store rewrite.
- CR1 selects exact-version SolusAgent generic execution and a complete framework-dependent managed .NET 10 DLL publish tree, with a trusted compatible runtime prerequisite. No apphost, AOT, trimming, single-file or self-contained target is selected. Whole-tree loading and actual archive execution need migration proof; current AOT checks remain until replacement.
- The Host owns GitHub facts, state acceptance, deterministic publishing, and side effects.
- Agent orchestration receives only sanitized context and narrow capabilities; it does not receive GitHub/Actions clients or credentials, credential-bearing Host configuration, environment abstractions, generic HTTP clients, arbitrary service resolution, or shell/process capabilities. The provider credential remains encapsulated only in the narrow provider transport.
- GitHub and Actions credential bytes must not enter provider-visible, model-visible, durable, logged, or published channels; verify this with secret-canary and captured-request tests.
- A separate worker process requires an observed fault, resource, extension, or trust need and a new design decision.
- The model may call only explicitly approved read-only repository tools and a side-effect-free terminal review tool.
- The existing bounded tool profile is `read_file`, `search_text`, terminal `finish_review`, `list_changed_files`, `read_diff` and `list_files`. CR1 retains APR domain validation and evidence acceptance over generic execution mechanics.
- APR owns product policy/limits, reviewed identity, tools/evidence acceptance, encrypted storage, lineage/reset and publication. Solus supplies generic loop/provider/tool mechanics and restricted runtime context. Validate shared outcomes and context separately from product acceptance; freshly bind authority and capabilities. Preserve required continuation/prefix semantics with migration evidence, never silent history rewriting.
- Current durable SESSION has project-owned logical message/tool/outcome records plus a separate provider-scoped continuation envelope. CR1 replaces generic context mechanics without transferring APR storage or acceptance authority. Opaque byte/string payloads remain value-exact; structured provider items preserve validated fields, array order, association, and adapter-required placement. This does not prohibit canonical project-owned logical records or require retaining raw transport data.
- Repository, pull-request, diff, file, search-result, and tool-result content always remains untrusted data. Its durable record kind, provider-facing role or framing, tool-call association, and control/data classification are fixed by Host/Agent code, authenticated as session structure, preserved across restoration and provider materialization, and never promoted into policy, system/developer instructions, tool definitions, provider configuration, endpoint selection, secret channels, or other control records.
- The initial supported live review profile requires provider thinking. Preserve provider-returned reasoning text, signed blocks, encrypted items, and thought signatures exactly as bounded restricted session state when the documented API requires or recommends replay; do not request, reconstruct, or infer hidden chain-of-thought.
- Provider continuation state is adapter-owned and provider/model/adapter-bound. It must never appear in logs, normal traces, Action outputs, summaries, annotations, or GitHub publications.
- Restricted state must not become repository-visible plaintext. Its storage class must define visibility, authorization, retention, deletion, fork/untrusted access, and encryption; inadequate transports require workflow-authorized Host-side encryption or omission of readable continuation payloads. Encrypted state must also be authenticated and bound to its format, Host-authoritative identity, provider/adapter scope, session, and required generation/provenance. Any state key remains Host-only, separate from the payload, and is not payload authority.
- Stable policy is loaded from a workflow-authorized configuration at an immutable Host-selected trusted commit SHA, never the reviewed head, model-controlled input, or an unverified mutable ref. The policy bytes and source identity participate in session/cache identity.
- The completed R2 MEAI/project-minimal comparison remains historical evidence, not a reason to preserve the APR-owned generic loop or require AOT support from Solus.
- Version only independently released or durable boundaries. Use canonical content digests and build identity for cache-relevant semantics; do not add duplicate manual version/id families to internal pre-1.0 types.
- CR1 refinement must inventory affected runtime, provider, state, Action, protocol, fixture and distribution families, with replacement/retirement owners. Documentation merge selects the new direction; creating CR milestones and their graphs remains separately scoped follow-up pinned to that merged SHA.
- CR1 detailed refinement comes first. CR2 initially has bounded goals/entry/exit requirements; its detailed graph waits for CR1 evidence. Do not force one refinement parent to equal one implementation PR.
- R2-R4 continuation, transport and publication proofs remain scoped history. New Solus/managed execution needs CR1 evidence; remaining R7 first-candidate preparation, qualification, publication and adoption follow CR1/CR2 and affected contract refinement.
- Preserve completed M/R work and existing R7 identities. Independently useful preparation may continue without superseded assumptions; affected unfinished contracts require re-refinement before execution. Shared paths need explicit handoff.
- Reuse the R2-R4 canary, authenticated-storage, transport and prior-only continuation techniques where applicable, while proving the migrated composition at its actual package/payload identity. An old passing result does not qualify new bytes.
- Start Solus integration with the published `.1` and explicit bounded development limits. Resolve upstream gaps during migration through separately owned immutable releases; verify equality of every resolved direct/transitive Solus version. CR1 accepts practical fresh/restored capacity without requiring every historical ceiling or silently clamping configuration, dropping history, compacting or resetting.
- Preserve selected-current fail-closed behavior and explicit authorized reset on incompatible context/pin changes. No prerelease upgrade guarantee, converter or arbitrary mid-operation recovery is implied. Public redistribution of included Solus binaries requires separate authority before release.
- Before executing a later migration or deletion issue, re-inventory live references, consumers, replacement evidence, safety behavior, and release impact. The architecture document is a lifecycle baseline, not a permanent file-level deletion manifest.
- Do not publish unshipped abandoned code merely to create a final legacy snapshot. Prefer an existing historical release or ordinary Git history unless an identified downstream, restore obligation, or audit requirement needs another immutable pin.
- Build the smallest useful agent vertical slice before expanding protocol, cost, replay, or migration infrastructure.
- Deterministic fixtures should exist before live provider-only validation, and agent milestones require must-find/must-not-find quality evidence rather than schema conformance alone.

## Non-Goals For Early Runtime Work

- hosted GitHub App;
- GitLab or Azure DevOps support;
- Semantic Kernel or Microsoft Agent Framework dependency;
- deep Roslyn/MSBuild semantic analysis;
- dynamic latest-runtime downloads;
- arbitrary shell, write, Git mutation, network, or GitHub tools;
- provider-neutral rewriting or public exposure of reasoning continuation state;
- mid-run checkpoint recovery;
- broad cost-graduation work before representative resumed agent traffic exists;
- a mandatory Host/Agent process split before an isolation need is demonstrated.
