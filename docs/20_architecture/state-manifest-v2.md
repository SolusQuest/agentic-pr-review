# StateManifestV2 And v2 State Bundle

Status: authoritative implementation-level contract for the M4 live-ledger path. Owned by issue #48. Do not modify without an approved refinement round.

Parent design: [`session-ledger-and-prefix-contract.md`](./session-ledger-and-prefix-contract.md) (issue #29).

## Purpose

`StateManifestV2` is the manifest for the M4 live-ledger state bundle. It is a distinct contract from the existing v1 manifest used by the legacy and deterministic-C# runtime paths (`src/state.ts`). v1 is never converted, backfilled, or used to synthesize a ledger; under the M4 live path v1 is only observable as `unsupported_legacy_v1` and follows the safe-bootstrap policy.

This document describes the shape, invariants, diagnostic taxonomy, byte caps, and ownership boundary. It reflects the code and JSON Schema; those artifacts are authoritative and this document explains them.

## Ownership boundary

`#48` (this contract) owns:

- Authoritative JSON Schema `protocol/schemas/state-manifest.v2.json`.
- Byte-stable serializer (RFC 8785 canonical UTF-8 JSON via the shared `src/canonical-json/` helper).
- Closed-shape validator with cross-field rules.
- Pure builder that assembles a manifest and reports the three exact byte buffers.
- Pure classifier that turns caller-supplied bytes + listing into a discriminated-union classification.
- Pure host-compatibility comparator that turns a validated manifest + expected host context into a stable outcome code.
- The fixed diagnostic-code taxonomy.
- The shared canonical-JSON helper `src/canonical-json/`.

Not owned by `#48`:

- Filesystem I/O. `#48` never imports `node:fs`. Directory enumeration and file reads are #55's responsibility.
- Manifest-last commit / staged writes / atomic rename / cleanup (#55).
- Automatic-vs-explicit fail-closed policy wiring (#55 maps classifications to lifecycle actions).
- Cross-workflow artifact selection, upload, acceptance marker, stale-writer CAS (#53).
- Ledger schema internals and record validation (#49).
- Prefix materialization (#50).
- `ProviderRunMetadataV1` schema internals and `metadataSemanticSha256` (#51).
- Live adapter and live-mode config (#52).
- Cost harness (#54).

## Local bundle layout

A v2 bundle is a directory containing exactly three regular files:

```
<bundle>/
  manifest.json                     # StateManifestV2, RFC 8785 canonical UTF-8 bytes
  ledger.json                       # ProviderSessionLedgerV1 bytes (owned by #49)
  provider-run-metadata.json        # ProviderRunMetadataV1 bytes (owned by #51)
```

No other entries; no sub-directories. Rendered review and structured-result belong to the sticky-publication surface, not the state bundle.

## Fixed constants

Exported from `src/state-v2/constants.ts`:

| Constant                               | Value                              | Purpose                                                             |
| -------------------------------------- | ---------------------------------- | ------------------------------------------------------------------- |
| `MANIFEST_MAX_BYTES`                   | `65536`                            | Classifier byte cap on `manifest.json` bytes.                       |
| `LEDGER_MAX_BYTES`                     | `1048576`                          | Descriptor and classifier byte cap on `ledger.json`.                |
| `METADATA_MAX_BYTES`                   | `65536`                            | Descriptor and classifier byte cap on `provider-run-metadata.json`. |
| `MANIFEST_FILENAME`                    | `"manifest.json"`                  |                                                                     |
| `LEDGER_FILENAME`                      | `"ledger.json"`                    |                                                                     |
| `PROVIDER_RUN_METADATA_FILENAME`       | `"provider-run-metadata.json"`     |                                                                     |
| `LEDGER_SCHEMA_VERSION`                | `1`                                | Ledger schema version bound by descriptor.                          |
| `PROVIDER_RUN_METADATA_SCHEMA_VERSION` | `1`                                | Metadata schema version bound by descriptor.                        |
| `PREFIX_CONTRACT_VERSION`              | `1`                                | Prefix contract version bound by cache-contract identity.           |
| `STATE_NAMESPACE`                      | `"m4-ledger-v2"`                   | Logical state-key namespace.                                        |
| `EPOCH_ID_REGEX`                       | `/^[A-Za-z0-9_-]{22}$/`            | Session/ledger epoch string format.                                 |
| `SHA256_HEX_REGEX`                     | `/^[a-f0-9]{64}$/`                 | Lowercase-hex SHA-256.                                              |
| `GIT_SHA_REGEX`                        | `/^([a-f0-9]{40}\|[a-f0-9]{64})$/` | Git object ID (SHA-1 or SHA-256).                                   |
| `MAX_DIAGNOSTIC_ERRORS`                | `8`                                | Diagnostic aggregation cap.                                         |
| `MAX_DIAGNOSTIC_MESSAGE_CHARS`         | `256`                              | Per-message truncation.                                             |
| `MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES`    | `1024`                             | Total UTF-8 byte cap including sentinel.                            |

The JSON Schema is the authoritative representation of filename `const`s and descriptor-payload `maximum` byte limits. `MANIFEST_MAX_BYTES` is a classifier-contract constant, not a schema `maximum`, because the manifest's own serialized byte count is not a field inside the manifest.

## Composite ledger descriptor

The parent design says the manifest binds the ledger by path, hash, byte length, ledger schema version, prefix contract version, session identity, provider/model/adapter identity, generation/commit identity, and provenance. In this v2 shape that binding is the composite of the `ledger` object plus the top-level `stateKey`, `sessionEpoch`, `cacheContractIdentity`, `generation`, `provenance`, and `transaction` objects; all live inside the same manifest bytes. The externally computed SHA-256 of those manifest bytes (used by predecessor/acceptance records) therefore covers the entire composite set. The manifest does not embed its own SHA-256.

## Semantic identity string rules

Every string field marked as an "identity" (`stateKey.repository`, `stateKey.headRepository`, `stateKey.workflowIdentity`, `stateKey.trustedExecutionDomain`, `cacheContractIdentity.providerId`, `cacheContractIdentity.modelId`) is validated by the semantic identity validator with these exact rules:

- Non-empty: `length >= 1`.
- `<= 256 UTF-8 bytes` when encoded as UTF-8.
- No characters in `U+0000..U+001F` or `U+007F`.
- Case-sensitive: no normalization.
- No Unicode normalization is applied by `#48`; producers must supply the canonical form.
- `stateKey.repository` and `stateKey.headRepository` additionally match `^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$` (GitHub `owner/name` canonical form).

Git SHAs match `/^([a-f0-9]{40}|[a-f0-9]{64})$/`. Refs and RFC-3339 timestamps keep their rough character caps and format checks.

## Classifier step order (fixed, test-observable)

`classifyStateBundleV2(input)` runs the following order. There is no separate global entry-listing pass over ledger/metadata entries. A legacy v1 manifest is classified as `unsupported_legacy_v1` before any v2 ledger/metadata listing or cap is inspected.

1. Manifest-entry safety and manifest listing/bytes consistency.
2. Manifest byte cap and parse (BOM rejection; strict UTF-8; duplicate-key rejection; trailing-byte rejection).
3. Legacy-v1 short-circuit (parsed value is an object whose own `version` is JSON number `1`).
4. V2 Ajv schema + cross-field validation.
5. Remaining v2 layout/listing consistency (ledger and metadata entries plus extras).
6. Ledger byte cap and integrity.
7. Provider-run metadata byte cap and integrity.

## Public TypeScript surface

`src/state-v2/index.ts` is the only supported consumer entry point. Sibling packages must not reach into individual files under `src/state-v2/**`.

Exported functions:

- `buildStateBundleV2(input, ledgerBytes, providerRunMetadataBytes): BuildResult`
- `classifyStateBundleV2(input): BundleClassification`
- `validateStateManifestV2(value): ValidationResult`
- `crossFieldValidate(manifest): string[]`
- `semanticIdentityValidate(manifest): string[]`
- `serializeStateManifestV2(manifest): Uint8Array`
- `checkStateManifestV2Compatibility(manifest, expected): CompatibilityOutcome`
- `canonicalJsonBytes(value): Uint8Array` (re-exported from `../canonical-json/`)

Exported error classes:

- `BuilderInputRejectedError` — the builder rejected the caller's input because it violates the canonical-JSON accepted-domain contract (getters, symbol keys, non-enumerable properties, non-plain prototypes, sparse arrays, cyclic references, or non-finite numbers).
- `BuilderValidationError` — the finalized manifest failed the schema + cross-field + semantic validators.
- `LedgerOverBoundError` / `MetadataOverBoundError` — supplied bytes exceed the frozen caps.
- `StateManifestSerializationError` — the manifest cannot be canonicalized.
- `CanonicalJsonInputError` — the canonical-JSON helper rejected its input.

Branded string types: `EpochId`, `Sha256Hex`, and `GitSha` are declared as branded aliases (a `unique symbol` phantom field). A bare `string` literal cannot be assigned to any of these without an explicit `as` cast, and the validator/builder boundary is responsible for producing a validated instance. This keeps generic strings, session/ledger epochs, SHA-256 digests, and Git object IDs distinct at compile time.

The AC-visible result-type alias for the builder is `BuildResult`. `BuildStateBundleV2Result` is retained as a synonym for stability with the earlier draft AC.

## Diagnostic taxonomy

See `src/state-v2/diagnostics.ts`. Bundle-loading codes:

`state_unsupported_legacy_v1`, `bundle_path_unsafe`, `bundle_extra_entry`, `bundle_listing_mismatch`, `manifest_missing`, `manifest_byte_limit_exceeded`, `manifest_invalid_json`, `manifest_unknown_version`, `manifest_unknown_field`, `manifest_shape_invalid`, `ledger_missing`, `ledger_byte_limit_exceeded`, `ledger_bytes_mismatch`, `ledger_hash_mismatch`, `provider_run_metadata_missing`, `provider_run_metadata_byte_limit_exceeded`, `provider_run_metadata_bytes_mismatch`, `provider_run_metadata_hash_mismatch`.

Cross-field failures always surface as `manifest_shape_invalid` with a specific `x_*` message code.

## Automatic vs explicit fail-closed policy

`#48` returns only classifications. `#55` maps them to lifecycle actions:

- Automatic caller: `unsupported_legacy_v1` and any `invalid` -> observable bootstrap lifecycle. `#55` chooses `transition.kind: bootstrap` for a clean new scope or missing initial state, and `transition.kind: recovery_root` for an invalid or unavailable selected accepted artifact.
- Explicit caller: any classification other than `valid` -> fail closed before provider invocation.

## Reusable protocol fixtures

Two committed fixture sets under `protocol/fixtures/`:

- `state-manifest-v2/positive-{bootstrap,continuation,reset,recovery-root}/` — golden bundle bytes plus `entryListing.json` and `manifest.serialized.bin`. Read-only in `fixtures.test.ts`; regenerable with `node scripts/regenerate-state-v2-fixtures.mjs`.
- `state-manifest-v2-compat/compat-*.json` — one JSON per `CompatibilityOutcome` code covering the full seven-outcome contract (`compatible_continuation`, three `expected_invalidation` codes, three `incompatible` codes). Verified read-only in `compat-fixtures.test.ts`; regenerable with `node scripts/regenerate-state-v2-compat-fixtures.mjs`. Sibling workstreams (#49 / #53 / #55) may consume these files directly rather than reconstructing them.

Regeneration is a maintainer-only step; changes to committed fixture bytes must be reviewed alongside the source change that motivated them.

## Constants mirror

`src/state-v2/constants.ts` mirrors the descriptor byte caps, filename `const`s, and schema-version `const`s that the JSON Schema declares authoritative. `constants-mirror.test.ts` pins the mirror against the loaded schema so a schema-only update fails typecheck-adjacent testing rather than at runtime.

## Coordination with sibling M4 workstreams

Frozen constants shared with siblings. Sibling PRs consume the JSON Schema `const` (authoritative) or `src/state-v2/constants.ts` (TypeScript mirror). Sibling PRs (#49 / #50 / #51) also consume the shared `src/canonical-json/canonicalJsonBytes` helper rather than implementing a second canonicalizer.

## Related

- Parent: #29 (design contract).
- Siblings: #49 (ledger schema), #50 (prefix materialization), #51 (metadata), #52 (live adapter), #53 (artifact selection/acceptance), #54 (cost harness), #55 (sidecar transport / manifest-last commit).
