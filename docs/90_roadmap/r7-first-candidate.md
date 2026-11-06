# First Retained Managed Candidate

[R7-P6 #354](https://github.com/SolusQuest/agentic-pr-review/issues/354) prepared and independently verified the candidate below on 2026-11-01. This is a signed, retained preparation result, still unqualified. The canonical map is bound at the accepted Action commit T below, and read-only finalization has frozen the original candidate-versus-T descriptor. The [release policy](../10_workflow/release-policy.md) and [preparation runbook](../10_workflow/release-runbook.md) govern its use.

## Source And Producer

| Identity                      | Exact value                                                                                   |
| ----------------------------- | --------------------------------------------------------------------------------------------- |
| APR version                   | `v0.2.0-internal.1`                                                                           |
| Payload source S              | `75a771908983cfb881a3fac4b2c57a69f53f6a60`                                                    |
| Approved and actual builder W | `75a771908983cfb881a3fac4b2c57a69f53f6a60`                                                    |
| Source tree                   | `03c6727b3d7f76bbe2113163feb29794c77dc6ef`                                                    |
| Repository / ID               | `SolusQuest/agentic-pr-review` / `1263978368`                                                 |
| Workflow                      | `.github/workflows/release.yml@refs/heads/main`, manual `operation=prepare`                   |
| Producer run / attempt        | [41285866016](https://github.com/SolusQuest/agentic-pr-review/actions/runs/41285866016) / `1` |
| Runner                        | GitHub-hosted `ubuntu-24.04` x64                                                              |
| Observed build toolchain      | Node `24.22.1`, .NET SDK `10.0.109`                                                           |
| Final Action T                | Not selected; preparation does not establish T                                                |

S and W were independently approved and checked; their equality is intentional. The actual run's head and workflow identity match W. A later documentation or binding commit is not the producer of these original bytes. The admitted source retains the bounded `cr1-solus-internal4-dev` profile and context format/compatibility `2/1`; preparation grants no capacity or quality graduation.

This was the second separately authorized actual preparation, a new run with GitHub attempt `1`. The first [run 41282605986 / attempt 1](https://github.com/SolusQuest/agentic-pr-review/actions/runs/41282605986) failed with `invalid_output`, retained no artifacts and never reached signing. The output-name correction was reviewed in [PR #475](https://github.com/SolusQuest/agentic-pr-review/pull/475) before the new exact S/W was approved. Neither run was rerun; the two authorized actual attempts are consumed.

## Original Bytes And Candidate

| Item                                         | Exact identity                                                     |
| -------------------------------------------- | ------------------------------------------------------------------ |
| Original B filename                          | `agentic-pr-review-v0.2.0-internal.1-linux-x64-managed.tar.gz`     |
| Original B size                              | `1911124` bytes                                                    |
| Original B SHA-256                           | `51c1ea4a82d9e87610b437ec6cb1282c4700e5157c9dc6bb73ac420840508a4d` |
| Original raw `receipt.json` SHA-256          | `feeaaf4d04c214e3757e9e209a052a3da7e26dba63cb3fb48ee8691f42755639` |
| Canonical `candidate.json` SHA-256           | `acadec14d05574b982d87bdb9175a4c6f8bb5c21ff1e99f2d6a31d1dc8c4f56c` |
| Canonical `proposed-action-map.json` SHA-256 | `7a83ac625d4d85140a5e9ed37c9d7704115e1b1784a397b8dbe77c11806a2edc` |
| Build identity                               | `015ac6c2e3a6e32b0526449681084417c36cf5c17dcd281a60c0ee1330c22bd0` |
| Entry DLL SHA-256                            | `9e3934f05011cb78fab31b7ce1ecee8add6df4a5d6e90ac81b683bf24c2a9497` |
| Original Sigstore `bundle.json` SHA-256      | `c730a5810c70127ed769473f098f774cfe16f240093c551355025c36495ffdba` |
| Attestation API ID                           | `62283967`                                                         |

The unchanged receipt inventories all 14 original members, including the managed entry DLL, runtime/dependency metadata, nine dependency assemblies, manifest and notices. The candidate retains the complete build inputs and locked dependency closure; the build identity binds those inputs. The five Solus packages remain exactly `0.1.0-internal.4`. `THIRD-PARTY-NOTICES.txt` is 14,800 bytes with SHA-256 `88137bec99f5cb29ee138f602ee8eb48183eba9a768ef076c1e39f275ad8f394`; its existing source-license materials were independently checked in original B.

Before dispatch, the maintainer explicitly confirmed entitlement to approve inclusion of the five pinned Solus packages and locked dependencies in this public repository's candidate artifacts. That scoped approval and the retained notices do not create a project license or approve a later public release.

## Exact Retained Storage

All three artifacts belong to run `41285866016`, attempt `1`, and request seven days of retention. The transport ZIPs were preserved and independently hashed before extracting their original files. Transport digests identify the Actions ZIPs; the original B digest above identifies different bytes.

| Artifact name                    | ID            | ZIP bytes | ZIP SHA-256                                                        | Observed `expires_at` (UTC) |
| -------------------------------- | ------------- | --------- | ------------------------------------------------------------------ | --------------------------- |
| `r7-p1-payload-41285866016-1`    | `13074758865` | `1914222` | `dab5d36ef8603dcc7d1b3976d0ffb0e3686fafdaa8b4b271556bf89f19da4fde` | `2026-11-08T07:46:02Z`      |
| `r7-p1-metadata-41285866016-1`   | `13075227321` | `5695`    | `6b9e07692b1a433b6d6f3eb61b6154405e19644dcaaef7e5f1568c1368506cf3` | `2026-11-08T07:46:19Z`      |
| `r7-p2-provenance-41285866016-1` | `13075027841` | `8364`    | `d03abe66f534763034e3edfd179deef6f1aa1f7c7328e3e837de5e86a45adad9` | `2026-11-08T07:46:49Z`      |

The payload contains original B and raw receipt; metadata contains the canonical candidate/map and detached digests; provenance contains the predicate, original Sigstore bundle and the workflow's verification report. No archive was rebuilt or repackaged, and no replacement bundle was signed for this record.

The earliest observed expiry is **2026-11-08 07:46:02 UTC**. Independent verification on 2026-11-01 had more than 72 hours remaining. The approved handoff target is within 48 hours, with at least 72 hours remaining when admitting the proposed next phase; these are this operation's scheduling bounds, not a storage SLA. Actual online expiry/deletion governs even if local copies remain. Recheck all exact IDs and live storage before further use; absent, expired, shortened or ambiguous retention stops handoff and requires an explicit new disposition.

## Bound Action And Finalized Pair

[R7-P7 binding PR #478](https://github.com/SolusQuest/agentic-pr-review/pull/478) was accepted and merged on 2026-11-01 at **T = `5fad61ef08f6cf61a2f26832e6174e4f35d624cd`**. T is that actual binding merge, distinct from S/W above; it is not the PR head or a later documentation/CI commit. Its exact canonical map and all eight Action assets are bound by the finalized descriptor.

The trusted merged tooling's full local read-only finalization succeeded against original B/S/W, exact merged PR #478/T, all three retained artifacts and the original attestation bundle. It rechecked content, exact Git objects and runtime policy, authenticated the merged binding and main ancestry, and repeated completed-run storage and cryptographic provenance admission. It executed no candidate or Action code and performed no workflow dispatch, payload build, signing or release mutation.

| Frozen input or output                           | Exact identity                                                     |
| ------------------------------------------------ | ------------------------------------------------------------------ |
| Finalized pair SHA-256                           | `8292db367d406d35a42720276aa53f030d54bcb10039d919d62874c4c49715bd` |
| Original pair size                               | 5000 bytes                                                         |
| Canonical ten-field finalization request SHA-256 | `b8012a2de4522d30a6a8a594abfca58fe765c1b861b3f8852ae5c76f4ae19017` |
| Request size                                     | 557 bytes, without a trailing newline                              |
| Pair qualification state                         | `unqualified`                                                      |

The original pair bytes, detached digest, summary and canonical request were retained independently for review. The pair still identifies the unchanged B, S/W, candidate/map, producer and three storage locators above. It adds no qualification receipt or Q self-reference. These are local read-only finalization outputs; this checkpoint creates no new retained Actions artifact and extends none of the original storage deadlines.

This documentation-only readback preserves all eight Action assets, accepted qualification tooling and workflow bytes at T. Q remains unset at this checkpoint: only this task's separately accepted documentation readback merge may become Q, distinct from S/W/T. Actual qualification must then use the frozen original pair and original B under the separately gated Q context. The actual qualification budget remains **0/1**, and P6 preparation remains exhausted at **2/2**. No qualification or additional Runner campaign occurred here.

## Independent Verification And Limits

The successful original build, record and sign jobs used their existing separate least-privilege permissions and no provider calls. The build restored the nine fixed-source locked packages before credential-free original production. The signing job used the existing GitHub OIDC route and `actions/attest@1e69f48acb82d1966a394da916b4c1698aa569d6`, without release-write authority.

After the entire run completed successfully, independent verification used the original downloaded files and the separately retained producer locator, rather than treating a downloaded hash or saved verification Boolean as approval. The merged `prepare-candidate.mjs inspect` and `storage` gates passed. The complete `verify-provenance.mjs verify` gate with the pinned official `gh 2.103.3` then returned `provenanceVerified: true` and `completedRunVerified: true`.

That gate authenticated original B, canonical candidate/map, full inventory/build inputs, exact successful run/attempt and storage association, plus the original bundle's cryptographic signature and predicate. It restricted the repository, exact signer `https://github.com/SolusQuest/agentic-pr-review/.github/workflows/release.yml@refs/heads/main`, OIDC issuer, main ref, hosted runner, certificate/source/workflow digest W, producer run/attempt, and the signed association to S. The predicate type is `https://github.com/SolusQuest/agentic-pr-review/attestations/candidate/v1`. The workflow's earlier in-progress verification report remains original evidence with `completedRunVerified: false`; the independent completed-run gate is the handoff proof.

The ordinary repository map contains the exact original canonical bytes with SHA-256 `7a83ac625d4d85140a5e9ed37c9d7704115e1b1784a397b8dbe77c11806a2edc`, with the four ordinary bundles regenerated and accepted at T. Historical CR2 rehearsal uses an isolated fixed-source Action fixture; it does not qualify this newly bound candidate. The complete finalization result above remains unqualified. No candidate-versus-T qualification, draft staging, tag, public release, downstream installation or adoption is established. Preparation, signature and finalization success are not those later gates, reproducible compilation, live provider quality or publication approval. Follow the runbook's restrictive verification again at each later authorized boundary, preserving the frozen pair and all original identities and bytes.

## Independent Internal.2 Qualification Input Checkpoint

On 2026-11-06, a separately authorized `v0.2.0-internal.2` preparation and map binding produced the independent inputs below. All earlier sections retain their historical `.1` checkpoint identities and limits; they do not describe the current `.2` map or reset any previous attempt. This new checkpoint remains **unqualified**.

### Internal.2 Source And Original Bytes

| Identity                         | Exact value                                                                                                           |
| -------------------------------- | --------------------------------------------------------------------------------------------------------------------- |
| Payload source S                 | `75a771908983cfb881a3fac4b2c57a69f53f6a60`                                                                            |
| Original builder W               | `da487ad3fc04ebba6b36efaad4b613669f6eea40`                                                                            |
| Source tree                      | `03c6727b3d7f76bbe2113163feb29794c77dc6ef`                                                                            |
| Producer run / attempt           | [42155761876](https://github.com/SolusQuest/agentic-pr-review/actions/runs/42155761876) / `1`, completed successfully |
| Actual build toolchain           | GitHub-hosted Ubuntu 24.04 x64, Node `24.22.2`, .NET SDK `10.0.109`                                                   |
| Original B filename              | `agentic-pr-review-v0.2.0-internal.2-linux-x64-managed.tar.gz`                                                        |
| Original B size / SHA-256        | `1911137` bytes / `85315e0c1cf4282bad9828de2e280ff549227c94a61d5aa16b44675fd4f2849d`                                  |
| Original raw receipt SHA-256     | `2836f5ecbd567324d9ece40c22a6956a5bfaea637cbf9d5c623aa23a43a8571f`                                                    |
| Canonical candidate SHA-256      | `eb8e7f24bc381eea8b14caa14709582fc39f5c7b07ef8078a271ee3b3d812b02`                                                    |
| Canonical map SHA-256            | `eb68da365d47a0a75daa2e001e1348d6c2224b2c7640661ed551e1749838eb5e`                                                    |
| Build identity                   | `a6e416afdec3737a7df40d74a1a019995f95a095ba490604cb7c726ffd6979e8`                                                    |
| Original Sigstore bundle SHA-256 | `efcb8e30a8bece2067219c1846230ec1871f3d605a07860ef714cc5567f4add8`                                                    |

S and W are intentionally different: the admitted main workflow at W built the clean S source with the new version identity. The signed predicate binds that detached source and original archive. The original receipt inventories 14 members and the same nine locked dependency pins, including the five Solus `0.1.0-internal.4` packages. The existing 14,800-byte notices remain byte-identical, SHA-256 `88137bec99f5cb29ee138f602ee8eb48183eba9a768ef076c1e39f275ad8f394`. This preparation stays within the approved package-inclusion and use scope; it grants no new license or public-release permission.

### Internal.2 Retained Storage

The following original Actions transport ZIPs belong only to producer run `42155761876`, attempt `1`. Their sizes and digests identify transport bytes, not original B.

| Artifact name                    | ID            | ZIP bytes | ZIP SHA-256                                                        | Observed expiry (UTC)  |
| -------------------------------- | ------------- | --------- | ------------------------------------------------------------------ | ---------------------- |
| `r7-p1-payload-42155761876-1`    | `13474437693` | `1914235` | `7cfbef35b82176d3a8370e87dda785bcef0989259013365e4966da7ed26e75a1` | `2026-11-13T03:23:25Z` |
| `r7-p1-metadata-42155761876-1`   | `13474377959` | `5768`    | `c2cfd0bfd2013c32103d90d844d873600233934577276231cc153b25ae0bc795` | `2026-11-13T03:23:45Z` |
| `r7-p2-provenance-42155761876-1` | `13474631449` | `8460`    | `22b80042a0ead0257a3f06c63836415f07da4f1bee1f836f2849d9ee5c1c87b4` | `2026-11-13T03:24:12Z` |

Fresh read-only admission before finalization confirmed the exact successful producer and all three original identities, with more than 72 hours remaining. The earliest actual expiry is `2026-11-13T03:23:25Z`; retained local copies do not extend it. The new route uses a 48-hour handoff target of `2026-11-08T03:10:06Z` and a hard maximum of `2026-11-10T03:10:06Z`. Later staging must still admit all four actual storage records with at least 72 hours remaining, choose `validUntil` no later than the earlier of that hard maximum and the earliest four-storage expiry minus 72 hours, and send at least one hour before `validUntil`. The qualification storage record does not exist at this checkpoint. Missing, expired, shortened or ambiguous retention stops the route; no extension is implied.

### Internal.2 Accepted Action And Frozen Pair

[Binding PR #489](https://github.com/SolusQuest/agentic-pr-review/pull/489) was actually merged on 2026-11-06 at `04:36:54Z`, producing **T = `cdf77848105faaad0913d9ab6ad4e65b60d5d8fb`**, tree `bfde615707f63baf0bb31e6c69eacd54d299b71f`. T is the binding merge, not its reviewed PR head, S or W. Its canonical map is the exact producer-generated `.2` file. The four ordinary bundle source-inventory footers were regenerated with every executable prefix byte unchanged from W.

The existing trusted full read-only P3 finalizer succeeded at `2026-11-06T04:42:26Z` against original B/S/W, the actual merged PR #489/T, the three retained storage records and original bundle. It authenticated the merged binding and main ancestry, checked exact Git objects and runtime policy, and performed the integrated completed-run storage and restrictive cryptographic provenance gates with pinned `gh 2.103.3`. It executed no candidate or Action code, rebuilt or signed no archive, and dispatched or uploaded nothing.

| Frozen input or output              | Exact identity                                                     |
| ----------------------------------- | ------------------------------------------------------------------ |
| Canonical ten-field request SHA-256 | `7a5b8e55ebe5db3053cf2de1bdb79826e16b14c0f81afef332cafe0e0ea832da` |
| Original request size               | `557` bytes, without a trailing newline                            |
| Finalized pair SHA-256              | `7f161365f5457a12bd81bc2abf999a59570cdd87540a5d3841c2784f4d808325` |
| Original pair size                  | `5000` bytes, without a trailing newline                           |
| Exact eight-asset inventory SHA-256 | `8f2f7159fc1d4eb2f528d87b991b85b458f32628bb3689db0496ac277d6a3e10` |
| Pair qualification state            | `unqualified`                                                      |

The original request, pair, detached digest, summary and process output were retained for independent acceptance. The descriptor binds the eight actual Action files at T, their Git blob and byte digests, the original candidate and storage locators, and the verified runtime policy. It creates no replacement archive or retained Actions artifact.

This documentation-only input record preserves every Action asset and the qualification/control implementation at T. Q is deliberately unset here: only this record's independently accepted merge may be selected as the actual qualifier/control commit, distinct from S, W and T. A future separately admitted qualification must use that exact Q and the frozen original `.2` pair and B; neither old `.1` receipts nor ordinary CI/V2 support receipts establish its result. Actual qualification and staging have not been run for this candidate. Earlier failures and spent budgets remain consumed. This checkpoint authorizes no retry, publication, provider use, new credential or protection change, and does not complete P8 or R7.
