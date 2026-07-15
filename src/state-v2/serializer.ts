import {
  canonicalJsonBytes,
  CanonicalJsonInputError,
  type CanonicalJsonValue,
} from '../canonical-json/index.js';
import type { StateManifestV2 } from './manifest.js';
import { boundedDiagnosticMessage, validateStateManifestV2 } from './schema.js';
import { renderWireEntry } from './shared-safe-path.js';

/**
 * Reason codes exposed by `StateManifestSerializationError`. Closed enum,
 * re-exported through the state-v2 public surface. `reason` is the
 * primary structured field for programmatic callers; the legacy
 * `diagnostic` alias is derived from `reason`.
 */
export type StateManifestSerializationReason =
  | 'manifest_shape_invalid'
  | 'manifest_unknown_field'
  | 'manifest_unknown_version'
  | 'canonical_json_input_rejected';

/**
 * The `diagnostic` alias maps each reason to the matching legacy top-level
 * `DiagnosticCode`. Consumers may still read the alias for back-compat,
 * but `reason` is the primary source of truth.
 */
export type StateManifestSerializationDiagnostic =
  | 'manifest_shape_invalid'
  | 'manifest_unknown_field'
  | 'manifest_unknown_version';

function diagnosticFromReason(
  reason: StateManifestSerializationReason,
): StateManifestSerializationDiagnostic {
  switch (reason) {
    case 'manifest_unknown_field':
      return 'manifest_unknown_field';
    case 'manifest_unknown_version':
      return 'manifest_unknown_version';
    case 'manifest_shape_invalid':
      return 'manifest_shape_invalid';
    case 'canonical_json_input_rejected':
      return 'manifest_shape_invalid';
  }
}

export class StateManifestSerializationError extends Error {
  readonly reason: StateManifestSerializationReason;
  readonly diagnostic: StateManifestSerializationDiagnostic;
  readonly detail: string;
  constructor(reason: StateManifestSerializationReason, detail: string) {
    // Assemble the public message under the shared bounded-message cap.
    // The message contains only the closed reason code and the already-
    // bounded wire detail; the bounded helper guards against any future
    // caller-supplied detail that would exceed the char/byte cap.
    const raw = `state manifest v2 rejected before serialization: ${reason}: ${detail}`;
    super(boundedDiagnosticMessage(raw));
    this.name = 'StateManifestSerializationError';
    this.reason = reason;
    this.diagnostic = diagnosticFromReason(reason);
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
      const detail = renderWireEntry('x_invalid_field', []).wireEntry;
      throw new StateManifestSerializationError('canonical_json_input_rejected', detail);
    }
    throw err;
  }
}
