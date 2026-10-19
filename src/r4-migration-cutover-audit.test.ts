import { execFileSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { describe, expect, test } from 'vitest';
import { residualReferenceRules } from './residual-reference-allowlist.js';

const root = process.cwd();

function trackedFiles(...pathspecs: string[]) {
  return execFileSync(
    'git',
    ['ls-files', '--cached', '--others', '--exclude-standard', '-z', '--', ...pathspecs],
    {
      cwd: root,
      encoding: 'utf8',
    },
  )
    .split('\0')
    .filter((relative) => relative !== '' && existsSync(path.join(root, relative)))
    .sort();
}

async function text(relative: string) {
  return readFile(path.join(root, relative), 'utf8');
}

function sourceClass(relative: string) {
  if (/^src\/action-wrapper\/.*\.test\.ts$/u.test(relative)) return 'wrapper-test';
  if (/^src\/action-wrapper\/.*\.ts$/u.test(relative)) return 'thin-wrapper-or-bridge';
  if (relative === 'src/artifact-provenance-vectors.ts') return 's2-negative-evidence';
  if (relative === 'src/residual-reference-allowlist.ts') return 'residual-inventory';
  if (/^src\/.*\.test\.ts$/u.test(relative)) return 'conformance-test';
  return undefined;
}

describe('R4 W13 closed migration inventory', () => {
  test('retires production native packaging while retaining named historical evidence', async () => {
    const policy = JSON.parse(await text('scripts/release/build-payload.inputs.json'));
    const dependencies = [...policy.dependencies, ...policy.managed.additionalDependencies];
    expect(dependencies).toHaveLength(9);
    expect(
      dependencies.some(({ id }: { id: string }) =>
        /ilcompiler|illink|runtime\.nativeaot|app\.runtime/u.test(id),
      ),
    ).toBe(false);
    for (const removed of [
      'scripts/release/build-payload.nuget.config',
      'runtime/src/AgenticPrReview.Runtime/packages.release.lock.json',
      'tests/distribution/verify-package.mjs',
      'tests/distribution/verify-generated-action.mjs',
    ])
      expect(existsSync(path.join(root, removed))).toBe(false);
    const current = (
      await Promise.all([
        text('scripts/release/build-payload.mjs'),
        text('scripts/release/candidate.mjs'),
        text('scripts/release/prepare-candidate.mjs'),
        text('src/action-wrapper/index.ts'),
      ])
    ).join('\n');
    expect(current).not.toMatch(
      /inspectHistoricalNative|materializeHistoricalNative|encodeHistoricalNative|resolveReleasePayload/u,
    );
    const dispositions = await text('docs/20_architecture/distribution.md');
    expect(dispositions).toContain('CR1-D6 retirement and retained evidence');
    expect(dispositions).toContain('FD3');
    expect(dispositions).toContain('FD4');
    expect(dispositions).toContain('trusted-proof-payload');
    expect(dispositions).toContain('verify-managed-candidate-archive.mjs');
  });

  test('classifies every surviving TypeScript or JavaScript source', () => {
    const sources = trackedFiles('src/*.ts', 'src/*.js', 'src/**/*.ts', 'src/**/*.js');
    expect(
      sources.map((relative) => [relative, sourceClass(relative)]).filter(([, owner]) => !owner),
    ).toEqual([]);
    expect(new Set(sources.map(sourceClass))).toEqual(
      new Set([
        'wrapper-test',
        'thin-wrapper-or-bridge',
        's2-negative-evidence',
        'residual-inventory',
        'conformance-test',
      ]),
    );
  });

  test('pins package scripts, workflow files, schema files, and fixture roots', async () => {
    const packageDocument = JSON.parse(await text('package.json')) as {
      scripts: Record<string, string>;
    };
    expect(packageDocument.scripts).toEqual({
      'build:action': 'node scripts/build-action.mjs',
      check: 'npm run format:check && npm run typecheck && npm test',
      'dist:check': 'node scripts/check-action-distribution.mjs',
      'format:check': 'prettier --check .',
      format: 'prettier --write .',
      'r3:live:policy': 'node scripts/check-r3-live-proof.mjs',
      'r4:trusted-proof:fixture-admission':
        'node scripts/check-r4-trusted-proof-fixture-admission.mjs',
      'runtime:verify': 'bash runtime/scripts/verify-runtime.sh all',
      'runtime:integration': 'node scripts/run-runtime-integration.mjs',
      typecheck: 'tsc --noEmit',
      test: 'vitest run',
      'test:native': 'node scripts/run-native-tests.mjs',
      'test:native:node': 'node scripts/run-native-tests.mjs node',
      'test:native:runtime': 'node scripts/run-native-tests.mjs runtime',
    });
    expect(trackedFiles('scripts/*')).toEqual([
      'scripts/assemble-r4-trusted-proof-evidence.mjs',
      'scripts/build-action.mjs',
      'scripts/check-action-distribution.mjs',
      'scripts/check-r3-live-proof.mjs',
      'scripts/check-r3-live-proof.test.ts',
      'scripts/check-r4-e2p-managed-architecture.mjs',
      'scripts/check-r4-e2p-managed-receipt-v2.mjs',
      'scripts/check-r4-e2p-preflight.mjs',
      'scripts/check-r4-e2p-preflight.test.ts',
      'scripts/check-r4-e2p-receipt-v2.mjs',
      'scripts/check-r4-e2p-receipt-v2.test.ts',
      'scripts/check-r4-e2p-receipt.mjs',
      'scripts/check-r4-trusted-proof-fixture-admission.mjs',
      'scripts/check-r4-trusted-proof-fixture-admission.test.ts',
      'scripts/check-r4-trusted-proof-fixture-refresh.mjs',
      'scripts/check-r4-trusted-proof-fixture-refresh.test.ts',
      'scripts/check-r4-trusted-proof.mjs',
      'scripts/check-r4-trusted-proof.test.ts',
      'scripts/compose-r4-e2-receipt.mjs',
      'scripts/compose-r4-e2-receipt.test.ts',
      'scripts/compose-r4-e2p-receipt-v2.mjs',
      'scripts/compose-r4-e2p-receipt.mjs',
      'scripts/compose-r4-e2p-receipt.test.ts',
      'scripts/correlation-observation.test.ts',
      'scripts/execute-r4-trusted-proof-enrollment.mjs',
      'scripts/execute-r4-trusted-proof-enrollment.test.ts',
      'scripts/generate-r4-trusted-proof-cleanup-plan.mjs',
      'scripts/linux-toolchain.test.ts',
      'scripts/materialize-r4-enrollment-observation.mjs',
      'scripts/materialize-r4-enrollment-observation.test.ts',
      'scripts/probe-r4-e2p-unauthorized-no-launch.mjs',
      'scripts/probe-r4-e2p-unauthorized-no-launch.test.ts',
      'scripts/project-r4-e2p-diagnostics.mjs',
      'scripts/project-r4-e2p-diagnostics.test.ts',
      'scripts/project-r4-trusted-proof-evidence.mjs',
      'scripts/project-r4-trusted-proof-evidence.test.ts',
      'scripts/r4-trusted-proof-contract.mjs',
      'scripts/r4-trusted-proof-enrollment-schedule.mjs',
      'scripts/r4-trusted-proof-enrollment-schedule.test.ts',
      'scripts/release/build-payload.format.d.mts',
      'scripts/release/build-payload.format.mjs',
      'scripts/release/build-payload.inputs.json',
      'scripts/release/build-payload.licenses.txt',
      'scripts/release/build-payload.managed.nuget.config',
      'scripts/release/build-payload.mjs',
      'scripts/release/candidate.mjs',
      'scripts/release/prepare-candidate.mjs',
      'scripts/release/solus-package-contract.d.mts',
      'scripts/release/solus-package-contract.mjs',
      'scripts/release/solus-package-proof.mjs',
      'scripts/run-clean-source-proof.mjs',
      'scripts/run-clean-source-proof.test.ts',
      'scripts/run-native-tests.mjs',
      'scripts/run-runtime-integration.mjs',
      'scripts/synthetic-bridge-lifetime.test.ts',
      'scripts/verify-runtime-ci-timing.mjs',
      'scripts/verify-runtime-ci-workflow.mjs',
    ]);
    expect(trackedFiles('.github/workflows/*')).toEqual([
      '.github/workflows/ci.yml',
      '.github/workflows/r3-live-proof.yml',
      '.github/workflows/r4-trusted-proof.yml',
      '.github/workflows/release.yml',
      '.github/workflows/runtime-ci.yml',
      '.github/workflows/solus-agent-package-proof.yml',
    ]);
    expect(trackedFiles('protocol/schemas/*')).toEqual([
      'protocol/schemas/provider-session-ledger.v1.json',
      'protocol/schemas/review-input.v1.json',
      'protocol/schemas/review-result.v1.json',
      'protocol/schemas/review-trace.v1.json',
    ]);
    expect([
      ...new Set(trackedFiles('protocol/fixtures/**').map((relative) => relative.split('/')[2])),
    ]).toEqual(['prefix-contract', 'v1']);
  });

  test('keeps only permanent residual ownership and the exact S2 evidence unit', () => {
    expect(
      residualReferenceRules.filter(({ lifecycleClass }) =>
        ['protocol-migration', 'state-migration', 'credential-canary'].includes(lifecycleClass),
      ),
    ).toEqual([]);
    expect(
      residualReferenceRules
        .filter(({ id }) => ['RR-001', 'RR-002', 'RR-009', 'RR-031'].includes(id))
        .map(({ id, lifecycleClass }) => [id, lifecycleClass]),
    ).toEqual([
      ['RR-001', 'conformance'],
      ['RR-002', 'conformance'],
      ['RR-009', 'conformance'],
      ['RR-031', 'conformance'],
    ]);
    expect(trackedFiles('src/artifact-provenance-vectors*')).toEqual([
      'src/artifact-provenance-vectors.test.ts',
      'src/artifact-provenance-vectors.ts',
    ]);
  });

  test('pins the governing handoff to the exact active residual inventory and scan boundary', async () => {
    const handoffPaths = trackedFiles('docs/20_architecture/r1-*-removal-handoff.md');
    expect(handoffPaths).toHaveLength(1);
    const handoff = await text(handoffPaths[0] ?? '');
    const section = handoff.split('## Residual-reference ownership\n')[1]?.split('\n## ')[0];
    expect(section).toBeDefined();

    const documentedIds = [
      ...(section ?? '').matchAll(/^\| (RR-\d{3}(?:\.\.\d{3})?)\s*\|/gmu),
    ].flatMap(([, cell]) => {
      const [first, last = first] = cell
        .split('..')
        .map((value) => Number(value.replace('RR-', '')));
      return Array.from(
        { length: last - first + 1 },
        (_, offset) => `RR-${String(first + offset).padStart(3, '0')}`,
      );
    });
    expect(documentedIds).toEqual(residualReferenceRules.map(({ id }) => id));

    expect(section).toContain('git ls-files --cached --others --exclude-standard');
    expect(section).toContain('cached tracked files plus non-ignored untracked files');
    expect(section).toContain('binary or invalid-UTF-8 content');
    expect(section).toContain('.github/actions/agentic-pr-review/dist/index.js');
    expect(section).toContain('src/residual-reference-allowlist.ts');
    expect(section).toContain('src/residual-reference-guard.test.ts');
    expect(section).toContain('src/root-shared-module-retirement.test.ts');
  });

  test('keeps every retired migration family and orphan resolver corpus absent', () => {
    const tracked = trackedFiles();
    const retired = [
      'protocol/fixtures/schema-resolver-conformance/',
      'src/canonical-json',
      'src/comments.',
      'src/inline-comments.',
      'src/ledger-csharp.',
      'src/live-provider/',
      'src/live-runtime-invocation/',
      'src/prefix-contract/',
      'src/protocol/',
      'src/provider-metadata/',
      'src/runtime-invocation/',
      'src/state-acceptance/',
      'src/state-v2/',
      'src/structured.',
      'src/target.',
      'src/types.ts',
      'src/utils.ts',
    ];
    expect(
      retired.flatMap((prefix) => tracked.filter((relative) => relative.startsWith(prefix))),
    ).toEqual([]);
  });

  test('pins the current handoff and rejects superseded ownership claims', async () => {
    const handoff = await text('docs/20_architecture/r4-migration-cutover-handoff.md');
    for (const heading of [
      '## Current implementation boundary',
      '## Contract and fixture inventory',
      '## Residual and historical ownership',
      '## E1 exact-source identity',
      '## Post-merge closure order',
      '## Validation',
    ]) {
      expect(handoff).toContain(heading);
    }

    const currentSurfaces = await Promise.all([
      text('README.md'),
      text('docs/00_project/project-context.md'),
      text('docs/20_architecture/agent-runtime-rebaseline.md'),
      text('docs/20_architecture/distribution.md'),
      text('docs/20_architecture/r4-actionhost-wrapper-plan.md'),
      text('docs/90_roadmap/roadmap-seed.md'),
      text('protocol/schemas/review-input.v1.json'),
      text('protocol/schemas/review-result.v1.json'),
      text('protocol/schemas/review-trace.v1.json'),
    ]).then((documents) => documents.join('\n'));
    for (const staleClaim of [
      'TypeScript host for a future review runtime',
      'back to the TypeScript publisher',
      'TS interfaces are convenience only',
      'prospective R4 product contract',
      'R4 is active only through its docs gate',
      'No implementation issue is published until that docs contract is merged',
      'Do not create R4 implementation or deletion issues until the R4 docs contract merges',
      'Remaining TypeScript canonicalization',
      'src/no-public-action.test.ts',
    ]) {
      expect(currentSurfaces).not.toContain(staleClaim);
    }
  });
});

describe('CR1 S5 current Host proof receipt admission', () => {
  async function oracle() {
    const { pathToFileURL } = await import('node:url');
    return import(
      pathToFileURL(path.join(root, 'scripts/check-r4-e2p-managed-receipt-v2.mjs')).href
    );
  }

  async function fixture(role: 'first' | 'second') {
    const m = await oracle();
    const value: Record<string, any> = {
      kind: 'apr-cr1-s5-managed-host-proof-v1',
      execution_kind: 'framework-single-file',
      production_payload_smoke: 'passed-six-zero-network-cases',
      synthetic_managed_v2_route: 'host-only-current-state-passed',
      two_root_preparation: 'passed',
      native_aot_disposition: 'historical-only',
      action_source_sha: '5b5769753653bb3fd3e68cf8b7bb88a1bd350613',
      payload_build_discriminator: 'r4-w2',
      archive_qualified: false,
      owner_issue: 409,
      host_role: 'host-only-structural-records',
      sdk_context_import_qualified: false,
      host_contract_sha256: m.s5HostContractSha256,
      current_host_observation_domain: 'apr-s5-current-host-typed-work-v1',
      current_host_unit_oracles: 9,
      role,
      source_commit: '1'.repeat(40),
      source_tree: '2'.repeat(40),
      compiled_payload_source_commit: '1'.repeat(40),
      compiled_payload_source_tree: '2'.repeat(40),
      compiled_payload_proof_kind: m.s5PayloadKind,
      wrapper_bundle_sha256: m.sha256('shared-wrapper-fixture'),
      proof_recipe_sha256: m.sha256('shared-recipe-fixture'),
      current_host_unit_results_sha256: m.sha256(`${role}:actual-result-fixture`),
      v2_cases: [...m.v2Cases],
      preparation_cases: [...m.preparationCases],
    };
    for (const key of m.identityFields.filter((key: string) => key.endsWith('_sha256')))
      value[key] = m.sha256(`${role}:${key}:fixture`);
    value.verifier_payload_managed_intermediate_sha256 = value.proof_managed_intermediate_sha256;
    value.verifier_runtime_managed_intermediate_sha256 = value.runtime_managed_intermediate_sha256;
    value.build_pair_sha256 = m.buildPair(value);
    return value;
  }

  test('pins immutable original evidence and the named actual SDK proof owners', async () => {
    const m = await oracle();
    const contract = m.validateS5HostContract(root);
    expect(contract.owner_issue).toBe(409);
    expect(contract.sdk_context_import_qualified).toBe(false);
    expect(contract.actual_sdk_qualification.must_match_final_source).toBe(true);
    expect(contract.actual_sdk_qualification.public_receipt_is_sdk_execution_evidence).toBe(false);
  });

  test('admits exactly two distinct current Host execution fixtures', async () => {
    const m = await oracle();
    const receipts = await Promise.all([fixture('first'), fixture('second')]);
    expect(() => m.validateS5ManagedReceiptPair(receipts)).not.toThrow();
    for (const receipt of receipts) expect(() => m.validateManagedV2Receipt(receipt)).toThrow();
  });

  test.each([
    'owner',
    'contract',
    'role',
    'sdk-overclaim',
    'missing-case',
    'duplicate-case',
    'count',
    'source',
    'compiled-source',
    'old-payload-kind',
    'unknown-field',
    'missing-unit-result',
    'preparation-drop',
    'missing-oracle-domain',
  ])('refuses an independent current receipt mutation: %s', async (mutation) => {
    const m = await oracle();
    const value = await fixture('first');
    switch (mutation) {
      case 'owner':
        value.owner_issue = 408;
        break;
      case 'contract':
        value.host_contract_sha256 = 'a'.repeat(64);
        break;
      case 'role':
        value.host_role = 'sdk-context-import';
        break;
      case 'sdk-overclaim':
        value.sdk_context_import_qualified = true;
        break;
      case 'missing-case':
        value.v2_cases.pop();
        break;
      case 'duplicate-case':
        value.v2_cases[1] = value.v2_cases[0];
        break;
      case 'count':
        value.current_host_unit_oracles = 8;
        break;
      case 'source':
        value.source_commit = 'invalid';
        break;
      case 'compiled-source':
        value.compiled_payload_source_tree = '3'.repeat(40);
        break;
      case 'old-payload-kind':
        value.compiled_payload_proof_kind = 'apr-r4-e2p-trusted-proof-payload-v2';
        break;
      case 'unknown-field':
        value.sdk_qualified = true;
        break;
      case 'missing-unit-result':
        delete value.current_host_unit_results_sha256;
        break;
      case 'preparation-drop':
        value.preparation_cases.pop();
        break;
      case 'missing-oracle-domain':
        delete value.current_host_observation_domain;
        break;
    }
    expect(() => m.validateS5ManagedReceipt(value)).toThrow();
  });

  test.each([
    'missing-second',
    'duplicate-role',
    'relabel-one-execution',
    'cross-source',
    'cross-tree',
    'cross-owner',
    'cross-contract',
    'sdk-overclaim',
    'missing-case',
    'count-swap',
    'same-unit-result',
    'one-old-one-new',
  ])('refuses an independent pair mutation: %s', async (mutation) => {
    const m = await oracle();
    const values = await Promise.all([fixture('first'), fixture('second')]);
    const second = values[1];
    switch (mutation) {
      case 'missing-second':
        values.pop();
        break;
      case 'duplicate-role':
        second.role = 'first';
        break;
      case 'relabel-one-execution':
        values[1] = { ...structuredClone(values[0]), role: 'second' };
        break;
      case 'cross-source':
        second.source_commit = second.compiled_payload_source_commit = '3'.repeat(40);
        break;
      case 'cross-tree':
        second.source_tree = second.compiled_payload_source_tree = '3'.repeat(40);
        break;
      case 'cross-owner':
        second.owner_issue = 408;
        break;
      case 'cross-contract':
        second.host_contract_sha256 = 'a'.repeat(64);
        break;
      case 'sdk-overclaim':
        second.sdk_context_import_qualified = true;
        break;
      case 'missing-case':
        second.v2_cases.pop();
        break;
      case 'count-swap':
        second.current_host_unit_oracles = 6;
        break;
      case 'same-unit-result':
        second.current_host_unit_results_sha256 = values[0].current_host_unit_results_sha256;
        break;
      case 'one-old-one-new':
        second.kind = 'apr-d5-v2-managed-proof-v1';
        break;
    }
    expect(() => m.validateS5ManagedReceiptPair(values)).toThrow();
  });
  test('parses the exact nine unit-result identities and refuses independent result faults', async () => {
    const m = await oracle();
    const escape = (value: string) => value.replaceAll('&', '&amp;').replaceAll('"', '&quot;');
    const rows = m.s5HostUnitCases.map(
      (name: string, index: number) =>
        `<UnitTestResult executionId="00000000-0000-0000-0000-${String(index).padStart(12, '0')}" testName="${escape(name)}" outcome="Passed" />`,
    );
    const encode = (values: string[]) =>
      Buffer.from(
        `<TestRun><Results>${values.join('')}</Results><ResultSummary outcome="Completed"><Counters executed="9" passed="9" failed="0" error="0" timeout="0" aborted="0" /></ResultSummary></TestRun>`,
      );
    expect(m.parseS5UnitOracleResults(encode(rows)).cases).toBe(9);
    for (const values of [
      rows.slice(1),
      [...rows, rows[0]],
      [rows[0].replace('Passed', 'Failed'), ...rows.slice(1)],
      [rows[0].replace('000000000000', 'not-a-guid'), ...rows.slice(1)],
      [rows[0].replace('Transcript', 'UnknownTranscript'), ...rows.slice(1)],
      [rows[0], rows[1].replace('000000000001', '000000000000'), ...rows.slice(2)],
      [rows[0].replace('Passed', '&undefined;'), ...rows.slice(1)],
    ])
      expect(() => m.parseS5UnitOracleResults(encode(values))).toThrow();
    expect(() => m.parseS5UnitOracleResults(Buffer.from('<!DOCTYPE x>' + encode(rows)))).toThrow();
    expect(() => m.parseS5UnitOracleResults(Buffer.from([0xff]))).toThrow();
    expect(() =>
      m.parseS5UnitOracleResults(
        Buffer.from(encode(rows).toString().replace('outcome="Completed"', 'outcome="Failed"')),
      ),
    ).toThrow();
    expect(() =>
      m.parseS5UnitOracleResults(
        Buffer.from(encode(rows).toString().replace('failed="0"', 'failed="1"')),
      ),
    ).toThrow();
    expect(() =>
      m.parseS5UnitOracleResults(
        encode([
          rows[0].replace('outcome="Passed"', 'outcome="Failed" outcome="Passed"'),
          ...rows.slice(1),
        ]),
      ),
    ).toThrow();
  });

  test('the actual aggregate invokes strict pair admission after its per-role checks', async () => {
    const m = await oracle();
    const { mkdtemp, mkdir, writeFile, rm } = await import('node:fs/promises');
    const { tmpdir } = await import('node:os');
    const directory = await mkdtemp(path.join(tmpdir(), 'apr-s5-aggregate-test-'));
    try {
      const workflow = await text('.github/workflows/runtime-ci.yml');
      const anchor = workflow.indexOf(
        'import { validateS5ManagedReceipt as validateManagedV2Receipt',
      );
      expect(anchor).toBeGreaterThan(0);
      const start = workflow.lastIndexOf("          import fs from 'node:fs';", anchor);
      const end = workflow.indexOf('\n          NODE', anchor);
      const code = workflow.slice(start, end).replace(/^ {10}/gmu, '');
      const values = await Promise.all([fixture('first'), fixture('second')]);
      const files = [];
      for (const [index, role] of ['first', 'second'].entries()) {
        const folder = path.join(directory, `r4-e2p-v2-123-1-${role}`);
        await mkdir(folder);
        const file = path.join(folder, `r4-e2p-v2-${role}.receipt`);
        files.push(file);
        values[index].proof_recipe_sha256 = m.recipeDigest(root);
        await writeFile(file, 'APR_D5_V2_MANAGED_PROOF ' + JSON.stringify(values[index]) + '\n');
      }
      const run = () =>
        execFileSync(
          process.execPath,
          ['--input-type=module', '-', directory, '123', '1', '1'.repeat(40), '2'.repeat(40)],
          { cwd: root, input: code, encoding: 'utf8', stdio: 'pipe' },
        );
      expect(run()).toContain('Both bounded current-head V2 receipts');
      await writeFile(
        files[1],
        'APR_D5_V2_MANAGED_PROOF ' + JSON.stringify({ ...values[0], role: 'second' }) + '\n',
      );
      expect(run).toThrow();
    } finally {
      await rm(directory, { recursive: true, force: true });
    }
  });

  test('the actual preparer receipt precheck refuses old or misbound current headers', async () => {
    const { mkdtemp, writeFile, rm } = await import('node:fs/promises');
    const { tmpdir } = await import('node:os');
    const directory = await mkdtemp(path.join(tmpdir(), 'apr-s5-precheck-test-'));
    try {
      const script = await text('runtime/scripts/prepare-r4-trusted-proof-payload-v2-managed.sh');
      const anchor = script.indexOf('receipt_kind=');
      const start = script.indexOf("import fs from 'node:fs';", anchor);
      const end = script.indexOf('\nNODE', start);
      const code = script.slice(start, end);
      const file = path.join(directory, 'receipt.json');
      const valid = await fixture('first');
      const run = () =>
        execFileSync(process.execPath, ['--input-type=module', '-', file], {
          cwd: root,
          input: code,
          encoding: 'utf8',
          stdio: 'pipe',
        });
      await writeFile(file, JSON.stringify(valid));
      expect(run()).toBe('apr-cr1-s5-managed-host-proof-v1');
      for (const [key, value] of [
        ['kind', 'apr-d5-v2-managed-proof-v1'],
        ['owner_issue', 408],
        ['host_contract_sha256', 'a'.repeat(64)],
        ['host_role', 'sdk-import'],
        ['sdk_context_import_qualified', true],
        ['compiled_payload_source_commit', '3'.repeat(40)],
        ['compiled_payload_source_tree', '3'.repeat(40)],
      ]) {
        await writeFile(file, JSON.stringify({ ...valid, [key as string]: value }));
        expect(run).toThrow();
      }
    } finally {
      await rm(directory, { recursive: true, force: true });
    }
  });
  test('the actual proof normalizer rejects SDK overclaim, old history fields and unexecuted oracles', async () => {
    const m = await oracle();
    const { mkdtemp, writeFile, rm } = await import('node:fs/promises');
    const { tmpdir } = await import('node:os');
    const directory = await mkdtemp(path.join(tmpdir(), 'apr-s5-normalizer-test-'));
    try {
      const script = await text('runtime/scripts/verify-r4-trusted-proof-payload-v2.sh');
      const anchor = script.indexOf(
        'const [inputPath, outputPath, expectedSourceCommit, expectedSourceTree, unitResultsPath]',
      );
      expect(anchor).toBeGreaterThan(0);
      const start = script.lastIndexOf("import fs from 'node:fs';", anchor);
      const end = script.indexOf('\nNODE', anchor);
      const code = script.slice(start, end);
      const input = path.join(directory, 'input.json');
      const output = path.join(directory, 'output.json');
      const results = path.join(directory, 'unit-results.trx');
      const rows = m.s5HostUnitCases.map(
        (name: string, index: number) =>
          `<UnitTestResult executionId="00000000-0000-0000-0000-${String(index).padStart(12, '0')}" testName="${name.replaceAll('"', '&quot;')}" outcome="Passed" />`,
      );
      const xml = `<TestRun><Results>${rows.join('')}</Results><ResultSummary outcome="Completed"><Counters executed="9" passed="9" failed="0" error="0" timeout="0" aborted="0" /></ResultSummary></TestRun>`;
      await writeFile(results, xml);
      const valid: Record<string, any> = {
        passed: true,
        verifier_executable_sha256: 'a'.repeat(64),
        compiled_payload_proof_kind: m.s5PayloadKind,
        compiled_payload_source_commit: '1'.repeat(40),
        compiled_payload_source_tree: '2'.repeat(40),
        launch_workflow_sha: '1'.repeat(40),
        launch_action_source_sha: '1'.repeat(40),
        fixture_base_sha: '1'.repeat(40),
        trusted_authority_exact: true,
        trusted_proof_request_budget_satisfied: true,
        shared_primary_bucket_start: 1000,
        shared_primary_bucket_end: 500,
        shared_primary_bucket_exact: true,
        cases: m.v2Cases.map((Name: string, index: number) => ({
          Name,
          Passed: true,
          HostPid: index === 4 ? 0 : 100 + index,
          CurrentHostTraceQualified: true,
          SdkContextImportQualified: false,
          SyntheticCalls: [6, 3, 6, 0, 0][index],
        })),
      };
      const run = () =>
        execFileSync(
          process.execPath,
          ['--input-type=module', '-', input, output, '1'.repeat(40), '2'.repeat(40), results],
          { cwd: root, input: code, encoding: 'utf8', stdio: 'pipe' },
        );
      await writeFile(input, JSON.stringify(valid));
      expect(run).not.toThrow();
      const normalized = JSON.parse(await readFile(output, 'utf8'));
      expect(normalized.current_host_unit_results_sha256).toBe(m.sha256(Buffer.from(xml)));
      for (const mutation of [
        'sdk-overclaim',
        'old-history-field',
        'unqualified-trace',
        'wrong-call-count',
        'wrong-case',
        'old-payload',
        'unauthorized-host',
        'wrong-workflow-source',
      ]) {
        const value = structuredClone(valid);
        switch (mutation) {
          case 'sdk-overclaim':
            value.cases[0].SdkContextImportQualified = true;
            break;
          case 'old-history-field':
            value.cases[1].ContinuationObserved = true;
            break;
          case 'unqualified-trace':
            value.cases[0].CurrentHostTraceQualified = false;
            break;
          case 'wrong-call-count':
            value.cases[1].SyntheticCalls = 6;
            break;
          case 'wrong-case':
            value.cases[1].Name = value.cases[0].Name;
            break;
          case 'old-payload':
            value.compiled_payload_proof_kind = 'apr-r4-e2p-trusted-proof-payload-v2';
            break;
          case 'unauthorized-host':
            value.cases[4].HostPid = 99;
            break;
          case 'wrong-workflow-source':
            value.launch_workflow_sha = '3'.repeat(40);
            break;
        }
        await writeFile(input, JSON.stringify(value));
        expect(run, mutation).toThrow();
      }
      await writeFile(input, JSON.stringify(valid));
      await writeFile(results, xml.replace(rows[0], ''));
      expect(run).toThrow();
    } finally {
      await rm(directory, { recursive: true, force: true });
    }
  });
});

describe('S5 finite current Framework source inventory', () => {
  const inventoryPath =
    'runtime/tests/fixtures/action-host/cr1-s5-host/framework-source-inventory.json';
  const verifierPath = '../runtime/tests/fixtures/action-host/cr1-s5-host/source-inventory.mjs';
  test('independently rebuilds current sources while pinning all original golden bytes', async () => {
    const { verifyS5FrameworkSourceInventory } = await import(verifierPath);
    expect(verifyS5FrameworkSourceInventory(root)).toMatchObject({
      sourceCount: 21,
      sdkContextImportQualified: false,
    });
  });
  test.each([
    'owner',
    'kind',
    'source-commit',
    'source-tree',
    'sdk',
    'role',
    'historical-current',
    'golden-digest',
    'other-golden',
    'missing',
    'extra',
    'duplicate',
    'wrong-source',
    'changed-original',
    'changed-current',
    'unknown-change',
    'retirement',
    'digest',
    'order',
    'extra-authority',
    'missing-link',
    'extra-link',
    'wrong-link',
    'link-digest',
    'link-order',
  ])('rejects %s inventory drift', async (mutation) => {
    const { verifyS5FrameworkSourceInventory } = await import(verifierPath);
    const inventory = JSON.parse(await readFile(path.join(root, inventoryPath), 'utf8'));
    const first = inventory.sources[0];
    const changed = inventory.sources.find(
      (row: { disposition: string }) => row.disposition !== 'unchanged',
    );
    if (mutation === 'owner') inventory.owner_issue = 403;
    if (mutation === 'kind') inventory.kind = 'old';
    if (mutation === 'source-commit') inventory.retirement_source_commit = '0'.repeat(40);
    if (mutation === 'source-tree') inventory.retirement_source_tree = '0'.repeat(40);
    if (mutation === 'sdk') inventory.sdk_context_import_qualified = true;
    if (mutation === 'role') inventory.current_role = 'sdk-import-qualified';
    if (mutation === 'historical-current')
      inventory.current_source_inventory_digest =
        inventory.historical_golden.source_inventory_digest;
    if (mutation === 'golden-digest') inventory.historical_golden.sha256 = '0'.repeat(64);
    if (mutation === 'other-golden')
      inventory.historical_golden.path =
        'runtime/tests/fixtures/action-host/framework/expected-evidence.json.golden';
    if (mutation === 'missing') inventory.sources.pop();
    if (mutation === 'extra') inventory.sources.push({ ...first, path: first.path + '.unknown' });
    if (mutation === 'duplicate') inventory.sources[1] = structuredClone(first);
    if (mutation === 'wrong-source') first.path = 'runtime/tests/unclassified.cs';
    if (mutation === 'changed-original') first.original_sha256 = '0'.repeat(64);
    if (mutation === 'changed-current') changed.current_sha256 = '0'.repeat(64);
    if (mutation === 'unknown-change') first.current_sha256 = '0'.repeat(64);
    if (mutation === 'retirement') changed.disposition = 'retired';
    if (mutation === 'digest') inventory.current_source_inventory_digest = '0'.repeat(64);
    if (mutation === 'order') inventory.sources.reverse();
    if (mutation === 'extra-authority') inventory.allow_unknown_sources = true;
    if (mutation === 'missing-link') inventory.linked_sources.pop();
    if (mutation === 'extra-link') inventory.linked_sources.push(inventory.linked_sources[0]);
    if (mutation === 'wrong-link') inventory.linked_sources[0].path = '../unclassified.cs';
    if (mutation === 'link-digest') inventory.linked_sources[0].sha256 = '0'.repeat(64);
    if (mutation === 'link-order') inventory.linked_sources.reverse();
    expect(() => verifyS5FrameworkSourceInventory(root, inventory)).toThrow();
  });
  test.each(['missing', 'extra', 'changed', 'golden-change', 'symlink'])(
    'rejects actual %s source changes',
    async (mutation) => {
      const { verifyS5FrameworkSourceInventory } = await import(verifierPath);
      const fs = await import('node:fs/promises');
      const { tmpdir } = await import('node:os');
      const inventory = JSON.parse(await readFile(path.join(root, inventoryPath), 'utf8'));
      const temporary = await fs.mkdtemp(path.join(tmpdir(), 'apr-s5-framework-inventory-'));
      try {
        for (const relative of [
          inventory.historical_golden.path,
          ...inventory.sources.map((row: { path: string }) => row.path),
          ...inventory.linked_sources.map((row: { path: string }) => row.path),
        ]) {
          await fs.mkdir(path.dirname(path.join(temporary, relative)), { recursive: true });
          await fs.copyFile(path.join(root, relative), path.join(temporary, relative));
        }
        const target = path.join(temporary, inventory.sources[0].path);
        if (mutation === 'missing') await fs.unlink(target);
        if (mutation === 'extra')
          await fs.writeFile(path.join(path.dirname(target), 'unclassified.cs'), 'unclassified');
        if (mutation === 'changed') await fs.appendFile(target, '\n');
        if (mutation === 'golden-change')
          await fs.appendFile(path.join(temporary, inventory.historical_golden.path), '\n');
        if (mutation === 'symlink') {
          await fs.unlink(target);
          await fs.symlink(path.join(root, inventory.sources[0].path), target);
        }
        expect(() => verifyS5FrameworkSourceInventory(temporary, inventory)).toThrow();
      } finally {
        await fs.rm(temporary, { recursive: true, force: true });
      }
    },
  );
});

describe('S5 finite full Framework golden selector', () => {
  const selectorPath = '../runtime/tests/fixtures/action-host/cr1-s5-host/framework-golden.mjs';
  const contractPath = 'runtime/tests/fixtures/action-host/cr1-s5-host/framework-expectations.json';
  test('preserves all untouched original invariants and selects an explicitly separate Host domain', async () => {
    const { createS5FrameworkGolden, verifyS5FrameworkEvidence } = await import(selectorPath);
    const original = JSON.parse(
      await text('runtime/tests/fixtures/action-host/cr1-r8-typed/expected-evidence.json.golden'),
    );
    const expected = createS5FrameworkGolden(root);
    expect(verifyS5FrameworkEvidence(root, expected)).toEqual({
      cases: 32,
      sdkContextImportQualified: false,
    });
    expect(expected.schema).toBe('apr.action-host.cr1-s5-current-framework-evidence.v1');
    expect(expected.observation_domain).toBe('apr-s5-current-framework-host-work-v1');
    expect(expected.sdk_context_import_qualified).toBe(false);
    expect(expected.scenarios.map((item: { name: string }) => item.name)).toEqual(
      original.scenarios.map((item: { name: string }) => item.name),
    );
    for (let index = 0; index < original.scenarios.length; index++) {
      const before = original.scenarios[index];
      const after = expected.scenarios[index];
      for (const key of [
        'ExitCode',
        'ToolSequence',
        'SyntheticCalls',
        'StickyMutations',
        'InlineMutations',
        'CanarySafe',
      ])
        expect(after[key], before.name + '.' + key).toEqual(before[key]);
      if (before.name !== 'dispatch-cross-head-conflict') expect(after.status).toBe(before.status);
    }
    expect(expected.scenarios[2]).toMatchObject({
      status: 'authorization_failed',
      ToolSequence: 0,
      StickyMutations: 0,
      InlineMutations: 0,
      SyntheticCalls: 0,
    });
    for (const key of [
      'replacement_record_digest',
      'base_inventory_digest',
      'canary_table_digest',
      'process',
      'exact_child_environment',
      'outputs_match_summary',
      'canary_oracle_passed',
      'canary_negative_injection_count',
      'artifact_metadata_route_probe_count',
    ])
      expect(expected[key], key).toEqual(original[key]);
    expect(expected.continuation.sticky_lineage_digest).toBe(
      original.continuation.sticky_lineage_digest,
    );
    expect(expected.official_bridge.delete).toBe(original.official_bridge.delete);
    expect(() => verifyS5FrameworkEvidence(root, original)).toThrow();
  });
  test.each([
    'owner',
    'kind',
    'role',
    'sdk',
    'source',
    'historical-pin',
    'missing-counter',
    'duplicate-counter',
    'wrong-counter',
    'unsupported-counter',
    'missing-identity',
    'wrong-identity',
    'reason',
    'extra-authority',
  ])('rejects contract %s drift', async (mutation) => {
    const { createS5FrameworkGolden } = await import(selectorPath);
    const contract = JSON.parse(await text(contractPath));
    if (mutation === 'owner') contract.owner_issue = 403;
    if (mutation === 'kind') contract.kind = 'historical';
    if (mutation === 'role') contract.host_role = 'sdk';
    if (mutation === 'sdk') contract.sdk_context_import_qualified = true;
    if (mutation === 'source') contract.source_inventory_digest = '0'.repeat(64);
    if (mutation === 'historical-pin') contract.historical_golden_sha256 = '0'.repeat(64);
    if (mutation === 'missing-counter') contract.counter_dispositions.pop();
    if (mutation === 'duplicate-counter')
      contract.counter_dispositions[1] = contract.counter_dispositions[0];
    if (mutation === 'wrong-counter') contract.counter_dispositions[0].current++;
    if (mutation === 'unsupported-counter')
      contract.counter_dispositions[0].path = 'scenarios.dispatch-bootstrap.StickyMutations';
    if (mutation === 'missing-identity') contract.identity_dispositions.pop();
    if (mutation === 'wrong-identity') contract.identity_dispositions[0].current = '0'.repeat(64);
    if (mutation === 'reason') contract.counter_dispositions[0].reason = 'no longer needed';
    if (mutation === 'extra-authority') contract.allow_unknown_scenarios = true;
    expect(() => createS5FrameworkGolden(root, contract)).toThrow();
  });
  test.each([
    'sdk',
    'owner',
    'role',
    'source',
    'missing-case',
    'duplicate-case',
    'case-order',
    'cross-head-code',
    'cross-head-provider',
    'publication',
    'tool-count',
    'call-count',
    'github-count',
    'state-count',
    'canary-count',
    'bridge-count',
    'carrier-overclaim',
    'old-domain',
    'old-field',
    'context-import',
    'missing-boundary',
    'source-substitution',
    'wrong-identity',
    'mutation-scope',
  ])('rejects actual evidence %s drift', async (mutation) => {
    const { createS5FrameworkGolden, verifyS5FrameworkEvidence } = await import(selectorPath);
    // Data admission only. This fixture is never a receipt for Host execution.
    const value = createS5FrameworkGolden(root);
    if (mutation === 'sdk') value.sdk_context_import_qualified = true;
    if (mutation === 'owner') value.owner_issue = 403;
    if (mutation === 'role') value.host_role = 'sdk-context';
    if (mutation === 'source') value.source_inventory_digest = '0'.repeat(64);
    if (mutation === 'missing-case') value.scenarios.pop();
    if (mutation === 'duplicate-case') value.scenarios[1] = value.scenarios[0];
    if (mutation === 'case-order') value.scenarios.reverse();
    if (mutation === 'cross-head-code') value.scenarios[2].status = 'state_conflict';
    if (mutation === 'cross-head-provider') value.scenarios[2].SyntheticCalls = 1;
    if (mutation === 'publication') value.scenarios[2].StickyMutations = 1;
    if (mutation === 'tool-count') value.scenarios[0].ToolSequence--;
    if (mutation === 'call-count') value.scenarios[0].SyntheticCalls--;
    if (mutation === 'github-count') value.scenarios[1].GitHubRequests--;
    if (mutation === 'state-count') value.scenarios[1].StateOperations--;
    if (mutation === 'canary-count') value.canary_negative_injection_count--;
    if (mutation === 'bridge-count') value.official_bridge.rest--;
    if (mutation === 'carrier-overclaim')
      value.continuation.prior_marker_first_request_exact = true;
    if (mutation === 'old-domain')
      value.observation_domain = 'apr-historical-authored-typed-chat-v1';
    if (mutation === 'old-field') value.scenarios[0].ContinuationObserved = true;
    if (mutation === 'context-import') value.continuation.sdk_context_import_qualified = true;
    if (mutation === 'missing-boundary') delete value.scenarios[2].CurrentWorkBoundarySatisfied;
    if (mutation === 'source-substitution') value.historical_golden_sha256 = '0'.repeat(64);
    if (mutation === 'wrong-identity') value.continuation.state_identity_digest = '0'.repeat(64);
    if (mutation === 'mutation-scope') value.official_bridge.delete++;
    expect(() => verifyS5FrameworkEvidence(root, value)).toThrow();
  });
  test('the actual wrapper chooses the new selector only in its framework branch', async () => {
    const script = await text('runtime/scripts/verify-action-host.sh');
    expect(script).toContain(
      'if [[ "$mode" == framework ]]; then\n  golden="$temporary_root/s5-current-framework-evidence.json"',
    );
    expect(script).toContain(
      '"$repo_root/runtime/tests/fixtures/action-host/cr1-s5-host/framework-golden.mjs"',
    );
    expect(script).toContain('--golden "$golden" --canaries "$canaries" --node "$node_path"');
    expect(script).toContain(
      '-p:PublishAot=true -p:IsAotCompatible=true -p:VerifyReferenceAotCompatibility=true',
    );
  });
});
