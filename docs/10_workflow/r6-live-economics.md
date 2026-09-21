# Bounded resumed-session economics runner

`ReviewEvaluationFixture economics-live --plan <path>` runs the authored scenarios with a deterministic transport by default. It uses fresh worker processes, the product Agent, read-only snapshot tools, the DeepSeek request/response adapter, and the restricted local SESSION store. `--dry-run` is an explicit spelling of the same mode. Neither spelling reads a provider credential or creates an HTTP transport.

Build the evaluator before preparing a plan:

```bash
dotnet build runtime/tests/ReviewEvaluationFixture/AgenticPrReview.Runtime.ReviewEvaluationFixture.csproj --configuration Release
dotnet runtime/tests/ReviewEvaluationFixture/bin/Release/net10.0/AgenticPrReview.Runtime.ReviewEvaluationFixture.dll economics-plan --replay runtime/tests/fixtures/agent/r5/replay --growth runtime/tests/fixtures/agent/r5/growth --tariff /absolute/path/tariff.json --out /absolute/path/prepared-plan.json
dotnet runtime/tests/ReviewEvaluationFixture/bin/Release/net10.0/AgenticPrReview.Runtime.ReviewEvaluationFixture.dll economics-live --dry-run --plan /absolute/path/prepared-plan.json
```

The tariff uses the existing [R6 pricing format](r6-economics.md). Preparation captures its digest and the exact compiled source/build, corpus identities, provider configuration, thinking mode, schedule, spacing and bounds. The output path must not already exist. Preparation performs no provider requests and grants no human execution authority. A plan becomes stale after a relevant build, source, corpus or tariff change; prepare it again rather than editing provenance to make old evidence fit.

The default preparation schedules three replay phases (bootstrap, same-head continuation and incremental continuation), six tools-growth phases followed by explicit reset and two fresh phases, and seven continuation-growth phases followed by reset and two fresh phases. These capacity positions belong to the authored deterministic workload. Live responses can reach a different boundary or fail to establish representative history. Such a campaign stops with an explicit insufficiency result; it does not move the reset, retry, truncate history or tune requests.

## Explicit execution

Only a separately authorized finite live observation may use:

```text
economics-live --execute --plan /absolute/path/prepared-plan.json
```

Execution requires a clean compiled source and the selected build. The parent admits the entire plan before starting children. Each child independently admits source/build, its full allocation, captured corpus and selected predecessor, then sends a bounded ready frame. Only after validating that frame does the parent read the existing R3 provider credential variable and forward it through a separate private frame to the transport constructor. No configured Host state key is read: the private local state uses a newly generated ephemeral key. Child environments are allowlisted and do not receive GitHub/Actions credentials or a parent environment snapshot. The worker has no GitHub publisher or ActionHost composition.

This implementation and its tests make zero paid requests. A generated plan is a preparation artifact, not approval for V2 or R7, a release, or publication.

## Allocation and observed costs

Each child receives the production maximum of eight model calls and eight complete uniform per-call reservations. The output basis is 4096 tokens, matching the unchanged adapter request policy. The input basis cannot exceed 32768 tokens, so a full lease fits current product token limits. Smaller independent child quotas are unsupported and reject before launch: their exhaustion would have a different meaning from T2's campaign-wide `budget_refused` event.

The parent commits a whole child allocation before launch and never reuses it, even when the child finishes cheaply. The complete expanded schedule must fit the campaign bounds, including repeats and spacing. The Agent prevents a ninth chat invocation. There is no automatic provider retry, allocation refill or implicit reset.

| Quantity               | Report meaning                                                              |
| ---------------------- | --------------------------------------------------------------------------- |
| `allocations`          | Parent capacity committed before child admission; includes unused capacity. |
| `journal.reservations` | Actual per-call reservation events admitted by the existing T2 reader.      |
| `journal.calls`        | Exact dispatch, refusal and known/unknown usage observations.               |
| `pricing`              | Existing T3 reference-tariff calculation for the admitted full campaign.    |

The per-call input and charge basis does not prove provider tokenization or billing. An observed basis violation retains its known usage and stops further sends. The fixed USD tariff must cover worst-case selected hit/miss/output reference charges; it is not authenticated current billing information. Review the conservative preparation ceilings before any separately authorized execution.

## State and failure outcomes

Agent completion, evaluation quality, candidate preparation, parent acceptance, independent readback and fresh-child restoration are separate events. A completed evaluation keeps its original status and costs if later state preparation or acceptance fails. A preparation failure's optional recovery receipt does not mean the candidate was prepared. A failed predecessor stops the remaining declared tail; repeated chains cannot bypass that stop. Only a specifically planned and evidenced capacity stop permits its following reset.

Reset uses existing authorized deletion of the owned local restricted-state scope after verifying its selected predecessor. Deletion and absence are read back before a new session accepts generation zero and a fresh child restores it. This proves the local harness transition. It does not claim the production ActionHost epoch or sticky-publication reset path.

Missing or invalid child receipts retain the full allocation, full scheduled denominator and any separately received evidence. Each receipted step keeps a scoped `observation` with its exact call inventory, reservations, bounded capacity measurements and prefix/session hashes. Total usage and cost remain incomplete, and full-campaign C1 export is unavailable. A smaller complete journal is never substituted for that campaign. A child that cannot be reaped prevents successors and private-root deletion; `cleanup_failed` records that outcome.

The public report excludes raw requests, responses, SESSION plaintext, keys, paths and environment contents. It contains fixed codes, selected hashes, counters, realized intervals and admitted evaluation/pricing projections. Private IPC is never a public artifact.

Ctrl+C cancels the command cooperatively: the runner stops admitting successors, terminates and reaps its active worker, retains the known prefix and committed allocations, cleans its owned root and emits a cancellation report with a nonzero exit. Cancellation before plan admission emits a fixed diagnostic. Stderr progress contains only `r6_economics_child_ready <index> <pid>` after validated readiness and `r6_economics_step_completed <index>` after acceptance/readback; stdout remains the final JSON report. Forced process termination is outside this cooperative guarantee.

The synthetic credential regression delivers a fake provider credential through the same second private frame into the existing HTTP transport's capturing test handler. It checks distinct credential canaries and the actual ephemeral state key across full outbound requests, completed/restored SESSION bytes, encrypted storage and actual worker environments. It performs no network request and keeps the reported execution kind `loopback`; captures never enter public evidence.

## C1 handoff and limits

When all attempted children are reconciled, `journal` and `pricing` pass the existing strict T2/T3 readers, including known failures and the unattempted tail. `outcomes` supplies the unchanged evaluation evidence for a C1 comparison side. Select the left/right pricing and evidence digests using C1's existing declaration format and run `economics-compare`; the runner does not invent a baseline, preregister a rule or authenticate annotation origins.

When `c1_handoff` is `unavailable_missing_receipt`, neither nested journal nor pricing is supplied. Scoped outcomes are still evidence of those receipted attempts, not the missing campaign traffic.

C2 lifecycle observations are not relabeled as deterministic P2 `HistoryReport` artifacts. C1's native history-equivalence and lifecycle-association limitations, unverified predeclaration, unknown execution-time billing, inconclusive historical R5 model quality and unevaluated R7 readiness remain unchanged. Deterministic and injected reports identify themselves as `loopback`.

Permanent R6 gate integration belongs to V1. A successful framework test or Native AOT build alone is not evidence that this command executed in a native worker.
