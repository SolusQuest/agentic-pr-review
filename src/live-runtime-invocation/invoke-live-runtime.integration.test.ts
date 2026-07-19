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

const integrationEnabled = Boolean(process.env.APR_RUNTIME_INTEGRATION_ROOT);
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
      changedFiles: [
        {
          path: 'src/new.ts',
          previousPath: null,
          status: 'renamed',
          additions: 2,
          deletions: 1,
          changes: 3,
          patch: {
            text: '@@ -1 +1 @@\n-old\n+new',
            truncated: false,
            sha256: sha256Hex(new TextEncoder().encode('@@ -1 +1 @@\n-old\n+new')),
            maxChars: 2000,
          },
        },
        {
          path: 'docs/guide.md',
          previousPath: 'docs/old-guide.md',
          status: 'modified',
          additions: 1,
          deletions: 0,
          changes: 1,
        },
      ],
    },
    previousState: { present: false, findingFingerprints: [] },
    commentEvidence: { existingFindingFingerprints: [] },
  };
}

describe('invokeLiveRuntime bootstrap transaction', () => {
  it('runs the framework-dependent runtime, returns a valid lease, and releases twice', async () => {
    if (!integrationEnabled) return;
    if (process.platform !== 'linux') {
      console.log('LIVE_RUNTIME_INTEGRATION_SKIPPED_NON_LINUX: live sidecar seam is Linux-only');
      return;
    }
    const runtimeExecutable = process.env.APR_RUNTIME_DOTNET;
    const prefixArgs = JSON.parse(process.env.APR_RUNTIME_PREFIX_ARGS_JSON ?? 'null') as unknown;
    if (
      !runtimeExecutable ||
      !path.isAbsolute(runtimeExecutable) ||
      !existsSync(runtimeExecutable) ||
      !Array.isArray(prefixArgs) ||
      prefixArgs.some((arg) => typeof arg !== 'string')
    )
      throw new Error('Runtime CI must provide an absolute live runtime command');
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
          executablePath: runtimeExecutable,
          prefixArgs: prefixArgs as string[],
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
      const predecessorLedgerBytes = new Uint8Array(bundle.ledgerBytes);
      const predecessorManifestBytes = new Uint8Array(bundle.manifestBytes);
      const predecessorLedgerSha256 = sha256Hex(predecessorLedgerBytes);
      const predecessorManifestSha256 = sha256Hex(predecessorManifestBytes);
      const continuationInteraction = deriveInteractionId(
        { kind: 'ledger', sha256Hex: predecessorLedgerSha256 },
        inputHash,
        headSha,
        1,
      );
      if (!continuationInteraction.ok) throw new Error('invalid continuation interaction');
      const continuationContext = {
        ...context,
        generation: { stateGeneration: 1, ledgerEpoch: epoch },
        transition: {
          kind: 'continuation' as const,
          predecessorManifestSha256,
          predecessorLedgerSha256,
          predecessorLedgerEpoch: epoch,
          predecessorStateGeneration: 0,
        },
        currentInteraction: {
          ...context.currentInteraction,
          interactionId: continuationInteraction.value,
          interactionOrdinal: 1,
        },
      };
      const continuationManifest = makeStateManifestV2Input({
        sessionEpoch: epoch as never,
        stateKey: continuationContext.stateKey,
        cacheContractIdentity: identity as never,
        generation: continuationContext.generation as never,
        transition: continuationContext.transition as never,
        provenance: {
          reviewedHeadSha: headSha as never,
          reviewedBaseSha: baseSha as never,
          currentHeadSha: headSha as never,
          currentBaseSha: baseSha as never,
          producingRunId: '1',
          producingRunAttempt: 1,
        },
      });
      const continuationLease = await invokeLiveRuntime({
        command: { executablePath: runtimeExecutable, prefixArgs: prefixArgs as string[] },
        input: reviewInput,
        context: continuationContext,
        manifestInput: continuationManifest,
        predecessorLedgerBytes,
        timeoutMs: 20_000,
        trustedRoot: root,
      });
      expect(
        classifyStateBundleV2({
          entryListing: (await readdir(continuationLease.bundleDirectory)).map((name) => ({
            name,
            isRegularFile: true,
          })),
          manifestBytes: await readFile(
            path.join(continuationLease.bundleDirectory, 'manifest.json'),
          ),
          ledgerBytes: await readFile(path.join(continuationLease.bundleDirectory, 'ledger.json')),
          providerRunMetadataBytes: await readFile(
            path.join(continuationLease.bundleDirectory, 'provider-run-metadata.json'),
          ),
        }).kind,
      ).toBe('valid');
      await continuationLease.release();
      await continuationLease.release();
      expect(existsSync(continuationLease.bundleDirectory)).toBe(false);
      await lease.release();
      await lease.release();
      expect(existsSync(lease.bundleDirectory)).toBe(false);
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  }, 60_000);
});
