import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import {
  LIMITS,
  inspectPackage,
  materializePackage,
  readBoundedFile,
  sha256,
} from '../../scripts/release/build-payload.format.mjs';

assert.equal(process.platform, 'linux');
assert.equal(process.arch, 'x64');
const repo = resolve(process.argv[2] || process.cwd());
const sourceCommit = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repo })
  .toString('utf8')
  .trim();
const work = await mkdtemp(join(tmpdir(), 'apr-d1-proof-'));
try {
  const output = join(work, 'package');
  execFileSync(
    process.execPath,
    [
      join(repo, 'scripts/release/build-payload.mjs'),
      'build',
      '--source',
      sourceCommit,
      '--version',
      'v0.0.0-internal.1',
      '--output',
      output,
    ],
    { cwd: repo, stdio: 'inherit', timeout: 600_000 },
  );
  const receipt = JSON.parse(await readFile(join(output, 'receipt.json'), 'utf8'));
  const archive = await readBoundedFile(join(output, receipt.archiveName), LIMITS.archive);
  const inspected = inspectPackage(archive, receipt);
  assert.equal(inspected.manifest.sourceCommit, sourceCommit);
  const extracted = await materializePackage(archive, receipt, work);
  assert.equal(
    sha256(await readBoundedFile(extracted.executable, LIMITS.executable)),
    receipt.members[0].sha256,
  );
  const env = { TMPDIR: work, TMP: work, TEMP: work };
  const baseInput = JSON.parse(
    await readFile(join(repo, 'protocol/fixtures/v1/cases/bootstrap/input.json'), 'utf8'),
  );
  for (const [label, requested] of [
    ['exact', receipt.identity.informationalVersion],
    ['mismatch', '0.1.0-dev'],
  ]) {
    const input = join(work, label + '-input.json'),
      result = join(work, label + '-result.json'),
      trace = join(work, label + '-trace.json');
    await writeFile(input, JSON.stringify({ ...baseInput, requestedRuntimeVersion: requested }), {
      flag: 'wx',
    });
    const child = spawnSync(
      extracted.executable,
      ['review', '--input', input, '--output', result, '--trace', trace],
      { cwd: work, env, timeout: 30_000, maxBuffer: 64 * 1024 },
    );
    assert.equal(child.error, undefined);
    assert.equal(child.signal, null);
    assert.equal(child.status, label === 'exact' ? 0 : 10);
    const traceDocument = JSON.parse(await readFile(trace, 'utf8'));
    assert.equal(traceDocument.runtimeVersion, receipt.identity.informationalVersion);
    if (label === 'exact')
      assert.equal(
        JSON.parse(await readFile(result, 'utf8')).runtimeVersion,
        receipt.identity.informationalVersion,
      );
    else {
      assert.equal(existsSync(result), false);
      assert.match(child.stderr.toString('utf8'), /APR_RUNTIME_VERSION_MISMATCH/);
    }
  }
  execFileSync(
    process.execPath,
    [
      join(repo, 'runtime/tests/ActionHostFixture/verify-entrypoint.mjs'),
      'native',
      extracted.executable,
    ],
    { cwd: work, env, stdio: 'inherit', timeout: 60_000 },
  );
  console.log(
    'D1_PACKAGED_EXECUTABLE ' +
      JSON.stringify({
        sourceCommit,
        archiveSha256: receipt.archiveSha256,
        archiveSize: receipt.archiveSize,
        executableSha256: receipt.members[0].sha256,
        buildId: receipt.identity.buildId,
        informationalVersion: receipt.identity.informationalVersion,
        sdkFreeChild: true,
        originalArchiveMember: true,
        exactRequestedVersion: true,
        wrongRequestedVersionRejected: true,
      }),
  );
} finally {
  await rm(work, { recursive: true, force: true });
}
