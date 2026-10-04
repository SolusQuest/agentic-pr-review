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
