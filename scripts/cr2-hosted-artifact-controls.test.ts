import { afterEach, describe, expect, it } from 'vitest';
import { createServer, request } from 'node:http';
import { cp, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { parse } from 'yaml';
import { createRepositoryTestManagedActionSourceResolver } from '../src/action-wrapper/launcher/action-source.js';
import {
  packageManagedFiles,
  minimalManagedFiles,
  managedMap,
} from '../tests/distribution/managed-host-fixture.js';
import {
  actionSource,
  acknowledgement,
  inspectAssets,
  repository,
  sourceMetadata,
} from './cr2-hosted-artifact-policy.mjs';
import {
  validateCr2HostedArtifactWorkflow,
  validateCr2CredentialFreeWorkflow,
} from './cr2-hosted-artifact-workflow.mjs';
import { checkR4TrustedProof } from './check-r4-trusted-proof.mjs';
import { inspectProofNetwork } from '../runtime/tests/ActionHostTrustedProofVerifier/HostedArtifactInterop/run-phases.mjs';

const cleanup: (() => Promise<unknown>)[] = [];
afterEach(async () => {
  for (const close of cleanup.splice(0).reverse()) await close();
});
describe('H1 source admission uses the real production resolver', () => {
  async function setup() {
    const root = await mkdtemp(join(tmpdir(), 'h1-source-'));
    cleanup.push(() => rm(root, { recursive: true, force: true }));
    await cp('.github/actions/agentic-pr-review', root, { recursive: true });
    const mapBytes = Buffer.from(
      JSON.stringify(managedMap(packageManagedFiles(minimalManagedFiles()).receipt)),
    );
    await writeFile(join(root, 'payload-map.json'), mapBytes);
    const config = { assets: inspectAssets(process.cwd()), mapBytes: mapBytes.toString() };
    const paths: string[] = [];
    const server = createServer((incoming, response) => {
      paths.push(incoming.url!);
      try {
        expect(incoming.headers.authorization).toBeUndefined();
        const metadata = sourceMetadata(new URL(incoming.url!, 'https://api.github.com'), config);
        const body = Buffer.from(JSON.stringify(metadata));
        response.writeHead(200, { 'content-length': body.length });
        response.end(body);
      } catch {
        response.destroy();
      }
    });
    await new Promise<void>((resolve, reject) => {
      server.once('error', reject);
      server.listen(0, '127.0.0.1', resolve);
    });
    cleanup.push(async () => {
      server.closeAllConnections();
      await new Promise<void>((resolve) => server.close(() => resolve()));
    });
    const address = server.address() as { port: number };
    const resolveSource = createRepositoryTestManagedActionSourceResolver(
      (url, options, callback) =>
        request(
          new URL(url.pathname + url.search, `http://127.0.0.1:${address.port}`),
          options,
          callback,
        ),
    );
    const args = {
      actionRoot: root,
      mapBytes,
      actionRepository: repository,
      actionRef: actionSource,
      signal: new AbortController().signal,
    };
    return { root, args, resolveSource, paths };
  }
  it('admits all eight synthetic source responses without changing the checked null map', async () => {
    const test = await setup();
    expect(await test.resolveSource(test.args)).toBe(actionSource);
    expect(test.paths.length).toBe(8);
    expect(
      JSON.parse(await readFile('.github/actions/agentic-pr-review/payload-map.json', 'utf8')),
    ).toBeNull();
  });
  it('refuses a different installed source, changed executable, or changed map identity', async () => {
    const test = await setup();
    await expect(test.resolveSource({ ...test.args, actionRef: 'a'.repeat(40) })).rejects.toThrow(
      'wrapper_action_source_invalid',
    );
    const file = join(test.root, 'internal/dist/index.js');
    await writeFile(
      file,
      Buffer.concat([await readFile(file), Buffer.from('\n// changed proof installation\n')]),
    );
    await expect(test.resolveSource(test.args)).rejects.toThrow('wrapper_action_source_invalid');
    const clean = await setup();
    const map = JSON.parse(clean.args.mapBytes.toString());
    map.payload.archiveSha256 = '0'.repeat(64);
    await expect(
      clean.resolveSource({ ...clean.args, mapBytes: Buffer.from(JSON.stringify(map)) }),
    ).rejects.toThrow('wrapper_action_source_invalid');
  });
});
describe('H1 dormant hosted route and credential-free automatic route', () => {
  it('requires a nonempty network observation and admits only the owned loopback port', () => {
    const ipv4 =
      'connect(9, {sa_family=AF_INET, sin_port=htons(43811), sin_addr=inet_addr("127.0.0.1")}, 16) = 0';
    const mapped =
      'connect(9, {sa_family=AF_INET6, sin6_port=htons(43811), inet_pton(AF_INET6, "::ffff:127.0.0.1", &sin6_addr)}, 28) = 0';
    expect(inspectProofNetwork(ipv4, 43811).externalServiceDestinations).toBe(0);
    expect(inspectProofNetwork(mapped, 43811).externalServiceDestinations).toBe(0);
    expect(() => inspectProofNetwork('', 43811)).toThrow();
    expect(() => inspectProofNetwork(ipv4.replace('127.0.0.1', '8.8.8.8'), 43811)).toThrow();
    expect(() => inspectProofNetwork(mapped.replace('127.0.0.1', '127.0.0.2'), 43811)).toThrow();
    expect(() => inspectProofNetwork(ipv4, 43812)).toThrow();
  });
  it('integrates only the fully closed H1 route into the repository token guard', async () => {
    const value = parse(await readFile('.github/workflows/cr2-hosted-artifact-proof.yml', 'utf8'));
    expect(() => validateCr2HostedArtifactWorkflow(value)).not.toThrow();
    expect(() => checkR4TrustedProof()).not.toThrow();
    const mutations = [
      (v) => {
        v.on.push = {};
      },
      (v) => {
        v.permissions = { 'id-token': 'write' };
      },
      (v) => {
        v.jobs.extra = structuredClone(v.jobs.proof);
      },
      (v) => {
        v.jobs.proof.if = 'true';
      },
      (v) => {
        v.jobs.proof.permissions['pull-requests'] = 'write';
      },
      (v) => {
        v.jobs.proof['timeout-minutes'] = 60;
      },
      (v) => {
        v.on.workflow_dispatch.inputs.phase.options.push('cleanup-all');
      },
      (v) => {
        v.jobs.proof.steps.push({ run: 'unreviewed-command' });
      },
      (v) => {
        v.jobs.proof.steps[0].uses = 'unreviewed/action@main';
      },
      (v) => {
        v.jobs.proof.steps[5].with['github-token'] = '${{ secrets.OTHER_TOKEN }}';
      },
      (v) => {
        v.jobs.proof.steps[4].env.PROOF_MODE = 'arbitrary';
      },
      (v) => {
        v.jobs.proof.steps[5].env.PROVIDER = '${{ secrets.PROVIDER }}';
      },
      (v) => {
        delete v.jobs.proof.steps[6];
      },
    ];
    for (const mutate of mutations) {
      const changed = structuredClone(value);
      mutate(changed);
      expect(() => validateCr2HostedArtifactWorkflow(changed)).toThrow();
    }
  });
  it('keeps live operations manual, finite, fixed-actor and source guarded', async () => {
    const text = await readFile('.github/workflows/cr2-hosted-artifact-proof.yml', 'utf8');
    const value = parse(text);
    expect(Object.keys(value.on)).toEqual(['workflow_dispatch']);
    expect(value.permissions).toEqual({});
    const job = value.jobs.proof;
    expect(job.permissions).toEqual({ contents: 'read', actions: 'write' });
    expect(job['runs-on']).toBe('ubuntu-24.04');
    expect(job['timeout-minutes']).toBe(10);
    for (const required of [
      "github.actor == 'Yuee98'",
      "github.actor_id == '16307884'",
      'github.run_attempt == 1',
      'inputs.expected-head-sha == github.sha',
      'inputs.expected-head-sha == github.workflow_sha',
      acknowledgement,
    ])
      expect(job.if).toContain(required);
    const composite = job.steps.find((step) =>
      step.uses?.includes('/.github/actions/agentic-pr-review@'),
    );
    expect(composite.uses.endsWith('@' + actionSource)).toBe(true);
    expect(composite.with).toEqual({ 'github-token': '${{ secrets.GITHUB_TOKEN }}' });
    expect(text).not.toMatch(
      /pull_request_target|id-token|packages:|environment:|PAT|provider-api-key:/u,
    );
  });
  it('automatic CI has no live route, user phase selector, secrets or write permission', async () => {
    const text = await readFile('.github/workflows/cr2-hosted-artifact-rehearsal.yml', 'utf8');
    const value = parse(text);
    expect(() => validateCr2CredentialFreeWorkflow(value)).not.toThrow();
    for (const mutate of [
      (v) => {
        v.jobs.synthetic.permissions = { actions: 'write' };
      },
      (v) => {
        v.jobs.synthetic.steps[5].run = 'node scripts/cr2-hosted-artifact-proof.mjs prepare';
      },
      (v) => {
        v.jobs.synthetic.steps[5].env = {
          PROOF_MODE: 'hosted',
          GITHUB_TOKEN: '${{ secrets.GITHUB_TOKEN }}',
        };
      },
      (v) => {
        v.jobs.synthetic.env = { APR_CR2_H1_EMULATED_DEVELOPMENT: '1' };
      },
      (v) => {
        v.on.workflow_dispatch = {};
      },
      (v) => {
        v.jobs.synthetic.steps.push({ uses: 'arbitrary/artifact-uploader@main' });
      },
    ]) {
      const changed = structuredClone(value);
      mutate(changed);
      expect(() => validateCr2CredentialFreeWorkflow(changed)).toThrow();
    }
    expect(Object.keys(value.on).sort()).toEqual(['pull_request', 'push']);
    expect(value.permissions).toEqual({ contents: 'read' });
    expect(text).not.toMatch(
      /secrets\.|actions: write|id-token|workflow_dispatch|PROOF_MODE: hosted/u,
    );
    expect(text).toContain('node scripts/cr2-hosted-artifact-rehearse.mjs');
    const action = parse(await readFile('.github/actions/agentic-pr-review/action.yml', 'utf8'));
    expect(Object.keys(action.inputs).length).toBe(7);
    expect(Object.keys(action.outputs).length).toBe(14);
    expect(JSON.stringify(action.inputs)).not.toContain('fixture');
  });
});
