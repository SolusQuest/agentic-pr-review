import { execFileSync } from 'node:child_process';
import { mkdir, writeFile, appendFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { admitOutput } from './build-payload.mjs';
import {
  LIMITS,
  canonicalJson,
  readBoundedFile,
  sha256,
  validateReleaseVersion,
} from './build-payload.format.mjs';
import {
  CAPS,
  REPOSITORY,
  REPOSITORY_ID,
  WORKFLOW,
  createCandidate,
  decimal,
  githubReader,
  hex,
  humanSummary,
  inspectCandidate,
  parseCanonical,
  requireThat,
  validateProducer,
  validateStorage,
  verifyStorage,
} from './candidate.mjs';

function git(repo, args) {
  try {
    return execFileSync('git', args, {
      cwd: repo,
      timeout: 30000,
      maxBuffer: 1024 * 1024,
      stdio: ['ignore', 'pipe', 'pipe'],
    })
      .toString('utf8')
      .trim();
  } catch {
    throw new Error('source_admission_failed');
  }
}
export function admitWorkflow(env, controlRepo) {
  requireThat(
    env.GITHUB_EVENT_NAME === 'workflow_dispatch' &&
      env.GITHUB_REPOSITORY === REPOSITORY &&
      env.GITHUB_REPOSITORY_ID === REPOSITORY_ID &&
      env.GITHUB_REF === 'refs/heads/main' &&
      env.GITHUB_WORKFLOW_REF === `${REPOSITORY}/${WORKFLOW}@refs/heads/main` &&
      hex(env.GITHUB_WORKFLOW_SHA, 40) &&
      env.GITHUB_SHA === env.GITHUB_WORKFLOW_SHA &&
      env.RUNNER_ENVIRONMENT === 'github-hosted' &&
      env.RUNNER_OS === 'Linux' &&
      env.RUNNER_ARCH === 'X64',
    'prepare_workflow_denied',
  );
  requireThat(
    git(controlRepo, ['rev-parse', 'HEAD']) === env.GITHUB_WORKFLOW_SHA,
    'workflow_checkout_drift',
  );
  const producer = {
    repository: REPOSITORY,
    repositoryId: REPOSITORY_ID,
    workflowPath: WORKFLOW,
    workflowCommit: env.GITHUB_WORKFLOW_SHA,
    runId: decimal(env.GITHUB_RUN_ID),
    runAttempt: decimal(env.GITHUB_RUN_ATTEMPT),
    runner: 'github-hosted-ubuntu-24.04-x64',
  };
  validateProducer(producer);
  return producer;
}
export function admitPreparation(controlRepo, sourceCommit, releaseVersion, env = process.env) {
  const producer = admitWorkflow(env, controlRepo);
  requireThat(hex(sourceCommit, 40), 'invalid_source_commit');
  validateReleaseVersion(releaseVersion);
  git(controlRepo, ['merge-base', '--is-ancestor', sourceCommit, producer.workflowCommit]);
  const sourceTree = git(controlRepo, ['rev-parse', sourceCommit + '^{tree}']);
  requireThat(hex(sourceTree, 40), 'invalid_source_tree');
  return { producer, sourceTree };
}
export async function buildOriginalPayload({ repo, sourceCommit, releaseVersion, output }) {
  // D1's standalone diagnostic stdout includes a local archive path. Retain its
  // original bytes/proof unchanged, but expose only the P1 bounded digest outputs.
  try {
    execFileSync(
      process.execPath,
      [
        fileURLToPath(new URL('./build-payload.mjs', import.meta.url)),
        'build',
        '--source',
        sourceCommit,
        '--version',
        releaseVersion,
        '--output',
        output,
      ],
      {
        cwd: repo,
        stdio: ['ignore', 'ignore', 'pipe'],
        timeout: 1200000,
        maxBuffer: 1024 * 1024,
      },
    );
  } catch {
    throw new Error('original_package_build_failed');
  }
  const receipt = JSON.parse(await readBoundedFile(join(output, 'receipt.json'), CAPS.metadata));
  return receipt;
}
async function outputs(values, env) {
  if (!env.GITHUB_OUTPUT) return;
  const text = Object.entries(values)
    .map(([key, value]) => {
      requireThat(
        /^[a-z_]+$/.test(key) &&
          typeof value === 'string' &&
          /^[a-zA-Z0-9.+_-]+$/.test(value) &&
          value.length <= 160,
        'invalid_output',
      );
      return key + '=' + value + '\n';
    })
    .join('');
  await appendFile(env.GITHUB_OUTPUT, text);
}
export async function writeCandidate(
  record,
  output,
  repo = dirname(fileURLToPath(import.meta.url)),
) {
  output = admitOutput(repo, output);
  const summary = humanSummary(record);
  // All validation precedes this exclusive visibility boundary. Partial outputs
  // remain unusable and are never removed/replaced to manufacture a retry.
  await mkdir(output, { mode: 0o700 });
  for (const [name, bytes] of Object.entries({
    'candidate.json': record.bytes,
    'proposed-action-map.json': record.mapBytes,
    'candidate.sha256': record.candidateSha256 + '\n',
    'proposed-action-map.sha256': record.mapSha256 + '\n',
    'summary.txt': summary,
  })) {
    await writeFile(join(output, name), bytes, { flag: 'wx', mode: 0o600 });
  }
  requireThat(
    sha256(await readBoundedFile(join(output, 'candidate.json'), CAPS.metadata)) ===
      record.candidateSha256 &&
      sha256(await readBoundedFile(join(output, 'proposed-action-map.json'), CAPS.map)) ===
        record.mapSha256,
    'candidate_write_readback_failed',
  );
  return record;
}
export async function recordPreparedCandidate({
  archivePath,
  receiptPath,
  producer,
  storage,
  expected,
  output,
  repo,
}) {
  requireThat(hex(expected.receiptSha256, 64), 'invalid_expectation');
  const receiptBytes = await readBoundedFile(receiptPath, CAPS.metadata);
  requireThat(sha256(receiptBytes) === expected.receiptSha256, 'receipt_digest_drift');
  let receipt;
  try {
    receipt = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(receiptBytes));
  } catch {
    throw new Error('invalid_receipt_json');
  }
  const archive = await readBoundedFile(archivePath, LIMITS.archive);
  const record = createCandidate(archive, receipt, producer, storage, expected);
  return writeCandidate(record, output, repo);
}
function argumentsFor(args, required) {
  requireThat(args.length === required.length * 2, 'invalid_arguments');
  const result = {};
  for (let i = 0; i < args.length; i += 2) {
    requireThat(
      required.includes(args[i]) && !Object.hasOwn(result, args[i]) && args[i + 1],
      'invalid_arguments',
    );
    result[args[i]] = args[i + 1];
  }
  return result;
}
async function localInspect(args) {
  const p = argumentsFor(args, ['--archive', '--candidate', '--map', '--sha256']);
  const archive = await readBoundedFile(p['--archive'], LIMITS.archive);
  const bytes = await readBoundedFile(p['--candidate'], CAPS.metadata);
  const map = await readBoundedFile(p['--map'], CAPS.map);
  return inspectCandidate(archive, bytes, map, p['--sha256']);
}
export async function main(argv = process.argv.slice(2), env = process.env) {
  const [command, ...args] = argv;
  const controlRepo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
  if (command === 'admit') {
    const p = argumentsFor(args, ['--source', '--version']);
    admitPreparation(controlRepo, p['--source'], p['--version'], env);
    return;
  }
  if (command === 'build') {
    const p = argumentsFor(args, ['--source', '--version', '--repo', '--output']);
    const { sourceTree } = admitPreparation(controlRepo, p['--source'], p['--version'], env);
    const receipt = await buildOriginalPayload({
      repo: p['--repo'],
      sourceCommit: p['--source'],
      releaseVersion: p['--version'],
      output: p['--output'],
    });
    requireThat(receipt.identity.sourceTree === sourceTree, 'source_tree_drift');
    await outputs(
      {
        archive_name: receipt.archiveName,
        archive_sha256: receipt.archiveSha256,
        receipt_sha256: sha256(
          await readBoundedFile(join(p['--output'], 'receipt.json'), CAPS.metadata),
        ),
      },
      env,
    );
    return;
  }
  if (command === 'create') {
    const p = argumentsFor(args, [
      '--archive',
      '--receipt',
      '--producer',
      '--storage',
      '--source',
      '--tree',
      '--version',
      '--archive-sha256',
      '--receipt-sha256',
      '--output',
    ]);
    const producer = parseCanonical(
      await readBoundedFile(p['--producer'], CAPS.metadata),
      CAPS.metadata,
    );
    const storage = parseCanonical(
      await readBoundedFile(p['--storage'], CAPS.metadata),
      CAPS.metadata,
    );
    const record = await recordPreparedCandidate({
      archivePath: p['--archive'],
      receiptPath: p['--receipt'],
      producer,
      storage,
      expected: {
        sourceCommit: p['--source'],
        sourceTree: p['--tree'],
        releaseVersion: p['--version'],
        archiveSha256: p['--archive-sha256'],
        receiptSha256: p['--receipt-sha256'],
      },
      output: p['--output'],
      repo: controlRepo,
    });
    process.stdout.write(
      canonicalJson({
        candidateSha256: record.candidateSha256,
        proposedMapSha256: record.mapSha256,
        contentVerified: true,
        storageVerified: false,
      }) + '\n',
    );
    return;
  }
  if (command === 'workflow-record') {
    const p = argumentsFor(args, [
      '--source',
      '--version',
      '--archive',
      '--receipt',
      '--archive-sha256',
      '--receipt-sha256',
      '--artifact-id',
      '--artifact-sha256',
      '--output',
    ]);
    const { producer, sourceTree } = admitPreparation(
      controlRepo,
      p['--source'],
      p['--version'],
      env,
    );
    const storage = await verifyStorage(
      producer,
      { artifactId: decimal(p['--artifact-id']), sha256: p['--artifact-sha256'] },
      githubReader(env.GITHUB_TOKEN),
      { currentProducer: producer },
    );
    const record = await recordPreparedCandidate({
      archivePath: p['--archive'],
      receiptPath: p['--receipt'],
      producer,
      storage,
      expected: {
        sourceCommit: p['--source'],
        sourceTree,
        releaseVersion: p['--version'],
        archiveSha256: p['--archive-sha256'],
        receiptSha256: p['--receipt-sha256'],
      },
      output: p['--output'],
      repo: controlRepo,
    });
    await outputs({ candidate_sha256: record.candidateSha256, map_sha256: record.mapSha256 }, env);
    if (env.GITHUB_STEP_SUMMARY) await appendFile(env.GITHUB_STEP_SUMMARY, humanSummary(record));
    return;
  }
  if (command === 'inspect') {
    await localInspect(args);
    process.stdout.write('{"contentVerified":true,"storageVerified":false}\n');
    return;
  }
  if (command === 'storage') {
    const p = argumentsFor(args, [
      '--archive',
      '--candidate',
      '--map',
      '--sha256',
      '--metadata-id',
      '--metadata-sha256',
    ]);
    const c = await localInspect([
      '--archive',
      p['--archive'],
      '--candidate',
      p['--candidate'],
      '--map',
      p['--map'],
      '--sha256',
      p['--sha256'],
    ]);
    const get = githubReader(env.GITHUB_TOKEN);
    await verifyStorage(c.producer, c.storage, get);
    await verifyStorage(
      c.producer,
      { artifactId: decimal(p['--metadata-id']), sha256: p['--metadata-sha256'] },
      get,
      { role: 'metadata' },
    );
    process.stdout.write('{"contentVerified":true,"storageVerified":true}\n');
    return;
  }
  if (command === 'locator') {
    const p = argumentsFor(args, ['--candidate', '--sha256', '--metadata-id', '--metadata-sha256']);
    const producer = admitWorkflow(env, controlRepo);
    const bytes = await readBoundedFile(p['--candidate'], CAPS.metadata);
    requireThat(
      hex(p['--sha256'], 64) && sha256(bytes) === p['--sha256'] && hex(p['--metadata-sha256'], 64),
      'locator_digest_drift',
    );
    const c = parseCanonical(bytes, CAPS.metadata);
    requireThat(canonicalJson(c.producer) === canonicalJson(producer), 'locator_producer_drift');
    validateStorage(c.storage, producer);
    const locator = {
      formatVersion: 1,
      repository: REPOSITORY,
      workflowCommit: producer.workflowCommit,
      runId: producer.runId,
      runAttempt: producer.runAttempt,
      candidateSha256: p['--sha256'],
      metadataArtifactId: decimal(p['--metadata-id']),
      metadataArtifactSha256: p['--metadata-sha256'],
      payloadArtifactId: c.storage.artifactId,
      payloadArtifactSha256: c.storage.sha256,
    };
    const text = canonicalJson(locator);
    requireThat(Buffer.byteLength(text) <= CAPS.summary, 'locator_too_large');
    if (env.GITHUB_STEP_SUMMARY)
      await appendFile(
        env.GITHUB_STEP_SUMMARY,
        '\nExternal storage locator (run must finish successfully before handoff):\n\n```json\n' +
          text +
          '\n```\n',
      );
    process.stdout.write(text + '\n');
    return;
  }
  throw new Error('invalid_arguments');
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    // No paths, HTTP bodies, tokens, environment or arbitrary exceptions escape.
    const code =
      error instanceof Error && /^[a-z][a-z0-9_]{0,79}$/.test(error.message)
        ? error.message
        : 'prepare_candidate_failed';
    process.stderr.write(code + '\n');
    process.exitCode = 1;
  });
}
