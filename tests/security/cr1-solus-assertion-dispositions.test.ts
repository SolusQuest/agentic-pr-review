import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, test } from 'vitest';
import {
  verifyAssertionDispositions,
  verifyS5AssertionSuccessor,
  verifyR7AssertionSuccessor,
  cr2FixtureLinkedSources,
  cr2BuildDiagnosticSources,
  cr2QualificationSources,
  verifyV1QualificationOwner,
} from '../../runtime/tests/SolusEvaluationFixture/assertion-dispositions.mjs';

const directory = 'runtime/tests/SolusEvaluationFixture/';
const load = (name: string) => JSON.parse(fs.readFileSync(directory + name, 'utf8'));
const protocol = load('s5-protocol-migrations.json');
const successor = () => load('s5-assertion-successor.json');
const r7 = load('r7-assertion-successor.json');
const r7Retired = new Set<string>(r7.retired_sources.map((row: { path: string }) => row.path));
const expected = {
  originalMethods: 152,
  currentNativeMethods: 116,
  authorizedDeletions: 5,
  migratedScenarios: 31,
};
function withClosure(action: (root: string) => void) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-s5-final-census-'));
  try {
    const files = new Set<string>([
      ...cr2FixtureLinkedSources.map(([path]: [string, string]) => path),
      ...cr2BuildDiagnosticSources.map(([path]: [string, string]) => path),
      ...cr2QualificationSources.map(([path]: [string, string]) => path),
      ...[
        'native-assertions.json',
        's5-original-method-identities.json',
        's5-protocol-migrations.json',
        's5-assertion-successor.json',
        'r7-assertion-successor.json',
      ].map((name) => directory + name),
      ...r7.current_sources.map((row: { path: string }) => row.path),
      ...r7.historical_records.map((row: { path: string }) => row.path),
      ...protocol.immutable_records.map((row: { path: string }) => row.path),
      ...protocol.oracles.map((row: { path: string }) => row.path),
      ...successor().current_sources.map((row: { path: string }) => row.path),
      ...[...protocol.preserved_owners, ...protocol.generic_migration_owners].map(
        (row: { source: string }) => row.source,
      ),
    ]);
    for (const relative of files) {
      if (r7Retired.has(relative)) continue;
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

describe('S5 history and current R7 assertion ownership', () => {
  test('admits only the single V1 read-only dispatch while preserving exact predecessor source and owner bodies', () => {
    const bytes = fs.readFileSync(cr2QualificationSources[0][0]);
    const owners = [...protocol.preserved_owners, ...protocol.generic_migration_owners].filter(
      (row) => row.source === cr2QualificationSources[0][0],
    );
    expect(owners.map((row) => row.method).sort()).toEqual(['Main', 'RequireEncryptedState']);
    for (const owner of owners) {
      const current = verifyV1QualificationOwner(bytes, owner);
      for (const assertion of owner.assertions ?? []) expect(current.body).toContain(assertion);
      expect(current.preservedOriginal).toBe(true);
      for (const change of [
        'extra',
        'duplicate',
        'approximate',
        'missing',
        'range',
        'body',
        'owner',
      ]) {
        const row = structuredClone(owner);
        let text = bytes.toString();
        const dispatch =
          text.split('\n').find((line) => line.includes('args is ["transaction-inventory"'))! +
          '\n';
        if (change === 'extra') text += '\n';
        if (change === 'duplicate') text = text.replace(dispatch, dispatch + dispatch);
        if (change === 'approximate')
          text = text.replace('var transactionInventory', 'var anotherInventory');
        if (change === 'missing') text = text.replace(dispatch, '');
        if (change === 'range') row.utf8_start++;
        if (change === 'body') row.body_sha256 = '0'.repeat(64);
        if (change === 'owner') row.method = 'UnknownOwner';
        expect(() => verifyV1QualificationOwner(Buffer.from(text), row)).toThrow();
      }
    }
  });
  test('binds the separate qualification helper without changing historical framework linkage', () => {
    withClosure((root) => {
      expect(
        verifyR7AssertionSuccessor(root).record.framework_successor.linked_sources,
      ).not.toContain(cr2QualificationSources[1][0]);
      fs.appendFileSync(path.join(root, cr2QualificationSources[1][0]), '\n');
      expect(() => verifyS5AssertionSuccessor(root)).toThrow('source identity');
    });
  });
  test('keeps immutable V1 identities separate from current execution ownership', () => {
    expect(verifyAssertionDispositions(process.cwd())).toBe(116);
    expect(verifyAssertionDispositions(process.cwd(), load('native-assertions.json'))).toBe(152);
    expect(verifyS5AssertionSuccessor(process.cwd())).toEqual(expected);
    expect(protocol.retired_consumers).toHaveLength(31);
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
    'build-diagnostic-helper',
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
      if (mutation === 'build-diagnostic-helper')
        fs.appendFileSync(path.join(root, cr2BuildDiagnosticSources[0][0]), '\n');
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
      fs.mkdirSync(path.dirname(path.join(root, source)), { recursive: true });
      fs.writeFileSync(
        path.join(root, source),
        `[Fact] public void ${method}() { Assert.True(true); }`,
      );
      expect(() => verifyS5AssertionSuccessor(root)).toThrow();
    });
  });
  test('binds real R7 bodies and keeps native counts historical', () => {
    expect(r7.owner_issue).toBe(403);
    expect(r7.sdk_context_import_qualified).toBe(false);
    expect(r7.source_identity_is_execution_qualification).toBe(false);
    expect(r7.current_native_sources).toHaveLength(7);
    expect(
      r7.current_native_sources.flatMap((row: { methods: string[] }) => row.methods),
    ).toHaveLength(r7.current_native_methods);
    const names = r7.current_native_sources.flatMap((row: { methods: string[] }) => row.methods);
    expect(names).toContain('AuthoredNativeFailureBoundsCannotMasqueradeAsCompletedEvaluation');
    expect(names).not.toContain('ProductionExhaustionReplyAdmits512AndRejectsAdjacentForgery');
    expect(
      r7.original_methods.some(
        (row: { original: { method: string }; current: string | null }) =>
          row.original.method === 'InvalidTrustedBudgetCannotMaterialize' &&
          row.current?.includes('/Agent/Core/AgentTrustedBudgetTests.cs:'),
      ),
    ).toBe(true);
  });

  test.each([
    'missing-current',
    'noop-current',
    'retired-resurrection',
    'catalog-variant',
    'catalog-assertion',
    'catalog-sdk',
    'historical-case',
  ])('rejects R7 closure drift %s', (mutation) => {
    withClosure((root) => {
      const current = path.join(
        root,
        'runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Tools/CurrentPathAuthorityTests.cs',
      );
      if (mutation === 'missing-current') fs.unlinkSync(current);
      if (mutation === 'noop-current')
        fs.writeFileSync(
          current,
          '[Theory] public void AuthoredHistoricalPathCannotGrantCurrentSnapshotAuthority() { Assert.True(true); }',
        );
      if (mutation === 'retired-resurrection') {
        const retired = path.join(
          root,
          'runtime/src/AgenticPrReview.Runtime/Agent/Loop/AgentLoop.cs',
        );
        fs.mkdirSync(path.dirname(retired), { recursive: true });
        fs.writeFileSync(retired, 'internal sealed class AgentLoop {}');
      }
      if (mutation.startsWith('catalog-')) {
        const value = load('r7-assertion-successor.json');
        if (mutation === 'catalog-variant') value.original_methods[0].original.attributes.pop();
        if (mutation === 'catalog-assertion')
          value.original_methods
            .find(
              (row: { assertion_dispositions: unknown[] }) => row.assertion_dispositions.length > 0,
            )
            .assertion_dispositions.pop();
        if (mutation === 'catalog-sdk') value.sdk_context_import_qualified = true;
        fs.writeFileSync(
          path.join(root, directory, 'r7-assertion-successor.json'),
          JSON.stringify(value),
        );
      }
      if (mutation === 'historical-case')
        fs.appendFileSync(
          path.join(root, 'runtime/tests/fixtures/agent/agent-loop/generic-negative-cases.txt'),
          'invented-case\\n',
        );
      expect(() => verifyS5AssertionSuccessor(root)).toThrow();
    });
  });
});
