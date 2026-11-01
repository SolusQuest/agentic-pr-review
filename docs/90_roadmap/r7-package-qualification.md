# Exact managed pair qualification

R7-V2 supplies the reusable qualification gate for one unchanged original managed payload B and reviewed composite T. [P3 finalization](../10_workflow/release-runbook.md) supplies the immutable unqualified pair and original storage/provenance contract. The actual first candidate is executed later by P7 under its separate exact-input authorization. This tooling change establishes no candidate, bound ordinary map, public release, installation, adoption or live-provider result.

## Inputs and authority

The dormant `qualify-original-pair` job in [Runtime CI](../../.github/workflows/runtime-ci.yml) runs only on an explicitly selected main `workflow_dispatch`. Supply all five inputs after separately authorizing the operation:

| Input                       | Meaning                                                                           |
| --------------------------- | --------------------------------------------------------------------------------- |
| `qualify-pair`              | Explicit true; default false                                                      |
| `qualifier-commit`          | Independently approved Q, exactly the main workflow SHA and checkout HEAD         |
| `qualification-pair`        | Original canonical P3 finalized-pair JSON, still unqualified                      |
| `qualification-pair-sha256` | Independent digest of those exact bytes                                           |
| `qualification-request`     | Original canonical P3 finalization request with the exact three artifact locators |

Q must differ from S, W and T and be authenticated on main. The Q-owned canonical [manifest](../../scripts/release/qualification-checks.json) is read from Q's exact Git blob and compared with the checkout and implemented case inventory. No self-referential pair/Q commit is needed. The request must match the pair; the official artifact downloader uses exact IDs and producing run, with digest mismatch fatal. The producer calls the complete P3 `verifyFinalizedPair` gate again, including source/tree/version/platform, inventory, build inputs, merged T/eight assets, runtime policy, retained original storage and provenance. It does not trust a saved verification Boolean or relabel a related-head result.

The job has only contents/actions read permission and no package/provider credentials. Its SDK compiles public supporting input/readback utilities outside the consumer. It neither builds B nor restores Solus packages. Both managed PreparationProbe builds, their failure-only diagnostics and the linked managed cutover-fault build are omitted from this original-pair mode. Historical defaults retain those separate proofs. Original receipt, archive and eight T blobs are copied unchanged; the map is never synthesized in this mode or installed into the repository.

These workflow inputs describe an operation; they do not create the human authorization for preparation, release, signing, staging, publication, paid services, provisioning or adoption. This gate performs none of those operations.

## Original execution and evidence reuse

The adapter reuses [ManagedArchive](../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/README.md), its production acquisition/Node launcher, actual original CLI/Host/Solus, read-only tools, official SDK upload and C# REST, state/publication assertions, private TLS fixtures and process supervision. The consumer runs on Ubuntu 24.04 x64 with a verified runtime only, no inherited credentials/capabilities, hidden builder/preinstalled SDKs and package cache, private mount/network/PID namespaces, loopback services and no external routes. Every unexpected service operation fails the gate; actual provider/GitHub mutation cannot leave that network namespace. Only closed counts, outcomes and identities leave the temporary consumer.

The current fixture admits exactly T's existing runtime policy 10.0.13, including its archive size and SHA-512. A different valid runtime policy receives an explicit unqualified refusal; it is never replaced or silently normalized. A reviewed fixture/policy update and affected execution are required to extend that scope.

The process-level composite driver is not GitHub Runner. Runner self-reference, input/output mapping, post actions and SDK composition reuse only the accepted [CR2 Runner/H2 scope](../20_architecture/cr2-technical-acceptance.md#original-managed-archive-and-runner-proof). The qualifier requires exact Git-blob equality for all three Action YAML assets and four generated bundles against installed Action `5207bf8058b3d1301c0a6e805e7f21334832223e`, whose composition equals the Action in Runner run 39972608852. H2 runs 40061259888 and 40061741768 supply their separately bounded hosted transport scope. The payload-map binding and original B are excluded from this reuse and must execute through this qualification's original-byte gate. A changed composition asset refuses qualification until separately reviewed actual-Runner evidence for that exact changed T is available; hashing changed YAML alone is insufficient.

The historical two-independent-build/41-case archive route remains separate. Exact-pair mode executes the same original archive across 50 concrete Host cases, including every retained case, both admitted H1 installation alternatives' bootstrap/continuation/reset, and nine bounded additions. The additions exercise all five tools through useful six-call/six-tool work, useful restored work with its complete prefix, six-tool/six-call/batch refusal, explicit lower/oversized call configuration, an output allowance too small for the first reservation, and unknown cache partition accounting. Capacity uses the managed `cr1-solus-internal4-dev` profile and context 2/1. Observed resource refusals retain the managed coarse termination; no native 64/128-call equivalence, indefinite continuation, clamping, history truncation or automatic reset is claimed. Each available C# completion is compared directly with Action output strings and escaped summary, preserving omitted unknown values and partial-accounting lower bounds.

## Mandatory checks and receipts

The manifest has 60 individually mandatory checks. Every original case has its own ID/evidence; no group-level PASS can conceal a missing restored or reset case.

| P3 group                             | Mandatory checks |
| ------------------------------------ | ---------------: |
| `original-archive`                   |                2 |
| `composite-assets`                   |                2 |
| `trusted-runtime-no-sdk`             |                1 |
| `managed-loading`                    |                8 |
| `fresh-restored-review`              |                8 |
| `read-only-tools`                    |                1 |
| `publication-state-ordering`         |               11 |
| `reset-stale-denied`                 |                9 |
| `incomplete-capacity-cancel-failure` |               15 |
| `outputs-accounting-parity`          |                1 |
| `external-confinement`               |                2 |

The producer uses P3's unchanged formatVersion 1 and inspector. A receipt binds the immutable pair digest, all twelve fixed B/S/W/T/map/inventory/build/provenance/asset/runtime identities, Q/run/attempt, manifest/results digests, concrete count, execution kind and closed outcome. Each performed case has one bounded evidence record hashing only its sanitized observation. Raw audits, private source/tool data, SESSION, reasoning, credentials and local paths are never uploaded. Limits remain 16 KiB receipt, 64 KiB results, 4 KiB per evidence file, at most 128 checks and a 1 MiB original qualification ZIP.

PASS is written only after all mandatory assertions, SDK/process-group/service closure, namespace exit, unchanged outside mounts and confirmed fixture teardown. If actual execution fails or its performed scope/close is unknown, the producer fails without a qualification receipt or artifact. It never invents an all-not-run receipt after partial work. A known pre-execution Runner/runtime denial can retain a truthful refusal: completed admission checks pass, the actual denied check is refused and never-started original cases are not-run. Expected product denials inside a proven negative test are passing qualification checks.

The exclusive evidence directory contains only `receipt.json`, `case-results.json` and `evidence/<check-id>.json`. The official uploader runs only after validated output sets `evidence_ready=true`, using `r7-v2-qualification-RUN-ATTEMPT`. Receipt digest, artifact ID and transport digest remain external; the pair is never rewritten. A passing in-job receipt is not yet operational qualification. P3 `verifyQualifiedPair` subsequently requires the exact Q-owned manifest, original downloaded ZIP bytes and authenticated completed-success main/manual producer/storage; extracted PASS files and a current-producer exception cannot substitute.

## Commands and validation limits

The workflow uses the existing installed Node entry, with explicit private temporary locations:

```bash
node control/scripts/release/qualify-pair.mjs admit --output "$RUNNER_TEMP/r7-v2-inputs"
node control/scripts/release/qualify-pair.mjs run --inputs "$RUNNER_TEMP/r7-v2-inputs" --artifacts "$RUNNER_TEMP/r7-v2-artifacts" --output "$RUNNER_TEMP/r7-v2-evidence"
```

These entries require the admitted GitHub workflow environment and exact inputs; they are not a local/offline or other-CI product workflow. The second entry also uses the workflow's checksum-pinned gh provenance verifier. The original role retains one 5,400-second monotonic deadline with its existing settlement reserve; cases cannot renew it. The job has a finite 150-minute setup/execution backstop.

Credential-free producer, official-ZIP interoperability, omission/failure/identity, original-copy, Runner reuse, capacity-script and workflow bypass regressions run with:

```bash
npm test -- tests/distribution/pair-qualification.test.ts tests/distribution/pair-qualification-workflow.test.ts --maxWorkers=2 --minWorkers=1
npm run check
npm run dist:check
```

The focused fixture command is an explicit Runtime CI gate and is also included in the ordinary source suite. It uses synthetic Git/GitHub/provenance identities and intercepted execution observations; it cannot authenticate a real candidate, even when testing the original-candidate receipt interface. The ordinary Ubuntu core lane also runs `node runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/verify-network.mjs`: this exercises actual public-probe TLS, runtime-only namespaces and cleanup without Solus packages, and cannot substitute for archive qualification. Actual namespace/Host/Solus execution of the selected B/T, hosted Runner changes and release/adoption evidence retain their own required environments and separate authority. [APR validation](../10_workflow/validation.md) still owns applicable `npm run runtime:verify` and credential-free framework/integration gates. Historical archive commands do not silently become this finalized-pair command.
