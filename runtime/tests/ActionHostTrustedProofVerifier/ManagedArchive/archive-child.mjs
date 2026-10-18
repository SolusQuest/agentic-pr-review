// Literal checked Action, unchanged D4 supervision, original D1 archive and real .NET Host.
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { copyFile, mkdir, readFile, writeFile } from 'node:fs/promises';
import { createServer as httpServer } from 'node:http';
import { createServer as httpsServer } from 'node:https';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { assertIsolatedBoundary } from './isolated-boundary.mjs';
import {
  createOfficialPlatform,
  githubKey,
  providerKey,
  repository,
  stateKey,
} from './official-platform.mjs';
import {
  actionSha,
  builderSha,
  createGitHubFixture,
  objectHash,
  workflowSha,
} from './github-fixture.mjs';
import { createProviderFixture, reasoningCanary } from './provider-fixture.mjs';
const root = process.argv[2];
let stage = 'boundary';
let diagnostic = {};
const servers = [],
  errors = [];
try {
  await assertIsolatedBoundary(root);
  const sdkProbe = spawnSync(join(root, 'runtime/dotnet'), ['--list-sdks'], {
    env: {},
    encoding: 'utf8',
  });
  assert.equal(sdkProbe.status, 0);
  assert.equal(sdkProbe.stdout, '');
  const settings = JSON.parse(await readFile(join(root, 'archive-settings.json'), 'utf8'));
  const repo = settings.repo;
  const { inspectManagedPackage } = await import(
    join(repo, 'scripts/release/build-payload.format.mjs')
  );
  const { readGeneratedActionAudit: audit } = await import(
    join(repo, 'tests/distribution/generated-action-audit.mjs')
  );
  const receipt = JSON.parse(await readFile(join(root, 'package/receipt.json'), 'utf8'));
  const archive = await readFile(join(root, 'package', receipt.archiveName));
  const inspected = inspectManagedPackage(archive, receipt);
  const closure = inspected.manifest.buildInputs;
  const solus = closure.dependencies.filter((x) => x.id.startsWith('solusagent.'));
  assert.equal(solus.length, 5);
  assert.ok(solus.every((x) => x.version === '0.1.0-internal.4'));
  assert.equal(settings.compiled.compiledIdentity, receipt.identity.informationalVersion);
  const runtime = await readFile(join(root, 'runtime.tar.gz'));
  const bundle = await readFile(join(repo, '.github/actions/agentic-pr-review/dist/index.js'));
  const workflow = (
    await readFile(
      join(
        repo,
        'runtime/tests/fixtures/action-host/trusted-proof-payload/workflow/r4-trusted-proof.yml.template',
      ),
      'utf8',
    )
  )
    .replaceAll('__ACTION_SOURCE_SHA__', actionSha)
    .replaceAll('__PAYLOAD_SHA256__', receipt.archiveSha256);
  const github = createGitHubFixture(workflow),
    provider = createProviderFixture(github);
  const map = {
    formatVersion: 1,
    repository,
    tag: 'payload-' + receipt.identity.releaseVersion,
    builder: {
      repository,
      workflowPath: '.github/workflows/r4-trusted-proof.yml',
      workflowCommit: builderSha,
    },
    payload: receipt,
  };
  let actionMetadata;
  const official = createOfficialPlatform(() => `http://127.0.0.1:${nodeServer.address().port}`);
  const guard = (handle) => (request, response) => {
    void handle(request, response).catch((error) => {
      errors.push(error);
      response.destroy();
    });
  };
  const nodeServer = httpServer(
    guard(async (request, response) => {
      if (await official.handle(request, response)) return;
      assert.equal(request.method, 'GET');
      assert.equal(request.headers.authorization, undefined);
      const url = new URL(request.url, 'http://fixture');
      if (url.pathname === '/dotnet/Runtime/10.0.13/dotnet-runtime-10.0.13-linux-x64.tar.gz')
        return response.writeHead(200, { 'Content-Length': runtime.length }).end(runtime);
      if (url.pathname === `/repos/${repository}/releases/tags/${map.tag}`)
        return send({ id: 123, tag_name: map.tag, draft: false });
      if (url.pathname === `/repos/${repository}/releases/123/assets`)
        return send([
          {
            id: 456,
            name: receipt.archiveName,
            state: 'uploaded',
            size: receipt.archiveSize,
            browser_download_url: `https://github.com/${repository}/releases/download/${map.tag}/${receipt.archiveName}`,
          },
        ]);
      if (url.pathname === `/${repository}/releases/download/${map.tag}/${receipt.archiveName}`)
        return response.writeHead(200, { 'Content-Length': archive.length }).end(archive);
      const prefix = `/repos/${repository}/contents/.github/actions/agentic-pr-review/`;
      if (url.pathname.startsWith(prefix)) {
        assert.equal(url.searchParams.get('ref'), actionSha);
        assert.equal(request.headers.accept, 'application/vnd.github.object+json');
        const value = actionMetadata[url.pathname.slice(prefix.length)];
        assert.ok(value);
        return send({ ...value, content: '', encoding: 'none' });
      }
      assert.fail('d5_node_acquisition_route');
      function send(value) {
        const bytes = Buffer.from(JSON.stringify(value));
        response
          .writeHead(200, { 'Content-Type': 'application/json', 'Content-Length': bytes.length })
          .end(bytes);
      }
    }),
  );
  const tlsServer = httpsServer(
    {
      key: await readFile(join(root, 'server.key')),
      cert: await readFile(join(root, 'server.pem')),
    },
    guard(async (request, response) => {
      if (request.headers.host === 'api.deepseek.com') return provider.handle(request, response);
      return github.handle(request, response);
    }),
  );
  tlsServer.on('tlsClientError', () => {});
  for (const [server, port] of [
    [nodeServer, 0],
    [tlsServer, 443],
  ]) {
    await new Promise((done, reject) => {
      server.once('error', reject);
      server.listen(port, '127.0.0.1', done);
    });
    servers.push(server);
  }
  const results = [];
  async function run(name, ordinal, options = {}) {
    stage = name.toLowerCase();
    const invocation = join(root, 'case-' + results.length),
      action = join(invocation, 'action'),
      runner = join(invocation, 'runner'),
      workspace = join(invocation, 'workspace'),
      temp = join(invocation, 'tmp');
    for (const path of [join(action, 'dist'), runner, workspace, temp])
      await mkdir(path, { recursive: true, mode: 0o700 });
    for (const path of ['action.yml', 'dist/index.js'])
      await copyFile(join(repo, '.github/actions/agentic-pr-review', path), join(action, path));
    assert.deepEqual(await readFile(join(action, 'dist/index.js')), bundle);
    await writeFile(join(action, 'package.json'), '{"type":"module"}\n');
    await writeFile(join(action, 'payload-map.json'), JSON.stringify(map));
    actionMetadata = {};
    for (const path of ['action.yml', 'dist/index.js', 'payload-map.json']) {
      const bytes = await readFile(join(action, path));
      actionMetadata[path] = {
        type: 'file',
        path: '.github/actions/agentic-pr-review/' + path,
        sha: objectHash('blob', bytes),
        size: bytes.length,
      };
    }
    const event = join(invocation, 'event.json'),
      output = join(invocation, 'output'),
      summary = join(invocation, 'summary'),
      auditPath = join(invocation, 'audit'),
      config = join(invocation, 'config.json');
    const runId = 1101 + results.length;
    github.configure(ordinal, runId);
    github.holdReadback(!!options.knownCommit);
    github.archiveMode(options.archiveMode ?? 'success');
    const runtimeToken = official.configure(runId);
    provider.configure(options.mode, options.prefix ?? null);
    for (const file of [output, summary, auditPath]) await writeFile(file, '', { mode: 0o600 });
    await writeFile(
      event,
      JSON.stringify({
        inputs: { 'pr-number': '147' },
        repository: { id: 42, full_name: repository },
        sender: { id: 7, login: 'maintainer' },
      }),
    );
    await writeFile(
      config,
      JSON.stringify({
        audit: auditPath,
        origin: `http://127.0.0.1:${nodeServer.address().port}`,
        payloadSubstitution: !!options.substitution,
        unsupportedRuntime: !!options.unsupportedRuntime,
      }),
    );
    const env = {
      TMPDIR: temp,
      TMP: temp,
      TEMP: temp,
      RUNNER_TEMP: runner,
      GITHUB_WORKSPACE: workspace,
      GITHUB_EVENT_PATH: event,
      GITHUB_REPOSITORY: repository,
      GITHUB_REPOSITORY_ID: '42',
      GITHUB_RUN_ID: String(runId),
      GITHUB_RUN_ATTEMPT: '1',
      GITHUB_WORKFLOW_REF: `${repository}/.github/workflows/r4-trusted-proof.yml@refs/heads/main`,
      GITHUB_WORKFLOW_SHA: workflowSha,
      GITHUB_ACTION_REPOSITORY: repository,
      GITHUB_ACTION_REF: actionSha,
      GITHUB_OUTPUT: output,
      GITHUB_STEP_SUMMARY: summary,
      GITHUB_API_URL: `http://127.0.0.1:${nodeServer.address().port}`,
      ACTIONS_RESULTS_URL: `http://127.0.0.1:${nodeServer.address().port}`,
      ACTIONS_RUNTIME_TOKEN: runtimeToken,
      ACTIONS_ARTIFACT_UPLOAD_CONCURRENCY: '1',
      APR_D4_FIXTURE_CONFIG: config,
      'INPUT_PR-NUMBER': '147',
      'INPUT_STATE-MODE': options.reset ? 'reset' : 'auto',
      'INPUT_CONFIG-PATH': '.github/agentic-pr-review/trusted-proof.json',
      'INPUT_GITHUB-TOKEN': githubKey,
      'INPUT_STATE-KEY': options.wrongKey ? Buffer.alloc(32, 0x45).toString('base64') : stateKey,
      'INPUT_PROVIDER-API-KEY': options.noProvider ? '' : providerKey,
      DOTNET_STARTUP_HOOKS: join(workspace, 'hostile.dll'),
      DOTNET_ADDITIONAL_DEPS: workspace,
      DOTNET_SHARED_STORE: workspace,
      COREHOST_TRACE: '1',
      COREHOST_TRACEFILE: join(workspace, 'hostile.trace'),
      DOTNET_ROOT: workspace,
      NUGET_PACKAGES: workspace,
    };
    const child = spawn(
      process.execPath,
      [
        '--import',
        join(repo, 'tests/distribution/managed-generated-action-preload.mjs'),
        join(action, 'dist/index.js'),
      ],
      { cwd: workspace, env, stdio: ['ignore', 'pipe', 'pipe'], detached: true },
    );
    const chunks = [];
    let bytes = 0;
    for (const stream of [child.stdout, child.stderr])
      stream.on('data', (chunk) => {
        bytes += chunk.length;
        if (bytes > 1024 * 1024) child.kill('SIGKILL');
        else chunks.push(chunk);
      });
    const watchdog = setTimeout(() => {
      try {
        process.kill(-child.pid, 'SIGKILL');
      } catch {}
    }, 70_000);
    const cancel =
      options.mode === 'cancel'
        ? setInterval(() => {
            if (provider.count) {
              clearInterval(cancel);
              child.kill('SIGTERM');
            }
          }, 20)
        : null;
    const killKnown = options.knownCommit
      ? setInterval(() => {
          if (!github.knownCommitBarrier) return;
          void audit(auditPath).then((records) => {
            const owned = records.find((x) => x.kind === 'spawn');
            if (owned?.pid) {
              clearInterval(killKnown);
              process.kill(owned.pid, 'SIGKILL');
            }
          });
        }, 20)
      : null;
    let exit;
    try {
      exit = await new Promise((done, reject) => {
        child.once('error', reject);
        child.once('close', (code, signal) => done({ code, signal }));
      });
    } finally {
      clearTimeout(watchdog);
      if (cancel) clearInterval(cancel);
      if (killKnown) clearInterval(killKnown);
    }
    diagnostic = {
      external_errors: errors.length,
      sends: provider.count,
      github_requests: github.calls.length,
      official_requests: official.calls.length,
    };
    if (errors[0])
      diagnostic.service_site =
        /(?:github-fixture|provider-fixture|official-platform|archive-child)\.mjs:[0-9]+/u.exec(
          errors[0].stack,
        )?.[0] ?? 'unknown';
    assert.equal(errors.length, 0, 'd5_external_service_refused');
    const records = await audit(auditPath, { settled: true }),
      launch = records.find((x) => x.kind === 'spawn'),
      close = records.find((x) => x.kind === 'managed-close');
    diagnostic.host_started = !!launch;
    diagnostic.host_exit = close?.code ?? -1;
    diagnostic.status =
      close?.completion?.payload?.status ?? close?.completion?.status ?? 'unavailable';
    const presentation = records.filter((x) => x.kind === 'presentation');
    assert.ok(presentation.length);
    for (const fact of presentation)
      for (const [key, value] of Object.entries(fact))
        if (key !== 'kind') assert.equal(value, true, 'd5_cleanup_' + key);
    const observedStdout = Buffer.concat(chunks).toString();
    const maskValues = new Set([
      githubKey,
      providerKey,
      stateKey,
      env['INPUT_STATE-KEY'],
      runtimeToken,
    ]);
    const visibleStdout = observedStdout
      .split('\n')
      .filter((line) => {
        if (!line.startsWith('::add-mask::')) return true;
        assert.ok(maskValues.has(line.slice('::add-mask::'.length)), 'd5_expected_mask_control');
        return false;
      })
      .join('\n');
    const text =
      visibleStdout +
      (await readFile(summary, 'utf8')) +
      (await readFile(output, 'utf8')) +
      (github.comment?.body ?? '');
    for (const canary of [
      githubKey,
      providerKey,
      stateKey,
      reasoningCanary,
      root,
      'PRIVATE_D5_TRUSTED_POLICY',
    ])
      assert.equal(text.includes(canary), false, 'd5_public_canary');
    if (launch) {
      assert.equal(launch.command, '/proc/self/fd/3');
      assert.equal(launch.executedCommand, launch.command);
      assert.equal(
        launch.args.at(-1),
        '/proc/self/fd/4/agentic-pr-review/AgenticPrReview.Runtime.dll',
      );
      assert.deepEqual(Object.keys(launch.env).sort(), [
        'DOTNET_CLI_TELEMETRY_OPTOUT',
        'DOTNET_EnableDiagnostics',
        'DOTNET_NOLOGO',
        'NO_COLOR',
        'TMPDIR',
      ]);
      assert.equal(existsSync(join(workspace, 'hostile.trace')), false);
    }
    const completion = close?.completion?.payload ?? close?.completion;
    const outputText = await readFile(output, 'utf8');
    const reported = {};
    const outputLines = outputText.trimEnd().split('\n');
    assert.equal(outputLines.length % 3, 0);
    for (let index = 0; index < outputLines.length; index += 3) {
      const [key, delimiter] = outputLines[index].split('<<');
      assert.equal(outputLines[index + 2], delimiter);
      assert.equal(Object.hasOwn(reported, key), false);
      reported[key] = outputLines[index + 1];
    }
    const statusMatch = /^status<<[^\n]+\n([^\n]+)\n/mu.exec(outputText);
    assert.ok(statusMatch);
    const status = statusMatch[1];
    results.push({
      name,
      status,
      exit: exit.code,
      sends: provider.count,
      actual_host: !!launch,
      completion_status: completion?.status ?? null,
    });
    return { status, completion, records, exit, reported, prefix: provider.previousContext };
  }
  function assertAccounting(result, sends) {
    assert.equal(result.reported['model-calls'], String(sends));
    assert.equal(result.reported['provider-attempts'], String(sends));
    assert.equal(result.reported['provider-retries'], '0');
    assert.equal(result.reported['provider-failed-attempts'], '0');
    assert.equal(result.reported['input-tokens'], String(sends * 10));
    assert.equal(result.reported['input-cache-hit-tokens'], String(sends * 4));
    assert.equal(result.reported['input-cache-miss-tokens'], String(sends * 6));
    assert.equal(result.reported['output-tokens'], String(sends * 5));
  }
  async function diagnosePreparation(records) {
    const launch = records.find((x) => x.kind === 'launch')?.document;
    if (!launch) return;
    const file = join(root, 'diagnostic-launch.json');
    await writeFile(file, JSON.stringify(launch), { mode: 0o600, flag: 'wx' });
    const temp = join(root, 'diagnostic-tmp');
    await mkdir(temp, { mode: 0o700 });
    for (const reflection of ['false', 'true']) {
      const child = spawn(
        join(root, 'runtime/dotnet'),
        [
          'exec',
          '--fx-version',
          '10.0.13',
          '--roll-forward',
          'Disable',
          join(
            root,
            'preparation-' + reflection,
            'AgenticPrReview.Runtime.ActionHostTrustedProofVerifier.dll',
          ),
          file,
        ],
        {
          env: {
            TMPDIR: temp,
            DOTNET_EnableDiagnostics: '0',
            DOTNET_NOLOGO: '1',
            DOTNET_CLI_TELEMETRY_OPTOUT: '1',
          },
          stdio: ['ignore', 'pipe', 'pipe'],
        },
      );
      const chunks = [];
      let size = 0;
      for (const stream of [child.stdout, child.stderr])
        stream.on('data', (bytes) => {
          size += bytes.length;
          if (size > 8192) child.kill('SIGKILL');
          else chunks.push(bytes);
        });
      const timeout = setTimeout(() => child.kill('SIGKILL'), 45_000);
      await new Promise((done, reject) => {
        child.once('error', reject);
        child.once('close', done);
      }).finally(() => clearTimeout(timeout));
      for (const line of Buffer.concat(chunks).toString().split('\n')) {
        if (/^APR_D5_PREPARATION_(PROBE|HOST) [a-zA-Z0-9=_ -]{1,512}$/u.test(line))
          process.stderr.write(line + '\n');
      }
    }
  }
  const fresh = await run('fresh', 0);
  if (fresh.status !== 'reviewed') await diagnosePreparation(fresh.records);
  assert.equal(fresh.status, 'reviewed');
  assertAccounting(fresh, 2);
  assert.equal(provider.count, 2);
  assert.equal(github.writes, 1);
  assert.ok(official.size > 0);
  const accepted = official.snapshot(),
    acceptedComment = github.snapshot(),
    prefix = fresh.prefix;
  const repeated = await run('same-head-recovery', 0, { noProvider: true });
  assert.equal(repeated.status, 'reviewed');
  assertAccounting(repeated, 0);
  assert.equal(provider.count, 0);
  assert.equal(github.writes, 1);
  const second = await run('restored-second-head', 1, { prefix });
  assert.equal(second.status, 'reviewed');
  assertAccounting(second, 2);
  assert.equal(provider.count, 2);
  assert.equal(github.writes, 2);
  const third = await run('restored-third-head', 2, { prefix: second.prefix });
  assert.equal(third.status, 'reviewed');
  assertAccounting(third, 2);
  assert.equal(provider.count, 2);
  assert.equal(github.writes, 3);
  for (const mode of ['provider-failure', 'stale', 'cancel']) {
    official.restore(accepted);
    github.restore(acceptedComment);
    const before = github.writes;
    const result = await run(mode, 1, { prefix, mode });
    assert.notEqual(result.status, 'reviewed');
    assert.equal(github.writes, before);
    assert.deepEqual(github.snapshot(), acceptedComment);
  }
  official.restore(accepted);
  github.restore(acceptedComment);
  const beforeKnown = github.writes;
  const crashed = await run('known-commit-crash', 1, { prefix, knownCommit: true });
  assert.notEqual(crashed.status, 'reviewed');
  assert.equal(github.writes, beforeKnown + 1);
  const recovered = await run('known-commit-recovery', 1, { noProvider: true });
  assert.equal(recovered.status, 'reviewed');
  assertAccounting(recovered, 0);
  assert.equal(provider.count, 0);
  assert.equal(github.writes, beforeKnown + 1);
  const corrupted = accepted.map(([id, item]) => [
    id,
    { metadata: structuredClone(item.metadata), bytes: Buffer.from(item.bytes) },
  ]);
  for (const [, item] of corrupted) item.bytes[item.bytes.length - 1] ^= 1;
  official.restore(corrupted);
  github.restore(acceptedComment);
  const beforeCorrupt = github.writes;
  const refusedCorrupt = await run('corrupt-context-artifact', 1, { prefix });
  assert.notEqual(refusedCorrupt.status, 'reviewed');
  assert.equal(provider.count, 0);
  assert.equal(github.writes, beforeCorrupt);
  for (const archiveMode of ['bad-redirect', 'wrong-archive']) {
    official.restore(accepted);
    github.restore(acceptedComment);
    const before = github.writes;
    const result = await run(archiveMode, 1, { prefix, archiveMode });
    assert.equal(result.status, 'snapshot_incomplete');
    assert.equal(provider.count, 0);
    assert.equal(github.writes, before);
  }
  for (const options of [
    { wrongKey: true },
    { reset: true },
    { noProvider: true },
    { substitution: true },
    { unsupportedRuntime: true },
  ]) {
    official.restore(accepted);
    github.restore(acceptedComment);
    const before = github.writes;
    const result = await run(Object.keys(options)[0], 1, { ...options, prefix });
    assert.notEqual(result.status, 'reviewed');
    assert.equal(provider.count, 0);
    assert.equal(github.writes, before);
  }
  assert.ok(github.calls.some((x) => x.host === 'codeload.github.com'));
  assert.ok(official.calls.some((x) => x.operation === 'download'));
  assert.equal(provider.receipts.filter((x) => x.kind === 'completed-round').length >= 3, true);
  process.stdout.write(
    'APR_D5_MANAGED_ARCHIVE ' +
      JSON.stringify({
        kind: 'apr-d5-managed-archive-v1',
        role: settings.role,
        source_commit: settings.source,
        source_tree: settings.tree,
        compiled_identity: settings.compiled.compiledIdentity,
        archive_sha256: receipt.archiveSha256,
        archive_inventory: receipt.members.length,
        build_id: receipt.identity.buildId,
        builder: closure.toolchain,
        dependency_closure: closure.dependencies,
        lock_sha256: closure.lockSha256,
        assets_sha256: closure.assetsSha256,
        producer_source_tree: receipt.identity.sourceTree,
        action_bundle_sha256: (await import('node:crypto'))
          .createHash('sha256')
          .update(bundle)
          .digest('hex'),
        action_source_projection: actionSha,
        builder_projection: builderSha,
        runner: 'ubuntu-24.04-x64',
        runtime: '10.0.13',
        private_network_namespace: true,
        private_mount_namespace: true,
        privileged_payload: false,
        external_routes: 0,
        sdk_count: 0,
        cases: results,
        context: provider.receipts,
        official_requests: official.calls.length,
        github_requests: github.calls.length,
        live_provider_requests: 0,
      }) +
      '\n',
  );
} catch (error) {
  diagnostic.assertion_site =
    /(?:github-fixture|provider-fixture|official-platform|archive-child)\.mjs:[0-9]+/u.exec(
      error.stack,
    )?.[0] ?? 'unknown';
  process.stderr.write('APR_D5_ARCHIVE_DIAGNOSTIC ' + JSON.stringify(diagnostic) + '\n');
  // No request/response/body/path/credential bytes are published on a failed proof.
  process.stderr.write(
    'APR_D5_ARCHIVE_FAILURE stage=' +
      stage +
      ' code=' +
      (error.code === 'ERR_ASSERTION' ? 'assertion' : 'harness') +
      '\n',
  );
  process.exitCode = 1;
} finally {
  for (const server of servers) {
    server.closeAllConnections();
    await new Promise((done) => server.close(done));
  }
}
