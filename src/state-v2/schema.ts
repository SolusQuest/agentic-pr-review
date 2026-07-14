import { Ajv, type ErrorObject } from 'ajv';
import schema from '../../protocol/schemas/state-manifest.v2.json' with { type: 'json' };
import type { CrossFieldMessageCode, DiagnosticCode } from './diagnostics.js';
import type { StateManifestV2 } from './manifest.js';
import {
  MAX_DIAGNOSTIC_ERRORS,
  MAX_DIAGNOSTIC_MESSAGE_CHARS,
  MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES,
  STATE_NAMESPACE,
} from './constants.js';

/** Result of the schema+cross-field validator. */
export type ValidationResult =
  | { ok: true; manifest: StateManifestV2 }
  | { ok: false; diagnostic: DiagnosticCode; message: string };

const ajv = new Ajv({ strict: true, allErrors: true, allowUnionTypes: false });
const validateSchema = ajv.compile<StateManifestV2>(schema);

/** Validate a parsed JSON value against the v2 schema and cross-field rules. */
export function validateStateManifestV2(value: unknown): ValidationResult {
  if (!validateSchema(value)) {
    return classifyAjvErrors(validateSchema.errors ?? []);
  }
  const manifest = value as StateManifestV2;
  const crossErrors = crossFieldValidate(manifest);
  if (crossErrors.length > 0) {
    return {
      ok: false,
      diagnostic: 'manifest_shape_invalid',
      message: boundedJoin(crossErrors),
    };
  }
  const semanticErrors = semanticIdentityValidate(manifest);
  if (semanticErrors.length > 0) {
    return {
      ok: false,
      diagnostic: 'manifest_shape_invalid',
      message: boundedJoin(semanticErrors),
    };
  }
  return { ok: true, manifest };
}

function classifyAjvErrors(errors: readonly ErrorObject[]): {
  ok: false;
  diagnostic: DiagnosticCode;
  message: string;
} {
  // Precedence: additionalProperties -> version -> shape.
  let diagnostic: DiagnosticCode = 'manifest_shape_invalid';
  let hasAdditional = false;
  let hasVersion = false;
  for (const err of errors) {
    if ((err.params as { additionalProperty?: string } | undefined)?.additionalProperty) {
      hasAdditional = true;
    }
    if (err.instancePath === '/version') {
      hasVersion = true;
    }
  }
  if (hasAdditional) diagnostic = 'manifest_unknown_field';
  else if (hasVersion) diagnostic = 'manifest_unknown_version';

  const messages: string[] = [];
  for (const err of errors) {
    if (messages.length >= MAX_DIAGNOSTIC_ERRORS) break;
    const additional = (err.params as { additionalProperty?: string } | undefined)
      ?.additionalProperty;
    const loc = err.instancePath || '/';
    const suffix = additional ? `: ${additional}` : '';
    messages.push(sanitizeError(`${loc} ${err.message ?? 'invalid'}${suffix}`));
  }
  return { ok: false, diagnostic, message: boundedJoin(messages) };
}

/**
 * Cross-field runtime rules that go beyond what the JSON Schema encodes.
 */
export function crossFieldValidate(manifest: StateManifestV2): string[] {
  const errors: string[] = [];
  function add(code: CrossFieldMessageCode, detail: string): void {
    errors.push(`${code}: ${detail}`);
  }

  if (manifest.stateKey.namespace !== manifest.stateNamespace) {
    add('x_state_namespace_mismatch', 'stateKey.namespace must equal stateNamespace');
  }
  if (manifest.transaction.candidateLedgerSha256 !== manifest.ledger.sha256) {
    add(
      'x_transaction_ledger_binding',
      'transaction.candidateLedgerSha256 must equal ledger.sha256',
    );
  }
  const producing = manifest.providerRunMetadata.producingGeneration;
  if (producing.sessionEpoch !== manifest.sessionEpoch) {
    add(
      'x_metadata_producing_session_epoch',
      'providerRunMetadata.producingGeneration.sessionEpoch must equal sessionEpoch',
    );
  }
  if (producing.stateGeneration !== manifest.generation.stateGeneration) {
    add(
      'x_metadata_producing_state_generation',
      'providerRunMetadata.producingGeneration.stateGeneration must equal generation.stateGeneration',
    );
  }
  if (producing.ledgerEpoch !== manifest.generation.ledgerEpoch) {
    add(
      'x_metadata_producing_ledger_epoch',
      'providerRunMetadata.producingGeneration.ledgerEpoch must equal generation.ledgerEpoch',
    );
  }

  const t = manifest.transition;
  const g = manifest.generation;
  const tx = manifest.transaction;
  if (t.kind === 'bootstrap') {
    if (g.stateGeneration !== 0) add('x_bootstrap_generation_nonzero', 'stateGeneration must be 0');
    if (tx.interactionOrdinal !== 0)
      add('x_bootstrap_ordinal_nonzero', 'interactionOrdinal must be 0');
  } else if (t.kind === 'recovery_root') {
    if (g.stateGeneration !== 0)
      add('x_recovery_root_generation_nonzero', 'stateGeneration must be 0');
    if (tx.interactionOrdinal !== 0)
      add('x_recovery_root_ordinal_nonzero', 'interactionOrdinal must be 0');
  } else if (t.kind === 'continuation') {
    if (t.predecessorLedgerEpoch !== g.ledgerEpoch)
      add(
        'x_continuation_epoch_mismatch',
        'predecessorLedgerEpoch must equal generation.ledgerEpoch',
      );
    if (t.predecessorStateGeneration + 1 !== g.stateGeneration)
      add(
        'x_continuation_generation_step',
        'predecessorStateGeneration + 1 must equal generation.stateGeneration',
      );
    if (tx.interactionOrdinal < 1)
      add('x_continuation_ordinal_zero', 'interactionOrdinal must be >= 1');
  } else if (t.kind === 'reset') {
    if (t.predecessorLedgerEpoch === g.ledgerEpoch)
      add('x_reset_epoch_same', 'predecessorLedgerEpoch must differ from generation.ledgerEpoch');
    if (t.predecessorStateGeneration + 1 !== g.stateGeneration)
      add(
        'x_reset_generation_step',
        'predecessorStateGeneration + 1 must equal generation.stateGeneration',
      );
    if (tx.interactionOrdinal !== 0) add('x_reset_ordinal_nonzero', 'interactionOrdinal must be 0');
  }

  return errors;
}

/** UTF-8 byte-length + control-char + repository-syntax checks for identity strings. */
export function semanticIdentityValidate(manifest: StateManifestV2): string[] {
  const errors: string[] = [];
  const encoder = new TextEncoder();
  const control = /[\u0000-\u001f\u007f]/;
  function check(path: string, value: string): void {
    if (value.length === 0) {
      errors.push(`x_identity_empty: ${path}`);
      return;
    }
    if (encoder.encode(value).byteLength > 256) {
      errors.push(`x_identity_too_long: ${path}`);
    }
    if (control.test(value)) {
      errors.push(`x_identity_control_chars: ${path}`);
    }
  }
  check('stateKey.repository', manifest.stateKey.repository);
  check('stateKey.headRepository', manifest.stateKey.headRepository);
  check('stateKey.workflowIdentity', manifest.stateKey.workflowIdentity);
  check('stateKey.trustedExecutionDomain', manifest.stateKey.trustedExecutionDomain);
  check('cacheContractIdentity.providerId', manifest.cacheContractIdentity.providerId);
  check('cacheContractIdentity.modelId', manifest.cacheContractIdentity.modelId);

  const repoRegex = /^[A-Za-z0-9._-]+\/[A-Za-z0-9._-]+$/;
  if (
    manifest.stateKey.namespace === STATE_NAMESPACE &&
    !repoRegex.test(manifest.stateKey.repository)
  ) {
    errors.push('x_repository_syntax: stateKey.repository');
  }
  if (
    manifest.stateKey.namespace === STATE_NAMESPACE &&
    !repoRegex.test(manifest.stateKey.headRepository)
  ) {
    errors.push('x_repository_syntax: stateKey.headRepository');
  }
  return errors;
}

/** Redact potentially sensitive value fragments and cap each message. */
function sanitizeError(text: string): string {
  const trimmed = text.replace(/\s+/g, ' ').trim();
  return trimmed.length <= MAX_DIAGNOSTIC_MESSAGE_CHARS
    ? trimmed
    : trimmed.slice(0, MAX_DIAGNOSTIC_MESSAGE_CHARS);
}

/** Join messages with `; ` and enforce a hard total UTF-8 byte cap. */
export function boundedJoin(messages: readonly string[]): string {
  const kept = messages.slice(0, MAX_DIAGNOSTIC_ERRORS);
  const joined = kept.join('; ');
  const encoder = new TextEncoder();
  const encoded = encoder.encode(joined);
  if (encoded.byteLength <= MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES) return joined;
  const sentinel = '...[truncated]';
  const sentinelBytes = encoder.encode(sentinel).byteLength;
  const budget = MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES - sentinelBytes;
  // Truncate on a safe UTF-8 boundary.
  const slice = encoded.slice(0, Math.max(0, budget));
  // Rewind to the previous UTF-8 code-point boundary if needed.
  let end = slice.length;
  while (end > 0 && (slice[end - 1] & 0b1100_0000) === 0b1000_0000) {
    end -= 1;
  }
  const decoder = new TextDecoder('utf-8', { fatal: false });
  const partial = decoder.decode(slice.slice(0, end));
  return `${partial}${sentinel}`;
}
