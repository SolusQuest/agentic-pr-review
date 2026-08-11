import { spawn } from 'node:child_process';
import path from 'node:path';
import type { Readable } from 'node:stream';
import { finished } from 'node:stream/promises';

import { H1_MAXIMUM_COMPLETION_DOCUMENT_BYTES } from './contracts.js';
import { fail } from './validation.js';

const CANCELLATION_KILL_GRACE_MS = 2_000;

export interface HostProcessResult {
  readonly completionBytes: Buffer;
  readonly exitCode: number;
}

export interface HostProcessRequest {
  readonly executablePath: string;
  readonly launchBytes: Uint8Array;
  readonly tempRoot: string;
  readonly signal: AbortSignal;
}

export type HostProcessRunner = (request: HostProcessRequest) => Promise<HostProcessResult>;

export async function runHostProcess(request: HostProcessRequest): Promise<HostProcessResult> {
  if (request.signal.aborted) fail('wrapper_cancelled_before_spawn');
  const child = spawn(request.executablePath, [], {
    cwd: path.dirname(request.executablePath),
    env: closedChildEnvironment(request.tempRoot),
    shell: false,
    windowsHide: true,
    stdio: ['pipe', 'pipe', 'ignore'],
  });
  let cancellationForwarded = false;
  let escalation: NodeJS.Timeout | undefined;
  const forwardCancellation = (): void => {
    if (cancellationForwarded) return;
    cancellationForwarded = true;
    child.kill('SIGTERM');
    escalation = setTimeout(() => child.kill('SIGKILL'), CANCELLATION_KILL_GRACE_MS);
    escalation.unref();
  };
  request.signal.addEventListener('abort', forwardCancellation, { once: true });
  const closePromise = new Promise<{
    readonly code: number | null;
    readonly signal: string | null;
  }>((resolve, reject) => {
    child.once('error', () => reject(new Error('wrapper_host_process_failed')));
    child.once('close', (code, signal) => resolve({ code, signal }));
  });
  const outputPromise = readSingleFrame(child.stdout, H1_MAXIMUM_COMPLETION_DOCUMENT_BYTES);
  try {
    child.stdin.end(encodeFrame(request.launchBytes));
    const [, completionBytes, closed] = await Promise.all([
      finished(child.stdin),
      outputPromise,
      closePromise,
    ]);
    if (closed.signal !== null || closed.code === null) fail('wrapper_host_process_failed');
    return { completionBytes, exitCode: closed.code };
  } catch {
    child.kill('SIGKILL');
    await closePromise.catch(() => undefined);
    return fail('wrapper_host_process_failed');
  } finally {
    if (escalation) clearTimeout(escalation);
    request.signal.removeEventListener('abort', forwardCancellation);
  }
}

export function encodeFrame(document: Uint8Array): Buffer {
  if (document.byteLength < 1 || document.byteLength > 0xffff_ffff) {
    fail('wrapper_frame_invalid');
  }
  const frame = Buffer.allocUnsafe(4 + document.byteLength);
  frame.writeUInt32BE(document.byteLength, 0);
  Buffer.from(document).copy(frame, 4);
  return frame;
}

export async function readSingleFrame(stream: Readable, maximumBytes: number): Promise<Buffer> {
  const chunks: Buffer[] = [];
  let total = 0;
  for await (const value of stream) {
    const chunk = Buffer.from(value as Uint8Array);
    total += chunk.byteLength;
    if (total > maximumBytes + 4) fail('wrapper_frame_invalid');
    chunks.push(chunk);
  }
  const frame = Buffer.concat(chunks, total);
  if (frame.byteLength < 4) fail('wrapper_frame_invalid');
  const declared = frame.readUInt32BE(0);
  if (declared < 1 || declared > maximumBytes || frame.byteLength !== declared + 4) {
    fail('wrapper_frame_invalid');
  }
  return frame.subarray(4);
}

export function closedChildEnvironment(tempRoot: string): NodeJS.ProcessEnv {
  return {
    TMPDIR: tempRoot,
    NO_COLOR: '1',
    DOTNET_NOLOGO: '1',
    DOTNET_CLI_TELEMETRY_OPTOUT: '1',
  };
}
