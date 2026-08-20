# Roadmap

This roadmap records the post-M4 architecture rebaseline for `agentic-pr-review`.

The detailed target architecture is [`docs/20_architecture/agent-runtime-rebaseline.md`](../20_architecture/agent-runtime-rebaseline.md). Existing M1-M4 contract documents remain current implementation references until migration removes their surfaces.

The R0 governance gate and post-merge metadata transition are tracked by [issue #75](https://github.com/SolusQuest/agentic-pr-review/issues/75).

## Long-Term Goal

`agentic-pr-review` should become a GitHub-first, stateful, policy-driven code review agent that produces grounded, low-noise, replayable findings through a deterministic safe publisher while preserving useful and cache-efficient session continuation.

In short:

```text
Stateful PR review agent, not a general coding agent.
```

The project optimizes for:

- review quality;
- grounded evidence;
- bounded read-only tool use;
- completed-session continuation across workflow runs;
- stable cacheable provider prefixes;
- deterministic and safe publishing;
- replay and evaluation;
- operational reliability;
- GitHub-first practicality;
- a small maintainable implementation surface.

## Rebaseline Decision

The previous roadmap intentionally completed protocol, deterministic CLI, cross-language integration, state, ledger, prefix, provider metadata, live-provider, and state-acceptance foundations before repository tool orchestration.

That sequence proved important safety and runtime properties, but it also:

- delayed the first real agent loop;
- treated cross-language contracts as an engineering objective;
- created duplicate TypeScript and C# validators and canonicalization logic;
- preserved migration-only runtime/provider concepts in the public shape;
- started broad cache-cost graduation work before representative agent tool traffic existed.

The new critical path prioritizes the product vertical slice:

1. remove the legacy Claude Code CLI path from the development head and collapse public runtime selection;
2. prove a C# agent loop, tools, session round-trip, Native AOT, and model-visible secret boundary;
3. prove a live thinking-enabled tool-calling review and exact continuation across fresh executable invocations;
4. migrate stable GitHub host responsibilities to `.NET ActionHost`, restore a thin Action, and prove continuation across independent workflow runs;
5. evaluate quality first, then cache economics and release graduation.

Sunk implementation cost does not determine which boundaries remain in the target architecture.

## Selected Engineering Direction

The selected target is:

```text
GitHub Action
  -> thin Node.js wrapper
  -> one .NET Native AOT application
       -> Host with GitHub and publisher authority
       -> in-process Agent with bounded tools and session
       -> provider adapter with provider-only credential
  -> Host validation and deterministic publication
```

C# and Native AOT remain selected commitments.

TypeScript is no longer a business-host commitment. It remains only where the official Actions JavaScript toolkit or payload launcher materially reduces risk and maintenance.

R2 compares a pinned `Microsoft.Extensions.AI.Abstractions` dependency with project-minimal exchange types and selects the smaller Native AOT-proven surface. The project owns the agent loop, tool execution, durable state, provider capability policy, budgets, and side effects either way. Agent/core owns the provider-neutral logical request plan and logical prefix identity; the provider adapter owns provider-specific projection, continuation placement, wire serialization, and any provider-specific cache-relevant identity.

## Non-Goals

The near-term roadmap does not include:

- a general coding or editing agent;
- shell or arbitrary process tools;
- repository writes;
- model-callable GitHub writes;
- untrusted build or test execution;
- a hosted GitHub App;
- multi-platform product expansion before GitHub review quality is proven;
- Microsoft Agent Framework or Semantic Kernel;
- deep Roslyn/MSBuild integration;
- MCP or dynamic plugin discovery;
- public or normal-trace exposure of provider reasoning state;
- mid-run checkpoint recovery;
- broad cost graduation before real resumed agent traffic.

## Current Baseline

The current implementation on `main` includes:

- nested seven-input/no-output Action metadata and a generated Node 24 wrapper for repository-controlled prepared-payload proof, without downstream support, release delivery, or a public default before R7;
- no Claude Code CLI, legacy TypeScript coordinator, or superseded single-request C# product route;
- one project-minimal C# chat seam and bounded multi-turn Agent with six read-only repository tools;
- validated tool arguments/results, canonical SESSION history, grounded structured findings, and must-find/must-not-find quality evaluation;
- the project-owned DeepSeek thinking adapter with exact provider continuation and a protected no-publication official-endpoint proof;
- authenticated encrypted local STATE with same-head/verified-ahead admission and two-fresh-process continuation;
- framework-dependent and Linux x64 Native AOT product validation;
- retained TypeScript state only as named R4 replacement evidence; R4-W3 removed the invocation families and obsolete live-context schema, R4-W5 removed the StateV2 family, schema, opaque sidecar fixture corpora, and generators after mapped C# state/publisher evidence passed, R4-W6 removed the Git-ref state-acceptance family and dedicated schemas after mapped C# state/publisher evidence passed, R4-W7 removed the ledger coordinator, R4-W8 removed the sticky publisher after mapped P1/P2/P5/P6/E1 proof, R4-W9 independently removed the inline/target publisher after mapped H2/H5/P3/P4/P6/E1 proof, R4-W10 removed the duplicate TypeScript review protocol/structured-result family while retaining the embedded Input/Result/Trace schemas and complete C#-owned fixture corpus, R4-W11 removed the prefix family and generator after mapped C# and immutable-corpus evidence passed, and R4-W12 removed the provider-metadata TypeScript surface, schema resource, and dedicated fixture corpus after C# DeepSeek/ActionHost proof. R4-W5 retired StateV2; no current reader or compatibility surface. The W8/W9 checked C# replacement evidence remains current.

R3 is complete at `736508728acc6b2064b75f2e1da81b9342aac70b`. The nested thin wrapper now exists for repository-controlled prepared-payload proof; downstream artifact-backed encrypted state, complete C# publication, supported release payload delivery, and the public default remain later R4/R7 work as allocated below.

## Historical Milestones

M0-M4 remain valuable implementation history:

1. `M0: Validation and roadmap baseline`
2. `M1: Runtime protocol contract`
3. `M2: Deterministic C# runtime CLI`
4. `M3: TypeScript runtime integration`
5. `M4: Cache-efficient runtime provider foundation`

Their completed code and documents are evidence and migration inputs. They do not require the target architecture to preserve every type, schema, language boundary, or public selector.

The old M3-M6 sequencing document is retained as historical implementation context and is explicitly superseded for new sequencing by this roadmap.

### Historical GitHub Milestone Transition

The following is a completed transition record, not current execution instructions. The 2026-07-25 rebaseline was activated by PR #76 and completed through issue #75. Live metadata was rechecked on 2026-08-09:

- PR #74 is closed without merge;
- issue #54 is closed as `not planned`;
- M4 is closed with no open issues;
- R1, R2, and R3 are closed with no open issues;
- R4 is open with refinement parent #138 and no implementation leaves before the R4 docs gate merges.

The earlier plan to create R1/R2 immediately and defer R3-R7 refinement was executed through R3. It no longer instructs agents to create, close, or defer those completed milestones. R6 may reuse ideas or tests from #54 or PR #74 only after real resumed Agent traffic exists and a new issue proves they fit the actual thinking, tool, session, telemetry, and cost model. Existing implementation effort is not a reason to merge an obsolete ordering or contract dependency.

## New Critical Path

### R0: Architecture And Roadmap Rebaseline

Goal: approve the breaking pre-1.0 reset, replace the old roadmap and milestone sequence, and make durable project truth reflect the product-first architecture.

R0 consists of the design issue, the coordinated documentation PR, and the post-merge GitHub metadata transition. It is not a GitHub milestone.

Deliver:

- explicit disposition of M0-M4, issue #54, and PR #74;
- the near-term R1 and R2 milestone and issue plan;
- a coordinated documentation PR that adds the detailed agent runtime architecture;
- concise architecture summary;
- security, distribution, release, and workflow updates;
- supersession notices on old sequencing and contract documents;
- an initial keep/adapt/replace/delete disposition for the current Action, protocol, schema, fixture, state, provider, and TypeScript business families;
- an R1-R7 migration and retirement schedule that names when analysis starts, approximate scope, and completion gates;
- two immediate parent refinement briefs for legacy removal and the C# Agent-loop spike;
- re-refinement requirements for later milestone-specific migration and deletion issues.

Exit criteria:

- the coordinated documentation PR has merged and activated the reset;
- docs distinguish current implementation from selected target;
- PR #74 is closed without merge, issue #54 is closed as `not planned`, and M4 is closed as superseded/incomplete;
- tools, provider abstraction, session semantics, capability boundaries, versioning, legacy removal, and validation are explicit;
- every major current implementation family has an initial lifecycle disposition and milestone owner;
- the R1 and R2 milestones and parent refinement issues exist with stable objectives, entry/exit gates, named migration boundaries, and approved briefs;
- the design issue is ready to close because the post-merge transition is complete;
- later R3-R7 file-level issues remain intentionally uncreated until their entry evidence exists;
- no old roadmap file instructs agents to delay tool orchestration until after broad cost work;
- `npm run check` passes.

### R1: Remove Claude And Collapse Runtime Selection

Goal: remove the legacy live runtime from the development head and stop preserving migration scaffolding as public architecture.

Implementation status: complete. The R1 milestone is closed with no open issues; the development head has no Claude execution path, mixed runtime selector, or public Action surface.

Entry criteria:

- R0 has activated the reset;
- the R1 parent issue has completed its live-tree refinement.

In scope:

- Claude Code CLI installation and version handling;
- Claude-specific environment and model configuration;
- Claude `--resume` sessions;
- Claude stream, usage, and raw-debug parsing;
- legacy runtime/provider branches;
- legacy state compatibility that exists only for Claude;
- the current mixed public Action metadata and generated bundle on the development head;
- migration-only public input combinations;
- public outputs that expose internal candidate, marker, selector, session, ledger, or transition machinery without a real consumer;
- documentation and tests for removed behavior.

Delivered public direction:

- one project-owned runtime;
- provider selection describes the actual provider;
- fake/deterministic providers remain internal test infrastructure;
- old state safely bootstraps;
- the existing `v0.1.0` tag remains the unmaintained historical legacy pin; later unshipped legacy work is not published as a new snapshot;
- the contributor entrypoint `CLAUDE.md` remains because it is not runtime integration.

Exit criteria:

- no live Claude execution path remains on the new head;
- the last published old mixed Action entrypoint remains available through `v0.1.0`, not the development head;
- public configuration no longer combines `legacy`, `deterministic-csharp`, `ledger-csharp`, `runtime_provider`, and `live_provider`;
- normal outputs describe stable review outcomes rather than internal state-machine records;
- migration notes describe state reset and the historical `v0.1.0` pin;
- no GitHub side-effect safety regression;
- applicable local and CI validation pass; the removed legacy bundle is not regenerated as a compatibility artifact.

### R2: Agent-Loop And AI-Abstraction Spike

Goal: answer the hard runtime questions with the smallest executable agent slice.

Implementation status: complete. The focused R2 children supplied the selected project-minimal chat seam, explicit loop/tools, current SESSION, restricted encrypted STATE, and issue #88's two-fresh-process framework and Linux x64 Native AOT product proof. The obsolete two-candidate comparison fixture and test-only MEAI dependency were removed after the replacement passed. R3 subsequently completed the live provider path and quality evidence; R4 owns production GitHub/Actions Host, state transport, publisher, and wrapper migration.

Entry criteria:

- R2 parent refinement may proceed in parallel with R1;
- R2 implementation may begin only after R1 completes or publishes an approved non-overlapping implementation boundary;
- active R1 and R2 work requires an explicit ownership handoff before changing shared files or contracts.

In scope:

- an evidence-backed choice between a pinned `Microsoft.Extensions.AI.Abstractions` dependency and project-minimal exchange types;
- a fake chat client behind the selected narrow abstraction;
- explicit tool schemas and dispatch;
- `read_file` and `search_text`;
- serial project-owned agent loop;
- terminal `finish_review`;
- a tested core/adapter request boundary: core-owned logical request planning and logical prefix identity, followed by adapter-owned provider projection, continuation placement, wire serialization, and any provider-specific cache identity;
- centralized provisional `AgentLimits`;
- session serialize/restore round-trip;
- synthetic readable and opaque provider-continuation state round-trip;
- project-owned logical message/tool/outcome records plus a separate provider-scoped continuation envelope;
- allowed logical and provider-owned record classes;
- immutable untrusted-data classification, durable record kind, provider-facing role or framing, and tool-call association for repository, pull-request, diff, file, search-result, and tool-result content;
- per-record, per-message, per-session, and record-count limits;
- repository/workflow trust scope, retention class, and explicit reset/failure behavior;
- restricted-state storage visibility, authorization, deletion, fork/untrusted access, and encryption requirements;
- a storage-conformance interface and negative matrix for any later production transport;
- local authenticated-encrypted state round-trip, tamper rejection, key separation, cross-scope substitution rejection, and stale-replay rejection;
- a new state namespace and current-format discriminator that cannot select old M4 state as current;
- secret-canary verification across provider-visible, model-visible, durable, logged, and published channels;
- complete URI/header/body capture through a fake outbound provider transport;
- Host-to-Agent dependency-boundary tests;
- Native AOT publish-and-run.

Out of scope:

- GitHub host rewrite;
- live provider secrets;
- the remaining R3 initial live tools beyond the R2 subset;
- parallel tools;
- live provider thinking-quality tuning;
- mid-run checkpoint;
- public state migration;
- cost graduation.

Exit criteria:

- fake provider requests a tool, consumes the result, and returns a terminal review;
- tool arguments and outputs are bounded and validated;
- session history round-trips and reconstructs the same canonical logical prefix;
- the core reconstructs the same provider-neutral logical request plan and logical prefix identity, and the fake adapter reconstructs the expected provider-specific projection without abstraction- or SDK-driven reordering;
- opaque byte or string continuation payloads remain value-exact, while structured provider items retain validated field values, array order, adapter-required placement, hashes, and tool-call association without unrelated raw transport data;
- a fresh executable invocation restores persisted state produced by an earlier invocation;
- automatic state selection rejects old M4 or non-current discriminators through an observable bootstrap, while an explicitly supplied incompatible artifact fails closed;
- missing, altered, oversized, structurally misplaced, or adapter-incompatible continuation state fails closed or follows the explicit bootstrap policy;
- no silent truncation or compaction occurs; provider-owned continuation artifacts are not rewritten into a provider-neutral form or interpreted across providers;
- prompt-injection text inside persisted repository or tool-result content round-trips as data/tool-result content and cannot be promoted into a system, developer, policy, tool-definition, provider-configuration, endpoint-selection, secret-channel, or other control record;
- otherwise authenticated state with inconsistent logical record kind, provider role or framing, tool-call association, or control/data classification is rejected before Agent or provider admission;
- plaintext reasoning, repository tool results, and provider continuation material cannot enter Git objects, repository-visible refs, caches, or normal public artifacts;
- the storage-conformance interface defines mandatory enumeration, restore, overwrite, decrypt, deletion, and publication denials for fork-origin and untrusted workflows without claiming a production workflow transport exists in R2;
- authenticated local state rejects tampering, identity mismatch, cross-scope substitution, stale replay, and decryption failure before Agent/provider admission;
- no GitHub or Actions credential reaches provider-visible, model-visible, durable, logged, or published bytes;
- the provider credential canary appears only in the intended provider authorization channel;
- when state encryption is selected, its key canary appears only at the Host cryptographic boundary and never in state bytes or Agent/model/provider-visible channels;
- Agent orchestration receives no GitHub/Actions client or credential, credential-bearing Host configuration, environment abstraction, generic HTTP client, service locator, or shell/process capability;
- the published Native AOT application executes the fixture;
- no reflection-based project DTO serialization is required.

### R3: Minimal Live Review Agent

Goal: prove a real provider can use bounded repository tools and continue a completed review session in a fresh executable invocation.

Implementation status: complete. Issue [#109](https://github.com/SolusQuest/agentic-pr-review/issues/109) supplied the deterministic concrete Host boundary, and issues [#111](https://github.com/SolusQuest/agentic-pr-review/issues/111) and [#112](https://github.com/SolusQuest/agentic-pr-review/issues/112) established the real Agent transport plus the full six-tool loop, exact continuation, encrypted local STATE, quality corpus, negative/canary matrices, and two fresh runtime processes in framework-dependent and Linux x64 Native AOT modes. Issue [#113](https://github.com/SolusQuest/agentic-pr-review/issues/113) retired the superseded single-request C# path behind an exact route/composition oracle and checked R4 handoff. Issue [#114](https://github.com/SolusQuest/agentic-pr-review/issues/114) added the sole protected no-publish DeepSeek proof route for an exact merged default-branch artifact; it did not restore a public Action or production Host. Production GitHub/Actions Host work and public Action restoration belong to R4; release payloads remain R7.

Initial live tools, consisting of the R2 subset plus the three R3 additions:

- `list_changed_files`;
- `read_diff`;
- `list_files`;
- `search_text`;
- `read_file`;
- `finish_review`.

In scope:

- project-owned DeepSeek adapter behind the selected narrow chat-client abstraction;
- thinking-mode tool calls;
- value-exact provider-returned opaque continuation payloads and validated structured continuation field values, ordering, and adapter-required placement through tool loops and fresh-process restore;
- provider capability checks;
- serial tool execution;
- model/tool/token/byte/time limits;
- cancellation and failure taxonomy;
- candidate session state containing exact bounded tool results;
- structured finding evidence references;
- trusted no-publish live workflow;
- quality fixtures.

Out of scope:

- shell, writes, tests, builds, network tools, MCP;
- a non-thinking fallback or production default;
- parallel tools;
- mid-run checkpoints;
- broad provider matrix;
- default live promotion.

Exit criteria:

- one trusted proof fixture withholds relevant repository content from the initial model context, the provider requests the required repository tool, and the final finding binds its path and location to the returned observation;
- that proof fails if the tool is not called or the finding is not grounded in the admitted tool result;
- the first invocation establishes a high-entropy synthetic continuation fact absent from the second invocation's fresh input, repository tool results, and policy;
- the second fresh process uses that prior-only fact in a validated structured response;
- capture of the second outbound request proves prior logical records, value-exact opaque payloads, and validated structured provider fields are present in their adapter-required positions;
- thinking is mandatory for the initial supported live profile;
- invalid tools, arguments, paths, symlinks, encodings, and sizes fail closed;
- compatible state reconstructs the intended stable prefix;
- must-find and must-not-find cases pass;
- no model-controlled GitHub side effect exists.

### R4: .NET ActionHost And Thin Node Wrapper

Goal: converge on one C# business implementation and remove cross-language duplication.

Planning status: milestone [R4](https://github.com/SolusQuest/agentic-pr-review/milestone/10) and refinement parent [#138](https://github.com/SolusQuest/agentic-pr-review/issues/138) own the design gate. The prospective decision-complete contract is [`r4-actionhost-wrapper-plan.md`](../20_architecture/r4-actionhost-wrapper-plan.md). No implementation issue is published until that docs contract is merged and remotely activated.

In scope:

- C# trusted `workflow_run`/`workflow_dispatch` event and input interpretation;
- C# GitHub REST reads and writes;
- PR, diff, and tracked-file snapshots;
- C# state selection, provenance, immutable candidate/acceptance records, conflict-safe lineage advancement, and stale-writer protection;
- one C# executable with explicit Host, Agent, tool, provider, state, and publisher modules;
- separate GitHub and provider HTTP clients, handlers, endpoints, and credentials;
- candidate result and state validation;
- C# fingerprinting, duplicate suppression, line mapping, sticky/inline publisher;
- thin Node.js wrapper;
- a small direct-input allowlist and one workflow-authorized configuration resolved at an immutable Host-selected trusted commit SHA;
- no stable public outputs until a named consumer requires one;
- official JavaScript artifact bridge where retained;
- deletion of duplicate TypeScript business modules, review-domain cross-language protocols, duplicate validators, and parity-only fixtures;
- R4-W8 removed the TypeScript sticky publisher and its tests after checked P1/P2/P5/P6 plus E1 replacement proof; R4-W9 independently removed the inline/target paths after checked H2/H5/P3/P4/P6/E1 proof, and both leaves retain independently owned C# replacement evidence;
- one minimal lockstep launcher/Host contract for cancellation, bounded outcome/output handoff, exit status, and executable build identity;
- a two-run trusted-workflow proof using an explicitly prepared trusted payload path;
- a downstream-repository-owned encrypted GitHub Actions artifact adapter satisfying the scope-bound state class while allowing public metadata/ciphertext visibility;
- one asynchronous internal opaque-snapshot state-store seam and conformance suite, with Supabase and any public extension protocol deferred.

Exit criteria:

- wrapper owns only the responsibilities listed in the architecture;
- configuration and instructions are resolved from an immutable Host-selected trusted commit, normally on the default-branch lineage, never the reviewed head, model-controlled input, or an unverified mutable ref;
- selected policy bytes and trusted source identity participate in the session/cache identity;
- every public input and output has a named external consumer and no migration-only selector is exposed;
- Agent inputs contain no GitHub or Actions credential-bearing object;
- provider request capture and secret-canary tests pass;
- restricted state cannot become repository-visible plaintext; public artifact metadata and ciphertext visibility are allowed, while fork/untrusted workflows cannot obtain the key, decrypt or admit SESSION, mutate or accept trusted lineage, invoke the provider through the trusted route, or publish;
- authorization rejection occurs before state decryption, provider construction, provider network activity, or publication;
- an integrated Action canary using GitHub/Actions-shaped Host credentials proves those values are absent from provider requests, model-visible content, tools, state, diagnostics, logs, outputs, summaries, annotations, and child environments;
- invalid Agent output cannot reach GitHub writes;
- both supported credential-bearing routes use the same verified authoritative repository/PR workflow concurrency group, while exact-head barriers, immutable receipts, unique-successor selection, and explicit conflict still fail closed on stale or externally introduced records;
- sticky, inline, duplicate, artifact, cancellation, and partial-failure behavior have end-to-end coverage;
- two independent GitHub Actions workflow runs prove bootstrap/select, continuation, state acceptance, stale-writer protection, and deterministic publication;
- TypeScript canonicalization, prefix, provider metadata, state-v2, state-acceptance, and runtime business modules are removed or have a named final deletion issue;
- automatic release-asset download and default payload resolution remain deferred to R7.

### R5: Quality Evaluation, Replay, And Session Growth

Goal: make agent quality and completed-session behavior measurable before default promotion.

In scope:

- must-find and must-not-find corpus;
- tool-required review cases;
- false-positive and duplicate analysis;
- evidence correctness;
- incremental review correctness;
- completed-session replay;
- synthetic, redacted, or explicitly public-safe checked-in replay fixtures; real restricted continuation state is never copied into repository fixtures or normal replay reports;
- provider and tool failure classification;
- state size and growth observation;
- explicit session reset behavior;
- no-publish shadow validation if a meaningful baseline still exists.

Exit criteria:

- reports distinguish quality failures from provider, host, tool, and evaluation infrastructure failures;
- replay does not require GitHub credentials or live GitHub state;
- evidence references remain verifiable;
- state growth stays within documented bounds on representative PR histories;
- no silent summarization or compaction occurs.

### R6: Cache Economics And Cost Evaluation

Goal: evaluate the actual resumed agent request shape rather than a single-shot proxy.

In scope:

- prefix hash continuity across real tool-bearing sessions;
- cache-read, cache-write, uncached-input, and output usage when exposed;
- normalized input cost;
- representative stateless comparison where the provider supports a real stateless/cache-disabled mode;
- provider-specific inconclusive classification;
- simplified graduation rules driven by observed product behavior.

Exit criteria:

- cost fixtures use the same logical message and tool history as the live agent;
- missing telemetry remains unknown rather than fabricated;
- quality and safety pass independently of cache hits;
- sustained prefix instability blocks promotion;
- cost regression is evaluated only on comparable complete runs.

A new R6 issue must be refined from real Agent evidence before cost-harness implementation resumes. After their R0 supersession, issue #54 and unmerged PR #74 remain historical inputs only; do not reopen or move their existing design unchanged into R6.

### R7: Distribution And Default Graduation

Goal: publish a pinned, verifiable runtime and make an evidence-based default decision.

In scope:

- thin bundled Node.js wrapper;
- one `linux-x64` Native AOT C# application;
- payload manifest and checksums;
- exact automatic Action/payload version mapping and release-asset download;
- compatibility and state-reset notes;
- default provider/runtime graduation thresholds;
- additional platforms only when demanded.

Exit criteria:

- downstream runner needs no .NET SDK;
- checksum mismatch fails closed;
- no implicit `latest` download;
- published payload executes Host, Agent, fake-provider, tool, and publisher smoke paths;
- quality, safety, resumability, operational, and cache-economics gates pass;
- the public Action inputs describe the project-owned architecture rather than migration scaffolding.

## Issue And Milestone Policy

- Do not mechanically reuse old M5/M6 placeholders.
- The historical R1/R2 parent-creation gate is complete; do not replay it as current work.
- Do not create an R0 GitHub milestone.
- The two parent issues may split into multiple focused implementation issues or PRs after live-tree refinement; do not assume one parent equals one PR.
- Do not create R4 implementation or deletion issues until the R4 docs contract merges; apply the same evidence-before-refinement rule to R5-R7.
- At each milestone entry, re-inventory live references and refine that milestone's migration and retirement issues before implementation.
- Each issue must identify its target owner: wrapper, C# Host, Agent, tools, provider adapter, state, publisher, migration deletion, or validation.
- Transitional code requires a named removal gate.
- An existing test reference is not sufficient justification to retain an obsolete contract; identify the surviving behavior or delete the test with the retired surface.
- Do not create a schema/version issue unless the data crosses an independently released or durable compatibility boundary.
- Do not create a cost or replay issue whose inputs do not yet exist in the live agent path.
- Do not close an agent milestone on schema conformance alone.
- Every product milestone must improve or validate a real end-to-end review scenario.
- Do not introduce a compatibility matrix without an identified consumer.
- Prefer canonical content digests over duplicate manual version and id fields.
- Keep at most one product execution path on the new development head.
- Evaluate review quality and evidence correctness before cache economics.
- Remove peripheral work from the critical path when it does not unlock the next real agent capability.

GitHub milestone and issue metadata are updated separately after the coordinated documentation PR activates the reset. This document does not claim that repository metadata already reflects the rebaseline.

## Validation Priorities

Priority order:

1. secret and side-effect boundaries;
2. tool path and output safety;
3. real agent behavior;
4. review quality and grounded evidence;
5. completed-session continuation;
6. stale-writer and state correctness;
7. operational reliability;
8. cache economics;
9. broader distribution and provider support.

Contract precision supports these outcomes; it is not a substitute for them.

## Deferred Productization

Only after the default GitHub Action path is proven should the project consider:

- additional provider adapters;
- additional operating-system payloads;
- optional language-intelligence tools;
- controlled build or test tools;
- organization policy;
- GitHub App hosting;
- GitLab or Azure DevOps;
- analytics or dashboard surfaces.
