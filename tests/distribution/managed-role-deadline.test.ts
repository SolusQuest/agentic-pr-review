import { EventEmitter } from 'node:events';
import { spawn, spawnSync, execFileSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { afterEach, describe, expect, test, vi } from 'vitest';
import {
  acceptArchiveRoleDeadline,
  createRoleDeadline,
  awaitFixtureGroupClose,
  withinFixtureBound,
  ROLE_DURATION_MS,
  CASE_RESERVE_MS,
  GROUP_CLOSE_MS,
  PREPARATION_DIAGNOSTICS_MS,
} from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/role-deadline.mjs';

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe('archive role deadline and settlement reserve', () => {
  test('uses one monotonic owner origin and consumes it across cases and setup', () => {
    let now = 1000;
    const token = createRoleDeadline(ROLE_DURATION_MS, () => now);
    now += 4000; // Namespace setup belongs to the existing role cap.
    const role = acceptArchiveRoleDeadline(token, () => now);
    expect(role.caseMilliseconds()).toBe(ROLE_DURATION_MS - 4000 - CASE_RESERVE_MS);
    expect(role.caseMilliseconds({ preparationDiagnostics: true })).toBe(
      ROLE_DURATION_MS - 4000 - CASE_RESERVE_MS - PREPARATION_DIAGNOSTICS_MS,
    );
    now += 180_000;
    expect(role.caseMilliseconds()).toBe(ROLE_DURATION_MS - 184000 - CASE_RESERVE_MS);
    // Constructing another consumer cannot renew the same trusted token.
    expect(acceptArchiveRoleDeadline(token, () => now).caseMilliseconds()).toBe(
      role.caseMilliseconds(),
    );
  });

  test('preserves the monotonic deadline across actual parent/consumer processes', () => {
    const token = createRoleDeadline(ROLE_DURATION_MS);
    const helper = new URL(
      '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/role-deadline.mjs',
      import.meta.url,
    ).href;
    const remaining = Number(
      execFileSync(
        process.execPath,
        [
          '--input-type=module',
          '-e',
          `import { acceptArchiveRoleDeadline } from ${JSON.stringify(helper)}; console.log(acceptArchiveRoleDeadline(process.argv[1]).caseMilliseconds());`,
          token,
        ],
        { encoding: 'utf8', timeout: 5000 },
      ),
    );
    expect(remaining).toBeGreaterThan(0);
    expect(remaining).toBeLessThanOrEqual(ROLE_DURATION_MS - CASE_RESERVE_MS);
  });

  test.each([
    undefined,
    '',
    '1',
    '1:2',
    '-1:1199999',
    '1:1200002',
    'NaN:Infinity',
    '9007199254740991:9007199254740992',
    '1:1200001:3',
  ])('rejects missing or malformed deadline %s before case admission', (value) => {
    expect(() => acceptArchiveRoleDeadline(value, () => 10)).toThrow('d5_role_deadline_invalid');
  });

  test('rejects expired/future origins and insufficient cleanup/diagnostic reserve', () => {
    let now = 1000;
    const token = createRoleDeadline(ROLE_DURATION_MS, () => now);
    expect(() => acceptArchiveRoleDeadline(token, () => 999)).toThrow();
    expect(() => acceptArchiveRoleDeadline(token, () => 1000 + ROLE_DURATION_MS)).toThrow();
    const role = acceptArchiveRoleDeadline(token, () => now);
    now = 1000 + ROLE_DURATION_MS - CASE_RESERVE_MS - PREPARATION_DIAGNOSTICS_MS;
    expect(() => role.caseMilliseconds({ preparationDiagnostics: true })).toThrow(
      'reserve_exhausted',
    );
    expect(role.caseMilliseconds()).toBe(PREPARATION_DIAGNOSTICS_MS);
    now += PREPARATION_DIAGNOSTICS_MS;
    expect(() => role.caseMilliseconds()).toThrow('reserve_exhausted');
    now += CASE_RESERVE_MS;
    expect(() => role.caseMilliseconds()).toThrow('reserve_exhausted');
  });

  test('rejects clock reversal and invalid or extended owner durations', () => {
    let now = 1000;
    const role = acceptArchiveRoleDeadline(
      createRoleDeadline(ROLE_DURATION_MS, () => now),
      () => now,
    );
    now--;
    expect(() => role.caseMilliseconds()).toThrow('d5_role_clock_invalid');
    for (const duration of [0, NaN, Infinity, ROLE_DURATION_MS + 1])
      expect(() => createRoleDeadline(duration, () => 1)).toThrow();
  });

  test('actual consumer rejects absent, malformed and expired argv before boundary work', () => {
    const entry = 'runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/archive-child.mjs';
    const now = Number(process.hrtime.bigint() / 1_000_000n);
    const expired = `${now - ROLE_DURATION_MS - 1000}:${now - 1000}`;
    for (const token of [undefined, 'invalid', expired]) {
      const result = spawnSync(
        process.execPath,
        [entry, 'PRIVATE_UNOPENED_ROOT', ...(token ? [token] : [])],
        {
          encoding: 'utf8',
          timeout: 5000,
        },
      );
      expect(result.error).toBeUndefined();
      expect(result.status).toBe(1);
      expect(result.stderr).toContain('APR_D5_ARCHIVE_FAILURE stage=boundary code=harness');
      expect(result.stderr).not.toContain('PRIVATE_UNOPENED_ROOT');
    }
  });

  test('returns only observed close and leaves expiry a failed case even after group close', async () => {
    vi.useFakeTimers();
    const kill = vi.spyOn(process, 'kill').mockImplementation(() => true);
    const child = Object.assign(new EventEmitter(), { pid: 123456 });
    const timed = awaitFixtureGroupClose(child, 100);
    await vi.advanceTimersByTimeAsync(100);
    expect(kill).toHaveBeenCalledWith(-123456, 'SIGKILL');
    child.emit('close', null, 'SIGKILL');
    expect(await timed).toEqual({
      code: null,
      signal: 'SIGKILL',
      watchdogFired: true,
      groupCloseConfirmed: true,
    });
    const normal = awaitFixtureGroupClose(child, 100);
    child.emit('close', 0, null);
    expect((await normal).watchdogFired).toBe(false);
  });

  test('never treats an unconfirmed process-group close as cleanup success', async () => {
    vi.useFakeTimers();
    vi.spyOn(process, 'kill').mockImplementation(() => {
      throw new Error('no confirmation');
    });
    const child = Object.assign(new EventEmitter(), { pid: 123456 });
    const timed = awaitFixtureGroupClose(child, 10);
    const rejected = expect(timed).rejects.toThrow('d5_fixture_group_close_unconfirmed');
    await vi.advanceTimersByTimeAsync(10 + GROUP_CLOSE_MS);
    await rejected;
  });

  test('bounds audit/diagnostic observations rather than waiting indefinitely', async () => {
    vi.useFakeTimers();
    const observed = withinFixtureBound(new Promise(() => {}), 5000);
    const rejected = expect(observed).rejects.toThrow('d5_fixture_observation_timeout');
    await vi.advanceTimersByTimeAsync(5000);
    await rejected;
  });

  test('hard-kills a real detached process group at expiry and observes close', async () => {
    const child = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], {
      detached: true,
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    try {
      const closed = await awaitFixtureGroupClose(child, 50);
      expect(closed.watchdogFired).toBe(true);
      expect(closed.signal).toBe('SIGKILL');
      expect(closed.groupCloseConfirmed).toBe(true);
    } finally {
      try {
        process.kill(-child.pid!, 'SIGKILL');
      } catch {}
    }
  });

  test('trusted owner hands one token through isolated argv without changing outer cap', async () => {
    const base = 'runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/';
    const owner = await readFile(base + 'verify-network.mjs', 'utf8');
    const shell = await readFile(base + 'isolate-network.sh', 'utf8');
    const consumer = await readFile(base + 'archive-child.mjs', 'utf8');
    expect(owner.match(/createRoleDeadline\(maximumSeconds \* 1000\)/gu)).toHaveLength(1);
    expect(owner).toContain("String(maximumSeconds) + 's'");
    expect(shell).toContain('"$fixture_program" "$fixture_root" "$fixture_deadline"');
    expect(consumer).toContain('acceptArchiveRoleDeadline(process.argv[3])');
    expect(consumer).not.toContain('180_000');
    expect(consumer).toContain("assert.equal(exit.watchdogFired, false, 'd5_role_watchdog_fired')");
  });
});
