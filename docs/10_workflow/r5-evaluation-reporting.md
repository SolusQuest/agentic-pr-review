# R5 evaluation reporting

[Issue #244](https://github.com/SolusQuest/agentic-pr-review/issues/244) adds internal reporting to `runtime/tests/ReviewEvaluationFixture/Reporting`. It consumes the [Q1 public-safe result contract](../20_architecture/r5-evaluation-contract.md); it does not execute a provider, import SESSION, authenticate report origins or add a product command.

## Input and ownership

`EvaluationReport.Create` takes an immutable array of UTF-8 Q1 outcome byte records. Every record passes through `EvaluationJson.ReadOutcome`, including its closed-field, exact enum, identity, count and state checks. A default array, malformed row, duplicate known selection, conflicting case definition or exceeded input bound rejects the entire batch with a `ReportingError` and submitted-row count. No malformed input is filtered out to produce a successful report. An explicitly empty array produces an empty report with unavailable ratios.

The harness supplies one selected result per `(corpus, case ID, attempt identity)`. Exact duplicates and pending/adjudicated versions of the same selected attempt are rejected. Legitimate repetitions have distinct attempt identities and remain separate case attempts. Within one corpus, a case ID has one case hash and expected-defect count; conflicting supplied definitions reject rather than being merged. Detached legal Q1 failures have no attempt identity, remain individual rows and are not deduplicated against a null key.

An admitted `EvaluationReport` has a private constructor. Its immutable document contains every admitted row in deterministic order, an overall summary and separate cohorts. Normal rendering accepts only this admitted wrapper. The report reader validates data and derived arithmetic; it grants no Runtime subject or execution authority. Syntactically legal case IDs must already be authored public-safe identifiers. Reporting does not infer secrets from arbitrary identifier contents.

`AttemptedCases` means supplied case-attempt result rows, including detached invalid rows whose actual execution is unknown. `KnownAttemptCases` and `DetachedCases` make that distinction explicit. Q4 cannot discover unsubmitted scheduled cases, prove a complete schedule or decide which adjudication revision is authoritative. The consuming harness selects the full population; [#251](https://github.com/SolusQuest/agentic-pr-review/issues/251) owns scheduling and [#250](https://github.com/SolusQuest/agentic-pr-review/issues/250) owns declared/executed coverage enforcement.

## Counts and eligibility

Execution counts partition admitted rows: `AttemptedCases = Completed + Failed + Invalid`. Other dimensions overlap. In particular, a completed execution can have failed evidence assertions, and invalid adjudication can retain a completed execution and a failed structural scenario. `InvalidAdjudications` is not restricted to executions with status `Invalid`.

The summary preserves:

- evidence and scenario passed/failed/not-evaluated counts;
- adjudicated, unadjudicated, model-not-evaluated and eligible case counts;
- all seven Q1 attribution categories, including `Unknown`;
- represented findings and tool observations, expected defects across all rows, structural matches/misses and duplicate/prohibited observations;
- partial adjudicated true/false observations, unique expected-defect credit and pending findings;
- eligible expected defects and eligible expected defects without adjudicated credit.

Structural misses differ from eligible uncredited defects: a structurally matched finding can be adjudicated false. A confirmed finding without expected-defect binding contributes to true findings but not recall credit. Duplicate observations are structural excess findings, not automatically false findings. Overlapping structural counters are never summed into a false-positive total.

Q1 zero counters on failed/not-evaluated rows do not establish a clean review or zero tool usage. `RepresentedToolObservations` counts only observations represented in admitted results. Token, duration, cost and usage telemetry are `NotSupplied` and rendered as unknown; this leaf does not collect or fabricate them. An invalid annotation has zero pending findings but still has model status `NotEvaluated`.

An eligible row has completed execution, passed evidence, adjudicated model observations and no invalid-adjudication rejection. Scenario failure does **not** exclude an otherwise eligible row: excluding a missing-defect case would falsely improve recall. Engineering status remains failed when evidence or scenario assertions fail. `HasBlockingFailures` also retains execution and evaluator/annotation failures, independently of model metrics; a semantic judgment cannot clear those failures.

## Ratios

Both metrics use the same explicitly counted eligible case population:

| Metric                 | Numerator                     | Denominator                                   |
| ---------------------- | ----------------------------- | --------------------------------------------- |
| Finding precision      | Eligible `AdjudicatedTrue`    | Eligible `AdjudicatedTrue + AdjudicatedFalse` |
| Expected-defect recall | Eligible `AdjudicatedDefects` | Eligible `ExpectedDefects`                    |

Ratios contain raw numerator, denominator, eligible/excluded case counts, availability and a nullable numeric value. No average of per-case percentages or structural credit substitutes for these sums.

- Fully eligible populations with positive denominators have an `Available` value, including a real zero.
- A zero denominator has `EmptyDenominator` and a null value. An empty valid review with one expected defect has precision unavailable at `0/0` and recall zero at `0/1`.
- Failed, invalid, evidence-rejected or incompletely adjudicated rows make a population ratio `Incomplete` and null. Its eligible numerator/denominator remain visible, so failures cannot improve an apparently complete percentage by disappearing from the denominator.
- Mixed cohorts have an `Incomparable` overall ratio. Each cohort has its own summary. Detached context also makes known-cohort population ratios incomplete because those unassigned rows cannot be safely attributed to a cohort; `UnknownContext` states that limitation even when the known cohort itself has no excluded rows.

Availability precedence is mixed cohort, then incomplete population, then empty denominator, then available. The reason list preserves additional conditions. Dirty source identity blocks exact runtime comparison but does not erase measured observations from a single report. No precision/recall threshold, model ranking, graduation or automatic regression verdict is imposed.

The nine-row self-test includes confirmed, rejected, missing, pending, provider-failed, detached-invalid, evidence-rejected, stale-annotation and empty-control results. It has nine case attempts, seven completed, one failed, one invalid, four model-not-evaluated and four eligible cases. Eligible precision counts are `1/2` and recall counts `1/3`; overall values are null/incomplete. This synthetic fixture tests reporting mechanics, not live model accuracy.

## Cohorts and runtime comparisons

Cohorts retain corpus hash, configuration hash, deterministic/live mode and source commit/tree/clean state. Q1's configuration identity already binds provider/model/adapter, policy/prompt, toolset, limits and selected provider settings. Q4 reuses it without a second configuration registry. A configuration mismatch is reported as `ConfigurationChanged` with both identities; opaque hashes cannot explain which individual component changed.

`EvaluationReportComparison.Create` accepts two admitted reports and preserves both source/cohort summaries and per-case expectation/repeat/eligible coverage. Its ordered reason set includes:

| Reason                                                             | Meaning                                               |
| ------------------------------------------------------------------ | ----------------------------------------------------- |
| `EmptyPopulation`, `UnknownContext`, `DirtySource`                 | Missing population or exact source/context evidence   |
| `MixedCorpus`, `MixedConfiguration`, `MixedMode`, `MixedSource`    | A side combines incompatible cohorts                  |
| `IncompleteExecution`, `RejectedEvidence`, `IncompleteAnnotations` | A side lacks complete eligible observations           |
| `CorpusChanged`, `ConfigurationChanged`, `ModeChanged`             | Inputs differ between the selected reports            |
| `ExpectationsChanged`                                              | Case identities or expected-defect definitions differ |
| `PopulationChanged`                                                | Selected case sets or repeat multiplicities differ    |

Compatible comparisons require complete eligible coverage for the same case/expectation multiplicities, known clean source identities and equal corpus/configuration/mode. Source commit and tree may differ: runtime revision is the declared comparison variable. Attempt/execution hashes and finding counts may differ; requiring those to match would prevent meaningful comparisons. Fully adjudicated zero-finding cases can be comparable while their precision is unavailable. A scenario failure remains visible and blocking even when its model observations are comparable.

The comparison never silently intersects two case sets or treats equal aggregate counts as equal coverage. A comparable decision means inputs permit interpretation together, not that the candidate has passed engineering assertions or improved model quality.

## Serialization, limits and safe failures

`EvaluationReportJson.Write` and `EvaluationReportMarkdown.Write` render the same admitted aggregate. JSON retains full row identities; Markdown contains fixed explanations, validated IDs/hashes, enum names and invariant-culture counts/ratios. Candidate prose, repository paths, provider/state bytes, error messages and stack traces are not reporting inputs. `WriteFailure` emits only a known error code and nonnegative submitted-row count.

`EvaluationReportJson.Read` uses closed source-generated metadata, passes original embedded outcome representations through Q1 admission and recomputes all summaries/cohorts. Forged totals, availability, ratios, cohort membership, unknown/duplicate/missing fields and numeric or incorrectly spelled enum tokens reject. It does not normalize an invalid embedded Q1 representation into a valid one. Object property order is immaterial; emitted arrays use deterministic canonical order. `ReadComparison` checks a comparison against the two selected admitted reports, rather than claiming to verify unseen inputs.

| Bound                                  | Maximum                  |
| -------------------------------------- | ------------------------ |
| Outcome rows                           | 256                      |
| Individual outcome input               | Q1's 64 KiB and depth 12 |
| Aggregate outcome input                | 4 MiB                    |
| Report/comparison JSON input or output | 1 MiB and depth 16       |
| Report/comparison Markdown output      | 256 KiB                  |

Counts are bounded by 256 admitted Q1 rows, at most 5120 findings or expected defects. The number of cohorts/case coverage entries cannot exceed row count. Output methods accept a smaller byte budget up to the fixed format cap. Insufficient budgets return `OutputLimit` with no partial output; invalid budgets return `InvalidOutputBudget`. A large many-cohort batch can be admitted yet exceed an output budget; callers must handle that explicit failure rather than omit cohorts. There is no silent truncation or production-limit change.

## Validation and later integration

Run the focused consumer tests and the complete repository gates:

```bash
dotnet test runtime/tests/AgenticPrReview.Runtime.Tests/AgenticPrReview.Runtime.Tests.csproj --configuration Release --filter FullyQualifiedName~R5EvaluationReportTests
dotnet test runtime/tests/AgenticPrReview.Runtime.Tests/AgenticPrReview.Runtime.Tests.csproj --configuration Release
npm run check
npm run dist:check
```

`EvaluationReportSelfTest.RunAsync` is an in-process reporting seam that exercises the real Q1 producer, nine-row aggregation, actual report/comparison readers and both renderers. Its source identities are authored synthetic test values; a native validation driver records its actual compiled source separately. Linux Native AOT verification must call the Reporting methods themselves. Publishing the unchanged Q1 command alone does not prove they executed.

This leaf adds no dispatcher command or project wiring. The parent design hands the shared dispatcher through Q1, Q2, completed replay, live harness and final verifier owners. Full R5 framework/AOT command and CI integration remains #250. All existing R3/R4 conformance paths remain in place.
