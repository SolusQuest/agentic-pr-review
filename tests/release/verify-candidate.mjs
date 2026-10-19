// Credential-free managed candidate CONTRACT smoke. The public loader fixture
// is executable, but is NOT the Solus production package or candidate provenance.
// Original native S=b8e7e501ed7371abd7d858aeb8c0dcf307be71cf remains historical.
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import {
  MANAGED_TARGET,
  canonicalJson,
  collectManagedPublishTree,
  encodeManagedPackage,
  inspectManagedPackage,
  materializeManagedPackage,
  sha256,
} from '../../scripts/release/build-payload.format.mjs';
import { createCandidate, inspectCandidate } from '../../scripts/release/candidate.mjs';
import { packageFixture, producerFixture, storageFixture } from './fixtures.mjs';
import { verifyManagedCandidateArchive } from './verify-managed-candidate-archive.mjs';

assert.equal(process.platform, 'linux');
assert.equal(process.arch, 'x64');
const repo = resolve(process.cwd());
const workflowFixture = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repo }).toString().trim();
const sourceFixture = 'e33e1a9b14507661ab5a0f0ade992964f457b213';
const work = await mkdtemp(join(tmpdir(), 'apr-managed-candidate-contract-'));
try {
  const sourceRepo = join(work, 'source');
  execFileSync('git', ['clone', '--quiet', '--no-hardlinks', '--no-checkout', repo, sourceRepo]);
  execFileSync('git', ['checkout', '--quiet', '--detach', sourceFixture], { cwd: sourceRepo });
  const sourceTree = execFileSync('git', ['rev-parse', 'HEAD^{tree}'], { cwd: sourceRepo })
    .toString()
    .trim();
  const sdk = execFileSync('which', ['dotnet']).toString().trim();
  assert.equal(execFileSync(sdk, ['--version'], { cwd: sourceRepo }).toString().trim(), '10.0.109');
  const output = join(work, 'publish'),
    emptyFeed = join(work, 'empty-feed');
  await mkdir(emptyFeed);
  const notices = Buffer.from(
    'Public FDD fixture only; no Solus package or release authorization.\n',
  );
  const structural = packageFixture();
  const inputs = {
    ...inspectManagedPackage(structural.archive, structural.receipt).manifest.buildInputs,
    sourceCommit: sourceFixture,
    sourceTree,
    noticesSha256: sha256(notices),
    target: MANAGED_TARGET,
  };
  const buildId = sha256(canonicalJson(inputs));
  execFileSync(
    sdk,
    [
      'publish',
      join(sourceRepo, 'tests/distribution/managed-loader/AgenticPrReview.Runtime.csproj'),
      '-c',
      'Release',
      '-r',
      'linux-x64',
      '--self-contained',
      'false',
      '--source',
      emptyFeed,
      '-o',
      output,
      '-p:PublishAot=false',
      '-p:PublishTrimmed=false',
      '-p:PublishSingleFile=false',
      '-p:InformationalVersion=' + inputs.releaseVersion + '+build.' + buildId,
    ],
    { cwd: sourceRepo, timeout: 180000, stdio: ['ignore', 'ignore', 'pipe'] },
  );
  // A native dependency is legitimate in a managed publish tree; not an AOT toolchain.
  execFileSync('/usr/bin/gcc', [
    '-shared',
    '-fPIC',
    join(sourceRepo, 'tests/distribution/managed-loader/native-probe.c'),
    '-o',
    join(output, 'libaprprobe.so'),
  ]);
  const depsPath = join(output, 'AgenticPrReview.Runtime.deps.json');
  const deps = JSON.parse(await readFile(depsPath, 'utf8'));
  const application = Object.values(deps.targets[deps.runtimeTarget.name]).find(
    (v) => v.runtime?.['AgenticPrReview.Runtime.dll'],
  );
  assert.ok(application);
  application.native = { 'libaprprobe.so': {} };
  await writeFile(depsPath, JSON.stringify(deps));
  const files = await collectManagedPublishTree(output);
  const packaged = encodeManagedPackage(files, notices, inputs);
  const producer = { ...producerFixture(), workflowCommit: workflowFixture };
  const storage = storageFixture(producer);
  const expected = { ...packaged.receipt.identity, archiveSha256: packaged.receipt.archiveSha256 };
  const record = createCandidate(packaged.archive, packaged.receipt, producer, storage, expected);
  const candidate = inspectCandidate(
    packaged.archive,
    record.bytes,
    record.mapBytes,
    record.candidateSha256,
  );
  assert.equal(candidate.receipt.identity.sourceCommit, sourceFixture);
  assert.notEqual(candidate.receipt.identity.sourceCommit, candidate.producer.workflowCommit);
  const checked = verifyManagedCandidateArchive({
    archive: packaged.archive,
    receipt: packaged.receipt,
    sourceCommit: sourceFixture,
    sourceTree,
  });
  const extracted = await materializeManagedPackage(packaged.archive, packaged.receipt, work);
  const entry = extracted.entrypoint;
  const frameBody = Buffer.from('{}'),
    frame = Buffer.alloc(4 + frameBody.length);
  frame.writeUInt32BE(frameBody.length);
  frameBody.copy(frame, 4);
  const child = spawnSync(sdk, [entry], {
    cwd: work,
    input: frame,
    env: { DOTNET_ROOT: dirname(sdk), TMPDIR: work },
    timeout: 30000,
  });
  assert.equal(child.error, undefined);
  assert.equal(child.status, 0);
  const length = child.stdout.readUInt32BE(0);
  assert.equal(child.stdout.length, length + 4);
  const observed = JSON.parse(child.stdout.subarray(4));
  assert.equal(observed.entry, 'verified-entry');
  assert.equal(observed.native, 7001);
  console.log(
    'P1_MANAGED_CONTRACT_FIXTURE ' +
      JSON.stringify({
        sourceFixture,
        workflowFixture,
        sourceDiffersFromWorkflow: true,
        originalArchiveSha256: packaged.receipt.archiveSha256,
        actualPublicFddExecuted: true,
        syntheticBuildAndRemoteMetadata: true,
        productionSolusArchiveQualified: false,
        candidateNegativeCases: checked.negativeCases,
      }),
  );
} finally {
  await rm(work, { recursive: true, force: true });
}
