import { describe, expect, it } from 'vitest';
import { LIVE_CONTEXT_MAX_BYTES } from './constants.js';
import { parseLiveRuntimeInvocationContext } from './context.js';
import {
  computeAdapterId,
  computeCacheContractDigest,
  computeCacheConfigId,
  computePolicyId,
  computeTemplateId,
  computeToolDefinitionId,
} from '../prefix-contract/digest.js';

const HASH = 'a'.repeat(64);
const EPOCH = 'A'.repeat(22);

function context(): Record<string, unknown> {
  const cacheContractEnvelopes = {
    template: { definition: {}, schemaVersion: 1, templateVersion: 1 },
    policy: { constraints: {}, instructions: '', policyVersion: 1, schemaVersion: 1 },
    tools: { definitions: [], schemaVersion: 1, toolsetVersion: 1 },
    cacheConfig: {
      cacheConfigVersion: 1,
      eligibility: 'unknown',
      markerPolicy: 'none',
      schemaVersion: 1,
      statelessMode: false,
    },
    adapter: { adapterBuildVersion: 'test', capabilityProfileVersion: 1, schemaVersion: 1 },
  };
  const templateId = computeTemplateId(cacheContractEnvelopes.template);
  const policyId = computePolicyId(cacheContractEnvelopes.policy);
  const toolDefinitionId = computeToolDefinitionId(cacheContractEnvelopes.tools);
  const cacheConfigId = computeCacheConfigId(cacheContractEnvelopes.cacheConfig);
  const adapterId = computeAdapterId(cacheContractEnvelopes.adapter);
  if (!templateId.ok || !policyId.ok || !toolDefinitionId.ok || !cacheConfigId.ok || !adapterId.ok)
    throw new Error('test envelope setup failed');
  const cacheContractDigest = computeCacheContractDigest({
    adapterId: adapterId.value,
    cacheConfigId: cacheConfigId.value,
    modelId: 'synthetic-model',
    policyId: policyId.value,
    providerId: 'synthetic',
    templateId: templateId.value,
    toolDefinitionId: toolDefinitionId.value,
  });
  if (!cacheContractDigest.ok) throw new Error('test digest setup failed');
  return {
    schemaVersion: 1,
    stateKey: {
      namespace: 'm4-ledger-v2',
      repository: 'owner/repo',
      headRepository: 'owner/repo',
      pullRequest: 55,
      workflowIdentity: 'workflow',
      trustedExecutionDomain: 'github-actions',
    },
    sessionEpoch: EPOCH,
    cacheContractIdentity: {
      ledgerSchemaVersion: 1,
      prefixContractVersion: 1,
      providerId: 'synthetic',
      modelId: 'synthetic-model',
      adapterId: adapterId.value,
      templateId: templateId.value,
      policyId: policyId.value,
      toolDefinitionId: toolDefinitionId.value,
      cacheConfigId: cacheConfigId.value,
    },
    generation: { stateGeneration: 0, ledgerEpoch: EPOCH },
    transition: {
      kind: 'bootstrap',
      reason: 'new_session',
      predecessorLedgerSha256: 'bootstrap',
      predecessorManifestSha256: 'bootstrap',
    },
    currentInteraction: {
      interactionId: HASH,
      interactionOrdinal: 0,
      consumedInputSha256: HASH,
      subjectDigest: HASH,
      cacheContractDigest: cacheContractDigest.value,
    },
    cacheContractEnvelopes,
    providerMode: 'synthetic',
    producingRun: { producingRunId: '1', runAttempt: 1 },
  };
}

function bytes(value: string): Uint8Array {
  return new TextEncoder().encode(value);
}

describe('LiveRuntimeInvocationContextV1 parser', () => {
  it('accepts the closed shape and permits NUL only in open envelope content', () => {
    expect(parseLiveRuntimeInvocationContext(bytes(JSON.stringify(context()))).valid).toBe(true);
  });

  it('reports syntax before duplicate-property classification', () => {
    expect(
      parseLiveRuntimeInvocationContext(bytes('{"schemaVersion":1,"schemaVersion":}')),
    ).toEqual({
      valid: false,
      code: 'live-context-invalid-json',
    });
  });

  it('rejects duplicate properties after valid JSON syntax', () => {
    const result = parseLiveRuntimeInvocationContext(
      bytes('{"schemaVersion":1,"schemaVersion":1}'),
    );
    expect(result).toEqual({ valid: false, code: 'live-context-duplicate-property' });
  });

  it('rejects controls in context-owned fields but not open envelopes', () => {
    const value = context();
    (value.stateKey as Record<string, unknown>).workflowIdentity = 'bad\u0000identity';
    expect(parseLiveRuntimeInvocationContext(bytes(JSON.stringify(value)))).toEqual({
      valid: false,
      code: 'live-context-semantic',
    });
  });

  it('rejects a BOM, lone surrogate, and raw cap overflow', () => {
    expect(parseLiveRuntimeInvocationContext(new Uint8Array([0xef, 0xbb, 0xbf, 0x7b]))).toEqual({
      valid: false,
      code: 'live-context-bom',
    });
    expect(
      parseLiveRuntimeInvocationContext(bytes(JSON.stringify({ ...context(), bad: '\ud800' }))),
    ).toEqual({
      valid: false,
      code: 'live-context-unicode',
    });
    expect(parseLiveRuntimeInvocationContext(new Uint8Array(LIVE_CONTEXT_MAX_BYTES + 1))).toEqual({
      valid: false,
      code: 'live-context-over-bound',
    });
  });

  it('rejects a root interaction with a nonzero ordinal before execution', () => {
    const value = context();
    (value.currentInteraction as Record<string, unknown>).interactionOrdinal = 1;
    expect(parseLiveRuntimeInvocationContext(bytes(JSON.stringify(value)))).toEqual({
      valid: false,
      code: 'live-context-semantic',
    });
  });

  it('does not throw while scanning deeply nested open-envelope content', () => {
    const value = context();
    (value.cacheContractEnvelopes as Record<string, unknown>).template = null;
    const nested = `${'['.repeat(2_000)}null${']'.repeat(2_000)}`;
    const text = JSON.stringify(value).replace('"template":null', `"template":${nested}`);
    expect(() => parseLiveRuntimeInvocationContext(bytes(text))).not.toThrow();
  });
});
