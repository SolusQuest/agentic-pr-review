import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, test } from 'vitest';
import {
  verifyAssertionDispositions,
  verifyS5AssertionSuccessor,
} from '../../runtime/tests/SolusEvaluationFixture/assertion-dispositions.mjs';

const directory = 'runtime/tests/SolusEvaluationFixture/';
const load = (name: string) => JSON.parse(fs.readFileSync(directory + name, 'utf8'));
const protocol = load('s5-protocol-migrations.json');
const successor = () => load('s5-assertion-successor.json');
const expected = {
  originalMethods: 152,
  currentNativeMethods: 117,
  authorizedDeletions: 5,
  migratedScenarios: 30,
};
function withClosure(action: (root: string) => void) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-s5-final-census-'));
  try {
    const files = new Set<string>([
      ...[
        'native-assertions.json',
        's5-original-method-identities.json',
        's5-protocol-migrations.json',
        's5-assertion-successor.json',
      ].map((name) => directory + name),
      ...protocol.immutable_records.map((row: { path: string }) => row.path),
      ...protocol.oracles.map((row: { path: string }) => row.path),
      ...successor().current_sources.map((row: { path: string }) => row.path),
      ...[...protocol.preserved_owners, ...protocol.generic_migration_owners].map(
        (row: { source: string }) => row.source,
      ),
    ]);
    for (const relative of files) {
      const target = path.join(root, relative);
      fs.mkdirSync(path.dirname(target), { recursive: true });
      fs.copyFileSync(relative, target);
    }
    expect(verifyS5AssertionSuccessor(root)).toEqual(expected);
    action(root);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
}

describe('final S5 assertion and source ownership', () => {
  test('keeps immutable V1 identities separate from current execution ownership', () => {
    expect(verifyAssertionDispositions(process.cwd())).toBe(117);
    expect(verifyAssertionDispositions(process.cwd(), load('native-assertions.json'))).toBe(152);
    expect(verifyS5AssertionSuccessor(process.cwd())).toEqual(expected);
    expect(protocol.retired_consumers).toHaveLength(30);
    for (const row of protocol.retired_consumers) {
      expect(row.variant_attributes.length).toBeGreaterThan(0);
      expect(row.original_assertions.length).toBeGreaterThan(0);
      expect(row.reason.length).toBeGreaterThan(80);
      expect(row.current_oracle_labels.length).toBeGreaterThan(0);
    }
    expect(protocol.sdk_context_import_qualified).toBe(false);
    expect(
      protocol.generic_migration_owners.map((row: { method: string }) => row.method),
    ).toContain('AuthoredHistoricalPathCannotGrantCurrentSnapshotAuthority');
    expect(
      protocol.generic_migration_owners.map((row: { method: string }) => row.method),
    ).toContain('FourMiBCompositeSerializesWithinPhysicalLayerCaps');
  });

  test.each([
    'missing',
    'duplicate',
    'unknown',
    'retired-as-retained',
    'empty-body',
    'wrong-source',
    'wrong-disposition',
    'reason',
    'variant',
    'assertion',
    'sdk-claim',
  ])('rejects altered finite disposition %s', (mutation) => {
    const value = successor();
    if (mutation === 'missing') value.assertions.pop();
    if (mutation === 'duplicate') value.assertions[1] = value.assertions[0];
    if (mutation === 'unknown') value.assertions[0].method = 'Unknown';
    const retired = value.assertions.find(
      (row: { disposition: string }) => row.disposition === 'migrated-scenario',
    );
    const retained = value.assertions.find((row: { current: unknown }) => row.current !== null);
    if (mutation === 'retired-as-retained') retired.current = retained.current;
    if (mutation === 'empty-body') retained.current.body_sha256 = '0'.repeat(64);
    if (mutation === 'wrong-source') retained.current.source = retired.source;
    if (mutation === 'wrong-disposition') retained.disposition = 'obsolete-format-retired';
    if (mutation === 'reason') retired.reason = 'Equivalent SDK test';
    if (mutation === 'variant') retired.migration_disposition.variant_attributes.pop();
    if (mutation === 'assertion')
      retired.migration_disposition.original_assertions[0] = 'Assert.True(true)';
    if (mutation === 'sdk-claim') value.sdk_context_import_qualified = true;
    expect(() => verifyS5AssertionSuccessor(process.cwd(), value)).toThrow();
  });

  test.each(protocol.immutable_records.map((row: { path: string }) => row.path))(
    'rejects historical record drift %s',
    (relative) => {
      withClosure((root) => {
        fs.appendFileSync(path.join(root, relative), '\n');
        expect(() => verifyS5AssertionSuccessor(root)).toThrow();
      });
    },
  );
  test.each([
    'original',
    'baseline',
    'protocol',
    'successor',
    'current-source',
    'owner-source',
    'missing-owner',
    'symlink-owner',
    'directory-owner',
    'resurrected-native-source',
  ])('rejects actual source closure drift %s', (mutation) => {
    withClosure((root) => {
      const owner = path.join(root, protocol.preserved_owners[0].source);
      if (mutation === 'original')
        fs.appendFileSync(path.join(root, directory, 'native-assertions.json'), '\n');
      if (mutation === 'baseline')
        fs.appendFileSync(path.join(root, directory, 's5-original-method-identities.json'), '\n');
      if (mutation === 'protocol')
        fs.appendFileSync(path.join(root, directory, 's5-protocol-migrations.json'), '\n');
      if (mutation === 'successor')
        fs.appendFileSync(path.join(root, directory, 's5-assertion-successor.json'), '\n');
      if (mutation === 'current-source')
        fs.appendFileSync(path.join(root, successor().current_sources[0].path), '\n');
      if (mutation === 'owner-source')
        fs.writeFileSync(owner, '[Fact] public void Pretend() { Assert.True(true); }');
      if (mutation === 'missing-owner') fs.unlinkSync(owner);
      if (mutation === 'symlink-owner') {
        fs.renameSync(owner, owner + '.original');
        fs.symlinkSync(owner + '.original', owner);
      }
      if (mutation === 'directory-owner') {
        fs.unlinkSync(owner);
        fs.mkdirSync(owner);
      }
      if (mutation === 'resurrected-native-source') {
        const target = path.join(root, protocol.removed_sources[0]);
        fs.mkdirSync(path.dirname(target), { recursive: true });
        fs.writeFileSync(target, '// native parser reintroduced\n');
      }
      expect(() => verifyS5AssertionSuccessor(root)).toThrow();
    });
  });

  test.each(
    protocol.generic_migration_owners.map((row: { source: string; method: string }) => [
      row.source,
      row.method,
    ]),
  )('rejects replacement of actual generic owner %s/%s', (source, method) => {
    withClosure((root) => {
      fs.writeFileSync(
        path.join(root, source),
        `[Fact] public void ${method}() { Assert.True(true); }`,
      );
      expect(() => verifyS5AssertionSuccessor(root)).toThrow();
    });
  });
});
