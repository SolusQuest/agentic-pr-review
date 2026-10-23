// Failure-only test diagnostics. Raw/binlog stay private to the trusted job;
// stdout is a closed vocabulary and never includes exception text or paths.
import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { chmod, copyFile, lstat, mkdtemp, readFile, realpath, writeFile } from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
import { basename, dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repository = fileURLToPath(new URL('../../', import.meta.url));
const tasks = [
  'GenerateDepsFile',
  'GenerateRuntimeConfigurationFiles',
  'Copy',
  'WriteLinesToFile',
  'ResolvePackageAssets',
  'ResolveAssemblyReference',
  'Csc',
  'MSBuild',
  'Exec',
  'GenerateAppHost',
];
const exceptions = [
  'System.IO.IOException',
  'System.IO.FileNotFoundException',
  'System.IO.DirectoryNotFoundException',
  'System.UnauthorizedAccessException',
  'System.OutOfMemoryException',
  'System.InvalidOperationException',
  'System.ArgumentException',
  'System.NullReferenceException',
  'System.TypeInitializationException',
  'System.ComponentModel.Win32Exception',
  'Microsoft.Build.Exceptions.InvalidProjectFileException',
];
const reasons = [
  ['sharing_violation', /being used by another process/iu],
  ['access_denied', /access.*denied|permission denied/iu],
  ['missing_path', /could not find.*(?:file|path)|no such file or directory/iu],
  ['disk_full', /no space left on device/iu],
  ['descriptor_exhausted', /too many open files/iu],
  ['resource_unavailable', /resource temporarily unavailable/iu],
  ['out_of_memory', /out of memory|cannot allocate memory/iu],
];
const targets = [
  ['runtime_deps', 'AgenticPrReview.Runtime.deps.json'],
  ['runtime_assembly', 'AgenticPrReview.Runtime.dll'],
  ['runtime_config', 'AgenticPrReview.Runtime.runtimeconfig.json'],
  ['restore_assets', 'project.assets.json'],
];

export function buildFailureSummary(text) {
  const failedTasks = [
    ...text.matchAll(/"([A-Za-z][A-Za-z0-9.]{0,127})" task failed unexpectedly/gu),
  ].map((match) => match[1]);
  const errorLines = text
    .split('\n')
    .filter((line) => /error (MSB|CS|NU|NETSDK)[0-9]{4}\b|Exception:/u.test(line))
    .join('\n');
  return {
    codes: [
      ...new Set([...text.matchAll(/\berror ((?:MSB|CS|NU|NETSDK)[0-9]{4})\b/gu)].map((m) => m[1])),
    ].slice(0, 8),
    tasks: tasks.filter((task) => failedTasks.includes(task)),
    exceptions: exceptions.filter((name) => errorLines.includes(name)),
    reasons: reasons.filter(([, pattern]) => pattern.test(errorLines)).map(([reason]) => reason),
    targets: targets.filter(([, name]) => errorLines.includes(name)).map(([target]) => target),
  };
}

async function ownedFile(path, expectedName, maximumBytes) {
  const info = await lstat(path);
  if (
    !info.isFile() ||
    info.isSymbolicLink() ||
    basename(path) !== expectedName ||
    info.uid !== process.getuid() ||
    info.size > maximumBytes
  )
    throw new Error('diagnostic_input_refused');
  return info;
}
async function digest(path) {
  const hash = createHash('sha256');
  for await (const bytes of createReadStream(path)) hash.update(bytes);
  return hash.digest('hex');
}
function sourceIdentity() {
  const read = (revision) => {
    try {
      const value = execFileSync('git', ['-C', repository, 'rev-parse', revision], {
        encoding: 'utf8',
        timeout: 5000,
        stdio: ['ignore', 'pipe', 'ignore'],
      }).trim();
      return /^[a-f0-9]{40}$/u.test(value) ? value : null;
    } catch {
      return null;
    }
  };
  return { commit: read('HEAD'), tree: read('HEAD^{tree}') };
}

export async function retainBuildFailure({ rawPath, binlogPath, parentDirectory }) {
  const rawInfo = await ownedFile(rawPath, 'build-framework.log', 64 * 1024 * 1024);
  const binInfo = await ownedFile(binlogPath, 'build-framework.binlog', 256 * 1024 * 1024);
  if ((await realpath(dirname(rawPath))) !== (await realpath(dirname(binlogPath))))
    throw new Error('diagnostic_input_refused');
  const buildRoot = await realpath(dirname(rawPath));
  await ownedFile(join(buildRoot, '.r5-v1-temp-root'), '.r5-v1-temp-root', 1024);
  const buildInfo = await lstat(buildRoot);
  if (buildInfo.uid !== process.getuid() || (buildInfo.mode & 0o077) !== 0)
    throw new Error('diagnostic_input_refused');
  const parent = await realpath(parentDirectory);
  const checkout = await realpath(repository);
  const inside = relative(checkout, parent);
  if (inside === '' || (!inside.startsWith('..') && !inside.startsWith('/')))
    throw new Error('diagnostic_destination_refused');
  const inBuild = relative(buildRoot, parent);
  if (inBuild === '' || (!inBuild.startsWith('..') && !inBuild.startsWith('/')))
    throw new Error('diagnostic_destination_refused');
  const destination = await mkdtemp(join(parent, 'apr-r5-build-diagnostic-'));
  await chmod(destination, 0o700);
  const copies = [
    [rawPath, 'build.raw.log', rawInfo.size],
    [binlogPath, 'build.binlog', binInfo.size],
  ];
  const files = [];
  for (const [path, name, size] of copies) {
    const output = join(destination, name);
    await copyFile(path, output);
    await chmod(output, 0o600);
    files.push({ name, bytes: size, sha256: await digest(output) });
  }
  const summary = buildFailureSummary(await readFile(join(destination, 'build.raw.log'), 'utf8'));
  const identity = sourceIdentity();
  await writeFile(
    join(destination, 'manifest.json'),
    JSON.stringify({
      kind: 'r5-private-build-failure-v1',
      source: identity,
      files,
      summary,
    }) + '\n',
    { flag: 'wx', mode: 0o600 },
  );
  return {
    destination,
    publicSummary: { kind: 'r5-build-failure-summary-v1', retained: true, ...summary, files },
  };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const result = await retainBuildFailure({
      rawPath: process.argv[2],
      binlogPath: process.argv[3],
      parentDirectory: process.argv[4],
    });
    process.stdout.write(
      'APR_R5_EVAL_BUILD_DIAGNOSTIC ' + JSON.stringify(result.publicSummary) + '\n',
    );
  } catch {
    // Never expose caught filesystem/exception strings from the private log.
    process.stdout.write(
      'APR_R5_EVAL_BUILD_DIAGNOSTIC {"kind":"r5-build-failure-summary-v1","retained":false,"capture":"failed"}\n',
    );
    process.exitCode = 1;
  }
}
