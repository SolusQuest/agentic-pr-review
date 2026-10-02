# Release Policy

The project is experimental in the initial `v0.x` line. Public API stability should be stated clearly for every release.

The selected post-rebaseline distribution is a thin Node.js Action wrapper plus a pinned .NET payload. See [`docs/20_architecture/distribution.md`](../20_architecture/distribution.md) and [`docs/20_architecture/agent-runtime-rebaseline.md`](../20_architecture/agent-runtime-rebaseline.md).

## R7 Experimental Channel And Execution Authority

The approved [R7 plan](../90_roadmap/r7-plan.md) targets public, downloadable `vMAJOR.MINOR.PATCH-internal.N` prereleases (`N >= 1`) for bounded maintainer adoption. `internal` denotes maturity, not private visibility or runtime privilege. No stable production, accuracy, compatibility, support SLA or maintenance-duration promise is made. Formal public/default graduation is deferred until a later explicit maintainer initiation; release or R7 completion does not trigger it. Fixed published bytes remain immutable, and the first exact version is a later execution input.

These are release targets, not a claim that the current seven-input/no-output prepared-payload Action has release resolution, accounting outputs or downstream support. C1's documentation merge activates the contract before dependent implementation; it publishes no artifact.

The target uses one `linux-x64` Native AOT executable on GitHub-hosted `ubuntu-24.04`, the existing nested Node 24 Action and no downstream SDK. No root alias, second provider/platform, fork/draft support, automatic reset/compaction or converter is introduced.

Preparation needs exact source/version/builder authority and produces candidate identities. A separately reviewed binding PR and maintainer merge produce the Action identity. Staging and public publication each need approval for the resulting exact qualified candidate and provisioned authority. OIDC signing, build and release-write authority are separate. Deployment/provider execution additionally needs selected repository/content and finite calls/time/cost approval; release-only operations make no provider calls. Settings/protection, secrets/environments, merge and issue/milestone closure remain maintainer-owned execution boundaries. Graph publication, implementation or review approval alone authorizes none of them.

## Version Pinning

Downstream workflows should pin the action to a release tag or full commit SHA. Do not design workflows that dynamically fetch `latest` runtime behavior at execution time.

The R7 Action and single-executable payload share one user-facing semantic version but distinct immutable commits/tags: transport `payload-vX.Y.Z-internal.N` at payload source `S`, and consumer `vX.Y.Z-internal.N` at later reviewed Action commit `T`. The strict map at `T` binds exact original archive bytes `B`, source `S`, approved builder/workflow `W`, asset identity, sizes/hashes/inventory and build identity; `S != T`, and `W` may differ from `S`. A fixed Action SHA uses the same map. Internal Host, Agent, tool, provider and publisher modules do not receive independent release versions.

Prepare/test and attest original `B` without publication credentials. Bind the map through review/maintainer merge, qualify original `B` against exact `T`, then stage the exact pair as an unpublished draft. Publication verifies the payload assets/provenance first and creates the consumer Action tag last. Finalization distinguishes source and Action identities without self-hash/self-source cycles; promotion never rebuilds/repackages. Expired/missing/ambiguous candidate artifacts stop or require new reviewed preparation, never implicit latest or replacement bytes. See the [R7 release identity contract](../90_roadmap/r7-plan.md#exact-release-identities-and-trust).

Historical tags remain immutable. A removed pre-1.0 runtime path can remain usable through its historical tag without requiring the new development head to maintain that implementation.

## Release Artifacts

The current action has bundled JavaScript `dist/` output. The target wrapper remains bundled JavaScript, but business logic moves into the .NET payload.

The selected C# distribution uses Native AOT binaries published as release assets once the agent, host boundary, and compatibility behavior reach the distribution gate. Release assets must include checksums, exact version selection, and a machine-readable payload manifest.

R4 validates the thin Action against an explicitly prepared trusted payload without creating a public release/download contract. R7 targets exact automatic release-asset acquisition through the reviewed Action map, with mandatory consumer SHA-256 verification and opened-executable identity preserved through spawn. Repository-test overrides remain fully verified and unreachable from ordinary downstream inputs.

Producer attestation and final publish verification must restrict repository, signer workflow, hosted runner and actual source/workflow claims. Attestation of `W` alone does not prove detached `S`; independently verify the candidate/build-policy binding. Mandatory online consumer attestation is deferred; the later runbook supplies independent verifier commands. Actual immutable-release/protection provisioning is maintainer-owned preflight, not inferred from this policy.

An exact candidate must pass credential-free production-composition qualification and public download verification. Subsequent bounded public-test, public-maintainer and private-maintainer adoption each needs separate authority. Preserve private source/SESSION confidentiality and report only scoped evidence and limitations; R6 unknowns are retained without new inherited cache-hit, billing or formal-regression gates.

## Pre-1.0 Compatibility

Compatibility machinery must remain proportional to real downstream needs.

Keep explicit identity for:

- Action and payload releases;
- durable cross-run state;
- replay and evaluation bundles.

Session reuse still records the resolved provider/model and identities for adapter behavior, instructions, policy, ordered tools, and cache-relevant configuration. Prefer canonical content digests and the runtime build identity over parallel manually bumped `version` and `id` fields.

Do not create public version families for internal C# DTOs, atomically released components, test helpers, or implementation-only boundaries.

Before 1.0, an incompatible durable state change should normally:

1. use the new Agent-state namespace and increment its one current-format discriminator;
2. ensure automatic selection cannot interpret old M4 or non-current bytes as current state;
3. perform an observable safe bootstrap only for true absence or Host-classified historical non-current candidates;
4. fail closed for selected-current incompatibility whether discovered automatically or supplied explicitly;
5. document the reset in the PR and release notes.

Do not add conversion, backfill, parallel readers, or parallel writers without an identified downstream that needs them.

## Breaking Changes

Breaking changes include:

- action input or output changes;
- released public schema/protocol changes;
- runtime/provider selection behavior changes;
- state artifact format changes;
- agent tool or session-persistence behavior changes after those surfaces become public;
- comment publishing behavior changes;
- release pinning or runtime download policy changes.

Breaking changes need explicit migration notes in the PR and release notes.

The first release that removes the Claude Code CLI path must state:

- the runtime path was removed;
- `v0.1.0` remains the unmaintained historical tag for the last tagged legacy Action;
- old Claude session state is not migrated;
- the first run on the new path safely bootstraps;
- runtime/provider inputs changed;
- any renamed or removed outputs.

Do not publish later unshipped legacy work merely to create a final snapshot. Git history retains the exact pre-removal source for investigation without creating a new support or compatibility promise. Removing Claude runtime integration does not remove the repository contributor entrypoint `CLAUDE.md`.
