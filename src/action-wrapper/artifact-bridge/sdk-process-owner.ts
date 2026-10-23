import { spawn, type ChildProcess } from 'node:child_process';
import type { Duplex } from 'node:stream';
import { readUploadFrame, writeUploadFrame } from './upload-framing.js';
import type { SdkChildRequest } from './sdk-child-protocol.js';
import {
  boundedUploadText,
  isUploadReply,
  UPLOAD_LIMITS,
  UPLOAD_PROTOCOL,
  type UploadReply,
} from './upload-protocol.js';

export interface SdkProcessOutcome {
  readonly reply: UploadReply;
  readonly terminated: boolean;
  readonly termination: Promise<void>;
}

/** Tracks OS lifetime independently of SDK receipt or request observation. */
export class SdkProcessOwner {
  private active: Promise<void> | undefined;
  private sealed = false;

  constructor(
    private readonly entryPath: string,
    private readonly environment: NodeJS.ProcessEnv,
  ) {}

  seal(): void {
    this.sealed = true;
  }

  async awaitTermination(milliseconds: number): Promise<boolean> {
    this.seal();
    if (!this.active) return true;
    return await bounded(
      this.active.then(() => true),
      milliseconds,
      false,
    );
  }

  async upload(
    request: SdkChildRequest,
    remainingMilliseconds: number,
  ): Promise<SdkProcessOutcome> {
    const base: UploadReply = {
      protocol: UPLOAD_PROTOCOL,
      build_discriminator: request.build_discriminator,
      correlation_id: request.correlation_id,
      dispatch: 'not_dispatched',
      failure: 'io',
      receipt: null,
    };
    const refused = (): SdkProcessOutcome => ({
      reply: base,
      terminated: true,
      termination: Promise.resolve(),
    });
    if (this.sealed || this.active || remainingMilliseconds <= 0) return refused();
    let environment: NodeJS.ProcessEnv;
    try {
      environment = sdkEnvironment(this.environment);
    } catch {
      return refused();
    }
    let resolveTermination!: () => void;
    const termination = new Promise<void>((resolve) => {
      resolveTermination = resolve;
    });
    this.active = termination;
    let dispatched = false;
    let terminated = false;
    let forced = false;
    let receipt: UploadReply | undefined;
    let child: ChildProcess;
    try {
      child = spawn(process.execPath, [this.entryPath], {
        env: environment,
        stdio: ['ignore', 'ignore', 'ignore', 'pipe'],
        windowsHide: true,
        shell: false,
      });
    } catch {
      this.active = undefined;
      resolveTermination();
      return refused();
    }
    const channel = child.stdio[3] as Duplex;
    let exited = false;
    let spawnFailed = false;
    let closed = false;
    let exitCode: number | null = null;
    let exitSignal: NodeJS.Signals | null = null;
    const requestController = new AbortController();
    const settle = (): void => {
      if (!closed || (!exited && !spawnFailed) || terminated) return;
      terminated = true;
      requestController.abort();
      this.active = undefined;
      resolveTermination();
    };
    child.once('error', () => {
      spawnFailed = !dispatched;
    });
    const terminate = (): void => {
      forced = true;
      try {
        child.kill('SIGKILL');
      } catch {
        // A failed signal does not prove exit. Keep the original close observer.
      }
    };
    child.once('exit', (code, signal) => {
      exited = true;
      exitCode = code;
      exitSignal = signal;
    });
    child.once('close', () => {
      closed = true;
      settle();
    });
    const message = readUploadFrame(channel, requestController.signal)
      .then((value) => {
        if (
          isUploadReply(value) &&
          value.build_discriminator === request.build_discriminator &&
          value.correlation_id === request.correlation_id &&
          value.dispatch === 'dispatched'
        )
          receipt = value;
      })
      .catch(() => {
        /* Missing/invalid IPC is uncertainty, never raw child text. */
      });
    child.once('spawn', () => {
      dispatched = true;
      void writeUploadFrame(channel, request).catch(() => {
        terminate();
      });
    });
    const observed = await bounded(
      termination.then(() => true),
      Math.min(UPLOAD_LIMITS.observationMs, remainingMilliseconds),
      false,
    );
    if (!observed) {
      terminate();
      // No added wait budget. The retained observer alone can confirm later close.
    } else await message;
    const reply = !dispatched
      ? base
      : observed && !forced && exitCode === 0 && exitSignal === null && receipt
        ? receipt
        : {
            ...base,
            dispatch: 'dispatched' as const,
            failure: 'outcome_unknown' as const,
            receipt: receipt?.receipt ?? null,
          };
    return { reply, terminated, termination };
  }
}

export function sdkEnvironment(source: NodeJS.ProcessEnv): NodeJS.ProcessEnv {
  if (source.NODE_OPTIONS || source.NODE_PATH) throw new Error('sdk_environment_invalid');
  const result: NodeJS.ProcessEnv = {};
  for (const key of ['ACTIONS_RUNTIME_TOKEN', 'ACTIONS_RESULTS_URL']) {
    const value = source[key];
    if (!boundedUploadText(value, 16 * 1024)) throw new Error('sdk_environment_invalid');
    result[key] = value;
  }
  for (const key of [
    'GITHUB_SERVER_URL',
    'HTTP_PROXY',
    'HTTPS_PROXY',
    'NO_PROXY',
    'http_proxy',
    'https_proxy',
    'no_proxy',
  ]) {
    const value = source[key];
    if (value !== undefined) {
      if (!boundedUploadText(value, 16 * 1024)) throw new Error('sdk_environment_invalid');
      result[key] = value;
    }
  }
  return result;
}

async function bounded<T>(promise: Promise<T>, milliseconds: number, fallback: T): Promise<T> {
  let timer: NodeJS.Timeout | undefined;
  try {
    return await Promise.race([
      promise,
      new Promise<T>((resolve) => {
        timer = setTimeout(() => resolve(fallback), Math.max(0, milliseconds));
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}
