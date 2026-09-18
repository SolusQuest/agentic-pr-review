# R5 deterministic evaluation gate

[Issue #250](https://github.com/SolusQuest/agentic-pr-review/issues/250) adds a
provider-secret-free CI gate that executes the complete named R5 deterministic
corpus — quality, incremental, completed replay, growth, reset and the V2
live-harness dry-run — in both framework-dependent and Linux x64 Native AOT
modes, and proves the two modes agree semantically.

The gate is `runtime/scripts/verify-r5-evaluation.sh`. It is additive: the
existing runtime, R2, R3 and R4 proof lanes in
`.github/workflows/runtime-ci.yml` are unchanged.

## Commands

```text
verify-cases --scenario <name> [--corpus <dir>] [--forbid <csv>] --report <file>
verify-cases --trx <file> --require <csv>
verify-cases --parity <verdict-a.json> <verdict-b.json>
verify-cases --cleanup <marked-temp-root>
r5-plan --corpus <bundle-dir> --out <plan.json>
```

All commands live in `runtime/tests/ReviewEvaluationFixture/Program.cs`
(`R5CaseVerifier`) and print one verdict line
(`{"schema":"r5-v1-verdict-v1", ...}`). Exit codes: `0` verified, `1` rejected,
`2` invalid usage.

`verify-cases --scenario` is the outside-in coverage check. The declared case
inventory is compiled into the verifier itself — it is deliberately not read
from the corpus manifests or the reports under test, so a producer that
shrinks or renames coverage cannot pass silently. Per scenario it requires:

- `quality` — `code=verified`, `expected_cases=executed_cases=verified_cases=13`,
  and the exact ordered case set (`cs-defect`, `cs-safe`, `ts-defect`,
  `ts-safe`, `repository-rule`, `sticky-only`, `no-required-tool`,
  `irrelevant-tool`, `wrong-evidence`, `wrong-location`, `safe-invention`,
  `duplicate-proposal`, `pathless-proposal`), every row `verified=true`.
- `replay` — `code=verified`, `cleanup=cleaned`, and the exact step sequence
  `replay-seed`, `replay-same`, `replay-ahead`, all accepted.
- `incremental` — same contract on `incremental-seed`, `incremental-same`,
  `incremental-ahead`.
- `growth` — `code=verified`, `cleanup=cleaned`, and the four declared profiles
  with their measured terminal oracles from the [#249](https://github.com/SolusQuest/agentic-pr-review/issues/249)
  operating envelope: `short` 21 rows ending
  `build/session_construction_limit/append_limit`, `tools` 6 rows ending
  `agent/agent_response_invalid/message_limit`, `continuation` 7 rows ending
  `agent/agent_response_invalid/continuation_limit`, `updates` 13 rows ending
  `agent/agent_response_invalid/message_limit`. Every non-terminal row must be
  accepted; the terminal row must be the rejected attempt with the declared
  classification. An intentional capacity change must update this declaration
  in the same commit.
- `reset-owner` — `code=r5_reset_owner_passed` plus the exact eight emitted
  owner-probe case names. This scenario has no corpus input; it is verified
  against source/topology identity and the case set.
- `live-self-test` — `code=r5_live_self_test_passed`, `cleanup=cleaned`, the
  two emitted pipeline cases.
- `live-plan` — summary `execution_kind=loopback`, `stop_reason=complete`,
  `actual_provider_calls=0`, `scheduled=attempted=13`, `unattempted=0`,
  `invalid=0`, with `plan_sha256`/`corpus_sha256` retained for parity.

For corpus scenarios `--corpus` re-admits the declared bundle directory and
requires its digest to equal the report's `corpus_sha256`
(`seed_corpus_sha256` for growth). `--forbid` scans the raw report bytes for
exact sentinel strings and rejects on any hit: the corpus scenarios list the
five `APR242_*` markers (only `APR242_TERMINAL_CANARY` is substantively
in-scope privacy evidence for the quality producers; the list is applied
uniformly as a conservative superset), and `live-self-test` lists
`APR251_PRIVATE_CONTENT_CANARY`.

Each emitted verdict carries a `parity` object with the semantic evidence for
that scenario. `verify-cases --parity` deep-compares the parity objects of the
framework and AOT verdicts — corpus identity, normalized digests, ordered case
rows, terminal oracles and call accounting must match; volatile identities
(operation/process/session/startup ids, timestamps) are excluded by
construction and artifact hashes are recorded separately, never compared.

`verify-cases --trx` parses the focused-test TRX and requires every declared
class to appear with at least one executed test, a nonzero total, and no
outcome other than `Passed` — a filter that matches nothing is a failure, not
a pass.

`verify-cases --cleanup` is a mechanically constrained private-root deleter:
it only removes a directory that lives under the system temp area, contains
the `.r5-v1-temp-root` marker, and is not a reparse point. It refuses
arbitrary, repository, symlinked or unmarked paths.

`r5-plan` generates the ephemeral V2 dry-run plan: it binds the compiled
`EvaluationSource` identity, re-admits the quality bundle for its digest,
fixes the DeepSeek provider/model/adapter configuration identity, schedules
the 13 declared quality cases once each, and reserves bounded per-call and
total limits. The plan is dry-run input only — it carries no credential, is
written to a private gate root, and the gate never dispatches `--execute`.
Live authorization remains a separate act owned by
[#252](https://github.com/SolusQuest/agentic-pr-review/issues/252).

## Gate topology

```text
bash runtime/scripts/verify-r5-evaluation.sh framework   # build once, all scenarios + focused tests
bash runtime/scripts/verify-r5-evaluation.sh aot         # publish linux-x64 once, all scenarios
bash runtime/scripts/verify-r5-evaluation.sh all         # framework → aot → parity → cleanup
```

- One `dotnet build -c Release` produces the framework artifact; one
  `dotnet publish -r linux-x64 --self-contained -p:PublishAot=true
-p:JsonSerializerIsReflectionEnabledByDefault=false` produces the Native
  AOT artifact. Each mode then runs all seven scenarios through that single
  artifact, followed by `verify-cases` per scenario.
- The focused Host/session set runs in framework mode only — xUnit tests are
  not claimed as Native AOT evidence:
  `R5IncrementalReviewTests`, `R5SessionGrowthTests`,
  `R5CapacityResetTests`, `R5ResetHandoffTests`,
  `R5VerifierCoverageTests`, with TRX admission as above.
- `all` finishes with per-scenario parity checks between the two verdict
  receipts, then fail-closed cleanup: the evidence root is removed through
  `verify-cases --cleanup`, the build root is removed with an explicit
  absence assertion, and the gate cannot pass while either remains.
- Public-safe log lines take the form
  `r5_eval_gate mode=<mode> scenario=<name> result=verified`,
  `r5_eval_gate parity=<name> result=verified`,
  `r5_eval_gate mode=<mode> artifact_sha256=<sha>` and
  `r5_eval_gate cleanup=verified`. The artifact hashes are retained for
  identity; the managed DLL and the native binary are not expected to match.
- All private state (build output, reports, the ephemeral plan, TRX) lives in
  marked temp roots outside the repository so `EvaluationSource.Clean` is
  identical across modes, and an `EXIT`/`INT`/`TERM` trap removes them on
  failure or cancellation.

## CI wiring

`runtime-ci.yml` adds one `r5-evaluation-gate` job that checks out the exact
event-selected head (`pull_request` → `head.sha`, `push` → `sha`,
`persist-credentials: false`, `fetch-depth: 0`), asserts `git rev-parse HEAD`
equals that sha, then runs `npm ci`, `npm run check`, `npm run dist:check`
and the gate's `all` mode. The R5 evidence therefore always reflects the
exact pushed implementation head — including on pull requests, where the
default job checkout would otherwise test the merge ref. No provider secrets,
no protected environments, and no event input reaches the script invocation.

## Validation

```bash
bash runtime/scripts/verify-r5-evaluation.sh all
npm run check
npm run dist:check
```

The `all` mode requires a Linux x64 Native AOT toolchain (clang, zlib); on
Windows run the `framework` mode locally and rely on CI for the AOT leg.
