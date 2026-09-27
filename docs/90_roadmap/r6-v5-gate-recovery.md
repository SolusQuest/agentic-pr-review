# R6 V5 gate recovery — #309

The latest #295 five-case 64K population remains a failed V6-entry observation. This follow-up addresses two limitations in its interpretation and future diagnostics. It does not amend the frozen #294 corpus, rewrite any #295 population, approve another paid invocation, or start #296.

## `list_files` rejection

The `repository-rule` case failed before tool dispatch with `agent_tool_arguments_invalid / list_files / list_files_path_invalid`. Its rejected `prefix` or `after` value was not retained. The historical report therefore cannot identify the field, lexical rule, or model/provider origin.

The inbound `function.arguments` value is read by `DeepSeekResponseParser`, carried by `DeepSeekChatBackend` and `MinimalChatClient` into `ProjectToolCallContent.ArgumentsJson`, then admitted by `TryListFilesProvider`. Deterministic response-to-Agent tests preserve that semantic string and reproduce a fail-closed path rejection with zero dispatched tools. Provider-argument JSON normalization can change syntax such as whitespace and escaped letters; an admitted path value retains its decoded meaning. The outbound rewrite of accepted historical tool calls is outside the first-response failure path. No repository-owned inbound path transformation defect was reproduced. This finding does not reconstruct the historical rejected value or prove a model/provider cause.

The opt-in live diagnostic now keeps the existing `list_files_path_invalid` category and adds only fixed `path_field` and `path_rule` values for future events. The categories come from the same ordered lexical authority as `RepositoryPath.IsValid`: `prefix` or `after` pairs with a concrete rule, `both` pairs only with `unknown`, and the fallback is `unknown`/`unknown`. The strict reader rejects other combinations. They contain no argument/path/model content. Successful tool calls still retain their admitted `prefix` and `after` internally for ordinary execution. Earlier diagnostic records without the new optional properties remain canonical and byte-identical.

## Focused safe-control gate

All five frozen cases intentionally expose the same PR snapshot, including other cases' real defects. The #295 `ts-safe` row was structurally `Scored` because its three confirmed findings cited those other files, not the prohibited safe line. Under the predeclared focused-control rule, a safe case with any finding is not clean. The real off-focus findings are not relabeled hallucinations, and the generic R5 `EvaluationScorer` result is unchanged.

A separate V5 post-adjudication candidate check is bound to the frozen corpus digest and exact five-case order. It requires a live, fully completed and accounted population, clean execution and cleanup, all five accepted AI/human assessment origins, expected grounded/credited positive defects, and zero findings in both safe controls. Generic `ModelStatus=Adjudicated` on a zero-finding case is insufficient evidence of reviewer origin. A confirmed valid extra finding on a positive case remains uncredited rather than automatically failing this predicate. The check returns only `candidate_pass`, `blocked`, or `not_evaluable`. Even `candidate_pass` is not the final V6-entry verdict; independent privacy, safety and cross-case assessment remains required.

## Correction and progression decision

The current offline evidence supports a diagnostic improvement and a focused V5 assessment improvement, but no causal change to the provider-visible request or the rejected path behavior. A new paid five-case sample after only these changes would test stochastic behavior without addressing the observed path failure. **No paid repeat is justified by #309's offline work.** The earlier user authorization to use the local key remains separate from #303's spent single-population 64K profile/progression exception; neither is used here.

V5 remains blocked, and #296 must not start. #271 should retain this decision and the remaining unknown: the exact rejected historical field/value and whether a separately approved model-visible contract change could reduce future invalid calls without weakening path security or changing the frozen experiment. Any such contract/progression decision would require its own reviewed evidence before a new clean five-case population. #309 can complete its bounded diagnostic and gate deliverable without converting #295's failure to a pass.
