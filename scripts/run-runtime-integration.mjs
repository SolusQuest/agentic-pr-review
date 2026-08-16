import { execFileSync, spawnSync } from 'node:child_process';
import { copyFile, mkdtemp, readFile, rm, stat } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const workspaceRoot = path.resolve(process.env.GITHUB_WORKSPACE || repoRoot);
const tempRoot = await mkdtemp(path.join(os.tmpdir(), 'agentic-pr-review-runtime-integration-'));
if (
  path.resolve(tempRoot) === workspaceRoot ||
  tempRoot.startsWith(`${workspaceRoot}${path.sep}`)
) {
  throw new Error('integration temporary directory must be outside the workspace');
}

const dotnet = findDotnet();
let dotnetWorkdir = process.env.APR_RUNTIME_DOTNET_WORKDIR || repoRoot;
if (!process.env.APR_RUNTIME_DOTNET_WORKDIR && process.platform !== 'linux') {
  try {
    execFileSync(dotnet, ['--version'], { cwd: repoRoot, stdio: 'ignore' });
  } catch {
    dotnetWorkdir = os.tmpdir();
    console.log(
      'DOTNET_SDK_FALLBACK: using the installed non-Linux SDK because global.json is unavailable locally',
    );
  }
}
const runtimePublish = path.join(tempRoot, 'runtime-framework');

try {
  run(dotnet, [
    'publish',
    path.join(repoRoot, 'runtime/src/AgenticPrReview.Runtime/AgenticPrReview.Runtime.csproj'),
    '-c',
    'Release',
    '--nologo',
    '--no-self-contained',
    '-p:PublishAot=false',
    '-o',
    runtimePublish,
  ]);
  await runCliSmoke(
    dotnet,
    [path.join(runtimePublish, 'AgenticPrReview.Runtime.dll')],
    'framework',
  );

  if (process.platform === 'linux') {
    const aotPublish = path.join(tempRoot, 'runtime-aot-linux-x64');
    run(dotnet, [
      'publish',
      path.join(repoRoot, 'runtime/src/AgenticPrReview.Runtime/AgenticPrReview.Runtime.csproj'),
      '-c',
      'Release',
      '--nologo',
      '-r',
      'linux-x64',
      '--self-contained',
      'true',
      '-p:PublishAot=true',
      '-o',
      aotPublish,
    ]);
    const aot = path.join(aotPublish, 'AgenticPrReview.Runtime');
    await runCliSmoke(aot, [], 'aot');
  } else {
    console.log('NON_PORTABLE_CASES_SKIPPED: Linux-only unsafe-file process cases');
    console.log('AOT_SKIPPED_NON_LINUX: Native AOT smoke is release-gated on Linux CI');
  }
} finally {
  await rm(tempRoot, { recursive: true, force: true });
}

function findDotnet() {
  const command = process.platform === 'win32' ? 'where.exe' : 'which';
  const output = execFileSync(command, ['dotnet'], { encoding: 'utf8' });
  const candidate = output
    .split(/\r?\n/)
    .map((line) => line.trim())
    .find(Boolean);
  if (!candidate) throw new Error('dotnet executable was not found');
  return path.resolve(candidate);
}

function run(command, args) {
  const result = spawnSync(command, args, {
    cwd: command === dotnet ? dotnetWorkdir : repoRoot,
    stdio: 'inherit',
    windowsHide: true,
  });
  if (result.error) throw result.error;
  if (result.status !== 0) {
    throw new Error(`${path.basename(command)} exited with status ${result.status ?? 'unknown'}`);
  }
}

async function runCliSmoke(command, prefixArgs, mode) {
  const work = await mkdtemp(path.join(tempRoot, `runtime-${mode}-work-`));
  const input = path.join(work, 'input.json');
  const output = path.join(work, 'result.json');
  const trace = path.join(work, 'trace.json');
  await copyFile(path.join(repoRoot, 'protocol/fixtures/v1/cases/bootstrap/input.json'), input);
  run(command, [...prefixArgs, 'review', '--input', input, '--output', output, '--trace', trace]);
  await stat(output);
  await stat(trace);
  for (const artifact of [output, trace]) {
    const parsed = JSON.parse(await readFile(artifact, 'utf8'));
    if (parsed.protocolVersion !== 1) {
      throw new Error(`${mode} ${path.basename(artifact)} has an unexpected protocol version`);
    }
  }
}
