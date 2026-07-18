/**
 * Deep descriptor snapshot (issue #50): recursively copies a caller graph
 * into plain null-prototype objects and dense arrays, reading every value
 * exactly once through its descriptor. Non-copyable nodes are preserved as
 * plain violation markers so the canonical-domain scan can reject them at
 * their exact positions — no later stage ever touches the caller's graph.
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

export function deepDescriptorSnapshot(value: unknown): unknown {
  return snapshot(value, new Set<object>());

  function snapshot(node: unknown, ancestors: Set<object>): unknown {
    if (typeof node !== 'object' || node === null) {
      // Primitives (including undefined / bigint / symbol / function) pass
      // through unchanged; the canonical-domain scan owns their rejection.
      return node;
    }

    if (ancestors.has(node)) {
      return marker('cyclic');
    }

    if (Array.isArray(node)) {
      if (Object.getPrototypeOf(node) !== Array.prototype) {
        return marker('non-plain-object');
      }
      if (Object.getOwnPropertySymbols(node).length > 0) {
        return marker('symbol-key');
      }
      const lengthDescriptor = Object.getOwnPropertyDescriptor(node, 'length');
      if (
        lengthDescriptor === undefined ||
        lengthDescriptor.enumerable ||
        'get' in lengthDescriptor ||
        'set' in lengthDescriptor ||
        typeof lengthDescriptor.value !== 'number'
      ) {
        return marker('non-plain-object');
      }
      const arrayLength = lengthDescriptor.value as number;
      for (const name of Object.getOwnPropertyNames(node)) {
        if (name === 'length') {
          continue;
        }
        if (!/^\d+$/.test(name) || Number(name) >= arrayLength) {
          return marker('non-enumerable-property');
        }
      }

      ancestors.add(node);
      try {
        const out: unknown[] = new Array(arrayLength);
        for (let i = 0; i < arrayLength; i++) {
          const descriptor = Object.getOwnPropertyDescriptor(node, String(i));
          if (descriptor === undefined || !descriptor.enumerable) {
            out[i] = marker('non-enumerable-property');
            continue;
          }
          if ('get' in descriptor || 'set' in descriptor) {
            out[i] = marker('accessor-property');
            continue;
          }
          out[i] = snapshot(descriptor.value, ancestors);
        }
        return out;
      } finally {
        ancestors.delete(node);
      }
    }

    const proto = Object.getPrototypeOf(node);
    if (proto !== Object.prototype && proto !== null) {
      return marker('non-plain-object');
    }
    if (Object.getOwnPropertySymbols(node).length > 0) {
      return marker('symbol-key');
    }

    ancestors.add(node);
    try {
      const out: Record<string, unknown> = Object.create(null);
      for (const name of Object.getOwnPropertyNames(node)) {
        const descriptor = Object.getOwnPropertyDescriptor(node, name)!;
        if ('get' in descriptor || 'set' in descriptor) {
          Object.defineProperty(out, name, {
            value: marker('accessor-property'),
            enumerable: true,
          });
          continue;
        }
        if (!descriptor.enumerable) {
          Object.defineProperty(out, name, {
            value: marker('non-enumerable-property'),
            enumerable: true,
          });
          continue;
        }
        Object.defineProperty(out, name, {
          value: snapshot(descriptor.value, ancestors),
          enumerable: true,
        });
      }
      return out;
    } finally {
      ancestors.delete(node);
    }
  }
}
