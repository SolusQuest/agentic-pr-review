import { chmod, lstat, mkdtemp, realpath, rm } from 'node:fs/promises';
import path from 'node:path';

import { fail } from './validation.js';

/** Owns a private child of runner-provided storage through acquisition and disposal. */
export async function acquireInPayloadStaging<T extends { dispose(): Promise<void> }>(request: {
  readonly runnerTemp: string;
  readonly workspace: string;
  readonly acquire: (stagingParent: string) => Promise<T>;
}): Promise<T> {
  if (!path.isAbsolute(request.runnerTemp) || !path.isAbsolute(request.workspace))
    fail('wrapper_payload_invalid');
  const runnerTemp = await realpath(request.runnerTemp);
  const workspace = await realpath(request.workspace);
  if (runnerTemp === workspace || runnerTemp.startsWith(`${workspace}${path.sep}`))
    fail('wrapper_payload_invalid');
  const stagingParent = await mkdtemp(path.join(runnerTemp, 'apr-payload-'));
  const removeParent = () => rm(stagingParent, { recursive: true, force: true });
  try {
    await chmod(stagingParent, 0o700);
    const stat = await lstat(stagingParent);
    if (!stat.isDirectory() || (stat.mode & 0o777) !== 0o700) fail('wrapper_payload_invalid');
    const payload = await request.acquire(stagingParent);
    let disposal: Promise<void> | undefined;
    return {
      ...payload,
      dispose: () =>
        (disposal ??= (async () => {
          try {
            await payload.dispose();
          } catch (error) {
            await removeParent().catch(() => {});
            throw error;
          }
          await removeParent();
        })()),
    };
  } catch (error) {
    await removeParent().catch(() => {});
    throw error;
  }
}
