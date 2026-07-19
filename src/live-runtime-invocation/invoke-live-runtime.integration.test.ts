import { existsSync } from 'node:fs';
import { mkdir, readFile, readdir, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import {
  computeCacheContractDigest,
  computeSubjectDigest,
  computeAdapterId,
  computeCacheConfigId,
  computePolicyId,
  computeTemplateId,
  computeToolDefinitionId,
} from '../prefix-contract/digest.js';
import { deriveInteractionId } from '../prefix-contract/interaction-id.js';
import { serializeInputBytes, sha256Hex } from '../runtime-invocation/runtime-files.js';
import { classifyStateBundleV2 } from '../state-v2/index.js';
import { makeStateManifestV2Input } from '../state-v2/test-helpers.js';
import type { ReviewInputV1 } from '../protocol/review-input.js';
import { invokeLiveRuntime } from './invoke-live-runtime.js';

const epoch = 'S00000000000000000000A';
const headSha = '0'.repeat(39) + '2';
const baseSha = '0'.repeat(39) + '1';

function envelopeSet() {
  const envelopes = {
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
  const ids = [
    computeTemplateId(envelopes.template),
    computePolicyId(envelopes.policy),
    computeToolDefinitionId(envelopes.tools),
    computeCacheConfigId(envelopes.cacheConfig),
    computeAdapterId(envelopes.adapter),
  ];
  const values = ids.map((id) => {
    if (!id.ok) throw new Error('invalid test envelope');
    return id.value;
  });
  return {
    envelopes,
    identity: {
      ledgerSchemaVersion: 1 as const,
      prefixContractVersion: 1 as const,
      providerId: 'synthetic',
      modelId: 'synthetic-model',
      templateId: values[0],
      policyId: values[1],
      toolDefinitionId: values[2],
      cacheConfigId: values[3],
      adapterId: values[4],
    },
  };
}

function input(): ReviewInputV1 {
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

describe('invokeLiveRuntime bootstrap transaction', () => {
  it('runs the framework-dependent runtime, returns a valid lease, and releases twice', async () => {
    if (process.platform === 'win32') return;
    const runtimeDll = path.resolve(
      'runtime/src/AgenticPrReview.Runtime/bin/Release/net10.0/AgenticPrReview.Runtime.dll',
    );
    if (!existsSync(runtimeDll)) return;
    const reviewInput = input();
    const inputHash = sha256Hex(serializeInputBytes(reviewInput));
    const { envelopes, identity } = envelopeSet();
    const cacheDigest = computeCacheContractDigest(identity);
    const subjectDigest = computeSubjectDigest(reviewInput.subject);
    if (!cacheDigest.ok || !subjectDigest.ok) throw new Error('invalid test digest');
    const interaction = deriveInteractionId({ kind: 'bootstrap' }, inputHash, headSha, 0);
    if (!interaction.ok) throw new Error('invalid interaction');
    const context = {
      schemaVersion: 1 as const,
      stateKey: {
        namespace: 'm4-ledger-v2' as const,
        repository: 'acme/widgets',
        headRepository: 'acme/widgets',
        pullRequest: 42,
        workflowIdentity: 'workflow',
        trustedExecutionDomain: 'trusted',
      },
      sessionEpoch: epoch,
      cacheContractIdentity: identity as never,
      generation: { stateGeneration: 0, ledgerEpoch: epoch },
      transition: {
        kind: 'bootstrap' as const,
        reason: 'new_session' as const,
        predecessorLedgerSha256: 'bootstrap' as const,
        predecessorManifestSha256: 'bootstrap' as const,
      },
      currentInteraction: {
        interactionId: interaction.value,
        interactionOrdinal: 0,
        consumedInputSha256: inputHash,
        subjectDigest: subjectDigest.value,
        cacheContractDigest: cacheDigest.value,
      },
      cacheContractEnvelopes: envelopes,
      providerMode: 'synthetic' as const,
      producingRun: { producingRunId: '1', runAttempt: 1 },
    };
    const manifestInput = makeStateManifestV2Input({
      sessionEpoch: epoch as never,
      stateKey: context.stateKey,
      cacheContractIdentity: identity as never,
      generation: context.generation as never,
      transition: context.transition,
      provenance: {
        reviewedHeadSha: headSha as never,
        reviewedBaseSha: baseSha as never,
        currentHeadSha: headSha as never,
        currentBaseSha: baseSha as never,
        producingRunId: '1',
        producingRunAttempt: 1,
      },
    });
    const root = path.join(os.tmpdir(), `apr-live-test-${Date.now()}`);
    await mkdir(root, {
      recursive: true,
    });
    try {
      const lease = await invokeLiveRuntime({
        command: {
          executablePath: process.env.DOTNET_HOST_PATH ?? 'dotnet',
          prefixArgs: ['exec', runtimeDll],
        },
        input: reviewInput,
        context,
        manifestInput,
        timeoutMs: 20_000,
        trustedRoot: root,
      });
      const names = await readdir(lease.bundleDirectory);
      const bundle = {
        entryListing: names.map((name) => ({ name, isRegularFile: true })),
        manifestBytes: await readFile(path.join(lease.bundleDirectory, 'manifest.json')),
        ledgerBytes: await readFile(path.join(lease.bundleDirectory, 'ledger.json')),
        providerRunMetadataBytes: await readFile(
          path.join(lease.bundleDirectory, 'provider-run-metadata.json'),
        ),
      };
      expect(classifyStateBundleV2(bundle).kind).toBe('valid');
      await lease.release();
      await lease.release();
      expect(existsSync(lease.bundleDirectory)).toBe(false);
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  }, 30_000);
});
