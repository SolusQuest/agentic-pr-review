# R5 session growth and reset results

Issue [#249](https://github.com/SolusQuest/agentic-pr-review/issues/249) records the measured session operating envelope produced by the merged R5-S1 growth matrix ([#247](https://github.com/SolusQuest/agentic-pr-review/issues/247)) and R5-S2 reset evidence ([#248](https://github.com/SolusQuest/agentic-pr-review/issues/248)). This document reports executed observations on one exact source revision. It changes no capacity, reset, privacy, or release contract and grants no R6/R7 graduation.

## Provenance

| Field           | Value                                                                                                                                                                             |
| --------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Source commit   | `55656c769ea09338896506037ebc9bb717704de9`                                                                                                                                        |
| Source tree     | `ad2ad98cbdea18f65ca686db5c1b737d5437f7e3`                                                                                                                                        |
| Source clean    | `true` at capture time (empty `git status --porcelain`)                                                                                                                           |
| Execution mode  | Windows x64, framework-dependent `net10.0` Release build; Linux framework/Native AOT parity is issue [#250](https://github.com/SolusQuest/agentic-pr-review/issues/250)'s CI gate |
| Growth corpus   | `f9f3ac942f8328650a398a595b4768fc6f1bab934a39a45eceb5d3f58f7dba8e`                                                                                                                |
| R1 seed corpus  | `7db3bb53f2a9453556307b2d11d00be106253c004cd29a7c5796b790b763f333`                                                                                                                |
| Growth schedule | `attempt_limit` 65 (`AgentSessionFormat.MaximumCompletedRuns + 1`), no injected fault                                                                                             |

Configuration identities emitted by the executed evidence:

| Identity                                                                                            | Value                                                              |
| --------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------ |
| S1 growth configuration (`configuration_sha256`, shared by all four profiles, `deterministic` mode) | `f8781a29c69fc94871b077cd56016c0f4143f3e7a66144aef32c805003a3dc72` |
| S2 policy digest (`policySha256`, all phases)                                                       | `30bf76478209c12ff7efb54955b57233070006afdfabe3cbcb2abc6ddca97557` |
| S2 limits digest (`limitsSha256`, all phases)                                                       | `587f64e18c085116482ac10369858f2f563de207b8dce215725194f52fff68b6` |
| S2 toolset digest (`toolsetSha256`, all phases)                                                     | `66ba6ceb1ddd5c2aef95e613bef43b4675c986d4756c05bf350a09badb9c535c` |

Each lifecycle phase carried a distinct adapted script digest (`scriptSha256`); the seed corpus digest alone does not identify the adapted workload:

| Phase | Adapted script digest                                              | Phase     | Adapted script digest                                              |
| ----- | ------------------------------------------------------------------ | --------- | ------------------------------------------------------------------ |
| 0     | `fbeb6ebd184ec55f4db239e0530033329ee977bab9fcffba613f8be600e56da1` | 5         | `acaa3949a50a3d5c35d3c4ecb7a93099d4519b2e88885832abadf06e6ff20cfa` |
| 1     | `0419694dca94848dbf5c33eba7df63887d2c26d80d4f8b4264d604ea2c60af24` | 6         | `74058ad7c98475aff72dd3ac626df2efda4b0a0a5ad83e3f9db9ab13de068b8e` |
| 2     | `6c71a432a760a96657295716dfcf4b497548ec38dfba17a9bdf4f29d3278fc10` | 7 (reset) | `ecd3cd1adace143ded1e2b859ce0a9adb340531b43896e24000c4465071598de` |
| 3     | `c1b96eac95deaa801f11b9d268f92f09e16ef92a29ac7323ec95761d39490256` | 8         | `dbbda72a9ce9500167af6b0f0e971514c2440b480de97aa3c369977276fb6291` |
| 4     | `5c8784cebdf38db052d78f64a357330bb8375ddb9af10e1a56d358cada1bf9d5` |           |                                                                    |

Commands executed from the repository root, each with exit status 0:

```sh
dotnet run --project runtime/tests/ReviewEvaluationFixture/AgenticPrReview.Runtime.ReviewEvaluationFixture.csproj --configuration Release -- replay --bundle runtime/tests/fixtures/agent/r5/growth
dotnet test runtime/tests/AgenticPrReview.Runtime.Tests/AgenticPrReview.Runtime.Tests.csproj --configuration Release --nologo --filter 'FullyQualifiedName~R5CapacityResetTests|FullyQualifiedName~R5ResetHandoffTests' --logger 'console;verbosity=detailed'
dotnet run --project runtime/tests/ReviewEvaluationFixture/AgenticPrReview.Runtime.ReviewEvaluationFixture.csproj --configuration Release -- reset --fixture self-test
```

The filtered test run passed 39/39 cases. The growth command was executed twice: both reports carried identical `normalized_sha256` (`352f83af7d400c73c3457f67b216d26dd76101fc509aac58c0823a88271648c8`) and identical `(terminal_stage, terminal_code, row count)` tuples for all four profiles. `R5SessionGrowthTests.MatrixMeasuresActualArtifactsAndReproducesFirstBoundary` remains the standing repository regression for that cross-execution agreement.

## Coverage

| Evidence                         | Exercises                                                                                                                   | Result                                                                            |
| -------------------------------- | --------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------- |
| `short` profile                  | Minimal finish-only review per accepted run                                                                                 | 20 accepted runs, then construction rejection                                     |
| `tools` profile                  | Eight real `read_file` results per accepted run                                                                             | 5 accepted runs, then request-admission rejection                                 |
| `continuation` profile           | One file read plus compact generated reasoning per run                                                                      | 6 accepted runs, then continuation-cap rejection                                  |
| `updates` profile                | One file read per run, alternating `same_head` and `verified_ahead`                                                         | 12 accepted runs, then request-admission rejection                                |
| `r5-reset-capacity-v1` lifecycle | Production-Host growth with the tool-heavy workload, predecessor readback, authorized reset, independent continuation       | Capacity rejection at phase 5, reset accepted at phase 7, continuation at phase 8 |
| `r5-reset-owner-v1` probe        | Independent executable owner cases for authorization, reset, publication target and acceptance                              | 8/8 cases passed                                                                  |
| `R5ResetHandoffTests`            | Reset intent/successor persistence, target substitution/absence/expiry, unresolved publication, old-epoch replay, tampering | All passed inside the 39-case filtered run                                        |

## Measured envelope

For every profile the table reports the last accepted state separately from the rejected attempt. A rejected attempt has no accepted generation or accepted sizes; `predecessor_preserved` was `true` on every rejection.

| Profile        | Accepted runs | Terminal stage/code (classification)                    | Last accepted: records / continuation B / plaintext B / envelope B / scope B |
| -------------- | ------------- | ------------------------------------------------------- | ---------------------------------------------------------------------------- |
| `short`        | 20            | build / `session_construction_limit` (`append_limit`)   | 80 / 120 / 36 646 / 36 755 / 72 655                                          |
| `tools`        | 5             | agent / `agent_response_invalid` (`message_limit`)      | 70 / 50 / 55 180 / 55 289 / 100 671                                          |
| `continuation` | 6             | agent / `agent_response_invalid` (`continuation_limit`) | 42 / 240 240 / 260 997 / 261 106 / 479 809                                   |
| `updates`      | 12            | agent / `agent_response_invalid` (`message_limit`)      | 84 / 120 / 40 698 / 40 807 / 79 222                                          |

Approximate per-accepted-run increments measured between consecutive accepted states: `short` +4 records, +≈1.8 KiB plaintext/envelope, +6 B continuation; `tools` +14 records, +≈10.7 KiB, +10 B; `continuation` +7 records, +≈42.4 KiB, +40 040 B; `updates` +7 records, +≈3.3 KiB, +10 B. Tool observations per accepted run: `short` 0, `tools` 8, `continuation` 1, `updates` 1 (the terminal `finish_review` emits no result event).

The rejected attempts measured the proposed rather than accepted shape at the project request boundary:

| Profile        | Reconstructed request messages | Proposed response messages | Serialized continuation after (B) | Bytes dispatched in rejected attempt   |
| -------------- | ------------------------------ | -------------------------- | --------------------------------- | -------------------------------------- |
| `short`        | 62                             | 63                         | 3 126                             | 9 467                                  |
| `tools`        | 62                             | 71                         | 1 626                             | 47 222                                 |
| `continuation` | 34                             | 35                         | 282 278                           | 524 760 over 2 requests (last 272 957) |
| `updates`      | 64                             | 65                         | 3 844                             | 41 991                                 |

## First effective limits

The measured first limits agree with the configured registry in `runtime/src/AgenticPrReview.Runtime/Agent/AgentLimits.cs` and the derivations in the [growth README](../../runtime/tests/ReviewEvaluationFixture/Growth/Profiles/README.md):

- `short` reached the session-construction proof at run 21. A minimal completed run reconstructs three messages, so `3n + 2` must fit `AgentLimits.Messages` = 64: `3 × 20 + 2 = 62` admits a minimal next context while `3 × 21 + 2 = 65` does not. The measured rejection at 20 accepted runs matches this source-derived bound exactly; the format maximum `AgentSessionFormat.MaximumCompletedRuns` = 64 is unreachable by a legal minimal accepted history under current limits.
- `tools` and `updates` rejected at the Agent boundary when the proposed response would push the reconstructed request above `AgentLimits.Messages` = 64 (proposed 71 and 65 messages respectively). The heavier per-run message cost of tool rounds is why `tools` stops at 5 accepted runs while `updates` reaches 12.
- `continuation` rejected on measured serialized continuation of 282 278 B against `AgentLimits.ContinuationTotalBytes` = 262 144 after six accepted runs of ≈40 040 B each.
- The production-Host lifecycle independently reached capacity at phase 5 — its sixth invocation — running the same eight-`read_file` workload as the `tools` profile, confirming the same boundary through the full Host path rather than the replay executor.

Configured bounds not reached by any measured profile: `SessionRecords` 256 (maximum observed 84), `SessionPlaintextBytes` 1 MiB (maximum observed 260 997), `SessionRecordBytes` 512 KiB, `ContinuationItemBytes` 64 KiB, `StateEnvelopeBytes` 2 MiB, `StateScopeTotalBytes` 6 MiB (maximum observed 479 809), `CandidateMetadataBytes` 16 KiB and `CandidateEnvelopeTotalBytes` 4 MiB. These remain defensive configured bounds; the executed profiles did not saturate them. `AcceptedCandidates` = 2 is the retained accepted tail (current plus immediate predecessor) visible at every post-acceptance sample; it is a retention shape, not a session-duration limit.

## Preservation, reset and failure evidence

Every rejected append across all four profiles left the accepted predecessor verifiably intact (`predecessor_preserved` = true on all 21/6/7/13-row matrices): the runner independently restored the prior accepted lineage and compared canonical bytes and normalized content rather than assuming store state.

The `r5-reset-capacity-v1` report (`r5_reset_capacity_passed`, `framework_production_host_synthetic_ports`, `sourceClean` true, `messageLimit` 64) records: five accepted phases with cumulative publication writes 1–5; capacity rejection at phase 5 (`agent_response_invalid`, `NotCommitted`); a further non-reset attempt at phase 6 (`agent_chat_failed`, `NotCommitted`); an authorized explicit reset accepted at phase 7; and an independent continuation accepted at phase 8. The report asserts `epochChanged`, `sessionChanged`, `priorContentExcluded` and `freshContinuationAccepted`, and the decrypted synthetic SESSION verified old-content exclusion while a fresh public fact remained independently observable. Publication writes are cumulative committed sticky writes: the post-reset review updated the previously published comment under the bounded target handoff, and cumulative writes reached 7 by phase 8.

The `r5-reset-owner-v1` probe (`r5_reset_owner_passed`, `production_host_synthetic_ports`) passed all eight owner cases covering historical-target carry, completed-reset reentry, successor continuation, final target-substitution rejection, and ordinary absence recreation with late-appearance rejection for both initial writes and known-not-written retries.

Observed failure classes stay distinct in both evidence sets: capacity outcomes (`session_construction_limit` at the build stage, `agent_response_invalid` classified `message_limit`/`continuation_limit` at the agent stage), non-capacity Agent failure (`agent_chat_failed`), state-acceptance outcomes (`state_accepted` vs `NotCommitted`), and evaluator attribution where the embedded Q4 report keeps an expected capacity rejection as a failed or unevaluated outcome — `host_state` attribution for the construction rejection and `unknown` for the agent-stage rejections — instead of folding it into model-quality counts. A failure without an admitted reply reports null execution counts, not a fabricated zero. The raw `agent_response_invalid` code is not capacity-specific on its own: the harness only names an observed over-limit quantity because its measured message/continuation counters prove the bound was crossed; the same production code also covers malformed provider responses, invalid message/tool structure, duplicate tool-call IDs, and admission or canonicalization failures.

## Unapproved observations

The measured envelope shows the reachable ceilings are the message-count and continuation-byte construction bounds, not the format's 64-run cap or the larger byte defenses; the unreachable bounds currently act only as defense-in-depth. No capacity or reset-contract change is proposed by this document. If a future decision revisits capacity, the concrete trigger is a workload whose real limit order differs from the measured one — for example sustained small reviews approaching the format cap — and the affected authority is `AgentLimits`/`AgentSessionFormat` plus the SESSION construction proof. That remains future decision input with no production effect here.

## R6 reuse and remaining unknowns

R6 can reuse the measured real logical-history and request shapes: per-generation provider request bytes and last-request bytes, reconstructed/proposed message counts, serialized continuation before/after bytes, tool-observation counts and the dispatch boundary between scripted request admission and SESSION construction. Unknowns that this evidence cannot supply: provider token usage and cache read/write telemetry (the scripted transport supplies none and Q4 keeps `Telemetry.NotSupplied`), live-provider continuation variance, and any cost comparison — all of which stay with R6's own evidence gathering.
