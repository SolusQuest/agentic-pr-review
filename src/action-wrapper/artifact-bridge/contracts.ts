import { ARTIFACT_BRIDGE_LIMITS } from './limits.js';

export const ARTIFACT_BRIDGE_OPERATIONS = [
  'list_exact',
  'metadata',
  'download',
  'upload_immutable',
  'readback_exact',
  'delete_exact',
] as const;

export type ArtifactBridgeOperation = (typeof ARTIFACT_BRIDGE_OPERATIONS)[number];

export const ARTIFACT_BRIDGE_FAILURES = [
  'none',
  'cancelled',
  'invalid',
  'not_found',
  'incomplete',
  'duplicate',
  'conflict',
  'expired',
  'digest_mismatch',
  'outcome_unknown',
  'cleanup',
  'io',
] as const;

export type ArtifactBridgeFailure = (typeof ARTIFACT_BRIDGE_FAILURES)[number];

export type ArtifactBridgeMutationState = 'not_committed' | 'committed' | 'outcome_unknown';

export interface ArtifactReferenceWire {
  readonly name: string;
  readonly object_id: string;
}

export interface ArtifactMetadataWire extends ArtifactReferenceWire {
  readonly producing_run_id: string;
  readonly producing_run_attempt: string;
  readonly archive_digest: string;
  readonly encrypted_object_digest: string;
  readonly expires_at_unix_seconds: string;
  readonly size: string;
}

interface ArtifactCommandBase {
  readonly operation: ArtifactBridgeOperation;
  readonly correlation_id: string;
}

export interface ListExactCommand extends ArtifactCommandBase {
  readonly operation: 'list_exact';
  readonly name: string;
  readonly maximum_objects: string;
}

export interface MetadataCommand extends ArtifactCommandBase {
  readonly operation: 'metadata';
  readonly name: string;
  readonly object_id: string;
}

export interface DownloadCommand extends ArtifactCommandBase {
  readonly operation: 'download';
  readonly expected: ArtifactMetadataWire;
  readonly destination_relative_path: string;
  readonly maximum_bytes: string;
}

export interface UploadImmutableCommand extends ArtifactCommandBase {
  readonly operation: 'upload_immutable';
  readonly name: string;
  readonly source_relative_path: string;
  readonly encrypted_object_digest: string;
  readonly minimum_expires_at_unix_seconds: string;
}

export interface ReadBackExactCommand extends ArtifactCommandBase {
  readonly operation: 'readback_exact';
  readonly expected: ArtifactMetadataWire;
}

export interface DeleteExactCommand extends ArtifactCommandBase {
  readonly operation: 'delete_exact';
  readonly expected: ArtifactMetadataWire;
}

export type ArtifactBridgeCommand =
  | ListExactCommand
  | MetadataCommand
  | DownloadCommand
  | UploadImmutableCommand
  | ReadBackExactCommand
  | DeleteExactCommand;

export interface ArtifactBridgeCommandEnvelope {
  readonly build_discriminator: string;
  readonly payload: ArtifactBridgeCommand;
}

export interface ArtifactBridgeResult {
  readonly operation: ArtifactBridgeOperation;
  readonly correlation_id: string;
  readonly failure: ArtifactBridgeFailure;
  readonly mutation_state?: ArtifactBridgeMutationState;
  readonly complete?: boolean;
  readonly objects?: readonly ArtifactReferenceWire[];
  readonly metadata?: ArtifactMetadataWire;
}

export interface ArtifactBridgeResultEnvelope {
  readonly build_discriminator: string;
  readonly payload: ArtifactBridgeResult;
}

const encoder = new TextEncoder();
const lowerSha256 = /^[0-9a-f]{64}$/u;
const positiveDecimal = /^[1-9][0-9]*$/u;

export function parseArtifactBridgeCommandEnvelope(
  value: unknown,
): ArtifactBridgeCommandEnvelope | undefined {
  if (!isRecordWithKeys(value, ['build_discriminator', 'payload'])) {
    return undefined;
  }

  const buildDiscriminator = boundedText(value.build_discriminator, 128);
  const payload = parseCommand(value.payload);
  return buildDiscriminator && payload
    ? { build_discriminator: buildDiscriminator, payload }
    : undefined;
}

export function isValidArtifactBridgeResult(value: ArtifactBridgeResult): boolean {
  if (!isObject(value)) return false;
  if (
    !ARTIFACT_BRIDGE_OPERATIONS.includes(value.operation) ||
    !boundedText(value.correlation_id, ARTIFACT_BRIDGE_LIMITS.maximumCorrelationBytes) ||
    !ARTIFACT_BRIDGE_FAILURES.includes(value.failure)
  ) {
    return false;
  }

  if (value.mutation_state !== undefined) {
    if (value.operation !== 'upload_immutable' && value.operation !== 'delete_exact') {
      return false;
    }
    if (!['not_committed', 'committed', 'outcome_unknown'].includes(value.mutation_state)) {
      return false;
    }
  }

  if (value.objects !== undefined) {
    if (
      value.operation !== 'list_exact' ||
      value.objects.length > ARTIFACT_BRIDGE_LIMITS.maximumRecords ||
      !value.objects.every(isReference)
    ) {
      return false;
    }
  }

  if (value.metadata !== undefined && !isMetadata(value.metadata)) {
    return false;
  }

  const baseKeys = ['operation', 'correlation_id', 'failure'];
  switch (value.operation) {
    case 'list_exact':
      return value.failure === 'none'
        ? value.complete === true &&
            value.objects !== undefined &&
            hasOnlyKeys(value, [...baseKeys, 'complete', 'objects'])
        : value.complete === false &&
            value.objects === undefined &&
            hasOnlyKeys(value, [...baseKeys, 'complete']);
    case 'metadata':
    case 'download':
    case 'readback_exact':
      return value.failure === 'none'
        ? value.metadata !== undefined && hasOnlyKeys(value, [...baseKeys, 'metadata'])
        : value.metadata === undefined && hasOnlyKeys(value, baseKeys);
    case 'upload_immutable':
      if (value.mutation_state === undefined) return false;
      if (value.failure === 'none') {
        return (
          value.mutation_state === 'committed' &&
          value.metadata !== undefined &&
          hasOnlyKeys(value, [...baseKeys, 'mutation_state', 'metadata'])
        );
      }
      return (
        (value.metadata === undefined || value.mutation_state === 'committed') &&
        hasOnlyKeys(
          value,
          value.metadata === undefined
            ? [...baseKeys, 'mutation_state']
            : [...baseKeys, 'mutation_state', 'metadata'],
        )
      );
    case 'delete_exact':
      return (
        value.mutation_state !== undefined &&
        value.metadata === undefined &&
        (value.failure !== 'none' || value.mutation_state === 'committed') &&
        hasOnlyKeys(value, [...baseKeys, 'mutation_state'])
      );
  }
}

function parseCommand(value: unknown): ArtifactBridgeCommand | undefined {
  if (!isObject(value) || typeof value.operation !== 'string') {
    return undefined;
  }

  const correlation = boundedText(
    value.correlation_id,
    ARTIFACT_BRIDGE_LIMITS.maximumCorrelationBytes,
  );
  if (!correlation) return undefined;

  switch (value.operation) {
    case 'list_exact': {
      if (!isRecordWithKeys(value, ['operation', 'correlation_id', 'name', 'maximum_objects'])) {
        return undefined;
      }
      const name = opaqueName(value.name);
      const maximum = boundedPositiveDecimal(
        value.maximum_objects,
        ARTIFACT_BRIDGE_LIMITS.maximumRecords,
      );
      return name && maximum
        ? {
            operation: value.operation,
            correlation_id: correlation,
            name,
            maximum_objects: maximum,
          }
        : undefined;
    }
    case 'metadata': {
      if (!isRecordWithKeys(value, ['operation', 'correlation_id', 'name', 'object_id'])) {
        return undefined;
      }
      const name = opaqueName(value.name);
      const objectId = safePositiveDecimal(value.object_id);
      return name && objectId
        ? {
            operation: value.operation,
            correlation_id: correlation,
            name,
            object_id: objectId,
          }
        : undefined;
    }
    case 'download': {
      if (
        !isRecordWithKeys(value, [
          'operation',
          'correlation_id',
          'expected',
          'destination_relative_path',
          'maximum_bytes',
        ])
      ) {
        return undefined;
      }
      const expected = parseMetadata(value.expected);
      const destination = relativePath(value.destination_relative_path);
      const maximum = boundedPositiveDecimal(
        value.maximum_bytes,
        ARTIFACT_BRIDGE_LIMITS.maximumEncryptedObjectBytes,
      );
      return expected && destination && maximum
        ? {
            operation: value.operation,
            correlation_id: correlation,
            expected,
            destination_relative_path: destination,
            maximum_bytes: maximum,
          }
        : undefined;
    }
    case 'upload_immutable': {
      if (
        !isRecordWithKeys(value, [
          'operation',
          'correlation_id',
          'name',
          'source_relative_path',
          'encrypted_object_digest',
          'minimum_expires_at_unix_seconds',
        ])
      ) {
        return undefined;
      }
      const name = opaqueName(value.name);
      const source = relativePath(value.source_relative_path);
      const digest = sha256(value.encrypted_object_digest);
      const expiry = safePositiveDecimal(value.minimum_expires_at_unix_seconds);
      return name && source && digest && expiry
        ? {
            operation: value.operation,
            correlation_id: correlation,
            name,
            source_relative_path: source,
            encrypted_object_digest: digest,
            minimum_expires_at_unix_seconds: expiry,
          }
        : undefined;
    }
    case 'readback_exact':
    case 'delete_exact': {
      if (!isRecordWithKeys(value, ['operation', 'correlation_id', 'expected'])) {
        return undefined;
      }
      const expected = parseMetadata(value.expected);
      return expected
        ? {
            operation: value.operation,
            correlation_id: correlation,
            expected,
          }
        : undefined;
    }
    default:
      return undefined;
  }
}

export function parseMetadata(value: unknown): ArtifactMetadataWire | undefined {
  if (
    !isRecordWithKeys(value, [
      'name',
      'object_id',
      'producing_run_id',
      'producing_run_attempt',
      'archive_digest',
      'encrypted_object_digest',
      'expires_at_unix_seconds',
      'size',
    ])
  ) {
    return undefined;
  }

  const metadata: ArtifactMetadataWire = {
    name: opaqueName(value.name) ?? '',
    object_id: safePositiveDecimal(value.object_id) ?? '',
    producing_run_id: safePositiveDecimal(value.producing_run_id) ?? '',
    producing_run_attempt: safePositiveDecimal(value.producing_run_attempt) ?? '',
    archive_digest: sha256(value.archive_digest) ?? '',
    encrypted_object_digest: sha256(value.encrypted_object_digest) ?? '',
    expires_at_unix_seconds: safePositiveDecimal(value.expires_at_unix_seconds) ?? '',
    size:
      boundedPositiveDecimal(value.size, ARTIFACT_BRIDGE_LIMITS.maximumEncryptedObjectBytes) ?? '',
  };
  return isMetadata(metadata) ? metadata : undefined;
}

export function safePositiveDecimal(value: unknown): string | undefined {
  if (typeof value !== 'string' || !positiveDecimal.test(value)) {
    return undefined;
  }
  const number = Number(value);
  return Number.isSafeInteger(number) && number > 0 ? value : undefined;
}

export function sha256(value: unknown): string | undefined {
  return typeof value === 'string' && lowerSha256.test(value) ? value : undefined;
}

export function opaqueName(value: unknown): string | undefined {
  const text = boundedText(value, ARTIFACT_BRIDGE_LIMITS.maximumNameBytes);
  if (!text || /[\/"':<>|*?\r\n\\]/u.test(text)) return undefined;
  return text;
}

export function relativePath(value: unknown): string | undefined {
  const text = boundedText(value, ARTIFACT_BRIDGE_LIMITS.maximumRelativePathBytes);
  if (
    !text ||
    text.includes('\\') ||
    text.includes('\0') ||
    text.startsWith('/') ||
    /^[A-Za-z]:/u.test(text) ||
    /^[A-Za-z][A-Za-z0-9+.-]*:/u.test(text)
  ) {
    return undefined;
  }
  const segments = text.split('/');
  return segments.every(
    (segment) =>
      segment.length > 0 &&
      segment !== '.' &&
      segment !== '..' &&
      !/[\u0000-\u001f\u007f]/u.test(segment),
  )
    ? text
    : undefined;
}

function isMetadata(value: ArtifactMetadataWire): boolean {
  return parseMetadataWithoutRecursion(value);
}

function parseMetadataWithoutRecursion(value: ArtifactMetadataWire): boolean {
  return (
    opaqueName(value.name) !== undefined &&
    safePositiveDecimal(value.object_id) !== undefined &&
    safePositiveDecimal(value.producing_run_id) !== undefined &&
    safePositiveDecimal(value.producing_run_attempt) !== undefined &&
    sha256(value.archive_digest) !== undefined &&
    sha256(value.encrypted_object_digest) !== undefined &&
    safePositiveDecimal(value.expires_at_unix_seconds) !== undefined &&
    boundedPositiveDecimal(value.size, ARTIFACT_BRIDGE_LIMITS.maximumEncryptedObjectBytes) !==
      undefined
  );
}

function isReference(value: ArtifactReferenceWire): boolean {
  return opaqueName(value.name) !== undefined && safePositiveDecimal(value.object_id) !== undefined;
}

function boundedPositiveDecimal(value: unknown, maximum: number): string | undefined {
  const canonical = safePositiveDecimal(value);
  return canonical && Number(canonical) <= maximum ? canonical : undefined;
}

function boundedText(value: unknown, maximumBytes: number): string | undefined {
  if (
    typeof value !== 'string' ||
    value.length === 0 ||
    value.trim().length === 0 ||
    /[\r\n\u0000]/u.test(value)
  ) {
    return undefined;
  }
  try {
    const length = encoder.encode(value).byteLength;
    return length > 0 && length <= maximumBytes ? value : undefined;
  } catch {
    return undefined;
  }
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isRecordWithKeys(
  value: unknown,
  keys: readonly string[],
): value is Record<string, unknown> {
  if (!isObject(value)) return false;
  const actual = Object.keys(value);
  return actual.length === keys.length && keys.every((key) => key in value);
}

function hasOnlyKeys(value: object, keys: readonly string[]): boolean {
  const actual = Object.keys(value);
  return actual.length === keys.length && keys.every((key) => key in value);
}
