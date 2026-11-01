# Distribution Direction

The development head contains the nested composite Action, its actual internal Node 24 launcher, strict managed release-map resolver, inherited-fd managed launcher and fourteen accounting outputs. The checked map is explicitly null until separately authorized binding, so ordinary preparation refuses before network access or launch. Qualification must execute the literal composite/internal Action assets and original managed archive on the selected hosted runner; synthetic map/transport fixtures do not establish a release, real artifact interoperability or successful downstream review. The [R7 target](../90_roadmap/r7-plan.md) retains separate qualification/publication gates, excludes a root alias and defers formal public/default graduation.

See the [CR1/CR2 plan](../90_roadmap/cr1-cr2-plan.md) for selected ownership and sequencing, the [runtime rebaseline](./agent-runtime-rebaseline.md) for earlier implementation contracts, [`r4-actionhost-wrapper-plan.md`](./r4-actionhost-wrapper-plan.md) for the activated R4 product contract, [`deepseek-flash-identity.md`](./deepseek-flash-identity.md) for the selected-current R7 provider identity and state boundary, and [`r4-migration-cutover-handoff.md`](./r4-migration-cutover-handoff.md) for the closed source inventory and exact-tree proof gate.

## CR1/CR2 Managed Contract

CR1 has replaced the AOT production target with a framework-dependent managed .NET 10 DLL archive, invoked as `dotnet <dll>` without apphost. Preserve the complete publish tree: entry DLL, `.deps.json`, `.runtimeconfig.json`, dependency assemblies and required resources/native assets. AOT, trimming, single-file and self-contained publishing are excluded. APR execution needs a trusted compatible runtime, not the SDK/MSBuild facilities required by ContractScribe's semantic commands; building/restoring still needs the SDK.

CR1 owns exact archive and whole-tree inventory verification, safe extraction, runtime prerequisites and selection, trusted dependency loading/environment controls, cleanup/cancellation and representative execution from the actual archive on GitHub-hosted `ubuntu-24.04` x64. Historical executable-only descriptor proof and the three-file native fixture format do not qualify that managed closure. Do not load reviewed-workspace code or allow ambient probing/startup injection to replace verified files.

CR1 adapts the existing nested Action/official artifact bridge. CR2 implements the .NET CLI as the sole product entry behind a thin composite host, retaining a narrow Node Action for official SDK upload and process/transport protection. The owning plan requires C# to own product validation, defaults and result/accounting semantics without mirrored TypeScript rules. The [CR2 contract](./cr2-cli-action-contract.md) defines invocation, result, upload and descriptor ownership. The approved [CR2 parent](https://github.com/SolusQuest/agentic-pr-review/issues/445) selects the supported SDK boundary; the synthetic pure .NET probe is alternative evidence. Complete Node removal is not required. The direction does not preselect Python, move the Action to the repository root or change encrypted artifact storage.

Current acquisition and managed descriptor launch follow the managed contract below. Original native format and proof records remain historical evidence under the named D6 dispositions. Preserve exact release identities and immutable original bytes; re-refine affected unfinished R7 contracts before execution, and complete CR1/CR2 before remaining first-candidate work. Public redistribution of Solus assemblies needs separate authority before release.

## CR1-D1 Local Managed Archive

Both `build` and `build-managed` select the same managed producer; both `inspect` and `inspect-managed` select its inspector. The ordinary Action and candidate preparation consume only the managed receipt and complete tree. There is no production native producer, native resolver or fallback. Private prepared proof fixtures retain their separate historical ABI. R2's trusted `solus-agent-package-proof.yml` exact-head workflow exercises the actual managed producer on Ubuntu 24.04 x64 with Node 24 and SDK 10.0.109, using the previously verified package cache without restore credentials. Ordinary PR/fork `runtime-core` retains credential-free format and staging unit checks; it has no private-feed authority. The trusted verify route owns current integrated Action/Solus qualification. Neither path binds the null release map or publishes a payload.

From a clean source checkout, with a new output directory outside the checkout:

```bash
node scripts/release/build-payload.mjs build-managed --source FULL_SOURCE_SHA --version v0.0.0-internal.1 --output NEW_DIRECTORY
node scripts/release/build-payload.mjs inspect-managed --archive ARCHIVE_FILE --receipt TRUSTED_RECEIPT_FILE
```

`APRManagedPackage=true` selects the `net10.0` executable DLL with `UseAppHost=false`, `PublishAot=false`, `PublishTrimmed=false`, `PublishSingleFile=false`, `SelfContained=false` and no AOT-compatibility tooling. The profile disables unused runtime/apphost and transitive framework pack downloads; reference assemblies come from the pinned SDK. Build/restore requires SDK 10.0.109; it does not require clang, a linker, Native AOT packs or successful native compilation. Execution requires a trusted compatible `Microsoft.NETCore.App` shared runtime in major version 10, at least 10.0.9 as specified by the emitted runtimeconfig. The downstream SDK, MSBuild and Roslyn discovery requirements of ContractScribe do not transfer to APR. The managed launcher owns trusted runtime selection and loading; actual archive qualification executes representative Solus reviews.

The producer snapshots the exact admitted clean source commit and tree. It restores into a fresh private cache through the checked NuGet configuration, admits every resolved package and raw archive against the checked managed dependency policy, and repeats admission after publish. The producer copies the checked actual APR `packages.managed.lock.json` into isolated build storage, restores in locked mode and independently checks the resolved packages and original archive hashes against the reviewed managed dependency policy. A verified cache may seed a fresh offline feed as specified by [Solus package consumption](../10_workflow/solus-package-consumption.md); it does not replace the separate cold selected-feed proof. The obsolete native release lock and public native producer configuration are removed. The managed lock, nine-package dependency policy, raw archive hashes and content hashes remain authoritative. Build inputs record the evaluated publish settings, SDK driver hash and version, Node/zlib and selected OS, policy/lock/assets/notices hashes and complete package identities. The DLL embeds the release version and build-input digest. Assets hashes record actual restore bytes, including build-local paths; deterministic encoding of the same files and inputs does not promise identical independent builds.

The producer recursively inventories every actual publish file, including dependency assemblies, resources and native assets. It rejects links, hard links, special files and unsafe paths rather than skipping them. The archive is canonical gzip/ustar with regular-file entries only, implicit parent directories, mode 0644, zero owner/time fields and ordinal path order beneath `agentic-pr-review/`. Empty files are allowed; empty directory entries have no payload bytes. The required DLL, deps and runtimeconfig must exist; deps-referenced runtime/resource/native assets must be present. A bundled apphost or shared-runtime host library is rejected.

The codec owns fixed bounds: 64 MiB compressed, 128 MiB expanded including tar framing, 32 MiB per file, 256 KiB manifest, 512 entries, 100 ASCII bytes per full archive path and depth 8. Paths admit only letters, digits, `_`, `.`, `+`, `-` and `/`, with no empty, dot or parent components. Duplicate names, case aliases, file/directory collisions, noncanonical headers/padding, forbidden entry kinds, trailing gzip/tar data, omitted/extra entries and hash mismatches fail closed. Unsupported longer paths fail visibly; they are never truncated.

`manifest.json` has discriminator `managed-v1`, entrypoint, source/build identity, truthful build inputs and the complete publish/notices inventory without a self-hash. The independently trusted `receipt.json` binds every member including the manifest and the exact original archive length/SHA-256. Inspection checks that receipt before bounded decompression and verifies exact member equality. The producer compares the decoded and locally extracted files byte-for-byte against the independently collected original publish tree, then performs a bounded bootstrap identity check on the extracted DLL. That check proves compiled identity only; it does not qualify production managed loading or migrated review execution.

The output archive is the original tested byte sequence. Promotion must retain those bytes; changed source or inputs require a new candidate, never reconstruction under an existing identity. This local artifact and its codec are not public redistribution authorization, a released installation, Action adoption or CR1 completion. D2 owns production staging/extraction, D3 loading/runtime selection, D4 semantic Action adoption, D5 final qualification and D6 native retirement. R2 owns subsequent package pins and their exact managed dependency-policy update.

## CR1-D2 Managed Release Admission

The explicit managed resolver accepts a separate trusted outer map with `managed-v1` receipt and a 256 KiB map bound. It preserves exact repository, version tag, source S, builder W, Action T and original archive identities. It uses the credential-free release/asset/download policy and the D1 full archive inspector before materialization. The ordinary Action uses this managed route with its 256 KiB map bound. The 16 KiB native map is retained only for historical fixtures; the checked ordinary map remains unbound.

Staging requires a launcher-owned 0700 parent outside the canonical reviewed workspace. Admission inventories the exact implicit directory set and every regular file through Linux directory descriptors with no-follow opens, checks ownership/type/link count/size/identity/digest and normalizes files to 0400 and directories to 0500. Missing, extra or substituted entries fail closed. Root and file guards remain open; each lease revalidates the full namespace and bytes before returning a pinned root descriptor and fixed entrypoint. The [managed staging security boundary](./security-boundary.md#managed-payload-staging-boundary) defines the trusted-runner assumptions and their limits.

Consumers use `acquireVerifiedManagedTreeLease` with the opaque tree token; structural copies or arbitrary paths cannot manufacture a lease. The returned complete descriptor inventory and frozen receipt/builder/Action identity belong to that lease. Disposal closes new admission immediately and reports `ManagedTreeInUseError` if a lease or acquisition remains active. It preserves the tree and parent, including after cancellation or unconfirmed process termination. Only confirmed lease release permits explicit cleanup retry; success removes the owned tree and closes its guards. D3 must inherit the directory descriptor into the child, establish actual trusted runtime/probing behavior and release only after confirmed dependency-loading completion. D2 does not choose or launch .NET.

The additive `CR1 managed release acquisition and staging proof` CI step builds the real exact-head archive, transports its original bytes through credential-free release fixtures, compares every staged asset and lease identity, then verifies cleanup on Ubuntu 24.04 x64. Synthetic fixtures cover malformed archives, non-entry tampering, namespace substitution, descriptor acquisition failure and concurrent/deferred disposal. The real producer proof remains mandatory if package integration relocates it to an authorized trusted package-read context; a different main commit or a synthetic tree cannot substitute for the reviewed head. D5 owns final gate replacement and integrated runtime execution qualification.

## CR1-D3 Managed Loading

`runManagedHostProcess` is a separate explicit consumer of the opaque verified tree. The launcher supplies a trusted, officially provisioned .NET 10 installation outside the reviewed workspace and a private 0700 working directory. The tested installation profile contains stable .NET 10 hostfxr/framework versions, selects the highest compatible installed patch at least 10.0.9, and requires that installation to remain stable for the invocation. A higher-major co-installed hostfxr is an unsupported layout for this profile, not an assertion that it is malicious. Execution neither discovers from ambient `PATH`/`DOTNET_ROOT` nor installs, restores or invokes SDK/MSBuild. A downstream SDK is unnecessary.

Admission checks Linux x64 ELF host identity, canonical workspace exclusion, runner/root ownership, non-shared-writable components, required runtime layout and a bounded loading-closure inventory. It retains executable/root/scratch guards and rechecks observed namespace and byte drift before launch. Limits are 64 version-directory entries, 1024 loading-closure entries, 64 MiB per runtime file and 512 MiB total. These checks support the documented trusted-runner boundary; they do not authenticate an arbitrary caller-selected binary or physically seal a system installation. The pinned host's `--list-runtimes` probe has a 64 KiB output bound and a ten-second deadline, followed by bounded signal escalation/close confirmation. A bad prerequisite fails before application startup.

The child inherits the trusted host as FD3 and the real D2 directory lease as FD4. Fixed `dotnet exec` arguments name the verified entry DLL, primary runtimeconfig and deps under that child directory descriptor; `--fx-version` pins the admitted installed patch and `--roll-forward Disable` prevents a different selection. The single application argument is the literal `run`, which selects the private framed Host entrypoint. CLR may canonicalize those paths to the owned private namespace, which remains available throughout loading. No workspace/path fallback is provided.

Authenticated configuration still grants loading capabilities. The runner reads the primary config through the lease's retained file descriptor, bounds it at 256 KiB and rejects duplicate keys, malformed UTF-8/BOM, development runtimeconfig sidecars and unreviewed loading options. Its closed configuration profile accepts the fixed net10 framework declaration and the pinned SDK's Boolean metadata-updater, unsafe-formatter and JSON-reflection feature switches. Startup hooks, additional probing paths, native-ICU paths and other unreviewed properties fail before application entry rather than being silently stripped. Environment closure independently supplies only private `TMPDIR`, no-color/no-logo/telemetry settings, diagnostics disabled and an optional admitted proof-budget profile. Ambient startup/probing/store/runtime-root, profiler, native loader, trace, SDK, package and service credentials never enter the child.

Framed IO and cancellation share the native engine: invalid declared frame lengths fail immediately, completion is bounded at 16 KiB, private stderr is drained with bounded capture, and cancellation retains its reconciliation grace before final kill. The runner owns its lease internally. Confirmed close or failed start permits release; unconfirmed termination returns a finite `ManagedHostProcessTerminationUnconfirmedError` while retaining runtime guards and any granted lease. Its actual-child `terminationConfirmed`/`resourcesReleased` promises expose late close and resource settlement. The caller retains the bridge and staging while termination is unconfirmed, then explicitly retries tree disposal after settlement. Guard cleanup failures remain observable and retryable; a signal or arbitrary caller assertion cannot authorize deletion.

The ordinary `CR1 managed loader and lifecycle proof` step runs a real no-package DLL/dependency/satellite/native fixture without private package access. The distinct `CR1 actual APR managed loader proof` runs in the trusted exact-head package workflow with the verified cache and no restore token: original APR archive bytes pass credential-free release transport and real D2 staging, then the actual explicit `run` framed Host completes a synthetic unsupported event without provider/GitHub side effects. Qualification requires both selected Ubuntu 24.04 x64 steps to pass on the reviewed source. Fixture-only, local emulation, an earlier main commit or an unexecuted flag is insufficient. The D4 ordinary Action supplies managed adoption and map capacity; D5 qualification executes representative Solus reviews. The null map remains unbound. Private native proof requirements remain only for the named historical consumers below.

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

## Current Composite And Node Capability

CR2 now implements the composite/internal Node layout and C# product entry. The [technical acceptance](cr2-technical-acceptance.md) records exact-source original archive, actual Runner and hosted artifact evidence; these do not bind a release map or authorize a public installation.

The checked-in outer metadata, null `payload-map.json`, internal launch/prepare metadata and four generated `internal/dist/` bundles form the ordinary entrypoint. Repository-controlled r4-w2 prepared proof uses a separately generated fixed private bundle. The unbound ordinary map provides no release or supported downstream installation.

With an authorized map, installed Action source binding uses bounded GitHub Contents object metadata, including for the multi-megabyte bundle. Payload acquisition owns a unique 0700 directory beneath runner-provided RUNNER_TEMP outside GITHUB_WORKSPACE; generic TMPDIR/TMP/TEMP values do not select that parent. Acquisition failures and completed payload disposal remove it before presentation.

The wrapper may:

- read Action inputs;
- register secret masks;
- resolve or download a pinned payload;
- launch the .NET application;
- forward cancellation;
- serve only Host-authorized official SDK uploads while C# owns REST discovery, verification and exact deletion;
- render bounded Host-approved annotations, step summary and [accounting outputs](../90_roadmap/r7-plan.md#summary-and-machine-readable-outputs);
- forward the host exit code.

Business behavior does not belong in the distribution wrapper.

The wrapper and Host still require one minimal lockstep process contract for cancellation, bounded outcome/output handoff, exit status, and executable build identity. This is an atomically maintained launcher seam, not a review-domain cross-language protocol or public compatibility matrix.

Run:

```bash
npm run dist:check
```

This read-only command verifies the exact metadata surface, package and lockfile ownership, generated-wrapper source/import inventory, byte-for-byte checked bundle, and retired root-alias/local-runner/workflow-invocation guards. It is the sole owner of the derived bundle's exact bytes and bounded source graph, so the repository residual-reference audit excludes the exact inventoried generated assets while continuing to fail closed for every other tracked path. Run `npm run build:action` only when intentionally regenerating the checked bundle.

## .NET Payload

The selected CR1 payload is the managed publish-tree archive above. It keeps Host, review product, Solus runtime/provider integration, state and publisher under one application entry; dependency assemblies do not become independently selected APR components. Package restore pins are distinct from APR release identities.

The original Native AOT package is historical fixture evidence under the D6 dispositions below. Current qualification executes representative managed Host, Agent, controlled-provider, tool, state and publisher paths from the actual publish tree; a successful publish alone is insufficient. Explicit historical AOT modes retain their separate supporting obligations.

A separate Host/Worker process remains deferred pending concrete isolation needs.

## Initial Platform Scope

For the maintainer-facing installation sequence and result interpretation, see [managed Action usage](../10_workflow/action-usage.md). [Operator lifecycle](../10_workflow/r7-operation.md) covers continuation/refusal, reset, key rotation, retention, immutable upgrades/rollback and shutdown. Those guides preserve this document's acquisition authority and current null-map boundary; they do not establish an available release or observed installation.

The current managed payload and CR2 composite target GitHub-hosted `ubuntu-24.04` x64, Node 24, a trusted compatible .NET 10 runtime and no downstream .NET SDK requirement; the original R7 AOT profile remains historical implementation evidence. Other Linux/self-hosted environments are outside the initial support claim. Windows development is not a distributed target.

The [initial CR2 CLI scope](../90_roadmap/cr1-cr2-plan.md#initial-cli-scope) keeps complete review, cross-run restore and publication inside the admitted GitHub Actions environment. Local use retains existing validation capabilities; complete local/offline and other-CI workflows require later scope and authority/storage design. The managed archive and single CLI entry do not establish those additional supported environments.

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

For the current managed payload path, the wrapper must not:

- search `PATH` for an arbitrary runtime;
- download implicit `latest`;
- accept an unverified mutable URL;
- choose a runtime version unrelated to the Action release;
- run a payload from the reviewed pull request workspace.

Repository-test local override paths bypass download but not executable, platform, payload-manifest, or exact build validation.

Ordinary resolution returns the verified publish tree and its owned-root disposer. The wrapper uses the trusted runtime executable as FD3 and the retained verified directory as FD4, then awaits disposal across completion, failure, cancellation and fatal termination before presentation. Protected R4 proof retains its own prepared admission, closed profiles and strict canonical receipts. Its existing current preparation generates/readbacks a fixed private proof bundle only after exact source/control validation in the ephemeral protected runner checkout; the checked product bundle, workflow/template policy anchors and historical sealed evidence remain separate. See [distribution execution fixtures](../../tests/distribution/README.md).

## Version Mapping

The R7 public experimental channel is `vMAJOR.MINOR.PATCH-internal.N` (`N >= 1`); `internal` describes maturity, not visibility. Fixed published bytes remain immutable, without stable API/accuracy/compatibility/SLA promises. The first exact version is a later execution input.

```text
payload-vX.Y.Z-internal.N at source S -> original prepared archive B
vX.Y.Z-internal.N at reviewed Action T -> exact map of B, S and builder W
```

`S != T`; builder/workflow identity `W` may differ from `S`. The strict map binds one version/platform, exact asset repository/tag/name, archive size/SHA-256, internal file inventory/sizes/hashes and build identity. The final pair also records original producer run/artifact/digest. Neither binary self-hash nor self-source cycles are introduced. Internal modules have no independent version matrix.

Prepare/test original `B` from `S` without publication credentials; review the binding map and obtain maintainer merge to `T`; qualify exact original `B` with `T`; stage that qualified pair as an unpublished draft; publish and verify payload assets, then create the consumer Action tag last. A fixed Action SHA resolves the same map. Promotion never rebuilds/repackages. Changed build inputs need a new candidate; expiry or ambiguous external state fails closed/reconciles by exact identity, never duplicates/replaces a version.

Mandatory producer attestation and final publish verification restrict expected repository, signer workflow, hosted runner and actual source claims. Attestation of `W` alone cannot prove detached `S` without independently verified candidate/build-policy binding. Consumers must verify SHA-256 against the reviewed map. The managed path retains trusted runtime and verified publish-tree authority through entrypoint/dependency loading. Historical native opened-executable proof does not qualify that managed closure. Mandatory online consumer attestation is deferred; independent verifier commands belong to the later runbook. See the full [release identity and authority contract](../90_roadmap/r7-plan.md#exact-release-identities-and-trust); this activation creates no release or tag.

## Composite ownership and current consumers

The outer `.github/actions/agentic-pr-review/action.yml` forwards seven raw inputs to `internal/action.yml` and maps its fourteen outputs directly. It supplies no product defaults. Its preparation step uses the declared `internal/prepare/action.yml` Node 24 Action; that Action registers its secret-free post cleanup before acquisition. Both internal references use literal `$/` paths at the composite repository's running commit. The selected github.com hosted runner supplies the actual Node Action lifecycle and SDK context; shell execution of a JavaScript bundle is not equivalent qualification.

Preparation keeps the exact original payload and pinned runtime archives in private runner storage and closes its own descriptors before handing off candidate locations and identities. Launch re-resolves the installed eight-file source graph and map, checks the original archives, reopens and verifies the complete normalized tree, reconstructs runtime-member hashes from the pinned original archive, and acquires fresh descriptors. A prior output, candidate JSON or claim directory never grants loading authority. Runtime source verification precedes the closed-environment prerequisite probe, followed by the existing loading and use-boundary checks.

The exclusive claim separates launch from preparation post cleanup. Post can clean only an unclaimed root with matching held parent/root identities. Launch retains its own candidate root through actual Host close, runtime guard release, bridge drain and SDK process/channel closure. An uncertain lifetime or identity preserves resources for runner teardown. Preparation failure leaves unavailable outer outputs and a failing step; after launch, the established fixed wrapper-failure projection and C# result sink rules apply. Empty numerical composite outputs never mean zero.

| Consumer                                     | Current disposition                                                                                                                                                           |
| -------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Ordinary root metadata/bundle                | Replaced by composite, internal launch and private preparation/post; no ordinary root bundle alias.                                                                           |
| Installed source and distribution validators | Exact closure: outer metadata, null map, two internal metadata files, and `internal/dist/{index,sdk-upload,prepare,cleanup}.js`; all are checked at the same source identity. |
| Managed generated/archive fixtures           | Current assets and separate preparation/launch/post processes; actual Runner self-reference and mapping remain an additional hosted proof.                                    |
| Protected R4 prepared workflows              | Explicit historical Node metadata and private `r4-w2` overlay; both retained calls remain outside ordinary composite selection.                                               |
| R3, framework and historical native proofs   | Explicit fixture builders and original evidence identities remain; they do not qualify the ordinary composite.                                                                |
| Release candidate/binding/staging tools      | Preserve S/W/B/T identities, original bytes and independent release authority; no map binding or additional channel.                                                          |

## Durable State Compatibility

The payload version and state format are different identities:

- payload version identifies executable behavior;
- state format identifies durable cross-run bytes;
- provider/model/policy/toolset/cache identities determine whether state is semantically reusable.

Use canonical content digests for policy, instructions, ordered tools, and cache-relevant configuration, plus resolved provider/model and runtime build identity. Do not add parallel manual version and id fields for the same content.

Before 1.0, a Host-classified non-current historical namespace or discriminator may cause observable safe bootstrap. A selected-current record that is malformed, unauthenticated, scope-incompatible, continuation-invalid, missing required data, ambiguous, or ancestry-invalid fails closed even when discovered automatically; an explicitly supplied incompatible artifact also fails closed. Release assets do not need to carry converters for unused historical state.

## Restricted State Transport

Current production uses Solus restricted context plus APR-owned logical/domain values and authenticated state. The old project SESSION/provider continuation codec is retired; APR retains this section’s storage, authentication, scope, lineage and acceptance authority. Because it may contain readable reasoning and bounded repository tool results, it is more sensitive than the existing M4 metadata-oriented state. R4 selects downstream-owned GitHub Actions artifacts as the sole built-in production/default adapter.

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

The historical native archive fixture uses a layout such as:

```text
agentic-pr-review/
  manifest.json
  agentic-pr-review
  THIRD-PARTY-NOTICES.txt
```

The example chooses no actual candidate/version and is not the CR1 managed layout. The retired R7-D1 native format remains historical fixture evidence; new candidate binding/qualification requires the managed tree described above.

## Dependency Policy

- Current production selects the five experimental SolusAgent `0.1.0-internal.4` packages with exact direct/transitive equality and checked hashes. The original `.1` selection was a historical integration entry condition; corrections require newly published immutable versions and explicit consumer pin changes.
- Private-feed authentication belongs to build/restore; the distributed publish tree must execute without package-feed credentials. Authorized internal consumption does not grant public redistribution rights.
- Revalidate APIs, behavior, retained-context compatibility and practical capacity on pin changes. Do not silently substitute versions or fall back to fresh state.
- Current managed qualification executes the original archive and complete loading tree; production AOT replacement is complete. Preserve only the named historical/supporting AOT consumers and their separate evidence obligations, rather than treating them as current production qualification.
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

## CR1-D6 retirement and retained evidence

The selected production producer and release preparation tools accept only `managed-v1`. `APRReleasePackage=true` fails with `APR_NATIVE_PRODUCTION_PACKAGE_RETIRED`; public development and supporting fixtures remain credential-free framework builds. Selecting the production managed profile still requires the locked private Solus package closure. Neither configuration falls back to native packaging.

| Surface                                                                                                     | Disposition and replacement                                                                                                                                                                                                                                                                                                                                                          |
| ----------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Native producer, ILCompiler/runtime-pack policy, native lock and native NuGet configuration                 | Removed. `build-payload.inputs.json`, the managed lock and full-tree admission own current production inputs. No clang/linker/AOT pack is required to build that target.                                                                                                                                                                                                             |
| Three-member native archive codec and source-policy unit vectors                                            | Historical fixtures only, under explicitly named `HistoricalNative...ForFixture` exports and `tests/distribution/historical-native-*`. Original receipts and `shared-map.fixture.json` remain unchanged. Current map/candidate validators reject native receipts.                                                                                                                    |
| Native ordinary resolver and installed-source entry                                                         | Removed from production selection. Explicit historical fixture seams retain hostile acquisition, handle identity, cancellation and cleanup tests.                                                                                                                                                                                                                                    |
| Old `verify-package.mjs` and `verify-generated-action.mjs` supervisors                                      | Removed: their native producer/ordinary-Action assumptions are retired. Current replacements are `verify-managed-action.mjs`, `verify-managed-generated-action.mjs` and the actual archive qualification harness.                                                                                                                                                                    |
| Runtime/Agent/ActionHost/live/evaluation/capacity AOT fixture modes                                         | Explicit historical/supporting modes only. Their old SESSION producer, rejection/reset, warning-policy and framing assertions remain executable. Current ordinary CI uses framework counterparts. Names containing AOT do not select a production package. Compatibility diagnostics follow the explicit historical `PublishAot=true` opt-in; their framework defaults remain false. |
| Fixed-source `trusted-proof-payload` CI job and protected R3/R4 prepared proof workflows                    | Retained historical executable consumers. Their clang/zlib prerequisites remain necessary for their explicit AOT preparation. They do not qualify the managed product.                                                                                                                                                                                                               |
| V2 FDD single-file production/verifier proof                                                                | Retained private proof fixture, including compiled-source readback, independent roles and complete strict receipt corpus. Framework publishing audits zero warnings; explicit historical AOT audits its original allowlist.                                                                                                                                                          |
| FD3 trusted runtime, FD4 verified directory, native filesystem primitives, strace and managed native assets | Retained current security/loading dependencies. Managed publishing does not remove them. gcc in public CI builds test native assets, not an APR production apphost.                                                                                                                                                                                                                  |

Candidate preparation tooling preserves source S, builder W, original archive bytes B and later Action T independently. Its map and candidate metadata are each bounded to 256 KiB, covering at most 512 bounded inventory records and 16 dependency records; archive admission uses the managed 64 MiB compressed bound. Closed fields, exact canonical workflow identity, storage lifetime and attestation requirements remain enforced. The public candidate smoke executes a real FDD loader/native-asset fixture with explicitly synthetic build/run/storage metadata; it is not Solus production or provenance qualification.

The actual managed archive harness passes **each original archive** and its actual producer receipt through `tests/release/verify-managed-candidate-archive.mjs` before cleanup. That pure validator checks current candidate/map acceptance and rejects altered original bytes, source/tree/build/member/digest identities, extra fields and oversized envelopes. Its remote metadata is explicitly synthetic. The same untouched archive then remains the input to the retained Action/direct/Solus qualification cases. A production source or affected build/contract change needs fresh applicable qualification; historical passing runs cannot establish changed bytes. A documentation-only revision may reuse the recorded final production proof through a verified source/tree and actual-diff chain; it must not relabel that proof as execution at the documentation head.

The [final CR1 technical acceptance record](../90_roadmap/cr1-cr2-plan.md#final-cr1-technical-acceptance) binds both original archive identities, full-tree inventory, generated Action and selected-runner execution to the final migrated source. Its controlled transport and synthetic release projections do not qualify an authorized public release pair. The same record supplies CR2 responsibilities and the issue-by-issue R7 changed-assumption inventory.

### Remaining R7 contract handoff

Before remaining R7 execution, separately re-refine #325, #345/#347, #350–#357 and #359–#363 for managed archive/receipt inventory, compatible runtime provisioning, candidate and canonical workflow identity, original-byte storage/promotion, signing/attestation, public acquisition, immutable Action T/map binding and exact-pair successful qualification. Preserve S/W/B/T separation and private restore isolation. This tooling change prepares, signs, binds, stages and publishes no actual candidate. The checked map stays unbound; public redistribution and release authority remain separate.
