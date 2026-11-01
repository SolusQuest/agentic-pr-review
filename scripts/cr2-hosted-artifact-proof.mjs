// Explicit repository harness. No caller dispatches workflows or acquires credentials.
import { build } from 'esbuild';
import { execFileSync, spawn } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createService } from '../runtime/tests/ActionHostTrustedProofVerifier/HostedArtifactInterop/service.mjs';
import {
  actorId,
  admitHosted,
  admitReceipt,
  admitRestoreReceipt,
  inspectAssets,
  historicalActionFixture,
  repository,
  repositoryId,
  requireProof,
  workflow,
} from './cr2-hosted-artifact-policy.mjs';

const relative = 'runtime/tests/ActionHostTrustedProofVerifier/HostedArtifactInterop';
const runtimeUrl =
  'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.13/dotnet-runtime-10.0.13-linux-x64.tar.gz';
const runtimeBytes = 36648012;
const runtimeSha512 =
  '9574d95364110409416735e7842796f8d614c21d74cb25d204a3e4cfd11b2c05efdf19ca89b8069650a04f64add2705967dcb04b8665a36d7a3f30a5d8c05afe';
const git = (repo, ...args) => execFileSync('git', args, { cwd: repo, encoding: 'utf8' }).trim();
const syntheticRuntimeToken =
  Buffer.from('{"alg":"none","typ":"JWT"}').toString('base64url') +
  '.' +
  Buffer.from(
    JSON.stringify({
      scp: 'Actions.Results:cr2-h1-run:cr2-h1-job',
      canary: 'cr2-h1-runtime-token-canary',
    }),
  ).toString('base64url') +
  '.';

function closedEnvironment(work) {
  const result = {
    PATH: process.env.PATH,
    DOTNET_CLI_TELEMETRY_OPTOUT: '1',
    DOTNET_NOLOGO: '1',
    TMPDIR: work,
    TMP: work,
    TEMP: work,
    HOME: join(work, 'public-home'),
    DOTNET_CLI_HOME: join(work, 'dotnet-home'),
    NUGET_PACKAGES: join(work, 'public-packages'),
  };
  if (process.env.DOTNET_ROOT) result.DOTNET_ROOT = process.env.DOTNET_ROOT;
  return result;
}
async function output(name, value) {
  requireProof(!value.includes('\n'));
  if (process.env.GITHUB_OUTPUT) {
    const { appendFile } = await import('node:fs/promises');
    await appendFile(process.env.GITHUB_OUTPUT, `${name}=${value}\n`);
  }
}
function syntheticEnvironment(config) {
  return {
    GITHUB_REPOSITORY: repository,
    GITHUB_REPOSITORY_ID: repositoryId,
    GITHUB_EVENT_NAME: 'workflow_dispatch',
    GITHUB_RUN_ID: config.runId,
    GITHUB_RUN_ATTEMPT: '1',
    GITHUB_SHA: config.source,
    GITHUB_WORKFLOW_SHA: config.source,
    GITHUB_WORKFLOW_REF: `${repository}/${workflow}@refs/heads/main`,
    GITHUB_EVENT_PATH: join(config.work, `event-${config.phase}.json`),
  };
}
async function eventFile(config) {
  config.syntheticEnvironment = syntheticEnvironment(config);
  await writeFile(
    config.syntheticEnvironment.GITHUB_EVENT_PATH,
    JSON.stringify({
      repository: { id: Number(repositoryId), full_name: repository },
      sender: { id: Number(actorId), login: 'Yuee98' },
    }),
    { mode: 0o600 },
  );
}
async function companionConfig(config) {
  config.companionConfig = join(config.work, `companion-${config.phase}.json`);
  const value = {};
  for (const key of [
    'mode',
    'phase',
    'source',
    'actorId',
    'runId',
    'runA',
    'origin',
    'receiptIn',
    'receiptOut',
    'faults',
  ])
    if (config[key] !== undefined) value[key] = config[key];
  await writeFile(config.companionConfig, JSON.stringify(value), { mode: 0o600 });
}

export async function prepare({
  mode,
  phase,
  repo = process.cwd(),
  receipt,
  workRoot,
  faults,
  existingRecords = 0,
}) {
  requireProof(['hosted', 'rehearsal'].includes(mode) && ['produce', 'restore'].includes(phase));
  requireProof(process.platform === 'linux' && process.arch === 'x64');
  const source = git(repo, 'rev-parse', 'HEAD');
  if (mode === 'hosted') {
    admitHosted(process.env, {
      source: process.env.EXPECTED_HEAD,
      phase,
      approval: process.env.PROOF_APPROVAL,
    });
    requireProof(
      source === process.env.EXPECTED_HEAD &&
        git(repo, 'status', '--porcelain', '--untracked-files=all') === '',
    );
  }
  const work = await mkdtemp(join(workRoot ?? process.env.RUNNER_TEMP ?? '/tmp', 'apr-cr2-h1-'));
  const runId = mode === 'hosted' ? process.env.GITHUB_RUN_ID : '7001';
  let runA = runId;
  if (phase === 'restore') {
    const candidate = admitReceipt(receipt, source, runId);
    requireProof(candidate.mode === mode);
    runA = candidate.runId;
    await writeFile(join(work, 'producer.json'), JSON.stringify(candidate), { mode: 0o600 });
  }
  const historicalActionRoot =
    mode === 'rehearsal'
      ? historicalActionFixture(repo, join(work, 'historical-action'))
      : undefined;
  const checkedMapSha256 = createHash('sha256')
    .update(await readFile(join(repo, '.github/actions/agentic-pr-review/payload-map.json')))
    .digest('hex');
  const config = {
    mode,
    historicalActionRoot,
    checkedMapSha256,
    phase,
    repo,
    work,
    source,
    workingTree: git(repo, 'status', '--porcelain', '--untracked-files=all') !== '',
    actorId,
    runId,
    runA,
    assets:
      mode === 'rehearsal'
        ? inspectAssets(repo, undefined, historicalActionRoot)
        : inspectAssets(repo),
    runtimeUrl,
    runtimeArchive: join(work, 'runtime.tar.gz'),
    runtimeToken: mode === 'rehearsal' ? syntheticRuntimeToken : undefined,
    existingRecords,
    faults,
    stopNonce: randomUUID(),
    createdAt: Date.now() - 1000,
    receiptOut: join(work, `receipt-${phase}.json`),
    receiptIn: join(work, 'producer.json'),
    companion: join(work, 'companion/AgenticPrReview.Runtime.ActionHostTrustedProofVerifier'),
  };
  const env = closedEnvironment(work);
  for (const directory of [env.HOME, env.DOTNET_CLI_HOME, env.NUGET_PACKAGES])
    await mkdir(directory, { mode: 0o700 });
  const common = [
    '-c',
    'Release',
    '-r',
    'linux-x64',
    '--nologo',
    '--self-contained',
    'false',
    '-p:UseAppHost=false',
    '-p:RuntimeFrameworkVersion=10.0.9',
    '-p:APRManagedPackage=false',
    '-p:RestoreSources=https://api.nuget.org/v3/index.json',
    '-p:NuGetAudit=false',
  ];
  for (const [project, directory] of [
    ['runtime/src/AgenticPrReview.Runtime/AgenticPrReview.Runtime.csproj', 'payload'],
    [relative + '/HostedArtifactInterop.csproj', 'companion'],
  ])
    execFileSync('dotnet', ['publish', project, ...common, '-o', join(work, directory)], {
      cwd: repo,
      env,
      stdio: 'inherit',
      timeout: 180000,
    });
  console.log('CR2_H1_PREPARATION publish_complete');
  const packageEntry = join(work, 'package.mjs');
  await build({
    entryPoints: [join(repo, relative, 'package.ts')],
    outfile: packageEntry,
    bundle: true,
    platform: 'node',
    format: 'esm',
    target: 'node24',
    banner: {
      js: "import {createRequire} from 'node:module'; const require=createRequire(import.meta.url);",
    },
  });
  execFileSync(process.execPath, [packageEntry, repo, join(work, 'payload'), work, source], {
    cwd: repo,
    env,
    stdio: 'inherit',
    timeout: 30000,
  });
  console.log('CR2_H1_PREPARATION package_complete');
  const payload = JSON.parse(await readFile(join(work, 'payload-receipt.json'), 'utf8'));
  config.archive = join(work, payload.archiveName);
  config.mapBytes = JSON.stringify({
    formatVersion: 1,
    repository,
    tag: 'payload-' + payload.identity.releaseVersion,
    builder: { repository, workflowPath: workflow, workflowCommit: source },
    payload,
  });
  const response = await fetch(runtimeUrl, {
    redirect: 'error',
    signal: AbortSignal.timeout(60000),
  });
  console.log('CR2_H1_RUNTIME_STATUS ' + response.status);
  requireProof(response.status === 200);
  const chunks = [];
  let count = 0;
  for await (const chunk of response.body) {
    requireProof((count += chunk.length) <= runtimeBytes);
    chunks.push(chunk);
  }
  const runtime = Buffer.concat(chunks);
  requireProof(
    count === runtimeBytes && createHash('sha512').update(runtime).digest('hex') === runtimeSha512,
  );
  console.log('CR2_H1_PREPARATION runtime_verified');
  await writeFile(config.runtimeArchive, runtime, { mode: 0o600 });
  if (mode === 'rehearsal') await eventFile(config);
  const configPath = join(work, 'config.json');
  await writeFile(configPath, JSON.stringify(config), { mode: 0o600 });
  // Child service has no token/provider/Runner capability in its environment.
  const child = spawn(process.execPath, [fileURLToPath(import.meta.url), 'serve', configPath], {
    cwd: repo,
    env: { PATH: process.env.PATH },
    stdio: 'ignore',
    detached: true,
  });
  child.unref();
  for (let i = 0; ; i++) {
    try {
      config.origin = (await readFile(join(work, 'origin'), 'utf8')).trim();
      break;
    } catch {
      requireProof(i < 100);
      await new Promise((resolve) => setTimeout(resolve, 50));
    }
  }
  await companionConfig(config);
  await writeFile(configPath, JSON.stringify(config), { mode: 0o600 });
  console.log('CR2_H1_PREPARATION loopback_ready');
  await output('config', configPath);
  await output('preload', '--import ' + join(repo, relative, 'preload.mjs'));
  return { config, configPath };
}

export async function prepareRestore(configPath) {
  const original = JSON.parse(await readFile(configPath, 'utf8'));
  requireProof(original.mode === 'rehearsal' && original.phase === 'produce');
  const candidate = admitReceipt(
    JSON.parse(await readFile(original.receiptOut, 'utf8')),
    original.source,
    '7002',
  );
  const config = {
    ...original,
    phase: 'restore',
    runId: '7002',
    receiptIn: original.receiptOut,
    receiptOut: join(original.work, 'receipt-restore.json'),
  };
  requireProof(candidate.runId === config.runA);
  await eventFile(config);
  await companionConfig(config);
  const path = join(config.work, 'config-restore.json');
  await writeFile(path, JSON.stringify(config), { mode: 0o600 });
  await output('config', path);
  return { config, configPath: path };
}

export async function prepareRehearsalCase(
  templatePath,
  { faults = {}, existingRecords = 0 } = {},
) {
  const template = JSON.parse(await readFile(templatePath, 'utf8'));
  requireProof(template.mode === 'rehearsal');
  const work = await mkdtemp('/tmp/apr-h1-case-');
  const config = {
    ...template,
    work,
    phase: 'produce',
    runId: '7001',
    runA: '7001',
    faults,
    existingRecords,
    receiptOut: join(work, 'receipt-produce.json'),
    receiptIn: join(work, 'producer.json'),
    stopNonce: randomUUID(),
  };
  await eventFile(config);
  const service = await createService(config);
  config.origin = service.origin;
  await companionConfig(config);
  const configPath = join(work, 'config.json');
  await writeFile(configPath, JSON.stringify(config), { mode: 0o600 });
  return { configPath, config, service };
}

export async function verify(configPath, publish = false) {
  const config = JSON.parse(await readFile(configPath, 'utf8'));
  if (config.mode === 'rehearsal') {
    requireProof(typeof config.historicalActionRoot === 'string');
    inspectAssets(config.repo, undefined, config.historicalActionRoot);
    requireProof(
      createHash('sha256')
        .update(
          await readFile(join(config.repo, '.github/actions/agentic-pr-review/payload-map.json')),
        )
        .digest('hex') === config.checkedMapSha256,
    );
  } else inspectAssets(config.repo);
  const bytes = await readFile(config.receiptOut, 'utf8');
  requireProof(Buffer.byteLength(bytes) <= 16384);
  const receipt = JSON.parse(bytes);
  if (config.mode === 'hosted' && process.env.PROOF_ACTION_OUTCOME !== 'success')
    receipt.outcome = 'refused';
  if (receipt.outcome !== 'passed') {
    requireProof(
      receipt.source === config.source &&
        receipt.runId === config.runId &&
        receipt.outcome === 'refused',
    );
    const id = receipt.artifact?.id;
    const safe = {
      format: 'apr.cr2.hosted-artifact-stop.v1',
      source: config.source,
      phase: config.phase,
      runId: config.runId,
      outcome: 'refused',
      knownArtifactId: typeof id === 'string' && /^[1-9][0-9]{0,15}$/u.test(id) ? id : null,
      mutationState: [
        'not_dispatched',
        'not_committed',
        'upload_possible',
        'upload_committed',
        'upload_unknown',
        'delete_possible',
        'delete_committed',
        'delete_unknown',
      ].includes(receipt.mutationState)
        ? receipt.mutationState
        : 'unknown',
      nextAction: 'STOP_NO_REPLAY_SEPARATE_RECONCILIATION_AUTHORITY',
    };
    if (publish) {
      console.log('CR2_H1_STOP ' + JSON.stringify(safe));
      if (process.env.GITHUB_STEP_SUMMARY) {
        const { appendFile } = await import('node:fs/promises');
        await appendFile(
          process.env.GITHUB_STEP_SUMMARY,
          'Stopped without replay; this record grants no cleanup authority.\n\n```json\n' +
            JSON.stringify(safe) +
            '\n```\n',
        );
      }
    }
    requireProof(false);
  }
  if (config.phase === 'produce') admitReceipt(receipt, config.source, '0');
  else {
    const producer = JSON.parse(await readFile(config.receiptIn, 'utf8'));
    admitRestoreReceipt(receipt, producer, config.source, config.runId);
  }
  requireProof(
    receipt.mode === config.mode && receipt.runId === config.runId && receipt.runA === config.runA,
  );
  for (const canary of [
    'cr2-h1-rest-token-canary',
    'cr2-h1-runtime-token-canary',
    'cr2-h1-state-key-canary',
    'cr2-h1-provider-canary',
    'signed-url-canary',
    'download-url-canary',
  ])
    requireProof(!bytes.includes(canary));
  if (publish) {
    console.log('CR2_H1_SANITIZED_RECEIPT ' + JSON.stringify(receipt));
    if (process.env.GITHUB_STEP_SUMMARY) {
      const { appendFile } = await import('node:fs/promises');
      await appendFile(
        process.env.GITHUB_STEP_SUMMARY,
        'Transport companion receipt; synthetic map/CLR entry; no original archive, SESSION or live-provider claim.\n\n```json\n' +
          JSON.stringify(receipt, null, 2) +
          '\n```\n',
      );
    }
  }
  return receipt;
}
export async function stop(configPath) {
  const config = JSON.parse(await readFile(configPath, 'utf8'));
  const origin = new URL(config.origin);
  requireProof(origin.protocol === 'http:' && origin.hostname === '127.0.0.1');
  await fetch(new URL('/stop', origin), {
    method: 'POST',
    body: config.stopNonce,
    redirect: 'error',
    signal: AbortSignal.timeout(5000),
  });
}

async function main() {
  const [command, value] = process.argv.slice(2);
  if (command === 'serve') {
    const config = JSON.parse(await readFile(value, 'utf8'));
    const service = await createService(config);
    await writeFile(join(config.work, 'origin'), service.origin, { mode: 0o600 });
    return;
  }
  if (command === 'prepare') {
    requireProof(Buffer.byteLength(process.env.PROOF_RECEIPT ?? '') <= 16384);
    return prepare({
      mode: process.env.PROOF_MODE,
      phase: process.env.PROOF_PHASE,
      receipt: process.env.PROOF_RECEIPT ? JSON.parse(process.env.PROOF_RECEIPT) : undefined,
    });
  }
  if (command === 'prepare-restore') return prepareRestore(value);
  if (command === 'verify') return verify(value, true);
  if (command === 'stop') return stop(value);
  requireProof(false);
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url))
  await main().catch(() => {
    console.error('CR2_H1_PROOF_REFUSED');
    process.exitCode = 1;
  });
