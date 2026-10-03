# Runtime CI parallel validation

[Issue #369](https://github.com/SolusQuest/agentic-pr-review/issues/369) moves the existing R2 and generic R4 proofs out of the serial Runtime job. Each worker owns a checkout, complete build, process tree and temporary evidence roots. Current-source jobs select the PR head SHA or triggering push SHA explicitly; the historical trusted-proof job keeps its immutable source pin. Ordinary CI uses synthetic providers and read-only permissions.

| Job                                                                 | Retained purpose                                                                                                                                                                        |
| ------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `runtime-core`                                                      | Fixture admission on PRs, Debug tests, framework/AOT bootstrap smoke and R3 deterministic/AOT transport and independent-root proofs.                                                    |
| `r2-agent-loop`                                                     | One unchanged `verify-agent-loop.sh all`: Release tests, framework and AOT Agent/SESSION/STATE proofs, positive/negative/current R7 capacity cases and replacement-inventory assertion. |
| `r4-action-host`                                                    | Four separately scheduled `{framework,aot}` × `{first,second}` workers. Each invokes the unchanged verifier, including its own complete build and clean-source guard.                   |
| `runtime`                                                           | Stable aggregate for exactly core, R2 and the R4 matrix. It uses `always()` and rejects every dependency result except exact success.                                                   |
| `r5-evaluation-gate`                                                | Additive deterministic quality, replay, growth, reset and dry-run coverage with actual framework/AOT semantic parity.                                                                   |
| `r6-economics-gate`                                                 | Additive offline economics/accounting/lifecycle/failure coverage with actual framework/AOT execution and syscall audit.                                                                 |
| `trusted-proof-payload`                                             | Historical source `5b5769753653bb3fd3e68cf8b7bb88a1bd350613`, two clean production/verifier builds and equal supplemental receipts.                                                     |
| `trusted-proof-payload-v2-first`, `trusted-proof-payload-v2-second` | Two independent current-source workers, each executing one unchanged complete production/verifier Native AOT proof with its own initialization and clean source.                        |
| `trusted-proof-payload-v2`                                          | Stable aggregate requiring both successful workers and exactly their bounded first/second receipts, with independently verified source and compiled-source identities.                  |
| `integration`                                                       | Trusted-live policy dry-run and host/runtime integration matrix on the selected source.                                                                                                 |

R5, R6, integration and both trusted-proof checks remain independent and required before PR readiness. They are outside the stable `runtime` aggregate's original responsibility. Older phase names do not establish that a gate is obsolete. Debug and Release coverage, the two independent builds per proof mode, and historical versus current-source proof authority remain distinct.

## Workflow and failure validation

Run `node scripts/verify-runtime-ci-workflow.mjs --self-test` after `npm ci`. Runtime core and the Runtime architecture test execute this check. It parses the actual YAML, expands the closed four-worker matrix, checks source selection, setup/proof ordering, clean-source checks, independent scheduling, permissions, preserved commands and aggregate wiring. Mutation tests remove or duplicate workers, change modes, conditionally skip/mask proofs, serialize/throttle jobs, omit aggregate dependencies, change source identity, weaken V2's own checkout and introduce secret/write/artifact-reuse surfaces.

The checker executes the actual inline aggregate programs extracted from the workflow. All-success input passes; failure, cancelled, skipped, missing, malformed, empty and unknown results reject for each of the original three dependencies, including the R4 matrix result, and for each V2 worker independently. `fail-fast:false` allows unaffected R4 matrix siblings to finish; no job or proof step can swallow failures through `continue-on-error`. The original `runtime` aggregate checks out no repository code and still has exactly core/R2/R4 scope. Both aggregates use job-level `always()`. A whole-workflow cancellation or hard platform kill can prevent a pending aggregate from starting; an absent/cancelled check supplies no success evidence. Ordinary successful remote CI is still required to prove actual Linux builds and the GitHub scheduler, rather than treating local semantic tests as Linux proof.

Each V2 worker uploads only its single receipt line, bounded by the existing 32,768-byte limit. Immutable artifact names identify first/second and the workflow run ID/attempt. The V2 aggregate first requires both exact successful dependency results, then independently checks out the event-selected commit, derives its tree, and downloads all artifacts in that attempt's receipt namespace into separate directories. It rejects missing/duplicate/extra artifact directories, unexpected files, nonregular files or links, invalid UTF-8, invalid prefix/single-terminal-newline framing, invalid JSON and any of the four source/compiled-source identity substitutions. The actual inline filesystem program is exercised with distinct valid build receipts and concrete negative evidence. Linux CI additionally executes file-symlink negatives; ordinary Windows hosts can create directory junctions but require additional privilege for file symlinks.

Artifact actions are admitted only for this receipt boundary: upload in the two V2 workers and download in their aggregate, with exact paths and current-run inputs. The existing prohibition on compiled payload/verifier output reuse remains active in every gate. The selected [official upload action](https://github.com/actions/upload-artifact/blob/v7/action.yml) archives the one file without overwrite, and the [official download action](https://github.com/actions/download-artifact/blob/v8/action.yml) preserves separate artifact directories and rejects digest mismatches. No additional GitHub permission, cross-run token, compiled cache, proof-log transfer or production receipt protocol is introduced. A partial rerun lacking the successful sibling's current-attempt receipt fails closed; a complete new attempt supplies both receipts.

Also run `npm run check`, `npm run dist:check` and the affected Runtime architecture tests. Before Ready, all existing workflows must pass on the exact pushed head, all admitted feedback must be handled and Relay must approve that head. Reconcile current merged `origin/main` before timing and final acceptance; changed heads require fresh validation and review. The coordinator owns merge and the successful post-merge main run.

## Comparable timing evidence

The issue-cited successful PR runs are the before anchors. The first includes the C4 snapshot-capacity implementation and is the primary comparison for a post-change head containing merged C4; the second supplies earlier context. Both used the serial Runtime topology.

| Before run                                                                                        | Elapsed to last job completion |   Longest active job | Active runner minutes |
| ------------------------------------------------------------------------------------------------- | -----------------------------: | -------------------: | --------------------: |
| [37099787929 / PR #368](https://github.com/SolusQuest/agentic-pr-review/actions/runs/37099787929) |                      94.83 min | `runtime`, 94.78 min |            148.00 min |
| [37088629141 / PR #367](https://github.com/SolusQuest/agentic-pr-review/actions/runs/37088629141) |                      91.95 min | `runtime`, 91.92 min |            142.18 min |

Collect one ordinary successful PR run after the change. Publish its exact run/head, before/after metrics, whether the approximately 30–35 minute target was achieved, and the remaining bottleneck in the PR Validation section and task report. Setup duplication and queue pressure can prevent the target; estimates supply no acceptance evidence. A real correction may require another ordinary head/run, but repeated performance-only experiments are outside this first validation.

```bash
gh api repos/SolusQuest/agentic-pr-review/actions/runs/RUN_ID > run.json
gh api --paginate --slurp 'repos/SolusQuest/agentic-pr-review/actions/runs/RUN_ID/jobs?per_page=100&filter=latest' > jobs.json
node scripts/verify-runtime-ci-timing.mjs --self-test
node scripts/verify-runtime-ci-timing.mjs run.json jobs.json
```

The read-only timing helper defaults to a terminal successful ordinary PR run and requires distinct successful jobs. For actual successful post-merge `push` runs on `main`, use `node scripts/verify-runtime-ci-timing.mjs --main run.json jobs.json`. Its output preserves the actual event, branch, exact head and run attempt; default mode rejects push and main mode rejects other events/branches. It reports:

- Total elapsed: workflow `created_at` to the latest job `completed_at`. This excludes mutable run-update timestamps and includes scheduling/setup/aggregate delay.
- Longest active job: maximum job `started_at` to `completed_at`; active means allocated-runner wall time, including setup and process waits.
- Queue: each job's `created_at` to `started_at`. The separate workflow-to-start value also includes dependency wait, particularly for the aggregate.
- Active critical path: the maximum of the independent gates, the longest core/R2/R4 worker plus its aggregate duration, and the longest V2 worker plus the V2 aggregate duration when split. Queue-free path duration is separate from the observed first-start to last-finish span and the workflow-to-aggregate completion interval; dependency wait is not counted as allocated runner time.
- Runner minutes: sum of actual active job durations, including repeated setup and the aggregate. This is elapsed resource time, not billing or minute-rounded usage.
- Concurrency: maximum overlap of half-open job intervals, with the four R4 workers reported separately. Split V2 evidence additionally requires two distinct positive runner IDs and actual overlap of the two successfully executed full-proof steps, and reports their overlap seconds and worker concurrency. Observed overlap does not establish an account-wide concurrency entitlement.
- Setup: job start to its first existing substantive proof/verification step, including any new topology check. Compare per-worker setup and proof duration when diagnosing the new bottleneck.

## Current-head V2 comparison for #371

The accepted #369 topology is the immediate before baseline for V2 splitting. The original serial table above remains historical CI1 evidence.

| Accepted CI1 baseline                                                                                 | Exact source                               | Event          | Total elapsed | V2 active job | Active runner minutes |
| ----------------------------------------------------------------------------------------------------- | ------------------------------------------ | -------------- | ------------: | ------------: | --------------------: |
| [37130976490 / PR #370](https://github.com/SolusQuest/agentic-pr-review/actions/runs/37130976490)     | `74000d573f0815064c05d0fe45f8671e0bc7e493` | `pull_request` |     26.30 min |     26.23 min |            159.60 min |
| [37134705553 / merged main](https://github.com/SolusQuest/agentic-pr-review/actions/runs/37134705553) | `7cc491fd6ca2387cffa78c8d71c47aede8f070d1` | `push/main`    |     26.68 min |     26.63 min |            143.17 min |

Retain complete run/jobs evidence and publish a successful exact-head #371 PR measurement in its PR Validation section and task report. It must demonstrate both unchanged full proofs on distinct runners, actual proof overlap, receipt transport and stable aggregate success, along with every retained check. Include per-proof active/setup/queue time, aggregate duration/completion, total elapsed/runner minutes and the new longest path. The coordinator separately retains successful post-merge main validation and timing before completing #371; pre-merge Ready cannot supply that future run.

V2 around 13–14 minutes and overall Runtime CI around 18–20 minutes are estimates to measure. The retained R4 path, queue pressure or extra initialization can keep total elapsed above the estimate; report that outcome and its bottleneck without weakening coverage. PR and main baselines have different heads/events, so these observations establish neither a controlled causal comparison nor billing savings. Runner minutes may rise while elapsed time falls.

Further R2 subdivision needs an explicit replacement-inventory gate. Conditional routing, old-gate retirement or scheduling changes, compiled-artifact reuse, authoritative proof caching and broad setup deduplication are separate proposals requiring their own coverage and evidence assessment; this change implements none of them.
