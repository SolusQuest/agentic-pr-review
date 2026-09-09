# R5 evaluation subject and scorer

Issue [#241](https://github.com/SolusQuest/agentic-pr-review/issues/241) introduces the test-only `runtime/tests/ReviewEvaluationFixture` executable. It references the existing Runtime through friend access; it does not add a production CLI, provider, Action input, persistent SESSION format or public compatibility promise.

Run the independently usable scorer vectors with:

```bash
dotnet run --project runtime/tests/ReviewEvaluationFixture/AgenticPrReview.Runtime.ReviewEvaluationFixture.csproj --configuration Release -- evaluate --fixture self-test
```

The command emits bounded per-case JSON and exits zero only when all 19 positive and expected-negative vectors have the expected outcomes and round-trip through the result reader. It also exercises the case/run/adjudication readers. Negative vectors deliberately report rejected subjects; their rejection is the expected test result. Authored synthetic annotations test scoring mechanics, not real human judgments or live model accuracy. Unknown arguments exit 2 with a stable code; infrastructure failures exit 1 without exception text. There is no provider or GitHub credential lookup.

## Admission and ownership

`EvaluationJson.ReadCase`, `ReadRun` and `ReadAdjudication` read internal input descriptors. All fields are required, including explicitly nullable annotation defect IDs. Unknown properties, duplicate properties, missing fields, invalid types, invalid UTF-8 and unsupported values reject. JSON input is capped at 64 KiB and depth 12. Identifiers use the existing 1–64 character identifier domain; hashes are lowercase fixed-length SHA identities. Paths retain the existing repository path rules. Each case has at most 20 expected defects, 20 prohibited ranges and 24 required observations. Each sidecar has at most 20 finding annotations. Strings, ranges and collections are validated before an input becomes an admitted case. Property order is not an input ABI; source-generated serialization supplies deterministic field order for content identity.

The corpus digest is supplied by the authored corpus owner; it does not authenticate external files. The case digest is computed from the complete admitted case descriptor, including expectation content and corpus identity. Case IDs must be authored public-safe synthetic identifiers, not copied from private source or runtime errors. A case contains only existing reviewed identity, expected evidence locations, required observation references and prohibited locations. Repository file/diff representation and replay input loading belong to [#245](https://github.com/SolusQuest/agentic-pr-review/issues/245).

`EvaluationSubject.Admit` accepts an in-memory `AgentSessionBuildInput` and a validated run descriptor. It reuses `R3QualitySubject.TryCreateCompleted`: existing SESSION construction validates stable request, history, continuation and capacity, then tool observations and the terminal review are reconstructed and admitted using current Runtime validators. Expected answers or arbitrary transcript JSON cannot construct a completed subject. The adapter hides the R3 fresh-input type and retains only the reconstructed findings, immutable observation references and identities. No SESSION is persisted or imported.

`EvaluationAttempt.Admit` captures an immutable safe attempt before Agent execution from the validated run descriptor and existing trusted-policy materializer. Configuration identity contains only provider/model/adapter identities, trusted policy/prompt digest, toolset and limits digests, selected provider-configuration digest and mode. Case/repository/PR identity, current input, restored history, prior SESSION digest, session/run IDs and source/build revision are excluded from configuration cohorts. Identity tuples use an unambiguous canonical JSON array, including when UTF-8 settings contain delimiter characters.

Attempt identity binds that configuration and the complete run descriptor, including source commit/tree/clean state and authored run ID. A completed subject uses the same attempt projection and adds a separate execution digest binding the actual complete stable plan (including build and prior SESSION), initial request/history/continuation bytes, session ID, terminal digest and ordered observations. Consequently, changed candidate prose invalidates old annotations even when structural matching is unchanged. The harness owner supplies distinct run IDs for distinct scheduled executions, including retries/resumption, and the exact provider-configuration digest; these inputs are not inferred from model prose.

The self-test build records Git HEAD, its tree and whether the checkout is clean in compiled metadata. A dirty build reports `source_clean: false`; its commit/tree describe the base, not a claim that the edited source equals that tree. Rebuild from the reviewed clean commit for reproducible evidence. Runtime source identity is separate from `StableAgentPlan.BuildId`. The test-only MSBuild source probe requires an ordinary Git checkout.

## Scoring and observations

The scorer compares the admitted reviewed identity and required tool/observation bindings first. A completed subject remains a `Completed` execution when these assertions fail: evidence status is `Failed`, execution-failure source/kind remain `None`, and model quality is `NotEvaluated`. An invalid sidecar is an evaluator-input rejection attached to the completed run, not a failed Agent execution. Scope, grounding or observation failures cannot become model-quality results. A bounded maximum bipartite matching then computes structural matches between finding ordinals and independent defect IDs using severity and exact evidence reference. Each finding and defect is matched at most once. Candidate prose is never a semantic predicate. Extra structurally related findings are recorded as duplicate observations; they are not automatically proven semantic duplicates.

Results keep three separate dimensions:

| Dimension                        | Meaning                                                                                                           |
| -------------------------------- | ----------------------------------------------------------------------------------------------------------------- |
| Execution status                 | A completed subject, failed execution or invalid evaluator input                                                  |
| Evidence and scenario assertions | Evidence/scope/tool validity separately from structural must-find, prohibited-location and duplicate observations |
| Model observations               | Confirmed, rejected and still-unadjudicated finding counts; no implicit semantic credit from structural matching  |

A structural scenario mismatch remains visible even with a valid annotation. Missing expected locations and prohibited-location observations are evidence about this particular scenario, not a new production quality threshold. An absent annotation leaves candidate semantics unadjudicated in both deterministic and live modes. An empty valid completed review is distinct from an incomplete run; failed or invalid runs always have model status `NotEvaluated`.

An invalid annotation also preserves the already-computed scenario status and structural match/missing/duplicate/prohibited counts. It overlays `AdjudicationInvalid`, evaluator-input provenance and model `NotEvaluated` with zero semantic credit, including when an earlier annotation entry was valid. Thus a stale sidecar cannot hide a known structural failure from downstream reporting.

An adjudication sidecar binds the corpus, computed case, actual configuration and execution digests plus a zero-based finding ordinal. A confirmed judgment can optionally name one structurally compatible expected defect; rejected judgments cannot claim a defect. Duplicate ordinals, duplicate defect credit, out-of-range ordinals, unknown defects and stale bindings reject. An annotation cannot repair failed evidence or wrong scope. A confirmed finding outside the expected defect set may be counted as confirmed without adding expected-defect credit. Aggregate denominators, precision/recall and cohort comparisons belong to [#244](https://github.com/SolusQuest/agentic-pr-review/issues/244).

## Failures and public output

Failure factories consume evidence from the actual provider transport, Agent outcome, tool execution or SESSION build boundary. Fixed typed categories distinguish provider calls, malformed output, tool operations, Host/state admission and evaluator-invalid input. A generic Agent chat error cannot identify its root cause and stays unknown; arbitrary error prefixes and exception messages are never classifiers. Pass the admitted `EvaluationAttempt` to `Failure` for every known attempt: source/configuration/mode/attempt metadata is retained even without a completed subject, and the completed-result execution digest stays `null`. Only genuinely detached failures or invalid input before attempt admission omit that context. No failed attempt becomes a successful empty review.

Normal output contains only public-safe case IDs, hashes, source provenance, fixed enum codes, bounded counts and status values. It excludes candidate title/message/summary, repository names and paths, source snippets, tool arguments/results, raw provider responses, continuation, credentials and exception strings. Source-generated JSON runs with reflection serialization disabled in the fixture executable. The scorer retains no mutable global state. Tests inject synthetic private canaries into candidate and tool text and failures and verify that normal output and diagnostic representations do not contain them.

`EvaluationJson.ReadOutcome` is the Q1-owned reader for downstream report aggregation. It applies the same 64 KiB/depth-12 closed source-generated JSON boundary, requires every field including explicit nulls and zero counts, and accepts only exact named string enums. Admission checks bounded identifiers/hashes, complete-or-absent attempt metadata, known source/mode/status codes, finite nonnegative counts, structural and adjudication count conservation, failure provenance pairs and legal execution/evidence/scenario/model combinations. Known structural results survive annotation errors; failed/detached attempts cannot claim completed-result or model-quality evidence. This reader validates report data, not its origin or truth, and cannot construct an `EvaluationSubject` or authenticate a completed run. Report selection/provenance remains the consuming harness's responsibility; no arbitrary transcript or new persistent SESSION format is admitted.

## Assertion provenance and retained evidence

| Existing evidence                                          | R5 use or additional assertion                                                                           |
| ---------------------------------------------------------- | -------------------------------------------------------------------------------------------------------- |
| R3 completed-subject SESSION/tool/terminal reconstruction  | Reused unchanged by the fixture-local subject adapter                                                    |
| R3 must-find tool and observation binding                  | Generalized to bounded case expectations and one-to-one structural attribution                           |
| R3 must-not-find locations                                 | Explicit prohibited-location observations, separate from model judgments                                 |
| R3 prior-only continuation and fresh-input isolation       | Retained unchanged; completed replay belongs to #246                                                     |
| R3 typed failures and public-safe outcomes                 | Separate R5 provenance categories, explicit unknown attribution and no model result for incomplete input |
| R3 structural match result                                 | Never reinterpreted as R5 semantic correctness; exact-bound sidecar required                             |
| R2/R3/R4 verifiers, direct-runtime fixtures and S2 vectors | All retained; no fixture retirement or replacement claim                                                 |

Validate with the Release Runtime test project and `npm run check`. Linux x64 Native AOT publication and the self-test exercise this fixture's source-generated codec and scorer. Full R5 corpus/replay/growth coverage and CI wiring remain [#250](https://github.com/SolusQuest/agentic-pr-review/issues/250); this standalone fixture does not claim that later gate has run.
