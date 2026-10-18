import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFile, readlink } from 'node:fs/promises';
import { join } from 'node:path';

export async function assertIsolatedBoundary(root) {
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

  return { sdk_count: 0, external_routes: 0, privileged_payload: false };
}
