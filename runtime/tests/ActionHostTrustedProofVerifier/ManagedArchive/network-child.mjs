import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { readFile, readlink } from 'node:fs/promises';
import { createServer } from 'node:https';
import { join } from 'node:path';
import { createSecureContext } from 'node:tls';

const root = process.argv[2];
const settings = JSON.parse(await readFile(join(root, 'network.json'), 'utf8'));
assert.notEqual(process.getuid(), 0);
for (const kind of ['net', 'mnt'])
  assert.notEqual(await readlink('/proc/self/ns/' + kind), settings.namespaces[kind]);
const status = await readFile('/proc/self/status', 'utf8');
for (const field of ['CapEff', 'CapPrm', 'CapBnd', 'CapAmb'])
  assert.match(status, new RegExp('^' + field + ':\\s+0+$', 'mu'));
assert.match(status, /^NoNewPrivs:\s+1$/mu);
const systemEnv = { PATH: '/usr/sbin:/usr/bin:/sbin:/bin' };
const ip = (...args) =>
  JSON.parse(execFileSync('/usr/sbin/ip', ['-j', ...args], { env: systemEnv }));
const active = ip('link', 'show').filter((entry) => entry.flags.includes('UP'));
assert.deepEqual(
  active.map((entry) => entry.ifname),
  ['lo'],
);
for (const family of ['-4', '-6']) {
  for (const route of ip(family, 'route', 'show', 'table', 'all')) {
    assert.equal(route.dev, 'lo');
    assert.ok(/^127\./u.test(route.dst) || route.dst === '::1');
  }
}

const trusted = {
  key: await readFile(join(root, 'server.key')),
  cert: await readFile(join(root, 'server.pem')),
};
const untrusted = createSecureContext({
  key: await readFile(join(root, 'untrusted.key')),
  cert: await readFile(join(root, 'untrusted.pem')),
});
const trustedContext = createSecureContext(trusted);
const acceptedHosts = [
  'api.github.com',
  'codeload.github.com',
  'api.deepseek.com',
  'apr-d5.blob.core.windows.net',
];
const expectedHosts = [...acceptedHosts, 'wrong-host.example', 'untrusted-root.example'];
const calls = [];
const server = createServer(
  {
    ...trusted,
    SNICallback: (name, done) => {
      if (!expectedHosts.includes(name)) return done(new Error('d5_tls_unexpected_host'));
      done(null, name === 'untrusted-root.example' ? untrusted : trustedContext);
    },
  },
  (request, response) => {
    try {
      assert.equal(request.method, 'GET');
      assert.equal(request.url, '/d5-tls-prerequisite');
      assert.ok(acceptedHosts.includes(request.headers.host));
      assert.equal(request.headers.authorization, undefined);
      calls.push(request.headers.host);
      response.writeHead(200, { 'Content-Type': 'text/plain' }).end('D5 synthetic HTTPS\n');
    } catch {
      response.destroy();
    }
  },
);
server.on('tlsClientError', () => {});
await new Promise((done, reject) => {
  server.once('error', reject);
  server.listen(443, '127.0.0.1', done);
});
const consumerEnv = {
  DOTNET_CLI_TELEMETRY_OPTOUT: '1',
  DOTNET_NOLOGO: '1',
  DOTNET_EnableDiagnostics: '0',
  TMPDIR: join(root, 'tmp'),
};
// Use asynchronous children: the same Node process owns the local TLS server.
async function run(args) {
  const child = spawn(join(root, 'runtime/dotnet'), args, {
    env: consumerEnv,
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  const chunks = [];
  let size = 0;
  for (const stream of [child.stdout, child.stderr])
    stream.on('data', (bytes) => {
      size += bytes.length;
      if (size > 8192) child.kill('SIGKILL');
      else chunks.push(bytes);
    });
  const code = await new Promise((done, reject) => {
    child.once('error', reject);
    child.once('close', done);
  });
  assert.equal(code, 0);
  assert.ok(size <= 8192);
  return Buffer.concat(chunks).toString();
}
try {
  assert.equal(await run(['--list-sdks']), '');
  assert.match(await run(['--list-runtimes']), /^Microsoft\.NETCore\.App 10\.0\.13 \[[^\n]+\]\n$/u);
  for (const name of expectedHosts) {
    const accept = acceptedHosts.includes(name);
    assert.equal(
      await run(['exec', join(root, 'probe/NetworkProbe.dll'), name, accept ? 'accept' : 'reject']),
      accept ? 'APR_D5_TLS_ACCEPTED\n' : 'APR_D5_TLS_REJECTED\n',
    );
  }
  assert.deepEqual(calls, acceptedHosts);
  process.stdout.write(
    'APR_D5_NETWORK_PREREQUISITE ' +
      JSON.stringify({
        kind: 'apr-d5-network-prerequisite-v1',
        runtime: '10.0.13',
        runner: 'ubuntu-24.04-x64',
        private_network_namespace: true,
        private_mount_namespace: true,
        privileged_payload: false,
        external_routes: 0,
        sdk_count: 0,
        https_positive_controls: calls.length,
        https_negative_controls: 2,
      }) +
      '\n',
  );
} finally {
  server.closeAllConnections();
  await new Promise((done) => server.close(done));
}
