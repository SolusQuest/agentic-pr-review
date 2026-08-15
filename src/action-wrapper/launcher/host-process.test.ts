import { createHash } from 'node:crypto';
import {
  access,
  chmod,
  copyFile,
  mkdtemp,
  open,
  readFile,
  rename,
  rm,
  writeFile,
  type FileHandle,
} from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { PassThrough } from 'node:stream';
import { afterEach, describe, expect, it } from 'vitest';

import {
  closedChildEnvironment,
  encodeFrame,
  HOST_CANCELLATION_RECONCILIATION_GRACE_MS,
  HostProcessTerminationUnconfirmedError,
  readSingleFrame,
  runHostProcess,
} from './host-process.js';
import { verifyPreparedPayload } from './prepared-payload.js';

const roots: string[] = [];
const handles: FileHandle[] = [];

afterEach(async () => {
  await Promise.all(handles.splice(0).map(async (handle) => await handle.close()));
  await Promise.all(
    roots.splice(0).map(async (root) => await rm(root, { recursive: true, force: true })),
  );
});

describe('W1 private Host framing', () => {
  it('reads exactly one declared frame across fragmented chunks', async () => {
    const stream = new PassThrough();
    const reading = readSingleFrame(stream, 32);
    for (const byte of encodeFrame(Buffer.from('result'))) stream.write(Buffer.of(byte));
    stream.end();
    await expect(reading).resolves.toEqual(Buffer.from('result'));
  });

  it.each([
    Buffer.alloc(0),
    Buffer.alloc(3),
    Buffer.alloc(4),
    Buffer.concat([encodeFrame(Buffer.from('x')), Buffer.of(0x20)]),
  ])('rejects zero, truncated, or out-of-frame bytes', async (bytes) => {
    const stream = new PassThrough();
    stream.end(bytes);
    await expect(readSingleFrame(stream, 32)).rejects.toThrow('wrapper_frame_invalid');
  });

  it('constructs a closed temp-only child environment', () => {
    expect(closedChildEnvironment('/tmp/apr-private')).toEqual({
      TMPDIR: '/tmp/apr-private',
      NO_COLOR: '1',
      DOTNET_NOLOGO: '1',
      DOTNET_CLI_TELEMETRY_OPTOUT: '1',
    });
  });

  it('keeps the production cancellation grace beyond the complete S2 operation bound', () => {
    expect(HOST_CANCELLATION_RECONCILIATION_GRACE_MS).toBeGreaterThan(120_000);
  });
});

describe.runIf(process.platform === 'linux')('W1 real private Host process', () => {
  it('launches one verified executable with no args, closed environment, and exact exit', async () => {
    const root = await fixtureRoot();
    const script = await executable(
      root,
      `
const chunks = [];
for await (const chunk of process.stdin) chunks.push(chunk);
const frame = Buffer.concat(chunks);
const size = frame.readUInt32BE(0);
const launch = JSON.parse(frame.subarray(4, 4 + size).toString('utf8'));
const expectedEnv = ['DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_NOLOGO','NO_COLOR','TMPDIR'];
if (process.argv.length !== 2 || JSON.stringify(Object.keys(process.env).sort()) !== JSON.stringify(expectedEnv)) process.exit(9);
const completion = {
  build_discriminator: launch.build_discriminator,
  status: 'reviewed',
  exit_class: 'success',
  process_exit_code: 0,
  summary: { reviewed_sha: '${'a'.repeat(40)}', publication_url: 'https://github.com/o/r/pull/1', finding_count: 0, state_disposition: 'accepted' },
  annotations: []
};
const body = Buffer.from(JSON.stringify(completion));
const output = Buffer.alloc(4 + body.length);
output.writeUInt32BE(body.length, 0);
body.copy(output, 4);
process.stdout.write(output);
`,
    );
    const result = await runHostProcess({
      executableHandle: await opened(script),
      launchBytes: Buffer.from(JSON.stringify({ build_discriminator: 'r4-h1' })),
      tempRoot: root,
      signal: new AbortController().signal,
    });
    expect(result.exitCode).toBe(0);
    expect(JSON.parse(result.completionBytes.toString('utf8')).status).toBe('reviewed');
  });

  it('forwards cancellation once and does not hang on a live child', async () => {
    const root = await fixtureRoot();
    const script = await executable(
      root,
      `
process.stdin.resume();
process.on('SIGTERM', () => process.exit(1));
setInterval(() => {}, 1000);
`,
    );
    const controller = new AbortController();
    const running = runHostProcess({
      executableHandle: await opened(script),
      launchBytes: Buffer.from('{}'),
      tempRoot: root,
      signal: controller.signal,
    });
    setTimeout(() => controller.abort(), 30);
    await expect(running).rejects.toThrow('wrapper_host_process_failed');
  });

  it('allows a Host to reconcile beyond the retired two-second grace', async () => {
    const root = await fixtureRoot();
    const ready = path.join(root, 'ready');
    const script = await executable(
      root,
      `
const { writeFileSync } = require('node:fs');
process.stdin.resume();
process.on('SIGTERM', () => {
  setTimeout(() => {
    const body = Buffer.from('{"reconciled":true}');
    const output = Buffer.alloc(4 + body.length);
    output.writeUInt32BE(body.length, 0);
    body.copy(output, 4);
    process.stdout.write(output, () => process.exit(0));
  }, 2100);
});
writeFileSync(${JSON.stringify(ready)}, 'ready');
setInterval(() => {}, 1000);
`,
    );
    const controller = new AbortController();
    const started = Date.now();
    const running = runHostProcess({
      executableHandle: await opened(script),
      launchBytes: Buffer.from('{}'),
      tempRoot: root,
      signal: controller.signal,
    });
    await waitForFile(ready);
    controller.abort();
    await expect(running).resolves.toMatchObject({
      completionBytes: Buffer.from('{"reconciled":true}'),
      exitCode: 0,
    });
    expect(Date.now() - started).toBeGreaterThan(2_000);
  });

  it('fails boundedly when final kill cannot prove close because a descendant retains stdout', async () => {
    const root = await fixtureRoot();
    const ready = path.join(root, 'ready');
    const script = await executable(
      root,
      `
const { spawn } = require('node:child_process');
const { writeFileSync } = require('node:fs');
spawn(process.execPath, ['-e', 'setTimeout(() => process.exit(0), 1000)'], {
  stdio: ['ignore', process.stdout, 'ignore']
});
process.stdin.resume();
process.on('SIGTERM', () => {});
writeFileSync(${JSON.stringify(ready)}, 'ready');
setInterval(() => {}, 1000);
`,
    );
    const controller = new AbortController();
    const started = Date.now();
    const running = runHostProcess({
      executableHandle: await opened(script),
      launchBytes: Buffer.from('{}'),
      tempRoot: root,
      signal: controller.signal,
      cancellationKillGraceMs: 20,
      postKillCloseGraceMs: 30,
    });
    await waitForFile(ready);
    controller.abort();
    await expect(running).rejects.toBeInstanceOf(HostProcessTerminationUnconfirmedError);
    expect(Date.now() - started).toBeLessThan(500);
  });

  it('executes the verified opened identity after the admitted pathname is replaced', async () => {
    const root = await fixtureRoot();
    const script = await copiedExecutable(root, '/usr/bin/cat', 'verified-host');
    const replacement = await copiedExecutable(root, '/usr/bin/false', 'replacement-host');
    const bytes = await readFile(script);
    const prepared = await verifyPreparedPayload({
      trustedRoot: root,
      executableRelativePath: path.basename(script),
      payloadSha256: createHash('sha256').update(bytes).digest('hex'),
      actionSourceSha: 'a'.repeat(40),
      buildDiscriminator: 'r4-h1',
      wrapperBuildDiscriminator: 'r4-h1',
    });
    handles.push(prepared.executableHandle);
    await rename(replacement, script);
    const launchBytes = Buffer.from('{"identity":"verified"}');

    const result = await runHostProcess({
      executableHandle: prepared.executableHandle,
      launchBytes,
      tempRoot: root,
      signal: new AbortController().signal,
    });

    expect(result).toEqual({ completionBytes: launchBytes, exitCode: 0 });
  });
});

async function fixtureRoot(): Promise<string> {
  const root = await mkdtemp(path.join(tmpdir(), 'apr-w1-process-'));
  roots.push(root);
  return root;
}

async function executable(root: string, body: string, name = 'host-fixture'): Promise<string> {
  const script = path.join(root, name);
  await writeFile(script, `#!${process.execPath}\n${body}`);
  await chmod(script, 0o700);
  return script;
}

async function opened(executablePath: string): Promise<FileHandle> {
  const handle = await open(executablePath, 'r');
  handles.push(handle);
  return handle;
}

async function copiedExecutable(root: string, source: string, name: string): Promise<string> {
  const target = path.join(root, name);
  await copyFile(source, target);
  await chmod(target, 0o700);
  return target;
}

async function waitForFile(filePath: string): Promise<void> {
  const deadline = Date.now() + 2_000;
  while (Date.now() < deadline) {
    try {
      await access(filePath);
      return;
    } catch {
      await new Promise<void>((resolve) => setTimeout(resolve, 10));
    }
  }
  throw new Error('fixture_not_ready');
}
