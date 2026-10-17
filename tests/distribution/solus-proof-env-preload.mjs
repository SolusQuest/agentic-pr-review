// Subprocess-boundary fixture only; this never qualifies package or startup behavior.
import childProcess from 'node:child_process';
import fs from 'node:fs';
import fsPromises from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { registerHooks, syncBuiltinESMExports } from 'node:module';

const scenario = process.env.APR_ENV_SCENARIO;
const auditPath = process.env.APR_ENV_AUDIT;
const events = [];
const save = fs.writeFileSync;
const credential = (key) =>
  /^(GITHUB_TOKEN|GH_TOKEN|NUGET_AUTH_TOKEN|NuGetPackageSourceCredentials_.*)$/i.test(key);
function record(stage, env) {
  events.push({
    stage,
    credentials: Object.keys(env).filter(credential),
    token: Object.values(env).some((v) => String(v).includes('synthetic-secret-marker')),
  });
  save(auditPath, JSON.stringify(events));
}
const ids = [
  'solusagent.api',
  'solusagent.providers.deepseek',
  'solusagent.runtime',
  'solusagent.runtime.api',
  'solusagent.tools.api',
];
const bytes = Buffer.from('synthetic package bytes');
const hash = createHash('sha512').update(bytes).digest('base64');
const head = 'a'.repeat(40);
const assets = {
  packageFolders: { [process.env.RUNNER_TEMP + '/proof/packages']: {} },
  libraries: Object.fromEntries(
    ids.map((id) => [
      id + '/0.1.0-internal.4',
      { type: 'package', path: id + '/0.1.0-internal.4', sha512: hash },
    ]),
  ),
};
if (scenario !== 'producer') {
  Object.defineProperty(process, 'platform', { value: 'linux' });
  Object.defineProperty(process, 'arch', { value: 'x64' });
  fsPromises.mkdir = async () => {};
  fsPromises.writeFile = async () => {};
  fsPromises.readFile = async (path, encoding) => {
    let value;
    if (String(path) === '/etc/os-release') value = 'ID=ubuntu\nVERSION_ID="24.04"\n';
    else if (String(path).endsWith('project.assets.json')) value = JSON.stringify(assets);
    else if (String(path).endsWith('.nupkg.metadata'))
      value = JSON.stringify({
        source: 'https://nuget.pkg.github.com/SolusQuest/index.json',
        contentHash: hash,
      });
    else if (String(path).endsWith('.nupkg.sha512')) value = hash;
    else if (String(path).endsWith('.nupkg')) return bytes;
    else if (String(path).endsWith('.dll')) return Buffer.from('synthetic DLL bytes');
    else value = '{}';
    return encoding ? value : Buffer.from(value);
  };
  registerHooks({
    load(url, context, nextLoad) {
      if (url.endsWith('/scripts/release/build-payload.mjs')) {
        record('project-import', process.env);
        if (scenario === 'import') throw Error('synthetic import failure');
        return {
          format: 'module',
          shortCircuit: true,
          source:
            'export const managedDependencyPolicy = x => x; export const verifyDependencies = async () => {};',
        };
      }
      return nextLoad(url, context);
    },
  });
}
childProcess.execFileSync = (command, args, options = {}) => {
  record(command === 'git' ? 'git:' + args.join(' ') : 'unzip', options.env ?? process.env);
  if (
    scenario === 'early-git' ||
    (scenario === 'late-git' &&
      command === 'git' &&
      events.filter((e) => e.stage === 'git:status --porcelain').length === 2)
  )
    throw Error('synthetic command failure');
  if (command === 'git') {
    if (args[0] === 'rev-parse') return head + '\n';
    if (args[0] === 'ls-tree') return Buffer.from('100644 blob ' + head + '\tglobal.json\0');
    return options.encoding ? '' : Buffer.from('');
  }
  if (scenario === 'unzip') throw Error('synthetic command failure');
  return Buffer.from('synthetic DLL bytes');
};
childProcess.spawnSync = (command, args, options = {}) => {
  const authRestore = args[0] === 'restore' && args[1].includes('/src/');
  const stage = authRestore
    ? 'authenticated-restore'
    : args[0] === 'build'
      ? 'build'
      : args[0]?.endsWith('.dll')
        ? 'startup'
        : args[0] === 'restore'
          ? 'fixture-restore'
          : args.includes('-t:ValidateSolusManagedProfile')
            ? 'negative-msbuild'
            : 'native-msbuild';
  record(stage, options.env ?? process.env);
  if ((scenario === 'restore' && authRestore) || (scenario === 'build' && stage === 'build'))
    return { status: 1, stdout: '', stderr: '' };
  if (stage === 'negative-msbuild')
    return { status: 1, stdout: 'APR_SOLUS_MANAGED_PROFILE_UNSUPPORTED', stderr: '' };
  return {
    status: 0,
    stdout:
      stage === 'startup'
        ? 'constructed=true dispatches=0 negative_checks=8 credential_free=true'
        : stage === 'native-msbuild'
          ? JSON.stringify({ Items: { PackageReference: [], Compile: [] } })
          : '',
    stderr: '',
  };
};
syncBuiltinESMExports();
