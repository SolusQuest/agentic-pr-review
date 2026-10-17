// Repository-only process supervision; compile with esbuild before running.
import assert from 'node:assert/strict';
import childProcess from 'node:child_process';
import { syncBuiltinESMExports } from 'node:module';
import { createReadStream } from 'node:fs';
import { mkdtemp, mkdir, readdir, rm } from 'node:fs/promises';
import { createServer, request as httpRequest } from 'node:http';
import path from 'node:path';
import {
  createRepositoryTestRuntimePrerequisiteAcquirer,
  PINNED_MANAGED_RUNTIME,
  RuntimePrerequisiteTerminationUnconfirmedError,
} from '../../src/action-wrapper/launcher/runtime-prerequisite.js';
assert.equal(process.platform, 'linux');
assert.equal(process.arch, 'x64');
assert.ok(process.argv[2]);
const active = new Map();
const work = await mkdtemp('/tmp/apr-d4-subprocess-');
const workspace = path.join(work, 'workspace');
await mkdir(workspace);
const server = createServer((_request, response) => {
  response.writeHead(200, { 'Content-Length': PINNED_MANAGED_RUNTIME.archiveBytes });
  createReadStream(process.argv[2]).pipe(response);
});
await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
const origin = 'http://127.0.0.1:' + server.address().port;
const acquire = createRepositoryTestRuntimePrerequisiteAcquirer((url, options, callback) => {
  assert.equal(url.href, PINNED_MANAGED_RUNTIME.url);
  return httpRequest(origin, options, callback);
});
const originalSpawn = childProcess.spawn;
const originalSetTimeout = globalThis.setTimeout;
const cases = [];
let unconfirmed;
try {
  for (const mode of ['failed-start', 'confirmed-cancel', 'deadline', 'unconfirmed-close']) {
    // Accelerate only the production listing deadline; kill/close grace and
    // real OS events keep their original timing and authority.
    globalThis.setTimeout =
      mode === 'deadline'
        ? (callback, milliseconds, ...args) =>
            originalSetTimeout(callback, milliseconds === 30_000 ? 10 : milliseconds, ...args)
        : originalSetTimeout;
    const runnerTemp = path.join(work, mode);
    await mkdir(runnerTemp, { mode: 0o700 });
    const controller = new AbortController();
    let spawned = 0;
    childProcess.spawn = (command, args, options) => {
      assert.equal(command, '/usr/bin/tar');
      assert.equal(args[0], '--list');
      spawned++;
      let child;
      if (mode === 'failed-start')
        child = originalSpawn('/definitely-absent-d4-test-extractor', [], options);
      else if (mode === 'confirmed-cancel' || mode === 'deadline') {
        child = originalSpawn(process.execPath, ['-e', 'setInterval(()=>{},1000)'], options);
        if (mode === 'confirmed-cancel') setTimeout(() => controller.abort(), 50);
      } else {
        const program =
          "const{spawn}=require('node:child_process');spawn(process.execPath,['-e','setTimeout(()=>{},7500)'],{stdio:['ignore',1,2]});process.on('SIGTERM',()=>{});process.stdout.write('ready');setInterval(()=>{},1000);";
        child = originalSpawn(process.execPath, ['-e', program], options);
        child.stdout.once('data', () => controller.abort());
      }
      active.set(
        child,
        new Promise((resolve) =>
          child.once('close', () => {
            active.delete(child);
            resolve();
          }),
        ),
      );
      return child;
    };
    syncBuiltinESMExports();
    let caught;
    try {
      await acquire({ runnerTemp, workspace, signal: controller.signal });
      assert.fail('fixture must fail');
    } catch (error) {
      caught = error;
    }
    assert.equal(spawned, 1);
    if (mode === 'unconfirmed-close') {
      assert.ok(caught instanceof RuntimePrerequisiteTerminationUnconfirmedError);
      unconfirmed = caught;
      assert.equal((await readdir(runnerTemp)).length, 1);
      await caught.terminationConfirmed;
      await caught.resourcesReleased;
      unconfirmed = undefined;
    } else assert.equal(caught instanceof RuntimePrerequisiteTerminationUnconfirmedError, false);
    assert.deepEqual(await readdir(runnerTemp), []);
    cases.push(mode);
  }
  console.log(
    JSON.stringify({
      originalOfficialArchiveAdmission: true,
      substitutedExtractorProcessOnly: true,
      actualOsCloseAndStorageRetention: true,
      acceleratedListingDeadline: true,
      hostLaunched: false,
      cases,
    }),
  );
} finally {
  childProcess.spawn = originalSpawn;
  globalThis.setTimeout = originalSetTimeout;
  syncBuiltinESMExports();
  const closing = [...active.entries()].map(([child, closed]) => {
    try {
      child.kill('SIGKILL');
    } catch {}
    return closed;
  });
  await Promise.all(closing);
  if (unconfirmed) await unconfirmed.resourcesReleased;
  server.closeAllConnections();
  await new Promise((resolve) => server.close(resolve));
  await rm(work, { recursive: true, force: true });
}
