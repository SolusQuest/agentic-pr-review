// Trusted exact-head construction followed by the literal managed Action proof.
// The producer needs the SDK and an already verified package cache. The consumer
// driver supplies a separate closed synthetic environment to the generated Action.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { build } from 'esbuild';

assert.equal(process.platform, 'linux');
assert.equal(process.arch, 'x64');
const repo = resolve(process.argv[2] ?? process.cwd());
const source = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repo }).toString().trim();
assert.equal(
  execFileSync('git', ['status', '--porcelain', '--untracked-files=no'], { cwd: repo }).toString(),
  '',
);
const work = await mkdtemp(join(tmpdir(), 'apr-d4-trusted-proof-'));
try {
  const output = join(work, 'managed-package');
  execFileSync(
    process.execPath,
    [
      join(repo, 'scripts/release/build-payload.mjs'),
      'build-managed',
      '--source',
      source,
      '--version',
      'v0.0.0-internal.1',
      '--output',
      output,
    ],
    { cwd: repo, stdio: 'inherit', timeout: 600_000 },
  );
  const receipt = JSON.parse(await readFile(join(output, 'receipt.json'), 'utf8'));
  assert.equal(receipt.identity.sourceCommit, source);
  const response = await fetch(
    'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.13/dotnet-runtime-10.0.13-linux-x64.tar.gz',
    {
      redirect: 'error',
      signal: AbortSignal.timeout(60_000),
      headers: { 'Accept-Encoding': 'identity' },
    },
  );
  assert.equal(response.status, 200);
  assert.ok(response.body);
  const chunks = [];
  let total = 0;
  try {
    for await (const bytes of response.body) {
      total += bytes.byteLength;
      assert.ok(total <= 36_648_012);
      chunks.push(Buffer.from(bytes));
    }
  } catch (error) {
    await response.body.cancel().catch(() => {});
    throw error;
  }
  assert.equal(total, 36_648_012);
  const runtime = Buffer.concat(chunks);
  assert.equal(
    createHash('sha512').update(runtime).digest('hex'),
    '9574d95364110409416735e7842796f8d614c21d74cb25d204a3e4cfd11b2c05efdf19ca89b8069650a04f64add2705967dcb04b8665a36d7a3f30a5d8c05afe',
  );
  const runtimeArchive = join(work, 'runtime.tar.gz');
  await writeFile(runtimeArchive, runtime, { mode: 0o600, flag: 'wx' });
  const subprocessFixture = join(work, 'runtime-process-fixture.mjs');
  await build({
    absWorkingDir: repo,
    entryPoints: ['tests/distribution/runtime-prerequisite-process-fixture.mjs'],
    outfile: subprocessFixture,
    bundle: true,
    platform: 'node',
    format: 'esm',
    packages: 'external',
  });
  execFileSync(process.execPath, [subprocessFixture, runtimeArchive], {
    cwd: work,
    env: { PATH: '/usr/bin:/bin', TMPDIR: work },
    stdio: 'inherit',
    timeout: 60_000,
  });
  execFileSync(
    process.execPath,
    [
      join(repo, 'tests/distribution/verify-managed-generated-action.mjs'),
      repo,
      join(output, receipt.archiveName),
      join(output, 'receipt.json'),
      runtimeArchive,
    ],
    { cwd: repo, stdio: 'inherit', timeout: 600_000 },
  );
} finally {
  await rm(work, { recursive: true, force: true });
}
