# R5 authorized live baseline — 2026-09-19

[Issue #252](https://github.com/SolusQuest/agentic-pr-review/issues/252) is delivered by [PR #264](https://github.com/SolusQuest/agentic-pr-review/pull/264). **The baseline is inconclusive.** Two explicitly scheduled five-case runs produced ten attempts, two completed executions, eight failed executions and zero quality-eligible cases. Neither precision nor recall is available. The engineering failures below remain blockers to a usable model-quality baseline; completing this observation does not assert model quality, promote a default, merge a PR or authorize release.

## Authorization, subject and bounds

Before execution, the maintainer explicitly authorized paid DeepSeek calls, independent Codex adjudication and correction-PR-head verification. The operator selected a cumulative USD 5 reservation ceiling, built and dry-ran the evaluator without credentials, and recorded finite plans before reading the local key. Execution used the built evaluator directly with a scrubbed environment and no GitHub credential or publication capability. No real PR was submitted to the model: the input was the reviewed R5 synthetic repository/review corpus. The implementation PR supplies the evaluator; it is not the review subject.

Both runs scheduled, in order, `cs-defect`, `cs-safe`, `ts-defect`, `ts-safe` and `repository-rule`, once each. Each plan allowed at most five evaluations, 40 model calls, 1,310,720 input tokens, 163,840 output tokens, 1,474,560 combined tokens, 600 execution seconds and USD 4 reserved spend. Each call reserved at most 32,768 input tokens, 4,096 output tokens and USD 0.10. The operator checked previous reservations plus the next plan ceiling against the USD 5 campaign ceiling before the second run. There were no automatic retries; the explicit diagnostic and corrected run are retained separately below.

The requested model was `deepseek-v4-flash` through the existing thinking adapter. [DeepSeek's pricing/model documentation](https://api-docs.deepseek.com/zh-cn/quick_start/pricing/) identifies that retained alias as served by V4.1-Flash; the diagnostic response identified itself as `deepseek-flash`. This is an observation of that service route on the stated date, not a measurement of a frozen retired release. The conservative per-call monetary reservation exceeds the documented Flash input/output price for the token caps; it is not a billing measurement or an R6 cost comparison.

## Executed identities

These identities describe actual clean builds and admitted inputs. They do not freeze a candidate source for subsequent work. A later documentation-only evidence commit does not change what ran, and does not justify repeating paid calls.

| Identity                                      | Value                                                              |
| --------------------------------------------- | ------------------------------------------------------------------ |
| Corpus SHA-256, both runs                     | `05b669903439aea13818e507b48c0c3742a979672d124f7d68113c63dfdf4e38` |
| Live outcome configuration SHA-256, both runs | `cfb58f964fde79ff40dd7d83485379109b835f06bafe1c4ca151b2d46b851991` |
| Plan provider-settings SHA-256, both runs     | `a2c605221edbd73a239123b52538fa47b9bb3e7d2db72192dde60a919a9603df` |
| Adapter SHA-256, both runs                    | `968abd371badaa785056ee783553d71763b8a8a6d0d07031f47acc3cfa24d502` |
| Initial source commit                         | `08c5abbad377c118f44abd9941327a9f51d0c09a`                         |
| Initial source tree                           | `097fd9b8c62c22bea4b75f3c978846474efd6569`                         |
| Initial plan SHA-256                          | `a242e748f1a9a3dfe3ced6fb7d10122f07b4e897fb52020e29c5992534d19fb8` |
| Corrected source commit                       | `95308bb85167886745fb00b672a49e387033cb4d`                         |
| Corrected source tree                         | `3e2a6e511af01e612f019a0de81b142c3da94d93`                         |
| Corrected plan SHA-256                        | `82ca492178e09d47bb91b98d2563cb94d2355686b8a042f8cb489a19676ac630` |

The provider-settings identity binds the plan settings; the outcome configuration additionally describes the admitted live evaluation context. They are different identity domains, not a mismatch.

## Complete attempt and usage accounting

| Invocation              | Scheduled / attempted | Completed / failed / invalid / unattempted | Provider calls | Known input / output tokens | Unknown-usage calls | Reserved USD |
| ----------------------- | --------------------- | ------------------------------------------ | -------------- | --------------------------- | ------------------- | ------------ |
| Initial evaluator run   | 5 / 5                 | 0 / 5 / 0 / 0                              | 5              | 0 / 0                       | 5                   | 0.50         |
| Model-name diagnostic   | Not an evaluation     | Not in quality denominator                 | 1              | 7 / 2                       | 0                   | 0.10         |
| Corrected evaluator run | 5 / 5                 | 2 / 3 / 0 / 0                              | 17             | 52,288 / 4,987              | 0                   | 1.70         |
| Campaign                | 10 / 10 evaluations   | 2 / 8 / 0 / 0                              | 23             | 52,295 / 4,989 known only   | 5                   | 2.30         |

USD 2.30 is the accumulated conservative reservation, **not the provider invoice**. Initial unknown usage is not zero consumption. The initial run reserved 163,840 input, 20,480 output and 184,320 combined tokens; the corrected run reserved 557,056 input, 69,632 output and 626,688 combined tokens. Corrected known combined usage was 57,275 tokens. Both evaluator runs stopped with `complete`, reported no accounting violation, and attempted each scheduled case once. The diagnostic was a single separately bounded request with a 16-token output cap, 30-second timeout, bounded response and no retry; it extracted only model/usage metadata and was excluded from quality reporting.

| Case              | Initial run                               | Corrected run                                            | Corrected findings |
| ----------------- | ----------------------------------------- | -------------------------------------------------------- | ------------------ |
| `cs-defect`       | Failed: normalization / `MalformedOutput` | Failed: Agent / `MalformedOutput`                        | 0                  |
| `cs-safe`         | Failed: normalization / `MalformedOutput` | Failed: Agent / `MalformedOutput`                        | 0                  |
| `ts-defect`       | Failed: normalization / `MalformedOutput` | Completed; evidence failed: `RequiredObservationMissing` | 1                  |
| `ts-safe`         | Failed: normalization / `MalformedOutput` | Failed: Agent / `MalformedOutput`                        | 0                  |
| `repository-rule` | Failed: normalization / `MalformedOutput` | Completed; evidence failed: `RequiredObservationMissing` | 2                  |

The initial run's five normalization failures exposed rejection of the provider's reply model alias. A one-call metadata diagnostic established the reply identity, then a synthetic regression reproduced the failure before the narrow parser repair. The corrected parser admits the exact `deepseek-flash` reply alongside the existing request identity; unrelated models and malformed usage/tool output remain rejected. The corrected run had no normalization exceptions or transport failure counters. Its three Agent `MalformedOutput` failures remain unresolved observations: the retained safe telemetry does not establish the exact terminal/schema violation, and no raw response was archived to invent a more specific diagnosis.

## Delegated AI adjudication

Codex inspected all three available findings against the admitted synthetic source, diff, scope and expectations while the evaluator retained the original subjects in memory. Bound annotations were accepted for both completed cases. There was **no independent human confirmation**: `ai_adjudicated_cases=2`, `human_confirmed_cases=0`. No provider calls were made during adjudication.

| Case / finding ordinal | AI verdict                  | Reason and scoring limit                                                                                                                                                                                                                                             |
| ---------------------- | --------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `ts-defect` / 0        | Confirmed; no defect credit | A truthy fallback replaces an explicit zero timeout with the default, contradicting the synthetic configuration's disable-timeout meaning. The finding is substantively correct, but the actual observation identity does not satisfy the expected evidence binding. |
| `repository-rule` / 0  | Rejected; no defect credit  | The claimed credential-in-logs exposure is not demonstrated: the admitted logging method has an empty implementation. This also exposes a semantic weakness in the synthetic expected-defect oracle; the oracle was not rewritten to manufacture a passing result.   |
| `repository-rule` / 1  | Rejected; no defect credit  | The timeout concern is real in the other synthetic file, but outside this case's focused change and review-only-focus policy. This is a scope rejection, not a claim that the timeout concern is factually false.                                                    |

The completed execution bindings were:

- `ts-defect`: `59e3ae32f1585f42ea89e6385756c5d3a162efe5f0e85753fc3a5253e40d0cbe`.
- `repository-rule`: `5f7799a841f90fca6afadf0f052f812ce0a9a46c74d17d1d289ac32bac12638a`.

Both completed subjects failed required observation-identity assertions before model scoring. Therefore Q4 correctly retains all five corrected cases as `NotEvaluated`, zero eligible cases, three represented findings, three expected defects, and `precision`/`recall` as `Incomplete` with null values. It gives zero semantic or expected-defect credit. The accepted review attestations do not override that evidence gate. `adjudication_status=pending` and the nonzero command exit reflect that non-evaluable result, not an unfinished request for the operator to judge the three findings. The qualitative verdicts above must not be converted into a 1/3 precision claim. Misses, duplicates and recall cannot be established from this ineligible population.

## Engineering disposition and limits

The observation establishes four distinct follow-up needs before a usable quality baseline: diagnose the remaining Agent output failures using bounded safe diagnostics; determine how valid live observations should satisfy reviewed corpus evidence bindings without weakening checks; repair and re-review the no-op logging oracle; and rerun a newly admitted bounded schedule only when those changes warrant it. Earlier attempts must remain in the history. This PR does not expand into an open-ended model-tuning or oracle-redesign campaign.

The added local adjudication path preserves pending cases and accounting, rejects stale/invalid annotations before granting provenance even on evidence-failed subjects, and separates AI attestations from human confirmation. Initial Relay feedback identified an evidence-first validation bypass and overly strong cleanup wording; both were corrected before the second run. The first run had no admitted subject or annotation, so that bypass did not affect its results.

The local Windows operator wrapper initially detached the evaluator's console while inheriting its input handles. After provider execution had ended, the operator recovered input to that same process and completed review without a model rerun. This was an operator-launch issue, not a claimed model failure; future hidden launchers must explicitly pipe all three standard streams. Both run summaries reported `cleanup=cleaned`, and the second run's announced private review directory was independently checked absent after process exit. No private packets, SESSION, reasoning/continuation, raw provider response or credential are published here.

Validation included 235 affected DeepSeek/scorer/live-harness tests on Windows, keyless selected-case dry-run, Linux framework/Native AOT R5 proof and corrected-head Native AOT verification. The TypeScript suite passed all 764 tests in a serial Linux rerun after local concurrent runs timed out; formatting/typecheck passed, and corrected-head GitHub `check`, integration, R5 gate and both CodeQL analyses passed. Required full-runtime CI and final PR review are tracked on [PR #264](https://github.com/SolusQuest/agentic-pr-review/pull/264); this report does not substitute an earlier source's checks for final-head validation.

This small synthetic, single-sample-per-case schedule cannot support general model rankings, real-repository performance, economics, cache benefits or release readiness. Delegated AI judgment has no independent human audit. R6 owns economics and R7 owns release decisions.
