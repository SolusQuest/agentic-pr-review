import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
import { readFile, writeFile } from 'node:fs/promises';
import {
  prepare,
  prepareRestore,
  prepareRehearsalCase,
  verify,
  stop,
} from './cr2-hosted-artifact-proof.mjs';
import { runPhases } from '../runtime/tests/ActionHostTrustedProofVerifier/HostedArtifactInterop/run-phases.mjs';
import { requireProof } from './cr2-hosted-artifact-policy.mjs';

export async function rehearse(options = {}) {
  const first = await prepare({ ...options, mode: 'rehearsal', phase: 'produce' });
  let firstStopped = false;
  try {
    const produced = await runPhases(first.configPath);
    requireProof(produced.prepared === 0 && produced.launched === 0 && produced.post === 0);
    const a = await verify(first.configPath);
    const second = await prepareRestore(first.configPath);
    const restored = await runPhases(second.configPath);
    requireProof(restored.prepared === 0 && restored.launched === 0 && restored.post === 0);
    const b = await verify(second.configPath);
    const allPhases = [...produced.statuses, ...restored.statuses];
    const observations = await (
      await fetch(first.config.origin + '/observations', {
        method: 'POST',
        body: first.config.stopNonce,
      })
    ).json();
    requireProof(
      observations.counts.create === 1 &&
        observations.counts.finalize === 1 &&
        observations.counts.deletes === 1 &&
        observations.objectIds.length === 0,
    );
    await stop(first.configPath);
    firstStopped = true;
    const controls = [];
    const cases = [
      {
        name: 'permission-denied-before-upload',
        faults: { permission: true },
        phase: 'produce',
        creates: 0,
      },
      {
        name: 'cancel-before-upload',
        faults: { cancelBefore: true },
        phase: 'produce',
        creates: 0,
      },
      {
        name: 'census-100-refuses-before-upload',
        existingRecords: 100,
        phase: 'produce',
        creates: 0,
      },
      {
        name: 'incomplete-census-refuses',
        faults: { incompleteCensus: true },
        phase: 'produce',
        creates: 0,
      },
      {
        name: 'metadata-byte-budget-refuses-before-upload',
        faults: { metadataLimit: true },
        phase: 'produce',
        creates: 0,
      },
      { name: 'census-99-admits-one-object', existingRecords: 99, phase: 'success', creates: 1 },
      {
        name: 'post-preflight-census-drift',
        existingRecords: 99,
        faults: { censusDrift: true },
        phase: 'produce',
        creates: 1,
      },
      {
        name: 'conflicting-family-refuses',
        faults: { duplicateFamily: true },
        phase: 'produce',
        creates: 1,
      },
      {
        name: 'cancel-after-committed-upload',
        faults: { cancelAfterUpload: true },
        phase: 'produce',
        creates: 1,
      },
      {
        name: 'lost-sdk-finalization-ack',
        faults: { lostFinalize: true },
        phase: 'produce',
        creates: 1,
      },
      {
        name: 'unsafe-archive-redirect',
        faults: { unsafeRedirect: true },
        phase: 'produce',
        creates: 1,
      },
      { name: 'wrong-physical-name', faults: { wrongName: true }, phase: 'produce', creates: 1 },
      { name: 'wrong-archive-digest', faults: { wrongDigest: true }, phase: 'produce', creates: 1 },
      { name: 'wrong-archive-size', faults: { wrongSize: true }, phase: 'produce', creates: 1 },
      { name: 'short-retention', faults: { shortRetention: true }, phase: 'produce', creates: 1 },
      {
        name: 'wrong-producer-attempt',
        faults: { wrongAttempt: true },
        phase: 'produce',
        creates: 1,
      },
      {
        name: 'wrong-producer-source',
        faults: { wrongSource: true },
        phase: 'restore',
        creates: 1,
        deletes: 0,
      },
      {
        name: 'candidate-archive-digest-is-not-authority',
        candidateArchiveDigest: true,
        phase: 'restore',
        creates: 1,
        deletes: 0,
      },
      {
        name: 'candidate-expiry-is-not-authority',
        candidateExpiry: true,
        phase: 'restore',
        creates: 1,
        deletes: 0,
      },
      {
        name: 'candidate-id-is-not-delete-authority',
        candidateId: true,
        phase: 'restore',
        creates: 1,
        deletes: 0,
      },
      {
        name: 'delete-permission-denied-no-replay',
        faults: { deleteDenied: true },
        phase: 'restore',
        creates: 1,
        deletes: 1,
      },
      {
        name: 'lost-delete-ack-no-replay',
        faults: { lostDelete: true },
        phase: 'restore',
        creates: 1,
        deletes: 1,
      },
    ];
    for (const test of cases) {
      const current = await prepareRehearsalCase(first.configPath, test);
      try {
        const phaseA = await runPhases(current.configPath);
        allPhases.push(...phaseA.statuses);
        requireProof(phaseA.prepared === 0 && phaseA.post === 0);
        if (test.phase === 'produce') {
          requireProof(phaseA.launched !== 0);
          const refused = JSON.parse(await readFile(current.config.receiptOut, 'utf8'));
          requireProof(refused.outcome === 'refused');
          if (test.faults?.lostFinalize) requireProof(refused.mutationState === 'upload_unknown');
          if (test.faults?.cancelAfterUpload)
            requireProof(refused.mutationState === 'upload_committed');
          if (test.creates === 0) requireProof(refused.mutationState === 'not_dispatched');
        } else {
          requireProof(phaseA.launched === 0 && phaseA.post === 0);
          await verify(current.configPath);
          if (test.candidateId || test.candidateArchiveDigest || test.candidateExpiry) {
            const candidate = JSON.parse(await readFile(current.config.receiptOut, 'utf8'));
            if (test.candidateId) candidate.artifact.id = '999999999';
            if (test.candidateArchiveDigest) candidate.artifact.archiveDigest = '0'.repeat(64);
            if (test.candidateExpiry) candidate.artifact.expires++;
            await writeFile(current.config.receiptOut, JSON.stringify(candidate), { mode: 0o600 });
          }
          const second = await prepareRestore(current.configPath);
          const phaseB = await runPhases(second.configPath);
          allPhases.push(...phaseB.statuses);
          requireProof(phaseB.prepared === 0 && phaseB.post === 0);
          if (test.phase === 'success') {
            requireProof(phaseB.launched === 0 && phaseB.post === 0);
            await verify(second.configPath);
          } else {
            requireProof(phaseB.launched !== 0);
            const refused = JSON.parse(await readFile(second.config.receiptOut, 'utf8'));
            requireProof(refused.outcome === 'refused');
            if (test.faults?.lostDelete || test.faults?.deleteDenied)
              requireProof(refused.mutationState === 'delete_unknown');
          }
        }
        requireProof(
          current.service.counts.create === test.creates &&
            current.service.counts.deletes === (test.deletes ?? (test.phase === 'success' ? 1 : 0)),
        );
        requireProof(
          current.service.counts.create <= 5 &&
            current.service.counts.finalize <= 5 &&
            current.service.counts.blob <= 8,
        );
        controls.push({
          case: test.name,
          passed: true,
          createRequests: current.service.counts.create,
          finalizeRequests: current.service.counts.finalize,
          deleteRequests: current.service.counts.deletes,
          possibleRemainingObjects: current.service.objects.size,
        });
        console.log('CR2_H1_CONTROL ' + JSON.stringify(controls.at(-1)));
      } finally {
        await current.service.close();
      }
    }
    const result = {
      kind: 'credential-free-process-rehearsal',
      actualGitHubRuns: 0,
      realArtifactOperations: 0,
      source: first.config.source,
      workingTree: first.config.workingTree,
      syntheticMap: true,
      companionEntry: true,
      publicDevelopmentProfile: true,
      phaseA: a.counts,
      phaseB: b.counts,
      controlledSdk: observations.counts,
      externalServiceDestinations: allPhases.every(
        (row) => row.network.externalServiceDestinations === 0,
      )
        ? 0
        : null,
      productSessionRestorationClaimed: false,
      controls,
    };
    if (process.env.PROOF_EVIDENCE)
      await writeFile(process.env.PROOF_EVIDENCE, JSON.stringify(result, null, 2) + '\n');
    console.log('CR2_H1_REHEARSAL ' + JSON.stringify(result));
    return result;
  } finally {
    if (!firstStopped) await stop(first.configPath);
  }
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url))
  await rehearse().catch(() => {
    console.error('CR2_H1_REHEARSAL_REFUSED');
    process.exitCode = 1;
  });
