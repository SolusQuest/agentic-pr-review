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
      errors: [{ code: PREFIX_CODES.envelopeInvalid, path: '/bogus' }],
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
