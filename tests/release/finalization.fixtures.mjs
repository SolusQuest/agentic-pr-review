import { execFileSync } from 'node:child_process';
import {
  chmodSync,
  lstatSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  readlinkSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { canonicalJson, sha256 } from '../../scripts/release/build-payload.format.mjs';
import { managedDependencyPolicy } from '../../scripts/release/build-payload.mjs';
import {
  artifactName,
  createCandidate,
  QUALIFICATION_WORKFLOW,
  REPOSITORY,
  REPOSITORY_ID,
} from '../../scripts/release/candidate.mjs';
import {
  ACTION_ASSETS,
  ACTION_ROOT,
  CHECK_GROUPS,
  CHECK_MANIFEST_PATH,
  QUALIFICATION_SCOPE,
  qualificationBindings,
} from '../../scripts/release/finalized-pair.format.mjs';
import { GH_VERSION, PREDICATE_TYPE, SIGNER } from '../../scripts/release/verify-provenance.mjs';
import {
  artifactFixture,
  NOW,
  packageFixture,
  producerFixture,
  runFixture,
  storageFixture,
} from './fixtures.mjs';
import { createTestZip } from '../../src/action-wrapper/artifact-bridge/zip-test-helper.ts';
const project = fileURLToPath(new URL('../../', import.meta.url));
const runtimePolicy = {
  version: '10.0.13',
  url: 'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.13/dotnet-runtime-10.0.13-linux-x64.tar.gz',
  sha512:
    '9574d95364110409416735e7842796f8d614c21d74cb25d204a3e4cfd11b2c05efdf19ca89b8069650a04f64add2705967dcb04b8665a36d7a3f30a5d8c05afe',
  archiveBytes: 36648012,
};

// All Git histories, service responses, records and binaries here are synthetic.
// Public dependency pin metadata is reused; no Solus binaries are built/loaded.
export function finalizationFixture(inputOverrides = {}) {
  const { runtimePolicy: selectedRuntime = runtimePolicy, ...packageOverrides } = inputOverrides;
  const policySource =
    'export const PINNED_MANAGED_RUNTIME = Object.freeze(' +
    JSON.stringify(selectedRuntime) +
    ');\n';
  const root = mkdtempSync(join(tmpdir(), 'apr-p3-')),
    repo = join(root, 'repo');
  mkdirSync(repo);
  const git = (...args) =>
    execFileSync('git', args, { cwd: repo, stdio: ['ignore', 'pipe', 'pipe'] })
      .toString('utf8')
      .trim();
  const write = (path, bytes) => {
    const dest = join(repo, path);
    mkdirSync(dirname(dest), { recursive: true });
    writeFileSync(dest, bytes);
  };
  const commit = (message) => {
    git('add', '.');
    git('commit', '-qm', message);
    return git('rev-parse', 'HEAD');
  };
  git('init', '-q', '--initial-branch=main');
  git('config', 'user.name', 'Synthetic fixture');
  git('config', 'user.email', 'fixture@example.invalid');
  const policyBytes = readFileSync(join(project, 'scripts/release/build-payload.inputs.json'));
  const lockBytes = readFileSync(
    join(project, 'runtime/src/AgenticPrReview.Runtime/packages.managed.lock.json'),
  );
  write('scripts/release/build-payload.inputs.json', policyBytes);
  write('runtime/src/AgenticPrReview.Runtime/packages.managed.lock.json', lockBytes);
  write('src/action-wrapper/launcher/runtime-prerequisite.ts', policySource);
  const source = commit('Synthetic detached source'),
    tree = git('rev-parse', 'HEAD^{tree}');
  write('workflow-fixture.txt', 'Synthetic trusted builder, not real signing.\n');
  const workflow = commit('Synthetic builder');
  const p = packageFixture([], {
    sourceCommit: source,
    sourceTree: tree,
    policySha256: sha256(policyBytes),
    lockSha256: sha256(lockBytes),
    dependencies: managedDependencyPolicy(JSON.parse(policyBytes)).dependencies,
    ...packageOverrides,
  });
  const producer = { ...producerFixture(), workflowCommit: workflow };
  const payload = { ...storageFixture(producer), sha256: sha256('synthetic payload transport') };
  const metadata = {
    ...storageFixture(producer, 'metadata'),
    sha256: sha256('synthetic metadata transport'),
  };
  const provenance = {
    ...storageFixture(producer, 'metadata'),
    artifactId: '203',
    name: artifactName('provenance', producer),
    sha256: sha256('synthetic provenance transport'),
  };
  const record = createCandidate(p.archive, p.receipt, producer, payload, {
    ...p.receipt.identity,
    archiveSha256: p.receipt.archiveSha256,
  });
  for (const [name] of ACTION_ASSETS) {
    let bytes;
    if (name === 'payload-map.json') bytes = record.mapBytes;
    else if (name.endsWith('.js'))
      bytes = name.endsWith('/sdk-upload.js')
        ? Buffer.from(
            'export const fixture = "synthetic SDK bundle"; throw new Error("MUST NOT EXECUTE");\n',
          )
        : Buffer.from(
            policySource +
              'export const fixture = "synthetic bundle; no runtime execution"; throw new Error("MUST NOT EXECUTE");\n',
          );
    else bytes = readFileSync(join(project, ACTION_ROOT, name));
    write(ACTION_ROOT + '/' + name, bytes);
  }
  const action = commit('Synthetic merged map/composite');
  const checks = CHECK_GROUPS.flatMap((group) =>
    group === 'fresh-restored-review'
      ? [
          { id: 'fresh-review', group },
          { id: 'restored-review', group },
        ]
      : [{ id: group, group }],
  ).sort((a, b) => (a.id < b.id ? -1 : 1));
  const expected = {
    formatVersion: 1,
    kind: 'r7-qualification-checks',
    scope: QUALIFICATION_SCOPE,
    checks,
  };
  const expectedBytes = Buffer.from(canonicalJson(expected));
  write(CHECK_MANIFEST_PATH, expectedBytes);
  const qualifier = commit('Synthetic future qualifier manifest; no producer');
  const qProducer = {
    ...producer,
    workflowPath: QUALIFICATION_WORKFLOW,
    workflowCommit: qualifier,
    runId: '301',
    runAttempt: '1',
  };
  const qualification = {
    ...storageFixture(producer, 'metadata'),
    artifactId: '401',
    name: artifactName('qualification', qProducer),
    sha256: '0'.repeat(64), // assigned from the actual fixture ZIP after receipt creation
  };
  const request = {
    candidateSha256: record.candidateSha256,
    workflowCommit: workflow,
    actionCommit: action,
    bindingPr: '999',
    payloadId: payload.artifactId,
    payloadSha256: payload.sha256,
    metadataId: metadata.artifactId,
    metadataSha256: metadata.sha256,
    provenanceId: provenance.artifactId,
    provenanceSha256: provenance.sha256,
  };
  const inputDirectory = join(root, 'input');
  mkdirSync(inputDirectory);
  const paths = {
    archivePath: join(inputDirectory, p.receipt.archiveName),
    candidatePath: join(inputDirectory, 'candidate.json'),
    mapPath: join(inputDirectory, 'proposed-action-map.json'),
    bundlePath: join(inputDirectory, 'bundle.json'),
  };
  writeFileSync(paths.archivePath, p.archive);
  writeFileSync(paths.candidatePath, record.bytes);
  writeFileSync(paths.mapPath, record.mapBytes);
  writeFileSync(paths.bundlePath, '{"synthetic":"not a Sigstore bundle"}');
  const requestPath = join(inputDirectory, 'request.json');
  writeFileSync(requestPath, canonicalJson(request));
  const cert = {
    issuer: 'https://token.actions.githubusercontent.com',
    subjectAlternativeName: SIGNER,
    buildSignerURI: SIGNER,
    buildSignerDigest: workflow,
    sourceRepositoryURI: `https://github.com/${REPOSITORY}`,
    sourceRepositoryIdentifier: REPOSITORY_ID,
    sourceRepositoryRef: 'refs/heads/main',
    sourceRepositoryDigest: workflow,
    buildConfigURI: SIGNER,
    buildConfigDigest: workflow,
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
  const ghResult = [
    {
      verificationResult: {
        signature: { certificate: cert },
        verifiedTimestamps: [{ type: 'Tlog', timestamp: '2026-10-05T09:59:00Z' }],
        statement,
      },
    },
  ];
  const resultPath = join(root, 'gh-result.json');
  const saveGh = () => writeFileSync(resultPath, JSON.stringify(ghResult));
  saveGh();
  const bin = join(root, 'bin');
  mkdirSync(bin);
  writeFileSync(
    join(bin, 'gh'),
    `#!${process.execPath}\nimport fs from 'node:fs';\nconst args=process.argv.slice(2);\nif(args[0]==='--version')console.log('gh version ${GH_VERSION} (synthetic)');else if(process.env.P3_TEST_GH_FAIL){console.error('PRIVATE-CANARY');process.exit(1);}else process.stdout.write(fs.readFileSync(${JSON.stringify(resultPath)}));\n`,
  );
  chmodSync(join(bin, 'gh'), 0o700);
  const releaseRun = runFixture(producer),
    qRun = { ...runFixture(qProducer), path: QUALIFICATION_WORKFLOW };
  const artifacts = [
    artifactFixture(payload, producer),
    artifactFixture(metadata, producer),
    artifactFixture(provenance, producer),
  ];
  const qArtifact = artifactFixture(qualification, qProducer);
  const main = { ref: 'refs/heads/main', object: { type: 'commit', sha: qualifier } };
  const pr = {
    number: 999,
    merged: true,
    state: 'closed',
    merge_commit_sha: action,
    merged_at: '2026-10-05T09:59:30Z',
    base: { ref: 'main', repo: { full_name: REPOSITORY, id: REPOSITORY_ID } },
    head: { repo: { full_name: REPOSITORY, id: REPOSITORY_ID } },
  };
  const calls = [],
    missing = new Set();
  async function fetch(url, options) {
    calls.push({ url: String(url), method: options.method });
    if (options.method !== 'GET' || options.redirect !== 'error')
      throw new Error('fixture_non_read_request');
    const base = `https://api.github.com/repos/${REPOSITORY}`;
    if (!String(url).startsWith(base)) throw new Error('fixture_foreign_endpoint');
    const path = String(url).slice(base.length);
    if (missing.has(path)) return new Response('{}', { status: 404 });
    let body;
    if (path === '/git/ref/heads/main') body = main;
    if (path === '/pulls/999') body = pr;
    if (path === '/actions/runs/101/attempts/2') body = releaseRun;
    if (path === '/actions/runs/301/attempts/1') body = qRun;
    if (path === '/actions/runs/101/artifacts?per_page=100&page=1')
      body = { total_count: artifacts.length, artifacts };
    if (path === '/actions/runs/301/artifacts?per_page=100&page=1')
      body = { total_count: 1, artifacts: [qArtifact] };
    if (path.startsWith('/actions/artifacts/'))
      body = [...artifacts, qArtifact].find((a) => a.id === path.split('/').at(-1));
    if (!body) throw new Error('fixture_unknown_endpoint:' + path);
    return new Response(JSON.stringify(body), { status: 200 });
  }
  const input = {
    repo,
    sourceCommit: source,
    releaseVersion: p.receipt.identity.releaseVersion,
    requestBytes: Buffer.from(canonicalJson(request)),
    ...paths,
    output: join(root, 'final'),
  };
  function qualificationFiles(pairBytes, { executionKind = 'synthetic' } = {}) {
    const pair = JSON.parse(pairBytes),
      pairSha256 = sha256(pairBytes),
      evidence = new Map();
    const results = {
      formatVersion: 1,
      kind: 'r7-qualification-case-results',
      pairSha256,
      scope: QUALIFICATION_SCOPE,
      expectedChecksSha256: sha256(expectedBytes),
      checks: expected.checks.map((check) => {
        const evidenceBytes = Buffer.from(
          canonicalJson({
            formatVersion: 1,
            kind: 'r7-qualification-case-evidence',
            checkId: check.id,
            group: check.group,
            pairSha256,
            outcome: 'pass',
            observationSha256: sha256('SYNTHETIC OBSERVATION ' + check.id),
          }),
        );
        evidence.set(check.id, evidenceBytes);
        return { ...check, status: 'pass', evidenceSha256: sha256(evidenceBytes) };
      }),
    };
    const resultBytes = Buffer.from(canonicalJson(results));
    const receipt = {
      formatVersion: 1,
      kind: 'r7-qualification-receipt',
      pairSha256,
      bindings: qualificationBindings(pair),
      scope: QUALIFICATION_SCOPE,
      executionKind,
      producer: structuredClone(qProducer),
      expectedChecksSha256: sha256(expectedBytes),
      caseResultsSha256: sha256(resultBytes),
      checkCount: expected.checks.length,
      result: 'pass',
      refusalReason: null,
    };
    const directory = join(root, 'qualification');
    mkdirSync(join(directory, 'evidence'), { recursive: true });
    const archivePath = join(root, 'qualification.zip');
    function entries(relative = '') {
      return readdirSync(join(directory, relative)).flatMap((name) => {
        const path = relative + name,
          full = join(directory, path),
          stat = lstatSync(full);
        if (stat.isDirectory()) return entries(path + '/');
        return [
          {
            name: path,
            data: stat.isSymbolicLink() ? Buffer.from(readlinkSync(full)) : readFileSync(full),
            mode: stat.mode,
          },
        ];
      });
    }
    function pack(bytes = createTestZip(entries())) {
      writeFileSync(archivePath, bytes);
      qualification.sha256 = sha256(bytes);
      qualification.size = bytes.length;
      qArtifact.digest = 'sha256:' + qualification.sha256;
      qArtifact.size_in_bytes = bytes.length;
      return { archivePath, archiveSha256: qualification.sha256 };
    }
    function save() {
      const r = Buffer.from(canonicalJson(results));
      receipt.caseResultsSha256 = sha256(r);
      writeFileSync(join(directory, 'case-results.json'), r);
      const receiptBytes = Buffer.from(canonicalJson(receipt));
      writeFileSync(join(directory, 'receipt.json'), receiptBytes);
      for (const [id, bytes] of evidence)
        writeFileSync(join(directory, 'evidence', id + '.json'), bytes);
      return { receiptBytes, receiptSha256: sha256(receiptBytes), resultBytes: r, ...pack() };
    }
    return { directory, receipt, results, evidence, expectedBytes, save, entries, pack, ...save() };
  }
  return {
    root,
    repo,
    git,
    write,
    commit,
    p,
    producer,
    source,
    workflow,
    action,
    qualifier,
    record,
    request,
    requestPath,
    input,
    paths,
    bin,
    ghResult,
    cert,
    statement,
    saveGh,
    artifacts,
    qArtifact,
    releaseRun,
    qRun,
    main,
    pr,
    calls,
    missing,
    fetch,
    payload,
    metadata,
    provenance,
    qualification,
    qProducer,
    expected,
    expectedBytes,
    qualificationFiles,
    now: NOW,
  };
}
