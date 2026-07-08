# Release Policy

The project is experimental in the initial `v0.x` line. Public API stability should be stated clearly for every release.

## Version Pinning

Downstream workflows should pin the action to a release tag or full commit SHA. Do not design workflows that dynamically fetch `latest` runtime behavior at execution time.

When a future external runtime binary exists, the action version and default runtime version should move together. For example, an action release should default to the matching runtime release instead of downloading an unrelated latest runtime.

## Release Artifacts

The current action is a JavaScript action with bundled `dist/` output.

Future C# runtime distribution may use Native AOT binaries published as release assets. If introduced, release assets should include checksums and exact version selection.

## Breaking Changes

Breaking changes include:

- action input or output changes;
- schema/protocol changes;
- runtime/provider selection behavior changes;
- state artifact format changes;
- comment publishing behavior changes;
- release pinning or runtime download policy changes.

Breaking changes need explicit migration notes in the PR and release notes.
