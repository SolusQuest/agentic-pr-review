# R6 bounded live economics observations

[R6-V2 / #280](https://github.com/SolusQuest/agentic-pr-review/issues/280) now records two separately authorized finite campaigns. The September 22 Linux campaign stopped on a connection timeout. The September 23 Windows repeat reached DeepSeek successfully, completed and accepted its bootstrap, restored that history in a fresh worker, then stopped on invalid tool arguments during the first continuation. Both populations remain visible; neither campaign was retried automatically.

The repeat adds observed provider usage, cache partitions, reference pricing and one restored live entry. It does not demonstrate a successfully completed restored continuation, the full representative history sequence, repeated chains, incremental continuation, growth/capacity/reset behavior, or independently adjudicated model quality. The permitted bounded-insufficiency task outcome still leaves missing R6 exit evidence for [#281](https://github.com/SolusQuest/agentic-pr-review/issues/281). The [R5 quality result](r5-evaluation-results.md) and release/R7 boundary remain unchanged.

## September 23: authorized Windows repeat

The maintainer requested another verification after the timeout diagnosis, under the existing explicit local-key/paid-call grant. This authorized one additional selected campaign; it did not change or reclaim the previous campaign's allocation. The [new live report](../../runtime/tests/fixtures/agent/r6/plans/2026-09-23-observation/live.json), [keyless rehearsal](../../runtime/tests/fixtures/agent/r6/plans/2026-09-23-observation/dry-run.json), [normalized plan](../../runtime/tests/fixtures/agent/r6/plans/2026-09-23-observation/plan-selection.json), [reference tariff](../../runtime/tests/fixtures/agent/r6/plans/2026-09-23-observation/tariff.json), and [operator evidence](../../runtime/tests/fixtures/agent/r6/plans/2026-09-23-observation/operator-evidence.json) form a separate observation. The September 22 JSON artifacts are unchanged.

Credential-free diagnosis found that the WSL execution environment could open TCP but timed out during TLS, including when tested against a public DNS address with the original hostname and certificate validation. Windows TLS succeeded; immediately before the repeat, a credential-free request to the provider's models endpoint returned the expected HTTP 401 in 0.172 seconds. These probes establish current connectivity differences, not the exact historical network cause or authenticated model success. No system DNS, proxy, routing, endpoint, certificate-validation or runtime transport policy was changed. The repeat used the existing Windows framework host; the two environments are not an experimentally equivalent comparison.

### Execution identity and selection

The same clean source used for the first campaign was built for Windows. `EconomicsBuild.Current()` bound the actual evaluator, dependencies, host and core library; the Windows artifact's new build commitment changes the full C2 plan commitment. The workload, provider configuration and admitted tariff remain the same. The prepared artifact was not rebuilt between rehearsal and live execution, and native admission rechecked it before dispatch.

| Identity                  | September 23 value                                                 |
| ------------------------- | ------------------------------------------------------------------ |
| Execution source          | `4698a5fbe26bf40d55cd41e479bf8e2caacca1d2`                         |
| Execution tree            | `5e6faf28ce5c163c836e8cbf65fea1447f60e3fa` (clean)                 |
| Windows framework build   | `3cd6f6a36ea67dde640d08b6dacf81195e3294e36b12223457dba62715dd50b8` |
| Normalized C2 plan        | `35ccfe7c1e0696baa7a2e381804e7a64d277bd7aa85d1bb3e548c3f07f08d988` |
| Composite workload        | `97c1e7cca0f77348de0e62864865a4b57a3fdbcd53441a34f262a8ab22246208` |
| Provider configuration    | `a2c605221edbd73a239123b52538fa47b9bb3e7d2db72192dde60a919a9603df` |
| Admitted reference tariff | `3c67f355b27a71c23e00c0990dc3b63f8f3ea7a9f0c5c86fe245298557b02baf` |
| Live campaign             | `c2-74db1beed5584ce59736`                                          |

The selected schedule remains 23 evaluations: two three-stage replay chains, six tools-growth stages followed by an explicit reset and two fresh stages, and seven continuation-growth stages followed by an explicit reset and two fresh stages. The ceilings remain 184 calls, 6,029,312 input tokens, 753,664 output tokens, 6,782,976 combined tokens, 7,130 seconds and $2.713264 reference reservation. Each child has eight calls and 300 seconds; spacing is 5,000 milliseconds. Per-call bases remain 32,768 input tokens, 4,096 output tokens and 14,746 micro-USD. These are reservations under the reference formula, not invoice evidence or tokenizer pre-enforcement.

The official price/alias page was refreshed at approximately 06:44:50 UTC before dispatch. It still documents the retained `deepseek-v4-flash` alias as served by V4.1-Flash and peak USD hit/miss/output rates of $0.006/$0.30/$1.20 per million tokens. The observed response spelling was `deepseek-flash`, accepted by the unchanged adapter. This does not expose a backend snapshot. The original tariff retrieval metadata was preserved; the new freshness check is a separate operator record. Its effective interval, native execution-time applicability and invoice settlement remain unknown. [Official models and pricing](https://api-docs.deepseek.com/quick_start/pricing/).

### Actual traffic and stopping result

The live invocation ran from `2026-09-23T06:45:17.0225344Z` to `2026-09-23T06:45:42.4334542Z`, exited 1 with a complete admitted report, and required no outer cancellation. Slot 0 made four successful model calls and six tool calls, completed, prepared and accepted its session, and passed readback. Slot 1 restored that accepted session in a fresh worker and received another successful provider response, but the Agent rejected tool arguments with `agent_tool_arguments_invalid`; it executed no tools in that stage. The native entry rule stopped the campaign with `representative_history_insufficient`. Slots 2–22 remain unattempted.

All five model calls returned known input/output usage and cache partitions. The failure is therefore a tool-argument validation outcome after successful provider transport, not another connection timeout. The safe report does not retain raw arguments or identify the precise offending argument. No prompt, parser, tool schema, model alias, reset position or stopping rule was tuned, and no further paid invocation followed.

| Quantity                                             | September 23 observation                                                        |
| ---------------------------------------------------- | ------------------------------------------------------------------------------- |
| Scheduled / attempted / unattempted                  | 23 / 2 / 21                                                                     |
| Completed / failed / invalid evaluations             | 1 / 1 / 0                                                                       |
| Model invocations / transport sends / local refusals | 5 / 5 / 0                                                                       |
| Known-usage / unknown-usage sends                    | 5 / 0                                                                           |
| Missing child receipts                               | 0                                                                               |
| Prepared / accepted / read back / restored / reset   | 1 / 1 / 1 / 1 / 0                                                               |
| Committed child allocation                           | 16 calls; 524,288 input + 65,536 output tokens; $0.235936 reference reservation |
| Actual call reservation                              | 5 calls; 163,840 input + 20,480 output tokens; $0.073730 reference reservation  |
| Measured input / output / combined tokens            | 12,252 / 2,977 / 15,229                                                         |
| Cache-hit / cache-miss input tokens                  | 9,472 / 2,780                                                                   |
| Reference-priced input / output                      | $0.000890832 / $0.003572400                                                     |
| New campaign reference-priced total                  | $0.004463232                                                                    |
| Same-token all-miss counterfactual                   | $0.007248000                                                                    |
| Execution-time amount / invoice settlement           | Unavailable / not evidenced                                                     |

Usage and reference-price completeness are true for the new five-send population, including the failed continuation's returned usage. They do not assert completion of the selected workload or settlement. The all-miss amount is hypothetical repricing of the same tokens, not a measured cache-disabled run or causal savings estimate. Across both live campaigns there are 46 scheduled stages, three attempted stages, six sends and one unknown-usage send. The new known subtotal cannot make the combined historical cost complete; the original send's usage and charge remain unknown.

The completed bootstrap has `Passed` evidence and scenario status, but its one finding is `Unadjudicated`. The continuation is `Failed`, with failure source `Agent`, kind `MalformedOutput`, and evidence/scenario/model status `NotEvaluated`. These statuses do not establish independently confirmed model quality. One restored entry is observed; no restored continuation completed successfully.

### Verification, privacy and remaining coverage

The prior exact-source Linux framework/Native AOT gate remains Linux evidence. Supplementary Windows verification admitted all 82 cases and rejected 36 hostile-report mutations. The exact selected keyless rehearsal completed all 23 stages: 21 accepts, 23 readbacks, 17 restores, two capacity stops and two planned resets, with at least 5,000 milliseconds between stages. Native report/pricing readers rejected changed denominators, the complete journal reproduced its nested pricing through `economics-price`, and independent Decimal reconciliation checked identities, counters, partitions and amounts for both the rehearsal and live report. Simulated rehearsal usage remains outside the live population.

Two keyless operator-environment failures were preserved as diagnostic history. Long Windows TEMP paths reached 278 characters and failed state preparation; shorter owner-only ACL paths allowed the unchanged state implementation to pass. Windows later reused a worker PID within a completed gate case; the private supervisor retained observed child process handles until run end, and the final gate's 20-worker full case had 20 distinct PIDs and startup nonces. That gate case is distinct from the 23-slot selected rehearsal. No source, report, oracle or global OS policy was modified to obtain these results. The failed diagnostic run's retained synthetic state is restricted and excluded from the clean-run claims.

The live supervisor verified both ready workers, all three externally observed direct children and the parent had exited, and the owned private TEMP was empty. Process handles were released. The fresh parent and child environments excluded GitHub/Actions credentials; the existing validated child secret ingress was used. No raw provider traffic, private IPC, session plaintext or key is published. Operator elapsed time includes post-exit checks, whereas UTC end is sampled after process/output completion; these measurements are not asserted equal. Operator evidence remains an attestation, not an independently authenticated execution or billing receipt.

Full same-head continuation, incremental continuation, repeated chains, live growth/capacity/reset, per-call segmented-prefix continuity, controlled cache-policy comparison and independent human adjudication remain unproven. The next bounded engineering action is an offline investigation of the existing tool-argument admission contract and representative malformed-output cases, using public-safe synthetic inputs. The retained safe diagnostic does not identify the exact offending argument, so that investigation must not claim to replay an unavailable payload. Any later live observation needs a new explicit finite selection; no third campaign is included here.

## September 22: original observation retained

The following sections describe the original Linux campaign and its historical next action. Its five JSON artifacts remain unchanged; the subsequent authorized repeat is documented above.

[R6-V2 / #280](https://github.com/SolusQuest/agentic-pr-review/issues/280) produced a bounded insufficiency result on 2026-09-22. One authorized live campaign attempted its first replay stage, recorded a `connect_timeout`, and stopped with `representative_history_insufficient`. The other 22 selected stages remain explicitly unattempted. No completed or restored live history, admitted provider usage, response-model identity, or priced amount was obtained. Worker and private-state cleanup passed.

This completes the leaf's permitted execution/report outcome. It leaves representative live restore and live economics evidence missing from R6's exit criteria. It does not change the [R5 quality result](r5-evaluation-results.md), establish savings or model quality, or authorize release/R7 promotion. [#281](https://github.com/SolusQuest/agentic-pr-review/issues/281) must retain these missing exit conditions when assessing the handoff.

### Evidence and immutable execution identity

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

### Authorization and finite selection

The maintainer explicitly authorized the local DeepSeek key and paid calls on 2026-09-22, with budget choices delegated to the operator. The operator recorded the following concrete selection before credential access. Formal plan review, the exact-source offline gate and the final-plan keyless rehearsal passed before the one live invocation. These numeric choices were made by the operator under that grant.

| Selected workload                                                                            | Zero-based slots | Count |
| -------------------------------------------------------------------------------------------- | ---------------- | ----: |
| Replay bootstrap, same-head continuation, incremental continuation; two independent chains   | 0–5              |     6 |
| Tools growth through its authored capacity position, explicit reset, two fresh stages        | 6–13             |     8 |
| Continuation growth through its authored capacity position, explicit reset, two fresh stages | 14–22            |     9 |

The selection reserved 23 evaluations, 184 model calls, 6,029,312 input tokens, 753,664 output tokens and 6,782,976 combined tokens. Each child had the existing eight-call allocation, a 300-second window, and per-call bases of 32,768 input tokens, 4,096 output tokens and 14,746 micro-USD. Spacing was 5,000 ms after completion; the native campaign deadline was 7,130 seconds, including setup/supervision and spacing. The full reference reservation was 2,713,264 micro-USD ($2.713264). Unused allocations were not recycled.

The existing `stop_remaining_tail` rule and structural entry checks governed execution. Concrete Agent/tool-protocol, receipt, usage and state failures follow the native stopping path. Scored evidence, scenario and quality eligibility remain separate; the runner does not add a new automatic quality threshold. Resets occur only after the selected capacity stop is evidenced. There was no live retry, alternate endpoint, proxy-policy change, reset relocation, request tuning or second campaign. Private launcher preflight errors were corrected before any campaign process started; they produced no provider calls.

### Provider and reference-price context

Official documentation was checked on 2026-09-22, with pricing refreshed again shortly before dispatch. The requested alias remained `deepseek-v4-flash`, which the provider documents as routed to DeepSeek-V4.1-Flash. No response-model spelling was admitted in this observation, so documented routing is not an observed backend identity. The selected peak USD reference rates per million tokens were $0.006 cache-hit input, $0.30 cache-miss input and $1.20 output. The tariff has an unknown effective period, no response-model filter, and nine-place half-even arithmetic. Native execution-time applicability remains unknown and settlement remains unevidenced. [Official models and pricing](https://api-docs.deepseek.com/quick_start/pricing/).

The unchanged enabled/high thinking path was retained. Cache behavior is best effort; `user_id` isolation is not a cache-off control. No such control or request field was introduced. There is no controlled cache-policy comparison or causal savings claim. [Thinking mode](https://api-docs.deepseek.com/guides/thinking_mode/), [context caching](https://api-docs.deepseek.com/guides/kv_cache/), [isolation](https://api-docs.deepseek.com/quick_start/rate_limit/).

### Live result and accounting

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

### Validation, cleanup and privacy

Before live execution, `npm run check` passed 764 tests in 42 files and `npm run dist:check` passed on the exact merged source. `bash runtime/scripts/verify-r6-economics.sh all` verified 82 cases and 37 adversarial probes in each of framework and actual Native AOT modes, then passed parity and private-state cleanup. The execution build succeeded with a NuGet vulnerability-database access warning (`NU1900`) and the existing JsonSchema.Net AOT advisory (`IL3058`); the actual Native AOT gate passed independently.

The final-plan loopback rehearsal ran from `06:32:19.515198Z` to `06:34:24.412960Z` on the same date. All 23 stages executed: 21 accepted completions, 17 fresh-child restorations, two capacity stops and two planned resets. All 23 stages had readback, the minimum inter-stage interval was 5,000 ms, and all 23 ready workers exited. Its 45 simulated sends and synthetic prices are rehearsal evidence only and are excluded from the live population.

Both saved reports were admitted through the unchanged `EconomicsReportJson.Read` and `PricingJson.Read` methods using an external temporary harness. Changing the scheduled denominator caused rejection. Extracted journals were admitted by `economics-price --journal <journal> --tariff <tariff>`, whose complete output matched each nested pricing document. Independent Decimal half-even calculations checked all available amounts, call/token/cache totals and the exact selected-plan projection. The formatted public report files were read back with those same native readers after publication formatting. The report/evidence delivery also passed `npm run check`.

The outer launcher supplied a fresh allowlisted environment and private working/TEMP directories without GitHub or Actions credentials. It transferred the authorized provider key privately into the existing ingress; the runtime forwarded it only after validated child readiness. Raw HTTP traffic, private IPC, SESSION plaintext and keys were not captured as report artifacts. The workload was the existing public-safe synthetic replay/growth corpus.

For each invocation, independent operator checks ran before returning: every reported ready worker PID was absent, the owned process group was absent, and the owned TEMP directory was empty after its observed private root disappeared. The live worker count was one. No unexplained retained state was deleted to manufacture a passing result. The designated public artifacts were inspected for credentials, local paths, private-frame fields and raw request/response/session content; only admitted safe reports, selected commitments, reference tariff and bounded operator metadata are published. The operator evidence records these observations; it is not a provider-origin attestation.

### Unproven coverage and bounded next action

Representative live tool-bearing completion and restoration, same-head/incremental continuation, repeated chains, growth/capacity/reset behavior, cache partitions, reference-priced live traffic and comparable model-quality outcomes remain unproven. The public `initial_prefix_sha256` is the digest of the initial provider-projected whole request. It neither proves transmission nor exposes per-call segmented-prefix continuity; deterministic P2 verification must not be relabeled as live evidence.

The next bounded diagnostic should verify credential-free DNS/TLS connectivity from the intended execution environment to the existing direct endpoint, without a chat-completion request or policy change. Retain this campaign and its unknown-cost population when deciding whether to select any later finite observation. A later invocation has its own campaign/state/budget identities and cannot be described as continuation of this failed bootstrap. If the existing direct path remains unavailable, preserve the insufficiency disposition for #281 rather than relaxing the runner, changing the provider or inferring R6 readiness from the synthetic rehearsal.
