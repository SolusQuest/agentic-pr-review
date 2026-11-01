# First Retained Managed Candidate

[R7-P6 #354](https://github.com/SolusQuest/agentic-pr-review/issues/354) prepared and independently verified the candidate below on 2026-11-01. This is a signed, retained preparation result, still unqualified; the canonical map is now checked into the Action source pending an accepted binding merge. The [release policy](../10_workflow/release-policy.md) and [preparation runbook](../10_workflow/release-runbook.md) govern its use.

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

## Independent Verification And Limits

The successful original build, record and sign jobs used their existing separate least-privilege permissions and no provider calls. The build restored the nine fixed-source locked packages before credential-free original production. The signing job used the existing GitHub OIDC route and `actions/attest@1e69f48acb82d1966a394da916b4c1698aa569d6`, without release-write authority.

After the entire run completed successfully, independent verification used the original downloaded files and the separately retained producer locator, rather than treating a downloaded hash or saved verification Boolean as approval. The merged `prepare-candidate.mjs inspect` and `storage` gates passed. The complete `verify-provenance.mjs verify` gate with the pinned official `gh 2.103.3` then returned `provenanceVerified: true` and `completedRunVerified: true`.

That gate authenticated original B, canonical candidate/map, full inventory/build inputs, exact successful run/attempt and storage association, plus the original bundle's cryptographic signature and predicate. It restricted the repository, exact signer `https://github.com/SolusQuest/agentic-pr-review/.github/workflows/release.yml@refs/heads/main`, OIDC issuer, main ref, hosted runner, certificate/source/workflow digest W, producer run/attempt, and the signed association to S. The predicate type is `https://github.com/SolusQuest/agentic-pr-review/attestations/candidate/v1`. The workflow's earlier in-progress verification report remains original evidence with `completedRunVerified: false`; the independent completed-run gate is the handoff proof.

This record identifies the exact candidate a separately authorized R7-P7 binding may review. The ordinary repository map now contains the exact original canonical map bytes with SHA-256 `7a83ac625d4d85140a5e9ed37c9d7704115e1b1784a397b8dbe77c11806a2edc`, and all four ordinary bundles are regenerated from that map. This source binding does not select T: only the accepted binding PR’s actual merge commit may become T. The separately retained original candidate, B, S/W and producer identities above are unchanged. Historical CR2 rehearsal uses an isolated fixed-source Action fixture; it does not qualify this newly bound candidate. No final T, candidate-versus-T qualification, draft staging, tag, public release, downstream installation or adoption is established. Preparation/signature success is not those later gates, reproducible compilation, live provider quality or publication approval. Follow the runbook's restrictive verification again at each later authorized boundary, preserving these original identities and bytes.
