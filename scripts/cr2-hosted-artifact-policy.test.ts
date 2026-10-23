import { describe, expect, it, vi } from 'vitest';
import { getBackendIdsFromToken } from '../node_modules/@actions/artifact/lib/internal/shared/util.js';
import {
  CreateArtifactRequest,
  FinalizeArtifactRequest,
} from '../node_modules/@actions/artifact/lib/generated/results/api/v1/artifact.js';
import {
  acknowledgement,
  actionSource,
  actor,
  actorId,
  admitHosted,
  admitReceipt,
  admitRestoreReceipt,
  assets,
  blobSha,
  census,
  fixture,
  fixtureDigest,
  format,
  limits,
  inspectAssets,
  repository,
  repositoryId,
  sha256,
  sourceMetadata,
  workflow,
} from './cr2-hosted-artifact-policy.mjs';

const source = 'a'.repeat(40);
function receipt() {
  const name = 'cr2-h1-7001';
  return {
    format,
    mode: 'rehearsal',
    phase: 'produce',
    source,
    actionSource,
    repositoryId,
    actorId,
    runId: '7001',
    runA: '7001',
    artifact: {
      id: '910001',
      name,
      physicalName: `apr-object-${sha256(Buffer.from('apr-artifact-family\0' + name))}-${fixtureDigest}`,
      encryptedDigest: fixtureDigest,
      archiveDigest: 'b'.repeat(64),
      expires: 1800000000,
      size: 4096,
      producer: '7001',
      attempt: 1,
    },
    counts: {
      api: 6,
      anonymous: 1,
      metadataBytes: 5000,
      archiveBytes: 6000,
      lists: 2,
      uploads: 1,
      deletes: 0,
    },
    outcome: 'passed',
    mutationState: 'upload_committed',
    syntheticMap: true,
    companionEntry: true,
    publicDevelopmentProfile: true,
  };
}
describe('finite H1 authority and candidate receipt', () => {
  const env = {
    GITHUB_EVENT_NAME: 'workflow_dispatch',
    GITHUB_REPOSITORY: repository,
    GITHUB_REPOSITORY_ID: repositoryId,
    GITHUB_ACTOR: actor,
    GITHUB_ACTOR_ID: actorId,
    GITHUB_RUN_ID: '7001',
    GITHUB_RUN_ATTEMPT: '1',
    GITHUB_SHA: source,
    WORKFLOW_SHA: source,
    GITHUB_WORKFLOW_REF: `${repository}/${workflow}@refs/heads/main`,
  };
  const input = { source, phase: 'produce', approval: acknowledgement };
  it('binds exact manual source/actor/context without a credential', () => {
    expect(() => admitHosted(env, input, { platform: 'linux', arch: 'x64' })).not.toThrow();
    for (const key of Object.keys(env))
      expect(() =>
        admitHosted({ ...env, [key]: 'foreign' }, input, { platform: 'linux', arch: 'x64' }),
      ).toThrow();
    for (const key of Object.keys(input))
      expect(() =>
        admitHosted(env, { ...input, [key]: 'foreign' }, { platform: 'linux', arch: 'x64' }),
      ).toThrow();
    expect(() => admitHosted(env, input, { platform: 'darwin', arch: 'arm64' })).toThrow();
  });
  it('freezes only a synthetic AEAD fixture and never logs plaintext', () => {
    const first = fixture(),
      second = fixture();
    expect(first.length).toBe(4096);
    expect(sha256(first)).toBe(fixtureDigest);
    expect(first.equals(second)).toBe(true);
    expect(first.includes(Buffer.from('CR2-H1-SYNTHETIC-OPAQUE-V1'))).toBe(false);
  });
  it('counts transport redirects as well as SDK retries in the finite body allowance', () => {
    const results =
      (limits.sdkCreateAttempts + limits.sdkFinalizeAttempts) *
      (1 + limits.resultsRedirectsPerAttempt);
    const blob = limits.blobWriteAttempts * (1 + limits.blobRedirectsPerAttempt);
    expect(results + blob).toBe(limits.sdkHttpRequests);
    // jwt-decode's Latin-1 fallback can expand to two UTF-8 bytes per payload byte.
    // Decoded payload <=12 KiB, plus <=1 KiB fixed request fields: Results <=25 KiB.
    expect(limits.uploadBodyBytes).toBeGreaterThanOrEqual(results * 25600 + blob * 16384);
    const previous = process.env.ACTIONS_RUNTIME_TOKEN;
    const output = vi.spyOn(process.stdout, 'write').mockReturnValue(true);
    let largest = 0;
    try {
      const payloads = [
        Buffer.from(JSON.stringify({ scp: 'Actions.Results:' + '"\n\\'.repeat(1800) + ':job' })),
        Buffer.from(JSON.stringify({ scp: 'Actions.Results:' + '😀'.repeat(2900) + ':job' })),
        Buffer.concat([
          Buffer.from('{"scp":"Actions.Results:'),
          Buffer.alloc(12000, 255),
          Buffer.from(':job"}'),
        ]),
      ];
      for (const payload of payloads) {
        const token = 'e30.' + payload.toString('base64url') + '.';
        expect(Buffer.byteLength(token)).toBeLessThanOrEqual(16384);
        process.env.ACTIONS_RUNTIME_TOKEN = token;
        const ids = getBackendIdsFromToken();
        const identifierBytes =
          Buffer.byteLength(JSON.stringify(ids.workflowRunBackendId)) +
          Buffer.byteLength(JSON.stringify(ids.workflowJobRunBackendId)) -
          4;
        expect(identifierBytes).toBeLessThanOrEqual(payload.length * 2);
        const name = receipt().artifact.physicalName;
        const options = { useProtoFieldName: true, emitDefaultValues: false };
        const requests = [
          CreateArtifactRequest.toJson(
            CreateArtifactRequest.create({
              ...ids,
              name,
              version: 7,
              expiresAt: { seconds: '1790000000', nanos: 0 },
              mimeType: { value: 'application/zip' },
            }),
            options,
          ),
          FinalizeArtifactRequest.toJson(
            FinalizeArtifactRequest.create({
              ...ids,
              name,
              size: '16384',
              hash: { value: 'sha256:' + 'c'.repeat(64) },
            }),
            options,
          ),
        ];
        for (const request of requests) {
          const bytes = Buffer.byteLength(JSON.stringify(request));
          expect(bytes - identifierBytes).toBeLessThanOrEqual(1024);
          expect(bytes).toBeLessThanOrEqual(25600);
          largest = Math.max(largest, bytes);
        }
      }
      expect(largest).toBeGreaterThan(16384);
    } finally {
      output.mockRestore();
      if (previous === undefined) delete process.env.ACTIONS_RUNTIME_TOKEN;
      else process.env.ACTIONS_RUNTIME_TOKEN = previous;
    }
  });
  it('reserves one census slot before mutation and closes on post-preflight drift', () => {
    expect(() => census(99, 'produce', true)).not.toThrow();
    expect(() => census(100, 'produce', true)).toThrow();
    expect(() => census(100, 'produce', false)).not.toThrow();
    expect(() => census(101, 'produce', false)).toThrow();
    for (const value of [-1, 0.5, NaN, Infinity])
      expect(() => census(value, 'restore', false)).toThrow();
  });
  it('candidate identity is closed and cannot authorize another artifact or same run', () => {
    expect(() => admitReceipt(receipt(), source, '7002')).not.toThrow();
    expect(() => admitReceipt(receipt(), source, '7001')).toThrow();
    for (const key of [
      'source',
      'actionSource',
      'repositoryId',
      'actorId',
      'runId',
      'runA',
      'mode',
      'outcome',
      'phase',
    ]) {
      expect(() => admitReceipt({ ...receipt(), [key]: 'foreign' }, source, '7002')).toThrow();
    }
    for (const key of Object.keys(receipt().artifact)) {
      const value = receipt();
      value.artifact[key] = 'foreign';
      expect(() => admitReceipt(value, source, '7002')).toThrow();
    }
    for (const field of [
      'api',
      'anonymous',
      'metadataBytes',
      'archiveBytes',
      'lists',
      'uploads',
      'deletes',
    ]) {
      const value = receipt();
      value.counts[field] = 9999999;
      expect(() => admitReceipt(value, source, '7002')).toThrow();
    }
    expect(() =>
      admitReceipt({ ...receipt(), token: 'synthetic-forbidden-field' }, source, '7002'),
    ).toThrow();
  });
  it('validates the actual restore role and exact producer artifact without rewriting counters', () => {
    const producer = receipt();
    const restored = {
      ...producer,
      phase: 'restore',
      runId: '7002',
      mutationState: 'delete_committed',
      counts: { ...producer.counts, api: 9, lists: 1, uploads: 0, deletes: 1 },
    };
    expect(() => admitRestoreReceipt(restored, producer, source, '7002')).not.toThrow();
    expect(() =>
      admitRestoreReceipt(
        { ...restored, mutationState: 'delete_unknown' },
        producer,
        source,
        '7002',
      ),
    ).toThrow();
    expect(() =>
      admitRestoreReceipt(
        { ...restored, artifact: { ...restored.artifact, id: '999999' } },
        producer,
        source,
        '7002',
      ),
    ).toThrow();
  });
  it('source authority covers exact eight paths while preserving original null map', () => {
    const original = inspectAssets(process.cwd());
    const config = { assets: original, mapBytes: JSON.stringify({ synthetic: true }) };
    expect(Object.keys(original)).toEqual(assets);
    for (const name of assets) {
      const url = new URL(
        `https://api.github.com/repos/${repository}/contents/.github/actions/agentic-pr-review/${name}?ref=${actionSource}`,
      );
      const value = sourceMetadata(url, config);
      expect(value.sha).toBe(
        name === 'payload-map.json' ? blobSha(Buffer.from(config.mapBytes)) : original[name].blob,
      );
      url.searchParams.set('ref', source);
      expect(() => sourceMetadata(url, config)).toThrow();
    }
    expect(() => sourceMetadata(new URL('https://example.invalid/forbidden'), config)).toThrow();
    expect(() =>
      sourceMetadata(
        new URL(
          `https://api.github.com/repos/${repository}/contents/.github/actions/agentic-pr-review/foreign?ref=${actionSource}`,
        ),
        config,
      ),
    ).toThrow();
  });
});
