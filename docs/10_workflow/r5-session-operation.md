# R5 session operation guide

This guide tells a maintainer how to read session-capacity outcomes and when the existing authorized reset applies. It summarizes the measured evidence in [r5-session-growth-results.md](../20_architecture/r5-session-growth-results.md) (issue [#249](https://github.com/SolusQuest/agentic-pr-review/issues/249)); the contracts cited there remain authoritative. Nothing here changes a limit, adds an automatic reset, or grants live execution authority.

## Usable envelope

Measured completed runs before the first effective limit, on the synthetic workloads in the results document:

| Workload shape                           | Completed runs observed | First effective limit                                            |
| ---------------------------------------- | ----------------------- | ---------------------------------------------------------------- |
| Minimal finish-only review               | 20                      | `session_construction_limit` while proving the next request fits |
| Eight file reads per run                 | 5                       | `AgentLimits.Messages` = 64 (proposed message count)             |
| One read plus large continuation per run | 6                       | `AgentLimits.ContinuationTotalBytes` = 262 144                   |
| One read per run with head updates       | 12                      | `AgentLimits.Messages` = 64 (proposed message count)             |

Real workloads differ; treat these as measured synthetic points, not guarantees. The format cap of 64 completed runs is not reachable by a legal minimal accepted history under current limits — the message-count construction proof binds first.

## Reading a rejection

Normal capacity exhaustion is a **rejected append**, not corrupted state:

- The Agent or SESSION builder returns `session_construction_limit` or `agent_response_invalid` when the next reconstructed request or continuation would cross a configured bound.
- The candidate is `NotCommitted`; the accepted predecessor remains the current session and continues to read back intact. Nothing is silently truncated, compacted, summarized, or reset.
- The measured matrix kept `predecessor_preserved` true on every rejection.

Distinguish this from other rejection families before acting:

- **Invalid or tampered state** — `session_*` validation/transition rejection codes (for example `session_transition_rejected`, continuation or canonicalization defects) mean the artifact failed admission. That is an integrity signal to investigate, not a capacity signal; do not respond by resetting.
- **Unauthorized reset denial** — a reset that lacks the Host-authorized capability, the bound workflow run/attempt, or the selected lineage fails before any mutation. Denial is the intended outcome, not a stuck session.
- **Ordinary Agent/provider failures** (for example `agent_chat_failed`) leave state `NotCommitted` and can be retried through normal continuation; they do not by themselves justify a reset.

## Existing explicit reset semantics

Reset is a deliberate, authorized operation — never automatic:

- Only the Host-authorized path can reset, bound to the verified workflow run/attempt. SESSION's `ExplicitReset` branch alone is not a production reset capability.
- An accepted reset starts a new session epoch at generation 0. Old SESSION records, tool history and provider continuation are not transferred into the new session; a fresh session may still independently observe the same public repository facts again.
- A bounded publication-target handoff may carry proof of the previously published sticky comment — scoped to repository/PR identity, exact comment ID/URL, body/scope digests, reviewed head, source acceptance identity and an absolute expiry — so the fresh review can update that same comment. It is never provider input, never extends its original expiry on retry, and a substituted, malformed, expired or unresolved target does not authorize publication. A proven-absent comment may be recreated once through ordinary admission; a comment appearing after that proof is rejected.
- An exact reset retry reuses the authenticated successor and its accepted state; a distinct authorized run/attempt produces a distinct session. Pending or completed retries recover the durable intent or head instead of reselecting partly deleted records.
- Once a fresh generation is accepted, ordinary continuation proceeds on the new lineage and no longer consults the older handoff.

The detailed normative contract is [r4-reset-publication-handoff.md](../20_architecture/r4-reset-publication-handoff.md); the lifecycle assertions are executed by `R5CapacityResetTests` and `R5ResetHandoffTests`.

## When an authorized reset is needed

Reset is appropriate only when the session has genuinely reached capacity — the measured first limits above — or when a maintainer deliberately retires a session's accumulated history. Because reset ends continuity and starts a new epoch, it is not a remediation for invalid state, transient provider failures, or unauthorized rejections; those keep their own fail-closed handling. There is no scope-mismatch reset and no best-effort replay.

## Reproducing the observations

From a clean repository checkout (`git status --porcelain` empty — the reports record `sourceClean` and dirty output is iteration evidence only):

```sh
dotnet run --project runtime/tests/ReviewEvaluationFixture/AgenticPrReview.Runtime.ReviewEvaluationFixture.csproj --configuration Release -- replay --bundle runtime/tests/fixtures/agent/r5/growth
```

Expect `code: "verified"` and `cleanup: "cleaned"`, four profile reports whose terminal rows are the rejected attempts listed above, and a stable `normalized_sha256` across repeated executions.

```sh
dotnet test runtime/tests/AgenticPrReview.Runtime.Tests/AgenticPrReview.Runtime.Tests.csproj --configuration Release --nologo --filter 'FullyQualifiedName~R5CapacityResetTests|FullyQualifiedName~R5ResetHandoffTests' --logger 'console;verbosity=detailed'
```

Expect the `r5-reset-capacity-v1` lifecycle JSON on a passing run: capacity phase 5 with `agent_response_invalid`, an explicit-reset phase 7 acceptance, `epochChanged`/`sessionChanged`/`priorContentExcluded`/`freshContinuationAccepted` all true, plus the reset owner-probe report `r5-reset-owner-v1` with all eight `passedCases`. Capture the runner exit status alongside the JSON.

```sh
dotnet run --project runtime/tests/ReviewEvaluationFixture/AgenticPrReview.Runtime.ReviewEvaluationFixture.csproj --configuration Release -- reset --fixture self-test
```

emits the standalone owner-probe report directly. All inputs are synthetic; no provider credential or GitHub mutation is involved.

## Authority pointers

- `runtime/src/AgenticPrReview.Runtime/Agent/AgentLimits.cs` — configured limit registry.
- [agent-session-format.md](../20_architecture/agent-session-format.md) — SESSION construction, capacity and rejection semantics.
- [r4-reset-publication-handoff.md](../20_architecture/r4-reset-publication-handoff.md) — authorized reset and bounded publication-target handoff.
- [Growth profiles README](../../runtime/tests/ReviewEvaluationFixture/Growth/Profiles/README.md) and [reset fixture README](../../runtime/tests/fixtures/agent/r5/reset/README.md) — harness semantics and command contracts.
