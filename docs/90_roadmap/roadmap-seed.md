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

1. remove the unshipped Claude Code CLI path and collapse public runtime selection;
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

R2 compares a pinned `Microsoft.Extensions.AI.Abstractions` dependency with project-minimal exchange types and selects the smaller Native AOT-proven surface. The project owns the agent loop, tool execution, durable state, prefix, provider capability policy, budgets, and side effects either way.

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

- TypeScript Action host and deterministic publisher;
- Claude Code CLI live provider path;
- deterministic and ledger-oriented C# runtime paths;
- project-owned DeepSeek live provider adapter;
- structured findings, sticky comments, optional inline comments, and duplicate suppression;
- durable state artifacts, selection, acceptance, and stale-writer safeguards;
- canonical ledger and prefix foundations;
- provider metadata and usage normalization;
- framework-dependent and Native AOT CI;
- extensive cross-language contract fixtures.

The current DeepSeek path is a bounded single provider request that returns a structured review. The durable ledger records review context and outcome, not a full provider/tool conversation. The following core product behaviors are still missing:

- provider-requested tools;
- a multi-turn agent loop;
- validated tool arguments;
- bounded tool execution;
- tool results returned to the provider;
- durable agent history containing bounded tool results;
- structured evidence binding from findings to observed content;
- agent-quality must-find and must-not-find evaluation.

## Historical Milestones

M0-M4 remain valuable implementation history:

1. `M0: Validation and roadmap baseline`
2. `M1: Runtime protocol contract`
3. `M2: Deterministic C# runtime CLI`
4. `M3: TypeScript runtime integration`
5. `M4: Cache-efficient runtime provider foundation`

Their completed code and documents are evidence and migration inputs. They do not require the target architecture to preserve every type, schema, language boundary, or public selector.

The old M3-M6 sequencing document is retained as historical implementation context and is explicitly superseded for new sequencing by this roadmap.

### GitHub Milestone Transition

As of 2026-07-25:

- M0, M1, M2, and M3 are closed with no open issues;
- M4 remains open with eight closed issues and one open issue, #54;
- issue #54 and draft PR #74 implement a TypeScript-only cost-evaluation harness bound to the pre-agent M4 ledger, prefix, provider-metadata, and graduation model.

This table is a point-in-time planning snapshot and must be rechecked against live GitHub state before any mutation.

Creating the design issue does not activate the reset. The reset becomes active only when a maintainer merges the coordinated documentation PR that references, but does not close, that issue. The maintainer then:

1. closes PR #74 without merging, with an explicit superseded-by-roadmap comment;
2. closes issue #54 as `not planned`, because its existing design is superseded rather than migrated;
3. closes M4 as superseded/incomplete after it has no open issue;
4. creates only the near-term R1 and R2 GitHub milestones and their parent refinement issues;
5. closes the design issue after those transition actions are complete;
6. keeps R3-R7 roadmap-only until the preceding phase produces enough evidence to refine their issue and milestone boundaries.

R6 may reuse ideas or tests from #54 or PR #74 only after real resumed Agent traffic exists and a new issue proves they fit the actual thinking, tool, session, telemetry, and cost model. Existing implementation effort is not a reason to merge an obsolete ordering or contract dependency.

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

Goal: remove the unshipped legacy live runtime and stop preserving migration scaffolding as public architecture.

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

Target public direction:

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
- centralized provisional `AgentLimits`;
- session serialize/restore round-trip;
- synthetic readable and opaque provider-continuation state round-trip;
- project-owned logical message/tool/outcome records plus a separate provider-scoped continuation envelope;
- allowed logical and provider-owned record classes;
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
- full tool set;
- parallel tools;
- live provider thinking-quality tuning;
- mid-run checkpoint;
- public state migration;
- cost graduation.

Exit criteria:

- fake provider requests a tool, consumes the result, and returns a terminal review;
- tool arguments and outputs are bounded and validated;
- session history round-trips and reconstructs the same canonical logical prefix;
- opaque byte or string continuation payloads remain value-exact, while structured provider items retain validated field values, array order, adapter-required placement, hashes, and tool-call association without unrelated raw transport data;
- a fresh executable invocation restores persisted state produced by an earlier invocation;
- automatic state selection rejects old M4 or non-current discriminators through an observable bootstrap, while an explicitly supplied incompatible artifact fails closed;
- missing, altered, oversized, structurally misplaced, or adapter-incompatible continuation state fails closed or follows the explicit bootstrap policy;
- no silent truncation or compaction occurs; provider-owned continuation artifacts are not rewritten into a provider-neutral form or interpreted across providers;
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

Initial tools:

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

In scope:

- C# GitHub event and input interpretation;
- C# GitHub REST reads and writes;
- PR, diff, and tracked-file snapshots;
- C# state selection, provenance, acceptance, and stale-writer protection;
- one C# executable with explicit Host, Agent, tool, provider, state, and publisher modules;
- separate GitHub and provider HTTP clients, handlers, endpoints, and credentials;
- candidate result and state validation;
- C# fingerprinting, duplicate suppression, line mapping, sticky/inline publisher;
- thin Node.js wrapper;
- a small direct-input allowlist and one workflow-authorized configuration resolved at an immutable Host-selected trusted commit SHA;
- a small stable-output allowlist;
- official JavaScript artifact bridge where retained;
- deletion of duplicate TypeScript business modules, review-domain cross-language protocols, duplicate validators, and parity-only fixtures;
- one minimal lockstep launcher/Host contract for cancellation, bounded outcome/output handoff, exit status, and executable build identity;
- a two-run trusted-workflow proof using an explicitly prepared trusted payload path;
- a production continuation transport satisfying the authenticated, scope-bound restricted storage class and fork/untrusted denial.

Exit criteria:

- wrapper owns only the responsibilities listed in the architecture;
- configuration and instructions are resolved from an immutable Host-selected trusted commit, normally on the default-branch lineage, never the reviewed head, model-controlled input, or an unverified mutable ref;
- selected policy bytes and trusted source identity participate in the session/cache identity;
- every public input and output has a named external consumer and no migration-only selector is exposed;
- Agent inputs contain no GitHub or Actions credential-bearing object;
- provider request capture and secret-canary tests pass;
- restricted state cannot become repository-visible plaintext, and the selected production transport prevents fork/untrusted workflows from enumerating, restoring, overwriting, decrypting, deleting, or publishing it;
- authorization rejection occurs before state decryption, provider construction, provider network activity, or publication;
- an integrated Action canary using GitHub/Actions-shaped Host credentials proves those values are absent from provider requests, model-visible content, tools, state, diagnostics, logs, outputs, summaries, annotations, and child environments;
- invalid Agent output cannot reach GitHub writes;
- stale head and concurrent writer cases fail closed;
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
- Create the R1 legacy-removal and R2 Agent-loop parent refinement issues only after the coordinated R0 docs PR activates the reset.
- Do not create an R0 GitHub milestone.
- The two parent issues may split into multiple focused implementation issues or PRs after live-tree refinement; do not assume one parent equals one PR.
- Do not create detailed R3-R7 implementation or deletion issues until the preceding phase provides their entry evidence.
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
