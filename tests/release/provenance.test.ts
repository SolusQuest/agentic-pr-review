import { execFileSync, spawnSync } from 'node:child_process';
import { chmodSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { delimiter, join, resolve } from 'node:path';
import { afterEach, describe, expect, test, vi } from 'vitest';
import { canonicalJson, sha256 } from '../../scripts/release/build-payload.format.mjs';
import {
  createCandidate,
  REPOSITORY,
  REPOSITORY_ID,
  WORKFLOW,
} from '../../scripts/release/candidate.mjs';
import {
  GH_VERSION,
  main,
  PREDICATE_TYPE,
  SIGNER,
} from '../../scripts/release/verify-provenance.mjs';
import {
  artifactFixture,
  NOW,
  packageFixture,
  producerFixture,
  runFixture,
  storageFixture,
} from './fixtures.mjs';

const directories: string[] = [];
afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  for (const dir of directories.splice(0)) rmSync(dir, { recursive: true, force: true });
});
function fixture(current = false) {
  const dir = mkdtempSync(join(tmpdir(), 'apr-p2-'));
  directories.push(dir);
  const git = (...args: string[]) => execFileSync('git', args).toString().trim();
  const producer = producerFixture();
  const inputs = current
    ? { sourceCommit: git('rev-parse', 'HEAD^'), sourceTree: git('rev-parse', 'HEAD^^{tree}') }
    : {};
  if (current) producer.workflowCommit = git('rev-parse', 'HEAD');
  const p = packageFixture([], inputs),
    storage = storageFixture(producer);
  const record = createCandidate(p.archive, p.receipt, producer, storage, {
    ...p.receipt.identity,
    archiveSha256: p.receipt.archiveSha256,
  });
  const metadata = storageFixture(producer, 'metadata');
  const c = record.candidate;
  const cert = {
    issuer: 'https://token.actions.githubusercontent.com',
    subjectAlternativeName: SIGNER,
    buildSignerURI: SIGNER,
    buildSignerDigest: producer.workflowCommit,
    sourceRepositoryURI: `https://github.com/${REPOSITORY}`,
    sourceRepositoryIdentifier: REPOSITORY_ID,
    sourceRepositoryRef: 'refs/heads/main',
    sourceRepositoryDigest: producer.workflowCommit,
    buildConfigURI: SIGNER,
    buildConfigDigest: producer.workflowCommit,
    runnerEnvironment: 'github-hosted',
    buildTrigger: 'workflow_dispatch',
    runInvocationURI: `https://github.com/${REPOSITORY}/actions/runs/${producer.runId}/attempts/${producer.runAttempt}`,
    sourceRepositoryVisibilityAtSigning: 'public',
  };
  const statement = {
    _type: 'https://in-toto.io/Statement/v1',
    subject: [{ name: p.receipt.archiveName, digest: { sha256: p.receipt.archiveSha256 } }],
    predicateType: PREDICATE_TYPE,
    predicate: {
      candidateSha256: record.candidateSha256,
      proposedMapSha256: record.mapSha256,
      source: structuredClone(p.receipt.identity),
      producer: structuredClone(producer),
    },
  };
  const result: any = [
    {
      verificationResult: {
        signature: { certificate: cert },
        verifiedTimestamps: [{ type: 'Tlog', timestamp: '2026-10-05T09:59:00Z' }],
        statement,
      },
    },
  ];
  for (const [name, bytes] of Object.entries({
    archive: p.archive,
    candidate: record.bytes,
    map: record.mapBytes,
    receipt: Buffer.from(JSON.stringify(p.receipt)),
    bundle: Buffer.from('{"synthetic":"not a real Sigstore bundle"}'),
  }))
    writeFileSync(join(dir, name), bytes);
  const resultPath = join(dir, 'tool-result');
  const setResult = (value: any = result) =>
    writeFileSync(resultPath, typeof value === 'string' ? value : JSON.stringify(value));
  setResult();
  // Executable test adapter: production still invokes gh, checks exit status and
  // parses only its stdout. No test JSON/bypass option exists in the real CLI.
  writeFileSync(
    join(dir, 'gh'),
    `#!${process.execPath}\nimport fs from 'node:fs';
const args = process.argv.slice(2);
fs.appendFileSync(${JSON.stringify(join(dir, 'args'))}, JSON.stringify(args)+'\\n');
if(args[0]==='--version') { console.log('gh version '+(process.env.P2_TEST_GH_VERSION || '${GH_VERSION}')+' (synthetic)'); }
else if(process.env.P2_TEST_FAIL) { console.error('PRIVATE-STDERR-CANARY'); process.exit(1); }
else { process.stdout.write(fs.readFileSync(${JSON.stringify(resultPath)})); }
`,
  );
  chmodSync(join(dir, 'gh'), 0o700);
  vi.stubEnv('PATH', dir + delimiter + process.env.PATH);
  vi.spyOn(Date, 'now').mockReturnValue(NOW);
  const run = runFixture(producer);
  const artifacts = [artifactFixture(storage, producer), artifactFixture(metadata, producer)];
  const fetchMock = vi.fn(async (url: string, options: any) => {
    expect(options.method).toBe('GET');
    expect(options.redirect).toBe('error');
    const root = `https://api.github.com/repos/${REPOSITORY}/actions`;
    const body =
      url === `${root}/runs/${producer.runId}/attempts/${producer.runAttempt}`
        ? run
        : url === `${root}/artifacts/${storage.artifactId}`
          ? artifacts[0]
          : url === `${root}/artifacts/${metadata.artifactId}`
            ? artifacts[1]
            : url === `${root}/runs/${producer.runId}/artifacts?per_page=100&page=1`
              ? { total_count: 2, artifacts }
              : null;
    expect(body).not.toBeNull();
    return new Response(JSON.stringify(body), { status: 200 });
  });
  vi.stubGlobal('fetch', fetchMock);
  const common = [
    '--archive',
    join(dir, 'archive'),
    '--candidate',
    join(dir, 'candidate'),
    '--map',
    join(dir, 'map'),
    '--sha256',
    record.candidateSha256,
    '--source',
    p.receipt.identity.sourceCommit,
    '--workflow',
    producer.workflowCommit,
    '--version',
    p.receipt.identity.releaseVersion,
    '--metadata-id',
    metadata.artifactId,
    '--metadata-sha256',
    metadata.sha256,
  ];
  const args = [
    'verify',
    ...common,
    '--bundle',
    join(dir, 'bundle'),
    '--output',
    join(dir, 'report'),
  ];
  const env: any = {
    GITHUB_EVENT_NAME: 'workflow_dispatch',
    GITHUB_REPOSITORY: REPOSITORY,
    GITHUB_REPOSITORY_ID: REPOSITORY_ID,
    GITHUB_REF: 'refs/heads/main',
    GITHUB_WORKFLOW_REF: `${REPOSITORY}/${WORKFLOW}@refs/heads/main`,
    GITHUB_WORKFLOW_SHA: producer.workflowCommit,
    GITHUB_SHA: producer.workflowCommit,
    GITHUB_RUN_ID: producer.runId,
    GITHUB_RUN_ATTEMPT: producer.runAttempt,
    RUNNER_ENVIRONMENT: 'github-hosted',
    RUNNER_OS: 'Linux',
    RUNNER_ARCH: 'X64',
    ARCHIVE_SHA256: p.receipt.archiveSha256,
    RECEIPT_SHA256: sha256(readFileSync(join(dir, 'receipt'))),
    MAP_SHA256: record.mapSha256,
    PAYLOAD_ID: storage.artifactId,
    PAYLOAD_SHA256: storage.sha256,
  };
  return {
    dir,
    p,
    c,
    cert,
    statement,
    result,
    setResult,
    run,
    artifacts,
    common,
    args,
    env,
    fetchMock,
  };
}

describe('P2 restrictive provenance gate (synthetic verifier responses, no signing)', () => {
  test('verifies S != W, original B, complete canonical candidate and both exact storage locators', async () => {
    const f = fixture();
    expect(f.p.receipt.identity.sourceCommit).not.toBe(f.c.producer.workflowCommit);
    const report = await main(f.args, {});
    expect(report).toMatchObject({
      provenanceVerified: true,
      completedRunVerified: true,
      candidateSha256: sha256(readFileSync(join(f.dir, 'candidate'))),
      bundleSha256: sha256(readFileSync(join(f.dir, 'bundle'))),
    });
    expect(JSON.parse(readFileSync(join(f.dir, 'report'), 'utf8'))).toEqual(report);
    const invocations = readFileSync(join(f.dir, 'args'), 'utf8')
      .trim()
      .split('\n')
      .map((x) => JSON.parse(x));
    expect(invocations).toEqual([
      ['--version'],
      [
        'attestation',
        'verify',
        join(f.dir, 'archive'),
        '--bundle',
        join(f.dir, 'bundle'),
        '--repo',
        REPOSITORY,
        '--cert-identity',
        SIGNER,
        '--cert-oidc-issuer',
        'https://token.actions.githubusercontent.com',
        '--signer-digest',
        f.c.producer.workflowCommit,
        '--source-digest',
        f.c.producer.workflowCommit,
        '--source-ref',
        'refs/heads/main',
        '--deny-self-hosted-runners',
        '--predicate-type',
        PREDICATE_TYPE,
        '--hostname',
        'github.com',
        '--format',
        'json',
      ],
    ]);
    expect(f.fetchMock).toHaveBeenCalledTimes(6);
    await expect(main(f.args, {})).rejects.toThrow(); // exclusive evidence, no overwrite retry
  });
  test.each([
    ['issuer', 'https://attacker.invalid'],
    ['subjectAlternativeName', SIGNER + '-suffix'],
    ['buildSignerURI', SIGNER.replace('release.yml', 'other.yml')],
    ['buildSignerDigest', 'a'.repeat(40)],
    ['sourceRepositoryURI', 'https://github.com/foreign/repo'],
    ['sourceRepositoryIdentifier', '9'],
    ['sourceRepositoryRef', 'refs/heads/Main'],
    ['sourceRepositoryDigest', 'a'.repeat(40)],
    ['buildConfigURI', SIGNER.replace('main', 'topic')],
    ['buildConfigDigest', 'a'.repeat(40)],
    ['runnerEnvironment', 'self-hosted'],
    ['runnerEnvironment', ''],
    ['buildTrigger', 'pull_request'],
    ['runInvocationURI', `https://github.com/${REPOSITORY}/actions/runs/101/attempts/1`],
    ['sourceRepositoryVisibilityAtSigning', 'private'],
  ])('rejects certified identity drift: %s = %s', async (key, value) => {
    const f = fixture();
    (f.cert as any)[key] = value;
    f.setResult();
    await expect(main(f.args, {})).rejects.toThrow('provenance_signer_denied');
  });
  test.each(['source', 'candidate', 'map', 'producer', 'subject', 'type', 'extra-source'])(
    'rejects verified cross-candidate association drift: %s',
    async (kind) => {
      const f = fixture(),
        s: any = f.statement;
      if (kind === 'source') s.predicate.source.sourceCommit = 'f'.repeat(40);
      if (kind === 'candidate') s.predicate.candidateSha256 = 'f'.repeat(64);
      if (kind === 'map') s.predicate.proposedMapSha256 = 'f'.repeat(64);
      if (kind === 'producer') s.predicate.producer.runAttempt = '1';
      if (kind === 'subject') s.subject[0].digest.sha256 = 'f'.repeat(64);
      if (kind === 'type') s.predicateType = 'https://slsa.dev/provenance/v1';
      if (kind === 'extra-source') s.predicate.source.extra = 'PRIVATE-SOURCE-CANARY';
      f.setResult();
      await expect(main(f.args, {})).rejects.toThrow('provenance_association_denied');
    },
  );
  test.each(['none', 'many', 'malformed', 'timestamp', 'cert', 'statement'])(
    'rejects missing/ambiguous verification evidence: %s',
    async (kind) => {
      const f = fixture();
      if (kind === 'none') f.setResult([]);
      if (kind === 'many') f.setResult([...f.result, ...f.result]);
      if (kind === 'malformed') f.setResult('not json');
      if (kind === 'timestamp') {
        f.result[0].verificationResult.verifiedTimestamps = [];
        f.setResult();
      }
      if (kind === 'cert') {
        delete f.result[0].verificationResult.signature.certificate;
        f.setResult();
      }
      if (kind === 'statement') {
        delete f.result[0].verificationResult.statement;
        f.setResult();
      }
      await expect(main(f.args, {})).rejects.toThrow();
    },
  );
  test.each(['source', 'workflow', 'version', 'sha256', 'metadata-sha256'])(
    'requires independently anchored expectation: %s',
    async (key) => {
      const f = fixture(),
        args = [...f.args];
      args[args.indexOf('--' + key) + 1] =
        key === 'version' ? 'v0.0.0-internal.2' : 'f'.repeat(key.includes('sha256') ? 64 : 40);
      await expect(main(args, {})).rejects.toThrow();
    },
  );
  test.each([
    'archive',
    'candidate',
    'map',
    'bundle-missing',
    'bundle-empty',
    'bundle-oversize',
    'tool-fail',
    'tool-version',
    'expired',
    'run-failed',
    'in-progress',
    'metadata-missing',
    'closure',
  ])('fails closed at production adapter: %s', async (kind) => {
    const f = fixture();
    if (['archive', 'candidate', 'map'].includes(kind))
      writeFileSync(join(f.dir, kind), 'substituted');
    if (kind === 'bundle-missing') rmSync(join(f.dir, 'bundle'));
    if (kind === 'bundle-empty') writeFileSync(join(f.dir, 'bundle'), '');
    if (kind === 'bundle-oversize')
      writeFileSync(join(f.dir, 'bundle'), Buffer.alloc(4 * 1024 * 1024 + 1));
    if (kind === 'tool-fail') vi.stubEnv('P2_TEST_FAIL', '1');
    if (kind === 'tool-version') vi.stubEnv('P2_TEST_GH_VERSION', '2.93.0');
    if (kind === 'expired') f.artifacts[0].expired = true;
    if (kind === 'run-failed') f.run.conclusion = 'failure';
    if (kind === 'in-progress') {
      f.run.status = 'in_progress';
      (f.run as any).conclusion = null;
    }
    if (kind === 'metadata-missing') f.artifacts[1].name = 'other-candidate';
    if (kind === 'closure') {
      const c = JSON.parse(readFileSync(join(f.dir, 'candidate'), 'utf8'));
      c.buildInputs.dependencies[0].sha256 = 'f'.repeat(64);
      const bytes = canonicalJson(c);
      writeFileSync(join(f.dir, 'candidate'), bytes);
      f.args[f.args.indexOf('--sha256') + 1] = sha256(bytes);
    }
    await expect(main(f.args, {})).rejects.toThrow();
    expect(() => readFileSync(join(f.dir, 'report'))).toThrow();
  });
  test('allows in-progress only for admitted exact current producer, and refuses build output drift', async () => {
    const f = fixture(true);
    f.run.status = 'in_progress';
    (f.run as any).conclusion = null;
    const args = [
      'workflow-predicate',
      ...f.common,
      '--receipt',
      join(f.dir, 'receipt'),
      '--output',
      join(f.dir, 'evidence'),
    ];
    await expect(main(args, { ...f.env, PAYLOAD_SHA256: 'f'.repeat(64) })).rejects.toThrow(
      'provenance_build_output_drift',
    );
    await expect(main(args, { ...f.env, RUNNER_ENVIRONMENT: 'self-hosted' })).rejects.toThrow(
      'prepare_workflow_denied',
    );
    await expect(main(args, { ...f.env, GITHUB_RUN_ATTEMPT: '1' })).rejects.toThrow(
      'provenance_producer_drift',
    );
    await expect(main(args, f.env)).resolves.toEqual({ predicatePrepared: true });
    const pred = JSON.parse(readFileSync(join(f.dir, 'evidence/predicate.json'), 'utf8'));
    expect(pred).toEqual(f.statement.predicate);
    const report = await main(['workflow-verify', ...f.args.slice(1)], f.env);
    expect(report).toMatchObject({ provenanceVerified: true, completedRunVerified: false });
    const ordinary = [...f.args];
    ordinary[ordinary.indexOf('--output') + 1] = join(f.dir, 'ordinary-report');
    await expect(main(ordinary, f.env)).rejects.toThrow('candidate_run_incomplete_stop');
  });
  test('public CLI refuses evidence bypasses and hides verifier errors', () => {
    const cli = resolve('scripts/release/verify-provenance.mjs');
    const result = spawnSync(
      process.execPath,
      [cli, 'verify', '--verified-json', 'untrusted.json'],
      { encoding: 'utf8' },
    );
    expect(result.status).toBe(1);
    expect(result.stderr).toBe('candidate_provenance_denied\n');
    expect(result.stdout).toBe('');
  });
});
