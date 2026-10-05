import {
  ACTION_OUTPUT_NAMES,
  FIXED_WRAPPER_FAILURE_OUTPUTS,
  projectCompletionOutputs,
} from './action-wrapper/presentation/outputs.js';
import {
  renderStepSummary,
  type ActionHostCompletionDocument,
} from './action-wrapper/presentation/completion.js';
import { zeroAccounting } from './action-wrapper/presentation/accounting-fixtures.js';
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
import { parse, stringify } from 'yaml';

const repoRoot = path.resolve('.');
const guardRelativePath = 'scripts/check-action-distribution.mjs';
const actionRootRelativePath = '.github/actions/agentic-pr-review';
const bundleRelativePath = `${actionRootRelativePath}/dist/index.js`;
const artifactBudgetReceiptPrefix = 'APR_R4_E2P_ARTIFACT_REST_BUDGET ';
const finalGitHubBudgetReceipt =
  'APR_R4_E2P_GITHUB_REQUEST_BUDGET {"authenticated_rest_requests":0,"authenticated_rest_limit":256,"anonymous_codeload_requests":0,"anonymous_codeload_limit":1,"rejected_requests":0,"measurement_only":false,"invalid_remaining_header":false,"terminal_rate_limited":false,"low_remaining_guard":false,"remaining_tail_reserve":80,"host_head_source_rest":{"raw":0,"primary":0,"not_modified":0,"secondary_points":0,"permission":0,"primary_rate_limited":0,"secondary_rate_limited":0,"combined_rate_limited":0,"invalid_rate_headers":0,"remaining_tail_required":0},"host_other_github_rest":{"raw":0,"primary":0,"not_modified":0,"secondary_points":0,"permission":0,"primary_rate_limited":0,"secondary_rate_limited":0,"combined_rate_limited":0,"invalid_rate_headers":0,"remaining_tail_required":0}}\n';
const finalControlBudgetReceipt =
  'APR_R4_E2P_CONTROL_REQUEST_BUDGET {"consumed":0,"limit":64,"primary":0,"not_modified":0,"secondary_points":0,"mutation_count":0,"remaining_tail_required":0,"remaining_tail_reserve":80,"permission_denied":0,"primary_rate_limited":0,"secondary_rate_limited":0,"combined_rate_limited":0,"invalid_remaining_header":false,"measurement_only":false,"rate_limited":false}\n';
const temporaryPaths: string[] = [];

afterEach(async () => {
  await Promise.all(
    temporaryPaths
      .splice(0)
      .map((temporaryPath) => rm(temporaryPath, { recursive: true, force: true })),
  );
});

describe('R7 Action distribution and retained private proof', () => {
  it('keeps the protected local consumer on an exact-source private proof overlay', async () => {
    const preparation = await readFile(
      'runtime/scripts/prepare-r4-trusted-proof-payload-current.sh',
      'utf8',
    );
    const generation = preparation.indexOf('npm ci --prefix "$control_root" --ignore-scripts');
    expect(generation).toBeGreaterThan(preparation.indexOf('fail source-tree'));
    expect(generation).toBeGreaterThan(preparation.indexOf('fail output-parent'));
    expect(generation).toBeGreaterThan(preparation.indexOf('ambient-credential-'));
    expect(preparation).toContain(
      "path.join(root, '.github/actions/agentic-pr-review/dist/index.js')",
    );
    expect(preparation).toContain('await writeTrustedProofActionBundle(output, root)');
    expect(preparation).toContain(
      'installed.equals((await generateTrustedProofActionBundle(root)).bytes)',
    );
    expect(preparation).toContain('"$(wc -l < "$output_lines")" -eq 7');
    const workflow = await readFile('.github/workflows/r4-trusted-proof.yml', 'utf8');
    expect(
      workflow.match(/uses: \.\/control-root\/\.github\/actions\/agentic-pr-review/gu),
    ).toHaveLength(2);
    expect(
      workflow.match(
        /bash control-root\/runtime\/scripts\/prepare-r4-trusted-proof-payload-current.sh/gu,
      ),
    ).toHaveLength(2);
  });
  it('declares the exact H1 inputs and fourteen H4 outputs', async () => {
    const metadata = parse(
      await readFile(path.join(repoRoot, actionRootRelativePath, 'action.yml'), 'utf8'),
    ) as Record<string, unknown>;
    const inputs = metadata.inputs as Record<string, Record<string, unknown>>;

    expect(Object.keys(metadata).sort()).toEqual([
      'description',
      'inputs',
      'name',
      'outputs',
      'runs',
    ]);
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
    expect(Object.keys(metadata.outputs as object).sort()).toEqual([...ACTION_OUTPUT_NAMES].sort());
    expect(metadata.runs).toEqual({ using: 'node24', main: 'dist/index.js' });
  });

  it('checks the generated source identity marker in the committed bundle', async () => {
    const bundle = await readFile(path.join(repoRoot, bundleRelativePath), 'utf8');

    expect(bundle).toContain('WRAPPER_BUILD_DISCRIMINATOR = "r7-d0"');
    expect(bundle).not.toContain('process.env.AGENTIC_PR_REVIEW_PREPARED_ROOT');
    expect(bundle).toMatch(/\/\/ Action source inventory sha256: [0-9a-f]{64}\n$/u);
    expect(bundle).not.toContain('require("supports-color")');
  });

  it('generates a fixed non-production framework fixture wrapper', async () => {
    const buildModule = (await import(
      pathToFileURL(path.join(repoRoot, 'scripts/build-action.mjs')).href
    )) as {
      generateActionBundle(root: string): Promise<{ bytes: Buffer }>;
      generateFrameworkFixtureActionBundle(root: string): Promise<{ bytes: Buffer }>;
    };

    const [production, frameworkFixture] = await Promise.all([
      buildModule.generateActionBundle(repoRoot),
      buildModule.generateFrameworkFixtureActionBundle(repoRoot),
    ]);

    expect(production.bytes.toString('utf8')).toContain('WRAPPER_BUILD_DISCRIMINATOR = "r7-d0"');
    expect(frameworkFixture.bytes.toString('utf8')).toContain(
      'WRAPPER_BUILD_DISCRIMINATOR = "r4-h1"',
    );
    expect(frameworkFixture.bytes).not.toEqual(production.bytes);
  }, 15_000);

  it('changes the source inventory identity for map, wrapper or build-script drift', async () => {
    const fixture = await temporaryDirectory('apr-action-source-identity-');
    await mkdir(path.join(fixture, 'scripts'), { recursive: true });
    await mkdir(path.join(fixture, 'src/action-wrapper'), { recursive: true });
    await mkdir(path.join(fixture, actionRootRelativePath), { recursive: true });
    await writeFile(path.join(fixture, actionRootRelativePath, 'payload-map.json'), 'null\n');
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
    await writeFile(path.join(fixture, actionRootRelativePath, 'payload-map.json'), 'null \n');
    const mapDrift = await buildModule.actionSourceDigest(fixture, metafile);
    await writeFile(sourcePath, 'export const probe = 2;\n');
    const sourceDrift = await buildModule.actionSourceDigest(fixture, metafile);
    await writeFile(buildPath, `${await readFile(buildPath, 'utf8')}\n// build drift\n`);
    const buildDrift = await buildModule.actionSourceDigest(fixture, metafile);

    expect(mapDrift).not.toBe(original);
    expect(sourceDrift).not.toBe(mapDrift);
    expect(buildDrift).not.toBe(sourceDrift);
  });

  it('detects metadata, dependency, inventory, and retired-surface drift', async () => {
    const fixture = await createDistributionFixture();
    const metadataPath = path.join(fixture, actionRootRelativePath, 'action.yml');
    const packagePath = path.join(fixture, 'package.json');
    await writeFile(metadataPath, `${await readFile(metadataPath, 'utf8')}extra: true\n`);
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

  it.each([
    'missing-root',
    'missing-name',
    'extra-name',
    'entry-scalar',
    'entry-value',
    'description-type',
    'description-multiline',
    'description-empty',
    'duplicate',
  ])('rejects H4 metadata %s drift', async (mutation) => {
    const fixture = await createDistributionFixture();
    const metadataPath = path.join(fixture, actionRootRelativePath, 'action.yml');
    const metadata = parse(await readFile(metadataPath, 'utf8')) as {
      outputs: Record<string, unknown>;
    };
    if (mutation === 'missing-root') delete (metadata as Partial<typeof metadata>).outputs;
    if (mutation === 'missing-name') delete metadata.outputs['provider-attempts'];
    if (mutation === 'extra-name') metadata.outputs.private = { description: 'Private' };
    if (mutation === 'entry-scalar') metadata.outputs.status = 'wrong';
    if (mutation === 'entry-value')
      metadata.outputs.status = { description: 'Status', value: '${{ secret }}' };
    if (mutation === 'description-type') metadata.outputs.status = { description: 1 };
    if (mutation === 'description-multiline')
      metadata.outputs.status = { description: 'Status\nprivate' };
    if (mutation === 'description-empty') metadata.outputs.status = { description: '' };
    let source = stringify(metadata);
    if (mutation === 'duplicate')
      source = source.replace('outputs:\n', 'outputs:\n  status:\n    description: duplicate\n');
    await writeFile(metadataPath, source);
    const guardModule = (await import(
      pathToFileURL(path.join(repoRoot, guardRelativePath)).href
    )) as {
      inspectActionDistribution(
        root: string,
        options: { testOnlySkipGeneratedBundle: true },
      ): Promise<{ kind: string }[]>;
    };
    const violations = await guardModule.inspectActionDistribution(fixture, {
      testOnlySkipGeneratedBundle: true,
    });
    expect(
      violations.some(
        (v) =>
          v.kind.startsWith('action-output-') ||
          v.kind === 'action-metadata-shape-invalid' ||
          v.kind === 'action-metadata-yaml-invalid',
      ),
    ).toBe(true);
  });

  it
    .runIf(process.platform === 'linux')
    .each([
      'retry_success',
      'final_failure_unknown_usage',
      'unknown_partition',
      'overflow_known_sum',
      'exact_int64',
      'missing_finalizer',
      'pre_provider',
      'unobserved_dispatch_usage',
    ])(
    'executes private proof bundle output projection for %s',
    async (name) => {
      const cases = JSON.parse(
        await readFile(
          'runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/Contracts/Fixtures/provider-accounting.json',
          'utf8',
        ),
      ) as { name: string; document: ActionHostCompletionDocument }[];
      const document = {
        ...cases.find((item) => item.name === name)!.document,
        build_discriminator: 'r4-w2',
      };
      const execution = await runIsolatedBundle('r4-w2', document);
      expect(execution.result.status).toBe(document.process_exit_code);
      expect(readNativeOutputs(await readFile(execution.outputPath, 'utf8'))).toEqual(
        projectCompletionOutputs(document),
      );
      expect(await readFile(execution.summaryPath, 'utf8')).toBe(renderStepSummary(document));
      const output = await readFile(execution.outputPath, 'utf8');
      expect(output).not.toContain(execution.trustedRoot);
      expect(output).not.toMatch(/synthetic-github-token|SESSION|CANARY|github.com/);
    },
    30_000,
  );

  it.runIf(process.platform === 'linux')(
    'runs the checked ESM bundle in its real package scope without node_modules and reaches the lazy executor locally',
    async () => {
      const execution = await runIsolatedBundle('r4-w2');

      expect(execution.result.status).toBe(0);
      const accountingSummary = await readFile(execution.summaryPath, 'utf8');
      expect(readNativeOutputs(await readFile(execution.outputPath, 'utf8'))).toEqual({
        status: 'skipped_untrusted_event',
        'termination-reason': 'not_started',
        ...Object.fromEntries(
          ACTION_OUTPUT_NAMES.filter(
            (name) => name !== 'status' && name !== 'termination-reason',
          ).map((name) => [name, name.endsWith('completeness') ? 'complete' : '0']),
        ),
      });
      expect(accountingSummary).toContain('| Review termination | not_started |');
      expect(accountingSummary).toContain('| Attempt accounting completeness | complete |');
      expect(accountingSummary).toContain('| Usage completeness | complete |');
      expect(accountingSummary).toContain('| Failed provider attempts | 0 |');
      expect(await readFile(execution.markerPath, 'utf8')).toBe('executor-reached\n');
      expect(await readFile(execution.summaryPath, 'utf8')).toContain('skipped_untrusted_event');
      const receiptLines = execution.result.stderr.trimEnd().split('\n');
      expect(receiptLines.slice(0, 2)).toEqual([
        finalGitHubBudgetReceipt.trimEnd(),
        finalControlBudgetReceipt.trimEnd(),
      ]);
      expect(receiptLines).toHaveLength(3);
      expect(receiptLines[2]).toMatch(new RegExp(`^${artifactBudgetReceiptPrefix}`, 'u'));
      expect(JSON.parse(receiptLines[2]!.slice(artifactBudgetReceiptPrefix.length))).toEqual({
        kind: 'apr-r4-trusted-proof-artifact-rest-budget-v2',
        protected_route: true,
        maximum_total_authenticated_api_requests: 4_096,
        total_authenticated_api_requests: 0,
        maximum_primary_rate_limit_requests: 256,
        primary_rate_limit_requests: 0,
        conditional_not_modified_requests: 0,
        secondary_limit_points: 0,
        permission_denied: 0,
        remaining_total_authenticated_api_requests: 4_096,
        remaining_primary_rate_limit_requests: 256,
        disposition: 'active',
        repository: 'owner/repository',
        repository_id: '123',
        workflow_sha: 'b'.repeat(40),
        action_source_sha: 'a'.repeat(40),
        payload_sha256: execution.payloadSha256,
        build_discriminator: 'r4-w2',
        run_id: '456',
        run_attempt: '1',
        cap_profile: 'apr-r4-artifact-rest-request-budget-v2',
        measurement_only: false,
        remaining_tail_required: 0,
        remaining_tail_reserve: 80,
      });
    },
    30_000,
  );

  it.runIf(process.platform === 'linux')(
    'still attempts fixed summary and error when the native output channel is missing',
    async () => {
      const execution = await runIsolatedBundle('r4-w2-mismatch', undefined, true);
      expect(execution.result.status).toBe(1);
      await expect(access(execution.outputPath)).rejects.toThrow();
      expect(await readFile(execution.summaryPath, 'utf8')).toContain('failed safely');
      expect(execution.result.stdout).toContain('The private review wrapper failed.');
      expect(execution.result.stdout).not.toContain(execution.outputPath);
    },
    30_000,
  );

  it.runIf(process.platform === 'linux')(
    'rejects a payload build mismatch before spawning the prepared executable',
    async () => {
      const execution = await runIsolatedBundle('r4-w2-mismatch');

      expect(execution.result.status).toBe(1);
      expect(readNativeOutputs(await readFile(execution.outputPath, 'utf8'))).toEqual(
        FIXED_WRAPPER_FAILURE_OUTPUTS,
      );
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

async function runIsolatedBundle(
  buildDiscriminator: string,
  completion?: ActionHostCompletionDocument,
  missingOutputChannel = false,
) {
  const trustedRoot = await temporaryDirectory('apr-isolated-action-');
  const bundlePath = path.join(trustedRoot, 'index.js');
  const hostPath = path.join(trustedRoot, 'synthetic-host.cjs');
  const markerPath = path.join(trustedRoot, 'executor-marker.txt');
  const summaryPath = path.join(trustedRoot, 'step-summary.md');
  const outputPath = path.join(trustedRoot, 'outputs.txt');
  const eventPath = path.join(trustedRoot, 'event.json');
  const buildModule = await import(
    pathToFileURL(path.join(repoRoot, 'scripts/build-action.mjs')).href
  );
  await buildModule.writeTrustedProofActionBundle(bundlePath, repoRoot);
  await writeFile(path.join(trustedRoot, 'package.json'), '{"type":"module"}\n');
  await writeFile(eventPath, '{}\n');
  await writeFile(summaryPath, '');
  if (!missingOutputChannel) await writeFile(outputPath, '');
  await writeFile(hostPath, syntheticHostSource(markerPath, completion));
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
    AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE: 'final-bootstrap',
    GITHUB_EVENT_PATH: eventPath,
    GITHUB_REPOSITORY: 'owner/repository',
    GITHUB_REPOSITORY_ID: '123',
    GITHUB_RUN_ID: '456',
    GITHUB_RUN_ATTEMPT: '1',
    GITHUB_WORKFLOW_REF: 'owner/repository/.github/workflows/review.yml@refs/heads/main',
    GITHUB_WORKFLOW_SHA: 'b'.repeat(40),
    GITHUB_STEP_SUMMARY: summaryPath,
    GITHUB_OUTPUT: outputPath,
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
  return { result, markerPath, summaryPath, outputPath, payloadSha256, trustedRoot };
}

function syntheticHostSource(markerPath: string, supplied?: ActionHostCompletionDocument) {
  return `#!${process.execPath}
const fs = require('node:fs');
const net = require('node:net');
const path = require('node:path');
const chunks = [];
process.stdin.on('data', (chunk) => chunks.push(chunk));
process.stdin.on('end', async () => {
  try {
    const frame = Buffer.concat(chunks);
    const length = frame.readUInt32BE(0);
    const launch = JSON.parse(frame.subarray(4, 4 + length).toString('utf8'));
    fs.writeFileSync(path.join(process.env.TMPDIR, 'artifact-staging', 'proof.bin'), 'proof');
    const result = await exchange(launch.artifact_bridge_endpoint, {
      build_discriminator: launch.build_discriminator,
      payload: {
        operation: 'upload_immutable',
        correlation_id: 'isolated-proof',
        name: 'proof',
        source_relative_path: 'proof.bin',
        encrypted_object_digest: '0'.repeat(64),
        minimum_expires_at_unix_seconds: '4102444800'
      }
    });
    if (result.payload.failure !== 'invalid' || result.payload.mutation_state !== 'not_committed') {
      throw new Error('lazy executor was not reached safely');
    }
    fs.writeFileSync(${JSON.stringify(markerPath)}, 'executor-reached\\n');
    const completion = ${JSON.stringify(
      supplied ?? {
        build_discriminator: 'r4-w2',
        status: 'skipped_untrusted_event',
        exit_class: 'success',
        process_exit_code: 0,
        summary: {
          reviewed_sha: null,
          publication_url: null,
          finding_count: null,
          state_disposition: 'not_accessed',
        },
        annotations: [],
        accounting: zeroAccounting,
        termination_reason: 'not_started',
      },
    )};
    completion.build_discriminator = launch.build_discriminator;
    process.exitCode = completion.process_exit_code;
    process.stderr.write(${JSON.stringify(finalGitHubBudgetReceipt + finalControlBudgetReceipt)});
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

function readNativeOutputs(source: string): Record<string, string> {
  const lines = source.replaceAll('\r\n', '\n').split('\n');
  expect(lines.pop()).toBe('');
  expect(lines.length % 3).toBe(0);
  const outputs: Record<string, string> = {};
  for (let i = 0; i < lines.length; i += 3) {
    const header = /^([a-z-]+)<<(ghadelimiter_[0-9a-f-]{36})$/u.exec(lines[i]!);
    expect(header).not.toBeNull();
    const [, name, delimiter] = header!;
    expect(ACTION_OUTPUT_NAMES).toContain(name);
    expect(outputs).not.toHaveProperty(name!);
    expect(lines[i + 2]).toBe(delimiter);
    outputs[name!] = lines[i + 1]!;
  }
  return outputs;
}
