import { describe, expect, it } from 'vitest';
import { computeTemplateId } from './index.js';

/**
 * TOCTOU coverage (round 7): validation and canonical emission must see
 * exactly the same values — the deep descriptor snapshot is the only graph
 * any later stage reads. Caller `get` traps must never fire.
 */

describe('deep snapshot eliminates validation/emission TOCTOU', () => {
  it('a nested object proxy cannot present different content to emission', () => {
    let getInvoked = 0;
    const inner = new Proxy(
      { benign: 1 },
      {
        getOwnPropertyDescriptor() {
          return {
            value: 1,
            enumerable: true,
            writable: true,
            configurable: true,
          };
        },
        get() {
          getInvoked++;
          return { stuffed: Array.from({ length: 300 }, (_, i) => ['k' + i, i]) };
        },
        ownKeys() {
          return ['benign'];
        },
      },
    );

    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: inner,
    });
    // Emission must use the descriptor's value (the benign object), never the
    // get trap's 300-property object.
    expect(getInvoked).toBe(0);
    expect(result.ok).toBe(true);
  });

  it('a nested array proxy cannot smuggle a different element into emission', () => {
    let getInvoked = 0;
    const inner = new Proxy([1], {
      getOwnPropertyDescriptor(target, prop) {
        if (prop === '0') {
          return { value: 1, enumerable: true, writable: true, configurable: true };
        }
        return Reflect.getOwnPropertyDescriptor(target, prop);
      },
      get(target, prop) {
        getInvoked++;
        if (prop === '0') {
          return 'smuggled';
        }
        return Reflect.get(target, prop);
      },
    });

    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: inner,
    });
    expect(getInvoked).toBe(0);
    expect(result.ok).toBe(true);
  });

  it('a stateful proxy yields a stable outcome across repeated calls', () => {
    const inner = new Proxy(
      { benign: 1 },
      {
        get() {
          return { stuffed: 1 };
        },
      },
    );
    const first = computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition: inner });
    const second = computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition: inner });
    expect(first.ok).toBe(true);
    expect(second.ok).toBe(true);
    if (first.ok && second.ok) {
      expect(first.value).toBe(second.value);
    }
  });

  it('emission never reads through a caller getter anywhere in the graph', () => {
    let invoked = false;
    const graph = {
      nested: {
        deep: [1, 2, { leaf: 'x' }],
      },
    };
    const proxy = new Proxy(graph, {
      get(target, prop) {
        invoked = true;
        return (target as Record<string | symbol, unknown>)[prop];
      },
    });
    const result = computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition: proxy });
    expect(result.ok).toBe(true);
    expect(invoked).toBe(false);
  });
});

describe('marker brand never collides with legitimate open JSON', () => {
  it('a caller object shaped like the old marker is plain data', () => {
    for (const reason of ['cyclic', 'non-plain-object', 'arbitrary-user-string']) {
      const result = computeTemplateId({
        schemaVersion: 1,
        templateVersion: 1,
        definition: { __canonicalViolation__: reason },
      });
      expect(result.ok).toBe(true);
    }
  });

  it('a nested class instance is a canonical-domain rejection, not a structure one', () => {
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: new Date(0),
    });
    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.errors[0].code).toBe('prefix-canonical-input-rejected');
    }
  });
});
