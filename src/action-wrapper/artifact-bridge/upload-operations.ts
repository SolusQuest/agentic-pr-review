import { performance } from 'node:perf_hooks';
import { SDK_CHILD_PROTOCOL } from './sdk-child-protocol.js';
import { SdkProcessOwner } from './sdk-process-owner.js';
import { UploadStaging, type OwnedUploadCopy } from './upload-staging.js';
import { UPLOAD_PROTOCOL, type UploadReply, type UploadRequest } from './upload-protocol.js';
import type { UploadExecutor } from './upload-server.js';

export class UploadOperations implements UploadExecutor {
  private sealed = false;
  private active: Promise<void> | undefined;
  private cleanupFailed = false;

  constructor(
    private readonly staging: UploadStaging,
    private readonly sdk: SdkProcessOwner,
    private readonly runId: string,
    private readonly runAttempt: string,
  ) {}

  sealAdmission(): void {
    this.sealed = true;
  }

  async execute(request: UploadRequest, signal: AbortSignal): Promise<UploadReply> {
    let reply: UploadReply = {
      protocol: UPLOAD_PROTOCOL,
      build_discriminator: request.build_discriminator,
      correlation_id: request.correlation_id,
      dispatch: 'not_dispatched',
      failure: 'invalid',
      receipt: null,
    };
    if (
      this.sealed ||
      this.active ||
      this.cleanupFailed ||
      request.run_id !== this.runId ||
      request.run_attempt !== this.runAttempt
    )
      return reply;
    if (signal.aborted) return { ...reply, failure: 'cancelled' };
    let finish!: () => void;
    this.active = new Promise<void>((resolve) => {
      finish = resolve;
    });
    const release = (): void => {
      this.active = undefined;
      finish();
    };
    const started = performance.now();
    const deadline = new AbortController();
    const timer = setTimeout(() => deadline.abort(), Number(request.timeout_ms));
    const sourceSignal = AbortSignal.any([signal, deadline.signal]);
    let copy: OwnedUploadCopy | undefined;
    let deferred = false;
    const cleanup = async (): Promise<void> => {
      try {
        await copy?.cleanup();
        copy = undefined;
      } catch {
        this.cleanupFailed = true;
        throw new Error('upload_copy_cleanup_failed');
      }
    };
    try {
      copy = await this.staging.copy(
        request.source_relative_path,
        Number(request.source_size),
        request.source_sha256,
        sourceSignal,
      );
      sourceSignal.throwIfAborted();
      const result = await this.sdk.upload(
        {
          protocol: SDK_CHILD_PROTOCOL,
          build_discriminator: request.build_discriminator,
          correlation_id: request.correlation_id,
          physical_name: request.physical_name,
          operation_directory: copy.operationDirectory,
          retention_days: request.retention_days,
        },
        Number(request.timeout_ms) - (performance.now() - started),
      );
      reply = result.reply;
      if (!result.terminated) {
        deferred = true;
        // The observer never changes the already reported ambiguous outcome.
        void result.termination
          .then(cleanup)
          .catch(() => {
            this.cleanupFailed = true;
          })
          .finally(release);
        return reply;
      }
      await cleanup();
      return reply;
    } catch {
      if (copy && !deferred) {
        try {
          await cleanup();
        } catch {
          this.cleanupFailed = true;
        }
      }
      return {
        ...reply,
        failure:
          reply.dispatch === 'dispatched'
            ? 'outcome_unknown'
            : this.cleanupFailed
              ? 'cleanup'
              : sourceSignal.aborted
                ? 'cancelled'
                : 'invalid',
      };
    } finally {
      clearTimeout(timer);
      if (!deferred) release();
    }
  }

  async awaitQuiescence(timeoutMs: number): Promise<boolean> {
    this.sealAdmission();
    const started = performance.now();
    if (!(await this.sdk.awaitTermination(timeoutMs))) return false;
    if (this.active) {
      let timer: NodeJS.Timeout | undefined;
      try {
        const settled = await Promise.race([
          this.active.then(() => true),
          new Promise<false>((resolve) => {
            timer = setTimeout(
              () => resolve(false),
              Math.max(0, timeoutMs - (performance.now() - started)),
            );
          }),
        ]);
        if (!settled) return false;
      } finally {
        clearTimeout(timer);
      }
    }
    return !this.cleanupFailed;
  }

  async dispose(): Promise<void> {
    this.sealAdmission();
    if (this.active || this.cleanupFailed) throw new Error('upload_resources_unconfirmed');
    await this.staging.dispose();
  }
}
