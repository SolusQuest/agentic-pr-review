/**
 * Host-authoritative identity agreement helper.
 *
 * `identityAgrees(metadata, expected)` compares metadata identity fields against
 * a host-supplied expected identity. It never compares metadata against itself.
 * The `metadata` parameter is a `ValidatedProviderRunMetadataV1` because
 * identity syntax and cross-field equality are enforced by the semantic
 * validator. This helper returns a plain boolean; cross-sidecar identity
 * mapping to a diagnostic code is a `#53` / `#55` host-mapping concern (issue
 * #51 does not emit `invalid-metadata-identity-mismatch`).
 */

import type { HostMetadataIdentity, ValidatedProviderRunMetadataV1 } from './types.js';

export function identityAgrees(
  metadata: ValidatedProviderRunMetadataV1,
  expected: HostMetadataIdentity,
): boolean {
  return (
    metadata.selectedProviderId === expected.providerId &&
    metadata.observedProviderId === expected.providerId &&
    metadata.resolvedModelId === expected.resolvedModelId &&
    metadata.adapterId === expected.adapterId
  );
}
