// Mirrors OpaqueStoreCapacity: these size the carrier, not SESSION admission.
const innerEnvelopeBytes = 32 * 1024 * 1024;
const generationBytes = innerEnvelopeBytes + 256 * 1024 + 16 * 1024;
const physicalCopyBytes = generationBytes + 16 * 1024;
const controlWrapperBytes = 16 * 1024 + 132;
const acceptanceRecoveryBytes =
  64 * 1024 + controlWrapperBytes + physicalCopyBytes + controlWrapperBytes + 1024;
const recoveryRecordBytes = acceptanceRecoveryBytes + 64 * 1024;
const targetEnvelopeBytes = recoveryRecordBytes + controlWrapperBytes;
const anchorBytes = targetEnvelopeBytes + 2048;
const maximumEncryptedObjectBytes = anchorBytes + controlWrapperBytes;
const { maximumBase64Bytes, maximumStagingFileBytes, maximumArchiveBytes } = artifactReadLimits(
  maximumEncryptedObjectBytes,
);

export const ARTIFACT_BRIDGE_LIMITS = Object.freeze({
  maximumNameBytes: 256,
  maximumCorrelationBytes: 256,
  maximumRelativePathBytes: 1_024,
  maximumEncryptedObjectBytes,
  maximumBase64Bytes,
  maximumStagingFileBytes,
  maximumArchiveBytes,
  maximumDocumentBytes: 256 * 1024,
  recordsPerPage: 100,
  maximumPages: 3,
  maximumRecords: 256,
  maximumRepositoryRecords: 1_024,
  maximumRepositoryPages: 11,
  requestTimeoutMs: 30_000,
  logicalOperationTimeoutMs: 120_000,
  maximumActiveCorrelations: 32,
  // One complete state transaction can legitimately cross the former 512-entry
  // boundary while retaining both current and previous state keys. Keep the
  // registry bounded, but leave enough room for the real Host route to finish.
  maximumTerminalCorrelations: 2_048,
});

export const ARTIFACT_ENVELOPE_DISCRIMINATOR = 'apr.private-artifact-envelope.s2';

export const ARTIFACT_ENVELOPE_ENTRY = 'artifact-envelope.json';

export function artifactReadLimits(maximumBytes: number) {
  if (
    !Number.isSafeInteger(maximumBytes) ||
    maximumBytes < 1 ||
    maximumBytes > maximumEncryptedObjectBytes
  ) {
    throw new RangeError('artifact_read_limit_invalid');
  }
  const maximumBase64Bytes = 4 * Math.ceil(maximumBytes / 3);
  const maximumStagingFileBytes = maximumBase64Bytes + 290;
  return {
    maximumEncryptedObjectBytes: maximumBytes,
    maximumBase64Bytes,
    maximumStagingFileBytes,
    maximumArchiveBytes:
      maximumStagingFileBytes +
      Math.ceil(maximumStagingFileBytes / 4096) +
      Math.ceil(maximumStagingFileBytes / 16384) +
      Math.ceil(maximumStagingFileBytes / 33554432) +
      13 +
      158,
  };
}
