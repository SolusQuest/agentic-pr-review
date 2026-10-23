// Test-only monotonic deadline. The trusted role owner creates this once;
// neither a case nor product input can renew the isolated role's allowance.
export const ROLE_DURATION_MS = 1_200_000;
export const SDK_QUIESCENCE_MS = 30_000;
export const HOST_CLOSE_MS = 5_000;
export const UPLOAD_SOCKET_DRAIN_MS = 2_000;
export const GROUP_CLOSE_MS = 5_000;
export const AUDIT_READ_MS = 5_000;
export const SERVER_CLOSE_MS = 5_000;
export const PREPARATION_DIAGNOSTICS_MS = 2 * 45_000;
export const CASE_RESERVE_MS =
  SDK_QUIESCENCE_MS +
  HOST_CLOSE_MS +
  UPLOAD_SOCKET_DRAIN_MS +
  GROUP_CLOSE_MS +
  AUDIT_READ_MS +
  SERVER_CLOSE_MS;
export const monotonicMilliseconds = () => Number(process.hrtime.bigint() / 1_000_000n);

export function createRoleDeadline(durationMs, now = monotonicMilliseconds) {
  const started = now();
  if (
    !Number.isSafeInteger(started) ||
    started < 0 ||
    !Number.isSafeInteger(durationMs) ||
    durationMs < 90_000 ||
    durationMs > ROLE_DURATION_MS ||
    !Number.isSafeInteger(started + durationMs)
  )
    throw new Error('d5_role_deadline_invalid');
  return `${started}:${started + durationMs}`;
}

export function acceptArchiveRoleDeadline(value, now = monotonicMilliseconds) {
  if (typeof value !== 'string' || !/^[0-9]{1,16}:[0-9]{1,16}$/u.test(value))
    throw new Error('d5_role_deadline_invalid');
  const [started, deadline] = value.split(':').map(Number);
  let observed = now();
  if (
    !Number.isSafeInteger(started) ||
    !Number.isSafeInteger(deadline) ||
    deadline - started !== ROLE_DURATION_MS ||
    !Number.isSafeInteger(observed) ||
    observed < started ||
    observed >= deadline
  )
    throw new Error('d5_role_deadline_invalid');
  return Object.freeze({
    caseMilliseconds({ preparationDiagnostics = false } = {}) {
      const current = now();
      if (!Number.isSafeInteger(current) || current < observed)
        throw new Error('d5_role_clock_invalid');
      observed = current;
      const remaining =
        deadline -
        current -
        CASE_RESERVE_MS -
        (preparationDiagnostics ? PREPARATION_DIAGNOSTICS_MS : 0);
      if (remaining <= 0) throw new Error('d5_role_cleanup_reserve_exhausted');
      return remaining;
    },
  });
}

export async function withinFixtureBound(promise, milliseconds) {
  let timer;
  try {
    return await Promise.race([
      promise,
      new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error('d5_fixture_observation_timeout')), milliseconds);
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}

export function awaitFixtureGroupClose(child, milliseconds, onHardKill = () => {}) {
  return new Promise((resolve, reject) => {
    let fired = false,
      closeTimer;
    const watchdog = setTimeout(() => {
      fired = true;
      onHardKill();
      try {
        process.kill(-child.pid, 'SIGKILL');
      } catch {
        /* Close still must be observed. */
      }
      closeTimer = setTimeout(() => {
        dispose();
        reject(new Error('d5_fixture_group_close_unconfirmed'));
      }, GROUP_CLOSE_MS);
    }, milliseconds);
    const dispose = () => {
      clearTimeout(watchdog);
      clearTimeout(closeTimer);
      child.removeListener('error', onError);
      child.removeListener('close', onClose);
    };
    const onError = (error) => {
      dispose();
      reject(error);
    };
    const onClose = (code, signal) => {
      dispose();
      resolve({ code, signal, watchdogFired: fired, groupCloseConfirmed: true });
    };
    child.once('error', onError);
    child.once('close', onClose);
  });
}
