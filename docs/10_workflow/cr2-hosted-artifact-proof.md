# Finite hosted artifact interoperability proof

H1 prepares a manual, dormant transport proof for [#453](https://github.com/SolusQuest/agentic-pr-review/issues/453). It does not authorize or execute [H2 #454](https://github.com/SolusQuest/agentic-pr-review/issues/454). Both H1 and V1 must be merged, and the maintainer must explicitly approve the concrete source and finite operations below, before either hosted dispatch. Acknowledging a workflow input does not create that approval.

The proof traverses the actual nested composite, its checked internal Node entries and official `@actions/artifact` SDK child, and the production C# `GitHubArtifactRestrictedStateStore`/`GitHubArtifactRestClient`. A repository-only companion uses the existing internal `RunActionHostAsync(..., linkedRunner)` seam. A trusted preload redirects the CLR entry to that companion and supplies a private synthetic map and closed source-metadata/acquisition fixture. Those interventions are explicit test wiring. They do not qualify an original Solus managed archive, ordinary release-map resolution, production PR authorization, accepted SESSION/lineage restoration, a completed review, or a provider. V1 owns original-archive qualification.

The ordinary checked `payload-map.json` stays `null`. No public Action input selects the fixture, companion, source-metadata override or test credentials. The public contract remains seven inputs and fourteen outputs. A companion completion reports that no product review ran; the separate transport receipt determines proof success.

## Source and trust binding

| Item                | Required value                                                                                                                                         |
| ------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Repository          | `SolusQuest/agentic-pr-review`, numeric ID `1263978368`                                                                                                |
| Hosted workflow     | `.github/workflows/cr2-hosted-artifact-proof.yml`, `workflow_dispatch` only                                                                            |
| Workflow ref        | `SolusQuest/agentic-pr-review/.github/workflows/cr2-hosted-artifact-proof.yml@refs/heads/main`                                                         |
| Workflow/source     | The full reviewed, merged H1+V1 source SHA, copied into the approval record; `expected-head-sha == github.sha == github.workflow_sha == checkout HEAD` |
| Actor for both runs | `Yuee98`, immutable actor ID `16307884`; no rerun actor substitution                                                                                   |
| Installed Action    | `f45b6c09a166eb0f4c1683d93d1766c607c3f4b9`                                                                                                             |
| Platform            | GitHub-hosted `ubuntu-24.04` x64, Node 24, .NET SDK `10.0.109`; pinned .NET runtime `10.0.13`                                                          |
| Runs                | Exactly two separately initiated run IDs, A then B; attempt `1` only; B must be different from A                                                       |
| Provider/PR         | No provider endpoint, provider credential or fixture PR is needed; this is a linked storage companion                                                  |

The final H1 handoff supplies its reviewed head and source inventory. After both PRs merge, the coordinator must bind the actual merged SHA in the approval record before asking the user to authorize H2. A moving `main`, an issue reference, a historical receipt or an unfilled source field is insufficient. If the merged transport/Action assets differ from the reviewed source inventory, reconcile the difference before requesting approval.

All eight installed assets are checked against the fixed Action commit and the checked source. Only the ephemeral installed map changes. Source admission still runs the production resolver: the loopback fixture returns exact path/type/size/Git-blob identities for eight closed Contents paths at the fixed ref, with the private map's independently bound digest. The original source map remains null. Other paths, refs, executable modifications and map substitutions fail closed. Historical A1 source pins and golden records remain unchanged.

## Synthetic fixture and restoration claim

The only opaque object is `cr2-h1-<Run-A-ID>`. Its physical name is the production `apr-object-<family-hash>-<encrypted-digest>` name. Run A's resulting numeric artifact ID is recorded only after C# verifies the uploaded ZIP, S2 carrier, producing run/attempt, encrypted digest, size and expiry. Run B treats that receipt as a candidate reference, independently verifies actual producer provenance and remote metadata, and admits cleanup only after exact download and authentication.

The deterministic synthetic AEAD fixture is exactly 4,096 bytes with SHA-256 `15d392b30989f7b08f94615c1c57202a47a95408e35abdf286662a4ab8c3ce86`. It contains a 12-byte nonce, 4,068-byte ciphertext and 16-byte AES-256-GCM tag. The public test key is SHA-256 of UTF-8 `apr.cr2.h1.public-test-key.v1`; the nonce is the first 12 bytes of SHA-256 of UTF-8 `apr.cr2.h1.public-test-nonce.v1`; AAD is UTF-8 `apr.cr2.h1.synthetic-only.v1`. Plaintext is 4,068 bytes of `0x5a`, with UTF-8 `CR2-H1-SYNTHETIC-OPAQUE-V1` at offset zero. This deliberately public key/nonce encrypts only this fixed synthetic data and grants no confidentiality claim for real state.

Run B verifies opaque transport and authenticates/decrypts this synthetic AEAD fixture. It does not restore an accepted Solus SESSION or prove application lineage. No raw restricted production state is used.

## Finite operation proposal

These are maximum allowances, not instructions to consume the budget. The exact source/actor/fixture above and the sole resulting Run A object define the target. There is no prefix deletion or unrelated-object cleanup.

| Operation/resource                  | Bound across the pair                                                                                                                                          |
| ----------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Manual dispatches                   | Exactly two, produce then restore; no automatic retry or rerun                                                                                                 |
| APR SDK upload calls                | One in Run A; zero in Run B; no APR replay                                                                                                                     |
| Exact lists                         | Three: Run A preflight, Run A post-upload, Run B census                                                                                                        |
| Preflight capacity                  | At most 99 existing repository artifacts and zero matching fixture-family objects; 100 refuses before upload                                                   |
| Each census                         | One page, at most 100 records, complete and consistent; no second page                                                                                         |
| Upload verification/readback        | Mandatory production upload readback plus one explicit Run A readback                                                                                          |
| Run B restoration                   | Producer provenance, exact list, metadata/download and synthetic AEAD authentication in a fresh C# store/process                                               |
| C# authenticated REST requests      | At most 16 per run, 32 total, including provenance, lists, readback, pre-delete metadata and absence verification                                              |
| Anonymous ZIP downloads             | At most two per run, four total; API Authorization is never forwarded                                                                                          |
| Exact artifact DELETE               | At most one, in B, for the independently restored A object; one dispatch followed by fresh absence verification                                                |
| Encrypted object / S2 carrier / ZIP | 4,096 / at most 8,192 / at most 16,384 bytes                                                                                                                   |
| Archive downloads                   | At most 32 KiB per run, 64 KiB total                                                                                                                           |
| Authenticated metadata bodies       | At most 512 KiB per run, 1 MiB total                                                                                                                           |
| SDK upload request bodies           | At most 16 MiB including the conservative SDK retry and transport-redirect bound below                                                                         |
| Retention                           | Request one day; verify the observed expiry covers the run pair and is no more than 25 hours away; no seven-day accepted-state claim                           |
| Time                                | Ten minutes per hosted job; B starts within 30 minutes of A's creation; existing 120-second logical operation and 30-second SDK/REST attempt boundaries remain |

Pinned `@actions/artifact` 6.2.1 uses at most five Results Create attempts and five Finalize attempts. Its Actions HTTP client has no additional POST retries and permits at most 50 redirects per attempt: at most 510 Results HTTP requests. The pinned Azure Blob 12.33.0 pipeline removes the general Retry phase and installs Storage Common 12.5.0's `maxTries: 4`. This small ZIP uses one block plus a block-list commit, at most eight Blob PUT attempts, each with at most 20 transport redirects: at most 168 Blob HTTP requests. Thus the conservative SDK transport ceiling is 678 HTTP requests, usually curtailed much earlier by the 30-second SDK owner deadline. This is one APR upload call, never 678 independently authorized uploads.

For the fixed fixture, each ZIP or single-block XML request body fits 16 KiB. The production SDK environment bounds the JWT to 16 KiB, so its base64-decoded JSON payload is at most 12 KiB. The pinned decoder can fall back to Latin-1 for invalid UTF-8; re-encoding those backend identifiers into JSON can take twice the payload bytes. Allow 24 KiB for the identifiers and 1 KiB for fixed Create/Finalize fields: at most 25 KiB per Results request. The separate bounds give `510 × 25 KiB + 168 × 16 KiB = 15,808,512 bytes`, below the 16 MiB allowance. The actual pinned SDK serializer is tested with escaped, Unicode and decoder-fallback boundary fixtures, without making a request. This deliberately includes redirects and retries rather than assuming ordinary UUID-sized identifiers or one network call per upload. Normal rehearsal observes one Create, two Blob PUTs and one Finalize. These are source-backed application request/body ceilings, not measured TLS/header/wire bytes or a cap on SDK control-response JSON. C# metadata/archive response bodies have the separately enforced limits above. Dependency/bundle drift invalidates the proposal until these assumptions are reverified.

Read-only setup downloads are separate: public npm/NuGet dependencies, the public-development publish tree and the pinned 36,648,012-byte runtime archive. They use no private feed credentials. The runtime archive's SHA-512 is checked before use. The workflow does not request `packages` or `id-token` permission and does not acquire or refresh credentials.

The existing Actions permission class is sufficient: `contents: read` for checkout and `actions: write` for the artifact REST read/delete operations. The isolated Node SDK receives the Runner's existing runtime token; C# receives the repository `GITHUB_TOKEN` through private launch framing. These are different credential classes. Read preflight does not prove deletion will be permitted. A later permission denial stops the proof; it does not justify obtaining a PAT, changing permissions/settings/secrets/environments, or retrying deletion.

## Stop and cleanup boundary

Stop for source/actor/ref/attempt drift, missing approval, wrong artifact identity/producer/expiry/digest, incomplete or conflicting census, permission denial, rate limit, exhausted request/byte/time budget, cancellation, failed authentication, leaked canary, or unconfirmed local child closure. If another writer changes the census after preflight, refuse a second page and stop; do not replay the upload to repair the proof.

Before dispatch, cancellation means no mutation. After possible dispatch, a missing SDK acknowledgement, cancellation or connection failure can mean an object exists. After DELETE dispatch, a lost response can mean deletion happened. Neither a killed child nor a missing response proves absence. The harness does not automatically repeat either mutation, broaden cleanup to a family, or claim success from a later speculative observation.

Normal B cleanup has this inventory: the one confirmed A artifact ID, its exact physical/logical name, archive and encrypted digests, size, producer run/attempt and expiry, under the approved repository/source/actor. B revalidates those facts from the service before issuing the single DELETE. A supplied numeric ID alone never grants deletion authority. Failed/uncertain receipts cannot enter the normal restore route. A sanitized stop record retains a safely known artifact ID where available; it grants no new cleanup authority. Leave uncertain service state to its requested retention expiry, or ask the user for a separately bounded reconciliation/deletion operation. Local owned resources remain subject to the production SDK lifetime owner and composite post cleanup.

No cleanup step uses `always()` to delete service artifacts. The final workflow cleanup only stops its nonce-bound loopback acquisition service. It does not delete artifacts. No token, key, signed URL, raw opaque bytes or raw exception text is included in receipts, annotations or summaries.

## Rehearsal and review

`cr2-hosted-artifact-rehearsal.yml` is a separate ordinary PR/push workflow with only `contents: read`, no secret inputs and a fixed credential-free entry. It builds public dependencies, substitutes synthetic capabilities before SDK admission, and exercises fresh process contexts over one controlled loopback service. `strace -s 0` checks destinations without recording payloads. The rehearsal cannot establish real GitHub persistence or two independent GitHub run identities.

```bash
npm ci
npm test -- scripts/cr2-hosted-artifact-policy.test.ts scripts/cr2-hosted-artifact-controls.test.ts
# Ubuntu 24.04 x64, pinned SDK and Node 24, strace installed:
node scripts/cr2-hosted-artifact-rehearse.mjs
npm run check
npm run dist:check
git diff --check
```

The source tests use the production resolver, covering positive eight-file admission, wrong ref, changed executable and changed private map. Rehearsal covers the actual checked SDK/C# store path, fresh-process restoration, scope refusals, 99/100 capacity, post-preflight drift, cancellation, unknown upload/delete outcomes and cleanup refusal. Four separate synthetic canary classes cover REST token, runtime token, encryption-key-shaped input and provider-shaped input. Captured output is checked before any diagnostic is persisted; toolkit mask commands are the admitted masking channel. SDK-child environments are checked for cross-class leakage. Linux execution evidence, local working-tree results and hosted CI results must retain their actual source/platform identity.

## H2 approval record and operator sequence

The coordinator completes this record after both prerequisites merge, then presents the complete record to the user. Do not execute a dispatch while any field is unresolved.

| Approval field                       | Concrete binding                                                                                                             |
| ------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------- |
| Exact merged source                  | Full SHA from the accepted H1+V1 main tree; copy, do not resolve a moving ref during execution                               |
| Reviewed H1 source / asset inventory | Exact H1 review head and eight fixed Action asset hashes from its handoff                                                    |
| Repository / workflow / actor        | The fixed identities in the trust table above                                                                                |
| Fixture                              | The fixed 4,096-byte digest above; logical name parameterized only by resulting A run ID                                     |
| Mutations and reads                  | The complete finite table above, including SDK retries and requested one-day retention                                       |
| Cleanup authority                    | Only the one future artifact resulting from the approved A run, after coordinator binds its exact safe receipt; one B DELETE |
| Failure/unknown disposition          | Stop without replay; expiry or separate user-authorized reconciliation; no permission/credential escalation                  |
| User approval evidence               | Explicit approval of this source and complete operation set, recorded by coordinator before dispatch                         |

After that approval only, the operator may issue these two manual dispatches. `APPROVED_SHA` must be the literal SHA in the approval record. The acknowledgement string is an execution guard, not the approval itself.

```bash
# H2 only. Never execute during H1 preparation.
gh workflow run cr2-hosted-artifact-proof.yml --repo SolusQuest/agentic-pr-review --ref main \
  -f expected-head-sha="$APPROVED_SHA" -f phase=produce \
  -f finite-approval=ONE-SYNTHETIC-OBJECT-TWO-RUNS-ONE-EXACT-DELETE
```

Observe the original run to terminal success without redispatch. Record A's actual run ID/attempt, source/actor and allowlisted summary receipt. If dispatch delivery or completion is unknown, reconcile that original run; never submit another produce request. Bind the sole resulting artifact ID to the prior approval. If A failed, stopped or has ambiguous state, stop before B.

```bash
# H2 only, after the coordinator verifies A's exact successful receipt.
gh workflow run cr2-hosted-artifact-proof.yml --repo SolusQuest/agentic-pr-review --ref main \
  -f expected-head-sha="$APPROVED_SHA" -f phase=restore \
  -f finite-approval=ONE-SYNTHETIC-OBJECT-TWO-RUNS-ONE-EXACT-DELETE \
  -f producer-receipt="$SANITIZED_A_RECEIPT"
```

Observe the original B run and its composite post result. A successful B receipt must agree with the independently checked A identity and report exactly one DELETE and verified absence. Record actual outcomes/counts/source and any limitations. A failed, cancelled, skipped or missing proof is not service acceptance. H1 does not run either command or claim either result.
