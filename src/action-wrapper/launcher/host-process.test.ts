import { chmod, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { PassThrough } from 'node:stream';
import { afterEach, describe, expect, it } from 'vitest';

import {
  closedChildEnvironment,
  encodeFrame,
  readSingleFrame,
  runHostProcess,
} from './host-process.js';

const roots: string[] = [];

afterEach(async () => {
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
      executablePath: script,
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
      executablePath: script,
      launchBytes: Buffer.from('{}'),
      tempRoot: root,
      signal: controller.signal,
    });
    setTimeout(() => controller.abort(), 30);
    await expect(running).rejects.toThrow('wrapper_host_process_failed');
  });
});

async function fixtureRoot(): Promise<string> {
  const root = await mkdtemp(path.join(tmpdir(), 'apr-w1-process-'));
  roots.push(root);
  return root;
}

async function executable(root: string, body: string): Promise<string> {
  const script = path.join(root, 'host-fixture');
  await writeFile(script, `#!${process.execPath}\n${body}`);
  await chmod(script, 0o700);
  return script;
}
