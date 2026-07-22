import { describe, expect, it } from 'vitest';
import {
  GitDataStateTransport,
  GitStateTransportError,
  type GitDataClient,
} from './github-git-data.js';

function client(overrides: Partial<GitDataClient> = {}): GitDataClient {
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
    ...overrides,
  };
}

describe('GitDataStateTransport', () => {
  it('uses a non-forced ref update for a commit rooted at the observed tree', async () => {
    let request: Parameters<GitDataClient['updateRef']>[0] | undefined;
    const transport = new GitDataStateTransport(
      client({ updateRef: async (input) => ((request = input), 'updated') }),
      'owner',
      'repo',
    );
    const state = await transport.read();
    expect(state).not.toBeNull();
    await expect(
      transport.commit(state!, new Map([['m4-state/v1/store.json', new Uint8Array([1])]]), 'm4'),
    ).resolves.toBe('applied');
    expect(request).toMatchObject({ ref: 'heads/agentic-pr-review-m4-state-v1', force: false });
  });

  it('fails closed on a truncated tree', async () => {
    const transport = new GitDataStateTransport(
      client({ getTree: async () => ({ truncated: true, entries: [] }) }),
      'owner',
      'repo',
    );
    await expect(transport.read()).rejects.toEqual(
      expect.objectContaining<Partial<GitStateTransportError>>({ code: 'tree_truncated' }),
    );
  });

  it('fails closed on duplicate tree paths', async () => {
    const transport = new GitDataStateTransport(
      client({
        getTree: async () => ({
          truncated: false,
          entries: [
            { path: 'm4-state/v1/store.json', mode: '100644', type: 'blob', sha: 'a'.repeat(40) },
            { path: 'm4-state/v1/store.json', mode: '100644', type: 'blob', sha: 'b'.repeat(40) },
          ],
        }),
      }),
      'owner',
      'repo',
    );
    await expect(transport.read()).rejects.toEqual(
      expect.objectContaining<Partial<GitStateTransportError>>({ code: 'duplicate_tree_entry' }),
    );
  });
});
