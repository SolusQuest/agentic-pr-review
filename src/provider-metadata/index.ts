/**
 * ProviderRunMetadataV1 module entry point.
 *
 * Public surface consumed by the sidecar transport (`#55`), manifest descriptor
 * (`#48`, already merged), selector CAS (`#53`), cost harness (`#54`), and live
 * provider adapter (`#52`). See `docs/20_architecture/provider-run-metadata-v1.md`
 * and issue `#51`.
 *
 * Design: only `parseProviderRunMetadata(bytes: Uint8Array)` is a fail-closed
 * entry point. Callers holding an in-memory value should serialize with
 * `canonicalJsonBytes` and re-parse.
 */

export * from './types.js';
export { parseProviderRunMetadata } from './parse.js';
export { validateProviderRunMetadata } from './validate.js';
export {
  deriveAggregate,
  type DeriveAggregateInput,
  type DeriveAggregateResult,
  type DerivedProviderRunMetadataAggregate,
} from './aggregate.js';
export { buildSemanticEnvelope, computeMetadataSemanticSha256 } from './semantic-hash.js';
export type { SemanticEnvelope } from './semantic-hash.js';
export { identityAgrees } from './identity.js';
