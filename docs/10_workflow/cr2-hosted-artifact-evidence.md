# Hosted artifact interoperability evidence

The finite H2 proof for [#454](https://github.com/SolusQuest/agentic-pr-review/issues/454) passed on 2026-10-24. Two separately dispatched, attempt-1 GitHub-hosted runs exercised the actual immutable composite, official artifact SDK upload child and production C# artifact store/REST client. A uploaded and read back one synthetic opaque object; B independently restored and authenticated it, deleted that exact object once, and verified fresh absence. Both runs and all post/fixture-stop steps completed successfully. No rerun or mutation replay was used.

This qualifies the observed GitHub artifact transport and synthetic AEAD path described in the [finite proof runbook](cr2-hosted-artifact-proof.md). The companion uses a synthetic map and public development profile. It does not establish original Solus archive execution, ordinary release-map resolution, production PR authorization, accepted SESSION/lineage restoration, provider/model behavior or release readiness.

## Bound source and actual runs

| Item                         | Observed identity/result                                                                                             |
| ---------------------------- | -------------------------------------------------------------------------------------------------------------------- |
| Approved workflow/source     | `7e0ce57903e83a34766eaf35fc37129352d40982`                                                                           |
| Approved tree                | `614bf742917de62e57e2eab9396587b27bb51fe3`                                                                           |
| Workflow blob                | `78591b18cc8627801b351b11a5828c49ff5c380e`                                                                           |
| Installed Action             | `5207bf8058b3d1301c0a6e805e7f21334832223e`                                                                           |
| Repository / actor           | `SolusQuest/agentic-pr-review` (`1263978368`) / `Yuee98` (`16307884`)                                                |
| Run A                        | [40061259888](https://github.com/SolusQuest/agentic-pr-review/actions/runs/40061259888), produce, attempt 1, success |
| Run B                        | [40061741768](https://github.com/SolusQuest/agentic-pr-review/actions/runs/40061741768), restore, attempt 1, success |
| A creation / terminal update | 2026-10-24 12:10:01 / 12:11:11 Asia/Shanghai                                                                         |
| B creation / terminal update | 2026-10-24 12:17:14 / 12:18:19 Asia/Shanghai                                                                         |
| Platform                     | GitHub-hosted Ubuntu 24.04 x64, Node 24, .NET SDK 10.0.109                                                           |

Both runs used the fixed approved source on `main`, the expected actor and workflow, and the eight checked immutable Action assets. Source, workflow and actor were revalidated before each single dispatch. The successful A receipt was bound to the prior finite approval before B. B's producer validation occurred within the 30-minute window from A creation; both jobs remained within their ten-minute limit. The documentation commit recording this evidence is not the execution source.

## Object verification and cleanup

| Fact                            | Observed value                                                     |
| ------------------------------- | ------------------------------------------------------------------ |
| Synthetic encrypted object      | 4,096 bytes                                                        |
| Encrypted SHA-256               | `15d392b30989f7b08f94615c1c57202a47a95408e35abdf286662a4ab8c3ce86` |
| Downloaded ZIP                  | 5,888 bytes                                                        |
| Archive SHA-256                 | `79e59d6f7d005ce8a8433445ade733676646f478ef375b93f3c50ad09b6623e0` |
| Requested retention             | One day                                                            |
| Observed expiry before deletion | 2026-10-25 12:11:03 Asia/Shanghai                                  |
| A receipt                       | `hosted`, `produce`, `passed`, `upload_committed`                  |
| B receipt                       | `hosted`, `restore`, `passed`, `delete_committed`                  |
| Final object state              | Fresh absence confirmed after the sole exact DELETE                |

The unchanged receipt policies admitted both actual receipts and bound B to the same A object, producer run/attempt, physical identity, encrypted/archive digests, size and expiry. B used a fresh C# store/process to authenticate producer provenance, complete its census, download the exact ZIP/carrier and authenticate/decrypt the public synthetic AES-256-GCM fixture before admitting deletion. A supplied numeric artifact ID alone was insufficient. The committed deletion result includes the production client's fresh absence check; it is not inferred from expiry. The object was deleted before its observed expiry, so retention until expiry was not tested.

## Observed operation accounting

These are the C# companion's measured counters, excluding operator run/job/log metadata reads, the coordinator's separate read-only exact-artifact metadata check, setup downloads and the SDK's internal upload HTTP attempts.

| Counter                        |       A |      B |    Pair | Approved pair ceiling |
| ------------------------------ | ------: | -----: | ------: | --------------------: |
| Authenticated C# REST requests |       6 |      9 |      15 |                    32 |
| Anonymous ZIP downloads        |       1 |      1 |       2 |                     4 |
| Authenticated metadata bytes   | 149,464 | 82,291 | 231,755 |             1,048,576 |
| Downloaded archive bytes       |   5,888 |  5,888 |  11,776 |                65,536 |
| Complete single-page censuses  |       2 |      1 |       3 |                     3 |
| APR SDK upload calls           |       1 |      0 |       1 |                     1 |
| Exact artifact DELETE calls    |       0 |      1 |       1 |                     1 |

All per-run request and byte ceilings also passed. The live receipt does **not** measure the SDK's HTTP attempt count. The runbook's 678-request / 16-MiB application-request-body ceilings are conservative bounds derived from the pinned SDK, rather than measured live counts. Rehearsal Create/Blob/Finalize counts are not observations of this hosted pair. The only permission classes were `contents: read` and `actions: write`, using the existing repository and Runner runtime tokens; no new credentials or provider calls were required.

## Privacy verification and limits

The actual complete job logs were inspected locally for the six fixed canaries and credential/JWT/signed-download-URL patterns, with no matches. Both check runs reported zero annotations. Private raw diagnostic material was not copied into this document. These bounded checks do not prove absence of arbitrary secrets.

Direct remote step-summary Markdown was not available in the fetched run/job HTML or check REST output (`summary` was null). Summary validation therefore used the fixed source: the proof writes the same admitted receipt JSON printed in the inspected console log, with a fixed preamble; the immutable Action writes its escaped closed completion summary, whose companion supplies no review fields, no provider accounting and `NotAccessed` state. Its wrapper failure summary is fixed text. No additional active summary writer was found. This is a source-backed inference about those known writers, not a claim that the remote Markdown was directly inspected. The harness itself publishes allowlisted object metadata in its Actions receipt; this document avoids duplicating artifact IDs/names.

The hosted pair covers the normal successful path only. Cancellation, permission denial, rate limiting, conflicting census, capacity exhaustion, uncertain upload/delete and canary injection were not exercised against the live service. H1's deterministic refusal tests remain separate evidence. The full safe receipts, run/attempt/object bindings, terminal/post evidence and private audit records are retained by the coordinator for closeout; public Actions log retention is not assumed permanent.
