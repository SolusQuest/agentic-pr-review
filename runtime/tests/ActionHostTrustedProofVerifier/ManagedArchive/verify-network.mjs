// Supporting target-runner prerequisite; this is not Solus archive qualification.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { constants, existsSync } from 'node:fs';
import {
  chmod,
  lstat,
  mkdir,
  mkdtemp,
  open,
  readFile,
  readlink,
  realpath,
  rm,
  writeFile,
} from 'node:fs/promises';
import { homedir } from 'node:os';
import { dirname, isAbsolute, join, relative, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import {
  createRoleDeadline,
  assertFixtureRoleSeconds,
  acceptArchiveRoleDeadline,
  monotonicMilliseconds,
  withinFixtureBound,
  CASE_RESERVE_MS,
  GROUP_CLOSE_MS,
} from './role-deadline.mjs';
import { ARCHIVE_CASE_COUNT, extractCaseTimings } from './case-timing.mjs';
import { ORIGINAL_PAIR_CASE_COUNT } from './qualification-cases.mjs';
import { teardownFixture, TEARDOWN_MS } from './fixture-teardown.mjs';
import { inspectManagedPackage } from '../../../../scripts/release/build-payload.format.mjs';

const directory = dirname(fileURLToPath(import.meta.url));
const repo = fileURLToPath(new URL('../../../../', import.meta.url));
const runtimeSize = 36_648_012;
const runtimeSha512 =
  '9574d95364110409416735e7842796f8d614c21d74cb25d204a3e4cfd11b2c05efdf19ca89b8069650a04f64add2705967dcb04b8665a36d7a3f30a5d8c05afe';
export const fixtureRuntimePolicy = Object.freeze({
  version: '10.0.13',
  url: 'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.13/dotnet-runtime-10.0.13-linux-x64.tar.gz',
  sha512: runtimeSha512,
  archiveBytes: runtimeSize,
});
const digest = (bytes) => createHash('sha256').update(bytes).digest('hex');
// Only this module's post-finally path can mint a completion. Caller-supplied
// JSON (even an old successful receipt) must never authorize new custody.
const completedArchiveObservations = new WeakSet();
// A timed-out asynchronous preparation never releases its root/custody as settled.
const unresolvedPreparationOwners = new Set();
// Only the private post-seal consumer below can register a live execution claim.
const originalPreviewInvocations = new WeakMap();
export function claimOriginalRuntimePreviewInvocation(token) {
  const current = originalPreviewInvocations.get(token);
  assert.ok(
    current && current.active && !current.claimed && current.pid === process.pid,
    'preview_not_current_genuine_consumer',
  );
  current.claimed = true; // Atomic, before the first caller await; never refunded.
  for (const [key, expected] of current.environment)
    assert.ok(expected && process.env[key] === expected, 'preview_context_changed');
  assert.equal(digest(current.archiveBytes), current.context.archiveSha256);
  assert.equal(digest(current.receiptBytes), current.context.receiptSha256);
  return current;
}

const within = (parent, child) => {
  const path = relative(parent, child);
  return path === '' || (!isAbsolute(path) && path !== '..' && !path.startsWith('..' + sep));
};

// Repository-test custody only. Staged bytes are not a usable qualification
// handoff; the caller seals them only after both isolated roles have returned.
export async function createArchiveCustody(parent, workspace) {
  assert.ok(isAbsolute(parent) && parent === (await realpath(parent)));
  const reviewed = await realpath(workspace);
  assert.ok(!within(reviewed, parent), 'custody_inside_workspace');
  const owner = await lstat(parent);
  assert.ok(owner.isDirectory() && !owner.isSymbolicLink());
  assert.equal(owner.uid, process.getuid());
  assert.equal(owner.mode & 0o077, 0, 'custody_parent_not_private');
  const root = await mkdtemp(join(parent, 'apr-managed-custody-'));
  await chmod(root, 0o700);
  const original = await lstat(root);
  let staged;
  let sealed = false;
  let handoffSha256;
  let disposalBegun = false;
  let consumptionBegun = false;
  let consumptionSettled = false;
  const context = {
    repository: process.env.GITHUB_REPOSITORY,
    job: process.env.GITHUB_JOB,
    workflowRef: process.env.GITHUB_WORKFLOW_REF,
    workflowSha: process.env.GITHUB_WORKFLOW_SHA,
    run: process.env.GITHUB_RUN_ID,
    attempt: process.env.GITHUB_RUN_ATTEMPT,
    source: process.env.EXPECTED_HEAD,
    workflow: process.env.WORKFLOW_HEAD,
    pid: process.pid,
  };
  async function owned() {
    assert.equal(await realpath(root), root);
    const current = await lstat(root);
    assert.ok(current.isDirectory() && !current.isSymbolicLink());
    assert.equal(current.uid, process.getuid());
    assert.equal(current.dev, original.dev);
    assert.equal(current.ino, original.ino);
    assert.equal(current.mode & 0o077, 0);
  }
  async function bounded(path, maximum) {
    const file = await open(path, constants.O_RDONLY | constants.O_NOFOLLOW);
    try {
      const stat = await file.stat();
      assert.ok(stat.isFile() && stat.size > 0 && stat.size <= maximum);
      assert.equal(stat.uid, process.getuid());
      const bytes = Buffer.alloc(stat.size + 1);
      let size = 0;
      while (size < bytes.length) {
        const read = await file.read(bytes, size, bytes.length - size, size);
        if (!read.bytesRead) break;
        size += read.bytesRead;
      }
      assert.equal(size, stat.size, 'custody_source_changed');
      return bytes.subarray(0, size);
    } finally {
      await file.close();
    }
  }
  return {
    async stage({ fixtureRoot, archivePath, receiptPath, source, tree }) {
      await owned();
      assert.ok(!staged && !sealed);
      const fixture = await realpath(fixtureRoot);
      assert.ok(!within(fixture, root) && !within(root, fixture));
      for (const path of [archivePath, receiptPath])
        assert.ok(within(fixture, await realpath(path)), 'custody_foreign_source');
      const archive = await bounded(archivePath, 16 * 1024 * 1024);
      const receiptBytes = await bounded(receiptPath, 65536);
      const receipt = JSON.parse(receiptBytes.toString('utf8'));
      assert.match(source, /^[a-f0-9]{40}$/u);
      assert.match(tree, /^[a-f0-9]{40}$/u);
      assert.equal(receipt.identity.sourceCommit, source);
      assert.equal(receipt.identity.sourceTree, tree);
      assert.equal(digest(archive), receipt.archiveSha256);
      assert.equal(archive.length, receipt.archiveSize);
      await writeFile(join(root, 'archive.tar.gz'), archive, { flag: 'wx', mode: 0o400 });
      await writeFile(join(root, 'receipt.json'), receiptBytes, { flag: 'wx', mode: 0o400 });
      staged = {
        source,
        tree,
        archiveSha256: digest(archive),
        archiveBytes: archive.length,
        receiptSha256: digest(receiptBytes),
        originalArchiveName: receipt.archiveName,
      };
    },
    async seal(observations) {
      await owned();
      assert.ok(staged && !sealed);
      assert.equal(observations.length, 2);
      for (const [index, role] of ['first', 'independent-second'].entries()) {
        const observed = observations[index];
        assert.ok(completedArchiveObservations.has(observed), 'custody_unobserved_role_cleanup');
        assert.equal(observed.role, role);
        assert.equal(observed.source_commit, staged.source);
        assert.equal(observed.source_tree, staged.tree);
        assert.equal(observed.outside_mounts_unchanged, true);
        assert.match(observed.archive_sha256, /^[a-f0-9]{64}$/u);
        assert.match(observed.workflow_run_id, /^[1-9][0-9]{0,18}$/u);
        assert.match(observed.workflow_run_attempt, /^[1-9][0-9]{0,18}$/u);
      }
      assert.equal(observations[0].archive_sha256, staged.archiveSha256);
      assert.equal(observations[0].archive_size, staged.archiveBytes);
      assert.notEqual(observations[0].archive_sha256, observations[1].archive_sha256);
      assert.equal(observations[0].workflow_run_id, observations[1].workflow_run_id);
      assert.equal(observations[0].workflow_run_attempt, observations[1].workflow_run_attempt);
      assert.equal(
        digest(await bounded(join(root, 'archive.tar.gz'), 16 * 1024 * 1024)),
        staged.archiveSha256,
      );
      assert.equal(digest(await bounded(join(root, 'receipt.json'), 65536)), staged.receiptSha256);
      const handoff = {
        testOnly: true,
        unpublished: true,
        officialLiveQualification: false,
        originalCandidate: false,
        ...staged,
        observations,
      };
      for (const observed of observations) completedArchiveObservations.delete(observed);
      const handoffBytes = Buffer.from(JSON.stringify(handoff) + '\n');
      await writeFile(join(root, 'handoff.json'), handoffBytes, {
        flag: 'wx',
        mode: 0o400,
      });
      handoffSha256 = digest(handoffBytes);
      sealed = true;
      return { custodyRoot: root, ...handoff };
    },
    // Fixed in-process byte consumer only: no caller callback, child or executor.
    // Mark before the first await so concurrent/reentrant/failed use is never refunded.
    async consume() {
      assert.ok(sealed && !consumptionBegun && !disposalBegun, 'custody_not_current_unused_seal');
      consumptionBegun = true;
      try {
        await owned();
        assert.equal(process.pid, context.pid);
        for (const [variable, expected] of [
          ['GITHUB_REPOSITORY', context.repository],
          ['GITHUB_JOB', context.job],
          ['GITHUB_WORKFLOW_REF', context.workflowRef],
          ['GITHUB_WORKFLOW_SHA', context.workflowSha],
          ['GITHUB_RUN_ID', context.run],
          ['GITHUB_RUN_ATTEMPT', context.attempt],
          ['EXPECTED_HEAD', context.source],
          ['WORKFLOW_HEAD', context.workflow],
        ])
          assert.ok(expected && process.env[variable] === expected, 'custody_context_changed');
        assert.equal(context.repository, 'SolusQuest/agentic-pr-review');
        assert.equal(context.job, 'install-proof');
        assert.equal(context.workflowSha, staged.source);
        assert.equal(context.source, staged.source);
        assert.equal(context.workflow, staged.source);
        const handoffBytes = await bounded(join(root, 'handoff.json'), 131072);
        assert.equal(digest(handoffBytes), handoffSha256);
        const handoff = JSON.parse(handoffBytes.toString('utf8'));
        for (const observation of handoff.observations) {
          assert.equal(observation.workflow_run_id, context.run);
          assert.equal(observation.workflow_run_attempt, context.attempt);
          assert.equal(observation.source_commit, staged.source);
          assert.equal(observation.source_tree, staged.tree);
        }
        const archive = await bounded(join(root, 'archive.tar.gz'), 16 * 1024 * 1024);
        const receiptBytes = await bounded(join(root, 'receipt.json'), 65536);
        assert.equal(digest(archive), staged.archiveSha256);
        assert.equal(archive.length, staged.archiveBytes);
        assert.equal(digest(receiptBytes), staged.receiptSha256);
        const receipt = JSON.parse(receiptBytes.toString('utf8'));
        assert.equal(receipt.identity.sourceCommit, staged.source);
        assert.equal(receipt.identity.sourceTree, staged.tree);
        const { manifest } = inspectManagedPackage(archive, receipt);
        assert.equal(manifest.buildId, receipt.identity.buildId);
        return {
          testOnly: true,
          unpublished: true,
          originalCandidate: false,
          paidPreview: false,
          managedHostExecution: false,
          repository: context.repository,
          job: context.job,
          source: staged.source,
          tree: staged.tree,
          workflowRunId: context.run,
          workflowRunAttempt: context.attempt,
          archiveSha256: staged.archiveSha256,
          archiveBytes: staged.archiveBytes,
          receiptSha256: staged.receiptSha256,
          handoffSha256,
          memberCount: receipt.members.length,
          consumer: 'inspectManagedPackage',
        };
      } finally {
        consumptionSettled = true;
      }
    },
    // Separate fixed preview. No caller callback/module/argv selects execution.
    async consumeOriginalRuntimePreview() {
      assert.ok(sealed && !consumptionBegun && !disposalBegun, 'custody_not_current_unused_seal');
      consumptionBegun = true;
      let invocation;
      let codeRoot;
      try {
        await owned();
        assert.equal(context.pid, process.pid);
        assert.equal(context.repository, 'SolusQuest/agentic-pr-review-sandbox');
        assert.equal(context.job, 'review');
        assert.equal(
          context.workflowRef,
          context.repository + '/.github/workflows/agentic-pr-review.yml@refs/heads/main',
        );
        assert.equal(context.source, staged.source);
        assert.equal(context.workflow, context.workflowSha);
        assert.notEqual(context.source, context.workflowSha);
        const environment = [
          ['GITHUB_REPOSITORY', context.repository],
          ['GITHUB_JOB', context.job],
          ['GITHUB_WORKFLOW_REF', context.workflowRef],
          ['GITHUB_WORKFLOW_SHA', context.workflowSha],
          ['GITHUB_RUN_ID', context.run],
          ['GITHUB_RUN_ATTEMPT', context.attempt],
          ['EXPECTED_HEAD', context.source],
          ['WORKFLOW_HEAD', context.workflow],
        ];
        for (const [key, expected] of environment) assert.equal(process.env[key], expected);
        assert.equal(context.attempt, '1');
        assert.match(context.run, /^[1-9][0-9]{0,15}$/u);
        const handoff = await bounded(join(root, 'handoff.json'), 131072);
        assert.equal(digest(handoff), handoffSha256);
        for (const observation of JSON.parse(handoff.toString('utf8')).observations) {
          assert.equal(observation.workflow_run_id, context.run);
          assert.equal(observation.workflow_run_attempt, context.attempt);
          assert.equal(observation.source_commit, staged.source);
          assert.equal(observation.source_tree, staged.tree);
        }
        const archiveBytes = await bounded(join(root, 'archive.tar.gz'), 16 * 1024 * 1024);
        const receiptBytes = await bounded(join(root, 'receipt.json'), 65536);
        assert.equal(digest(archiveBytes), staged.archiveSha256);
        assert.equal(digest(receiptBytes), staged.receiptSha256);
        const checked = inspectManagedPackage(
          archiveBytes,
          JSON.parse(receiptBytes.toString('utf8')),
        ).receipt;
        const rawEvent = await readFile(process.env.GITHUB_EVENT_PATH);
        assert.ok(rawEvent.length > 0 && rawEvent.length <= 262144);
        const event = JSON.parse(rawEvent.toString('utf8'));
        assert.equal(event.sender?.id, 16307884);
        assert.equal(event.repository?.id, 1383960589);
        assert.equal(event.inputs?.['pr-number'], '3');
        assert.equal(typeof event.inputs?.request, 'string');
        const sourceRequest = JSON.parse(event.inputs.request);
        assert.ok(['pr', 'main'].includes(sourceRequest.role));
        invocation = Object.freeze({});
        const actualContext = Object.freeze({
          sourceCommit: staged.source,
          sourceTree: staged.tree,
          workflowCommit: context.workflowSha,
          runId: Number(context.run),
          attempt: 1,
          archiveSha256: staged.archiveSha256,
          receiptSha256: staged.receiptSha256,
          assemblySha256: checked.members.find(
            (item) => item.path === 'agentic-pr-review/AgenticPrReview.Runtime.dll',
          ).sha256,
          repository: context.repository,
          job: context.job,
          workflowRef: context.workflowRef,
          sourceRole: sourceRequest.role,
        });
        originalPreviewInvocations.set(invocation, {
          pid: process.pid,
          active: true,
          claimed: false,
          environment,
          archiveBytes,
          receiptBytes,
          context: actualContext,
          repo: await realpath(repo),
          stagingParent: parent,
          requestRaw: Buffer.from(event.inputs.request),
          eventSha256: digest(rawEvent),
          handoffSha256,
        });
        codeRoot = await mkdtemp(join(parent, 'fixed-preview-code-'));
        await chmod(codeRoot, 0o700);
        const entry = join(codeRoot, 'consumer.mjs');
        const ownModule = pathToFileURL(await realpath(fileURLToPath(import.meta.url))).href;
        const { build } = await import('esbuild');
        const compiled = await build({
          absWorkingDir: repo,
          bundle: true,
          platform: 'node',
          format: 'esm',
          packages: 'bundle',
          target: 'node24',
          write: false,
          banner: {
            js: "import{createRequire as __aprRequire}from'node:module';const require=__aprRequire(import.meta.url);",
          },
          stdin: {
            contents:
              "export{runOriginalRuntimePreviewFromCustody}from'./tests/distribution/managed-host-fixture.ts';",
            loader: 'ts',
            resolveDir: repo,
            sourcefile: 'apr-fixed-preview-entry.ts',
          },
          plugins: [
            {
              name: 'fixed-live-custody-registry',
              setup(builder) {
                builder.onResolve({ filter: /verify-network\.mjs$/ }, (args) => {
                  assert.equal(
                    args.path,
                    '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/verify-network.mjs',
                  );
                  return { path: ownModule, external: true };
                });
              },
            },
          ],
        });
        assert.equal(compiled.outputFiles.length, 1);
        const code = compiled.outputFiles[0].contents;
        await writeFile(entry, code, { flag: 'wx', mode: 0o400 });
        assert.equal(digest(await bounded(entry, 8 * 1024 * 1024)), digest(code));
        const fixed = await import(pathToFileURL(entry).href);
        const result = await fixed.runOriginalRuntimePreviewFromCustody(invocation);
        assert.equal(digest(await bounded(entry, 8 * 1024 * 1024)), digest(code));
        return result;
      } finally {
        // The fixed consumer does not return/reject until its original child barriers settle.
        if (invocation) originalPreviewInvocations.delete(invocation);
        if (codeRoot) await rm(codeRoot, { recursive: true, force: false });
        consumptionSettled = true;
      }
    },
    async dispose() {
      assert.ok(!consumptionBegun || consumptionSettled, 'custody_consumer_unsettled');
      await owned();
      assert.ok(
        !disposalBegun && (!consumptionBegun || consumptionSettled),
        'custody_cleanup_conflict',
      );
      disposalBegun = true;
      await rm(root, { recursive: true, force: false });
    },
  };
}

export async function runIsolatedFixture({
  prepare,
  childName = 'network-child.mjs',
  prefix = 'APR_D5_NETWORK_PREREQUISITE ',
  maximumSeconds = 90,
  archiveRole,
  maximumCases = ARCHIVE_CASE_COUNT,
  returnObservation = false,
} = {}) {
  assert.ok(['network-child.mjs', 'archive-child.mjs'].includes(childName));
  assert.ok(['APR_D5_NETWORK_PREREQUISITE ', 'APR_D5_MANAGED_ARCHIVE '].includes(prefix));
  assertFixtureRoleSeconds(childName, maximumSeconds);
  assert.ok([ARCHIVE_CASE_COUNT, ORIGINAL_PAIR_CASE_COUNT].includes(maximumCases));
  assert.ok(!returnObservation || childName === 'archive-child.mjs');
  if (childName === 'archive-child.mjs')
    assert.ok(['first', 'independent-second'].includes(archiveRole));
  const roleDeadline = createRoleDeadline(maximumSeconds * 1000);
  const roleBudget =
    childName === 'archive-child.mjs' ? acceptArchiveRoleDeadline(roleDeadline) : undefined;
  const roleHardAt = Number(roleDeadline.split(':')[1]);
  const hardRemaining = () => roleHardAt - monotonicMilliseconds();
  if (roleBudget) assert.ok(TEARDOWN_MS + GROUP_CLOSE_MS <= CASE_RESERVE_MS);
  let preparationUnknown = false;
  const observePreparationIO = async (operation) => {
    const allowance = roleBudget?.supportingMilliseconds(maximumSeconds * 1000);
    const pending = operation();
    try {
      return roleBudget ? await withinFixtureBound(pending, allowance) : await pending;
    } catch (error) {
      if (roleBudget && error?.message === 'd5_fixture_observation_timeout') {
        preparationUnknown = true;
        unresolvedPreparationOwners.add({ root, pending });
      }
      throw error;
    }
  };
  const preparationIO = {
    readFile: (...args) => observePreparationIO(() => readFile(...args)),
    writeFile: (...args) => observePreparationIO(() => writeFile(...args)),
    readlink: (...args) => observePreparationIO(() => readlink(...args)),
    realpath: (...args) => observePreparationIO(() => realpath(...args)),
    mkdtemp: (...args) => observePreparationIO(() => mkdtemp(...args)),
    mkdir: (...args) => observePreparationIO(() => mkdir(...args)),
    chmod: (...args) => observePreparationIO(() => chmod(...args)),
    fetch: (...args) => observePreparationIO(() => fetch(...args)),
  };
  let stage = 'preflight';
  let root;
  let receipt;
  let namespaceStarted = false;
  let namespaceClosed = false;
  let cleanupConfirmed = false;
  try {
    assert.equal(process.platform, 'linux');
    assert.equal(process.arch, 'x64');
    assert.notEqual(process.getuid(), 0);
    assert.match(await preparationIO.readFile('/etc/os-release', 'utf8'), /^ID=ubuntu$/mu);
    assert.match(
      await preparationIO.readFile('/etc/os-release', 'utf8'),
      /^VERSION_ID="24\.04"$/mu,
    );
    const builderEnv = {};
    for (const name of ['PATH', 'HOME', 'DOTNET_ROOT'])
      if (process.env[name]) builderEnv[name] = process.env[name];
    builderEnv.DOTNET_CLI_TELEMETRY_OPTOUT = '1';
    builderEnv.DOTNET_NOLOGO = '1';
    const run = (command, args) => {
      const isolated = stage === 'isolated-runtime';
      if (isolated) namespaceStarted = true;
      const result = spawnSync(command, args, {
        cwd: repo,
        env: builderEnv,
        encoding: 'utf8',
        timeout: roleBudget
          ? isolated
            ? Math.max(1, hardRemaining() - TEARDOWN_MS - GROUP_CLOSE_MS)
            : roleBudget.supportingMilliseconds(maximumSeconds * 1000)
          : (maximumSeconds + 30) * 1000,
        ...(roleBudget ? { killSignal: 'SIGKILL' } : {}),
        maxBuffer: 1024 * 1024,
      });
      if (!isolated && result.error) preparationUnknown = true;
      if (isolated) {
        namespaceClosed =
          !result.error && result.signal === null && Number.isInteger(result.status);
        if (childName === 'archive-child.mjs')
          for (const line of extractCaseTimings(result.stderr ?? '', archiveRole, { maximumCases }))
            process.stderr.write(line + '\n');
      }
      assert.equal(result.error, undefined);
      if (result.status !== 0) {
        const failure = result.stderr
          .split('\n')
          .find((line) =>
            /^APR_D5_ARCHIVE_FAILURE stage=[a-z0-9-]+ code=(assertion|harness)$/u.test(line),
          );
        if (failure) process.stderr.write(failure + '\n');
        const detail = result.stderr
          .split('\n')
          .find((line) => line.startsWith('APR_D5_ARCHIVE_DIAGNOSTIC '));
        if (detail && detail.length <= 1024) process.stderr.write(detail + '\n');
        const operations = result.stderr
          .split('\n')
          .find((line) => line.startsWith('APR_D5_ARCHIVE_OPERATION_TAIL '));
        if (operations && Buffer.byteLength(operations, 'utf8') <= 4096)
          process.stderr.write(operations + '\n');
        for (const line of result.stderr.split('\n')) {
          if (/^APR_D5_PREPARATION_(PROBE|HOST) [a-zA-Z0-9=_ -]{1,512}$/u.test(line))
            process.stderr.write(line + '\n');
        }
      }
      assert.equal(result.status, 0);
      return result.stdout;
    };
    const source = run('git', ['rev-parse', 'HEAD']).trim();
    const tree = run('git', ['rev-parse', 'HEAD^{tree}']).trim();
    assert.match(source, /^[a-f0-9]{40}$/u);
    assert.match(tree, /^[a-f0-9]{40}$/u);
    assert.equal(run('git', ['status', '--porcelain']), '');
    const original = {
      hosts: digest(await preparationIO.readFile('/etc/hosts')),
      trust: digest(await preparationIO.readFile('/etc/ssl/certs/ca-certificates.crt')),
      namespaces: {
        net: await preparationIO.readlink('/proc/self/ns/net'),
        mnt: await preparationIO.readlink('/proc/self/ns/mnt'),
      },
    };
    root = await preparationIO.mkdtemp('/tmp/apr-d5-network-');
    await preparationIO.chmod(root, 0o700);
    for (const name of ['tmp', 'trust', 'runtime', 'probe'])
      await preparationIO.mkdir(join(root, name), { mode: 0o700 });
    builderEnv.DOTNET_CLI_HOME = join(root, 'builder-home');
    const dotnet = await preparationIO.realpath(run('/usr/bin/which', ['dotnet']).trim());
    const sdk = dirname(dotnet);
    const configuredPackages = process.env.NUGET_PACKAGES ?? join(homedir(), '.nuget/packages');
    const packages = existsSync(configuredPackages)
      ? await preparationIO.realpath(configuredPackages)
      : '';

    stage = 'public-probe-build';
    const config = join(root, 'public.nuget.config');
    await preparationIO.writeFile(
      config,
      '<?xml version="1.0"?><configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>\n',
      { mode: 0o600, flag: 'wx' },
    );
    const project = join(directory, 'NetworkProbe/NetworkProbe.csproj');
    run(dotnet, ['restore', project, '--configfile', config]);
    run(dotnet, ['publish', project, '--no-restore', '-c', 'Release', '-o', join(root, 'probe')]);

    stage = 'pinned-runtime';
    const response = await preparationIO.fetch(
      'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.13/dotnet-runtime-10.0.13-linux-x64.tar.gz',
      {
        redirect: 'error',
        signal: AbortSignal.timeout(
          roleBudget ? roleBudget.supportingMilliseconds(60_000) : 60_000,
        ),
        headers: { 'Accept-Encoding': 'identity' },
      },
    );
    assert.equal(response.status, 200);
    assert.ok(response.body);
    const chunks = [];
    let size = 0;
    for await (const chunk of response.body) {
      size += chunk.length;
      assert.ok(size <= runtimeSize);
      chunks.push(chunk);
    }
    const archive = Buffer.concat(chunks);
    assert.equal(archive.length, runtimeSize);
    assert.equal(createHash('sha512').update(archive).digest('hex'), runtimeSha512);
    const archivePath = join(root, 'runtime.tar.gz');
    await preparationIO.writeFile(archivePath, archive, { mode: 0o600, flag: 'wx' });
    run('/usr/bin/tar', ['-xzf', archivePath, '-C', join(root, 'runtime')]);

    stage = 'private-tls';
    await preparationIO.writeFile(
      join(root, 'ca.cnf'),
      '[req]\ndistinguished_name=dn\nx509_extensions=ca\nprompt=no\n[dn]\nCN=D5 synthetic root\n[ca]\nbasicConstraints=critical,CA:TRUE\nkeyUsage=critical,keyCertSign,cRLSign\nsubjectKeyIdentifier=hash\n',
      { mode: 0o600, flag: 'wx' },
    );
    run('/usr/bin/openssl', [
      'req',
      '-x509',
      '-newkey',
      'rsa:2048',
      '-nodes',
      '-days',
      '1',
      '-config',
      join(root, 'ca.cnf'),
      '-keyout',
      join(root, 'ca.key'),
      '-out',
      join(root, 'ca.pem'),
    ]);
    run('/usr/bin/openssl', [
      'req',
      '-newkey',
      'rsa:2048',
      '-nodes',
      '-subj',
      '/CN=api.github.com',
      '-keyout',
      join(root, 'server.key'),
      '-out',
      join(root, 'server.csr'),
    ]);
    await preparationIO.writeFile(
      join(root, 'server.ext'),
      'subjectAltName=DNS:api.github.com,DNS:codeload.github.com,DNS:api.deepseek.com,DNS:apr-d5.blob.core.windows.net\nbasicConstraints=critical,CA:FALSE\nextendedKeyUsage=serverAuth\n',
      { mode: 0o600, flag: 'wx' },
    );
    run('/usr/bin/openssl', [
      'x509',
      '-req',
      '-days',
      '1',
      '-in',
      join(root, 'server.csr'),
      '-CA',
      join(root, 'ca.pem'),
      '-CAkey',
      join(root, 'ca.key'),
      '-CAcreateserial',
      '-extfile',
      join(root, 'server.ext'),
      '-out',
      join(root, 'server.pem'),
    ]);
    run('/usr/bin/openssl', [
      'req',
      '-x509',
      '-newkey',
      'rsa:2048',
      '-nodes',
      '-days',
      '1',
      '-subj',
      '/CN=untrusted-root.example',
      '-addext',
      'subjectAltName=DNS:untrusted-root.example',
      '-keyout',
      join(root, 'untrusted.key'),
      '-out',
      join(root, 'untrusted.pem'),
    ]);
    const ca = await preparationIO.readFile(join(root, 'ca.pem'));
    await preparationIO.writeFile(join(root, 'trust/ca-certificates.crt'), ca, {
      mode: 0o600,
      flag: 'wx',
    });
    run('/usr/bin/openssl', ['rehash', join(root, 'trust')]);
    await preparationIO.writeFile(
      join(root, 'hosts'),
      '127.0.0.1 localhost api.github.com codeload.github.com api.deepseek.com apr-d5.blob.core.windows.net wrong-host.example untrusted-root.example\n::1 localhost\n',
      { mode: 0o600, flag: 'wx' },
    );
    await preparationIO.writeFile(
      join(root, 'network.json'),
      JSON.stringify({ namespaces: original.namespaces }),
      {
        mode: 0o600,
        flag: 'wx',
      },
    );

    if (prepare) {
      stage = 'prepare-archive';
      const prepareAllowance = roleBudget?.supportingMilliseconds(maximumSeconds * 1000);
      const pending = prepare(root, { source, tree, builderEnv, roleDeadline: roleBudget });
      try {
        if (roleBudget) await withinFixtureBound(pending, prepareAllowance);
        else await pending;
      } catch (error) {
        // Callback internals can include an unfinished async build or descendants.
        // Its rejection/timeout alone does not establish their original ownership closure.
        preparationUnknown = true;
        unresolvedPreparationOwners.add({ root, pending });
        throw error;
      }
    }
    stage = 'isolated-runtime';
    // The original token predates preparation; downstream consumers cannot renew it.
    const isolatedSeconds = roleBudget
      ? Math.floor((hardRemaining() - TEARDOWN_MS - GROUP_CLOSE_MS) / 1000)
      : maximumSeconds;
    assert.ok(isolatedSeconds > 0, 'd5_role_cleanup_reserve_exhausted');
    const output = run('/usr/bin/sudo', [
      '-n',
      '/usr/bin/timeout',
      '--signal=KILL',
      String(isolatedSeconds) + 's',
      '/usr/bin/unshare',
      '--mount',
      '--net',
      '--pid',
      '--fork',
      '--mount-proc',
      '/bin/bash',
      join(directory, 'isolate-network.sh'),
      root,
      String(process.getuid()),
      String(process.getgid()),
      process.execPath,
      join(directory, childName),
      sdk,
      packages,
      roleDeadline,
    ]);
    assert.ok(output.startsWith(prefix) && output.indexOf('\n') === output.length - 1);
    assert.ok(Buffer.byteLength(output, 'utf8') <= 32768);
    const facts = JSON.parse(output.slice(prefix.length));
    if (childName === 'archive-child.mjs') assert.equal(facts.role, archiveRole);
    assert.equal(digest(await preparationIO.readFile('/etc/hosts')), original.hosts);
    assert.equal(
      digest(await preparationIO.readFile('/etc/ssl/certs/ca-certificates.crt')),
      original.trust,
    );
    for (const kind of ['net', 'mnt'])
      assert.equal(
        await preparationIO.readlink('/proc/self/ns/' + kind),
        original.namespaces[kind],
      );
    const workflow = {};
    if (childName === 'archive-child.mjs') {
      for (const [field, variable] of [
        ['workflow_run_id', 'GITHUB_RUN_ID'],
        ['workflow_run_attempt', 'GITHUB_RUN_ATTEMPT'],
      ]) {
        assert.match(process.env[variable] ?? '', /^[1-9][0-9]{0,18}$/u);
        workflow[field] = process.env[variable];
      }
    }
    receipt =
      prefix +
      JSON.stringify({
        ...facts,
        ...workflow,
        source_commit: source,
        source_tree: tree,
        probe_sha256: digest(await preparationIO.readFile(join(root, 'probe/NetworkProbe.dll'))),
        runtime_archive_sha512: runtimeSha512,
        outside_mounts_unchanged: true,
      }) +
      '\n';
    assert.ok(Buffer.byteLength(receipt, 'utf8') <= 32768);
  } catch {
    process.stderr.write('APR_D5_NETWORK_FAILURE stage=' + stage + '\n');
    process.exitCode = 1;
  } finally {
    if (root) {
      try {
        assert.ok(!preparationUnknown, 'd5_preparation_ownership_unconfirmed');
        assert.ok(!namespaceStarted || namespaceClosed, 'd5_namespace_close_unconfirmed');
        // One original reserve must still fit the existing teardown+group-close caps.
        if (roleBudget)
          assert.ok(
            hardRemaining() >= TEARDOWN_MS + GROUP_CLOSE_MS,
            'd5_role_teardown_reserve_exhausted',
          );
        await teardownFixture(root);
        if (roleBudget) assert.ok(hardRemaining() > 0, 'd5_role_hard_deadline_exhausted');
        cleanupConfirmed = true;
      } catch {
        process.stderr.write('APR_D5_FIXTURE_CLEANUP_FAILURE\n');
        process.exitCode = 1;
      }
    }
  }
  // Publishing before cleanup would make an unknown teardown look qualified.
  if (receipt && cleanupConfirmed && (process.exitCode ?? 0) === 0) {
    if (returnObservation) {
      const observation = Object.freeze(JSON.parse(receipt.slice(prefix.length)));
      completedArchiveObservations.add(observation);
      return observation;
    }
    process.stdout.write(receipt);
  } else if (returnObservation) {
    // Missing aggregate/close/teardown evidence says nothing about unperformed
    // cases. Do not manufacture a not-run receipt after partial execution.
    if (preparationUnknown || (namespaceStarted && !namespaceClosed) || !cleanupConfirmed)
      throw Object.assign(new Error('qualification_ownership_unconfirmed'), {
        code: 'APR_TEST_ONLY_ORIGINAL_OWNERSHIP_UNKNOWN',
      });
    throw new Error('qualification_execution_unobserved');
  }
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href)
  await runIsolatedFixture();
