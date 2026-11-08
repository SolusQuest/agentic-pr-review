// Trusted exact-head construction followed by the literal managed Action proof.
// The producer needs the SDK and an already verified package cache. The consumer
// driver supplies a separate closed synthetic environment to the generated Action.
import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import {
  chmod,
  lstat,
  mkdtemp,
  readFile,
  readdir,
  realpath,
  rmdir,
  rm,
  writeFile,
} from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { qualifyManagedArchivePair } from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/verify-archive.mjs';
import { build } from 'esbuild';

assert.equal(process.platform, 'linux');
assert.equal(process.arch, 'x64');
const preview =
  process.argv.length === 3 && process.argv[2] === '--test-only-original-runtime-preview';
const repo = preview
  ? await realpath(fileURLToPath(new URL('../../', import.meta.url)))
  : resolve(process.argv[2] ?? process.cwd());
const source = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repo }).toString().trim();
assert.equal(
  execFileSync('git', ['status', '--porcelain', '--untracked-files=no'], { cwd: repo }).toString(),
  '',
);
if (preview) {
  try {
    await runOriginalPreview();
  } catch {
    process.stderr.write('APR_TEST_ONLY_ORIGINAL_PREVIEW_FAILED\n');
    process.exitCode = 1;
  }
} else {
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
}

// This default-off manual route never reads a desktop/global credential. The
// normal job token is confined to the existing private cache preparation child;
// provider/state inputs are absent from every build/qualification child env.
async function runOriginalPreview() {
  assert.equal(process.env.GITHUB_REPOSITORY, 'SolusQuest/agentic-pr-review-sandbox');
  assert.equal(process.env.GITHUB_REPOSITORY_ID, '1383960589');
  assert.equal(process.env.GITHUB_JOB, 'review');
  assert.equal(process.env.GITHUB_RUN_ATTEMPT, '1');
  assert.match(process.env.GITHUB_RUN_ID ?? '', /^[1-9][0-9]{0,15}$/u);
  assert.match(process.env.GITHUB_WORKFLOW_SHA ?? '', /^[a-f0-9]{40}$/u);
  assert.equal(
    process.env.GITHUB_WORKFLOW_REF,
    'SolusQuest/agentic-pr-review-sandbox/.github/workflows/agentic-pr-review.yml@refs/heads/main',
  );
  assert.notEqual(source, process.env.GITHUB_WORKFLOW_SHA);
  const eventBytes = await readFile(process.env.GITHUB_EVENT_PATH);
  assert.ok(eventBytes.length > 0 && eventBytes.length <= 262144);
  const event = JSON.parse(eventBytes.toString('utf8'));
  assert.equal(event.sender?.id, 16307884);
  assert.equal(event.repository?.id, 1383960589);
  assert.equal(event.inputs?.['pr-number'], '3');
  assert.equal(typeof event.inputs?.request, 'string');
  const request = JSON.parse(event.inputs.request);
  assert.equal(request.kind, 'apr.test-only.original-runtime.pair.v1');
  assert.ok(['pr', 'main'].includes(request.role));
  assert.equal(request.sourceCommit, source);
  assert.equal(
    request.sourceTree,
    execFileSync('/usr/bin/git', ['rev-parse', 'HEAD^{tree}'], {
      cwd: repo,
      encoding: 'utf8',
    }).trim(),
  );
  assert.equal(request.workflowCommit, process.env.GITHUB_WORKFLOW_SHA);
  assert.match(request.reviewBaseSha ?? '', /^[a-f0-9]{40}$/u);
  const { setSecret } = await import('@actions/core');
  for (const name of ['GITHUB_TOKEN', 'DEEPSEEK_API_KEY', 'AGENTIC_PR_REVIEW_STATE_KEY']) {
    assert.ok(process.env[name]);
    setSecret(process.env[name]);
  }
  const get = async (endpoint) => {
    const response = await fetch(
      'https://api.github.com/repos/SolusQuest/agentic-pr-review-sandbox/' + endpoint,
      {
        method: 'GET',
        redirect: 'error',
        signal: AbortSignal.timeout(30_000),
        headers: {
          Authorization: 'Bearer ' + process.env.GITHUB_TOKEN,
          Accept: 'application/vnd.github+json',
          'X-GitHub-Api-Version': '2022-11-28',
          'User-Agent': 'APR-TEST-ONLY-original-preview',
        },
      },
    );
    assert.equal(response.status, 200);
    assert.ok(response.body);
    const reader = response.body.getReader();
    const chunks = [];
    let count = 0;
    try {
      while (true) {
        const part = await reader.read();
        if (part.done) break;
        count += part.value.byteLength;
        assert.ok(count <= 262144);
        chunks.push(part.value);
      }
    } finally {
      await reader.cancel().catch(() => {});
      reader.releaseLock();
    }
    return JSON.parse(Buffer.concat(chunks).toString('utf8'));
  };
  const actual = await get('actions/runs/' + process.env.GITHUB_RUN_ID);
  assert.equal(String(actual.id), process.env.GITHUB_RUN_ID);
  assert.equal(actual.run_attempt, 1);
  assert.equal(actual.workflow_id, 410349377);
  assert.equal(actual.event, 'workflow_dispatch');
  assert.equal(actual.head_branch, 'main');
  assert.equal(actual.head_sha, request.workflowCommit);
  assert.equal(actual.repository?.id, 1383960589);
  assert.equal(actual.head_repository?.id, 1383960589);
  assert.equal(actual.actor?.id, 16307884);
  assert.equal(actual.triggering_actor?.id, 16307884);
  assert.equal(actual.actor?.login, process.env.GITHUB_ACTOR);
  const declared = await readFile(
    join(repo, 'runtime/tests/ActionHostTrustedProofPayload/TrustedProofV2WorkflowAdmission.cs'),
    'utf8',
  );
  const match = /private const string Template = """\n([\s\S]*?)\n""" \+ "\\n";/u.exec(declared);
  assert.ok(match);
  const rendered = match[1].replaceAll('__APR_SOURCE__', source) + '\n';
  assert.equal(createHash('sha256').update(rendered).digest('hex'), request.workflowTemplateSha256);
  const workflow = await get(
    'contents/.github/workflows/agentic-pr-review.yml?ref=' + request.workflowCommit,
  );
  assert.equal(workflow.encoding, 'base64');
  assert.equal(Buffer.from(workflow.content, 'base64').toString('utf8'), rendered);
  const posted = Date.parse(request.window?.postedAtUtc),
    until = Date.parse(request.window?.validUntilUtc);
  assert.ok(Number.isSafeInteger(posted) && Number.isSafeInteger(until));
  assert.ok(
    posted <= Date.now() &&
      Date.parse(actual.created_at) + 1000 >= posted &&
      Date.parse(actual.created_at) - posted <= 300000,
  );
  const expiries = request.window?.storageExpiries;
  assert.ok(Array.isArray(expiries) && expiries.length === 4);
  assert.deepEqual(
    expiries.map((item) => item.id).sort((a, b) => a - b),
    [13474437693, 13474377959, 13474631449, 13483914599].sort((a, b) => a - b),
  );
  assert.ok(
    until > Date.now() &&
      until <=
        Math.min(
          posted + 21600000,
          Date.parse('2026-11-10T03:10:06.000Z'),
          ...expiries.map((item) => Date.parse(item.expiresAtUtc) - 72 * 3600000),
        ),
  );
  assert.ok(posted <= until - 3600000);
  const temporary = await realpath(process.env.RUNNER_TEMP);
  assert.equal(temporary, process.env.RUNNER_TEMP);
  const parent = await mkdtemp(join(temporary, 'apr-original-preview-'));
  await chmod(parent, 0o700);
  const identity = await lstat(parent);
  // Actual future ACL/read permission is separately held. This fixed restore
  // uses the original exact lock/hash policy; it is not registry-readback or a repeated test campaign.
  const cacheParent = await mkdtemp(join(temporary, 'apr-preview-cache-'));
  await chmod(cacheParent, 0o700);
  const cacheIdentity = await lstat(cacheParent);
  const cache = join(cacheParent, 'verified');
  process.env.EXPECTED_HEAD = source;
  process.env.WORKFLOW_HEAD = request.workflowCommit;
  let result;
  let unresolved = false;
  try {
    await preparePreviewCache(cache, temporary, until);
    process.env.NUGET_PACKAGES = join(cache, 'packages');
    result = await qualifyManagedArchivePair({
      custodyParent: parent,
      consumeOriginalPreview: true,
    });
    assert.equal(result?.pairComplete, true);
  } catch (error) {
    unresolved = error?.code === 'APR_TEST_ONLY_ORIGINAL_OWNERSHIP_UNKNOWN';
    throw error;
  } finally {
    if (!unresolved) {
      const current = await lstat(parent);
      assert.equal(await realpath(parent), parent);
      assert.equal(current.uid, process.getuid());
      assert.equal(current.dev, identity.dev);
      assert.equal(current.ino, identity.ino);
      // Empty-only removal: an unknown retained child/storage never becomes false cleanup.
      await rmdir(parent);
      const cacheCurrent = await lstat(cacheParent);
      assert.equal(await realpath(cacheParent), cacheParent);
      assert.ok(cacheCurrent.isDirectory() && !cacheCurrent.isSymbolicLink());
      assert.equal(cacheCurrent.uid, process.getuid());
      assert.equal(cacheCurrent.dev, cacheIdentity.dev);
      assert.equal(cacheCurrent.ino, cacheIdentity.ino);
      await rm(cacheParent, { recursive: true, force: false });
    }
  }
  process.stdout.write('APR_TEST_ONLY_ORIGINAL_PAIR ' + JSON.stringify(result) + '\n');
}

async function preparePreviewCache(cache, temporary, until) {
  const { mkdir } = await import('node:fs/promises');
  await mkdir(cache, { mode: 0o700 });
  for (const name of ['packages', 'home', 'tmp', 'http', 'plugins'])
    await mkdir(join(cache, name), { mode: 0o700 });
  const lock = join(cache, 'checked.lock.json');
  const checked = join(repo, 'runtime/src/AgenticPrReview.Runtime/packages.managed.lock.json');
  const originalLock = await readFile(checked);
  await writeFile(lock, originalLock, { flag: 'wx', mode: 0o600 });
  const environment = {
    PATH: process.env.PATH,
    DOTNET_ROOT: process.env.DOTNET_ROOT,
    HOME: join(cache, 'home'),
    DOTNET_CLI_HOME: join(cache, 'home'),
    TMPDIR: join(cache, 'tmp'),
    NUGET_PACKAGES: join(cache, 'packages'),
    NUGET_HTTP_CACHE_PATH: join(cache, 'http'),
    NUGET_PLUGINS_CACHE_PATH: join(cache, 'plugins'),
    DOTNET_NOLOGO: '1',
    DOTNET_CLI_TELEMETRY_OPTOUT: '1',
    MSBUILDDISABLENODEREUSE: '1',
    DOTNET_CLI_USE_MSBUILD_SERVER: '0',
    NuGetPackageSourceCredentials_solus:
      'Username=' +
      process.env.GITHUB_ACTOR +
      ';Password=' +
      process.env.GITHUB_TOKEN +
      ';ValidAuthenticationTypes=Basic',
  };
  assert.ok(await realpath(cache).then((path) => path.startsWith(temporary + '/')));
  const args = [
    'restore',
    'runtime/src/AgenticPrReview.Runtime/AgenticPrReview.Runtime.csproj',
    '-r',
    'linux-x64',
    '--no-http-cache',
    '--locked-mode',
    '--disable-parallel',
    '--configfile',
    'scripts/release/build-payload.managed.nuget.config',
    '-p:APRManagedPackage=true',
    '-p:RestoreLockedMode=true',
    '-p:NuGetLockFilePath=' + lock,
    '-p:NuGetAudit=false',
    '-p:RestoreFallbackFolders=',
    '-p:UseSharedCompilation=false',
    '-nodeReuse:false',
    '-m:1',
  ];
  const child = spawn('dotnet', args, {
    cwd: repo,
    env: environment,
    detached: true,
    stdio: ['ignore', 'pipe', 'pipe'],
    shell: false,
  });
  const stopAt = Math.min(Date.now() + 300000, until - 180000);
  await settleOriginalPreviewPreparationChild(child, stopAt, Math.min(until, stopAt + 180000));
  assert.ok((await readFile(lock)).equals(originalLock));
  const assets = JSON.parse(
    await readFile(
      join(repo, 'runtime/src/AgenticPrReview.Runtime/obj/managed/project.assets.json'),
      'utf8',
    ),
  );
  assert.deepEqual(
    Object.keys(assets.packageFolders).map((path) => resolve(path)),
    [resolve(environment.NUGET_PACKAGES)],
  );
  const { managedDependencyPolicy, verifyDependencies } =
    await import('../../scripts/release/build-payload.mjs');
  const policy = managedDependencyPolicy(
    JSON.parse(await readFile(join(repo, 'scripts/release/build-payload.inputs.json'), 'utf8')),
  );
  await verifyDependencies(environment.NUGET_PACKAGES, policy, assets);
}

async function beforeOriginalPreviewDeadline(pending, deadline) {
  let timer;
  try {
    return await Promise.race([
      pending,
      new Promise((_, reject) => {
        timer = setTimeout(
          () =>
            reject(
              Object.assign(new Error('test_only_original_ownership_unknown'), {
                code: 'APR_TEST_ONLY_ORIGINAL_OWNERSHIP_UNKNOWN',
              }),
            ),
          Math.max(0, deadline - Date.now()),
        );
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}
/** Settle this already-spawned detached preparation child only. Never selects an executor. */
async function settleOriginalPreviewPreparationChild(child, stopAt, hardAt) {
  let closed = false,
    code = null,
    signal = null;
  let failed = false,
    stopped = false,
    bytes = 0;
  let naturalCloseUntil;
  let observedNaturalTail = false;
  let members = new Map();
  const interrupt = () => {
    failed = true;
  };
  process.once('SIGINT', interrupt);
  process.once('SIGTERM', interrupt);
  child.once('error', () => {
    failed = true;
  });
  child.once('close', (exit, exitSignal) => {
    closed = true;
    code = exit;
    signal = exitSignal;
  });
  child.once('exit', (exit, exitSignal) => {
    if (exit !== 0 || exitSignal !== null) failed = true;
    else if (!failed) naturalCloseUntil = Math.min(stopAt, hardAt, Date.now() + 2000);
  });
  for (const stream of [child.stdout, child.stderr]) {
    stream?.on('error', () => {
      failed = true;
    });
    stream?.on('data', (chunk) => {
      bytes += chunk.byteLength;
      if (bytes > 1024 * 1024) failed = true;
    });
  }
  const groupAbsent = () => {
    if (!child.pid) return closed;
    try {
      process.kill(-child.pid, 0);
      return false;
    } catch (error) {
      return error.code === 'ESRCH';
    }
  };
  const member = async (pid) => {
    try {
      const named = '/proc/' + pid;
      const [identity, raw] = await Promise.all([lstat(named), readFile(named + '/stat', 'utf8')]);
      const fields = raw
        .slice(raw.lastIndexOf(')') + 2)
        .trim()
        .split(/\s+/);
      if (
        identity.uid !== process.getuid?.() ||
        Number(fields[2]) !== child.pid ||
        Number(fields[3]) !== child.pid
      )
        return;
      return fields[19]; // Linux stat field22 starttime, after removing pid/comm.
    } catch {
      return;
    }
  };
  try {
    // Anchor the original leader while it is live. Later descendant stops require
    // an unchanged original-session member, not a recycled numeric PGID.
    if (child.pid) {
      const birth = await beforeOriginalPreviewDeadline(member(child.pid), hardAt);
      if (birth) members.set(child.pid, birth);
    }
    while (Date.now() < hardAt) {
      if (!members.size && child.pid && child.exitCode === null && child.signalCode === null) {
        const birth = await beforeOriginalPreviewDeadline(member(child.pid), hardAt);
        if (birth) members.set(child.pid, birth);
      }
      const absent = groupAbsent();
      const naturalExit =
        child.exitCode === 0 && child.signalCode === null && naturalCloseUntil !== undefined;
      if (!failed && naturalExit && !absent) observedNaturalTail = true;
      if (
        Date.now() >= stopAt ||
        (observedNaturalTail && naturalCloseUntil !== undefined && Date.now() >= naturalCloseUntil)
      )
        failed = true;
      if (closed && absent) {
        if (failed || code !== 0 || signal !== null)
          assert.fail('test_only_original_preparation_failed');
        return;
      }
      let anchored = false;
      const next = new Map();
      if (members.size) {
        const names = await beforeOriginalPreviewDeadline(
          readdir('/proc').catch(() => []),
          hardAt,
        );
        const table = await beforeOriginalPreviewDeadline(
          Promise.all(
            names
              .filter((name) => /^[1-9][0-9]*$/.test(name))
              .map(async (name) => {
                const pid = Number(name),
                  birth = await member(pid);
                return { pid, birth };
              }),
          ),
          hardAt,
        );
        anchored = table.some(
          (item) => item.birth !== undefined && members.get(item.pid) === item.birth,
        );
        if (anchored) for (const item of table) if (item.birth) next.set(item.pid, item.birth);
        members = next;
      }
      if (
        !stopped &&
        (failed ||
          Date.now() >= stopAt ||
          ((closed || child.exitCode !== null || child.signalCode !== null) &&
            !absent &&
            !(
              naturalExit &&
              !failed &&
              naturalCloseUntil !== undefined &&
              Date.now() < naturalCloseUntil
            )))
      ) {
        stopped = true;
        failed = true;
        try {
          if (child.pid && anchored) process.kill(-child.pid, 'SIGKILL');
          else if (child.pid && child.exitCode === null && child.signalCode === null)
            child.kill('SIGKILL');
          // Parent already exited with no unchanged anchor: group identity is unknown.
        } catch {
          /* A signal is never an exit/closure fact. */
        }
      }
      await new Promise((resolve) =>
        setTimeout(resolve, Math.max(0, Math.min(20, hardAt - Date.now()))),
      );
    }
    if (
      Date.now() >= stopAt ||
      (observedNaturalTail && naturalCloseUntil !== undefined && Date.now() >= naturalCloseUntil)
    )
      failed = true;
    if (closed && groupAbsent()) {
      if (failed || code !== 0 || signal !== null)
        assert.fail('test_only_original_preparation_failed');
      return;
    }

    throw Object.assign(new Error('test_only_original_ownership_unknown'), {
      code: 'APR_TEST_ONLY_ORIGINAL_OWNERSHIP_UNKNOWN',
    });
  } catch (error) {
    if (
      error?.code === 'APR_TEST_ONLY_ORIGINAL_OWNERSHIP_UNKNOWN' &&
      !stopped &&
      child.pid &&
      child.exitCode === null &&
      child.signalCode === null
    ) {
      stopped = true;
      try {
        child.kill('SIGKILL');
      } catch {
        /* Unknown remains retained. */
      }
    }
    throw error;
  } finally {
    process.removeListener('SIGINT', interrupt);
    process.removeListener('SIGTERM', interrupt);
  }
}
