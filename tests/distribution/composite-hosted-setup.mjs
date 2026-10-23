import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import * as core from '@actions/core';
import { actionSource, repository, assets, cases } from './composite-proof-source.mjs';
const repo = process.cwd();
const git = (...args) => execFileSync('git', args, { cwd: repo, encoding: 'utf8' }).trim();
const source = git('rev-parse', 'HEAD');
assert.equal(process.platform, 'linux');
assert.equal(process.arch, 'x64');
assert.equal(process.env.GITHUB_EVENT_NAME, 'workflow_dispatch');
assert.equal(process.env.GITHUB_REPOSITORY, repository);
assert.equal(source, process.env.EXPECTED_HEAD);
assert.equal(source, process.env.GITHUB_SHA);
assert.equal(source, process.env.WORKFLOW_HEAD);
assert.equal(git('status', '--porcelain', '--untracked-files=no'), '');
git('merge-base', '--is-ancestor', actionSource, source);
const proofOnly = git('diff', '--name-only', actionSource, source).split('\n').filter(Boolean);
assert.ok(
  proofOnly.every(
    (file) =>
      file.startsWith('tests/distribution/composite-') ||
      [
        '.github/workflows/solus-agent-package-proof.yml',
        'scripts/check-r4-trusted-proof.mjs',
        'scripts/check-r4-trusted-proof.test.ts',
        'tests/distribution/solus-tail-route.test.ts',
        'tests/distribution/managed-generated-action-preload.mjs',
        'tests/distribution/run-generated-composite.mjs',
        'tests/distribution/verify-managed-generated-action.mjs',
        'tests/distribution/README.md',
      ].includes(file),
  ),
);
const work = await mkdtemp(join(process.env.RUNNER_TEMP, 'apr-a1-hosted-'));
const packageRoot = join(work, 'package');
const closed = {
  PATH: process.env.PATH,
  DOTNET_CLI_TELEMETRY_OPTOUT: '1',
  DOTNET_NOLOGO: '1',
  NUGET_PACKAGES: resolve(process.env.NUGET_PACKAGES),
  TMPDIR: work,
  TMP: work,
  TEMP: work,
};
if (process.env.DOTNET_ROOT) closed.DOTNET_ROOT = process.env.DOTNET_ROOT;
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
    packageRoot,
  ],
  { cwd: repo, env: closed, stdio: 'inherit', timeout: 600_000 },
);
const receipt = JSON.parse(await readFile(join(packageRoot, 'receipt.json'), 'utf8'));
assert.equal(receipt.identity.sourceCommit, source);
const runtimeUrl =
  'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.13/dotnet-runtime-10.0.13-linux-x64.tar.gz';
const response = await fetch(runtimeUrl, {
  redirect: 'error',
  signal: AbortSignal.timeout(60_000),
});
assert.equal(response.status, 200);
const chunks = [];
let bytes = 0;
for await (const chunk of response.body) {
  bytes += chunk.length;
  assert.ok(bytes <= 36_648_012);
  chunks.push(chunk);
}
const runtime = Buffer.concat(chunks);
assert.equal(runtime.length, 36_648_012);
assert.equal(
  createHash('sha512').update(runtime).digest('hex'),
  '9574d95364110409416735e7842796f8d614c21d74cb25d204a3e4cfd11b2c05efdf19ca89b8069650a04f64add2705967dcb04b8665a36d7a3f30a5d8c05afe',
);
await writeFile(join(work, 'runtime.tar.gz'), runtime, { mode: 0o600, flag: 'wx' });
const assetSha1 = {},
  assetSha256 = {},
  assetSizes = {};
for (const relative of assets) {
  const original = execFileSync(
    'git',
    ['show', actionSource + ':.github/actions/agentic-pr-review/' + relative],
    { cwd: repo },
  );
  const current = await readFile(join(repo, '.github/actions/agentic-pr-review', relative));
  assert.ok(original.equals(current), 'proof-only source lineage changed installed asset');
  assetSha1[relative] = createHash('sha1')
    .update('blob ' + original.length + '\0')
    .update(original)
    .digest('hex');
  assetSha256[relative] = createHash('sha256').update(original).digest('hex');
  assetSizes[relative] = original.length;
}
const map = {
  formatVersion: 1,
  repository,
  tag: 'payload-' + receipt.identity.releaseVersion,
  builder: {
    repository,
    workflowPath: '.github/workflows/r4-trusted-proof.yml',
    workflowCommit: 'c'.repeat(40),
  },
  payload: receipt,
};
for (const name of cases) {
  const directory = join(work, name);
  await mkdir(directory, { mode: 0o700 });
  for (const folder of ['workspace', 'runner-temp', 'temp'])
    await mkdir(join(directory, folder), { mode: 0o700 });
  await writeFile(join(directory, 'audit.jsonl'), '');
  await writeFile(
    join(directory, 'event.json'),
    JSON.stringify({
      pull_request: {},
      repository: { id: 42, full_name: repository },
      sender: { id: 7, login: 'maintainer' },
    }),
  );
}
const preload = join(repo, 'tests/distribution/composite-hosted-preload.mjs');
await writeFile(
  join(work, 'config.json'),
  JSON.stringify({
    work,
    repo,
    workflowSource: source,
    actionSource,
    proofOnly,
    map,
    archive: join(packageRoot, receipt.archiveName),
    runtime: join(work, 'runtime.tar.gz'),
    runtimeUrl,
    assetSha1,
    assetSha256,
    assetSizes,
    preload,
  }),
);
const daemon = spawn(
  process.execPath,
  [join(repo, 'tests/distribution/composite-hosted-service.mjs'), work],
  {
    cwd: repo,
    env: {
      PATH: process.env.PATH,
      ...(process.env.RUNNER_TRACKING_ID
        ? { RUNNER_TRACKING_ID: process.env.RUNNER_TRACKING_ID }
        : {}),
    },
    detached: true,
    stdio: 'ignore',
  },
);
daemon.unref();
const end = Date.now() + 10_000;
for (;;) {
  try {
    await readFile(join(work, 'ready'));
    break;
  } catch {
    assert.ok(Date.now() < end, 'controlled_service_start_failed');
    await new Promise((r) => setTimeout(r, 50));
  }
}
core.setOutput('config', join(work, 'config.json'));
core.setOutput('preload', '--import ' + preload);
