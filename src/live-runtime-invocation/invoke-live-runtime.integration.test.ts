import { existsSync } from 'node:fs';
import { mkdir, readFile, readdir, rm, writeFile } from 'node:fs/promises';
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
import { canonicalJsonBytes } from '../canonical-json/index.js';
import { deriveInteractionId } from '../prefix-contract/interaction-id.js';
import {
  computeMetadataSemanticSha256,
  parseProviderRunMetadata,
} from '../provider-metadata/index.js';
import { serializeInputBytes, sha256Hex } from '../runtime-invocation/runtime-files.js';
import { classifyStateBundleV2, LEDGER_MAX_BYTES, METADATA_MAX_BYTES } from '../state-v2/index.js';
import { makeStateManifestV2Input } from '../state-v2/test-helpers.js';
import {
  acceptLocalCandidate,
  candidateIdentity,
  computeSelectionSnapshotId,
  observedSelectorSnapshotSha256,
  ReferenceStateStore,
} from '../state-acceptance/index.js';
import type { ReviewInputV1 } from '../protocol/review-input.js';
import { invokeLiveRuntime, runProcess } from './invoke-live-runtime.js';

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
    const runDirectReviewLive = async (directInput: ReviewInputV1, directContext = context) => {
      const directory = path.join(root, 'direct-cli');
      await mkdir(directory, { recursive: true });
      const inputPath = path.join(directory, 'input.json');
      const contextPath = path.join(directory, 'live-context.json');
      const outputPaths = [
        path.join(directory, 'result.json'),
        path.join(directory, 'trace.json'),
        path.join(directory, 'candidate-ledger.json'),
        path.join(directory, 'provider-run-metadata.json'),
      ];
      await writeFile(inputPath, serializeInputBytes(directInput), { flag: 'wx', mode: 0o600 });
      await writeFile(contextPath, new TextEncoder().encode(JSON.stringify(directContext)), {
        flag: 'wx',
        mode: 0o600,
      });
      const directResult = await runProcess(
        { executablePath: runtimeExecutable, prefixArgs: prefixArgs as string[] },
        [
          'review-live',
          '--input',
          inputPath,
          '--context',
          contextPath,
          '--output',
          outputPaths[0],
          '--trace',
          outputPaths[1],
          '--candidate-ledger',
          outputPaths[2],
          '--provider-run-metadata',
          outputPaths[3],
        ],
        20_000,
        undefined,
        directory,
      );
      await rm(directory, { recursive: true, force: true });
      return new TextDecoder().decode(directResult.stderr);
    };
    await expect(
      runDirectReviewLive({
        ...reviewInput,
        host: {
          ...reviewInput.host,
          review: { ...reviewInput.host.review, runtimeProvider: 'claude-code-cli' },
        },
      }),
    ).resolves.toContain('APR_INPUT_RUNTIME_PROVIDER_INVALID');
    await expect(
      runDirectReviewLive({ ...reviewInput, requestedRuntimeVersion: 'not-this-runtime' }),
    ).resolves.toContain('APR_RUNTIME_VERSION_MISMATCH');
    const surrogateContext = structuredClone(context);
    (surrogateContext.stateKey as Record<string, unknown>).workflowIdentity = '\ud800';
    await expect(runDirectReviewLive(reviewInput, surrogateContext)).resolves.toContain(
      'APR_LIVE_CONTEXT_UNICODE',
    );
    const surrogatePropertyContext = structuredClone(context);
    (
      surrogatePropertyContext.cacheContractEnvelopes.template as Record<string, unknown>
    ).definition = {
      '\ud800': 'value',
    };
    await expect(runDirectReviewLive(reviewInput, surrogatePropertyContext)).resolves.toContain(
      'APR_LIVE_CONTEXT_UNICODE',
    );
    const controlContext = structuredClone(context);
    (controlContext.stateKey as Record<string, unknown>).workflowIdentity = 'bad\u0000identity';
    await expect(runDirectReviewLive(reviewInput, controlContext)).resolves.toContain(
      'APR_LIVE_CONTEXT_SEMANTIC_INVALID',
    );
    await mkdir(root, {
      recursive: true,
    });
    try {
      const foreignHeadRepositoryContext = structuredClone(context);
      (foreignHeadRepositoryContext.stateKey as Record<string, unknown>).headRepository =
        'other/widgets';
      await expect(
        invokeLiveRuntime({
          command: { executablePath: runtimeExecutable, prefixArgs: prefixArgs as string[] },
          input: reviewInput,
          context: foreignHeadRepositoryContext,
          manifestInput,
          timeoutMs: 20_000,
          trustedRoot: root,
        }),
      ).rejects.toMatchObject({ kind: 'binding-mismatch' });
      const tooManyChangedFiles = Array.from({ length: 201 }, (_, index) => ({
        ...reviewInput.subject.changedFiles[0]!,
        path: `src/generated-${index}.ts`,
        previousPath: null,
        status: 'added' as const,
      }));
      await expect(
        invokeLiveRuntime({
          command: { executablePath: runtimeExecutable, prefixArgs: prefixArgs as string[] },
          input: {
            ...reviewInput,
            subject: { ...reviewInput.subject, changedFiles: tooManyChangedFiles },
          },
          context,
          manifestInput,
          timeoutMs: 20_000,
          trustedRoot: root,
        }),
      ).rejects.toMatchObject({ kind: 'options-invalid' });
      const invalidStatusInput = {
        ...reviewInput,
        subject: {
          ...reviewInput.subject,
          changedFiles: reviewInput.subject.changedFiles.map((file, index) =>
            index === 0 ? { ...file, status: 'unknown' } : file,
          ),
        },
      };
      await expect(
        invokeLiveRuntime({
          command: { executablePath: runtimeExecutable, prefixArgs: prefixArgs as string[] },
          input: invalidStatusInput,
          context,
          manifestInput,
          timeoutMs: 20_000,
          trustedRoot: root,
        }),
      ).rejects.toMatchObject({ kind: 'options-invalid' });
      const oversizedPatchInput = {
        ...reviewInput,
        subject: {
          ...reviewInput.subject,
          changedFiles: reviewInput.subject.changedFiles.map((file, index) =>
            index === 0 && file.patch
              ? { ...file, patch: { ...file.patch, maxChars: 1_000_001 } }
              : file,
          ),
        },
      };
      await expect(
        invokeLiveRuntime({
          command: { executablePath: runtimeExecutable, prefixArgs: prefixArgs as string[] },
          input: oversizedPatchInput,
          context,
          manifestInput,
          timeoutMs: 20_000,
          trustedRoot: root,
        }),
      ).rejects.toMatchObject({ kind: 'options-invalid' });
      const oversizedPathInputs = [
        {
          ...reviewInput.subject.changedFiles[0]!,
          path: 'x'.repeat(501),
        },
        {
          ...reviewInput.subject.changedFiles[0]!,
          previousPath: 'x'.repeat(501),
        },
      ];
      for (const oversizedFile of oversizedPathInputs) {
        await expect(
          invokeLiveRuntime({
            command: { executablePath: runtimeExecutable, prefixArgs: prefixArgs as string[] },
            input: {
              ...reviewInput,
              subject: {
                ...reviewInput.subject,
                changedFiles: [oversizedFile],
              },
            },
            context,
            manifestInput,
            timeoutMs: 20_000,
            trustedRoot: root,
          }),
        ).rejects.toMatchObject({ kind: 'options-invalid' });
      }
      const oversizedManifestInput = {
        ...manifestInput,
        provenance: {
          ...manifestInput.provenance,
          producingWorkflowRef: 'x'.repeat(70_000),
        },
      };
      await expect(
        invokeLiveRuntime({
          command: { executablePath: runtimeExecutable, prefixArgs: prefixArgs as string[] },
          input: reviewInput,
          context,
          manifestInput: oversizedManifestInput,
          timeoutMs: 20_000,
          trustedRoot: root,
        }),
      ).rejects.toMatchObject({ kind: 'options-invalid' });
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
      expect(lease.manifest.sessionEpoch).not.toBe(epoch);
      expect(lease.manifest.generation.ledgerEpoch).not.toBe(epoch);
      const bundle = {
        entryListing: names.map((name) => ({ name, isRegularFile: true })),
        manifestBytes: await readFile(path.join(lease.bundleDirectory, 'manifest.json')),
        ledgerBytes: await readFile(path.join(lease.bundleDirectory, 'ledger.json')),
        providerRunMetadataBytes: await readFile(
          path.join(lease.bundleDirectory, 'provider-run-metadata.json'),
        ),
      };
      expect(classifyStateBundleV2(bundle).kind).toBe('valid');
      const acceptanceIdentity = candidateIdentity(lease);
      expect(acceptanceIdentity.bundle.manifestBytes).toEqual(new Uint8Array(bundle.manifestBytes));
      expect(acceptanceIdentity.bundle.ledgerBytes).toEqual(new Uint8Array(bundle.ledgerBytes));
      expect(acceptanceIdentity.bundle.providerRunMetadataBytes).toEqual(
        new Uint8Array(bundle.providerRunMetadataBytes),
      );
      const selectionSnapshotDraft = {
        schemaVersion: 1 as const,
        kind: 'bootstrap_selected' as const,
        stateKey: lease.manifest.stateKey,
        currentHeadSha: lease.manifest.provenance.currentHeadSha,
        currentBaseSha: lease.manifest.provenance.currentBaseSha,
        currentBaseRef: lease.manifest.provenance.currentBaseRef,
        observedSelectorBytes: null,
        observedSelectorRevision: 'bootstrap' as const,
        observedSelectorSnapshotSha256: observedSelectorSnapshotSha256(null),
        transitionPlan: 'bootstrap' as const,
        selectionSnapshotId: '' as never,
      };
      const bootstrapSelectionSnapshot = {
        ...selectionSnapshotDraft,
        selectionSnapshotId: computeSelectionSnapshotId(selectionSnapshotDraft),
      };
      const acceptanceStore = new ReferenceStateStore(path.join(root, 'state-acceptance'));
      const acceptance = await acceptLocalCandidate(acceptanceStore, {
        selectionSnapshot: bootstrapSelectionSnapshot,
        candidate: lease,
        interactionId: lease.manifest.transaction.interactionId,
        interactionOrdinal: lease.manifest.transaction.interactionOrdinal,
        producingRunId: lease.manifest.provenance.producingRunId,
        producingRunAttempt: lease.manifest.provenance.producingRunAttempt,
        acceptingRunId: '2',
        acceptingRunAttempt: 1,
        consumedInputSha256: lease.manifest.transaction.consumedInputSha256,
        transition: lease.manifest.transition,
        publishSticky: async (markerId) => {
          expect(markerId).toMatch(/^[a-f0-9]{64}$/);
        },
      });
      expect(acceptance.acceptance).toBe('accepted');
      await acceptanceStore.close();
      const reopenedAcceptanceStore = new ReferenceStateStore(path.join(root, 'state-acceptance'));
      const { ledgerSchemaVersion, prefixContractVersion, ...cacheContractIdentity } =
        lease.manifest.cacheContractIdentity;
      const restored = await reopenedAcceptanceStore.selectAcceptedState({
        stateKey: lease.manifest.stateKey,
        expectedLedgerSchemaVersion: ledgerSchemaVersion,
        expectedPrefixContractVersion: prefixContractVersion,
        cacheContractIdentity,
        currentHeadSha: lease.manifest.provenance.currentHeadSha,
        currentBaseSha: lease.manifest.provenance.currentBaseSha,
        currentBaseRef: lease.manifest.provenance.currentBaseRef,
        provenanceTrusted: true,
        workflowIdentity: lease.manifest.stateKey.workflowIdentity,
        trustedExecutionDomain: lease.manifest.stateKey.trustedExecutionDomain,
        headRelationship: 'same',
      });
      expect(restored.selection).toBe('selected');
      if (restored.selection !== 'selected' || restored.snapshot.kind !== 'continuation_selected')
        throw new Error('accepted bootstrap must restore as continuation');
      const predecessorLedgerBytes = new Uint8Array(restored.snapshot.predecessorBytes.ledgerBytes);
      const predecessorManifestBytes = new Uint8Array(
        restored.snapshot.predecessorBytes.manifestBytes,
      );
      const predecessorProviderRunMetadataBytes = new Uint8Array(
        restored.snapshot.predecessorBytes.providerRunMetadataBytes,
      );
      const predecessorLedgerSha256 = sha256Hex(predecessorLedgerBytes);
      const predecessorManifestSha256 = sha256Hex(predecessorManifestBytes);
      const continuationInteraction = deriveInteractionId(
        { kind: 'ledger', sha256Hex: predecessorLedgerSha256 },
        inputHash,
        headSha,
        1,
      );
      if (!continuationInteraction.ok) throw new Error('invalid continuation interaction');
      const sessionEpoch = lease.manifest.sessionEpoch;
      const ledgerEpoch = lease.manifest.generation.ledgerEpoch;
      const continuationContext = {
        ...context,
        sessionEpoch,
        generation: { stateGeneration: 1, ledgerEpoch },
        transition: {
          kind: 'continuation' as const,
          predecessorManifestSha256,
          predecessorLedgerSha256,
          predecessorLedgerEpoch: ledgerEpoch,
          predecessorStateGeneration: 0,
        },
        currentInteraction: {
          ...context.currentInteraction,
          interactionId: continuationInteraction.value,
          interactionOrdinal: 1,
        },
      };
      const continuationManifest = makeStateManifestV2Input({
        sessionEpoch: sessionEpoch as never,
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
      const mismatchedManifestHash = 'f'.repeat(64);
      const mismatchedContinuationTransition = {
        ...continuationContext.transition,
        predecessorManifestSha256: mismatchedManifestHash as never,
      };
      await expect(
        invokeLiveRuntime({
          command: { executablePath: runtimeExecutable, prefixArgs: prefixArgs as string[] },
          input: reviewInput,
          context: {
            ...continuationContext,
            transition: mismatchedContinuationTransition as never,
          },
          manifestInput: {
            ...continuationManifest,
            transition: mismatchedContinuationTransition as never,
          },
          predecessorLedgerBytes,
          predecessorManifestBytes,
          predecessorProviderRunMetadataBytes,
          timeoutMs: 20_000,
          trustedRoot: root,
        }),
      ).rejects.toMatchObject({ kind: 'restore-plan-invalid' });
      const inconsistentPredecessorProvenance = JSON.parse(
        new TextDecoder().decode(predecessorManifestBytes),
      ) as { provenance: Record<string, unknown> };
      inconsistentPredecessorProvenance.provenance.reviewedHeadSha = '1'.repeat(40);
      const inconsistentProvenanceManifestBytes = canonicalJsonBytes(
        inconsistentPredecessorProvenance,
      );
      const inconsistentProvenanceTransition = {
        ...continuationContext.transition,
        predecessorManifestSha256: sha256Hex(inconsistentProvenanceManifestBytes) as never,
      };
      await expect(
        invokeLiveRuntime({
          command: { executablePath: runtimeExecutable, prefixArgs: prefixArgs as string[] },
          input: reviewInput,
          context: {
            ...continuationContext,
            transition: inconsistentProvenanceTransition as never,
          },
          manifestInput: {
            ...continuationManifest,
            transition: inconsistentProvenanceTransition as never,
          },
          predecessorLedgerBytes,
          predecessorManifestBytes: inconsistentProvenanceManifestBytes,
          predecessorProviderRunMetadataBytes,
          timeoutMs: 20_000,
          trustedRoot: root,
        }),
      ).rejects.toMatchObject({ kind: 'restore-plan-invalid' });
      const inconsistentPredecessorManifest = JSON.parse(
        new TextDecoder().decode(predecessorManifestBytes),
      ) as { cacheContractIdentity: Record<string, unknown> };
      inconsistentPredecessorManifest.cacheContractIdentity.modelId = 'different-model';
      const inconsistentPredecessorManifestBytes = canonicalJsonBytes(
        inconsistentPredecessorManifest,
      );
      const inconsistentPredecessorManifestSha256 = sha256Hex(inconsistentPredecessorManifestBytes);
      const inconsistentIdentityTransition = {
        ...continuationContext.transition,
        predecessorManifestSha256: inconsistentPredecessorManifestSha256 as never,
      };
      await expect(
        invokeLiveRuntime({
          command: { executablePath: runtimeExecutable, prefixArgs: prefixArgs as string[] },
          input: reviewInput,
          context: {
            ...continuationContext,
            transition: inconsistentIdentityTransition as never,
          },
          manifestInput: {
            ...continuationManifest,
            transition: inconsistentIdentityTransition as never,
          },
          predecessorLedgerBytes,
          predecessorManifestBytes: inconsistentPredecessorManifestBytes,
          predecessorProviderRunMetadataBytes,
          timeoutMs: 20_000,
          trustedRoot: root,
        }),
      ).rejects.toMatchObject({ kind: 'restore-plan-invalid' });
      const inconsistentPredecessorTransaction = JSON.parse(
        new TextDecoder().decode(predecessorManifestBytes),
      ) as { transaction: Record<string, unknown> };
      inconsistentPredecessorTransaction.transaction.interactionOrdinal = 99;
      const inconsistentPredecessorTransactionBytes = canonicalJsonBytes(
        inconsistentPredecessorTransaction,
      );
      const inconsistentTransactionTransition = {
        ...continuationContext.transition,
        predecessorManifestSha256: sha256Hex(inconsistentPredecessorTransactionBytes) as never,
      };
      await expect(
        invokeLiveRuntime({
          command: { executablePath: runtimeExecutable, prefixArgs: prefixArgs as string[] },
          input: reviewInput,
          context: {
            ...continuationContext,
            transition: inconsistentTransactionTransition as never,
          },
          manifestInput: {
            ...continuationManifest,
            transition: inconsistentTransactionTransition as never,
          },
          predecessorLedgerBytes,
          predecessorManifestBytes: inconsistentPredecessorTransactionBytes,
          predecessorProviderRunMetadataBytes,
          timeoutMs: 20_000,
          trustedRoot: root,
        }),
      ).rejects.toMatchObject({ kind: 'restore-plan-invalid' });
      const inconsistentPredecessorLedgerDescriptor = JSON.parse(
        new TextDecoder().decode(predecessorManifestBytes),
      ) as { ledger: Record<string, unknown> };
      inconsistentPredecessorLedgerDescriptor.ledger.bytes = predecessorLedgerBytes.byteLength + 1;
      const inconsistentPredecessorLedgerDescriptorBytes = canonicalJsonBytes(
        inconsistentPredecessorLedgerDescriptor,
      );
      const inconsistentLedgerDescriptorTransition = {
        ...continuationContext.transition,
        predecessorManifestSha256: sha256Hex(inconsistentPredecessorLedgerDescriptorBytes) as never,
      };
      await expect(
        invokeLiveRuntime({
          command: { executablePath: runtimeExecutable, prefixArgs: prefixArgs as string[] },
          input: reviewInput,
          context: {
            ...continuationContext,
            transition: inconsistentLedgerDescriptorTransition as never,
          },
          manifestInput: {
            ...continuationManifest,
            transition: inconsistentLedgerDescriptorTransition as never,
          },
          predecessorLedgerBytes,
          predecessorManifestBytes: inconsistentPredecessorLedgerDescriptorBytes,
          predecessorProviderRunMetadataBytes,
          timeoutMs: 20_000,
          trustedRoot: root,
        }),
      ).rejects.toMatchObject({ kind: 'restore-plan-invalid' });
      await expect(
        invokeLiveRuntime({
          command: { executablePath: runtimeExecutable, prefixArgs: prefixArgs as string[] },
          input: reviewInput,
          context: continuationContext,
          manifestInput: continuationManifest,
          predecessorLedgerBytes,
          predecessorManifestBytes,
          timeoutMs: 20_000,
          trustedRoot: root,
        }),
      ).rejects.toMatchObject({ kind: 'options-invalid' });
      await expect(
        invokeLiveRuntime({
          command: { executablePath: runtimeExecutable, prefixArgs: prefixArgs as string[] },
          input: reviewInput,
          context: continuationContext,
          manifestInput: continuationManifest,
          predecessorLedgerBytes: new Uint8Array(LEDGER_MAX_BYTES + 1),
          predecessorManifestBytes,
          predecessorProviderRunMetadataBytes,
          timeoutMs: 20_000,
          trustedRoot: root,
        }),
      ).rejects.toMatchObject({ kind: 'restore-plan-invalid' });
      await expect(
        invokeLiveRuntime({
          command: { executablePath: runtimeExecutable, prefixArgs: prefixArgs as string[] },
          input: reviewInput,
          context: continuationContext,
          manifestInput: continuationManifest,
          predecessorLedgerBytes,
          predecessorManifestBytes,
          predecessorProviderRunMetadataBytes: new Uint8Array(METADATA_MAX_BYTES + 1),
          timeoutMs: 20_000,
          trustedRoot: root,
        }),
      ).rejects.toMatchObject({ kind: 'restore-plan-invalid' });
      const invalidPredecessorMetadataBytes = new TextEncoder().encode('{');
      const inconsistentPredecessorMetadataManifest = JSON.parse(
        new TextDecoder().decode(predecessorManifestBytes),
      ) as { providerRunMetadata: Record<string, unknown> };
      inconsistentPredecessorMetadataManifest.providerRunMetadata.bytes =
        invalidPredecessorMetadataBytes.byteLength;
      inconsistentPredecessorMetadataManifest.providerRunMetadata.sha256 = sha256Hex(
        invalidPredecessorMetadataBytes,
      );
      const inconsistentPredecessorMetadataManifestBytes = canonicalJsonBytes(
        inconsistentPredecessorMetadataManifest,
      );
      const inconsistentMetadataTransition = {
        ...continuationContext.transition,
        predecessorManifestSha256: sha256Hex(inconsistentPredecessorMetadataManifestBytes) as never,
      };
      await expect(
        invokeLiveRuntime({
          command: { executablePath: runtimeExecutable, prefixArgs: prefixArgs as string[] },
          input: reviewInput,
          context: {
            ...continuationContext,
            transition: inconsistentMetadataTransition as never,
          },
          manifestInput: {
            ...continuationManifest,
            transition: inconsistentMetadataTransition as never,
          },
          predecessorLedgerBytes,
          predecessorManifestBytes: inconsistentPredecessorMetadataManifestBytes,
          predecessorProviderRunMetadataBytes: invalidPredecessorMetadataBytes,
          timeoutMs: 20_000,
          trustedRoot: root,
        }),
      ).rejects.toMatchObject({ kind: 'restore-plan-invalid' });
      const semanticallyTamperedMetadata = JSON.parse(
        new TextDecoder().decode(predecessorProviderRunMetadataBytes),
      ) as { logicalPrefixSha256: string };
      semanticallyTamperedMetadata.logicalPrefixSha256 = 'f'.repeat(64);
      const semanticallyTamperedMetadataBytes = canonicalJsonBytes(semanticallyTamperedMetadata);
      const semanticallyTamperedManifest = JSON.parse(
        new TextDecoder().decode(predecessorManifestBytes),
      ) as { providerRunMetadata: Record<string, unknown> };
      semanticallyTamperedManifest.providerRunMetadata.bytes =
        semanticallyTamperedMetadataBytes.byteLength;
      semanticallyTamperedManifest.providerRunMetadata.sha256 = sha256Hex(
        semanticallyTamperedMetadataBytes,
      );
      const semanticallyTamperedManifestBytes = canonicalJsonBytes(semanticallyTamperedManifest);
      const semanticallyTamperedTransition = {
        ...continuationContext.transition,
        predecessorManifestSha256: sha256Hex(semanticallyTamperedManifestBytes) as never,
      };
      await expect(
        invokeLiveRuntime({
          command: { executablePath: runtimeExecutable, prefixArgs: prefixArgs as string[] },
          input: reviewInput,
          context: {
            ...continuationContext,
            transition: semanticallyTamperedTransition as never,
          },
          manifestInput: {
            ...continuationManifest,
            transition: semanticallyTamperedTransition as never,
          },
          predecessorLedgerBytes,
          predecessorManifestBytes: semanticallyTamperedManifestBytes,
          predecessorProviderRunMetadataBytes: semanticallyTamperedMetadataBytes,
          timeoutMs: 20_000,
          trustedRoot: root,
        }),
      ).rejects.toMatchObject({ kind: 'restore-plan-invalid' });
      const transactionTamperedMetadata = JSON.parse(
        new TextDecoder().decode(predecessorProviderRunMetadataBytes),
      ) as { candidateLedgerSha256: string };
      transactionTamperedMetadata.candidateLedgerSha256 = 'e'.repeat(64);
      const transactionTamperedMetadataBytes = canonicalJsonBytes(transactionTamperedMetadata);
      const transactionTamperedManifest = JSON.parse(
        new TextDecoder().decode(predecessorManifestBytes),
      ) as { providerRunMetadata: Record<string, unknown> };
      transactionTamperedManifest.providerRunMetadata.bytes =
        transactionTamperedMetadataBytes.byteLength;
      transactionTamperedManifest.providerRunMetadata.sha256 = sha256Hex(
        transactionTamperedMetadataBytes,
      );
      const transactionTamperedManifestBytes = canonicalJsonBytes(transactionTamperedManifest);
      const transactionTamperedTransition = {
        ...continuationContext.transition,
        predecessorManifestSha256: sha256Hex(transactionTamperedManifestBytes) as never,
      };
      await expect(
        invokeLiveRuntime({
          command: { executablePath: runtimeExecutable, prefixArgs: prefixArgs as string[] },
          input: reviewInput,
          context: {
            ...continuationContext,
            transition: transactionTamperedTransition as never,
          },
          manifestInput: {
            ...continuationManifest,
            transition: transactionTamperedTransition as never,
          },
          predecessorLedgerBytes,
          predecessorManifestBytes: transactionTamperedManifestBytes,
          predecessorProviderRunMetadataBytes: transactionTamperedMetadataBytes,
          timeoutMs: 20_000,
          trustedRoot: root,
        }),
      ).rejects.toMatchObject({ kind: 'restore-plan-invalid' });
      const continuationLease = await invokeLiveRuntime({
        command: { executablePath: runtimeExecutable, prefixArgs: prefixArgs as string[] },
        input: reviewInput,
        context: continuationContext,
        manifestInput: continuationManifest,
        predecessorLedgerBytes,
        predecessorManifestBytes,
        predecessorProviderRunMetadataBytes,
        timeoutMs: 20_000,
        trustedRoot: root,
      });
      const tamperedHistoricalLedger = JSON.parse(
        new TextDecoder().decode(continuationLease.ledgerBytes),
      ) as { header: Record<string, unknown> };
      tamperedHistoricalLedger.header.predecessorLedgerSha256 = 'd'.repeat(64);
      const tamperedHistoricalLedgerBytes = canonicalJsonBytes(tamperedHistoricalLedger);
      const tamperedHistoricalLedgerSha256 = sha256Hex(tamperedHistoricalLedgerBytes);
      const tamperedHistoricalMetadata = JSON.parse(
        new TextDecoder().decode(continuationLease.providerRunMetadataBytes),
      ) as { candidateLedgerSha256: string };
      tamperedHistoricalMetadata.candidateLedgerSha256 = tamperedHistoricalLedgerSha256;
      const tamperedHistoricalMetadataBytes = canonicalJsonBytes(tamperedHistoricalMetadata);
      const parsedTamperedHistoricalMetadata = parseProviderRunMetadata(
        tamperedHistoricalMetadataBytes,
      );
      if (!parsedTamperedHistoricalMetadata.valid)
        throw new Error('tampered historical metadata should remain schema-valid');
      const tamperedHistoricalManifest = JSON.parse(
        new TextDecoder().decode(continuationLease.manifestBytes),
      ) as {
        ledger: Record<string, unknown>;
        providerRunMetadata: Record<string, unknown>;
        transaction: Record<string, unknown>;
      };
      tamperedHistoricalManifest.ledger.bytes = tamperedHistoricalLedgerBytes.byteLength;
      tamperedHistoricalManifest.ledger.sha256 = tamperedHistoricalLedgerSha256;
      tamperedHistoricalManifest.transaction.candidateLedgerSha256 = tamperedHistoricalLedgerSha256;
      tamperedHistoricalManifest.providerRunMetadata.bytes =
        tamperedHistoricalMetadataBytes.byteLength;
      tamperedHistoricalManifest.providerRunMetadata.sha256 = sha256Hex(
        tamperedHistoricalMetadataBytes,
      );
      tamperedHistoricalManifest.transaction.metadataSemanticSha256 = computeMetadataSemanticSha256(
        parsedTamperedHistoricalMetadata.metadata,
      );
      const tamperedHistoricalManifestBytes = canonicalJsonBytes(tamperedHistoricalManifest);
      const tamperedHistoricalManifestSha256 = sha256Hex(tamperedHistoricalManifestBytes);
      const nextInteraction = deriveInteractionId(
        { kind: 'ledger', sha256Hex: tamperedHistoricalLedgerSha256 },
        inputHash,
        headSha,
        2,
      );
      if (!nextInteraction.ok) throw new Error('invalid next continuation interaction');
      const nextTransition = {
        kind: 'continuation' as const,
        predecessorManifestSha256: tamperedHistoricalManifestSha256,
        predecessorLedgerSha256: tamperedHistoricalLedgerSha256,
        predecessorLedgerEpoch: ledgerEpoch,
        predecessorStateGeneration: 1,
      };
      const nextContext = {
        ...continuationContext,
        generation: { stateGeneration: 2, ledgerEpoch },
        transition: nextTransition,
        currentInteraction: {
          ...continuationContext.currentInteraction,
          interactionId: nextInteraction.value,
          interactionOrdinal: 2,
        },
      };
      const nextManifest = makeStateManifestV2Input({
        sessionEpoch: sessionEpoch as never,
        stateKey: nextContext.stateKey,
        cacheContractIdentity: identity as never,
        generation: nextContext.generation as never,
        transition: nextTransition as never,
        provenance: {
          reviewedHeadSha: headSha as never,
          reviewedBaseSha: baseSha as never,
          currentHeadSha: headSha as never,
          currentBaseSha: baseSha as never,
          producingRunId: '1',
          producingRunAttempt: 1,
        },
      });
      await expect(
        invokeLiveRuntime({
          command: { executablePath: runtimeExecutable, prefixArgs: prefixArgs as string[] },
          input: reviewInput,
          context: nextContext,
          manifestInput: nextManifest,
          predecessorLedgerBytes: tamperedHistoricalLedgerBytes,
          predecessorManifestBytes: tamperedHistoricalManifestBytes,
          predecessorProviderRunMetadataBytes: tamperedHistoricalMetadataBytes,
          timeoutMs: 20_000,
          trustedRoot: root,
        }),
      ).rejects.toMatchObject({ kind: 'restore-plan-invalid' });
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
      const continuationAcceptance = await acceptLocalCandidate(reopenedAcceptanceStore, {
        selectionSnapshot: restored.snapshot,
        candidate: continuationLease,
        interactionId: continuationLease.manifest.transaction.interactionId,
        interactionOrdinal: continuationLease.manifest.transaction.interactionOrdinal,
        producingRunId: continuationLease.manifest.provenance.producingRunId,
        producingRunAttempt: continuationLease.manifest.provenance.producingRunAttempt,
        acceptingRunId: '3',
        acceptingRunAttempt: 1,
        consumedInputSha256: continuationLease.manifest.transaction.consumedInputSha256,
        transition: continuationLease.manifest.transition,
        publishSticky: async (markerId) => {
          expect(markerId).toMatch(/^[a-f0-9]{64}$/);
        },
      });
      expect(continuationAcceptance.acceptance).toBe('accepted');
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
