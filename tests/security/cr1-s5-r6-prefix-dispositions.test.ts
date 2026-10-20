import { createHash } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, test } from 'vitest';
import {
  verifyR6PrefixFixtureDispositions,
  verifyR6PrefixSourceCorrespondence,
} from '../../runtime/tests/ReviewEvaluationFixture/Economics/Prefix/verify-s5-authored-trace.mjs';

type Slice = {
  source: string;
  method: string;
  utf8_start: number;
  utf8_length: number;
  body_sha256: string;
};
const relative =
  'runtime/tests/ReviewEvaluationFixture/Economics/Prefix/s5-authored-trace-dispositions.json';
const load = () => JSON.parse(readFileSync(relative, 'utf8'));
const sha = (bytes: Buffer) => createHash('sha256').update(bytes).digest('hex');
const expected = { methods: 9, expandedCases: 18, sdkContextImportQualified: false };
function withSource(action: (root: string, policy: ReturnType<typeof load>) => void) {
  const root = mkdtempSync(path.join(os.tmpdir(), 'apr-s5-r6-prefix-'));
  const policy = load();
  try {
    for (const record of policy.current_sources) {
      const target = path.join(root, record.path);
      mkdirSync(path.dirname(target), { recursive: true });
      writeFileSync(target, readFileSync(record.path));
    }
    expect(verifyR6PrefixSourceCorrespondence(root, policy)).toEqual(expected);
    action(root, policy);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}
function replaceRange(
  root: string,
  policy: ReturnType<typeof load>,
  target: Slice,
  replacement: string,
) {
  const file = path.join(root, target.source);
  const before = readFileSync(file);
  const bytes = Buffer.from(replacement);
  const after = Buffer.concat([
    before.subarray(0, target.utf8_start),
    bytes,
    before.subarray(target.utf8_start + target.utf8_length),
  ]);
  const start = target.utf8_start,
    delta = bytes.length - target.utf8_length;
  const slices: Slice[] = [
    ...policy.rows.map((row: { current: Slice }) => row.current),
    policy.fixture_helper,
    policy.factory,
  ];
  for (const record of slices.filter((record) => record.source === target.source)) {
    if (record === target) record.utf8_length = bytes.length;
    else if (record.utf8_start > start) record.utf8_start += delta;
    record.body_sha256 = sha(
      after.subarray(record.utf8_start, record.utf8_start + record.utf8_length),
    );
  }
  writeFileSync(file, after);
  policy.current_sources.find((record: { path: string }) => record.path === target.source).sha256 =
    sha(after);
}
function mutateText(
  root: string,
  policy: ReturnType<typeof load>,
  source: string,
  old: string,
  value: string,
) {
  const bytes = readFileSync(path.join(root, source));
  const start = bytes.indexOf(Buffer.from(old));
  expect(start).toBeGreaterThanOrEqual(0);
  expect(bytes.indexOf(Buffer.from(old), start + 1)).toBe(-1);
  replaceRange(
    root,
    policy,
    {
      source,
      method: 'authored-mutation',
      utf8_start: start,
      utf8_length: Buffer.byteLength(old),
      body_sha256: sha(Buffer.from(old)),
    },
    value,
  );
}

describe('S5 R6 finite authored prefix fixture dispositions', () => {
  test('preserves all 9 original generic methods and 18 variants without claiming SDK restore', () => {
    expect(verifyR6PrefixFixtureDispositions(process.cwd())).toEqual(expected);
  });

  test.each(load().rows.map((row: { original: { method: string } }) => row.original.method))(
    'rejects an actual no-op generic body even after source/body metadata are re-signed: %s',
    (method) => {
      withSource((root, policy) => {
        const row = policy.rows.find(
          (row: { original: { method: string } }) => row.original.method === method,
        );
        const source = readFileSync(path.join(root, row.current.source));
        const body = source
          .subarray(row.current.utf8_start, row.current.utf8_start + row.current.utf8_length)
          .toString();
        const noop =
          body.slice(0, body.indexOf('{') + 1) +
          '\n        await Task.CompletedTask;\n    ' +
          body.slice(body.lastIndexOf('}'));
        replaceRange(root, policy, row.current, noop);
        expect(() => verifyR6PrefixSourceCorrespondence(root, policy)).toThrow(String(method));
      });
    },
  );

  test('rejects removal of the real seed Loop even with re-signed fixture metadata', () => {
    withSource((root, policy) => {
      const record = policy.fixture_helper;
      const source = readFileSync(path.join(root, record.source));
      const body = source
        .subarray(record.utf8_start, record.utf8_start + record.utf8_length)
        .toString();
      replaceRange(
        root,
        policy,
        record,
        body.slice(0, body.indexOf('{') + 1) + '\n throw new NotSupportedException();\n}' + '\n',
      );
      expect(() => verifyR6PrefixSourceCorrespondence(root, policy)).toThrow(
        'fixed generic seed fixture body',
      );
    });
  });

  test('rejects bypassing Stable and the fixed authored shape even with re-signed factory metadata', () => {
    withSource((root, policy) => {
      const record = policy.factory;
      const source = readFileSync(path.join(root, record.source));
      const body = source
        .subarray(record.utf8_start, record.utf8_start + record.utf8_length)
        .toString();
      replaceRange(
        root,
        policy,
        record,
        body.slice(0, body.indexOf('{') + 1) +
          '\n return Create(run, 0, 0, run.StablePlan.PriorSessionSha256);\n}\n',
      );
      expect(() => verifyR6PrefixSourceCorrespondence(root, policy)).toThrow(
        'fixed authored factory body',
      );
    });
  });

  test('does not normalize away a privacy predicate in a supporting helper', () => {
    withSource((root, policy) => {
      mutateText(
        root,
        policy,
        policy.current_sources[0].path,
        'Assert.False(text.Contains(Canary, StringComparison.Ordinal));',
        'Assert.True(text.Contains(Canary, StringComparison.Ordinal));',
      );
      expect(() => verifyR6PrefixSourceCorrespondence(root, policy)).toThrow('all generic helpers');
    });
  });

  test('keeps the original request bound predicate outside the new fixture method', () => {
    withSource((root, policy) => {
      const value = '!AgentRequestBounds.TryValidateReconstructedRequest(run) ||';
      mutateText(root, policy, policy.current_sources[1].path, value, ' '.repeat(value.length));
      expect(() => verifyR6PrefixSourceCorrespondence(root, policy)).toThrow(
        'original Stable/AgentRequestBounds/Restored',
      );
    });
  });

  test('keeps the existing Economics Restored generation predicate', () => {
    withSource((root, policy) => {
      const value = 'artifact.Document.Generation';
      mutateText(
        root,
        policy,
        policy.current_sources[1].path,
        value,
        '0' + ' '.repeat(value.length - 1),
      );
      expect(() => verifyR6PrefixSourceCorrespondence(root, policy)).toThrow(
        'original Stable/AgentRequestBounds/Restored',
      );
    });
  });

  test.each([
    'sdk',
    'authority',
    'fixture-sdk',
    'drop-original',
    'variant',
    'case-count',
    'extra-authority',
    'original-commit',
    'fixture-hash-claim',
    'skeleton-hash-claim',
  ])('rejects role/identity drift %s', (mutation) => {
    const policy = load();
    if (mutation === 'sdk') policy.sdk_context_import_qualified = true;
    if (mutation === 'authority') policy.accepted_state_authority = true;
    if (mutation === 'fixture-sdk') policy.fixture_identity.sdk_import = true;
    if (mutation === 'drop-original') policy.rows.pop();
    if (mutation === 'variant') policy.rows[0].original.attributes = [];
    if (mutation === 'case-count') policy.expanded_cases--;
    if (mutation === 'extra-authority') policy.allow_fresh_fallback = true;
    if (mutation === 'original-commit') policy.original_source_commit = '0'.repeat(40);
    if (mutation === 'fixture-hash-claim') policy.fixture_identity_sha256 = '0'.repeat(64);
    if (mutation === 'skeleton-hash-claim')
      policy.unchanged_test_source_skeleton_sha256 = '0'.repeat(64);
    expect(() => verifyR6PrefixSourceCorrespondence(process.cwd(), policy)).toThrow();
  });
});
