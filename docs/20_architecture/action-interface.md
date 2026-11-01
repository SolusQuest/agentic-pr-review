# Trusted Review Configuration

The [CR2 CLI/Action contract](./cr2-cli-action-contract.md) defines the current `run`/raw-input and final-result boundaries. C# owns product input parsing and defaults; the ordinary Node Action captures raw strings without product validation or metadata defaults. C# emits the final outputs, plain-text summary and canonical annotations; Node validates the private transport and safely presents those supplied facts. The product semantics below remain unchanged.

## Downstream Workflow Admission

H1 ships two installation alternatives: [automatic plus manual maintenance](../../examples/workflows/automatic.yml.template) and [manual only](../../examples/workflows/manual.yml.template). Install exactly one as `.github/workflows/agentic-pr-review.yml` on the repository's default branch, replacing only `__ACTION_SOURCE_SHA__` with the full 40-character authorized released Action commit `T`. These are compiled Host contracts, not a claim that a release is available: the ordinary payload map is still null, and ordinary acquisition refuses execution until separate binding, qualification and publication are authorized. Do not install a moving development ref.

The automatic alternative listens for successful `pull_request` runs of `CI` at `.github/workflows/ci.yml`. It also retains `workflow_dispatch` so an operator can continue or explicitly reset that same workflow's state. Manual dispatch requires a same-repository open nondraft `pr-number` and `state-mode` of `auto` or `reset`; the Host checks the actor's write/admin permission, default-branch run identity and matching explicit event input. Forks, drafts, closed PRs, ambiguous or changed CI/PR associations and untrusted workflow sources cannot reach state, provider or publication work. Automatic execution admits only `auto` and no PR-number override.

Both alternatives use the exact nested composite ref, the same repository/PR concurrency group and `cancel-in-progress: false`. Workflow permissions are empty; the sole review job grants `contents: read`, `pull-requests: write` and `actions: write` for state artifact operations. It grants no `id-token: write`, declares no environment and performs no checkout, SDK setup, build or private-feed restore. The composite supplies the [verified runtime-only .NET prerequisite](./distribution.md#cr1-d4-ordinary-managed-action); an ambient SDK or caller-selected runtime is not its authority. Secrets remain the existing `GITHUB_TOKEN`, `DEEPSEEK_API_KEY`, `AGENTIC_PR_REVIEW_STATE_KEY` and optional `AGENTIC_PR_REVIEW_PREVIOUS_STATE_KEY`, passed only to the nested Action's private inputs.

The admitted H1 profile fixes `config-path` to `.github/agentic-pr-review.json`. Configuration and referenced instructions come from the authorized immutable workflow commit, never the reviewed checkout. A different input path or a source edit to ref, policy mapping, permissions, environment, concurrency, extra steps or other template content is rejected. CRLF normalization is allowed. The two alternatives share an installed path, not interchangeable state identities: changing the template or trusted policy changes the authenticated identity and grants no conversion, automatic reset or cross-workflow alias. Keep the same immutable workflow source for a continuation; selected-current incompatibility fails visibly and any applicable reset remains explicit.

The production selector keeps the historical `.github/workflows/r4-trusted-proof.yml` exact V1 contract in its separate path for the immutable old SESSION producer and managed archive regression. Historical V2 candidate admission stays explicitly injected. Neither proof YAML at the downstream path nor downstream YAML at the proof path is admitted, and candidate-proof fields are denied on the downstream route. Historical H2 proof is not an installation template.

Credential-free authorization/composition tests exercise both alternatives and deny-before-side-effect mutations. The [original managed archive gate](../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/README.md) includes automatic bootstrap, useful manual continuation and explicit reset with output/summary parity, independently of the retained historical cases. Its exact-source Ubuntu result is required for managed execution qualification; source test success alone is insufficient.

## Trusted Configuration

The current Host reads `.github/agentic-pr-review.json` (or an admitted `config-path` input in the separate historical profile) and its instructions only from the authorized immutable workflow commit. Reviewed PR files, ambient checkout content and model output cannot select configuration. The [R4 interface](./r4-actionhost-wrapper-plan.md#r4-d04-trusted-configuration-and-policy) still defines the other inputs and publication fields. This R7-C6 supplement adds only the optional `review` object to `agentic-pr-review.config.v1`.

```json
{
  "schema": "agentic-pr-review.config.v1",
  "instructionsPath": ".github/agentic-pr-review/instructions.md",
  "publication": { "mode": "sticky" },
  "review": {
    "maxModelCalls": 6,
    "maxUncachedInputTokens": 65536,
    "maxCachedInputTokens": 65536,
    "maxOutputTokens": 8192,
    "timeoutSeconds": 120
  }
}
```

The managed production composition uses the accepted `cr1-solus-internal4-dev` profile and exact `0.1.0-internal.4` closure. Its effective configuration follows the [V3 capacity contract](../../runtime/src/AgenticPrReview.Runtime/Agent/Loop/capacity.md). The original trusted policy and outer state hashes still bind the immutable source configuration; actual Solus compatibility independently binds the selected runtime, tools, correction policy and effective limits.

| Integer field            | Allowed range | Omitted default |
| ------------------------ | ------------- | --------------: |
| `maxModelCalls`          | 1..6          |               6 |
| `maxUncachedInputTokens` | 1..65,536     |          65,536 |
| `maxCachedInputTokens`   | 1..65,536     |          65,536 |
| `maxOutputTokens`        | 1..8,192      |           8,192 |
| `timeoutSeconds`         | 1..120        |             120 |

Omitting `review`, using `{}`, or omitting individual fields selects the corresponding managed defaults. Explicit smaller values are respected. Nulls, nonintegers, duplicate or unknown keys and oversized values fail before provider work; they are not clamped or silently interpreted using the historical 64-call profile. The existing strict UTF-8/config-size boundary and trusted workflow source remain. Provider, endpoint, credentials, tools and retry policy are not configurable through this object.

The current profile separately bounds logical calls, physical attempts and work units at six, tools at six, each tool batch at three with concurrency one, review duration at 120 seconds and provider attempts at 30 seconds with one second settlement grace. Independent uncached and cached input limits are 65,536, with a separate total input ceiling of 65,536; their sum does not grant 131,072 total input. Output is bounded at 8,192. Retained context has independent 64-attempt/64-record, 32-input and 196,608-variable-byte ceilings, plus its wire and logical limits. Missing usage remains unavailable; counters do not establish billing guarantees.

Ordinary production Action and retained direct/live argv use the same Solus runtime and state policy. A genuine selected-current historical SESSION, incompatible package or context is rejected before provider work. Only the existing independently authorized reset may start a new epoch; there is no converter, alternate SESSION reader, automatic reset or fallback to the old provider loop. Action reset retains its repository, PR, workflow, producing run and attempt authorization checks. Seven Action inputs, fourteen outputs, framing, current-run accounting, publication barriers and official bridge access keep their existing contracts.

The nonmanaged ordinary Host performs the shared authorization/snapshot preparation and then emits an internal-failure completion with complete zero usage and `not_started` termination. Its default dependencies cannot construct the historical provider runner. Managed archive and installed-runtime proof remain required; native framework/AOT smoke alone does not establish the cutover.

## Current-run Provider Accounting

The immutable R2 numerical aggregate passes through the C# completion domain without new counter collection. P1 emits exactly `protocol`, `build_discriminator`, `process_exit_code`, `outputs`, `summary` and `annotations`, using protocol `apr.cli.result.cr2`. All fourteen output keys are present; the ten numerical values are exact decimal strings or null, and the four status/termination/completeness values are strings. The private Host and Node wrapper switch together with no ordinary H2 reader or compatibility fallback. Strict UTF-8, duplicate/unknown-member, frame/EOF and 16 KiB document boundaries remain.

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

`termination_reason` is a closed review vocabulary. Successful Agent outcomes map to `review_completed`; proven absence of invocation maps to `not_started`. Agent cancellation and deadline codes map to `cancelled` and `deadline_exceeded`; chat failure to `provider_failure`; model/tool/token exhaustion to `model_limit`, `tool_limit`, `token_limit`; request/response size limits to `request_limit`, `response_limit`; context exhaustion to `context_limit`; remaining invalid Agent results to `invalid_result`. `resource_limit` preserves a managed runtime resource refusal when the exact exhausted dimension is unavailable; it never permits a reviewed status. `host_failure` is available for wrapper failures. Arbitrary diagnostic text is never forwarded. Review termination and final Host status are separate: a later Host failure can retain `review_completed` and still exit unsuccessfully. Reviewed statuses allow `review_completed` or recovery `not_started`; skips require complete zero and `not_started`.

The summary retains status, reviewed SHA, publication URL, finding count and state disposition, then adds the termination reason, independent completeness, counters and known sums. Unavailable values are displayed as `Not available`. A missing or malformed Host completion uses fixed wrapper failure text with unavailable facts and no accepted-state claim. Private diagnostics, raw requests/responses, SESSION content, continuations, credentials and private paths never enter accounting or presentation.

C# owns retry geometry, independent completeness, exact Int64 strings, overflow and success consistency. Node consumes checked C# producer fixtures and tests structural fields, protocol/build/actual exit, hostile input and the completion cap; it has no product status table or accounting arithmetic. The supplied summary is escaped inside a fixed literal `<pre>` wrapper, including a bound after entity expansion. Real synthetic HTTP, composition, recovery, early cancellation and exception tests verify every production completion family. Validation remains `npm run check`, `npm run dist:check`, the affected/full Release runtime tests and `npm run runtime:integration`; provider execution is synthetic.

## Machine-readable Action Outputs

R7-H4 introduced exactly fourteen outputs, now preserved by the nested composite and internal Node Action. The [CR2 acceptance](cr2-technical-acceptance.md) identifies current result/metadata owners and actual final-layout execution evidence. C# supplies both summary and outputs from the same canonical completion; no second accounting collector or stored prior-run aggregate is used.

| Output                                      | Meaning                                                |
| ------------------------------------------- | ------------------------------------------------------ |
| `status`                                    | Final Host status, or fixed wrapper `failed`           |
| `termination-reason`                        | Closed review termination reason described above       |
| `model-calls`                               | Logical invocations                                    |
| `provider-attempts`                         | Observed physical sends                                |
| `provider-retries`                          | Additional physical sends                              |
| `provider-failed-attempts`                  | Observed sends without successful finalization         |
| `provider-unknown-usage-attempts`           | Sends missing input or output usage                    |
| `provider-unknown-cache-partition-attempts` | Sends with known input but unavailable cache partition |
| `attempt-accounting-completeness`           | `complete`, `partial`, or `unavailable`                |
| `usage-completeness`                        | Independently `complete`, `partial`, or `unavailable`  |
| `input-tokens`                              | Known input token sum                                  |
| `input-cache-hit-tokens`                    | Known cache hit token sum                              |
| `input-cache-miss-tokens`                   | Known cache miss token sum                             |
| `output-tokens`                             | Known output token sum                                 |

All counts and sums are nonnegative canonical decimal strings within the C# accounting bounds above. Values beyond JavaScript safe integer precision remain exact through Int64.MaxValue. Each unknown or overflowed numerical value is omitted, independently; known lower bounds remain available even when completeness is partial or unavailable. A known zero alongside unknown usage is an observed lower bound, not proof of no consumption. Proven pre-provider and recovery-only invocations emit complete zero for this run.

The Host status vocabulary is `reviewed`, `reviewed_with_inline_warnings`, `skipped_untrusted_event`, `skipped_fork`, `skipped_draft`, `skipped_closed`, `configuration_invalid`, `authorization_failed`, `credentials_missing`, `snapshot_incomplete`, `provider_failed`, `agent_result_invalid`, `stale_head`, `state_conflict`, `sticky_publication_failed`, `outcome_ambiguous`, `cancelled`, and `internal_failure`. The wrapper-only `failed` token accompanies `host_failure` and both completeness values `unavailable`; all ten numerical outputs are omitted. Missing or untrusted completion and ordinary wrapper replacement before presentation use that fixed projection, including late receipt or cleanup failures after parsing. Raw errors, private paths, prompts, tools, SESSION data and summary-only publication metadata never enter outputs.

Once quiescence and cleanup admit a completion, its outputs are attempted before the summary and canonical annotation, including unsuccessful Host results. A later output, summary or annotation sink failure returns a failing step without rewriting those facts as a contradictory wrapper fallback. Each native output write is independent: a failing sink or runner hard kill may leave only a prefix or no outputs. Consumers must inspect the step result and available completeness; absent outputs never imply zero or complete accounting. Unconfirmed Host termination or failed drain/quiescence exits fatally before presentation. Fixed fallback independently attempts each of its four constants and its summary/error once, without retries or numerical substitution. These outputs describe observations rather than billing.

Credential-free validation includes the retained C# accounting corpus and final-result producer fixtures, actual entrypoint replacement/sink/fatal cases, strict metadata drift, real Linux checked-bundle `GITHUB_OUTPUT` execution, and strict native output/summary parity in the framework/AOT verifier. Run `npm run check`, `npm run dist:check`, `npm run runtime:integration`, the affected Release output-oracle/architecture tests, and `bash runtime/scripts/verify-action-host.sh framework` (plus `aot` in its Linux toolchain gate). Historical proof goldens remain preserved; the active H4 golden names the parity metric `outputs_match_summary`.
