/**
 * Shared safe-diagnostic-path machinery for M4 Batch #1 workstreams.
 *
 * This module implements the algorithms frozen in the design contract
 * `docs/20_architecture/session-ledger-and-prefix-contract.md`, section
 * `## M4 Batch #1 Frozen Vocabulary`:
 *
 * - `### Schema-position resolver` — `SchemaPosition`, `normalizePosition`,
 *   `resolveProperty`, `resolveArrayItem`, `CompositePosition`, per-call
 *   `activeSchemaNodes` cycle guard.
 * - `### Safe diagnostic path for Unicode / additional-property rejections`
 *   — the six-rule sanitizer table, JSON-Pointer safe-path composition, and
 *   greedy per-path truncation with the `<path-truncated>` placeholder.
 * - `### Shared traversal order and stage precedence` — the deterministic
 *   traversal that scans string values and property names for NUL and
 *   unpaired UTF-16 surrogates.
 *
 * Sibling workstream sidecars (#49 C# ledger, #51 metadata) implement the
 * same algorithms in their target language. Cross-language byte-equality is
 * asserted through the shared conformance vectors G1..G7 and V1..V3.
 */

import { MAX_DIAGNOSTIC_MESSAGE_CHARS, MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES } from './constants.js';

// ---------------------------------------------------------------------------
// Schema-position resolver types.
// ---------------------------------------------------------------------------

export type SchemaNode = Readonly<Record<string, unknown>>;

/** Sentinel schema position — every resolve call on it degrades to unknown. */
export interface UnknownPosition {
  readonly kind: 'unknown';
}

export interface ObjectPosition {
  readonly kind: 'object';
  readonly node: SchemaNode;
  readonly activeSchemaNodes: ReadonlySet<SchemaNode>;
}

export interface ArrayPosition {
  readonly kind: 'array';
  readonly node: SchemaNode;
  readonly activeSchemaNodes: ReadonlySet<SchemaNode>;
}

export interface CompositePosition {
  readonly kind: 'composite';
  readonly children: readonly SchemaPosition[];
}

export type SchemaPosition = UnknownPosition | ObjectPosition | ArrayPosition | CompositePosition;

export const UNKNOWN_POSITION: UnknownPosition = { kind: 'unknown' };

export interface ResolveResult {
  readonly schemaKnown: boolean;
  readonly childSchemaPosition: SchemaPosition;
}

const UNKNOWN_RESULT: ResolveResult = {
  schemaKnown: false,
  childSchemaPosition: UNKNOWN_POSITION,
};

// ---------------------------------------------------------------------------
// normalizePosition: turn a schema node into a SchemaPosition.
//
// Follows the shared contract: exclusive-$ref rule; per-call activeSchemaNodes
// cycle guard; unsupported schema keywords contribute no schema-known keys
// but do not fail-closed the whole node unless $ref is present.
// ---------------------------------------------------------------------------

export function normalizePosition(
  node: unknown,
  activeSchemaNodes: ReadonlySet<SchemaNode> = new Set<SchemaNode>(),
  rootSchema: SchemaNode | undefined = isObject(node) ? (node as SchemaNode) : undefined,
): SchemaPosition {
  if (!isObject(node)) return UNKNOWN_POSITION;
  const schemaNode = node as SchemaNode;
  if (activeSchemaNodes.has(schemaNode)) return UNKNOWN_POSITION;

  // Exclusive-$ref rule.
  if (typeof schemaNode.$ref === 'string') {
    const siblingCount = Object.keys(schemaNode).filter((k) => k !== '$ref').length;
    if (siblingCount > 0) return UNKNOWN_POSITION;
    if (rootSchema === undefined) return UNKNOWN_POSITION;
    const target = dereferenceJsonPointer(rootSchema, schemaNode.$ref);
    if (target === undefined) return UNKNOWN_POSITION;
    // Include the current node in the activeSchemaNodes set so a
    // back-edge from within the target that lands on `schemaNode` again
    // is detected. The recursive call will itself add `target` to the
    // active set (per its own head-of-function guard), so we don't add
    // it here — otherwise independent branches sharing the same target
    // would be misclassified as cyclic.
    const childActive = union(activeSchemaNodes, [schemaNode]);
    return normalizePosition(target, childActive, rootSchema);
  }

  const childActive = union(activeSchemaNodes, [schemaNode]);
  const positions: SchemaPosition[] = [];

  // Object-shape keyword.
  if (isObject(schemaNode.properties)) {
    positions.push({
      kind: 'object',
      node: schemaNode,
      activeSchemaNodes: childActive,
    });
  }

  // Array-shape keyword. `items` must be a single schema (tuple-form
  // rejected at author time). If items is missing/not-a-schema, the
  // ArrayPosition still resolves to unknown for items, but it exists so
  // resolveArrayItem returns a consistent result.
  if ('items' in schemaNode) {
    positions.push({
      kind: 'array',
      node: schemaNode,
      activeSchemaNodes: childActive,
    });
  }

  // Composition keywords.
  const composedChildren: SchemaPosition[] = [];
  for (const keyword of ['oneOf', 'anyOf', 'allOf'] as const) {
    const branches = schemaNode[keyword];
    if (Array.isArray(branches)) {
      for (const branch of branches) {
        composedChildren.push(normalizePosition(branch, childActive, rootSchema));
      }
    }
  }
  if (composedChildren.length > 0) {
    positions.push(compositeOf(composedChildren));
  }

  if (positions.length === 0) return UNKNOWN_POSITION;
  if (positions.length === 1) return positions[0]!;
  return compositeOf(positions);
}

function compositeOf(children: readonly SchemaPosition[]): SchemaPosition {
  const filtered = children.filter((c) => c.kind !== 'unknown');
  if (filtered.length === 0) return UNKNOWN_POSITION;
  if (filtered.length === 1) return filtered[0]!;
  return { kind: 'composite', children: filtered };
}

// ---------------------------------------------------------------------------
// resolveProperty / resolveArrayItem.
// ---------------------------------------------------------------------------

export function resolveProperty(
  position: SchemaPosition,
  key: string,
  rootSchema?: SchemaNode,
): ResolveResult {
  switch (position.kind) {
    case 'unknown':
      return UNKNOWN_RESULT;
    case 'object': {
      const props = (position.node.properties ?? {}) as Record<string, unknown>;
      if (!Object.prototype.hasOwnProperty.call(props, key)) return UNKNOWN_RESULT;
      const childSchema = props[key];
      const childPosition = normalizePosition(childSchema, position.activeSchemaNodes, rootSchema);
      return { schemaKnown: true, childSchemaPosition: childPosition };
    }
    case 'array':
      return UNKNOWN_RESULT;
    case 'composite': {
      const matches: SchemaPosition[] = [];
      for (const child of position.children) {
        const r = resolveProperty(child, key, rootSchema);
        if (r.schemaKnown) matches.push(r.childSchemaPosition);
      }
      if (matches.length === 0) return UNKNOWN_RESULT;
      return {
        schemaKnown: true,
        childSchemaPosition: compositeOf(matches),
      };
    }
  }
}

export function resolveArrayItem(position: SchemaPosition, rootSchema?: SchemaNode): ResolveResult {
  switch (position.kind) {
    case 'unknown':
    case 'object':
      return UNKNOWN_RESULT;
    case 'array': {
      const items = position.node.items;
      if (!isObject(items)) return UNKNOWN_RESULT;
      const childPosition = normalizePosition(items, position.activeSchemaNodes, rootSchema);
      return { schemaKnown: true, childSchemaPosition: childPosition };
    }
    case 'composite': {
      const matches: SchemaPosition[] = [];
      for (const child of position.children) {
        const r = resolveArrayItem(child, rootSchema);
        if (r.schemaKnown) matches.push(r.childSchemaPosition);
      }
      if (matches.length === 0) return UNKNOWN_RESULT;
      return {
        schemaKnown: true,
        childSchemaPosition: compositeOf(matches),
      };
    }
  }
}

// ---------------------------------------------------------------------------
// Property-name sanitizer (six-rule table). Ancestor segments use this table.
// ---------------------------------------------------------------------------

/**
 * Sanitize a property-name segment for inclusion in a safe path. The
 * six-rule table (deterministic, first match wins):
 *
 *   1. Empty name -> `<empty-name>`.
 *   2. Schema-known name -> RFC 6901 escaped verbatim.
 *   3. Contains unpaired UTF-16 surrogate -> `<invalid-utf16>`.
 *   4. Contains U+0000 -> `<invalid-nul>`.
 *   5. Contains any other control char (U+0001..U+001F or U+007F) ->
 *      `<invalid-control>`.
 *   6. Otherwise -> `<untrusted-property>`.
 */
export function sanitizeSegment(key: string, keyIsSchemaKnown: boolean): string {
  if (key.length === 0) return '<empty-name>';
  if (keyIsSchemaKnown) return rfc6901Escape(key);
  if (containsLoneSurrogate(key)) return '<invalid-utf16>';
  if (key.includes('\u0000')) return '<invalid-nul>';
  if (containsOtherControlChar(key)) return '<invalid-control>';
  return '<untrusted-property>';
}

/**
 * RFC 6901 JSON Pointer segment escape: `~` -> `~0`, `/` -> `~1`.
 * The order (~ first) matters.
 */
export function rfc6901Escape(segment: string): string {
  return segment.replace(/~/g, '~0').replace(/\//g, '~1');
}

// ---------------------------------------------------------------------------
// Shared traversal.
// ---------------------------------------------------------------------------

/**
 * Result of running the shared traversal over a parsed JSON value against
 * a starting schema position. When no violation is found, returns
 * `undefined`; otherwise returns the safe path (before per-path
 * truncation is applied) as an ordered list of already-sanitized segments,
 * plus the terminal marker if the violation is at a property-name
 * position.
 */
export interface StringSafetyViolation {
  readonly segments: readonly string[];
}

/**
 * Recursive shared traversal. Rejects the first NUL or unpaired UTF-16
 * surrogate found in either a string value or a property name. Returns
 * `undefined` if no violation is found.
 */
export function scanStringSafety(
  value: unknown,
  rootSchema: SchemaNode | undefined,
): StringSafetyViolation | undefined {
  const rootPosition = normalizePosition(rootSchema, new Set<SchemaNode>(), rootSchema);
  return scanInternal(value, [], rootPosition, true, rootSchema);
}

function scanInternal(
  value: unknown,
  path: readonly string[],
  schemaPosition: SchemaPosition,
  trustedChain: boolean,
  rootSchema: SchemaNode | undefined,
): StringSafetyViolation | undefined {
  if (typeof value === 'string') {
    if (violatesStringSafety(value)) {
      return { segments: [...path] };
    }
    return undefined;
  }
  if (Array.isArray(value)) {
    const arrResult = resolveArrayItem(schemaPosition, rootSchema);
    const itemTrusted = trustedChain && arrResult.schemaKnown;
    const itemPosition = itemTrusted ? arrResult.childSchemaPosition : UNKNOWN_POSITION;
    for (let i = 0; i < value.length; i += 1) {
      const child = value[i];
      const res = scanInternal(child, [...path, String(i)], itemPosition, itemTrusted, rootSchema);
      if (res) return res;
    }
    return undefined;
  }
  if (isObject(value)) {
    // Sort keys by UTF-16 code units (RFC 8785 canonical order) so the
    // traversal is deterministic and matches the shared pseudocode.
    const keys = Object.keys(value as Record<string, unknown>).sort(utf16CodeUnitCompare);
    for (const key of keys) {
      // Property-name terminal safety: unpaired surrogate or NUL
      // terminate the scan.
      if (containsLoneSurrogate(key)) {
        return { segments: [...path, '<invalid-utf16>'] };
      }
      if (key.includes('\u0000')) {
        return { segments: [...path, '<invalid-nul>'] };
      }
      const propResult = resolveProperty(schemaPosition, key, rootSchema);
      const keyIsSchemaKnown = trustedChain && propResult.schemaKnown;
      const segment = sanitizeSegment(key, keyIsSchemaKnown);
      const childTrusted = keyIsSchemaKnown;
      const childPosition = childTrusted ? propResult.childSchemaPosition : UNKNOWN_POSITION;
      const child = (value as Record<string, unknown>)[key];
      const res = scanInternal(child, [...path, segment], childPosition, childTrusted, rootSchema);
      if (res) return res;
    }
    return undefined;
  }
  return undefined;
}

function utf16CodeUnitCompare(a: string, b: string): number {
  if (a === b) return 0;
  return a < b ? -1 : 1;
}

function violatesStringSafety(s: string): boolean {
  for (let i = 0; i < s.length; i += 1) {
    const c = s.charCodeAt(i);
    if (c === 0x0000) return true;
    if (c >= 0xd800 && c <= 0xdbff) {
      const next = i + 1 < s.length ? s.charCodeAt(i + 1) : NaN;
      if (!(next >= 0xdc00 && next <= 0xdfff)) return true;
      i += 1;
      continue;
    }
    if (c >= 0xdc00 && c <= 0xdfff) return true;
  }
  return false;
}

function containsLoneSurrogate(s: string): boolean {
  for (let i = 0; i < s.length; i += 1) {
    const c = s.charCodeAt(i);
    if (c >= 0xd800 && c <= 0xdbff) {
      const next = i + 1 < s.length ? s.charCodeAt(i + 1) : NaN;
      if (!(next >= 0xdc00 && next <= 0xdfff)) return true;
      i += 1;
      continue;
    }
    if (c >= 0xdc00 && c <= 0xdfff) return true;
  }
  return false;
}

function containsOtherControlChar(s: string): boolean {
  for (let i = 0; i < s.length; i += 1) {
    const c = s.charCodeAt(i);
    if (c === 0x0000) continue; // covered by rule 4
    if ((c >= 0x0001 && c <= 0x001f) || c === 0x007f) return true;
  }
  return false;
}

// ---------------------------------------------------------------------------
// Safe-path composition and per-path truncation.
// ---------------------------------------------------------------------------

/**
 * Compose a list of already-sanitized segments into a JSON-Pointer-shaped
 * safe path. An empty list yields the empty string (root).
 */
export function composeSafePath(segments: readonly string[]): string {
  if (segments.length === 0) return '';
  return '/' + segments.join('/');
}

/**
 * Apply the shared greedy per-path truncation algorithm from
 * `### Safe diagnostic path for Unicode / additional-property rejections`.
 *
 * The result includes the `<code>:` prefix and honors both the UTF-16
 * code-unit and UTF-8 byte caps. When truncation is applied the
 * `<path-truncated>` JSON Pointer segment appears immediately before the
 * preserved final segment: `<code>:/prefix/<path-truncated>/<finalSegment>`.
 */
export interface TruncatedPath {
  readonly wireEntry: string;
  readonly truncated: boolean;
}

const PATH_TRUNCATED_SEGMENT = '/<path-truncated>';

export function renderWireEntry(code: string, segments: readonly string[]): TruncatedPath {
  const prefix = code + ':';
  const budgetChars = MAX_DIAGNOSTIC_MESSAGE_CHARS - prefix.length;
  const budgetBytes = MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES - utf8ByteLength(prefix);

  const fullPath = composeSafePath(segments);
  if (fullPath.length <= budgetChars && utf8ByteLength(fullPath) <= budgetBytes) {
    return { wireEntry: prefix + fullPath, truncated: false };
  }

  // Greedy: find longest prefix of segments (excluding final) that fits
  // together with `/<path-truncated>/<finalSegment>` under both caps.
  if (segments.length === 0) {
    // No final segment; fall back to just the prefix + <path-truncated>.
    return { wireEntry: prefix + PATH_TRUNCATED_SEGMENT, truncated: true };
  }
  const finalSegment = segments[segments.length - 1]!;
  const leading = segments.slice(0, segments.length - 1);
  const suffix = PATH_TRUNCATED_SEGMENT + '/' + finalSegment;
  const suffixChars = suffix.length;
  const suffixBytes = utf8ByteLength(suffix);
  const remainingChars = budgetChars - suffixChars;
  const remainingBytes = budgetBytes - suffixBytes;

  let accepted = '';
  let acceptedBytes = 0;
  for (const seg of leading) {
    const chunk = '/' + seg;
    const chunkBytes = utf8ByteLength(chunk);
    if (
      accepted.length + chunk.length > remainingChars ||
      acceptedBytes + chunkBytes > remainingBytes
    ) {
      break;
    }
    accepted += chunk;
    acceptedBytes += chunkBytes;
  }
  return { wireEntry: prefix + accepted + suffix, truncated: true };
}

export function utf8ByteLength(s: string): number {
  return new TextEncoder().encode(s).byteLength;
}

// ---------------------------------------------------------------------------
// Small utilities.
// ---------------------------------------------------------------------------

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function union<T>(base: ReadonlySet<T>, extras: readonly T[]): Set<T> {
  const result = new Set<T>(base);
  for (const e of extras) result.add(e);
  return result;
}

function dereferenceJsonPointer(root: SchemaNode, ref: string): SchemaNode | undefined {
  if (!ref.startsWith('#')) return undefined;
  const pointer = ref.slice(1);
  if (pointer === '' || pointer === '/') return root;
  if (!pointer.startsWith('/')) return undefined;
  const parts = pointer
    .slice(1)
    .split('/')
    .map((p) => p.replace(/~1/g, '/').replace(/~0/g, '~'));
  let cur: unknown = root;
  for (const p of parts) {
    if (!isObject(cur)) return undefined;
    cur = (cur as Record<string, unknown>)[p];
  }
  return isObject(cur) ? (cur as SchemaNode) : undefined;
}
