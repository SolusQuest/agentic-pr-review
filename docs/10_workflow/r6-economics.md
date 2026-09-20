# R6 usage observations and reconciliation

R6-T1 preserves the existing DeepSeek parser's validated cache-read and uncached-input partition through the backend and project chat usage into the existing live-local summary. Required input and output totals, thinking requests, adapter identity, continuation, SESSION and reservation accounting are unchanged. The parser still rejects missing, malformed, negative, overflowing or inconsistent required counters. Optional cached-token detail and reasoning subcounts are not added to totals.

The optional project-owned provider observation is independent of required total usage. Synthetic backends can continue returning only input and output totals. Missing or invalid optional cache observations do not introduce an Agent failure rule and do not increment `usage_unknown_calls` when total usage is known.

The source-generated `cache_usage` summary has a fixed DeepSeek scope:

- `measured` means all observed known usage has an admitted partition and there are no known unknown-usage observations or unmatched sends. Measured zero is a numeric zero.
- `partial` preserves measured subtotals when other observations are unavailable. These subtotals must not be presented as the complete input partition.
- `unavailable` has null token subtotals when nothing was measured or defensive aggregate overflow prevents a representable subtotal.
- `measured_calls` and `known_usage_without_cache_calls` count independent cache-observation outcomes. The existing total-usage unknown counter and send counts retain their own meanings; they are not added together to invent an exact unknown-send count.
- `cache_write_billing_status` is `not_applicable`: the current DeepSeek hit/miss/output billing components do not require an independent cache-write statistic. There is no fabricated cache-write token value.

`requested_model` records the unchanged request alias `deepseek-v4-flash`. `response_models` is a bounded, deterministic distinct list of the parser-admitted `deepseek-v4-flash` and `deepseek-flash` identities. The summary independently checks that allowlist before accepting an optional observation. No raw provider request ID, fingerprint, content, exception, credential or arbitrary identity is retained. `backend_snapshot_status` remains `unavailable`; neither response alias nor `system_fingerprint` establishes a backend snapshot.

This cache summary retains its R5/T1 aggregate meanings. The R6-T2 journal below supplies exact attempt/send reconciliation separately. R6-T3 owns tariff arithmetic. No public Action contract, durable format, provider policy, paid execution or release decision is added. R5 model-quality evidence remains inconclusive, independently of usage observations.

Tests exercise the actual loopback transport, parser, backend, minimal chat projection, live observer and source-generated summary with distinct hit/miss/output values, both response aliases, measured zero, absent/invalid optional observations, partial aggregation and canaries. Existing parser negative matrices, Agent/session tests and the framework/Native AOT CI proofs remain applicable.

## Bounded attempt and call journal

The existing live-local command adds `usage_journal` to its final summary line. Outcome and report lines, their order, R5 scoring and existing summary fields retain their meanings. The journal is internal evaluator data consumed by the current runner; it is not a durable SESSION format or a public Action compatibility family.

Every admitted schedule slot has an attempt row, including repeated case IDs and the never-started tail. `scheduled = attempted + unattempted` and `attempted = completed + failed + invalid`. Attempt status records the existing evaluation result; `agent_status` independently records whether the Agent started and returned success or failure. An interrupted Agent can remain `unknown`. Each attempt records its finalized call, send and local-refusal counts, plus the existing evaluation attempt digest when available.

A harness-owned `call_id` identifies each chat invocation. Its `dispatched` flag becomes true immediately before delegation to the underlying transport. This is a send attempt, not proof of network delivery or provider billing. Agent model-call counters and R5 reservation counts are not substituted for this flag. Budget refusal, accounting-violation refusal, request projection failure and cancellation before dispatch are local refusals. The transport's defensive `request_rejected` result also means no send, even if a reservation was already taken. Reservations are never refunded.

Every dispatched call has either `known` usage with nonnegative counters or `unknown` usage with null counters. A non-dispatched call has `not_sent` usage and null counters. Known zero is explicitly measured zero. Thus `actual_sends = known_usage_sends + unknown_usage_sends`; `usage_complete` is false whenever a dispatched call lacks admitted usage. Known token subtotals can be zero while completeness is false, and must not be presented as complete zero-cost usage. Optional cache availability is independent: `measured_cache_sends` counts the admitted partitions, and cache subtotals are null if none were measured. A partial cache subtotal is not the full input partition. Cache-write billing remains `not_applicable`.

The observer copies only the existing adapter-normalized `ProjectChatUsage`, before downstream Agent tool, terminal, evidence or SESSION validation. Such later rejection does not erase already known usage. Provider normalization failure, malformed required usage, HTTP errors, rate limiting, timeouts, discarded oversized responses and transport failures retain an unknown send when no typed usage was admitted. There is no second raw-response usage parser. Raw provider request IDs, fingerprints, content, reasoning, paths, credentials and exception text are excluded.

## Identity, admission and finalization

Campaign IDs use the existing live-run nonce; attempt and call IDs add schedule and per-attempt ordinals. Provenance binds campaign, source commit/tree/clean status, the existing evaluator build profile, corpus digest, provider-configuration digest, plan digest and live/loopback execution kind. Every row carries the same domain-separated provenance binding. `provider_configuration_sha256` is the admitted provider configuration, distinct from the evaluation request configuration. The build profile is `r5-live-local`; it is not a claim of binary attestation. The required `plan` field carries the existing normalized selection: source, corpus digest, fixed provider/model/adapter/configuration, expanded schedule and full reservation bounds. It excludes the private corpus path, and its existing plan digest must match the provenance.

`UsageJournalJson.Read(bytes)` admits a standalone serialized journal without the original plan file, in-memory expectation, network or credentials. It validates the embedded normalized selection using the live plan's shared provider/bounds rules, verifies its digest and provenance links, then checks every schedule slot in order, row identities, contiguous call inventories, lifecycle tuples, derived totals and optional cache partitions. Historical source metadata is validated as data; standalone admission does not require rebuilding the producer's commit. Duplicate, unknown or missing JSON fields, substituted provenance, missing rows, malformed UTF-8, invalid counters and oversized documents fail admission.

The producer additionally calls `Matches(expected)` with an independently selected `UsageJournalExpectation` derived from the admitted plan, corpus, compiled source/build and new campaign identity. `Read(bytes, expected)` provides the same additional check for a caller that already owns that context. Deriving this extra expectation from candidate bytes cannot establish independent origin. Standalone admission verifies structural claims; the optional external check also binds a selected plan. Neither authenticates a coordinated rewrite or proves historical paid traffic. The self-contained artifact is the input handoff for R6-T3's declared offline journal consumer.

Lifecycle admission rejects combinations such as a budget refusal or HTTP error paired with a successful chat return. An interrupted seal can legitimately retain `not_observed` after a transport receipt; such usage remains unknown. A `not_started` Agent cannot own call rows, and a succeeded Agent requires a send and evaluation digest. Post-Agent admission failure may still retain a succeeded Agent, while defensive evaluator failure can occur after Agent start. Actual sends and explicit request rejections require reservations; budget/violation/projection refusals do not add them. Pre-dispatch cancellation or an interrupted reservation-to-dispatch transition can retain a reservation. These relationships supplement the exact bound multiplications without refunding any reservation.

Within one attempt, a following call or Agent success requires a preceding dispatched successful return with known, valid usage. Exceptions, visible local refusals, oversized responses, unknown usage, cancellation and unfinished sealed observations must be the final call of their attempt and cannot establish Agent success. Cancellation terminates the Agent even when an abandoned transport task later completes; the maintained backend and Agent check cancellation before continuing. A later evaluator failure may still retain Agent success after an admissible final response. Ordinary transport failures can end one attempt without preventing a later scheduled attempt; the separate campaign stop rules decide that boundary.

Refusal admission follows the reservation prefix. Capacity is the minimum of the call, input, output, combined-token and spend limits divided by their fixed per-call reservations. A budget refusal requires that capacity to have been reached before the refused call; cancellation and incomplete observation retain only the feasible zero-or-one reservation range. Later grants rule out an earlier hidden budget refusal. An accounting-violation refusal requires an earlier observation that could have exceeded a per-call usage bound, and a definite violation prevents further grants and takes priority over budget exhaustion.

The journal must also support its accounting, budget or rate-limit stop reason. Definite stop signals prevent a later scheduled attempt and contradict `complete`; definite higher-priority signals rule out a lower-priority stop. Observation timing remains significant: the journal can record a transport receipt or normalized usage before R5 accounting records it, while cancellation lets Agent waiting finish independently. An interrupted observation therefore preserves possible causes without inventing a completed accounting update. A later call in the same serial attempt, successful Agent completion, or an explicit violation refusal establishes completion of the relevant usage accounting; a 429 followed by a chat exception establishes the rate-limit update. Caller cancellation, deadlines and infrastructure failure can arise between calls, so their external cause is not reconstructed from call rows.

The limits are the existing 256 expanded attempts and eight model calls per attempt, at most 2048 call rows, JSON depth 12 and 4 MiB. Individual observations use signed 64-bit nonnegative integer counters with overflow-safe partition checks. Aggregate token counters use exact integral `decimal` arithmetic so the bounded observations remain representable; no floating-point price or tariff computation is performed. Maximum-cardinality tests cover measured zero and a final maximum-counter observation after within-bound sends, retain totals above Int64, and verify the serialized document fits the byte cap.

The collector synchronizes mutations and gives callbacks captured attempt/call handles. A terminal call cannot be reopened, moved to another attempt or counted twice. Scheduling completion, cancellation and exceptional unwind seal the journal and R5 accounting once, before adjudication or public output. In-flight sends without admitted usage remain unknown; untouched slots remain unattempted. Late callbacks cannot reserve further work or mutate the frozen report. Exceptional unwind retains the existing command failure policy and does not introduce persistence or crash recovery. The runner exercises the generated writer, standalone strict reader and independent selection match before publishing, including on its existing Native AOT path.

R6-T3 consumes the admitted journal's complete population, typed known usage and explicit unknown sends alongside monotonic reservations. A token subtotal, reservation ceiling, cache observation or successful reconciliation alone does not establish complete cost, pricing, quality or release readiness.

## Offline tariff pricing

The evaluator provides `economics-price --journal <path> --tariff <path>`. It reads two bounded local files and writes one public JSON report to stdout. Add `--format markdown` for the corresponding human report; `--format json` is also accepted. Options can appear in either order, but missing, repeated, unknown or empty options reject. For example, after building the evaluator:

```sh
dotnet run --project runtime/tests/ReviewEvaluationFixture/AgenticPrReview.Runtime.ReviewEvaluationFixture.csproj --configuration Release --no-build -- economics-price --journal journal.json --tariff tariff.json
```

This command does not construct a provider client, read provider credentials, fetch tariff sources or execute reviews. It enters the pricing module before the live commands. Historical journal source metadata is data, so the original corpus, producer build and private input paths are unnecessary. Successful report generation returns exit code 0 even when amounts are explicitly incomplete. Invalid arguments, journals or tariffs and unsupported tariff terms return 2 with a fixed `r6_pricing_*` stderr diagnostic. Arithmetic overflow, failed report readback or infrastructure failure returns 1. Rejected input, paths, exception messages and partial price reports are not emitted.

### Tariff snapshot

The input identifies one fixed three-rate reference tariff, with no automatically selected billing class. This example uses **synthetic rates**, not a current provider quote:

```json
{
  "format": "apr.r6.tariff.v1",
  "source_url": "https://api-docs.deepseek.com/quick_start/pricing/",
  "retrieved_on": "2026-09-20",
  "terms": {
    "formula": "deepseek_hit_miss_output",
    "provider_id": "deepseek",
    "requested_model": "deepseek-v4-flash",
    "response_model": null,
    "price_class": "standard",
    "reference_at": "2026-09-20T12:00:00Z",
    "effective_period": {
      "status": "known",
      "from_inclusive": "2026-09-20T00:00:00Z",
      "until_exclusive": "2026-09-21T00:00:00Z"
    },
    "token_unit": 100,
    "rate_decimal_places": 0,
    "rates": {
      "cache_hit_input": { "currency": "USD", "units": 1 },
      "cache_miss_input": { "currency": "USD", "units": 4 },
      "output": { "currency": "USD", "units": 5 }
    },
    "arithmetic": {
      "rounding": "half_even",
      "decimal_places": 3,
      "aggregation": "campaign_components_then_sum",
      "normalization": "exact_unrounded_amount_per_input_token"
    }
  }
}
```

Each rate is `units / 10^rate_decimal_places` currency units per `token_unit` tokens. Rates are nonnegative signed 64-bit integers; the token unit is 1 through 1,000,000,000, and both decimal-place fields are 0 through 12. USD and CNY are supported reference currencies, and all three rates must use the same currency. Mixed currency rejects; other currencies, providers, models, formulas, arithmetic policies or billing classes have explicit unsupported/mismatch diagnostics. There is no foreign-exchange conversion, tiered tariff, extra cache-write charge or independently charged reasoning partition.

The requested profile remains the current DeepSeek thinking profile. A null `response_model` explicitly selects requested-alias reference pricing. Setting it to `deepseek-v4-flash` or `deepseek-flash` additionally requires that exact observed response identity on each priced call. A missing cache observation also lacks response-model evidence in T2, so that restriction can reduce both input and output coverage. Neither alias establishes an independent backend snapshot.

`retrieved_on` is an exact `YYYY-MM-DD` date. All timestamps are exact `YYYY-MM-DDTHH:mm:ssZ` UTC values. A known effective interval requires both endpoints with start before end; the start is inclusive and the end exclusive. An unknown interval uses `status: "unknown"` and explicit null endpoints. `price_class` is `standard`, `peak`, `off_peak` or `unknown`. A snapshot describes one chosen class/window; the command does not infer a recurring schedule, time zone or execution class.

The tariff reader enforces 64 KiB, depth 8, strict UTF-8, required fields (including explicit nullable fields), and rejects duplicate/unknown members or lossy numeric forms such as fractional rate coefficients. The source URL is an ASCII HTTPS URL of at most 2048 characters with a DNS host, default port, and no credentials, query or fragment. Percent-encoded URLs are permitted. The source is never fetched. Public JSON and Markdown contain a domain-separated SHA-256 of the exact supplied URL string instead of the raw URL. Retrieval dates, intervals, rates and source commitments remain user assertions, not verified quotations or billing settlement.

### Amounts, missing observations and arithmetic

For each applicable known usage observation, the input numerator is `hit_tokens * hit_rate_units + miss_tokens * miss_rate_units`, and the output numerator is `output_tokens * output_rate_units`. Both divide by `token_unit * 10^rate_decimal_places`. Completion tokens enter once; `combined_tokens`, reasoning detail and cache-write observations are not added as extra charges. Failed attempts with known usage remain priced. Local refusals and unattempted slots stay in the embedded journal without inventing sends.

Exact integer numerators aggregate across the campaign before rounding. Integer quotient/remainder arithmetic performs half-even rounding to the declared output quantum separately for input and output. The displayed subtotal/total is the exact sum of those rounded component coefficients. Trailing scale zeros are reduced before conversion to `decimal`; any coefficient that still cannot represent the declared value fails as `r6_pricing_arithmetic_overflow`. This also rejects addition that would silently lose fractional precision in ordinary `decimal` arithmetic. The bounded call population, counters, rates and scales bound every integer intermediate. A nonzero amount below the declared quantum may legitimately round to zero; the original integer tariff remains in the report.

`known_input_amount` and `known_output_amount` describe their priced contributors. A component with no priced contributors is null, while a measured zero is zero. A no-send population has complete zero traffic arithmetic, with its unattempted population still visible. `known_total_subtotal` retains whichever components are known, but `total_amount` is null unless every dispatched call has complete input and output pricing. Coverage lists priced input/output sends, unknown usage, absent cache observations and unknown/mismatched required response identity. Those availability counts can overlap and are not additional billing partitions.

Known totals without a cache partition retain priceable output; positive input remains unpriced in the observed-partition view. Measured zero input determines zero input charge without manufacturing cache statistics. Unknown usage always prevents a complete amount, including under a zero-rate snapshot. Thus journal `usage_complete`, monetary completeness, execution completion and tariff applicability are separate dimensions. Reservations retain their own call/token counts and `spend_micro_usd`; even a CNY tariff never relabels or adds that USD reservation to usage cost.

`input_amount_per_input_token` and `total_amount_per_input_token` name distinct numerators. Both normalize exact, unrounded aggregate amounts and then apply their own declared final rounding. They do not divide the rounded display amount. Each requires its complete numerator and the complete positive input-token denominator; incomplete usage, incomplete pricing and zero input have explicit unavailable reasons and null values. These are not averages of per-call percentages or quality-eligible-review costs.

The separate `same_token_all_miss` view applies the miss rate to the identical known input counts and the same output rate to the identical output counts. Its input coverage may exceed observed-partition pricing when the partition is missing, but unknown usage or model applicability remains unknown. It is hypothetical repricing, not another execution, measured savings, a cache-disabled control or a cost-regression verdict. No difference between differently covered subtotals is reported as savings.

### Reference applicability and standalone handoff

`reference_applicability` reports period and class evidence separately. A supplied reference time outside the half-open interval yields `reference_period_mismatch`; an unknown interval or class retains its own unknown code. Those limits do not erase a valid fixed-reference arithmetic result. Selecting an off-peak reference snapshot does not prove that the calls occurred off peak.

T2 contains no execution timestamps. Consequently, `execution_time_applicability` is `execution_time_unknown` and `execution_time_total_amount` is null for current journals. Retrieval/reference dates, source commit dates, file timestamps and the command's clock are never substituted for execution evidence. Backend snapshot is `not_exposed`, and invoice status is `not_evidenced`. Reference prices establish neither historical settlement nor R5 quality or R7 readiness.

The JSON report embeds the complete admitted public journal and the sanitized tariff snapshot, including its arithmetic policy. `journal_sha256` binds the full generated admitted journal, rather than only T2's provenance binding; `tariff_sha256` binds the generated public snapshot, including the URL digest. The report cap is 5 MiB at depth 16. Markdown is a compact bounded summary of identities, terms, coverage, amounts and separate reservations.

`PricingJson.Read(bytes)` re-admits both embedded inputs, recomputes the report, and compares every identity, amount, completeness and applicability claim. It needs no original files or in-memory producer context. `Matches(selectedJournal, selectedTariff)` separately checks independently selected inputs; neither operation authenticates a coordinated rewrite or attests paid traffic. This is R6-C1's standalone priced-journal handoff, retaining every failed and unattempted row for its later independent comparison and quality decisions.

`R6PricingTests` covers arithmetic with independent expected values, rounding and representability failures, mismatches and unknown applicability, partial component coverage, maximum admitted population, strict/tampered reports, public-safe rendering and the actual command. The evaluator keeps source-generated JSON and reflection disabled. The permanent R6 framework/Native AOT gate remains R6-V1's responsibility; an AOT build alone must not be reported as execution of the new pricing command.
