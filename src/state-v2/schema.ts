import { Ajv, type ErrorObject } from 'ajv';
import schema from '../../protocol/schemas/state-manifest.v2.json' with { type: 'json' };
import type { CrossFieldMessageCode, DiagnosticCode } from './diagnostics.js';
import type { StateManifestV2 } from './manifest.js';
import {
  MAX_DIAGNOSTIC_ERRORS,
  MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES,
  STATE_NAMESPACE,
} from './constants.js';

/** Result of the schema + cross-field validator. */
export type ValidationResult =
  | { ok: true; manifest: StateManifestV2 }
  | { ok: false; diagnostic: DiagnosticCode; message: string };

const ajv = new Ajv({ strict: true, allErrors: true, allowUnionTypes: false });
const validateSchema = ajv.compile<StateManifestV2>(schema);

/**
 * Validate a parsed JSON value against the v2 schema and cross-field rules.
 *
 * Diagnostic messages contain only fixed reason codes and structural JSON
 * paths (e.g. `/generation/ledgerEpoch`). They never include unknown property
 * names, duplicate key names, actual manifest identity values, or observed
 * hash digests — those would be caller-controlled content and could leak
 * sensitive data through classification output.
 */
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
  const seenKeys = new Set<string>();
  for (const err of errors) {
    if (messages.length >= MAX_DIAGNOSTIC_ERRORS) break;
    const summary = summarizeAjvError(err);
    if (seenKeys.has(summary.key)) continue;
    seenKeys.add(summary.key);
    messages.push(summary.text);
  }
  return { ok: false, diagnostic, message: boundedJoin(messages) };
}

interface AjvErrorSummary {
  key: string;
  text: string;
}

/**
 * Reduce an Ajv error to a code + JSON pointer path. Never includes the
 * offending value, additionalProperty name, or Ajv-provided detail text.
 */
function summarizeAjvError(err: ErrorObject): AjvErrorSummary {
  const path = err.instancePath || '/';
  const keyword = err.keyword ?? 'invalid';
  let code = 'invalid';
  switch (keyword) {
    case 'additionalProperties':
      code = 'unknown_property';
      break;
    case 'required':
      code = 'missing_property';
      break;
    case 'type':
      code = 'type_mismatch';
      break;
    case 'const':
      code = 'const_mismatch';
      break;
    case 'enum':
      code = 'enum_mismatch';
      break;
    case 'pattern':
      code = 'pattern_mismatch';
      break;
    case 'minimum':
    case 'maximum':
    case 'exclusiveMinimum':
    case 'exclusiveMaximum':
      code = 'numeric_bound';
      break;
    case 'minLength':
    case 'maxLength':
      code = 'length_bound';
      break;
    case 'oneOf':
      code = 'discriminated_union_mismatch';
      break;
    default:
      code = 'invalid';
  }
  const text = `${path} ${code}`;
  return { key: text, text };
}

/** Cross-field runtime rules that go beyond what the JSON Schema encodes. */
export function crossFieldValidate(manifest: StateManifestV2): string[] {
  const errors: string[] = [];
  function add(code: CrossFieldMessageCode): void {
    errors.push(code);
  }

  if (manifest.stateKey.namespace !== manifest.stateNamespace) {
    add('x_state_namespace_mismatch');
  }
  if (manifest.transaction.candidateLedgerSha256 !== manifest.ledger.sha256) {
    add('x_transaction_ledger_binding');
  }
  const producing = manifest.providerRunMetadata.producingGeneration;
  if (producing.sessionEpoch !== manifest.sessionEpoch) {
    add('x_metadata_producing_session_epoch');
  }
  if (producing.stateGeneration !== manifest.generation.stateGeneration) {
    add('x_metadata_producing_state_generation');
  }
  if (producing.ledgerEpoch !== manifest.generation.ledgerEpoch) {
    add('x_metadata_producing_ledger_epoch');
  }

  const t = manifest.transition;
  const g = manifest.generation;
  const tx = manifest.transaction;
  if (t.kind === 'bootstrap') {
    if (g.stateGeneration !== 0) add('x_bootstrap_generation_nonzero');
    if (tx.interactionOrdinal !== 0) add('x_bootstrap_ordinal_nonzero');
  } else if (t.kind === 'recovery_root') {
    if (g.stateGeneration !== 0) add('x_recovery_root_generation_nonzero');
    if (tx.interactionOrdinal !== 0) add('x_recovery_root_ordinal_nonzero');
  } else if (t.kind === 'continuation') {
    if (t.predecessorLedgerEpoch !== g.ledgerEpoch) add('x_continuation_epoch_mismatch');
    if (t.predecessorStateGeneration + 1 !== g.stateGeneration)
      add('x_continuation_generation_step');
    if (tx.interactionOrdinal < 1) add('x_continuation_ordinal_zero');
  } else if (t.kind === 'reset') {
    if (t.predecessorLedgerEpoch === g.ledgerEpoch) add('x_reset_epoch_same');
    if (t.predecessorStateGeneration + 1 !== g.stateGeneration) add('x_reset_generation_step');
    if (tx.interactionOrdinal !== 0) add('x_reset_ordinal_nonzero');
  }

  return errors;
}

/**
 * UTF-8 byte-length + control-char + repository-syntax + RFC 3339 checks for
 * identity and provenance strings. Emits fixed code paths only; identity
 * values, repository names, and timestamps are never echoed.
 */
export function semanticIdentityValidate(manifest: StateManifestV2): string[] {
  const errors: string[] = [];
  const encoder = new TextEncoder();
  const control = /[\u0000-\u001f\u007f]/;

  function checkIdentity(path: string, value: string): void {
    if (value.length === 0) {
      errors.push(`x_identity_empty:${path}`);
      return;
    }
    if (encoder.encode(value).byteLength > 256) {
      errors.push(`x_identity_too_long:${path}`);
    }
    if (control.test(value)) {
      errors.push(`x_identity_control_chars:${path}`);
    }
  }

  checkIdentity('stateKey.repository', manifest.stateKey.repository);
  checkIdentity('stateKey.headRepository', manifest.stateKey.headRepository);
  checkIdentity('stateKey.workflowIdentity', manifest.stateKey.workflowIdentity);
  checkIdentity('stateKey.trustedExecutionDomain', manifest.stateKey.trustedExecutionDomain);
  checkIdentity('cacheContractIdentity.providerId', manifest.cacheContractIdentity.providerId);
  checkIdentity('cacheContractIdentity.modelId', manifest.cacheContractIdentity.modelId);

  const repoRegex = /^[A-Za-z0-9._-]+\/[A-Za-z0-9._-]+$/;
  if (
    manifest.stateKey.namespace === STATE_NAMESPACE &&
    !repoRegex.test(manifest.stateKey.repository)
  ) {
    errors.push('x_repository_syntax:stateKey.repository');
  }
  if (
    manifest.stateKey.namespace === STATE_NAMESPACE &&
    !repoRegex.test(manifest.stateKey.headRepository)
  ) {
    errors.push('x_repository_syntax:stateKey.headRepository');
  }

  if (!isRfc3339(manifest.provenance.producedAt)) {
    errors.push('x_producedAt_invalid_rfc3339:provenance.producedAt');
  }

  return errors;
}

/**
 * RFC 3339 date-time validator (calendar-valid). Requires an explicit Z or
 * +HH:MM/-HH:MM offset. Rejects timestamps missing timezone, out-of-range
 * fields, or trailing content.
 */
export function isRfc3339(value: string): boolean {
  const match =
    /^(\d{4})-(\d{2})-(\d{2})[Tt](\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?(Z|z|[+-]\d{2}:\d{2})$/.exec(
      value,
    );
  if (!match) return false;
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const hour = Number(match[4]);
  const minute = Number(match[5]);
  const second = Number(match[6]);
  const tz = match[8];
  if (month < 1 || month > 12) return false;
  const daysInMonth = calendarDaysInMonth(year, month);
  if (day < 1 || day > daysInMonth) return false;
  if (hour > 23) return false;
  if (minute > 59) return false;
  // RFC 3339 allows :60 for leap seconds; treat 0..60 inclusive.
  if (second > 60) return false;
  if (tz && tz.toUpperCase() !== 'Z') {
    const off = /^([+-])(\d{2}):(\d{2})$/.exec(tz);
    if (!off) return false;
    const oh = Number(off[2]);
    const om = Number(off[3]);
    if (oh > 23 || om > 59) return false;
  }
  return true;
}

function calendarDaysInMonth(year: number, month: number): number {
  if (month === 2) {
    const leap = (year % 4 === 0 && year % 100 !== 0) || year % 400 === 0;
    return leap ? 29 : 28;
  }
  return [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31][month - 1];
}

/**
 * Join messages with `; ` and enforce a hard total UTF-8 byte cap that
 * includes the trailing `...[truncated]` sentinel. Truncation happens on a
 * UTF-8 codepoint boundary and never emits a replacement character.
 */
export function boundedJoin(messages: readonly string[]): string {
  const kept = messages.slice(0, MAX_DIAGNOSTIC_ERRORS);
  const joined = kept.join('; ');
  const encoder = new TextEncoder();
  const encoded = encoder.encode(joined);
  if (encoded.byteLength <= MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES) return joined;

  const sentinel = '...[truncated]';
  const sentinelBytes = encoder.encode(sentinel);
  const budget = MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES - sentinelBytes.byteLength;
  const truncated = truncateAtCodepointBoundary(joined, Math.max(0, budget));
  const result = `${truncated}${sentinel}`;
  // Defensive: recompute and confirm we are strictly at or below the cap.
  const finalBytes = encoder.encode(result).byteLength;
  if (finalBytes > MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES) {
    // Recurse with a smaller message to absorb any remaining slack; the
    // truncation algorithm should not require this in practice.
    return boundedJoin([truncated.slice(0, Math.floor(truncated.length / 2))]);
  }
  return result;
}

/**
 * Take the longest prefix of `value` whose UTF-8 encoding is at most
 * `maxBytes` bytes long, preserving codepoint boundaries. This iterates
 * through the string character by character and never returns a partial
 * multi-byte codepoint.
 */
function truncateAtCodepointBoundary(value: string, maxBytes: number): string {
  if (maxBytes <= 0) return '';
  const encoder = new TextEncoder();
  let bytes = 0;
  let cutOffset = 0;
  // for..of iterates full codepoints (handling surrogate pairs).
  let charOffset = 0;
  for (const codepoint of value) {
    const encoded = encoder.encode(codepoint);
    if (bytes + encoded.byteLength > maxBytes) break;
    bytes += encoded.byteLength;
    charOffset += codepoint.length;
    cutOffset = charOffset;
  }
  return value.slice(0, cutOffset);
}
