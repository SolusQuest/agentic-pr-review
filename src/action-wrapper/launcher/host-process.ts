import { spawn } from 'node:child_process';
import path from 'node:path';
import type { Readable } from 'node:stream';
import { finished } from 'node:stream/promises';

import { H1_MAXIMUM_COMPLETION_DOCUMENT_BYTES } from './contracts.js';
import { fail } from './validation.js';

export const HOST_CANCELLATION_RECONCILIATION_GRACE_MS = 130_000;
export const HOST_POST_KILL_CLOSE_GRACE_MS = 5_000;

export class HostProcessTerminationUnconfirmedError extends Error {
  constructor() {
    super('wrapper_host_termination_unconfirmed');
    this.name = 'HostProcessTerminationUnconfirmedError';
  }
}

export interface HostProcessResult {
  readonly completionBytes: Buffer;
  readonly exitCode: number;
}

export interface HostProcessRequest {
  readonly executablePath: string;
  readonly launchBytes: Uint8Array;
  readonly tempRoot: string;
  readonly signal: AbortSignal;
  readonly cancellationKillGraceMs?: number;
  readonly postKillCloseGraceMs?: number;
}

export type HostProcessRunner = (request: HostProcessRequest) => Promise<HostProcessResult>;

export async function runHostProcess(request: HostProcessRequest): Promise<HostProcessResult> {
  if (request.signal.aborted) fail('wrapper_cancelled_before_spawn');
  const cancellationKillGraceMs = duration(
    request.cancellationKillGraceMs ?? HOST_CANCELLATION_RECONCILIATION_GRACE_MS,
  );
  const postKillCloseGraceMs = duration(
    request.postKillCloseGraceMs ?? HOST_POST_KILL_CLOSE_GRACE_MS,
  );
  let child: ReturnType<typeof spawn>;
  try {
    child = spawn(request.executablePath, [], {
      cwd: path.dirname(request.executablePath),
      env: closedChildEnvironment(request.tempRoot),
      shell: false,
      windowsHide: true,
      stdio: ['pipe', 'pipe', 'ignore'],
    });
  } catch {
    return fail('wrapper_host_process_failed');
  }
  let spawned = false;
  let closeObserved = false;
  let cancellationForwarded = false;
  let escalation: NodeJS.Timeout | undefined;
  let closeDeadline: NodeJS.Timeout | undefined;
  let rejectUnconfirmed!: (error: HostProcessTerminationUnconfirmedError) => void;
  const unconfirmedPromise = new Promise<never>((_resolve, reject) => {
    rejectUnconfirmed = reject;
  });
  const closePromise = new Promise<{
    readonly code: number | null;
    readonly signal: NodeJS.Signals | null;
  }>((resolve) => {
    child.once('close', (code, signal) => {
      closeObserved = true;
      resolve({ code, signal });
    });
  });
  let rejectProcessError!: (error: Error) => void;
  const processErrorPromise = new Promise<never>((_resolve, reject) => {
    rejectProcessError = reject;
  });
  const onSpawn = (): void => {
    spawned = true;
  };
  const onError = (): void => {
    rejectProcessError(new Error('wrapper_host_process_failed'));
  };
  child.once('spawn', onSpawn);
  child.once('error', onError);
  const armCloseDeadline = (): void => {
    if (closeDeadline || closeObserved) return;
    closeDeadline = setTimeout(
      () => rejectUnconfirmed(new HostProcessTerminationUnconfirmedError()),
      postKillCloseGraceMs,
    );
  };
  const finalKill = (): void => {
    if (closeObserved) return;
    if (child.exitCode === null && child.signalCode === null) {
      try {
        child.kill('SIGKILL');
      } catch {
        // The bounded close deadline remains authoritative when signal delivery fails.
      }
    }
    armCloseDeadline();
  };
  const forwardCancellation = (): void => {
    if (cancellationForwarded) return;
    cancellationForwarded = true;
    try {
      child.kill('SIGTERM');
    } catch {
      // Final escalation and its close deadline still provide the fixed liveness bound.
    }
    escalation = setTimeout(finalKill, cancellationKillGraceMs);
  };
  request.signal.addEventListener('abort', forwardCancellation, { once: true });
  if (request.signal.aborted) forwardCancellation();
  const outputPromise = readSingleFrame(child.stdout!, H1_MAXIMUM_COMPLETION_DOCUMENT_BYTES);
  try {
    child.stdin!.end(encodeFrame(request.launchBytes));
    const [, completionBytes, closed] = await Promise.race([
      Promise.all([finished(child.stdin!), outputPromise, closePromise]),
      processErrorPromise,
      unconfirmedPromise,
    ]);
    if (closed.signal !== null || closed.code === null) fail('wrapper_host_process_failed');
    return { completionBytes, exitCode: closed.code };
  } catch (error) {
    if (error instanceof HostProcessTerminationUnconfirmedError) throw error;
    if (!spawned) return fail('wrapper_host_process_failed');
    if (!closeObserved) {
      finalKill();
      await Promise.race([closePromise, unconfirmedPromise]);
    }
    return fail('wrapper_host_process_failed');
  } finally {
    if (escalation) clearTimeout(escalation);
    if (closeDeadline) clearTimeout(closeDeadline);
    request.signal.removeEventListener('abort', forwardCancellation);
    child.off('spawn', onSpawn);
    child.off('error', onError);
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

function duration(value: number): number {
  if (!Number.isSafeInteger(value) || value < 1) fail('wrapper_host_process_failed');
  return value;
}
