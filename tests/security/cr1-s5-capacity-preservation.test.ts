import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, expect, test } from 'vitest';

const source = readFileSync('runtime/tests/SolusCapacityFixture/verify.mjs', 'utf8');
const begin = source.indexOf('  function retainedAcceptedObjects(before) {');
const end = source.indexOf("  stage = 'useful_fresh_and_restored';", begin);
assert.ok(begin > 0 && end > begin);
// Run the actual installed-proof audit, not a mirrored model or SDK acceptance.
const audit = new Function(
  'assert',
  source.slice(begin, end) + '\nreturn refusedWorkPreservesPredecessor;',
)(assert) as (before: unknown[], after: unknown[]) => void;
const candidate = {
  operation: 'upload',
  kind: 'candidate',
  object_id: '1001',
  digest: 'a'.repeat(64),
  committed: true,
};
const acceptance = {
  ...candidate,
  kind: 'acceptance',
  object_id: '1002',
  digest: 'b'.repeat(64),
};
const before = [candidate, acceptance];

describe('actual capacity proof predecessor audit', () => {
  test('allows a failed round to claim and clean its own objects', () => {
    const added = [
      { ...candidate, kind: 'context_claim', object_id: '1003' },
      { operation: 'delete', object_id: '1003', committed: true },
    ];
    expect(() => audit(before, [...before, ...added])).not.toThrow();
  });

  test.each(['1001', '1002'])('rejects deletion attempts against predecessor %s', (object_id) => {
    for (const committed of [false, true])
      expect(() =>
        audit(before, [...before, { operation: 'delete', object_id, committed }]),
      ).toThrow();
  });

  test.each(['candidate', 'acceptance'])('rejects a committed replacement %s', (kind) => {
    expect(() => audit(before, [...before, { ...candidate, kind, object_id: '1003' }])).toThrow();
    expect(() =>
      audit(before, [...before, { ...candidate, kind, object_id: '1003', committed: false }]),
    ).not.toThrow();
  });

  test('uses the current live predecessor inventory instead of deleted historical uploads', () => {
    const prior = [
      { ...candidate, object_id: '999' },
      { operation: 'delete', object_id: '999', committed: true },
      ...before,
    ];
    expect(() =>
      audit(prior, [...prior, { operation: 'delete', object_id: '999', committed: true }]),
    ).not.toThrow();
    expect(() =>
      audit(prior, [...prior, { operation: 'delete', object_id: '1001', committed: true }]),
    ).toThrow();
  });

  test.each([
    [],
    [candidate],
    [acceptance],
    [{ ...candidate, committed: false }, acceptance],
    [{ ...candidate, object_id: undefined }, acceptance],
    [{ ...candidate, digest: 'wrong' }, acceptance],
    [...before, { operation: 'delete', object_id: '1001', committed: true }],
  ])('requires an actual complete accepted predecessor inventory %j', (rows) => {
    expect(() => audit(rows, rows)).toThrow();
  });
});
