import { access } from 'node:fs/promises';
import { spawn, type ChildProcess } from 'node:child_process';
import { EventEmitter } from 'node:events';
import type { SdkProcessOwner } from '../../src/action-wrapper/artifact-bridge/sdk-process-owner.js';
import { canonicalJson, sha256 } from '../../scripts/release/build-payload.format.mjs';
import {
  stageRepositoryTestOriginalRuntimeArchive,
  acquireRepositoryTestOriginalRuntimeArchiveLease,
  acquireVerifiedManagedTreeLease,
  type VerifiedManagedTree,
  type RepositoryTestOriginalArchiveContext,
} from '../../src/action-wrapper/launcher/payload-staging.js';
import {
  mkdtemp,
  mkdir,
  writeFile,
  readFile,
  chmod,
  symlink,
  realpath,
  rm,
  stat,
  readdir,
  rename,
} from 'node:fs/promises';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import {
  claimOriginalRuntimePreviewInvocation,
  createArchiveCustody,
} from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/verify-network.mjs';
import { describe, expect, test, vi } from 'vitest';
import { parse } from 'yaml';
import { requireOriginalPreviewRouteBudget } from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/verify-archive.mjs';
import {
  readOriginalPreviewCompletedObservation,
  originalSdkSettlement,
  settleOriginalPreviewPreparationChild,
  deriveOriginalRuntimePreviewWindow,
  createOriginalRuntimePreviewPairSchedule,
  createOriginalRuntimePreviewPreparationBudget,
  runOriginalRuntimePreviewFromCustody,
  packageManagedFiles,
  minimalManagedFiles,
} from './managed-host-fixture.js';
import {
  caseTimingLine,
  extractCaseTimings,
  CASE_TIMING_PREFIX,
} from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/case-timing.mjs';
import {
  teardownFixture,
  TEARDOWN_MS,
} from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/fixture-teardown.mjs';
import {
  ROLE_DURATION_MS,
  GROUP_CLOSE_MS,
} from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/role-deadline.mjs';
const base = 'runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/';
const sample = {
  kind: 'apr-d5-case-timing-v1',
  role: 'independent-second',
  ordinal: 23,
  elapsed_ms: ROLE_DURATION_MS,
  role_elapsed_ms: ROLE_DURATION_MS + GROUP_CLOSE_MS,
  closed: false,
  watchdog_fired: true,
};

describe('closed archive role observations and owner teardown', () => {
  test('emits bounded canonical timing for observed success and failed closure', () => {
    const line = caseTimingLine(sample);
    expect(Buffer.byteLength(line)).toBeLessThanOrEqual(256);
    expect(JSON.parse(line.slice(CASE_TIMING_PREFIX.length))).toEqual(sample);
    const lines = Array.from({ length: 23 }, (_, i) =>
      caseTimingLine({ ...sample, ordinal: i + 1, closed: i !== 22, watchdog_fired: i === 22 }),
    );
    expect(extractCaseTimings('PRIVATE_UNPUBLISHED\n' + lines.join('\n'), sample.role)).toEqual(
      lines,
    );
    const current = Array.from({ length: 41 }, (_, i) =>
      caseTimingLine({ ...sample, ordinal: i + 1 }),
    );
    expect(extractCaseTimings(current.join('\n'), sample.role)).toEqual(current);
    expect(lines.join('\n')).not.toMatch(/PRIVATE|path|accepted|qualified/);
    expect(() => extractCaseTimings(lines.join('\n') + '\n' + lines[0], sample.role)).toThrow();
  });
  test('refuses extra fields, private strings, invalid counts, ordering and role substitution', () => {
    for (const value of [
      { ...sample, path: 'PRIVATE' },
      { ...sample, role: 'PRIVATE' },
      { ...sample, ordinal: 42 },
      { ...sample, elapsed_ms: -1 },
      { ...sample, closed: 'true' },
      { ...sample, role_elapsed_ms: Infinity },
    ])
      expect(() => caseTimingLine(value)).toThrow();
    expect(() => extractCaseTimings(caseTimingLine(sample), sample.role)).toThrow('order');
    expect(() => extractCaseTimings(caseTimingLine({ ...sample, ordinal: 1 }), 'first')).toThrow(
      'order',
    );
  });
  test('trusted teardown removes mode-restricted owned directories without following links', async () => {
    const tmp = await realpath('/tmp');
    const root = await mkdtemp(join(tmp, 'apr-d5-owner-test-'));
    const outside = await mkdtemp(join(tmp, 'apr-d5-outside-test-'));
    try {
      await writeFile(join(outside, 'preserved'), 'outside');
      await mkdir(join(root, 'locked'));
      await writeFile(join(root, 'locked', 'private'), 'private');
      await symlink(outside, join(root, 'link'));
      await chmod(join(root, 'locked'), 0);
      await teardownFixture(root);
      await expect(stat(root)).rejects.toMatchObject({ code: 'ENOENT' });
      expect(await readFile(join(outside, 'preserved'), 'utf8')).toBe('outside');
      expect(TEARDOWN_MS).toBe(30_000);
    } finally {
      await rm(root, { force: true, recursive: true });
      await rm(outside, { force: true, recursive: true });
    }
  });
  test('teardown refuses a root outside the closed fixture naming contract', async () => {
    const root = await mkdtemp(join(await realpath('/tmp'), 'unowned-fixture-test-'));
    try {
      await writeFile(join(root, 'preserved'), 'private');
      await expect(teardownFixture(root)).rejects.toThrow('unconfirmed');
      expect(await readFile(join(root, 'preserved'), 'utf8')).toBe('private');
    } finally {
      await rm(root, { force: true, recursive: true });
    }
  });
  test('owner publishes only after observed namespace close and confirmed bounded teardown', async () => {
    const owner = await readFile(base + 'verify-network.mjs', 'utf8');
    expect(owner).toContain('!namespaceStarted || namespaceClosed');
    expect(owner.indexOf('await teardownFixture(root)')).toBeLessThan(
      owner.indexOf('process.stdout.write(receipt)'),
    );
    expect(owner).toContain('receipt && cleanupConfirmed && (process.exitCode ?? 0) === 0');
    const archive = await readFile(base + 'verify-archive.mjs', 'utf8');
    expect(archive).toContain('maximumSeconds: 5400');
    const workflow = parse(
      await readFile('.github/workflows/solus-agent-package-proof.yml', 'utf8'),
    );
    const job = workflow.jobs['install-proof'];
    const timeout = job['timeout-minutes'];
    expect(timeout).toBe("${{ inputs.mode == 'private-managed-regression' && 15 || 360 }}");
    const branches =
      /^\$\{\{ inputs\.mode == '(private-managed-regression)' && (15) \|\| (360) \}\}$/u.exec(
        timeout,
      );
    expect(branches).not.toBeNull();
    const mode = 'verify';
    const effectiveTimeout = parseInt(mode === branches![1] ? branches![2] : branches![3], 10);
    expect(effectiveTimeout).toBe(360);
    expect(effectiveTimeout).toBeGreaterThanOrEqual(210);
    const steps = job.steps.filter(
      (step: Record<string, unknown>) => step['timeout-minutes'] === 210,
    );
    expect(steps).toHaveLength(1);
    expect(steps[0].run).toBe('bash runtime/scripts/verify-runtime.sh all');
    expect(await readFile('runtime/scripts/verify-runtime.sh', 'utf8')).toContain(
      'node "${REPO_ROOT}/runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/verify-archive.mjs"',
    );
  });
});

describe('test-only current-job custody consumption boundary', () => {
  test('rejects unsealed use and forged role JSON without returning bytes', async () => {
    const parent = await mkdtemp(join(await realpath('/tmp'), 'apr-custody-owner-test-'));
    const fixture = await mkdtemp(join(await realpath('/tmp'), 'apr-custody-source-test-'));
    await chmod(parent, 0o700);
    const custody = await createArchiveCustody(parent, process.cwd());
    try {
      await expect(custody.consume()).rejects.toThrow('custody_not_current_unused_seal');
      const archive = Buffer.from('synthetic custody negative, never a qualified archive');
      const source = 'a'.repeat(40),
        tree = 'b'.repeat(40);
      const receipt = {
        identity: { sourceCommit: source, sourceTree: tree },
        archiveName: 'original.tar.gz',
        archiveSize: archive.length,
        archiveSha256: createHash('sha256').update(archive).digest('hex'),
      };
      await writeFile(join(fixture, 'original.tar.gz'), archive, { mode: 0o600 });
      await writeFile(join(fixture, 'receipt.json'), JSON.stringify(receipt), { mode: 0o600 });
      await custody.stage({
        fixtureRoot: fixture,
        archivePath: join(fixture, 'original.tar.gz'),
        receiptPath: join(fixture, 'receipt.json'),
        source,
        tree,
      });
      const forged = ['first', 'independent-second'].map((role) => ({
        role,
        source_commit: source,
        source_tree: tree,
        outside_mounts_unchanged: true,
        workflow_run_id: '1',
        workflow_run_attempt: '1',
        archive_sha256: receipt.archiveSha256,
        archive_size: archive.length,
      }));
      await expect(custody.seal(JSON.parse(JSON.stringify(forged)))).rejects.toThrow(
        'custody_unobserved_role_cleanup',
      );
      await expect(custody.consume()).rejects.toThrow('custody_not_current_unused_seal');
      const root = (await readdir(parent))[0];
      expect(await readdir(join(parent, root))).not.toContain('handoff.json');
    } finally {
      await custody.dispose();
      expect(await readdir(parent)).toEqual([]);
      await rm(parent, { recursive: true });
      await rm(fixture, { recursive: true });
    }
  });

  test('refuses foreign replacement during cleanup and preserves outside bytes', async () => {
    const parent = await mkdtemp(join(await realpath('/tmp'), 'apr-custody-owner-test-'));
    const outside = await mkdtemp(join(await realpath('/tmp'), 'apr-custody-outside-test-'));
    await chmod(parent, 0o700);
    await writeFile(join(outside, 'preserved'), 'outside-original');
    const custody = await createArchiveCustody(parent, process.cwd());
    const root = join(parent, (await readdir(parent))[0]);
    const retained = root + '-retained';
    try {
      await rename(root, retained);
      await symlink(outside, root);
      await expect(custody.dispose()).rejects.toThrow();
      expect(await readFile(join(outside, 'preserved'), 'utf8')).toBe('outside-original');
      await rm(root);
      await rename(retained, root);
      await custody.dispose();
      expect(await readdir(parent)).toEqual([]);
    } finally {
      await rm(parent, { recursive: true, force: true });
      await rm(outside, { recursive: true, force: true });
    }
  });

  test('strict workflow opt-in is only on the verify archive owner and exports no binary', async () => {
    const workflow = parse(
      await readFile('.github/workflows/solus-agent-package-proof.yml', 'utf8'),
    );
    const steps = workflow.jobs['install-proof'].steps;
    const selected = steps.filter(
      (step: { env?: Record<string, string> }) =>
        step.env?.APR_TEST_MANAGED_CUSTODY_CONSUME !== undefined,
    );
    expect(selected).toHaveLength(1);
    expect(selected[0].if).toBe("inputs.mode == 'verify'");
    expect(selected[0].env.APR_TEST_MANAGED_CUSTODY_CONSUME).toBe('1');
    expect(selected[0].env.GITHUB_TOKEN).toBeUndefined();
    expect(selected[0].run).toBe('bash runtime/scripts/verify-runtime.sh all');
    const paths = steps
      .filter((step: { uses?: string }) => step.uses?.startsWith('actions/upload-artifact@'))
      .map((step: { with: { path: string } }) => step.with.path);
    expect(paths.every((path: string) => path.endsWith('/metadata.json'))).toBe(true);
  });
});

describe('pure original-preview outer and two-unit windows', () => {
  test('serialized receipts or handoffs cannot claim the real pipeline before any SDK or Host starts', async () => {
    for (const token of [{}, JSON.parse('{"sealed":true,"qualification":true}')]) {
      expect(() => claimOriginalRuntimePreviewInvocation(token)).toThrow(
        'preview_not_current_genuine_consumer',
      );
      await expect(runOriginalRuntimePreviewFromCustody(token)).rejects.toThrow(
        'preview_not_current_genuine_consumer',
      );
    }
  });

  const posted = Date.parse('2026-11-08T14:00:00.000Z');
  const proposal = () => ({
    postedAtUtc: new Date(posted).toISOString(),
    validUntilUtc: new Date(posted + 21_600_000).toISOString(),
    storageExpiries: [13474437693, 13474377959, 13474631449, 13483914599].map((id) => ({
      id,
      expiresAtUtc: '2026-11-14T00:00:00.000Z',
    })),
  });
  const derive = (value: unknown) => {
    const bytes = Buffer.from(JSON.stringify(value));
    return deriveOriginalRuntimePreviewWindow(
      bytes,
      createHash('sha256').update(bytes).digest('hex'),
      posted + 1000,
      posted,
    );
  };
  test('keeps the outer POST deadline and full capacity while phases are one-shot and never refunded', () => {
    const window = derive(proposal());
    const schedule = createOriginalRuntimePreviewPairSchedule(window);
    const first = schedule.beginUnit(0, posted + 10_000_000);
    const second = schedule.beginUnit(1, first.hardAtMs + 300_000);
    expect(first).toMatchObject({ physicalAttemptLimit: 6, providerSecondsLimit: 120 });
    expect(first.stopAtMs - first.startedAtMs).toBe(1_020_000);
    expect(second.hardAtMs - second.startedAtMs).toBe(1_200_000);
    expect(window.validUntilMs).toBe(posted + 21_600_000);
    expect(() => schedule.beginUnit(1, second.hardAtMs)).toThrow(
      'test_only_preview_phase_replayed',
    );
    const failed = createOriginalRuntimePreviewPairSchedule(window);
    expect(() => failed.beginUnit(0, window.validUntilMs - 1000)).toThrow(
      'test_only_preview_phase_window_expired',
    );
    expect(() => failed.beginUnit(0, posted)).toThrow('test_only_preview_phase_replayed');
    expect(() => failed.beginUnit(1, posted)).toThrow('test_only_preview_phase_replayed');
    const backwards = createOriginalRuntimePreviewPairSchedule(window);
    backwards.beginUnit(0, posted + 1000);
    expect(() => backwards.beginUnit(1, posted)).toThrow('test_only_preview_phase_window_expired');
  });
  test('rejects renewed windows, storage shortfall, hard-deadline extension and altered bytes', () => {
    const value = proposal();
    expect(() =>
      derive({ ...value, validUntilUtc: new Date(posted + 21_600_001).toISOString() }),
    ).toThrow();
    expect(() => derive({ ...value, validUntilUtc: '2026-11-10T03:10:06.001Z' })).toThrow();
    expect(() =>
      derive({
        ...value,
        storageExpiries: value.storageExpiries.map((item) => ({
          ...item,
          expiresAtUtc: '2026-11-11T14:00:00.000Z',
        })),
      }),
    ).toThrow();
    expect(() =>
      derive({
        ...value,
        storageExpiries: [value.storageExpiries[0], ...value.storageExpiries.slice(0, 3)],
      }),
    ).toThrow();
    expect(() =>
      deriveOriginalRuntimePreviewWindow(
        Buffer.from(JSON.stringify(value)),
        '0'.repeat(64),
        posted,
        posted,
      ),
    ).toThrow();
  });
});

describe.skipIf(process.platform !== 'linux' || process.arch !== 'x64')(
  'unpublished original archive filesystem ownership only',
  () => {
    test('keeps genuine publication provenance absent and enforces independent context/token/lifetime guards', async () => {
      const root = await mkdtemp(join(await realpath('/tmp'), 'apr-original-owner-'));
      const directories = { runnerTemp: join(root, 'staging'), workspace: join(root, 'workspace') };
      await mkdir(directories.runnerTemp, { mode: 0o700 });
      await mkdir(directories.workspace, { mode: 0o700 });
      const packaged = packageManagedFiles(minimalManagedFiles()); // Synthetic bytes, never executed/qualified.
      const receiptBytes = Buffer.from(canonicalJson(packaged.receipt) + '\n');
      const context: RepositoryTestOriginalArchiveContext = {
        workflowCommit: '3'.repeat(40),
        runId: 123,
        attempt: 1,
        sourceCommit: packaged.receipt.identity.sourceCommit,
        sourceTree: packaged.receipt.identity.sourceTree,
        archiveSha256: packaged.receipt.archiveSha256,
        receiptSha256: sha256(receiptBytes),
        assemblySha256: packaged.receipt.members.find(
          (member) => member.path === 'agentic-pr-review/AgenticPrReview.Runtime.dll',
        )!.sha256,
        repository: 'SolusQuest/agentic-pr-review-sandbox',
        job: 'review',
        sourceRole: 'pr',
        workflowRef:
          'SolusQuest/agentic-pr-review-sandbox/.github/workflows/agentic-pr-review.yml@refs/heads/main',
      };
      for (const [name, value] of Object.entries({
        GITHUB_REPOSITORY: context.repository,
        GITHUB_JOB: context.job,
        GITHUB_WORKFLOW_REF: context.workflowRef,
        GITHUB_WORKFLOW_SHA: context.workflowCommit,
        GITHUB_RUN_ID: String(context.runId),
        GITHUB_RUN_ATTEMPT: String(context.attempt),
        EXPECTED_HEAD: context.sourceCommit,
        WORKFLOW_HEAD: context.workflowCommit,
      }))
        vi.stubEnv(name, value);
      const owned = await stageRepositoryTestOriginalRuntimeArchive({
        archiveBytes: packaged.archive,
        receiptBytes,
        context,
        stagingParent: directories.runnerTemp,
        workspace: directories.workspace,
        signal: new AbortController().signal,
      });
      let lease:
        | Awaited<ReturnType<typeof acquireRepositoryTestOriginalRuntimeArchiveLease>>
        | undefined;
      try {
        expect(owned).not.toHaveProperty('builder');
        expect(owned).not.toHaveProperty('tree');
        lease = await acquireRepositoryTestOriginalRuntimeArchiveLease(
          owned.archive,
          context,
          new AbortController().signal,
        );
        expect(lease).not.toHaveProperty('builder');
        expect(lease.files.length).toBe(packaged.receipt.members.length);
        await expect(
          acquireVerifiedManagedTreeLease(
            owned.archive as unknown as VerifiedManagedTree,
            new AbortController().signal,
          ),
        ).rejects.toThrow('wrapper_managed_tree_invalid');
        await expect(
          acquireRepositoryTestOriginalRuntimeArchiveLease(
            owned.archive,
            { ...context, runId: context.runId + 1 },
            new AbortController().signal,
          ),
        ).rejects.toThrow('wrapper_test_original_archive_invalid');
        vi.stubEnv('GITHUB_JOB', 'other');
        await expect(
          acquireRepositoryTestOriginalRuntimeArchiveLease(
            owned.archive,
            context,
            new AbortController().signal,
          ),
        ).rejects.toThrow('wrapper_test_original_archive_invalid');
        vi.stubEnv('GITHUB_JOB', context.job);
        await expect(owned.dispose()).rejects.toThrow('wrapper_managed_tree_in_use');
      } finally {
        lease?.release();
        await owned.dispose();
        vi.unstubAllEnvs();
        await rm(root, { recursive: true, force: true });
      }
      await expect(access(owned.root)).rejects.toThrow();
    });
  },
);

describe('original preview completed observation and finite settlement', () => {
  const context = {
    runId: 42480000123,
    attempt: 1,
    sourceCommit: 'a'.repeat(40),
    workflowCommit: 'b'.repeat(40),
  };
  const head = 'c'.repeat(40),
    nextHead = 'd'.repeat(40);
  const fresh = {
    classification: 'FreshAccepted',
    runId: context.runId,
    runAttempt: 1,
    actionSourceSha: context.sourceCommit,
    workflowSha: context.workflowCommit,
    reviewedHead: head,
    familySha256: '1'.repeat(64),
    logicalGenerationIdentity: '2'.repeat(64),
    envelopeSha256: '3'.repeat(64),
    checkpointSha256: '4'.repeat(64),
    predecessorLogicalGenerationIdentity: null,
    predecessorEnvelopeSha256: null,
    sourceCheckpointSha256: null,
  };
  const encode = (value: unknown) => Buffer.from(JSON.stringify(value));
  test('requires an exact original completed family link and changed successor checkpoint', () => {
    // Pure channel-contract test only; these authored bytes mint no custody/Host authority.
    const first = readOriginalPreviewCompletedObservation(encode(fresh), context, head);
    const restored = {
      ...fresh,
      classification: 'RestoredConsumedAccepted',
      reviewedHead: nextHead,
      logicalGenerationIdentity: '5'.repeat(64),
      envelopeSha256: '6'.repeat(64),
      checkpointSha256: '7'.repeat(64),
      predecessorLogicalGenerationIdentity: fresh.logicalGenerationIdentity,
      predecessorEnvelopeSha256: fresh.envelopeSha256,
      sourceCheckpointSha256: fresh.checkpointSha256,
    };
    expect(
      readOriginalPreviewCompletedObservation(encode(restored), context, nextHead, first)
        .classification,
    ).toBe('RestoredConsumedAccepted');
    for (const mutation of [
      { classification: 'FreshAccepted' },
      { familySha256: '8'.repeat(64) },
      { predecessorLogicalGenerationIdentity: '8'.repeat(64) },
      { sourceCheckpointSha256: '8'.repeat(64) },
      { checkpointSha256: fresh.checkpointSha256 },
      { runId: context.runId + 1 },
      { reviewedHead: head },
    ])
      expect(() =>
        readOriginalPreviewCompletedObservation(
          encode({ ...restored, ...mutation }),
          context,
          nextHead,
          first,
        ),
      ).toThrow();
    expect(() => readOriginalPreviewCompletedObservation(undefined, context, head)).toThrow();
    expect(() =>
      readOriginalPreviewCompletedObservation(
        encode({ classification: 'Unavailable' }),
        context,
        head,
      ),
    ).toThrow();
  });
  test('SDK unknown and nonreturning original owner stop at the same absolute deadline', async () => {
    const began = Date.now();
    for (const response of [Promise.resolve(false), new Promise<boolean>(() => {})]) {
      const owner = { awaitTermination: vi.fn(() => response) } as unknown as SdkProcessOwner;
      await expect(originalSdkSettlement(owner, Date.now() + 20)).rejects.toMatchObject({
        code: 'APR_TEST_ONLY_ORIGINAL_OWNERSHIP_UNKNOWN',
      });
      expect(owner.awaitTermination).toHaveBeenCalledTimes(1);
    }
    expect(Date.now() - began).toBeLessThan(1000);
  });
  test('successful preparation requires observed parent and final owned-group closure', async () => {
    const child = spawn(process.execPath, ['-e', 'setTimeout(()=>{},30)'], {
      detached: true,
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    await settleOriginalPreviewPreparationChild(child, Date.now() + 1000, Date.now() + 1500);
    expect(child.exitCode).toBe(0);
    expect(() => process.kill(-child.pid!, 0)).toThrow();
  });
  test('successful parent waits for a short real descendant natural close without intervention', async () => {
    const grandchild =
      "process.send('ready');setTimeout(()=>{process.stdout.write('tail-exited\\n');process.exit(0)},200)";
    const parent =
      "const{spawn}=require('node:child_process');const child=spawn(process.execPath,['-e'," +
      JSON.stringify(grandchild) +
      "],{stdio:['ignore','inherit','inherit','ipc']});child.once('message',()=>{process.stdout.write('parent-exit\\n');process.exit(0)});";
    const child = spawn(process.execPath, ['-e', parent], {
      detached: true,
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    const output: Buffer[] = [];
    child.stdout.on('data', (bytes: Buffer) => output.push(bytes));
    let parentExitAt = 0;
    child.once('exit', () => {
      parentExitAt = Date.now();
    });
    const stop = vi.spyOn(child, 'kill');
    const signals = vi.spyOn(process, 'kill');
    try {
      await settleOriginalPreviewPreparationChild(child, Date.now() + 3000, Date.now() + 4000);
      expect(child.exitCode).toBe(0);
      expect(child.signalCode).toBeNull();
      expect(parentExitAt).toBeGreaterThan(0);
      expect(Date.now() - parentExitAt).toBeGreaterThanOrEqual(100);
      expect(Buffer.concat(output).toString()).toBe('parent-exit\n' + 'tail-exited\n');
      expect(stop).not.toHaveBeenCalled();
      expect(signals.mock.calls.every(([, signal]) => signal === 0)).toBe(true);
      expect(() => process.kill(-child.pid!, 0)).toThrow();
    } finally {
      signals.mockRestore();
      stop.mockRestore();
    }
  });
  test('failed preparation cannot become successful closure', async () => {
    const child = spawn(process.execPath, ['-e', 'process.exit(7)'], {
      detached: true,
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    await expect(
      settleOriginalPreviewPreparationChild(child, Date.now() + 1000, Date.now() + 1500),
    ).rejects.toThrow();
    expect(child.exitCode).toBe(7);
    expect(() => process.kill(-child.pid!, 0)).toThrow();
  });
  test('preparation deadline stops its known live parent and still requires close', async () => {
    const child = spawn(process.execPath, ['-e', 'setInterval(()=>{},1000)'], {
      detached: true,
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    await expect(
      settleOriginalPreviewPreparationChild(child, Date.now() + 20, Date.now() + 500),
    ).rejects.toThrow();
    expect(child.signalCode).toBe('SIGKILL');
    expect(() => process.kill(-child.pid!, 0)).toThrow();
  });
  test('missing close cannot spin, invent an exit, or kill an unbound PID', async () => {
    const child = Object.assign(new EventEmitter(), {
      stdout: null,
      stderr: null,
      pid: undefined,
      exitCode: null,
      signalCode: null,
      kill: vi.fn(),
    }) as unknown as ChildProcess;
    const began = Date.now();
    await expect(
      settleOriginalPreviewPreparationChild(child, Date.now() + 10, Date.now() + 40),
    ).rejects.toMatchObject({ code: 'APR_TEST_ONLY_ORIGINAL_OWNERSHIP_UNKNOWN' });
    expect(Date.now() - began).toBeLessThan(1000);
    expect(child.kill).not.toHaveBeenCalled();
    expect(child.exitCode).toBeNull();
  });
});

describe('fixed original-preview preparation budget', () => {
  test('rejects global entry unless every remaining declared stage fits', () => {
    vi.useFakeTimers();
    vi.setSystemTime(1000);
    try {
      const until = Date.now() + 15_420_000;
      expect(requireOriginalPreviewRouteBudget(until, 2, 480_000)).toBe(15_420_000);
      expect(() => requireOriginalPreviewRouteBudget(until - 1, 2, 480_000)).toThrow();
      vi.setSystemTime(Date.now() + 480_000);
      expect(requireOriginalPreviewRouteBudget(until, 2)).toBe(14_940_000);
      vi.setSystemTime(Date.now() + 5_400_000);
      expect(requireOriginalPreviewRouteBudget(until, 1)).toBe(9_540_000);
      expect(() => requireOriginalPreviewRouteBudget(Date.now() + 3_600_000, 1)).toThrow();
      expect(() => createOriginalRuntimePreviewPreparationBudget(Date.now() + 4_139_999)).toThrow();
      expect(() => requireOriginalPreviewRouteBudget(until, 3)).toThrow();
    } finally {
      vi.useRealTimers();
    }
  });

  test('charges all elapsed preparation against one stop and preserves smaller leaf limits', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(1000);
    const phase = createOriginalRuntimePreviewPreparationBudget(Date.now() + 21_600_000);
    try {
      const stop = phase.stopAtMs,
        hard = phase.hardAtMs;
      expect(stop).toBe(601_000);
      expect(hard).toBe(781_000);
      await vi.advanceTimersByTimeAsync(180_000);
      expect(phase.leafMilliseconds(120_000)).toBe(120_000);
      await vi.advanceTimersByTimeAsync(419_999);
      expect(phase.leafMilliseconds(120_000)).toBe(1);
      expect(phase.stopAtMs).toBe(stop);
      expect(phase.hardAtMs).toBe(hard);
    } finally {
      phase.stop();
      vi.useRealTimers();
    }
  });

  test('ready clears only the preparation timer and cannot later abort an outer-owned unit', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(1000);
    const outer = new AbortController();
    const phase = createOriginalRuntimePreviewPreparationBudget(Date.now() + 21_600_000);
    const linked = AbortSignal.any([outer.signal, phase.signal]);
    try {
      phase.finish();
      expect(() => phase.leafMilliseconds(120_000)).toThrow();
      await vi.advanceTimersByTimeAsync(780_001);
      expect(phase.signal.aborted).toBe(false);
      expect(linked.aborted).toBe(false);
      outer.abort();
      expect(linked.aborted).toBe(true);
    } finally {
      phase.stop();
      vi.useRealTimers();
    }
  });

  test('expiration and explicit stop cannot become a ready capability', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(1000);
    const phase = createOriginalRuntimePreviewPreparationBudget(Date.now() + 21_600_000);
    try {
      await vi.advanceTimersByTimeAsync(600_000);
      expect(phase.signal.aborted).toBe(true);
      expect(() => phase.finish()).toThrow();
      expect(() => phase.leafMilliseconds(60_000)).toThrow();
      expect(phase.hardAtMs).toBe(781_000);
    } finally {
      phase.stop();
      vi.useRealTimers();
    }
  });

  test('unknown preparation stays owned instead of qualifying a partial seal', async () => {
    const owner = await readFile('tests/distribution/managed-host-fixture.ts', 'utf8');
    const qualifier = await readFile(
      'runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/verify-archive.mjs',
      'utf8',
    );
    expect(owner).toContain('unresolvedPreviewOwners.add(retained)');
    expect(owner).toContain('unresolvedPreviewOwners.add(preparationResources)');
    expect(owner).toContain('beforePreviewDeadline(retained.pending, preparation.hardAtMs)');
    expect(owner.indexOf('preparation.finish();')).toBeLessThan(
      owner.indexOf('  const runUnit = async'),
    );
    expect(qualifier).toContain(
      'if (custody && !handedOff && !unresolved) await custody.dispose()',
    );
    expect(qualifier).toContain('unresolvedQualificationCustodies.add({ custody, observations })');
  });
});
