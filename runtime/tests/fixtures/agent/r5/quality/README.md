# R5 deterministic single-review corpus

Run from the repository root:

```bash
dotnet run --project runtime/tests/ReviewEvaluationFixture/AgenticPrReview.Runtime.ReviewEvaluationFixture.csproj --configuration Release -- quality --corpus runtime/tests/fixtures/agent/r5/quality
```

The evaluated input is `bundle/manifest.json` and its declared members. R1 admits the entire directory and verifies exact bytes, inventory, roles and finite limits before Q2 executes anything. The neighboring README and byte-preservation attributes are documentation, outside the admitted bundle. The manifest enumerates thirteen scenarios and fifty members, below R1's existing limits. All scenarios share the same immutable reviewed snapshot and diff. Their `same_head` descriptors order cases; every case starts a fresh in-process Agent bootstrap without predecessor observations or continuation. This command does not execute completed-session history or prove fresh-process replay.

## Coverage matrix

| Case                 | Engineering purpose                                                                                                       | Expected actual outcome                           |
| -------------------- | ------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------- |
| `cs-defect`          | Caller dereferences a nullable callee result; changed-file discovery, diff and both file reads execute                    | `Scored`                                          |
| `cs-safe`            | Null-safe caller control                                                                                                  | `Scored`, zero findings                           |
| `ts-defect`          | A zero-valued configuration is overwritten by a truthy fallback; configuration read, consumer search and read execute     | `Scored`                                          |
| `ts-safe`            | Nullish fallback preserves the zero-valued configuration                                                                  | `Scored`, zero findings                           |
| `repository-rule`    | A repository logging rule forbids the consumer's token logging; file listing, rule read, consumer search and read execute | `Scored`                                          |
| `sticky-only`        | Grounded evidence in the unchanged callee has no current right-side inline coordinate                                     | `Scored`, no eligible inline location             |
| `no-required-tool`   | Valid empty terminal without repository calls                                                                             | `RequiredToolMissing`                             |
| `irrelevant-tool`    | Replace the decisive callee read with a valid irrelevant file read                                                        | `RequiredObservationMissing`                      |
| `wrong-evidence`     | Invent an observation ID                                                                                                  | `ExecutionFailed`, Agent `agent_terminal_invalid` |
| `wrong-location`     | Cite a genuinely returned but incorrect source line                                                                       | `ExpectedFindingMissing`                          |
| `safe-invention`     | Ground an invented defect on the safe control                                                                             | `ProhibitedFinding`                               |
| `duplicate-proposal` | Different prose proposes the same structural defect twice                                                                 | `DuplicateObservation`                            |
| `pathless-proposal`  | Propose evidence without a valid repository path                                                                          | `ExecutionFailed`, Agent `agent_terminal_invalid` |

The small compiled `QualityCoverage` inventory is independent of the mutable manifest and provider scripts. Required, declared and executed IDs must match exactly. Positive cases require the checked observation IDs for their assigned list/read/search/diff operations in the actual admitted Agent subject. Registering a tool or running the independent preflight does not establish execution coverage. Tests remove or replace required operations while keeping the source, preflight and expected assertions intact.

## Evidence and interpretation

Only `SnapshotToolExecutor` over R1's captured memory supplies tool results. Authored provider inputs contain calls and candidate findings; they cannot supply observations. The real Agent validates arguments, executes tools, admits their canonical results and validates the terminal. Completed results pass through the existing SESSION/R3-backed Q1 subject admission. The command never compiles or executes the reviewed C#/TypeScript source and constructs no HTTP, credential, state-transport or publication component.

An independent real-tool preflight checks the fixed decisive source lines and checked observation IDs. Its observations are not inserted into Agent history. A private witness checks the actual first request, including materialized control/policy messages, for leakage of the authored decisive facts. Source and context mutation tests keep the candidate answers unchanged and update only R1 integrity entries, so the checks exercise Q2 rather than merely failing stale file hashes. These are checks of the explicit authored cases, not a general semantic detector for paraphrased secrets or arbitrary natural-language claims.

Q2 uses the explicit empty-`reasoning_content`, no-continuation subset of the R1 script representation. Unsupported nonempty reasoning rejects rather than being discarded. Missing/extra cases, mismatched expectations, unused script suffixes and unexpected script exhaustion cannot pass. Byte-identical duplicate proposals are separately tested at the terminal rejection boundary; differently worded structural duplicates exercise the scorer.

Output consists of thirteen bounded Q1 JSON outcomes followed by a bounded verification summary. It retains corpus/case/configuration/source identities and reports `mode: deterministic`. Exit 0 means every required case executed with its expected engineering result; exit 1 means verification or infrastructure failure, and exit 2 means invalid corpus input. Expected negatives retain their failed execution or failed assertion statuses. They are not rewritten into a uniformly successful Q4 result population. Nonempty scripted findings remain unadjudicated model observations. No source text, tool result, candidate prose, reasoning, SESSION, credential or raw exception is emitted.

Green deterministic verification is not a live model accuracy measurement or semantic certification of arbitrary prose. No paid/live calls are performed. Local filesystem staging remains the caller's R1 precondition; Linux mount origin is not inspected. #246 owns completed-session/fresh-process replay, #243 owns incremental publication scenarios, and #250 owns full R5 framework/AOT CI wiring.

The checked hashes and literal observation IDs are reviewable semantic fixture data. Ordinary verification never rewrites them or derives new expected results from a faulty execution. Any intentional fixture change requires review of the source, scripts, assertions and resulting identities together.
