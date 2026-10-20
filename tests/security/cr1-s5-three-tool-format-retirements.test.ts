import { createHash } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, test } from 'vitest';
import {
  verifyThreeToolFormatContract,
  verifyThreeToolFormatRetirements,
  verifyThreeToolFormatSourceLink,
} from '../../runtime/tests/SolusEvaluationFixture/three-tool-format-retirements.mjs';

const relative = 'runtime/tests/SolusEvaluationFixture/s5-three-tool-format-retirements.json';
const load = () => JSON.parse(readFileSync(relative, 'utf8'));
const original = load();
const digest = (bytes: Buffer) => createHash('sha256').update(bytes).digest('hex');
function withClosure(action: (root: string, policy: ReturnType<typeof load>) => void) {
  const root = mkdtempSync(path.join(os.tmpdir(), 'apr-s5-three-tool-'));
  const policy = load();
  try {
    for (const source of new Set<string>([
      relative,
      policy.current_source.path,
      ...policy.current_owners.map((row: { source: string }) => row.source),
      ...policy.independent_safety_owners.map((row: { source: string }) => row.source),
      ...policy.retained_generic_sources.map((row: { path: string }) => row.path),
      ...policy.immutable_records.map((row: { path: string }) => row.path),
    ])) {
      const target = path.join(root, source);
      mkdirSync(path.dirname(target), { recursive: true });
      writeFileSync(target, readFileSync(source));
    }
    expect(verifyThreeToolFormatRetirements(root).remainingMethods).toBe(36);
    action(root, policy);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}
function replaceDeclaration(
  root: string,
  row: {
    source: string;
    method: string;
    utf8_start: number;
    utf8_length: number;
  },
) {
  const target = path.join(root, row.source),
    bytes = readFileSync(target);
  writeFileSync(
    target,
    Buffer.concat([
      bytes.subarray(0, row.utf8_start),
      Buffer.from('    [Fact] public void ' + row.method + '() { Assert.True(true); }\n'),
      bytes.subarray(row.utf8_start + row.utf8_length),
    ]),
  );
  return readFileSync(target);
}

describe('three native tool-format consumers preserve generic argument admission', () => {
  test('binds every original assertion to actual current owners or explicit old-format retirement', () => {
    expect(verifyThreeToolFormatRetirements(process.cwd())).toEqual({
      methods: 3,
      assertionExpressions: 25,
      retainedGenericAssertions: 5,
      formatRetiredAssertions: 20,
      remainingMethods: 36,
      sdkContextImportQualified: false,
    });
  });
  test.each(
    original.rows.flatMap((row: { method: string; assertions: unknown[] }, r: number) =>
      row.assertions.map((_: unknown, a: number) => [r, a, row.method] as const),
    ),
  )('rejects altered original assertion %s/%s (%s)', (r, a) => {
    const policy = load();
    policy.rows[r].assertions[a].exact_expression = 'Assert.True(true)';
    expect(() => verifyThreeToolFormatContract(policy)).toThrow();
  });
  test.each(
    original.rows.flatMap((row: { assertions: { disposition: string }[] }, r: number) =>
      row.assertions.flatMap((a, index) =>
        a.disposition === 'retained-generic-execution' ? [[r, index] as const] : [],
      ),
    ),
  )('rejects physical generic assertion weakening %s/%s', (r, a) => {
    withClosure((root, policy) => {
      const assertion = policy.rows[r].assertions[a];
      const owner = policy.current_owners.find(
        (row: { method: string }) => row.method === assertion.owner.method,
      );
      const target = path.join(root, owner.source),
        bytes = readFileSync(target);
      const body = bytes
        .subarray(owner.utf8_start, owner.utf8_start + owner.utf8_length)
        .toString('utf8');
      expect(body).toContain(assertion.exact_expression);
      writeFileSync(
        target,
        Buffer.concat([
          bytes.subarray(0, owner.utf8_start),
          Buffer.from(body.replace(assertion.exact_expression, 'Assert.True(true)')),
          bytes.subarray(owner.utf8_start + owner.utf8_length),
        ]),
      );
      expect(() => verifyThreeToolFormatSourceLink(root)).toThrow();
    });
  });
  test.each(original.current_owners.map((row: { method: string }) => row.method))(
    'rejects actual owner NOP and resealed owner %s',
    (method) => {
      withClosure((root, policy) => {
        const row = policy.current_owners.find(
          (owner: { method: string }) => owner.method === method,
        );
        const bytes = replaceDeclaration(root, row);
        expect(() => verifyThreeToolFormatSourceLink(root)).toThrow();
        const replacement = Buffer.from(
          '    [Fact] public void ' + row.method + '() { Assert.True(true); }\n',
        );
        const oldLength = row.utf8_length;
        for (const owner of policy.current_owners) {
          if (owner.source !== row.source) continue;
          owner.source_sha256 = digest(bytes);
          if (owner !== row && owner.utf8_start > row.utf8_start)
            owner.utf8_start += replacement.length - oldLength;
        }
        row.utf8_length = replacement.length;
        row.body_sha256 = digest(replacement);
        row.attributes = ['Fact'];
        row.assertions = ['Assert.True(true)'];
        for (const declaration of policy.rows)
          for (const assertion of declaration.assertions)
            if (assertion.owner?.method === row.method)
              assertion.owner.body_sha256 = row.body_sha256;
        writeFileSync(path.join(root, relative), JSON.stringify(policy));
        expect(() => verifyThreeToolFormatSourceLink(root, policy)).toThrow(
          'exact three tool-format assertion dispositions',
        );
      });
    },
  );
  test('rejects missing actual generic Fact', () => {
    withClosure((root, policy) => {
      const target = path.join(root, policy.current_owners[0].source);
      writeFileSync(target, readFileSync(target, 'utf8').replace('[Fact]', ''));
      expect(() => verifyThreeToolFormatSourceLink(root)).toThrow();
    });
  });
  test.each([
    'equivalent-memberdata',
    'invalid-memberdata',
    'equivalent-first-row',
    'invalid-first-row',
  ])('rejects missing actual provider variant %s', (mutation) => {
    withClosure((root) => {
      const target = path.join(
        root,
        'runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Tools/AgentToolProviderArgumentTests.cs',
      );
      let source = readFileSync(target, 'utf8');
      const family = mutation.startsWith('equivalent') ? 'EquivalentSpellings' : 'InvalidSpellings';
      if (mutation.endsWith('memberdata')) {
        const attribute = '[MemberData(nameof(' + family + '))]';
        expect(source).toContain(attribute);
        source = source.replace(attribute, '');
      } else {
        const property = source.indexOf(family + ' => new()');
        expect(property).toBeGreaterThan(0);
        const start = source.indexOf('\n        {\n', property),
          end = source.indexOf('\n        },', start);
        expect(start).toBeGreaterThan(property);
        expect(end).toBeGreaterThan(start);
        source = source.slice(0, start) + source.slice(end + '\n        },'.length);
      }
      writeFileSync(target, source);
      expect(() => verifyThreeToolFormatSourceLink(root)).toThrow();
    });
  });
  test.each(original.rows.map((row: { method: string }) => row.method))(
    'rejects dropped original Fact attribute %s',
    (method) => {
      const policy = load();
      policy.rows.find(
        (row: { method: string }) => row.method === method,
      ).retirement_input.attributes = [];
      expect(() => verifyThreeToolFormatContract(policy)).toThrow();
    },
  );
  test.each(original.independent_safety_owners.map((row: { method: string }) => row.method))(
    'rejects independent current safety owner NOP %s',
    (method) => {
      withClosure((root, policy) => {
        replaceDeclaration(
          root,
          policy.independent_safety_owners.find((row: { method: string }) => row.method === method),
        );
        expect(() => verifyThreeToolFormatSourceLink(root)).toThrow();
      });
    },
  );
  test.each(original.retained_generic_sources.map((row: { path: string }) => row.path))(
    'rejects generic tool/loop source drift %s',
    (source) => {
      withClosure((root) => {
        const target = path.join(root, source);
        writeFileSync(target, readFileSync(target, 'utf8') + '\n// generic guard changed');
        expect(() => verifyThreeToolFormatSourceLink(root)).toThrow();
      });
    },
  );
  test.each(original.remaining_test_methods.map((row: { method: string }) => row.method))(
    'rejects retained method NOP %s',
    (method) => {
      withClosure((root, policy) => {
        replaceDeclaration(
          root,
          policy.remaining_test_methods.find((row: { method: string }) => row.method === method),
        );
        expect(() => verifyThreeToolFormatSourceLink(root)).toThrow();
      });
    },
  );
  test.each([
    'row-drop',
    'row-swap',
    'generic-reclassified',
    'reason',
    'sdk',
    'count',
    'input',
    'owner-drop',
    'variant',
    'baseline',
    'current-resigned',
  ])('rejects %s policy substitution', (mutation) => {
    const policy = load();
    if (mutation === 'row-drop') policy.rows.pop();
    if (mutation === 'row-swap') policy.rows[1] = policy.rows[0];
    if (mutation === 'generic-reclassified')
      policy.rows[1].assertions[0].disposition = 'obsolete-native-format-only';
    if (mutation === 'reason')
      policy.rows[0].assertions[0].reason = 'Equivalent SDK state admission';
    if (mutation === 'sdk') policy.sdk_context_import_qualified = true;
    if (mutation === 'count') policy.current_source.attributed_methods = 42;
    if (mutation === 'input') policy.input_source.sha256 = '0'.repeat(64);
    if (mutation === 'owner-drop') policy.current_owners.pop();
    if (mutation === 'variant') policy.current_owners[0].attributes.pop();
    if (mutation === 'baseline') policy.rows[0].original.body_sha256 = '0'.repeat(64);
    if (mutation === 'current-resigned') policy.current_source.sha256 = '0'.repeat(64);
    expect(() => verifyThreeToolFormatContract(policy)).toThrow();
  });
  test.each(['missing', 'retired-reintroduced', 'helper-nop'])(
    'rejects actual %s current source',
    (mutation) => {
      withClosure((root, policy) => {
        const target = path.join(root, policy.current_source.path);
        if (mutation === 'missing') rmSync(target);
        if (mutation === 'retired-reintroduced')
          writeFileSync(target, readFileSync(target, 'utf8') + '\n// ' + policy.rows[0].method);
        if (mutation === 'helper-nop')
          replaceDeclaration(
            root,
            policy.remaining_methods.find(
              (row: { attributes: unknown[] }) => row.attributes.length === 0,
            ),
          );
        expect(() => verifyThreeToolFormatSourceLink(root)).toThrow();
      });
    },
  );
  test.each(original.immutable_records.map((row: { path: string }) => row.path))(
    'rejects rewriting immutable prior record %s',
    (source) => {
      withClosure((root) => {
        const target = path.join(root, source);
        writeFileSync(target, readFileSync(target, 'utf8') + '\n');
        expect(() => verifyThreeToolFormatRetirements(root)).toThrow(
          'prior disposition record is immutable',
        );
      });
    },
  );
});
