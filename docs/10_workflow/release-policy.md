# Release Policy

The project is experimental in the initial `v0.x` line. Public API stability should be stated clearly for every release.

The selected post-rebaseline distribution is a thin Node.js Action wrapper plus a pinned .NET payload. See [`docs/20_architecture/distribution.md`](../20_architecture/distribution.md) and [`docs/20_architecture/agent-runtime-rebaseline.md`](../20_architecture/agent-runtime-rebaseline.md).

## Version Pinning

Downstream workflows should pin the action to a release tag or full commit SHA. Do not design workflows that dynamically fetch `latest` runtime behavior at execution time.

The Action version and default single-executable .NET payload move together. Internal Host, Agent, tool, provider, and publisher modules do not receive independent release versions.

Historical tags remain immutable. A removed pre-1.0 runtime path can remain usable through its historical tag without requiring the new development head to maintain that implementation.

## Release Artifacts

The current action has bundled JavaScript `dist/` output. The target wrapper remains bundled JavaScript, but business logic moves into the .NET payload.

The selected C# distribution uses Native AOT binaries published as release assets once the agent, host boundary, and compatibility behavior reach the distribution gate. Release assets must include checksums, exact version selection, and a machine-readable payload manifest.

R4 may validate the thin Action against an explicitly prepared trusted payload without creating a public release/download contract. Exact automatic Action-to-payload resolution and release-asset download become required at R7.

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
