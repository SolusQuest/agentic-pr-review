import { describe, expect, it } from 'vitest';

import {
  type ArtifactBridgeResult,
  isValidArtifactBridgeResult,
  parseArtifactBridgeCommandEnvelope,
  relativePath,
  safePositiveDecimal,
} from './contracts.js';
import { ARTIFACT_BRIDGE_LIMITS } from './limits.js';

const digest = 'a'.repeat(64);
const metadata = {
  name: 'state-object',
  object_id: '42',
  producing_run_id: '7001',
  producing_run_attempt: '2',
  archive_digest: digest,
  encrypted_object_digest: digest,
  expires_at_unix_seconds: '2000000000',
  size: '9',
};

describe('artifact bridge command contract', () => {
  it.each([
    { operation: 'list_exact', name: 'state', maximum_objects: '256' },
    { operation: 'metadata', name: 'state', object_id: '42' },
    {
      operation: 'download',
      expected: metadata,
      destination_relative_path: 'csharp/op/object.bin',
      maximum_bytes: '2097152',
    },
    {
      operation: 'upload_immutable',
      name: 'state',
      source_relative_path: 'csharp/op/object.bin',
      encrypted_object_digest: digest,
      minimum_expires_at_unix_seconds: '2000000000',
    },
    { operation: 'readback_exact', expected: metadata },
    { operation: 'delete_exact', expected: metadata },
  ])('accepts the closed $operation shape', (payload) => {
    expect(
      parseArtifactBridgeCommandEnvelope({
        build_discriminator: 'build-1',
        payload: { correlation_id: 'correlation-1', ...payload },
      }),
    ).toBeDefined();
  });

  it('rejects unknown, wrong-case, and open fields', () => {
    expect(
      parseArtifactBridgeCommandEnvelope({
        build_discriminator: 'build-1',
        payload: {
          operation: 'list_exact',
          correlation_id: 'correlation-1',
          name: 'state',
          maximum_objects: '1',
          extra: true,
        },
      }),
    ).toBeUndefined();
    expect(
      parseArtifactBridgeCommandEnvelope({
        BuildDiscriminator: 'build-1',
        payload: {},
      }),
    ).toBeUndefined();
  });

  it('enforces UTF-8 byte bounds rather than UTF-16 length', () => {
    const atCap = '界'.repeat(85) + 'a';
    const aboveCap = `${atCap}b`;
    expect(new TextEncoder().encode(atCap)).toHaveLength(ARTIFACT_BRIDGE_LIMITS.maximumNameBytes);
    expect(
      parseArtifactBridgeCommandEnvelope({
        build_discriminator: 'build-1',
        payload: {
          operation: 'list_exact',
          correlation_id: 'c',
          name: atCap,
          maximum_objects: '1',
        },
      }),
    ).toBeDefined();
    expect(
      parseArtifactBridgeCommandEnvelope({
        build_discriminator: 'build-1',
        payload: {
          operation: 'list_exact',
          correlation_id: 'c',
          name: aboveCap,
          maximum_objects: '1',
        },
      }),
    ).toBeUndefined();
  });

  it('accepts correlation and relative-path caps and rejects cap plus one', () => {
    const correlationAtCap = 'c'.repeat(ARTIFACT_BRIDGE_LIMITS.maximumCorrelationBytes);
    const pathAtCap = 'p'.repeat(ARTIFACT_BRIDGE_LIMITS.maximumRelativePathBytes);
    const atCap = parseArtifactBridgeCommandEnvelope({
      build_discriminator: 'build-1',
      payload: {
        operation: 'download',
        correlation_id: correlationAtCap,
        expected: metadata,
        destination_relative_path: pathAtCap,
        maximum_bytes: '1',
      },
    });
    expect(atCap).toBeDefined();
    expect(
      parseArtifactBridgeCommandEnvelope({
        build_discriminator: 'build-1',
        payload: { ...atCap!.payload, correlation_id: `${correlationAtCap}c` },
      }),
    ).toBeUndefined();
    expect(
      parseArtifactBridgeCommandEnvelope({
        build_discriminator: 'build-1',
        payload: { ...atCap!.payload, destination_relative_path: `${pathAtCap}p` },
      }),
    ).toBeUndefined();
  });

  it.each([
    '/absolute',
    '../escape',
    'a/../b',
    'a\\b',
    'C:/drive',
    'https:payload',
    'a//b',
    'a/./b',
  ])('rejects unsafe relative path %s', (candidate) => {
    expect(relativePath(candidate)).toBeUndefined();
  });

  it('rejects noncanonical and JavaScript-unsafe artifact identities', () => {
    expect(safePositiveDecimal('1')).toBe('1');
    expect(safePositiveDecimal('0')).toBeUndefined();
    expect(safePositiveDecimal('01')).toBeUndefined();
    expect(safePositiveDecimal('-1')).toBeUndefined();
    expect(safePositiveDecimal('9007199254740992')).toBeUndefined();
  });
});

describe('artifact bridge result contract', () => {
  it('accepts only the closed success shape for each operation', () => {
    const base = { correlation_id: 'correlation-1', failure: 'none' } as const;
    const results: ArtifactBridgeResult[] = [
      { ...base, operation: 'list_exact', complete: true, objects: [] },
      { ...base, operation: 'metadata', metadata },
      { ...base, operation: 'download', metadata },
      {
        ...base,
        operation: 'upload_immutable',
        mutation_state: 'committed',
        metadata,
      },
      { ...base, operation: 'readback_exact', metadata },
      { ...base, operation: 'delete_exact', mutation_state: 'committed' },
    ];

    expect(results.every(isValidArtifactBridgeResult)).toBe(true);
  });

  it.each([
    { operation: 'list_exact', correlation_id: 'c', failure: 'none', complete: true },
    {
      operation: 'metadata',
      correlation_id: 'c',
      failure: 'none',
      metadata,
      extra: true,
    },
    {
      operation: 'upload_immutable',
      correlation_id: 'c',
      failure: 'none',
      mutation_state: 'outcome_unknown',
      metadata,
    },
    {
      operation: 'delete_exact',
      correlation_id: 'c',
      failure: 'none',
      mutation_state: 'not_committed',
    },
  ])('rejects an open or inconsistent result %#', (result) => {
    expect(isValidArtifactBridgeResult(result as ArtifactBridgeResult)).toBe(false);
  });
});
