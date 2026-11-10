import { access, mkdir, mkdtemp, rename, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';
import * as core from '@actions/core';
import * as candidates from './action-candidate.js';
import { runCompositePreparation } from '../composite-preparation.js';
import { RuntimePrerequisiteTerminationUnconfirmedError } from './runtime-prerequisite.js';
import { createManagedScratch, reopenManagedScratch } from './managed-scratch-owner.js';
import { cleanupActionCandidate } from './action-candidate.js';

vi.mock('@actions/core', () => ({
  setOutput: vi.fn(),
  setFailed: vi.fn(),
  error: vi.fn(),
  saveState: vi.fn(),
}));

const roots: string[] = [];
afterEach(async () => {
  vi.restoreAllMocks();
  vi.unstubAllEnvs();
  await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true, force: true })));
});

function preparationFixture() {
  for (const [key, value] of Object.entries({
    GITHUB_RUN_ID: '1',
    GITHUB_RUN_ATTEMPT: '1',
    RUNNER_TEMP: '/owned',
    GITHUB_WORKSPACE: '/workspace',
  }))
    vi.stubEnv(key, value);
  const prepare = vi.spyOn(candidates, 'prepareActionCandidate').mockResolvedValue('candidate');
  const output = vi
    .mocked(core.setOutput)
    .mockReset()
    .mockImplementation(() => {});
  const failed = vi
    .mocked(core.setFailed)
    .mockReset()
    .mockImplementation(() => {});
  const diagnostic = vi
    .mocked(core.error)
    .mockReset()
    .mockImplementation(() => {});
  return { prepare, output, failed, diagnostic };
}

describe('preparation failure presentation', () => {
  const root = new URL('file:///action/');
  it('keeps successful candidate handoff unchanged without emitting an error', async () => {
    const f = preparationFixture();
    expect(await runCompositePreparation(root, 'r7-d0')).toBe(0);
    expect(f.output).toHaveBeenCalledWith('candidate', 'candidate');
    expect(f.diagnostic).not.toHaveBeenCalled();
    expect(f.failed).not.toHaveBeenCalled();
  });
  it('distinguishes missing environment, preparation and output failures', async () => {
    const f = preparationFixture();
    vi.stubEnv('GITHUB_RUN_ID', '');
    expect(await runCompositePreparation(root, 'r7-d0')).toBe(1);
    expect(f.prepare).not.toHaveBeenCalled();
    expect(f.diagnostic.mock.calls[0]![0]).toContain('"stage":"environment"');
    vi.stubEnv('GITHUB_RUN_ID', '1');
    f.prepare.mockRejectedValueOnce(new Error('wrapper_action_source_invalid'));
    expect(await runCompositePreparation(root, 'r7-d0')).toBe(1);
    expect(f.diagnostic.mock.calls[1]![0]).toContain('"code":"wrapper_action_source_invalid"');
    f.output.mockImplementationOnce(() => {
      throw new Error('PRIVATE_OUTPUT_PATH');
    });
    expect(await runCompositePreparation(root, 'r7-d0')).toBe(1);
    expect(f.diagnostic.mock.calls[2]![0]).toContain('"stage":"candidate_output"');
    expect(JSON.stringify(f.diagnostic.mock.calls)).not.toContain('PRIVATE_OUTPUT_PATH');
    expect(f.failed).toHaveBeenCalledWith('Unable to prepare the managed review payload.');
  });
  it('does not serialize private errors or evaluate thrown-object getters', async () => {
    const f = preparationFixture();
    const getter = vi.fn(() => {
      throw new Error('PRIVATE_GETTER_TOKEN');
    });
    const hostile = Object.defineProperty({}, 'message', { get: getter });
    for (const error of [new Error('PRIVATE_TOKEN_PATH'), hostile, 'PRIVATE_STRING']) {
      f.prepare.mockRejectedValueOnce(error);
      expect(await runCompositePreparation(root, 'r7-d0')).toBe(1);
    }
    expect(getter).not.toHaveBeenCalled();
    expect(JSON.stringify(f.diagnostic.mock.calls)).not.toContain('PRIVATE');
    expect(
      f.diagnostic.mock.calls.every(([value]) => String(value).includes('"code":"unclassified"')),
    ).toBe(true);
  });
  it('keeps retained lifetime listeners until actual release even if diagnostics fail', async () => {
    const f = preparationFixture();
    let release!: () => void;
    const settled = new Promise<void>((resolve) => {
      release = resolve;
    });
    f.prepare.mockRejectedValueOnce(
      new RuntimePrerequisiteTerminationUnconfirmedError(settled, settled),
    );
    f.diagnostic.mockImplementation(() => {
      throw new Error('PRIVATE_SINK');
    });
    const before = process.listenerCount('SIGTERM');
    expect(await runCompositePreparation(root, 'r7-d0')).toBe(1);
    expect(process.listenerCount('SIGTERM')).toBe(before + 1);
    release();
    await settled;
    await Promise.resolve();
    expect(process.listenerCount('SIGTERM')).toBe(before);
    expect(f.failed).toHaveBeenCalledOnce();
  });
});
async function fixture() {
  const root = await mkdtemp(path.join(tmpdir(), 'apr-candidate-test-'));
  roots.push(root);
  const runnerTemp = path.join(root, 'runner'),
    workspace = path.join(root, 'workspace');
  await mkdir(runnerTemp, { mode: 0o700 });
  await mkdir(workspace, { mode: 0o700 });
  const owner = await createManagedScratch(runnerTemp);
  const identity = owner.identity;
  await owner.detachForHandoff();
  return { root, runnerTemp, workspace, identity, encoded: JSON.stringify(identity) };
}

describe.skipIf(process.platform !== 'linux')('preparation post ownership', () => {
  it('cleans an unclaimed exact owner once and accepts a subsequent post', async () => {
    const f = await fixture();
    await writeFile(path.join(f.identity.tempRoot, 'partial'), 'no secrets');
    await cleanupActionCandidate(f.encoded, f.runnerTemp, f.workspace);
    await expect(access(f.identity.tempRoot)).rejects.toThrow();
    await cleanupActionCandidate(f.encoded, f.runnerTemp, f.workspace);
  });
  it('retains a launch claim and its files without reclaiming its lifetime', async () => {
    const f = await fixture();
    await mkdir(path.join(f.identity.tempRoot, 'claimed'), { mode: 0o700 });
    await writeFile(path.join(f.identity.tempRoot, 'retained'), 'active SDK data');
    await cleanupActionCandidate(f.encoded, f.runnerTemp, f.workspace);
    await access(path.join(f.identity.tempRoot, 'retained'));
    const launch = await reopenManagedScratch(f.identity);
    await launch.cleanup();
  });
  it('refuses a replacement directory and preserves both original and replacement', async () => {
    const f = await fixture();
    const original = f.identity.tempRoot + '-old';
    await rename(f.identity.tempRoot, original);
    await mkdir(f.identity.tempRoot, { mode: 0o700 });
    await writeFile(path.join(f.identity.tempRoot, 'preserved'), 'replacement');
    await expect(cleanupActionCandidate(f.encoded, f.runnerTemp, f.workspace)).rejects.toThrow();
    await access(original);
    await access(path.join(f.identity.tempRoot, 'preserved'));
  });
  it('refuses a changed cleanup identity or reviewed-workspace candidate', async () => {
    const f = await fixture();
    for (const encoded of [
      JSON.stringify({ ...f.identity, rootIno: '0' }),
      JSON.stringify({ ...f.identity, extra: true }),
    ])
      await expect(cleanupActionCandidate(encoded, f.runnerTemp, f.workspace)).rejects.toThrow();
    await expect(cleanupActionCandidate(f.encoded, f.runnerTemp, f.runnerTemp)).rejects.toThrow();
    await access(f.identity.tempRoot);
  });
});
