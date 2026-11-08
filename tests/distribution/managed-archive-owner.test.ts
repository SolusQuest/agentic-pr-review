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
import { createArchiveCustody } from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/verify-network.mjs';
import { describe, expect, test } from 'vitest';
import { parse } from 'yaml';
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
    expect(job['timeout-minutes'] ?? 360).toBeGreaterThanOrEqual(210);
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
