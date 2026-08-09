# Distribution Direction

The development head is currently between public Action generations. R1 removed the mixed JavaScript Action metadata and bundle while retaining direct TypeScript/C# validation. The selected target is a thin bundled Node.js Action wrapper plus pinned, self-contained .NET payloads.

See [`agent-runtime-rebaseline.md`](./agent-runtime-rebaseline.md) for component ownership and migration sequencing and [`r4-actionhost-wrapper-plan.md`](./r4-actionhost-wrapper-plan.md) for the prospective R4 product contract.

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

No checked-in Action entrypoint exists during the R1-R3 transition. R4 introduces the replacement wrapper only after the C# Host target, small public configuration surface, and integrated proof are ready.

The wrapper may:

- read Action inputs;
- register secret masks;
- resolve or download a pinned payload;
- launch the .NET application;
- forward cancellation;
- bridge the official Actions artifact client;
- set outputs, annotations, and step summary;
- forward the host exit code.

Business behavior does not belong in the distribution wrapper.

The wrapper and Host still require one minimal lockstep process contract for cancellation, bounded outcome/output handoff, exit status, and executable build identity. This is an atomically maintained launcher seam, not a review-domain cross-language protocol or public compatibility matrix.

During the no-public-Action interval, run:

```bash
npm run dist:check
```

This command verifies that the retired Action directory, bundle builder, local runner, and local workflow invocation remain absent. It does not build product code. R4 restores generated-wrapper reproducibility validation when the replacement wrapper exists.

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

The first post-rebaseline production payload should target `linux-x64`, matching the primary GitHub-hosted runner path.

Additional targets are added only when there is an identified downstream need and CI can publish and execute them:

- `linux-arm64`;
- `win-x64`;
- `osx-x64`;
- `osx-arm64`.

Platform expansion must not delay the first agent vertical slice.

## Payload Resolution

Target production resolution order:

1. an explicit trusted local payload path for repository tests and advanced development;
2. a payload bundled with the Action release, if size and repository policy permit;
3. an exact release asset selected by the Action release manifest.

The wrapper must not:

- search `PATH` for an arbitrary runtime;
- download implicit `latest`;
- accept an unverified mutable URL;
- choose a runtime version unrelated to the Action release;
- run a payload from the reviewed pull request workspace.

Local override paths bypass download but not executable, platform, payload-manifest, or exact build validation.

R4 proves the thin wrapper and two-run workflow behavior using an explicitly prepared trusted local payload path. Automatic release-asset download, exact default Action-to-payload resolution, and public release mapping belong to R7.

## Version Mapping

The Action release and default .NET payload move together.

For a normal release:

```text
action tag vX.Y.Z
  -> payload manifest vX.Y.Z
  -> agentic-pr-review build vX.Y.Z
```

Internal Host, Agent, tool, provider, and publisher modules do not need independent semantic versions.

Advanced runtime override is not a reason to create independent compatibility promises before a real downstream use case exists.

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

Potential assets:

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

The exact layout becomes a release contract only when the first production asset is published.

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
