import type { ArtifactClient } from '@actions/artifact';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { ArtifactActionsRestClient } from './official-artifact-operations.js';
import { OfficialArtifactOperations } from './official-artifact-operations.js';
import { ArtifactBridgeStaging, ArtifactBridgeStagingError } from './staging.js';
import { digestBytes, encodeArtifactTransportEnvelope } from './transport-envelope.js';
import { ARTIFACT_ENVELOPE_ENTRY } from './limits.js';
import { ArtifactBridgeOperationBudget } from './operation-budget.js';
import { ArtifactRestAttemptDeadlineError } from './artifact-rest-request-budget.js';
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

  it('allows three distinct 29-second page requests inside one 120-second operation', async () => {
    let elapsed = 0;
    const operations = await createOperations(
      {
        listArtifactsForRepo: async (input) => {
          elapsed += 29_000;
          const start = (input.page - 1) * input.per_page;
          const count = input.page < 3 ? 100 : 56;
          return {
            status: 200,
            data: {
              total_count: 256,
              artifacts: Array.from({ length: count }, (_, index) => ({
                id: start + index + 1,
                name: 'opaque-state',
                size_in_bytes: 1,
                expired: false,
                expires_at: '2030-01-01T00:00:00Z',
              })),
            },
          };
        },
      },
      () => elapsed,
    );

    const result = await operations.execute(
      {
        operation: 'list_exact',
        correlation_id: 'three-request-budget',
        name: 'opaque-state',
        maximum_objects: '256',
      },
      new AbortController().signal,
    );

    expect(result.failure).toBe('none');
    expect(result.objects).toHaveLength(256);
    expect(elapsed).toBe(87_000);
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
      const encrypted = Buffer.from(`ciphertext-${attempt}`);
      const envelope = encodeArtifactTransportEnvelope(
        '7001',
        attempt,
        encrypted,
        digestBytes(encrypted),
        testBudget(),
      );
      archives.set(
        id,
        createTestZip([
          {
            name: ARTIFACT_ENVELOPE_ENTRY,
            data: envelope,
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
      downloadArtifactArchive: async (input) => ({
        status: 200,
        data: archives.get(input.artifact_id)!,
      }),
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
      downloadArtifact: async () => {
        throw new Error('unexpected package download');
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

describe('verified envelope ownership', () => {
  it.each(['run-attempt verification', 'cache store', 'cache copy'] as const)(
    'zeroes decoded ciphertext when %s throws',
    async (failurePoint) => {
      const encrypted = Buffer.from('ephemeral-verified-ciphertext');
      const envelope = encodeArtifactTransportEnvelope(
        '7001',
        '2',
        encrypted,
        digestBytes(encrypted),
        testBudget(),
      );
      const archive = createTestZip([{ name: ARTIFACT_ENVELOPE_ENTRY, data: envelope }]);
      const operations = await createOperations({
        getArtifact: async () => ({
          status: 200,
          data: {
            id: 42,
            name: 'opaque-state',
            size_in_bytes: archive.length,
            expired: false,
            expires_at: '2030-01-01T00:00:00Z',
            digest: `sha256:${digestBytes(archive)}`,
            workflow_run: { id: 7001 },
          },
        }),
        downloadArtifactArchive: async () => ({ status: 200, data: archive }),
        getWorkflowRunAttempt: async () => {
          if (failurePoint === 'run-attempt verification') {
            throw new Error('synthetic verification failure');
          }
          return { status: 200, data: { id: 7001, run_attempt: 2 } };
        },
      });
      const cache = (
        operations as unknown as {
          verifiedRecords: {
            store: (...arguments_: readonly unknown[]) => void;
            copy: (...arguments_: readonly unknown[]) => unknown;
          };
        }
      ).verifiedRecords;
      if (failurePoint === 'cache store') {
        vi.spyOn(cache, 'store').mockImplementation(() => {
          throw new Error('synthetic cache store failure');
        });
      }
      if (failurePoint === 'cache copy') {
        vi.spyOn(cache, 'copy').mockImplementation(() => {
          throw new Error('synthetic cache copy failure');
        });
      }
      const fill = vi.spyOn(Buffer.prototype, 'fill');

      const result = await operations.execute(
        {
          operation: 'metadata',
          correlation_id: `ownership-${failurePoint}`,
          name: 'opaque-state',
          object_id: '42',
        },
        new AbortController().signal,
      );

      expect(result.failure).toBe('io');
      expect(
        fill.mock.contexts.some(
          (context, index) =>
            fill.mock.calls[index]?.[0] === 0 &&
            Buffer.isBuffer(context) &&
            context.equals(Buffer.alloc(encrypted.length)),
        ),
      ).toBe(true);
    },
  );
});

describe('upload buffer ownership', () => {
  it('zeroes source and encoded envelope if staging rejects persistence', async () => {
    const source = Buffer.from('upload-ciphertext-must-not-survive');
    let encodedEnvelope: Buffer | undefined;
    const unsupported = async (): Promise<never> => {
      throw new Error('unexpected provider call');
    };
    const staging = {
      readSource: async () => source,
      writeUploadEnvelope: async (_relative: string, bytes: Buffer) => {
        encodedEnvelope = bytes;
        throw new ArtifactBridgeStagingError();
      },
    } as unknown as ArtifactBridgeStaging;
    const operations = new OfficialArtifactOperations({
      owner: 'owner',
      repository: 'repository',
      currentRunId: '7001',
      currentRunAttempt: '2',
      staging,
      artifactClient: {
        uploadArtifact: unsupported,
        downloadArtifact: unsupported,
        listArtifacts: unsupported,
        getArtifact: unsupported,
        deleteArtifact: unsupported,
      },
      actions: {
        listArtifactsForRepo: unsupported,
        getArtifact: unsupported,
        downloadArtifactArchive: unsupported,
        getWorkflowRunAttempt: unsupported,
        deleteArtifact: unsupported,
      },
    });

    const result = await operations.execute(
      {
        operation: 'upload_immutable',
        correlation_id: 'upload-owned-buffer-failure',
        name: 'opaque-state',
        source_relative_path: 'source/object.bin',
        encrypted_object_digest: digestBytes(source),
        minimum_expires_at_unix_seconds: '1',
      },
      new AbortController().signal,
    );

    expect(result).toMatchObject({ failure: 'invalid', mutation_state: 'not_committed' });
    expect(source.every((byte) => byte === 0)).toBe(true);
    expect(encodedEnvelope?.every((byte) => byte === 0)).toBe(true);
  });
});

describe('post-dispatch upload mutation truthfulness', () => {
  it.each([
    ['malformed platform expiry', 'expiry', 'invalid'],
    ['missing producing run metadata', 'run', 'invalid'],
    ['invalid downloaded ZIP', 'zip', 'invalid'],
    ['raw archive digest mismatch', 'digest', 'digest_mismatch'],
    ['run-attempt authority failure', 'attempt', 'conflict'],
  ] as const)(
    'keeps a concrete created artifact committed after %s',
    async (_description, failureCase, expectedFailure) => {
      const root = await mkdtemp(path.join(os.tmpdir(), 'apr-upload-state-test-'));
      roots.push(root);
      const staging = await ArtifactBridgeStaging.create(root);
      const sourceDirectory = path.join(root, 'source');
      await mkdir(sourceDirectory, { mode: 0o700 });
      const encrypted = Buffer.from('opaque-encrypted-state');
      const encryptedDigest = digestBytes(encrypted);
      await writeFile(path.join(sourceDirectory, 'object.bin'), encrypted);
      await writeFile(path.join(sourceDirectory, ARTIFACT_ENVELOPE_ENTRY), Buffer.alloc(0));
      const envelope = encodeArtifactTransportEnvelope(
        '7001',
        '2',
        encrypted,
        encryptedDigest,
        testBudget(),
      );
      const validArchive = createTestZip([{ name: ARTIFACT_ENVELOPE_ENTRY, data: envelope }]);
      const downloadedArchive = failureCase === 'zip' ? Buffer.from('not-a-zip') : validArchive;
      const platformDigest =
        failureCase === 'digest' ? 'f'.repeat(64) : digestBytes(downloadedArchive);
      const unsupported = async (): Promise<never> => {
        throw new Error('unexpected official call');
      };
      const artifactClient: ArtifactClient = {
        uploadArtifact: async () => ({
          id: 42,
          size: downloadedArchive.length,
          digest: platformDigest,
        }),
        downloadArtifact: unsupported,
        listArtifacts: unsupported,
        getArtifact: unsupported,
        deleteArtifact: unsupported,
      };
      const actions: ArtifactActionsRestClient = {
        listArtifactsForRepo: unsupported,
        getArtifact: async () => ({
          status: 200,
          data: {
            id: 42,
            name: 'opaque-state',
            size_in_bytes: downloadedArchive.length,
            expired: false,
            expires_at: failureCase === 'expiry' ? 'not-a-date' : '2030-01-01T00:00:00Z',
            digest: `sha256:${platformDigest}`,
            workflow_run: failureCase === 'run' ? null : { id: 7001 },
          },
        }),
        downloadArtifactArchive: async () => ({
          status: 200,
          data: downloadedArchive,
        }),
        getWorkflowRunAttempt: async (input) => ({
          status: failureCase === 'attempt' ? 404 : 200,
          data:
            failureCase === 'attempt'
              ? { id: 999, run_attempt: 99 }
              : { id: input.run_id, run_attempt: input.attempt_number },
        }),
        deleteArtifact: unsupported,
      };
      const operations = new OfficialArtifactOperations({
        owner: 'owner',
        repository: 'repository',
        currentRunId: '7001',
        currentRunAttempt: '2',
        artifactClient,
        actions,
        staging,
      });

      const result = await operations.execute(
        {
          operation: 'upload_immutable',
          correlation_id: `upload-${failureCase}`,
          name: 'opaque-state',
          source_relative_path: 'source/object.bin',
          encrypted_object_digest: encryptedDigest,
          minimum_expires_at_unix_seconds: '1',
        },
        new AbortController().signal,
      );

      expect(result).toMatchObject({
        failure: expectedFailure,
        mutation_state: 'committed',
      });
    },
  );
});

describe('upload mutation phase matrix', () => {
  it.each([
    ['pre-dispatch validation', 'invalid_source', 'invalid', 'not_committed'],
    ['synchronous local rejection', 'sync_error', 'io', 'not_committed'],
    ['proven create conflict', 'create_conflict', 'conflict', 'not_committed'],
    ['ambiguous HTTP conflict', 'dispatch_409', 'outcome_unknown', 'outcome_unknown'],
    ['post-dispatch failure', 'dispatch_error', 'outcome_unknown', 'outcome_unknown'],
    ['post-success deadline', 'response_deadline', 'outcome_unknown', 'outcome_unknown'],
  ] as const)(
    '%s maps to %s / %s',
    async (_description, scenario, expectedFailure, expectedMutation) => {
      const root = await mkdtemp(path.join(os.tmpdir(), 'apr-upload-phase-test-'));
      roots.push(root);
      const sourceDirectory = path.join(root, 'source');
      await mkdir(sourceDirectory, { mode: 0o700 });
      const encrypted = Buffer.from('phase-matrix-ciphertext');
      const encryptedDigest = digestBytes(encrypted);
      await writeFile(path.join(sourceDirectory, 'object.bin'), encrypted);
      await writeFile(path.join(sourceDirectory, ARTIFACT_ENVELOPE_ENTRY), Buffer.alloc(0));
      const staging = await ArtifactBridgeStaging.create(root);
      let elapsed = 0;
      let uploadCalls = 0;
      const unsupported = async (): Promise<never> => {
        throw new Error('unexpected official call');
      };
      const artifactClient: ArtifactClient = {
        uploadArtifact: () => {
          uploadCalls += 1;
          if (scenario === 'sync_error') {
            throw new Error('synthetic local rejection');
          }
          if (scenario === 'create_conflict') {
            return Promise.reject(
              Object.assign(new Error('synthetic conflict'), {
                code: 'already_exists',
              }),
            );
          }
          if (scenario === 'dispatch_409') {
            return Promise.reject(
              Object.assign(new Error('synthetic ambiguous conflict'), {
                status: 409,
              }),
            );
          }
          if (scenario === 'dispatch_error') {
            return Promise.reject(Object.assign(new Error('synthetic failure'), { status: 503 }));
          }
          elapsed = 120_000;
          return Promise.resolve({ id: 42, size: 1, digest: 'a'.repeat(64) });
        },
        downloadArtifact: unsupported,
        listArtifacts: unsupported,
        getArtifact: unsupported,
        deleteArtifact: unsupported,
      };
      const actions: ArtifactActionsRestClient = {
        listArtifactsForRepo: unsupported,
        getArtifact: unsupported,
        downloadArtifactArchive: unsupported,
        getWorkflowRunAttempt: unsupported,
        deleteArtifact: unsupported,
      };
      const operations = new OfficialArtifactOperations({
        owner: 'owner',
        repository: 'repository',
        currentRunId: '7001',
        currentRunAttempt: '2',
        artifactClient,
        actions,
        staging,
        monotonicNow: () => elapsed,
      });

      const result = await operations.execute(
        {
          operation: 'upload_immutable',
          correlation_id: `upload-${scenario}`,
          name: 'opaque-state',
          source_relative_path: 'source/object.bin',
          encrypted_object_digest: scenario === 'invalid_source' ? '0'.repeat(64) : encryptedDigest,
          minimum_expires_at_unix_seconds: '1',
        },
        new AbortController().signal,
      );

      expect(result).toMatchObject({
        failure: expectedFailure,
        mutation_state: expectedMutation,
      });
      expect(uploadCalls).toBe(scenario === 'invalid_source' ? 0 : 1);
    },
  );
});

describe('delete mutation phase matrix', () => {
  it.each([
    ['preflight absence', 'preflight_404', 'not_found', 'not_committed'],
    ['synchronous local rejection', 'sync_error', 'io', 'not_committed'],
    ['delete-call absence', 'delete_404', 'outcome_unknown', 'outcome_unknown'],
    ['delete-call cancellation', 'delete_cancelled', 'outcome_unknown', 'outcome_unknown'],
    ['verified absence', 'verified_absent', 'none', 'committed'],
    ['ambiguous observation', 'still_present', 'outcome_unknown', 'outcome_unknown'],
  ] as const)(
    '%s maps to %s / %s',
    async (_description, scenario, expectedFailure, expectedMutation) => {
      const expected = metadataFixture();
      let getCalls = 0;
      let deleteCalls = 0;
      const invalidations: Array<Record<string, unknown>> = [];
      const controller = new AbortController();
      const operations = await createOperations({
        invalidateArtifactMutation: (input) => invalidations.push(input),
        getArtifact: async () => {
          getCalls += 1;
          if (scenario === 'preflight_404' || (scenario === 'verified_absent' && getCalls === 2)) {
            throw Object.assign(new Error('synthetic absence'), { status: 404 });
          }
          return { status: 200, data: platformRecord(expected) };
        },
        deleteArtifact: (_input, requestSignal, _latestAttemptStartAt, onDispatched) => {
          deleteCalls += 1;
          if (scenario === 'sync_error') throw new Error('synthetic local rejection');
          onDispatched?.();
          if (scenario === 'delete_404') return Promise.resolve({ status: 404 });
          if (scenario === 'delete_cancelled') {
            controller.abort();
            if (requestSignal.aborted) {
              return Promise.reject(new Error('synthetic cancellation'));
            }
            return new Promise<never>((_resolve, reject) => {
              requestSignal.addEventListener(
                'abort',
                () => reject(new Error('synthetic cancellation')),
                { once: true },
              );
            });
          }
          return Promise.resolve({ status: 204 });
        },
      });

      const result = await operations.execute(
        {
          operation: 'delete_exact',
          correlation_id: `delete-${scenario}`,
          expected,
        },
        controller.signal,
      );

      expect(result).toMatchObject({
        failure: expectedFailure,
        mutation_state: expectedMutation,
      });
      expect(deleteCalls).toBe(scenario === 'preflight_404' ? 0 : 1);
      const invalidationCount =
        scenario === 'verified_absent' || scenario === 'still_present'
          ? 2
          : scenario === 'delete_404' || scenario === 'delete_cancelled'
            ? 1
            : 0;
      expect(invalidations).toEqual(
        Array.from({ length: invalidationCount }, () => ({
          owner: 'owner',
          repo: 'repository',
          name: expected.name,
          artifact_id: 42,
        })),
      );
    },
  );
});

describe('precise conditional-representation invalidation', () => {
  it('invalidates the named list representation before an outcome-unknown upload', async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'apr-upload-invalidation-test-'));
    roots.push(root);
    const sourceDirectory = path.join(root, 'source');
    await mkdir(sourceDirectory, { mode: 0o700 });
    const encrypted = Buffer.from('outcome-unknown-ciphertext');
    await writeFile(path.join(sourceDirectory, 'object.bin'), encrypted);
    await writeFile(path.join(sourceDirectory, ARTIFACT_ENVELOPE_ENTRY), Buffer.alloc(0));
    const invalidations: Array<Record<string, unknown>> = [];
    const unsupported = async (): Promise<never> => {
      throw new Error('unexpected official call');
    };
    const operations = new OfficialArtifactOperations({
      owner: 'owner',
      repository: 'repository',
      currentRunId: '7001',
      currentRunAttempt: '2',
      artifactClient: {
        uploadArtifact: async () => {
          throw Object.assign(new Error('synthetic ambiguous upload'), { status: 503 });
        },
        downloadArtifact: unsupported,
        listArtifacts: unsupported,
        getArtifact: unsupported,
        deleteArtifact: unsupported,
      },
      actions: {
        invalidateArtifactMutation: (input) => invalidations.push(input),
        listArtifactsForRepo: unsupported,
        getArtifact: unsupported,
        downloadArtifactArchive: unsupported,
        getWorkflowRunAttempt: unsupported,
        deleteArtifact: unsupported,
      },
      staging: await ArtifactBridgeStaging.create(root),
    });

    const result = await operations.execute(
      {
        operation: 'upload_immutable',
        correlation_id: 'upload-outcome-unknown-invalidation',
        name: 'opaque-state',
        source_relative_path: 'source/object.bin',
        encrypted_object_digest: digestBytes(encrypted),
        minimum_expires_at_unix_seconds: '1',
      },
      new AbortController().signal,
    );

    expect(result).toMatchObject({ failure: 'outcome_unknown', mutation_state: 'outcome_unknown' });
    expect(invalidations).toEqual([{ owner: 'owner', repo: 'repository', name: 'opaque-state' }]);
  });

  it('invalidates the target before the delete postcondition requires a fresh 404', async () => {
    const expected = metadataFixture();
    const invalidations: Array<Record<string, unknown>> = [];
    let getCalls = 0;
    const operations = await createOperations({
      invalidateArtifactMutation: (input) => invalidations.push(input),
      getArtifact: async () => {
        getCalls += 1;
        if (getCalls === 1) return { status: 200, data: platformRecord(expected) };
        expect(invalidations).toEqual([
          { owner: 'owner', repo: 'repository', name: expected.name, artifact_id: 42 },
          { owner: 'owner', repo: 'repository', name: expected.name, artifact_id: 42 },
        ]);
        throw Object.assign(new Error('fresh synthetic absence'), { status: 404 });
      },
      deleteArtifact: async (_input, _signal, _latestAttemptStartAt, onDispatched) => {
        onDispatched?.();
        return { status: 204 };
      },
    });

    const result = await operations.execute(
      {
        operation: 'delete_exact',
        correlation_id: 'delete-fresh-404-invalidation',
        expected,
      },
      new AbortController().signal,
    );

    expect(result).toMatchObject({ failure: 'none', mutation_state: 'committed' });
    expect(getCalls).toBe(2);
  });

  it('evicts every list page participating in a rejected aggregate', async () => {
    const invalidations: Array<Record<string, unknown>> = [];
    let page = 0;
    const operations = await createOperations({
      invalidateArtifactListRepresentation: (input) => invalidations.push(input),
      listArtifactsForRepo: async () => {
        page += 1;
        return {
          status: 200,
          data: {
            total_count: page === 1 ? 101 : 102,
            artifacts: Array.from({ length: page === 1 ? 100 : 1 }, (_, index) => ({
              id: (page - 1) * 100 + index + 1,
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
        correlation_id: 'semantic-list-invalidation',
        name: 'opaque-state',
        maximum_objects: '256',
      },
      new AbortController().signal,
    );

    expect(result).toMatchObject({ failure: 'incomplete', complete: false });
    expect(invalidations).toEqual([
      { owner: 'owner', repo: 'repository', name: 'opaque-state', per_page: 100, page: 1 },
      { owner: 'owner', repo: 'repository', name: 'opaque-state', per_page: 100, page: 2 },
    ]);
  });

  it('evicts a rejected artifact descriptor and its verified-record identity', async () => {
    const invalidations: Array<Record<string, unknown>> = [];
    const expected = metadataFixture();
    const operations = await createOperations({
      invalidateArtifactRepresentation: (input) => invalidations.push(input),
      getArtifact: async () => ({
        status: 200,
        data: { ...platformRecord(expected), name: 'wrong-name' },
      }),
    });

    const result = await operations.execute(
      {
        operation: 'metadata',
        correlation_id: 'semantic-artifact-invalidation',
        name: expected.name,
        object_id: expected.object_id,
      },
      new AbortController().signal,
    );

    expect(result).toMatchObject({ failure: 'invalid' });
    expect(invalidations).toEqual([{ owner: 'owner', repo: 'repository', artifact_id: 42 }]);
  });

  it('evicts rejected run-attempt and related artifact representations', async () => {
    const encrypted = Buffer.from('semantic-attempt-ciphertext');
    const envelope = encodeArtifactTransportEnvelope(
      '7001',
      '2',
      encrypted,
      digestBytes(encrypted),
      testBudget(),
    );
    const archive = createTestZip([{ name: ARTIFACT_ENVELOPE_ENTRY, data: envelope }]);
    const expected = {
      ...metadataFixture(),
      archive_digest: digestBytes(archive),
      encrypted_object_digest: digestBytes(encrypted),
      size: String(encrypted.length),
    };
    const artifactInvalidations: Array<Record<string, unknown>> = [];
    const attemptInvalidations: Array<Record<string, unknown>> = [];
    const operations = await createOperations({
      invalidateArtifactRepresentation: (input) => artifactInvalidations.push(input),
      invalidateWorkflowRunAttemptRepresentation: (input) => attemptInvalidations.push(input),
      getArtifact: async () => ({
        status: 200,
        data: { ...platformRecord(expected), size_in_bytes: archive.length },
      }),
      downloadArtifactArchive: async () => ({ status: 200, data: archive }),
      getWorkflowRunAttempt: async () => ({ status: 200, data: { id: 7001, run_attempt: 9 } }),
    });

    const result = await operations.execute(
      {
        operation: 'metadata',
        correlation_id: 'semantic-attempt-invalidation',
        name: expected.name,
        object_id: expected.object_id,
      },
      new AbortController().signal,
    );

    expect(result).toMatchObject({ failure: 'conflict' });
    expect(attemptInvalidations).toEqual([
      { owner: 'owner', repo: 'repository', run_id: 7001, attempt_number: 2 },
    ]);
    expect(artifactInvalidations).toEqual([
      { owner: 'owner', repo: 'repository', artifact_id: 42 },
    ]);
  });
});

describe('delete dispatch marker', () => {
  it('reports a pre-wire pacing deadline as cancelled and never marks deletion dispatched', async () => {
    const expected = metadataFixture();
    let deleteWireCalls = 0;
    const operations = await createOperations({
      getArtifact: async () => ({ status: 200, data: platformRecord(expected) }),
      deleteArtifact: async (_input, _signal, _latestAttemptStartAt, onDispatched) => {
        expect(onDispatched).toBeTypeOf('function');
        throw new ArtifactRestAttemptDeadlineError();
      },
    });

    const result = await operations.execute(
      {
        operation: 'delete_exact',
        correlation_id: 'delete-prewire-deadline',
        expected,
      },
      new AbortController().signal,
    );

    expect(result).toMatchObject({ failure: 'cancelled', mutation_state: 'not_committed' });
    expect(deleteWireCalls).toBe(0);
  });
});

describe('official artifact lifecycle', () => {
  it('uses UTC time for expiry while the monotonic clock remains deadline-only', async () => {
    const expected = metadataFixture();
    const invalidations: Array<Record<string, unknown>> = [];
    let downloadCalls = 0;
    const operations = await createOperations(
      {
        invalidateArtifactRepresentation: (input) => invalidations.push(input),
        getArtifact: async () => ({ status: 200, data: platformRecord(expected) }),
        downloadArtifactArchive: async () => {
          downloadCalls += 1;
          return { status: 200, data: Buffer.from('unexpected') };
        },
      },
      () => 0,
      () => Date.parse('2031-01-01T00:00:00Z'),
    );

    const result = await operations.execute(
      {
        operation: 'download',
        correlation_id: 'utc-expiry',
        expected,
        destination_relative_path: 'destination/object.bin',
        maximum_bytes: '1024',
      },
      new AbortController().signal,
    );

    expect(result).toMatchObject({ failure: 'expired' });
    expect(downloadCalls).toBe(0);
    expect(invalidations).toEqual([{ owner: 'owner', repo: 'repository', artifact_id: 42 }]);
  });

  it('uploads, verifies, downloads, reads back, and authorizes exact deletion', async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'apr-lifecycle-test-'));
    roots.push(root);
    const staging = await ArtifactBridgeStaging.create(root);
    const writeDestination = staging.writeDestination.bind(staging);
    let callerRecordBytes: Buffer | undefined;
    let uploadSourceBytes: Buffer | undefined;
    let uploadEnvelopeBytes: Buffer | undefined;
    const readSource = staging.readSource.bind(staging);
    const writeUploadEnvelope = staging.writeUploadEnvelope.bind(staging);
    vi.spyOn(staging, 'readSource').mockImplementation(async (relative, budget) => {
      const bytes = await readSource(relative, budget);
      uploadSourceBytes = bytes;
      return bytes;
    });
    vi.spyOn(staging, 'writeUploadEnvelope').mockImplementation(async (relative, bytes, budget) => {
      uploadEnvelopeBytes = bytes;
      return await writeUploadEnvelope(relative, bytes, budget);
    });
    vi.spyOn(staging, 'writeDestination').mockImplementation(
      async (relativePath, bytes, budget) => {
        callerRecordBytes = bytes;
        await writeDestination(relativePath, bytes, budget);
      },
    );
    const sourceDirectory = path.join(root, 'source');
    const destinationDirectory = path.join(root, 'destination');
    await mkdir(sourceDirectory, { mode: 0o700 });
    await mkdir(destinationDirectory, { mode: 0o700 });
    const encrypted = Buffer.from('opaque-encrypted-state');
    await writeFile(path.join(sourceDirectory, 'object.bin'), encrypted);
    await writeFile(path.join(sourceDirectory, ARTIFACT_ENVELOPE_ENTRY), Buffer.alloc(0));
    await writeFile(path.join(destinationDirectory, 'object.bin'), Buffer.alloc(0));

    const now = Date.parse('2029-01-01T00:00:00Z');
    const minimumExpiry = Math.floor(now / 1000) + 86_401;
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
    let getArtifactCalls = 0;
    let deleteCalls = 0;
    const conditionalCacheInvalidations: Array<{
      readonly owner: string;
      readonly repo: string;
      readonly name: string;
      readonly artifact_id?: number;
    }> = [];

    const artifactClient: ArtifactClient = {
      uploadArtifact: async (name, files, operationRoot, options) => {
        expect(operationRoot).toBe(sourceDirectory);
        expect(files).toHaveLength(1);
        expect(options?.compressionLevel).toBe(0);
        expect(options?.retentionDays).toBe(2);
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
          digest: stored.archiveDigest,
        };
      },
      downloadArtifact: async () => {
        throw new Error('unexpected package download');
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
      invalidateArtifactMutation: (input) => {
        conditionalCacheInvalidations.push(input);
      },
      listArtifactsForRepo: async () => {
        throw new Error('unexpected repository list');
      },
      getArtifact: async (input) => {
        getArtifactCalls += 1;
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
      downloadArtifactArchive: async (input) => {
        downloadCalls += 1;
        expect(input.artifact_id).toBe(stored?.id);
        if (!stored) throw new Error('missing synthetic artifact');
        return { status: 200, data: stored.archive };
      },
      getWorkflowRunAttempt: async (input) => ({
        status: 200,
        data: { id: input.run_id, run_attempt: input.attempt_number },
      }),
      deleteArtifact: async (input, _signal, _latestAttemptStartAt, onDispatched) => {
        deleteCalls += 1;
        expect(input.artifact_id).toBe(stored?.id);
        onDispatched?.();
        deleted = true;
        return { status: 204, data: undefined };
      },
    };
    const operations = new OfficialArtifactOperations({
      owner: 'owner',
      repository: 'repository',
      currentRunId: '7001',
      currentRunAttempt: '2',
      artifactClient,
      actions,
      staging,
      monotonicNow: () => 0,
      utcNow: () => now,
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
    expect(uploadSourceBytes?.every((byte) => byte === 0)).toBe(true);
    expect(uploadEnvelopeBytes?.every((byte) => byte === 0)).toBe(true);
    expect(conditionalCacheInvalidations).toEqual([
      { owner: 'owner', repo: 'repository', name: 'opaque-state' },
    ]);
    expect(downloadCalls).toBe(1);
    expect(getArtifactCalls).toBe(1);

    const readBack = await operations.execute(
      {
        operation: 'readback_exact',
        correlation_id: 'readback-lifecycle',
        expected: uploaded.metadata!,
      },
      signal,
    );
    expect(readBack.failure).toBe('none');
    expect(downloadCalls).toBe(1);
    expect(getArtifactCalls).toBe(2);
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
    expect(downloadCalls).toBe(1);
    expect(getArtifactCalls).toBe(3);
    await expect(readFile(path.join(destinationDirectory, 'object.bin'))).resolves.toEqual(
      encrypted,
    );
    expect(callerRecordBytes?.every((byte) => byte === 0)).toBe(true);
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
    const expiredReadBack = await operations.execute(
      {
        operation: 'readback_exact',
        correlation_id: 'readback-expired',
        expected: uploaded.metadata!,
      },
      signal,
    );
    expect(expiredReadBack.failure).toBe('none');
    expect(downloadCalls).toBe(downloadsBeforeDelete + 1);
    const downloadsBeforeExpiredDownload = downloadCalls;
    const expiredDownload = await operations.execute(
      {
        operation: 'download',
        correlation_id: 'download-expired',
        expected: uploaded.metadata!,
        destination_relative_path: 'destination/object.bin',
        maximum_bytes: String(encrypted.length),
      },
      signal,
    );
    expect(expiredDownload.failure).toBe('expired');
    expect(downloadCalls).toBe(downloadsBeforeExpiredDownload);
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
    expect(downloadCalls).toBe(downloadsBeforeExpiredDownload);
    expect(getArtifactCalls).toBe(9);
    expect(conditionalCacheInvalidations).toEqual([
      { owner: 'owner', repo: 'repository', name: 'opaque-state' },
      { owner: 'owner', repo: 'repository', name: 'opaque-state', artifact_id: 42 },
      { owner: 'owner', repo: 'repository', name: 'opaque-state', artifact_id: 42 },
    ]);
    await operations.dispose();
    await expect(
      operations.execute(
        {
          operation: 'metadata',
          correlation_id: 'metadata-after-dispose',
          name: uploaded.metadata!.name,
          object_id: uploaded.metadata!.object_id,
        },
        signal,
      ),
    ).rejects.toThrow('artifact_lifecycle_coordinator_stopped');
  });
});

async function createOperations(
  overrides: Partial<ArtifactActionsRestClient>,
  now?: () => number,
  utcNow?: () => number,
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
    downloadArtifactArchive: unsupported,
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
    artifactClient,
    actions,
    staging,
    monotonicNow: now,
    utcNow,
  });
}

function metadataFixture() {
  return {
    name: 'opaque-state',
    object_id: '42',
    producing_run_id: '7001',
    producing_run_attempt: '2',
    archive_digest: 'a'.repeat(64),
    encrypted_object_digest: 'b'.repeat(64),
    expires_at_unix_seconds: '1893456000',
    size: '1',
  };
}

function platformRecord(expected: ReturnType<typeof metadataFixture>) {
  return {
    id: Number(expected.object_id),
    name: expected.name,
    size_in_bytes: 1,
    expired: false,
    expires_at: new Date(Number(expected.expires_at_unix_seconds) * 1000).toISOString(),
    digest: `sha256:${expected.archive_digest}`,
    workflow_run: { id: Number(expected.producing_run_id) },
  };
}

function testBudget(): ArtifactBridgeOperationBudget {
  return new ArtifactBridgeOperationBudget(new AbortController().signal, () => 0, 0);
}
