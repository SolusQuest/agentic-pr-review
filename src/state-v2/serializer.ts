import { canonicalJsonBytes, type CanonicalJsonValue } from '../canonical-json/index.js';
import type { StateManifestV2 } from './manifest.js';
import { validateStateManifestV2 } from './schema.js';

export class StateManifestSerializationError extends Error {
  readonly diagnostic: 'manifest_shape_invalid';
  readonly detail: string;
  constructor(detail: string) {
    super(`state manifest v2 rejected before serialization: ${detail}`);
    this.name = 'StateManifestSerializationError';
    this.diagnostic = 'manifest_shape_invalid';
    this.detail = detail;
  }
}

/**
 * Serialize a validated StateManifestV2 into RFC 8785 canonical UTF-8 bytes.
 *
 * Re-runs `validateStateManifestV2` on entry; callers can never persist an
 * invalid manifest through this API. The output has no BOM and no trailing
 * newline; the manifest bytes covered by any external SHA-256 (predecessor,
 * acceptance marker) are exactly these bytes.
 */
export function serializeStateManifestV2(manifest: StateManifestV2): Uint8Array {
  const result = validateStateManifestV2(manifest);
  if (!result.ok) {
    throw new StateManifestSerializationError(result.message);
  }
  return canonicalJsonBytes(result.manifest as unknown as CanonicalJsonValue);
}
