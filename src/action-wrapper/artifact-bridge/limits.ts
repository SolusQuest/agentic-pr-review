export const ARTIFACT_BRIDGE_LIMITS = Object.freeze({
  maximumNameBytes: 256,
  maximumCorrelationBytes: 256,
  maximumRelativePathBytes: 1_024,
  maximumEncryptedObjectBytes: 2 * 1024 * 1024,
  maximumStagingFileBytes: 4 * 1024 * 1024,
  maximumDocumentBytes: 256 * 1024,
  recordsPerPage: 100,
  maximumPages: 3,
  maximumRecords: 256,
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
