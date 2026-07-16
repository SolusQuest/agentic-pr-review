import { describe, expect, it } from 'vitest';
import {
  normalizePosition,
  resolveArrayItem,
  resolveProperty,
  sanitizeSegment,
  scanStringSafety,
  type SchemaNode,
} from '../state-v2/shared-safe-path.js';

describe('ProviderRunMetadataV1 shared hypothetical-schema vectors', () => {
  it.each([
    [
      'G1',
      { secretToken: { nestedProp: '\ud800' } },
      ['<untrusted-property>', '<untrusted-property>'],
    ],
    [
      'G2',
      { ['attacker\ncontrolled']: { nestedProp: '\ud800' } },
      ['<invalid-control>', '<untrusted-property>'],
    ],
    ['G3', { ['\ud800']: 1 }, ['<invalid-utf16>']],
    ['G4', { ['contains\u0000nul']: 1 }, ['<invalid-nul>']],
    ['G5', { '': 'anything' }, ['<empty-name>']],
  ] as const)('%s sanitizes the shared property-name vector', (_id, value, segments) => {
    if (_id === 'G5') expect(sanitizeSegment('', false)).toBe('<empty-name>');
    else expect(scanStringSafety(value, { type: 'object' })?.segments).toEqual(segments);
  });

  it('G6 keeps schema-known ancestors and G7 accepts a valid surrogate pair', () => {
    const schema: SchemaNode = {
      type: 'object',
      properties: {
        stateKey: { type: 'object', properties: { workflowIdentity: { type: 'string' } } },
      },
    };
    expect(
      scanStringSafety({ stateKey: { workflowIdentity: '\ud800' } }, schema)?.segments,
    ).toEqual(['stateKey', 'workflowIdentity']);
    expect(
      scanStringSafety({ stateKey: { workflowIdentity: 'agentic\ud83d\ude00review' } }, schema),
    ).toBeUndefined();
  });

  it('V1 preserves unknown-ancestor segments', () => {
    const result = scanStringSafety({ a: { b: { c: '\ud800' } } }, { type: 'object' });
    expect(result?.segments).toEqual([
      '<untrusted-property>',
      '<untrusted-property>',
      '<untrusted-property>',
    ]);
  });

  it('V2 preserves the terminal invalid UTF-16 segment', () => {
    const result = scanStringSafety({ a: { ['\ud800']: 1 } }, { type: 'object' });
    expect(result?.segments).toEqual(['<untrusted-property>', '<invalid-utf16>']);
  });

  it('V3 distinguishes per-branch and aggregate union knowledge', () => {
    const branchA: SchemaNode = {
      type: 'object',
      properties: { payload: { type: 'object', properties: { alpha: { type: 'string' } } } },
    };
    const branchB: SchemaNode = {
      type: 'object',
      properties: { payload: { type: 'object', properties: { beta: { type: 'string' } } } },
    };
    const hypothetical: SchemaNode = { oneOf: [branchA, branchB] };
    const root = normalizePosition(hypothetical);
    const aPayload = resolveProperty(
      normalizePosition(branchA, undefined, hypothetical),
      'payload',
    );
    const bPayload = resolveProperty(
      normalizePosition(branchB, undefined, hypothetical),
      'payload',
    );
    expect(resolveProperty(aPayload.childSchemaPosition, 'beta').schemaKnown).toBe(false);
    expect(resolveProperty(bPayload.childSchemaPosition, 'beta').schemaKnown).toBe(true);
    const aggregatePayload = resolveProperty(root, 'payload');
    expect(aggregatePayload.schemaKnown).toBe(true);
    expect(resolveProperty(aPayload.childSchemaPosition, 'alpha').schemaKnown).toBe(true);
    expect(resolveProperty(bPayload.childSchemaPosition, 'beta').schemaKnown).toBe(true);
    expect(resolveProperty(aggregatePayload.childSchemaPosition, 'beta').schemaKnown).toBe(true);
    expect(resolveProperty(root, 'payload').schemaKnown).toBe(true);
    const aBeta = resolveProperty(aPayload.childSchemaPosition, 'beta');
    const bBeta = resolveProperty(bPayload.childSchemaPosition, 'beta');
    expect(aBeta.schemaKnown).toBe(false);
    expect(bBeta.schemaKnown).toBe(true);
    expect(resolveProperty(aBeta.childSchemaPosition, 'later').schemaKnown).toBe(false);
    expect(resolveArrayItem(aBeta.childSchemaPosition).schemaKnown).toBe(false);
    expect(resolveProperty(bBeta.childSchemaPosition, 'later').schemaKnown).toBe(false);
    expect(resolveArrayItem(bBeta.childSchemaPosition).schemaKnown).toBe(false);
    const aggregateBeta = resolveProperty(aggregatePayload.childSchemaPosition, 'beta');
    expect(aggregateBeta.schemaKnown).toBe(true);
    expect(resolveProperty(aggregateBeta.childSchemaPosition, 'later').schemaKnown).toBe(false);
    expect(resolveArrayItem(aggregateBeta.childSchemaPosition).schemaKnown).toBe(false);
    const violation = scanStringSafety({ payload: { beta: '\u0000' } }, hypothetical);
    expect(violation?.segments).toEqual(['payload', 'beta']);
    expect(`/${violation?.segments.join('/')}`).toBe('/payload/beta');
    expect(resolveProperty(aggregatePayload.childSchemaPosition, 'extraneous').schemaKnown).toBe(
      false,
    );
  });

  it('keeps shared $ref target identity stable across independent branches', () => {
    const leaf: SchemaNode = { type: 'object', properties: { value: { type: 'string' } } };
    const hypothetical: SchemaNode = {
      oneOf: [{ $ref: '#/definitions/leaf' }, { $ref: '#/definitions/leaf' }],
      definitions: { leaf },
    };
    const root = normalizePosition(hypothetical);
    const branches = (hypothetical.oneOf as readonly SchemaNode[]).map((branch) =>
      normalizePosition(branch, undefined, hypothetical),
    );
    const aValue = resolveProperty(branches[0]!, 'value');
    const bValue = resolveProperty(branches[1]!, 'value');
    const aggregateValue = resolveProperty(root, 'value');
    expect(aValue.schemaKnown).toBe(true);
    expect(bValue.schemaKnown).toBe(true);
    expect(aggregateValue.schemaKnown).toBe(true);
    expect((aValue.childSchemaPosition as { node?: SchemaNode }).node).toBe(
      (bValue.childSchemaPosition as { node?: SchemaNode }).node,
    );

    const cyclicLeaf: SchemaNode = { type: 'object', properties: {} };
    (cyclicLeaf.properties as Record<string, unknown>).next = { $ref: '#/definitions/leaf' };
    const cyclic: SchemaNode = {
      type: 'object',
      properties: { next: { $ref: '#/definitions/leaf' } },
      definitions: { leaf: cyclicLeaf },
    };
    const cyclicPosition = normalizePosition(cyclic, undefined, cyclic);
    const leafPosition = resolveProperty(cyclicPosition, 'next').childSchemaPosition;
    const backEdge = resolveProperty(leafPosition, 'next');
    expect(backEdge.schemaKnown).toBe(true);
    expect(backEdge.childSchemaPosition.kind).toBe('unknown');
  });
});
