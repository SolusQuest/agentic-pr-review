/**
 * Iterative implementation of the shared stage-6 string-safety scan for the
 * ProviderRunMetadataV1 pipeline. Semantically identical to `scanStringSafety`
 * from `src/state-v2/shared-safe-path.ts` but uses an explicit work stack so a
 * deeply-nested value tree cannot exhaust the JavaScript call stack. Path
 * management uses a single mutable stack; traversal memory is O(depth) rather
 * than O(depth^2).
 */

import {
  normalizePosition,
  resolveArrayItem,
  resolveProperty,
  sanitizeSegment,
  UNKNOWN_POSITION,
  type SchemaNode,
  type SchemaPosition,
  type StringSafetyViolation,
} from '../state-v2/shared-safe-path.js';

interface ArrayFrame {
  kind: 'array';
  node: unknown[];
  schemaPosition: SchemaPosition;
  trustedChain: boolean;
  index: number;
  itemPosition: SchemaPosition;
  itemTrusted: boolean;
  segmentPushed: boolean;
}
interface ObjectFrame {
  kind: 'object';
  node: Record<string, unknown>;
  schemaPosition: SchemaPosition;
  trustedChain: boolean;
  keys: string[];
  keyIdx: number;
  segmentPushed: boolean;
}
type Frame = ArrayFrame | ObjectFrame;

export function scanStringSafetyIterative(
  value: unknown,
  rootSchema: SchemaNode | undefined,
): StringSafetyViolation | undefined {
  const rootPosition = normalizePosition(rootSchema, new Set<SchemaNode>(), rootSchema);
  const currentPath: string[] = [];

  // Root check first.
  if (typeof value === 'string') {
    if (violatesStringSafety(value)) return { segments: [] };
    return undefined;
  }
  if (!Array.isArray(value) && !isObject(value)) return undefined;

  const stack: Frame[] = [];
  if (Array.isArray(value)) {
    const itemR = resolveArrayItem(rootPosition);
    const itemTrusted = itemR.schemaKnown; // root trustedChain=true
    stack.push({
      kind: 'array',
      node: value,
      schemaPosition: rootPosition,
      trustedChain: true,
      index: 0,
      itemPosition: itemTrusted ? itemR.childSchemaPosition : UNKNOWN_POSITION,
      itemTrusted,
      segmentPushed: false,
    });
  } else {
    stack.push({
      kind: 'object',
      node: value as Record<string, unknown>,
      schemaPosition: rootPosition,
      trustedChain: true,
      keys: Object.keys(value as Record<string, unknown>).sort(utf16Compare),
      keyIdx: 0,
      segmentPushed: false,
    });
  }

  while (stack.length > 0) {
    const frame = stack[stack.length - 1]!;

    if (frame.kind === 'array') {
      // Pop the previous element's segment if it is still on the path.
      if (frame.segmentPushed) {
        currentPath.pop();
        frame.segmentPushed = false;
      }
      if (frame.index >= frame.node.length) {
        stack.pop();
        continue;
      }
      const child = frame.node[frame.index]!;
      currentPath.push(String(frame.index));
      frame.segmentPushed = true;
      frame.index += 1;
      const violation = descendChild(
        child,
        frame.itemPosition,
        frame.itemTrusted,
        currentPath,
        stack,
      );
      if (violation) return violation;
      continue;
    }

    // Object frame.
    if (frame.segmentPushed) {
      currentPath.pop();
      frame.segmentPushed = false;
    }
    if (frame.keyIdx >= frame.keys.length) {
      stack.pop();
      continue;
    }
    const key = frame.keys[frame.keyIdx]!;
    frame.keyIdx += 1;
    if (containsLoneSurrogate(key)) {
      currentPath.push('<invalid-utf16>');
      return { segments: [...currentPath] };
    }
    if (key.includes('\u0000')) {
      currentPath.push('<invalid-nul>');
      return { segments: [...currentPath] };
    }
    const propR = resolveProperty(frame.schemaPosition, key);
    const keyIsSchemaKnown = frame.trustedChain && propR.schemaKnown;
    const segment = sanitizeSegment(key, keyIsSchemaKnown);
    const childTrusted = keyIsSchemaKnown;
    const childPosition = childTrusted ? propR.childSchemaPosition : UNKNOWN_POSITION;
    currentPath.push(segment);
    frame.segmentPushed = true;
    const child = frame.node[key];
    const violation = descendChild(child, childPosition, childTrusted, currentPath, stack);
    if (violation) return violation;
  }
  return undefined;
}

function descendChild(
  child: unknown,
  childPosition: SchemaPosition,
  trusted: boolean,
  currentPath: string[],
  stack: Frame[],
): StringSafetyViolation | undefined {
  if (typeof child === 'string') {
    if (violatesStringSafety(child)) {
      return { segments: [...currentPath] };
    }
    return undefined;
  }
  if (Array.isArray(child)) {
    const itemR = resolveArrayItem(childPosition);
    const itemTrusted = trusted && itemR.schemaKnown;
    stack.push({
      kind: 'array',
      node: child,
      schemaPosition: childPosition,
      trustedChain: trusted,
      index: 0,
      itemPosition: itemTrusted ? itemR.childSchemaPosition : UNKNOWN_POSITION,
      itemTrusted,
      segmentPushed: false,
    });
    return undefined;
  }
  if (isObject(child)) {
    stack.push({
      kind: 'object',
      node: child as Record<string, unknown>,
      schemaPosition: childPosition,
      trustedChain: trusted,
      keys: Object.keys(child as Record<string, unknown>).sort(utf16Compare),
      keyIdx: 0,
      segmentPushed: false,
    });
    return undefined;
  }
  return undefined;
}

function isObject(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function utf16Compare(a: string, b: string): number {
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
