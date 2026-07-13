import { cp, rm, stat } from 'node:fs/promises';
import path from 'node:path';
import artifactClient from '@actions/artifact';
import { type UploadedArtifact } from './types.js';
import { ensureDir, walkFiles } from './utils.js';

export interface ArtifactRef {
  id: number;
  name: string;
  workflowRunId: number;
  runHeadSha?: string;
}

export interface ArtifactLookupContext {
  targetMode: 'pull-request' | 'synthetic-fixture';
  prNumber?: number;
}

interface WorkflowRunMetadata {
  id: number;
  workflowId?: number;
  workflowPath?: string;
  event?: string;
  conclusion?: string;
  headSha?: string;
  headRepository?: string;
  pullRequestNumbers: number[];
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
    private readonly lookupContext?: ArtifactLookupContext,
  ) {}

  async findStateArtifact(name: string, explicitRunId?: number): Promise<ArtifactRef | undefined> {
    const currentRun = await this.getWorkflowRun(this.currentRunId);
    const artifacts = explicitRunId
      ? await this.listWorkflowRunArtifacts(name, explicitRunId)
      : await this.listRepoArtifacts(name);
    const trusted = [] as Array<{ artifact: any; run: WorkflowRunMetadata }>;
    for (const artifact of artifacts) {
      if (artifact.name !== name || artifact.expired || !artifact.id) continue;
      const runId = Number(artifact.workflow_run?.id ?? explicitRunId ?? 0);
      if (!runId) continue;
      const run = runId === currentRun.id ? currentRun : await this.getWorkflowRun(runId);
      if (this.isTrustedRun(run, currentRun, explicitRunId !== undefined)) {
        trusted.push({ artifact, run });
      }
    }
    const match = trusted.sort((a, b) =>
      String(b.artifact.created_at).localeCompare(String(a.artifact.created_at)),
    )[0];
    if (!match?.artifact?.id) {
      return undefined;
    }
    return {
      id: Number(match.artifact.id),
      name: String(match.artifact.name),
      workflowRunId: match.run.id,
      runHeadSha: match.run.headSha,
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

  private async getWorkflowRun(runId: number): Promise<WorkflowRunMetadata> {
    const response = await this.octokit.request('GET /repos/{owner}/{repo}/actions/runs/{run_id}', {
      owner: this.owner,
      repo: this.repo,
      run_id: runId,
    });
    const run = response.data;
    return {
      id: Number(run.id),
      workflowId: Number(run.workflow_id) || undefined,
      workflowPath: typeof run.path === 'string' ? run.path : undefined,
      event: typeof run.event === 'string' ? run.event : undefined,
      conclusion: typeof run.conclusion === 'string' ? run.conclusion : undefined,
      headSha: typeof run.head_sha === 'string' ? run.head_sha : undefined,
      headRepository:
        typeof run.head_repository?.full_name === 'string'
          ? run.head_repository.full_name
          : undefined,
      pullRequestNumbers: Array.isArray(run.pull_requests)
        ? run.pull_requests
            .map((pull: any) => Number(pull.number))
            .filter((number: number) => Number.isInteger(number) && number > 0)
        : [],
    };
  }

  private isTrustedRun(
    candidate: WorkflowRunMetadata,
    current: WorkflowRunMetadata,
    explicitRunId: boolean,
  ): boolean {
    if (candidate.conclusion !== 'success') return false;
    if (current.workflowId && candidate.workflowId && current.workflowId !== candidate.workflowId) {
      return false;
    }
    if (
      current.workflowPath &&
      candidate.workflowPath &&
      current.workflowPath !== candidate.workflowPath
    ) {
      return false;
    }
    if (!current.workflowId && !current.workflowPath) return false;
    if (current.event && candidate.event !== current.event) return false;
    if (current.headRepository && candidate.headRepository !== current.headRepository) return false;
    if (this.lookupContext?.targetMode === 'pull-request' && this.lookupContext.prNumber) {
      if (!candidate.pullRequestNumbers.includes(this.lookupContext.prNumber) && !explicitRunId) {
        return false;
      }
    }
    return true;
  }
}

export class LocalArtifactStore implements ArtifactStore {
  constructor(private readonly root: string) {}

  async findStateArtifact(name: string): Promise<ArtifactRef | undefined> {
    const artifactRoot = path.join(this.root, name);
    try {
      const info = await stat(artifactRoot);
      return info.isDirectory() ? { id: 1, name, workflowRunId: 1 } : undefined;
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') {
        return undefined;
      }
      throw error;
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
