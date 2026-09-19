# R6 cache usage observations

R6-T1 preserves the existing DeepSeek parser's validated cache-read and uncached-input partition through the backend and project chat usage into the existing live-local summary. Required input and output totals, thinking requests, adapter identity, continuation, SESSION and reservation accounting are unchanged. The parser still rejects missing, malformed, negative, overflowing or inconsistent required counters. Optional cached-token detail and reasoning subcounts are not added to totals.

The optional project-owned provider observation is independent of required total usage. Synthetic backends can continue returning only input and output totals. Missing or invalid optional cache observations do not introduce an Agent failure rule and do not increment `usage_unknown_calls` when total usage is known.

The source-generated `cache_usage` summary has a fixed DeepSeek scope:

- `measured` means all observed known usage has an admitted partition and there are no known unknown-usage observations or unmatched sends. Measured zero is a numeric zero.
- `partial` preserves measured subtotals when other observations are unavailable. These subtotals must not be presented as the complete input partition.
- `unavailable` has null token subtotals when nothing was measured or defensive aggregate overflow prevents a representable subtotal.
- `measured_calls` and `known_usage_without_cache_calls` count independent cache-observation outcomes. The existing total-usage unknown counter and send counts retain their own meanings; they are not added together to invent an exact unknown-send count.
- `cache_write_billing_status` is `not_applicable`: the current DeepSeek hit/miss/output billing components do not require an independent cache-write statistic. There is no fabricated cache-write token value.

`requested_model` records the unchanged request alias `deepseek-v4-flash`. `response_models` is a bounded, deterministic distinct list of the parser-admitted `deepseek-v4-flash` and `deepseek-flash` identities. The summary independently checks that allowlist before accepting an optional observation. No raw provider request ID, fingerprint, content, exception, credential or arbitrary identity is retained. `backend_snapshot_status` remains `unavailable`; neither response alias nor `system_fingerprint` establishes a backend snapshot.

This is an internal aggregate of the current live observer, not a billing ledger or proof of complete campaign economics. R6-T2 owns per-attempt/send reconciliation and finalization. R6-T3 owns tariff arithmetic. No public Action contract, durable format, provider policy, paid execution or release decision is added. R5 model-quality evidence remains inconclusive, independently of cache observations.

Tests exercise the actual loopback transport, parser, backend, minimal chat projection, live observer and source-generated summary with distinct hit/miss/output values, both response aliases, measured zero, absent/invalid optional observations, partial aggregation and canaries. Existing parser negative matrices, Agent/session tests and the framework/Native AOT CI proofs remain applicable.
