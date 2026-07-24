import { mkdtemp, realpath, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { computeCacheContractDigest, computeSubjectDigest } from '../prefix-contract/digest.js';
import { deriveInteractionId } from '../prefix-contract/interaction-id.js';
import { deepSeekContractForMaxFindings } from '../live-provider/deepseek-contract.js';
import type { ReviewInputV1 } from '../protocol/review-input.js';
import { serializeInputBytes, sha256Hex } from '../runtime-invocation/runtime-files.js';
import { makeStateManifestV2Input } from '../state-v2/test-helpers.js';
import { invokeLiveRuntime } from './invoke-live-runtime.js';

const headSha = '0'.repeat(39) + '2';
const baseSha = '0'.repeat(39) + '1';
const sessionEpoch = 'S00000000000000000000A';

function makeInput(): ReviewInputV1 {
  return {
    protocolVersion: 1,
    requestedRuntimeVersion: null,
    host: {
      repository: { owner: 'acme', name: 'widgets' },
      review: {
        phase: 'bootstrap',
        baseSha,
        headSha,
        stateKey: 'acme-widgets-pr-42',
        runtimeProvider: 'test',
      },
    },
    subject: {
      pullRequest: {
        number: 42,
        title: 'Bootstrap',
        body: 'Test',
        baseRef: 'main',
        headRef: 'feature',
        draft: false,
      },
      changedFiles: [],
    },
    previousState: { present: false, findingFingerprints: [] },
    commentEvidence: { existingFindingFingerprints: [] },
  };
}

describe('DeepSeek provider-key channel separation', () => {
  it.skipIf(process.platform !== 'linux')(
    'reaches the child for valid keys that collide with ordinary JSON literals',
    async () => {
      const input = makeInput();
      const inputSha256 = sha256Hex(serializeInputBytes(input));
      const contract = deepSeekContractForMaxFindings(10);
      const subjectDigest = computeSubjectDigest(input.subject);
      const cacheContractDigest = computeCacheContractDigest(contract.identity);
      if (!subjectDigest.ok || !cacheContractDigest.ok) throw new Error('invalid test digest');
      const interaction = deriveInteractionId({ kind: 'bootstrap' }, inputSha256, headSha, 0);
      if (!interaction.ok) throw new Error('invalid test interaction');
      const stateKey = {
        namespace: 'm4-ledger-v2' as const,
        repository: 'acme/widgets',
        headRepository: 'acme/widgets',
        pullRequest: 42,
        workflowIdentity: 'workflow',
        trustedExecutionDomain: 'trusted',
      };
      const generation = { stateGeneration: 0, ledgerEpoch: sessionEpoch };
      const transition = {
        kind: 'bootstrap' as const,
        reason: 'new_session' as const,
        predecessorLedgerSha256: 'bootstrap' as const,
        predecessorManifestSha256: 'bootstrap' as const,
      };
      const context = {
        schemaVersion: 1 as const,
        stateKey,
        sessionEpoch,
        cacheContractIdentity: contract.identity,
        generation,
        transition,
        currentInteraction: {
          interactionId: interaction.value,
          interactionOrdinal: 0,
          consumedInputSha256: inputSha256,
          subjectDigest: subjectDigest.value,
          cacheContractDigest: cacheContractDigest.value,
        },
        cacheContractEnvelopes: contract.envelopes,
        providerMode: 'live' as const,
        producingRun: { producingRunId: '1', runAttempt: 1 },
      };
      const manifestInput = makeStateManifestV2Input({
        sessionEpoch: sessionEpoch as never,
        stateKey,
        cacheContractIdentity: contract.identity as never,
        generation: generation as never,
        transition,
        provenance: {
          reviewedHeadSha: headSha as never,
          reviewedBaseSha: baseSha as never,
          currentHeadSha: headSha as never,
          currentBaseSha: baseSha as never,
          producingRunId: '1',
          producingRunAttempt: 1,
        },
      });
      const root = await mkdtemp(path.join(os.tmpdir(), 'apr-provider-key-'));
      const shellExecutable = await realpath('/bin/sh');
      const originalKey = process.env.AGENTIC_REVIEW_DEEPSEEK_API_KEY;
      try {
        for (const key of ['null', 'aaaa']) {
          process.env.AGENTIC_REVIEW_DEEPSEEK_API_KEY = key;
          await expect(
            invokeLiveRuntime({
              command: {
                executablePath: shellExecutable,
                prefixArgs: [
                  '-c',
                  'printf "APR_PROVIDER_CONFIG: Provider invocation failed.\\n" >&2; exit 20',
                  'agentic-review-live',
                ],
              },
              input,
              context,
              manifestInput,
              timeoutMs: 20_000,
              trustedRoot: root,
            }),
          ).rejects.toMatchObject({ kind: 'provider-config', exitCode: 20 });
        }
      } finally {
        if (originalKey === undefined) delete process.env.AGENTIC_REVIEW_DEEPSEEK_API_KEY;
        else process.env.AGENTIC_REVIEW_DEEPSEEK_API_KEY = originalKey;
        await rm(root, { recursive: true, force: true });
      }
    },
  );
});
