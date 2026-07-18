/**
 * Deep descriptor snapshot (issue #50): recursively copies a caller graph
 * into plain null-prototype objects and dense arrays, reading every value
 * exactly once through its descriptor. Structural bounds are enforced
 * DURING the copy, so an oversize graph is rejected before its size can be
 * iterated or allocated. Non-copyable nodes are preserved as plain violation
 * markers so the canonical-domain scan can reject them at their exact
 * positions — no later stage ever touches the caller's graph.
 */

export type CanonicalViolationReason =
  | 'cyclic'
  | 'non-plain-object'
  | 'symbol-key'
  | 'accessor-property'
  | 'non-enumerable-property';

/**
 * Out-of-band marker identity: only marker objects created by the deep
 * snapshot itself are recorded here, so caller data can never collide with
 * the internal marker representation.
 */
const markerReasons = new WeakMap<object, CanonicalViolationReason>();

export function isCanonicalViolationMarker(value: unknown): boolean {
  return typeof value === 'object' && value !== null && markerReasons.has(value);
}

export function canonicalViolationReason(value: unknown): CanonicalViolationReason {
  const reason = markerReasons.get(value as object);
  if (reason === undefined) {
    throw new Error('not a canonical violation marker');
  }
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
}

export interface SnapshotStructuralViolation {
  readonly segments: readonly import('./safe-path.js').PrefixPathSegment[];
  readonly reason: 'depth-exceeded' | 'property-count-exceeded' | 'array-length-exceeded';
}

export type SnapshotOutcome =
  | { readonly ok: true; readonly value: unknown }
  | { readonly ok: false; readonly violation: SnapshotStructuralViolation };

/**
 * Iterative deep snapshot with inline structural bounds. When
 * `rootEntriesOverride` is provided, the ROOT frame uses those pre-captured
 * entries instead of re-reading the root object's own keys (single-capture
 * contract for stateful proxies); nested values are still snapshotted.
 */
export function deepDescriptorSnapshot(
  root: unknown,
  bounds: SnapshotBounds,
  rootEntriesOverride?: readonly { readonly name: string; readonly value: unknown }[],
): SnapshotOutcome {
  interface ChildFrame {
    value: unknown;
    segments: import('./safe-path.js').PrefixPathSegment[];
    depth: number;
    /** The exact ancestor chain of this node — never reconstructed from strings. */
    ancestors: ReadonlySet<object>;
    assign: (child: unknown) => void;
  }

  const childStack: ChildFrame[] = [];

  let rootValue: unknown;

  if (rootEntriesOverride === undefined) {
    childStack.push({
      value: root,
      segments: [],
      depth: 0,
      ancestors: new Set<object>(),
      assign: (child) => {
        rootValue = child;
      },
    });
  } else {
    const rootOut: Record<string, unknown> = Object.create(null);
    rootValue = rootOut;
    const rootAncestors = new Set<object>([root as object]);
    for (let i = rootEntriesOverride.length - 1; i >= 0; i--) {
      const entry = rootEntriesOverride[i];
      childStack.push({
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

  while (childStack.length > 0) {
    const frame = childStack.pop()!;
    const { value, segments, depth, ancestors, assign } = frame;

    const out = snapshotNode(value, segments, depth, ancestors, assign);
    if (out !== null) {
      // Fail-fast: the first structural violation wins.
      return { ok: false, violation: out };
    }
  }

  return { ok: true, value: rootValue };

  function snapshotNode(
    node: unknown,
    segments: import('./safe-path.js').PrefixPathSegment[],
    depth: number,
    ancestors: ReadonlySet<object>,
    assign: (child: unknown) => void,
  ): SnapshotStructuralViolation | null {
    if (typeof node !== 'object' || node === null) {
      assign(node);
      return null;
    }

    if (isCanonicalViolationMarker(node)) {
      // An already-created marker (e.g. from an accessor descriptor) passes
      // through untouched; copying it would erase its identity.
      assign(node);
      return null;
    }

    if (ancestors.has(node)) {
      assign(marker('cyclic'));
      return null;
    }

    if (Array.isArray(node)) {
      if (Object.getPrototypeOf(node) !== Array.prototype) {
        assign(marker('non-plain-object'));
        return null;
      }
      const keys = Reflect.ownKeys(node);
      if (keys.some((key) => typeof key === 'symbol')) {
        assign(marker('symbol-key'));
        return null;
      }
      const lengthDescriptor = Object.getOwnPropertyDescriptor(node, 'length');
      if (
        lengthDescriptor === undefined ||
        lengthDescriptor.enumerable ||
        'get' in lengthDescriptor ||
        'set' in lengthDescriptor ||
        typeof lengthDescriptor.value !== 'number'
      ) {
        assign(marker('non-plain-object'));
        return null;
      }
      const arrayLength = lengthDescriptor.value as number;
      if (depth > bounds.maxDepth) {
        return { segments, reason: 'depth-exceeded' };
      }
      if (arrayLength > bounds.maxArrayItems) {
        return { segments, reason: 'array-length-exceeded' };
      }
      for (const name of keys as string[]) {
        if (name === 'length') {
          continue;
        }
        if (!/^\d+$/.test(name) || Number(name) >= arrayLength) {
          assign(marker('non-enumerable-property'));
          return null;
        }
      }

      const out: unknown[] = new Array(arrayLength);
      assign(out);
      const childAncestors: ReadonlySet<object> = new Set([...ancestors, node]);
      // Push children reversed so they pop in ascending index order.
      for (let i = arrayLength - 1; i >= 0; i--) {
        const index = i;
        childStack.push({
          value: arrayIndexDescriptorValue(node, index),
          segments: [...segments, { name: String(index), isIndex: true }],
          depth: depth + 1,
          ancestors: childAncestors,
          assign: (child) => {
            out[index] = child;
          },
        });
      }
      return null;
    }

    const proto = Object.getPrototypeOf(node);
    if (proto !== Object.prototype && proto !== null) {
      assign(marker('non-plain-object'));
      return null;
    }
    const keys = Reflect.ownKeys(node);
    if (keys.some((key) => typeof key === 'symbol')) {
      assign(marker('symbol-key'));
      return null;
    }
    if (depth > bounds.maxDepth) {
      return { segments, reason: 'depth-exceeded' };
    }
    const names = keys as string[];
    if (names.length > bounds.maxObjectProperties) {
      return { segments, reason: 'property-count-exceeded' };
    }

    const out: Record<string, unknown> = Object.create(null);
    assign(out);
    const childAncestors: ReadonlySet<object> = new Set([...ancestors, node]);
    for (let i = names.length - 1; i >= 0; i--) {
      const name = names[i];
      childStack.push({
        value: propertyDescriptorValue(node, name),
        segments: [...segments, { name }],
        depth: depth + 1,
        ancestors: childAncestors,
        assign: (child) => {
          Object.defineProperty(out, name, { value: child, enumerable: true });
        },
      });
    }
    return null;
  }

  function propertyDescriptorValue(node: object, name: string): unknown {
    const descriptor = Object.getOwnPropertyDescriptor(node, name);
    if (descriptor === undefined || !descriptor.enumerable) {
      return marker('non-enumerable-property');
    }
    if ('get' in descriptor || 'set' in descriptor) {
      return marker('accessor-property');
    }
    return descriptor.value;
  }

  function arrayIndexDescriptorValue(node: unknown[], index: number): unknown {
    const descriptor = Object.getOwnPropertyDescriptor(node, String(index));
    if (descriptor === undefined || !descriptor.enumerable) {
      return marker('non-enumerable-property');
    }
    if ('get' in descriptor || 'set' in descriptor) {
      return marker('accessor-property');
    }
    return descriptor.value;
  }
}
