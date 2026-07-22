import type { GitDataClient } from './github-git-data.js';

/** Narrow adapter around the GitHub REST Git-data endpoints. */
export class OctokitGitDataClient implements GitDataClient {
  constructor(private readonly octokit: any) {}

  async getRef(input: { owner: string; repo: string; ref: string }): Promise<{ readonly sha: string } | null> {
    try {
      const response = await this.octokit.rest.git.getRef(input);
      return { sha: response.data.object.sha };
    } catch (error) {
      if (status(error) === 404) return null;
      throw error;
    }
  }
  async getCommit(input: { owner: string; repo: string; commitSha: string }): Promise<{ readonly treeSha: string }> {
    const response = await this.octokit.rest.git.getCommit({ owner: input.owner, repo: input.repo, commit_sha: input.commitSha });
    return { treeSha: response.data.tree.sha };
  }
  async getTree(input: { owner: string; repo: string; treeSha: string; recursive: true }) {
    const response = await this.octokit.rest.git.getTree({ owner: input.owner, repo: input.repo, tree_sha: input.treeSha, recursive: '1' });
    return {
      truncated: response.data.truncated === true,
      entries: response.data.tree.map((entry: any) => ({ path: entry.path, mode: entry.mode, type: entry.type, sha: entry.sha })),
    };
  }
  async getBlob(input: { owner: string; repo: string; blobSha: string }): Promise<{ readonly contentBase64: string }> {
    const response = await this.octokit.rest.git.getBlob({ owner: input.owner, repo: input.repo, file_sha: input.blobSha });
    return { contentBase64: response.data.content.replace(/\s/gu, '') };
  }
  async createBlob(input: { owner: string; repo: string; contentBase64: string }): Promise<{ readonly sha: string }> {
    const response = await this.octokit.rest.git.createBlob({ owner: input.owner, repo: input.repo, content: input.contentBase64, encoding: 'base64' });
    return { sha: response.data.sha };
  }
  async createTree(input: { owner: string; repo: string; baseTreeSha: string; entries: readonly { readonly path: string; readonly mode: '100644'; readonly blobSha: string }[] }): Promise<{ readonly sha: string }> {
    const response = await this.octokit.rest.git.createTree({ owner: input.owner, repo: input.repo, base_tree: input.baseTreeSha, tree: input.entries.map((entry) => ({ path: entry.path, mode: entry.mode, type: 'blob', sha: entry.blobSha })) });
    return { sha: response.data.sha };
  }
  async createCommit(input: { owner: string; repo: string; treeSha: string; parentSha: string; message: string }): Promise<{ readonly sha: string }> {
    const response = await this.octokit.rest.git.createCommit({ owner: input.owner, repo: input.repo, message: input.message, tree: input.treeSha, parents: [input.parentSha] });
    return { sha: response.data.sha };
  }
  async updateRef(input: { owner: string; repo: string; ref: string; sha: string; force: false }): Promise<'updated' | 'rejected' | 'unknown'> {
    try {
      await this.octokit.rest.git.updateRef({ owner: input.owner, repo: input.repo, ref: input.ref, sha: input.sha, force: false });
      return 'updated';
    } catch (error) {
      return status(error) === 409 || status(error) === 422 ? 'rejected' : 'unknown';
    }
  }
  async createRef(input: { owner: string; repo: string; ref: string; sha: string }): Promise<'created' | 'already_exists' | 'unknown'> {
    try {
      await this.octokit.rest.git.createRef({ owner: input.owner, repo: input.repo, ref: `refs/${input.ref}`, sha: input.sha });
      return 'created';
    } catch (error) {
      return status(error) === 422 ? 'already_exists' : 'unknown';
    }
  }
}

function status(error: unknown): number | undefined {
  const value = error as { status?: unknown } | undefined;
  return typeof value?.status === 'number' ? value.status : undefined;
}
