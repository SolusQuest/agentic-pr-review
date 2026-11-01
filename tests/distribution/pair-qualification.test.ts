import { execFileSync } from 'node:child_process';
import {
  copyFileSync,
  existsSync,
  mkdirSync,
  readFileSync,
  rmSync,
  statSync,
  writeFileSync,
} from 'node:fs';
import { delimiter, join } from 'node:path';
import { Readable } from 'node:stream';
import { afterEach, beforeEach, describe, expect, test, vi } from 'vitest';
import { canonicalJson, sha256 } from '../../scripts/release/build-payload.format.mjs';
import {
  finalizeCandidate,
  verifyQualifiedPair,
} from '../../scripts/release/finalize-candidate.mjs';
import {
  inspectQualificationArchive,
  inspectQualificationContent,
  parseExpectedChecks,
  CHECK_MANIFEST_PATH,
} from '../../scripts/release/finalized-pair.format.mjs';
import {
  qualificationEvidence,
  writeQualificationEvidence,
} from '../../scripts/release/qualification-evidence.mjs';
import {
  admitQualifier,
  admitQualificationRequest,
  matchRequest,
  qualifyPair,
} from '../../scripts/release/qualify-pair.mjs';
import { finalizationFixture } from '../release/finalization.fixtures.mjs';
import {
  originalCases,
  qualificationManifest,
  ORIGINAL_PAIR_CASE_COUNT,
} from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/qualification-cases.mjs';
import {
  originalPairObservations,
  prepareOriginalPair,
} from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/qualify-original-pair.mjs';
import { createGitHubFixture } from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/github-fixture.mjs';
import { createProviderFixture } from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/provider-fixture.mjs';
import { providerKey } from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/official-platform.mjs';
import {
  caseTimingLine,
  extractCaseTimings,
} from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/case-timing.mjs';
import { residualReferenceRules } from '../../src/residual-reference-allowlist.js';

// Fake GitHub/provenance and an intercepted archive observer test the producer
// boundary. No synthetic result here is original-candidate execution evidence.
const harness = vi.hoisted(() => ({
  execute: vi.fn(),
  runner: vi.fn(),
  supported: vi.fn(),
  support: vi.fn(),
}));
vi.mock(
  '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/qualify-original-pair.mjs',
  async (original) => ({
    ...(await original<object>()),
    executeOriginalPair: harness.execute,
    runnerEvidence: harness.runner,
    supportsRuntime: harness.supported,
  }),
);
vi.mock(
  '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/cutover-proof.mjs',
  async (original) => ({
    ...(await original<object>()),
    prepareCutoverSupport: harness.support,
  }),
);
const roots: string[] = [];
beforeEach(() => {
  harness.execute.mockReset();
  harness.runner.mockReset();
  harness.supported.mockReset();
  harness.support.mockReset();
  harness.runner.mockReturnValue({
    unchanged: true,
    assetCount: 7,
    actionCommit: '5'.repeat(40),
    runs: ['39972608852'],
  });
  harness.supported.mockReturnValue(true);
});
afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  for (const root of roots.splice(0)) rmSync(root, { force: true, recursive: true });
});

const expectedBytes = Buffer.from(canonicalJson(qualificationManifest));
async function fixture() {
  const f = finalizationFixture();
  roots.push(f.root);
  vi.stubGlobal('fetch', f.fetch);
  vi.stubEnv('PATH', f.bin + delimiter + process.env.PATH);
  vi.spyOn(Date, 'now').mockReturnValue(f.now);
  const finalized = await finalizeCandidate(f.input, {});
  const pairPath = join(f.input.output, 'finalized-pair.json');
  const pairBytes = readFileSync(pairPath),
    pair = JSON.parse(pairBytes.toString());
  f.write(CHECK_MANIFEST_PATH, expectedBytes);
  const q = f.commit('Synthetic canonical V2 manifest, no operational execution');
  f.main.object.sha = q;
  f.qRun.head_sha = q;
  f.qProducer.workflowCommit = q;
  f.qArtifact.workflow_run.head_sha = q;
  const env = {
    GITHUB_EVENT_NAME: 'workflow_dispatch',
    GITHUB_REPOSITORY: pair.repository,
    GITHUB_REPOSITORY_ID: '1263978368',
    GITHUB_REF: 'refs/heads/main',
    GITHUB_WORKFLOW_REF: pair.repository + '/.github/workflows/runtime-ci.yml@refs/heads/main',
    GITHUB_WORKFLOW_SHA: q,
    GITHUB_SHA: q,
    GITHUB_RUN_ID: '301',
    GITHUB_RUN_ATTEMPT: '1',
    RUNNER_ENVIRONMENT: 'github-hosted',
    RUNNER_OS: 'Linux',
    RUNNER_ARCH: 'X64',
    QUALIFIER_COMMIT: q,
    QUALIFICATION_PAIR: pairBytes.toString(),
    QUALIFICATION_PAIR_SHA256: finalized.pairSha256,
    QUALIFICATION_REQUEST: canonicalJson(f.request),
    GITHUB_TOKEN: 'synthetic-read-only',
  };
  const inputs = join(f.root, 'qualifier-inputs'),
    artifacts = join(f.root, 'originals'),
    output = join(f.root, 'evidence-output');
  mkdirSync(inputs);
  writeFileSync(join(inputs, 'pair.json'), pairBytes);
  writeFileSync(join(inputs, 'request.json'), canonicalJson(f.request));
  for (const role of ['payload', 'metadata', 'provenance'])
    mkdirSync(join(artifacts, role), { recursive: true });
  copyFileSync(f.paths.archivePath, join(artifacts, 'payload', pair.payload.archiveName));
  writeFileSync(join(artifacts, 'payload/receipt.json'), canonicalJson(f.p.receipt));
  copyFileSync(f.paths.candidatePath, join(artifacts, 'metadata/candidate.json'));
  copyFileSync(f.paths.mapPath, join(artifacts, 'metadata/proposed-action-map.json'));
  copyFileSync(f.paths.bundlePath, join(artifacts, 'provenance/bundle.json'));
  const facts = {
    qualification_pair: {
      pair_sha256: finalized.pairSha256,
      source_commit: pair.payload.identity.sourceCommit,
      source_tree: pair.payload.identity.sourceTree,
      action_commit: pair.action.commit,
      workflow_commit: pair.producer.workflowCommit,
      map_sha256: pair.payload.proposedMapSha256,
      assets_sha256: pair.action.assetsSha256,
      original_bytes_unchanged: true,
      output_summary_parity: true,
    },
    archive_sha256: pair.payload.archiveSha256,
    archive_size: pair.payload.archiveSize,
    archive_inventory_sha256: pair.payload.inventorySha256,
    synthetic_release_projection: false,
    runner: 'ubuntu-24.04-x64',
    runtime: pair.action.runtime.policy.version,
    private_network_namespace: true,
    private_mount_namespace: true,
    outside_mounts_unchanged: true,
    privileged_payload: false,
    external_routes: 0,
    sdk_count: 0,
    live_provider_requests: 0,
    profile: 'cr1-solus-internal4-dev',
    context_format: 2,
    context_compatibility: 1,
    checked_action_assets: Object.fromEntries(
      pair.action.assets.map((asset: any) => [asset.path, asset.sha256]),
    ),
    cases: originalCases.map(({ name }) => ({
      name,
      status: 'reviewed',
      completion_status: 'reviewed',
      exit: 0,
      sends: 2,
      actual_host: true,
    })),
    direct_cutover: {
      cli_unknown_usage_cases: 3,
      direct_processes: 9,
      original_members_unchanged: true,
      linked_managed_fault_proof: false,
    },
    transactions: [{ case: 'fresh' }, { case: 'restored-second-head' }],
    official_requests: 10,
    github_requests: 20,
  };
  harness.execute.mockImplementation(async (input: any, verified: any) => {
    expect(readFileSync(input.archivePath)).toEqual(f.p.archive);
    expect(readFileSync(input.mapPath)).toEqual(f.record.mapBytes);
    expect(verified.pairBytes).toEqual(pairBytes);
    return facts;
  });
  const run = () => qualifyPair(f.repo, inputs, artifacts, output, env);
  return { f, finalized, pairBytes, pairPath, pair, q, env, inputs, artifacts, output, facts, run };
}

describe('exact-pair qualifier producer (synthetic service identities only)', () => {
  test('zero-external-request ownership admits only its three exact consumers and zero assertions', () => {
    const rule = residualReferenceRules.find((r) => r.id === 'RR-047')!;
    const field = 'live_provider_requests: 0';
    const assertion = 'assert.equal(facts.live_provider_requests, 0);';
    for (const file of [
      'runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/archive-child.mjs',
      'runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/qualify-original-pair.mjs',
      'tests/distribution/pair-qualification.test.ts',
    ])
      expect(rule.path.test(file)).toBe(true);
    expect(rule.path.test('tests/distribution/unregistered.mjs')).toBe(false);
    expect(rule.term.test(field)).toBe(true);
    expect(rule.term.test(assertion)).toBe(true);
    expect(rule.term.test(field.replace(': 0', ': 1'))).toBe(false);
    expect(rule.term.test(field.replace(': 0', ': 0.5'))).toBe(false);
    expect(rule.term.test(assertion.replace(', 0)', ', 1)'))).toBe(false);
  });
  test('canonical manifest enumerates every concrete case and all P3 groups', () => {
    expect(readFileSync(CHECK_MANIFEST_PATH)).toEqual(expectedBytes);
    const manifest = parseExpectedChecks(expectedBytes);
    expect(originalCases).toHaveLength(50);
    expect(manifest.checks).toHaveLength(60);
    for (const variant of ['automatic', 'manual'])
      expect(originalCases.filter((c) => c.name.startsWith('h1-' + variant))).toHaveLength(3);
    expect(new Set(manifest.checks.map((c: any) => c.id)).size).toBe(60);
  });
  test('producer authenticates originals first and its official uploader ZIP roundtrips through unchanged P3', async () => {
    const x = await fixture();
    expect(await x.run()).toMatchObject({ passed: true, qualified: false });
    expect(harness.execute).toHaveBeenCalledTimes(1);
    const receiptBytes = readFileSync(join(x.output, 'receipt.json'));
    const r = JSON.parse(receiptBytes.toString());
    expect(r.checkCount).toBe(60);
    expect(r.producer.workflowCommit).toBe(x.q);
    const moduleUrl = new URL(
      '../../node_modules/@actions/artifact/lib/internal/upload/zip.js',
      import.meta.url,
    );
    const { createZipUploadStream } = await import(/* @vite-ignore */ moduleUrl.href);
    const names = [
      'receipt.json',
      'case-results.json',
      ...qualificationManifest.checks.map((c) => 'evidence/' + c.id + '.json'),
    ];
    const stream = await createZipUploadStream(
      names.map((name) => ({
        sourcePath: join(x.output, name),
        destinationPath: name,
        stats: statSync(join(x.output, name)),
      })),
      0,
    );
    const chunks = [];
    for await (const chunk of stream) chunks.push(chunk);
    const archive = Buffer.concat(chunks),
      archivePath = join(x.f.root, 'qualification.zip');
    writeFileSync(archivePath, archive);
    const parsed = inspectQualificationArchive(archive, sha256(archive));
    expect(
      inspectQualificationContent({
        pairBytes: x.pairBytes,
        pairSha256: x.finalized.pairSha256,
        expectedChecksBytes: expectedBytes,
        receiptSha256: sha256(receiptBytes),
        ...parsed,
      }),
    ).toMatchObject({ contentVerified: true, operationallyQualified: false });
    x.f.qArtifact.digest = 'sha256:' + sha256(archive);
    x.f.qArtifact.size_in_bytes = archive.length;
    // Fake API authentication exercises the entire P3 consumer interface; real
    // GitHub cannot authenticate these synthetic run/commit/artifact identities.
    expect(
      await verifyQualifiedPair(
        {
          ...x.f.input,
          pairPath: x.pairPath,
          pairSha256: x.finalized.pairSha256,
          qualifierCommit: x.q,
          qualificationArchivePath: archivePath,
          qualificationSha256: sha256(archive),
          qualificationId: '401',
          receiptSha256: sha256(receiptBytes),
        },
        {},
      ),
    ).toMatchObject({ qualified: true, checkCount: 60 });
    expect(x.f.calls.every((c: any) => c.method === 'GET')).toBe(true);
    expect(readFileSync(x.pairPath)).toEqual(x.pairBytes);
  });
  test.each([
    'throw-after-work',
    'missing-case',
    'duplicate-case',
    'wrong-pair',
    'escaped-route',
    'missing-close',
    'changed-map',
    'wrong-profile',
  ])('unknown or failed execution publishes no receipt: %s', async (kind) => {
    const x = await fixture();
    if (kind === 'throw-after-work')
      harness.execute.mockRejectedValue(new Error('PRIVATE_PARTIAL_SCOPE'));
    if (kind === 'missing-case') x.facts.cases.pop();
    if (kind === 'duplicate-case') x.facts.cases[1] = x.facts.cases[0]!;
    if (kind === 'wrong-pair') x.facts.qualification_pair.pair_sha256 = 'f'.repeat(64);
    if (kind === 'escaped-route') x.facts.external_routes = 1;
    if (kind === 'missing-close') x.facts.outside_mounts_unchanged = false;
    if (kind === 'changed-map') x.facts.qualification_pair.map_sha256 = 'f'.repeat(64);
    if (kind === 'wrong-profile') x.facts.profile = 'native-64';
    await expect(x.run()).rejects.toThrow();
    expect(existsSync(x.output)).toBe(false);
  });
  test.each(['runner', 'runtime'])(
    'known pre-execution %s denial records performed scope and never runs B',
    async (kind) => {
      const x = await fixture();
      if (kind === 'runner') harness.runner.mockReturnValue({ unchanged: false, assetCount: 7 });
      else harness.supported.mockReturnValue(false);
      expect(await x.run()).toMatchObject({ passed: false, qualified: false });
      expect(harness.execute).not.toHaveBeenCalled();
      const receipt = JSON.parse(readFileSync(join(x.output, 'receipt.json'), 'utf8'));
      const results = JSON.parse(readFileSync(join(x.output, 'case-results.json'), 'utf8'));
      expect(receipt.result).toBe('refusal');
      expect(results.checks.find((c: any) => c.id === 'pair-authentication').status).toBe('pass');
      expect(
        results.checks.find(
          (c: any) => c.id === (kind === 'runner' ? 'composite-runner-reuse' : 'runtime-only'),
        ).status,
      ).toBe('refused');
      expect(
        results.checks
          .filter((c: any) => c.id.startsWith('case-'))
          .every((c: any) => c.status === 'not-run' && c.evidenceSha256 === null),
      ).toBe(true);
    },
  );
  test.each(['archive', 'map', 'provenance', 'source', 'version', 'platform', 'member', 'asset'])(
    'full P3 original admission refuses %s drift before runtime',
    async (kind) => {
      const x = await fixture();
      if (kind === 'archive' || kind === 'member')
        writeFileSync(join(x.artifacts, 'payload', x.pair.payload.archiveName), 'substitute');
      if (kind === 'map')
        writeFileSync(join(x.artifacts, 'metadata/proposed-action-map.json'), '{}');
      if (kind === 'provenance') {
        x.f.cert.buildSignerDigest = 'f'.repeat(40);
        x.f.saveGh();
      }
      if (kind === 'asset') x.pair.action.assets[0].sha256 = 'f'.repeat(64);
      if (['source', 'version', 'platform'].includes(kind))
        x.pair.payload.identity[
          kind === 'source' ? 'sourceCommit' : kind === 'version' ? 'releaseVersion' : 'platform'
        ] =
          kind === 'source' ? 'f'.repeat(40) : kind === 'version' ? 'v9.9.9-internal.1' : 'win-x64';
      if (['source', 'version', 'platform', 'asset'].includes(kind)) {
        const bytes = Buffer.from(canonicalJson(x.pair));
        writeFileSync(join(x.inputs, 'pair.json'), bytes);
        x.env.QUALIFICATION_PAIR_SHA256 = sha256(bytes);
      }
      await expect(x.run()).rejects.toThrow();
      expect(harness.execute).not.toHaveBeenCalled();
      expect(existsSync(x.output)).toBe(false);
    },
  );
  test('pre-download request uses exact three authenticated original storage identities and exclusive output', async () => {
    const x = await fixture(),
      admitted = join(x.f.root, 'admitted');
    expect(await admitQualificationRequest(x.f.repo, admitted, x.env)).toMatchObject({
      admitted: true,
      qualified: false,
    });
    expect(readFileSync(join(admitted, 'pair.json'))).toEqual(x.pairBytes);
    await expect(admitQualificationRequest(x.f.repo, admitted, x.env)).rejects.toThrow();
    expect(() => matchRequest(x.pair, { ...x.f.request, metadataId: '999' })).toThrow(
      'qualification_request_drift',
    );
  });
  test.each([
    ['GITHUB_EVENT_NAME', 'pull_request'],
    ['GITHUB_EVENT_NAME', 'push'],
    ['GITHUB_REF', 'refs/heads/other'],
    ['GITHUB_REPOSITORY_ID', '42'],
    ['GITHUB_REPOSITORY', 'other/repo'],
    ['GITHUB_WORKFLOW_SHA', 'f'.repeat(40)],
    ['GITHUB_SHA', 'f'.repeat(40)],
    ['RUNNER_ENVIRONMENT', 'self-hosted'],
    ['RUNNER_ARCH', 'ARM64'],
    ['RUNNER_OS', 'Windows'],
    ['GITHUB_WORKFLOW_REF', 'other'],
    ['GITHUB_RUN_ATTEMPT', '0'],
    ['GITHUB_RUN_ID', '01'],
  ])('refuses operational context drift %s=%s', async (key, value) => {
    const x = await fixture();
    expect(() => admitQualifier(x.f.repo, x.q, { ...x.env, [key]: value })).toThrow();
  });
  test('output cannot overwrite an earlier receipt and no all-not-run fallback exists', async () => {
    const x = await fixture();
    const producer = admitQualifier(x.f.repo, x.q, x.env);
    const observations = originalPairObservations(x.facts, x.pair, x.finalized.pairSha256);
    observations.set('pair-authentication', { status: 'pass', observation: {} });
    observations.set('composite-runner-reuse', { status: 'pass', observation: {} });
    const content = qualificationEvidence({
      pairBytes: x.pairBytes,
      pairSha256: x.finalized.pairSha256,
      expectedChecksBytes: expectedBytes,
      producer,
      executionKind: 'synthetic',
      observations,
    });
    await writeQualificationEvidence(x.output, x.f.repo, content);
    await expect(writeQualificationEvidence(x.output, x.f.repo, content)).rejects.toThrow();
    expect(() =>
      qualificationEvidence({
        pairBytes: x.pairBytes,
        pairSha256: x.finalized.pairSha256,
        expectedChecksBytes: expectedBytes,
        producer,
        executionKind: 'synthetic',
        observations: new Map(),
      }),
    ).toThrow('qualification_scope_unknown');
  });
  test('materializes exact archive, original receipt and eight T blobs without managed build/probe paths', async () => {
    const x = await fixture(),
      root = join(x.f.root, 'consumer');
    mkdirSync(root);
    const input = {
      ...x.f.input,
      pairSha256: x.finalized.pairSha256,
      receiptPath: join(x.artifacts, 'payload/receipt.json'),
    };
    await prepareOriginalPair(root, input, { pair: x.pair, pairBytes: x.pairBytes }, {});
    expect(readFileSync(join(root, 'package', x.pair.payload.archiveName))).toEqual(x.f.p.archive);
    expect(readFileSync(join(root, 'package/receipt.json'))).toEqual(
      readFileSync(input.receiptPath),
    );
    for (const asset of x.pair.action.assets)
      expect(sha256(readFileSync(join(root, 'action-assets', asset.path)))).toBe(asset.sha256);
    expect(harness.support).toHaveBeenCalledWith(root, x.f.repo, {}, { originalPair: true });
    const code = readFileSync(
      'runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/qualify-original-pair.mjs',
      'utf8',
    );
    expect(code).not.toContain("'publish'");
    expect(code).not.toContain('build-managed');
    const child = readFileSync(
      'runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/archive-child.mjs',
      'utf8',
    );
    expect(child).toContain("fresh.status !== 'reviewed' && !pair");
  });
  test('Runner reuse checks actual seven blobs; map binding is separate and changed metadata refuses', async () => {
    const x = await fixture();
    const actual = await vi.importActual<
      typeof import('../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/qualify-original-pair.mjs')
    >(
      '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/qualify-original-pair.mjs',
    );
    const repo = join(x.f.root, 'runner-source');
    execFileSync('git', ['clone', '--shared', '--no-checkout', '--quiet', process.cwd(), repo]);
    const git = (...args: string[]) =>
      execFileSync('git', args, { cwd: repo, stdio: ['ignore', 'pipe', 'pipe'] })
        .toString()
        .trim();
    git('config', 'user.name', 'Synthetic fixture');
    git('config', 'user.email', 'fixture@example.invalid');
    git('read-tree', 'HEAD');
    const head = git('rev-parse', 'HEAD');
    expect(actual.runnerEvidence(repo, { action: { commit: head } }).unchanged).toBe(true);
    const action = '.github/actions/agentic-pr-review';
    mkdirSync(join(repo, action), { recursive: true });
    writeFileSync(join(repo, action, 'payload-map.json'), '{"synthetic":"binding-only"}');
    git('add', action + '/payload-map.json');
    git('commit', '-qm', 'Synthetic map-only binding');
    expect(
      actual.runnerEvidence(repo, { action: { commit: git('rev-parse', 'HEAD') } }).unchanged,
    ).toBe(true);
    writeFileSync(join(repo, action, 'action.yml'), 'changed composition');
    git('add', action + '/action.yml');
    git('commit', '-qm', 'Synthetic changed metadata');
    expect(
      actual.runnerEvidence(repo, { action: { commit: git('rev-parse', 'HEAD') } }).unchanged,
    ).toBe(false);
  });
});

describe('bounded provider scripts and pair-only case timing', () => {
  function body() {
    return {
      model: 'deepseek-flash',
      max_tokens: 1024,
      stream: false,
      thinking: { type: 'enabled' },
      reasoning_effort: 'high',
      tools: ['list_changed_files', 'list_files', 'read_diff', 'read_file', 'search_text'].map(
        (name) => ({ type: 'function', function: { name } }),
      ),
      messages: [{ role: 'user', content: 'review' }] as any[],
    };
  }
  async function send(provider: any, document: any) {
    const request = Object.assign(Readable.from([Buffer.from(JSON.stringify(document))]), {
      url: '/chat/completions',
      method: 'POST',
      headers: { authorization: 'Bearer ' + providerKey },
    });
    const response = {
      bytes: Buffer.alloc(0),
      writeHead() {
        return this;
      },
      end(bytes: Buffer) {
        this.bytes = bytes;
        return this;
      },
    };
    await provider.handle(request, response);
    return JSON.parse(response.bytes.toString());
  }
  test('six-call useful script returns six associated observations using every admitted tool', async () => {
    const provider = createProviderFixture(createGitHubFixture('workflow'));
    provider.configure('useful-six', null);
    const document = body(),
      calls = [];
    for (let turn = 0; turn < 6; turn++) {
      const response = await send(provider, document),
        message = response.choices[0].message;
      document.messages.push(message);
      if (turn < 5) {
        expect(response.choices[0].finish_reason).toBe('tool_calls');
        for (const call of message.tool_calls) {
          calls.push(call.function.name);
          document.messages.push({
            role: 'tool',
            tool_call_id: call.id,
            content: JSON.stringify({
              observation_id: 'synthetic-observation',
              path: 'Caller.cs',
              content: 'Lookup.Find(key).Trim()',
            }),
          });
        }
      } else expect(JSON.parse(message.content).findings).toHaveLength(1);
    }
    expect(calls).toHaveLength(6);
    expect(new Set(calls).size).toBe(5);
    expect(provider.receipts.at(-1)).toMatchObject({ sends: 6, tools: 6 });
    await expect(send(provider, document)).rejects.toThrow();
  });
  test.each([
    ['tool-capacity', 3],
    ['call-capacity', 1],
    ['batch-capacity', 4],
  ])('capacity script %s proposes a bounded concrete batch', async (mode, count) => {
    const provider = createProviderFixture(createGitHubFixture('workflow'));
    provider.configure(mode, null);
    expect((await send(provider, body())).choices[0].message.tool_calls).toHaveLength(count);
  });
  test('pair count is explicit; historical timing still refuses case42', () => {
    const value = {
      kind: 'apr-d5-case-timing-v1',
      role: 'first',
      ordinal: 42,
      elapsed_ms: 1,
      role_elapsed_ms: 1,
      closed: true,
      watchdog_fired: false,
    };
    expect(() => caseTimingLine(value)).toThrow();
    const lines = Array.from({ length: ORIGINAL_PAIR_CASE_COUNT }, (_, i) =>
      caseTimingLine({ ...value, ordinal: i + 1 }, { maximumCases: ORIGINAL_PAIR_CASE_COUNT }),
    );
    expect(
      extractCaseTimings(lines.join('\n'), 'first', { maximumCases: ORIGINAL_PAIR_CASE_COUNT }),
    ).toHaveLength(50);
    expect(() =>
      caseTimingLine({ ...value, ordinal: 51 }, { maximumCases: ORIGINAL_PAIR_CASE_COUNT }),
    ).toThrow();
  });
});
