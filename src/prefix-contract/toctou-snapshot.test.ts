import { describe, expect, it } from 'vitest';
import { computeTemplateId, computeToolDefinitionId } from './index.js';
import { deepDescriptorSnapshot, type SnapshotStats } from './deep-snapshot.js';

/**
 * TOCTOU coverage (round 7): validation and canonical emission must see
 * exactly the same values — the deep descriptor snapshot is the only graph
 * any later stage reads. Caller `get` traps must never fire.
 */

describe('deep snapshot eliminates validation/emission TOCTOU', () => {
  for (const extraName of ['00', '01', '000']) {
    it(`rejects non-canonical open-array key ${extraName} without digest aliasing`, () => {
      const base = [1, 2];
      const baseline = computeTemplateId({
        schemaVersion: 1,
        templateVersion: 1,
        definition: base,
      });
      const mutated = [1, 2];
      Object.defineProperty(mutated, extraName, { value: 'hidden', enumerable: true });
      const result = computeTemplateId({
        schemaVersion: 1,
        templateVersion: 1,
        definition: mutated,
      });

      expect(baseline.ok).toBe(true);
      expect(result).toEqual({
        ok: false,
        errors: [{ code: 'prefix-canonical-input-rejected', path: '/definition' }],
      });
    });
  }

  it('rejects a non-canonical open-array accessor without invoking it', () => {
    const value = [1, 2];
    let reads = 0;
    Object.defineProperty(value, '00', {
      get: () => {
        reads++;
        return 'hidden';
      },
      enumerable: true,
    });

    expect(computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition: value })).toEqual({
      ok: false,
      errors: [{ code: 'prefix-canonical-input-rejected', path: '/definition' }],
    });
    expect(reads).toBe(0);
  });

  for (const descriptor of ['data', 'accessor'] as const) {
    it(`rejects a non-canonical definitions-array ${descriptor} property structurally`, () => {
      const definitions = [{ name: 'a', description: 'd', inputSchema: {} }];
      let reads = 0;
      Object.defineProperty(
        definitions,
        '00',
        descriptor === 'data'
          ? { value: definitions[0], enumerable: true }
          : {
              get: () => {
                reads++;
                return definitions[0];
              },
              enumerable: true,
            },
      );

      expect(computeToolDefinitionId({ schemaVersion: 1, toolsetVersion: 1, definitions })).toEqual(
        {
          ok: false,
          errors: [{ code: 'prefix-envelope-invalid', path: '/definitions' }],
        },
      );
      expect(reads).toBe(0);
    });
  }

  it('does not confuse slash-delimited property paths with nested paths', () => {
    const shared = { x: 1 };
    const aliased = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { 'a/b': shared, a: { b: shared } },
    });
    const dealiased = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { 'a/b': { x: 1 }, a: { b: { x: 1 } } },
    });

    expect(aliased.ok).toBe(true);
    expect(dealiased.ok).toBe(true);
    if (aliased.ok && dealiased.ok) {
      expect(aliased.value).toBe(dealiased.value);
    }
  });

  it('does not confuse a hash-number property path with an array index path', () => {
    const shared = { x: 1 };
    const aliased = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { 'items/#0': shared, items: [shared] },
    });
    const dealiased = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { 'items/#0': { x: 1 }, items: [{ x: 1 }] },
    });

    expect(aliased.ok).toBe(true);
    expect(dealiased.ok).toBe(true);
    if (aliased.ok && dealiased.ok) {
      expect(aliased.value).toBe(dealiased.value);
    }
  });

  it('still rejects a true cycle inside a path-collision graph', () => {
    const cyclic: Record<string, unknown> = { x: 1 };
    cyclic.self = cyclic;
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { 'a/b': cyclic, a: { b: cyclic } },
    });

    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.errors[0].code).toBe('prefix-canonical-input-rejected');
    }
  });

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

  it('captures nested own keys exactly once', () => {
    let ownKeysCalls = 0;
    const definition = new Proxy(
      { benign: 1 },
      {
        ownKeys(target) {
          ownKeysCalls++;
          return Reflect.ownKeys(target);
        },
      },
    );

    expect(computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition }).ok).toBe(true);
    expect(ownKeysCalls).toBe(1);
  });
});

describe('root own-key capture is atomic', () => {
  const legalKeys = ['schemaVersion', 'templateVersion', 'definition'];

  it('does not make a second ownKeys observation that can add bogus', () => {
    let ownKeysCalls = 0;
    const target = { schemaVersion: 1, templateVersion: 1, definition: {}, bogus: 1 };
    const envelope = new Proxy(target, {
      ownKeys() {
        ownKeysCalls++;
        return ownKeysCalls === 1 ? legalKeys : [...legalKeys, 'bogus'];
      },
    });

    expect(computeTemplateId(envelope).ok).toBe(true);
    expect(ownKeysCalls).toBe(1);
  });

  it('does not make a second ownKeys observation that can add __proto__', () => {
    let ownKeysCalls = 0;
    const target: Record<string, unknown> = {
      schemaVersion: 1,
      templateVersion: 1,
      definition: {},
    };
    Object.defineProperty(target, '__proto__', {
      value: null,
      enumerable: true,
      configurable: true,
    });
    const envelope = new Proxy(target, {
      ownKeys() {
        ownKeysCalls++;
        return ownKeysCalls === 1 ? legalKeys : [...legalKeys, '__proto__'];
      },
    });

    expect(computeTemplateId(envelope).ok).toBe(true);
    expect(ownKeysCalls).toBe(1);
  });

  it('rejects a changed key set on the next call instead of producing another digest', () => {
    let ownKeysCalls = 0;
    const target = { schemaVersion: 1, templateVersion: 1, definition: {}, bogus: 1 };
    const envelope = new Proxy(target, {
      ownKeys() {
        ownKeysCalls++;
        return ownKeysCalls === 1 ? legalKeys : [...legalKeys, 'bogus'];
      },
    });

    const first = computeTemplateId(envelope);
    const second = computeTemplateId(envelope);
    expect(first.ok).toBe(true);
    expect(second.ok).toBe(false);
    expect(ownKeysCalls).toBe(2);
  });

  it('produces the same digest when only captured key order changes', () => {
    let ownKeysCalls = 0;
    const target = { schemaVersion: 1, templateVersion: 1, definition: { x: 1 } };
    const envelope = new Proxy(target, {
      ownKeys() {
        ownKeysCalls++;
        return ownKeysCalls % 2 === 1 ? legalKeys : [...legalKeys].reverse();
      },
    });

    const first = computeTemplateId(envelope);
    const second = computeTemplateId(envelope);
    expect(first.ok).toBe(true);
    expect(second.ok).toBe(true);
    if (first.ok && second.ok) {
      expect(first.value).toBe(second.value);
    }
    expect(ownKeysCalls).toBe(2);
  });

  it('captures root descriptors in closed-key sort order', () => {
    const reads: PropertyKey[] = [];
    const target = { schemaVersion: 1, templateVersion: 1, definition: {} };
    const envelope = new Proxy(target, {
      ownKeys() {
        return ['templateVersion', 'schemaVersion', 'definition'];
      },
      getOwnPropertyDescriptor(current, property) {
        reads.push(property);
        return Reflect.getOwnPropertyDescriptor(current, property);
      },
    });

    expect(computeTemplateId(envelope).ok).toBe(true);
    expect(reads).toEqual(['definition', 'schemaVersion', 'templateVersion']);
  });

  it('rejects an earlier unknown key without observing any descriptor', () => {
    let descriptorCalls = 0;
    const target: Record<string, unknown> = { a: 1, schemaVersion: 1, templateVersion: 1 };
    Object.defineProperty(target, 'definition', {
      get: () => ({}),
      enumerable: true,
      configurable: true,
    });
    const envelope = new Proxy(target, {
      getOwnPropertyDescriptor(current, property) {
        descriptorCalls++;
        return Reflect.getOwnPropertyDescriptor(current, property);
      },
    });

    expect(computeTemplateId(envelope)).toEqual({
      ok: false,
      errors: [{ code: 'prefix-envelope-invalid', path: '/<untrusted-property>' }],
    });
    expect(descriptorCalls).toBe(0);
  });

  it('reports a missing root key before observing allowed descriptors', () => {
    let descriptorCalls = 0;
    const target: Record<string, unknown> = { templateVersion: 1 };
    Object.defineProperty(target, 'schemaVersion', {
      get: () => 1,
      enumerable: true,
      configurable: true,
    });
    const envelope = new Proxy(target, {
      getOwnPropertyDescriptor(current, property) {
        descriptorCalls++;
        return Reflect.getOwnPropertyDescriptor(current, property);
      },
    });

    expect(computeTemplateId(envelope)).toEqual({
      ok: false,
      errors: [{ code: 'prefix-envelope-invalid', path: '/definition' }],
    });
    expect(descriptorCalls).toBe(0);
  });
});

describe('nested descriptor capture order', () => {
  it('captures object properties in unsigned UTF-16 order', () => {
    const reads: PropertyKey[] = [];
    const definition = new Proxy(
      { z: 1, a: 2 },
      {
        ownKeys() {
          return ['z', 'a'];
        },
        getOwnPropertyDescriptor(current, property) {
          reads.push(property);
          return Reflect.getOwnPropertyDescriptor(current, property);
        },
      },
    );

    expect(computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition }).ok).toBe(true);
    expect(reads).toEqual(['a', 'z']);
  });

  it('captures array elements in ascending index order', () => {
    const reads: PropertyKey[] = [];
    const definition = new Proxy([1, 2], {
      getOwnPropertyDescriptor(current, property) {
        reads.push(property);
        return Reflect.getOwnPropertyDescriptor(current, property);
      },
    });

    expect(computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition }).ok).toBe(true);
    expect(reads.filter((property) => property !== 'length')).toEqual(['0', '1']);
  });
});

describe('bounded alias-preserving snapshots', () => {
  const atDepth65 = (value: unknown) => {
    let nested = value;
    for (let index = 0; index < 63; index++) {
      nested = [nested];
    }
    return nested;
  };

  it('checks the path-dependent depth before reusing a shallow-first alias', () => {
    const shared = { x: 1 };
    const aliased = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { a: shared, b: atDepth65(shared) },
    });
    const dealiased = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { a: { x: 1 }, b: atDepth65({ x: 1 }) },
    });
    expect(aliased).toEqual(dealiased);
    expect(aliased.ok).toBe(false);
    if (!aliased.ok) expect(aliased.errors[0].code).toBe('prefix-envelope-invalid');
  });

  it('reports the same depth defect when the deep alias occurs first', () => {
    const shared = { x: 1 };
    const aliased = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { a: atDepth65(shared), b: shared },
    });
    const dealiased = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { a: atDepth65({ x: 1 }), b: { x: 1 } },
    });
    expect(aliased).toEqual(dealiased);
  });

  it('does not let a memoized object containing a violation marker bypass depth', () => {
    const shared = Object.create(null) as Record<string, unknown>;
    Object.defineProperty(shared, 'bad', { get: () => 1, enumerable: true });
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { a: shared, b: atDepth65(shared) },
    });
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.errors[0].code).toBe('prefix-envelope-invalid');
  });

  it('checks memoized descendants when an alias root remains at depth 64', () => {
    const shared = { child: {} };
    let deep: unknown = shared;
    for (let index = 0; index < 62; index++) deep = [deep];
    let dealiasedDeep: unknown = { child: {} };
    for (let index = 0; index < 62; index++) dealiasedDeep = [dealiasedDeep];

    const aliased = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { a: shared, b: deep },
    });
    const dealiased = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { a: { child: {} }, b: dealiasedDeep },
    });

    expect(aliased).toEqual(dealiased);
    expect(aliased.ok).toBe(false);
    if (!aliased.ok) expect(aliased.errors[0].code).toBe('prefix-envelope-invalid');
  });

  it('finds the same depth defect when the deep alias occurrence comes first', () => {
    const shared = { child: [] };
    let deep: unknown = shared;
    for (let index = 0; index < 62; index++) deep = [deep];
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { a: deep, b: shared },
    });

    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.errors[0].code).toBe('prefix-envelope-invalid');
  });

  it('checks memoized descendant depth after entering validation-only mode', () => {
    const shared = { child: [] };
    let deep: unknown = shared;
    for (let index = 0; index < 63; index++) deep = [deep];
    const outcome = deepDescriptorSnapshot(
      { a: shared, b: deep },
      {
        maxDepth: 64,
        maxObjectProperties: 256,
        maxArrayItems: 1024,
        maxRetainedCanonicalBytes: 1,
      },
    );

    expect(outcome.ok).toBe(false);
    if (!outcome.ok) expect(outcome.violation.reason).toBe('depth-exceeded');
  });

  it('rejects a compact shared DAG at the canonical byte cap without expanding it in memory', () => {
    const leaf = new Array(1024).fill(0);
    const middle = new Array(1024).fill(leaf);
    const definition = new Array(1024).fill(middle);

    expect(computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition })).toEqual({
      ok: false,
      errors: [{ code: 'prefix-envelope-too-large', path: '' }],
    });
  });

  it('stops retaining wide dealiased slots after proving the canonical cap', () => {
    const definition = Array.from({ length: 1024 }, () => new Array(1024).fill(0));
    const stats: SnapshotStats = { retainedContainerSlots: 0 };
    const outcome = deepDescriptorSnapshot(
      definition,
      {
        maxDepth: 64,
        maxObjectProperties: 256,
        maxArrayItems: 1024,
        maxRetainedCanonicalBytes: 262_144,
      },
      undefined,
      undefined,
      stats,
    );

    expect(outcome.ok).toBe(true);
    if (outcome.ok) expect(outcome.retentionExceeded).toBe(true);
    expect(stats.retainedContainerSlots).toBeLessThan(300_000);
  });

  it('reports a late canonical defect after entering validation-only mode', () => {
    const definition = Array.from({ length: 1024 }, () => new Array(1024).fill(0));
    definition[1023][1023] = Number.POSITIVE_INFINITY;

    const result = computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition });
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.errors[0].code).toBe('prefix-canonical-input-rejected');
  });

  it('reports a late structural defect after entering validation-only mode', () => {
    const definition: unknown[][] = Array.from({ length: 1024 }, () =>
      new Array<unknown>(1024).fill(0),
    );
    let tooDeep: unknown = 0;
    for (let index = 0; index < 65; index++) tooDeep = [tooDeep];
    definition[1023][1023] = tooDeep;

    const result = computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition });
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.errors[0].code).toBe('prefix-envelope-invalid');
  });
});

describe('contract-owned tool wrappers are captured once by identity', () => {
  const envelope = (definitions: unknown[]) => ({
    schemaVersion: 1,
    toolsetVersion: 1,
    definitions,
  });

  it('reuses one captured plain wrapper and rejects its duplicate name', () => {
    const shared = { name: 'tool', description: 'd', inputSchema: {} };
    const result = computeToolDefinitionId(envelope([shared, shared]));
    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.errors[0]).toEqual({
        code: 'prefix-envelope-invalid',
        path: '/definitions/1/name',
      });
    }
  });

  it('rejects an earlier unknown wrapper key without observing any descriptor', () => {
    let descriptorCalls = 0;
    const target: Record<string, unknown> = { a: 1, name: 'tool', inputSchema: {} };
    Object.defineProperty(target, 'description', {
      get: () => 'd',
      enumerable: true,
      configurable: true,
    });
    const tool = new Proxy(target, {
      getOwnPropertyDescriptor(current, property) {
        descriptorCalls++;
        return Reflect.getOwnPropertyDescriptor(current, property);
      },
    });

    expect(computeToolDefinitionId(envelope([tool]))).toEqual({
      ok: false,
      errors: [{ code: 'prefix-envelope-invalid', path: '/definitions/0/<untrusted-property>' }],
    });
    expect(descriptorCalls).toBe(0);
  });

  it('reports a missing wrapper key before observing allowed descriptors', () => {
    let descriptorCalls = 0;
    const target: Record<string, unknown> = { name: 'tool' };
    Object.defineProperty(target, 'description', {
      get: () => 'd',
      enumerable: true,
      configurable: true,
    });
    const tool = new Proxy(target, {
      getOwnPropertyDescriptor(current, property) {
        descriptorCalls++;
        return Reflect.getOwnPropertyDescriptor(current, property);
      },
    });

    expect(computeToolDefinitionId(envelope([tool]))).toEqual({
      ok: false,
      errors: [{ code: 'prefix-envelope-invalid', path: '/definitions/0' }],
    });
    expect(descriptorCalls).toBe(0);
  });

  it('fully validates an earlier wrapper before observing the next index', () => {
    let laterIndexReads = 0;
    const definitions = [{ name: 'a', description: 'd', inputSchema: {}, bogus: 1 }, null];
    Object.defineProperty(definitions, '1', {
      get: () => null,
      enumerable: true,
      configurable: true,
    });
    const proxied = new Proxy(definitions, {
      getOwnPropertyDescriptor(current, property) {
        if (property === '1') laterIndexReads++;
        return Reflect.getOwnPropertyDescriptor(current, property);
      },
    });

    expect(computeToolDefinitionId(envelope(proxied))).toEqual({
      ok: false,
      errors: [{ code: 'prefix-envelope-invalid', path: '/definitions/0/<untrusted-property>' }],
    });
    expect(laterIndexReads).toBe(0);
  });

  it('observes a shared stateful proxy wrapper only once per public call', () => {
    let ownKeysCalls = 0;
    let prototypeCalls = 0;
    const descriptorCalls = new Map<PropertyKey, number>();
    const target = { name: 'tool-a', description: 'd', inputSchema: {} };
    const shared = new Proxy(target, {
      getPrototypeOf(current) {
        prototypeCalls++;
        return Reflect.getPrototypeOf(current);
      },
      ownKeys(current) {
        ownKeysCalls++;
        return Reflect.ownKeys(current);
      },
      getOwnPropertyDescriptor(current, property) {
        descriptorCalls.set(property, (descriptorCalls.get(property) ?? 0) + 1);
        const descriptor = Reflect.getOwnPropertyDescriptor(current, property)!;
        if (property === 'name') {
          return {
            ...descriptor,
            value: descriptorCalls.get(property) === 1 ? 'tool-a' : 'tool-b',
          };
        }
        return descriptor;
      },
    });

    const first = computeToolDefinitionId(envelope([shared, shared]));
    expect(first.ok).toBe(false);
    if (!first.ok) expect(first.errors[0].path).toBe('/definitions/1/name');
    expect(ownKeysCalls).toBe(1);
    expect(prototypeCalls).toBe(1);
    expect([...descriptorCalls.values()]).toEqual([1, 1, 1]);

    const second = computeToolDefinitionId(envelope([shared, shared]));
    expect(second).toEqual(first);
    expect(ownKeysCalls).toBe(2);
    expect(prototypeCalls).toBe(2);
    expect([...descriptorCalls.values()]).toEqual([2, 2, 2]);
  });

  it('matches the duplicate diagnostic of equivalent dealiased wrappers', () => {
    const shared = { name: 'tool', description: 'd', inputSchema: {} };
    expect(computeToolDefinitionId(envelope([shared, shared]))).toEqual(
      computeToolDefinitionId(
        envelope([
          { name: 'tool', description: 'd', inputSchema: {} },
          { name: 'tool', description: 'd', inputSchema: {} },
        ]),
      ),
    );
  });

  it('does not re-observe a wrapper reached through its own inputSchema', () => {
    let ownKeysCalls = 0;
    const target: Record<string, unknown> = { name: 'tool', description: 'd' };
    const tool = new Proxy(target, {
      ownKeys(current) {
        ownKeysCalls++;
        return Reflect.ownKeys(current);
      },
    });
    target.inputSchema = tool;
    const result = computeToolDefinitionId(envelope([tool]));
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.errors[0].code).toBe('prefix-canonical-input-rejected');
    expect(ownKeysCalls).toBe(1);
  });

  it('uses one prepared graph for policyMetadata array and mutual wrapper back-references', () => {
    let leftReads = 0;
    let rightReads = 0;
    let definitionsReads = 0;
    const leftTarget: Record<string, unknown> = { name: 'left', description: 'd' };
    const rightTarget: Record<string, unknown> = { name: 'right', description: 'd' };
    const left = new Proxy(leftTarget, {
      ownKeys(target) {
        leftReads++;
        return Reflect.ownKeys(target);
      },
    });
    const right = new Proxy(rightTarget, {
      ownKeys(target) {
        rightReads++;
        return Reflect.ownKeys(target);
      },
    });
    const definitions = new Proxy([left, right], {
      ownKeys(target) {
        definitionsReads++;
        return Reflect.ownKeys(target);
      },
    });
    leftTarget.inputSchema = right;
    leftTarget.policyMetadata = definitions;
    rightTarget.inputSchema = left;
    const result = computeToolDefinitionId(envelope(definitions));
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.errors[0].code).toBe('prefix-canonical-input-rejected');
    expect(leftReads).toBe(1);
    expect(rightReads).toBe(1);
    expect(definitionsReads).toBe(1);
  });
});

describe('structural caps precede canonical anomalies', () => {
  it('finds descendant depth under a modified-prototype array', () => {
    let deep: unknown = 0;
    for (let index = 0; index < 64; index++) deep = [deep];
    const definition = [deep];
    Object.setPrototypeOf(definition, null);

    const result = computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition });
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.errors[0].code).toBe('prefix-envelope-invalid');
  });

  it('finds descendant property count under a symbol-keyed object', () => {
    const overCap = Object.fromEntries(
      Array.from({ length: 257 }, (_, index) => [`k${index}`, index]),
    );
    const definition = { child: overCap };
    Object.defineProperty(definition, Symbol('bad'), { value: 1, enumerable: true });

    const result = computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition });
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.errors[0].code).toBe('prefix-envelope-invalid');
  });

  it('continues anomaly traversal after entering validation-only mode', () => {
    let deep: unknown = 0;
    for (let index = 0; index < 65; index++) deep = [deep];
    const root = [deep];
    Object.setPrototypeOf(root, null);

    const outcome = deepDescriptorSnapshot(root, {
      maxDepth: 64,
      maxObjectProperties: 256,
      maxArrayItems: 1024,
      maxRetainedCanonicalBytes: 1,
    });
    expect(outcome.ok).toBe(false);
    if (!outcome.ok) expect(outcome.violation.reason).toBe('depth-exceeded');
  });

  it('finds descendant property count under a validation-only symbol anomaly', () => {
    const overCap = Object.fromEntries(
      Array.from({ length: 257 }, (_, index) => [`k${index}`, index]),
    );
    const root = { child: overCap };
    Object.defineProperty(root, Symbol('bad'), { value: 1, enumerable: true });

    const outcome = deepDescriptorSnapshot(root, {
      maxDepth: 64,
      maxObjectProperties: 256,
      maxArrayItems: 1024,
      maxRetainedCanonicalBytes: 1,
    });
    expect(outcome.ok).toBe(false);
    if (!outcome.ok) expect(outcome.violation.reason).toBe('property-count-exceeded');
  });

  it('observes a shared anomalous proxy once and freezes its first captured graph', () => {
    let ownKeysCalls = 0;
    let prototypeCalls = 0;
    const target = { child: 1 };
    const nonPlainPrototype = {};
    const shared = new Proxy(target, {
      ownKeys(current) {
        ownKeysCalls++;
        if (ownKeysCalls === 1) return Reflect.ownKeys(current);
        return Array.from({ length: 257 }, (_, index) => `k${index}`);
      },
      getPrototypeOf(current) {
        prototypeCalls++;
        return prototypeCalls === 1 ? nonPlainPrototype : Reflect.getPrototypeOf(current);
      },
      getOwnPropertyDescriptor(current, property) {
        return Reflect.getOwnPropertyDescriptor(current, property);
      },
    });

    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { a: shared, b: shared },
    });
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.errors[0].code).toBe('prefix-canonical-input-rejected');
    expect(ownKeysCalls).toBe(1);
    expect(prototypeCalls).toBe(1);
  });

  it('rejects an over-cap open array before observing its own keys', () => {
    let ownKeysCalls = 0;
    const definition = new Proxy(new Array(1025).fill(0), {
      ownKeys(target) {
        ownKeysCalls++;
        return Reflect.ownKeys(target);
      },
    });

    const result = computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition });
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.errors[0].code).toBe('prefix-envelope-invalid');
    expect(ownKeysCalls).toBe(0);
  });

  it('rejects over-cap tool definitions before observing their own keys', () => {
    let ownKeysCalls = 0;
    const definitions = new Proxy(new Array(65).fill(null), {
      ownKeys(target) {
        ownKeysCalls++;
        return Reflect.ownKeys(target);
      },
    });

    const result = computeToolDefinitionId({ schemaVersion: 1, toolsetVersion: 1, definitions });
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.errors[0].code).toBe('prefix-envelope-invalid');
    expect(ownKeysCalls).toBe(0);
  });

  it('array length wins over symbol keys and modified prototypes', () => {
    const withSymbol = new Array(1025).fill(0);
    Object.defineProperty(withSymbol, Symbol('bad'), { value: 1, enumerable: true });
    const withPrototype = new Array(1025).fill(0);
    Object.setPrototypeOf(withPrototype, null);
    for (const definition of [withSymbol, withPrototype]) {
      const result = computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition });
      expect(result.ok).toBe(false);
      if (!result.ok) expect(result.errors[0].code).toBe('prefix-envelope-invalid');
    }
  });

  it('object property count wins over a symbol key', () => {
    const definition = Object.fromEntries(
      Array.from({ length: 257 }, (_, index) => [`k${index}`, index]),
    );
    Object.defineProperty(definition, Symbol('bad'), { value: 1, enumerable: true });
    const result = computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition });
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.errors[0].code).toBe('prefix-envelope-invalid');
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
