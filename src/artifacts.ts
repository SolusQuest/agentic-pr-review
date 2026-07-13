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
  runtimeBackend?: 'legacy' | 'deterministic-csharp';
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
    if (this.lookupContext?.runtimeBackend === 'legacy') {
      return this.findLegacyStateArtifact(name, explicitRunId);
    }
    const strictProvenance = true;
    const currentRun = await this.getWorkflowRun(this.currentRunId, strictProvenance);
    const artifacts = explicitRunId
      ? await this.listWorkflowRunArtifacts(name, explicitRunId)
      : await this.listRepoArtifacts(name);
    const trusted = [] as Array<{ artifact: any; run: WorkflowRunMetadata }>;
    for (const artifact of artifacts) {
      if (artifact.name !== name || artifact.expired || !artifact.id) continue;
      const runId = Number(artifact.workflow_run?.id ?? explicitRunId ?? 0);
      if (!runId) continue;
      let run: WorkflowRunMetadata;
      try {
        run =
          runId === currentRun.id ? currentRun : await this.getWorkflowRun(runId, strictProvenance);
      } catch {
        continue;
      }
      if (this.isTrustedRun(run, currentRun, explicitRunId, strictProvenance)) {
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

  private async findLegacyStateArtifact(
    name: string,
    explicitRunId?: number,
  ): Promise<ArtifactRef | undefined> {
    const artifacts = explicitRunId
      ? await this.listWorkflowRunArtifacts(name, explicitRunId)
      : await this.listRepoArtifacts(name);
    const match = artifacts
      .filter((artifact) => artifact.name === name && !artifact.expired && artifact.id)
      .sort((a, b) => String(b.created_at).localeCompare(String(a.created_at)))[0];
    if (!match?.id) return undefined;
    return {
      id: Number(match.id),
      name: String(match.name),
      workflowRunId: Number(match.workflow_run?.id ?? explicitRunId ?? 0),
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

  private async getWorkflowRun(
    runId: number,
    strictProvenance: boolean,
  ): Promise<WorkflowRunMetadata> {
    const response = await this.octokit.request('GET /repos/{owner}/{repo}/actions/runs/{run_id}', {
      owner: this.owner,
      repo: this.repo,
      run_id: runId,
    });
    const run = response.data;
    const workflowId = Number(run?.workflow_id);
    const id = Number(run?.id);
    const workflowPath = run?.path;
    const event = run?.event;
    const conclusion = typeof run?.conclusion === 'string' ? run.conclusion : undefined;
    const headSha = run?.head_sha;
    const headRepository = run?.head_repository?.full_name;
    if (
      !Number.isSafeInteger(id) ||
      id <= 0 ||
      (strictProvenance &&
        (!Number.isSafeInteger(workflowId) ||
          workflowId <= 0 ||
          typeof workflowPath !== 'string' ||
          !workflowPath ||
          typeof event !== 'string' ||
          !event ||
          typeof headSha !== 'string' ||
          !headSha ||
          typeof headRepository !== 'string' ||
          !headRepository))
    ) {
      throw new Error('artifact provenance metadata is incomplete');
    }
    return {
      id,
      workflowId,
      workflowPath,
      event,
      conclusion,
      headSha,
      headRepository,
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
    explicitRunId: number | undefined,
    strictProvenance: boolean,
  ): boolean {
    if (candidate.conclusion !== 'success') return false;
    if (strictProvenance) {
      if (candidate.workflowId !== current.workflowId) return false;
      if (candidate.workflowPath !== current.workflowPath) return false;
      if (candidate.event !== current.event) return false;
      if (candidate.headRepository !== current.headRepository) return false;
      if (this.lookupContext?.targetMode === 'pull-request' && this.lookupContext.prNumber) {
        if (candidate.event !== 'pull_request') return false;
        if (!candidate.pullRequestNumbers.includes(this.lookupContext.prNumber)) return false;
      }
      return true;
    }
    if (current.workflowId && candidate.workflowId && candidate.workflowId !== current.workflowId) {
      return false;
    }
    if (
      current.workflowPath &&
      candidate.workflowPath &&
      candidate.workflowPath !== current.workflowPath
    ) {
      return false;
    }
    if (!current.workflowId && !current.workflowPath) return false;
    if (current.event && candidate.event !== current.event) return false;
    if (current.headRepository && candidate.headRepository !== current.headRepository) {
      return false;
    }
    if (
      this.lookupContext?.targetMode === 'pull-request' &&
      this.lookupContext.prNumber &&
      !candidate.pullRequestNumbers.includes(this.lookupContext.prNumber) &&
      !explicitRunId
    ) {
      return false;
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
