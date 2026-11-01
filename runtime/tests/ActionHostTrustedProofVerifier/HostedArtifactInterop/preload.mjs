// Explicit trusted proof wiring. No public input, production import or SDK-child import.
import fs from 'node:fs';
import https from 'node:https';
import http from 'node:http';
import childProcess from 'node:child_process';
import { syncBuiltinESMExports } from 'node:module';
import { dirname, join, basename } from 'node:path';
import {
  actionSource,
  inspectAssets,
  assets,
  repository,
  requireProof,
  sha256,
} from '../../../../scripts/cr2-hosted-artifact-policy.mjs';

const configPath = process.env.APR_CR2_H1_CONFIG;
requireProof(configPath && process.env.NODE_OPTIONS === '--import ' + import.meta.filename);
const config = JSON.parse(fs.readFileSync(configPath, 'utf8'));
requireProof(['hosted', 'rehearsal'].includes(config.mode));
requireProof(
  process.platform === 'linux' &&
    process.arch === 'x64' &&
    process.versions.node.split('.')[0] === '24',
);
const entry = basename(process.argv[1]);
requireProof(['prepare.js', 'index.js', 'cleanup.js'].includes(entry));
requireProof(
  process.env.GITHUB_ACTION_REPOSITORY === repository &&
    process.env.GITHUB_ACTION_REF === actionSource,
);
const actionRoot = dirname(dirname(dirname(fs.realpathSync(process.argv[1]))));
for (const name of assets) {
  const bytes = fs.readFileSync(join(actionRoot, name));
  if (name === 'payload-map.json' && bytes.equals(Buffer.from(config.mapBytes))) continue;
  requireProof(sha256(bytes) === config.assets[name].sha256);
  if (name === 'payload-map.json') requireProof(JSON.parse(bytes) === null);
}
if (entry === 'prepare.js')
  fs.writeFileSync(join(actionRoot, 'payload-map.json'), config.mapBytes, { mode: 0o600 });
// Historical rehearsal assets remain separate from the checked production map.
if (config.mode === 'rehearsal') {
  inspectAssets(config.repo, undefined, config.historicalActionRoot);
  requireProof(
    sha256(
      fs.readFileSync(join(config.repo, '.github/actions/agentic-pr-review/payload-map.json')),
    ) === config.checkedMapSha256,
  );
} else
  requireProof(
    JSON.parse(
      fs.readFileSync(
        join(config.repo, '.github/actions/agentic-pr-review/payload-map.json'),
        'utf8',
      ),
    ) === null,
  );
if (config.mode === 'rehearsal') {
  // Replace genuine Runner context before any SDK admission. Closed synthetic capabilities only.
  process.env.ACTIONS_RUNTIME_TOKEN = config.runtimeToken;
  process.env.ACTIONS_RESULTS_URL = config.origin;
  Object.assign(process.env, config.syntheticEnvironment);
}
requireProof(process.env.GITHUB_RUN_ID === config.runId && process.env.GITHUB_RUN_ATTEMPT === '1');
requireProof(process.env.GITHUB_SHA === config.source);
delete process.env.NODE_OPTIONS;
delete process.env.NODE_PATH;
for (const name of [
  'HTTP_PROXY',
  'HTTPS_PROXY',
  'NO_PROXY',
  'http_proxy',
  'https_proxy',
  'no_proxy',
])
  delete process.env[name];
const local = new URL(config.origin);
requireProof(local.protocol === 'http:' && local.hostname === '127.0.0.1');
const acquisition = (url) => {
  const uri = new URL(url);
  return (
    (uri.origin === 'https://api.github.com' &&
      uri.pathname.startsWith(`/repos/${repository}/contents/`)) ||
    (uri.origin === 'https://api.github.com' &&
      uri.pathname.startsWith(`/repos/${repository}/releases/`)) ||
    uri.href === config.runtimeUrl ||
    (uri.origin === 'https://github.com' &&
      uri.pathname.startsWith(`/${repository}/releases/download/`))
  );
};
https.request = (url, options, callback) => {
  requireProof(acquisition(url));
  const uri = new URL(url);
  const headers = { ...options.headers };
  delete headers.Authorization;
  delete headers.authorization;
  return http.request(
    new URL(uri.pathname + uri.search, config.origin),
    { ...options, headers },
    callback,
  );
};
const originalFetch = globalThis.fetch;
globalThis.fetch = (input, options) => {
  const uri = new URL(input instanceof Request ? input.url : input);
  requireProof(acquisition(uri));
  const headers = new Headers(options?.headers);
  headers.delete('authorization');
  return originalFetch(new URL(uri.pathname + uri.search, config.origin), { ...options, headers });
};
const spawn = childProcess.spawn.bind(childProcess);
childProcess.spawn = (command, args, options) => {
  if (command === '/proc/self/fd/3' && args[0] === 'exec') {
    requireProof(args.at(-1) === 'run');
    // Explicit companion entry, real CLI framing/store and original Node upload capability.
    const companionArgs = [
      'exec',
      '--runtimeconfig',
      config.companion + '.runtimeconfig.json',
      '--depsfile',
      config.companion + '.deps.json',
      '--fx-version',
      '10.0.13',
      '--roll-forward',
      'Disable',
      config.companion + '.dll',
    ];
    for (const secret of [
      config.runtimeToken,
      'cr2-h1-rest-token-canary',
      'cr2-h1-state-key-canary',
      'cr2-h1-provider-canary',
    ])
      if (secret) requireProof(!JSON.stringify(options.env).includes(secret));
    return spawn(command, companionArgs, {
      ...options,
      env: { ...options.env, APR_CR2_H1_CONFIG: config.companionConfig },
    });
  }
  if (command === process.execPath && args.length === 1 && basename(args[0]) === 'sdk-upload.js') {
    requireProof(
      sha256(fs.readFileSync(args[0])) === config.assets['internal/dist/sdk-upload.js'].sha256,
    );
    requireProof(!options.env.NODE_OPTIONS && !options.env.NODE_PATH);
    for (const forbidden of [
      'cr2-h1-rest-token-canary',
      'cr2-h1-state-key-canary',
      'cr2-h1-provider-canary',
    ])
      requireProof(!JSON.stringify(options.env).includes(forbidden));
  }
  return spawn(command, args, options);
};
syncBuiltinESMExports();
