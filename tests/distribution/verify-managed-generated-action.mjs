// Literal checked Action -> ordinary managed resolver -> pinned runtime -> real framed APR Host.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync } from 'node:fs';
import { copyFile, mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import { createConnection } from 'node:net';
import { dirname, join, resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { inspectManagedPackage, sha256 } from '../../scripts/release/build-payload.format.mjs';
import { readGeneratedActionAudit as audit } from './generated-action-audit.mjs';
import {
  createOfficialPlatform,
  syntheticGitHubToken,
  syntheticRuntimeToken,
} from './managed-generated-official-platform.mjs';

assert.equal(process.platform, 'linux');
assert.equal(process.arch, 'x64');
const [repoArgument, archiveArgument, receiptArgument] = process.argv.slice(2);
const repo = resolve(repoArgument);
const receipt = JSON.parse(await readFile(receiptArgument, 'utf8'));
const archive = await readFile(archiveArgument);
inspectManagedPackage(archive, receipt);
const runtimeArchive = await readFile(process.argv[5]);
assert.equal(runtimeArchive.length, 36_648_012);
assert.equal(
  createHash('sha512').update(runtimeArchive).digest('hex'),
  '9574d95364110409416735e7842796f8d614c21d74cb25d204a3e4cfd11b2c05efdf19ca89b8069650a04f64add2705967dcb04b8665a36d7a3f30a5d8c05afe',
);
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
const work = await mkdtemp(join(tmpdir(), 'apr-d4-'));
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
async function until(predicate, timeout = 15_000) {
  const end = performance.now() + timeout;
  while (performance.now() < end) {
    const result = await predicate();
    if (result) return result;
    await delay(10);
  }
  throw new Error('generated_action_fixture_barrier_timeout');
}

async function probeOfficialBridge(launch, officialCount) {
  let correlation = 0;
  const command = (payload, discriminator = 'r7-d0', expectedFailure = 'none') =>
    new Promise((resolve, reject) => {
      const socket = createConnection(launch.artifact_bridge_endpoint);
      const chunks = [];
      let size = 0;
      socket.setTimeout(15_000, () => socket.destroy(new Error('official_bridge_probe_timeout')));
      socket.once('error', reject);
      socket.on('data', (bytes) => {
        size += bytes.length;
        if (size > 256 * 1024 + 4) socket.destroy(new Error('official_bridge_response_overflow'));
        else chunks.push(bytes);
      });
      socket.once('connect', () => {
        const bytes = Buffer.from(
          JSON.stringify({
            build_discriminator: discriminator,
            payload: { ...payload, correlation_id: 'd4-proof-' + ++correlation },
          }),
        );
        const message = Buffer.alloc(bytes.length + 8);
        message.writeUInt32BE(bytes.length);
        bytes.copy(message, 4);
        socket.write(message);
      });
      socket.once('end', () => {
        try {
          const bytes = Buffer.concat(chunks);
          assert.equal(bytes.length, bytes.readUInt32BE(0) + 4);
          const result = JSON.parse(bytes.subarray(4));
          assert.equal(result.build_discriminator, 'r7-d0');
          assert.equal(result.payload.operation, payload.operation);
          assert.equal(result.payload.correlation_id, 'd4-proof-' + correlation);
          assert.equal(result.payload.failure, expectedFailure);
          resolve(result.payload);
        } catch (error) {
          reject(error);
        }
      });
    });
  const name = 'd4-generated-managed-proof';
  const before = officialCount();
  const denied = await command(
    { operation: 'list_exact', name, maximum_objects: '4' },
    'r4-w2',
    'invalid',
  );
  assert.equal(denied.complete, false);
  await new Promise((resolve, reject) => {
    const socket = createConnection(launch.artifact_bridge_endpoint);
    const received = [];
    socket.setTimeout(5_000, () =>
      socket.destroy(new Error('malformed_bridge_frame_not_rejected')),
    );
    socket.once('error', reject);
    socket.on('data', (bytes) => received.push(bytes));
    socket.once('connect', () => {
      const bytes = Buffer.alloc(10);
      bytes.writeUInt32BE(2);
      bytes.write('{}', 4);
      socket.write(bytes);
    });
    socket.once('end', () => {
      try {
        assert.equal(Buffer.concat(received).length, 0);
        resolve();
      } catch (error) {
        reject(error);
      }
    });
  });
  assert.equal(officialCount(), before);
  const initial = await command({ operation: 'list_exact', name, maximum_objects: '4' });
  assert.deepEqual(initial.objects, []);
  const staging = join(dirname(launch.artifact_bridge_endpoint), 'artifact-staging');
  const operation = join(staging, 'proof');
  await mkdir(operation, { mode: 0o700 });
  const encryptedBytes = Buffer.from('opaque synthetic encrypted bytes for transport proof');
  await Promise.all([
    writeFile(join(operation, 'source'), encryptedBytes, { mode: 0o600 }),
    writeFile(join(operation, 'artifact-envelope.json'), '', { mode: 0o600 }),
    writeFile(join(operation, 'download'), '', { mode: 0o600 }),
  ]);
  const uploaded = await command({
    operation: 'upload_immutable',
    name,
    source_relative_path: 'proof/source',
    encrypted_object_digest: sha256(encryptedBytes),
    minimum_expires_at_unix_seconds: String(Math.floor(Date.now() / 1000) + 600),
  });
  assert.equal(uploaded.mutation_state, 'committed');
  const expected = uploaded.metadata;
  assert.equal(expected.encrypted_object_digest, sha256(encryptedBytes));
  for (const payload of [
    { operation: 'metadata', name, object_id: expected.object_id, maximum_bytes: '1024' },
    { operation: 'readback_exact', expected },
    {
      operation: 'download',
      expected,
      destination_relative_path: 'proof/download',
      maximum_bytes: '1024',
    },
  ])
    assert.deepEqual((await command(payload)).metadata, expected);
  assert.deepEqual(await readFile(join(operation, 'download')), encryptedBytes);
  assert.equal(
    (await command({ operation: 'delete_exact', expected })).mutation_state,
    'committed',
  );
  assert.deepEqual(
    (await command({ operation: 'list_exact', name, maximum_objects: '4' })).objects,
    [],
  );
}

async function run(name, options = {}) {
  const root = join(work, String(results.length));
  const action = join(root, 'action'),
    temp = join(root, 'tmp'),
    runnerTemp = join(root, 'runner-temp'),
    workspace = join(root, 'workspace');
  await Promise.all([
    mkdir(join(action, 'dist'), { recursive: true }),
    mkdir(temp, { recursive: true }),
    mkdir(runnerTemp, { recursive: true }),
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
  else
    await writeFile(
      join(action, 'payload-map.json'),
      JSON.stringify(map) + (options.largeMap ? ' '.repeat(20 * 1024) : '') + '\n',
    );
  if (options.largeMap) {
    const size = (await readFile(join(action, 'payload-map.json'))).length;
    assert.ok(size > 16 * 1024 && size <= 256 * 1024);
  }
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
  let runtimeStarted = false;
  const platform = createOfficialPlatform(() => `http://127.0.0.1:${server.address().port}`);
  const serverErrors = [];
  const handleRequest = async (request, response) => {
    if (await platform.handle(request, response)) return;
    requests.push(request.url);
    assert.equal(request.method, 'GET');
    assert.equal(request.headers.authorization, undefined);
    if (request.url === '/dotnet/Runtime/10.0.13/dotnet-runtime-10.0.13-linux-x64.tar.gz') {
      runtimeStarted = true;
      if (options.runtimeRedirect) {
        response.writeHead(302, { location: 'https://example.invalid/runtime' }).end();
        return;
      }
      if (options.runtimeOversize) {
        response.writeHead(200);
        response.write(runtimeArchive);
        response.end(Buffer.from([0]));
        return;
      }
      response.writeHead(200, { 'Content-Length': runtimeArchive.length });
      if (options.runtimeTruncated) {
        response.end(runtimeArchive.subarray(0, 1024));
        return;
      }
      if (options.runtimeCancel) {
        response.write(runtimeArchive.subarray(0, 1));
        return;
      }
      if (options.runtimeBadDigest) {
        const changed = Buffer.from(runtimeArchive);
        changed[changed.length - 1] ^= 1;
        response.end(changed);
      } else response.end(runtimeArchive);
      return;
    }
    const prefix = '/repos/' + repository;
    let document;
    if (request.url === prefix + '/commits/' + receipt.identity.releaseVersion) {
      assert.equal(request.headers.accept, 'application/vnd.github+json');
      document = { sha: actionT };
    } else if (request.url.startsWith(prefix + '/contents/')) {
      const relative = request.url
        .slice((prefix + '/contents/.github/actions/agentic-pr-review/').length)
        .split('?')[0];
      assert.equal(new URL(request.url, 'http://fixture').searchParams.get('ref'), actionT);
      document = metadata[relative];
      // Real checked bundle exceeds 1 MB: generic Contents JSON is unsupported.
      if (request.headers.accept !== 'application/vnd.github.object+json') {
        response.writeHead(415).end();
        return;
      }
      document = { ...document, content: '', encoding: 'none' };
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
  };
  const server = createServer((request, response) => {
    void handleRequest(request, response).catch((error) => {
      serverErrors.push(error);
      response.destroy();
    });
  });
  await new Promise((done) => server.listen(0, '127.0.0.1', done));
  const config = join(root, 'config.json');
  await writeFile(
    config,
    JSON.stringify({
      audit: auditPath,
      origin: `http://127.0.0.1:${server.address().port}`,
      bridgeProbe: !!options.bridgeProbe,
      bridgeRelease: join(root, 'release-host'),
      failedExec: !!options.failedExec,
      unsupportedRuntime: !!options.unsupportedRuntime,
      payloadSubstitution: !!options.payloadSubstitution,
    }),
  );
  const env = {
    TMPDIR: temp,
    TMP: temp,
    TEMP: temp,
    RUNNER_TEMP: options.runnerWorkspace ? workspace : runnerTemp,
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
    APR_D4_FIXTURE_CONFIG: config,
    AGENTIC_PR_REVIEW_PREPARED_ROOT: workspace,
    AGENTIC_PR_REVIEW_PREPARED_EXECUTABLE: 'PRIVATE_D4_PATH_CANARY',
    AGENTIC_PR_REVIEW_PREPARED_PAYLOAD_SHA256: 'd'.repeat(64),
    AGENTIC_PR_REVIEW_ACTION_SOURCE_SHA: 'e'.repeat(40),
    AGENTIC_PR_REVIEW_PAYLOAD_BUILD_DISCRIMINATOR: 'r4-w2',
    AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE: options.tag
      ? 'measurement'
      : 'invalid-proof-profile',
    'INPUT_PR-NUMBER': '344',
    'INPUT_STATE-MODE': 'auto',
    'INPUT_PAYLOAD-MAP': 'PRIVATE_D4_PATH_CANARY',
    INPUT_EXECUTABLE: 'PRIVATE_D4_PATH_CANARY',
    'INPUT_PROOF-PROFILE': 'final-bootstrap',
  };
  if (options.bridgeProbe) {
    env['INPUT_GITHUB-TOKEN'] = syntheticGitHubToken;
    env.GITHUB_API_URL = `http://127.0.0.1:${server.address().port}`;
    env.ACTIONS_RESULTS_URL = `http://127.0.0.1:${server.address().port}`;
    env.ACTIONS_RUNTIME_TOKEN = syntheticRuntimeToken;
    env.ACTIONS_ARTIFACT_UPLOAD_CONCURRENCY = '1';
  }
  if (options.genericTempKey) {
    delete env.TMPDIR;
    delete env.TMP;
    delete env.TEMP;
    env[options.genericTempKey] = workspace;
  }
  if (options.missingRunnerTemp) delete env.RUNNER_TEMP;
  const args = [
    '--import',
    join(repo, 'tests/distribution/managed-generated-action-preload.mjs'),
    join(action, 'dist/index.js'),
  ];
  const child = options.managedCancel
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
    if (options.bridgeProbe) {
      const launch = await until(async () =>
        (await audit(auditPath)).find((record) => record.kind === 'launch'),
      );
      await probeOfficialBridge(launch.document, () => platform.calls.length);
      assert.ok(platform.calls.some((route) => route.endsWith('/CreateArtifact')));
      assert.ok(platform.calls.some((route) => route.endsWith('/FinalizeArtifact')));
      assert.ok(platform.calls.includes('signed-download'));
      assert.ok(platform.calls.some((route) => route.startsWith('DELETE ')));
      await writeFile(join(root, 'release-host'), 'release unchanged launch frame');
    }
    if (options.acquisitionCancel || options.runtimeCancel) {
      const wrapper = await until(
        async () =>
          (options.runtimeCancel ? runtimeStarted : assetStarted) &&
          (await audit(auditPath)).find((record) => record.kind === 'wrapper'),
      );
      process.kill(wrapper.pid, 'SIGTERM');
    }
    if (options.managedCancel) {
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
    assert.deepEqual(serverErrors, []);
    assert.equal(exit.signal, null);
    const records = await audit(auditPath, { settled: true }),
      spawned = records.find((record) => record.kind === 'spawn');
    const actual = outputs(await readFile(output, 'utf8'));
    const summaryText = await readFile(summary, 'utf8');
    const publicText = Buffer.concat([...stdout, ...stderr]).toString('utf8') + summaryText;
    for (const canary of [work, 'PRIVATE_D4_PATH_CANARY', receipt.archiveSha256])
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
        ownedParentsGone: true,
        bridgeGone: true,
        admittedHandleClosed: true,
        runtimeGone: true,
      });
    if (
      options.unbound ||
      options.wrongRef ||
      options.wrongRepository ||
      options.contentMismatch ||
      options.badDigest ||
      options.runtimeBadDigest ||
      options.runtimeCancel ||
      options.runtimeRedirect ||
      options.runtimeOversize ||
      options.runtimeTruncated ||
      options.unsupportedRuntime ||
      options.payloadSubstitution ||
      options.acquisitionCancel ||
      options.runnerWorkspace ||
      options.missingRunnerTemp
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
      if (options.unsupportedRuntime || options.payloadSubstitution)
        assert.ok(records.some((record) => record.kind === 'resource-fault' && record.applied));
    } else if (options.failedExec) {
      assert.ok(spawned);
      assert.equal(spawned.executedCommand, '/definitely-absent-d4-host-exec');
      assert.equal(spawned.pid, undefined);
      const close = records.find((record) => record.kind === 'managed-close');
      assert.ok(close);
      assert.notEqual(close.code, 0);
      assert.equal(close.signal, null);
      assert.equal(close.completion, undefined);
      assert.equal(exit.code, 1);
      assert.equal(actual.status, 'failed');
      assert.equal(Object.keys(actual).length, 4);
    } else {
      assert.ok(spawned);
      assert.deepEqual(spawned.args, [
        'exec',
        '--runtimeconfig',
        '/proc/self/fd/4/agentic-pr-review/AgenticPrReview.Runtime.runtimeconfig.json',
        '--depsfile',
        '/proc/self/fd/4/agentic-pr-review/AgenticPrReview.Runtime.deps.json',
        '--fx-version',
        '10.0.13',
        '--roll-forward',
        'Disable',
        '/proc/self/fd/4/agentic-pr-review/AgenticPrReview.Runtime.dll',
      ]);
      assert.equal(spawned.command, '/proc/self/fd/3');
      assert.equal(dirname(spawned.stagingParent), runnerTemp);
      assert.equal(spawned.parentMode, 0o700);
      assert.equal(dirname(spawned.runtimeParent), runnerTemp);
      assert.equal(existsSync(spawned.runtimeParent), false);
      assert.deepEqual(Object.keys(spawned.env).sort(), [
        'DOTNET_CLI_TELEMETRY_OPTOUT',
        'DOTNET_EnableDiagnostics',
        'DOTNET_NOLOGO',
        'NO_COLOR',
        'TMPDIR',
      ]);
      assert.equal(spawned.env.PATH, undefined);
      assert.equal(spawned.env.DOTNET_ROOT, undefined);
      const opened = records.findLast(
        (record) =>
          record.kind === 'open' && record.fd === spawned.fd && record.path.endsWith('/dotnet'),
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
      assert.equal(launch.payload_sha256, receipt.archiveSha256);
      assert.equal(launch.build_discriminator, 'r7-d0');
      assert.equal(launch.cancellation, 'active');
      const managedClose = records.find((record) => record.kind === 'managed-close');
      assert.equal(managedClose.signal, null);
      assert.equal(managedClose.code, exit.code);
      const status = options.managedCancel
        ? 'cancelled'
        : options.untrusted
          ? 'skipped_untrusted_event'
          : 'credentials_missing';
      assert.equal(managedClose.completion.status, status);
      assert.equal(actual.status, status);
      assert.equal(exit.code, managedClose.completion.process_exit_code);
      assert.equal(actual['model-calls'], '0');
      assert.equal(actual['provider-attempts'], '0');
      assert.equal(Object.keys(actual).length, 14);
      assert.match(summaryText, new RegExp(status, 'u'));
      assert.equal(existsSync(spawned.payloadRoot), false);
      assert.equal(existsSync(spawned.bridgeRoot), false);
      assert.equal(existsSync(`/proc/${spawned.pid}`), false);
      if (options.managedCancel)
        assert.ok(
          records.some((record) => record.kind === 'forwarded' && record.signal === 'SIGTERM'),
        );
    }
    assert.deepEqual(await readdir(temp), []);
    assert.deepEqual(await readdir(runnerTemp), []);
    assert.deepEqual(await readdir(workspace), []);
    const parents = records.filter((record) => record.kind === 'payload-parent');
    for (const parent of parents) {
      assert.equal(dirname(parent.path), runnerTemp);
      assert.equal(parent.mode, 0o700);
      assert.equal(
        records.filter((record) => record.kind === 'removed' && record.path === parent.path).length,
        1,
      );
    }
    results.push({
      name,
      status: actual.status,
      exit: exit.code,
      managed: !!spawned?.pid,
      failedExec: !!options.failedExec,
      requests: requests.length,
      officialRequests: platform.calls.length,
    });
  } finally {
    clearTimeout(watchdog);
    try {
      process.kill(-child.pid, 'SIGKILL');
    } catch {}
    await closed;
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
  await run('runtime-digest-mismatch', { runtimeBadDigest: true });
  await run('runtime-acquisition-signal', { runtimeCancel: true });
  await run('runtime-redirect', { runtimeRedirect: true });
  await run('runtime-chunked-overflow', { runtimeOversize: true });
  await run('runtime-truncated', { runtimeTruncated: true });
  await run('unsupported-runtime-prerequisite', { unsupportedRuntime: true });
  await run('verified-nonentry-substitution', { payloadSubstitution: true });
  await run('acquisition-signal', { acquisitionCancel: true });
  await run('exact-sha');
  await run('large-managed-map', { largeMap: true });
  await run('real-failed-exec', { failedExec: true });
  await run('immutable-tag', { tag: true });
  await run('untrusted-event', { untrusted: true });
  await run('official-bridge-roundtrip', { untrusted: true, bridgeProbe: true });
  await run('managed-signal', { managedCancel: true });
  await run('runner-temp-in-workspace', { runnerWorkspace: true });
  await run('missing-runner-temp', { missingRunnerTemp: true });
  for (const genericTempKey of ['TMPDIR', 'TMP', 'TEMP'])
    await run('hostile-' + genericTempKey, { genericTempKey });
  console.log(
    'D4_GENERATED_ACTION_MANAGED ' +
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
