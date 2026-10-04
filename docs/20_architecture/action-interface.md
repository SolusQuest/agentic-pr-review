# Trusted Review Configuration

The current Host reads `.github/agentic-pr-review.json` (or the existing `config-path` input) and its instructions only from the authorized immutable workflow commit. Reviewed PR files, ambient checkout content and model output cannot select configuration. The [R4 interface](./r4-actionhost-wrapper-plan.md#r4-d04-trusted-configuration-and-policy) still defines the other inputs and publication fields. This R7-C6 supplement adds only the optional `review` object to `agentic-pr-review.config.v1`.

```json
{
  "schema": "agentic-pr-review.config.v1",
  "instructionsPath": ".github/agentic-pr-review/instructions.md",
  "publication": { "mode": "sticky" },
  "review": {
    "maxModelCalls": 64,
    "maxUncachedInputTokens": 2000000,
    "maxCachedInputTokens": 38000000,
    "maxOutputTokens": 524288,
    "timeoutSeconds": 900
  }
}
```

| Integer field            | Allowed range | Omitted default |
| ------------------------ | ------------- | --------------: |
| `maxModelCalls`          | 1..128        |              64 |
| `maxUncachedInputTokens` | 1..2,000,000  |       2,000,000 |
| `maxCachedInputTokens`   | 1..38,000,000 |      38,000,000 |
| `maxOutputTokens`        | 1..524,288    |         524,288 |
| `timeoutSeconds`         | 1..900        |             900 |

Omitting `review`, using `{}`, or omitting individual fields selects the corresponding defaults. Explicit `null`, strings, nonintegers, wrong types, duplicate/unknown keys and out-of-range values fail closed before state or provider admission. The existing 16 KiB strict UTF-8 config boundary remains. There is no interpolation or provider, model, endpoint, credential, tool-count, structural-limit or retry-policy override.

The Host materializes one immutable numeric budget authority. Its five effective values enter the canonical limits identity used by Host state, both SESSION construction paths and the real Agent. Omitted and explicit default values have identical effective limits bytes; the full policy identity still includes the exact source/config bytes and may differ. A changed limit cannot silently reuse incompatible selected-current state. Existing true-absence/historical-state classification and explicitly authorized reset semantics remain; no automatic reset, migration or compaction is added.

Calls count logical invocations; each invocation still has at most two additional physical attempts and each review at most eight retries. Default/evaluator profiles retain their existing 64-call behavior. Only configured Current may admit up to 128 calls; its maximum physical sends are `L + min(2L, 8)` (136 at 128 logical calls), subject to earlier stopping. Tools and all other structural ceilings are unchanged.

Token fields are stopping thresholds for validated observed usage. Known uncached and cached input are independent; total input allowance is their sum (40,000,000 at defaults). Known total input with unavailable cache split is conservatively debited against uncached allowance without inventing observed cache misses. Unknown usage remains unknown and may be billed; the selected availability policy still permits eligible bounded retries. These limits are not hard billing or currency guarantees, and final-response overruns are possible.

Every attempt consults the shared balances and deadline. The real provider `max_tokens` is `min(65,536, remaining known output allowance)`. A retry preserves the identical logical request: if failed-attempt output usage leaves less headroom than its frozen `max_tokens`, the retry is declined rather than resized. Exhaustion/overrun preserves incomplete status and cannot commit a successful successor state or publish a clean review.

The monotonic Agent deadline is the smaller of configured `timeoutSeconds` and remaining Host pre-publication time. It covers attempts, backoff and tools; transient Host headroom does not change canonical identity. The [Host 24-minute/4-minute envelope](./review-deadline.md) and retained profile deadlines remain unchanged.

Credential-free policy and production-composition tests exercise parsing, canonical identity, 65/128-call boundaries, configured token/output/deadline consumers, retry headroom and SESSION rejection. Run `npm run check`, the full Release runtime test suite and `npm run runtime:integration`. Windows integration covers framework execution; Linux CI owns its Linux-only and Native AOT gates. This configuration feature does not authorize live provider calls or imply release/default graduation.

## Current-run Provider Accounting

R7-H2 carries the immutable R2 numerical aggregate through Host completion into the existing Action step summary. It adds no Action outputs or counter collection. The private Host and its Node wrapper ship together: both require `accounting` and `termination_reason` on every completion, with no previous wire fallback. The strict UTF-8, duplicate/unknown-member and 16 KiB completion boundaries remain.

`accounting` has exactly the following members. Every numerical member is a canonical nonnegative decimal string or explicit `null`, never a JSON number. This preserves exact Int64 values beyond JavaScript's safe integer range. All members, including nullable members, are required.

| Wire member                                 | Meaning and bound                                                   |
| ------------------------------------------- | ------------------------------------------------------------------- |
| `model_calls`                               | Logical invocations, 0..128                                         |
| `provider_attempts`                         | Observed physical sends, 0..136                                     |
| `provider_retries`                          | Additional physical sends, 0..8                                     |
| `provider_failed_attempts`                  | Observed sends without successful finalization, 0..attempts         |
| `provider_unknown_usage_attempts`           | Sends missing observed input or output usage, 0..attempts           |
| `provider_unknown_cache_partition_attempts` | Sends with known input but unavailable cache partition, 0..attempts |
| `input_tokens`                              | Known input sum, 0..Int64.MaxValue or null on overflow              |
| `input_cache_hit_tokens`                    | Known cache hit sum, 0..Int64.MaxValue or null on overflow          |
| `input_cache_miss_tokens`                   | Known cache miss sum, 0..Int64.MaxValue or null on overflow         |
| `output_tokens`                             | Known output sum, 0..Int64.MaxValue or null on overflow             |
| `attempt_accounting_completeness`           | `complete`, `partial` or `unavailable`                              |
| `usage_completeness`                        | Independently `complete`, `partial` or `unavailable`                |

Incomplete counts and known sums are observed lower bounds. A zero known sum alongside unknown usage does not mean zero consumption. Token sums describe observations rather than billing or currency, and an observed overrun is retained. Cache partitions are never inferred by subtraction. Attempt completeness may be unavailable while partial known usage exists: an unobserved dispatch is not proof of no send.

The six count fields are either all known or all null. Missing finalization means every numerical field is null and both completeness fields are unavailable. Confirmed pre-provider exits use R2's empty aggregate: complete zero with `not_started`. Recovery-only acceptance also reports complete zero for this invocation, without loading prior-run accounting from durable state. Once the runner returns an outcome, an invocation-local carrier retains its validated numerical projection before disposal. Later provider disposal, state, publication, cancellation and composition replacement paths preserve that aggregate and the Agent termination reason. An escaped runner without a finalizer remains unavailable.

Validators enforce the retry geometry: with attempts P and retries R, initial physical sends I=P-R obey 0<=I<=model_calls, R<=2I and R<=failed_attempts. Complete usage requires complete attempts, no unknown counters, all sums known and input=hit+miss. Unavailable usage preserves zero known sums and marks every observed send as having unknown usage. Unavailable finalized attempts have zero physical derived counters. Known cache sums cannot exceed known input. Complete physical accounting claiming `review_completed` must contain at least one successful send; incomplete counts cannot establish that every actual send failed.

`termination_reason` is a closed review vocabulary. Successful Agent outcomes map to `review_completed`; proven absence of invocation maps to `not_started`. Agent cancellation and deadline codes map to `cancelled` and `deadline_exceeded`; chat failure to `provider_failure`; model/tool/token exhaustion to `model_limit`, `tool_limit`, `token_limit`; request/response size limits to `request_limit`, `response_limit`; context exhaustion to `context_limit`; remaining invalid Agent results to `invalid_result`. `host_failure` is available for wrapper failures. Arbitrary diagnostic text is never forwarded. Review termination and final Host status are separate: a later Host failure can retain `review_completed` and still exit unsuccessfully. Reviewed statuses allow `review_completed` or recovery `not_started`; skips require complete zero and `not_started`.

The summary retains status, reviewed SHA, publication URL, finding count and state disposition, then adds the termination reason, independent completeness, counters and known sums. Unavailable values are displayed as `Not available`. A missing or malformed Host completion uses fixed wrapper failure text with unavailable facts and no accepted-state claim. Private diagnostics, raw requests/responses, SESSION content, continuations, credentials and private paths never enter accounting or presentation.

Matching C#/Node fixtures cover retry geometry, independent completeness, exact Int64 strings, overflow, success consistency, hostile/missing/duplicate fields and the completion cap. Real synthetic HTTP, composition, recovery, early cancellation and exception tests verify every production completion family. Validation remains `npm run check`, `npm run dist:check`, the affected/full Release runtime tests and `npm run runtime:integration`; provider execution is synthetic.
