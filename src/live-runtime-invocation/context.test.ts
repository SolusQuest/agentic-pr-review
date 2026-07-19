import { describe, expect, it } from 'vitest';
import { LIVE_CONTEXT_MAX_BYTES } from './constants.js';
import { parseLiveRuntimeInvocationContext } from './context.js';

const HASH = 'a'.repeat(64);
const EPOCH = 'A'.repeat(22);

function context(): Record<string, unknown> {
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
      adapterId: HASH,
      templateId: HASH,
      policyId: HASH,
      toolDefinitionId: HASH,
      cacheConfigId: HASH,
    },
    generation: { stateGeneration: 0, ledgerEpoch: EPOCH },
    transition: { kind: 'bootstrap', reason: 'new_session' },
    currentInteraction: {
      interactionId: HASH,
      interactionOrdinal: 0,
      consumedInputSha256: HASH,
      subjectDigest: HASH,
      cacheContractDigest: HASH,
    },
    cacheContractEnvelopes: {
      template: { version: 1, content: '\u0000 is valid open content' },
      policy: {},
      tools: [],
      cacheConfig: null,
      adapter: { mode: 'synthetic' },
    },
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
});
