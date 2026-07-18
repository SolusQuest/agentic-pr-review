import { describe, expect, it } from 'vitest';
import {
  computeAdapterId,
  computeCacheConfigId,
  computePolicyId,
  computeTemplateId,
  computeToolDefinitionId,
  deriveInteractionId,
  validateIdentity,
  validateModelSnapshot,
  PREFIX_CODES,
} from './index.js';

const TEMPLATE = {
  schemaVersion: 1,
  templateVersion: 3,
  definition: { role: 'system', text: 'x' },
};
const POLICY = { schemaVersion: 1, policyVersion: 1, instructions: 'i', constraints: {} };
const TOOLS = {
  schemaVersion: 1,
  toolsetVersion: 1,
  definitions: [{ name: 'submit', description: 'd', inputSchema: { type: 'object' } }],
};
const CONFIG = {
  schemaVersion: 1,
  cacheConfigVersion: 1,
  markerPolicy: 'm',
  eligibility: 'e',
  statelessMode: false,
};
const ADAPTER = {
  schemaVersion: 1,
  capabilityProfileVersion: 1,
  adapterBuildVersion: '0.0.0-fixture',
};

const HEX64 = /^[a-f0-9]{64}$/;

describe('cache-contract digest helpers', () => {
  it('computes 64-char lowercase hex digests', () => {
    for (const result of [
      computeTemplateId(TEMPLATE),
      computePolicyId(POLICY),
      computeToolDefinitionId(TOOLS),
      computeCacheConfigId(CONFIG),
      computeAdapterId(ADAPTER),
    ]) {
      expect(result.ok).toBe(true);
      if (result.ok) {
        expect(result.value).toMatch(HEX64);
      }
    }
  });

  it('is stable across object insertion order', () => {
    const a = computeTemplateId(TEMPLATE);
    const b = computeTemplateId({
      definition: { text: 'x', role: 'system' },
      templateVersion: 3,
      schemaVersion: 1,
    });
    expect(a.ok && b.ok && a.value === b.value).toBe(true);
  });

  it('rejects unknown fields', () => {
    const result = computeTemplateId({ ...TEMPLATE, bogus: 1 });
    expect(result).toEqual({
      ok: false,
      errors: [{ code: PREFIX_CODES.envelopeInvalid, path: '/<untrusted-property>' }],
    });
  });

  it('rejects missing required fields', () => {
    const result = computeTemplateId({ schemaVersion: 1, templateVersion: 3 });
    expect(result).toEqual({
      ok: false,
      errors: [{ code: PREFIX_CODES.envelopeInvalid, path: '/definition' }],
    });
  });

  it('rejects non-integer and out-of-range versions', () => {
    for (const version of [0, 1.5, '3', 2_147_483_648]) {
      const result = computeTemplateId({ ...TEMPLATE, templateVersion: version });
      expect(result.ok).toBe(false);
    }
    expect(computeTemplateId({ ...TEMPLATE, templateVersion: 1 }).ok).toBe(true);
    expect(computeTemplateId({ ...TEMPLATE, templateVersion: 2_147_483_647 }).ok).toBe(true);
  });

  it('rejects duplicate tool names exactly but not case-variant names', () => {
    const two = (a: string, b: string) =>
      computeToolDefinitionId({
        schemaVersion: 1,
        toolsetVersion: 1,
        definitions: [
          { name: a, description: 'd', inputSchema: {} },
          { name: b, description: 'd', inputSchema: {} },
        ],
      });
    expect(two('submit', 'submit').ok).toBe(false);
    expect(two('ReadFile', 'readfile').ok).toBe(true);
  });

  it('distinguishes absent, null, and empty-object policyMetadata', () => {
    const make = (meta: unknown, include: boolean) =>
      computeToolDefinitionId({
        schemaVersion: 1,
        toolsetVersion: 1,
        definitions: [
          {
            name: 'submit',
            description: 'd',
            inputSchema: {},
            ...(include ? { policyMetadata: meta } : {}),
          },
        ],
      });
    const absent = make(undefined, false);
    const explicitNull = make(null, true);
    const empty = make({}, true);
    expect(absent.ok && explicitNull.ok && empty.ok).toBe(true);
    if (absent.ok && explicitNull.ok && empty.ok) {
      expect(absent.value).not.toBe(explicitNull.value);
      expect(absent.value).not.toBe(empty.value);
      expect(explicitNull.value).not.toBe(empty.value);
    }
  });

  it('escapes NUL inside open JSON instead of rejecting it', () => {
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: 'a\u0000b',
    });
    expect(result.ok).toBe(true);
  });

  it('rejects non-plain and non-JSON values without throwing', () => {
    const cyclic: Record<string, unknown> = {};
    cyclic.self = cyclic;
    const withAccessor: Record<string, unknown> = {};
    Object.defineProperty(withAccessor, 'schemaVersion', { get: () => 1 });
    const sparse = new Array(3);
    sparse[0] = { schemaVersion: 1, templateVersion: 1, definition: 'x' };

    const inputs: unknown[] = [
      undefined,
      null,
      42n,
      Symbol('x'),
      () => 1,
      cyclic,
      withAccessor,
      sparse,
      new Date(),
      [],
      'string',
      42,
    ];
    for (const input of inputs) {
      expect(() => computeTemplateId(input)).not.toThrow();
      expect(computeTemplateId(input).ok).toBe(false);
    }
  });

  it('rejects an enumerable throwing getter without throwing or invoking it', () => {
    let invoked = false;
    const hostile = {
      schemaVersion: 1,
      templateVersion: 1,
      definition: 'x',
      get trap() {
        invoked = true;
        throw new Error('getter invoked');
      },
    };
    const result = computeTemplateId(hostile);
    expect(invoked).toBe(false);
    expect(result).toEqual({
      ok: false,
      errors: [{ code: PREFIX_CODES.envelopeInvalid, path: '/<untrusted-property>' }],
    });
  });

  it('rejects throwing proxies without throwing', () => {
    const proxy = new Proxy(
      {},
      {
        get() {
          throw new Error('proxy trap');
        },
        ownKeys() {
          throw new Error('proxy ownKeys');
        },
      },
    );
    const result = computeTemplateId(proxy);
    expect(result.ok).toBe(false);
  });

  it('rejects null and malformed predecessors without throwing', () => {
    const consumed = 'e5'.repeat(32);
    const head = '7'.repeat(40);
    for (const predecessor of [
      null,
      undefined,
      42,
      'bootstrap',
      {},
      { kind: 'sideways' },
      { kind: 'ledger' },
    ]) {
      const result = deriveInteractionId(predecessor as never, consumed, head, 0);
      expect(result.ok).toBe(false);
    }
  });

  it('rejects structural-bound violations', () => {
    const nest = (depth: number): unknown => (depth === 0 ? 1 : [nest(depth - 1)]);
    expect(
      computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition: nest(64) }).ok,
    ).toBe(true);
    expect(
      computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition: nest(65) }).ok,
    ).toBe(false);

    const props = (n: number) =>
      Object.fromEntries(Array.from({ length: n }, (_, i) => ['k' + i, i]));
    expect(
      computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition: props(256) }).ok,
    ).toBe(true);
    expect(
      computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition: props(257) }).ok,
    ).toBe(false);

    expect(
      computeTemplateId({
        schemaVersion: 1,
        templateVersion: 1,
        definition: new Array(1024).fill(1),
      }).ok,
    ).toBe(true);
    expect(
      computeTemplateId({
        schemaVersion: 1,
        templateVersion: 1,
        definition: new Array(1025).fill(1),
      }).ok,
    ).toBe(false);
  });

  it('rejects oversize envelopes by canonical bytes', () => {
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: 'x'.repeat(300_000),
    });
    expect(result).toEqual({ ok: false, errors: [{ code: PREFIX_CODES.envelopeTooLarge }] });
  });

  it('rejects embedded identity violations', () => {
    expect(
      computeToolDefinitionId({
        schemaVersion: 1,
        toolsetVersion: 1,
        definitions: [{ name: '', description: 'd', inputSchema: {} }],
      }),
    ).toEqual({
      ok: false,
      errors: [{ code: PREFIX_CODES.identityInvalid, path: '/definitions/0/name' }],
    });
    expect(computeAdapterId({ ...ADAPTER, adapterBuildVersion: 'bad' })).toEqual({
      ok: false,
      errors: [{ code: PREFIX_CODES.identityInvalid, path: '/adapterBuildVersion' }],
    });
  });
});

describe('deriveInteractionId', () => {
  const consumed = 'e5'.repeat(32);
  const head = '7'.repeat(40);

  it('derives for bootstrap and ledger predecessors', () => {
    const boot = deriveInteractionId({ kind: 'bootstrap' }, consumed, head, 0);
    const ledger = deriveInteractionId(
      { kind: 'ledger', sha256Hex: 'f'.repeat(64) },
      consumed,
      head,
      0,
    );
    expect(boot.ok && ledger.ok).toBe(true);
    if (boot.ok && ledger.ok) {
      expect(boot.value).toMatch(HEX64);
      expect(ledger.value).toMatch(HEX64);
      expect(boot.value).not.toBe(ledger.value);
    }
  });

  it('accepts both 40- and 64-char git SHAs and ordinal endpoints', () => {
    expect(deriveInteractionId({ kind: 'bootstrap' }, consumed, '9'.repeat(64), 0).ok).toBe(true);
    expect(deriveInteractionId({ kind: 'bootstrap' }, consumed, head, 1_000_000).ok).toBe(true);
  });

  it('rejects invalid inputs with typed failures', () => {
    expect(
      deriveInteractionId({ kind: 'ledger', sha256Hex: 'bootstrap' }, consumed, head, 0),
    ).toEqual({
      ok: false,
      errors: [{ code: PREFIX_CODES.digestInvalid, path: '/predecessor' }],
    });
    expect(deriveInteractionId({ kind: 'bootstrap' }, 'short', head, 0)).toEqual({
      ok: false,
      errors: [{ code: PREFIX_CODES.digestInvalid, path: '/consumedInputSha256' }],
    });
    expect(deriveInteractionId({ kind: 'bootstrap' }, consumed, '7'.repeat(39), 0)).toEqual({
      ok: false,
      errors: [{ code: PREFIX_CODES.gitShaInvalid, path: '/currentHeadSha' }],
    });
    for (const ordinal of [-1, 1_000_001, 1.5, Number.NaN, '0']) {
      const result = deriveInteractionId({ kind: 'bootstrap' }, consumed, head, ordinal as number);
      expect(result.ok).toBe(false);
    }
  });

  it('does not encode sessionEpoch anywhere', () => {
    const a = deriveInteractionId({ kind: 'bootstrap' }, consumed, head, 0);
    const b = deriveInteractionId({ kind: 'bootstrap' }, consumed, head, 0);
    expect(a.ok && b.ok && a.value === b.value).toBe(true);
  });
});

describe('identity validation', () => {
  it('accepts valid identities and rejects invalid ones', () => {
    expect(validateIdentity('provider').ok).toBe(true);
    expect(validateIdentity('x'.repeat(256)).ok).toBe(true);
    expect(validateIdentity('é'.repeat(128)).ok).toBe(true); // 256 UTF-8 bytes
    expect(validateIdentity('').ok).toBe(false);
    expect(validateIdentity('x'.repeat(257)).ok).toBe(false);
    expect(validateIdentity('é'.repeat(129)).ok).toBe(false); // 258 UTF-8 bytes
    expect(validateIdentity('a\u0001b').ok).toBe(false);
    expect(validateIdentity('ab').ok).toBe(false);
    expect(validateIdentity(42).ok).toBe(false);
  });

  it('rejects only the exact latest literal as a floating alias', () => {
    expect(validateModelSnapshot('latest')).toEqual({
      ok: false,
      errors: [{ code: PREFIX_CODES.modelAliasLiteral }],
    });
    expect(validateModelSnapshot('Latest').ok).toBe(true);
    expect(validateModelSnapshot('LATEST').ok).toBe(true);
    expect(validateModelSnapshot('model-2024-01-01').ok).toBe(true);
  });
});

describe('canonical traversal determinism and accepted domain', () => {
  const tooManyProperties = () =>
    Object.fromEntries(Array.from({ length: 257 }, (_, index) => [`k${index}`, index]));

  it('checks structural object branches in unsigned UTF-16 order regardless of insertion order', () => {
    for (const definition of [
      { z: [0, tooManyProperties()], a: [tooManyProperties()] },
      { a: [tooManyProperties()], z: [0, tooManyProperties()] },
    ]) {
      expect(computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition })).toEqual({
        ok: false,
        errors: [
          { code: PREFIX_CODES.envelopeInvalid, path: '/definition/<untrusted-property>/0' },
        ],
      });
    }
  });

  it('sorts numeric-looking object keys lexically rather than by JS integer-index order', () => {
    const definition = { '2': [0, tooManyProperties()], '10': [tooManyProperties()] };
    expect(computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition })).toEqual({
      ok: false,
      errors: [{ code: PREFIX_CODES.envelopeInvalid, path: '/definition/<untrusted-property>/0' }],
    });
  });

  it('checks structural array branches in ascending index order', () => {
    const definition = [tooManyProperties(), [tooManyProperties()]];
    expect(computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition })).toEqual({
      ok: false,
      errors: [{ code: PREFIX_CODES.envelopeInvalid, path: '/definition/0' }],
    });
  });

  it('contract-owned field defects beat nested structural bounds in the same tool', () => {
    let deep: unknown = 0;
    for (let depth = 0; depth < 65; depth++) {
      deep = [deep];
    }
    expect(
      computeToolDefinitionId({
        schemaVersion: 1,
        toolsetVersion: 1,
        definitions: [{ name: 't', description: 42, inputSchema: { deep } }],
      }),
    ).toEqual({
      ok: false,
      errors: [{ code: PREFIX_CODES.envelopeInvalid, path: '/definitions/0/description' }],
    });
  });

  it('invalid UTF-16 names in closed roots and tool wrappers stay in the structure stage', () => {
    const invalidName = String.fromCharCode(0xd800);
    expect(
      computeTemplateId({
        schemaVersion: 1,
        templateVersion: 1,
        definition: {},
        [invalidName]: 1,
      }),
    ).toEqual({
      ok: false,
      errors: [{ code: PREFIX_CODES.envelopeInvalid, path: '/<invalid-utf16>' }],
    });
    expect(
      computeToolDefinitionId({
        schemaVersion: 1,
        toolsetVersion: 1,
        definitions: [{ name: 't', description: 'd', inputSchema: {}, [invalidName]: 1 }],
      }),
    ).toEqual({
      ok: false,
      errors: [{ code: PREFIX_CODES.envelopeInvalid, path: '/definitions/0/<invalid-utf16>' }],
    });
  });

  it('reports the first violation in array-index order', () => {
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: [Number.NaN, [Number.NaN]],
    });
    expect(result).toEqual({
      ok: false,
      errors: [{ code: PREFIX_CODES.canonicalInputRejected, path: '/definition/0' }],
    });
  });

  it('reports the first violation in UTF-16 key order, not insertion order', () => {
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { z: Number.NaN, a: Number.NaN },
    });
    expect(result).toEqual({
      ok: false,
      errors: [
        { code: PREFIX_CODES.canonicalInputRejected, path: '/definition/<untrusted-property>' },
      ],
    });
  });

  it('accepts shared non-cyclic references across sibling fields', () => {
    const shared = { x: 1 };
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { a: shared, b: shared },
    });
    expect(result.ok).toBe(true);
  });

  it('rejects a true ancestor cycle', () => {
    const cyclic: Record<string, unknown> = {};
    cyclic.self = cyclic;
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: cyclic,
    });
    // The deep snapshot marks the cycle at its exact position; the
    // canonical-domain scan rejects it there.
    expect(result).toEqual({
      ok: false,
      errors: [
        { code: PREFIX_CODES.canonicalInputRejected, path: '/definition/<untrusted-property>' },
      ],
    });
  });

  it('rejects class-instance roots', () => {
    class FakeEnvelope {
      schemaVersion = 1;
      templateVersion = 1;
      definition = {};
    }
    expect(computeTemplateId(new FakeEnvelope()).ok).toBe(false);
  });

  it('rejects symbol-keyed roots without dropping the symbol property silently', () => {
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: {},
      [Symbol('secret')]: 1,
    } as never);
    expect(result.ok).toBe(false);
  });

  it('rejects nested accessors in open JSON without invoking getters', () => {
    let invoked = false;
    const definition = {
      nested: {},
    };
    Object.defineProperty(definition.nested, 'boom', {
      enumerable: true,
      get() {
        invoked = true;
        throw new Error('getter invoked');
      },
    });
    const result = computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition });
    expect(invoked).toBe(false);
    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.errors[0].code).toBe(PREFIX_CODES.canonicalInputRejected);
    }
  });

  it('rejects nested non-plain objects in open JSON', () => {
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { when: new Date(0) },
    });
    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.errors[0].code).toBe(PREFIX_CODES.canonicalInputRejected);
    }
  });
});
describe('first-defect ordering and descriptor safety', () => {
  it('earlier value defect beats a later property-name defect', () => {
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { a: Number.NaN, ['b' + String.fromCharCode(0xd800)]: 1 },
    });
    expect(result).toEqual({
      ok: false,
      errors: [
        {
          code: PREFIX_CODES.canonicalInputRejected,
          path: '/definition/<untrusted-property>',
        },
      ],
    });
  });

  it('undefined is rejected before a later non-finite number', () => {
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { a: undefined, b: Number.NaN },
    });
    expect(result).toEqual({
      ok: false,
      errors: [
        {
          code: PREFIX_CODES.canonicalInputRejected,
          path: '/definition/<untrusted-property>',
        },
      ],
    });
  });

  it('bigint is rejected before a later lone surrogate', () => {
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { a: 1n, b: String.fromCharCode(0xd800) },
    });
    expect(result).toEqual({
      ok: false,
      errors: [
        {
          code: PREFIX_CODES.canonicalInputRejected,
          path: '/definition/<untrusted-property>',
        },
      ],
    });
  });

  it('reports nested function and symbol values at their exact positions', () => {
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { nested: { fn: () => 1 } },
    });
    expect(result).toEqual({
      ok: false,
      errors: [
        {
          code: PREFIX_CODES.canonicalInputRejected,
          path: '/definition/<untrusted-property>/<untrusted-property>',
        },
      ],
    });
  });

  it('never invokes an array-index getter', () => {
    let invoked = false;
    const array = [0];
    Object.defineProperty(array, '0', {
      enumerable: true,
      get() {
        invoked = true;
        throw new Error('getter invoked');
      },
    });
    const result = computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition: array });
    expect(invoked).toBe(false);
    expect(result.ok).toBe(false);
  });

  it('never invokes tool-wrapper getters', () => {
    let invoked = false;
    const tool: Record<string, unknown> = { description: 'd', inputSchema: {} };
    Object.defineProperty(tool, 'name', {
      enumerable: true,
      get() {
        invoked = true;
        throw new Error('getter invoked');
      },
    });
    const result = computeToolDefinitionId({
      schemaVersion: 1,
      toolsetVersion: 1,
      definitions: [tool],
    });
    expect(invoked).toBe(false);
    expect(result.ok).toBe(false);
  });

  it('a get-trap proxy is read through descriptors only (trap never fires)', () => {
    let invoked = false;
    const proxy = new Proxy([{ name: 'a', description: 'd', inputSchema: {} }], {
      get() {
        invoked = true;
        throw new Error('proxy trap');
      },
    });
    const result = computeToolDefinitionId({
      schemaVersion: 1,
      toolsetVersion: 1,
      definitions: proxy,
    });
    expect(invoked).toBe(false);
    expect(result.ok).toBe(true);
  });

  it('a descriptor-throwing proxy yields a typed failure without escaping', () => {
    const proxy = new Proxy([{ name: 'a', description: 'd', inputSchema: {} }], {
      getOwnPropertyDescriptor() {
        throw new Error('descriptor trap');
      },
    });
    const result = computeToolDefinitionId({
      schemaVersion: 1,
      toolsetVersion: 1,
      definitions: proxy,
    });
    expect(result.ok).toBe(false);
  });

  it('rejects arrays with a modified prototype or symbol keys', () => {
    const weird: unknown[] = [1];
    Object.setPrototypeOf(weird, { custom: true });
    expect(computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition: weird }).ok).toBe(
      false,
    );

    const symbolKeyed: unknown[] = [1];
    (symbolKeyed as unknown as Record<symbol, unknown>)[Symbol('x')] = 1;
    expect(
      computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition: symbolKeyed }).ok,
    ).toBe(false);
  });

  it('rejects own __proto__ properties as unknown fields', () => {
    const parsed = JSON.parse(
      '{"schemaVersion":1,"templateVersion":1,"definition":{},"__proto__":null}',
    );
    const result = computeTemplateId(parsed);
    expect(result).toEqual({
      ok: false,
      errors: [{ code: PREFIX_CODES.envelopeInvalid, path: '/<untrusted-property>' }],
    });

    const parsedObjectProto = JSON.parse(
      '{"schemaVersion":1,"templateVersion":1,"definition":{},"__proto__":{}}',
    );
    expect(computeTemplateId(parsedObjectProto).ok).toBe(false);

    const parsedWithBogus = JSON.parse(
      '{"schemaVersion":1,"templateVersion":1,"definition":{},"__proto__":null,"bogus":1}',
    );
    expect(computeTemplateId(parsedWithBogus).ok).toBe(false);
  });
});

describe('byte cap never masks canonical defects; invalid names are structured', () => {
  it('early oversize string does not mask a later lone surrogate', () => {
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { a: 'x'.repeat(300_000), z: String.fromCharCode(0xd800) },
    });
    expect(result).toEqual({
      ok: false,
      errors: [
        {
          code: PREFIX_CODES.canonicalInputRejected,
          path: '/definition/<untrusted-property>',
        },
      ],
    });
  });

  it('early oversize string does not mask a later non-finite number', () => {
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { a: 'x'.repeat(300_000), z: 1e999 },
    });
    expect(result).toEqual({
      ok: false,
      errors: [
        {
          code: PREFIX_CODES.canonicalInputRejected,
          path: '/definition/<untrusted-property>',
        },
      ],
    });
  });

  it('invalid property name at the open-JSON root gets the terminal marker', () => {
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { [String.fromCharCode(0xd800)]: 1 },
    });
    expect(result).toEqual({
      ok: false,
      errors: [
        {
          code: PREFIX_CODES.canonicalInputRejected,
          path: '/definition/<invalid-utf16>',
        },
      ],
    });
  });

  it('invalid property name under an unknown ancestor keeps both markers', () => {
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { secretToken: { [String.fromCharCode(0xd800)]: 1 } },
    });
    expect(result).toEqual({
      ok: false,
      errors: [
        {
          code: PREFIX_CODES.canonicalInputRejected,
          path: '/definition/<untrusted-property>/<invalid-utf16>',
        },
      ],
    });
  });

  it('high escape-inflation strings are rejected at the cap', () => {
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: ''.repeat(100_000),
    });
    expect(result).toEqual({ ok: false, errors: [{ code: PREFIX_CODES.envelopeTooLarge }] });
  });

  it('long plain strings over the cap are rejected', () => {
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: 'x'.repeat(300_000),
    });
    expect(result).toEqual({ ok: false, errors: [{ code: PREFIX_CODES.envelopeTooLarge }] });
  });

  it('exact envelope cap passes and cap+1 fails', () => {
    const make = (pad: number) =>
      computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition: 'x'.repeat(pad) });
    // Find the exact pad landing the canonical envelope on the cap.
    let lo = 262_000;
    let hi = 262_144;
    while (lo < hi) {
      const mid = (lo + hi) >> 1;
      const result = make(mid);
      if (result.ok) {
        lo = mid + 1;
      } else {
        hi = mid;
      }
    }
    const atCap = make(lo - 1);
    expect(atCap.ok).toBe(true);
    const overCap = make(lo);
    expect(overCap.ok).toBe(false);
  });

  it('proxies and inherited toJSON do not affect the cap semantics', () => {
    const proxy = new Proxy(
      { schemaVersion: 1, templateVersion: 1, definition: 'x'.repeat(300_000) },
      {
        get(target, prop) {
          if (prop === 'toJSON') {
            return () => ({ schemaVersion: 1, templateVersion: 1, definition: 'tiny' });
          }
          return (target as Record<string | symbol, unknown>)[prop];
        },
      },
    );
    // The descriptor snapshot rejects proxies whose reads trap; it never
    // reaches a toJSON-influenced serialization.
    const result = computeTemplateId(proxy);
    expect(result.ok).toBe(false);
  });
});
