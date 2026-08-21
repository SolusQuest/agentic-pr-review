# R4 migration cutover handoff

Status: W13 and E1 are closed at commit `2337e8ae9d3ca0db88f8a38f36c2f17e46a868fc`, tree `17bbdc8e1cb6591112a7c871ffba9108ecf3680f`, as recorded on issue [#175](https://github.com/SolusQuest/agentic-pr-review/issues/175). E2 issue [#179](https://github.com/SolusQuest/agentic-pr-review/issues/179) consumes that durable identity and adds the final-tree Linux x64 Native AOT gate.

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

`runtime/scripts/verify-action-host.sh aot` publishes the proof fixture for `linux-x64` as a self-contained Native AOT executable with reflection-based JSON disabled. The fixture runs the complete E1 scenario set from that executable. It additionally requires a domain-separated build-pair record that binds the running executable digest to exactly one captured managed intermediate, rejects dynamic-code or reflection fallback, and writes proof identity only after the E1 golden evidence and privacy canaries pass.

The AOT publish treats every warning as an error except the two exact `IL3058` diagnostics for the known `JsonSchema.Net` dependency edge. The verifier self-tests the warning audit against injected, mutated, duplicated, and missing diagnostics before accepting the real publish log. The checked warning policy and receipt contract are versioned under `runtime/tests/fixtures/action-host/aot/`.

Runtime CI invokes the exact AOT command twice in fresh proof roots and requires the two `APR_R4_E2_RECEIPT` lines to be byte-identical. Each bounded public-safe receipt binds the recorded W13 base commit/tree, exact clean-source commit/tree, Action metadata, checked wrapper bundle, launch source identity, wrapper discriminator, native executable, managed intermediate, build pair, warning policy, E1 normalized evidence, closed source inventory, replacement record, base inventory, canary table, and receipt contract.

Merge alone does not complete E2. After maintainer merge, the exact merge commit must pass the `push` Runtime CI run. Both AOT invocations in that run must produce the same canonical receipt. That complete receipt, merge SHA, run URL, invocation count, and passed result are then recorded in a durable public-safe comment on #179 and read back before #179 closes. E3 [#180](https://github.com/SolusQuest/agentic-pr-review/issues/180) may consume only that recorded receipt; later movement of `main` does not rebind the closed E2 proof.

## Validation

The cutover gate is:

```bash
npm run check
npm run dist:check
npm run runtime:integration
dotnet test runtime/tests/AgenticPrReview.Runtime.Tests/AgenticPrReview.Runtime.Tests.csproj --configuration Release --nologo
bash runtime/scripts/verify-action-host.sh framework
bash runtime/scripts/verify-action-host.sh aot
```

The Linux framework verifier is authoritative on the exact PR head and again on the exact merged `main` commit. Pull-request and push validation remain synthetic and provider-secret-free.
