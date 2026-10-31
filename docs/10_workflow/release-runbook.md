# Candidate Preparation Runbook

This implements the R7-P1 preparation, R7-P2 provenance and R7-P3 read-only finalization boundaries in the [release policy](release-policy.md) and [R7 identity contract](../90_roadmap/r7-plan.md#exact-release-identities-and-trust). The P1 record is unsigned; the separate P2 job signs and verifies its association with the original archive. A signed candidate remains unqualified. It is not a release, a consumer binding, or authorization to publish. Exact source/version selection and an actual manual run require a separately authorized execution task.

## Identities And Authority

- `S` is the explicit 40-character lowercase payload source commit. The source checkout must be clean and match `S`; it must be an ancestor of the admitted main workflow commit `W`.
- `W` is the actual `.github/workflows/release.yml` commit from `GITHUB_WORKFLOW_SHA`. The control checkout, dispatched main ref, repository name/ID and workflow ref must agree with it. `W` may differ from `S`.
- `B` is the original D1 package archive built from `S`. Its size, SHA-256, complete managed publish-tree inventory, manifest/build-input identity and original DLL are checked before it becomes visible. Preparation never repackages `B` after the build.
- The producer also records exact decimal run ID/attempt, repository ID, workflow path and the hosted `ubuntu-24.04` x64 runner selection. These are workflow/storage claims, not a cryptographic attestation.
- Final Action commit `T`, credential-free qualification against `T`, draft staging and public publication remain separate gates. They are absent from this candidate and proposed map; preparation creates no PR, tag or release. Signing evidence is separate from the P1 candidate/map bytes.

The main-only manual `prepare-build` job has `contents: read` and `packages: read`. Only its separate fixed-source dependency restore step receives the existing repository token. The existing Solus proof script checks the exact trusted control commit `W`, removes ambient token variables before imports or commands, and supplies `NuGetPackageSourceCredentials_solus` only to the locked fixed-feed restore subprocess. It verifies package source, lock and archive hashes before exposing the fresh runner-local cache to the original producer. Later build, publish, archive and test processes receive no restore token; the producer copies verified raw archives into an isolated feed and restores into a new private cache. Missing restore authority or mismatched cache bytes fail closed. No credential configuration or package cache is uploaded.

`prepare-record` has only `contents: read` and `actions: read` for exact storage readback. Neither job receives publication, signing, state or provider credentials. Checkout credentials are not persisted. Ordinary push/PR jobs retain only `contents: read`, run synthetic fake-API fixtures and a public FDD package smoke, and upload no candidate artifacts. They cannot activate the manual preparation jobs. Only the separate `prepare-sign` job requests OIDC and attestation-write authority, as described below. No job requests an environment or release-write permission.

The repository's R4 policy checker admits this route only after checking the complete canonical parsed workflow against its reviewed topology identity in `scripts/release/candidate.mjs`. It permits exactly one package-restore token expression, two P1 metadata read-token expressions, four P2 read/verification token expressions and five P3 read-token expressions and retains the historical proof-owner, credential, write-permission and other-artifact-route checks. Future workflow changes must update the reviewed topology identity and policy regression evidence together; a filename alone grants no artifact or credential authority.

## Preparation And Storage

After separate authorization, dispatch the main-only **Candidate preparation** workflow with `operation=prepare`, the exact `source-commit` and canonical `release-version` (`vMAJOR.MINOR.PATCH-internal.N`, positive `N`, at most 48 characters). The tooling admits `W` before checking out `S`, validates the control tooling, verifies the nine exact managed dependencies through the trusted control checkout, then uses the managed producer to build and execute the original package from `S` with Node 24 and .NET SDK 10.0.109. Native AOT tooling is not required. See [distribution](../20_architecture/distribution.md) for the package and dependency closure.

P1 writes two fresh artifacts per run attempt, with `overwrite: false`:

| Artifact                     | Contents                                                                                             | Address                                 |
| ---------------------------- | ---------------------------------------------------------------------------------------------------- | --------------------------------------- |
| `r7-p1-payload-RUN-ATTEMPT`  | Original archive `B` and original producer `receipt.json`                                            | Exact artifact ID and transport SHA-256 |
| `r7-p1-metadata-RUN-ATTEMPT` | `candidate.json`, `proposed-action-map.json`, their detached SHA-256 files and bounded `summary.txt` | Exact artifact ID and transport SHA-256 |

The record job downloads by artifact ID, checks the independent original archive and raw receipt digests passed from the build job, and checks the run/attempt and artifact through the read-only GitHub API. The in-progress exception exists only inside the admitted current producer while it records or signs its own candidate. Handoff verification requires the exact run attempt to have completed successfully.

`candidate.json` contains the unchanged D1 receipt, package build inputs, producer identity, observed payload storage identity and proposed map digest. Its own digest is detached. After metadata upload, the workflow summary emits an **external locator** containing the run/attempt, `W`, candidate digest and both artifact IDs/transport digests. The metadata artifact ID/digest stays outside its own bytes, avoiding a self-hash cycle. Retain this locator with the exact reviewed execution record; fetching a hash alongside untrusted bytes does not establish approval or provenance.

The P1 artifacts and separate P2 evidence artifact request seven days of retention. Actual API `expires_at` is authoritative and repository retention policy may shorten it. The candidate records the observed payload expiry; separately check metadata expiry during handoff. The P1-only deadline is the earlier of its two actual expiries; P3 finalization additionally authenticates the separate P2 provenance artifact and uses the earliest actual expiry across all three. Publication-safe storage metadata is bounded and contains no archive paths, environment, HTTP response bodies, tokens, source content or raw logs. Candidate JSON and the proposed managed map are each at most 256 KiB, each public summary/locator at most 4 KiB, and each API body at most 1 MiB. The exact run listing is bounded to five pages of 100 artifacts; an incomplete or changing listing stops verification.

## Independent Content And Storage Verification

Use the original downloaded archive and exact metadata bytes, with the candidate digest anchored in the separately reviewed locator. Replace every uppercase placeholder below with that exact candidate's value; these are not selected release inputs.

```bash
node scripts/release/prepare-candidate.mjs inspect \
  --archive /local/original/PAYLOAD_ARCHIVE \
  --candidate /local/metadata/candidate.json \
  --map /local/metadata/proposed-action-map.json \
  --sha256 CANDIDATE_SHA256
```

`inspect` checks original archive bytes, manifest/member identity, source/version/build-input consistency, canonical candidate metadata and the exact shared map projection. It reports `contentVerified: true, storageVerified: false`; this is neither online provenance nor qualification.

With an independently provisioned read-only Actions token in `GITHUB_TOKEN` when required, additionally verify both exact artifacts:

```bash
node scripts/release/prepare-candidate.mjs storage \
  --archive /local/original/PAYLOAD_ARCHIVE \
  --candidate /local/metadata/candidate.json \
  --map /local/metadata/proposed-action-map.json \
  --sha256 CANDIDATE_SHA256 \
  --metadata-id METADATA_ARTIFACT_ID \
  --metadata-sha256 METADATA_TRANSPORT_SHA256
```

The CLI uses only fixed `api.github.com` GET routes, rejects redirects and accepts no arbitrary endpoint or in-progress handoff flag. It rechecks repository, workflow, `W`, main/manual event, exact historical run attempt, completion, artifact provenance, attempt timestamps, names, IDs, sizes, digests and expiry. It exhausts the bounded exact-run listing to reject ambiguous names or inconsistent readback. The original archive digest and GitHub artifact transport digest describe different bytes. Content verification independently rejects substitution even if an artifact download only warns about a transport digest. Neither result is signing or final `T` qualification.

Expired/missing artifacts explicitly require reprepare. Failed, incomplete, unknown, oversized or ambiguous storage stops handoff. Do not select another successful run, resolve by name alone, accept a different artifact ID, replace `B`, or recompute an expected digest to hide drift. A fresh run/attempt has a new candidate/storage identity even with the same source/version. Its bytes must be independently tested and reviewed; no reproducibility promise is made for separate builds. Preserve the previous candidate and review record. A partial local output is unusable and cannot be overwritten as a retry.

## Proposed Map Handoff

The proposed map uses the shared R7-D2 consumer schema. Its only root keys are `formatVersion`, `repository`, `tag`, `builder`, `payload`; the only builder keys are `repository`, `workflowPath`, `workflowCommit`. `payload` is the unchanged closed D1 receipt:

```text
formatVersion = 1
repository = SolusQuest/agentic-pr-review
tag = payload-<payload.identity.releaseVersion>
builder.repository = SolusQuest/agentic-pr-review
builder.workflowPath = .github/workflows/release.yml
builder.workflowCommit = W
payload = exact D1 receipt for original B and S
```

Run/attempt/artifact identities belong in the candidate envelope and external locator, not in the map. There is no final `T` field and no self-source/hash cycle. A separately authorized map PR must review the exact candidate/locator and proposed map digest, preserve the original bytes and keep the binding inactive until the independent consumer contract and later gates admit it. Maintainer merge establishes `T`; preparation itself creates no map PR. Consumer activation, final qualification, staging, publication and deployment remain independent leaves and authority boundaries. P2 evidence does not activate the proposed map.

## Restrictive Provenance Verification

`prepare-sign` depends on successful `prepare-build` and `prepare-record`, runs only for the same main/manual event on GitHub-hosted `ubuntu-24.04`, and has `contents: read`, `actions: read`, `id-token: write` and `attestations: write`. Build/restore, OIDC signing, later release writes and consumer SDK upload remain separate authority domains. The signing job checks out only trusted `W`, executes no source-`S` or archive code and performs no package restore. Its verification scripts use the standard library and the existing P1 parser. P2 implementation and ordinary PR/push CI perform no real signing; the first actual invocation remains the separately authorized P6 operation.

Before signing, `workflow-predicate` admits the current workflow/source/version, verifies original `B`, canonical candidate/map and complete managed inventory/dependency/build closure, checks the independent build/record hashes and raw receipt, and rechecks both exact storage identities. The small custom predicate binds the candidate digest, map digest, source identity and producer. The candidate digest already covers the full inventory/build inputs; this introduces no second manifest. `actions/attest@1e69f48acb82d1966a394da916b4c1698aa569d6` (v4.2.2) signs the explicit original archive name and SHA-256, using predicate type `https://github.com/SolusQuest/agentic-pr-review/attestations/candidate/v1`. It does not attest the Actions ZIP, repackaged bytes or only the entry DLL. Registry upload and artifact storage-record creation are disabled.

The job installs GitHub CLI **2.103.3** from the official Linux AMD64 archive with SHA-256 `4c806978ffce1cec70853302a56d70ac6da4f19bae7adca4e1a8ab00633dd91d`. The gate refuses a different version. This pin includes the fixes for [workflow prefix matching](https://github.com/cli/cli/security/advisories/GHSA-wjmr-j3rp-mh2g) and [source-ref case matching](https://github.com/cli/cli/security/advisories/GHSA-4mq3-hpgx-9cx8); updating it requires review of the binary digest, output schema and identity tests together. See the official [attest action](https://github.com/actions/attest/tree/v4.2.2) and [CLI verifier](https://cli.github.com/manual/gh_attestation_verify).

Retain the P1 locator plus `r7-p2-provenance-RUN-ATTEMPT`, addressed by its exact artifact ID and transport SHA-256. Its only files are `predicate.json`, original Sigstore `bundle.json` and bounded `verification.json`; the external summary also records the attestation API ID. `verification.json` binds the bundle SHA-256, candidate/map/archive digests, source and producer. It is evidence of that invocation, not an approval token or an alternative to re-running verification. Never put source files, prompts, SESSION, environment or raw logs in this artifact. The usable handoff deadline includes all retained artifacts' actual expiries; missing/expired evidence requires new reviewed preparation, never a replacement bundle or a different successful run selected by name.

Install that exact trusted CLI and put it on `PATH`. With the original files and independently reviewed locator/authorization, run the complete gate below. Use an independently provisioned read-only Actions token in `GITHUB_TOKEN` if required; no OIDC or write permission is needed for verification. All uppercase placeholders are independently selected expectations, not values extracted from an untrusted bundle:

```bash
node scripts/release/verify-provenance.mjs verify \
  --archive /local/original/PAYLOAD_ARCHIVE \
  --candidate /local/metadata/candidate.json \
  --map /local/metadata/proposed-action-map.json \
  --sha256 CANDIDATE_SHA256 \
  --source AUTHORIZED_SOURCE_S \
  --workflow REVIEWED_WORKFLOW_W \
  --version AUTHORIZED_RELEASE_VERSION \
  --metadata-id METADATA_ARTIFACT_ID \
  --metadata-sha256 METADATA_TRANSPORT_SHA256 \
  --bundle /local/provenance/bundle.json \
  --output /local/fresh-verification.json
```

The output file must not exist. Success requires **content, online storage, cryptographic signature and association** together. The CLI verifies the bundle against the actual original `B`, fixed repository, exact certificate workflow/main identity, signer digest `W`, certificate source digest `W`, main ref and hosted runner using the pinned verifier. It then checks the verified certificate's repository ID, workflow/config revision, event, exact run/attempt and public visibility, witnessed timestamps, exactly one verified statement, exact subject and custom predicate. Missing, failed, ambiguous or mismatched evidence cannot yield `provenanceVerified: true`. There is no supplied-JSON or unsigned fallback. The bounded report and errors exclude raw verifier stderr. The `workflow-predicate` and `workflow-verify` commands are internal preparation paths: both require full main/manual/current-producer admission. Their narrowly admitted in-progress report has `completedRunVerified: false`; ordinary `verify` has no in-progress bypass and requires completed-success storage.

The certificate proves that the allowed GitHub-hosted workflow at `W` signed a claim about these bytes. Its certificate source digest describes **W**, not detached **S**. The signed candidate digest plus independently inspected candidate/map/build-input policy binds the actual asserted `S`, original `B`, complete inventory/dependencies and producer to one candidate. It does not independently observe compilation, establish reproducibility, quality, final `T` qualification, redistribution permission or publication approval. The map remains a separately reviewed binding at later `T`. Mandatory online attestation verification by downstream consumers is not part of this release policy; consumer SHA-256 verification remains mandatory.

P3 finalization, P4 staging before its first write and P5 promotion before publication must call this full gate again with their exact approved `B/S/W` and candidate/metadata locators. A missing or failed attestation stops those operations; neither a saved boolean, P1 `contentVerified` nor signature existence suffices. P2 does not implement those later entrypoints or claim their integration is complete.

## Read-Only Finalization

P3 freezes one exact original `B/S/W` candidate against the independently reviewed, merged Action commit `T`; it does not bind a map, execute the archive, issue qualification, sign, stage or publish. The original map remains `null` in this implementation. A future separately authorized binding PR must copy the exact canonical proposed map bytes to `.github/actions/agentic-pr-review/payload-map.json`. That file and the future Q-owned check manifest below are precise byte-stable formatter exceptions; no whitespace conversion or recomputed expected hash may disguise a changed map.

Use trusted merged control tooling, Node 24, locked dependencies (`npm ci`) and the exact gh verifier specified above. Read-only Actions/contents authority is sufficient. Do not execute or import code from `S`, `T` or downloaded artifacts. The finalizer reads Git blobs and parses the existing literal runtime policy with TypeScript's syntax parser; it does not evaluate it. Fetch the fixed public repository's current main history before selecting the exact approved inputs. Current main is only an ancestry authority, never a substitute for an explicitly selected `T` or candidate.

The canonical finalization request has exactly these fields; all IDs are decimal strings and all digests are lowercase hex:

```text
candidateSha256
workflowCommit = approved original W
actionCommit = exact reviewed T
bindingPr = exact merged map PR number
payloadId / payloadSha256 = original P1 artifact / transport digest
metadataId / metadataSha256 = original P1 metadata artifact / transport digest
provenanceId / provenanceSha256 = original P2 evidence artifact / transport digest
```

Create the request with the trusted `canonicalJson` helper in `scripts/release/build-payload.format.mjs`, without a trailing newline, and retain it with the reviewed locator. The request is at most 4 KiB and accepts no endpoint, mutable ref, local path, latest selection or replacement permission. The P2 attestation API ID remains an informational external locator; the pair binds the authenticated provenance artifact, reverified bundle SHA-256 and signer/producer identity.

Replace all uppercase placeholders with independently approved inputs. The output directory must be fresh and outside the source repository:

```bash
node scripts/release/finalize-candidate.mjs finalize \
  --repo /local/trusted-control \
  --source AUTHORIZED_SOURCE_S \
  --version AUTHORIZED_RELEASE_VERSION \
  --request /local/finalize-request.json \
  --archive /local/original/PAYLOAD_ARCHIVE \
  --candidate /local/metadata/candidate.json \
  --map /local/metadata/proposed-action-map.json \
  --bundle /local/provenance/bundle.json \
  --output /local/fresh-finalized
```

Admission requires the exact binding PR's authenticated `merged` state, main base and `merge_commit_sha=T`, with the fixed repository name/ID; a PR head is insufficient. Require `S` ancestor of `W`, `W` ancestor of `T`, and `T` ancestor of authenticated main. `S` may equal `W`, but neither equals later `T`. Missing local history stops verification; explicitly refresh trusted history rather than substituting a new source or target. The original source tree, raw policy/lock hashes, exact nine managed dependency pins, original archive/manifest/full inventory and canonical candidate/map must agree. At `T`, the entire nested Action tree must contain exactly eight regular files: the outer map/metadata, two internal metadata files and four `internal/dist/{index,sdk-upload,prepare,cleanup}.js` bundles. Git blob identities, SHA-256, modes and sizes bind every asset. The declared compatible .NET runtime policy must match its compiled copies in the ordinary bundles. This is static policy binding, not actual managed loading or clean-consumer qualification.

The completed-run P2 cryptographic gate is executed again; saved `verification.json` booleans are not accepted. Exact payload, metadata and provenance storage must remain unexpired, unique and tied to the original successful main/manual run/attempt. Any missing, expired, ambiguous or changed evidence stops handoff and requires new reviewed preparation; never choose another run, replace bytes, rebuild or repackage.

Success writes only fresh local `finalized-pair.json`, detached `finalized-pair.sha256` and `summary.txt`. The pair is at most 16 KiB and always has `qualification.state=unqualified`. It binds original payload/source/build-input/inventory/candidate/map digests, original producer, all three storage identities, reverified provenance, exact `T`/binding PR/eight-asset inventory and runtime policy. It is itself V2's input descriptor. Its SHA is detached; it includes no receipt hash. Neither `T` nor archive self-hash is inserted into the original payload build inputs. Retrying cannot overwrite existing outputs; partial outputs after a filesystem failure are unusable.

The main/manual `operation=finalize` workflow route uses only `contents: read` and `actions: read`. It first admits bounded inputs in the trusted control checkout. Before emitting download inputs, it resolves the original run/attempt from the selected payload artifact API identity and validates all three fixed-role locators/digests against that same approved `W`, completed-success main/manual producer, timestamps, uniqueness and expiry. Only the authenticated decimal `producer_run_id` is emitted; all three `download-artifact@v8` steps explicitly use that original `run-id`, rather than the new finalization run. Run and artifact IDs must be safely representable by the download adapter. The request gains no caller-selected run or fallback. It then downloads the three exact artifact IDs and runs the same CLI, which repeats full content/provenance/storage admission. Preparation/recording/signing require `operation=prepare`, so finalization cannot activate them. The finalize route has no upload, OIDC, package-restore or release-write operation. It exposes the bounded descriptor/hash through job outputs and step summary; retain the exact descriptor locally with its independent digest. This is not a new remote artifact/archive retention service.

`inspect` accepts the same original inputs plus `--pair FINALIZED_PAIR_FILE --sha256 APPROVED_PAIR_SHA256` and performs local content/Git-object consistency checks only. It reports `contentVerified=true`, `storageVerified=false`, `provenanceVerified=false`, `qualified=false`. `verify-pair` uses those arguments and repeats all authenticated finalization gates; it still reports `qualified=false`. A descriptor or successful local inspection is never a seal or permission to stage.

## Frozen Qualification Receipt Contract For V2 And P4

The validators and constants are owned by `scripts/release/finalized-pair.format.mjs`; the composed read-only gates are in `finalize-candidate.mjs`. V2 owns actual qualification case execution, its producer and the future main/manual route in `.github/workflows/runtime-ci.yml`. No such operational producer, production check manifest or passing receipt is created by P3. The exact approved qualifier/workflow revision `Q` is independent of original `W`, `T` and the unqualified pair; never derive trusted Q or digest expectations from a received document.

The fixed scope is `r7-v2-managed-pair`, profile `cr1-solus-internal4-dev`, context format/compatibility `2/1`. Required groups cover original archive, complete composite assets, trusted runtime/no SDK, managed loading, fresh/restored review, read-only tools, publication/state ordering, reset/stale/denied identity, incomplete/capacity/cancellation/failure, output/accounting parity and external-service confinement. These are qualification records, not an expansion of the [runtime capacity profile](../../runtime/src/AgenticPrReview.Runtime/Agent/Loop/capacity.md#v3-managed-review-admission).

V2 must commit/review canonical `scripts/release/qualification-checks.json` alongside its concrete implementation at approved Q. Its closed format has `formatVersion=1`, `kind=r7-qualification-checks`, the exact scope, and a sorted unique mandatory `checks` list of `{id, group}`. Every required group is represented. IDs match `[a-z][a-z0-9-]{0,63}` and select only `evidence/<id>.json`; traversal, case aliases, duplicate/unknown identities and silent normalization are refused. The manifest contains no own Q, pair hash or self-hash. The consumer reads its exact Git blob at independently approved Q, not a self-selected manifest supplied by the artifact.

The single qualification artifact `r7-v2-qualification-RUN-ATTEMPT` contains exactly:

| File                 | Closed role and bounds                                                                                                                                                                                                                                                  |
| -------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `receipt.json`       | At most 16 KiB; `kind=r7-qualification-receipt`, detached pair digest, exact B/S/W/T/candidate/map/inventory/build-input/provenance/asset/runtime bindings, scope, execution kind, Q producer/run/attempt, expected-check/result digests, count and pass/refusal state. |
| `case-results.json`  | At most 64 KiB; `kind=r7-qualification-case-results`, pair/scope/expected-check digest and the exact sorted mandatory set of `{id, group, status, evidenceSha256}`.                                                                                                     |
| `evidence/<id>.json` | At most 4 KiB per performed case; `kind=r7-qualification-case-evidence`, check ID/group, pair digest, pass/refused outcome and `observationSha256`. No raw logs, paths, prompts, source, SESSION or credentials.                                                        |

All envelopes have `formatVersion=1` and canonical UTF-8 JSON bytes without a trailing newline. The expected-check manifest is at most 32 KiB; each manifest/results set has at most 128 cases. The whole artifact is at most 1 MiB including transport overhead. Receipt state is `pass` or `refusal`; concrete status is `pass`, `refused` or `not-run`. A not-run case has no evidence file and a null evidence digest. A performed case must have the actual matching retained evidence file, ID/group/pair/outcome and digest. PASS requires exact expected-set coverage and every mandatory concrete case passed. A group Boolean or opaque overall digest cannot hide a missing restored-review case. Refusal requires explicit non-pass cases and a closed reason; unknown or incomplete work never becomes PASS.

The core receipt binds `expectedChecksSha256`, `caseResultsSha256` and exact case count. Result digests bind the retained evidence files. V2 computes observation identities from actual cases; those hashes are authenticated claims of performed checks, not independent behavioral replay. The external qualification artifact ID/transport digest and detached receipt digest stay outside the receipt, avoiding a self-reference cycle. The pair remains unchanged regardless of qualification outcome.

`inspectQualificationContent` validates format, exact identities, concrete coverage, outcomes and evidence but always returns `operationallyQualified=false`. The operational consumer uses `verifyQualifiedPair`/`verify-qualification`: it repeats full pair/provenance/storage admission, reads the expected check set at external approved Q, hashes the original downloaded qualification ZIP against the independent transport digest, parses all receipt/results/evidence directly from those bytes without extraction, rejects synthetic execution, and authenticates the exact qualification artifact against Q's fixed repository/workflow, main/manual event, completed-success run/attempt, unique name, digest, timestamps and expiry. The qualification role has no preparation-style in-progress exception. A `synthetic=false` label or self-written PASS document alone cannot authorize staging.

For independent verification, use the same original inputs as `verify-pair`, then add:

```text
--qualifier APPROVED_QUALIFIER_Q
--receipt-sha256 APPROVED_RECEIPT_SHA256
--qualification-id QUALIFICATION_ARTIFACT_ID
--qualification-sha256 QUALIFICATION_TRANSPORT_SHA256
--qualification-archive /local/original-qualification-artifact.zip
```

The command is `verify-qualification`; its archive is the original downloaded Actions ZIP, before extraction, with the exact file inventory above. Its actual byte length and SHA-256 must match the authenticated artifact metadata. A separately supplied directory or self-consistent local PASS cannot substitute for those bytes. The bounded ZIP32 reader admits stored/deflate files and matching data descriptors from the maintained Actions uploader, with an optional empty `evidence/` directory; it rejects encryption, ZIP64, extra fields/comments, duplicate/unsafe members, symlinks/executable files, inconsistent offsets/headers/CRC and decompression beyond per-member limits. It writes no extracted files. Receipt digest, Q and transport locator are independently retained/reviewed expectations. P4/P5 must call the complete gate before their release writes; parsing a saved PASS or successful content-only check is insufficient. Their synthetic fixtures prove validator behavior only. Actual P7/V2 receipt production and P8/P9 consumption remain separate authorized operations. Any shared contract defect requires a coordinated correction rather than different P4/V2 interpretations.

## Qualified Unpublished Draft Staging And Recovery

P4 implements `scripts/release/stage-candidate.mjs`. It does not execute an
operational prepare, sign, qualify, stage or publish during development. An actual
run requires a separately authorized original candidate and actual V2 evidence.
The original finalized pair remains **unqualified**; it is never rewritten into a
PASS. Before its first persistent side effect, staging calls the complete P3
`verifyQualifiedPair` gate, including original bytes, cryptographic provenance,
all original storage, external Q, concrete cases and the original qualification
ZIP. Saved reports and supplied flags cannot replace this gate.

Maintainers must provision the fixed `r7-staging` environment, intended main-only
deployment policy/review protection and a finite environment-scoped
`R7_STAGING_TOKEN` before enabling staging. The token needs draft visibility and
release/asset write authority for this repository, plus Actions read for intent
and environment readback; ordinary `GITHUB_TOKEN` remains contents/actions read.
No administration or settings-write authority is used. Independently retain a
reviewed protection preflight report (environment ID, main restriction, reviewer
policy and publication protection disposition), and bind its SHA-256 in the
approved request. A draft is mutable: GitHub's immutable-release protection
applies after publication. The report records a separate operational check, not
a claim that choosing an environment makes a draft immutable. P5 must freshly
verify applicable protection before publication. See [environment readback](https://docs.github.com/en/rest/deployments/environments#get-an-environment)
and [release immutability](https://docs.github.com/en/repositories/releasing-projects-on-github/about-immutable-releases).

Set repository variables `R7_STAGE_REQUEST_SHA256` to the independently approved
canonical request digest and `R7_STAGING_ENABLED=true` only after that provisioning.
No tooling here creates an environment, token, variable, reviewer policy or
protection. A read-only job without an environment first checks that the selected
environment ID already exists, so a missing environment is refused before the
protected job can select it. The protected job checks the same ID again. Its
fixed concurrency group serializes stage/recovery jobs only, without locking CI
or V2. All entries require this repository, hosted Ubuntu 24.04 x64, manual main
workflow and exact control checkout R. The staging token is captured in a private
HTTP closure and erased before any P3 `gh` or artifact SDK child starts.

`stage-request` is canonical JSON, at most 32 KiB, with exactly these fields:

| Field                                                                        | Independently selected meaning                                                               |
| ---------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------- |
| `formatVersion`, `kind`                                                      | `1`, `r7-stage-request`                                                                      |
| `pair`, `pairSha256`                                                         | Exact original P3 JSON object and detached digest                                            |
| `finalization`                                                               | Original ten-field P3 finalization request, unchanged                                        |
| `qualifierCommit`, `qualificationId`, `qualificationSha256`, `receiptSha256` | External Q and original qualification artifact/receipt identities                            |
| `controlCommit`                                                              | Exact current main release-workflow revision R; does not change S/W/T/Q                      |
| `environmentId`, `protectionEvidenceSha256`                                  | Existing fixed environment ID (decimal string) and retained external preflight report digest |
| `approvalId`, `validUntil`                                                   | Bounded approval identifier and finite UTC deadline                                          |

Use `canonicalJson` from `build-payload.format.mjs` to serialize a locally
assembled request and retain its detached digest for maintainer review. No
endpoint, filesystem path, asset override, run selector, overwrite, retry or
qualified flag is accepted. Source/version workflow inputs must match the pair.
Dispatch `release.yml` on main with operation `stage`, those independently approved
source/version inputs and the exact canonical `stage-request` string. This is an
instruction for a separately authorized operational run, not authorization to
dispatch now. The tool downloads only exact original locators, never latest or
current-run substitutes. It installs no runtime and executes no code from S/T.

The closed release projection is exactly fourteen nonempty assets:

- Original managed archive B and deterministic `<archive>.sha256` text.
- Unchanged `candidate.json`, `finalized-pair.json`, `provenance-bundle.json` and
  the original transport `qualification.zip` (not a reconstructed ZIP).
- The eight original Action files at T. Each name is `action-` followed by the
  existing closed path with `/` replaced by `__`; the proposed map is therefore
  `action-payload-map.json`, byte-for-byte the original canonical map.

The canonical draft body binds S/W/T/Q, original proof digests, approval and the
complete sorted name/size/SHA-256 table. The only release creation is
`draft=true`, `prerelease=true`, `payload-<releaseVersion>`, exact source S and
`make_latest=false`. No consumer tag, publication, rebuild, repack, PATCH, delete,
replacement or ref write exists. The staging-authorized identity enumerates all
releases, including drafts; a read-only identity must not infer absence from a
published-only view. An existing payload tag must peel to S and the consumer
`v…` tag must remain absent. Release and asset IDs, uploaded state, positive exact
sizes, unique names/IDs, complete lists, metadata digests and actual downloaded
bytes must all match. Extra, duplicate, starter, zero-size or foreign state stops
the operation. See [release enumeration](https://docs.github.com/en/rest/releases/releases#list-releases)
and [asset upload behavior](https://docs.github.com/en/rest/releases/assets#upload-a-release-asset).

Before the first release POST, one immutable Actions artifact named
`r7-p4-intent-<releaseVersion>.json` reserves fifteen distinct operations (create
plus fourteen uploads), each bound to its exact input digest. The locked SDK uses
single-file `skipArchive:true`; the persisted transport is the actual canonical
JSON bytes, not an inferred ZIP digest. API metadata, trusted main/manual control
producer, retention covering usable authority and independently downloaded raw
bytes are verified before mutation. Only its creating invocation may execute
these reserved operations, each with pre-read, **one POST**, and exact readback.
Raw SDK logs are privately captured; no token, header, raw error or local path is
written to release assets, intent or public summaries.

A lost/malformed response is reconciled by reads only. If exact committed state
is visible, that still-running owner may continue to its next never-dispatched
operation. Absence is not proof that a POST was not delivered. On runner/process
loss the entire immutable batch is consumed: `recover-stage` (or a repeated
`stage`) can only adopt a completely exact draft and all fourteen assets. A
failed/cancelled originating run's authenticated intent remains a valid fence;
it does not authorize another write. Incomplete, absent, conflicting, missing or
expired intent/state stops with `candidate_staging_denied`, without automatic
repair or cleanup. This deliberately sacrifices partial-resume availability.
Do not delete an intent or draft to manufacture a retry, change approval nonce,
or replay POST. A later operational decision requires separate authorization.
The exact approved R and request must still be usable; a newer control revision
does not silently adopt an old request. Original evidence and approval must stay
unexpired even for successful read-only adoption.

Success writes fresh local `staged-pair.json` and `staged-pair.sha256`; bounded job
outputs/summary retain the canonical public record, its digest and release ID.
The external format-1 record binds
the request/descriptor, intent artifact/digest/producer, release ID and complete
asset IDs/sizes/digests without a self-hash cycle. Retain it and its detached hash
independently for P5. `verifyStagedCandidate` requires the exact original inputs,
approved request and independently selected record/hash; it forces recovery-only
behavior, reruns full P3 authentication and downloads remote bytes again. A
staged record is evidence, never qualification or publication authority. No
release or asset ID is replaced during recovery.

The current verifier retains the staging route's main/manual control-R admission
and staging-authorized draft visibility. It is not a generic verifier for a
different P5 workflow or a read-only token that cannot see drafts. P5 owns its
separate publication admission and any corresponding verifier integration; this
helper does not waive those boundaries.

## Credential-Free Validation

```bash
npm run check
npm run dist:check
npm run runtime:integration
npx vitest run tests/release
npx vitest run tests/release/provenance.test.ts
npx vitest run tests/release/finalization.test.ts tests/release/qualification-contract.test.ts tests/release/finalization-workflow.test.ts
node tests/release/verify-candidate.mjs
```

The final command requires Linux x64, Node 24, .NET SDK 10.0.109 and gcc for the public native-asset loader fixture. It packages and executes that public FDD fixture, with explicit fixture source `e33e1a9b14507661ab5a0f0ade992964f457b213` and fixture version `v0.0.0-internal.1`, using the actual tested tooling commit as fixture `W`, so `S != W` is exercised. Build and run/artifact/transport metadata are synthetic. It checks the managed candidate contract, eleven rejection cases and original extracted DLL/native-asset loading. It does not validate the production Solus dependency closure or qualify a real release. The original native fixture source `b8e7e501ed7371abd7d858aeb8c0dcf307be71cf` remains historical evidence. The `candidate-smoke` push/PR CI job runs the public FDD smoke; `candidate-fixtures` runs the fake-API and consistency suite. Neither makes paid provider calls.

`npm run runtime:verify` runs the complete current runtime route, including actual managed archive qualification. It requires the reviewed-source Ubuntu builder and an already verified private package cache; it does not acquire credentials. The separately authorized trusted Solus package-proof workflow exercises cold locked restore, credential-free actual managed producer/archive execution and current candidate validation. Neither that proof nor the public FDD fixture performs an operational candidate prepare, signing, binding, staging or publication.

The synthetic P4 command is `npx vitest run tests/release/staging*.test.ts`.
It is included in the existing `tests/release` CI glob. Tests compose the real
P3 gate with synthetic Git/API/gh evidence, verify all fourteen original assets,
fault every mutation with committed/uncommitted lost replies, authenticate failed
intent producers, enforce credential boundaries and reject conflicting recovery.
This validates implementation behavior, not a real candidate or operational
environment/protection configuration.

The provenance suite runs the real gate and executable verifier adapter with synthetic tool responses and fake GitHub API storage. It covers signer/ref/revision/runner/source/subject/closure substitution, absence, ambiguity, tool failure/version mismatch and the same-run boundary; it does not establish a real Sigstore signature. `candidate-fixtures` includes it through `tests/release`. No credentials or provider calls are required.

P3's finalization/receipt fixtures exercise actual Git-object inspection, the real P2 verifier adapter with synthetic responses and fake read-only API storage, including omitted concrete cases and evidence. They are included automatically in `candidate-fixtures` and `npm run check`; they do not produce an operational candidate, signature or qualification. The original-byte case implementation and clean-consumer proof remain V2-owned.
