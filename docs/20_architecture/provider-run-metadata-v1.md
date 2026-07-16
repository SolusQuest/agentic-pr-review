# ProviderRunMetadataV1

`ProviderRunMetadataV1` is the bounded, provider-neutral telemetry sidecar for the M4 state bundle. Its shared vocabulary, diagnostic traversal, safe paths, and hash framing are defined by the [session ledger and prefix contract](session-ledger-and-prefix-contract.md); this document records only the TypeScript surface owned by issue #51.

The public byte boundary is `parseProviderRunMetadata(bytes: Uint8Array)`. It owns the input, applies the raw transport gates before schema and semantic validation, and returns either branded validated metadata or bounded `MetadataError` values. `deriveAggregate` is a pure reducer over branded attempts. `buildSemanticEnvelope` and `computeMetadataSemanticSha256` reconstruct the allowlisted semantic projection; provenance and transaction-binding fields are deliberately excluded from that projection. `identityAgrees` is a boolean host-authority check.

The sidecar contains no provider request IDs, raw error text, endpoints, prompts, credentials, or arbitrary extension objects. Filesystem transport, provider invocation, prefix construction, and cross-sidecar acceptance remain owned by the sibling M4 workstreams.
