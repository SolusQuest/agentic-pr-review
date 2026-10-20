import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, test } from 'vitest';
import {
  verifyNativeCodecContract,
  verifyNativeCodecRetirements,
  verifyNativeCodecSourceLink,
} from '../../runtime/tests/SolusEvaluationFixture/native-codec-retirements.mjs';

function toolFormatClosurePaths(): string[] {
  const relative = 'runtime/tests/SolusEvaluationFixture/s5-three-tool-format-retirements.json';
  const policy = JSON.parse(readFileSync(relative, 'utf8'));
  const nextRelative = 'runtime/tests/SolusEvaluationFixture/s5-seven-restorer-retirements.json';
  const nextPolicy = JSON.parse(readFileSync(nextRelative, 'utf8'));
  return [
    nextRelative,
    ...[...nextPolicy.current_owners, ...nextPolicy.independent_safety_owners].map(
      (row: { source: string }) => row.source,
    ),
    ...[...nextPolicy.retained_generic_sources, ...nextPolicy.immutable_records].map(
      (row: { path: string }) => row.path,
    ),
    relative,
    ...[...policy.current_owners, ...policy.independent_safety_owners].map(
      (row: { source: string }) => row.source,
    ),
    ...[...policy.retained_generic_sources, ...policy.immutable_records].map(
      (row: { path: string }) => row.path,
    ),
  ];
}

const relative = 'runtime/tests/SolusEvaluationFixture/s5-native-codec-retirements.json';
const load = () => JSON.parse(readFileSync(relative, 'utf8'));
const original = load();
const expressions = original.rows.flatMap(
  (row: { method: string; assertions: unknown[] }, index: number) =>
    row.assertions.map((_: unknown, assertion: number) => [index, assertion, row.method] as const),
);
function withClosure(action: (root: string, policy: ReturnType<typeof load>) => void) {
  const root = mkdtempSync(path.join(os.tmpdir(), 'apr-s5-six-codec-'));
  const policy = load();
  const mixed = JSON.parse(
    readFileSync(
      'runtime/tests/SolusEvaluationFixture/s5-three-mixed-consumer-retirements.json',
      'utf8',
    ),
  );
  try {
    for (const source of [
      relative,
      ...toolFormatClosurePaths(),
      policy.current_source.path,
      'runtime/tests/SolusEvaluationFixture/s5-three-mixed-consumer-retirements.json',
      ...mixed.current_owners.map((row: { source: string }) => row.source),
      ...mixed.immutable_records.map((row: { path: string }) => row.path),
      ...policy.retained_generic_sources.map((row: { path: string }) => row.path),
      ...policy.independent_current_sources.map((row: { path: string }) => row.path),
    ]) {
      const target = path.join(root, source);
      mkdirSync(path.dirname(target), { recursive: true });
      writeFileSync(target, readFileSync(source));
    }
    expect(verifyNativeCodecRetirements(root).remainingMethods).toBe(42);
    action(root, policy);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

describe('six exact native codec retirements outside original V1', () => {
  test('binds all six original methods and every assertion while retaining generic owners', () => {
    expect(verifyNativeCodecRetirements(process.cwd())).toEqual({
      methods: 6,
      assertionExpressions: 43,
      remainingMethods: 42,
      sdkContextImportQualified: false,
    });
    const v1 = JSON.parse(
      readFileSync('runtime/tests/SolusEvaluationFixture/native-assertions.json', 'utf8'),
    );
    expect(v1.deletion_authorized).toBe(false);
    expect(v1.assertions).toHaveLength(152);
    for (const row of original.rows) {
      expect(v1.assertions.some((item: { method: string }) => item.method === row.method)).toBe(
        false,
      );
    }
  });
  test.each(expressions)('rejects altered original assertion %s/%s (%s)', (row, assertion) => {
    const policy = load();
    policy.rows[row].assertions[assertion].exact_expression = 'Assert.True(true)';
    expect(() => verifyNativeCodecContract(policy)).toThrow(
      'exact six-codec assertion dispositions',
    );
  });
  test.each([
    'drop',
    'duplicate',
    'swap',
    'identity',
    'attributes',
    'reason',
    'sdk',
    'v1',
    'count',
    'input',
  ])('rejects %s policy substitution', (mutation) => {
    const policy = load();
    if (mutation === 'drop') policy.rows.pop();
    if (mutation === 'duplicate') policy.rows.push(policy.rows[0]);
    if (mutation === 'swap') policy.rows[1] = policy.rows[0];
    if (mutation === 'identity') policy.rows[0].original.body_sha256 = '0'.repeat(64);
    if (mutation === 'attributes') policy.rows[0].retirement_input.attributes = [];
    if (mutation === 'reason') policy.rows[0].assertions[0].reason = 'Equivalent SDK capacity';
    if (mutation === 'sdk') policy.sdk_context_import_qualified = true;
    if (mutation === 'v1') policy.original_v1_membership = true;
    if (mutation === 'count') policy.current_source.attributed_methods = 43;
    if (mutation === 'input') policy.retirement_input_source.sha256 = '0'.repeat(64);
    expect(() => verifyNativeCodecContract(policy)).toThrow();
  });
  test.each(['missing', 'changed', 'reintroduced', 'retained-noop'])(
    'rejects actual %s source',
    (mutation) => {
      withClosure((root, policy) => {
        const file = path.join(root, policy.current_source.path);
        if (mutation === 'missing') rmSync(file);
        if (mutation === 'changed') writeFileSync(file, readFileSync(file, 'utf8') + '\n');
        if (mutation === 'reintroduced')
          writeFileSync(file, readFileSync(file, 'utf8') + '\n// ' + policy.rows[0].method);
        if (mutation === 'retained-noop') {
          const bytes = readFileSync(file);
          const method = policy.remaining_test_methods[0];
          writeFileSync(
            file,
            Buffer.concat([
              bytes.subarray(0, method.utf8_start),
              Buffer.from(
                '    [Fact] public void ' + method.method + '() { Assert.True(true); }\n',
              ),
              bytes.subarray(method.utf8_start + method.utf8_length),
            ]),
          );
        }
        expect(() => verifyNativeCodecSourceLink(root)).toThrow();
      });
    },
  );
  test.each(original.retained_generic_sources.map((row: { path: string }) => row.path))(
    'rejects generic owner drift: %s',
    (source) => {
      withClosure((root) => {
        const file = path.join(root, source);
        writeFileSync(file, readFileSync(file, 'utf8') + '\n// unauthorized owner drift\n');
        expect(() => verifyNativeCodecRetirements(root)).toThrow(
          'generic and independent current owners are preserved',
        );
      });
    },
  );
  test.each(original.independent_current_sources.map((row: { path: string }) => row.path))(
    'rejects independent SDK owner drift: %s',
    (source) => {
      withClosure((root) => {
        const file = path.join(root, source);
        writeFileSync(file, '/* no independent execution proof */\n');
        expect(() => verifyNativeCodecRetirements(root)).toThrow(
          'generic and independent current owners are preserved',
        );
      });
    },
  );
});
