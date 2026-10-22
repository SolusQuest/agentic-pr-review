import { describe, expect, it, vi } from 'vitest';
import { UploadOperations } from './upload-operations.js';
import type { UploadStaging } from './upload-staging.js';
import type { SdkProcessOwner } from './sdk-process-owner.js';
import { UPLOAD_PROTOCOL, type UploadReply, type UploadRequest } from './upload-protocol.js';

const request: UploadRequest = {
  protocol: UPLOAD_PROTOCOL,
  build_discriminator: 'test',
  correlation_id: 'once',
  run_id: '1',
  run_attempt: '1',
  physical_name: `apr-object-${'a'.repeat(64)}-${'b'.repeat(64)}`,
  source_relative_path: 'source/carrier',
  source_size: '1',
  source_sha256: 'a'.repeat(64),
  retention_days: '1',
  timeout_ms: '1000',
};
const reply: UploadReply = {
  protocol: UPLOAD_PROTOCOL,
  build_discriminator: 'test',
  correlation_id: 'once',
  dispatch: 'dispatched',
  failure: 'none',
  receipt: { artifact_id: '1', archive_sha256: 'a'.repeat(64), archive_size: '100' },
};
const signal = () => new AbortController().signal;

describe('upload copy and process ownership', () => {
  it('reports cleanup failure after dispatch as unknown before replying and blocks quiescence', async () => {
    const cleanup = vi.fn().mockRejectedValue(new Error('synthetic cleanup failure'));
    const staging = {
      copy: vi.fn().mockResolvedValue({ operationDirectory: '/private/copy', cleanup }),
      dispose: vi.fn(),
    };
    const sdk = {
      upload: vi
        .fn()
        .mockResolvedValue({ reply, terminated: true, termination: Promise.resolve() }),
      awaitTermination: vi.fn().mockResolvedValue(true),
    };
    const operations = new UploadOperations(
      staging as unknown as UploadStaging,
      sdk as unknown as SdkProcessOwner,
      '1',
      '1',
    );
    expect(await operations.execute(request, signal())).toMatchObject({
      dispatch: 'dispatched',
      failure: 'outcome_unknown',
    });
    expect(cleanup).toHaveBeenCalled();
    expect(await operations.awaitQuiescence(10)).toBe(false);
    await expect(operations.dispose()).rejects.toThrow();
    expect(staging.dispose).not.toHaveBeenCalled();
  });
  it('retains the copy and admission until late confirmed termination; recovery does not replay', async () => {
    let finish!: () => void;
    const termination = new Promise<void>((resolve) => {
      finish = resolve;
    });
    const cleanup = vi.fn().mockResolvedValue(undefined);
    const staging = {
      copy: vi.fn().mockResolvedValue({ operationDirectory: '/private/copy', cleanup }),
      dispose: vi.fn(),
    };
    const sdk = {
      upload: vi.fn().mockResolvedValue({
        reply: { ...reply, failure: 'outcome_unknown' },
        terminated: false,
        termination,
      }),
    };
    const operations = new UploadOperations(
      staging as unknown as UploadStaging,
      sdk as unknown as SdkProcessOwner,
      '1',
      '1',
    );
    const result = await operations.execute(request, signal());
    expect(result.failure).toBe('outcome_unknown');
    expect(cleanup).not.toHaveBeenCalled();
    expect(
      (await operations.execute({ ...request, correlation_id: 'two' }, signal())).dispatch,
    ).toBe('not_dispatched');
    expect(sdk.upload).toHaveBeenCalledTimes(1);
    finish();
    await vi.waitFor(() => expect(cleanup).toHaveBeenCalledTimes(1));
    await new Promise((resolve) => setImmediate(resolve));
    expect(result.failure).toBe('outcome_unknown');
    expect(
      (await operations.execute({ ...request, correlation_id: 'three' }, signal())).dispatch,
    ).toBe('dispatched');
    expect(sdk.upload).toHaveBeenCalledTimes(2);
  });
});
