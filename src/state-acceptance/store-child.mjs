import { readFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';

const [, , bundlePath, command, payloadText] = process.argv;
const payload = JSON.parse(payloadText);
const {
  ReferenceStateStore,
  GitHubGitStateAcceptanceStore,
  acceptLocalCandidate,
  computeSelectionSnapshotId,
  observedSelectorSnapshotSha256,
  sha256Hex,
} = await import(pathToFileURL(bundlePath).href);

const hooks =
  command === 'register-kill-after-temp'
    ? {
        afterRegistrationTempWrite: () => {
          process.kill(process.pid, 'SIGKILL');
        },
      }
    : undefined;
const store = payload.githubUrl
  ? new GitHubGitStateAcceptanceStore(httpGitDataClient(payload.githubUrl), 'owner', 'repo')
  : new ReferenceStateStore(payload.root, hooks);
if (!payload.githubUrl) await store.close();

let result;
switch (command) {
  case 'init':
    result = { kind: 'initialized' };
    break;
  case 'register':
  case 'register-kill-after-temp':
    result = await store.registerCandidate(payload.draft);
    break;
  case 'snapshot': {
    const snapshot = await store.createAcceptanceSnapshot(
      payload.expectedObservedSelectorRevision,
      payload.competingScope,
      payload.selectionSnapshotId,
    );
    result = { cutoff: snapshot.cutoff, registrations: snapshot.registrations.length };
    break;
  }
  case 'marker':
    result = await store.writeMarker(payload.marker);
    break;
  case 'cas':
    result = await store.casSelector(payload.expectedRevision, payload.selector);
    break;
  case 'read-selector': {
    const read = await store.readSelector(payload.stateKey);
    result = { hasBytes: read.bytes !== null, selector: read.selector };
    break;
  }
  case 'accept-fixture': {
    const accepted = await acceptFixture(store);
    result = summarizeAcceptance(accepted);
    break;
  }
  case 'restore-and-accept-fixture': {
    const restored = await restoreAndAcceptFixture(store);
    result = restored;
    break;
  }
  default:
    throw new Error(`unknown child command: ${command}`);
}

process.stdout.write(`${JSON.stringify(result)}\n`);

async function acceptFixture(targetStore) {
  const { manifest, candidate } = await initialFixture();
  await initializeGitStore(targetStore, manifest);
  const selectionSnapshot = bootstrapSelection(manifest.stateKey, manifest.provenance);
  return acceptLocalCandidate(targetStore, acceptanceOptions(selectionSnapshot, candidate));
}

async function restoreAndAcceptFixture(targetStore) {
  const { manifest } = await initialFixture();
  await initializeGitStore(targetStore, manifest);
  const selectionOptions = selectionOptionsFor(manifest);
  const restored = await targetStore.selectAcceptedState(selectionOptions);
  if (restored.selection !== 'selected' || restored.snapshot.kind !== 'continuation_selected') {
    return { selection: restored.selection, snapshot: restored.snapshot ?? null };
  }
  const predecessor = restored.snapshot.predecessorBytes;
  const successor = await successorFixture(manifest, predecessor);
  const accepted = await acceptLocalCandidate(
    targetStore,
    acceptanceOptions(restored.snapshot, successor),
  );
  const initial = await initialFixture();
  return {
    selection: restored.selection,
    snapshotKind: restored.snapshot.kind,
    predecessorBytesMatch:
      bytesEqual(predecessor.manifestBytes, initial.candidate.manifestBytes) &&
      bytesEqual(predecessor.ledgerBytes, initial.candidate.ledgerBytes) &&
      bytesEqual(predecessor.providerRunMetadataBytes, initial.candidate.providerRunMetadataBytes),
    acceptance: summarizeAcceptance(accepted),
  };
}

async function initializeGitStore(targetStore, manifest) {
  if (!payload.githubUrl) return;
  await targetStore.ensureInitialized({
    defaultBranchCommitSha: 'c'.repeat(40),
    stateKey: manifest.stateKey,
    runId: String(payload.runId),
    runAttempt: 1,
  });
}

function httpGitDataClient(baseUrl) {
  const call = async (method, input) => {
    const response = await fetch(baseUrl, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ method, input }),
    });
    if (!response.ok) throw new Error(`fake git data ${method} failed`);
    return response.json();
  };
  return {
    getRef: (input) => call('getRef', input),
    getCommit: (input) => call('getCommit', input),
    getTree: (input) => call('getTree', input),
    getBlob: (input) => call('getBlob', input),
    createBlob: (input) => call('createBlob', input),
    createTree: (input) => call('createTree', input),
    createCommit: (input) => call('createCommit', input),
    updateRef: (input) => call('updateRef', input),
    createRef: (input) => call('createRef', input),
  };
}

async function initialFixture() {
  const manifest = JSON.parse(
    await readFile(
      'protocol/fixtures/state-manifest-v2/positive-bootstrap/bundle/manifest.json',
      'utf8',
    ),
  );
  const ledgerBytes = new Uint8Array(
    await readFile('protocol/fixtures/state-manifest-v2/positive-bootstrap/bundle/ledger.json'),
  );
  const providerRunMetadataBytes = new Uint8Array(
    await readFile(
      'protocol/fixtures/state-manifest-v2/positive-bootstrap/bundle/provider-run-metadata.json',
    ),
  );
  const resultBytes = new TextEncoder().encode('{}');
  const traceBytes = new TextEncoder().encode('{}');
  const inputSha256 = sha256Hex(new Uint8Array([7]));
  manifest.ledger.sha256 = sha256Hex(ledgerBytes);
  manifest.ledger.bytes = ledgerBytes.byteLength;
  manifest.transaction.candidateLedgerSha256 = sha256Hex(ledgerBytes);
  manifest.providerRunMetadata.sha256 = sha256Hex(providerRunMetadataBytes);
  manifest.providerRunMetadata.bytes = providerRunMetadataBytes.byteLength;
  manifest.transaction.consumedInputSha256 = inputSha256;
  manifest.transaction.resultSha256 = sha256Hex(resultBytes);
  manifest.transaction.traceSha256 = sha256Hex(traceBytes);
  const manifestBytes = canonicalBytes(manifest);
  return {
    manifest,
    candidate: {
      manifest,
      manifestBytes,
      ledgerBytes,
      providerRunMetadataBytes,
      resultBytes,
      traceBytes,
      inputSha256,
      resultSha256: sha256Hex(resultBytes),
      traceSha256: sha256Hex(traceBytes),
      candidateLedgerSha256: sha256Hex(ledgerBytes),
      metadataSemanticSha256: manifest.transaction.metadataSemanticSha256,
      release: async () => undefined,
    },
  };
}

async function successorFixture(previousManifest, predecessor) {
  const manifest = structuredClone(previousManifest);
  const ledgerBytes = new TextEncoder().encode('positive-continuation-ledger');
  const providerRunMetadataBytes = new TextEncoder().encode('positive-continuation-metadata');
  const resultBytes = new TextEncoder().encode('{"successor":true}');
  const traceBytes = new TextEncoder().encode('{"successor":true}');
  const predecessorManifestSha256 = sha256Hex(predecessor.manifestBytes);
  const predecessorLedgerSha256 = sha256Hex(predecessor.ledgerBytes);
  const ledgerEpoch = previousManifest.generation.ledgerEpoch;
  const stateGeneration = previousManifest.generation.stateGeneration + 1;
  manifest.generation = { stateGeneration, ledgerEpoch };
  manifest.transition = {
    kind: 'continuation',
    predecessorManifestSha256,
    predecessorLedgerSha256,
    predecessorStateGeneration: previousManifest.generation.stateGeneration,
    predecessorLedgerEpoch: ledgerEpoch,
  };
  manifest.provenance.producingRunId = '123456790';
  manifest.transaction.interactionId = sha256Hex(new TextEncoder().encode('successor-interaction'));
  manifest.transaction.interactionOrdinal = 1;
  manifest.transaction.consumedInputSha256 = sha256Hex(new Uint8Array([8]));
  manifest.transaction.resultSha256 = sha256Hex(resultBytes);
  manifest.transaction.traceSha256 = sha256Hex(traceBytes);
  manifest.transaction.candidateLedgerSha256 = sha256Hex(ledgerBytes);
  manifest.ledger.sha256 = sha256Hex(ledgerBytes);
  manifest.ledger.bytes = ledgerBytes.byteLength;
  manifest.providerRunMetadata.sha256 = sha256Hex(providerRunMetadataBytes);
  manifest.providerRunMetadata.bytes = providerRunMetadataBytes.byteLength;
  manifest.providerRunMetadata.producingGeneration = {
    sessionEpoch: manifest.sessionEpoch,
    stateGeneration,
    ledgerEpoch,
  };
  return {
    manifest,
    manifestBytes: canonicalBytes(manifest),
    ledgerBytes,
    providerRunMetadataBytes,
    resultBytes,
    traceBytes,
    inputSha256: manifest.transaction.consumedInputSha256,
    resultSha256: manifest.transaction.resultSha256,
    traceSha256: manifest.transaction.traceSha256,
    candidateLedgerSha256: manifest.transaction.candidateLedgerSha256,
    metadataSemanticSha256: manifest.transaction.metadataSemanticSha256,
    release: async () => undefined,
  };
}

function bootstrapSelection(stateKey, provenance) {
  const selection = {
    schemaVersion: 1,
    kind: 'bootstrap_selected',
    stateKey,
    currentHeadSha: provenance.currentHeadSha,
    currentBaseSha: provenance.currentBaseSha,
    currentBaseRef: provenance.currentBaseRef,
    observedSelectorBytes: null,
    observedSelectorRevision: 'bootstrap',
    observedSelectorSnapshotSha256: observedSelectorSnapshotSha256(null),
    transitionPlan: 'bootstrap',
    selectionSnapshotId: '',
  };
  selection.selectionSnapshotId = computeSelectionSnapshotId(selection);
  return selection;
}

function selectionOptionsFor(manifest) {
  const { ledgerSchemaVersion, prefixContractVersion, ...cacheContractIdentity } =
    manifest.cacheContractIdentity;
  return {
    stateKey: manifest.stateKey,
    expectedLedgerSchemaVersion: ledgerSchemaVersion,
    expectedPrefixContractVersion: prefixContractVersion,
    cacheContractIdentity,
    currentHeadSha: manifest.provenance.currentHeadSha,
    currentBaseSha: manifest.provenance.currentBaseSha,
    currentBaseRef: manifest.provenance.currentBaseRef,
    provenanceTrusted: true,
    workflowIdentity: manifest.stateKey.workflowIdentity,
    trustedExecutionDomain: manifest.stateKey.trustedExecutionDomain,
    headRelationship: 'same',
  };
}

function acceptanceOptions(selectionSnapshot, candidate) {
  const manifest = candidate.manifest;
  return {
    selectionSnapshot,
    candidate,
    interactionId: manifest.transaction.interactionId,
    interactionOrdinal: manifest.transaction.interactionOrdinal,
    producingRunId: manifest.provenance.producingRunId,
    producingRunAttempt: manifest.provenance.producingRunAttempt,
    acceptingRunId: '99',
    acceptingRunAttempt: 1,
    consumedInputSha256: manifest.transaction.consumedInputSha256,
    transition: manifest.transition,
  };
}

function summarizeAcceptance(accepted) {
  return {
    acceptance: accepted.acceptance,
    reason: accepted.reason ?? null,
    selectorRevision: accepted.selectorRevision ?? null,
  };
}

function canonical(value) {
  if (Array.isArray(value)) return value.map(canonical);
  if (value !== null && typeof value === 'object') {
    return Object.fromEntries(
      Object.keys(value)
        .sort()
        .map((key) => [key, canonical(value[key])]),
    );
  }
  return value;
}

function canonicalBytes(value) {
  return new TextEncoder().encode(JSON.stringify(canonical(value)));
}

function bytesEqual(left, right) {
  if (left.byteLength !== right.byteLength) return false;
  return left.every((value, index) => value === right[index]);
}
