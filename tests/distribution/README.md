# Distribution fixtures

These fixtures use synthetic versions and identities. They select no release candidate and make no GitHub mutations or provider calls.

`npm run check` discovers the package, map and release-resolver Vitest suites. Existing CI runs that command on Linux with Node 24. Run the D2 suites directly with:

```bash
npx vitest run tests/distribution/payload-map.test.ts tests/distribution/release-payload.test.ts
```

The Linux x64 D2 suite compiles `launcher-fixture.c` with `/usr/bin/cc` into a test-owned temporary directory. It drives the production resolver through a local HTTP server, then the existing `runHostProcess` inherited-descriptor consumer. It exercises downloaded-path replacement, failures before launch, cancellation and owned-resource cleanup. The C executable is a framing/identity fixture. D3 separately proves actual managed APR loading; D4 executes the literal checked managed Action; full original-candidate successful-review qualification remains #359. Unsupported platforms run map parsing and explicit platform refusal; they do not claim Linux execution passed. `npm run runtime:integration` remains a separate required runtime regression gate.

## Literal generated managed Action

D6 removes the obsolete native `verify-package.mjs` and `verify-generated-action.mjs` supervisors. Their replacement is actual managed archive and generated Action qualification below. Explicit historical native fixture codecs and resolver seams retain hostile-archive/handle regression coverage; they are not production entrypoints.

The trusted `verify` and `generated-action-tail` routes execute the `CR1 actual generated managed Action and official bridge proof` step, which runs `node tests/distribution/verify-managed-action.mjs`. It uses only the previously verified package cache, builds the exact clean-head managed APR archive, downloads the original pinned official runtime bytes without credentials, and runs the literal checked bundle through `verify-managed-generated-action.mjs`. To reuse those already built test-owned bytes:

```bash
node tests/distribution/verify-managed-generated-action.mjs "$PWD" /absolute/managed/archive.tar.gz /absolute/managed/receipt.json /absolute/runtime.tar.gz
```

The finite `generated-action-tail` mode retains the same exact reviewed SHA,
workflow SHA, clean-source, read-only permissions and private package guards.
Its cache preparation maps to the existing `verify` startup/lock/negative-control
command: this produces real `mode=verify` and `constructed=true` metadata required
by the capacity fixture. It does not relabel preparatory metadata. That startup
command does not invoke the dual original archive harness.

The mode runs the existing generated-Action entry and all remaining state,
provider/accounting, context, tool, fresh/reset/restored Host, evaluation,
economics and capacity proofs. Their commands and child budgets are unchanged.
It skips the network/selection prerequisite, dual original archive, inventory,
release-staging and loader steps. The original `verify`, `collect` and
`network-prerequisite` selections remain unchanged; the job retains its existing
360-minute default limit. This tail mode does not create new-head dual original
archive receipts or relabel earlier source evidence.

The supervisor copies the unchanged checked bundle and Action metadata into a test installation and projects only an adjacent synthetic managed map. Logical release routes, installed-file Git blob binding, real D1/D2 managed admission, production runtime provisioning, D3 descriptor/configuration guards and actual framed APR Host remain active. The preload observes real handles, child spawn and completion, substitutes the credential-free download socket destination and holds the real Host's unchanged launch frame only during a bridge probe. Explicit negative controls also remove a runtime prerequisite, mutate a verified nonentry file, or direct the Host exec attempt to an absent executable; the observer records these effective faults and actual OS failure. It never manufactures a successful completion, tree lease or Host runner.

The probe connects to that same generated process's actual private upload endpoint. It sends the current flat `apr.sdk.upload.cr2` request as exactly four length bytes plus JSON, then closes the write half before reading the flat reply. Wrong build/protocol, retired operation fields, malformed/trailing/second frames and replay cannot dispatch an SDK request. One valid upload traverses the real isolated SDK child and `DefaultArtifactClient` against bounded localhost Results/blob responses. Its receipt's artifact ID, archive digest and size must match the service's original SDK ZIP bytes; the observer requires actual SDK child spawn and close. The generated child environment uses only synthetic tokens/endpoints; hosted job credentials and proxy variables are not inherited. Unsupported-event completion and missing-credential failure are loading/adoption proofs, not a successful Solus review.

The former six-operation Node probe has explicit current owners. It does not restore read/delete authority to Node:

| Former probe assertion                                         | Current owner and retained assertion                                                                                                                                                                                                                                                                                                                                            |
| -------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Wrong discriminator / malformed request does no official work  | `generated-upload-probe.mjs` checks closed upload refusals, invalid frame closure and unchanged official request count before its valid upload.                                                                                                                                                                                                                                 |
| Immutable upload succeeds and identifies original stored bytes | Current generated probe checks flat dispatch/receipt against the original SDK archive snapshot and actual child close. `GitHubArtifactRestConformanceTests.SharedConformanceTraversesCSharpRestAndRealPrivateSdkUploadIncludingMaximumCarrier` also traverses the coupled C# store and real private SDK.                                                                        |
| Complete exact list contains the uploaded reference            | That C# conformance method invokes `RestrictedStateStoreConformanceHarness.VerifyAsync`: complete single-object list equals the uploaded reference, duplicate/incomplete census fails. `CompleteRepositoryCensusIncludesLaterPages_APV026_027` retains pagination.                                                                                                              |
| Metadata equals uploaded metadata and enforces bounds          | The same C# harness checks equality and size limits; `MalformedNumericDescriptorFailsReadsAndRefusesDeleteBeforeDispatch` retains fail-closed reads.                                                                                                                                                                                                                            |
| Exact readback equals expected metadata/lineage                | The C# harness checks persisted readback and refuses wrong run, attempt and archive digest. `ValidSdkReceiptCannotCommitWithoutIndependentMatchingRestReadback` preserves independent observation.                                                                                                                                                                              |
| Download restores exact encrypted carrier bytes                | The C# harness checks byte equality, digest, expiry and maximum carrier; `ArtifactTransportFormatTests` retains stored/deflated ZIP, hash and expansion controls. Actual original archive fresh/restored/corrupt-context scenarios traverse that C# REST/download path against original SDK ZIP bytes.                                                                          |
| Exact delete succeeds and subsequent observation is absent     | The C# harness deletes the maximum object then requires missing metadata; `DeleteReservesExpectedReadMutationAndFreshAbsenceBeforeAnyDelete` and `LostDeleteAcknowledgementIsUnknownWithoutReplayOrCachedAbsence` retain mutation/absence/uncertainty rules. Actual original archive state/reset/recovery scenarios retain real SDK/REST cleanup and no duplicate sticky write. |

These C# owners are under `runtime/tests/AgenticPrReview.Runtime.Tests/Host/State/`, with transport tests in its `GitHubArtifacts/` directory. Both independently built original archive roles still require every one of their 23 scenarios. A result at an older source remains evidence for that source, never an exact-head receipt for later fixture edits.

The generated supervisor retains all later cancellation, runner-temp and hostile-temp cases. It observes wrapper close and process-group disappearance within a bounded close window before allowing cleanup. Only a canonical owned `/tmp/apr-d4-*` root can use the shared bounded trusted teardown; sealed product modes remain unchanged. A secondary cleanup failure cannot replace the primary case error. Unknown group/service close keeps the root and fails. The final generated-Action receipt is printed only after trusted cleanup succeeds.

Acquisition rejection, fixed runtime digest rejection, partial-download cancellation, real Host cancellation, fourteen completion outputs, ordinary fixed failure outputs and resource cleanup before presentation remain separate assertions. Private runner namespaces stay outside the workspace; hostile generic temporary-directory variables cannot select payload, runtime or bridge storage. The real cancellation supervisor uses strace's bounded delay on the test-owned event file, confirms the child's Tgid, and signals the wrapper. These controls are repository test orchestration, never Action inputs.

Runner-owned GITHUB_ACTION_REPOSITORY/GITHUB_ACTION_REF provide the fixed repository and exact SHA or mapped version tag. T for a tag is its resolved commit under the required later R7 immutable consumer-tag/protection publication invariant. Installed-file comparison detects content mismatch; it does not expose the runner's download SHA or distinguish identical-content tag retarget. Actual tag/protection provisioning and public-route qualification remain later authorized publication gates. The checked payload-map.json is explicitly null until a separately authorized binding; no fixture creates a real map or release.

Retained r4-w2 tests and native proof supervisors execute a separately generated fixed private proof bundle. Current canonical receipts fingerprint the actual executed file; current-source readback regenerates the expected proof bytes. Sealed historical verification remains dependency-free. The existing protected workflow's current preparation validates separate clean source/control commits and credentials, then installs locked dependencies and overlays/readbacks proof bytes only in its temporary control checkout before the unchanged local uses step. Its seven preparation outputs, profiles, strict Host receipts and policy/template anchors are preserved.

The fake transport substitutes only the socket destination. The resolver still evaluates logical production HTTPS URLs, exact release/tag/asset selection, headers and redirect policy. The ordinary `resolveManagedReleasePayload` entry has no endpoint, credential, environment or local-path option. `createHistoricalNativePayloadResolverForFixture` is the explicitly historical native test seam; its local archive option still passes historical map, package and opened-file admission.

## Ordinary managed Action map

The trusted Action-owned map is bounded to 256 KiB and contains only `formatVersion`, fixed `repository`, `tag`, `builder` and `payload`. The payload must be a complete `managed-v1` receipt, with its entrypoint and every member, original compressed length/hash and source/tree/build identity. The managed inspector enforces the 64 MiB compressed, 128 MiB expanded, 512-member archive bounds and exact inventory. Current parsing rejects historical native receipts. No real map is installed by these fixtures.

Builder W retains its repository, workflow path and 40-hex workflow commit. Source S, builder W and trusted Action T remain independent. Candidate metadata separately owns producer run/attempt/artifact and proposed-map digest; it is not an alternate consumer-map shape. W alone does not establish online attestation. `shared-map.fixture.json` stays immutable historical evidence and is rejected by the current managed parser.

## Acquisition and lifetime

The resolver uses fixed public GitHub REST routes with a fixed User-Agent, then the exact `github.com/<repository>/releases/download/<tag>/<asset>` URL. Release metadata only locates bytes; the map authenticates them. The separate asset-list request rejects pagination and full 100-entry pages, so incomplete enumeration never establishes uniqueness. Metadata is bounded to 256 KiB, the archive to its exact map size, acquisition to 60 seconds, and asset redirects to three. API redirects are rejected. Asset redirects are restricted to HTTPS `release-assets.githubusercontent.com` without credentials, fragments or nonstandard ports; no hop sends authorization or ambient credentials. Route drift fails closed and needs later actual public-route qualification.

Pass a trusted temporary parent outside the reviewed workspace. Successful managed resolution returns a verified full-tree lease, full payload/builder identity, an owned private root and idempotent `dispose()`. Consumers must await disposal in `finally` on success, failure, cancellation and fatal unconfirmed termination; it closes held handles and attempts root removal. Cleanup errors propagate and must never be labeled successful cleanup. Spawn only through the managed host runner with its trusted runtime FD3 and held tree FD4, never through ambient PATH or a reopened extracted entrypoint. Acquisition failures clean up their own resources before rejecting.

The `build-payload.test.ts` owns both current managed format checks and explicitly historical native codec regressions. Actual production archive execution belongs to the trusted qualification harness. D2 adds representative hostile archives at the HTTP-to-launch boundary rather than duplicating the complete D1 mutation corpus.

## CR1 managed loading and lifetime

`managed-host-process.test.ts` consumes authentic D2 leases through the separate production managed runner. Ordinary tests use a deterministic child seam for finite cancellation, unconfirmed/late close, failed start, configuration parsing and resource cleanup. The `APR_CR1_MANAGED_LOADER_PROOF=1` flag also builds the real `managed-loader/` C# entry/dependency/satellite and native fixture outside the source checkout, using the pinned SDK only for construction. Its consumer receives a separate trusted runtime-only root, fixed FD3/FD4 arguments and a closed environment. Effective malicious controls precede negative loading assertions; no model/provider or real GitHub service is called.

The ordinary runtime-core loader step requires this actual fixture on Ubuntu 24.04 x64. `APR_CR1_MANAGED_APR_LOADER_PROOF=1` additionally builds the exact clean-head APR managed archive and executes its actual framed Host through the explicit `run` command with a synthetic unsupported event and live caller-owned artifact bridge. This flag belongs to the trusted `solus-agent-package-proof.yml` verify step with its previously verified `NUGET_PACKAGES` cache; it must not restore a private feed in ordinary PR/fork CI. Package credentials do not enter transport or runtime children. All task-owned resources stay available until actual child close, including on failed tests. These are D3 loading proofs; D4 adoption and D5 representative Solus review remain separate.

## Candidate and retirement evidence

`tests/release/verify-candidate.mjs` executes a public FDD loader plus a legitimate native dependency asset through the current candidate codec. Its source/build/remote metadata is explicitly a supporting fixture, not production Solus provenance. The actual archive harness additionally invokes the same pure `tests/release/verify-managed-candidate-archive.mjs` validator on each independently built original production archive and receipt. Positive candidate acceptance and identity/byte/hash/member/extra-field/size negatives are checked without rewriting B or contacting a service.

See the [D6 named dispositions](../../docs/20_architecture/distribution.md#cr1-d6-retirement-and-retained-evidence) for retired producers and retained historical AOT/protected proof consumers. No fixture prepares, signs, binds or publishes a real R7 candidate.
