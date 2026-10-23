// Preserve the primary proof failure even if trusted cleanup also fails.
import { setTimeout as delay } from 'node:timers/promises';
import { teardownFixture } from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/fixture-teardown.mjs';
import { withinFixtureBound } from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/role-deadline.mjs';
export async function withGeneratedCleanup(
  action,
  cleanup,
  secondary = () => process.stderr.write('APR_D4_SECONDARY_CLEANUP_FAILURE\n'),
) {
  let failed = false;
  try {
    return await action();
  } catch (error) {
    failed = true;
    throw error;
  } finally {
    try {
      await cleanup();
    } catch (error) {
      if (!failed) throw error;
      try {
        secondary();
      } catch {}
    }
  }
}
export async function confirmGeneratedGroupClose(child, closed) {
  const controller = new AbortController();
  try {
    return await withinFixtureBound(
      (async () => {
        const result = await closed;
        if (!result.groupCloseConfirmed || !Number.isSafeInteger(child.pid) || child.pid <= 0)
          throw new Error('generated_group_close_unconfirmed');
        while (true) {
          controller.signal.throwIfAborted();
          try {
            process.kill(-child.pid, 0);
          } catch (error) {
            if (error.code === 'ESRCH') return result;
            throw new Error('generated_group_close_unconfirmed');
          }
          await delay(10, undefined, { signal: controller.signal });
        }
      })(),
      5000,
    );
  } finally {
    controller.abort();
  }
}
export async function cleanupGeneratedFixture(root, confirmed) {
  if (!confirmed) throw new Error('generated_group_or_service_close_unconfirmed');
  await teardownFixture(root);
}

export async function closeGeneratedServers(servers) {
  await withinFixtureBound(
    Promise.all(
      servers.map((server) => {
        server.closeAllConnections();
        return new Promise((done, reject) =>
          server.close((error) => {
            if (!error || error.code === 'ERR_SERVER_NOT_RUNNING') done();
            else reject(error);
          }),
        );
      }),
    ),
    5000,
  );
}
