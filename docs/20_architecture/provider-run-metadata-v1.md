# ProviderRunMetadataV1

Status: authoritative under the normative [`session-ledger-and-prefix-contract.md`](session-ledger-and-prefix-contract.md) #29 design contract. Implementation issue: #51.

## Authority

The JSON Schema [`protocol/schemas/provider-run-metadata.v1.json`](../../protocol/schemas/provider-run-metadata.v1.json) is authoritative for the ProviderRunMetadataV1 serialization shape, closed properties, enums, and local bounds. The TypeScript module `src/provider-metadata/` is authoritative for cross-field consistency, ordering, contiguity, identity syntax, derivation equality, and semantic-hash bytes.

Both authorities are subordinate to the normative #29 design contract; the top-level `semanticMetadata` field list, transaction-binding exclusions, and hash framing are frozen there and are not redefined here.

## Semantic hash

`metadataSemanticSha256 = SHA256(UTF8("agentic-pr-review/provider-run-metadata-semantic/v1") || 0x00 || RFC8785(semanticMetadata))`.

`semanticMetadata` is built by allowlist from the top-level fields fixed by #29:

- `schemaVersion`
- `selectedProviderId`, `observedProviderId`, `resolvedModelId`, `adapterId`
- `logicalPrefixSha256`, `prefixSha256`
- `capability`
- `cacheStatus`
- `normalizedUsage`
- `retryObservations`
- `errorCodes`
- `telemetryCompleteness`

Excluded from the hash (bound by the outer duplicate key at ledger/manifest layer):

- `producingRunId`, `runAttempt` (provenance)
- `interactionId`, `consumedInputSha256`, `resultSha256`, `traceSha256`, `predecessorLedgerSha256`, `candidateLedgerSha256` (transaction-binding)

The golden byte oracle for this hash lives in `protocol/fixtures/provider-run-metadata/v1/golden-hash-*.json`. If #50 later replaces the internal RFC 8785 JCS producer with a shared helper, the bytes and hashes must not change; the goldens gate that migration.

## Two-stage derivation

Producers write per-attempt observations to `normalizedUsage.attempts[]`. The semantic validator recomputes and requires exact equality with the stored values for:

- `normalizedUsage.requests` (attempt → request reduction, retries summed by attempt)
- `normalizedUsage.aggregate` (field-wise sum across requests; `null` propagates)
- `capability.aggregate` and `cacheStatus` (run-level precedence; zero-request rule fires first)
- `retryObservations.requests` and `.aggregate`
- `errorCodes` (sorted, deduplicated union of per-attempt codes)
- `telemetryCompleteness` (usage / cache / statelessProof / aggregate)

Any drift is `invalid-metadata-aggregate-mismatch`.

## Provider `errorCodes` are observations, not diagnostics

`errorCodes` and per-attempt `attemptErrorCodes` are drawn from a closed allowlist of provider/telemetry observations:

`provider_timeout`, `provider_4xx`, `provider_5xx`, `provider_rate_limited`, `provider_cancelled`, `capability_unsupported`, `cache_marker_mismatch`, `stateless_proof_missing`.

Validator failure codes (`invalid-metadata-*`) live in a separate namespace and never appear inside metadata.

## Host authority

`identityAgrees(metadata, expectedHostIdentity)` compares metadata identity fields against a host-supplied expected identity, never against metadata itself. The literal `latest` model id and characters outside the allowed ASCII class are rejected. Identity values are bounded, ASCII, and case-sensitive (`[A-Za-z0-9._:/-]`); case is never normalized. Host-authoritative identity agreement, not string normalization, is the protection against an incorrect provider, model, or adapter identity.

## Downstream consumers

- #48 (StateManifestV2 descriptor): binds `path`, `sha256`, `byteLength`, `schemaVersion`, `producingGeneration`, and a distinct `metadataSemanticSha256` field derived from this schema.
- #52 (trusted live provider adapter): produces this metadata; must consume the same JSON Schema and golden fixtures.
- #53 (selector CAS acceptance): uses `metadataSemanticSha256` as part of the semantic duplicate key.
- #54 (cost harness): consumes `normalizedUsage.aggregate` plus `telemetryCompleteness.aggregate == complete`.
- #55 (sidecar I/O): parses and validates the file, then hashes it for the manifest descriptor.

## `errorCodes` ordering (normative)

Provider `errorCodes` and per-attempt `attemptErrorCodes` are sorted by the position of each code in the frozen `ALLOWED_ERROR_CODES` allowlist:

`provider_timeout`, `provider_4xx`, `provider_5xx`, `provider_rate_limited`, `provider_cancelled`, `capability_unsupported`, `cache_marker_mismatch`, `stateless_proof_missing`.

This ordering is what the reference aggregator emits, what the golden hashes fix, and what a C# or other cross-language implementation of `#52` must reproduce. Ordinal ASCII sort would not match this order (e.g. `capability_unsupported` sorts before `provider_*` alphabetically) and must not be used.
