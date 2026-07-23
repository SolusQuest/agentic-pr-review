# M4 Trusted Live Provider Adapter

Issue #52 owns the opt-in Anthropic reference adapter. The canonical #50 provider block stream and `prefixSha256` remain unchanged; the adapter owns the provider-specific HTTP projection, which derives its top-level `tools` definition from the same digest-identified tools envelope. This distinction is intentional: the canonical stream is the cross-provider prefix contract, while Anthropic's cache hierarchy is an adapter concern.

The complete source values and identities are in [m4-live-provider-envelope.json](m4-live-provider-envelope.json). Runtime and host code must recompute the envelope digests with the #50 domain-separated digest helpers and reject drift. The fixed model is `claude-sonnet-4-6`; model retirement is a terminal configuration failure and never triggers automatic substitution.

Live execution is selected only by `AGENTIC_REVIEW_M4_LIVE_VERIFICATION=1` from the checked-in trusted verification workflow. The child process receives only the fixed secret overlay, `AGENTIC_REVIEW_ANTHROPIC_API_KEY`, plus the fixed live selector. The key is never placed in input, context, CLI arguments, durable outputs, logs, or diagnostics. The adapter sends one bounded non-streaming request, disables redirects/proxies, and requires one terminal `submit_review` tool-use response.

Standard and stateless live verification use distinct cache identities and state namespaces. Standard places one explicit marker at the stable/dynamic boundary. Stateless sends no marker and is accepted only when both provider cache counters are present and zero, bound to the request observation; missing or inconsistent telemetry fails closed. A provider failure produces no authoritative output transaction.

The host process deadline is 150 seconds with a 30-second close margin; the provider request deadline is 120 seconds. `#55` returns a validated local candidate lease, and `#53` owns state acceptance and optional sticky publication.
