import { describe, expect, it } from 'vitest';
import schema from '../../protocol/schemas/provider-run-metadata.v1.json' with { type: 'json' };
import { PROVIDER_RUN_METADATA_SCHEMA_VERSION } from '../state-v2/constants.js';

function walk(value: unknown, visit: (node: Record<string, unknown>) => void): void {
  if (Array.isArray(value)) {
    for (const item of value) walk(item, visit);
    return;
  }
  if (value === null || typeof value !== 'object') return;
  const node = value as Record<string, unknown>;
  visit(node);
  for (const child of Object.values(node)) walk(child, visit);
}

describe('ProviderRunMetadataV1 schema conformance', () => {
  it('is draft-07, closed at every object node, and has no tuple extensions', () => {
    expect(schema.$schema).toBe('http://json-schema.org/draft-07/schema#');
    walk(schema, (node) => {
      if (node.type === 'object' || node.properties !== undefined)
        expect(node.additionalProperties).toBe(false);
      if (node.items !== undefined) expect(Array.isArray(node.items)).toBe(false);
      expect(node.additionalItems).toBeUndefined();
    });
  });

  it('has an acyclic local ref graph and binds schema version to the shared constant', () => {
    assertAcyclicLocalRefs(schema);
    expect((schema.properties as Record<string, { const?: number }>).schemaVersion.const).toBe(
      PROVIDER_RUN_METADATA_SCHEMA_VERSION,
    );
  });

  it('rejects an inline-ancestor local ref cycle', () => {
    const cyclic: Record<string, unknown> = {
      type: 'object',
      properties: { payload: { type: 'object', properties: {} } },
    };
    const payload = cyclic.properties as Record<string, Record<string, unknown>>;
    (payload.payload.properties as Record<string, unknown>).next = {
      $ref: '#/properties/payload',
    };
    expect(() => assertAcyclicLocalRefs(cyclic)).toThrow(/cycle/i);
  });
});

function assertAcyclicLocalRefs(root: unknown): void {
  const activeNodes = new Set<object>();
  const completedNodes = new Set<object>();
  const resolvedNodes = new Map<string, object>();
  const visit = (value: unknown): void => {
    if (Array.isArray(value)) {
      for (const item of value) visit(item);
      return;
    }
    if (value === null || typeof value !== 'object') return;
    const node = value as Record<string, unknown>;
    if (completedNodes.has(node)) return;
    if (activeNodes.has(node)) throw new Error('schema $ref cycle');
    activeNodes.add(node);
    if (typeof node.$ref === 'string') {
      const ref = node.$ref;
      expect(ref.startsWith('#/')).toBe(true);
      let target: unknown = root;
      for (const part of ref.slice(2).split('/'))
        target = (target as Record<string, unknown>)[part];
      expect(target).toBeDefined();
      const prior = resolvedNodes.get(ref);
      if (prior) expect(prior).toBe(target);
      else resolvedNodes.set(ref, target as object);
      visit(target);
    }
    for (const child of Object.values(node)) visit(child);
    activeNodes.delete(node);
    completedNodes.add(node);
  };
  visit(root);
}
