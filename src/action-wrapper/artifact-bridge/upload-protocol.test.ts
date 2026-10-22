import { mkdir, mkdtemp, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import type { ArtifactClient } from '@actions/artifact';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { OfficialArtifactOperations } from './historical-upload-operations.fixture.js';
import { ArtifactBridgeStaging } from './historical-staging.fixture.js';
import { digestBytes } from './historical-transport-envelope.fixture.js';
import {
  isValidArtifactBridgeResult,
  type ArtifactBridgeResult,
} from './historical-upload-contracts.fixture.js';
import { OfficialCallTracker } from '../launcher/official-calls.js';

const roots: string[] = [];
afterEach(async () => {
  await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true, force: true })));
});
const receipt = { artifact_id: '1', archive_sha256: 'a'.repeat(64), archive_size: '512' };
const base = { operation: 'upload_immutable' as const, correlation_id: 'operation' };

describe('T1 provisional upload protocol', () => {
  it('retains the original SDK promise after cancellation and never replays it', async () => {
    let release!: (value: { id: number; digest: string; size: number }) => void;
    let started!: () => void;
    const entered = new Promise<void>((resolve) => {
      started = resolve;
    });
    const upload = vi.fn(() => {
      started();
      return new Promise<{ id: number; digest: string; size: number }>((resolve) => {
        release = resolve;
      });
    });
    const tracker = new OfficialCallTracker();
    const tracked = tracker.wrap({ uploadArtifact: upload });
    const { operations, command } = await fixture(tracked.uploadArtifact);
    const controller = new AbortController();
    const pending = operations.execute(command, controller.signal);
    await entered;
    controller.abort();
    expect(await pending).toMatchObject({
      dispatch: 'dispatched',
      failure: 'outcome_unknown',
      receipt: null,
    });
    await expect(tracker.awaitQuiescence(5)).resolves.toBe(false);
    expect(upload).toHaveBeenCalledOnce();
    release({ id: 1, digest: receipt.archive_sha256, size: 512 });
    await expect(tracker.awaitQuiescence(100)).resolves.toBe(true);
    await operations.dispose();
    expect(upload).toHaveBeenCalledOnce();
  });
  it.each([
    ['not_dispatched', 'invalid', null, true],
    ['not_dispatched', 'cancelled', null, true],
    ['not_dispatched', 'io', null, true],
    ['not_dispatched', 'cleanup', null, true],
    ['dispatched', 'none', receipt, true],
    ['dispatched', 'conflict', null, true],
    ['dispatched', 'outcome_unknown', null, true],
    ['dispatched', 'outcome_unknown', receipt, true],
    ['not_dispatched', 'none', receipt, false],
    ['dispatched', 'none', null, false],
    ['dispatched', 'io', null, false],
    ['dispatched', 'cleanup', receipt, false],
    ['not_dispatched', 'conflict', null, false],
    ['not_dispatched', 'invalid', receipt, false],
  ])('checks %s/%s', (dispatch, failure, value, valid) => {
    expect(
      isValidArtifactBridgeResult({
        ...base,
        dispatch,
        failure,
        receipt: value,
      } as ArtifactBridgeResult),
    ).toBe(valid);
  });
  it('rejects old committed metadata, missing members, malformed receipt and extra keys', () => {
    for (const value of [
      { ...base, failure: 'none', mutation_state: 'committed', metadata: {} },
      { ...base, failure: 'io', dispatch: 'not_dispatched' },
      {
        ...base,
        failure: 'none',
        dispatch: 'dispatched',
        receipt: { ...receipt, artifact_id: '01' },
      },
      {
        ...base,
        failure: 'none',
        dispatch: 'dispatched',
        receipt: { ...receipt, archive_size: '45413723' },
      },
      { ...base, failure: 'none', dispatch: 'dispatched', receipt, metadata: {} },
    ])
      expect(isValidArtifactBridgeResult(value as ArtifactBridgeResult)).toBe(false);
  });
  it('calls only upload and returns SDK facts without metadata or REST', async () => {
    const call = vi.fn(async () => ({ id: 1, digest: receipt.archive_sha256, size: 512 }));
    const { operations, command } = await fixture(call);
    const reply = await operations.execute(command, new AbortController().signal);
    expect(reply).toEqual({ ...base, failure: 'none', dispatch: 'dispatched', receipt });
    expect(call).toHaveBeenCalledOnce();
    expect(reply).not.toHaveProperty('metadata');
    expect(reply).not.toHaveProperty('mutation_state');
    const unsupported = await operations.execute(
      { operation: 'list_exact', correlation_id: 'list', name: 'name', maximum_objects: '8' },
      new AbortController().signal,
    );
    expect(unsupported.failure).toBe('invalid');
    expect(call).toHaveBeenCalledOnce();
    await operations.dispose();
  });
  it.each([
    () => {
      throw new Error('synchronous');
    },
    async () => {
      throw new Error('asynchronous');
    },
    async () => ({ id: 1, digest: 'bad', size: 10 }),
  ])('preserves possible dispatch without replay for SDK failures', async (call) => {
    const upload = vi.fn(call);
    const { operations, command } = await fixture(upload);
    expect(await operations.execute(command, new AbortController().signal)).toMatchObject({
      dispatch: 'dispatched',
      failure: 'outcome_unknown',
      receipt: null,
    });
    expect(upload).toHaveBeenCalledOnce();
    await operations.dispose();
  });
  it('does not enter SDK when source digest or caller cancellation refuses', async () => {
    const upload = vi.fn();
    const { operations, command } = await fixture(upload);
    expect(
      await operations.execute(
        { ...command, encrypted_object_digest: '0'.repeat(64) },
        new AbortController().signal,
      ),
    ).toMatchObject({ dispatch: 'not_dispatched', failure: 'invalid', receipt: null });
    const controller = new AbortController();
    controller.abort();
    await expect(operations.execute(command, controller.signal)).rejects.toThrow();
    expect(upload).not.toHaveBeenCalled();
    await operations.dispose();
  });
});

async function fixture(call: unknown) {
  const root = await mkdtemp(path.join(tmpdir(), 'apr-t1-upload-'));
  roots.push(root);
  const directory = path.join(root, 'csharp', 'op-test');
  await mkdir(directory, { recursive: true, mode: 0o700 });
  const bytes = Buffer.from('encrypted');
  await writeFile(path.join(directory, 'object.bin'), bytes, { mode: 0o600 });
  await writeFile(path.join(directory, 'artifact-envelope.json'), '', { mode: 0o600 });
  const operations = new OfficialArtifactOperations({
    currentRunId: '7001',
    currentRunAttempt: '2',
    staging: await ArtifactBridgeStaging.create(root),
    artifactClient: { uploadArtifact: call } as ArtifactClient,
  });
  return {
    operations,
    command: {
      ...base,
      name: 'name',
      source_relative_path: 'csharp/op-test/object.bin',
      encrypted_object_digest: digestBytes(bytes),
      minimum_expires_at_unix_seconds: String(Math.floor(Date.now() / 1000) + 86400),
    },
  };
}
