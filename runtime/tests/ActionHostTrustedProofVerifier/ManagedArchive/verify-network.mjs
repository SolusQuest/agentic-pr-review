// Supporting target-runner prerequisite; this is not Solus archive qualification.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync } from 'node:fs';
import {
  chmod,
  mkdir,
  mkdtemp,
  readFile,
  readlink,
  realpath,
  rm,
  writeFile,
} from 'node:fs/promises';
import { homedir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const directory = dirname(fileURLToPath(import.meta.url));
const repo = fileURLToPath(new URL('../../../../', import.meta.url));
const runtimeSize = 36_648_012;
const runtimeSha512 =
  '9574d95364110409416735e7842796f8d614c21d74cb25d204a3e4cfd11b2c05efdf19ca89b8069650a04f64add2705967dcb04b8665a36d7a3f30a5d8c05afe';
const digest = (bytes) => createHash('sha256').update(bytes).digest('hex');
export async function runIsolatedFixture({
  prepare,
  childName = 'network-child.mjs',
  prefix = 'APR_D5_NETWORK_PREREQUISITE ',
  maximumSeconds = 90,
} = {}) {
  assert.ok(['network-child.mjs', 'archive-child.mjs'].includes(childName));
  assert.ok(['APR_D5_NETWORK_PREREQUISITE ', 'APR_D5_MANAGED_ARCHIVE '].includes(prefix));
  assert.ok(Number.isSafeInteger(maximumSeconds) && maximumSeconds >= 90 && maximumSeconds <= 1200);
  let stage = 'preflight';
  let root;
  try {
    assert.equal(process.platform, 'linux');
    assert.equal(process.arch, 'x64');
    assert.notEqual(process.getuid(), 0);
    assert.match(await readFile('/etc/os-release', 'utf8'), /^ID=ubuntu$/mu);
    assert.match(await readFile('/etc/os-release', 'utf8'), /^VERSION_ID="24\.04"$/mu);
    const builderEnv = {};
    for (const name of ['PATH', 'HOME', 'DOTNET_ROOT'])
      if (process.env[name]) builderEnv[name] = process.env[name];
    builderEnv.DOTNET_CLI_TELEMETRY_OPTOUT = '1';
    builderEnv.DOTNET_NOLOGO = '1';
    const run = (command, args) => {
      const result = spawnSync(command, args, {
        cwd: repo,
        env: builderEnv,
        encoding: 'utf8',
        timeout: (maximumSeconds + 30) * 1000,
        maxBuffer: 1024 * 1024,
      });
      assert.equal(result.error, undefined);
      if (result.status !== 0) {
        const failure = result.stderr
          .split('\n')
          .find((line) =>
            /^APR_D5_ARCHIVE_FAILURE stage=[a-z0-9-]+ code=(assertion|harness)$/u.test(line),
          );
        if (failure) process.stderr.write(failure + '\n');
      }
      assert.equal(result.status, 0);
      return result.stdout;
    };
    const source = run('git', ['rev-parse', 'HEAD']).trim();
    const tree = run('git', ['rev-parse', 'HEAD^{tree}']).trim();
    assert.match(source, /^[a-f0-9]{40}$/u);
    assert.match(tree, /^[a-f0-9]{40}$/u);
    assert.equal(run('git', ['status', '--porcelain']), '');
    const original = {
      hosts: digest(await readFile('/etc/hosts')),
      trust: digest(await readFile('/etc/ssl/certs/ca-certificates.crt')),
      namespaces: {
        net: await readlink('/proc/self/ns/net'),
        mnt: await readlink('/proc/self/ns/mnt'),
      },
    };
    root = await mkdtemp('/tmp/apr-d5-network-');
    await chmod(root, 0o700);
    for (const name of ['tmp', 'trust', 'runtime', 'probe'])
      await mkdir(join(root, name), { mode: 0o700 });
    builderEnv.DOTNET_CLI_HOME = join(root, 'builder-home');
    const dotnet = await realpath(run('/usr/bin/which', ['dotnet']).trim());
    const sdk = dirname(dotnet);
    const configuredPackages = process.env.NUGET_PACKAGES ?? join(homedir(), '.nuget/packages');
    const packages = existsSync(configuredPackages) ? await realpath(configuredPackages) : '';

    stage = 'public-probe-build';
    const config = join(root, 'public.nuget.config');
    await writeFile(
      config,
      '<?xml version="1.0"?><configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>\n',
      { mode: 0o600, flag: 'wx' },
    );
    const project = join(directory, 'NetworkProbe/NetworkProbe.csproj');
    run(dotnet, ['restore', project, '--configfile', config]);
    run(dotnet, ['publish', project, '--no-restore', '-c', 'Release', '-o', join(root, 'probe')]);

    stage = 'pinned-runtime';
    const response = await fetch(
      'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.13/dotnet-runtime-10.0.13-linux-x64.tar.gz',
      {
        redirect: 'error',
        signal: AbortSignal.timeout(60_000),
        headers: { 'Accept-Encoding': 'identity' },
      },
    );
    assert.equal(response.status, 200);
    assert.ok(response.body);
    const chunks = [];
    let size = 0;
    for await (const chunk of response.body) {
      size += chunk.length;
      assert.ok(size <= runtimeSize);
      chunks.push(chunk);
    }
    const archive = Buffer.concat(chunks);
    assert.equal(archive.length, runtimeSize);
    assert.equal(createHash('sha512').update(archive).digest('hex'), runtimeSha512);
    const archivePath = join(root, 'runtime.tar.gz');
    await writeFile(archivePath, archive, { mode: 0o600, flag: 'wx' });
    run('/usr/bin/tar', ['-xzf', archivePath, '-C', join(root, 'runtime')]);

    stage = 'private-tls';
    await writeFile(
      join(root, 'ca.cnf'),
      '[req]\ndistinguished_name=dn\nx509_extensions=ca\nprompt=no\n[dn]\nCN=D5 synthetic root\n[ca]\nbasicConstraints=critical,CA:TRUE\nkeyUsage=critical,keyCertSign,cRLSign\nsubjectKeyIdentifier=hash\n',
      { mode: 0o600, flag: 'wx' },
    );
    run('/usr/bin/openssl', [
      'req',
      '-x509',
      '-newkey',
      'rsa:2048',
      '-nodes',
      '-days',
      '1',
      '-config',
      join(root, 'ca.cnf'),
      '-keyout',
      join(root, 'ca.key'),
      '-out',
      join(root, 'ca.pem'),
    ]);
    run('/usr/bin/openssl', [
      'req',
      '-newkey',
      'rsa:2048',
      '-nodes',
      '-subj',
      '/CN=api.github.com',
      '-keyout',
      join(root, 'server.key'),
      '-out',
      join(root, 'server.csr'),
    ]);
    await writeFile(
      join(root, 'server.ext'),
      'subjectAltName=DNS:api.github.com,DNS:codeload.github.com,DNS:api.deepseek.com\nbasicConstraints=critical,CA:FALSE\nextendedKeyUsage=serverAuth\n',
      { mode: 0o600, flag: 'wx' },
    );
    run('/usr/bin/openssl', [
      'x509',
      '-req',
      '-days',
      '1',
      '-in',
      join(root, 'server.csr'),
      '-CA',
      join(root, 'ca.pem'),
      '-CAkey',
      join(root, 'ca.key'),
      '-CAcreateserial',
      '-extfile',
      join(root, 'server.ext'),
      '-out',
      join(root, 'server.pem'),
    ]);
    run('/usr/bin/openssl', [
      'req',
      '-x509',
      '-newkey',
      'rsa:2048',
      '-nodes',
      '-days',
      '1',
      '-subj',
      '/CN=untrusted-root.example',
      '-addext',
      'subjectAltName=DNS:untrusted-root.example',
      '-keyout',
      join(root, 'untrusted.key'),
      '-out',
      join(root, 'untrusted.pem'),
    ]);
    const ca = await readFile(join(root, 'ca.pem'));
    await writeFile(join(root, 'trust/ca-certificates.crt'), ca, { mode: 0o600, flag: 'wx' });
    run('/usr/bin/openssl', ['rehash', join(root, 'trust')]);
    await writeFile(
      join(root, 'hosts'),
      '127.0.0.1 localhost api.github.com codeload.github.com api.deepseek.com wrong-host.example untrusted-root.example\n::1 localhost\n',
      { mode: 0o600, flag: 'wx' },
    );
    await writeFile(
      join(root, 'network.json'),
      JSON.stringify({ namespaces: original.namespaces }),
      {
        mode: 0o600,
        flag: 'wx',
      },
    );

    if (prepare) {
      stage = 'prepare-archive';
      await prepare(root, { source, tree, builderEnv });
    }
    stage = 'isolated-runtime';
    const output = run('/usr/bin/sudo', [
      '-n',
      '/usr/bin/timeout',
      '--signal=KILL',
      String(maximumSeconds) + 's',
      '/usr/bin/unshare',
      '--mount',
      '--net',
      '--pid',
      '--fork',
      '--mount-proc',
      '/bin/bash',
      join(directory, 'isolate-network.sh'),
      root,
      String(process.getuid()),
      String(process.getgid()),
      process.execPath,
      join(directory, childName),
      sdk,
      packages,
    ]);
    assert.ok(output.startsWith(prefix) && output.indexOf('\n') === output.length - 1);
    const facts = JSON.parse(output.slice(prefix.length));
    assert.equal(digest(await readFile('/etc/hosts')), original.hosts);
    assert.equal(digest(await readFile('/etc/ssl/certs/ca-certificates.crt')), original.trust);
    for (const kind of ['net', 'mnt'])
      assert.equal(await readlink('/proc/self/ns/' + kind), original.namespaces[kind]);
    process.stdout.write(
      prefix +
        JSON.stringify({
          ...facts,
          source_commit: source,
          source_tree: tree,
          probe_sha256: digest(await readFile(join(root, 'probe/NetworkProbe.dll'))),
          runtime_archive_sha512: runtimeSha512,
          outside_mounts_unchanged: true,
        }) +
        '\n',
    );
  } catch {
    process.stderr.write('APR_D5_NETWORK_FAILURE stage=' + stage + '\n');
    process.exitCode = 1;
  } finally {
    if (root) await rm(root, { recursive: true, force: true });
  }
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href)
  await runIsolatedFixture();
