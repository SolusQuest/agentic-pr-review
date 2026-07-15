/**
 * Iterative implementation of the shared stage-6 string-safety scan for the
 * ProviderRunMetadataV1 pipeline. Semantically identical to
 * `scanStringSafety` from `src/state-v2/shared-safe-path.ts` but uses an
 * explicit work stack so a deeply-nested value tree (up to
 * `METADATA_MAX_BYTES` worth of `[[[...[0]...]]]`) cannot exhaust the
 * JavaScript call stack.
 *
 * Segment sanitization uses the same shared helpers as the recursive version:
 * a schema-known key appears verbatim after RFC 6901 escaping; every other
 * key is replaced with the closed marker (`<invalid-utf16>`, `<invalid-nul>`,
 * `<invalid-control>`, `<empty-name>`, or `<untrusted-property>`).
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

export function scanStringSafetyIterative(
  value: unknown,
  rootSchema: SchemaNode | undefined,
): StringSafetyViolation | undefined {
  const rootPosition = normalizePosition(rootSchema, new Set<SchemaNode>(), rootSchema);

  interface Frame {
    node: unknown;
    schemaPosition: SchemaPosition;
    trustedChain: boolean;
    path: readonly string[];
    // For arrays: current child index. For objects: sorted key iterator index
    // plus the pre-sorted key list.
    kind: 'root' | 'array' | 'object';
    childIdx: number;
    keys: string[]; // populated for object frames
  }

  const initial: Frame = {
    node: value,
    schemaPosition: rootPosition,
    trustedChain: true,
    path: [],
    kind: 'root',
    childIdx: 0,
    keys: [],
  };
  const stack: Frame[] = [initial];

  while (stack.length > 0) {
    const frame = stack[stack.length - 1]!;
    const val = frame.node;

    if (frame.kind === 'root' && frame.childIdx === 0) {
      const check = checkStringOrEnter(val, frame);
      if (check.type === 'violation') return check.violation;
      if (check.type === 'done') {
        stack.pop();
        continue;
      }
      // 'enter' -- push a container child for later descent handling
      frame.childIdx = 1; // mark root processed
      // We convert the root frame into the container that will be popped
      // when its children complete.
      if (Array.isArray(val)) {
        // Re-frame the root as an array container.
        stack[stack.length - 1] = {
          ...frame,
          kind: 'array',
          childIdx: 0,
        };
        continue;
      }
      // Object.
      const keys = Object.keys(val as Record<string, unknown>).sort(utf16Compare);
      stack[stack.length - 1] = {
        ...frame,
        kind: 'object',
        childIdx: 0,
        keys,
      };
      continue;
    }

    if (frame.kind === 'array') {
      const arr = frame.node as unknown[];
      if (frame.childIdx >= arr.length) {
        stack.pop();
        continue;
      }
      const idx = frame.childIdx;
      frame.childIdx += 1;
      const itemResult = resolveArrayItem(frame.schemaPosition);
      const trusted = frame.trustedChain && itemResult.schemaKnown;
      const childPosition = trusted ? itemResult.childSchemaPosition : UNKNOWN_POSITION;
      const child = arr[idx];
      const childPath = [...frame.path, String(idx)];
      const step = descendChild(child, childPosition, trusted, childPath);
      if (step.type === 'violation') return step.violation;
      if (step.type === 'push') stack.push(step.frame);
      continue;
    }

    if (frame.kind === 'object') {
      const obj = frame.node as Record<string, unknown>;
      if (frame.childIdx >= frame.keys.length) {
        stack.pop();
        continue;
      }
      const key = frame.keys[frame.childIdx]!;
      frame.childIdx += 1;
      if (containsLoneSurrogate(key)) {
        return { segments: [...frame.path, '<invalid-utf16>'] };
      }
      if (key.includes('\u0000')) {
        return { segments: [...frame.path, '<invalid-nul>'] };
      }
      const propResult = resolveProperty(frame.schemaPosition, key);
      const keyIsSchemaKnown = frame.trustedChain && propResult.schemaKnown;
      const segment = sanitizeSegment(key, keyIsSchemaKnown);
      const childTrusted = keyIsSchemaKnown;
      const childPosition = childTrusted ? propResult.childSchemaPosition : UNKNOWN_POSITION;
      const child = obj[key];
      const childPath = [...frame.path, segment];
      const step = descendChild(child, childPosition, childTrusted, childPath);
      if (step.type === 'violation') return step.violation;
      if (step.type === 'push') stack.push(step.frame);
      continue;
    }
  }
  return undefined;

  function checkStringOrEnter(
    v: unknown,
    frame: Frame,
  ):
    | { type: 'violation'; violation: StringSafetyViolation }
    | { type: 'done' }
    | { type: 'enter' } {
    if (typeof v === 'string') {
      if (violatesStringSafety(v)) {
        return { type: 'violation', violation: { segments: [...frame.path] } };
      }
      return { type: 'done' };
    }
    if (Array.isArray(v)) return { type: 'enter' };
    if (isObject(v)) return { type: 'enter' };
    return { type: 'done' };
  }

  function descendChild(
    child: unknown,
    childPosition: SchemaPosition,
    trusted: boolean,
    childPath: readonly string[],
  ):
    | { type: 'violation'; violation: StringSafetyViolation }
    | { type: 'push'; frame: Frame }
    | { type: 'done' } {
    if (typeof child === 'string') {
      if (violatesStringSafety(child)) {
        return { type: 'violation', violation: { segments: [...childPath] } };
      }
      return { type: 'done' };
    }
    if (Array.isArray(child)) {
      return {
        type: 'push',
        frame: {
          node: child,
          schemaPosition: childPosition,
          trustedChain: trusted,
          path: childPath,
          kind: 'array',
          childIdx: 0,
          keys: [],
        },
      };
    }
    if (isObject(child)) {
      const keys = Object.keys(child as Record<string, unknown>).sort(utf16Compare);
      return {
        type: 'push',
        frame: {
          node: child,
          schemaPosition: childPosition,
          trustedChain: trusted,
          path: childPath,
          kind: 'object',
          childIdx: 0,
          keys,
        },
      };
    }
    return { type: 'done' };
  }
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
