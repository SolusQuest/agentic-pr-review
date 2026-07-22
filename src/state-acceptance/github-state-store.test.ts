import { describe, expect, it } from 'vitest';
import { mkdtemp, rm } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { build } from 'esbuild';
import { canonicalJsonBytes } from '../canonical-json/index.js';
import { buildStateBundleV2 } from '../state-v2/index.js';
import {
  makeCacheContract,
  makeStateKey,
  makeStateManifestV2Input,
  sha256Hex,
} from '../state-v2/test-helpers.js';
import {
  acceptLocalCandidate,
  computeCandidateId,
  decodeRecord,
  type CandidateRegistrationDraft,
} from './index.js';
import type { GitDataClient } from './github-git-data.js';
import {
  GitHubGitStateAcceptanceStore,
  manifestProvenanceMatches,
  StoreCorruptionError,
} from './github-state-store.js';
import { gitStatePaths } from './github-state-paths.js';

function client(): GitDataClient {
  return {
    getRef: async () => ({ sha: 'c'.repeat(40) }),
    getCommit: async () => ({ treeSha: 't'.repeat(40) }),
    getTree: async () => ({ truncated: false, entries: [] }),
    getBlob: async () => ({ contentBase64: '' }),
    createBlob: async () => ({ sha: 'b'.repeat(40) }),
    createTree: async () => ({ sha: 't'.repeat(40) }),
    createCommit: async () => ({ sha: 'n'.repeat(40) }),
    updateRef: async () => 'updated',
    createRef: async () => 'created',
  };
}

class MemoryGitDataClient implements GitDataClient {
  private readonly blobs = new Map<string, string>();
  private readonly trees = new Map<
    string,
    Map<string, { mode: string; type: 'blob'; sha: string }>
  >();
  private readonly commits = new Map<string, { treeSha: string; parentSha: string | null }>();
  private ref = 'c'.repeat(40);
  private serial = 0;

  constructor() {
    this.trees.set('t'.repeat(40), new Map());
    this.commits.set(this.ref, { treeSha: 't'.repeat(40), parentSha: null });
  }

  snapshot(): ReadonlyMap<
    string,
    { readonly mode: string; readonly type: 'blob'; readonly sha: string }
  > {
    return this.trees.get(this.commits.get(this.ref)!.treeSha)!;
  }

  addEntry(
    path: string,
    entry: { readonly mode: string; readonly type: 'blob' | 'tree'; readonly sha: string },
  ) {
    (this.snapshot() as Map<string, { mode: string; type: 'blob'; sha: string }>).set(
      path,
      entry as { mode: string; type: 'blob'; sha: string },
    );
  }

  async getRef() {
    return { sha: this.ref };
  }
  async getCommit(input: { readonly commitSha: string }) {
    return { treeSha: this.commits.get(input.commitSha)!.treeSha };
  }
  async getTree(input: { readonly treeSha: string; readonly recursive: true }) {
    return {
      truncated: false,
      entries: [...this.trees.get(input.treeSha)!.entries()].map(([path, entry]) => ({
        path,
        ...entry,
      })),
    };
  }
  async getBlob(input: { readonly blobSha: string }) {
    return { contentBase64: this.blobs.get(input.blobSha)! };
  }
  async createBlob(input: { readonly contentBase64: string }) {
    const sha = this.next('b');
    this.blobs.set(sha, input.contentBase64);
    return { sha };
  }
  async createTree(input: {
    readonly baseTreeSha: string;
    readonly entries: readonly {
      readonly path: string;
      readonly mode: '100644';
      readonly blobSha: string;
    }[];
  }) {
    const tree = new Map(this.trees.get(input.baseTreeSha)!);
    for (const entry of input.entries)
      tree.set(entry.path, { mode: entry.mode, type: 'blob', sha: entry.blobSha });
    const sha = this.next('t');
    this.trees.set(sha, tree);
    return { sha };
  }
  async createCommit(input: {
    readonly treeSha: string;
    readonly parentSha: string;
    readonly message: string;
  }) {
    const sha = this.next('c');
    this.commits.set(sha, { treeSha: input.treeSha, parentSha: input.parentSha });
    return { sha };
  }
  async updateRef(input: { readonly sha: string; readonly force: false }) {
    return this.commits.get(input.sha)?.parentSha === this.ref
      ? ((this.ref = input.sha), 'updated' as const)
      : ('rejected' as const);
  }
  async createRef() {
    return 'already_exists' as const;
  }

  private next(prefix: string): string {
    this.serial += 1;
    return `${prefix}${String(this.serial).padStart(39, '0')}`;
  }
}

async function buildStoreChildBundle(root: string): Promise<string> {
  const outfile = path.join(root, '.m4-github-state-child.mjs');
  await build({
    entryPoints: [fileURLToPath(new URL('./index.ts', import.meta.url))],
    bundle: true,
    format: 'esm',
    outfile,
    platform: 'node',
    logLevel: 'silent',
  });
  return outfile;
}

function runStoreChild(bundlePath: string, command: string, payload: Record<string, unknown>) {
  const childScript = fileURLToPath(new URL('./store-child.mjs', import.meta.url));
  return new Promise<{ readonly code: number | null; readonly result: Record<string, unknown> }>(
    (resolve, reject) => {
      const child = spawn(
        process.execPath,
        [childScript, bundlePath, command, JSON.stringify(payload)],
        {
          stdio: ['ignore', 'pipe', 'pipe'],
        },
      );
      let stdout = '';
      let stderr = '';
      child.stdout.on('data', (chunk: Buffer) => {
        stdout += chunk.toString();
      });
      child.stderr.on('data', (chunk: Buffer) => {
        stderr += chunk.toString();
      });
      child.once('error', reject);
      child.once('exit', (code) => {
        if (code !== 0) return reject(new Error(stderr || `child exited ${code}`));
        resolve({ code, result: JSON.parse(stdout) as Record<string, unknown> });
      });
    },
  );
}

async function withGitDataServer<T>(run: (url: string) => Promise<T>): Promise<T> {
  const transport = new MemoryGitDataClient();
  const server = createServer(async (request, response) => {
    try {
      let body = '';
      for await (const chunk of request) body += chunk.toString();
      const { method, input } = JSON.parse(body) as { method: keyof GitDataClient; input: unknown };
      const result = await (transport as any)[method](input);
      response.writeHead(200, { 'content-type': 'application/json' });
      response.end(JSON.stringify(result));
    } catch {
      response.writeHead(500);
      response.end();
    }
  });
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error('fake Git server did not bind');
  try {
    return await run(`http://127.0.0.1:${address.port}`);
  } finally {
    await new Promise<void>((resolve, reject) =>
      server.close((error) => (error ? reject(error) : resolve())),
    );
  }
}

class RejectOnceMemoryGitDataClient extends MemoryGitDataClient {
  updateAttempts = 0;
  rejectNextUpdate = false;

  override async updateRef(input: { readonly sha: string; readonly force: false }) {
    this.updateAttempts += 1;
    if (this.rejectNextUpdate) {
      this.rejectNextUpdate = false;
      return 'rejected' as const;
    }
    return super.updateRef(input);
  }
}

const stateKey = makeStateKey();
const sha = 'a'.repeat(64) as never;
const epoch = 'A'.repeat(22) as never;

function draft(overrides: Partial<CandidateRegistrationDraft> = {}): CandidateRegistrationDraft {
  return {
    schemaVersion: 1,
    candidateId: computeCandidateId({
      manifestSha256: sha,
      candidateLedgerSha256: sha,
      providerRunMetadataSha256: sha,
      metadataSemanticSha256: sha,
      consumedInputSha256: sha,
      resultSha256: sha,
      traceSha256: sha,
    }),
    observedSelectorRevision: 'bootstrap',
    observedSelectorSnapshotSha256: sha,
    predecessorMarkerId: 'bootstrap',
    predecessorManifestSha256: 'bootstrap',
    predecessorLedgerSha256: 'bootstrap',
    stateKey,
    sessionEpoch: epoch,
    stateGeneration: 0,
    ledgerEpoch: epoch,
    transition: {
      kind: 'bootstrap',
      predecessorManifestSha256: 'bootstrap',
      predecessorLedgerSha256: 'bootstrap',
      reason: 'new_session',
    },
    interactionId: sha,
    interactionOrdinal: 0,
    producingRunId: '10',
    producingRunAttempt: 1,
    consumedInputSha256: sha,
    manifestSha256: sha,
    candidateLedgerSha256: sha,
    providerRunMetadataSha256: sha,
    metadataSemanticSha256: sha,
    resultSha256: sha,
    traceSha256: sha,
    ...overrides,
  };
}

function options(explicitRestore = false) {
  const stateKey = makeStateKey();
  const { ledgerSchemaVersion, prefixContractVersion, ...cacheContractIdentity } =
    makeCacheContract();
  return {
    stateKey,
    expectedLedgerSchemaVersion: ledgerSchemaVersion,
    expectedPrefixContractVersion: prefixContractVersion,
    cacheContractIdentity,
    currentHeadSha: 'a'.repeat(40) as any,
    currentBaseSha: 'b'.repeat(40) as any,
    currentBaseRef: 'refs/heads/main',
    provenanceTrusted: true,
    workflowIdentity: stateKey.workflowIdentity,
    trustedExecutionDomain: stateKey.trustedExecutionDomain,
    explicitRestore,
  } as const;
}

describe('GitHubGitStateAcceptanceStore selection', () => {
  it('selects bootstrap when no selector exists', async () => {
    await expect(
      new GitHubGitStateAcceptanceStore(client(), 'owner', 'repo').selectAcceptedState(options()),
    ).resolves.toMatchObject({
      selection: 'selected',
      snapshot: { kind: 'bootstrap_selected', observedSelectorRevision: 'bootstrap' },
    });
  });
  it('fails explicit restore when no selector exists', async () => {
    await expect(
      new GitHubGitStateAcceptanceStore(client(), 'owner', 'repo').selectAcceptedState(
        options(true),
      ),
    ).resolves.toMatchObject({
      selection: 'selected',
      snapshot: { kind: 'explicit_restore_invalid', failure: 'explicit_state_invalid' },
    });
  });
});

describe('GitHubGitStateAcceptanceStore registrations', () => {
  it('accepts ordinary default-branch content and legal M4 ancestor tree entries', async () => {
    const transport = new MemoryGitDataClient();
    transport.addEntry('README.md', { mode: '100644', type: 'blob', sha: 'source' });
    transport.addEntry('m4-state', { mode: '040000', type: 'tree', sha: 'tree-1' });
    transport.addEntry('m4-state/v1', { mode: '040000', type: 'tree', sha: 'tree-2' });
    await expect(
      new GitHubGitStateAcceptanceStore(transport, 'owner', 'repo').registerCandidate(draft()),
    ).resolves.toMatchObject({ kind: 'created' });
  });

  it('commits the canonical counter and immutable registration in one ref transaction', async () => {
    const transport = new MemoryGitDataClient();
    const store = new GitHubGitStateAcceptanceStore(transport, 'owner', 'repo');
    const result = await store.registerCandidate(draft());
    expect(result).toMatchObject({ kind: 'created', registration: { registrationSequence: '1' } });

    const entries = transport.snapshot();
    const counter = entries.get(gitStatePaths.counter(stateKey));
    expect(counter).toBeDefined();
    const counterBytes = Buffer.from(
      (await transport.getBlob({ blobSha: counter!.sha })).contentBase64,
      'base64',
    );
    expect(decodeRecord(counterBytes)).toEqual({
      schemaVersion: 1,
      kind: 'm4-registration-counter',
      stateKeyDigest: expect.any(String),
      lastAllocatedSequence: '1',
      lastRegistrationId: expect.any(String),
      lastCompetingScopeDigest: expect.any(String),
    });
    expect([...entries.keys()].filter((path) => path.includes('/registrations/'))).toHaveLength(1);
  });

  it('serializes concurrent registrations into a contiguous per-state-key sequence', async () => {
    const transport = new MemoryGitDataClient();
    const left = new GitHubGitStateAcceptanceStore(transport, 'owner', 'repo');
    const right = new GitHubGitStateAcceptanceStore(transport, 'owner', 'repo');
    const [first, second] = await Promise.all([
      left.registerCandidate(draft()),
      right.registerCandidate(draft({ interactionId: 'b'.repeat(64) as never })),
    ]);
    expect([first, second].map((result) => result.kind)).toEqual(['created', 'created']);
    expect(
      [first, second]
        .flatMap((result) =>
          result.kind === 'created' && result.registration
            ? [result.registration.registrationSequence]
            : [],
        )
        .sort(),
    ).toEqual(['1', '2']);

    const counter = transport.snapshot().get(gitStatePaths.counter(stateKey))!;
    const counterBytes = Buffer.from(
      (await transport.getBlob({ blobSha: counter.sha })).contentBase64,
      'base64',
    );
    expect(decodeRecord(counterBytes)).toMatchObject({ lastAllocatedSequence: '2' });
  });

  it('uses the global state-key counter as the acceptance cutoff across competing scopes', async () => {
    const transport = new MemoryGitDataClient();
    const store = new GitHubGitStateAcceptanceStore(transport, 'owner', 'repo');
    const first = draft();
    await store.registerCandidate(first);
    await store.registerCandidate(draft({ interactionId: 'b'.repeat(64) as never }));
    const snapshot = await store.createAcceptanceSnapshot(
      'bootstrap',
      {
        stateKey,
        sessionEpoch: first.sessionEpoch,
        observedSelectorRevision: first.observedSelectorRevision,
        predecessorMarkerId: first.predecessorMarkerId,
        predecessorManifestSha256: first.predecessorManifestSha256,
        predecessorLedgerSha256: first.predecessorLedgerSha256,
        ledgerEpoch: first.ledgerEpoch,
        targetStateGeneration: first.stateGeneration,
        interactionId: first.interactionId,
      },
      'c'.repeat(64),
    );
    expect(snapshot.cutoff).toBe('2');
    expect(snapshot.registrations).toHaveLength(1);
  });

  it('fails closed when the counter does not account for every immutable registration', async () => {
    const transport = new MemoryGitDataClient();
    const store = new GitHubGitStateAcceptanceStore(transport, 'owner', 'repo');
    await store.registerCandidate(draft());
    const counterPath = gitStatePaths.counter(stateKey);
    const counter = transport.snapshot().get(counterPath)!;
    const replacement = await transport.createBlob({
      contentBase64: Buffer.from(
        canonicalJsonBytes({
          schemaVersion: 1,
          kind: 'm4-registration-counter',
          stateKeyDigest: (
            decodeRecord(
              Buffer.from(
                (await transport.getBlob({ blobSha: counter.sha })).contentBase64,
                'base64',
              ),
            ) as { stateKeyDigest: string }
          ).stateKeyDigest,
          lastAllocatedSequence: '2',
          lastRegistrationId: 'a'.repeat(64),
          lastCompetingScopeDigest: 'b'.repeat(64),
        }),
      ).toString('base64'),
    });
    const state = transport.snapshot() as Map<string, { mode: string; type: 'blob'; sha: string }>;
    state.set(counterPath, { mode: '100644', type: 'blob', sha: replacement.sha });
    await expect(
      store.registerCandidate(draft({ interactionId: 'b'.repeat(64) as never })),
    ).rejects.toBeInstanceOf(StoreCorruptionError);
  });
});

describe('GitHubGitStateAcceptanceStore durable receipts', () => {
  it('retries a moved global state ref before persisting an otherwise unchanged receipt', async () => {
    const transport = new RejectOnceMemoryGitDataClient();
    const store = new GitHubGitStateAcceptanceStore(transport, 'owner', 'repo');
    await store.ensureInitialized({
      defaultBranchCommitSha: 'c'.repeat(40),
      stateKey,
      runId: '10',
      runAttempt: 1,
    });
    transport.rejectNextUpdate = true;
    await expect(
      store.writePublicationReceipt({
        markerId: 'a'.repeat(64) as never,
        stateKey,
        selectorRevision: `sha256:${'b'.repeat(64)}` as never,
        acceptingRunId: '10',
        acceptingRunAttempt: 1,
        publicationStatus: 'not_attempted',
        recordedAt: '2026-07-23T00:00:00.000Z',
      }),
    ).resolves.toBe('created');
    expect(transport.updateAttempts).toBeGreaterThanOrEqual(2);
  });
});

describe('GitHubGitStateAcceptanceStore control plane', () => {
  it('classifies a noncanonical state sentinel as typed store corruption', async () => {
    const transport = new MemoryGitDataClient();
    const store = new GitHubGitStateAcceptanceStore(transport, 'owner', 'repo');
    await store.ensureInitialized({
      defaultBranchCommitSha: 'c'.repeat(40),
      stateKey,
      runId: '10',
      runAttempt: 1,
    });
    const replacement = await transport.createBlob({
      contentBase64: Buffer.from('{"invalid":true}').toString('base64'),
    });
    const entries = transport.snapshot() as Map<
      string,
      { mode: string; type: 'blob'; sha: string }
    >;
    entries.set(gitStatePaths.sentinel, { mode: '100644', type: 'blob', sha: replacement.sha });

    await expect(
      store.ensureInitialized({
        defaultBranchCommitSha: 'c'.repeat(40),
        stateKey,
        runId: '11',
        runAttempt: 1,
      }),
    ).rejects.toBeInstanceOf(StoreCorruptionError);
  });
});

describe('GitHubGitStateAcceptanceStore acceptance integration', () => {
  it('uses two independent processes against a shared Git-data service for bootstrap then continuation', async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'm4-github-store-child-'));
    try {
      const bundlePath = await buildStoreChildBundle(root);
      await withGitDataServer(async (githubUrl) => {
        const bootstrap = await runStoreChild(bundlePath, 'accept-fixture', {
          githubUrl,
          runId: '1',
        });
        expect(bootstrap.result).toMatchObject({ acceptance: 'accepted' });

        const continuation = await runStoreChild(bundlePath, 'restore-and-accept-fixture', {
          githubUrl,
          runId: '2',
        });
        expect(continuation.result).toMatchObject({
          selection: 'selected',
          snapshotKind: 'continuation_selected',
          predecessorBytesMatch: true,
          acceptance: { acceptance: 'accepted' },
        });
      });
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });

  it('accepts at most one competing selector successor and suppresses loser sticky publication', async () => {
    const transport = new MemoryGitDataClient();
    const left = new GitHubGitStateAcceptanceStore(transport, 'owner', 'repo');
    const right = new GitHubGitStateAcceptanceStore(transport, 'owner', 'repo');
    const candidate = (suffix: string) => {
      const resultBytes = new TextEncoder().encode(`{"result":"${suffix}"}`);
      const traceBytes = new TextEncoder().encode(`{"trace":"${suffix}"}`);
      const inputBytes = new TextEncoder().encode(`{"input":"${suffix}"}`);
      const bundle = buildStateBundleV2(
        makeStateManifestV2Input({
          transaction: {
            interactionId: sha256Hex(`race-interaction-${suffix}`),
            consumedInputSha256: sha256Hex(inputBytes),
            resultSha256: sha256Hex(resultBytes),
            traceSha256: sha256Hex(traceBytes),
          },
          provenance: { producingRunId: suffix === 'left' ? '10' : '11' },
        }),
        new TextEncoder().encode(`ledger-${suffix}`),
        new TextEncoder().encode(`metadata-${suffix}`),
      );
      return {
        bundle,
        lease: {
          ...bundle,
          resultBytes,
          traceBytes,
          inputSha256: sha256Hex(inputBytes),
          resultSha256: sha256Hex(resultBytes),
          traceSha256: sha256Hex(traceBytes),
          candidateLedgerSha256: sha256Hex(bundle.ledgerBytes),
          metadataSemanticSha256: bundle.manifest.transaction.metadataSemanticSha256,
          release: async () => undefined,
        },
      };
    };
    const leftCandidate = candidate('left');
    const rightCandidate = candidate('right');
    await left.ensureInitialized({
      defaultBranchCommitSha: 'c'.repeat(40),
      stateKey: leftCandidate.bundle.manifest.stateKey,
      runId: '1',
      runAttempt: 1,
    });
    const { ledgerSchemaVersion, prefixContractVersion, ...cacheContractIdentity } =
      leftCandidate.bundle.manifest.cacheContractIdentity;
    const select = (store: GitHubGitStateAcceptanceStore) =>
      store.selectAcceptedState({
        stateKey: leftCandidate.bundle.manifest.stateKey,
        expectedLedgerSchemaVersion: ledgerSchemaVersion,
        expectedPrefixContractVersion: prefixContractVersion,
        cacheContractIdentity,
        currentHeadSha: leftCandidate.bundle.manifest.provenance.currentHeadSha,
        currentBaseSha: leftCandidate.bundle.manifest.provenance.currentBaseSha,
        currentBaseRef: leftCandidate.bundle.manifest.provenance.currentBaseRef,
        provenanceTrusted: true,
        workflowIdentity: leftCandidate.bundle.manifest.stateKey.workflowIdentity,
        trustedExecutionDomain: leftCandidate.bundle.manifest.stateKey.trustedExecutionDomain,
      });
    const [leftSelection, rightSelection] = await Promise.all([select(left), select(right)]);
    expect(leftSelection).toMatchObject({
      selection: 'selected',
      snapshot: { kind: 'bootstrap_selected' },
    });
    expect(rightSelection).toMatchObject({
      selection: 'selected',
      snapshot: { kind: 'bootstrap_selected' },
    });
    if (
      leftSelection.selection !== 'selected' ||
      rightSelection.selection !== 'selected' ||
      leftSelection.snapshot.kind !== 'bootstrap_selected' ||
      rightSelection.snapshot.kind !== 'bootstrap_selected'
    )
      return;
    let leftPublished = 0;
    let rightPublished = 0;
    const accept = (
      store: GitHubGitStateAcceptanceStore,
      selection: typeof leftSelection.snapshot,
      current: typeof leftCandidate,
      acceptingRunId: string,
      publish: () => void,
    ) =>
      acceptLocalCandidate(store, {
        selectionSnapshot: selection,
        candidate: current.lease,
        interactionId: current.bundle.manifest.transaction.interactionId,
        interactionOrdinal: current.bundle.manifest.transaction.interactionOrdinal,
        producingRunId: current.bundle.manifest.provenance.producingRunId,
        producingRunAttempt: current.bundle.manifest.provenance.producingRunAttempt,
        acceptingRunId,
        acceptingRunAttempt: 1,
        consumedInputSha256: current.lease.inputSha256,
        transition: current.bundle.manifest.transition,
        publishSticky: async () => publish(),
      });
    const [leftResult, rightResult] = await Promise.all([
      accept(left, leftSelection.snapshot, leftCandidate, '1', () => {
        leftPublished += 1;
      }),
      accept(right, rightSelection.snapshot, rightCandidate, '2', () => {
        rightPublished += 1;
      }),
    ]);
    expect(
      [leftResult.acceptance, rightResult.acceptance].filter((value) => value === 'accepted'),
    ).toHaveLength(1);
    expect([leftResult, rightResult]).toContainEqual(
      expect.objectContaining({ acceptance: 'not_accepted', reason: 'stale_candidate' }),
    );
    expect(leftPublished + rightPublished).toBe(1);
  });

  it('persists a bootstrap acceptance and restores its exact predecessor bytes for continuation', async () => {
    const transport = new MemoryGitDataClient();
    const store = new GitHubGitStateAcceptanceStore(transport, 'owner', 'repo');
    const resultBytes = new TextEncoder().encode('{"summary":"bootstrap"}');
    const traceBytes = new TextEncoder().encode('{"trace":true}');
    const inputBytes = new TextEncoder().encode('{"input":true}');
    const input = makeStateManifestV2Input({
      transaction: {
        consumedInputSha256: sha256Hex(inputBytes),
        resultSha256: sha256Hex(resultBytes),
        traceSha256: sha256Hex(traceBytes),
      },
    });
    const bundle = buildStateBundleV2(
      input,
      new TextEncoder().encode('ledger-bytes'),
      new TextEncoder().encode('metadata-bytes'),
    );
    await store.ensureInitialized({
      defaultBranchCommitSha: 'c'.repeat(40),
      stateKey: bundle.manifest.stateKey,
      runId: '1',
      runAttempt: 1,
    });
    const { ledgerSchemaVersion, prefixContractVersion, ...cacheContractIdentity } =
      bundle.manifest.cacheContractIdentity;
    const selection = await store.selectAcceptedState({
      stateKey: bundle.manifest.stateKey,
      expectedLedgerSchemaVersion: ledgerSchemaVersion,
      expectedPrefixContractVersion: prefixContractVersion,
      cacheContractIdentity,
      currentHeadSha: bundle.manifest.provenance.currentHeadSha,
      currentBaseSha: bundle.manifest.provenance.currentBaseSha,
      currentBaseRef: bundle.manifest.provenance.currentBaseRef,
      provenanceTrusted: true,
      workflowIdentity: bundle.manifest.stateKey.workflowIdentity,
      trustedExecutionDomain: bundle.manifest.stateKey.trustedExecutionDomain,
    });
    expect(selection).toMatchObject({
      selection: 'selected',
      snapshot: { kind: 'bootstrap_selected' },
    });
    if (selection.selection !== 'selected' || selection.snapshot.kind !== 'bootstrap_selected')
      return;
    const acceptance = await acceptLocalCandidate(store, {
      selectionSnapshot: selection.snapshot,
      candidate: {
        ...bundle,
        resultBytes,
        traceBytes,
        inputSha256: sha256Hex(inputBytes),
        resultSha256: sha256Hex(resultBytes),
        traceSha256: sha256Hex(traceBytes),
        candidateLedgerSha256: sha256Hex(bundle.ledgerBytes),
        metadataSemanticSha256: bundle.manifest.transaction.metadataSemanticSha256,
        release: async () => undefined,
      },
      interactionId: bundle.manifest.transaction.interactionId,
      interactionOrdinal: bundle.manifest.transaction.interactionOrdinal,
      producingRunId: bundle.manifest.provenance.producingRunId,
      producingRunAttempt: bundle.manifest.provenance.producingRunAttempt,
      acceptingRunId: '1',
      acceptingRunAttempt: 1,
      consumedInputSha256: sha256Hex(inputBytes),
      transition: bundle.manifest.transition,
    });
    expect(acceptance.acceptance).toBe('accepted');
    const acceptedSelector = await store.readSelector(bundle.manifest.stateKey);
    expect(acceptedSelector.selector).not.toBeNull();
    if (acceptedSelector.selector === null) return;
    await expect(store.casSelector('bootstrap', acceptedSelector.selector)).resolves.toMatchObject({
      kind: 'already_applied_same_target',
    });
    const retry = await acceptLocalCandidate(store, {
      selectionSnapshot: selection.snapshot,
      candidate: {
        ...bundle,
        resultBytes,
        traceBytes,
        inputSha256: sha256Hex(inputBytes),
        resultSha256: sha256Hex(resultBytes),
        traceSha256: sha256Hex(traceBytes),
        candidateLedgerSha256: sha256Hex(bundle.ledgerBytes),
        metadataSemanticSha256: bundle.manifest.transaction.metadataSemanticSha256,
        release: async () => undefined,
      },
      interactionId: bundle.manifest.transaction.interactionId,
      interactionOrdinal: bundle.manifest.transaction.interactionOrdinal,
      producingRunId: bundle.manifest.provenance.producingRunId,
      producingRunAttempt: bundle.manifest.provenance.producingRunAttempt,
      acceptingRunId: '1',
      acceptingRunAttempt: 1,
      consumedInputSha256: sha256Hex(inputBytes),
      transition: bundle.manifest.transition,
    });
    expect(retry.acceptance).toBe('already_accepted');

    const reopened = new GitHubGitStateAcceptanceStore(transport, 'owner', 'repo');
    const continuation = await reopened.selectAcceptedState({
      stateKey: bundle.manifest.stateKey,
      expectedLedgerSchemaVersion: ledgerSchemaVersion,
      expectedPrefixContractVersion: prefixContractVersion,
      cacheContractIdentity,
      currentHeadSha: bundle.manifest.provenance.currentHeadSha,
      currentBaseSha: bundle.manifest.provenance.currentBaseSha,
      currentBaseRef: bundle.manifest.provenance.currentBaseRef,
      provenanceTrusted: true,
      workflowIdentity: bundle.manifest.stateKey.workflowIdentity,
      trustedExecutionDomain: bundle.manifest.stateKey.trustedExecutionDomain,
      headRelationship: 'same',
    });
    expect(continuation).toMatchObject({
      selection: 'selected',
      snapshot: { kind: 'continuation_selected' },
    });
    if (
      continuation.selection !== 'selected' ||
      continuation.snapshot.kind !== 'continuation_selected'
    )
      return;
    expect(continuation.snapshot.predecessorBytes.manifestBytes).toEqual(bundle.manifestBytes);
    expect(continuation.snapshot.predecessorBytes.ledgerBytes).toEqual(bundle.ledgerBytes);
    expect(continuation.snapshot.predecessorBytes.providerRunMetadataBytes).toEqual(
      bundle.providerRunMetadataBytes,
    );

    const successorResultBytes = new TextEncoder().encode('{"summary":"continuation"}');
    const successorTraceBytes = new TextEncoder().encode('{"trace":"continuation"}');
    const successorInputBytes = new TextEncoder().encode('{"input":"continuation"}');
    const successor = buildStateBundleV2(
      makeStateManifestV2Input({
        stateKey: bundle.manifest.stateKey,
        cacheContractIdentity: bundle.manifest.cacheContractIdentity,
        sessionEpoch: bundle.manifest.sessionEpoch,
        generation: {
          stateGeneration: bundle.manifest.generation.stateGeneration + 1,
          ledgerEpoch: bundle.manifest.generation.ledgerEpoch,
        },
        transition: {
          kind: 'continuation',
          predecessorManifestSha256: sha256Hex(
            continuation.snapshot.predecessorBytes.manifestBytes,
          ),
          predecessorLedgerSha256: sha256Hex(continuation.snapshot.predecessorBytes.ledgerBytes),
          predecessorStateGeneration: bundle.manifest.generation.stateGeneration,
          predecessorLedgerEpoch: bundle.manifest.generation.ledgerEpoch,
        },
        provenance: { producingRunId: '2' },
        transaction: {
          interactionId: sha256Hex('continuation-interaction'),
          interactionOrdinal: 1,
          consumedInputSha256: sha256Hex(successorInputBytes),
          resultSha256: sha256Hex(successorResultBytes),
          traceSha256: sha256Hex(successorTraceBytes),
        },
      }),
      new TextEncoder().encode('continuation-ledger-bytes'),
      new TextEncoder().encode('continuation-metadata-bytes'),
    );
    const continuationAcceptance = await acceptLocalCandidate(reopened, {
      selectionSnapshot: continuation.snapshot,
      candidate: {
        ...successor,
        resultBytes: successorResultBytes,
        traceBytes: successorTraceBytes,
        inputSha256: sha256Hex(successorInputBytes),
        resultSha256: sha256Hex(successorResultBytes),
        traceSha256: sha256Hex(successorTraceBytes),
        candidateLedgerSha256: sha256Hex(successor.ledgerBytes),
        metadataSemanticSha256: successor.manifest.transaction.metadataSemanticSha256,
        release: async () => undefined,
      },
      interactionId: successor.manifest.transaction.interactionId,
      interactionOrdinal: successor.manifest.transaction.interactionOrdinal,
      producingRunId: successor.manifest.provenance.producingRunId,
      producingRunAttempt: successor.manifest.provenance.producingRunAttempt,
      acceptingRunId: '2',
      acceptingRunAttempt: 1,
      consumedInputSha256: sha256Hex(successorInputBytes),
      transition: successor.manifest.transition,
    });
    expect(continuationAcceptance.acceptance).toBe('accepted');
    const persistedSelector = await reopened.readSelector(bundle.manifest.stateKey);
    expect(persistedSelector.selector).toMatchObject({
      stateGeneration: successor.manifest.generation.stateGeneration,
      ledgerEpoch: successor.manifest.generation.ledgerEpoch,
      transition: { kind: 'continuation' },
    });
  });
});

describe('GitHubGitStateAcceptanceStore provenance binding', () => {
  it('requires every supplied immutable workflow provenance field to match', () => {
    const manifest = {
      provenance: {
        workflowEvent: 'workflow_run',
        producingWorkflowRef: 'owner/repo/.github/workflows/m4.yml@refs/heads/main',
        producingGitRef: 'refs/heads/main',
        producingActionSourceSha: 'c'.repeat(40),
      },
    } as never;
    expect(
      manifestProvenanceMatches(manifest, {
        expectedWorkflowEvent: 'workflow_run',
        expectedProducingWorkflowRef: 'owner/repo/.github/workflows/m4.yml@refs/heads/main',
        expectedProducingGitRef: 'refs/heads/main',
        expectedProducingActionSourceSha: 'c'.repeat(40) as never,
      } as never),
    ).toBe(true);
    expect(
      manifestProvenanceMatches(manifest, {
        expectedProducingActionSourceSha: 'd'.repeat(40) as never,
      } as never),
    ).toBe(false);
    expect(
      manifestProvenanceMatches(manifest, {
        expectedWorkflowEvent: 'workflow_run',
        expectedProducingWorkflowRef: 'owner/repo/.github/workflows/m4.yml@refs/heads/main',
        expectedProducingGitRef: 'refs/heads/main',
      } as never),
    ).toBe(true);
  });
});
