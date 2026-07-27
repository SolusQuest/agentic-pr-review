# AI Abstraction Decision

Status: selected and implemented in PR #85; pending merge.

Decision date: 2026-07-27.

Tracking: [issue #81](https://github.com/SolusQuest/agentic-pr-review/issues/81), parent [issue #78](https://github.com/SolusQuest/agentic-pr-review/issues/78), and [PR #85](https://github.com/SolusQuest/agentic-pr-review/pull/85).

## Decision

The production Agent boundary uses project-minimal in-memory exchange types. The selected non-streaming capability and its private mapping types live under `runtime/src/AgenticPrReview.Runtime/Agent/Chat/**`.

`Microsoft.Extensions.AI.Abstractions` is not a production dependency. Version 10.8.1 remains in the isolated comparison fixture only so the decision can be reproduced until the replacement gate below is satisfied.

This choice does not assign the Agent loop, tool dispatch, durable state, provider projection, budgets, terminal semantics, or side effects to the abstraction. Those concerns remain project-owned.

## Traceability

| Record                                            | Exact value                                                                                                                          |
| ------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------ |
| Implementation-start `main`                       | `812c98be3a298de1b449759a94e7facb20a52ad5`                                                                                           |
| R1 completion                                     | issue #80, PR #83, merge `8603a5c6acc77b55f7790ed6a17b56edbad0bc67`                                                                  |
| Neutral comparison evidence commit                | `405e0468fc15dc932970b378081ae030028409fe`                                                                                           |
| Selection implementation commit                   | `337bec8000746cd67cb37ca1784732839662b1d9`                                                                                           |
| Neutral Ubuntu Runtime CI                         | [run 30271101423, runtime job 89993553987](https://github.com/SolusQuest/agentic-pr-review/actions/runs/30271101423/job/89993553987) |
| Selection and production-parity Ubuntu Runtime CI | [run 30271957701, runtime job 89996440677](https://github.com/SolusQuest/agentic-pr-review/actions/runs/30271957701/job/89996440677) |
| SDK                                               | .NET SDK `10.0.109`, pinned by `global.json`                                                                                         |
| AOT target                                        | `linux-x64`, self-contained Native AOT                                                                                               |
| Runner                                            | `ubuntu-24.04`, runner-image version `20260720.247.2`, runner version `2.336.0`                                                      |

The selection implementation commit is the decision commit: it introduces the selected production seam and activates the selected-production parity gate. This document is a later documentation-only record and therefore does not attempt to embed its own commit hash.

## Common Proof

Both candidates compile the same shared runner and immutable synthetic input bytes. The comparison project has no reference to the production runtime project or its existing package graph. Each candidate and mode receives an isolated restore package root, MSBuild extensions root, intermediate root, output root, publish root, and execution root.

The first fresh process:

1. constructs fixed project-owned instructions, ordered closed-schema tools, a logical request, and a prefix identity;
2. receives assistant text, readable and opaque reasoning continuation, explicit placement and association, and a `read_file` call;
3. validates and serially dispatches bounded in-memory `read_file` and `search_text` tools;
4. accepts only a valid `finish_review` terminal call;
5. commits canonical public-safe evidence before committing source-generated proof state as the success marker.

The second fresh process:

1. reads and validates the proof state;
2. verifies candidate, provider, model, adapter, session, continuation hashes, framing, position, and tool-result association;
3. reconstructs the prior logical records and provider-facing request;
4. proves that a fact available only in the first process participated in the restored request;
5. reaches a later terminal review and emits first, second, and combined evidence.

The Minimal and Microsoft.Extensions.AI candidates produce byte-identical canonical evidence. Candidate identity and implementation metrics are kept outside that equality surface.

Both framework and Native AOT modes execute the same negative matrix. It rejects invalid compile-time candidate selection; unknown or malformed tools and arguments; oversized tool results; ordering defects; missing, altered, oversized, misplaced, misframed, misassociated, or wrong-role continuation; incorrect tool-result association; restore-binding mismatches; pending-call cancellation; candidate-object proof-state admission; and evidence/state commit faults. A successful proof-state transition is absent on every negative path.

Nine synthetic canary categories cover GitHub, Actions, provider, state cryptography, runner, package registry, cloud, signing, and unrelated workflow values. Candidate-visible messages, tools, proof state, evidence, logs, stdout, and stderr are scanned and contain none of them.

## Candidate A: Project-Minimal Exchange

### Surface

The Agent-facing interface has one method:

```text
Task<ProjectChatResponse> GetResponseAsync(
    ProjectChatRequest request,
    CancellationToken cancellationToken)
```

The project-owned request contains ordered logical messages, ordered explicit tool declarations, and a bounded continuation envelope. The response contains one inspected project-owned message. The selected `MinimalChatClient` maps these values to private request, message, content, tool, continuation, and response records used by the provider-adapter boundary.

The interface exposes no generic network client, credential, endpoint, service locator, middleware pipeline, streaming operation, automatic tool invocation, shell, process, publisher, or GitHub/Actions capability.

### Dependency graph

The comparison and selected production seam add no direct or transitive managed package. The production runtime retains its pre-existing `JsonSchema.Net` dependency for the current deterministic runtime; that dependency is unrelated to the selected chat seam.

### Evidence

- Framework warning count: 0.
- Native AOT warning count: 0.
- Ubuntu Native AOT executable: 3,236,904 bytes.
- Ubuntu publish directory: 9,445,096 bytes.
- Candidate-owned neutral-head adapter and fake-backend Git blob bytes: 8,445.
- The selected source is the exact physical `MinimalChatClient.cs` compiled by both the production runtime and the comparison fixture; it is not a copied implementation.
- Framework and Native AOT selected-production parity passed on the selection commit.

## Candidate B: Microsoft.Extensions.AI.Abstractions 10.8.1

### Surface actually exercised

The private candidate adapter used:

- `IChatClient.GetResponseAsync`;
- `ChatMessage`, `ChatRole`, `TextContent`, and `TextReasoningContent`;
- `TextReasoningContent.ProtectedData` for the opaque continuation sentinel;
- `FunctionCallContent` and `FunctionResultContent`;
- `ChatOptions` with `ReasoningEffort.High` and `ReasoningOutput.Full`;
- explicit `AITool`/`AIFunctionDeclaration` declarations with closed `JsonElement` schemas;
- `AdditionalPropertiesDictionary` for adapter-owned structural metadata.

The fake backend implemented the interface-required streaming and service-retrieval members only to fail closed if reached. The Agent-facing seam did not expose or call them. The candidate did not use automatic function invocation, reflection-driven tool schema generation, middleware construction, response caching, chat reduction, generic DI, or package implementation objects in proof state.

### Dependency graph

The comparison project pins exactly `Microsoft.Extensions.AI.Abstractions` 10.8.1. Its `net10.0` asset has no transitive managed package dependencies. SDK-provided runtime and Native AOT packs are execution infrastructure and are recorded separately from the candidate package graph.

### Evidence

- Framework warning count: 0.
- Native AOT warning count: 0.
- Ubuntu Native AOT executable: 3,258,008 bytes.
- Ubuntu publish directory: 9,485,672 bytes.
- Candidate-owned neutral-head adapter and fake-backend Git blob bytes: 16,693.
- Framework and Native AOT common proof passed, including reasoning-enabled tool calls and fresh-process restoration.

## Selection Rationale

The frozen selection order produces a clear result:

1. Both candidates preserve project ownership of the loop, validation, serial tool ordering, state, prefix identity, provider projection, continuation, terminal review, and side effects.
2. Both candidates pass reflection-disabled source-generated serialization and add no candidate-specific trimming or AOT warning.
3. Both present the same one-operation Agent-facing capability, but Minimal has no additional package or package API surface.
4. Minimal needs substantially less candidate-owned adapter/fake-backend source and no compatibility work for broader interface members that the project does not use.
5. Minimal also has the smaller resolved managed dependency graph and the smaller published executable and directory, although size alone did not determine the result.

Project-minimal exchange types therefore provide the smaller production and maintenance surface without rewriting provider continuation into provider-neutral assistant text.

## Security And Privacy Impact

The selected seam is internal and capability-minimal. Architecture tests reject raw MEAI references, generic networking, service resolution, host environment, publisher, shell, process, GitHub, and Actions types under the Agent chat namespace. Production has no compile-time candidate selector and references only the selected path.

Reasoning remains enabled. The fixture preserves both readable and opaque continuation with exact hashes, framing, association, and placement. It proves that this data can participate in a later provider request without publishing the sentinel values. The fixture uses only synthetic public-safe values; it does not establish the final restricted-state transport, encryption, authorization, or production provider wire format.

## Migration And Maintenance Impact

The selected seam is deliberately not the Agent loop. Later R2 work can build explicit response inspection, budgets, tool validation, and terminal handling on the one-operation capability without accepting a framework-owned loop. R3 can implement a real provider adapter behind the private backend boundary without exposing provider transport or credentials to Agent orchestration.

No current Application, Execution, Protocol, Ledger, Prefix, TypeScript, schema, state-acceptance, publisher, package, or Action contract was migrated into this decision.

Dependency upgrades are not incidental maintenance. A future AI abstraction or provider package must have a focused issue and rerun representative framework and Native AOT execution.

## Non-Selected Candidate Disposition

The Microsoft.Extensions.AI candidate remains only under `runtime/tests/AiAbstractionAotFixture/**`. It is excluded from the production project graph and cannot be selected at production runtime. Retention keeps the exact comparison reproducible while the selected seam is still only a spike-level vertical slice.

The deletion gate is mandatory: #78 Phase 2 must assign removal of the dual-candidate fixture and its non-selected package to the focused child that replaces this proof with the real selected Agent loop and fresh-process Agent-session fixture. R2 cannot close until that ownership is assigned and the replacement evidence is green. Retention beyond that gate requires a new explicit maintenance decision.

## Open Questions

These questions remain for later owned work and do not invalidate the selection:

- the exact production provider request and response projection;
- the final Agent loop, budget, and terminal-result types;
- the final durable logical records and provider-scoped continuation envelope;
- restricted-state transport, encryption, authorization, and cross-workflow proof;
- whether later real-provider requirements justify revisiting the private backend mapping.

## Reproduction

```bash
bash runtime/scripts/verify-ai-abstraction.sh all
bash runtime/scripts/verify-ai-abstraction.sh selected-production
bash runtime/scripts/verify-runtime.sh all
dotnet test runtime/tests/AgenticPrReview.Runtime.Tests/AgenticPrReview.Runtime.Tests.csproj --nologo
npm run check
npm run dist:check
```

`all` executes both candidates in framework and Native AOT modes. `selected-production` executes the same common fixture through the exact selected production source in both modes. Existing direct runtime validation remains a separate gate.
