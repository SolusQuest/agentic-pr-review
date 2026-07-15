/**
 * Resolver runtime consequence test — asserts the three observable
 * consequences frozen in the design contract's
 * `### Schema-position resolver`, section "Concrete observable
 * consequences", against synthetic hypothetical schemas that only the
 * runtime resolver (not the author-time detector) is asked to evaluate.
 */

import { describe, expect, it } from 'vitest';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { normalizePosition, resolveProperty, type SchemaNode } from './shared-safe-path.js';

const FIXTURE_DIR = path.join(process.cwd(), 'protocol', 'fixtures', 'schema-resolver-conformance');
function loadFixture(name: string): SchemaNode {
  return JSON.parse(
    fs.readFileSync(path.join(FIXTURE_DIR, `${name}.schema.json`), 'utf8'),
  ) as SchemaNode;
}

describe('resolver runtime consequence — root back-edge', () => {
  it('parent key remains schemaKnown = true; child position degrades to UnknownPosition-equivalent', () => {
    const schema = loadFixture('cyclic-root-back-edge');
    const rootPos = normalizePosition(schema);
    const nextResult = resolveProperty(rootPos, 'next', schema);
    expect(nextResult.schemaKnown).toBe(true);
    // Child position is unknown-equivalent: any further resolve returns
    // schemaKnown = false.
    const nested = resolveProperty(nextResult.childSchemaPosition, 'anything', schema);
    expect(nested.schemaKnown).toBe(false);
  });
});

describe('resolver runtime consequence — inline ancestor back-edge', () => {
  it('the back-edge point yields parent-known + child-unknown observation', () => {
    const schema = loadFixture('cyclic-inline-ancestor-back-edge');
    const rootPos = normalizePosition(schema);
    const payloadResult = resolveProperty(rootPos, 'payload', schema);
    expect(payloadResult.schemaKnown).toBe(true);
    const nextResult = resolveProperty(payloadResult.childSchemaPosition, 'next', schema);
    expect(nextResult.schemaKnown).toBe(true);
    const deeper = resolveProperty(nextResult.childSchemaPosition, 'anything', schema);
    expect(deeper.schemaKnown).toBe(false);
  });
});

describe('resolver runtime consequence — independent multi-branch reference is not a cycle', () => {
  it('both branches of a shared non-recursive $ref independently declare their leaf', () => {
    const schema = loadFixture('independent-multi-branch-reference');
    const rootPos = normalizePosition(schema);
    // Union at the top level should still find `payload` as schema-known.
    const payloadResult = resolveProperty(rootPos, 'payload', schema);
    expect(payloadResult.schemaKnown).toBe(true);
    const valueResult = resolveProperty(payloadResult.childSchemaPosition, 'value', schema);
    expect(valueResult.schemaKnown).toBe(true);
    // extraneous is not declared under leaf.
    const extraneous = resolveProperty(payloadResult.childSchemaPosition, 'extraneous', schema);
    expect(extraneous.schemaKnown).toBe(false);
  });
});
