import { Ajv, type ErrorObject } from 'ajv';
import schema from '../../protocol/schemas/state-manifest.v2.json' with { type: 'json' };
import type { DiagnosticCode } from './diagnostics.js';
import type { StateManifestV2 } from './manifest.js';
import {
  normalizePosition,
  renderWireEntry,
  resolveArrayItem,
  resolveProperty,
  sanitizeSegment,
  scanStringSafety,
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
  // Stage: shared string-safety traversal runs before Ajv, mirroring the
  // classifier stage order. Any NUL or unpaired UTF-16 surrogate in a
  // string value or property name yields manifest_shape_invalid with
  // wire message x_invalid_unicode:<safe-path>.
  const stringSafety = scanStringSafety(value, schema as unknown as SchemaNode);
  if (stringSafety) {
    const wire = renderWireEntry('x_invalid_unicode', stringSafety.segments);
    return { ok: false, diagnostic: 'manifest_shape_invalid', message: wire.wireEntry };
  }
  if (!validateSchema(value)) {
    return classifyAjvErrors(validateSchema.errors ?? []);
  }
  const manifest = value as StateManifestV2;
  const crossCandidates = crossFieldCandidates(manifest);
  const semanticCandidates = semanticIdentityCandidates(manifest);
  const combined = [...crossCandidates, ...semanticCandidates];
  if (combined.length > 0) {
    return finalizeAggregation(combined, 'manifest_shape_invalid');
  }
  return { ok: true, manifest };
}

// ---------------------------------------------------------------------------
// Aggregation pipeline (candidate-v7 shared algorithm).
//
// Every candidate (Ajv-derived, cross-field, semantic) is rendered through
// `renderWireEntry` and then fed to the same pipeline:
//   1. Stable-sort by (stage, rawSafePath, subCode, index).
//   2. Render all candidates.
//   3. Dedup on the fully rendered wire entry.
//   4. Cap at MAX_DIAGNOSTIC_ERRORS distinct entries.
//   5. Sentinel only when distinct-entry count exceeded the cap.
//   6. Enforce absolute char/byte caps by dropping trailing entries.
// ---------------------------------------------------------------------------

/**
 * A single validator finding, pre-render. All stages produce candidates in
 * this shape so they can flow through a single aggregation pipeline.
 */
export interface AggregatorCandidate {
  /** Stage of origin; lower runs first for ties. */
  readonly stage: number;
  /** Stable index within a stage (insertion order). */
  readonly index: number;
  /** JSON-Pointer-shaped rendering used for stable ordering only. */
  readonly rawSafePath: string;
  /** Internal, never-emitted sub-code used for stable ordering only. */
  readonly subCode: string;
  /** Wire code prefix. */
  readonly code: 'x_invalid_field';
  /** Already-sanitized ordered segments. */
  readonly segments: readonly string[];
  /** Per-candidate top-level diagnostic contribution. */
  readonly diagnostic: DiagnosticCode;
}

const AGG_SENTINEL = '; ...[truncated]';

function finalizeAggregation(
  candidates: readonly AggregatorCandidate[],
  fallbackDiagnostic: DiagnosticCode,
): { ok: false; diagnostic: DiagnosticCode; message: string } {
  // 1. Stable sort by (stage, rawSafePath, subCode, index).
  const sorted = candidates.slice().sort((a, b) => {
    if (a.stage !== b.stage) return a.stage - b.stage;
    if (a.rawSafePath !== b.rawSafePath) return a.rawSafePath < b.rawSafePath ? -1 : 1;
    if (a.subCode !== b.subCode) return a.subCode < b.subCode ? -1 : 1;
    return a.index - b.index;
  });

  // 2. Render every candidate through the shared truncation algorithm.
  const rendered = sorted.map((c) => ({
    candidate: c,
    wireEntry: renderWireEntry(c.code, c.segments).wireEntry,
  }));

  // 3. Dedup by fully rendered wire entry, preserving stable order.
  const seen = new Set<string>();
  const unique: typeof rendered = [];
  for (const r of rendered) {
    if (seen.has(r.wireEntry)) continue;
    seen.add(r.wireEntry);
    unique.push(r);
  }

  // 4. Cap at MAX_DIAGNOSTIC_ERRORS distinct entries.
  const kept = unique.slice(0, MAX_DIAGNOSTIC_ERRORS);

  // 5. Sentinel iff distinct-entry count > cap (i.e. entries were dropped
  //    because of the cap, NOT because of ordinary dedup).
  const droppedByCap = unique.length > kept.length;

  // Top-level diagnostic precedence: unknown_version > unknown_field >
  // shape_invalid > fallback.
  const hasVersion = sorted.some((c) => c.diagnostic === 'manifest_unknown_version');
  const hasUnknownField = sorted.some((c) => c.diagnostic === 'manifest_unknown_field');
  const diagnostic: DiagnosticCode = hasVersion
    ? 'manifest_unknown_version'
    : hasUnknownField
      ? 'manifest_unknown_field'
      : fallbackDiagnostic;

  if (kept.length === 0) {
    return { ok: false, diagnostic, message: '' };
  }
  if (kept.length === 1 && !droppedByCap) {
    return { ok: false, diagnostic, message: kept[0]!.wireEntry };
  }

  // 6. Assemble the message with `; ` and append the aggregate sentinel
  //    when the cap actually forced truncation. Enforce absolute char /
  //    byte caps by dropping trailing entries; never truncate inside an
  //    entry. If only the first entry plus sentinel fits, emit just the
  //    first entry.
  const encoder = new TextEncoder();
  let parts = kept.map((k) => k.wireEntry);
  let joined = parts.join('; ') + (droppedByCap ? AGG_SENTINEL : '');
  while (
    (joined.length > MAX_DIAGNOSTIC_MESSAGE_CHARS ||
      encoder.encode(joined).byteLength > MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES) &&
    parts.length > 1
  ) {
    parts = parts.slice(0, parts.length - 1);
    // Once we drop an entry to make room, the message is definitionally
    // truncated even if dedup was the only reason the count dropped
    // before this loop.
    joined = parts.join('; ') + AGG_SENTINEL;
  }
  if (
    parts.length === 1 &&
    (joined.length > MAX_DIAGNOSTIC_MESSAGE_CHARS ||
      encoder.encode(joined).byteLength > MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES)
  ) {
    joined = parts[0]!;
  }
  return { ok: false, diagnostic, message: joined };
}

function classifyAjvErrors(errors: readonly ErrorObject[]): ValidationResult {
  const rootPos = normalizePosition(schema as unknown as SchemaNode);
  const candidates: AggregatorCandidate[] = [];
  errors.forEach((err, i) => {
    candidates.push(renderAjvCandidate(err, i, rootPos));
  });
  return finalizeAggregation(candidates, 'manifest_shape_invalid');
}

function renderAjvCandidate(
  err: ErrorObject,
  index: number,
  rootPos: SchemaPosition,
): AggregatorCandidate {
  const instancePath = err.instancePath ?? '';
  const rawSegments = instancePath === '' ? [] : instancePath.slice(1).split('/');

  // Resolve schema positions for the raw path and sanitize each ancestor
  // segment via the shared six-rule table. Use resolveArrayItem when the
  // current position is an ArrayPosition and the segment parses as a
  // non-negative base-10 integer.
  let pos: SchemaPosition = rootPos;
  let trusted = true;
  const sanitized: string[] = [];
  for (const rawSeg of rawSegments) {
    const decoded = rawSeg.replace(/~1/g, '/').replace(/~0/g, '~');
    const [nextPos, nextTrusted, segment] = advanceAjvSegment(pos, decoded, trusted);
    sanitized.push(segment);
    trusted = nextTrusted;
    pos = nextPos;
  }

  // If the offending Ajv keyword names a specific final property/item, add
  // it to the safe path as an additional segment.
  const params = (err.params ?? {}) as Record<string, unknown>;
  let finalSegment: string | undefined;
  let subCode: string;
  let topDiagnostic: DiagnosticCode = 'manifest_shape_invalid';

  const keyword = err.keyword ?? 'invalid';
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
    subCode = keywordToSubCode(keyword);
  }

  if (isVersion) {
    subCode = 'unknown_version';
    topDiagnostic = 'manifest_unknown_version';
  }

  const segments = finalSegment === undefined ? sanitized : [...sanitized, finalSegment];
  const rawSafePath = segments.length === 0 ? '' : '/' + segments.join('/');

  return {
    stage: 6,
    index,
    rawSafePath,
    subCode,
    code: 'x_invalid_field',
    segments,
    diagnostic: topDiagnostic,
  };
}

function advanceAjvSegment(
  pos: SchemaPosition,
  decoded: string,
  trusted: boolean,
): [SchemaPosition, boolean, string] {
  // Numeric array index against an array position uses resolveArrayItem
  // per the frozen resolver contract.
  if (pos.kind === 'array' || pos.kind === 'composite') {
    const asIndex = Number(decoded);
    if (Number.isInteger(asIndex) && asIndex >= 0 && String(asIndex) === decoded) {
      const arrResult = resolveArrayItemIfPresent(pos);
      if (arrResult.schemaKnown) {
        return [
          trusted ? arrResult.childSchemaPosition : UNKNOWN_POSITION,
          trusted,
          decoded, // numeric indices pass through verbatim (ASCII digits are safe)
        ];
      }
    }
  }
  const propResult = resolveProperty(pos, decoded);
  const segKnown: boolean = trusted && propResult.schemaKnown;
  const seg = sanitizeSegment(decoded, segKnown);
  return [segKnown ? propResult.childSchemaPosition : UNKNOWN_POSITION, segKnown, seg];
}

function resolveArrayItemIfPresent(pos: SchemaPosition): {
  schemaKnown: boolean;
  childSchemaPosition: SchemaPosition;
} {
  return resolveArrayItem(pos);
}

function keywordToSubCode(keyword: string): string {
  switch (keyword) {
    case 'type':
      return 'type_mismatch';
    case 'const':
      return 'const_mismatch';
    case 'enum':
      return 'enum_mismatch';
    case 'pattern':
      return 'pattern_mismatch';
    case 'format':
      return 'format_mismatch';
    case 'minimum':
    case 'maximum':
    case 'exclusiveMinimum':
    case 'exclusiveMaximum':
    case 'multipleOf':
      return 'range_out_of_bounds';
    case 'minLength':
    case 'maxLength':
      return 'length_out_of_bounds';
    case 'minItems':
    case 'maxItems':
    case 'uniqueItems':
      return 'array_bounds';
    case 'oneOf':
    case 'anyOf':
    case 'allOf':
    case 'if':
    case 'then':
    case 'else':
    case 'not':
    case 'discriminator':
      return 'discriminator_mismatch';
    default:
      return 'type_mismatch';
  }
}

// ---------------------------------------------------------------------------
// Cross-field candidates.
// ---------------------------------------------------------------------------

/**
 * Emit structured cross-field candidates. `crossFieldValidate` (which
 * historically returned rendered wire strings) remains available as a
 * convenience wrapper for external callers and legacy tests.
 */
export function crossFieldCandidates(manifest: StateManifestV2): AggregatorCandidate[] {
  const out: AggregatorCandidate[] = [];
  let index = 0;
  const add = (subCode: string, safePath: string): void => {
    const segments = safePath === '' ? [] : safePath.slice(1).split('/');
    out.push({
      stage: 7,
      index: index++,
      rawSafePath: safePath,
      subCode,
      code: 'x_invalid_field',
      segments,
      diagnostic: 'manifest_shape_invalid',
    });
  };

  // stateKey.namespace/stateNamespace equality is owned by JSON Schema
  // const at Ajv step; the semantic stage no longer checks it.
  if (manifest.transaction.candidateLedgerSha256 !== manifest.ledger.sha256) {
    add('cross_transaction_ledger_binding', '/transaction/candidateLedgerSha256');
  }
  const producing = manifest.providerRunMetadata.producingGeneration;
  if (producing.sessionEpoch !== manifest.sessionEpoch) {
    add(
      'cross_metadata_producing_session_epoch',
      '/providerRunMetadata/producingGeneration/sessionEpoch',
    );
  }
  if (producing.stateGeneration !== manifest.generation.stateGeneration) {
    add(
      'cross_metadata_producing_state_generation',
      '/providerRunMetadata/producingGeneration/stateGeneration',
    );
  }
  if (producing.ledgerEpoch !== manifest.generation.ledgerEpoch) {
    add(
      'cross_metadata_producing_ledger_epoch',
      '/providerRunMetadata/producingGeneration/ledgerEpoch',
    );
  }

  const t = manifest.transition;
  const g = manifest.generation;
  const tx = manifest.transaction;
  if (t.kind === 'bootstrap') {
    if (g.stateGeneration !== 0)
      add('cross_bootstrap_generation_nonzero', '/generation/stateGeneration');
    if (tx.interactionOrdinal !== 0)
      add('cross_bootstrap_ordinal_nonzero', '/transaction/interactionOrdinal');
  } else if (t.kind === 'recovery_root') {
    if (g.stateGeneration !== 0)
      add('cross_recovery_root_generation_nonzero', '/generation/stateGeneration');
    if (tx.interactionOrdinal !== 0)
      add('cross_recovery_root_ordinal_nonzero', '/transaction/interactionOrdinal');
  } else if (t.kind === 'continuation') {
    if (t.predecessorLedgerEpoch !== g.ledgerEpoch)
      add('cross_continuation_epoch_mismatch', '/transition/predecessorLedgerEpoch');
    if (t.predecessorStateGeneration + 1 !== g.stateGeneration)
      add('cross_continuation_generation_step', '/transition/predecessorStateGeneration');
    if (tx.interactionOrdinal < 1)
      add('cross_continuation_ordinal_zero', '/transaction/interactionOrdinal');
  } else if (t.kind === 'reset') {
    if (t.predecessorLedgerEpoch === g.ledgerEpoch)
      add('cross_reset_epoch_same', '/transition/predecessorLedgerEpoch');
    if (t.predecessorStateGeneration + 1 !== g.stateGeneration)
      add('cross_reset_generation_step', '/transition/predecessorStateGeneration');
    if (tx.interactionOrdinal !== 0)
      add('cross_reset_ordinal_nonzero', '/transaction/interactionOrdinal');
  }
  return out;
}

/** Legacy string-array wrapper (already-rendered wire entries). */
export function crossFieldValidate(manifest: StateManifestV2): string[] {
  return crossFieldCandidates(manifest).map((c) => renderWireEntry(c.code, c.segments).wireEntry);
}

// ---------------------------------------------------------------------------
// Semantic identity candidates.
// ---------------------------------------------------------------------------

export function semanticIdentityCandidates(manifest: StateManifestV2): AggregatorCandidate[] {
  const out: AggregatorCandidate[] = [];
  let index = 0;
  const encoder = new TextEncoder();
  const control = /[\u0000-\u001f\u007f]/;

  const add = (subCode: string, safePath: string): void => {
    const segments = safePath === '' ? [] : safePath.slice(1).split('/');
    out.push({
      stage: 8,
      index: index++,
      rawSafePath: safePath,
      subCode,
      code: 'x_invalid_field',
      segments,
      diagnostic: 'manifest_shape_invalid',
    });
  };

  function checkIdentity(path: string, value: string): void {
    if (value.length === 0) {
      add('semantic_identity_empty', path);
      return;
    }
    if (encoder.encode(value).byteLength > 256) {
      add('semantic_identity_utf8_over_cap', path);
    }
    if (control.test(value)) {
      add('semantic_identity_control_char', path);
    }
  }

  checkIdentity('/stateKey/repository', manifest.stateKey.repository);
  checkIdentity('/stateKey/headRepository', manifest.stateKey.headRepository);
  checkIdentity('/stateKey/workflowIdentity', manifest.stateKey.workflowIdentity);
  checkIdentity('/stateKey/trustedExecutionDomain', manifest.stateKey.trustedExecutionDomain);
  checkIdentity('/cacheContractIdentity/providerId', manifest.cacheContractIdentity.providerId);
  checkIdentity('/cacheContractIdentity/modelId', manifest.cacheContractIdentity.modelId);

  if (manifest.cacheContractIdentity.modelId === 'latest') {
    add('semantic_floating_alias', '/cacheContractIdentity/modelId');
  }

  if (!isRfc3339(manifest.provenance.producedAt)) {
    add('semantic_produced_at_rfc3339', '/provenance/producedAt');
  }

  return out;
}

/** Legacy string-array wrapper. */
export function semanticIdentityValidate(manifest: StateManifestV2): string[] {
  return semanticIdentityCandidates(manifest).map(
    (c) => renderWireEntry(c.code, c.segments).wireEntry,
  );
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

// ---------------------------------------------------------------------------
// Legacy boundedJoin / boundedDiagnosticMessage — retained for callers that
// operate on already-rendered strings (builder detail messages, classifier
// wire-format short-circuits). These do NOT drive multi-candidate
// aggregation; that pipeline is `finalizeAggregation`.
// ---------------------------------------------------------------------------

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
  const finalBytes = encoder.encode(result).byteLength;
  if (finalBytes > MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES) {
    return boundedJoin([truncated.slice(0, Math.floor(truncated.length / 2))]);
  }
  return result;
}

function truncateAtCodepointBoundary(value: string, maxBytes: number): string {
  if (maxBytes <= 0) return '';
  const encoder = new TextEncoder();
  let bytes = 0;
  let cutOffset = 0;
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

export function boundedDiagnosticMessage(message: string): string {
  return boundedJoin([message]);
}
