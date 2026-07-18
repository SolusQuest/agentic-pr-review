/**
 * Descriptor-only graph validation and snapshotting for prefix envelopes.
 * Caller-owned properties are observed once, in canonical traversal order.
 * Once a conservative canonical-size lower bound exceeds the configured cap,
 * traversal continues in validation-only mode without retaining more output
 * containers or child slots.
 */

import type { PrefixPathSegment } from './safe-path.js';

export type CanonicalViolationReason =
  | 'cyclic'
  | 'non-plain-object'
  | 'symbol-key'
  | 'accessor-property'
  | 'non-enumerable-property';

const markerReasons = new WeakMap<object, CanonicalViolationReason>();

export function isCanonicalViolationMarker(value: unknown): boolean {
  return typeof value === 'object' && value !== null && markerReasons.has(value);
}

export function canonicalViolationReason(value: unknown): CanonicalViolationReason {
  const reason = markerReasons.get(value as object);
  if (reason === undefined) throw new Error('not a canonical violation marker');
  return reason;
}

function marker(reason: CanonicalViolationReason): Record<string, never> {
  const out: Record<string, never> = Object.create(null);
  markerReasons.set(out, reason);
  return out;
}

export interface SnapshotBounds {
  readonly maxDepth: number;
  readonly maxObjectProperties: number;
  readonly maxArrayItems: number;
  readonly maxRetainedCanonicalBytes?: number;
}

export interface SnapshotStats {
  retainedContainerSlots: number;
}

export interface SnapshotStructuralViolation {
  readonly segments: readonly PrefixPathSegment[];
  readonly reason: 'depth-exceeded' | 'property-count-exceeded' | 'array-length-exceeded';
}

export type SnapshotOutcome =
  | {
      readonly ok: true;
      readonly value: unknown;
      readonly retentionExceeded: boolean;
      readonly canonicalViolation?: { readonly segments: readonly PrefixPathSegment[] };
    }
  | { readonly ok: false; readonly violation: SnapshotStructuralViolation };

/** True only for the canonical decimal spelling of an in-range array index. */
export function isCanonicalArrayIndexName(name: string, length: number): boolean {
  return /^(?:0|[1-9]\d*)$/.test(name) && Number(name) < length;
}

interface NodeFrame {
  readonly kind: 'node';
  readonly value: unknown;
  readonly segments: PrefixPathSegment[];
  readonly depth: number;
  readonly ancestors: ReadonlySet<object>;
  readonly assign: (child: unknown) => void;
}

interface ArrayFrame {
  readonly kind: 'array';
  readonly node: unknown[];
  readonly out: unknown[] | undefined;
  readonly segments: PrefixPathSegment[];
  readonly depth: number;
  readonly ancestors: ReadonlySet<object>;
  readonly index: number;
  readonly length: number;
}

interface ObjectFrame {
  readonly kind: 'object';
  readonly node: object;
  readonly out: Record<string, unknown> | undefined;
  readonly segments: PrefixPathSegment[];
  readonly depth: number;
  readonly ancestors: ReadonlySet<object>;
  readonly names: readonly string[];
  readonly index: number;
}

type Frame = NodeFrame | ArrayFrame | ObjectFrame;

/**
 * Iterative depth-first snapshot. A continuation captures exactly one child
 * descriptor before visiting it, so array indices are observed 0..n and
 * object properties in unsigned UTF-16 order without preallocating a wide
 * frame stack.
 */
export function deepDescriptorSnapshot(
  root: unknown,
  bounds: SnapshotBounds,
  rootEntriesOverride?: readonly { readonly name: string; readonly value: unknown }[],
  replacements?: readonly { readonly source: object; readonly target: object }[],
  stats?: SnapshotStats,
): SnapshotOutcome {
  const frames: Frame[] = [];
  const snapshotMemo = new WeakMap<object, unknown>();
  const validationOnlyDone = new WeakSet<object>();
  const replacementMap = new WeakMap<object, object>();
  for (const replacement of replacements ?? [])
    replacementMap.set(replacement.source, replacement.target);

  let rootValue: unknown;
  let lowerBound = 0;
  let retaining = true;
  let canonicalViolation: { segments: readonly PrefixPathSegment[] } | undefined;
  if (stats !== undefined) stats.retainedContainerSlots = 0;

  const addLowerBound = (bytes: number): void => {
    if (!retaining || bounds.maxRetainedCanonicalBytes === undefined) return;
    lowerBound += bytes;
    if (lowerBound > bounds.maxRetainedCanonicalBytes) retaining = false;
  };
  const retainSlots = (count: number): void => {
    if (stats !== undefined) stats.retainedContainerSlots += count;
  };
  const noteCanonical = (segments: readonly PrefixPathSegment[]): void => {
    canonicalViolation ??= { segments };
  };

  if (rootEntriesOverride === undefined) {
    frames.push({
      kind: 'node',
      value: root,
      segments: [],
      depth: 0,
      ancestors: new Set<object>(),
      assign: (child) => {
        rootValue = child;
      },
    });
  } else {
    const rootEntries = [...rootEntriesOverride].sort((a, b) => compareNames(a.name, b.name));
    addLowerBound(containerLowerBound(rootEntries.length));
    const rootOut: Record<string, unknown> = Object.create(null);
    rootValue = rootOut;
    retainSlots(rootEntries.length);
    const rootAncestors = new Set<object>([root as object]);
    frames.push({
      kind: 'object',
      node: root as object,
      out: rootOut,
      segments: [],
      depth: 0,
      ancestors: rootAncestors,
      names: rootEntries.map((entry) => entry.name),
      index: rootEntries.length,
    });
    // Root descriptors were captured by the caller. Schedule their values in
    // ascending order without observing the root again.
    for (let index = rootEntries.length - 1; index >= 0; index--) {
      const entry = rootEntries[index];
      frames.push({
        kind: 'node',
        value: entry.value,
        segments: [{ name: entry.name }],
        depth: 1,
        ancestors: rootAncestors,
        assign: (child) => {
          Object.defineProperty(rootOut, entry.name, { value: child, enumerable: true });
        },
      });
    }
  }

  while (frames.length > 0) {
    const frame = frames.pop()!;
    if (frame.kind === 'node') {
      const violation = visitNode(frame);
      if (violation !== null) return { ok: false, violation };
    } else if (frame.kind === 'array') {
      continueArray(frame);
    } else {
      continueObject(frame);
    }
  }

  return {
    ok: true,
    value: rootValue,
    retentionExceeded: !retaining,
    ...(canonicalViolation === undefined ? {} : { canonicalViolation }),
  };

  function visitNode(frame: NodeFrame): SnapshotStructuralViolation | null {
    const { segments, depth, ancestors, assign } = frame;
    const rawNode = frame.value;
    if (typeof rawNode !== 'object' || rawNode === null) {
      addPrimitiveLowerBound(rawNode);
      if (typeof rawNode === 'string' && hasUnpairedSurrogate(rawNode)) noteCanonical(segments);
      else if (typeof rawNode === 'number' && !Number.isFinite(rawNode)) noteCanonical(segments);
      else if (
        rawNode !== null &&
        typeof rawNode !== 'string' &&
        typeof rawNode !== 'number' &&
        typeof rawNode !== 'boolean'
      )
        noteCanonical(segments);
      if (retaining) assign(rawNode);
      return null;
    }

    const node = replacementMap.get(rawNode) ?? rawNode;
    if (depth > bounds.maxDepth) return { segments, reason: 'depth-exceeded' };
    if (isCanonicalViolationMarker(node)) {
      noteCanonical(segments);
      if (retaining) assign(node);
      return null;
    }
    if (ancestors.has(node)) {
      noteCanonical(segments);
      if (retaining) assign(marker('cyclic'));
      return null;
    }
    if (validationOnlyDone.has(node)) return null;
    const memoized = snapshotMemo.get(node);
    if (memoized !== undefined) {
      if (retaining) assign(memoized);
      return null;
    }

    if (Array.isArray(node)) return startArray(node, frame);
    return startObject(node, frame);
  }

  function startArray(node: unknown[], frame: NodeFrame): SnapshotStructuralViolation | null {
    const lengthDescriptor = Object.getOwnPropertyDescriptor(node, 'length');
    if (
      lengthDescriptor === undefined ||
      lengthDescriptor.enumerable ||
      'get' in lengthDescriptor ||
      'set' in lengthDescriptor ||
      typeof lengthDescriptor.value !== 'number'
    ) {
      noteCanonical(frame.segments);
      if (retaining) frame.assign(marker('non-plain-object'));
      return null;
    }
    const length = lengthDescriptor.value as number;
    if (length > bounds.maxArrayItems)
      return { segments: frame.segments, reason: 'array-length-exceeded' };
    const keys = Reflect.ownKeys(node);
    if (Object.getPrototypeOf(node) !== Array.prototype) {
      noteCanonical(frame.segments);
      if (retaining) frame.assign(marker('non-plain-object'));
      return null;
    }
    if (keys.some((key) => typeof key === 'symbol')) {
      noteCanonical(frame.segments);
      if (retaining) frame.assign(marker('symbol-key'));
      return null;
    }
    for (const name of keys as string[]) {
      if (name !== 'length' && !isCanonicalArrayIndexName(name, length)) {
        noteCanonical(frame.segments);
        if (retaining) frame.assign(marker('non-plain-object'));
        return null;
      }
    }

    addLowerBound(arrayLowerBound(length));
    const out = retaining ? new Array<unknown>(length) : undefined;
    if (out !== undefined) {
      frame.assign(out);
      snapshotMemo.set(node, out);
      retainSlots(length);
    }
    const ancestors = new Set([...frame.ancestors, node]);
    frames.push({
      kind: 'array',
      node,
      out,
      segments: frame.segments,
      depth: frame.depth,
      ancestors,
      index: 0,
      length,
    });
    return null;
  }

  function continueArray(frame: ArrayFrame): void {
    if (frame.index >= frame.length) {
      if (frame.out === undefined) validationOnlyDone.add(frame.node);
      return;
    }
    const index = frame.index;
    const segments = [...frame.segments, { name: String(index), isIndex: true }];
    const descriptor = Object.getOwnPropertyDescriptor(frame.node, String(index));
    frames.push({ ...frame, index: index + 1 });
    if (descriptor === undefined || !descriptor.enumerable) {
      noteCanonical(segments);
      if (retaining && frame.out !== undefined)
        frame.out[index] = marker('non-enumerable-property');
      return;
    }
    if ('get' in descriptor || 'set' in descriptor) {
      noteCanonical(segments);
      if (retaining && frame.out !== undefined) frame.out[index] = marker('accessor-property');
      return;
    }
    frames.push({
      kind: 'node',
      value: descriptor.value,
      segments,
      depth: frame.depth + 1,
      ancestors: frame.ancestors,
      assign: (child) => {
        if (frame.out !== undefined) frame.out[index] = child;
      },
    });
  }

  function startObject(node: object, frame: NodeFrame): SnapshotStructuralViolation | null {
    const keys = Reflect.ownKeys(node);
    const names = keys.filter((key): key is string => typeof key === 'string').sort(compareNames);
    if (names.length > bounds.maxObjectProperties)
      return { segments: frame.segments, reason: 'property-count-exceeded' };
    const proto = Object.getPrototypeOf(node);
    if (proto !== Object.prototype && proto !== null) {
      noteCanonical(frame.segments);
      if (retaining) frame.assign(marker('non-plain-object'));
      return null;
    }
    if (keys.some((key) => typeof key === 'symbol')) {
      noteCanonical(frame.segments);
      if (retaining) frame.assign(marker('symbol-key'));
      return null;
    }

    addLowerBound(containerLowerBound(names.length));
    const out: Record<string, unknown> | undefined = retaining ? Object.create(null) : undefined;
    if (out !== undefined) {
      frame.assign(out);
      snapshotMemo.set(node, out);
      retainSlots(names.length);
    }
    const ancestors = new Set([...frame.ancestors, node]);
    frames.push({
      kind: 'object',
      node,
      out,
      segments: frame.segments,
      depth: frame.depth,
      ancestors,
      names,
      index: 0,
    });
    return null;
  }

  function continueObject(frame: ObjectFrame): void {
    if (frame.index >= frame.names.length) {
      if (frame.out === undefined) validationOnlyDone.add(frame.node);
      return;
    }
    const name = frame.names[frame.index];
    const segments = [...frame.segments, { name }];
    frames.push({ ...frame, index: frame.index + 1 });
    if (hasUnpairedSurrogate(name)) noteCanonical(segments);
    const descriptor = Object.getOwnPropertyDescriptor(frame.node, name);
    if (descriptor === undefined || !descriptor.enumerable) {
      noteCanonical(segments);
      if (retaining && frame.out !== undefined) {
        Object.defineProperty(frame.out, name, {
          value: marker('non-enumerable-property'),
          enumerable: true,
        });
      }
      return;
    }
    if ('get' in descriptor || 'set' in descriptor) {
      noteCanonical(segments);
      if (retaining && frame.out !== undefined) {
        Object.defineProperty(frame.out, name, {
          value: marker('accessor-property'),
          enumerable: true,
        });
      }
      return;
    }
    frames.push({
      kind: 'node',
      value: descriptor.value,
      segments,
      depth: frame.depth + 1,
      ancestors: frame.ancestors,
      assign: (child) => {
        if (frame.out !== undefined)
          Object.defineProperty(frame.out, name, { value: child, enumerable: true });
      },
    });
  }

  function addPrimitiveLowerBound(value: unknown): void {
    if (value === null) addLowerBound(4);
    else if (value === true) addLowerBound(4);
    else if (value === false) addLowerBound(5);
    else if (typeof value === 'string') addLowerBound(2);
    else addLowerBound(1);
  }
}

function arrayLowerBound(length: number): number {
  return 2 + Math.max(0, length - 1);
}

function containerLowerBound(properties: number): number {
  // Empty quoted key + colon per property, braces, and separators.
  return 2 + properties * 3 + Math.max(0, properties - 1);
}

function compareNames(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}

function hasUnpairedSurrogate(value: string): boolean {
  for (let index = 0; index < value.length; index++) {
    const code = value.charCodeAt(index);
    if (code >= 0xd800 && code <= 0xdbff) {
      if (index + 1 >= value.length) return true;
      const low = value.charCodeAt(index + 1);
      if (low < 0xdc00 || low > 0xdfff) return true;
      index++;
    } else if (code >= 0xdc00 && code <= 0xdfff) return true;
  }
  return false;
}
