# Runtime Design Refinement

Use this procedure before implementing high-impact runtime or action architecture changes.

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

## Output

Produce a short design decision summary before implementation:

- decision;
- recommended option;
- alternatives considered;
- rationale;
- security/privacy impact;
- migration impact;
- validation plan;
- open questions.

## Current Architectural Defaults

- New runtime design follows `docs/20_architecture/agent-runtime-rebaseline.md`.
- TypeScript converges on a thin Action wrapper and official artifact-toolkit bridge, not a business host.
- The first target uses one Native AOT C# executable with explicit Host, Agent, tool, provider, state, and publisher modules.
- The Host owns GitHub facts, state acceptance, deterministic publishing, and side effects.
- Agent orchestration receives only sanitized context and narrow capabilities; it does not receive GitHub/Actions clients or credentials, credential-bearing Host configuration, environment abstractions, generic HTTP clients, arbitrary service resolution, or shell/process capabilities. The provider credential remains encapsulated only in the narrow provider transport.
- GitHub and Actions credential bytes must not enter provider-visible, model-visible, durable, logged, or published channels; verify this with secret-canary and captured-request tests.
- A separate worker process requires an observed fault, resource, extension, or trust need and a new design decision.
- The model may call only explicitly approved read-only repository tools and a side-effect-free terminal review tool.
- The project owns the agent loop, durable session state, tool ordering, budgets, prefix materialization, and candidate validation.
- Durable state has project-owned logical message/tool/outcome records plus a separate provider-scoped continuation envelope. Opaque byte/string payloads remain value-exact; structured provider items preserve validated fields, array order, association, and adapter-required placement. This does not prohibit canonical project-owned logical records or require retaining raw transport data.
- The initial supported live review profile requires provider thinking. Preserve provider-returned reasoning text, signed blocks, encrypted items, and thought signatures exactly as bounded restricted session state when the documented API requires or recommends replay; do not request, reconstruct, or infer hidden chain-of-thought.
- Provider continuation state is adapter-owned and provider/model/adapter-bound. It must never appear in logs, normal traces, Action outputs, summaries, annotations, or GitHub publications.
- Restricted state must not become repository-visible plaintext. Its storage class must define visibility, authorization, retention, deletion, fork/untrusted access, and encryption; inadequate transports require workflow-authorized Host-side encryption or omission of readable continuation payloads. Encrypted state must also be authenticated and bound to its format, Host-authoritative identity, provider/adapter scope, session, and required generation/provenance. Any state key remains Host-only, separate from the payload, and is not payload authority.
- Stable policy is loaded from a workflow-authorized configuration at an immutable Host-selected trusted commit SHA, never the reviewed head, model-controlled input, or an unverified mutable ref. The policy bytes and source identity participate in session/cache identity.
- R2 compares a pinned `Microsoft.Extensions.AI.Abstractions` dependency with project-minimal exchange types under the same Native AOT proof. Either choice leaves the loop, durable state, validation, and side-effect semantics project-owned.
- Version only independently released or durable boundaries. Use canonical content digests and build identity for cache-relevant semantics; do not add duplicate manual version/id families to internal pre-1.0 types.
- A broad rebaseline design issue records the initial disposition and milestone owner for current runtime, Action, protocol, schema, fixture, and migration families. Creating the issue does not activate the reset; the coordinated documentation PR and named post-merge metadata transition do.
- R0 creates only the R1/R2 parent refinement issues. Their live-tree refinement may split implementation across focused issues or PRs; do not freeze one parent issue as one implementation PR.
- R2/R3 prove continuation across fresh executable invocations. R4 owns the two-run GitHub Actions transport and publication proof through a minimal launcher/Host contract; R7 owns automatic release-asset resolution.
- R1 implementation starts after reset activation and live-tree refinement. R2 may refine in parallel, but implementation waits for R1 completion or an approved non-overlap boundary; shared paths require an explicit ownership handoff.
- R2 secret tests use a fake outbound provider transport that captures the complete URI, headers, and body. R2 defines the storage-conformance contract and proves local authenticated-state behavior; R4 proves the selected production transport and same-process GitHub/Actions Host canaries. R3 proves both request reconstruction and semantic continuation through a high-entropy prior-only synthetic fact.
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
