import { Ajv, type ErrorObject } from 'ajv';
import schema from '../../protocol/schemas/state-manifest.v2.json' with { type: 'json' };
import type { CrossFieldMessageCode, DiagnosticCode } from './diagnostics.js';
import type { StateManifestV2 } from './manifest.js';
import {
  normalizePosition,
  renderWireEntry,
  resolveProperty,
  sanitizeSegment,
  type SchemaNode,
  type SchemaPosition,
  UNKNOWN_POSITION,
} from './shared-safe-path.js';
import {
  MAX_DIAGNOSTIC_ERRORS,
  MAX_DIAGNOSTIC_MESSAGE_CHARS,
  MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES,
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
  // Layer 1: order, render, wire-dedup, cap.
  // Ordering key: (step=6, resolver-mapped raw safe path, subCode, ajv-index).
  // Because ordering is stable and dedup runs on the fully rendered wire
  // entry AFTER shared per-path truncation, the resulting entries are
  // guaranteed byte-distinct on the wire.
  interface Candidate {
    readonly index: number;
    readonly rawSafePath: string;
    readonly subCode: string;
    readonly wireEntry: string;
    readonly diagnostic: DiagnosticCode;
  }
  const rootPos = normalizePosition(schema as unknown as SchemaNode);

  const candidates: Candidate[] = [];
  errors.forEach((err, i) => {
    const c = renderAjvError(err, i, rootPos);
    candidates.push(c);
  });

  // Stable sort by (rawSafePath lex, subCode, index).
  candidates.sort((a, b) => {
    if (a.rawSafePath !== b.rawSafePath) {
      return a.rawSafePath < b.rawSafePath ? -1 : 1;
    }
    if (a.subCode !== b.subCode) {
      return a.subCode < b.subCode ? -1 : 1;
    }
    return a.index - b.index;
  });

  // Wire-level dedup by rendered wireEntry (post-truncation).
  const seen = new Set<string>();
  const kept: Candidate[] = [];
  for (const c of candidates) {
    if (seen.has(c.wireEntry)) continue;
    seen.add(c.wireEntry);
    kept.push(c);
    if (kept.length >= MAX_DIAGNOSTIC_ERRORS) break;
  }

  // Top-level diagnostic precedence: unknown_version > unknown_field > shape_invalid.
  const hasVersion = candidates.some((c) => c.diagnostic === 'manifest_unknown_version');
  const hasUnknownField = candidates.some((c) => c.diagnostic === 'manifest_unknown_field');
  const diagnostic: DiagnosticCode = hasVersion
    ? 'manifest_unknown_version'
    : hasUnknownField
      ? 'manifest_unknown_field'
      : 'manifest_shape_invalid';

  // If only one distinct rendered wire entry survives, emit exactly that
  // entry with no aggregate sentinel — this matches the shared deep-path
  // oracle byte-exact requirement.
  if (kept.length <= 1) {
    return { ok: false, diagnostic, message: kept[0]?.wireEntry ?? '' };
  }

  // Multiple entries. Join with `; ` and append aggregate sentinel when
  // more errors were dropped than the kept set.
  const droppedByCap = candidates.length > kept.length;
  const AGG_SENTINEL = '; ...[truncated]';
  const parts = kept.map((c) => c.wireEntry);
  let joined = parts.join('; ');
  if (droppedByCap) {
    joined += AGG_SENTINEL;
  }
  // Enforce absolute UTF-16 and UTF-8 caps by dropping trailing entries.
  const encoder = new TextEncoder();
  while (
    (joined.length > MAX_DIAGNOSTIC_MESSAGE_CHARS ||
      encoder.encode(joined).byteLength > MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES) &&
    parts.length > 1
  ) {
    parts.pop();
    joined = parts.join('; ') + AGG_SENTINEL;
  }
  // Fallback: first-entry + sentinel does not fit — emit just the first entry.
  if (
    parts.length === 1 &&
    (joined.length > MAX_DIAGNOSTIC_MESSAGE_CHARS ||
      encoder.encode(joined).byteLength > MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES)
  ) {
    joined = parts[0]!;
  }
  return { ok: false, diagnostic, message: joined };
}

interface RenderedAjvCandidate {
  readonly index: number;
  readonly rawSafePath: string;
  readonly subCode: string;
  readonly wireEntry: string;
  readonly diagnostic: DiagnosticCode;
}

function renderAjvError(
  err: ErrorObject,
  index: number,
  rootPos: SchemaPosition,
): RenderedAjvCandidate {
  const instancePath = err.instancePath ?? '';
  const rawSegments = instancePath === '' ? [] : instancePath.slice(1).split('/');

  // Resolve schema positions for the raw path and sanitize each ancestor
  // segment via the shared six-rule table.
  let pos = rootPos;
  let trusted = true;
  const sanitized: string[] = [];
  for (const rawSeg of rawSegments) {
    const decoded = rawSeg.replace(/~1/g, '/').replace(/~0/g, '~');
    const propResult = resolveProperty(pos, decoded);
    const segKnown: boolean = trusted && propResult.schemaKnown;
    const seg = sanitizeSegment(decoded, segKnown);
    sanitized.push(seg);
    trusted = segKnown;
    pos = segKnown ? propResult.childSchemaPosition : UNKNOWN_POSITION;
  }

  // If the offending Ajv keyword names a specific final property/item, add
  // it to the safe path as an additional segment.
  const params = (err.params ?? {}) as Record<string, unknown>;
  let finalSegment: string | undefined;
  let subCode: string;
  let topDiagnostic: DiagnosticCode = 'manifest_shape_invalid';

  const keyword = err.keyword ?? 'invalid';

  // Special-case: any /version error remaps to manifest_unknown_version.
  const isVersion = instancePath === '/version';

  if (
    keyword === 'additionalProperties' &&
    Object.prototype.hasOwnProperty.call(params, 'additionalProperty')
  ) {
    const offendingKey = String(params.additionalProperty ?? '');
    const propResult = resolveProperty(pos, offendingKey);
    const isKnown: boolean = trusted && propResult.schemaKnown;
    finalSegment = sanitizeSegment(offendingKey, isKnown);
    if (isKnown) {
      subCode = 'variant_forbidden_field';
      // Variant-forbidden: schema-known name at a schema-known parent —
      // remains manifest_shape_invalid.
      topDiagnostic = 'manifest_shape_invalid';
    } else {
      subCode = 'unknown_field';
      topDiagnostic = 'manifest_unknown_field';
    }
  } else if (keyword === 'required' && typeof params.missingProperty === 'string') {
    const missing = params.missingProperty;
    const missingResult = resolveProperty(pos, missing);
    finalSegment = sanitizeSegment(missing, trusted && missingResult.schemaKnown);
    subCode = 'missing_required_property';
  } else {
    switch (keyword) {
      case 'type':
        subCode = 'type_mismatch';
        break;
      case 'const':
        subCode = 'const_mismatch';
        break;
      case 'enum':
        subCode = 'enum_mismatch';
        break;
      case 'pattern':
        subCode = 'pattern_mismatch';
        break;
      case 'format':
        subCode = 'format_mismatch';
        break;
      case 'minimum':
      case 'maximum':
      case 'exclusiveMinimum':
      case 'exclusiveMaximum':
      case 'multipleOf':
        subCode = 'range_out_of_bounds';
        break;
      case 'minLength':
      case 'maxLength':
        subCode = 'length_out_of_bounds';
        break;
      case 'minItems':
      case 'maxItems':
      case 'uniqueItems':
        subCode = 'array_bounds';
        break;
      case 'oneOf':
      case 'anyOf':
      case 'allOf':
      case 'if':
      case 'then':
      case 'else':
      case 'not':
      case 'discriminator':
        subCode = 'discriminator_mismatch';
        break;
      default:
        subCode = 'type_mismatch';
        break;
    }
  }

  if (isVersion) {
    subCode = 'unknown_version';
    topDiagnostic = 'manifest_unknown_version';
  }

  const segments = finalSegment === undefined ? sanitized : [...sanitized, finalSegment];
  const rendered = renderWireEntry('x_invalid_field', segments);

  // Raw path for stable ordering (post-sanitization but pre-truncation).
  const rawSafePath = segments.length === 0 ? '' : '/' + segments.join('/');

  return {
    index,
    rawSafePath,
    subCode,
    wireEntry: rendered.wireEntry,
    diagnostic: topDiagnostic,
  };
}

/** Cross-field runtime rules that go beyond what the JSON Schema encodes. */
export function crossFieldValidate(manifest: StateManifestV2): string[] {
  const errors: string[] = [];
  function add(code: CrossFieldMessageCode, safePath: string): void {
    errors.push(
      renderWireEntry('x_invalid_field', safePath === '' ? [] : safePath.slice(1).split('/'))
        .wireEntry,
    );
    void code; // sub-code kept in code for ordering; internal only.
  }

  if (manifest.stateKey.namespace !== manifest.stateNamespace) {
    add('x_state_namespace_mismatch', '/stateKey/namespace');
  }
  if (manifest.transaction.candidateLedgerSha256 !== manifest.ledger.sha256) {
    add('x_transaction_ledger_binding', '/transaction/candidateLedgerSha256');
  }
  const producing = manifest.providerRunMetadata.producingGeneration;
  if (producing.sessionEpoch !== manifest.sessionEpoch) {
    add(
      'x_metadata_producing_session_epoch',
      '/providerRunMetadata/producingGeneration/sessionEpoch',
    );
  }
  if (producing.stateGeneration !== manifest.generation.stateGeneration) {
    add(
      'x_metadata_producing_state_generation',
      '/providerRunMetadata/producingGeneration/stateGeneration',
    );
  }
  if (producing.ledgerEpoch !== manifest.generation.ledgerEpoch) {
    add(
      'x_metadata_producing_ledger_epoch',
      '/providerRunMetadata/producingGeneration/ledgerEpoch',
    );
  }

  const t = manifest.transition;
  const g = manifest.generation;
  const tx = manifest.transaction;
  if (t.kind === 'bootstrap') {
    if (g.stateGeneration !== 0)
      add('x_bootstrap_generation_nonzero', '/generation/stateGeneration');
    if (tx.interactionOrdinal !== 0)
      add('x_bootstrap_ordinal_nonzero', '/transaction/interactionOrdinal');
  } else if (t.kind === 'recovery_root') {
    if (g.stateGeneration !== 0)
      add('x_recovery_root_generation_nonzero', '/generation/stateGeneration');
    if (tx.interactionOrdinal !== 0)
      add('x_recovery_root_ordinal_nonzero', '/transaction/interactionOrdinal');
  } else if (t.kind === 'continuation') {
    if (t.predecessorLedgerEpoch !== g.ledgerEpoch)
      add('x_continuation_epoch_mismatch', '/transition/predecessorLedgerEpoch');
    if (t.predecessorStateGeneration + 1 !== g.stateGeneration)
      add('x_continuation_generation_step', '/transition/predecessorStateGeneration');
    if (tx.interactionOrdinal < 1)
      add('x_continuation_ordinal_zero', '/transaction/interactionOrdinal');
  } else if (t.kind === 'reset') {
    if (t.predecessorLedgerEpoch === g.ledgerEpoch)
      add('x_reset_epoch_same', '/transition/predecessorLedgerEpoch');
    if (t.predecessorStateGeneration + 1 !== g.stateGeneration)
      add('x_reset_generation_step', '/transition/predecessorStateGeneration');
    if (tx.interactionOrdinal !== 0)
      add('x_reset_ordinal_nonzero', '/transaction/interactionOrdinal');
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

  function emit(safePath: string): void {
    const segs = safePath === '' ? [] : safePath.slice(1).split('/');
    errors.push(renderWireEntry('x_invalid_field', segs).wireEntry);
  }

  function checkIdentity(path: string, value: string): void {
    if (value.length === 0) {
      emit(path);
      return;
    }
    if (encoder.encode(value).byteLength > 256) {
      emit(path);
    }
    if (control.test(value)) {
      emit(path);
    }
  }

  checkIdentity('/stateKey/repository', manifest.stateKey.repository);
  checkIdentity('/stateKey/headRepository', manifest.stateKey.headRepository);
  checkIdentity('/stateKey/workflowIdentity', manifest.stateKey.workflowIdentity);
  checkIdentity('/stateKey/trustedExecutionDomain', manifest.stateKey.trustedExecutionDomain);
  checkIdentity('/cacheContractIdentity/providerId', manifest.cacheContractIdentity.providerId);
  checkIdentity('/cacheContractIdentity/modelId', manifest.cacheContractIdentity.modelId);

  // Floating-alias rejection restricted to modelId per the shared contract
  // (### Floating alias rejection).
  if (manifest.cacheContractIdentity.modelId === 'latest') {
    emit('/cacheContractIdentity/modelId');
  }

  if (!isRfc3339(manifest.provenance.producedAt)) {
    emit('/provenance/producedAt');
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
  const trimmed = messages
    .slice(0, MAX_DIAGNOSTIC_ERRORS)
    .map((m) => truncateToCodepoints(m, MAX_DIAGNOSTIC_MESSAGE_CHARS));
  const joined = trimmed.join('; ');
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

/**
 * Truncate `value` to at most `maxCodepoints` Unicode code points. Iterates
 * with `for..of` so surrogate pairs count as one code point and are never
 * split. Used as the per-message cap inside `boundedJoin`.
 */
function truncateToCodepoints(value: string, maxCodepoints: number): string {
  if (maxCodepoints <= 0) return '';
  let count = 0;
  let cut = 0;
  for (const cp of value) {
    if (count >= maxCodepoints) break;
    count += 1;
    cut += cp.length;
  }
  return value.slice(0, cut);
}

/**
 * Public wrapper: format a single bounded diagnostic message. Applies the
 * per-message code-point cap and the total UTF-8 byte cap through the same
 * `boundedJoin` pipeline. All builder / classifier error paths must funnel
 * their final `Error.message` payload through this helper so no code path
 * can bypass the diagnostic bounds.
 */
export function boundedDiagnosticMessage(message: string): string {
  return boundedJoin([message]);
}
