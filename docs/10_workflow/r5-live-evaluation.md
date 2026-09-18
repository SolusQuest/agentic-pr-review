# R5 live-local evaluation harness

[Issue #251](https://github.com/SolusQuest/agentic-pr-review/issues/251) adds `live-local` to `runtime/tests/ReviewEvaluationFixture/Live`. It is a locally invoked, opt-in evaluator that runs the reviewed R5 corpus through the real DeepSeek thinking adapter under an explicit bounded run plan. It owns admission, scheduling, reservation accounting and public-safe reporting. It does not publish, call GitHub, add a provider or model default, host a benchmark, or perform the authorized live observation — [#252](https://github.com/SolusQuest/agentic-pr-review/issues/252) owns actual provider runs and adjudication, and any real invocation requires separate maintainer authorization against the exact source, configuration and budget.

## Commands

```text
live-local --dry-run --fixture self-test
live-local --dry-run --plan <plan.json>
live-local --execute --plan <plan.json>
```

`--fixture self-test` builds a minimal bundle and plan inside a private temporary root, runs the complete admission/scheduling/adapter/scorer/report path in loopback, verifies cleanup, and prints one bounded JSON report. It uses no provider credential and makes no network call.

`--dry-run --plan` admits the plan and corpus, then runs every scheduled evaluation through the real adapter path — `AgentLoop` → `DeepSeekChatBackend`/`MinimalChatClient` → `DeepSeekRequestWriter` → a synthetic `IDeepSeekTransport` producing valid DeepSeek-shaped responses → `DeepSeekResponseParser` → tools → SESSION construction via `DeepSeekReasoningContinuationCodec` → `EvaluationSubject` → `EvaluationScorer` → `EvaluationReport`. No credential is read; the provider variable is left untouched.

`--execute --plan` is the only mode that may reach the real provider. It performs the full deterministic preflight first — plan admission, source/corpus/configuration binding, schedule expansion, bound consistency, corpus admission — and reads the credential only after every input check has passed.

Exit codes: `0` complete, `2` invalid input/admission, `1` execution/infrastructure/stopped run. stderr carries a stable `r5_live_*` code on admission failures.

## Run plan

A plan is a closed JSON document (`apr.r5.live-plan.v1`, at most 64 KiB, depth ≤ 12, no unknown fields):

```json
{
  "format": "apr.r5.live-plan.v1",
  "source": { "commit": "<40-hex>", "tree": "<40-hex>", "clean": true },
  "corpus": { "path": "<dir>", "sha256": "<64-hex>" },
  "provider": {
    "provider_id": "deepseek",
    "model_id": "deepseek-v4-flash",
    "adapter_id": "<64-hex>",
    "configuration_sha256": "<64-hex>"
  },
  "schedule": [{ "case_id": "<case>", "repeats": 1 }],
  "bounds": {
    "max_evaluations": 4,
    "max_model_calls": 8,
    "max_input_tokens": 262144,
    "max_output_tokens": 32768,
    "max_combined_tokens": 294912,
    "max_seconds": 120,
    "spend_ceiling_micro_usd": 100000,
    "per_call": {
      "max_input_tokens": 65536,
      "max_output_tokens": 4096,
      "max_charge_micro_usd": 1000
    }
  }
}
```

Admission order is fail-closed: closed-schema parse → format → source fields → provider triple and configuration hash → schedule expansion → bound consistency → digest. Corpus files, credentials and any transport are untouched until all plan checks pass; `--execute` additionally requires the plan's `clean` and the compiled source's `clean` to both be true.

- **Source** must equal the compiled evaluator's `EvaluationSource` commit/tree/clean exactly; a plan cannot describe a different or cleaner build than the one running.
- **Corpus** is the reviewed R5 bundle path plus its admission digest; the runner re-admits the directory and rejects on digest mismatch or an unknown scheduled case. Live runs reuse the reviewed repository/review/workflow/policy identity and rebind only the provider triple — reports can never label actual DeepSeek execution as the synthetic manifest configuration.
- **Provider** accepts only the fixed `deepseek`/`deepseek-v4-flash`/current-adapter triple; `configuration_sha256` must equal the harness-computed provider-settings hash.
- **Schedule** is ordered `(case_id, repeats)` entries; expansion is capped at 256 evaluations. Every expanded evaluation receives its own run ID, attempt identity and outcome row — repeats are never collapsed.
- **Bounds** must all be positive and consistent: total ceilings cannot exceed `expanded evaluations × AgentLimits`, per-call values must fit their totals, `per_call.max_output_tokens` cannot exceed the adapter's wire cap (4096), and the spend ceiling must cover one per-call charge.

The canonical `plan_sha256` (domain `apr.r5.live-plan`) binds the normalized source, corpus, provider, expanded schedule and all bounds. It is the only public plan identity; run IDs are late-bound outputs (`live-<nonce>-<ordinal>` from a fresh cryptographic nonce per invocation), never plan fields.

## Reservation accounting

The harness has no provider billing oracle. Token and spend ceilings are maintainer-authorized reservation bounds enforced mechanically before each send; they are not a claim about actual provider pricing.

Before every adapter send the metered transport checks cumulative **reserved** counters — `reserved_input/output/combined_tokens`, `reserved_spend_micro_usd`, model-call count — against the totals. Each accepted send permanently consumes one per-call reservation; reservations are never released by reported usage. A send that would exceed any bound is refused and counted as `budget_refused`, and the schedule stops with `bound_stop`.

Reported provider usage updates separate **known** counters only. Usage above its authorized per-call bound falsifies the reservation basis itself: the run records `accounting_violation` and stops rather than presenting the run as valid. When usage cannot be observed — transport failure, normalization failure — the call's reservation still stands and `usage_unknown_calls` records the gap honestly; the schedule continues because the bound never depended on observed usage.

If a maintainer cannot supply a conservative per-call charge, there is no admissible spend basis: the plan fails admission (`Unpriceable` for ceiling < charge) or, equivalently, real execution must be refused. The harness makes no cost comparison and no cache-graduation claim.

## Stop and cancellation semantics

Every scheduled evaluation is attempted at most once — there is no automatic retry of a failed provider attempt. Accounting is conservative: `scheduled = attempted + unattempted` and `attempted = completed + failed + invalid`.

| Stop reason            | Trigger                                                      |
| ---------------------- | ------------------------------------------------------------ |
| `complete`             | Every scheduled evaluation attempted once                    |
| `bound_stop`           | A reservation gate refused the next send                     |
| `rate_limited`         | Provider returned HTTP 429 (stops after the current attempt) |
| `accounting_violation` | Reported usage exceeded its authorized per-call bound        |
| `caller_cancelled`     | Caller token (Ctrl+C is bridged to it at the command layer)  |
| `deadline`             | The plan's `max_seconds` outer deadline                      |

One outer deadline is created from `max_seconds`, linked through every `AgentLoop.RunAsync` call into the transport and tools, so an in-flight provider call is cancelled rather than merely preventing later sends. Caller cancellation and the outer deadline are distinguished in the report.

## Credentials

`--execute` reads `AGENTIC_REVIEW_DEEPSEEK_API_KEY` once after full admission, clears the variable immediately, validates it via `DeepSeekCredential`, and hands it to the existing `DeepSeekTransport` boundary. The R3 state-key variable is never touched. The credential never enters a child environment (the harness is a library invocation, not `dotnet run` inside itself), a snapshot, SESSION, a log line or a report. Missing or malformed credentials reject before any transport is constructed. Default tests and `pull_request`/`push` CI never read a credential.

The authorized invocation sequence keeps the provider key out of every child environment:

1. **Build and dry-run keyless.** `dotnet build -c Release` (or `dotnet run --dry-run`) must run without the variable set — `dotnet`/`MSBuild` spawn compiler and tooling children that would inherit it.
2. **Execute the built binary directly.** Invoke the published `AgenticPrReview.Runtime.ReviewEvaluationFixture` binary with `--execute --plan <plan.json>` and the variable set on that one process. Never run `dotnet run ... --execute` with the credential present: every `dotnet` child of that invocation would carry the key before the harness could clear it.

## Output

stdout carries, per attempted evaluation, one Q1 outcome row; then the Q4 `EvaluationReport` JSON; then one `LiveRunSummary` line containing `plan_sha256`, corpus/source identity, schedule and attempt accounting, simulated vs actual call counts, reserved vs known token counters, `usage_unknown_calls`, `accounting_violation`, transport outcome classes, `reserved_spend_micro_usd` vs ceiling, `stop_reason` and `cleanup`. Outcome rows carry `mode` `deterministic` (loopback) or `live` (execute) and distinct attempt identities per repeat.

No provider key, raw provider response, session material, continuation payload, private adjudication data or caller-authored identifier is emitted. The harness writes no persistent adjudication output; the only writable material is the self-test's private scratch root, which is bounded, ACL'd and verified deleted.

## Relationship to V3

`--execute` exists so that [#252](https://github.com/SolusQuest/agentic-pr-review/issues/252) can perform the authorized live observation and manual adjudication. V2 provides the mechanics — bounded admission, real-adapter execution, reservation enforcement, safe reporting — and nothing more. Whether a specific plan, source and budget are authorized for real spend is a maintainer decision outside this command.
