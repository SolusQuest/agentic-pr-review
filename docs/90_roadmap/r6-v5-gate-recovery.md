# R6 V5 gate recovery — #309

The latest #295 five-case 64K population remains a failed V6-entry observation. This follow-up adds bounded recovery for invalid arguments to registered nonterminal Agent tools while preserving the diagnostic and focused V5 gate work below. It does not amend the frozen #294 corpus, rewrite any #295 population, or start #296.

## `list_files` rejection

The `repository-rule` case failed before tool dispatch with `agent_tool_arguments_invalid / list_files / list_files_path_invalid`. Its rejected `prefix` or `after` value was not retained. The historical report therefore cannot identify the field, lexical rule, or model/provider origin.

The inbound `function.arguments` value is read by `DeepSeekResponseParser`, carried by `DeepSeekChatBackend` and `MinimalChatClient` into `ProjectToolCallContent.ArgumentsJson`, then admitted by `TryListFilesProvider`. Deterministic response-to-Agent tests preserve that semantic string and reproduce a fail-closed path rejection with zero dispatched tools. Provider-argument JSON normalization can change syntax such as whitespace and escaped letters; an admitted path value retains its decoded meaning. The outbound rewrite of accepted historical tool calls is outside the first-response failure path. No repository-owned inbound path transformation defect was reproduced. This finding does not reconstruct the historical rejected value or prove a model/provider cause.

The opt-in live diagnostic now keeps the existing `list_files_path_invalid` category and adds only fixed `path_field` and `path_rule` values for future events. The categories come from the same ordered lexical authority as `RepositoryPath.IsValid`: `prefix` or `after` pairs with a concrete rule, `both` pairs only with `unknown`, and the fallback is `unknown`/`unknown`. The strict reader rejects other combinations. They contain no argument/path/model content. Successful tool calls still retain their admitted `prefix` and `after` internally for ordinary execution. Earlier diagnostic records without the new optional properties remain canonical and byte-identical.

## Focused safe-control gate

All five frozen cases intentionally expose the same PR snapshot, including other cases' real defects. The #295 `ts-safe` row was structurally `Scored` because its three confirmed findings cited those other files, not the prohibited safe line. Under the predeclared focused-control rule, a safe case with any finding is not clean. The real off-focus findings are not relabeled hallucinations, and the generic R5 `EvaluationScorer` result is unchanged.

A separate V5 post-adjudication candidate check is bound to the frozen corpus digest and exact five-case order. It requires a live, fully completed and accounted population, clean execution and cleanup, all five accepted AI/human assessment origins, expected grounded/credited positive defects, and zero findings in both safe controls. Generic `ModelStatus=Adjudicated` on a zero-finding case is insufficient evidence of reviewer origin. A confirmed valid extra finding on a positive case remains uncredited rather than automatically failing this predicate. The check returns only `candidate_pass`, `blocked`, or `not_evaluable`. Even `candidate_pass` is not the final V6-entry verdict; independent privacy, safety and cross-case assessment remains required.

## Model-visible argument recovery

For a structurally valid response with an invalid argument to one of the five registered read-only tools, the Agent validates the complete batch before execution. It returns one fixed error per call ID in original order. A valid sibling receives `batch_not_executed`; no member is preflighted or dispatched, and no observation is created. Rejected arguments are replaced with `{"_apr_rejected":true}` in the next provider request and restricted SESSION. Valid siblings retain admitted canonical arguments. The recovery exchange uses distinct logical and SESSION kinds so it cannot be mistaken for a successful tool result or evidence. Original call IDs, assistant text, provider reasoning and continuation placement are preserved. Actual usage and fixed error bytes consume existing resource limits. If any staging check fails, the exchange is not committed. Terminal, unknown, malformed, preflight and tool execution failures remain terminal.

Synthetic tests cover parser failures, mixed batches, finite repeated rejection, exact DeepSeek request projection, SESSION restore, privacy and capacity. The prefix observer now recognizes this transformed history; an error result never provides positive read evidence. DeepSeek acceptance of the transformed history remains to be established by a separately reviewed one-turn live protocol probe.

## Correction and progression decision

The earlier offline diagnostic and gate improvements alone did not change the provider-visible request or rejected path behavior. The new recovery contract does change the next provider-visible request. A one-turn live protocol probe can test whether DeepSeek accepts that transformed history after a finite plan and profile authority are reviewed. It is not a five-case V5 quality population. The earlier key authorization does not itself establish a new population profile.

V5 remains blocked, and #296 must not start. The prospective V5 eligibility rule still disqualifies an argument rejection even when later corrected; changing that rule requires an explicit maintainer decision. A new five-case population would then need a clean protocol probe and separately reviewed finite plan and profile. The historical rejected field/value remains unknown. Neither the offline tests nor a successful one-turn probe converts #295's failure to a pass.
