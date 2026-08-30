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
  HOST_STDERR_CAPTURE_MAXIMUM_BYTES,
  HostProcessTerminationUnconfirmedError,
  readSingleFrame,
  readTrustedProofBudgetReceiptLines,
  runHostProcess,
} from './host-process.js';
import { verifyPreparedPayload } from './prepared-payload.js';

const roots: string[] = [];
const handles: FileHandle[] = [];
const githubBudgetReceipt =
  'APR_R4_E2P_GITHUB_REQUEST_BUDGET {"authenticated_rest_requests":180,"authenticated_rest_limit":216,"anonymous_codeload_requests":1,"anonymous_codeload_limit":1,"rejected_requests":0,"measurement_only":true,"invalid_remaining_header":false,"terminal_rate_limited":false,"low_remaining_guard":false,"remaining_tail_reserve":1,"host_head_source_rest":{"raw":180,"primary":180,"not_modified":0,"secondary_points":180,"permission":0,"primary_rate_limited":0,"secondary_rate_limited":0,"combined_rate_limited":0,"invalid_rate_headers":0,"remaining_tail_required":0},"host_other_github_rest":{"raw":0,"primary":0,"not_modified":0,"secondary_points":0,"permission":0,"primary_rate_limited":0,"secondary_rate_limited":0,"combined_rate_limited":0,"invalid_rate_headers":0,"remaining_tail_required":0}}\n';
const controlBudgetReceipt =
  'APR_R4_E2P_CONTROL_REQUEST_BUDGET {"consumed":9,"limit":64,"primary":9,"not_modified":0,"secondary_points":13,"mutation_count":1,"remaining_tail_required":0,"remaining_tail_reserve":1,"permission_denied":0,"primary_rate_limited":0,"secondary_rate_limited":0,"combined_rate_limited":0,"invalid_remaining_header":false,"measurement_only":true,"rate_limited":false}\n';

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

  it('constructs a closed temp-only child environment without ambient leakage', () => {
    process.env.R4_AMBIENT_SECRET_CANARY = 'must-not-cross';
    expect(closedChildEnvironment('/tmp/apr-private')).toEqual({
      TMPDIR: '/tmp/apr-private',
      NO_COLOR: '1',
      DOTNET_NOLOGO: '1',
      DOTNET_CLI_TELEMETRY_OPTOUT: '1',
    });
    expect(closedChildEnvironment('/tmp/apr-private')).not.toHaveProperty(
      'R4_AMBIENT_SECRET_CANARY',
    );
    delete process.env.R4_AMBIENT_SECRET_CANARY;
  });

  it('forwards only the verified measurement profile to a protected child', () => {
    expect(closedChildEnvironment('/tmp/apr-private', 'measurement')).toEqual({
      TMPDIR: '/tmp/apr-private',
      NO_COLOR: '1',
      DOTNET_NOLOGO: '1',
      DOTNET_CLI_TELEMETRY_OPTOUT: '1',
      AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE: 'measurement',
    });
  });

  it('keeps the production cancellation grace beyond the complete S2 operation bound', () => {
    expect(HOST_CANCELLATION_RECONCILIATION_GRACE_MS).toBeGreaterThan(120_000);
  });

  it('admits only canonical proof-budget records from otherwise private Host stderr', async () => {
    const stream = new PassThrough();
    const reading = readTrustedProofBudgetReceiptLines(stream);
    stream.end(
      `private-provider-canary\n${controlBudgetReceipt}${githubBudgetReceipt}private-tail\n`,
    );

    await expect(reading).resolves.toEqual([githubBudgetReceipt, controlBudgetReceipt]);
  });

  it.each([
    githubBudgetReceipt,
    githubBudgetReceipt + githubBudgetReceipt + controlBudgetReceipt,
    githubBudgetReceipt.replace(
      '"authenticated_rest_limit":216',
      '"authenticated_rest_limit":217',
    ) + controlBudgetReceipt,
    githubBudgetReceipt.replace(
      '"invalid_remaining_header":false',
      '"invalid_remaining_header":true',
    ) + controlBudgetReceipt,
    githubBudgetReceipt.replace('"secondary_points":180', '"secondary_points":179') +
      controlBudgetReceipt,
    githubBudgetReceipt.replace('"terminal_rate_limited":false', '"terminal_rate_limited":true') +
      controlBudgetReceipt,
    githubBudgetReceipt.replace('"remaining_tail_reserve":1', '"remaining_tail_reserve":0') +
      controlBudgetReceipt,
    githubBudgetReceipt.replace('"raw":180', '"raw":179') + controlBudgetReceipt,
    githubBudgetReceipt.replace(',"primary_rate_limited":0', '') + controlBudgetReceipt,
    githubBudgetReceipt.replace('"primary_rate_limited":0', '"primary_rate_limited":1') +
      controlBudgetReceipt,
    githubBudgetReceipt.replace(
      '"remaining_tail_required":0}',
      '"remaining_tail_required":0,"unexpected":0}',
    ) + controlBudgetReceipt,
    githubBudgetReceipt +
      controlBudgetReceipt.replace('"measurement_only":true', '"measurement_only":false'),
    githubBudgetReceipt + controlBudgetReceipt.replace(',"primary_rate_limited":0', ''),
    githubBudgetReceipt +
      controlBudgetReceipt.replace('"primary_rate_limited":0', '"primary_rate_limited":1'),
    githubBudgetReceipt +
      controlBudgetReceipt.replace('"rate_limited":false', '"rate_limited":true'),
    githubBudgetReceipt +
      controlBudgetReceipt.replace(
        '"measurement_only":true',
        '"measurement_only":true,"unexpected":0',
      ),
    githubBudgetReceipt +
      controlBudgetReceipt.replace('"secondary_points":13', '"secondary_points":12'),
    githubBudgetReceipt +
      controlBudgetReceipt.replace('"remaining_tail_required":0', '"remaining_tail_required":1'),
    'APR_R4_E2P_GITHUB_REQUEST_BUDGET {malformed}\n' + controlBudgetReceipt,
    'x'.repeat(HOST_STDERR_CAPTURE_MAXIMUM_BYTES + 1) + githubBudgetReceipt + controlBudgetReceipt,
  ])(
    'suppresses incomplete, duplicate, malformed, or oversized Host stderr receipts',
    async (body) => {
      const stream = new PassThrough();
      const reading = readTrustedProofBudgetReceiptLines(stream);
      stream.end(body);
      await expect(reading).resolves.toEqual([]);
    },
  );
});

describe.runIf(process.platform === 'linux')('W1 real private Host process', () => {
  it('forwards the protected measurement profile and no other ambient environment', async () => {
    const root = await fixtureRoot();
    const script = await executable(
      root,
      `
const chunks = [];
for await (const chunk of process.stdin) chunks.push(chunk);
const expectedEnv = ['AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_NOLOGO','NO_COLOR','TMPDIR'];
if (JSON.stringify(Object.keys(process.env).sort()) !== JSON.stringify(expectedEnv)) process.exit(9);
if (process.env.AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE !== 'measurement') process.exit(10);
const body = Buffer.from('{"protected":true}');
const output = Buffer.alloc(4 + body.length);
output.writeUInt32BE(body.length, 0);
body.copy(output, 4);
process.stdout.write(output);
`,
    );
    process.env.R4_AMBIENT_SECRET_CANARY = 'must-not-cross';
    try {
      const result = await runHostProcess({
        executableHandle: await opened(script),
        launchBytes: Buffer.from('{}'),
        tempRoot: root,
        signal: new AbortController().signal,
        requestBudgetProfile: 'measurement',
      });
      expect(result).toMatchObject({
        exitCode: 0,
        completionBytes: Buffer.from('{"protected":true}'),
      });
    } finally {
      delete process.env.R4_AMBIENT_SECRET_CANARY;
    }
  });

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

    expect(result).toEqual({
      completionBytes: launchBytes,
      exitCode: 0,
      trustedProofBudgetReceiptLines: [],
    });
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
