# R4 migration cutover handoff

Status: W13 and E1 are closed at commit `2337e8ae9d3ca0db88f8a38f36c2f17e46a868fc`, tree `17bbdc8e1cb6591112a7c871ffba9108ecf3680f`, as recorded on issue [#175](https://github.com/SolusQuest/agentic-pr-review/issues/175). E2 issue [#179](https://github.com/SolusQuest/agentic-pr-review/issues/179) closed the final-tree Linux x64 Native AOT gate. R4-E2P issue [#216](https://github.com/SolusQuest/agentic-pr-review/issues/216) owns the private trusted payload and supplemental receipt required before E3 [#180](https://github.com/SolusQuest/agentic-pr-review/issues/180).

## Current implementation boundary

The development tree has one C# business Host and one thin Node wrapper. C# owns GitHub facts, configuration admission, snapshots, Agent execution, protocol validation, encrypted state, sticky and inline publication, and transaction recovery. The Node surface is limited to the nested seven-input/no-output Action wrapper, its lockstep launcher seam, official artifact operations, bounded presentation, cancellation, and exit forwarding.

The nested Action remains repository-controlled prepared-payload proof. It has no root alias, automatic payload or release download, stable output, implicit `latest`, or downstream support promise. R7 owns any supported public distribution surface.

The surviving TypeScript/JavaScript source has five closed classifications:

1. thin wrapper and official artifact bridge production code under `src/action-wrapper/**`;
2. wrapper, bridge, distribution, migration, and conformance tests;
3. `src/artifact-provenance-vectors.ts` plus its test as the permanent S2 negative and transport evidence;
4. the residual allowlist and guard as permanent conformance machinery;
5. generated distribution and bounded build/verification scripts.

No TypeScript business Host, state selector/store, publisher, review protocol adapter, structured-result mapper, prefix implementation, provider-metadata implementation, canonicalizer, runtime invocation route, or shared review DTO/utility module remains.

## Contract and fixture inventory

The live direct-runtime contracts are `protocol/schemas/review-input.v1.json`, `review-result.v1.json`, and `review-trace.v1.json`, embedded and validated through C# `SchemaContracts` and `RuntimeApplication`. `provider-session-ledger.v1.json` remains the C# ledger contract. Their versions, validation keywords, and enums are unchanged by W13.

`protocol/fixtures/v1/**` remains the complete mixed direct-runtime and ledger conformance corpus and is byte-identical across W13. `protocol/fixtures/prefix-contract/**` remains immutable language-neutral prefix evidence. The consumerless `protocol/fixtures/schema-resolver-conformance/**` corpus was deleted: its only consumers were the StateV2 schema-resolution tests removed by W5, and no current C# or Node path referenced it.

The W10 retained-corpus record still contains exactly the three review schemas plus the 145 files below `protocol/fixtures/v1/**`, for 148 files total. W13 changes only the three top-level schema descriptions to state current C# ownership and mechanically refreshes the aggregate and dependent replacement-record evidence digest. Fixture bytes, schema semantics, the E1 base inventory, and prefix corpus do not change.

## Residual and historical ownership

The residual allowlist has no temporary lifecycle entry after W13. RR-001 permanently and narrowly owns the accepted internal direct-runtime selector enum; RR-002 owns the same selector only in synthetic C# protocol/ledger fixtures; RR-031 owns synthetic provider/model branding in that fixture root. Their term matchers remain narrow so unrelated matches cannot acquire multiple owners.

RR-009 permanently owns `src/artifact-provenance-vectors.ts`. The source, its executable test, the allowlist entry, and ordinary retained-conformance CI form one evidence unit. The vectors prove that Node cannot select or author state and keep obsolete artifact paths absent; they are the sole explicitly retained current negative migration-family evidence.

Historical tags, releases, roadmap records, removal handoffs, and the E1 base inventory remain immutable evidence. Historical terminology does not reactivate an executable route or override this current inventory.

## E1 exact-source identity

`runtime/scripts/verify-action-host.sh framework` runs the complete proof through `scripts/run-clean-source-proof.mjs`. Before and after its child command, the runner rejects:

- staged tracked changes;
- unstaged tracked changes;
- every non-ignored untracked path.

The verifier publishes all .NET intermediates under its proof-owned temporary root. The runner captures `HEAD^{commit}` and `HEAD^{tree}`, requires both identities to remain unchanged, requires the child command to succeed, repeats the clean-source checks, and only then emits:

```text
APR_R4_W13_SOURCE_COMMIT <40-lowercase-hex>
APR_R4_W13_SOURCE_TREE <40-lowercase-hex>
```

No marker is emitted for dirty input, failed execution, post-command drift, or a changed Git identity. These markers are CI evidence, not a public Action output, release receipt, or new protocol version.

## Post-merge closure order

The W13 pull request references #175 without an automatic closure keyword. After maintainer merge, completion proceeds in this order:

1. identify the exact merge commit on `main`;
2. require the `push` Runtime CI run for that commit to pass;
3. require both fresh framework-verifier invocations in that run to pass and emit the same commit/tree identity;
4. add a public-safe #175 comment containing `source_commit=<40hex>`, `source_tree=<40hex>`, `e1_workflow_run=<URL-or-id>`, `e1_result=passed`, and `framework_invocations=2`;
5. only then close #175 and treat its native dependency on E2 [#179](https://github.com/SolusQuest/agentic-pr-review/issues/179) as satisfied.

If `main` advances later, E2 remains bound to the recorded W13 identity unless E2 deliberately validates a newer exact tree. The known downstream Action pin is read-only audit evidence and is not changed by W13.

## E2 Native AOT receipt

`runtime/scripts/verify-action-host.sh aot` publishes the proof fixture for `linux-x64` as a self-contained Native AOT executable with reflection-based JSON disabled. The fixture runs the complete E1 scenario set from that executable. It additionally requires a domain-separated build-pair record that binds the running executable digest to the exact fixture and production Runtime managed inputs supplied to ILC. A metadata-only audit pins the required source-generated JSON roots and Host seams and rejects closed deleted/fallback assembly and type families before any scenario runs. The verifier rejects dynamic-code or reflection fallback and writes proof identity only after the E1 golden evidence and privacy canaries pass.

The AOT publish treats every warning as an error except the two exact `IL3058` diagnostics for the known `JsonSchema.Net` dependency edge. The verifier self-tests the warning audit against injected, mutated, duplicated, and missing diagnostics before accepting the real publish log. The checked warning policy and receipt contract are versioned under `runtime/tests/fixtures/action-host/aot/`.

Runtime CI invokes the exact AOT command twice in fresh proof roots and requires the two `APR_R4_E2_RECEIPT` lines to be byte-identical. Each bounded public-safe receipt binds the recorded W13 base commit/tree, exact clean-source commit/tree, Action metadata, checked wrapper bundle, launch source identity, wrapper discriminator, native executable, both managed inputs, the managed-architecture audit, build pair, warning policy, E1 normalized evidence, closed source inventory, replacement record, base inventory, canary table, and receipt contract.

Merge alone does not complete E2. After maintainer merge, the exact merge commit must pass the `push` Runtime CI run. Both AOT invocations in that run must produce the same canonical receipt. That complete receipt, merge SHA, run URL, invocation count, and passed result are then recorded in a durable public-safe comment on #179 and read back before #179 closes. R4-E2P [#216](https://github.com/SolusQuest/agentic-pr-review/issues/216) consumes that immutable receipt; later movement of `main` does not rebind the closed E2 proof.

## E2P trusted payload supplemental receipt

R4-E2P freezes the single final E3 topology in the compiled Runtime validator and adds the private proof payload `AgenticPrReview.Runtime.ActionHostTrustedProofPayload`. The payload retains production GitHub authorization, exact-object snapshot, encrypted Actions artifact state, transaction/recovery, sticky and inline publication, and system-time owners. Only the DeepSeek transport is replaced by a deterministic in-process handler. The payload has private proof kind `apr-r4-e2p-trusted-proof-payload-v1` and role `r4-e2p`; the wrapper and payload launch discriminators remain exactly `r4-w2`.

`runtime/scripts/verify-r4-trusted-proof-payload.sh` builds two native `linux-x64` executables from the same exact source. The production payload retains its default production GitHub/state/publisher composition and is executed only through exact fail-before-network framing/entrypoint smoke. The separate proof-only `AgenticPrReview.Runtime.ActionHostTrustedProofVerifier` invokes the actual payload Host stream overload, deterministic provider, proof-control service, and stale coordinator under Native AOT with verifier-owned synthetic outer dependencies. The verifier's managed graph must consume byte-identical payload and Runtime assemblies to the production build. The script audits separate warning/architecture identities, executes the complete provider-secret-free synthetic route, and emits one canonical `APR_R4_E2P_RECEIPT`. A separate parallel Runtime CI job invokes it twice in fresh roots on `ubuntu-24.04` and requires byte-identical receipt lines.

The receipt separately binds production native/managed identity and `production_payload_smoke=passed`, verifier native/managed identity and bounded evidence with `synthetic_native_aot_route=passed`, their shared managed payload/Runtime identities, and `standalone_default_github=not_executed_e4_owned`. Shared fields retain the exact source commit/tree, immutable #179 receipt anchor, Action metadata and bundle, build pair, final topology and public resolver, deterministic provider, proof control/stale coordinator, trusted policy, preparation contract, toolchain, warning policies, and receipt schema. #180 consumes and prepares only the production payload; it neither builds nor invokes the verifier. #181 owns the first separately authorized successful execution of the exact standalone production payload through its default composition against real GitHub.

The final receipt cannot be checked into the commit it identifies. Merge therefore does not complete R4-E2P: the exact merge-SHA `push` Runtime CI must produce two identical receipts, and the canonical JSON bytes plus their SHA-256 must be recorded and read back on #216. E3 [#180](https://github.com/SolusQuest/agentic-pr-review/issues/180) alone copies those exact bytes to `runtime/tests/fixtures/action-host/trusted-proof/trusted-proof-payload-receipt.json` in its control root. Its workflow checks out that exact control root and the separate #216 payload source root, then runs `prepare-r4-trusted-proof-payload.sh`; preparation never fetches an issue comment, mutable branch, release, artifact, or network receipt. The dependency chain is `#179 -> #216 -> #180 -> #181`.

## E3 trusted workflow infrastructure

E3 checks in `.github/workflows/r4-trusted-proof.yml` as the exact E2P template rendered only with the receipted source and payload identities. The no-permission authorization preflight is the sole eligible job while `R4_TRUSTED_PROOF_AUTHORIZATION` is absent or invalid. Both credential-bearing jobs retain the exact `r4-trusted-proof` environment, permission set, two-root preparation, immutable Action source, and shared workflow-level concurrency group. Pull-request, push, and this infrastructure PR do not execute the trusted proof; environment, variable, secret, fixture, dispatch, comment, artifact, cleanup, and evidence mutations remain exclusively authorized by E4 [#181](https://github.com/SolusQuest/agentic-pr-review/issues/181).

`runtime/tests/fixtures/action-host/trusted-proof/**` is the closed E3 operation contract. A normal fixture PR and a separate all-`7` stale fixture PR both use the exact authorized default-branch workflow commit as their base and inherit its full tree. The canary must be absent from the base; each initial head is a direct child adding only the regular LF-terminated `proof/apr178-path-canary.txt` blob. The evidence binds the base to the workflow SHA and records every parent SHA rather than accepting boolean ancestry claims. Both normal runs keep one stable head. In the stale case, the already-authorized protected run reaches the provider signal and stale-ready readback before E4 advances only the canary, releases the run, and observes exact Host head revalidation reject the admitted head without candidate, sticky, or acceptance persistence. The separate workflow run caused by the advanced head must be observed queued without a job while the admitted run still owns the shared group, start only after that run completes, fail the old authorization before protected allocation, and terminate inertly.

The dedicated operation admits only a completely enumerated empty relevant R4 artifact pre-state across the repository locator root, normal base scope, and stale base scope. E4 must stop before state-key resolution or provider construction when the repository-root `locator_root`, any scoped `lineage_head`, `candidate`, `publication_intent`, `acceptance`, `publication_failure`, `abandonment`, `reset`, `expiry_transition`, or `cleanup`, or any unknown, unauthenticated, or incompletely enumerated artifact exists. It records unique physical artifact IDs separately from authenticated object identities and binds every record to the exact scope digest and production-derived opaque family name. Scoped evidence records the production string epoch and session ID plus class-specific decoded fields; it does not invent a universal numeric generation. Normal bootstrap and continuation reuse one initial lineage-head object at ordinal zero because no reset or expiry occurs, while their session generations are zero and one. A continuation candidate points to the current acceptance, its publication intent points to that candidate, and its complete production acceptance receipt points to the prior logical generation and acceptance. The operation records any separately scoped stale setup artifacts, deletes the exact operation-created physical-ID set after all runs settle, and requires complete re-enumeration of the root plus all nine scoped production families in both scopes to reproduce the empty pre-state before removing the state key or publishing evidence.

The host-restricted schema also retains the exact `apr-r4-e2p-authorization-manifest-v1` bytes and readback digest for each operation, absence/set/readback/job/terminal/removal lifecycle timestamps, and exact protected-environment snapshots read before each privileged phase. Normal and stale proof-control evidence contains the production marker coordinates and recomputed body digest, predecessor-linked release comment, release-actor permission, dispatch completion, and exact cleanup receipt/comment set. The environment approver, workflow-token ready author, and release actor are independent identities; only the release actor is required to have current `write` or `admin` permission. Sink-specific credential observations bind each authorized credential to its sole sink, and retained host evidence names a maintainer-approved restricted destination by kind and identity digest. Both sticky publication transitions bind every production acceptance-receipt field, exact comment, body digest, marker, mutation, and readback; the final cleanup readback and public projection use only the surviving continuation marker. The stale proof binds both runs to the exact stale PR and `agentic-pr-review-r4-<repository_id>-pr-<pr_number>` group. The offline projector emits the separate closed public-safe representation only after all of those gates and cleanup succeed. It performs no network request or mutation, and no real evidence instance is part of E3.

## Validation

The cutover gate is:

```bash
npm run check
npm run dist:check
npm run runtime:integration
dotnet test runtime/tests/AgenticPrReview.Runtime.Tests/AgenticPrReview.Runtime.Tests.csproj --configuration Release --nologo
bash runtime/scripts/verify-action-host.sh framework
bash runtime/scripts/verify-action-host.sh aot
bash runtime/scripts/verify-r4-trusted-proof-payload.sh
bash runtime/scripts/verify-r4-trusted-proof-payload.sh
```

The Linux framework verifier is authoritative on the exact PR head and again on the exact merged `main` commit. Pull-request and push validation remain synthetic and provider-secret-free.
