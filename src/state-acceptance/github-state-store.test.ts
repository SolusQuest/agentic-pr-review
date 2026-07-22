import { describe, expect, it } from 'vitest';
import { makeCacheContract, makeStateKey } from '../state-v2/test-helpers.js';
import type { GitDataClient } from './github-git-data.js';
import { GitHubGitStateAcceptanceStore } from './github-state-store.js';

function client(): GitDataClient {
  return {
    getRef: async () => ({ sha: 'c'.repeat(40) }), getCommit: async () => ({ treeSha: 't'.repeat(40) }),
    getTree: async () => ({ truncated: false, entries: [] }), getBlob: async () => ({ contentBase64: '' }),
    createBlob: async () => ({ sha: 'b'.repeat(40) }), createTree: async () => ({ sha: 't'.repeat(40) }),
    createCommit: async () => ({ sha: 'n'.repeat(40) }), updateRef: async () => 'updated', createRef: async () => 'created',
  };
}

function options(explicitRestore = false) {
  const stateKey = makeStateKey();
  const { ledgerSchemaVersion, prefixContractVersion, ...cacheContractIdentity } = makeCacheContract();
  return { stateKey, expectedLedgerSchemaVersion: ledgerSchemaVersion, expectedPrefixContractVersion: prefixContractVersion, cacheContractIdentity, currentHeadSha: 'a'.repeat(40) as any, currentBaseSha: 'b'.repeat(40) as any, currentBaseRef: 'refs/heads/main', provenanceTrusted: true, workflowIdentity: stateKey.workflowIdentity, trustedExecutionDomain: stateKey.trustedExecutionDomain, explicitRestore } as const;
}

describe('GitHubGitStateAcceptanceStore selection', () => {
  it('selects bootstrap when no selector exists', async () => {
    await expect(new GitHubGitStateAcceptanceStore(client(), 'owner', 'repo').selectAcceptedState(options())).resolves.toMatchObject({ selection: 'selected', snapshot: { kind: 'bootstrap_selected', observedSelectorRevision: 'bootstrap' } });
  });
  it('fails explicit restore when no selector exists', async () => {
    await expect(new GitHubGitStateAcceptanceStore(client(), 'owner', 'repo').selectAcceptedState(options(true))).resolves.toMatchObject({ selection: 'selected', snapshot: { kind: 'explicit_restore_invalid', failure: 'explicit_state_invalid' } });
  });
});
