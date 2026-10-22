import { strictParseArtifactBridgeJson } from './strict-json.js';

export const UPLOAD_PROTOCOL = 'apr.sdk.upload.cr2';
export const UPLOAD_LIMITS = Object.freeze({
  documentBytes: 256 * 1024,
  carrierBytes: 45_399_694,
  archiveBytes: 45_413_722,
  timeoutMs: 120_000,
  observationMs: 30_000,
  terminalCorrelations: 4096,
});

export interface UploadRequest {
  readonly protocol: typeof UPLOAD_PROTOCOL;
  readonly build_discriminator: string;
  readonly correlation_id: string;
  readonly run_id: string;
  readonly run_attempt: string;
  readonly physical_name: string;
  readonly source_relative_path: string;
  readonly source_size: string;
  readonly source_sha256: string;
  readonly retention_days: string;
  readonly timeout_ms: string;
}

export interface UploadReceipt {
  readonly artifact_id: string;
  readonly archive_sha256: string;
  readonly archive_size: string;
}

export interface UploadReply {
  readonly protocol: typeof UPLOAD_PROTOCOL;
  readonly build_discriminator: string;
  readonly correlation_id: string;
  readonly dispatch: 'not_dispatched' | 'dispatched';
  readonly failure:
    | 'none'
    | 'invalid'
    | 'cancelled'
    | 'conflict'
    | 'io'
    | 'outcome_unknown'
    | 'cleanup';
  readonly receipt: UploadReceipt | null;
}

const requestKeys = [
  'protocol',
  'build_discriminator',
  'correlation_id',
  'run_id',
  'run_attempt',
  'physical_name',
  'source_relative_path',
  'source_size',
  'source_sha256',
  'retention_days',
  'timeout_ms',
] as const;
const replyKeys = [
  'protocol',
  'build_discriminator',
  'correlation_id',
  'dispatch',
  'failure',
  'receipt',
];

export function closedRecord(
  value: unknown,
  keys: readonly string[],
): value is Record<string, unknown> {
  return (
    typeof value === 'object' &&
    value !== null &&
    !Array.isArray(value) &&
    Object.keys(value).length === keys.length &&
    keys.every((key) => Object.hasOwn(value, key))
  );
}

export function positiveDecimal(
  value: unknown,
  maximum = Number.MAX_SAFE_INTEGER,
): value is string {
  return (
    typeof value === 'string' &&
    /^[1-9][0-9]*$/u.test(value) &&
    Number.isSafeInteger(Number(value)) &&
    Number(value) <= maximum
  );
}

export function uploadDigest(value: unknown): value is string {
  return typeof value === 'string' && /^[a-f0-9]{64}$/u.test(value);
}

export function boundedUploadText(value: unknown, maximum: number): value is string {
  return (
    typeof value === 'string' &&
    value.length > 0 &&
    value.trim().length > 0 &&
    !/[\u0000-\u001f\u007f]/u.test(value) &&
    Buffer.byteLength(value, 'utf8') <= maximum &&
    Buffer.from(value, 'utf8').toString('utf8') === value
  );
}

export function uploadRelativePath(value: unknown): value is string {
  return (
    boundedUploadText(value, 1024) &&
    !value.includes('\\') &&
    !value.includes(':') &&
    value.split('/').every((part) => part !== '' && part !== '.' && part !== '..')
  );
}

export function isUploadRequest(value: unknown): value is UploadRequest {
  return (
    closedRecord(value, requestKeys) &&
    value.protocol === UPLOAD_PROTOCOL &&
    boundedUploadText(value.build_discriminator, 256) &&
    boundedUploadText(value.correlation_id, 256) &&
    positiveDecimal(value.run_id) &&
    positiveDecimal(value.run_attempt) &&
    typeof value.physical_name === 'string' &&
    /^apr-object-[a-f0-9]{64}-[a-f0-9]{64}$/u.test(value.physical_name) &&
    uploadRelativePath(value.source_relative_path) &&
    positiveDecimal(value.source_size, UPLOAD_LIMITS.carrierBytes) &&
    uploadDigest(value.source_sha256) &&
    positiveDecimal(value.retention_days, 90) &&
    positiveDecimal(value.timeout_ms, UPLOAD_LIMITS.timeoutMs)
  );
}

export function isUploadReceipt(value: unknown): value is UploadReceipt {
  return (
    closedRecord(value, ['artifact_id', 'archive_sha256', 'archive_size']) &&
    positiveDecimal(value.artifact_id) &&
    uploadDigest(value.archive_sha256) &&
    positiveDecimal(value.archive_size, UPLOAD_LIMITS.archiveBytes)
  );
}

export function isUploadReply(value: unknown): value is UploadReply {
  if (
    !closedRecord(value, replyKeys) ||
    value.protocol !== UPLOAD_PROTOCOL ||
    !boundedUploadText(value.build_discriminator, 256) ||
    !boundedUploadText(value.correlation_id, 256)
  )
    return false;
  if (value.dispatch === 'not_dispatched')
    return (
      value.receipt === null &&
      ['invalid', 'cancelled', 'io', 'cleanup'].includes(value.failure as string)
    );
  return (
    value.dispatch === 'dispatched' &&
    (value.failure === 'none'
      ? isUploadReceipt(value.receipt)
      : value.failure === 'conflict'
        ? value.receipt === null
        : value.failure === 'outcome_unknown' &&
          (value.receipt === null || isUploadReceipt(value.receipt)))
  );
}

export function decodeUploadJson(bytes: Uint8Array): unknown {
  if (
    bytes.length === 0 ||
    bytes.length > UPLOAD_LIMITS.documentBytes ||
    (bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf)
  )
    throw new Error('upload_frame_invalid');
  return strictParseArtifactBridgeJson(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
}

export function encodeUploadFrame(value: unknown): Buffer {
  const bytes = Buffer.from(JSON.stringify(value), 'utf8');
  if (bytes.length === 0 || bytes.length > UPLOAD_LIMITS.documentBytes)
    throw new Error('upload_frame_invalid');
  const frame = Buffer.alloc(4 + bytes.length);
  frame.writeUInt32BE(bytes.length);
  bytes.copy(frame, 4);
  return frame;
}
