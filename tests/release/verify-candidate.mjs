import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import {
  LIMITS,
  canonicalJson,
  materializePackage,
  readBoundedFile,
  sha256,
} from '../../scripts/release/build-payload.format.mjs';
import { inspectCandidate, proposedMap } from '../../scripts/release/candidate.mjs';
import {
  buildOriginalPayload,
  recordPreparedCandidate,
} from '../../scripts/release/prepare-candidate.mjs';
import { producerFixture, storageFixture } from './fixtures.mjs';

assert.equal(process.platform, 'linux');
assert.equal(process.arch, 'x64');
const repo = resolve(process.cwd());
const workflowFixture = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repo }).toString().trim();
// This merged D1 baseline is an explicit fixture source, NOT a selected release S.
// Using it with current tooling makes actual tested S != current W.
const sourceFixture = 'b8e7e501ed7371abd7d858aeb8c0dcf307be71cf';
const versionFixture = 'v0.0.0-internal.1';
const work = await mkdtemp(join(tmpdir(), 'apr-p1-smoke-'));
try {
  const sourceRepo = join(work, 'source');
  execFileSync('git', ['clone', '--quiet', '--no-hardlinks', '--no-checkout', repo, sourceRepo], {
    timeout: 30000,
  });
  execFileSync('git', ['checkout', '--quiet', '--detach', sourceFixture], {
    cwd: sourceRepo,
    timeout: 30000,
  });
  const output = join(work, 'package');
  const receipt = await buildOriginalPayload({
    repo: sourceRepo,
    sourceCommit: sourceFixture,
    releaseVersion: versionFixture,
    output,
  });
  const archivePath = join(output, receipt.archiveName),
    receiptPath = join(output, 'receipt.json');
  const producer = { ...producerFixture(), workflowCommit: workflowFixture };
  const storage = storageFixture(producer);
  // Run/artifact/transport digest/timestamps are deliberately synthetic fixtures.
  assert.notEqual(storage.sha256, receipt.archiveSha256);
  const record = await recordPreparedCandidate({
    archivePath,
    receiptPath,
    producer,
    storage,
    expected: {
      sourceCommit: sourceFixture,
      sourceTree: receipt.identity.sourceTree,
      releaseVersion: versionFixture,
      archiveSha256: receipt.archiveSha256,
      receiptSha256: sha256(await readBoundedFile(receiptPath, 65536)),
    },
    output: join(work, 'candidate'),
    repo,
  });
  const archive = await readBoundedFile(archivePath, LIMITS.archive);
  const candidate = inspectCandidate(
    archive,
    record.bytes,
    record.mapBytes,
    record.candidateSha256,
  );
  assert.equal(candidate.receipt.identity.sourceCommit, sourceFixture);
  assert.equal(candidate.producer.workflowCommit, workflowFixture);
  assert.notEqual(sourceFixture, workflowFixture);
  const pinned = JSON.parse(
    await readFile(join(sourceRepo, 'scripts/release/build-payload.inputs.json'), 'utf8'),
  );
  assert.equal(candidate.buildInputs.dependencies.length, 9);
  assert.deepEqual(candidate.buildInputs.dependencies, pinned.dependencies);
  assert.deepEqual(JSON.parse(record.mapBytes), proposedMap(receipt, producer));
  const extracted = await materializePackage(archive, receipt, work);
  assert.equal(
    sha256(await readBoundedFile(extracted.executable, LIMITS.executable)),
    receipt.members[0].sha256,
  );
  const env = { TMPDIR: work, TMP: work, TEMP: work };
  const base = JSON.parse(
    await readFile(join(sourceRepo, 'protocol/fixtures/v1/cases/bootstrap/input.json'), 'utf8'),
  );
  for (const [label, requested] of [
    ['exact', receipt.identity.informationalVersion],
    ['wrong', '0.1.0-dev'],
  ]) {
    const input = join(work, label + '-input.json'),
      result = join(work, label + '-result.json'),
      trace = join(work, label + '-trace.json');
    await writeFile(input, JSON.stringify({ ...base, requestedRuntimeVersion: requested }), {
      flag: 'wx',
    });
    const child = spawnSync(
      extracted.executable,
      ['review', '--input', input, '--output', result, '--trace', trace],
      { cwd: work, env, timeout: 30000 },
    );
    assert.equal(child.error, undefined);
    assert.equal(child.signal, null);
    assert.equal(child.status, label === 'exact' ? 0 : 10);
  }
  execFileSync(
    process.execPath,
    [
      join(sourceRepo, 'runtime/tests/ActionHostFixture/verify-entrypoint.mjs'),
      'native',
      extracted.executable,
    ],
    { cwd: work, env, stdio: 'inherit', timeout: 60000 },
  );
  for (const change of [
    (map) => {
      map.payload.identity.sourceCommit = 'f'.repeat(40);
    },
    (map) => {
      map.payload.identity.releaseVersion = 'v0.0.0-internal.2';
    },
    (map) => {
      map.payload.archiveSha256 = 'f'.repeat(64);
    },
    (map) => {
      map.builder.workflowCommit = 'f'.repeat(40);
    },
  ]) {
    const map = JSON.parse(record.mapBytes);
    change(map);
    assert.throws(() =>
      inspectCandidate(
        archive,
        record.bytes,
        Buffer.from(canonicalJson(map)),
        record.candidateSha256,
      ),
    );
  }
  const changed = Buffer.from(archive);
  changed[20] ^= 1;
  assert.throws(() =>
    inspectCandidate(changed, record.bytes, record.mapBytes, record.candidateSha256),
  );
  console.log(
    'P1_LOCAL_PACKAGE_FIXTURE ' +
      JSON.stringify({
        fixtureSource: sourceFixture,
        fixtureWorkflow: workflowFixture,
        fixtureVersion: versionFixture,
        syntheticStorage: true,
        sourceDiffersFromWorkflow: true,
        originalArchiveSha256: receipt.archiveSha256,
        originalArchiveSize: receipt.archiveSize,
        originalExecutableSha256: receipt.members[0].sha256,
        candidateSha256: record.candidateSha256,
        proposedMapSha256: record.mapSha256,
        dependencies: candidate.buildInputs.dependencies.length,
        sdkFreeOriginalExecutable: true,
        productionActionHostFixture: true,
        sourceVersionMapDigestDriftRejected: true,
        releaseAuthorized: false,
      }),
  );
} finally {
  await rm(work, { recursive: true, force: true });
}
