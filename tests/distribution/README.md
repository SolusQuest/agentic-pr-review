# Distribution fixtures

These fixtures use synthetic versions and identities. They select no release candidate and make no GitHub mutations or provider calls.

`npm run check` discovers the package, map and release-resolver Vitest suites. Existing CI runs that command on Linux with Node 24. Run the D2 suites directly with:

```bash
npx vitest run tests/distribution/payload-map.test.ts tests/distribution/release-payload.test.ts
```

The Linux x64 suite compiles `launcher-fixture.c` with `/usr/bin/cc` into a test-owned temporary directory. It drives the production resolver through a local HTTP server, then the existing `runHostProcess` inherited-descriptor consumer. It exercises downloaded-path replacement, failures before launch, cancellation and owned-resource cleanup. The C executable is a framing/identity fixture, not the production .NET payload; generated Action integration belongs to #344 and full original-candidate qualification to #359. Unsupported platforms run map parsing and explicit platform refusal; they do not claim the Linux execution tests passed. `npm run runtime:integration` is a separate required runtime regression gate.

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
