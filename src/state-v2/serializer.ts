import {
  canonicalJsonBytes,
  CanonicalJsonInputError,
  type CanonicalJsonValue,
} from '../canonical-json/index.js';
import type { StateManifestV2 } from './manifest.js';
import { validateStateManifestV2 } from './schema.js';
import { renderWireEntry } from './shared-safe-path.js';

/**
 * Reason codes exposed by `StateManifestSerializationError`. Closed enum,
 * re-exported through the state-v2 public surface. `reason` is the
 * primary structured field for programmatic callers; `diagnostic`
 * remains for legacy consumers.
 */
export type StateManifestSerializationReason =
  | 'manifest_shape_invalid'
  | 'manifest_unknown_field'
  | 'manifest_unknown_version'
  | 'canonical_json_input_rejected';

export class StateManifestSerializationError extends Error {
  readonly reason: StateManifestSerializationReason;
  readonly diagnostic: 'manifest_shape_invalid';
  readonly detail: string;
  constructor(reason: StateManifestSerializationReason, detail: string) {
    // Message contains only the closed reason code and the already-bounded
    // detail (which is either a `<code>:<safe-path>` wire string from the
    // validator or a `canonical_json_input_rejected:<code>` marker from
    // the canonical helper). No caller-controlled content is admitted.
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
 * Re-runs `validateStateManifestV2` on entry. Any validator failure OR
 * canonical-domain failure is wrapped in `StateManifestSerializationError`
 * so callers observe a single typed error. Output has no BOM and no
 * trailing newline.
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
  try {
    return canonicalJsonBytes(result.manifest as unknown as CanonicalJsonValue);
  } catch (err) {
    if (err instanceof CanonicalJsonInputError) {
      // Never leak the caller's structural path — collapse to the
      // deterministic wire-safe root marker with the fixed sub-code.
      const detail = renderWireEntry('x_invalid_field', []).wireEntry;
      throw new StateManifestSerializationError('canonical_json_input_rejected', detail);
    }
    throw err;
  }
}
