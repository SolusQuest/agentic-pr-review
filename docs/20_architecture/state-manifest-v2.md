# StateManifestV2 And v2 State Bundle

Workstream-specific implementation guide for the M4 v2 state-bundle contract. This document does NOT re-state the shared M4 Batch #1 machinery — the sole normative source for cross-workstream vocabulary, byte caps, resolver semantics, traversal, safe-path sanitizer, deep-path oracle, and conformance vectors is [session-ledger-and-prefix-contract.md](./session-ledger-and-prefix-contract.md), section `## M4 Batch #1 Frozen Vocabulary`.

Reader flow: read this document for `#48`'s workstream-specific bindings (schema, TypeScript public surface, workstream error types, filesystem layout of positive/negative fixtures) and follow anchor references to the shared contract for algorithmic details.

## Purpose

Turn the shared vocabulary into a concrete, byte-stable v2 manifest contract implemented under `src/state-v2/` and `src/canonical-json/`. All shared algorithms (resolver, traversal, sanitizer, truncation, oracle) are consumed by reference — the code in this workstream implements them; this document does not re-freeze them.

## Ownership boundary

`#48` owns: the authoritative JSON Schema `protocol/schemas/state-manifest.v2.json`; the byte-stable canonical serializer; the closed-shape validator; the discriminated-union classifier for an already-loaded bundle; the pure builder; the pure host-compatibility comparator; the workstream diagnostic-code taxonomy; the workstream-specific bounded-aggregation algorithm; and the shared canonical-JSON helper under `src/canonical-json/` (per `### Canonical JSON helper (per-language)` in the shared contract).

Not owned by `#48`: filesystem I/O (owned by `#55`); artifact selection and stale-writer CAS (`#53`); ledger internal schema (`#49`); prefix materialization (`#50`); provider-run-metadata internal schema (`#51`); the live adapter and mode config (`#52`); cost harness (`#54`); `EpochId` generation (`#55`).

## Local bundle layout

The v2 state-bundle directory layout consumed by `classifyStateBundleV2` is exactly three regular files at the bundle root: `manifest.json`, `ledger.json`, `provider-run-metadata.json`. Sibling workstreams may not introduce additional files inside a v2 bundle. Fixture directory layout under `protocol/fixtures/state-manifest-v2/`:

```
<fixture-name>/
  bundle/
    manifest.json
    ledger.json
    provider-run-metadata.json
  expected/
    manifest.serialized.bin     # golden bytes matching serializeStateManifestV2(manifest)
    entryListing.json           # EntryDescriptor[] passed to the classifier
```

## Public TypeScript surface

`src/state-v2/index.ts` exports the following names (the authoritative surface is asserted by `src/state-v2/public-surface.test.ts`): types `StateManifestV2`, `StateManifestV2Input`, `ClassifyStateBundleV2Input`, `BundleClassification`, `BuildResult` / `BuildStateBundleV2Result`, `EntryDescriptor`, `ExpectedStateManifestV2Context`, `HeadRelationship`, `CompatibilityOutcome`, `ExpectedInvalidationCode`, `IncompatibilityCode`, `EpochId`, `Sha256Hex`, `GitSha`, `DiagnosticCode`, `InvalidDiagnosticCode`, `UnsupportedLegacyDiagnostic`, `CrossFieldMessageCode`; functions `validateStateManifestV2`, `serializeStateManifestV2`, `buildStateBundleV2`, `classifyStateBundleV2`, `checkStateManifestV2Compatibility`, `boundedDiagnosticMessage`, `boundedJoin`, `crossFieldValidate`, `semanticIdentityValidate`, `isRfc3339`; typed errors `LedgerOverBoundError`, `MetadataOverBoundError`, `BuilderValidationError`, `BuilderInputRejectedError`, `StateManifestSerializationError`; constants module (see below).

The canonical-JSON helper exports `canonicalJsonBytes`, `CANONICAL_JSON_VERSION`, `CanonicalJsonValue`, and `CanonicalJsonInputError` from `src/canonical-json/index.ts`.

## Constants module

`src/state-v2/constants.ts` mirrors the JSON Schema authoritative values plus a small set of workstream-specific constants that do not appear in the schema. The `constants-mirror.test.ts` regression asserts these three invariants: (a) every constant that has a schema-side representation matches the JSON Schema value byte-exactly; (b) every constant that has a shared-contract source of truth (in `session-ledger-and-prefix-contract.md`) matches that value; (c) the module never diverges silently from either source.

Schema-backed: `LEDGER_FILENAME`, `PROVIDER_RUN_METADATA_FILENAME`, `LEDGER_SCHEMA_VERSION`, `PROVIDER_RUN_METADATA_SCHEMA_VERSION`, `PREFIX_CONTRACT_VERSION`, `STATE_NAMESPACE`, `LEDGER_MAX_BYTES`, `METADATA_MAX_BYTES`, and every pattern regex (`SHA256_HEX_REGEX`, `GIT_SHA_REGEX`, `EPOCH_ID_REGEX`, `PRODUCING_RUN_ID_REGEX`, `REPOSITORY_REGEX`).

Workstream-only: `MANIFEST_MAX_BYTES`, `MANIFEST_FILENAME`, `EXPECTED_BUNDLE_FILENAMES`, `MAX_DIAGNOSTIC_ERRORS`, `MAX_DIAGNOSTIC_MESSAGE_CHARS`, `MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES`, `STATE_GENERATION_MAX`, `PREDECESSOR_STATE_GENERATION_MAX`, `INTERACTION_ORDINAL_MAX`, `PULL_REQUEST_MAX`, `PRODUCING_RUN_ATTEMPT_MAX`, `REPOSITORY_MIN_LENGTH`, `REPOSITORY_MAX_LENGTH`, `IDENTITY_STRING_MAX_LENGTH`, `IDENTITY_STRING_MAX_UTF8_BYTES`.

## Classifier step order

The 10-step ordered pipeline is spelled out in the issue body of #48 (`## Classifier API`, steps 1..10). The shared string-safety traversal at step 5 uses the resolver and safe-path sanitizer from the shared contract; the schema stage at step 6 maps every Ajv keyword through the workstream Ajv sub-code table (see `## Schema diagnostic mapping and bounded aggregation` in the issue body). All wire messages follow the shared `<code>:<safe-path>` format.

## Diagnostic taxonomy

The classifier returns exactly one of three `kind` values (`valid` / `unsupported_legacy_v1` / `invalid`). Only the `invalid` branch carries a `DiagnosticCode` restricted to `InvalidDiagnosticCode`; the `unsupported_legacy_v1` branch carries only `UnsupportedLegacyDiagnostic` (`'state_unsupported_legacy_v1'`). See `src/state-v2/diagnostics.ts` for the enums and the issue body's `## Diagnostic code taxonomy (fixed enum)` for the full contract.

## Automatic vs explicit fail-closed policy

`#48` returns classifications only. Mapping `invalid` to bootstrap vs explicit fail-closed lives in `#55`; see `## Automatic vs explicit fail-closed policy` in the issue body.

## Coordination with sibling M4 workstreams

Shared values consumed / published are listed in `## Dependencies and coordination points` of the issue body. Anchor drift: if a heading in `session-ledger-and-prefix-contract.md` changes, every referring workstream issue must be updated in the same PR — this document contains no shared values that would need mirror updates.

## Related

- Parent: `#29`
- Shared contract (single normative source for M4 Batch #1 machinery): [session-ledger-and-prefix-contract.md](./session-ledger-and-prefix-contract.md), section `## M4 Batch #1 Frozen Vocabulary`
- Repo architecture: [architecture.md](./architecture.md)
- Runtime protocol: [runtime-protocol.md](./runtime-protocol.md)
