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
  readlink,
  readdir,
  realpath,
  rmdir,
  rm,
  writeFile,
} from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  qualifyManagedArchivePair,
  requireOriginalPreviewRouteBudget,
  ORIGINAL_PREVIEW_CACHE_M1_MS,
} from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/verify-archive.mjs';
import { build } from 'esbuild';

const preview =
  process.argv.length === 3 && process.argv[2] === '--test-only-original-runtime-preview';
const privateManagedRegression =
  process.argv.length === 3 && process.argv[2] === '--test-only-private-managed-regression';
let repo, source;
let privateManagedRegressionSetupFailed = false;
try {
  assert.equal(process.platform, 'linux');
  assert.equal(process.arch, 'x64');
  repo =
    preview || privateManagedRegression
      ? await realpath(fileURLToPath(new URL('../../', import.meta.url)))
      : resolve(process.argv[2] ?? process.cwd());
  source = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repo }).toString().trim();
  assert.equal(
    execFileSync('git', ['status', '--porcelain', '--untracked-files=no'], {
      cwd: repo,
    }).toString(),
    '',
  );
} catch (error) {
  if (!privateManagedRegression) throw error;
  process.stderr.write(
    'APR_TEST_ONLY_PRIVATE_MANAGED_REGRESSION_FAILED stage=admission settlement=unknown\n',
  );
  process.exitCode = 1;
  privateManagedRegressionSetupFailed = true;
}
let originalPreviewStage = 'unknown';
const originalPreviewSettlementFacts = new WeakMap();
let privateManagedRegressionCustody;
if ((preview || privateManagedRegression) && !privateManagedRegressionSetupFailed) {
  try {
    if (privateManagedRegression) await runPrivateManagedRegression();
    else await runOriginalPreview();
  } catch (error) {
    const stage = [
      'admission',
      'cache',
      'cache-restore',
      'cache-lock-assets-policy',
      'm1',
      'm1-archive',
      'm1-restore',
      'm1-sdk-info',
      'm1-test',
      'm1-receipt-counts',
      'm1-receipt-path',
      'm1-receipt-xml',
      'm1-receipt-identity',
      'm1-receipt-assemblies',
      'm1-receipt-policy',
      'm1-receipt-source',
      'm1-receipt-summary',
      'qualification-preview',
      'cleanup',
    ].includes(originalPreviewStage)
      ? originalPreviewStage
      : 'unknown';
    const fact = originalPreviewSettlementFacts.get(error);
    const settlement = ['settled-failure', 'ownership-unknown'].includes(fact) ? fact : 'unknown';
    const marker = privateManagedRegression
      ? 'APR_TEST_ONLY_PRIVATE_MANAGED_REGRESSION_FAILED'
      : 'APR_TEST_ONLY_ORIGINAL_PREVIEW_FAILED';
    process.stderr.write(marker + ' stage=' + stage + ' settlement=' + settlement + '\n');
    process.exitCode = 1;
  }
} else if (!privateManagedRegressionSetupFailed) {
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
async function runPrivateManagedRegression() {
  originalPreviewStage = 'admission';
  assert.equal(process.env.GITHUB_REPOSITORY, 'SolusQuest/agentic-pr-review');
  assert.equal(process.env.GITHUB_REPOSITORY_ID, '1263978368');
  assert.equal(process.env.GITHUB_EVENT_NAME, 'workflow_dispatch');
  assert.equal(process.env.GITHUB_JOB, 'install-proof');
  assert.equal(process.env.GITHUB_RUN_ATTEMPT, '1');
  assert.match(process.env.GITHUB_RUN_ID ?? '', /^[1-9][0-9]{0,15}$/u);
  assert.match(source, /^[a-f0-9]{40}$/u);
  for (const key of ['EXPECTED_HEAD', 'WORKFLOW_HEAD', 'GITHUB_SHA', 'GITHUB_WORKFLOW_SHA'])
    assert.equal(process.env[key], source);
  const ref = 'refs/heads/codex/r7-v4-premerge-validation-361';
  const workflowPath = '.github/workflows/solus-agent-package-proof.yml';
  assert.equal(process.env.GITHUB_REF, ref);
  assert.equal(
    process.env.GITHUB_WORKFLOW_REF,
    'SolusQuest/agentic-pr-review/' + workflowPath + '@' + ref,
  );
  for (const key of ['DEEPSEEK_API_KEY', 'AGENTIC_PR_REVIEW_STATE_KEY'])
    assert.equal(Object.hasOwn(process.env, key), false);
  assert.ok(process.env.GITHUB_TOKEN);
  const times = [process.env.PREPARATION_POSTED_AT_UTC, process.env.PREPARATION_VALID_UNTIL_UTC];
  for (const value of times) {
    assert.match(value, /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/u);
    assert.equal(new Date(value).toISOString(), value);
  }
  const [posted, until] = times.map(Date.parse);
  assert.ok(
    posted <= Date.now() &&
      until > posted &&
      until - posted <= 900000 &&
      until <= Date.parse('2026-11-10T03:10:06.000Z'),
  );
  if (until - Date.now() < 660000) {
    process.stdout.write('APR_TEST_ONLY_PRIVATE_MANAGED_REGRESSION_NO_PRIVATE_START\n');
    process.exitCode = 1;
    return;
  }
  const custody = { root: undefined, identity: undefined, pending: undefined };
  privateManagedRegressionCustody = custody;
  custody.pending = import('@actions/core');
  const { setSecret } = await beforeOriginalPreviewDeadline(custody.pending, until);
  setSecret(process.env.GITHUB_TOKEN);
  custody.pending = readFile(process.env.GITHUB_EVENT_PATH);
  const eventBytes = await beforeOriginalPreviewDeadline(custody.pending, until);
  custody.pending = undefined;
  assert.ok(eventBytes.length > 0 && eventBytes.length <= 262144);
  const event = JSON.parse(eventBytes.toString('utf8'));
  assert.equal(event.repository?.id, 1263978368);
  assert.equal(event.sender?.id, 16307884);
  assert.equal(event.ref, ref);
  assert.deepEqual(Object.keys(event.inputs ?? {}).sort(), [
    'expected-head-sha',
    'mode',
    'preparation-posted-at-utc',
    'preparation-valid-until-utc',
  ]);
  assert.equal(event.inputs['expected-head-sha'], source);
  assert.equal(event.inputs.mode, 'private-managed-regression');
  assert.equal(event.inputs['preparation-posted-at-utc'], times[0]);
  assert.equal(event.inputs['preparation-valid-until-utc'], times[1]);
  const get = async (endpoint) => {
    const remaining = until - Date.now();
    assert.ok(remaining > 0);
    const response = await fetch(
      'https://api.github.com/repos/SolusQuest/agentic-pr-review/' + endpoint,
      {
        method: 'GET',
        redirect: 'error',
        signal: AbortSignal.timeout(Math.min(30000, remaining)),
        headers: {
          Authorization: 'Bearer ' + process.env.GITHUB_TOKEN,
          Accept: 'application/vnd.github+json',
          'X-GitHub-Api-Version': '2022-11-28',
          'User-Agent': 'APR-TEST-ONLY-private-managed-regression',
        },
      },
    );
    assert.equal(response.status, 200);
    assert.ok(response.body);
    const reader = response.body.getReader();
    const chunks = [];
    let bytes = 0;
    try {
      while (true) {
        const part = await reader.read();
        if (part.done) break;
        bytes += part.value.byteLength;
        assert.ok(bytes <= 262144);
        chunks.push(Buffer.from(part.value));
      }
    } finally {
      await reader.cancel().catch(() => {});
      reader.releaseLock();
    }
    return JSON.parse(Buffer.concat(chunks).toString('utf8'));
  };
  custody.pending = get('actions/runs/' + process.env.GITHUB_RUN_ID);
  const actual = await beforeOriginalPreviewDeadline(custody.pending, until);
  custody.pending = undefined;
  assert.equal(String(actual.id), process.env.GITHUB_RUN_ID);
  assert.equal(actual.run_attempt, 1);
  assert.equal(actual.workflow_id, 386432796);
  assert.equal(actual.name, 'SolusAgent APR managed package proof');
  assert.equal(actual.event, 'workflow_dispatch');
  assert.equal(actual.status, 'in_progress');
  assert.equal(actual.head_branch, ref.slice('refs/heads/'.length));
  assert.equal(actual.head_sha, source);
  assert.equal(actual.head_commit?.id, source);
  const tree = execFileSync('/usr/bin/git', ['rev-parse', 'HEAD^{tree}'], {
    cwd: repo,
    encoding: 'utf8',
  }).trim();
  assert.match(tree, /^[a-f0-9]{40}$/u);
  assert.equal(actual.head_commit?.tree_id, tree);
  assert.equal(actual.repository?.id, 1263978368);
  assert.equal(actual.head_repository?.id, 1263978368);
  assert.equal(actual.repository?.full_name, 'SolusQuest/agentic-pr-review');
  assert.equal(actual.head_repository?.full_name, 'SolusQuest/agentic-pr-review');
  assert.equal(actual.actor?.id, 16307884);
  assert.equal(actual.triggering_actor?.id, 16307884);
  assert.equal(actual.actor?.login, process.env.GITHUB_ACTOR);
  assert.ok(
    Number.isSafeInteger(Date.parse(actual.created_at)) &&
      Date.parse(actual.created_at) + 1000 >= posted &&
      Date.parse(actual.created_at) <= Date.now() + 1000 &&
      Date.parse(actual.created_at) - posted <= 300000,
  );
  custody.pending = get('actions/workflows/386432796');
  const workflow = await beforeOriginalPreviewDeadline(custody.pending, until);
  custody.pending = undefined;
  assert.equal(workflow.id, 386432796);
  assert.equal(workflow.path, workflowPath);
  assert.equal(workflow.name, 'SolusAgent APR managed package proof');
  assert.equal(workflow.state, 'active');
  custody.pending = get('contents/' + workflowPath + '?ref=' + source);
  const declared = await beforeOriginalPreviewDeadline(custody.pending, until);
  assert.equal(declared.encoding, 'base64');
  custody.pending = readFile(join(repo, workflowPath));
  const localWorkflow = await beforeOriginalPreviewDeadline(custody.pending, until);
  custody.pending = undefined;
  assert.ok(localWorkflow.length > 0 && localWorkflow.length <= 65536);
  assert.ok(Buffer.from(declared.content, 'base64').equals(localWorkflow));
  custody.pending = realpath(process.env.RUNNER_TEMP);
  const temporary = await beforeOriginalPreviewDeadline(custody.pending, until);
  custody.pending = undefined;
  assert.equal(temporary, process.env.RUNNER_TEMP);
  if (until - Date.now() < 660000) {
    privateManagedRegressionCustody = undefined;
    process.stdout.write('APR_TEST_ONLY_PRIVATE_MANAGED_REGRESSION_NO_PRIVATE_START\n');
    process.exitCode = 1;
    return;
  }
  const stopAt = Math.min(Date.now() + 300000, until - 180000);
  const hardAt = Math.min(until, stopAt + 180000);
  let unresolved = false;
  let failureStage;
  originalPreviewStage = 'cache';
  try {
    custody.pending = mkdtemp(join(temporary, 'apr-m1-cache-'));
    custody.root = await beforeOriginalPreviewDeadline(custody.pending, hardAt);
    custody.pending = chmod(custody.root, 0o700);
    await beforeOriginalPreviewDeadline(custody.pending, hardAt);
    custody.pending = lstat(custody.root);
    custody.identity = await beforeOriginalPreviewDeadline(custody.pending, hardAt);
    custody.pending = realpath(custody.root);
    assert.equal(await beforeOriginalPreviewDeadline(custody.pending, hardAt), custody.root);
    assert.ok(custody.root.startsWith(temporary + '/'));
    assert.ok(custody.identity.isDirectory() && !custody.identity.isSymbolicLink());
    assert.equal(custody.identity.uid, process.getuid());
    assert.equal(custody.identity.mode & 0o777, 0o700);
    assert.ok(Date.now() < stopAt);
    custody.pending = preparePreviewCache(
      join(custody.root, 'verified'),
      temporary,
      stopAt,
      hardAt,
    );
    const environment = await beforeOriginalPreviewDeadline(custody.pending, hardAt);
    originalPreviewStage = 'm1';
    assert.ok(Date.now() < stopAt);
    custody.pending = verifyOriginalManagedRegression(
      join(custody.root, 'verified'),
      environment,
      stopAt,
      hardAt,
    );
    await beforeOriginalPreviewDeadline(custody.pending, hardAt);
    custody.pending = undefined;
    assert.ok(Date.now() < hardAt);
  } catch (error) {
    failureStage = originalPreviewStage;
    unresolved = error?.code === 'APR_TEST_ONLY_ORIGINAL_OWNERSHIP_UNKNOWN';
    throw error;
  } finally {
    if (custody.identity && !unresolved) {
      originalPreviewStage = 'cleanup';
      assert.ok(Date.now() < until);
      custody.pending = lstat(custody.root);
      const current = await beforeOriginalPreviewDeadline(custody.pending, until);
      assert.ok(current.isDirectory() && !current.isSymbolicLink());
      assert.equal(current.uid, custody.identity.uid);
      assert.equal(current.dev, custody.identity.dev);
      assert.equal(current.ino, custody.identity.ino);
      assert.equal(current.mode & 0o777, 0o700);
      assert.ok(Date.now() < until);
      custody.pending = realpath(custody.root);
      assert.equal(await beforeOriginalPreviewDeadline(custody.pending, until), custody.root);
      assert.ok(Date.now() < until);
      custody.pending = rm(custody.root, { recursive: true, force: false });
      await beforeOriginalPreviewDeadline(custody.pending, until);
      assert.ok(Date.now() < until);
      custody.pending = lstat(custody.root);
      let absent = false;
      try {
        await beforeOriginalPreviewDeadline(custody.pending, until);
      } catch (error) {
        if (error?.code !== 'ENOENT') throw error;
        absent = true;
      }
      assert.ok(absent);
      custody.pending = undefined;
      privateManagedRegressionCustody = undefined;
    }
    if (failureStage !== undefined) originalPreviewStage = failureStage;
  }
  assert.equal(privateManagedRegressionCustody, undefined);
  process.stdout.write(
    'APR_TEST_ONLY_PRIVATE_MANAGED_REGRESSION_PASS total=1 executed=1 passed=1 skipped=0 cleanup=confirmed\n',
  );
}

async function runOriginalPreview() {
  originalPreviewStage = 'admission';
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
      Date.parse(actual.created_at) - posted <= 1800000,
  );
  const expiries = request.window?.storageExpiries;
  assert.ok(Array.isArray(expiries) && expiries.length === 4);
  assert.deepEqual(
    expiries.map((item) => item.id).sort((a, b) => a - b),
    [13735372189, 13735427087, 13735412078, 13736025188].sort((a, b) => a - b),
  );
  assert.ok(
    until > Date.now() &&
      until <=
        Math.min(
          posted + 21600000,
          Date.parse('2026-12-10T03:10:06.000Z'),
          ...expiries.map((item) => Date.parse(item.expiresAtUtc) - 72 * 3600000),
        ),
  );
  assert.ok(posted <= until - 3600000);
  requireOriginalPreviewRouteBudget(until, 2, ORIGINAL_PREVIEW_CACHE_M1_MS);
  originalPreviewStage = 'cache';
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
  const preparationStopAt = Math.min(Date.now() + 300000, until - 180000);
  const preparationHardAt = Math.min(until, preparationStopAt + 180000);
  let result;
  let unresolved = false;
  let failureStage;
  try {
    requireOriginalPreviewRouteBudget(until, 2, ORIGINAL_PREVIEW_CACHE_M1_MS);
    const environment = await beforeOriginalPreviewDeadline(
      preparePreviewCache(cache, temporary, preparationStopAt, preparationHardAt),
      preparationHardAt,
    );
    originalPreviewStage = 'm1';
    await beforeOriginalPreviewDeadline(
      verifyOriginalManagedRegression(cache, environment, preparationStopAt, preparationHardAt),
      preparationHardAt,
    );
    requireOriginalPreviewRouteBudget(until, 2);
    process.env.NUGET_PACKAGES = join(cache, 'packages');
    originalPreviewStage = 'qualification-preview';
    result = await qualifyManagedArchivePair({
      custodyParent: parent,
      consumeOriginalPreview: true,
      originalPreviewOuterDeadline: until,
    });
    assert.equal(result?.pairComplete, true);
  } catch (error) {
    failureStage = originalPreviewStage;
    unresolved = error?.code === 'APR_TEST_ONLY_ORIGINAL_OWNERSHIP_UNKNOWN';
    throw error;
  } finally {
    if (!unresolved) {
      originalPreviewStage = 'cleanup';
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
    if (failureStage !== undefined) originalPreviewStage = failureStage;
  }
  process.stdout.write('APR_TEST_ONLY_ORIGINAL_PAIR ' + JSON.stringify(result) + '\n');
}

async function preparePreviewCache(cache, temporary, stopAt, hardAt) {
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
  originalPreviewStage = 'cache-restore';
  assert.ok(Date.now() < stopAt, 'original_preview_preparation_stop_exhausted');
  const child = spawn('dotnet', args, {
    cwd: repo,
    env: environment,
    detached: true,
    stdio: ['ignore', 'pipe', 'pipe'],
    shell: false,
  });
  await settleOriginalPreviewPreparationChild(child, stopAt, hardAt);
  originalPreviewStage = 'cache-lock-assets-policy';
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
  return environment;
}

// Fixed preparation regression only: fresh tracked source, no qualified DLL reuse.
async function verifyOriginalManagedRegression(cache, environment, stopAt, hardAt) {
  const { mkdir, open } = await import('node:fs/promises');
  const { constants } = await import('node:fs');
  const root = join(cache, 'managed-regression');
  await mkdir(root, { mode: 0o700 });
  const rootIdentity = await lstat(root);
  const owned = async (path, maximum) => {
    assert.ok(path.startsWith(root + '/'));
    assert.equal(await realpath(path), path);
    const handle = await open(path, constants.O_RDONLY | constants.O_NOFOLLOW);
    try {
      const identity = await handle.stat();
      assert.ok(identity.isFile() && identity.uid === process.getuid());
      assert.ok(identity.size > 0 && identity.size <= maximum);
      const bytes = await handle.readFile();
      const after = await lstat(path);
      assert.equal(after.dev, identity.dev);
      assert.equal(after.ino, identity.ino);
      assert.equal(bytes.length, identity.size);
      return bytes;
    } finally {
      await handle.close();
    }
  };
  // The original cache-preparation stop/hard times are shared by all children.
  const settle = async (child) => {
    const chunks = [];
    let size = 0;
    child.stdout?.on('data', (chunk) => {
      size += chunk.byteLength;
      if (size <= 1024 * 1024) chunks.push(Buffer.from(chunk));
    });
    await settleOriginalPreviewPreparationChild(child, stopAt, hardAt);
    assert.ok(size <= 1024 * 1024);
    return Buffer.concat(chunks).toString('utf8');
  };
  const closed = { ...environment };
  delete closed.NuGetPackageSourceCredentials_solus;
  const gitEnvironment = {
    ...closed,
    GIT_CONFIG_NOSYSTEM: '1',
    GIT_CONFIG_GLOBAL: '/dev/null',
    GIT_ATTR_NOSYSTEM: '1',
    GIT_TERMINAL_PROMPT: '0',
    GIT_OPTIONAL_LOCKS: '0',
  };
  const gitOptions = [
    '-c',
    'core.hooksPath=/dev/null',
    '-c',
    'core.fsmonitor=false',
    '-c',
    'core.autocrlf=false',
    '-c',
    'core.attributesFile=/dev/null',
    '-c',
    'credential.helper=',
    '-c',
    'uploadpack.packObjectsHook=',
    '-c',
    'protocol.allow=never',
    '-c',
    'protocol.file.allow=always',
  ];
  const git = async (cwd, args) => {
    assert.ok(Date.now() < stopAt);
    return settle(
      spawn('/usr/bin/git', [...gitOptions, ...args], {
        cwd,
        env: gitEnvironment,
        detached: true,
        stdio: ['ignore', 'pipe', 'pipe'],
        shell: false,
      }),
    );
  };
  const snapshot = join(root, 'source');
  assert.equal(await realpath(repo), repo);
  assert.equal((await git(repo, ['rev-parse', 'HEAD'])).trim(), source);
  const sourceTree = (await git(repo, ['rev-parse', 'HEAD^{tree}'])).trim();
  assert.match(sourceTree, /^[a-f0-9]{40}$/u);
  const trackedTree = await git(repo, ['ls-tree', '-rz', '--full-tree', source]);
  const tracked = trackedTree
    .split('\0')
    .filter(Boolean)
    .map((record) => {
      const match = /^(100644|100755|120000|160000) (blob|commit) ([a-f0-9]{40})\t([\s\S]+)$/u.exec(
        record,
      );
      assert.ok(match);
      const [, mode, type, oid, path] = match;
      assert.ok(
        !path.startsWith('/') &&
          path.split('/').every((part) => part && part !== '.' && part !== '..' && part !== '.git'),
      );
      assert.equal(type, mode === '160000' ? 'commit' : 'blob');
      return { mode, oid, path };
    });
  assert.ok(tracked.length > 0);
  // Normal local transport copies objects, never borrows objects/worktree or runs submodules.
  await git(root, [
    'clone',
    '--no-local',
    '--no-hardlinks',
    '--no-checkout',
    '--depth=1',
    '--single-branch',
    '--no-tags',
    '--no-recurse-submodules',
    '--config',
    'core.hooksPath=/dev/null',
    '--config',
    'core.fsmonitor=false',
    '--config',
    'core.autocrlf=false',
    '--config',
    'core.attributesFile=/dev/null',
    '--config',
    'credential.helper=',
    '--',
    repo,
    snapshot,
  ]);
  const snapshotIdentity = await lstat(snapshot);
  const metadata = join(snapshot, '.git');
  const metadataIdentity = await lstat(metadata);
  const validateMetadata = async () => {
    for (const [path, identity] of [
      [root, rootIdentity],
      [snapshot, snapshotIdentity],
      [metadata, metadataIdentity],
    ]) {
      assert.ok(Date.now() < stopAt);
      const current = await lstat(path);
      assert.ok(current.isDirectory() && !current.isSymbolicLink());
      assert.equal(await realpath(path), path);
      assert.equal(current.uid, process.getuid());
      assert.equal(current.dev, identity.dev);
      assert.equal(current.ino, identity.ino);
    }
    // Independent metadata has no symlinks, shared files, or external object stores.
    const metadataFiles = async (path) => {
      assert.ok(Date.now() < stopAt);
      const info = await lstat(path);
      assert.equal(info.uid, process.getuid());
      assert.equal(info.dev, metadataIdentity.dev);
      assert.ok(!info.isSymbolicLink());
      if (info.isDirectory())
        for (const entry of await readdir(path)) await metadataFiles(join(path, entry));
      else {
        assert.ok(info.isFile());
        assert.equal(info.nlink, 1);
        assert.ok(
          !['alternates', 'http-alternates', 'commondir', 'gitdir'].includes(
            path.slice(path.lastIndexOf('/') + 1),
          ),
        );
      }
    };
    await metadataFiles(metadata);
  };
  await validateMetadata();
  await git(snapshot, ['checkout', '--detach', source]);
  assert.equal((await git(repo, ['rev-parse', 'HEAD'])).trim(), source);
  assert.equal((await git(repo, ['rev-parse', 'HEAD^{tree}'])).trim(), sourceTree);
  const validateSource = async () => {
    await validateMetadata();
    assert.equal((await git(snapshot, ['rev-parse', 'HEAD'])).trim(), source);
    assert.equal((await git(snapshot, ['rev-parse', 'HEAD^{tree}'])).trim(), sourceTree);
    assert.equal(await git(snapshot, ['ls-tree', '-rz', '--full-tree', 'HEAD']), trackedTree);
    assert.equal((await git(snapshot, ['write-tree'])).trim(), sourceTree);
    assert.equal(await git(snapshot, ['status', '--porcelain', '--untracked-files=no']), '');
    for (const { mode, oid, path } of tracked) {
      assert.ok(Date.now() < stopAt);
      const file = join(snapshot, path);
      assert.equal(await realpath(join(file, '..')), join(file, '..'));
      const info = await lstat(file);
      assert.equal(info.uid, process.getuid());
      assert.equal(info.dev, snapshotIdentity.dev);
      if (mode === '160000') {
        assert.ok(info.isDirectory() && !info.isSymbolicLink());
        assert.deepEqual(await readdir(file), []); // Uninitialized gitlink, never a borrowed submodule.
        continue;
      }
      let bytes;
      if (mode === '120000') {
        assert.ok(info.isSymbolicLink());
        bytes = await readlink(file, { encoding: 'buffer' });
      } else {
        assert.ok(info.isFile() && !info.isSymbolicLink());
        assert.equal(info.nlink, 1);
        assert.equal(Boolean(info.mode & 0o111), mode === '100755');
        const handle = await open(file, constants.O_RDONLY | constants.O_NOFOLLOW);
        try {
          const opened = await handle.stat();
          assert.ok(opened.isFile() && opened.uid === info.uid && opened.nlink === 1);
          assert.equal(opened.dev, info.dev);
          assert.equal(opened.ino, info.ino);
          assert.ok(opened.size >= 0 && opened.size <= 128 * 1024 * 1024);
          bytes = await handle.readFile();
          assert.equal(bytes.length, opened.size);
        } finally {
          await handle.close();
        }
      }
      assert.equal(
        createHash('sha1')
          .update('blob ' + bytes.length + '\0')
          .update(bytes)
          .digest('hex'),
        oid,
      );
      const after = await lstat(file);
      assert.equal(after.dev, info.dev);
      assert.equal(after.ino, info.ino);
    }
  };
  await validateSource();
  const archive = join(root, 'source.tar');
  originalPreviewStage = 'm1-archive';
  assert.ok(Date.now() < stopAt);
  const output = await open(archive, 'wx', 0o600);
  try {
    await settle(
      spawn('/usr/bin/git', ['archive', '--format=tar', source], {
        cwd: repo,
        env: closed,
        detached: true,
        stdio: ['ignore', output.fd, 'pipe'],
        shell: false,
      }),
    );
  } finally {
    await output.close();
  }
  const archiveDigest = createHash('sha256')
    .update(await owned(archive, 128 * 1024 * 1024))
    .digest('hex');
  assert.ok(Date.now() < stopAt);
  await settle(
    spawn('/usr/bin/tar', ['-xf', archive, '--no-same-owner', '-C', snapshot], {
      cwd: root,
      env: closed,
      detached: true,
      stdio: ['ignore', 'pipe', 'pipe'],
      shell: false,
    }),
  );
  assert.equal(
    createHash('sha256')
      .update(await owned(archive, 128 * 1024 * 1024))
      .digest('hex'),
    archiveDigest,
  );
  await validateSource();
  const checked = 'runtime/src/AgenticPrReview.Runtime/packages.managed.lock.json';
  const originalLock = await readFile(join(repo, checked));
  assert.ok((await owned(join(snapshot, checked), 1024 * 1024)).equals(originalLock));
  const project = 'runtime/tests/AgenticPrReview.Runtime.Tests';
  // Runtime Directory.Build.props alone chooses its checked production lock.
  // Do not globally apply NuGetLockFilePath or locked-mode to the test graph.
  originalPreviewStage = 'm1-restore';
  assert.ok(Date.now() < stopAt);
  await settle(
    spawn(
      'dotnet',
      [
        'restore',
        project,
        '--disable-parallel',
        '--no-http-cache',
        '--configfile',
        join(snapshot, 'scripts/release/build-payload.managed.nuget.config'),
        '-p:APRManagedPackage=true',
        '-p:SelfContained=false',
        '-p:UseAppHost=false',
        '-p:EnableRuntimePackDownload=false',
        '-p:DisableTransitiveFrameworkReferenceDownloads=true',
        '-p:EnableAppHostPackDownload=false',
        '-p:RuntimeIdentifiers=linux-x64',
        '-p:NuGetAudit=false',
        '-p:RestoreFallbackFolders=',
        '-p:UseSharedCompilation=false',
        '-nodeReuse:false',
        '-m:1',
      ],
      {
        cwd: snapshot,
        env: environment,
        detached: true,
        stdio: ['ignore', 'pipe', 'pipe'],
        shell: false,
      },
    ),
  );
  originalPreviewStage = 'm1-sdk-info';
  assert.ok(Date.now() < stopAt);
  const sdk = await settle(
    spawn('dotnet', ['--info'], {
      cwd: snapshot,
      env: closed,
      detached: true,
      stdio: ['ignore', 'pipe', 'pipe'],
      shell: false,
    }),
  );
  const sdkVersion = /\.NET SDK:\s*\n\s*Version:\s*(10\.0\.[0-9]+)\s*\n/u.exec(sdk)?.[1];
  const runtimeVersion = /Host:\s*\n\s*Version:\s*(10\.0\.[0-9]+)\s*\n/u.exec(sdk)?.[1];
  assert.ok(sdkVersion && runtimeVersion);
  await writeFile(join(root, 'sdk-info.txt'), sdk, { mode: 0o600, flag: 'wx' });
  const method =
    'OriginalManagedCompletedObservationRejectsCancelledConcurrentAndAuthoredCompletions';
  const className = 'AgenticPrReview.Runtime.Tests.Host.Action.ActionHostCompositionTests';
  const fullName = className + '.' + method;
  const started = Date.now();
  originalPreviewStage = 'm1-test';
  assert.ok(Date.now() < stopAt);
  const stdout = await settle(
    spawn(
      process.execPath,
      [
        join(snapshot, 'scripts/run-native-tests.mjs'),
        'runtime',
        '--filter',
        'FullyQualifiedName=' + fullName,
        '--no-restore',
        '-p:APRManagedPackage=true',
        '-p:SelfContained=false',
        '-p:UseAppHost=false',
        '-p:EnableRuntimePackDownload=false',
        '-p:DisableTransitiveFrameworkReferenceDownloads=true',
        '-p:EnableAppHostPackDownload=false',
        '-p:UseSharedCompilation=false',
        '-nodeReuse:false',
      ],
      {
        cwd: snapshot,
        env: closed,
        detached: true,
        stdio: ['ignore', 'pipe', 'pipe'],
        shell: false,
      },
    ),
  );
  originalPreviewStage = 'm1-receipt-counts';
  assert.deepEqual(stdout.match(/^Runner counts: .*$/gmu), [
    'Runner counts: total=1 executed=1 passed=1 failed=0 not-executed=0',
  ]);
  originalPreviewStage = 'm1-receipt-path';
  const results = join(snapshot, '.local/native-tests');
  assert.equal(await realpath(results), results);
  const entries = await readdir(results);
  assert.equal(entries.length, 1);
  assert.match(entries[0], /^runtime-[A-Za-z0-9]+$/u);
  const directory = join(results, entries[0]);
  const directoryIdentity = await lstat(directory);
  assert.ok(directoryIdentity.isDirectory() && !directoryIdentity.isSymbolicLink());
  assert.equal(directoryIdentity.uid, process.getuid());
  const trx = join(directory, 'results.trx');
  assert.deepEqual(stdout.match(/^TRX: .*$/gmu), ['TRX: ' + trx]);
  const trxBytes = await owned(trx, 1024 * 1024);
  originalPreviewStage = 'm1-receipt-xml';
  const xml = trxBytes.toString('utf8').replace(/^\uFEFF/u, '');
  assert.ok(!/<!|<\?/u.test(xml.replace(/^<\?xml[^?]*\?>/u, '')));
  const one = (name) => {
    const tags = [...xml.matchAll(new RegExp('<' + name + '\\b([^>]*)>', 'gu'))];
    assert.equal(tags.length, 1);
    const attributes = {};
    const raw = tags[0][1].replace(/\/$/u, '');
    let end = 0;
    for (const match of raw.matchAll(/\s+([A-Za-z][A-Za-z0-9]*)="([^"<>]*)"/gu)) {
      assert.equal(match.index, end);
      assert.equal(attributes[match[1]], undefined);
      assert.ok(!match[2].includes('&'));
      attributes[match[1]] = match[2];
      end = match.index + match[0].length;
    }
    assert.equal(raw.slice(end).trim(), '');
    return attributes;
  };
  const counters = one('Counters');
  for (const key of ['total', 'executed', 'passed']) assert.equal(counters[key], '1');
  for (const [key, value] of Object.entries(counters))
    if (!['total', 'executed', 'passed'].includes(key)) assert.equal(value, '0');
  assert.equal(counters.failed, '0');
  assert.equal(counters.notExecuted, '0');
  originalPreviewStage = 'm1-receipt-identity';
  const result = one('UnitTestResult'),
    test = one('UnitTest'),
    execution = one('Execution'),
    definition = one('TestMethod');
  assert.equal(result.outcome, 'Passed');
  assert.equal(result.testName, fullName);
  assert.equal(test.name, fullName);
  assert.match(result.testId, /^[a-f0-9-]{36}$/u);
  assert.match(result.executionId, /^[a-f0-9-]{36}$/u);
  assert.equal(result.testId, test.id);
  assert.equal(result.executionId, execution.id);
  assert.equal(definition.className, className);
  assert.equal(definition.name, method);
  assert.ok(Date.parse(result.startTime) >= started && Date.parse(result.endTime) <= Date.now());
  originalPreviewStage = 'm1-receipt-assemblies';
  const assembly = join(snapshot, project, 'bin/Debug/net10.0/AgenticPrReview.Runtime.Tests.dll');
  assert.equal(definition.codeBase, assembly);
  const testBytes = await owned(assembly, 64 * 1024 * 1024);
  const runtimeBytes = await owned(
    join(snapshot, project, 'bin/Debug/net10.0/AgenticPrReview.Runtime.dll'),
    64 * 1024 * 1024,
  );
  assert.ok((await owned(join(snapshot, checked), 1024 * 1024)).equals(originalLock));
  assert.ok((await readFile(join(repo, checked))).equals(originalLock));
  originalPreviewStage = 'm1-receipt-policy';
  const assets = JSON.parse(
    (
      await owned(
        join(snapshot, 'runtime/src/AgenticPrReview.Runtime/obj/managed/project.assets.json'),
        16 * 1024 * 1024,
      )
    ).toString('utf8'),
  );
  assert.deepEqual(
    Object.keys(assets.packageFolders).map((path) => resolve(path)),
    [resolve(environment.NUGET_PACKAGES)],
  );
  const { managedDependencyPolicy, verifyManagedRegressionDependencies } =
    await import('../../scripts/release/build-payload.mjs');
  const testAssets = JSON.parse(
    (await owned(join(snapshot, project, 'obj/project.assets.json'), 16 * 1024 * 1024)).toString(
      'utf8',
    ),
  );
  await verifyManagedRegressionDependencies(
    environment.NUGET_PACKAGES,
    managedDependencyPolicy(
      JSON.parse(
        await owned(join(snapshot, 'scripts/release/build-payload.inputs.json'), 1024 * 1024),
      ),
    ),
    assets,
    testAssets,
    snapshot,
    stopAt,
  );
  originalPreviewStage = 'm1-receipt-source';
  await validateSource();
  const current = await lstat(root);
  assert.equal(current.dev, rootIdentity.dev);
  assert.equal(current.ino, rootIdentity.ino);
  assert.equal(await realpath(root), root);
  assert.ok(Date.now() < stopAt);
  originalPreviewStage = 'm1-receipt-summary';
  // Always compute the original receipt; preparation-only mode emits no hashes/versions.
  const regressionSummary =
    'APR_TEST_ONLY_MANAGED_REGRESSION total=1 executed=1 passed=1 skipped=0 source=' +
    source +
    ' sdk_version=' +
    sdkVersion +
    ' runtime_version=' +
    runtimeVersion +
    ' source_archive_sha256=' +
    archiveDigest +
    ' sdk_sha256=' +
    createHash('sha256').update(sdk).digest('hex') +
    ' trx_sha256=' +
    createHash('sha256').update(trxBytes).digest('hex') +
    ' test_sha256=' +
    createHash('sha256').update(testBytes).digest('hex') +
    ' runtime_sha256=' +
    createHash('sha256').update(runtimeBytes).digest('hex') +
    '\n';
  if (!privateManagedRegression) process.stdout.write(regressionSummary);
}

async function beforeOriginalPreviewDeadline(pending, deadline) {
  let timer;
  try {
    return await Promise.race([
      pending,
      new Promise((_, reject) => {
        timer = setTimeout(
          () => {
            const error = Object.assign(new Error('test_only_original_ownership_unknown'), {
              code: 'APR_TEST_ONLY_ORIGINAL_OWNERSHIP_UNKNOWN',
            });
            originalPreviewSettlementFacts.set(error, 'ownership-unknown');
            reject(error);
          },
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
      // Exit/close callbacks may have run during the final process-table await.
      // Classify only freshly observed facts; never mix them with a pre-await snapshot.
      const absent = groupAbsent();
      const naturalExit =
        child.exitCode === 0 && child.signalCode === null && naturalCloseUntil !== undefined;
      if (
        Date.now() >= stopAt ||
        (naturalCloseUntil !== undefined && Date.now() >= naturalCloseUntil)
      )
        failed = true;
      if (closed && absent) {
        if (failed || code !== 0 || signal !== null) {
          try {
            assert.fail('test_only_original_preparation_failed');
          } catch (error) {
            originalPreviewSettlementFacts.set(error, 'settled-failure');
            throw error;
          }
        }
        return;
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
          if (child.pid && anchored && !absent) process.kill(-child.pid, 'SIGKILL');
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
      (naturalCloseUntil !== undefined && Date.now() >= naturalCloseUntil)
    )
      failed = true;
    if (closed && groupAbsent()) {
      if (failed || code !== 0 || signal !== null) {
        try {
          assert.fail('test_only_original_preparation_failed');
        } catch (error) {
          originalPreviewSettlementFacts.set(error, 'settled-failure');
          throw error;
        }
      }
      return;
    }

    const error = Object.assign(new Error('test_only_original_ownership_unknown'), {
      code: 'APR_TEST_ONLY_ORIGINAL_OWNERSHIP_UNKNOWN',
    });
    originalPreviewSettlementFacts.set(error, 'ownership-unknown');
    throw error;
  } catch (error) {
    if (
      error?.code === 'APR_TEST_ONLY_ORIGINAL_OWNERSHIP_UNKNOWN' &&
      !stopped &&
      child.pid &&
      child.exitCode === null &&
      child.signalCode === null
    ) {
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
