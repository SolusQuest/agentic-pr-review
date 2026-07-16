/**
 * Shared safe-diagnostic-path machinery for M4 Batch #1 workstreams.
 *
 * Implements the algorithms frozen in the design contract
 * `docs/20_architecture/session-ledger-and-prefix-contract.md`,
 * section `## M4 Batch #1 Frozen Vocabulary`:
 *
 * - `### Schema-position resolver`
 * - `### Safe diagnostic path for Unicode / additional-property rejections`
 * - `### Shared traversal order and stage precedence`
 *
 * Sibling workstreams (#49 C# ledger, #51 metadata) implement the same
 * algorithms in their target language. Byte-equality is asserted by the
 * shared conformance vectors G1..G7 and V1..V3.
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

/**
 * A schema position that carries its rootSchema context. Every resolved
 * child position is derived from the same rootSchema, so callers of
 * `resolveProperty` / `resolveArrayItem` never need to pass the root
 * schema explicitly. This mirrors the frozen resolver contract:
 *   rootPos = normalizePosition(root)
 *   resolveProperty(rootPos, key)  // no root argument required
 */
export interface ObjectPosition {
  readonly kind: 'object';
  readonly node: SchemaNode;
  readonly rootSchema: SchemaNode | undefined;
  readonly activeSchemaNodes: ReadonlySet<SchemaNode>;
}

export interface ArrayPosition {
  readonly kind: 'array';
  readonly node: SchemaNode;
  readonly rootSchema: SchemaNode | undefined;
  readonly activeSchemaNodes: ReadonlySet<SchemaNode>;
}

export interface CompositePosition {
  readonly kind: 'composite';
  readonly rootSchema: SchemaNode | undefined;
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
// normalizePosition.
// ---------------------------------------------------------------------------

/**
 * Turn a schema node into a SchemaPosition. Follows the shared contract:
 * exclusive-$ref rule, per-call activeSchemaNodes cycle guard, and
 * fail-closed behavior when supported schema keywords have a malformed
 * shape.
 */
export function normalizePosition(
  node: unknown,
  activeSchemaNodes: ReadonlySet<SchemaNode> = new Set<SchemaNode>(),
  rootSchema: SchemaNode | undefined = isObject(node) ? (node as SchemaNode) : undefined,
): SchemaPosition {
  if (!isObject(node)) return UNKNOWN_POSITION;
  const schemaNode = node as SchemaNode;
  if (activeSchemaNodes.has(schemaNode)) return UNKNOWN_POSITION;

  // Exclusive-$ref rule. Fail-closed when $ref is present but not a
  // string, or when it is a string but has sibling supported keywords.
  if ('$ref' in schemaNode) {
    if (typeof schemaNode.$ref !== 'string') return UNKNOWN_POSITION;
    const siblingCount = Object.keys(schemaNode).filter((k) => k !== '$ref').length;
    if (siblingCount > 0) return UNKNOWN_POSITION;
    if (rootSchema === undefined) return UNKNOWN_POSITION;
    const target = dereferenceJsonPointer(rootSchema, schemaNode.$ref);
    if (target === undefined) return UNKNOWN_POSITION;
    const childActive = union(activeSchemaNodes, [schemaNode]);
    return normalizePosition(target, childActive, rootSchema);
  }

  const childActive = union(activeSchemaNodes, [schemaNode]);
  const positions: SchemaPosition[] = [];
  let hasSupportedKeyword = false;

  // Object-shape keyword. Fail-closed if `properties` exists but is not
  // an object.
  if ('properties' in schemaNode) {
    hasSupportedKeyword = true;
    if (!isObject(schemaNode.properties)) {
      return UNKNOWN_POSITION;
    }
    positions.push({
      kind: 'object',
      node: schemaNode,
      rootSchema,
      activeSchemaNodes: childActive,
    });
  }

  // Array-shape keyword. `items` must be a single schema (tuple-form is
  // rejected at author time by the conformance checker). Fail-closed if
  // items exists but is not a schema object.
  if ('items' in schemaNode) {
    hasSupportedKeyword = true;
    if (!isObject(schemaNode.items)) {
      return UNKNOWN_POSITION;
    }
    positions.push({
      kind: 'array',
      node: schemaNode,
      rootSchema,
      activeSchemaNodes: childActive,
    });
  }

  // Composition keywords. Fail-closed if the value is present but not an
  // array of schema objects.
  const composedChildren: SchemaPosition[] = [];
  for (const keyword of ['oneOf', 'anyOf', 'allOf'] as const) {
    if (!(keyword in schemaNode)) continue;
    hasSupportedKeyword = true;
    const branches = schemaNode[keyword];
    if (!Array.isArray(branches)) return UNKNOWN_POSITION;
    for (const branch of branches) {
      if (!isObject(branch)) return UNKNOWN_POSITION;
      composedChildren.push(normalizePosition(branch, childActive, rootSchema));
    }
  }
  if (composedChildren.length > 0) {
    positions.push(compositeOf(composedChildren, rootSchema));
  }

  if (!hasSupportedKeyword) return UNKNOWN_POSITION;
  if (positions.length === 0) return UNKNOWN_POSITION;
  if (positions.length === 1) return positions[0]!;
  return compositeOf(positions, rootSchema);
}

function compositeOf(
  children: readonly SchemaPosition[],
  rootSchema: SchemaNode | undefined,
): SchemaPosition {
  const filtered = children.filter((c) => c.kind !== 'unknown');
  if (filtered.length === 0) return UNKNOWN_POSITION;
  if (filtered.length === 1) return filtered[0]!;
  return { kind: 'composite', rootSchema, children: filtered };
}

function positionRoot(position: SchemaPosition): SchemaNode | undefined {
  switch (position.kind) {
    case 'unknown':
      return undefined;
    default:
      return position.rootSchema;
  }
}

// ---------------------------------------------------------------------------
// resolveProperty / resolveArrayItem.
// ---------------------------------------------------------------------------

export function resolveProperty(
  position: SchemaPosition,
  key: string,
  rootSchemaOverride?: SchemaNode,
): ResolveResult {
  const rootSchema = rootSchemaOverride ?? positionRoot(position);
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
        childSchemaPosition: compositeOf(matches, rootSchema),
      };
    }
  }
}

export function resolveArrayItem(
  position: SchemaPosition,
  rootSchemaOverride?: SchemaNode,
): ResolveResult {
  const rootSchema = rootSchemaOverride ?? positionRoot(position);
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
        childSchemaPosition: compositeOf(matches, rootSchema),
      };
    }
  }
}

// ---------------------------------------------------------------------------
// Property-name sanitizer (six-rule table).
// ---------------------------------------------------------------------------

export function sanitizeSegment(key: string, keyIsSchemaKnown: boolean): string {
  if (key.length === 0) return '<empty-name>';
  if (keyIsSchemaKnown) return rfc6901Escape(key);
  if (containsLoneSurrogate(key)) return '<invalid-utf16>';
  if (key.includes('\u0000')) return '<invalid-nul>';
  if (containsOtherControlChar(key)) return '<invalid-control>';
  return '<untrusted-property>';
}

export function rfc6901Escape(segment: string): string {
  return segment.replace(/~/g, '~0').replace(/\//g, '~1');
}

// ---------------------------------------------------------------------------
// Shared traversal.
// ---------------------------------------------------------------------------

export interface StringSafetyViolation {
  readonly segments: readonly string[];
}

export function scanStringSafety(
  value: unknown,
  rootSchema: SchemaNode | undefined,
): StringSafetyViolation | undefined {
  const rootPosition = normalizePosition(rootSchema, new Set<SchemaNode>(), rootSchema);
  type Frame =
    | {
        readonly kind: 'value';
        readonly value: unknown;
        readonly path: readonly string[];
        readonly schemaPosition: SchemaPosition;
        readonly trustedChain: boolean;
      }
    | {
        readonly kind: 'property';
        readonly object: Record<string, unknown>;
        readonly key: string;
        readonly path: readonly string[];
        readonly schemaPosition: SchemaPosition;
        readonly trustedChain: boolean;
      };
  const stack: Frame[] = [
    { kind: 'value', value, path: [], schemaPosition: rootPosition, trustedChain: true },
  ];

  while (stack.length > 0) {
    const frame = stack.pop()!;
    if (frame.kind === 'property') {
      const { object, key, path, schemaPosition, trustedChain } = frame;
      if (containsLoneSurrogate(key)) return { segments: [...path, '<invalid-utf16>'] };
      if (key.includes('\u0000')) return { segments: [...path, '<invalid-nul>'] };
      const propResult = resolveProperty(schemaPosition, key);
      const keyIsSchemaKnown = trustedChain && propResult.schemaKnown;
      stack.push({
        kind: 'value',
        value: object[key],
        path: [...path, sanitizeSegment(key, keyIsSchemaKnown)],
        schemaPosition: keyIsSchemaKnown ? propResult.childSchemaPosition : UNKNOWN_POSITION,
        trustedChain: keyIsSchemaKnown,
      });
      continue;
    }
    const { value: current, path, schemaPosition, trustedChain } = frame;
    if (typeof current === 'string') {
      if (violatesStringSafety(current)) return { segments: [...path] };
      continue;
    }
    if (Array.isArray(current)) {
      const arrResult = resolveArrayItem(schemaPosition);
      const itemTrusted = trustedChain && arrResult.schemaKnown;
      const itemPosition = itemTrusted ? arrResult.childSchemaPosition : UNKNOWN_POSITION;
      for (let i = current.length - 1; i >= 0; i -= 1) {
        stack.push({
          kind: 'value',
          value: current[i],
          path: [...path, String(i)],
          schemaPosition: itemPosition,
          trustedChain: itemTrusted,
        });
      }
      continue;
    }
    if (!isObject(current)) continue;
    const keys = Object.keys(current as Record<string, unknown>).sort(utf16CodeUnitCompare);
    for (let i = keys.length - 1; i >= 0; i -= 1) {
      const key = keys[i]!;
      stack.push({
        kind: 'property',
        object: current as Record<string, unknown>,
        key,
        path,
        schemaPosition,
        trustedChain,
      });
    }
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
    if (c === 0x0000) continue;
    if ((c >= 0x0001 && c <= 0x001f) || c === 0x007f) return true;
  }
  return false;
}

// ---------------------------------------------------------------------------
// Safe-path composition and per-path truncation.
// ---------------------------------------------------------------------------

export function composeSafePath(segments: readonly string[]): string {
  if (segments.length === 0) return '';
  return '/' + segments.join('/');
}

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

  if (segments.length === 0) {
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

/**
 * Dereference an RFC 6901 JSON Pointer against `root`. Handles object
 * property segments, numeric array-index segments (base-10 non-negative,
 * in-range only), and the RFC 6901 empty-name member (`#/` returns
 * undefined unless the root has an empty-name property). `#` returns the
 * root document itself.
 */
export function dereferenceJsonPointer(root: SchemaNode, ref: string): SchemaNode | undefined {
  if (!ref.startsWith('#')) return undefined;
  const pointer = ref.slice(1);
  if (pointer === '') return root;
  if (!pointer.startsWith('/')) return undefined;
  const parts = pointer
    .slice(1)
    .split('/')
    .map((p) => p.replace(/~1/g, '/').replace(/~0/g, '~'));
  let cur: unknown = root;
  for (const p of parts) {
    if (Array.isArray(cur)) {
      // RFC 6901 canonical decimal index grammar: exactly '0' or an
      // integer with no leading zero, no sign, no other characters.
      if (!/^(0|[1-9][0-9]*)$/.test(p)) return undefined;
      const idx = Number(p);
      if (idx >= (cur as unknown[]).length) return undefined;
      cur = (cur as unknown[])[idx];
      continue;
    }
    if (!isObject(cur)) return undefined;
    cur = (cur as Record<string, unknown>)[p];
  }
  return isObject(cur) ? (cur as SchemaNode) : undefined;
}
