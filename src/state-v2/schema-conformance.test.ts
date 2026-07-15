/**
 * Schema author-time conformance test.
 *
 * Walks the real StateManifestV2 schema and asserts:
 *   - No direct, indirect, or multi-node $ref cycles.
 *   - No tuple-form `items` (items is a single schema or omitted).
 *   - No `additionalItems` keyword.
 *
 * Also loads negative-fixture schemas from
 * `protocol/fixtures/schema-resolver-conformance/` and asserts the
 * author-time detector rejects each; loads the positive fixture and
 * asserts it is accepted.
 *
 * See the design contract's `### Schema-position resolver` for the
 * frozen semantics.
 */

import { describe, expect, it } from 'vitest';
import * as fs from 'node:fs';
import * as path from 'node:path';
import schema from '../../protocol/schemas/state-manifest.v2.json' with { type: 'json' };

type Node = Readonly<Record<string, unknown>>;

function isObject(v: unknown): v is Node {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

/**
 * Author-time detector: walks every subtree of `root`, dereferences $ref,
 * and reports whether the walk encountered a cycle by any path. Also
 * flags tuple-form `items` and `additionalItems`.
 */
export interface ConformanceReport {
  readonly hasRefCycle: boolean;
  readonly hasTupleItems: boolean;
  readonly hasAdditionalItems: boolean;
}

function conformanceCheck(root: Node): ConformanceReport {
  const report = { hasRefCycle: false, hasTupleItems: false, hasAdditionalItems: false };

  function dereference(ref: string): Node | undefined {
    if (!ref.startsWith('#')) return undefined;
    const pointer = ref.slice(1);
    if (pointer === '') return root;
    if (!pointer.startsWith('/')) return undefined;
    const parts = pointer
      .slice(1)
      .split('/')
      .map((p) => p.replace(/~1/g, '/').replace(/~0/g, '~'));
    let cur: unknown = root;
    for (const p of parts) {
      // Array-aware traversal: accept a numeric segment against an array
      // (base-10 non-negative in-range only), reject any other segment
      // against an array.
      if (Array.isArray(cur)) {
        const idx = Number(p);
        if (!Number.isInteger(idx) || idx < 0 || idx >= (cur as unknown[]).length) return undefined;
        cur = (cur as unknown[])[idx];
        continue;
      }
      if (!isObject(cur)) return undefined;
      cur = (cur as Record<string, unknown>)[p];
    }
    return isObject(cur) ? cur : undefined;
  }

  function walk(node: unknown, activeRefs: ReadonlySet<Node>): void {
    if (!isObject(node)) return;

    if (typeof node.$ref === 'string') {
      const target = dereference(node.$ref);
      if (target === undefined) return;
      if (activeRefs.has(target)) {
        report.hasRefCycle = true;
        return;
      }
      const nextActive = new Set(activeRefs);
      nextActive.add(target);
      walk(target, nextActive);
      return;
    }

    if ('items' in node && Array.isArray(node.items)) {
      report.hasTupleItems = true;
    }
    if ('additionalItems' in node) {
      report.hasAdditionalItems = true;
    }

    for (const [key, value] of Object.entries(node)) {
      if (key === '$defs' || key === 'definitions' || key === 'properties') {
        if (isObject(value)) {
          for (const child of Object.values(value)) {
            walk(child, activeRefs);
          }
        }
      } else if (key === 'oneOf' || key === 'anyOf' || key === 'allOf') {
        if (Array.isArray(value)) for (const c of value) walk(c, activeRefs);
      } else if (isObject(value)) {
        walk(value, activeRefs);
      }
    }
  }

  walk(root, new Set<Node>());
  return report;
}

describe('schema author-time conformance', () => {
  it('the real StateManifestV2 schema has no $ref cycles, no tuple items, no additionalItems', () => {
    const report = conformanceCheck(schema as unknown as Node);
    expect(report.hasRefCycle).toBe(false);
    expect(report.hasTupleItems).toBe(false);
    expect(report.hasAdditionalItems).toBe(false);
  });
});

// Author-time negative fixtures. Loaded on demand.
const FIXTURE_DIR = path.join(process.cwd(), 'protocol', 'fixtures', 'schema-resolver-conformance');

function loadFixture(name: string): Node {
  const p = path.join(FIXTURE_DIR, `${name}.schema.json`);
  return JSON.parse(fs.readFileSync(p, 'utf8')) as Node;
}

describe('schema author-time negative fixtures are rejected', () => {
  it('cyclic-root-back-edge is rejected (root $ref back-edge)', () => {
    const report = conformanceCheck(loadFixture('cyclic-root-back-edge'));
    expect(report.hasRefCycle).toBe(true);
  });

  it('cyclic-inline-ancestor-back-edge is rejected (inline ancestor back-edge)', () => {
    const report = conformanceCheck(loadFixture('cyclic-inline-ancestor-back-edge'));
    expect(report.hasRefCycle).toBe(true);
  });

  it('cyclic-multi-node is rejected ($defs/A -> $defs/B -> $defs/A)', () => {
    const report = conformanceCheck(loadFixture('cyclic-multi-node'));
    expect(report.hasRefCycle).toBe(true);
  });

  it('tuple-form-items is rejected', () => {
    const report = conformanceCheck(loadFixture('tuple-form-items'));
    expect(report.hasTupleItems).toBe(true);
  });

  it('additional-items-present is rejected', () => {
    const report = conformanceCheck(loadFixture('additional-items-present'));
    expect(report.hasAdditionalItems).toBe(true);
  });
});

describe('schema author-time positive fixture is accepted', () => {
  it('independent-multi-branch-reference is accepted', () => {
    const report = conformanceCheck(loadFixture('independent-multi-branch-reference'));
    expect(report.hasRefCycle).toBe(false);
    expect(report.hasTupleItems).toBe(false);
    expect(report.hasAdditionalItems).toBe(false);
  });
});
