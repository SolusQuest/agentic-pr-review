# Candidate Preparation Runbook

This implements the R7-P1 preparation boundary in the [release policy](release-policy.md) and [R7 identity contract](../90_roadmap/r7-plan.md#exact-release-identities-and-trust). A prepared candidate is unqualified and unsigned. It is not a release, a consumer binding, or authorization to publish. Exact source/version selection and an actual manual run require a separately authorized execution task.

## Identities And Authority

- `S` is the explicit 40-character lowercase payload source commit. The source checkout must be clean and match `S`; it must be an ancestor of the admitted main workflow commit `W`.
- `W` is the actual `.github/workflows/release.yml` commit from `GITHUB_WORKFLOW_SHA`. The control checkout, dispatched main ref, repository name/ID and workflow ref must agree with it. `W` may differ from `S`.
- `B` is the original D1 package archive built from `S`. Its size, SHA-256, exact three-member inventory, manifest/build-input identity and original executable are checked before it becomes visible. Preparation never repackages `B` after the build.
- The producer also records exact decimal run ID/attempt, repository ID, workflow path and the hosted `ubuntu-24.04` x64 runner selection. These are workflow/storage claims, not a cryptographic attestation.
- Final Action commit `T`, signing, credential-free qualification against `T`, draft staging and public publication are separate gates. They are absent from this candidate and proposed map; preparation creates no PR, tag, release or attestation.

The manual `prepare-build` job has only `contents: read`. It receives no publication, signing, state or provider credentials. `prepare-record` adds only `actions: read` for exact storage readback. Checkout credentials are not persisted. Ordinary push/PR jobs run synthetic fake-API fixtures and a real local package smoke without uploading candidate artifacts. They cannot activate the manual preparation jobs. No environment, release-write permission or OIDC permission is requested.

The repository's R4 policy checker admits this route only after checking the complete canonical parsed workflow against its reviewed topology identity in `scripts/release/candidate.mjs`. It permits exactly the two existing metadata read-token expressions and retains the historical proof-owner, credential, write-permission and other-artifact-route checks. Future workflow changes must update the reviewed topology identity and policy regression evidence together; a filename alone grants no artifact or credential authority.

## Preparation And Storage

After separate authorization, dispatch the main-only **Candidate preparation** workflow with the exact `source-commit` and canonical `release-version` (`vMAJOR.MINOR.PATCH-internal.N`, positive `N`, at most 48 characters). The tooling admits `W` before checking out `S`, validates the control tooling, then uses the D1 producer to build and execute the original package from `S` with Node 24, .NET SDK 10.0.109 and clang 18.1.3. See [distribution](../20_architecture/distribution.md) for the package and dependency closure.

Each run attempt writes two fresh artifacts, with `overwrite: false`:

| Artifact                     | Contents                                                                                             | Address                                 |
| ---------------------------- | ---------------------------------------------------------------------------------------------------- | --------------------------------------- |
| `r7-p1-payload-RUN-ATTEMPT`  | Original archive `B` and original producer `receipt.json`                                            | Exact artifact ID and transport SHA-256 |
| `r7-p1-metadata-RUN-ATTEMPT` | `candidate.json`, `proposed-action-map.json`, their detached SHA-256 files and bounded `summary.txt` | Exact artifact ID and transport SHA-256 |

The record job downloads by artifact ID, checks the independent original archive and raw receipt digests passed from the build job, and checks the run/attempt and artifact through the read-only GitHub API. The in-progress exception exists only inside the admitted current producer while it records its own artifact. Handoff verification requires the exact run attempt to have completed successfully.

`candidate.json` contains the unchanged D1 receipt, package build inputs, producer identity, observed payload storage identity and proposed map digest. Its own digest is detached. After metadata upload, the workflow summary emits an **external locator** containing the run/attempt, `W`, candidate digest and both artifact IDs/transport digests. The metadata artifact ID/digest stays outside its own bytes, avoiding a self-hash cycle. Retain this locator with the exact reviewed execution record; fetching a hash alongside untrusted bytes does not establish approval or provenance.

Both artifacts request seven days of retention. Actual API `expires_at` is authoritative and repository retention policy may shorten it. The candidate records the observed payload expiry; separately check metadata expiry during handoff. The usable deadline is the earlier of the two actual expiries. Publication-safe storage metadata is bounded and contains no archive paths, environment, HTTP response bodies, tokens, source content or raw logs. Candidate JSON is at most 64 KiB, proposed map at most 16 KiB, each public summary/locator at most 4 KiB, and each API body at most 1 MiB. The exact run listing is bounded to five pages of 100 artifacts; an incomplete or changing listing stops verification.

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

Run/attempt/artifact identities belong in the candidate envelope and external locator, not in the map. There is no final `T` field and no self-source/hash cycle. A separately authorized map PR must review the exact candidate/locator and proposed map digest, preserve the original bytes and keep the binding inactive until the independent consumer contract and later gates admit it. Maintainer merge establishes `T`; preparation itself creates no map PR. Consumer activation, final qualification, attestation, staging, publication and deployment remain independent leaves and authority boundaries.

## Credential-Free Validation

```bash
npm run check
npm run dist:check
npm run runtime:verify
npx vitest run tests/release
node tests/release/verify-candidate.mjs
```

The final command requires Linux x64 and the selected D1 toolchain. It builds a real original package with explicit fixture source `b8e7e501ed7371abd7d858aeb8c0dcf307be71cf` and fixture version `v0.0.0-internal.1`, using the actual tested tooling commit as fixture `W`, so `S != W` is exercised. Run/artifact/transport identities are synthetic. It validates all nine pinned package dependencies, executes the original extracted executable without an SDK environment, checks requested-version failure and the production ActionHost composition, and rejects source/version/map/digest drift. This is local fixture evidence, not a selected release version, operational prepare, online storage or signing proof. The `candidate-smoke` push/PR CI job runs it; `candidate-fixtures` runs the fake-API and consistency suite. Neither makes paid provider calls.
