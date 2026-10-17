# Distribution Direction

The development head contains the nested Node 24 ordinary Action, strict managed release-map resolver, inherited-fd managed launcher and fourteen accounting outputs. The checked map is explicitly null until separately authorized binding, so ordinary execution refuses before network access or launch. Credential-free fixtures execute the literal checked bundle and original packaged Runtime; they do not qualify a real release or successful downstream review. The [R7 target](../90_roadmap/r7-plan.md) retains separate qualification/publication gates, excludes a root alias and defers formal public/default graduation.

See the [CR1/CR2 plan](../90_roadmap/cr1-cr2-plan.md) for selected ownership and sequencing, the [runtime rebaseline](./agent-runtime-rebaseline.md) for earlier implementation contracts, [`r4-actionhost-wrapper-plan.md`](./r4-actionhost-wrapper-plan.md) for the activated R4 product contract, [`deepseek-flash-identity.md`](./deepseek-flash-identity.md) for the selected-current R7 provider identity and state boundary, and [`r4-migration-cutover-handoff.md`](./r4-migration-cutover-handoff.md) for the closed source inventory and exact-tree proof gate.

## CR1/CR2 Selected Target

CR1 replaces the AOT production target with a framework-dependent managed .NET 10 DLL archive, invoked as `dotnet <dll>` without apphost. Preserve the complete publish tree: entry DLL, `.deps.json`, `.runtimeconfig.json`, dependency assemblies and required resources/native assets. AOT, trimming, single-file and self-contained publishing are excluded. APR execution needs a trusted compatible runtime, not the SDK/MSBuild facilities required by ContractScribe's semantic commands; building/restoring still needs the SDK.

CR1 owns exact archive and whole-tree inventory verification, safe extraction, runtime prerequisites and selection, trusted dependency loading/environment controls, cleanup/cancellation and representative execution from the actual archive on GitHub-hosted `ubuntu-24.04` x64. The current executable-only descriptor proof and three-file package format below do not qualify that managed closure. Do not load reviewed-workspace code or allow ambient probing/startup injection to replace verified files.

CR1 adapts the existing nested Action/official artifact bridge. CR2 makes the .NET CLI the sole product entry behind a thin composite host and resolves the bridge's retain/reduce/replace disposition with evidence. It does not preselect Python, move the Action to the repository root or rewrite state transport.

The current acquisition, descriptor-launch and package-format sections below remain operating contracts until replacement. Their AOT-only details do not override the selected target. Preserve exact release identities and immutable original bytes; re-refine affected unfinished R7 contracts before execution, and complete CR1/CR2 before remaining first-candidate work. Public redistribution of Solus assemblies needs separate authority before release.

## CR1-D1 Local Managed Archive

The explicit local `build-managed` producer supplies the selected complete managed archive. The ordinary Action consumes that managed receipt and complete tree; candidate preparation and private native proof routes retain their native codec until their owning cutover. The temporary native producer and format retire under CR1-D6. R2's trusted `solus-agent-package-proof.yml` exact-head workflow exercises the actual managed producer on Ubuntu 24.04 x64 with Node 24 and SDK 10.0.109, using the previously verified package cache without restore credentials. Ordinary PR/fork `runtime-core` retains credential-free format and staging unit checks; it has no private-feed authority. D5 owns later CI gate replacement and integrated Action/Solus qualification. Neither path binds the null release map or publishes a payload.

From a clean source checkout, with a new output directory outside the checkout:

```bash
node scripts/release/build-payload.mjs build-managed --source FULL_SOURCE_SHA --version v0.0.0-internal.1 --output NEW_DIRECTORY
node scripts/release/build-payload.mjs inspect-managed --archive ARCHIVE_FILE --receipt TRUSTED_RECEIPT_FILE
```

`APRManagedPackage=true` selects the `net10.0` executable DLL with `UseAppHost=false`, `PublishAot=false`, `PublishTrimmed=false`, `PublishSingleFile=false`, `SelfContained=false` and no AOT-compatibility tooling. The profile disables unused runtime/apphost and transitive framework pack downloads; reference assemblies come from the pinned SDK. Build/restore requires SDK 10.0.109; it does not require clang, a linker, Native AOT packs or successful native compilation. Execution requires a trusted compatible `Microsoft.NETCore.App` shared runtime in major version 10, at least 10.0.9 as specified by the emitted runtimeconfig. The downstream SDK, MSBuild and Roslyn discovery requirements of ContractScribe do not transfer to APR. D3 owns trusted runtime selection and managed loading, and D5 owns representative execution qualification.

The producer snapshots the exact admitted clean source commit and tree. It restores into a fresh private cache through the checked NuGet configuration, admits every resolved package and raw archive against the checked managed dependency policy, and repeats admission after publish. The producer copies the checked actual APR `packages.managed.lock.json` into isolated build storage, restores in locked mode and independently checks the resolved packages and original archive hashes against the reviewed managed dependency policy. A verified cache may seed a fresh offline feed as specified by [Solus package consumption](../10_workflow/solus-package-consumption.md); it does not replace the separate cold selected-feed proof. Native release locks and dependency/toolchain admission remain intact. Build inputs record the evaluated publish settings, SDK driver hash and version, Node/zlib and selected OS, policy/lock/assets/notices hashes and complete package identities. The DLL embeds the release version and build-input digest. Assets hashes record actual restore bytes, including build-local paths; deterministic encoding of the same files and inputs does not promise identical independent builds.

The producer recursively inventories every actual publish file, including dependency assemblies, resources and native assets. It rejects links, hard links, special files and unsafe paths rather than skipping them. The archive is canonical gzip/ustar with regular-file entries only, implicit parent directories, mode 0644, zero owner/time fields and ordinal path order beneath `agentic-pr-review/`. Empty files are allowed; empty directory entries have no payload bytes. The required DLL, deps and runtimeconfig must exist; deps-referenced runtime/resource/native assets must be present. A bundled apphost or shared-runtime host library is rejected.

The codec owns fixed bounds: 64 MiB compressed, 128 MiB expanded including tar framing, 32 MiB per file, 256 KiB manifest, 512 entries, 100 ASCII bytes per full archive path and depth 8. Paths admit only letters, digits, `_`, `.`, `+`, `-` and `/`, with no empty, dot or parent components. Duplicate names, case aliases, file/directory collisions, noncanonical headers/padding, forbidden entry kinds, trailing gzip/tar data, omitted/extra entries and hash mismatches fail closed. Unsupported longer paths fail visibly; they are never truncated.

`manifest.json` has discriminator `managed-v1`, entrypoint, source/build identity, truthful build inputs and the complete publish/notices inventory without a self-hash. The independently trusted `receipt.json` binds every member including the manifest and the exact original archive length/SHA-256. Inspection checks that receipt before bounded decompression and verifies exact member equality. The producer compares the decoded and locally extracted files byte-for-byte against the independently collected original publish tree, then performs a bounded bootstrap identity check on the extracted DLL. That check proves compiled identity only; it does not qualify production managed loading or migrated review execution.

The output archive is the original tested byte sequence. Promotion must retain those bytes; changed source or inputs require a new candidate, never reconstruction under an existing identity. This local artifact and its codec are not public redistribution authorization, a released installation, Action adoption or CR1 completion. D2 owns production staging/extraction, D3 loading/runtime selection, D4 semantic Action adoption, D5 final qualification and D6 native retirement. R2 owns subsequent package pins and their exact managed dependency-policy update.

## CR1-D2 Managed Release Admission

The explicit managed resolver accepts a separate trusted outer map with `managed-v1` receipt and a 256 KiB map bound. It preserves exact repository, version tag, source S, builder W, Action T and original archive identities. It uses the same credential-free release/asset/download policy as native acquisition, then applies the D1 full archive inspector before materialization. Ordinary native consumers retain their current 16 KiB map contract, selected route and unbound map; D4 must reconcile actual managed map capacity when adopting the route.

Staging requires a launcher-owned 0700 parent outside the canonical reviewed workspace. Admission inventories the exact implicit directory set and every regular file through Linux directory descriptors with no-follow opens, checks ownership/type/link count/size/identity/digest and normalizes files to 0400 and directories to 0500. Missing, extra or substituted entries fail closed. Root and file guards remain open; each lease revalidates the full namespace and bytes before returning a pinned root descriptor and fixed entrypoint. The [managed staging security boundary](./security-boundary.md#managed-payload-staging-boundary) defines the trusted-runner assumptions and their limits.

Consumers use `acquireVerifiedManagedTreeLease` with the opaque tree token; structural copies or arbitrary paths cannot manufacture a lease. The returned complete descriptor inventory and frozen receipt/builder/Action identity belong to that lease. Disposal closes new admission immediately and reports `ManagedTreeInUseError` if a lease or acquisition remains active. It preserves the tree and parent, including after cancellation or unconfirmed process termination. Only confirmed lease release permits explicit cleanup retry; success removes the owned tree and closes its guards. D3 must inherit the directory descriptor into the child, establish actual trusted runtime/probing behavior and release only after confirmed dependency-loading completion. D2 does not choose or launch .NET.

The additive `CR1 managed release acquisition and staging proof` CI step builds the real exact-head archive, transports its original bytes through credential-free release fixtures, compares every staged asset and lease identity, then verifies cleanup on Ubuntu 24.04 x64. Synthetic fixtures cover malformed archives, non-entry tampering, namespace substitution, descriptor acquisition failure and concurrent/deferred disposal. The real producer proof remains mandatory if package integration relocates it to an authorized trusted package-read context; a different main commit or a synthetic tree cannot substitute for the reviewed head. D5 owns final gate replacement and integrated runtime execution qualification.

## CR1-D3 Managed Loading

`runManagedHostProcess` is a separate explicit consumer of the opaque verified tree. The launcher supplies a trusted, officially provisioned .NET 10 installation outside the reviewed workspace and a private 0700 working directory. The tested installation profile contains stable .NET 10 hostfxr/framework versions, selects the highest compatible installed patch at least 10.0.9, and requires that installation to remain stable for the invocation. A higher-major co-installed hostfxr is an unsupported layout for this profile, not an assertion that it is malicious. Execution neither discovers from ambient `PATH`/`DOTNET_ROOT` nor installs, restores or invokes SDK/MSBuild. A downstream SDK is unnecessary.

Admission checks Linux x64 ELF host identity, canonical workspace exclusion, runner/root ownership, non-shared-writable components, required runtime layout and a bounded loading-closure inventory. It retains executable/root/scratch guards and rechecks observed namespace and byte drift before launch. Limits are 64 version-directory entries, 1024 loading-closure entries, 64 MiB per runtime file and 512 MiB total. These checks support the documented trusted-runner boundary; they do not authenticate an arbitrary caller-selected binary or physically seal a system installation. The pinned host's `--list-runtimes` probe has a 64 KiB output bound and a ten-second deadline, followed by bounded signal escalation/close confirmation. A bad prerequisite fails before application startup.

The child inherits the trusted host as FD3 and the real D2 directory lease as FD4. Fixed `dotnet exec` arguments name the verified entry DLL, primary runtimeconfig and deps under that child directory descriptor; `--fx-version` pins the admitted installed patch and `--roll-forward Disable` prevents a different selection. There are zero application arguments, preserving the private framed Host entrypoint. CLR may canonicalize those paths to the owned private namespace, which remains available throughout loading. No workspace/path fallback is provided.

Authenticated configuration still grants loading capabilities. The runner reads the primary config through the lease's retained file descriptor, bounds it at 256 KiB and rejects duplicate keys, malformed UTF-8/BOM, development runtimeconfig sidecars and unreviewed loading options. Its closed configuration profile accepts the fixed net10 framework declaration and the pinned SDK's Boolean metadata-updater, unsafe-formatter and JSON-reflection feature switches. Startup hooks, additional probing paths, native-ICU paths and other unreviewed properties fail before application entry rather than being silently stripped. Environment closure independently supplies only private `TMPDIR`, no-color/no-logo/telemetry settings, diagnostics disabled and an optional admitted proof-budget profile. Ambient startup/probing/store/runtime-root, profiler, native loader, trace, SDK, package and service credentials never enter the child.

Framed IO and cancellation share the native engine: invalid declared frame lengths fail immediately, completion is bounded at 16 KiB, private stderr is drained with bounded capture, and cancellation retains its reconciliation grace before final kill. The runner owns its lease internally. Confirmed close or failed start permits release; unconfirmed termination returns a finite `ManagedHostProcessTerminationUnconfirmedError` while retaining runtime guards and any granted lease. Its actual-child `terminationConfirmed`/`resourcesReleased` promises expose late close and resource settlement. The caller retains the bridge and staging while termination is unconfirmed, then explicitly retries tree disposal after settlement. Guard cleanup failures remain observable and retryable; a signal or arbitrary caller assertion cannot authorize deletion.

The ordinary `CR1 managed loader and lifecycle proof` step runs a real no-package DLL/dependency/satellite/native fixture without private package access. The distinct `CR1 actual APR managed loader proof` runs in the trusted exact-head package workflow with the verified cache and no restore token: original APR archive bytes pass credential-free release transport and real D2 staging, then the actual zero-argument framed Host completes a synthetic unsupported event without provider/GitHub side effects. Qualification requires both selected Ubuntu 24.04 x64 steps to pass on the reviewed source. Fixture-only, local emulation, an earlier main commit or an unexecuted flag is insufficient. D4 owns selected Action adoption and map capacity; D5 owns representative Solus review qualification. The ordinary Action uses the managed consumer; the null map remains unbound and private native gates remain required.

## CR1-D4 Ordinary Managed Action

The fixed ordinary entry admits only its adjacent managed receipt/map and exact installed Action source. It passes the original registered managed payload through the staging owner before constructing its launch adapter. Consumers cannot select a native fallback, local payload, runtime root, runtime version, proof profile or download endpoint through inputs or environment. The seven inputs, fourteen validated outputs, framed Host contract and official Node artifact SDK/REST bridge remain shared with the private native proof adapter.

For the supported Ubuntu 24.04 x64 profile, the wrapper provisions official runtime-only .NET 10.0.13 in a fresh private `RUNNER_TEMP` namespace outside the reviewed workspace. The HTTPS URL, exact byte count and SHA512 are compiled policy, with no latest lookup, downloaded installer script, SDK, private NuGet restore, system installation or cache selection. Hash admission precedes bounded trusted GNU tar listing and extraction; the D3 runtime/loading admission remains authoritative afterward. The official archive's versioned layout is a maintained dependency of this profile.

Managed map bytes remain bounded at 256 KiB. Only the corresponding GitHub Contents metadata response permits 512 KiB for its Base64 representation; other metadata and native map limits remain unchanged. Exact Action T, Git blob identity, path, type and byte-count verification still precede archive acquisition. A larger transport envelope grants no new release or execution authority.

Success and safely confirmed failure drain official work and release the bridge, verified tree and runtime before presentation. Unconfirmed Host termination synchronously seals new bridge commands while retaining admitted reconciliation, storage, leases and runtime guards until actual close and resource settlement. Late cleanup never authorizes retrospective presentation. Failed official quiescence remains fatal without outputs, summary or annotations. Unconfirmed tar termination similarly retains its installation until the extractor actually closes and never launches the Host.

Qualification requires the literal generated Action, original managed APR archive, production prerequisite/loader and real official SDK/REST bridge on the reviewed hosted Ubuntu x64 head. Pure wrapper seams, a C# loader fixture, local emulation and historical native proof do not replace that gate. D5 still owns a successful representative Solus review, and R7 still owns binding/publication.

## Distribution Goals

The distribution must:

- run without a preinstalled .NET SDK on downstream runners;
- select exact payload bytes;
- fail closed on checksum or compatibility mismatch;
- start quickly enough for GitHub Actions use;
- keep Action integration reproducible and small;
- keep one product entry DLL and one payload build identity, with its complete dependency tree;
- avoid dynamic `latest` selection;
- make framework-dependent managed execution the selected downstream target, with explicit runtime prerequisites.

## Target Node.js Action Wrapper

This section describes the current entry and its CR1 adaptation boundary. CR2 owns the later CLI/composite replacement.

The checked-in nested Action metadata, payload-map.json and generated Node 24 bundle form the ordinary r7-d0 entrypoint. Repository-controlled r4-w2 prepared proof uses a separately generated fixed private bundle. The unbound ordinary map provides no release or supported downstream installation.

With an authorized map, installed Action source binding uses bounded GitHub Contents object metadata, including for the multi-megabyte bundle. Payload acquisition owns a unique 0700 directory beneath runner-provided RUNNER_TEMP outside GITHUB_WORKSPACE; generic TMPDIR/TMP/TEMP values do not select that parent. Acquisition failures and completed payload disposal remove it before presentation.

The wrapper may:

- read Action inputs;
- register secret masks;
- resolve or download a pinned payload;
- launch the .NET application;
- forward cancellation;
- bridge the official Actions artifact client;
- render bounded Host-approved annotations, step summary and [accounting outputs](../90_roadmap/r7-plan.md#summary-and-machine-readable-outputs);
- forward the host exit code.

Business behavior does not belong in the distribution wrapper.

The wrapper and Host still require one minimal lockstep process contract for cancellation, bounded outcome/output handoff, exit status, and executable build identity. This is an atomically maintained launcher seam, not a review-domain cross-language protocol or public compatibility matrix.

Run:

```bash
npm run dist:check
```

This read-only command verifies the exact metadata surface, package and lockfile ownership, generated-wrapper source/import inventory, byte-for-byte checked bundle, and retired root-alias/local-runner/workflow-invocation guards. It is the sole owner of the derived bundle's exact bytes and bounded source graph, so the repository residual-reference audit excludes that one generated file while continuing to fail closed for every other tracked path. Run `npm run build:action` only when intentionally regenerating the checked bundle.

## .NET Payload

The selected CR1 payload is the managed publish-tree archive above. It keeps Host, review product, Solus runtime/provider integration, state and publisher under one application entry; dependency assemblies do not become independently selected APR components. Package restore pins are distinct from APR release identities.

The current Native AOT implementation is preserved in the R7-D1 package-format section and existing qualification commands below. Current framework-dependent/AOT checks remain applicable until their paths are replaced or retired with evidence. Future CR1 validation must execute representative managed Host, Agent, fake-provider, tool, state and publisher paths from the actual publish tree; a successful publish alone is insufficient.

A separate Host/Worker process remains deferred pending concrete isolation needs.

## Initial Platform Scope

The selected CR1 managed payload targets GitHub-hosted `ubuntu-24.04` x64, initially through the existing nested Action, with a compatible .NET 10 runtime and no downstream SDK requirement. CR2 changes the Action integration; the original R7 AOT profile remains current implementation evidence. Other Linux/self-hosted environments are outside the initial support claim. Windows development is not a distributed target.

Additional targets are outside R7 and require separately scoped downstream demand and CI publish/execution proof:

- `linux-arm64`;
- `win-x64`;
- `osx-x64`;
- `osx-arm64`.

Platform expansion must not delay the first agent vertical slice.

## Payload Resolution

The ordinary entry reads only its fixed adjacent trusted Action map and downloads the exact authorized release asset. There is no public prepared-root/local-path fallback, arbitrary URL or implicit latest selection. The checked map is null until authorized binding. Seven consumer inputs cannot select proof, transport, map, identity or executable authority.

Runner-owned Action repository/ref context supplies exact SHA T or the map's version tag. The latter resolves T under the required later immutable consumer-tag/protection publication invariant. Comparing the installed metadata, map and bundle to Git blob identities at T detects content mismatch; it does not expose the runner's resolved download SHA or independently detect identical-content tag retarget. Actual protection provisioning remains a maintainer-owned publication precondition.

Fully verified repository-test overrides remain isolated and unreachable through ordinary downstream inputs. They bypass download, not executable/platform/manifest/build admission, and do not expand downstream support. Other distribution forms require a separate future contract.

For the current native payload path, the wrapper must not:

- search `PATH` for an arbitrary runtime;
- download implicit `latest`;
- accept an unverified mutable URL;
- choose a runtime version unrelated to the Action release;
- run a payload from the reviewed pull request workspace.

Repository-test local override paths bypass download but not executable, platform, payload-manifest, or exact build validation.

Ordinary resolution returns the verified executable's opened identity and an owned-root disposer. The wrapper executes that inherited descriptor without pathname readmission and awaits disposal across completion, failure, cancellation and fatal termination, before presentation. Protected R4 proof retains its own prepared admission, closed profiles and strict canonical receipts. Its existing current preparation generates/readbacks a fixed private proof bundle only after exact source/control validation in the ephemeral protected runner checkout; the checked product bundle, workflow/template policy anchors and historical sealed evidence remain separate. See [distribution execution fixtures](../../tests/distribution/README.md).

## Version Mapping

The R7 public experimental channel is `vMAJOR.MINOR.PATCH-internal.N` (`N >= 1`); `internal` describes maturity, not visibility. Fixed published bytes remain immutable, without stable API/accuracy/compatibility/SLA promises. The first exact version is a later execution input.

```text
payload-vX.Y.Z-internal.N at source S -> original prepared archive B
vX.Y.Z-internal.N at reviewed Action T -> exact map of B, S and builder W
```

`S != T`; builder/workflow identity `W` may differ from `S`. The strict map binds one version/platform, exact asset repository/tag/name, archive size/SHA-256, internal file inventory/sizes/hashes and build identity. The final pair also records original producer run/artifact/digest. Neither binary self-hash nor self-source cycles are introduced. Internal modules have no independent version matrix.

Prepare/test original `B` from `S` without publication credentials; review the binding map and obtain maintainer merge to `T`; qualify exact original `B` with `T`; stage that qualified pair as an unpublished draft; publish and verify payload assets, then create the consumer Action tag last. A fixed Action SHA resolves the same map. Promotion never rebuilds/repackages. Changed build inputs need a new candidate; expiry or ambiguous external state fails closed/reconciles by exact identity, never duplicates/replaces a version.

Mandatory producer attestation and final publish verification restrict expected repository, signer workflow, hosted runner and actual source claims. Attestation of `W` alone cannot prove detached `S` without independently verified candidate/build-policy binding. Consumers must verify SHA-256 against the reviewed map. The current native path retains opened-executable identity through spawn; CR1 must replace that proof with verified managed entrypoint/dependency loading without weakening source or workspace boundaries. Mandatory online consumer attestation is deferred; independent verifier commands belong to the later runbook. See the full [release identity and authority contract](../90_roadmap/r7-plan.md#exact-release-identities-and-trust); this activation creates no release or tag.

## Durable State Compatibility

The payload version and state format are different identities:

- payload version identifies executable behavior;
- state format identifies durable cross-run bytes;
- provider/model/policy/toolset/cache identities determine whether state is semantically reusable.

Use canonical content digests for policy, instructions, ordered tools, and cache-relevant configuration, plus resolved provider/model and runtime build identity. Do not add parallel manual version and id fields for the same content.

Before 1.0, a Host-classified non-current historical namespace or discriminator may cause observable safe bootstrap. A selected-current record that is malformed, unauthenticated, scope-incompatible, continuation-invalid, missing required data, ambiguous, or ancestry-invalid fails closed even when discovered automatically; an explicitly supplied incompatible artifact also fails closed. Release assets do not need to carry converters for unused historical state.

## Restricted State Transport

The current Agent session has project-owned logical records plus a separate provider-scoped continuation envelope. CR1 replaces generic context mechanics with Solus context while APR retains this section’s storage, authentication, scope, lineage and acceptance authority. Because it may contain readable reasoning and bounded repository tool results, it is more sensitive than the existing M4 metadata-oriented state. R4 selects downstream-owned GitHub Actions artifacts as the sole built-in production/default adapter.

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

The current native archive uses a layout such as:

```text
agentic-pr-review/
  manifest.json
  agentic-pr-review
  THIRD-PARTY-NOTICES.txt
```

The example chooses no actual candidate/version and is not the CR1 managed layout. The R7-D1 section defines the current implemented native format; CR1 replaces it with a complete managed tree before new candidate binding/qualification.

## Dependency Policy

- CR1 explicitly selects the experimental SolusAgent internal packages. Start bounded integration at `0.1.0-internal.1`, pin exact equality for all resolved direct/transitive Solus dependencies, and consume only newly published immutable versions for corrections.
- Private-feed authentication belongs to build/restore; the distributed publish tree must execute without package-feed credentials. Authorized internal consumption does not grant public redistribution rights.
- Revalidate APIs, behavior, retained-context compatibility and practical capacity on pin changes. Do not silently substitute versions or fall back to fresh state.
- Preserve existing validation for dependencies on current AOT paths until replacement. CR1's new target needs managed archive execution and whole-tree loading proof, not new AOT qualification.
- Treat provider request-shape and continuation changes as cache/state compatibility risks. Other preview dependency adoption still requires an explicit architecture decision.

The historical R2 MEAI/project-minimal selection does not constrain the approved Solus migration. See the [package and capacity contract](../90_roadmap/cr1-cr2-plan.md#package-and-capacity-feedback).

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

The following records the earlier R1-R7 transition. Current new migration work follows CR1/CR2 above:

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

This is the implemented native format and its validation contract. CR1 must replace its executable-only layout and launch proof for the selected managed target; retain the original evidence and checks until replacement.

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

Runtime CI installs SDK 10.0.109 into its fresh runner-temp r7-d1-dotnet directory for the core package lane. The workflow verifier requires that exact version and isolated install root and rejects roll-forward configuration, shared SDK roots and version drift. The repository-wide development global.json and other lane setup policies retain their existing behavior.

The producer requires clean HEAD == explicit S before and after building. It validates regular Git objects in the admitted closure and creates a fresh Git-object snapshot of global.json, runtime, scripts/release and protocol/schemas. Ignored checkout bin/obj files are excluded. Compilation uses fresh publish/intermediate/private NuGet/home directories, explicit package-only props/config and disabled ambient parent/user MSBuild imports. The packages.release.lock.json file applies only to opted-in package builds. Restore and publish both receive explicit publish/Native AOT runtime-pack context; .NET 10 splits Microsoft.NETCore.App.Runtime.NativeAOT.linux-x64 from the compiler tools, so a plain standalone restore is insufficient. The actual Native AOT runtime pack is pinned and hashed; compiler-only packages and the SDK-requested ASP.NET download remain build inputs. If all exact raw nupkg archives are available in the caller's conventional NuGet cache, their pinned SHA-256 values are checked and only those archives seed a private feed; cached DLL/native files are never copied. Otherwise restore uses only the scoped nuget.org source. The complete fresh NuGet ID/version directory inventory must equal the pinned policy, including PackageDownload and implicit runtime packs outside assets.libraries. The union of versioned library/target and exact download metadata must match that inventory, use only the isolated package folder, and agree with the raw archive, metadata and NuGet hash file. The metadata/library content identity is pinned separately from the cache hash file, which is verified against the raw signed archive bytes. This admission runs before and after publishing; missing, extra or wrong-version packages and inconsistent hashes/paths are rejected. BuildInputs and notices use the admitted complete inventory, and unexpected published outputs are rejected.

The notices member retains actual resolved package license/notices plus pinned source MIT/copyright material, including json-everything OSMFEULA and Humanizer's Inflector/ByteSize declarations. Build-only inputs are inventoried separately from compiled/runtime dependencies. The source tree declares no project LICENSE; these third-party notices do not assign one or settle future release licensing.

Every produced archive is inspected and its actual extracted executable runs direct-runtime bootstrap identity and the existing production zero-argument ActionHost fixture before output acceptance. The CI supervisor reads the original archive again, extracts that member, verifies its digest, checks exact requested-version success and mismatch rejection, and repeats the executable fixture with an SDK-free child environment. This is bootstrap and production denial/cancellation/framing/signal proof; generated Action qualification stays #344 and full successful same-binary review stays #359.

Create an authorized local candidate commit before full runtime:verify: a dirty implementation checkout is correctly refused by the strict producer. The producer rejects outputs inside the source tree, including symlinked parents, and refuses existing output destinations. Local proof runs and the existing credential-free runtime-core AOT gate invoke the same package supervisor; the CI topology and checked generated wrapper remain unchanged.

Source S, future authorized builder W, original archive bytes B and later Action T retain separate identities. The build record closes the selected source, exact package inputs and observed tools/environment; system tool/library images and restore infrastructure remain external inputs. Normalized archive metadata reduces accidental differences but does not promise byte-identical unrelated rebuilds. Original verified B and its external digest remain the distribution authority. GitHub release/tag publication, attestation, network resolver, default generated Action selection and exact-pair successful qualification are later outcomes.
