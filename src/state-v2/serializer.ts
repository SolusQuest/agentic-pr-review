import { canonicalJsonBytes, type CanonicalJsonValue } from '../canonical-json/index.js';
import type { StateManifestV2 } from './manifest.js';
import { validateStateManifestV2 } from './schema.js';

/**
 * Reason codes exposed by `StateManifestSerializationError`. The enum is
 * closed and re-exported through the state-v2 public surface. `reason` is
 * the primary structured field for programmatic callers; `diagnostic`
 * remains for legacy consumers.
 */
export type StateManifestSerializationReason =
  | 'manifest_shape_invalid'
  | 'manifest_unknown_field'
  | 'manifest_unknown_version';

export class StateManifestSerializationError extends Error {
  readonly reason: StateManifestSerializationReason;
  readonly diagnostic: 'manifest_shape_invalid';
  readonly detail: string;
  constructor(reason: StateManifestSerializationReason, detail: string) {
    // Message contains only the closed reason code and the already-bounded
    // detail (which itself is a `<code>:<safe-path>` wire string). No
    // caller-controlled content is admitted here.
    super(`state manifest v2 rejected before serialization: ${reason}: ${detail}`);
    this.name = 'StateManifestSerializationError';
    this.reason = reason;
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
    const reason: StateManifestSerializationReason =
      result.diagnostic === 'manifest_unknown_field'
        ? 'manifest_unknown_field'
        : result.diagnostic === 'manifest_unknown_version'
          ? 'manifest_unknown_version'
          : 'manifest_shape_invalid';
    throw new StateManifestSerializationError(reason, result.message);
  }
  return canonicalJsonBytes(result.manifest as unknown as CanonicalJsonValue);
}
