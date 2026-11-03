# P7: Exact Original-Pair Qualification

This dated technical record covers [P7 #355](https://github.com/SolusQuest/agentic-pr-review/issues/355). On 2026-11-03, [Runtime CI run 41572338236, attempt 1](https://github.com/SolusQuest/agentic-pr-review/actions/runs/41572338236) completed successfully, including all sixteen jobs. The unchanged original managed candidate and bound composite passed the `r7-v2-managed-pair` qualification under profile `cr1-solus-internal4-dev`, context format 2 and compatibility 1. This is exact-pair evidence for subsequent admission; it does not establish a public release, downstream adoption or R7 completion.

## Qualified Identities

| Identity                                                                               | Exact value                                                                     |
| -------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------- |
| Version                                                                                | `v0.2.0-internal.1`                                                             |
| Payload source and producer workflow, S=W                                              | `75a771908983cfb881a3fac4b2c57a69f53f6a60`                                      |
| Original managed archive B                                                             | `agentic-pr-review-v0.2.0-internal.1-linux-x64-managed.tar.gz`, 1,911,124 bytes |
| B SHA-256                                                                              | `51c1ea4a82d9e87610b437ec6cb1282c4700e5157c9dc6bb73ac420840508a4d`              |
| Bound composite T, [PR #478](https://github.com/SolusQuest/agentic-pr-review/pull/478) | `5fad61ef08f6cf61a2f26832e6174e4f35d624cd`                                      |
| Qualifier Q5, [PR #483](https://github.com/SolusQuest/agentic-pr-review/pull/483)      | `9fefe43620d722544bfe19fd9ea3aa880cc1b29d`                                      |
| Finalized pair SHA-256                                                                 | `8292db367d406d35a42720276aa53f030d54bcb10039d919d62874c4c49715bd`              |
| Finalization request SHA-256                                                           | `b8012a2de4522d30a6a8a594abfca58fe765c1b861b3f8852ae5c76f4ae19017`              |
| Qualification receipt SHA-256                                                          | `86f6300ef0291ff57361c33e9b3b272d36993a29a59549d28350a2727b7acf01`              |

The original canonical pair remains 5,000 bytes with its preparatory `qualification.state=unqualified`; the request remains 557 bytes. Qualification is established by the separate run-bound passing receipt, authenticated completed workflow and original artifact, and full verifier result. The pair was not rewritten to refer to its own later proof. Neither B nor T was rebuilt, repacked or resigned. A later documentation merge does not replace Q5, S, W, B or T.

## Execution And Verification

The existing Ubuntu 24.04 x64 workflow executed original B and exact T in its isolated consumer with the trusted runtime, keeping SDK/cache use in supporting tooling. It retained the existing 150-minute job, 90-minute role and 45-second supporting-operation limits and read-only job permissions. All 50 timed cases closed, no case watchdog fired, and final role elapsed time was 5,187,056 ms against 5,400,000 ms. All 60 mandatory checks passed across the eleven declared qualification groups, including capacity, recovery/reset refusal, managed loading, original assets, publication ordering, output/accounting parity and external confinement.

The exact Q5 qualifier enforces zero external-service errors and successful presentation cleanup assertions before case acceptance. The success log retains all case closure timings; it does not retain each raw presentation cleanup object. The original qualification ZIP contains 62 members: 60 check-evidence files, `case-results.json` and `receipt.json`. Each evidence digest and both receipt/result bindings were verified.

After whole-run success, the authenticated artifact API and original ZIP were checked independently for producer, run attempt, source, name, size, digest and CRC. The actual `finalize-candidate.mjs verify-qualification` invocation then verified the original candidate, map, provenance bundle, pair, request, exact Q5, original qualification ZIP and receipt. It exited zero and returned `qualified=true`, `checkCount=60`, the exact pair/receipt hashes above and `caseResultsSha256=314412ff0868c5394c7d6aa6e0b1c8b86cea209582130930c05688f88f54f9fb`. A job-only result, saved PASS, ordinary source test or V2 receipt does not substitute for that full verification.

## Evidence Availability

The four objects below are the original three candidate artifacts plus the qualification ZIP. The run's two separate ordinary V2 artifacts expire earlier and are not members of this exact-pair handoff set. These are GitHub Actions storage objects, not release assets. Their expiry timestamps were read back after qualification; the earliest governs subsequent admission. Preserving a local copy or creating a new receipt does not extend them.

| Object                                                                                                          | Artifact ID   | ZIP bytes | ZIP SHA-256                                                        | Expires at (UTC)    |
| --------------------------------------------------------------------------------------------------------------- | ------------- | --------- | ------------------------------------------------------------------ | ------------------- |
| Original payload                                                                                                | `13074758865` | 1,914,222 | `dab5d36ef8603dcc7d1b3976d0ffb0e3686fafdaa8b4b271556bf89f19da4fde` | 2026-11-08 07:46:02 |
| Original metadata                                                                                               | `13075227321` | 5,695     | `6b9e07692b1a433b6d6f3eb61b6154405e19644dcaaef7e5f1568c1368506cf3` | 2026-11-08 07:46:19 |
| Original provenance                                                                                             | `13075027841` | 8,364     | `d03abe66f534763034e3edfd179deef6f1aa1f7c7328e3e837de5e86a45adad9` | 2026-11-08 07:46:49 |
| [Qualification](https://github.com/SolusQuest/agentic-pr-review/actions/runs/41572338236/artifacts/13205572099) | `13205572099` | 41,768    | `010b5b2e0fc232501c6f55466204444c319e839c054dc8093d825a57b8b69161` | 2026-11-10 04:56:06 |

The first three objects belong to [producer 41285866016, attempt 1](https://github.com/SolusQuest/agentic-pr-review/actions/runs/41285866016). Later use requires fresh authenticated storage, identity, expiry and applicable cryptographic verification; this dated record cannot replace those gates.

## Failure History And Remaining Boundaries

Prior qualification runs [41303452434](https://github.com/SolusQuest/agentic-pr-review/actions/runs/41303452434), [41395568224](https://github.com/SolusQuest/agentic-pr-review/actions/runs/41395568224) and [41437719878](https://github.com/SolusQuest/agentic-pr-review/actions/runs/41437719878) remain failed. The third run's case 46 reported value was not retained and remains unknown. The independently demonstrated oracle correction in PR #483 distinguishes a rejected four-tool provider batch, which counts one failed exchange, from a successful provider exchange followed by a local tool/call limit, which counts zero. [Installed-package source proof 41469944643](https://github.com/SolusQuest/agentic-pr-review/actions/runs/41469944643) passed 1,191 checks and 117 controlled sends with zero live calls; it supports that correction without retrospectively passing earlier runs or replacing the final original-pair qualification.

This record grants no staging, publication, redistribution, provider spending, tag, settings or secret operation. The [release policy](../10_workflow/release-policy.md) and [R7 dependency plan](r7-plan.md) retain their separate gates. P8 staging, P9 publication, public-download validation and the bounded adoption campaigns remain outside this result. Issue closure and overall milestone acceptance remain separate maintainer decisions.
