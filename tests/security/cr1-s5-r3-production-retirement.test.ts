import { createHash } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, test } from 'vitest';
import {
  verifyR3ProductionContract,
  verifyR3ProductionRetirement,
  verifyR3ProductionSources,
} from '../../runtime/tests/SolusProductionCutoverFixture/r3-production-retirement.mjs';

const relative = 'runtime/tests/SolusProductionCutoverFixture/s5-r3-production-retirement.json';
const load = () => JSON.parse(readFileSync(relative, 'utf8'));
const original = load();
const digest = (bytes: Buffer) => createHash('sha256').update(bytes).digest('hex');
function withClosure(action: (root: string, policy: ReturnType<typeof load>) => void) {
  const root = mkdtempSync(path.join(os.tmpdir(), 'apr-s5-r3-production-'));
  const policy = load();
  try {
    for (const source of [
      relative,
      policy.retired_residual_rule.path,
      ...policy.current_sources.map((r: { path: string }) => r.path),
      ...policy.retained_current_sources.map((r: { path: string }) => r.path),
      'runtime/tests/SolusEvaluationFixture/native-assertions.json',
      'runtime/tests/SolusEvaluationFixture/s5-original-method-identities.json',
      'runtime/tests/SolusEvaluationFixture/s5-format-retirements.json',
    ]) {
      const target = path.join(root, source);
      mkdirSync(path.dirname(target), { recursive: true });
      writeFileSync(target, readFileSync(source));
    }
    expect(verifyR3ProductionRetirement(root).durableMethods).toBe(31);
    action(root, policy);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}
function noop(root: string, row: { source: string; utf8_start: number; utf8_length: number }) {
  const file = path.join(root, row.source),
    bytes = readFileSync(file);
  writeFileSync(
    file,
    Buffer.concat([
      bytes.subarray(0, row.utf8_start),
      Buffer.from('    private static void Noop() {}\n'),
      bytes.subarray(row.utf8_start + row.utf8_length),
    ]),
  );
}

describe('R3 production retirement preserves actual current owners', () => {
  test('binds actual source bodies and separates native evidence from SDK qualification', () => {
    expect(verifyR3ProductionRetirement(process.cwd())).toEqual({
      durableMethods: 31,
      architectureAffected: 10,
      architectureUnchanged: 27,
      authoredReplayCases: 13,
      sdkContextImportQualified: false,
    });
    expect(original.actual_admission_identity.requires_actual_archive_execution).toBe(true);
  });
  test.each(
    original.durable_preservation.current.map((r: { method: string }, i: number) => [i, r.method]),
  )('rejects retained durable body no-op %s %s', (index) => {
    withClosure((root, policy) => {
      noop(root, policy.durable_preservation.current[index]);
      expect(() => verifyR3ProductionSources(root, policy)).toThrow(
        'exact current or retained R3 source',
      );
    });
  });
  test.each(
    original.architecture.affected.map((r: { before: { method: string } }, i: number) => [
      i,
      r.before.method,
    ]),
  )('rejects architecture responsibility substitution %s %s', (index) => {
    const policy = load();
    policy.architecture.affected[index].disposition = 'Drop all checks';
    expect(() => verifyR3ProductionContract(policy)).toThrow(
      'exact R3 production declarations and obligations',
    );
  });
  test.each(
    original.architecture.retained.map((r: { input: { method: string } }, i: number) => [
      i,
      r.input.method,
    ]),
  )('rejects unchanged generic architecture no-op %s %s', (index) => {
    withClosure((root, policy) => {
      noop(root, policy.architecture.retained[index].current);
      expect(() => verifyR3ProductionSources(root, policy)).toThrow();
    });
  });
  test.each([
    'service-fallback',
    'factory-guard',
    'replay-body',
    'replay-variant',
    'guard-test',
    'sdk-qualified',
    'sdk-private-claim',
    'count-swap',
    'missing-current',
    'current-resigned',
  ])('rejects %s mutation', (mutation) => {
    withClosure((root, policy) => {
      if (mutation === 'service-fallback') noop(root, policy.durable_preservation.service_current);
      if (mutation === 'factory-guard') {
        const file = path.join(root, policy.durable_preservation.source);
        const text = readFileSync(file, 'utf8');
        expect(text).toContain('admission ?? throw new ArgumentNullException(nameof(admission))');
        writeFileSync(
          file,
          text.replace(
            'admission ?? throw new ArgumentNullException(nameof(admission))',
            'admission!',
          ),
        );
      }
      if (mutation === 'replay-body') noop(root, policy.authored_replay.predicate_current[0]);
      if (mutation === 'replay-variant') policy.authored_replay.methods[1].current.attributes.pop();
      if (mutation === 'guard-test') noop(root, policy.new_guard_methods[0]);
      if (mutation === 'sdk-qualified') policy.sdk_context_import_qualified = true;
      if (mutation === 'sdk-private-claim')
        policy.actual_admission_identity.role = 'Synthetic SDK execution counts';
      if (mutation === 'count-swap')
        policy.architecture.retained[1] = policy.architecture.retained[0];
      if (mutation === 'missing-current') rmSync(path.join(root, policy.current_sources[0].path));
      if (mutation === 'current-resigned') {
        const row = policy.durable_preservation.current[0];
        noop(root, row);
        policy.current_sources.find((s: { path: string }) => s.path === row.source).sha256 = digest(
          readFileSync(path.join(root, row.source)),
        );
      }
      expect(() => verifyR3ProductionRetirement(root, policy)).toThrow();
    });
  });
  test.each(original.removed_sources.map((r: { path: string }) => r.path))(
    'rejects reintroduced native execution source %s',
    (source) => {
      withClosure((root, policy) => {
        const file = path.join(root, source);
        mkdirSync(path.dirname(file), { recursive: true });
        writeFileSync(
          file,
          'namespace AgenticPrReview.Runtime; internal class RemovedNativePathProbe {}\n',
        );
        expect(() => verifyR3ProductionSources(root, policy)).toThrow(
          'retired native execution source absent',
        );
      });
    },
  );
  test.each(original.retained_current_sources.map((r: { path: string }) => r.path))(
    'rejects current secret/proof/manifest owner drift %s',
    (source) => {
      withClosure((root, policy) => {
        writeFileSync(path.join(root, source), '/* unrelated retirement */\n');
        expect(() => verifyR3ProductionSources(root, policy)).toThrow(
          'exact current or retained R3 source',
        );
      });
    },
  );
});
