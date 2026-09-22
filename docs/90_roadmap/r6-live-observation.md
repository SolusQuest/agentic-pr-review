# R6 bounded live economics observation

[R6-V2 / #280](https://github.com/SolusQuest/agentic-pr-review/issues/280) produced a bounded insufficiency result on 2026-09-22. One authorized live campaign attempted its first replay stage, recorded a `connect_timeout`, and stopped with `representative_history_insufficient`. The other 22 selected stages remain explicitly unattempted. No completed or restored live history, admitted provider usage, response-model identity, or priced amount was obtained. Worker and private-state cleanup passed.

This completes the leaf's permitted execution/report outcome. It leaves representative live restore and live economics evidence missing from R6's exit criteria. It does not change the [R5 quality result](r5-evaluation-results.md), establish savings or model quality, or authorize release/R7 promotion. [#281](https://github.com/SolusQuest/agentic-pr-review/issues/281) must retain these missing exit conditions when assessing the handoff.

## Evidence and immutable execution identity

The [live report](../../runtime/tests/fixtures/agent/r6/plans/2026-09-22-observation/live.json), [loopback rehearsal](../../runtime/tests/fixtures/agent/r6/plans/2026-09-22-observation/dry-run.json), [normalized plan](../../runtime/tests/fixtures/agent/r6/plans/2026-09-22-observation/plan-selection.json), [tariff](../../runtime/tests/fixtures/agent/r6/plans/2026-09-22-observation/tariff.json), and [operator evidence](../../runtime/tests/fixtures/agent/r6/plans/2026-09-22-observation/operator-evidence.json) preserve the selected and actual populations. The plan projection omits execution-local paths and is not a directly runnable CLI input. The operator record is an execution attestation, not an independent producer-authenticated experimental preregistration or billing receipt.

The unchanged framework evaluator ran from a clean isolated Linux checkout of merged PR #289. These identities belong to the executed source; the later documentation/evidence commit is not substituted for them.

| Identity               | Value                                                              |
| ---------------------- | ------------------------------------------------------------------ |
| Source commit          | `4698a5fbe26bf40d55cd41e479bf8e2caacca1d2`                         |
| Source tree            | `5e6faf28ce5c163c836e8cbf65fea1447f60e3fa`                         |
| Execution build        | `a0c6aafbdcd5c374126e36a434149e81ed3a3bd4b07f05e4cbd6499a5ffc9dee` |
| Replay corpus          | `f32f29e8c121ca3bca94e59c2f6cfaabaaff3fea2755fd24319ce8233aeb3c68` |
| Growth corpus          | `7db3bb53f2a9453556307b2d11d00be106253c004cd29a7c5796b790b763f333` |
| Composite workload     | `97c1e7cca0f77348de0e62864865a4b57a3fdbcd53441a34f262a8ab22246208` |
| Normalized plan        | `fcc3c7ec685b921e1cf5efe222e3eb5fec81b240cbabc754e3264dc878b555b6` |
| Provider configuration | `a2c605221edbd73a239123b52538fa47b9bb3e7d2db72192dde60a919a9603df` |
| Admitted tariff        | `3c67f355b27a71c23e00c0990dc3b63f8f3ea7a9f0c5c86fe245298557b02baf` |

The build digest covers the evaluator, runtime and selected dependencies, actual .NET host, runtime configuration, dependency metadata and core library through `EconomicsBuild.Current()`. It is not just a source-tree digest. Both runs used the same prepared artifact and selected plan without an intervening rebuild. Original CLI report byte hashes are retained in the operator evidence, alongside hashes of the formatted published files; formatting does not rebind execution provenance.

## Authorization and finite selection

The maintainer explicitly authorized the local DeepSeek key and paid calls on 2026-09-22, with budget choices delegated to the operator. The operator recorded the following concrete selection before credential access. Formal plan review, the exact-source offline gate and the final-plan keyless rehearsal passed before the one live invocation. These numeric choices were made by the operator under that grant.

| Selected workload                                                                            | Zero-based slots | Count |
| -------------------------------------------------------------------------------------------- | ---------------- | ----: |
| Replay bootstrap, same-head continuation, incremental continuation; two independent chains   | 0–5              |     6 |
| Tools growth through its authored capacity position, explicit reset, two fresh stages        | 6–13             |     8 |
| Continuation growth through its authored capacity position, explicit reset, two fresh stages | 14–22            |     9 |

The selection reserved 23 evaluations, 184 model calls, 6,029,312 input tokens, 753,664 output tokens and 6,782,976 combined tokens. Each child had the existing eight-call allocation, a 300-second window, and per-call bases of 32,768 input tokens, 4,096 output tokens and 14,746 micro-USD. Spacing was 5,000 ms after completion; the native campaign deadline was 7,130 seconds, including setup/supervision and spacing. The full reference reservation was 2,713,264 micro-USD ($2.713264). Unused allocations were not recycled.

The existing `stop_remaining_tail` rule and structural entry checks governed execution. Concrete Agent/tool-protocol, receipt, usage and state failures follow the native stopping path. Scored evidence, scenario and quality eligibility remain separate; the runner does not add a new automatic quality threshold. Resets occur only after the selected capacity stop is evidenced. There was no live retry, alternate endpoint, proxy-policy change, reset relocation, request tuning or second campaign. Private launcher preflight errors were corrected before any campaign process started; they produced no provider calls.

## Provider and reference-price context

Official documentation was checked on 2026-09-22, with pricing refreshed again shortly before dispatch. The requested alias remained `deepseek-v4-flash`, which the provider documents as routed to DeepSeek-V4.1-Flash. No response-model spelling was admitted in this observation, so documented routing is not an observed backend identity. The selected peak USD reference rates per million tokens were $0.006 cache-hit input, $0.30 cache-miss input and $1.20 output. The tariff has an unknown effective period, no response-model filter, and nine-place half-even arithmetic. Native execution-time applicability remains unknown and settlement remains unevidenced. [Official models and pricing](https://api-docs.deepseek.com/quick_start/pricing/).

The unchanged enabled/high thinking path was retained. Cache behavior is best effort; `user_id` isolation is not a cache-off control. No such control or request field was introduced. There is no controlled cache-policy comparison or causal savings claim. [Thinking mode](https://api-docs.deepseek.com/guides/thinking_mode/), [context caching](https://api-docs.deepseek.com/guides/kv_cache/), [isolation](https://api-docs.deepseek.com/quick_start/rate_limit/).

## Live result and accounting

The live invocation ran from `2026-09-22T06:40:06.385404Z` to `2026-09-22T06:40:22.613395Z`. Its CLI exit was 1, with a complete admitted failure report. The outer supervisor did not cancel or forcibly terminate it. Slot 0 (`c2-replay-0`) reached validated child readiness, then its first chat invocation failed with `transport_outcome=connect_timeout`, `chat_outcome=threw`, and `usage_status=unknown`. The Agent diagnostic was `agent_chat_failed`; there were no tool results or prepared, accepted or restored sessions. Slots 1–22 remain unattempted in both report and journal.

The single `actual_sends` count is the transport's dispatch observation. It does not prove that the provider accepted or billed the request. The safe evidence does not identify a DNS, routing, TLS, firewall or provider root cause, nor does it show an unsupported model response. The unchanged transport uses direct connections and a 15-second connect timeout.

| Quantity                                            | Live observation                                                               |
| --------------------------------------------------- | ------------------------------------------------------------------------------ |
| Scheduled / attempted / unattempted                 | 23 / 1 / 22                                                                    |
| Completed / failed / invalid evaluations            | 0 / 1 / 0                                                                      |
| Chat invocations / transport sends / local refusals | 1 / 1 / 0                                                                      |
| Known-usage / unknown-usage sends                   | 0 / 1                                                                          |
| Missing child receipts                              | 0                                                                              |
| Prepared / accepted / read back / restored / reset  | 0 / 0 / 0 / 0 / 0                                                              |
| Committed child allocation                          | 8 calls; 262,144 input + 32,768 output tokens; $0.117968 reference reservation |
| Actual call reservation                             | 1 call; 32,768 input + 4,096 output tokens; $0.014746 reference reservation    |
| Measured input/output tokens                        | Unknown; the known-only subtotal counters are zero                             |
| Cache-hit / cache-miss input                        | Unknown (`null`)                                                               |
| Observed reference-price subtotal / total           | Unknown (`null`)                                                               |
| Same-token all-miss counterfactual total            | Unknown (`null`)                                                               |
| Execution-time amount / invoice settlement          | Unavailable / not evidenced                                                    |

The complete child receipt establishes which call failed and permits a full-population T2 journal and T3 pricing document. Consequently `c1_handoff=available` means the evidence can be admitted downstream; it does not mean usage or money is complete. Both completeness flags are false, and no smaller complete population replaces the failed call or unattempted tail. Zero known-token subtotals do not imply zero consumption or zero charge.

The evaluation remains `Failed`, with evidence, scenario and model statuses `NotEvaluated`. There are zero quality-eligible completions and no independent human confirmation. Its retained evaluation failure-source/kind fields are `Unknown`; the separate transport observation supplies the bounded connection-timeout diagnostic without rewriting that projection.

## Validation, cleanup and privacy

Before live execution, `npm run check` passed 764 tests in 42 files and `npm run dist:check` passed on the exact merged source. `bash runtime/scripts/verify-r6-economics.sh all` verified 82 cases and 37 adversarial probes in each of framework and actual Native AOT modes, then passed parity and private-state cleanup. The execution build succeeded with a NuGet vulnerability-database access warning (`NU1900`) and the existing JsonSchema.Net AOT advisory (`IL3058`); the actual Native AOT gate passed independently.

The final-plan loopback rehearsal ran from `06:32:19.515198Z` to `06:34:24.412960Z` on the same date. All 23 stages executed: 21 accepted completions, 17 fresh-child restorations, two capacity stops and two planned resets. All 23 stages had readback, the minimum inter-stage interval was 5,000 ms, and all 23 ready workers exited. Its 45 simulated sends and synthetic prices are rehearsal evidence only and are excluded from the live population.

Both saved reports were admitted through the unchanged `EconomicsReportJson.Read` and `PricingJson.Read` methods using an external temporary harness. Changing the scheduled denominator caused rejection. Extracted journals were admitted by `economics-price --journal <journal> --tariff <tariff>`, whose complete output matched each nested pricing document. Independent Decimal half-even calculations checked all available amounts, call/token/cache totals and the exact selected-plan projection. The formatted public report files were read back with those same native readers after publication formatting. The report/evidence delivery also passed `npm run check`.

The outer launcher supplied a fresh allowlisted environment and private working/TEMP directories without GitHub or Actions credentials. It transferred the authorized provider key privately into the existing ingress; the runtime forwarded it only after validated child readiness. Raw HTTP traffic, private IPC, SESSION plaintext and keys were not captured as report artifacts. The workload was the existing public-safe synthetic replay/growth corpus.

For each invocation, independent operator checks ran before returning: every reported ready worker PID was absent, the owned process group was absent, and the owned TEMP directory was empty after its observed private root disappeared. The live worker count was one. No unexplained retained state was deleted to manufacture a passing result. The designated public artifacts were inspected for credentials, local paths, private-frame fields and raw request/response/session content; only admitted safe reports, selected commitments, reference tariff and bounded operator metadata are published. The operator evidence records these observations; it is not a provider-origin attestation.

## Unproven coverage and bounded next action

Representative live tool-bearing completion and restoration, same-head/incremental continuation, repeated chains, growth/capacity/reset behavior, cache partitions, reference-priced live traffic and comparable model-quality outcomes remain unproven. The public `initial_prefix_sha256` is the digest of the initial provider-projected whole request. It neither proves transmission nor exposes per-call segmented-prefix continuity; deterministic P2 verification must not be relabeled as live evidence.

The next bounded diagnostic should verify credential-free DNS/TLS connectivity from the intended execution environment to the existing direct endpoint, without a chat-completion request or policy change. Retain this campaign and its unknown-cost population when deciding whether to select any later finite observation. A later invocation has its own campaign/state/budget identities and cannot be described as continuation of this failed bootstrap. If the existing direct path remains unavailable, preserve the insufficiency disposition for #281 rather than relaxing the runner, changing the provider or inferring R6 readiness from the synthetic rehearsal.
