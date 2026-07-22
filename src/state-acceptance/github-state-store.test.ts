import { describe, expect, it } from 'vitest';
import { canonicalJsonBytes } from '../canonical-json/index.js';
import { makeCacheContract, makeStateKey } from '../state-v2/test-helpers.js';
import { computeCandidateId, decodeRecord, type CandidateRegistrationDraft } from './index.js';
import type { GitDataClient } from './github-git-data.js';
import { GitHubGitStateAcceptanceStore } from './github-state-store.js';
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
    ).rejects.toMatchObject({
      reason: 'store_transaction_failed',
    });
  });
});
