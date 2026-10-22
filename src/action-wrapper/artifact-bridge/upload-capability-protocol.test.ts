import { describe, expect, it } from 'vitest';
import {
  decodeUploadJson,
  isUploadReply,
  isUploadRequest,
  UPLOAD_PROTOCOL,
  UPLOAD_LIMITS,
} from './upload-protocol.js';

const request = {
  protocol: UPLOAD_PROTOCOL,
  build_discriminator: 'r4-w2',
  correlation_id: 'once',
  run_id: '7001',
  run_attempt: '2',
  physical_name: `apr-object-${'a'.repeat(64)}-${'b'.repeat(64)}`,
  source_relative_path: 'csharp/operation/carrier.json',
  source_size: String(UPLOAD_LIMITS.carrierBytes),
  source_sha256: 'c'.repeat(64),
  retention_days: '90',
  timeout_ms: '120000',
};
const receipt = {
  artifact_id: '1',
  archive_sha256: 'a'.repeat(64),
  archive_size: String(UPLOAD_LIMITS.archiveBytes),
};

describe('closed upload capability', () => {
  it('admits the existing maximum carrier and archive without increasing caps', () => {
    expect(isUploadRequest(request)).toBe(true);
    expect(
      isUploadReply({
        protocol: UPLOAD_PROTOCOL,
        build_discriminator: 'r4-w2',
        correlation_id: 'once',
        dispatch: 'dispatched',
        failure: 'none',
        receipt,
      }),
    ).toBe(true);
    expect(
      isUploadRequest({ ...request, source_size: String(UPLOAD_LIMITS.carrierBytes + 1) }),
    ).toBe(false);
  });
  it.each([
    { operation: 'upload_immutable' },
    { purpose: 'recovery' },
    { retention_days: '91' },
    { timeout_ms: '120001' },
    { source_size: '01' },
    { run_id: '9007199254740992' },
    { source_relative_path: '../escape' },
    { source_relative_path: '/absolute' },
    { source_relative_path: 'a//b' },
    { source_relative_path: 'a/./b' },
    { source_relative_path: 'a\\b' },
    { correlation_id: '\ud800' },
    { source_sha256: 'A'.repeat(64) },
  ])('refuses malformed or expanded capability %j', (change) => {
    expect(isUploadRequest({ ...request, ...change })).toBe(false);
  });
  it('requires all eleven members', () => {
    for (const key of Object.keys(request)) {
      const copy: Record<string, unknown> = { ...request };
      delete copy[key];
      expect(isUploadRequest(copy)).toBe(false);
    }
  });
  it('does not accept contradictory receipt/dispatch claims', () => {
    const base = {
      protocol: UPLOAD_PROTOCOL,
      build_discriminator: 'r4-w2',
      correlation_id: 'once',
    };
    for (const dispatch of ['not_dispatched', 'dispatched']) {
      for (const failure of [
        'none',
        'invalid',
        'cancelled',
        'conflict',
        'io',
        'outcome_unknown',
        'cleanup',
      ]) {
        for (const item of [null, receipt]) {
          const expected =
            dispatch === 'not_dispatched'
              ? item === null && ['invalid', 'cancelled', 'io', 'cleanup'].includes(failure)
              : failure === 'none'
                ? item !== null
                : failure === 'conflict'
                  ? item === null
                  : failure === 'outcome_unknown';
          expect(isUploadReply({ ...base, dispatch, failure, receipt: item })).toBe(expected);
        }
      }
    }
  });
  it.each(['{"x":1,"x":2}', '{"x":1,"\\u0078":2}', '\ufeff{}'])(
    'rejects ambiguous JSON %s',
    (text) => {
      expect(() => decodeUploadJson(Buffer.from(text))).toThrow();
    },
  );
  it('rejects invalid UTF8 and oversized documents before semantic parsing', () => {
    expect(() => decodeUploadJson(Buffer.from([0xff]))).toThrow();
    expect(() => decodeUploadJson(Buffer.alloc(UPLOAD_LIMITS.documentBytes + 1))).toThrow();
  });
});
