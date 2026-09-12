# R5 bounded session growth

From the repository root:

```sh
dotnet run --project runtime/tests/ReviewEvaluationFixture/AgenticPrReview.Runtime.ReviewEvaluationFixture.csproj --configuration Release -- replay --bundle runtime/tests/fixtures/agent/r5/growth
```

The command runs four synthetic profiles through the existing R2 fresh-process executor. Every accepted generation comes from actual Agent execution, SESSION construction, STATE preparation, reply admission and parent acceptance. Each child re-admits the captured R1 seeds. The ordinary R1 manifest and replay phase bound remain unchanged; the private growth selector has its own finite schedule, capped at `AgentSessionFormat.MaximumCompletedRuns + 1`. No supplied history, live provider, credential, reset or capacity change is involved.

| Profile        | Authored workload                                                                    | Observed stopping boundary                                                                     |
| -------------- | ------------------------------------------------------------------------------------ | ---------------------------------------------------------------------------------------------- |
| `short`        | Minimal finish-only review                                                           | Builder `session_construction_limit` while reserving the next request                          |
| `tools`        | Batch of eight actual synthetic file reads, then finish                              | Agent `agent_response_invalid` with a proposed message count above the current limit           |
| `continuation` | File read and finish with compact generated reasoning inputs                         | Agent `agent_response_invalid` with measured serialized continuation above the aggregate limit |
| `updates`      | File read and finish, alternating repeated head and synthetic verified-ahead updates | Agent `agent_response_invalid` with a proposed message count above the current limit           |

These are measurements for the checked inputs and source, not throughput guarantees or a new SLA. The raw producer stage/code is retained even when multiple checks share a code. The additional classification names an observed over-limit quantity; it does not invent a more specific production diagnostic. Profile reproduction compares normalized count/size progression and the actual classified stop. Operational SESSION/envelope hashes remain separate from comparison-only logical hashes.

## Measurement and accounting

`before` is the previous accepted sample, independently restored before the attempt. `state` exists only after verified acceptance and readback. A rejected candidate has no accepted generation or accepted size; a failure without an admitted child reply has null execution/transport counts, not a fabricated zero. Every started attempt has an associated Q1 outcome. Schedule exhaustion reports `attempt_limit` without claiming a capacity failure. A pre-attempt interruption retains the completed prefix and reports the schedule interruption without inventing another attempt.

SESSION records count durable records plus continuation items. SESSION continuation bytes count decoded codec payload bytes. Project request bytes and serialized project-continuation bytes come from the actual chat boundary. Provider bytes are the actual dispatched bodies observed by the injected transport; an unsent rejected request has no measured provider body. Token usage from the scripted transport is not a cost measurement.

Scope samples sum accepted envelope lengths and `RestrictedStateSnapshotCodec.CandidateMetadataBytes` at the post-acceptance/before-attempt sampling point, where no staging candidate exists. They are not filesystem allocation or all historical generations. STATE itself retains the current and immediate predecessor candidates. A failed prepare may leave staging; the report does not claim a post-failure total staging-size sample. Preservation checks restore the accepted predecessor again and compare its accepted lineage, canonical bytes and normalized content, rather than assuming the entire store is unchanged.

Each profile embeds an ordinary Q4 report admitted through the existing Q1/Q4 readers. Expected capacity rejection remains a failed or unevaluated Q1 outcome even when the growth assertion succeeds. Q4's `Telemetry.NotSupplied` keeps its existing meaning; growth measurements are in the separate closed document. `GrowthJson.Read` checks bounds, schedule/corpus identities, row links, progression, failure positions and recomputed Q4 accounting. Reading a supplied report is data admission, not authentication of an unseen execution. Tests independently compare measurements with actual private artifacts and reject tampered artifact/receipt combinations.

The generated corpus binds the R1 seed corpus, compiled profile specification, attempt ceiling and test-control schedule. The source and configuration identities also remain present in Q1 outcomes. No raw provider, continuation, source, plaintext SESSION, exception or environment values are public output. Null or empty private byte-array representations both mean no SESSION bytes; nonempty failed candidates are rejected.

Zero-call Agent cancellation retains its actual diagnostic and known zero execution counts, with no project request measurement. The reader correlates Agent/build failures with failed Q1 execution, while allowing completed SESSION outcomes followed by prepare/accept/readback failure. An unconfirmed child termination retains the confirmed profile prefix and an indeterminate failed attempt; it performs neither state readback nor workspace cleanup and reports `cleanup_failed`. A throwing cleanup operation also reports `cleanup_failed`. The typed supervisor-failure test covers this report handling without claiming to induce an operating-system reaping failure. Git attributes preserve the content-addressed fixture bytes even in conversion-enabled checkouts.

Q1 evidence is consumed only after the complete child reply and unique startup are admitted. Rejected diagnostics, provider projections or repeated startup identities produce evaluator-invalid accounting even if the rejected reply contained a completed outcome. Admitted completed execution can still precede a later state-acceptance failure. Both original and private-copy R1 admission retain typed cancellation and I/O failures as operational outcomes; genuine content rejection remains `input_invalid`.

## Boundary evidence and limits of reachability

The `R5Growth*` tests in the partial `AgentSessionRoundTripTests` class exercise below/exact/above count, record-byte, plaintext-byte, continuation-item/aggregate, cumulative record and reconstructed request/message bounds. They reuse real writers, validators and builders. Their names and assertions distinguish root grammar, serialization and request admission from accepted history.

Writer-sized context/workflow strings deliberately exceed semantic limits: successful writing is not successful root/record admission. Root-count witnesses have valid root metadata but are not executed histories. The synthetic sized continuation codec reaches decoded payload boundaries independently of the DeepSeek representation, whose overhead can stop execution earlier. Existing builder tests for next message, part and request capacity remain active.

A minimal completed run reconstructs at least three messages. With the policy and next context, `3n + 2` must fit the message cap, so the nominal 64 completed runs are not reachable through a legal minimal accepted history under current limits. This is a source-derived bound, not a claim of executing 64 reviews. Likewise, ordinary encrypted SESSION growth cannot be claimed to saturate the larger defensive envelope/scope caps merely by padding serialized objects. The fixture never raises a limit, drops history or resets to force those combinations.

Validation includes the full Runtime suite, `npm run check`, `npm run dist:check`, repeated framework execution of the command above, and execution of the published Linux x64 Native AOT fixture and its children. Existing R1/R2/Q3 and Q4 tests remain required. Full R5 CI orchestration and reset/operating-envelope work belong to later issues.
