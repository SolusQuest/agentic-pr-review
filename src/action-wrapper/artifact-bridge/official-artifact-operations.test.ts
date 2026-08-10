import type { ArtifactClient } from '@actions/artifact';
import { mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { ArtifactActionsRestClient } from './official-artifact-operations.js';
import { OfficialArtifactOperations } from './official-artifact-operations.js';
import { ArtifactBridgeStaging } from './staging.js';
import { digestBytes, writeArtifactTransportEnvelope } from './transport-envelope.js';
import { ARTIFACT_ENVELOPE_ENTRY } from './limits.js';
import { createTestZip } from './zip-test-helper.js';

const roots: string[] = [];

afterEach(async () => {
  await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true })));
});

describe('repository-wide exact artifact enumeration', () => {
  it('returns all 256 exact records over three complete pages', async () => {
    const artifacts = Array.from({ length: 256 }, (_, index) => ({
      id: index + 1,
      name: 'opaque-state',
      size_in_bytes: 1,
      expired: false,
      expires_at: '2030-01-01T00:00:00Z',
    }));
    const calls: number[] = [];
    const operations = await createOperations({
      listArtifactsForRepo: async (input) => {
        calls.push(input.page);
        const start = (input.page - 1) * input.per_page;
        return {
          status: 200,
          data: {
            total_count: artifacts.length,
            artifacts: artifacts.slice(start, start + input.per_page),
          },
        };
      },
    });

    const result = await operations.execute(
      {
        operation: 'list_exact',
        correlation_id: 'list-256',
        name: 'opaque-state',
        maximum_objects: '256',
      },
      new AbortController().signal,
    );

    expect(result.failure).toBe('none');
    expect(result.complete).toBe(true);
    expect(result.objects).toHaveLength(256);
    expect(calls).toEqual([1, 2, 3]);
  });

  it('returns incomplete with no partial authority at 257 records', async () => {
    const operations = await createOperations({
      listArtifactsForRepo: async () => ({
        status: 200,
        data: {
          total_count: 257,
          artifacts: [],
        },
      }),
    });
    const result = await operations.execute(
      {
        operation: 'list_exact',
        correlation_id: 'list-257',
        name: 'opaque-state',
        maximum_objects: '256',
      },
      new AbortController().signal,
    );
    expect(result).toEqual({
      operation: 'list_exact',
      correlation_id: 'list-257',
      failure: 'incomplete',
      complete: false,
    });
  });

  it('rejects a page above the fixed 100-record response bound', async () => {
    const operations = await createOperations({
      listArtifactsForRepo: async () => ({
        status: 200,
        data: {
          total_count: 101,
          artifacts: Array.from({ length: 101 }, (_, index) => ({
            id: index + 1,
            name: 'opaque-state',
            size_in_bytes: 1,
            expired: false,
            expires_at: '2030-01-01T00:00:00Z',
          })),
        },
      }),
    });
    const result = await operations.execute(
      {
        operation: 'list_exact',
        correlation_id: 'page-101',
        name: 'opaque-state',
        maximum_objects: '256',
      },
      new AbortController().signal,
    );
    expect(result).toEqual({
      operation: 'list_exact',
      correlation_id: 'page-101',
      failure: 'incomplete',
      complete: false,
    });
  });

  it.each(['changing total', 'late-page failure'])('fails closed for %s', async (caseName) => {
    let page = 0;
    const operations = await createOperations({
      listArtifactsForRepo: async () => {
        page += 1;
        if (caseName === 'late-page failure' && page === 2) {
          throw Object.assign(new Error('canary'), { status: 503 });
        }
        return {
          status: 200,
          data: {
            total_count: caseName === 'changing total' && page === 2 ? 199 : 200,
            artifacts: Array.from({ length: 100 }, (_, index) => ({
              id: page === 1 ? index + 1 : 101 + index,
              name: 'opaque-state',
              size_in_bytes: 1,
              expired: false,
              expires_at: '2030-01-01T00:00:00Z',
            })),
          },
        };
      },
    });
    const result = await operations.execute(
      {
        operation: 'list_exact',
        correlation_id: `list-${caseName}`,
        name: 'opaque-state',
        maximum_objects: '256',
      },
      new AbortController().signal,
    );
    expect(result.failure).not.toBe('none');
    expect(result.complete).toBe(false);
    expect(result.objects).toBeUndefined();
  });

  it('rejects a duplicate artifact id without returning partial records', async () => {
    const operations = await createOperations({
      listArtifactsForRepo: async () => ({
        status: 200,
        data: {
          total_count: 2,
          artifacts: [
            {
              id: 1,
              name: 'opaque-state',
              size_in_bytes: 1,
              expired: false,
              expires_at: '2030-01-01T00:00:00Z',
            },
            {
              id: 1,
              name: 'opaque-state',
              size_in_bytes: 1,
              expired: false,
              expires_at: '2030-01-01T00:00:00Z',
            },
          ],
        },
      }),
    });
    const result = await operations.execute(
      {
        operation: 'list_exact',
        correlation_id: 'list-duplicate',
        name: 'opaque-state',
        maximum_objects: '256',
      },
      new AbortController().signal,
    );
    expect(result).toEqual({
      operation: 'list_exact',
      correlation_id: 'list-duplicate',
      failure: 'duplicate',
      complete: false,
    });
  });

  it('fails the logical operation when an otherwise valid response crosses 120 seconds', async () => {
    const readings = [0, 0, 120_000];
    const operations = await createOperations(
      {
        listArtifactsForRepo: async () => ({
          status: 200,
          data: { total_count: 0, artifacts: [] },
        }),
      },
      () => readings.shift() ?? 120_000,
    );
    const result = await operations.execute(
      {
        operation: 'list_exact',
        correlation_id: 'logical-timeout',
        name: 'opaque-state',
        maximum_objects: '1',
      },
      new AbortController().signal,
    );
    expect(result).toEqual({
      operation: 'list_exact',
      correlation_id: 'logical-timeout',
      failure: 'cancelled',
      complete: false,
    });
  });
});

describe('artifact-specific producing attempt authority', () => {
  it('does not assign the latest attempt to two same-name artifacts from one run', async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'apr-attempt-test-'));
    roots.push(root);
    const staging = await ArtifactBridgeStaging.create(root);
    const archives = new Map<number, Buffer>();
    for (const [id, attempt] of [
      [41, '1'],
      [42, '2'],
    ] as const) {
      const envelopeRoot = path.join(root, `envelope-${id}`);
      await mkdir(envelopeRoot);
      const encrypted = Buffer.from(`ciphertext-${attempt}`);
      const envelopePath = await writeArtifactTransportEnvelope(
        envelopeRoot,
        '7001',
        attempt,
        encrypted,
        digestBytes(encrypted),
      );
      archives.set(
        id,
        createTestZip([
          {
            name: ARTIFACT_ENVELOPE_ENTRY,
            data: await readFile(envelopePath),
          },
        ]),
      );
    }
    const requestedAttempts: number[] = [];
    const actions: ArtifactActionsRestClient = {
      listArtifactsForRepo: async () => {
        throw new Error('unexpected list');
      },
      getArtifact: async (input) => {
        const archive = archives.get(input.artifact_id)!;
        return {
          status: 200,
          data: {
            id: input.artifact_id,
            name: 'opaque-state',
            size_in_bytes: archive.length,
            expired: false,
            expires_at: '2030-01-01T00:00:00Z',
            digest: `sha256:${digestBytes(archive)}`,
            workflow_run: { id: 7001 },
          },
        };
      },
      getWorkflowRunAttempt: async (input) => {
        requestedAttempts.push(input.attempt_number);
        return {
          status: 200,
          data: { id: input.run_id, run_attempt: input.attempt_number },
        };
      },
      deleteArtifact: async () => {
        throw new Error('unexpected delete');
      },
    };
    const artifactClient: ArtifactClient = {
      uploadArtifact: async () => {
        throw new Error('unexpected upload');
      },
      downloadArtifact: async (id, options) => {
        const destination = options?.path;
        if (!destination) throw new Error('missing destination');
        await writeFile(path.join(destination, 'artifact.zip'), archives.get(id)!);
        return { downloadPath: destination, digestMismatch: false };
      },
      listArtifacts: async () => {
        throw new Error('unexpected list');
      },
      getArtifact: async () => {
        throw new Error('unexpected get');
      },
      deleteArtifact: async () => {
        throw new Error('unexpected delete');
      },
    };
    const operations = new OfficialArtifactOperations({
      owner: 'owner',
      repository: 'repository',
      currentRunId: '7001',
      currentRunAttempt: '2',
      artifactToken: 'SYNTHETIC_TOKEN',
      artifactClient,
      actions,
      staging,
    });

    const first = await operations.execute(
      {
        operation: 'metadata',
        correlation_id: 'attempt-1',
        name: 'opaque-state',
        object_id: '41',
      },
      new AbortController().signal,
    );
    const second = await operations.execute(
      {
        operation: 'metadata',
        correlation_id: 'attempt-2',
        name: 'opaque-state',
        object_id: '42',
      },
      new AbortController().signal,
    );

    expect(first.metadata?.producing_run_attempt).toBe('1');
    expect(second.metadata?.producing_run_attempt).toBe('2');
    expect(requestedAttempts).toEqual([1, 2]);
  });
});

describe('official artifact lifecycle', () => {
  it('uploads, verifies, downloads, reads back, and authorizes exact deletion', async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'apr-lifecycle-test-'));
    roots.push(root);
    const staging = await ArtifactBridgeStaging.create(root);
    const sourceDirectory = path.join(root, 'source');
    const destinationDirectory = path.join(root, 'destination');
    await mkdir(sourceDirectory);
    await mkdir(destinationDirectory);
    const encrypted = Buffer.from('opaque-encrypted-state');
    await writeFile(path.join(sourceDirectory, 'object.bin'), encrypted);

    const now = Date.parse('2029-01-01T00:00:00Z');
    const minimumExpiry = Math.floor(now / 1000) + 3600;
    const expiresAt = minimumExpiry + 86400;
    let nextId = 42;
    let stored:
      | {
          readonly id: number;
          readonly name: string;
          readonly archive: Buffer;
          readonly archiveDigest: string;
        }
      | undefined;
    let deleted = false;
    let expired = false;
    let downloadCalls = 0;
    let deleteCalls = 0;

    const artifactClient: ArtifactClient = {
      uploadArtifact: async (name, files, operationRoot, options) => {
        expect(operationRoot).toMatch(new RegExp(`${escapeRegExp(root)}[/\\\\]op-`, 'u'));
        expect(files).toHaveLength(1);
        expect(options?.compressionLevel).toBe(0);
        expect(options?.retentionDays).toBe(1);
        const archive = createTestZip([
          {
            name: ARTIFACT_ENVELOPE_ENTRY,
            data: await readFile(files[0]!),
          },
        ]);
        stored = {
          id: nextId++,
          name,
          archive,
          archiveDigest: digestBytes(archive),
        };
        deleted = false;
        return {
          id: stored.id,
          size: archive.length,
          digest: `sha256:${stored.archiveDigest}`,
        };
      },
      downloadArtifact: async (id, options) => {
        downloadCalls += 1;
        expect(stored?.id).toBe(id);
        expect(options?.expectedHash).toBe(`sha256:${stored?.archiveDigest}`);
        const destination = options?.path;
        if (!destination || !stored) throw new Error('missing synthetic artifact');
        await writeFile(path.join(destination, 'artifact.zip'), stored.archive);
        return { downloadPath: destination, digestMismatch: false };
      },
      listArtifacts: async () => {
        throw new Error('unexpected artifact-client list');
      },
      getArtifact: async () => {
        throw new Error('unexpected artifact-client get');
      },
      deleteArtifact: async () => {
        throw new Error('unexpected artifact-client delete');
      },
    };
    const actions: ArtifactActionsRestClient = {
      listArtifactsForRepo: async () => {
        throw new Error('unexpected repository list');
      },
      getArtifact: async (input) => {
        if (deleted || !stored) {
          throw Object.assign(new Error('synthetic absence'), { status: 404 });
        }
        expect(input.artifact_id).toBe(stored.id);
        return {
          status: 200,
          data: {
            id: stored.id,
            name: stored.name,
            size_in_bytes: stored.archive.length,
            expired,
            expires_at: new Date(expiresAt * 1000).toISOString(),
            digest: `sha256:${stored.archiveDigest}`,
            workflow_run: { id: 7001 },
          },
        };
      },
      getWorkflowRunAttempt: async (input) => ({
        status: 200,
        data: { id: input.run_id, run_attempt: input.attempt_number },
      }),
      deleteArtifact: async (input) => {
        deleteCalls += 1;
        expect(input.artifact_id).toBe(stored?.id);
        deleted = true;
        return { status: 204, data: undefined };
      },
    };
    const operations = new OfficialArtifactOperations({
      owner: 'owner',
      repository: 'repository',
      currentRunId: '7001',
      currentRunAttempt: '2',
      artifactToken: 'SYNTHETIC_TOKEN',
      artifactClient,
      actions,
      staging,
      now: () => now,
    });
    const signal = new AbortController().signal;
    const uploadCommand = {
      operation: 'upload_immutable' as const,
      correlation_id: 'upload-lifecycle',
      name: 'opaque-state',
      source_relative_path: 'source/object.bin',
      encrypted_object_digest: digestBytes(encrypted),
      minimum_expires_at_unix_seconds: String(minimumExpiry),
    };

    const uploaded = await operations.execute(uploadCommand, signal);
    expect(uploaded).toMatchObject({
      failure: 'none',
      mutation_state: 'committed',
      metadata: {
        producing_run_id: '7001',
        producing_run_attempt: '2',
        encrypted_object_digest: digestBytes(encrypted),
        expires_at_unix_seconds: String(expiresAt),
        size: String(encrypted.length),
      },
    });
    expect(uploaded.metadata?.archive_digest).toBe(stored?.archiveDigest);
    expect(uploaded.metadata?.archive_digest).not.toBe(digestBytes(encrypted));

    const readBack = await operations.execute(
      {
        operation: 'readback_exact',
        correlation_id: 'readback-lifecycle',
        expected: uploaded.metadata!,
      },
      signal,
    );
    expect(readBack.failure).toBe('none');
    const downloaded = await operations.execute(
      {
        operation: 'download',
        correlation_id: 'download-lifecycle',
        expected: uploaded.metadata!,
        destination_relative_path: 'destination/object.bin',
        maximum_bytes: String(encrypted.length),
      },
      signal,
    );
    expect(downloaded.failure).toBe('none');
    await expect(readFile(path.join(destinationDirectory, 'object.bin'))).resolves.toEqual(
      encrypted,
    );

    const wrongDelete = await operations.execute(
      {
        operation: 'delete_exact',
        correlation_id: 'delete-wrong-authority',
        expected: { ...uploaded.metadata!, archive_digest: '0'.repeat(64) },
      },
      signal,
    );
    expect(wrongDelete).toMatchObject({ failure: 'conflict', mutation_state: 'not_committed' });
    expect(deleteCalls).toBe(0);
    expired = true;
    const downloadsBeforeDelete = downloadCalls;
    const expiredMetadata = await operations.execute(
      {
        operation: 'metadata',
        correlation_id: 'metadata-expired',
        name: uploaded.metadata!.name,
        object_id: uploaded.metadata!.object_id,
      },
      signal,
    );
    expect(expiredMetadata.failure).toBe('none');
    expect(downloadCalls).toBe(downloadsBeforeDelete + 1);
    const downloadsAfterMetadata = downloadCalls;
    const deletedResult = await operations.execute(
      {
        operation: 'delete_exact',
        correlation_id: 'delete-lifecycle',
        expected: uploaded.metadata!,
      },
      signal,
    );
    expect(deletedResult).toMatchObject({ failure: 'none', mutation_state: 'committed' });
    expect(deleteCalls).toBe(1);
    expect(downloadCalls).toBe(downloadsAfterMetadata);
    expect((await readdir(root)).filter((entry) => entry.startsWith('op-'))).toEqual([]);

    expired = false;
    const cleanupFailure = vi
      .spyOn(staging, 'cleanupOperationDirectory')
      .mockRejectedValueOnce(new Error('synthetic cleanup failure'));
    const cleanupResult = await operations.execute(
      { ...uploadCommand, correlation_id: 'upload-cleanup' },
      signal,
    );
    expect(cleanupResult).toMatchObject({
      failure: 'cleanup',
      mutation_state: 'committed',
    });
    cleanupFailure.mockRestore();
  });
});

async function createOperations(
  overrides: Partial<ArtifactActionsRestClient>,
  now?: () => number,
): Promise<OfficialArtifactOperations> {
  const root = await mkdtemp(path.join(os.tmpdir(), 'apr-official-test-'));
  roots.push(root);
  const staging = await ArtifactBridgeStaging.create(root);
  const unsupported = async (): Promise<never> => {
    throw new Error('unexpected official call');
  };
  const actions: ArtifactActionsRestClient = {
    listArtifactsForRepo: unsupported,
    getArtifact: unsupported,
    getWorkflowRunAttempt: unsupported,
    deleteArtifact: unsupported,
    ...overrides,
  };
  const artifactClient: ArtifactClient = {
    uploadArtifact: unsupported,
    downloadArtifact: unsupported,
    listArtifacts: unsupported,
    getArtifact: unsupported,
    deleteArtifact: unsupported,
  };
  return new OfficialArtifactOperations({
    owner: 'owner',
    repository: 'repository',
    currentRunId: '7001',
    currentRunAttempt: '2',
    artifactToken: 'SYNTHETIC_TOKEN',
    artifactClient,
    actions,
    staging,
    now,
  });
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&');
}
