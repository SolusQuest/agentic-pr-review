# R7: Versioned Experimental Distribution And Maintainer Adoption

## Authority And Activation

This records the approved R7 target contract. Activation requires the coordinated documentation change to merge for [R7-C1 #329](https://github.com/SolusQuest/agentic-pr-review/issues/329). It translates the maintainer-approved 2026-10-01 decision appendix in [R7-MASTER #322](https://github.com/SolusQuest/agentic-pr-review/issues/322) into durable repository authority for [milestone 13](https://github.com/SolusQuest/agentic-pr-review/milestone/13).

- Design and implementation baseline: [`93fa835e8932eed30e706a79825a367ac02b9247`](https://github.com/SolusQuest/agentic-pr-review/tree/93fa835e8932eed30e706a79825a367ac02b9247).
- Master decision source SHA-256: `61c993ee1917928df52dedd90f50700ff3b3830e4312ae5fe284b7f4f24f573f`, as recorded in the approved master appendix.
- Dependent implementation begins only after this documentation change merges and its declared native issue dependencies are satisfied. An issue, draft PR, ready-for-review state or review verdict does not activate it.

The targets below are not claims of implemented features or a published release. This plan supersedes older forward-looking R7 default-graduation, distribution and capacity projections, including those in the R4 plan. R4 documents remain authoritative for their current implementation facts and historical proof. The [security boundary](../20_architecture/security-boundary.md) and Host-owned credential, state and publication safeguards remain required.

| Surface       | Implementation at the pinned baseline                           | Approved R7 target                                                                 |
| ------------- | --------------------------------------------------------------- | ---------------------------------------------------------------------------------- |
| Action        | Nested Node 24 prepared-payload proof; seven inputs, no outputs | Same nested path and inputs; exact release download and defined accounting outputs |
| Provider      | `deepseek-v4-flash`; smaller request and review limits          | Explicit `deepseek-flash` identity and the bounded profile below                   |
| Configuration | Trusted instructions/publication configuration                  | Optional trusted `review` object with five bounded fields                          |
| Distribution  | Repository-controlled proof; no supported downstream release    | Publicly downloadable, pinned experimental prerelease on `ubuntu-24.04`            |
| Evidence      | R4 proof, R5 evaluation and bounded accepted R6 evidence        | Exact-byte qualification and separately authorized bounded adoption                |

R6 bounded engineering/evaluation closeout was accepted by the [final #271 assessment](https://github.com/SolusQuest/agentic-pr-review/issues/271#issuecomment-5928401721) after PR #320. Complete descriptive A/B populations, independently AI-adjudicated bounded V5 quality/safety and H's reduced-capacity reset observation supplement the earlier insufficient evidence. Historical reports remain unchanged. Native-history equivalence, live segmented-prefix association, formal regression, actual billing and default-capacity adequacy remain unknown or inconclusive; none supplies graduation credit or becomes an inherited mandatory R7 gate. R5's earlier live quality result is not retrospectively changed.

## Product And Authority Boundaries

Deliver a public, downloadable, explicitly versioned experimental prerelease for maintainer use in public, private and test repositories. Others may use it without promises of completeness, accuracy, production suitability, compatibility, support SLA or maintenance duration. Fixed published bytes remain immutable. Formal public recommendation/default graduation is excluded and requires a later explicit maintainer initiation after actual use; neither release nor R7 completion triggers it.

The canonical channel is `vMAJOR.MINOR.PATCH-internal.N`, with `N >= 1`. `internal` describes maturity, not private visibility or runtime privilege. The first exact version remains an execution input.

The initial target is one `linux-x64` Native AOT executable, GitHub-hosted `ubuntu-24.04`, the bundled Node 24 thin wrapper and existing `.github/actions/agentic-pr-review` path. Keep the existing seven inputs: `github-token`, `provider-api-key`, `state-key`, optional `previous-state-key`, `config-path`, manual-only `pr-number`, and `state-mode` (`auto` or authorized manual `reset`). The outputs below are additions owned by later implementation.

There is no root alias, second provider/platform, worker split, arbitrary Linux/self-hosted support, fork/draft PR support, automatic reset/compaction, converter or internal component release matrix. Windows development remains possible but is not a distributed target. Fully verified repository-test payload overrides remain unreachable through ordinary downstream inputs.

Each leaf requires a separate execution task. Preparation starts with exact source/version/builder authority and produces candidate identities. Binding produces the reviewed Action commit; staging and publication require the resulting exact qualified candidate and stage-appropriate approval. Release-only operations make no provider calls and need no fictitious provider allocation. Deployment/provider execution additionally requires selected repositories, approved content and finite calls/time/cost authority. Secret/environment/protection provisioning and merge remain maintainer-owned. No planning, implementation or review verdict authorizes release, paid calls, deployment or issue/milestone closure.

## Provider, Defaults And Trusted Configuration

The R7 target uses DeepSeek `deepseek-flash`, thinking enabled/high, the official endpoint and `stream:false`. The approved decision records a 2026-10-01 documentation observation that `deepseek-v4-flash` is an older model alias served by DeepSeek-V4.1-Flash. [R7-R1 #335](https://github.com/SolusQuest/agentic-pr-review/issues/335) owns the explicit request/response identity and continuation update; old and new adapter/session identities are not interchangeable. Selected-current incompatible state fails closed and requires explicit authorized reset where applicable.

| Target limit                                           |             Default / ceiling |
| ------------------------------------------------------ | ----------------------------: |
| Output per request, including provider reasoning       |                 65,536 tokens |
| Logical model calls per review                         |  64; configurable ceiling 128 |
| Tools per review / per response / concurrent execution |                  512 / 16 / 1 |
| Known uncached input stopping allowance                |              2,000,000 tokens |
| Known cached input stopping allowance                  |             38,000,000 tokens |
| Derived total input allowance                          | 40,000,000 tokens at defaults |
| Known output stopping allowance                        |                524,288 tokens |
| Whole Agent review                                     |                   900 seconds |
| Complete provider attempt / connection                 |      300 seconds / 15 seconds |
| Downstream workflow                                    |                    30 minutes |

Calls are upper bounds, not promises of completing them within the deadline. Four-way tool execution is deferred; adoption timings may motivate a separate optimization without reopening this release acceptance.

Trusted configuration adds an optional `review` object to the current closed JSON contract:

| Integer field            | Allowed range | Omitted default |
| ------------------------ | ------------- | --------------: |
| `maxModelCalls`          | 1..128        |              64 |
| `maxUncachedInputTokens` | 1..2,000,000  |       2,000,000 |
| `maxCachedInputTokens`   | 1..38,000,000 |      38,000,000 |
| `maxOutputTokens`        | 1..524,288    |         524,288 |
| `timeoutSeconds`         | 1..900        |             900 |

This allows lowering token/time limits and raising calls from 64 to 128. Increasing safety ceilings later requires a reviewed change. Derive total input from the configured partitions and remove the obsolete combined-token threshold. The effective policy participates in canonical policy/limits identity. No model/provider/endpoint/secret/structural-limit knobs are added. The Host selects immutable trusted configuration/instructions; reviewed code, consumer payloads and model input cannot select proof controls.

## Physical Attempts, Usage And Retries

Logical calls, physical sends, additional retries, failed attempts and unknown-usage sends are distinct. Conservatively count a physical send once transport dispatch begins; a provably local rejection before dispatch is not a send. Retain received validated known usage even if subsequent response/tool validation fails. Invalid or unavailable usage stays unknown. Charge validated total input with unavailable cache split conservatively to the uncached stopping allowance without inventing an observed cache miss. Record missing total/output usage separately, never as guessed measurements.

The selected policy permits availability-oriented bounded retries after potentially billed unknown-usage failures. The token allowances are known-usage post-response stopping thresholds, not billing guarantees; final-response overruns are possible. No hard currency cap or usage estimate from an assumed cache-hit ratio is permitted. Retry count and deadline bound unknown exposure, while actual billing may exceed known totals.

Before each physical attempt, check that known-usage thresholds and the shared deadline are not exhausted. Account for each response or unknown observation before further attempts or tools. Known overrun stops work and cannot become a successful clean review. Request output is capped at `min(65,536, remaining known output allowance)`; no output allowance means no dispatch. Byte/message bounds are distinct from model tokens. Before sending, use a verified provider-specific token count or documented conservative upper bound with output headroom; reject visibly if no defensible admission bound exists. Provider context rejection is non-retryable and incomplete. Never silently truncate/compact or present byte length as exact token counting.

Retry rules:

- At most two additional sends per logical call and eight per review.
- Only explicit transient connection failures and HTTP `408`, `429`, `500`, `502`, `503`, `504` qualify.
- Authentication/payment/invalid request, malformed response, invalid tool arguments, cancellation, exhausted known-usage threshold and context-capacity failures do not qualify.
- Retry the identical projected logical request with fresh transport. Tools, state and publisher operations are not retried by this mechanism.
- Exponential full jitter uses a one-second base and 30-second cap. Valid bounded `Retry-After` may extend delay only within the shared deadline.
- Every retry shares the review clock and contributes all known usage. A server error with missing usage remains unknown and potentially billed but does not alone prohibit the selected bounded retry.

With `L` logical calls, dispatched sends cannot exceed `L + min(2L, 8)`: 72 at 64 calls and 136 at 128, before earlier stops.

The Host uses one envelope: at most 24 minutes before sticky/publication admission, including snapshot/state/Agent work; the Agent receives `min(900 seconds, remaining pre-sticky time)`. At most four minutes remain for post-write reconciliation/finalization within 28 minutes of Host time. The 30-minute workflow leaves wrapper/cleanup room. State provisioning, locator authority and latest acceptance coverage derive from this same horizon, replacing the old 900-second pre-sticky assumption for the R7 production profile. Existing proof profiles gain no unrelated capabilities. Hard kill may prevent finalization and remains unknown.

Only a successful validated response contributes continuation/history once. Failed attempts create no partial SESSION records. Provider failures and capacity/deadline exhaustion preserve incomplete status, cannot publish a normal no-findings review and cannot commit successful successor state. Existing reconciliation of already-authorized Host side effects remains authoritative.

## Capacity And State Closure

These are independent R7 target ceilings, not a promise that all maxima fit simultaneously:

| Logical / transport target                |               Ceiling |
| ----------------------------------------- | --------------------: |
| Messages / parts / SESSION records        | 4,096 / 8,192 / 8,192 |
| Single / cumulative continuation          |         1 MiB / 8 MiB |
| Serialized request / response             |         8 MiB / 2 MiB |
| SESSION plaintext / encrypted envelope    |       16 MiB / 32 MiB |
| Completed reviews per session             |                    64 |
| Changed files                             |                   500 |
| Complete diff snapshot / per-file diff    |        32 MiB / 2 MiB |
| Raw file read                             |   1 MiB and 800 lines |
| Serialized single / aggregate tool result |        64 KiB / 8 MiB |
| Findings / inline publications            |                20 / 5 |
| Tracked paths / repository root           |      20,000 / 256 MiB |

Implementation owners must derive and test subordinate bounds across parsers, SESSION records, encryption, candidate/scope totals, the Node artifact bridge, staging and upload/download. Their exact capacity table must record effective minima and encoding multipliers. Audit unique-object/request/metadata/search/page caps so declared supported scenarios execute; test deterministic pagination and explicit incompleteness rather than increasing every unrelated cap. Preserve bounded-before-allocation checks and whole-batch validation before any tool runs.

Retain complete history, authenticated scope/current-state admission, encrypted continuation and Host lineage. True absence or Host-classified historical non-current state may bootstrap observably. Selected-current incompatibility, corruption, missing data, ambiguity, unsafe ancestry or invalid continuation fails closed, including automatic discovery. Explicit restore of incompatible state fails closed. Operator recovery uses explicit authorized reset where applicable; capacity exhaustion does not automatically reset or compact. Plaintext reasoning/tool/continuation material stays out of public artifacts, Git, caches, logs and outputs; the downstream-owned state key stays separate from provider credentials and payloads.

## Summary And Machine-Readable Outputs

One validated Host completion projection drives both the existing summary and these target outputs:

| Output family      | Names                                                                                                                                                              |
| ------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Outcome            | `status`, `termination-reason`                                                                                                                                     |
| Calls and attempts | `model-calls`, `provider-attempts`, `provider-retries`, `provider-failed-attempts`, `provider-unknown-usage-attempts`, `provider-unknown-cache-partition-attempts` |
| Completeness       | `attempt-accounting-completeness`, `usage-completeness`                                                                                                            |
| Known token sums   | `input-tokens`, `input-cache-hit-tokens`, `input-cache-miss-tokens`, `output-tokens`                                                                               |

Counters are decimal nonnegative strings. Known usage fields sum validated known observations and must be interpreted with `usage-completeness=complete|partial|unavailable`. Attempt completeness independently uses the same closed three-value domain. A fully observed no-provider run has complete zero counters; absent completion is not a zero run. Unknown cache-partition count covers admitted total usage whose split is unavailable. The status/termination enum vocabulary remains closed and product-owned by the implementation owners, never arbitrary exception text; this activation does not invent its exact members.

Emit valid failure completion before nonzero exit when possible. For pre-launch/abrupt termination with no trustworthy Host finalization, the wrapper emits fixed failure/unavailable status if it can run and omits unknown counters. Runner hard kill may leave no outputs. Missing outputs never mean zero. Preserve summary facts: reviewed SHA, publication URL, finding count and state disposition. Export no prompts, tools' content, continuation, raw transport data, private paths, credentials or arbitrary exception strings.

## Exact Release Identities And Trust

Use four separate identities:

- `S`: payload source commit.
- `W`: approved builder/workflow identity, which may differ from `S`.
- `B`: original prepared archive bytes plus producer run/artifact/digests.
- `T`: later trusted Action commit authorizing the exact map; `S != T`.

The small strict Action map covers one version/platform: user-facing `releaseVersion`, `S`, `W`, exact asset repository/tag/name, archive byte count/SHA-256, internal file inventory/sizes/hashes and build identity. The candidate pair binds `T` to original `B` and producer identity without binary self-hash/self-source cycles. Concrete manifest/layout/map mechanics remain implementation-owned within this contract.

Use transport tag `payload-vX.Y.Z-internal.N` at `S` and consumer Action tag `vX.Y.Z-internal.N` at `T`, sharing one semantic release version. A fixed Action SHA uses the same map. No component version matrix is introduced.

The implementation tooling and first real execution are separate outcomes:

1. Prepare builds/tests `B` from `S` without publication credentials, recording actual `W`, source/build-input closure, original artifact/run/digest and proposed map. The first authorized preparation does not take final `T` as an input.
2. A focused binding PR records the map digest. Separately authorized maintainer merge produces `T`. Run the implemented qualifier on original `B` and exact `T`; read-only finalization validates the map and emits a new candidate digest without pretending the payload source was `T`. The qualification receipt binds the same `B/S/W/T` for later staging/publication.
3. Stage the exact qualified pair as an unpublished draft under staging authority. Preserve original bytes; read back exact release/asset IDs, tags, digests, attestation, protection and draft-state qualification. The consumer Action tag stays absent.
4. Under exact-candidate publication approval and fresh qualification/protection evidence, publish the payload assets, verify public bytes/provenance, then create the consumer Action tag last.

Changed admitted payload build inputs require a new candidate. Promotion never rebuilds/repackages. Expired/missing/ambiguous producer artifacts stop the handoff; external mutation uncertainty reconciles by exact identity rather than duplicates, replacement/clobber or implicit latest. Stage/publish operations use pre-read, one write, readback and durable at-most-once intent as owned by the release leaves.

Producer attestation and final publish verification are mandatory, restricted to expected repository, signer workflow, hosted-runner identity and actual workflow/source claims. OIDC signing authority is separate from build and release-write authority. Attesting `W` alone does not prove detached `S`: independently verify the candidate/build-policy binding. Consumer SHA-256 verification against the reviewed Action map is mandatory. Mandatory online consumer attestation is deferred to avoid runtime network/tool dependencies; the later release runbook supplies independent verifier commands. Consumers reject wrong version/platform/build/archive/member/executable and preserve opened-executable identity through spawn. Immutable release settings/protection provisioning remain maintainer-owned execution preflight, not inferred from docs or changed by this activation.

## Trusted Workflows, Qualification And Adoption

SDK-free templates and C# Host admission change together. Provide `workflow_run` and manual `workflow_dispatch` routes with exact trusted Action ref, a common repository/PR concurrency key, `cancel-in-progress:false`, immutable Host-selected policy, least-privilege credentials, encrypted state, explicit reset and fork/draft denial. Remove SDK-build/proof environment requirements from downstream templates; close and test allowed substitutions. GitHub/state/publication remain Host-owned; tools remain read-only and provider clients receive only provider credentials. Recheck immutable review identity before side effects and reject stale/incomplete results.

Credential-free deterministic gates cover the expanded multi-turn/default-profile capacity, retry accounting, hostile inputs/secrets, original Native AOT candidate bytes through production Host/Agent/fake-provider/tool/publisher composition, cross-process/cross-workflow continuation/reset, failures, cancellation and wrong identity. A successful build, rebuilt verifier or linked unit coverage cannot substitute for exact-byte qualification. Ordinary push/PR tests use fake providers and no provider secrets. Historical proof fixtures remain evidence until a named replacement owner and retirement gate exist. Planned fixture commands are not advertised as already available.

After exact-candidate publication approval, the public prerelease is independently verified through normal release acquisition in a public synthetic test repository, then bounded maintainer public and private repositories. Each campaign needs exact repository/PR/head/version, approved provider-visible content, credential/environment ownership, finite calls/time/cost and cleanup/stop authority. Private visibility does not mean local-only model processing. Publish only allowed metadata and sanitized conclusions; no private source, PR content, tools, SESSION/reasoning or secrets. Evidence remains limited to tested cases and supplies no broad quality/default graduation claim, new cache-hit target, billing proof or inherited formal-regression gate.

## Owners, Dependency Order And Completion

The [published master graph](https://github.com/SolusQuest/agentic-pr-review/issues/322#issuecomment-5938127678) contains six trackers and 35 separate leaves. Native blocked-by edges and each leaf's current contract govern execution; parentage expresses ownership, not execution permission.

| Tracker                                                                 | Owned delivery                                                                                                    |
| ----------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------- |
| [R7-C #323](https://github.com/SolusQuest/agentic-pr-review/issues/323) | This activation; opaque transport, SESSION, snapshot, tools and trusted review configuration                      |
| [R7-R #324](https://github.com/SolusQuest/agentic-pr-review/issues/324) | Provider identity, physical attempt accounting, logical/tool ceilings, budgets, deadlines and retries             |
| [R7-D #325](https://github.com/SolusQuest/agentic-pr-review/issues/325) | Production Host route, versioned AOT package, strict resolver and nested Action wiring                            |
| [R7-H #326](https://github.com/SolusQuest/agentic-pr-review/issues/326) | Host/summary accounting, matching outputs, trusted SDK-free templates and operating instructions                  |
| [R7-P #327](https://github.com/SolusQuest/agentic-pr-review/issues/327) | Prepare/attest/finalize/stage/promote tooling, then separately authorized first-candidate binding and publication |
| [R7-V #328](https://github.com/SolusQuest/agentic-pr-review/issues/328) | Capacity proof, exact-pair qualifier, adoption procedure and three separately authorized campaigns                |

After C1 merges, C2 [#330](https://github.com/SolusQuest/agentic-pr-review/issues/330) and R1 [#335](https://github.com/SolusQuest/agentic-pr-review/issues/335) may proceed independently, then converge at C3 [#331](https://github.com/SolusQuest/agentic-pr-review/issues/331). Shared paths follow declared handoff order. Actual first preparation P6 [#354](https://github.com/SolusQuest/agentic-pr-review/issues/354) follows implemented qualification V2 [#359](https://github.com/SolusQuest/agentic-pr-review/issues/359); P7 [#355](https://github.com/SolusQuest/agentic-pr-review/issues/355) binds and qualifies exact `T`, P8 [#356](https://github.com/SolusQuest/agentic-pr-review/issues/356) stages that pair, and P9 [#357](https://github.com/SolusQuest/agentic-pr-review/issues/357) publishes only after the adoption procedure V3 [#360](https://github.com/SolusQuest/agentic-pr-review/issues/360) is also ready. Public test V4 [#361](https://github.com/SolusQuest/agentic-pr-review/issues/361) precedes public/private maintainer campaigns V5 [#362](https://github.com/SolusQuest/agentic-pr-review/issues/362) and V6 [#363](https://github.com/SolusQuest/agentic-pr-review/issues/363).

R7 completion requires one pinned downloadable public experimental prerelease without downstream SDK/latest; implemented and tested capacities, budgets, retries and matching summary/outputs; authorized SDK-free templates preserving state/credentials/publication safety; exact candidate qualification with refusal paths; accepted bounded evidence for all three adoption targets and usable instructions; explicit limitations and separate maintainer closeout authorization. Formal graduation remains a later decision.

## Documentation Activation Validation

For C1, run the existing credential-free gates:

```bash
npm run check
npm run dist:check
```

Also inspect the seven-file diff, relative links, current/target wording and semantic coverage against the approved master. This documentation activation changes no runtime, metadata, fixture, generated bundle or historical result. Later implementation owners must document and add their applicable fixture commands to CI before claiming those gates are available or passed.
