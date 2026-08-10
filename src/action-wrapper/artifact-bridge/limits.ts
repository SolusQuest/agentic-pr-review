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
  maximumTerminalCorrelations: 512,
});

export const ARTIFACT_ENVELOPE_DISCRIMINATOR = 'apr.private-artifact-envelope.s2';

export const ARTIFACT_ENVELOPE_ENTRY = 'artifact-envelope.json';
