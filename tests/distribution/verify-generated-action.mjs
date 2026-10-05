// Literal checked Action -> ordinary resolver -> original packaged Native AOT Runtime.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync } from 'node:fs';
import { copyFile, mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { inspectPackage, sha256 } from '../../scripts/release/build-payload.format.mjs';

assert.equal(process.platform, 'linux');
assert.equal(process.arch, 'x64');
const [repoArgument, archiveArgument, receiptArgument] = process.argv.slice(2);
const repo = resolve(repoArgument);
const receipt = JSON.parse(await readFile(receiptArgument, 'utf8'));
const archive = await readFile(archiveArgument);
inspectPackage(archive, receipt);
const bundle = await readFile(join(repo, '.github/actions/agentic-pr-review/dist/index.js'));
assert.match(bundle.toString('utf8'), /WRAPPER_BUILD_DISCRIMINATOR = "r7-d0"/u);
const repository = 'SolusQuest/agentic-pr-review';
const actionT = 'b'.repeat(40),
  builderW = 'c'.repeat(40);
assert.notEqual(actionT, receipt.identity.sourceCommit);
assert.notEqual(builderW, receipt.identity.sourceCommit);
const map = {
  formatVersion: 1,
  repository,
  tag: 'payload-' + receipt.identity.releaseVersion,
  builder: {
    repository,
    workflowPath: '.github/workflows/r4-trusted-proof.yml',
    workflowCommit: builderW,
  },
  payload: receipt,
};
const work = await mkdtemp(join(tmpdir(), 'apr-d3-'));
const results = [];

function outputs(source) {
  const lines = source.trimEnd().split('\n'),
    result = {};
  assert.equal(lines.length % 3, 0);
  for (let index = 0; index < lines.length; index += 3) {
    const [name, delimiter] = lines[index].split('<<');
    assert.equal(lines[index + 2], delimiter);
    assert.equal(Object.hasOwn(result, name), false);
    result[name] = lines[index + 1];
  }
  return result;
}
async function audit(path) {
  return (await readFile(path, 'utf8').catch(() => ''))
    .trim()
    .split('\n')
    .filter(Boolean)
    .map((line) => JSON.parse(line));
}
async function until(predicate, timeout = 15_000) {
  const end = performance.now() + timeout;
  while (performance.now() < end) {
    const result = await predicate();
    if (result) return result;
    await delay(10);
  }
  throw new Error('generated_action_fixture_barrier_timeout');
}

async function run(name, options = {}) {
  const root = join(work, String(results.length));
  const action = join(root, 'action'),
    temp = join(root, 'tmp'),
    workspace = join(root, 'workspace');
  await Promise.all([
    mkdir(join(action, 'dist'), { recursive: true }),
    mkdir(temp, { recursive: true }),
    mkdir(workspace, { recursive: true }),
  ]);
  await copyFile(
    join(repo, '.github/actions/agentic-pr-review/action.yml'),
    join(action, 'action.yml'),
  );
  await copyFile(
    join(repo, '.github/actions/agentic-pr-review/dist/index.js'),
    join(action, 'dist/index.js'),
  );
  assert.deepEqual(await readFile(join(action, 'dist/index.js')), bundle);
  await writeFile(join(action, 'package.json'), '{"type":"module"}\n');
  // Only test-owned adjacent map is projected. Literal bundle/compiled identity are unchanged.
  if (options.unbound)
    await copyFile(
      join(repo, '.github/actions/agentic-pr-review/payload-map.json'),
      join(action, 'payload-map.json'),
    );
  else await writeFile(join(action, 'payload-map.json'), JSON.stringify(map) + '\n');
  const metadata = {};
  for (const relative of ['action.yml', 'payload-map.json', 'dist/index.js']) {
    const bytes = await readFile(join(action, relative));
    metadata[relative] = {
      type: 'file',
      path: '.github/actions/agentic-pr-review/' + relative,
      size: bytes.length,
      sha: createHash('sha1').update(`blob ${bytes.length}\0`).update(bytes).digest('hex'),
    };
  }
  if (options.contentMismatch) metadata['dist/index.js'].sha = '0'.repeat(40);
  const event = join(root, 'event.json');
  await writeFile(
    event,
    JSON.stringify(
      options.untrusted
        ? {
            pull_request: {},
            repository: { id: 42, full_name: repository },
            sender: { id: 7, login: 'maintainer' },
          }
        : {
            inputs: { 'pr-number': '344' },
            repository: { id: 42, full_name: repository },
            sender: { id: 7, login: 'maintainer' },
          },
    ),
  );
  const auditPath = join(root, 'audit.jsonl'),
    output = join(root, 'outputs'),
    summary = join(root, 'summary'),
    trace = join(root, 'trace');
  await Promise.all([writeFile(auditPath, ''), writeFile(output, ''), writeFile(summary, '')]);
  const requests = [];
  let assetStarted = false;
  const server = createServer((request, response) => {
    requests.push(request.url);
    assert.equal(request.method, 'GET');
    assert.equal(request.headers.authorization, undefined);
    const prefix = '/repos/' + repository;
    let document;
    if (request.url === prefix + '/commits/' + receipt.identity.releaseVersion)
      document = { sha: actionT };
    else if (request.url.startsWith(prefix + '/contents/')) {
      const relative = request.url
        .slice((prefix + '/contents/.github/actions/agentic-pr-review/').length)
        .split('?')[0];
      assert.equal(new URL(request.url, 'http://fixture').searchParams.get('ref'), actionT);
      document = metadata[relative];
    } else if (request.url === prefix + '/releases/tags/' + map.tag)
      document = { id: 123, tag_name: map.tag, draft: false };
    else if (request.url === prefix + '/releases/123/assets?per_page=100')
      document = [
        {
          id: 456,
          name: receipt.archiveName,
          state: 'uploaded',
          size: receipt.archiveSize,
          browser_download_url: `https://github.com/${repository}/releases/download/${map.tag}/${receipt.archiveName}`,
        },
      ];
    else if (request.url === `/${repository}/releases/download/${map.tag}/${receipt.archiveName}`) {
      assetStarted = true;
      response.writeHead(200, { 'Content-Length': archive.length });
      if (options.acquisitionCancel) {
        response.write(archive.subarray(0, 1));
        return;
      }
      if (options.badDigest) {
        const altered = Buffer.from(archive);
        altered[altered.length - 1] ^= 1;
        response.end(altered);
      } else response.end(archive);
      return;
    }
    assert.ok(document, request.url);
    const bytes = Buffer.from(JSON.stringify(document));
    response.writeHead(200, { 'Content-Length': bytes.length });
    response.end(bytes);
  });
  await new Promise((done) => server.listen(0, '127.0.0.1', done));
  const config = join(root, 'config.json');
  await writeFile(
    config,
    JSON.stringify({ audit: auditPath, origin: `http://127.0.0.1:${server.address().port}` }),
  );
  const env = {
    TMPDIR: temp,
    TMP: temp,
    TEMP: temp,
    GITHUB_WORKSPACE: workspace,
    GITHUB_EVENT_PATH: event,
    GITHUB_REPOSITORY: repository,
    GITHUB_REPOSITORY_ID: '42',
    GITHUB_RUN_ID: '900',
    GITHUB_RUN_ATTEMPT: '1',
    GITHUB_WORKFLOW_REF: `${repository}/.github/workflows/r4-trusted-proof.yml@refs/heads/main`,
    GITHUB_WORKFLOW_SHA: 'a'.repeat(40),
    GITHUB_ACTION_REPOSITORY: options.wrongRepository ? 'untrusted/repo' : repository,
    GITHUB_ACTION_REF: options.wrongRef
      ? 'main'
      : options.tag
        ? receipt.identity.releaseVersion
        : actionT,
    GITHUB_OUTPUT: output,
    GITHUB_STEP_SUMMARY: summary,
    APR_D3_FIXTURE_CONFIG: config,
    AGENTIC_PR_REVIEW_PREPARED_ROOT: workspace,
    AGENTIC_PR_REVIEW_PREPARED_EXECUTABLE: 'PRIVATE_D3_PATH_CANARY',
    AGENTIC_PR_REVIEW_PREPARED_PAYLOAD_SHA256: 'd'.repeat(64),
    AGENTIC_PR_REVIEW_ACTION_SOURCE_SHA: 'e'.repeat(40),
    AGENTIC_PR_REVIEW_PAYLOAD_BUILD_DISCRIMINATOR: 'r4-w2',
    AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE: options.tag
      ? 'measurement'
      : 'invalid-proof-profile',
    'INPUT_PR-NUMBER': '344',
    'INPUT_STATE-MODE': 'auto',
    'INPUT_PAYLOAD-MAP': 'PRIVATE_D3_PATH_CANARY',
    INPUT_EXECUTABLE: 'PRIVATE_D3_PATH_CANARY',
    'INPUT_PROOF-PROFILE': 'final-bootstrap',
  };
  const args = [
    '--import',
    join(repo, 'tests/distribution/generated-action-preload.mjs'),
    join(action, 'dist/index.js'),
  ];
  const child = options.nativeCancel
    ? spawn(
        '/usr/bin/strace',
        [
          '-f',
          '-qq',
          '-o',
          trace,
          '-e',
          'trace=openat',
          '-P',
          event,
          '-e',
          'inject=openat:delay_exit=2s',
          process.execPath,
          ...args,
        ],
        { cwd: workspace, env, stdio: ['ignore', 'pipe', 'pipe'], detached: true },
      )
    : spawn(process.execPath, args, {
        cwd: workspace,
        env,
        stdio: ['ignore', 'pipe', 'pipe'],
        detached: true,
      });
  const stdout = [],
    stderr = [];
  let captured = 0;
  for (const [stream, sink] of [
    [child.stdout, stdout],
    [child.stderr, stderr],
  ])
    stream.on('data', (bytes) => {
      captured += bytes.length;
      if (captured > 1024 * 1024) child.kill('SIGKILL');
      else sink.push(bytes);
    });
  const closed = new Promise((done, reject) => {
    child.once('error', reject);
    child.once('close', (code, signal) => done({ code, signal }));
  });
  const watchdog = setTimeout(() => {
    try {
      process.kill(-child.pid, 'SIGKILL');
    } catch {}
  }, 25_000);
  try {
    if (options.acquisitionCancel) {
      const wrapper = await until(
        async () =>
          assetStarted && (await audit(auditPath)).find((record) => record.kind === 'wrapper'),
      );
      process.kill(wrapper.pid, 'SIGTERM');
    }
    if (options.nativeCancel) {
      const spawned = await until(async () =>
        (await audit(auditPath)).find((record) => record.kind === 'spawn'),
      );
      await until(async () => {
        const text = await readFile(trace, 'utf8').catch(() => '');
        for (const match of text.matchAll(/^(\d+)\s+openat\([^\n]*event\.json/gmu)) {
          const status = await readFile(`/proc/${match[1]}/status`, 'utf8').catch(() => '');
          if (new RegExp(`^Tgid:\\s+${spawned.pid}$`, 'mu').test(status)) return true;
        }
        return false;
      });
      process.kill(spawned.wrapperPid, 'SIGTERM');
    }
    const exit = await closed;
    assert.equal(exit.signal, null);
    const records = await audit(auditPath),
      spawned = records.find((record) => record.kind === 'spawn');
    const actual = outputs(await readFile(output, 'utf8'));
    const summaryText = await readFile(summary, 'utf8');
    const publicText = Buffer.concat([...stdout, ...stderr]).toString('utf8') + summaryText;
    for (const canary of [work, 'PRIVATE_D3_PATH_CANARY', receipt.archiveSha256])
      assert.equal(publicText.includes(canary), false);
    assert.equal(Buffer.concat(stderr).length, 0);
    assert.equal(
      records.some((record) => record.kind === 'presentation'),
      true,
    );
    for (const record of records.filter((record) => record.kind === 'presentation'))
      assert.deepEqual(record, {
        kind: 'presentation',
        payloadGone: true,
        bridgeGone: true,
        admittedHandleClosed: true,
      });
    if (
      options.unbound ||
      options.wrongRef ||
      options.wrongRepository ||
      options.contentMismatch ||
      options.badDigest ||
      options.acquisitionCancel
    ) {
      assert.equal(spawned, undefined);
      assert.equal(exit.code, 1);
      assert.equal(actual.status, 'failed');
      assert.equal(actual['usage-completeness'], 'unavailable');
      assert.deepEqual(Object.keys(actual).sort(), [
        'attempt-accounting-completeness',
        'status',
        'termination-reason',
        'usage-completeness',
      ]);
      if (options.unbound || options.wrongRef || options.wrongRepository)
        assert.equal(requests.length, 0);
      if (options.unbound) assert.match(publicText, /No release payload has been authorized/u);
    } else {
      assert.ok(spawned);
      assert.deepEqual(spawned.args, []);
      assert.equal(spawned.command, '/proc/self/fd/3');
      assert.deepEqual(Object.keys(spawned.env).sort(), [
        'DOTNET_CLI_TELEMETRY_OPTOUT',
        'DOTNET_NOLOGO',
        'NO_COLOR',
        'TMPDIR',
      ]);
      assert.equal(spawned.env.PATH, undefined);
      assert.equal(spawned.env.DOTNET_ROOT, undefined);
      const opened = records.findLast(
        (record) =>
          record.kind === 'open' && record.fd === spawned.fd && record.path === spawned.executable,
      );
      assert.ok(opened);
      assert.equal(opened.dev, spawned.dev);
      assert.equal(opened.ino, spawned.ino);
      const spawnIndex = records.indexOf(spawned);
      const openIndex = records.indexOf(opened);
      assert.equal(
        records
          .slice(openIndex + 1, spawnIndex)
          .some((record) => record.kind === 'close' && record.fd === spawned.fd),
        false,
      );
      const launch = records.find((record) => record.kind === 'launch').document;
      assert.equal(launch.action_source_sha, actionT);
      assert.equal(launch.payload_sha256, receipt.members[0].sha256);
      assert.equal(launch.build_discriminator, 'r7-d0');
      assert.equal(launch.cancellation, 'active');
      const nativeClose = records.find((record) => record.kind === 'native-close');
      assert.equal(nativeClose.signal, null);
      assert.equal(nativeClose.code, exit.code);
      const status = options.nativeCancel
        ? 'cancelled'
        : options.untrusted
          ? 'skipped_untrusted_event'
          : 'credentials_missing';
      assert.equal(nativeClose.completion.status, status);
      assert.equal(actual.status, status);
      assert.equal(exit.code, nativeClose.completion.process_exit_code);
      assert.equal(actual['model-calls'], '0');
      assert.equal(actual['provider-attempts'], '0');
      assert.equal(Object.keys(actual).length, 14);
      assert.match(summaryText, new RegExp(status, 'u'));
      assert.equal(existsSync(spawned.payloadRoot), false);
      assert.equal(existsSync(spawned.bridgeRoot), false);
      assert.equal(existsSync(`/proc/${spawned.pid}`), false);
      if (options.nativeCancel)
        assert.ok(
          records.some((record) => record.kind === 'forwarded' && record.signal === 'SIGTERM'),
        );
    }
    assert.deepEqual(await readdir(temp), []);
    results.push({
      name,
      status: actual.status,
      exit: exit.code,
      native: !!spawned,
      requests: requests.length,
    });
  } finally {
    clearTimeout(watchdog);
    try {
      process.kill(-child.pid, 'SIGKILL');
    } catch {}
    server.closeAllConnections();
    await new Promise((done) => server.close(done));
  }
}

try {
  await run('null-map', { unbound: true });
  await run('wrong-repository', { wrongRepository: true });
  await run('moving-ref', { wrongRef: true });
  await run('installed-content-mismatch', { contentMismatch: true });
  await run('archive-digest-mismatch', { badDigest: true });
  await run('acquisition-signal', { acquisitionCancel: true });
  await run('exact-sha');
  await run('immutable-tag', { tag: true });
  await run('untrusted-event', { untrusted: true });
  await run('native-signal', { nativeCancel: true });
  console.log(
    'D3_GENERATED_ACTION_NATIVE ' +
      JSON.stringify({
        bundleSha256: sha256(bundle),
        originalArchiveSha256: receipt.archiveSha256,
        sourceS: receipt.identity.sourceCommit,
        builderW,
        actionT,
        buildId: receipt.identity.buildId,
        tagRequiresImmutablePublication: true,
        cases: results,
      }),
  );
} finally {
  await rm(work, { recursive: true, force: true });
}
