# CR1 / CR2: Shared Agent Runtime And CLI-First Action

## Authority And Current Position

This records the direction approved in [Task #390](https://github.com/SolusQuest/agentic-pr-review/issues/390). Its documentation merge activates the selected direction for new design and refinement. It implements no migration, creates no milestone or execution graph, and authorizes no release, deployment, live-provider use or closeout.

**CR** means **Change Request**: a product-level change batch with an outcome and acceptance boundary. Identifiers increase by initiation order; dependencies determine execution order. Use **CR1: SolusAgent migration and managed runtime** and **CR2: CLI-first Action integration**. Retain historical M/R identifiers and the existing R7 milestone and issue identities.

This document governs the changed runtime ownership, managed distribution, Action direction and sequencing. It supersedes conflicting future-target assumptions in the [runtime rebaseline](../20_architecture/agent-runtime-rebaseline.md), [roadmap](roadmap-seed.md) and [R7 plan](r7-plan.md). Current implementation contracts still govern operating and validating code until replacement lands. Historical evidence remains limited to its original revision and conditions.

| Surface        | Current implementation                                                                                                          | Selected change                                                                                               |
| -------------- | ------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------- |
| Agent          | APR-owned C# loop, chat seam, DeepSeek adapter and SESSION                                                                      | CR1 consumes exact-version SolusAgent packages for generic execution and runtime context                      |
| Payload        | Native AOT packaging and inherited-descriptor launcher                                                                          | CR1 uses a framework-dependent managed .NET 10 DLL archive and complete publish tree                          |
| Action         | Nested Node 24 entry, official artifact bridge, seven inputs and fourteen outputs                                               | CR1 adapts managed launch; CR2 makes the .NET CLI the sole product entry and the Action a thin composite host |
| Release        | Ordinary exact-map resolution exists, but the checked map is null; no experimental release or supported downstream installation | Remaining R7 first-candidate work follows CR1/CR2 and affected contract refinement                            |
| Solus evidence | Five private `0.1.0-internal.1` packages and APR repository-token installation proof                                            | Installation enables integration; it does not establish migrated APR behavior or capacity                     |

## CR1: SolusAgent Migration And Managed Runtime

### Outcome And Ownership

Replace APR's generic agent execution with SolusAgent while preserving APR's review product. Keep one managed C# application with explicit capability boundaries; no worker split, second provider or platform expansion is selected.

| APR retains                                                               | SolusAgent supplies                                                  |
| ------------------------------------------------------------------------- | -------------------------------------------------------------------- |
| Immutable reviewed PR/snapshot identity and trusted policy                | Generic agent execution and runtime bookkeeping                      |
| Review-specific bounded tools, evidence rules and findings acceptance     | Generic provider/tool execution mechanics                            |
| Product limits, incomplete-outcome handling and acceptance decisions      | Runtime enforcement and its restricted runtime context               |
| Encrypted storage, provenance, lineage, reset and current-state selection | Context capture/admission mechanics under freshly supplied authority |
| GitHub/Actions credentials, deterministic publication and side effects    | Provider transport with a separately confined provider credential    |

APR must validate shared-runtime outcomes and tool results against its reviewed snapshot and product policy. A valid runtime context or successful runtime result is not permission to publish or accept successor product state. Preserve stale-head checks, incomplete-result barriers, authorization before decryption/provider work, and deterministic Host-owned writes.

The runtime receives only sanitized input and narrow capabilities. GitHub/Actions credentials, state keys, credential-bearing Host configuration, ambient environment/service access and arbitrary process capabilities do not become Agent inputs. Repository and tool content remains untrusted through restoration and provider reconstruction. Restricted reasoning, tool results and continuation material remain authenticated encrypted state; they cannot enter normal logs, outputs, summaries or publications. The [security boundary](../20_architecture/security-boundary.md) continues to apply.

### Package And Capacity Feedback

Begin bounded integration with the published `0.1.0-internal.1`. Pin exact equality and verify that every resolved direct and transitive Solus dependency uses the selected version. Keep restore credentials in the build/restore boundary; downstream execution must not need private-feed credentials. Published identities are immutable: upstream corrections require new internal versions and explicit consumer pin changes, never replacement bytes or an implicit latest version.

Before exercising the migrated route, define an explicit development profile that the selected package can admit. During integration, identify concrete semantic/capacity gaps early, coordinate separately owned upstream corrections and publication, then revalidate the new exact pin. Do not defer all upstream work until milestone closeout. Final practical capacity acceptance belongs to CR1; `.1` is a starting dependency, not a permanent product ceiling or a claim of equivalence.

The dimensions are different: `.1` caps retained attempts and records at 64 each, cumulatively across restored rounds, while APR currently has 64 default/128 maximum logical calls per review. Retries, classified inputs and tool-result records consume different inventories. Provider wire bytes, retained-context bytes, messages, model tokens and current-round allowances must be assessed separately. Equal numbers do not establish equivalent review capacity.

CR1 must demonstrate a useful fresh review and subsequent review from compatible saved context, including realistic tool/history growth and visible exhaustion/refusal. It need not preserve every historical maximum, but changes to admitted configuration require an explicit reviewed product contract. Never silently clamp selected limits, discard history, truncate/compact context or reset on exhaustion. Unsupported profiles fail visibly before the affected work; incomplete execution cannot masquerade as a clean review.

### Context And Cutover

APR chooses and authenticates saved state, binds it to the reviewed scope and lineage, and controls acceptance/reset. Solus owns its runtime context representation. Reinject current providers, tools, credentials and authority; serialized bytes are not authority. Context admission, work outcome, context capture and durable product-state acceptance are distinct checks.

Use compatible completed context for new Host-authorized review work; unfinished continuation has different admission semantics. This migration adds no arbitrary-kill or mid-operation recovery guarantee and cannot replay uncertain effects. Existing bounded Host publication/transport recovery contracts remain at their current scope.

Solus supplies no prerelease API, behavior or saved-context upgrade guarantee. CR1 must define the affected state/pin transition before admitting restored state. Preserve selected-current fail-closed behavior and explicit authorized reset; no automatic conversion, discriminator rewriting, truncation or silent fresh fallback. Retaining a compatible pin/context pair is different from promising cross-version support. Do not add converters or parallel readers without a demonstrated consumer need.

### Managed Distribution

Adopt ContractScribe's framework-dependent DLL archive model: `dotnet <dll>`, no apphost, and the complete publish tree, including `.deps.json`, `.runtimeconfig.json`, product/dependency assemblies and required resources/native assets. Disable Native AOT, trimming, single-file and self-contained publishing for the selected target. APR execution requires a trusted compatible .NET 10 runtime; building/restoring needs the SDK. ContractScribe's operational SDK/MSBuild requirement does not transfer to APR.

The initial target remains GitHub-hosted `ubuntu-24.04` x64. CR1 owns runtime prerequisite handling, exact archive/inventory verification, safe extraction, trusted runtime selection and loading, dependency resolution and environment controls. Neither an ambient reviewed-workspace DLL nor an injected probing/startup path may replace verified code. Existing single-executable descriptor proof does not qualify the managed loading closure. Representative execution from the actual archive is required; successful publish alone is insufficient.

CR1 adapts the existing nested Action and retains its official Node artifact bridge. CLI/Action contract consolidation belongs to CR2. Preserve exact release identities and immutable original candidate bytes; re-refine affected R7 package/map/qualification contracts before new candidate execution. Internal Solus consumption permission does not grant public redistribution rights: the relevant maintainer/rights owner must authorize the actual included binaries before a public APR payload release.

### Entry And Exit

Entry requires the merged design revision, a refined CR1 contract/graph, access to the selected published packages and a bounded development profile before migrated execution. Package installation proof satisfies only the dependency-access part.

CR1 exits when:

- APR actually executes through the pinned Solus runtime/provider with the ownership and security boundaries above;
- fresh and restored reviews, evidence acceptance, incomplete/failure/cancellation paths and meaningful capacity pass the selected acceptance profile;
- state/context and incompatible-pin behavior are explicit and tested without silent fallback or new recovery promises;
- the managed archive and existing Action execute on the selected runner with full-tree integrity/loading and runtime-prerequisite proof;
- superseded loop/provider/AOT production paths have explicit replacement and retirement evidence; historical fixtures are retained or retired by named disposition;
- actual package versions, resolved gaps, remaining limitations and CR2 handoff evidence are recorded.

These are future acceptance requirements, not results supplied by this documentation change. Live quality proof, if required by refinement, needs separately authorized content and finite calls/time/cost.

## CR2: CLI-First Action Integration

CR2 starts detailed refinement from CR1's actual runtime, payload and integration evidence. Its outcome is one .NET CLI product entry owning input interpretation, configuration, review/state/publication behavior, outcomes and accounting. The thin composite Action performs acquisition/prerequisite/invocation and Actions presentation glue. Keep product semantics in one implementation.

The official artifact bridge requires an explicit retain/reduce/replace decision with equivalent transport, authorization, cancellation and failure evidence before retiring Node responsibilities. A retained narrow bridge is not a second product entry. Do not infer a state-transport rewrite, root Action relocation, Python requirement or complete removal of Node from ContractScribe's precedent.

CR2 exits with the CLI and Action contracts documented, duplicated product semantics removed, Action/CLI behavior and failure/output handling verified, artifact transport disposition validated, and exact distribution/qualification handoff supplied to R7. Detailed implementation issues wait for CR1 evidence; this brief selects neither new public CLI flags nor an artifact implementation.

## R7 Continuation And Follow-Up

The execution dependency is **CR1 → CR2 → remaining R7 first-candidate preparation, qualification, publication and adoption**. Independently useful preparation may continue only where it does not rely on superseded assumptions. Affected unfinished implementation contracts must be re-refined before execution; old AOT or permanent Node-wrapper assumptions cannot authorize a new candidate.

Preserve completed R7 leaves and their exact evidence, existing issue/milestone identities, and the unaffected `S` source, `W` builder, original `B` bytes and reviewed Action `T` release model. Reuse evidence only where its tested conditions still apply; old AOT or APR-owned-loop proof does not qualify new managed/Solus bytes. R7's separate binding, staging, publication, deployment and closeout authorities remain. No formal public/default graduation is implied.

After this documentation merges:

1. Under separately scoped metadata authority, create the two named milestones and refinement parents without renumbering R7.
2. Pin the exact merged design SHA and use Relay `milestone-to-issue-graph` for CR1's live-tree refinement and dependency graph.
3. Record affected open R7 contracts and re-refine their target assumptions under that follow-up's authority, preserving completed work.
4. Refine CR2's detailed graph when CR1 supplies the required evidence; then reconcile remaining R7 release execution with the resulting product.

This Task creates only its documentation issue and PR. It does not publish either CR graph or alter existing R7 metadata.

## Evidence References

- [APR baseline and package-install workflow](https://github.com/SolusQuest/agentic-pr-review/blob/873296314bd01ee89706255b1a9a4fd09b1a7579/.github/workflows/solus-agent-package-proof.yml): installation of Solus consumer fixtures, not migrated APR acceptance.
- [Solus package consumption](https://github.com/SolusQuest/solus-agent/blob/642e6cd9dcdc2faa1297e82b5b852887f5bf7dff/docs/10_workflow/package-consumption.md) and [published `.1` runtime limits](https://github.com/SolusQuest/solus-agent/blob/076bcda2fbe1da2677ac5c6fd03d75bc0fef66ad/src/SolusAgent.Runtime/Startup/RuntimeOptions.cs): exact versions, context admission, managed support and internal-use boundary.
- [ContractScribe DLL archive decision](https://github.com/SolusQuest/contract-scribe/blob/81ff788fc8f70b8d8ad9c908a40c965cb7adb0a1/docs/20_architecture/decisions/0005-d2-dll-payload-archive.md) and [composite host decision](https://github.com/SolusQuest/contract-scribe/blob/81ff788fc8f70b8d8ad9c908a40c965cb7adb0a1/docs/20_architecture/decisions/0006-composite-action-host.md): distribution precedents, not APR product or release qualification.
