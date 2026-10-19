# Completed-context Solus review

`SolusRestoredReviewComposition` is the compiled internal Host entry for a distinct review from compatible, completed, APR-accepted Solus context. It consumes the [S2 source authority](../../runtime/src/AgenticPrReview.Runtime/Host/State/Restore/SolusContextGrantAdmission.cs) and the [S3 state commit](solus-state-commit.md) path. The ordinary Action entrypoint does not select this route; CR1-R6 owns that later integration.

The selected package closure remains `0.1.0-internal.4`, profile `cr1-solus-internal4-dev`, context format 2 and compatibility version 1. No conversion, unfinished `ContinueRun`, automatic reset or fresh fallback is provided.

## Current authority and historical data

The Host prepares the current authorized invocation, policy, bounded snapshot, exclusion lease and deadline before restoring state. Its actual runtime construction supplies current instructions, provider credentials and options, tools, limits and hooks. Compatibility is described before claiming a source and compared again against the actual construction. Untrusted repository/model input cannot select the execution mode or become instructions.

Shared R4 recovery runs before the source preparer. An already accepted invocation returns its authenticated result without provider credentials, context import, tool replay or another claim. The existing trusted invocation policy controls same-head behavior: `ReuseAcceptedState` returns the prior result; `ContinueAcrossWorkflowRuns` permits new work for a distinct run. A higher attempt of the same run remains recovery. Transaction mode does not override that policy.

Current Solus generation admission also requires the exact producer payload digest to equal the current authorized payload digest. Trusted workflow equivalence does not relax this Solus guard. Same-payload new-run continuation and higher-attempt recovery are supported; a changed payload is rejected before provider construction, source claim, publication or state mutation. The former native cross-payload continuation fixture is historical and does not qualify Solus cross-rebuild compatibility.

Only `NoPendingWork` can enter the S2 source path. Current credentials, input classes and input bounds are checked before the durable claim. Source selection authenticates the completed accepted record and current same-head or verified-ahead relationship. Source preparation re-observes the selected receipt and inventory under the existing exclusion, then persists its one-shot claim through an owned journal operation. A known claim is consumed even if later import or work fails; an uncertain mutation remains uncertain. There is no refund or alternative source retry.

The actual runtime receives `NewRunFromContext`, the selected envelope and the source-bound grant. It retains historical classified inputs, model/tool associations, usage and accepted receipts, while creating a new execution, round and logical work. Completed historical tools are not invoked again. APR validates the current domain result against only this invocation's new tool observations; old observation IDs and lines cannot independently ground a current finding.

## Explicit reset and consumed claims

Explicit reset retains its existing authorized exact-inventory transition and publication-target binding. Current context claims are spent storage data, not reset authority. For a selected current record and its immediate predecessor, reset validates every claim against the actual authenticated source receipt, logical head, generation, envelope and record digests, decoded S1 checkpoint, owner, grant derivation and time bounds. It performs no SDK import or second consumption. Unknown, under-retained, conflicting, duplicate, unaccepted or uncertain work remains a refusal; existing P5 settlement and reset cleanup keep their original owners.

This repair has a bounded source closure. A claim whose original source was already evicted beyond the current record and immediate predecessor cannot be authenticated from the claim alone, so reset still refuses that longer session. The existing eight-claim limit is unchanged. This limitation is separately exercised at the actual claim-budget boundary; it is not full-session reset qualification or permission to discard old claims. Reset from a genuine historical SESSION without current claims retains its existing path.

## Independent results and capacities

Admission, current work, terminal/domain acceptance, capture validity and durable acceptance are separate observations. Import rejection has a context admission result and no invented current work. Valid work remains visible when capture is unavailable, invalid, duplicated or throws, or when a later state/publication effect fails. A capture alone authorizes no publication or acceptance.

Current usage and accounting contain the new round's attempts and token observations. Historical usage stays associated with its prior rounds. Current allowances renew, while retained inputs, records, attempts, tool associations, candidate episodes, submissions and encoded context remain cumulative. Runtime capacity exhaustion is visible; APR does not truncate, compact, drop history or retry as fresh to make the context fit.

## Successor and restart binding

Restored preparation uses the authenticated selected generation plus one, its exact predecessor envelope and previous logical generation. It requires the consumed source to match that selection, the same current targets and compatibility, exactly one matching durable claim, no conflicting/unknown claim or pending publication, and a new checkpoint bound to the claim's execution and round. Existing S3 fresh preparation retains its generation-zero guard.

S1 seals the current captured record; the official artifact bridge and shared R4 transaction/publication machinery perform persistence, exact-head checks, sticky readback and acceptance. Recovery authenticates a pending successor against the stored original claim and checkpoint rather than a newly described execution ID. Known sticky/acceptance state can complete without another review; an intent with an uncertain unobserved effect retains its existing unknown disposition. Existing classifier, producer/header equality, retention and cancellation rules remain in force.

Restricted payload, model response, reasoning, credentials and continuation content are excluded from ordinary results and publication. Recovered publication uses the authenticated bounded finding count and does not reconstruct a current inline evidence capability.

## Qualification

The selected-runner proof is:

```bash
node runtime/tests/ActionHostFixture/verify-solus-restored.mjs
```

It runs after cold package qualification in `solus-agent-package-proof.yml`, on GitHub-hosted Ubuntu 24.04 x64, using the five actual installed assemblies with package-cache hash equality. Provider and GitHub behavior are controlled synthetic transports; the fixture receives no restore credentials and makes no live provider call.

Separate Host processes create a genuine S3 source, execute a useful subsequent review, authenticate its selected successor, and exercise recovery. Fixed authored source/script/oracle cases cover same-head C#/TypeScript/repository-policy reviews and a verified successor that fixes the old C# defect while introducing a changed TypeScript fallback. Negative cases distinguish current admission, import, work, capture and publication failures, source consumption, tampering, ancestry, policy compatibility, cumulative input capacity, exact data boundaries, token reservations and crash/restart behavior. Existing S1/S2/R4 proofs retain ownership of their primitive format, pin, competing-claim and recovery matrix.

The shared synthetic executor optionally reads strictly validated producer run/attempt facts from its private control directory. Missing control preserves its existing default; malformed control refuses upload. Each uploaded object's producer is snapshotted. Real bridge contracts, the official Actions executor and Host producer/header validation are unchanged.

Package-independent native Host tests cover exact successor/predecessor and consumed-checkpoint binding. Local source-reference compilation or Linux container execution is supporting feedback; it does not qualify the installed managed route. Required repository checks and exact-head selected-runner evidence remain separate gates.
