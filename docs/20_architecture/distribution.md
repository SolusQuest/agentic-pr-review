# Distribution Direction

The development head now contains the replacement nested Action metadata and reproducibly generated Node 24 wrapper for repository-controlled proof with an explicitly prepared payload. It is not a supported downstream Action: release assets, automatic payload download and accounting outputs are not implemented at this baseline. The approved [R7 target](../90_roadmap/r7-plan.md) adds public experimental distribution and bounded maintainer adoption; it excludes a root alias and defers formal public/default graduation.

See [`agent-runtime-rebaseline.md`](./agent-runtime-rebaseline.md) for component ownership and migration sequencing, [`r4-actionhost-wrapper-plan.md`](./r4-actionhost-wrapper-plan.md) for the activated R4 product contract, [`deepseek-flash-identity.md`](./deepseek-flash-identity.md) for the selected-current R7 provider identity and state boundary, and [`r4-migration-cutover-handoff.md`](./r4-migration-cutover-handoff.md) for the closed source inventory and exact-tree proof gate.

## Distribution Goals

The distribution must:

- run without a preinstalled .NET SDK on downstream runners;
- select exact payload bytes;
- fail closed on checksum or compatibility mismatch;
- start quickly enough for GitHub Actions use;
- keep the Node wrapper reproducible and small;
- keep the first .NET payload to one executable and one build identity;
- avoid dynamic `latest` selection;
- allow framework-dependent execution for development and tests without making it the downstream default.

## Target Node.js Action Wrapper

The checked-in nested Action metadata and generated Node 24 wrapper are the R4 repository-controlled prepared-payload proof entrypoint. They are not a supported downstream Action, root alias, release-download path, or stable output surface.

The wrapper may:

- read Action inputs;
- register secret masks;
- resolve or download a pinned payload;
- launch the .NET application;
- forward cancellation;
- bridge the official Actions artifact client;
- render bounded Host-approved annotations and step summary; the current R4 proof has no Action outputs, while R7 targets matching [accounting outputs](../90_roadmap/r7-plan.md#summary-and-machine-readable-outputs);
- forward the host exit code.

Business behavior does not belong in the distribution wrapper.

The wrapper and Host still require one minimal lockstep process contract for cancellation, bounded outcome/output handoff, exit status, and executable build identity. This is an atomically maintained launcher seam, not a review-domain cross-language protocol or public compatibility matrix.

Run:

```bash
npm run dist:check
```

This read-only command verifies the exact metadata surface, package and lockfile ownership, generated-wrapper source/import inventory, byte-for-byte checked bundle, and retired root-alias/local-runner/workflow-invocation guards. It is the sole owner of the derived bundle's exact bytes and bounded source graph, so the repository residual-reference audit excludes that one generated file while continuing to fail closed for every other tracked path. Run `npm run build:action` only when intentionally regenerating the checked bundle.

## .NET Payload

Native AOT remains the selected production distribution form.

The target payload contains:

- one `agentic-pr-review` .NET executable containing Host, Agent, tools, provider adapters, state, and publisher modules;
- shared runtime libraries required by that executable;
- a payload manifest identifying release, platform, files, sizes, and checksums.

The first target does not maintain separate Host and Worker executables or an internal version matrix. If later evidence requires kill, resource, extension, or trust isolation, a same-binary `worker` subcommand should be evaluated before adding a second artifact.

Native AOT provides:

- no downstream .NET SDK requirement;
- fast startup;
- self-contained execution;
- exact platform-specific assets;
- checksum-verifiable files;
- earlier discovery of reflection and trimming incompatibilities.

Framework-dependent and selected Native AOT paths continue to run in CI. A successful publish is not sufficient; CI must execute representative Host, Agent, fake-provider, tool-loop, and publisher paths from the published output.

## Initial Platform Scope

The approved R7 experimental payload target is one `linux-x64` executable for GitHub-hosted `ubuntu-24.04`, consumed through the existing nested Node 24 Action without a .NET SDK. Other Linux/self-hosted environments are outside the initial support claim. Windows development is not a distributed target.

Additional targets are outside R7 and require separately scoped downstream demand and CI publish/execution proof:

- `linux-arm64`;
- `win-x64`;
- `osx-x64`;
- `osx-arm64`.

Platform expansion must not delay the first agent vertical slice.

## Payload Resolution

The selected R7 ordinary downstream path downloads only the exact release asset authorized by the small strict map committed with the trusted Action. There is no public local-path/bundled-payload fallback, arbitrary URL or implicit latest selection. This is an approved target; current R4 proof still uses an explicitly prepared trusted payload.

Fully verified repository-test overrides remain isolated and unreachable through ordinary downstream inputs. They bypass download, not executable/platform/manifest/build admission, and do not expand downstream support. Other distribution forms require a separate future contract.

The wrapper must not:

- search `PATH` for an arbitrary runtime;
- download implicit `latest`;
- accept an unverified mutable URL;
- choose a runtime version unrelated to the Action release;
- run a payload from the reviewed pull request workspace.

Local override paths bypass download but not executable, platform, payload-manifest, or exact build validation.

R4 proves the thin wrapper and two-run workflow behavior using an explicitly prepared trusted local payload path. The wrapper holds the verified executable's opened identity through Linux process creation and executes that inherited descriptor, so replacing the admitted pathname after hashing cannot substitute another executable. Automatic release-asset download, exact default Action-to-payload resolution, and public release mapping belong to R7.

## Version Mapping

The R7 public experimental channel is `vMAJOR.MINOR.PATCH-internal.N` (`N >= 1`); `internal` describes maturity, not visibility. Fixed published bytes remain immutable, without stable API/accuracy/compatibility/SLA promises. The first exact version is a later execution input.

```text
payload-vX.Y.Z-internal.N at source S -> original prepared archive B
vX.Y.Z-internal.N at reviewed Action T -> exact map of B, S and builder W
```

`S != T`; builder/workflow identity `W` may differ from `S`. The strict map binds one version/platform, exact asset repository/tag/name, archive size/SHA-256, internal file inventory/sizes/hashes and build identity. The final pair also records original producer run/artifact/digest. Neither binary self-hash nor self-source cycles are introduced. Internal modules have no independent version matrix.

Prepare/test original `B` from `S` without publication credentials; review the binding map and obtain maintainer merge to `T`; qualify exact original `B` with `T`; stage that qualified pair as an unpublished draft; publish and verify payload assets, then create the consumer Action tag last. A fixed Action SHA resolves the same map. Promotion never rebuilds/repackages. Changed build inputs need a new candidate; expiry or ambiguous external state fails closed/reconciles by exact identity, never duplicates/replaces a version.

Mandatory producer attestation and final publish verification restrict expected repository, signer workflow, hosted runner and actual source claims. Attestation of `W` alone cannot prove detached `S` without independently verified candidate/build-policy binding. Consumers must verify SHA-256 against the reviewed map and retain opened-executable identity through spawn. Mandatory online consumer attestation is deferred; independent verifier commands belong to the later runbook. See the full [release identity and authority contract](../90_roadmap/r7-plan.md#exact-release-identities-and-trust); this activation creates no release or tag.

## Durable State Compatibility

The payload version and state format are different identities:

- payload version identifies executable behavior;
- state format identifies durable cross-run bytes;
- provider/model/policy/toolset/cache identities determine whether state is semantically reusable.

Use canonical content digests for policy, instructions, ordered tools, and cache-relevant configuration, plus resolved provider/model and runtime build identity. Do not add parallel manual version and id fields for the same content.

Before 1.0, a Host-classified non-current historical namespace or discriminator may cause observable safe bootstrap. A selected-current record that is malformed, unauthenticated, scope-incompatible, continuation-invalid, missing required data, ambiguous, or ancestry-invalid fails closed even when discovered automatically; an explicitly supplied incompatible artifact also fails closed. Release assets do not need to carry converters for unused historical state.

## Restricted State Transport

The Agent session has project-owned logical records plus a separate provider-scoped continuation envelope. Because it may contain readable reasoning and bounded repository tool results, it is more sensitive than the existing M4 metadata-oriented state. R4 selects downstream-owned GitHub Actions artifacts as the sole built-in production/default adapter.

Production transport must document:

- platform visibility and authorized decryption/mutation principals;
- read, write, enumeration, restore, deletion, and publication authority;
- retention and deletion behavior;
- fork and untrusted-workflow key, mutation, provider, and publication denial;
- confidentiality-at-rest and encryption requirements;
- authenticated binding to the current-format discriminator, Host-authoritative state identity, provider/adapter scope, session identity, and required generation/provenance.

Plaintext reasoning, repository tool results, and provider continuation material must not enter Git objects, Git history, repository-visible state refs, caches, or unencrypted public artifacts. The selected artifact adapter stores only authenticated ciphertext and bounded encrypted receipts. In a public repository, opaque artifact names, metadata, provenance, digests, and encrypted bytes may be public. The downstream-owned 256-bit state key remains separate from the payload and provider key and is never exposed to the Agent or model. Without that key path, readable continuation content is not persisted.

Encrypted payloads provide authenticated confidentiality or equivalent independent integrity protection. The key or key identifier is not payload authority. Authentication failure, identity mismatch, cross-scope substitution, stale replay, or decryption failure is handled before content reaches the Agent or provider.

R2 defines a storage-conformance interface and negative matrix. R4 evolves it into an asynchronous internal opaque-snapshot seam shared by the local and GitHub artifact adapters. `RestrictedStateService` owns authorization, encryption, SESSION admission, scope, lineage, retention, and transitions; adapters own bounded opaque list/download/immutable-upload/readback/delete outcomes. R4 does not expose a dynamic .NET plugin ABI or external adapter protocol. A maintained Supabase/Postgres adapter is a future candidate after a separate transactional, RLS, authentication, and operations proof.

R4 proves that public observers can see only allowed metadata and ciphertext, while untrusted and fork-origin workflows cannot obtain the state key, decrypt or admit SESSION, create or delete trusted state, replace or accept lineage, call the provider through the trusted route, or publish. Authorization rejection occurs before key resolution, state decryption, provider construction, provider network activity, or publication.

## Release Assets

The following are historical/future naming examples, not the R7 selected asset set or an available download. R7 selects only `linux-x64`; its package owner defines the exact layout/name within the reviewed-map contract before publication. Platform expansion requires separate scope.

Examples:

- `agentic-pr-review-vX.Y.Z-linux-x64.tar.gz`;
- `agentic-pr-review-vX.Y.Z-linux-arm64.tar.gz`;
- `agentic-pr-review-vX.Y.Z-win-x64.zip`;
- `agentic-pr-review-vX.Y.Z-osx-x64.tar.gz`;
- `agentic-pr-review-vX.Y.Z-osx-arm64.tar.gz`;
- `SHA256SUMS`;
- a machine-readable payload manifest.

An archive should contain a fixed top-level layout such as:

```text
agentic-pr-review/
  manifest.json
  agentic-pr-review
  THIRD-PARTY-NOTICES.txt
```

The example above chooses no actual first candidate or version. The R7-D1 package format 1 section below defines the implemented layout; later map and qualification owners bind it before publication.

## Dependency Policy

- If R2 adopts `Microsoft.Extensions.AI`, pin it and provider dependencies to exact stable versions through repository package management.
- Do not consume preview packages in the production payload without an explicit architecture exception.
- Run framework-dependent and Native AOT validation on dependency updates.
- Preserve source-generated JSON and reflection-disabled execution.
- Treat provider SDK request-shape changes as cache and compatibility risks.

R2 compares `Microsoft.Extensions.AI.Abstractions` with project-minimal exchange types. Either choice keeps the provider adapter and durable/side-effect semantics project-owned.

## Secret And Source Boundary

The distribution wrapper and host must not launch a payload from untrusted PR code when provider or GitHub secrets are present.

Trusted workflows should:

- check out the Action/runtime from a trusted default-branch or immutable release ref;
- resolve configuration and instructions from an immutable Host-selected workflow-authorized commit SHA, normally on the default-branch lineage, and bind the selected bytes and source identity into session/cache identity;
- review a separate target snapshot without executing it;
- mask secrets before launch;
- avoid forwarding secrets or environment dictionaries into Agent-visible objects;
- use separate GitHub and provider HTTP clients, handlers, endpoints, and authorization headers;
- exercise secret-canary and captured-provider-request tests;
- exercise an integrated Action canary using GitHub/Actions-shaped Host credentials and prove authorization rejection precedes decryption, provider construction, provider network activity, and publication;
- never place provider or GitHub credentials in payload files or job JSON.

## Migration From The Current Distribution

During migration:

- the existing `v0.1.0` tag remains the unmaintained historical pin for the last tagged legacy Action; no new tag promotes later unshipped legacy work;
- R1 removes the current mixed Action metadata and generated bundle from the development head instead of publishing a partially migrated compatibility Action;
- R2 and R3 may leave the development head without a supported public Action while the C# Agent path is proven through direct test and trusted workflow entrypoints;
- R4 introduces the new thin bundled wrapper only after its C# Host target and small public configuration surface are defined, and proves two independent workflow runs against an explicitly prepared trusted payload;
- R7 adds release assets, checksums, exact automatic payload resolution, and release-owned download behavior;
- new business logic moves toward C# Host, Agent, tool, provider, state, and publisher modules;
- legacy Claude installation and runtime selection are removed;
- old release tags remain unchanged;
- the first post-removal release documents state reset and input changes.

Do not keep two permanent distribution architectures. Every transitional TypeScript business module must have a target owner and deletion gate.

## R7-D1 package format 1

The producer scripts/release/build-payload.mjs owns the first package format. The archive name is agentic-pr-review-<releaseVersion>-linux-x64.tar.gz, where releaseVersion is canonical vMAJOR.MINOR.PATCH-internal.N, without leading zeroes, with positive N and at most 48 ASCII characters. These tools and fixtures select no real candidate version.

One normalized gzip stream contains a USTAR archive with exactly these regular files in this order:

| Member                                    | Mode | Maximum bytes |
| ----------------------------------------- | ---- | ------------- |
| agentic-pr-review/agentic-pr-review       | 0755 | 64 MiB        |
| agentic-pr-review/manifest.json           | 0644 | 64 KiB        |
| agentic-pr-review/THIRD-PARTY-NOTICES.txt | 0644 | 1 MiB         |

All members are nonempty. The compressed archive is at most 32 MiB and total expansion at most 66 MiB. Gzip metadata is fixed, one raw deflate stream is followed by its CRC32 and length, and no concatenated stream or trailing bytes are accepted. Tar uid, gid, timestamps, owner names, links, prefix, device fields and reserved bytes are zero; names, modes, checksums, octal sizes, magic/version, member order, zero padding and exactly two zero end blocks are canonical. No directories, alternate spellings, extensions, symlinks, hardlinks or additional members are accepted.

The UTF-8 manifest is sorted-key canonical JSON without whitespace or duplicate keys. Its closed fields are formatVersion (1), releaseVersion, platform (linux-x64), exact 40-hex sourceCommit and sourceTree, 64-hex buildId, informationalVersion, launcher (r7-d0), buildInputs, nativeDependencies, and two members entries for the executable/notices. Each member entry has only path, mode, size and sha256. Build inputs bind the source/tree/version/platform, policy/lock/notices SHA-256, actual toolchain and exact dependency inventory. Dependency entries contain id, version, raw nupkg SHA-256, NuGet signature-excluding contentHash, and managed/native-runtime/build role. Toolchain entries record selected SDK/compiler/linker and driver hashes, Node/zlib, Ubuntu identity and bounded system-package versions. Native dependencies distinguish ELF NEEDED from runtime-loaded ICU/OpenSSL requirements.

buildId = SHA256(canonicalJson(buildInputs)) is computed before compilation. The compiled assembly informational version is exactly releaseVersion + "+build." + buildId, at most 119 characters under the retained 120-character runtime result/trace contracts. Source/tree are already committed by buildId and are not redundantly appended. Ordinary development and historical proof builds keep 0.1.0-dev; r7-d0 is a launcher discriminator, not another semantic version.

The external receipt.json has only formatVersion, archiveName, archiveSize, archiveSha256, identity (the six release/platform/source/tree/build/informational fields) and all three inspected member entries, including the manifest's own size/hash. The archive digest is never put inside its content, and the manifest does not hash itself. A local producer-written receipt is build evidence, not authentication. Inspection requires an independently trusted receipt; #343's future reviewed release map owns consumer expectations and acquisition.

From a clean checkout, build with explicit committed source S and the chosen version:

    node scripts/release/build-payload.mjs build --source <40-hex-S> --version <releaseVersion> --output <new-directory>

Inspect using a receipt obtained from an independent trusted source:

    node scripts/release/build-payload.mjs inspect --archive <archive-file> --receipt <trusted-receipt-file>

Run deterministic structural/identity/source-admission tests:

    npx vitest run tests/distribution/build-payload.test.ts

Run credential-free synthetic package build/extract/actual-executable proof:

    node tests/distribution/verify-package.mjs

Run the full retained runtime gates, including package proof inside the AOT subcommand:

    npm run runtime:verify

The builder supports Ubuntu 24.04 x64, Node 24, exact selected .NET SDK 10.0.109 and clang 18.1.3, with binutils and the existing Native AOT prerequisites (clang and zlib1g-dev). Its native child requires no downstream SDK, framework installation, PATH or DOTNET_ROOT. Ubuntu still supplies glibc, libgcc, zlib, ICU 74 and OpenSSL 3; the build records installed versions and actual ELF dependencies. This does not claim compatibility with arbitrary Linux images or an entirely static executable.

The producer requires clean HEAD == explicit S before and after building. It validates regular Git objects in the admitted closure and creates a fresh Git-object snapshot of global.json, runtime, scripts/release and protocol/schemas. Ignored checkout bin/obj files are excluded. Compilation uses fresh publish/intermediate/private NuGet/home directories, explicit package-only props/config and disabled ambient parent/user MSBuild imports. The packages.release.lock.json file applies only to opted-in package builds. Restore and publish both receive explicit publish/Native AOT runtime-pack context; .NET 10 splits Microsoft.NETCore.App.Runtime.NativeAOT.linux-x64 from the compiler tools, so a plain standalone restore is insufficient. The actual Native AOT runtime pack is pinned and hashed; compiler-only packages and the SDK-requested ASP.NET download remain build inputs. If all exact raw nupkg archives are available in the caller's conventional NuGet cache, their pinned SHA-256 values are checked and only those archives seed a private feed; cached DLL/native files are never copied. Otherwise restore uses only the scoped nuget.org source. Both raw archive and NuGet content hashes are checked on fresh restored inputs, and unexpected resolved/published outputs are rejected.

The notices member retains actual resolved package license/notices plus pinned source MIT/copyright material, including json-everything OSMFEULA and Humanizer's Inflector/ByteSize declarations. Build-only inputs are inventoried separately from compiled/runtime dependencies. The source tree declares no project LICENSE; these third-party notices do not assign one or settle future release licensing.

Every produced archive is inspected and its actual extracted executable runs direct-runtime bootstrap identity and the existing production zero-argument ActionHost fixture before output acceptance. The CI supervisor reads the original archive again, extracts that member, verifies its digest, checks exact requested-version success and mismatch rejection, and repeats the executable fixture with an SDK-free child environment. This is bootstrap and production denial/cancellation/framing/signal proof; generated Action qualification stays #344 and full successful same-binary review stays #359.

Create an authorized local candidate commit before full runtime:verify: a dirty implementation checkout is correctly refused by the strict producer. The producer rejects outputs inside the source tree, including symlinked parents, and refuses existing output destinations. Local proof runs and the existing credential-free runtime-core AOT gate invoke the same package supervisor; the CI topology and checked generated wrapper remain unchanged.

Source S, future authorized builder W, original archive bytes B and later Action T retain separate identities. The build record closes the selected source, exact package inputs and observed tools/environment; system tool/library images and restore infrastructure remain external inputs. Normalized archive metadata reduces accidental differences but does not promise byte-identical unrelated rebuilds. Original verified B and its external digest remain the distribution authority. GitHub release/tag publication, attestation, network resolver, default generated Action selection and exact-pair successful qualification are later outcomes.
