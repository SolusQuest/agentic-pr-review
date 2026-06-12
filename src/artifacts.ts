import { cp, mkdir, rm } from 'node:fs/promises';
import path from 'node:path';
import artifactClient from '@actions/artifact';
import { type UploadedArtifact } from './types.js';
import { ensureDir, walkFiles } from './utils.js';

export interface ArtifactRef {
  id: number;
  name: string;
  workflowRunId: number;
}

export interface ArtifactStore {
  findStateArtifact(name: string, explicitRunId?: number): Promise<ArtifactRef | undefined>;
  download(ref: ArtifactRef, destination: string): Promise<void>;
  upload(
    name: string,
    rootDirectory: string,
    files: string[],
    retentionDays: number,
  ): Promise<UploadedArtifact>;
}

export class GitHubArtifactStore implements ArtifactStore {
  constructor(
    private readonly octokit: any,
    private readonly token: string,
    private readonly owner: string,
    private readonly repo: string,
    private readonly currentRunId: number,
  ) {}

  async findStateArtifact(name: string, explicitRunId?: number): Promise<ArtifactRef | undefined> {
    const artifacts = explicitRunId
      ? await this.listWorkflowRunArtifacts(name, explicitRunId)
      : await this.listRepoArtifacts(name);
    const match = artifacts
      .filter((artifact: any) => artifact.name === name && !artifact.expired)
      .sort((a: any, b: any) => String(b.created_at).localeCompare(String(a.created_at)))[0];
    if (!match?.id) {
      return undefined;
    }
    return {
      id: Number(match.id),
      name: String(match.name),
      workflowRunId: Number(match.workflow_run?.id ?? explicitRunId ?? this.currentRunId),
    };
  }

  async download(ref: ArtifactRef, destination: string): Promise<void> {
    await ensureDir(destination);
    await artifactClient.downloadArtifact(ref.id, {
      path: destination,
      findBy: {
        token: this.token,
        workflowRunId: ref.workflowRunId,
        repositoryOwner: this.owner,
        repositoryName: this.repo,
      },
    });
  }

  async upload(
    name: string,
    rootDirectory: string,
    files: string[],
    retentionDays: number,
  ): Promise<UploadedArtifact> {
    const result = await artifactClient.uploadArtifact(name, files, rootDirectory, {
      retentionDays,
    });
    return {
      name,
      id: result.id,
      url: result.id
        ? `https://github.com/${this.owner}/${this.repo}/actions/runs/${this.currentRunId}/artifacts/${result.id}`
        : undefined,
      retentionDays,
    };
  }

  private async listRepoArtifacts(name: string): Promise<any[]> {
    const response = await this.octokit.request('GET /repos/{owner}/{repo}/actions/artifacts', {
      owner: this.owner,
      repo: this.repo,
      name,
      per_page: 100,
    });
    return response.data.artifacts ?? [];
  }

  private async listWorkflowRunArtifacts(name: string, runId: number): Promise<any[]> {
    const response = await this.octokit.request(
      'GET /repos/{owner}/{repo}/actions/runs/{run_id}/artifacts',
      {
        owner: this.owner,
        repo: this.repo,
        run_id: runId,
        name,
        per_page: 100,
      },
    );
    return response.data.artifacts ?? [];
  }
}

export class LocalArtifactStore implements ArtifactStore {
  constructor(private readonly root: string) {}

  async findStateArtifact(name: string): Promise<ArtifactRef | undefined> {
    const artifactRoot = path.join(this.root, name);
    try {
      await mkdir(artifactRoot, { recursive: false });
      await rm(artifactRoot, { recursive: true, force: true });
      return undefined;
    } catch {
      return { id: 1, name, workflowRunId: 1 };
    }
  }

  async download(ref: ArtifactRef, destination: string): Promise<void> {
    await rm(destination, { recursive: true, force: true });
    await ensureDir(destination);
    await cp(path.join(this.root, ref.name), destination, { recursive: true });
  }

  async upload(
    name: string,
    rootDirectory: string,
    _files: string[],
    retentionDays: number,
  ): Promise<UploadedArtifact> {
    const destination = path.join(this.root, name);
    await rm(destination, { recursive: true, force: true });
    await ensureDir(destination);
    for (const file of await walkFiles(rootDirectory)) {
      const relative = path.relative(rootDirectory, file);
      const target = path.join(destination, relative);
      await ensureDir(path.dirname(target));
      await cp(file, target);
    }
    return { name, id: 1, url: `file://${destination}`, retentionDays };
  }
}
