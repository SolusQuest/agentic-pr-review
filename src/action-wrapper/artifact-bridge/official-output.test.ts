import { DefaultArtifactClient } from '@actions/artifact';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, it, vi } from 'vitest';

import {
  OfficialCallError,
  OfficialCallTimeoutError,
  runContainedOfficialCall,
} from './official-output.js';
import { OfficialArtifactOperations } from './official-artifact-operations.js';
import { ArtifactBridgeStaging } from './staging.js';
import { digestBytes } from './transport-envelope.js';

describe('official artifact output containment', () => {
  it('suppresses workflow-command, stdout, stderr, and thrown canaries', async () => {
    const captured: string[] = [];
    const originalStdout = process.stdout.write;
    const originalStderr = process.stderr.write;
    process.stdout.write = ((chunk: string | Uint8Array) => {
      captured.push(String(chunk));
      return true;
    }) as typeof process.stdout.write;
    process.stderr.write = ((chunk: string | Uint8Array) => {
      captured.push(String(chunk));
      return true;
    }) as typeof process.stderr.write;
    try {
      const controller = new AbortController();
      await expect(
        runContainedOfficialCall(
          async () => {
            process.stdout.write(
              '::warning::token=SECRET signed=https://blob/?sig=CANARY path=C:\\secret',
            );
            process.stderr.write('UNRELATED_ENV_SECRET');
            throw new Error('RAW_ERROR_CANARY');
          },
          1_000,
          controller.signal,
        ),
      ).rejects.toBeInstanceOf(OfficialCallError);
    } finally {
      process.stdout.write = originalStdout;
      process.stderr.write = originalStderr;
    }
    expect(captured.join('')).toBe('');
  });

  it('keeps suppression installed until a timed-out SDK promise settles', async () => {
    const captured: string[] = [];
    const originalStdout = process.stdout.write;
    process.stdout.write = ((chunk: string | Uint8Array) => {
      captured.push(String(chunk));
      return true;
    }) as typeof process.stdout.write;
    let timeout: OfficialCallTimeoutError | undefined;
    try {
      await runContainedOfficialCall(
        async () => {
          await new Promise((resolve) => setTimeout(resolve, 20));
          process.stdout.write('LATE_SECRET_CANARY');
          return true;
        },
        1,
        new AbortController().signal,
      );
    } catch (error) {
      expect(error).toBeInstanceOf(OfficialCallTimeoutError);
      timeout = error as OfficialCallTimeoutError;
    }
    await timeout?.settled;
    process.stdout.write = originalStdout;
    expect(captured.join('')).toBe('');
  });

  it('does not dispatch a queued call after its logical wait expires', async () => {
    vi.useFakeTimers();
    let finishFirst!: () => void;
    let secondDispatched = false;
    try {
      const first = runContainedOfficialCall(
        async () =>
          await new Promise<void>((resolve) => {
            finishFirst = resolve;
          }),
        1_000,
        new AbortController().signal,
      );
      await vi.advanceTimersByTimeAsync(1);
      const second = runContainedOfficialCall(
        async () => {
          secondDispatched = true;
        },
        100,
        new AbortController().signal,
      );
      const secondRejected = expect(second).rejects.toBeInstanceOf(OfficialCallTimeoutError);
      await vi.advanceTimersByTimeAsync(100);
      await secondRejected;
      expect(secondDispatched).toBe(false);
      let thirdDispatched = false;
      const third = runContainedOfficialCall(
        async () => {
          thirdDispatched = true;
        },
        1_000,
        new AbortController().signal,
      );
      await vi.advanceTimersByTimeAsync(1);
      expect(thirdDispatched).toBe(false);
      finishFirst();
      await first;
      await third;
      expect(thirdDispatched).toBe(true);
    } finally {
      vi.useRealTimers();
    }
  });

  it('contains output at the actual DefaultArtifactClient production binding', async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'apr-output-binding-'));
    const sourceParent = path.join(root, 'csharp', 'op');
    await mkdir(sourceParent, { recursive: true });
    const bytes = Buffer.from('ciphertext');
    await writeFile(path.join(sourceParent, 'object.bin'), bytes);
    const artifactClient = new DefaultArtifactClient();
    artifactClient.uploadArtifact = async () => {
      process.stdout.write('::warning::TOKEN_CANARY https://blob/?sig=SIGNED_URL PATH_CANARY');
      process.stderr.write('UNRELATED_SECRET_CANARY');
      throw Object.assign(new Error('RAW_PACKAGE_ERROR'), { status: 503 });
    };
    const unsupported = async (): Promise<never> => {
      throw new Error('unexpected REST call');
    };
    const operations = new OfficialArtifactOperations({
      owner: 'owner',
      repository: 'repository',
      currentRunId: '7001',
      currentRunAttempt: '2',
      artifactToken: 'TOKEN_CANARY',
      artifactClient,
      actions: {
        listArtifactsForRepo: unsupported,
        getArtifact: unsupported,
        getWorkflowRunAttempt: unsupported,
        deleteArtifact: unsupported,
      },
      staging: await ArtifactBridgeStaging.create(root),
    });
    const captured: string[] = [];
    const stdout = process.stdout.write;
    const stderr = process.stderr.write;
    process.stdout.write = ((chunk: string | Uint8Array) => {
      captured.push(String(chunk));
      return true;
    }) as typeof process.stdout.write;
    process.stderr.write = ((chunk: string | Uint8Array) => {
      captured.push(String(chunk));
      return true;
    }) as typeof process.stderr.write;
    try {
      const result = await operations.execute(
        {
          operation: 'upload_immutable',
          correlation_id: 'package-binding',
          name: 'opaque-state',
          source_relative_path: 'csharp/op/object.bin',
          encrypted_object_digest: digestBytes(bytes),
          minimum_expires_at_unix_seconds: '2000000000',
        },
        new AbortController().signal,
      );
      expect(result.failure).toBe('outcome_unknown');
      expect(result.mutation_state).toBe('outcome_unknown');
    } finally {
      process.stdout.write = stdout;
      process.stderr.write = stderr;
      await rm(root, { recursive: true, force: true });
    }
    expect(captured.join('')).toBe('');
  });
});
