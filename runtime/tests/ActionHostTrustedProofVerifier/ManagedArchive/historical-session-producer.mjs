import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { chmod, mkdtemp, mkdir, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

// S5 #409's sole historical producer exception. This source is never patched,
// linked into the current runtime, or used as current-package qualification.
export const historicalSource = 'eb2d4d0a0380cf6083fa6dc8f9ebee8c904dc4e2';
export const historicalTree = '484c40c6aae3087428fb5169a18685eb744aa590';
const publicRepository = 'https://github.com/SolusQuest/agentic-pr-review.git';

function git(args, cwd) {
  return spawnSync('git', args, { cwd, timeout: 180_000, maxBuffer: 32 * 1024 * 1024 });
}

export async function prepareHistoricalSessionProducer(root, repo, builderEnv, run) {
  const temporary = await mkdtemp(join(tmpdir(), 'apr-s5-historical-'));
  await chmod(temporary, 0o700);
  assert.equal((await stat(temporary)).mode & 0o777, 0o700);
  let sourceRemoved = false;
  let acquisition = 'existing-local-object';
  const outputs = [
    ['cutover-historical', 'LiveAgentVerifierFixture'],
    ['cutover-pinned-action', 'SolusProductionCutoverFixture'],
  ];
  try {
    let objectRepo = repo;
    const identity = git(['rev-parse', historicalSource + '^{tree}'], repo);
    if (identity.status !== 0) {
      // A depth-one CI checkout may lack the immutable public predecessor.
      // Fetch only into our temporary object database, never the user's repo.
      objectRepo = join(temporary, 'objects.git');
      assert.equal(git(['init', '--bare', objectRepo], temporary).status, 0);
      const fetched = git(['fetch', '--depth=1', publicRepository, historicalSource], objectRepo);
      assert.equal(fetched.status, 0, 's5_historical_public_pin_unavailable');
      acquisition = 'public-exact-pin-fetch';
    }
    assert.equal(
      git(['rev-parse', historicalSource + '^{tree}'], objectRepo)
        .stdout.toString()
        .trim(),
      historicalTree,
    );
    const archive = git(['archive', '--format=tar', historicalSource], objectRepo);
    assert.equal(archive.status, 0, 's5_historical_archive_failed');
    const source = join(temporary, 'source');
    await mkdir(source, { mode: 0o700 });
    const extracted = spawnSync('/usr/bin/tar', ['-xf', '-', '-C', source], {
      input: archive.stdout,
      timeout: 30_000,
      maxBuffer: 8192,
    });
    assert.equal(extracted.status, 0, 's5_historical_extract_failed');
    for (const [name, project] of outputs) {
      run(
        'dotnet',
        [
          'publish',
          join(source, `runtime/tests/${project}/AgenticPrReview.Runtime.${project}.csproj`),
          '-c',
          'Release',
          '-r',
          'linux-x64',
          '--artifacts-path',
          join(temporary, 'artifacts-' + name),
          '-o',
          join(root, name),
          '-p:PublishAot=false',
          '-p:SelfContained=false',
          '-p:UseAppHost=false',
          '-p:UseSharedCompilation=false',
          '-p:NuGetAudit=false',
          '-p:RestoreSources=https://api.nuget.org/v3/index.json',
        ],
        { cwd: source, env: { ...builderEnv, NUGET_PACKAGES: process.env.NUGET_PACKAGES } },
      );
    }
  } finally {
    await rm(temporary, { recursive: true, force: true });
    sourceRemoved = await stat(temporary).then(
      () => false,
      (error) => {
        if (error.code !== 'ENOENT') throw error;
        return true;
      },
    );
    assert.equal(sourceRemoved, true, 's5_historical_source_cleanup_failed');
  }
  const assemblies = [];
  for (const [name, project] of outputs) {
    const bytes = await readFile(join(root, name, `AgenticPrReview.Runtime.${project}.dll`));
    assemblies.push({ role: name, sha256: createHash('sha256').update(bytes).digest('hex') });
  }
  await writeFile(
    join(root, 's5-historical-producer.json'),
    JSON.stringify({
      kind: 'apr-s5-immutable-historical-producer-v1',
      source_commit: historicalSource,
      source_tree: historicalTree,
      acquisition,
      source_modified: false,
      temporary_source_removed: sourceRemoved,
      assemblies,
      purpose: 'genuine old SESSION negative input only',
      current_package_qualification: false,
    }),
    { mode: 0o600, flag: 'wx' },
  );
}
