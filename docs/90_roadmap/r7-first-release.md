# First Qualified Managed Candidate Staging Record

The exact `v0.2.0-internal.2` Action-payload pair completed staging into existing **unpublished prerelease draft `436244771`**. Independent full read-only verification passed on **2026-11-07 at 09:34:24 UTC**. The draft has 14 verified assets totaling **7,321,884 bytes**. This records P8 operational evidence; final documentation/administrative acceptance, P9 public publication and adoption are separate boundaries. It does not establish a publicly downloadable release or complete R7.

This checkpoint follows [the first-candidate inputs](r7-first-candidate.md#independent-internal2-qualification-input-checkpoint), [package qualification](r7-package-qualification.md), [historical `.1` P7 qualification](r7-p7-qualification.md), and the [adoption runbook](../10_workflow/r7-adoption-runbook.md). It supplies the sanitized staged identity for [P8 / #356](https://github.com/SolusQuest/agentic-pr-review/issues/356), subject to the [release policy](../10_workflow/release-policy.md). Earlier `.1` and unqualified `.2` input checkpoints retain their historical meaning. The historical P7 record does not establish `.2` qualification; the actual `.2` run, pair and receipt below supply that evidence.

## Frozen Pair And Successful Producer

| Identity                                           | Exact value                                                                                                                   |
| -------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------- |
| Payload source S                                   | `75a771908983cfb881a3fac4b2c57a69f53f6a60`                                                                                    |
| Original builder W                                 | `da487ad3fc04ebba6b36efaad4b613669f6eea40`                                                                                    |
| Original B                                         | `agentic-pr-review-v0.2.0-internal.2-linux-x64-managed.tar.gz`                                                                |
| Original B bytes / SHA-256                         | `1911137` / `85315e0c1cf4282bad9828de2e280ff549227c94a61d5aa16b44675fd4f2849d`                                                |
| Accepted Action T                                  | `cdf77848105faaad0913d9ab6ad4e65b60d5d8fb` ([binding PR #489](https://github.com/SolusQuest/agentic-pr-review/pull/489))      |
| Independent qualifier / original staging control Q | `f8f9d78e7cb3ec3b9ac8e452a245197d18fca248` ([input-record PR #490](https://github.com/SolusQuest/agentic-pr-review/pull/490)) |
| Successful completion control C                    | `57e1659a6b6e97478f4d79d06dc7b1f5e0587bc2` ([controller PR #491](https://github.com/SolusQuest/agentic-pr-review/pull/491))   |
| Finalized pair bytes / SHA-256                     | `5000` / `7f161365f5457a12bd81bc2abf999a59570cdd87540a5d3841c2784f4d808325`                                                   |
| Qualification ZIP bytes / SHA-256                  | `41768` / `6a249bc96d970f6ece2bf65b3149ddc614397560476fb2e67d3d231aac8d7e02`                                                  |
| Raw passing qualification receipt SHA-256          | `34f9a1c5788792825fbd93dace6b86b8f969a8a92b8ab0a85f8fe611a6b8e856`                                                            |
| Original draft descriptor bytes / SHA-256          | `2687` / `760bf4d6a08bf4de9c0c4a7d04508b2efe3e0031f216b24b9a1f173c9e45bb61`                                                   |
| Format-2 staged record bytes / SHA-256             | `13118` / `71c9f629a1654a0da4f695e7305c3bac795f28b10459d5317a29f449b324ff6d`                                                  |

S, W, T, Q and C are distinct identities. C controls the successful completion; it does not replace the original builder, Action binding or qualifier. The frozen finalized-pair bytes retain their original `unqualified` checkpoint field. The separate actual original-candidate qualification receipt and genuine full `qualified: true` verification establish the later qualification result without rewriting that pair.

| Actual operation                 | Evidence and disposition                                                                                                                                                                                                                                                                           |
| -------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Original preparation             | [Run `42155761876`](https://github.com/SolusQuest/agentic-pr-review/actions/runs/42155761876), attempt `1`, W, whole success; original B, candidate and signed provenance retained                                                                                                                 |
| Original candidate qualification | [Run `42170346041`](https://github.com/SolusQuest/agentic-pr-review/actions/runs/42170346041), attempt `1`, Q, whole success; 50 cases / 60 checks against unchanged original B and exact T; full qualification passed                                                                             |
| Failed ordinary staging          | [Run `42184810758`](https://github.com/SolusQuest/agentic-pr-review/actions/runs/42184810758), attempt `1`, Q, failed at `release_readback` with `stage_create_unknown`; original intent and the same empty draft were preserved                                                                   |
| Successful dedicated completion  | [Run `42359259235`](https://github.com/SolusQuest/agentic-pr-review/actions/runs/42359259235), attempt `1`, C, whole success; [stage job `128364010486`](https://github.com/SolusQuest/agentic-pr-review/actions/runs/42359259235/job/128364010486) succeeded after the normal `r7-staging` review |

The failed Q run remains failed. Its original job-log digest was authenticated; the underlying create/readback cause remains unknown. The separately authorized completion populated the same draft, created no new draft and did not replay the failed ordinary request. Its real successful producer, canonical owner-review comment, reviewer and environment were independently authenticated through the actual run and approvals records.

## Two Intent Fences

These are actual retained raw intent bytes, not the payload/provenance/qualification transport ZIPs.

| Role / actual artifact ID             | Raw bytes | Raw SHA-256                                                        | Producer                | Observed expiry (UTC)  |
| ------------------------------------- | --------- | ------------------------------------------------------------------ | ----------------------- | ---------------------- |
| Failed-parent intent `13487651370`    | `2399`    | `4ef454e4c52e21bc18a2013943ced4c3e54bcf2ed595d2aa352b0ba304e1370a` | `42184810758` / `1` / Q | `2026-11-11T08:08:51Z` |
| New supplemental intent `13568599088` | `2845`    | `a4098760854372d13f27a8d730d09b601cc0d38b40bec7d855c5ec98ebe15a52` | `42359259235` / `1` / C | `2026-11-11T09:24:11Z` |

The original 15 operation reservations remain consumed. The new supplemental intent binds that exact parent, the same descriptor/release and 14 original asset operations, with no release-create operation. The new workflow, supplemental SDK upload application and each of the 14 asset upload applications are consumed; SDK application counts do not prove internal wire-attempt counts. Previous `.1` failures, draft `435034509`, its 13 assets and all three original claims remain separate preserved history. No allowance is refunded, transferred or made retryable by this record.

## Actual Draft Asset Manifest

At acceptance, [release `436244771`](https://api.github.com/repos/SolusQuest/agentic-pr-review/releases/436244771) had `draft: true`, `prerelease: true` and `published_at: null`. Its name and intended transport tag were `payload-v0.2.0-internal.2`, target S, with the exact original descriptor body. Both the consumer Action ref `v0.2.0-internal.2` and payload transport ref `payload-v0.2.0-internal.2` were absent. The draft name does not establish a Git ref or publication. Draft visibility and asset access require the applicable maintainer authorization; these links do not advertise public downloads.

Each unique actual asset was `uploaded`, with exact API name, size and SHA-256. Independent live downloads verified every original byte against the frozen source assets, archive, candidate, pair, signature bundle and qualification ZIP.

| Actual asset ID | Name                                                                  | Bytes     | SHA-256                                                            |
| --------------- | --------------------------------------------------------------------- | --------- | ------------------------------------------------------------------ |
| `720220914`     | `action-action.yml`                                                   | `4061`    | `f062d0ff62d9ef4c247b84651c5b3e7e5bb6eff698dca79c64a9c94b2335c747` |
| `720221038`     | `action-internal__action.yml`                                         | `2648`    | `057c65a5c9c44ba996ad5a4d1a84b27ea08b24318c8976ddeaf2ed0c5778eaa7` |
| `720221124`     | `action-internal__dist__cleanup.js`                                   | `15944`   | `00f00ac7bac9cd32ca2c634d08e96361010f23660a4698d02922d4fe66b9529e` |
| `720221207`     | `action-internal__dist__index.js`                                     | `966209`  | `d51f86db69a0000a5b02f0b98e156388d21aaf46caf80bfb348b6a91eef4cd4b` |
| `720221362`     | `action-internal__dist__prepare.js`                                   | `869949`  | `e8a1001fbd2ec0108f4c68951b2571880a6ef19aa616e265081ce236266e3ff2` |
| `720221423`     | `action-internal__dist__sdk-upload.js`                                | `3483773` | `4600a59de2f1c0cbb687abc489cb7e46878bfe96082837496eb28c7a6a2b3ecc` |
| `720221493`     | `action-internal__prepare__action.yml`                                | `322`     | `a8f8943eab8148d41e238ed26eceec8657ed1abd39a69a44e70dc7ca433e9ea0` |
| `720221549`     | `action-payload-map.json`                                             | `3018`    | `eb68da365d47a0a75daa2e001e1348d6c2224b2c7640661ed551e1749838eb5e` |
| `720221668`     | `agentic-pr-review-v0.2.0-internal.2-linux-x64-managed.tar.gz`        | `1911137` | `85315e0c1cf4282bad9828de2e280ff549227c94a61d5aa16b44675fd4f2849d` |
| `720221752`     | `agentic-pr-review-v0.2.0-internal.2-linux-x64-managed.tar.gz.sha256` | `127`     | `688d8feac7c1b2b84b888835c0484af8386a9ec091042560f4acfec6725f7f85` |
| `720221851`     | `candidate.json`                                                      | `6745`    | `eb8e7f24bc381eea8b14caa14709582fc39f5c7b07ef8078a271ee3b3d812b02` |
| `720221945`     | `finalized-pair.json`                                                 | `5000`    | `7f161365f5457a12bd81bc2abf999a59570cdd87540a5d3841c2784f4d808325` |
| `720222098`     | `provenance-bundle.json`                                              | `11183`   | `efcb8e30a8bece2067219c1846230ec1871f3d605a07860ef714cc5567f4add8` |
| `720222205`     | `qualification.zip`                                                   | `41768`   | `6a249bc96d970f6ece2bf65b3149ddc614397560476fb2e67d3d231aac8d7e02` |

The manifest has **14 assets / 7,321,884 bytes**. The first eight rows are the exact Action files at T, under their staging names. The `.tar.gz.sha256` row identifies the original checksum-file bytes; its digest is distinct from B's digest. `qualification.zip` identifies this `.2` candidate's actual qualification, not an older `.1` ZIP or a companion V2 receipt.

## Full Verification And Record Provenance

The staged record is a **SHA-bound exact canonical recovery**, assembled from the actual identities and retained request/descriptor bytes and required to equal the unique staged-record SHA-256 emitted by the successful cloud job. It was not a directly downloaded runner-local record file. The retained canonical bytes and their hash are required evidence for a later separately authorized publication; the hash alone is not a replacement for that record.

The unmodified production `verifyStagedEvidence` subsequently passed independently, using the historical C and original qualified pair. It authenticated the historical failed-parent source and original log, both live raw intents, the new whole-success producer and actual normal approval, integrated full P3/provenance and qualification, the same unpublished draft/descriptor, and all 14 actual downloaded asset bytes and identities. No saved PASS, parser-only result, ordinary CI or V2 support receipt substituted for those gates.

The first local read-only verifier attempt failed with HTTP `415` because its helper applied a release-asset `Accept` header to a raw Actions-artifact endpoint. That negative result was retained. One bounded read-only recovery corrected only that helper's media-header handling and returned native success through the complete production verifier, including integrated P3. This was not a workflow rerun, SDK restart, new candidate qualification or asset repair. Neither the successful completion nor this read-only recovery changes the historical ordinary-stage failure or establishes its cause.

## Retention And Original Window

The original four Actions storage records remained unchanged and unexpired at admission and acceptance. The first three belong to preparation `42155761876` / `1` / W; qualification belongs to `42170346041` / `1` / Q. The preparation transport ZIPs are distinct from original B and from release-asset bytes.

| Role / actual artifact ID   | Transport bytes | SHA-256                                                            | Observed expiry (UTC)  |
| --------------------------- | --------------- | ------------------------------------------------------------------ | ---------------------- |
| Payload `13474437693`       | `1914235`       | `7cfbef35b82176d3a8370e87dda785bcef0989259013365e4966da7ed26e75a1` | `2026-11-13T03:23:25Z` |
| Metadata `13474377959`      | `5768`          | `c2cfd0bfd2013c32103d90d844d873600233934577276231cc153b25ae0bc795` | `2026-11-13T03:23:45Z` |
| Provenance `13474631449`    | `8460`          | `22b80042a0ead0257a3f06c63836415f07da4f1bee1f836f2849d9ee5c1c87b4` | `2026-11-13T03:24:12Z` |
| Qualification `13483914599` | `41768`         | `6a249bc96d970f6ece2bf65b3149ddc614397560476fb2e67d3d231aac8d7e02` | `2026-11-13T07:14:23Z` |

The original route began at `2026-11-06T03:10:06Z`, with a 48-hour handoff target of `2026-11-08T03:10:06Z`, hard `validUntil` of `2026-11-10T03:10:06Z` (Beijing 11:10:06), and latest dispatch of `2026-11-10T02:10:06Z` (Beijing 10:10:06). The one completion request was sent on November 7 at approximately `09:19:39Z`, within that window. Fresh admission retained at least 72 hours on all four actual storage records; even at the fixed hard end the earliest original storage had more than 72 hours left.

Local copies, a staged asset, a new intent or this document do not extend the original window, actual expiry or authority. These observed expiries are not future-availability promises. Later authorized consumers must authenticate the retained canonical records and current storage/expiry themselves; missing, expired or ambiguous required evidence stops that operation.

## Limits And Next Boundary

This result covers only the original `.2` Linux x64 managed pair and the existing approved Solus package/license/use scope, including its bounded qualification profile. It does not add license or redistribution rights, prove a second platform/provider, establish live provider quality, offer production support, or demonstrate downstream adoption.

P9 publication requires its own precise candidate-specific authority and current restrictive verification; it must not rebuild, replace or infer public availability from this unpublished draft. No public publication, provider calls, new credential/protection changes, retry, repair, reupload, owner transfer, new candidate or deadline extension is granted here. Issue closure, final administrative acceptance and overall R7 completion remain separately controlled.

## P9 Public Experimental Release

The same qualified `v0.2.0-internal.2` pair was published on **2026-11-07 at 11:56:26 UTC** as an [immutable public experimental prerelease](https://github.com/SolusQuest/agentic-pr-review/releases/tag/payload-v0.2.0-internal.2), release `436244771`. The earlier sections retain their P8 checkpoint meaning; the public state in this section records the subsequent P9 outcome. The original 14 asset IDs, names, sizes and SHA-256 values in the manifest above are unchanged, totaling **7,321,884 bytes**. The canonical descriptor body remains unchanged.

### Exact Consumer Identity

Use the [managed Action guide](../10_workflow/action-usage.md) and its admitted templates with full Action commit **`cdf77848105faaad0913d9ab6ad4e65b60d5d8fb`** (T):

```text
SolusQuest/agentic-pr-review/.github/actions/agentic-pr-review@cdf77848105faaad0913d9ab6ad4e65b60d5d8fb
```

The consumer tag `v0.2.0-internal.2` resolves to that T, with tree `bfde615707f63baf0bb31e6c69eacd54d299b71f`. The separate payload tag `payload-v0.2.0-internal.2` resolves to source S `75a771908983cfb881a3fac4b2c57a69f53f6a60`. Original builder W, qualifier Q and staging completion C remain as recorded above. Publication controller `95fbfd0634dfa74bb64739957f0c481138a0682c` governs promotion only. Published B and the map at T retain their original identities; publication did not rebuild them.

### Observed Publication And Verification

[Publication run `42369302503`](https://github.com/SolusQuest/agentic-pr-review/actions/runs/42369302503), attempt `1`, and its [promote job `128393982217`](https://github.com/SolusQuest/agentic-pr-review/actions/runs/42369302503/job/128393982217) succeeded after the normal `r7-release` environment review. Exact-candidate public redistribution and publication were separately approved before execution. The protected production path authenticated full staged evidence before its new claim and publication. Fresh unauthenticated public downloads then passed original-byte, Action-map, qualification, provenance, trusted fixed-runtime and native release-integrity verification before the consumer tag was created last. Independent terminal readback also downloaded all 14 assets without authentication and passed pinned `gh` 2.103.3 `release verify` and `verify-asset` against the fresh original B.

| Publication evidence             | Exact value                                                                 |
| -------------------------------- | --------------------------------------------------------------------------- |
| Promotion request SHA-256        | `7ef481eacb5cce9eab5fc91831ff76d50c2fb2fbaa21d3a1edcd1603b6113ed1`          |
| New intent artifact / raw bytes  | `13573630619` / `721`                                                       |
| New intent raw SHA-256           | `20be18d251b441b98de80248b707568e5d3a9d3f725072f23a83398f26054df1`          |
| Published record bytes / SHA-256 | `6310` / `504bdc5ae29c223cf074df5d6f7aa425ec2f81b08a701f49517bb482d047b5ab` |

The retained published record is an exact canonical reconstruction required to match the unique SHA-256 emitted in the original successful cloud log. It was not directly downloaded from the runner. Its intent binds the actual publication producer and the release-publish and consumer-tag-create operations. Repository immutable-release protection was enabled, the published release read back as immutable, and ruleset `26753855` protected the exact consumer tag against update and deletion with no bypass actors. The workflow, logical SDK claim application, publication and tag allowances are consumed. Internal wire-attempt counts remain unknown. All earlier failures and consumed allowances retain their original meaning.

### Experimental Scope And Remaining Limits

The supported environment for this experiment is GitHub.com Actions on GitHub-hosted `ubuntu-24.04` x64 with Node 24. The composite verifies its fixed runtime-only .NET **10.0.13** and the complete managed publish tree. Consumers need no .NET SDK or private package-feed credential. Self-hosted runners, other platforms, full local/offline review and other CI systems remain outside this scope.

The five exact Solus `0.1.0-internal.5` packages use the bounded `cr1-solus-internal4-dev` profile, context format **2 / compatibility 1**. Current maxima are six logical calls, physical attempts and model work units; six tool invocations, three per batch, concurrency one; and 120 seconds of runtime execution. The [capacity owner](../../runtime/src/AgenticPrReview.Runtime/Agent/Loop/capacity.md#v3-managed-review-admission) specifies the separate token, logical/wire-byte and retained-context bounds. Current work does not renew retained history. Unsupported explicit limits and incompatible selected-current state fail visibly; publication adds no conversion, truncation, automatic reset or historical native-capacity equivalence.

This release supplies a downloadable experimental pair. It does not establish downstream installation, live provider quality or cost, production suitability, API stability, compatibility, support duration or an SLA. V4 installation and V5/V6 adoption evidence remain separate, as do target-content consent, provider spending and credentials. Publication does not graduate defaults, close issues automatically or complete R7.
