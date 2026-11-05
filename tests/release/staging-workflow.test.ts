import { readFileSync, rmSync, mkdtempSync, mkdirSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { execFileSync, spawnSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';
import { parse } from 'yaml';
import { afterEach, describe, expect, test, vi } from 'vitest';
import { createRawFileUploadStream } from '../../node_modules/@actions/artifact/lib/internal/upload/stream.js';
import { canonicalJson, sha256 } from '../../scripts/release/build-payload.format.mjs';
import { verifyCandidateWorkflow } from '../../scripts/release/candidate.mjs';
import { stageHttp } from '../../scripts/release/stage-candidate.http.mjs';
import {
  artifactTransport,
  main,
  stagingDiagnostic,
} from '../../scripts/release/stage-candidate.mjs';
import { stagingFixture } from './staging.fixtures.mjs';
const root = '/repos/SolusQuest/agentic-pr-review';
const roots: string[] = [];
afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  for (const path of roots.splice(0)) rmSync(path, { recursive: true, force: true });
});
async function cliFixture() {
  const x = await stagingFixture(vi);
  roots.push(x.f.root);
  // main() deliberately uses the real control checkout, not the synthetic T tree.
  const current = execFileSync('git', ['rev-parse', 'HEAD']).toString().trim();
  x.request.controlCommit = current;
  x.approved();
  const env = {
    ...x.env,
    GITHUB_WORKFLOW_SHA: current,
    GITHUB_SHA: current,
    STAGE_REQUEST: canonicalJson(x.request),
    R7_STAGING_TOKEN: undefined,
  };
  const args = [
    '--source',
    x.f.source,
    '--version',
    x.input.releaseVersion,
    '--output',
    join(x.f.root, 'preflight'),
  ];
  return { x, env, args };
}
describe('P4 workflow/transport authority and actual locked SDK codec', () => {
  test('completion preflight authenticates its historical parent with read authority before a normal review exists', async () => {
    const x = await stagingFixture(vi, { completion: true });
    roots.push(x.f.root);
    const current = execFileSync('git', ['rev-parse', 'HEAD']).toString().trim();
    const parent = '4a7656cb0f402d3ea536bc8f00e5f8e3faab8b8e';
    x.request.controlCommit = parent;
    x.completionRequest.origin.controlCommit = parent;
    x.completionRequest.stageRequestSha256 = sha256(canonicalJson(x.request));
    x.env.R7_STAGE_REQUEST_SHA256 = x.completionRequest.stageRequestSha256;
    x.state.stageRun.head_sha = parent;
    x.state.parentJob.head_sha = parent;
    x.completionRequest.controlCommit = current;
    x.completionApproved();
    x.state.approvals = [];
    const env = {
      ...x.env,
      R7_STAGING_TOKEN: undefined,
      GITHUB_WORKFLOW_SHA: current,
      GITHUB_SHA: current,
      STAGE_REQUEST: canonicalJson(x.completionRequest),
    };
    await expect(
      main(
        [
          'preflight',
          '--source',
          x.input.sourceCommit,
          '--version',
          x.input.releaseVersion,
          '--output',
          join(x.f.root, 'completion-preflight'),
        ],
        env,
      ),
    ).resolves.toEqual({ environmentAdmitted: true });
    expect(x.state.downloads).toHaveLength(0);
    expect(x.state.writes).toHaveLength(0);
  });
  test('dedicated completion uses the existing stage action with an explicit CLI route', async () => {
    vi.stubEnv('PATH', process.env.PATH);
    const staging = await import('../../scripts/release/stage-candidate.mjs');
    const action = await import('../../scripts/release/release-action.mjs');
    const invoke = vi
      .spyOn(staging, 'main')
      .mockResolvedValue({ releaseId: '801', stagedSha256: 'b'.repeat(64) });
    await action.main({
      INPUT_PHASE: 'stage',
      RELEASE_OPERATION: 'complete-stage',
      INPUT_SOURCE: 'a'.repeat(40),
      INPUT_VERSION: 'v1.0.0-internal.1',
      RUNNER_TEMP: '/synthetic',
      PATH: 'synthetic',
    });
    expect(invoke.mock.calls[0][0][0]).toBe('workflow-complete-stage');
  });
  test.each(['missing', 'foreign'])(
    'protected completion CLI refuses %s review before its first SDK input download',
    async (kind) => {
      const x = await stagingFixture(vi, { completion: true });
      roots.push(x.f.root);
      const current = execFileSync('git', ['rev-parse', 'HEAD']).toString().trim();
      x.completionRequest.controlCommit = current;
      x.completionApproved();
      if (kind === 'missing') x.state.approvals = [];
      else x.state.approvals[0].user.id = 999;
      const env = {
        ...x.env,
        GITHUB_WORKFLOW_SHA: current,
        GITHUB_SHA: current,
        STAGE_REQUEST: canonicalJson(x.completionRequest),
      };
      const error = await main(
        [
          'workflow-complete-stage',
          '--source',
          x.input.sourceCommit,
          '--version',
          x.input.releaseVersion,
          '--output',
          join(x.f.root, 'protected-cli'),
        ],
        env,
      ).catch((e) => e);
      expect(stagingDiagnostic(error)).toBe(
        'candidate_staging_denied checkpoint=completion_owner code=completion_owner_denied\n',
      );
      expect(x.state.downloads).toHaveLength(0);
      expect(x.state.writes).toHaveLength(0);
    },
  );
  test('Node action routes stage with fixed arguments and confines runtime capabilities to SDK environment', async () => {
    const staging = await import('../../scripts/release/stage-candidate.mjs');
    const promotion = await import('../../scripts/release/promote-candidate.mjs');
    const action = await import('../../scripts/release/release-action.mjs');
    vi.stubEnv('PATH', 'UNPINNED_PATH');
    vi.stubEnv('ACTIONS_RUNTIME_TOKEN', 'SYNTHETIC_RUNTIME');
    vi.stubEnv('ACTIONS_RESULTS_URL', 'https://results.invalid');
    const env = {
      INPUT_PHASE: 'stage',
      RELEASE_OPERATION: 'recover-stage',
      INPUT_SOURCE: 'a'.repeat(40),
      INPUT_VERSION: 'v1.0.0-internal.1',
      RUNNER_TEMP: '/synthetic',
      PATH: 'UNPINNED_PATH',
      ACTIONS_RUNTIME_TOKEN: 'SYNTHETIC_RUNTIME',
      ACTIONS_RESULTS_URL: 'https://results.invalid',
      R7_STAGING_TOKEN: 'SYNTHETIC_WRITE',
    };
    const result = { stagedSha256: 'b'.repeat(64), releaseId: '1' };
    const stage = vi.spyOn(staging, 'main').mockResolvedValue(result);
    const promote = vi.spyOn(promotion, 'main');
    await expect(action.main(env)).resolves.toBe(result);
    const [args, supplied] = stage.mock.calls[0];
    expect(args).toEqual([
      'workflow-stage',
      '--source',
      env.INPUT_SOURCE,
      '--version',
      env.INPUT_VERSION,
      '--output',
      '/synthetic/r7-p4-staged',
    ]);
    expect(supplied).toEqual({ ...env, PATH: '/synthetic/r7-p4-gh:UNPINNED_PATH' });
    expect(supplied).not.toBe(env);
    expect(process.env.PATH).toBe(supplied?.PATH);
    expect(process.env.ACTIONS_RUNTIME_TOKEN).toBeUndefined();
    expect(process.env.ACTIONS_RESULTS_URL).toBeUndefined();
    expect(promote).not.toHaveBeenCalled();
    stage.mockClear();
    await expect(action.main({ ...env, RELEASE_OPERATION: 'promote' })).rejects.toThrow(
      'release_action_denied',
    );
    await expect(action.main({ ...env, INPUT_PHASE: 'PRIVATE_CANARY' })).rejects.toThrow(
      'release_action_denied',
    );
    expect(stage).not.toHaveBeenCalled();
    expect(promote).not.toHaveBeenCalled();
  });
  test('Node action CLI preserves bounded stage and promotion rejection output', () => {
    for (const phase of ['stage', 'promote', 'PRIVATE_CANARY']) {
      const child = spawnSync(process.execPath, ['scripts/release/release-action.mjs'], {
        encoding: 'utf8',
        env: {
          INPUT_PHASE: phase,
          RELEASE_OPERATION: phase,
          RUNNER_TEMP: '/synthetic',
          INPUT_SOURCE: 'a'.repeat(40),
          INPUT_VERSION: 'v1.0.0-internal.1',
        },
      });
      expect(child.status).toBe(1);
      expect(child.stdout).toBe('');
      expect(child.stderr).toBe(
        phase === 'stage'
          ? 'candidate_staging_denied checkpoint=request code=unknown\n'
          : phase === 'promote'
            ? 'candidate_promotion_denied\n'
            : 'candidate_release_denied\n',
      );
    }
  });
  test('CLI diagnostics keep trusted guard provenance and never inspect arbitrary exceptions', async () => {
    const { env, args } = await cliFixture();
    const isolation = await main(['workflow-stage', ...args], { ...env }).catch((e) => e);
    expect(stagingDiagnostic(isolation)).toBe(
      'candidate_staging_denied checkpoint=isolation code=stage_token_isolation_denied\n',
    );
    const invalid = await main([], { ...env }).catch((e) => e);
    expect(stagingDiagnostic(invalid)).toBe(
      'candidate_staging_denied checkpoint=arguments code=stage_arguments_invalid\n',
    );
    for (const error of [
      'PRIVATE_CANARY',
      { message: 'PRIVATE_CANARY', code: 'stage_environment_denied' },
      new Error('stage_environment_denied'),
      Object.defineProperty(new Error(), 'message', {
        get() {
          throw new Error('PRIVATE_CANARY');
        },
      }),
    ]) {
      // stageHttp also flattens transport errors; neither it nor an arbitrary error is trusted.
      vi.stubGlobal('fetch', async () => {
        throw error;
      });
      const failed = await main(['preflight', ...args], { ...env }).catch((e) => e);
      expect(stagingDiagnostic(failed)).toBe(
        'candidate_staging_denied checkpoint=environment code=unknown\n',
      );
      expect(stagingDiagnostic(error)).toBe(
        'candidate_staging_denied checkpoint=unknown code=unknown\n',
      );
    }
    // The executable uses exactly the same formatter and retains nonzero exit.
    const cli = spawnSync(process.execPath, ['scripts/release/stage-candidate.mjs'], {
      encoding: 'utf8',
      env: {},
    });
    expect(cli.status).toBe(1);
    expect(cli.stdout).toBe('');
    expect(cli.stderr).toBe(stagingDiagnostic(invalid));
  });
  test('real SDK child publishes only bounded private failures for transfer and its own readback', () => {
    const directory = mkdtempSync(join(tmpdir(), 'apr-stage-sdk-'));
    roots.push(directory);
    const config = join(directory, 'config.json'),
      probe = join(directory, 'probe.mjs');
    const sdkUrl = pathToFileURL(resolve('node_modules/@actions/artifact/lib/artifact.js')).href;
    writeFileSync(
      config,
      JSON.stringify({
        operation: 'download',
        id: '1',
        directory,
        raw: true,
        sha256: 'a'.repeat(64),
        runId: '1',
      }),
    );
    for (const fault of ['success', 'readback', 'transfer']) {
      const result = join(directory, fault + '.json');
      writeFileSync(
        probe,
        `import { DefaultArtifactClient } from ${JSON.stringify(sdkUrl)};
        DefaultArtifactClient.prototype.downloadArtifact = async () => {
          process.stdout.write('PRIVATE_CANARY'); process.stderr.write('PRIVATE_CANARY');
          ${fault !== 'transfer' ? `return { digestMismatch: ${fault === 'readback'}, downloadPath: ${JSON.stringify(directory)} };` : "throw new Error('sdk_readback_denied PRIVATE_CANARY');"}
        };`,
      );
      const child = spawnSync(
        process.execPath,
        ['scripts/release/stage-candidate.sdk.mjs', config, result],
        {
          encoding: 'utf8',
          env: { NODE_OPTIONS: '--import=' + probe },
        },
      );
      expect(child.status).toBe(fault === 'success' ? 0 : 1);
      expect(JSON.parse(readFileSync(result, 'utf8'))).toEqual(
        fault === 'success'
          ? { downloaded: true }
          : { checkpoint: fault, code: fault === 'readback' ? 'sdk_readback_denied' : 'unknown' },
      );
      expect(statSync(result).mode & 0o777).toBe(0o600);
      expect(readFileSync(result, 'utf8')).not.toContain('PRIVATE_CANARY');
      expect(statSync(result).size).toBeLessThan(256);
    }
  });
  test('parent SDK boundary rejects missing, malformed and contradictory child results without leaking streams', async () => {
    const directory = mkdtempSync(join(tmpdir(), 'apr-stage-parent-'));
    roots.push(directory);
    const probe = join(directory, 'probe.mjs'),
      observed = join(directory, 'env.json');
    const controlled = { checkpoint: 'readback', code: 'sdk_readback_denied' };
    const cases: {
      body: string | null;
      exit: number;
      sdk?: boolean;
      signal?: boolean;
      success?: boolean;
    }[] = [
      { body: JSON.stringify(controlled), exit: 1, sdk: true },
      { body: null, exit: 1 },
      { body: 'not-json', exit: 1 },
      { body: JSON.stringify({ ...controlled, extra: 'PRIVATE_CANARY' }), exit: 1 },
      { body: JSON.stringify({ checkpoint: 'transfer', code: 'PRIVATE_CANARY' }), exit: 1 },
      { body: JSON.stringify(controlled) + ' '.repeat(256), exit: 1 },
      { body: JSON.stringify(controlled), exit: 0 },
      { body: JSON.stringify({ downloaded: true }), exit: 1 },
      { body: JSON.stringify(controlled), exit: 1, signal: true },
      { body: JSON.stringify({ downloaded: true }), exit: 0, success: true },
    ];
    for (const [n, c] of cases.entries()) {
      const scratch = join(directory, 'scratch-' + n);
      mkdirSync(scratch);
      writeFileSync(
        probe,
        `import fs from 'node:fs';
        fs.writeFileSync(${JSON.stringify(observed)}, JSON.stringify({ write: process.env.R7_STAGING_TOKEN, read: process.env.GITHUB_TOKEN, runtime: process.env.ACTIONS_RUNTIME_TOKEN, results: process.env.ACTIONS_RESULTS_URL }));
        ${c.body === null ? '' : `fs.writeFileSync(process.argv[3], ${JSON.stringify(c.body)}, { mode: 0o600 });`}
        process.stdout.write('PRIVATE_CANARY'); process.stderr.write('PRIVATE_CANARY');
        ${c.signal ? "process.kill(process.pid, 'SIGTERM');" : `process.exit(${c.exit});`}`,
      );
      const transport = artifactTransport(scratch, {
        NODE_OPTIONS: '--import=' + probe,
        R7_STAGING_TOKEN: 'PRIVATE_CANARY',
        GITHUB_TOKEN: 'SYNTHETIC_READ',
        ACTIONS_RUNTIME_TOKEN: 'SYNTHETIC_RUNTIME',
        ACTIONS_RESULTS_URL: 'https://results.invalid',
      });
      const error = await transport
        .download({ id: '1', workflow_run: { id: '1' }, digest: 'sha256:' + 'a'.repeat(64) }, false)
        .catch((e) => e);
      if (c.success) expect(error.directory).toMatch(/artifact-1-/);
      else {
        expect(error).toBeInstanceOf(Error);
        expect(stagingDiagnostic(error)).toBe(
          c.sdk
            ? 'candidate_staging_denied checkpoint=unknown sdk_checkpoint=readback code=sdk_readback_denied\n'
            : 'candidate_staging_denied checkpoint=unknown code=unknown\n',
        );
      }
      expect(JSON.parse(readFileSync(observed, 'utf8'))).toEqual({
        read: 'SYNTHETIC_READ',
        runtime: 'SYNTHETIC_RUNTIME',
        results: 'https://results.invalid',
      });
    }
  });
  test('real preflight CLI reads the exact environment with read authority and no staging secret', async () => {
    const { x, env, args } = await cliFixture();
    await expect(main(['preflight', ...args], env)).resolves.toEqual({ environmentAdmitted: true });
    expect(x.state.reads).toEqual([
      { path: '/environments/r7-staging', auth: 'Bearer READ_ONLY_CANARY' },
    ]);
    expect(x.state.writes).toHaveLength(0);
  });
  test('real workflow-stage CLI shares environment readback before requiring write authority or SDK access', async () => {
    const { x, env, args } = await cliFixture();
    await expect(main(['workflow-stage', ...args], env)).rejects.toThrow(
      'stage_token_isolation_denied',
    );
    expect(x.state.reads).toEqual([
      { path: '/environments/r7-staging', auth: 'Bearer READ_ONLY_CANARY' },
    ]);
    expect(x.state.writes).toHaveLength(0);
  });
  test.each(['preflight', 'workflow-stage'])(
    'real %s CLI refuses absent or replaced environment without writes',
    async (command) => {
      const { x, env, args } = await cliFixture();
      x.state.environment = null;
      await expect(main([command, ...args], { ...env })).rejects.toThrow('stage_read_denied');
      x.state.environment = { id: '799', name: 'r7-staging' };
      await expect(main([command, ...args], { ...env })).rejects.toThrow(
        'stage_environment_denied',
      );
      expect(x.state.writes).toHaveLength(0);
      expect(
        x.state.reads.every(
          (r) => r.path === '/environments/r7-staging' && r.auth === 'Bearer READ_ONLY_CANARY',
        ),
      ).toBe(true);
    },
  );
  test('real preflight CLI preserves main/manual admission before any environment API access', async () => {
    const { x, env, args } = await cliFixture();
    await expect(
      main(['preflight', ...args], { ...env, GITHUB_EVENT_NAME: 'pull_request' }),
    ).rejects.toThrow('prepare_workflow_denied');
    expect(x.state.reads).toHaveLength(0);
    expect(x.state.writes).toHaveLength(0);
  });
  test('isolates main/manual stage authority, prechecks existing environment and keeps tests credential-free', () => {
    const w = parse(readFileSync('.github/workflows/release.yml', 'utf8'));
    expect(verifyCandidateWorkflow(w)).toBe(true);
    const p = w.jobs['stage-preflight'],
      s = w.jobs.stage;
    expect(p.environment).toBeUndefined();
    expect(s.needs).toBe('stage-preflight');
    expect(s.environment).toBe('r7-staging');
    expect(s.concurrency).toEqual({ group: 'r7-p4-staging', 'cancel-in-progress': false });
    for (const job of [p, s]) {
      expect(job.if).toContain("github.event_name == 'workflow_dispatch'");
      expect(job.if).toContain("github.ref == 'refs/heads/main'");
      expect(job.if).toContain("vars.R7_STAGING_ENABLED == 'true'");
      expect(job.if).toContain("inputs.operation == 'recover-stage'");
      expect(job.permissions).toEqual({ contents: 'read', actions: 'read' });
      expect(job['runs-on']).toBe('ubuntu-24.04');
      expect(job.steps[0].with).toMatchObject({
        ref: '${{ github.workflow_sha }}',
        'persist-credentials': false,
        'fetch-depth': 0,
      });
      for (const step of job.steps.filter((x: any) => x.run))
        expect(step.run).not.toContain('${{ inputs.');
    }
    expect(JSON.stringify(p)).not.toMatch(/secrets\.|id-token|attestations/);
    expect(JSON.stringify(s)).not.toMatch(
      /actions\/attest|id-token|packages:|setup-dotnet|release create|tag push/,
    );
    const secretSteps = s.steps.filter((x: any) => JSON.stringify(x).includes('secrets.'));
    expect(secretSteps).toHaveLength(1);
    expect(secretSteps[0].env.R7_STAGING_TOKEN).toBe('${{ secrets.R7_STAGING_TOKEN }}');
    expect(secretSteps[0].uses).toBe('./control/.github/actions/r7-release');
    expect(secretSteps[0].with).toEqual({ phase: 'stage' });
    expect(secretSteps[0].run).toBeUndefined();
    const action = parse(readFileSync('.github/actions/r7-release/action.yml', 'utf8'));
    expect(action.runs).toEqual({
      using: 'node24',
      main: '../../../scripts/release/release-action.mjs',
    });
    expect(action.inputs.phase.required).toBe(true);
    expect(resolve('.github/actions/r7-release', action.runs.main)).toBe(
      resolve('scripts/release/release-action.mjs'),
    );
    for (const name of ['candidate-fixtures', 'candidate-smoke']) {
      expect(w.jobs[name].if).toBe("github.event_name != 'workflow_dispatch'");
      expect(JSON.stringify(w.jobs[name])).not.toContain('secrets.');
    }
    for (const mutate of [
      (x: any) => {
        delete x.jobs['stage-preflight'];
      },
      (x: any) => {
        x.jobs.stage.permissions.contents = 'write';
      },
      (x: any) => {
        x.jobs.stage.environment = '${{ inputs.environment }}';
      },
      (x: any) => {
        x.jobs.stage.if = 'true';
      },
      (x: any) => {
        x.jobs.stage.concurrency['cancel-in-progress'] = true;
      },
    ]) {
      const changed = structuredClone(w);
      mutate(changed);
      expect(() => verifyCandidateWorkflow(changed)).toThrow('candidate_workflow_topology_drift');
    }
  });
  test('locked SDK raw codec preserves exact canonical intent bytes and digest', async () => {
    const directory = mkdtempSync(join(tmpdir(), 'apr-stage-codec-'));
    try {
      const bytes = Buffer.from(
        canonicalJson({ kind: 'synthetic-intent', operations: [{ id: 'create' }] }),
      );
      const path = join(directory, 'intent.json');
      writeFileSync(path, bytes);
      const stream = await createRawFileUploadStream(path),
        chunks = [];
      for await (const chunk of stream) chunks.push(Buffer.from(chunk));
      const actual = Buffer.concat(chunks);
      expect(actual).toEqual(bytes);
      expect(sha256(actual)).toBe(sha256(bytes));
      const adapter = readFileSync('scripts/release/stage-candidate.sdk.mjs', 'utf8');
      expect(adapter).toContain('skipArchive: true');
      expect(adapter).toContain("expectedHash: 'sha256:' + c.sha256");
      expect(adapter).toContain('r.digestMismatch !== false');
    } finally {
      rmSync(directory, { force: true, recursive: true });
    }
  });
  test('asset redirect uses only fixed GitHub hosts and strips write authority', async () => {
    const calls: any[] = [],
      bytes = Buffer.from('original');
    vi.stubGlobal('fetch', async (url: any, o: any) => {
      calls.push({ url: String(url), options: o });
      return calls.length === 1
        ? new Response(null, {
            status: 302,
            headers: {
              location: 'https://release-assets.githubusercontent.com/file?sig=synthetic',
            },
          })
        : new Response(bytes);
    });
    const http = stageHttp('PRIVATE_CANARY');
    await expect(http.assetHash('123', bytes.length)).resolves.toBe(sha256(bytes));
    expect(calls[0].options.headers.Authorization).toBe('Bearer PRIVATE_CANARY');
    expect(calls[1].options.headers).toBeUndefined();
    expect(calls[1].options.redirect).toBe('error');
    vi.stubGlobal(
      'fetch',
      async () =>
        new Response(null, {
          status: 302,
          headers: { location: 'https://evil.invalid/PRIVATE_CANARY' },
        }),
    );
    await expect(http.assetHash('123', 8)).rejects.toThrow('stage_asset_redirect_denied');
  });
  test('bounded pagination refuses incomplete or excessive responses and never retries a POST', async () => {
    const mock = vi.fn(
      async () => new Response(JSON.stringify({ total_count: 2, artifacts: [{ id: 1 }] })),
    );
    vi.stubGlobal('fetch', mock);
    const h = stageHttp('PRIVATE_CANARY');
    await expect(h.list(root + '/actions/artifacts?name=fixed', 'artifacts')).rejects.toThrow(
      'stage_pagination_incomplete',
    );
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(JSON.stringify(Array(100).fill({ id: 1 })))),
    );
    await expect(h.list(root + '/releases')).rejects.toThrow('stage_pagination_limit');
    const post = vi.fn(async () => {
      throw new Error('PRIVATE_CANARY');
    });
    vi.stubGlobal('fetch', post);
    await expect(h.post(root + '/releases', {})).rejects.toThrow('stage_transport_unknown');
    expect(post).toHaveBeenCalledTimes(1);
  });
});
