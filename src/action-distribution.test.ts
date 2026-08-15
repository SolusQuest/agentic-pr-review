import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import {
  access,
  chmod,
  copyFile,
  cp,
  mkdir,
  mkdtemp,
  readFile,
  rm,
  symlink,
  writeFile,
} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import { afterEach, describe, expect, it } from 'vitest';
import { parse } from 'yaml';

const repoRoot = path.resolve('.');
const guardRelativePath = 'scripts/check-action-distribution.mjs';
const actionRootRelativePath = '.github/actions/agentic-pr-review';
const bundleRelativePath = `${actionRootRelativePath}/dist/index.js`;
const temporaryPaths: string[] = [];

afterEach(async () => {
  await Promise.all(
    temporaryPaths
      .splice(0)
      .map((temporaryPath) => rm(temporaryPath, { recursive: true, force: true })),
  );
});

describe('R4 Action distribution', () => {
  it('declares exactly the H1 metadata surface with no outputs', async () => {
    const metadata = parse(
      await readFile(path.join(repoRoot, actionRootRelativePath, 'action.yml'), 'utf8'),
    ) as Record<string, unknown>;
    const inputs = metadata.inputs as Record<string, Record<string, unknown>>;

    expect(Object.keys(metadata).sort()).toEqual(['description', 'inputs', 'name', 'runs']);
    expect(Object.keys(inputs).sort()).toEqual([
      'config-path',
      'github-token',
      'pr-number',
      'previous-state-key',
      'provider-api-key',
      'state-key',
      'state-mode',
    ]);
    expect(Object.values(inputs).every((input) => input.required === false)).toBe(true);
    expect(inputs['config-path'].default).toBe('.github/agentic-pr-review.json');
    expect(inputs['state-mode'].default).toBe('auto');
    expect(metadata).not.toHaveProperty('outputs');
    expect(metadata.runs).toEqual({ using: 'node24', main: 'dist/index.js' });
  });

  it('checks the generated source identity marker in the committed bundle', async () => {
    const bundle = await readFile(path.join(repoRoot, bundleRelativePath), 'utf8');

    expect(bundle).toContain('WRAPPER_BUILD_DISCRIMINATOR = "r4-w2"');
    expect(bundle).toMatch(/\/\/ Action source inventory sha256: [0-9a-f]{64}\n$/u);
    expect(bundle).not.toContain('require("supports-color")');
  });

  it('changes the source inventory identity for wrapper or build-script drift', async () => {
    const fixture = await temporaryDirectory('apr-action-source-identity-');
    await mkdir(path.join(fixture, 'scripts'), { recursive: true });
    await mkdir(path.join(fixture, 'src/action-wrapper'), { recursive: true });
    const buildPath = path.join(fixture, 'scripts/build-action.mjs');
    const sourcePath = path.join(fixture, 'src/action-wrapper/probe.ts');
    await copyFile(path.join(repoRoot, 'scripts/build-action.mjs'), buildPath);
    await writeFile(sourcePath, 'export const probe = 1;\n');
    const buildModule = (await import(
      pathToFileURL(path.join(repoRoot, 'scripts/build-action.mjs')).href
    )) as {
      actionSourceDigest(
        root: string,
        metafile: { inputs: Record<string, object> },
      ): Promise<string>;
    };
    const metafile = { inputs: { 'src/action-wrapper/probe.ts': {} } };
    const original = await buildModule.actionSourceDigest(fixture, metafile);
    await writeFile(sourcePath, 'export const probe = 2;\n');
    const sourceDrift = await buildModule.actionSourceDigest(fixture, metafile);
    await writeFile(buildPath, `${await readFile(buildPath, 'utf8')}\n// build drift\n`);
    const buildDrift = await buildModule.actionSourceDigest(fixture, metafile);

    expect(sourceDrift).not.toBe(original);
    expect(buildDrift).not.toBe(sourceDrift);
  });

  it('detects metadata, dependency, inventory, and retired-surface drift', async () => {
    const fixture = await createDistributionFixture();
    const metadataPath = path.join(fixture, actionRootRelativePath, 'action.yml');
    const packagePath = path.join(fixture, 'package.json');
    await writeFile(metadataPath, `${await readFile(metadataPath, 'utf8')}outputs: {}\n`);
    await writeFile(
      packagePath,
      (await readFile(packagePath, 'utf8')).replace(
        '"@actions/core": "3.0.1"',
        '"@actions/core": "3.0.0"',
      ),
    );
    await write('action.yml', 'name: retired root alias\n', fixture);
    await write('scripts/run-local-synthetic.mjs', 'retired\n', fixture);
    await write(
      '.github/workflows/review.yml',
      'steps:\n  - uses: ./.github/actions/agentic-pr-review\n',
      fixture,
    );
    await write(`${actionRootRelativePath}/extra.txt`, 'extra\n', fixture);
    const linkTarget = path.join(fixture, 'linked-target');
    await mkdir(linkTarget);
    await symlink(
      linkTarget,
      path.join(fixture, actionRootRelativePath, 'linked'),
      process.platform === 'win32' ? 'junction' : 'dir',
    );
    const guardModule = (await import(
      pathToFileURL(path.join(repoRoot, guardRelativePath)).href
    )) as {
      inspectActionDistribution(
        root: string,
        options: { testOnlySkipGeneratedBundle: true },
      ): Promise<readonly { relativePath: string; kind: string }[]>;
    };

    const violations = await guardModule.inspectActionDistribution(fixture, {
      testOnlySkipGeneratedBundle: true,
    });
    const diagnostics = violations.map(({ relativePath, kind }) => `${relativePath} (${kind})`);

    for (const expected of [
      'action-metadata-shape-invalid',
      'action-runtime-dependencies-invalid',
      'unexpected-action-file',
      'action-entry-symlink',
      'retired-local-action-invocation',
      'retired-root-action-alias',
      'retired-local-runner',
    ]) {
      expect(diagnostics.some((diagnostic) => diagnostic.includes(expected))).toBe(true);
    }
    expect(violations).toEqual(
      [...violations].sort((left, right) => {
        const pathOrder = left.relativePath.localeCompare(right.relativePath);
        return pathOrder || left.kind.localeCompare(right.kind);
      }),
    );
  });

  it.runIf(process.platform === 'linux')(
    'runs the checked CommonJS bundle without node_modules and reaches the lazy executor locally',
    async () => {
      const execution = await runIsolatedBundle('r4-w2');

      expect(execution.result.status).toBe(0);
      expect(await readFile(execution.markerPath, 'utf8')).toBe('executor-reached\n');
      expect(await readFile(execution.summaryPath, 'utf8')).toContain('skipped_untrusted_event');
      expect(execution.result.stderr).toBe('');
    },
    30_000,
  );

  it.runIf(process.platform === 'linux')(
    'rejects a payload build mismatch before spawning the prepared executable',
    async () => {
      const execution = await runIsolatedBundle('r4-w2-mismatch');

      expect(execution.result.status).toBe(1);
      await expect(access(execution.markerPath)).rejects.toThrow();
      expect(execution.result.stdout).not.toContain(execution.payloadSha256);
      expect(execution.result.stdout).not.toContain(execution.trustedRoot);
    },
    30_000,
  );
});

async function createDistributionFixture() {
  const fixture = await temporaryDirectory('apr-action-distribution-');
  await copyFile(path.join(repoRoot, 'package.json'), path.join(fixture, 'package.json'));
  await copyFile(path.join(repoRoot, 'package-lock.json'), path.join(fixture, 'package-lock.json'));
  await cp(
    path.join(repoRoot, actionRootRelativePath),
    path.join(fixture, actionRootRelativePath),
    { recursive: true },
  );
  await symlink(
    path.join(repoRoot, 'node_modules'),
    path.join(fixture, 'node_modules'),
    process.platform === 'win32' ? 'junction' : 'dir',
  );
  return fixture;
}

async function runIsolatedBundle(buildDiscriminator: string) {
  const trustedRoot = await temporaryDirectory('apr-isolated-action-');
  const bundlePath = path.join(trustedRoot, 'index.js');
  const hostPath = path.join(trustedRoot, 'synthetic-host');
  const markerPath = path.join(trustedRoot, 'executor-marker.txt');
  const summaryPath = path.join(trustedRoot, 'step-summary.md');
  const eventPath = path.join(trustedRoot, 'event.json');
  await copyFile(path.join(repoRoot, bundleRelativePath), bundlePath);
  await writeFile(eventPath, '{}\n');
  await writeFile(hostPath, syntheticHostSource(markerPath));
  await chmod(hostPath, 0o700);
  const payloadSha256 = createHash('sha256')
    .update(await readFile(hostPath))
    .digest('hex');
  const environment: NodeJS.ProcessEnv = { ...process.env };
  delete environment.NODE_PATH;
  Object.assign(environment, {
    AGENTIC_PR_REVIEW_PREPARED_ROOT: trustedRoot,
    AGENTIC_PR_REVIEW_PREPARED_EXECUTABLE: path.basename(hostPath),
    AGENTIC_PR_REVIEW_PREPARED_PAYLOAD_SHA256: payloadSha256,
    AGENTIC_PR_REVIEW_ACTION_SOURCE_SHA: 'a'.repeat(40),
    AGENTIC_PR_REVIEW_PAYLOAD_BUILD_DISCRIMINATOR: buildDiscriminator,
    GITHUB_EVENT_PATH: eventPath,
    GITHUB_REPOSITORY: 'owner/repository',
    GITHUB_REPOSITORY_ID: '123',
    GITHUB_RUN_ID: '456',
    GITHUB_RUN_ATTEMPT: '1',
    GITHUB_WORKFLOW_REF: 'owner/repository/.github/workflows/review.yml@refs/heads/main',
    GITHUB_WORKFLOW_SHA: 'b'.repeat(40),
    GITHUB_STEP_SUMMARY: summaryPath,
    'INPUT_GITHUB-TOKEN': 'synthetic-github-token',
    'INPUT_PROVIDER-API-KEY': '',
    'INPUT_STATE-KEY': '',
    'INPUT_PREVIOUS-STATE-KEY': '',
    'INPUT_CONFIG-PATH': '.github/agentic-pr-review.json',
    'INPUT_PR-NUMBER': '',
    'INPUT_STATE-MODE': 'auto',
  });
  const result = spawnSync(process.execPath, [bundlePath], {
    cwd: trustedRoot,
    encoding: 'utf8',
    env: environment,
    timeout: 20_000,
    windowsHide: true,
  });
  return { result, markerPath, summaryPath, payloadSha256, trustedRoot };
}

function syntheticHostSource(markerPath: string) {
  return `#!${process.execPath}
const fs = require('node:fs');
const net = require('node:net');
const chunks = [];
process.stdin.on('data', (chunk) => chunks.push(chunk));
process.stdin.on('end', async () => {
  try {
    const frame = Buffer.concat(chunks);
    const length = frame.readUInt32BE(0);
    const launch = JSON.parse(frame.subarray(4, 4 + length).toString('utf8'));
    const result = await exchange(launch.artifact_bridge_endpoint, {
      build_discriminator: launch.build_discriminator,
      payload: {
        operation: 'upload_immutable',
        correlation_id: 'isolated-proof',
        name: 'proof',
        source_relative_path: 'missing.bin',
        encrypted_object_digest: '0'.repeat(64),
        minimum_expires_at_unix_seconds: '4102444800'
      }
    });
    if (result.payload.failure !== 'invalid' || result.payload.mutation_state !== 'not_committed') {
      throw new Error('lazy executor was not reached safely');
    }
    fs.writeFileSync(${JSON.stringify(markerPath)}, 'executor-reached\\n');
    const completion = {
      build_discriminator: launch.build_discriminator,
      status: 'skipped_untrusted_event',
      exit_class: 'success',
      process_exit_code: 0,
      summary: {
        reviewed_sha: null,
        publication_url: null,
        finding_count: null,
        state_disposition: 'not_accessed'
      },
      annotations: []
    };
    process.stdout.write(encode(completion));
  } catch {
    process.exitCode = 1;
  }
});
function exchange(endpoint, envelope) {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection(endpoint);
    const response = [];
    socket.on('connect', () => socket.write(Buffer.concat([encode(envelope), Buffer.alloc(4)])));
    socket.on('data', (chunk) => response.push(chunk));
    socket.on('end', () => {
      try {
        const frame = Buffer.concat(response);
        const length = frame.readUInt32BE(0);
        resolve(JSON.parse(frame.subarray(4, 4 + length).toString('utf8')));
      } catch (error) {
        reject(error);
      }
    });
    socket.on('error', reject);
  });
}
function encode(value) {
  const payload = Buffer.from(JSON.stringify(value), 'utf8');
  const frame = Buffer.allocUnsafe(4 + payload.length);
  frame.writeUInt32BE(payload.length, 0);
  payload.copy(frame, 4);
  return frame;
}
`;
}

async function temporaryDirectory(prefix: string) {
  const directory = await mkdtemp(path.join(os.tmpdir(), prefix));
  temporaryPaths.push(directory);
  return directory;
}

async function write(relativePath: string, contents: string, fixture: string) {
  const absolutePath = path.join(fixture, relativePath);
  await mkdir(path.dirname(absolutePath), { recursive: true });
  await writeFile(absolutePath, contents);
}
