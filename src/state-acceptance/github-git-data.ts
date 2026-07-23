/**
 * Minimal Git-data transport used by the production M4 state store.
 *
 * The interface deliberately models only immutable blob/tree/commit creation
 * plus a non-forced ref update. Higher layers own record validation and every
 * state-machine decision.
 */
export interface GitDataClient {
  getRef(input: {
    owner: string;
    repo: string;
    ref: string;
  }): Promise<{ readonly sha: string } | null>;
  getCommit(input: {
    owner: string;
    repo: string;
    commitSha: string;
  }): Promise<{ readonly treeSha: string }>;
  getTree(input: { owner: string; repo: string; treeSha: string; recursive: true }): Promise<{
    readonly truncated: boolean;
    readonly entries: readonly {
      readonly path: string;
      readonly mode: string;
      readonly type: 'blob' | 'tree' | 'commit';
      readonly sha: string;
    }[];
  }>;
  getBlob(input: {
    owner: string;
    repo: string;
    blobSha: string;
  }): Promise<{ readonly contentBase64: string }>;
  createBlob(input: {
    owner: string;
    repo: string;
    contentBase64: string;
  }): Promise<{ readonly sha: string }>;
  createTree(input: {
    owner: string;
    repo: string;
    baseTreeSha: string;
    entries: readonly {
      readonly path: string;
      readonly mode: '100644';
      readonly blobSha: string;
    }[];
  }): Promise<{ readonly sha: string }>;
  createCommit(input: {
    owner: string;
    repo: string;
    treeSha: string;
    parentSha: string;
    message: string;
  }): Promise<{ readonly sha: string }>;
  updateRef(input: {
    owner: string;
    repo: string;
    ref: string;
    sha: string;
    force: false;
  }): Promise<'updated' | 'rejected' | 'unknown'>;
  createRef(input: {
    owner: string;
    repo: string;
    ref: string;
    sha: string;
  }): Promise<'created' | 'already_exists' | 'unknown'>;
}

export interface GitStateRef {
  readonly commitSha: string;
  readonly treeSha: string;
  readonly entries: ReadonlyMap<
    string,
    { readonly mode: string; readonly type: string; readonly sha: string }
  >;
}

export class GitDataStateTransport {
  constructor(
    private readonly client: GitDataClient,
    private readonly owner: string,
    private readonly repo: string,
    readonly ref = 'heads/agentic-pr-review-m4-state-v1',
  ) {}

  async read(): Promise<GitStateRef | null> {
    const ref = await this.client.getRef({ owner: this.owner, repo: this.repo, ref: this.ref });
    if (ref === null) return null;
    const commit = await this.client.getCommit({
      owner: this.owner,
      repo: this.repo,
      commitSha: ref.sha,
    });
    const tree = await this.client.getTree({
      owner: this.owner,
      repo: this.repo,
      treeSha: commit.treeSha,
      recursive: true,
    });
    if (tree.truncated) throw new GitStateTransportError('tree_truncated');
    const entries = new Map<
      string,
      { readonly mode: string; readonly type: string; readonly sha: string }
    >();
    for (const entry of tree.entries) {
      if (entries.has(entry.path)) throw new GitStateTransportError('duplicate_tree_entry');
      entries.set(entry.path, { mode: entry.mode, type: entry.type, sha: entry.sha });
    }
    return { commitSha: ref.sha, treeSha: commit.treeSha, entries };
  }

  async readBlob(state: GitStateRef, path: string): Promise<Uint8Array | null> {
    const entry = state.entries.get(path);
    if (!entry) return null;
    if (entry.type !== 'blob' || entry.mode !== '100644')
      throw new GitStateTransportError('invalid_tree_entry');
    const result = await this.client.getBlob({
      owner: this.owner,
      repo: this.repo,
      blobSha: entry.sha,
    });
    return Uint8Array.from(Buffer.from(result.contentBase64, 'base64'));
  }

  async initialize(baseCommitSha: string): Promise<'created' | 'already_exists' | 'unknown'> {
    return this.client.createRef({
      owner: this.owner,
      repo: this.repo,
      ref: this.ref,
      sha: baseCommitSha,
    });
  }

  async commit(
    state: GitStateRef,
    writes: ReadonlyMap<string, Uint8Array>,
    message: string,
  ): Promise<'applied' | 'rejected' | 'unknown'> {
    const entries = await Promise.all(
      [...writes.entries()].map(async ([path, bytes]) => {
        const blob = await this.client.createBlob({
          owner: this.owner,
          repo: this.repo,
          contentBase64: Buffer.from(bytes).toString('base64'),
        });
        return { path, mode: '100644' as const, blobSha: blob.sha };
      }),
    );
    const tree = await this.client.createTree({
      owner: this.owner,
      repo: this.repo,
      baseTreeSha: state.treeSha,
      entries,
    });
    const commit = await this.client.createCommit({
      owner: this.owner,
      repo: this.repo,
      treeSha: tree.sha,
      parentSha: state.commitSha,
      message,
    });
    const result = await this.client.updateRef({
      owner: this.owner,
      repo: this.repo,
      ref: this.ref,
      sha: commit.sha,
      force: false,
    });
    return result === 'updated' ? 'applied' : result;
  }
}

export class GitStateTransportError extends Error {
  constructor(readonly code: 'tree_truncated' | 'duplicate_tree_entry' | 'invalid_tree_entry') {
    super(code);
    this.name = 'GitStateTransportError';
  }
}
