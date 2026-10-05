# Distribution fixtures

These fixtures use synthetic versions and identities. They select no release candidate and make no GitHub mutations or provider calls.

`npm run check` discovers the package, map and release-resolver Vitest suites. Existing CI runs that command on Linux with Node 24. Run the D2 suites directly with:

```bash
npx vitest run tests/distribution/payload-map.test.ts tests/distribution/release-payload.test.ts
```

The Linux x64 D2 suite compiles `launcher-fixture.c` with `/usr/bin/cc` into a test-owned temporary directory. It drives the production resolver through a local HTTP server, then the existing `runHostProcess` inherited-descriptor consumer. It exercises downloaded-path replacement, failures before launch, cancellation and owned-resource cleanup. The C executable is a framing/identity fixture. D3 separately executes the literal checked Action and actual ordinary Native AOT Runtime; full original-candidate successful-review qualification remains #359. Unsupported platforms run map parsing and explicit platform refusal; they do not claim Linux execution passed. `npm run runtime:integration` remains a separate required runtime regression gate.

## Literal generated Action and ordinary Native Runtime

`node tests/distribution/verify-package.mjs` builds an original synthetic D1 package from clean HEAD, verifies its direct executable behavior, then invokes `verify-generated-action.mjs`. The existing runtime-core AOT CI gate runs this same supervisor and explicitly installs strace. To reuse an already built test-only original package:

```bash
node tests/distribution/verify-generated-action.mjs "$PWD" /absolute/package/archive.tar.gz /absolute/package/receipt.json
```

The supervisor copies literal checked `dist/index.js` and Action metadata into a test-owned ESM installation, projects the original receipt into an adjacent synthetic map and intercepts only built-in HTTPS transport through the repository-only preload. Product logical routes, installed-file Git blob binding, D2 parsing/download/materialization, opened fd launcher, ordinary compiled identity and actual Runtime remain unchanged. The preload observes real file handles, spawn, active launch and framed completion; it substitutes no launch state, Host runner or completion. Cases prove null-map refusal without network, unsupported context/content/archive rejection, acquisition cancellation, SHA/tag payload equivalence, credential-free denial and unsupported-event completion, fixed outputs/summary, SDK/PATH-free native children and payload/bridge disposal before presentation.

For real cancellation, strace delays only openat on the test-owned event path. The supervisor identifies the native child from unchanged spawn and cross-checks the trace thread's Tgid before signalling the real wrapper. It requires actual forwarded SIGTERM, an admitted cancelled completion, bounded native exit and cleanup/presentation. The syscall delay is test orchestration; neither it nor transport interception is selectable by Action inputs or the ordinary environment contract.

Runner-owned GITHUB_ACTION_REPOSITORY/GITHUB_ACTION_REF provide the fixed repository and exact SHA or mapped version tag. T for a tag is its resolved commit under the required later R7 immutable consumer-tag/protection publication invariant. Installed-file comparison detects content mismatch; it does not expose the runner's download SHA or distinguish identical-content tag retarget. Actual tag/protection provisioning and public-route qualification remain later authorized publication gates. The checked payload-map.json is explicitly null until a separately authorized binding; no fixture creates a real map or release.

Retained r4-w2 tests and native proof supervisors execute a separately generated fixed private proof bundle. Current canonical receipts fingerprint the actual executed file; current-source readback regenerates the expected proof bytes. Sealed historical verification remains dependency-free. The existing protected workflow's current preparation validates separate clean source/control commits and credentials, then installs locked dependencies and overlays/readbacks proof bytes only in its temporary control checkout before the unchanged local uses step. Its seven preparation outputs, profiles, strict Host receipts and policy/template anchors are preserved.

The fake transport substitutes only the socket destination. The resolver still evaluates logical production HTTPS URLs, exact release/tag/asset selection, headers and redirect policy. The ordinary `resolveReleasePayload` entry has no endpoint, credential, environment or local-path option. `createRepositoryTestPayloadResolver` is an explicit repository-test seam; even its local archive option passes the same map, platform, D1 package and opened-file admission.

## Ordinary Action map format 1

The map comes only from trusted Action-owned bytes. It is UTF-8 JSON of 1–16,384 bytes, with closed records, no BOM or duplicate keys, and safe integer numbers. No real map is installed by this issue. `release-payload-fixture.ts` contains a representative producer projection with distinct synthetic payload source S, builder W and separately supplied Action T.

| Record           | Exact keys and constraints                                                                                                                                     |
| ---------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Root             | `formatVersion` = 1, `repository` = `SolusQuest/agentic-pr-review`, `tag` = `payload-` + payload release version, `builder`, `payload`                         |
| Builder W        | `repository` equals root; `workflowPath` matches `^\.github/workflows/[A-Za-z0-9][A-Za-z0-9_-]{0,79}\.ya?ml$`; `workflowCommit` is 40 lowercase hex characters |
| Payload          | Exact original D1 receipt: `formatVersion`, `archiveName`, `archiveSize`, `archiveSha256`, `identity`, `members`                                               |
| Payload identity | `releaseVersion`, `platform`, `sourceCommit`, `sourceTree`, `buildId`, `informationalVersion`                                                                  |
| Each member      | `path`, `mode`, `size`, `sha256`, in the exact D1 three-member order                                                                                           |

The payload receipt retains all D1 invariants: format 1, canonical `vMAJOR.MINOR.PATCH-internal.N` (no leading zeros, positive N, at most 48 ASCII characters), `linux-x64`, exact generated archive name, positive archive size at most 32 MiB, archive SHA-256, source/tree 40-hex identities and 64-hex build ID. Informational version is exactly `releaseVersion + '+build.' + buildId`. Members are the executable, manifest and notices at their D1 fixed paths with modes 0755/0644/0644, nonempty sizes capped at 64 MiB/64 KiB/1 MiB, and lowercase SHA-256 digests. The unchanged D1 inspector validates gzip/USTAR grammar, total expansion, actual member bytes, manifest/ELF and build preimage before materialization.

Producer #349 projects its original D1 receipt into `payload` and its actual workflow identity into `builder`. The candidate envelope separately holds producer run/attempt/artifact coordinates and the digest of the proposed map bytes; those fields are not accepted as alternate map keys. #351 uses the same consumer shape. T is separate trusted launcher context, never inferred from S/W or included in a self-referential map. Recording W does not perform online attestation verification.

## Acquisition and lifetime

The resolver uses fixed public GitHub REST routes with a fixed User-Agent, then the exact `github.com/<repository>/releases/download/<tag>/<asset>` URL. Release metadata only locates bytes; the map authenticates them. The separate asset-list request rejects pagination and full 100-entry pages, so incomplete enumeration never establishes uniqueness. Metadata is bounded to 256 KiB, the archive to its exact map size, acquisition to 60 seconds, and asset redirects to three. API redirects are rejected. Asset redirects are restricted to HTTPS `release-assets.githubusercontent.com` without credentials, fragments or nonstandard ports; no hop sends authorization or ambient credentials. Route drift fails closed and needs later actual public-route qualification.

Pass a trusted temporary parent outside the reviewed workspace. Successful resolution returns an open verified executable handle, full payload/builder identity, an owned private root and idempotent `dispose()`. Consumers must await disposal in `finally` on success, failure, cancellation and fatal unconfirmed termination; it closes the handle and attempts root removal. Cleanup errors propagate and must never be labeled successful cleanup. Spawn only through `runHostProcess` with the inherited descriptor, never by reopening the extracted pathname. Acquisition failures clean up their own resources before rejecting.

The D1 `build-payload.test.ts` and `verify-package.mjs` remain the package-format and original .NET package proof owners. D2 adds representative hostile archives at the HTTP-to-launch boundary rather than duplicating the complete D1 mutation corpus.
