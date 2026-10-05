import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import YAML from 'yaml';

const root = fileURLToPath(new URL('../', import.meta.url));
const selectedSource =
  "${{ github.event_name == 'pull_request' && github.event.pull_request.head.sha || github.sha }}";
const required = ['runtime-core', 'r2-agent-loop', 'r4-action-host'];
const v2Workers = ['trusted-proof-payload-v2-first', 'trusted-proof-payload-v2-second'];
const artifactNamespace = 'r4-e2p-v2-${{ github.run_id }}-${{ github.run_attempt }}-';
const historicalSource = '5b5769753653bb3fd3e68cf8b7bb88a1bd350613';
const coreProofs = [
  'bash runtime/scripts/verify-runtime.sh test',
  'bash runtime/scripts/verify-runtime.sh framework',
  'bash runtime/scripts/verify-runtime.sh aot',
  'bash runtime/scripts/verify-live-agent.sh deterministic',
  'bash runtime/scripts/verify-live-agent.sh aot',
];
const r2Proof = 'bash runtime/scripts/verify-agent-loop.sh all';
const r4Proof = 'bash runtime/scripts/verify-action-host.sh "${{ matrix.mode }}"';
const cleanCheck = 'test -z "$(git status --porcelain)"';
const sourceCheck = 'test "$(git rev-parse HEAD)" = "$EXPECTED_SOURCE_SHA"';

function runs(job) {
  assert.ok(Array.isArray(job.steps), 'Every job must have explicit steps');
  return job.steps.filter((step) => typeof step.run === 'string');
}

function requireRun(job, command) {
  const matches = runs(job).filter((step) => step.run.trim() === command);
  assert.equal(matches.length, 1, `Required command must execute exactly once: ${command}`);
  assert.equal(matches[0].if, undefined, `Required command must be unconditional: ${command}`);
  return matches[0];
}

function assertSource(job, expected) {
  const checkouts = job.steps.filter((step) => step.uses === 'actions/checkout@v6');
  assert.equal(checkouts.length, 1, 'Each source job owns one checkout');
  assert.equal(checkouts[0].with?.ref, expected, 'Checkout must select its own required source');
  assert.equal(checkouts[0].with?.['persist-credentials'], false);
  assert.equal(checkouts[0].if, undefined);
  if (expected === selectedSource) {
    const identities = runs(job).filter((step) => step.run.includes(sourceCheck));
    assert.ok(identities.some((step) => step.env?.EXPECTED_SOURCE_SHA === selectedSource));
    assert.ok(identities.every((step) => step.if === undefined));
  }
}

function assertIsolatedLane(job, proofCommands, terminalClean, exactPackageSdk = false) {
  assert.equal(job['runs-on'], 'ubuntu-24.04');
  assert.equal(job.needs, undefined, 'Proof lanes must be independently schedulable');
  assert.equal(job.concurrency, undefined, 'Proof lanes must not serialize through concurrency');
  assert.equal(job.if, undefined, 'Proof lanes must not be conditionally skipped');
  assertSource(job, selectedSource);
  assert.equal(job.steps[0].uses, 'actions/checkout@v6');
  assert.equal(job.steps[0].with['fetch-depth'], 0);
  const node = job.steps.filter((step) => step.uses === 'actions/setup-node@v6');
  const dotnet = job.steps.filter((step) => step.uses === 'actions/setup-dotnet@v5');
  assert.equal(node.length, 1);
  assert.equal(node[0].with?.['node-version'], 24);
  assert.equal(node[0].with?.cache, 'npm');
  assert.equal(dotnet.length, 1);
  if (exactPackageSdk) {
    assert.deepEqual(dotnet[0].with, { 'dotnet-version': '10.0.109' });
    assert.deepEqual(dotnet[0].env, { DOTNET_INSTALL_DIR: '${{ runner.temp }}/r7-d1-dotnet' });
  } else {
    assert.equal(dotnet[0].with?.['global-json-file'], 'global.json');
  }
  const install = requireRun(job, 'npm ci');
  const apt = runs(job).find((step) =>
    step.run.includes('sudo apt-get install -y clang zlib1g-dev'),
  );
  assert.ok(apt && apt.if === undefined);
  const firstProof = Math.min(
    ...proofCommands.map((command) => job.steps.indexOf(requireRun(job, command))),
  );
  const cleanBefore = job.steps.findIndex(
    (step) =>
      step.run?.includes(cleanCheck) &&
      step.run.includes(sourceCheck) &&
      step.env?.EXPECTED_SOURCE_SHA === selectedSource &&
      step.if === undefined,
  );
  assert.ok(
    cleanBefore > job.steps.indexOf(install) && cleanBefore < firstProof,
    'Source identity and cleanliness must be checked after setup and before proofs',
  );
  for (const setup of [...node, ...dotnet, apt]) {
    assert.equal(setup.if, undefined);
    assert.ok(job.steps.indexOf(setup) < cleanBefore);
  }
  if (terminalClean) {
    assert.ok(
      job.steps.some(
        (step, index) =>
          index > firstProof && step.run?.trim() === cleanCheck && step.if === undefined,
      ),
      'Core/R2 must finish with a clean source',
    );
    assert.equal(job.steps.at(-1).run?.trim(), cleanCheck);
  }
}

function aggregateProgram(job) {
  assert.equal(job.name, 'runtime');
  assert.equal(job.if?.replaceAll(/\s/g, ''), '${{always()}}');
  assert.deepEqual([...job.needs].sort(), [...required].sort());
  assert.equal(job.steps.length, 1, 'Aggregate must not check out or execute repository code');
  const step = job.steps[0];
  assert.equal(step.uses, undefined);
  assert.equal(step.if, undefined);
  assert.deepEqual(step.env, { RUNTIME_CI_NEEDS: '${{ toJSON(needs) }}' });
  const program = /^node --input-type=module <<'NODE'\n([\s\S]+)\nNODE\n?$/.exec(step.run);
  assert.ok(program, 'Test the complete production aggregate program without shell masking');
  return program[1];
}

function inlineProgram(step, shell) {
  assert.equal(step.if, undefined);
  assert.equal(step.uses, undefined);
  const marker = " <<'NODE'\n";
  assert.ok(step.run.startsWith(shell + marker));
  assert.ok(step.run.endsWith('\nNODE\n'));
  return step.run.slice((shell + marker).length, -'\nNODE\n'.length);
}

function v2Programs(jobs) {
  for (const [index, role] of ['first', 'second'].entries()) {
    const job = jobs[v2Workers[index]];
    assert.deepEqual(Object.keys(job).sort(), ['permissions', 'runs-on', 'steps']);
    assert.deepEqual(job.permissions, { contents: 'read' });
    assert.equal(job['runs-on'], 'ubuntu-24.04');
    assertSource(job, selectedSource);
    assert.equal(
      job.steps.length,
      10,
      'Each V2 worker owns only a complete proof and receipt upload',
    );
    const [checkout, source, node, install, apt, dotnet, clean, proof, extraction, upload] =
      job.steps;
    assert.deepEqual(checkout.with, {
      ref: selectedSource,
      'fetch-depth': 0,
      'persist-credentials': false,
    });
    assert.equal(
      source.run,
      sourceCheck +
        '\n' +
        'test "$(git rev-parse \'HEAD^{tree}\')" = "$(git show -s --format=%T HEAD)"\n',
    );
    assert.deepEqual(source.env, { EXPECTED_SOURCE_SHA: selectedSource });
    assert.equal(node.uses, 'actions/setup-node@v6');
    assert.deepEqual(node.with, { 'node-version': 24 });
    assert.equal(install.run, 'npm ci');
    assert.equal(apt.run, 'sudo apt-get update\nsudo apt-get install -y clang strace zlib1g-dev\n');
    assert.equal(dotnet.uses, 'actions/setup-dotnet@v5');
    assert.deepEqual(dotnet.with, { 'dotnet-version': '10.0.109' });
    assert.deepEqual(dotnet.env, { DOTNET_INSTALL_DIR: '${{ runner.temp }}/r4-e2p-v2-dotnet' });
    assert.equal(clean.run, sourceCheck + '\n' + cleanCheck + '\n');
    assert.deepEqual(clean.env, { EXPECTED_SOURCE_SHA: selectedSource });
    assert.equal(
      proof.run,
      `bash runtime/scripts/verify-r4-trusted-proof-payload-v2.sh > "$RUNNER_TEMP/r4-e2p-v2-${role}.log"`,
    );
    assert.equal(
      proof.name,
      `Current-head R4 E2P ${role} clean production and verifier Native AOT build`,
    );
    const receipt = `"$RUNNER_TEMP/r4-e2p-v2-${role}.receipt"`;
    assert.equal(
      extraction.run,
      `grep '^APR_R4_E2P_RECEIPT_V2 ' "$RUNNER_TEMP/r4-e2p-v2-${role}.log" > ${receipt}\n` +
        `test "$(wc -l < ${receipt})" -eq 1\n` +
        `test "$(wc -c < ${receipt})" -le 32768\n`,
    );
    assert.equal(upload.uses, 'actions/upload-artifact@v7');
    assert.deepEqual(upload.with, {
      name: artifactNamespace + role,
      path: '${{ runner.temp }}/r4-e2p-v2-' + role + '.receipt',
      'if-no-files-found': 'error',
      'retention-days': 3,
      overwrite: false,
      'include-hidden-files': false,
      archive: true,
    });
    for (const step of job.steps) {
      assert.equal(step.if, undefined);
      assert.equal(step['continue-on-error'], undefined);
      assert.deepEqual(
        Object.keys(step).filter((key) => !['name', 'uses', 'with', 'env', 'run'].includes(key)),
        [],
      );
    }
  }
  const aggregate = jobs['trusted-proof-payload-v2'];
  assert.deepEqual(Object.keys(aggregate).sort(), [
    'if',
    'needs',
    'permissions',
    'runs-on',
    'steps',
  ]);
  assert.equal(aggregate.if?.replaceAll(/\s/g, ''), '${{always()}}');
  assert.deepEqual(aggregate.needs, v2Workers);
  assert.equal(aggregate['runs-on'], 'ubuntu-24.04');
  assert.equal(aggregate.steps.length, 6);
  const [guard, checkout, source, directory, download, receipts] = aggregate.steps;
  assert.equal(guard.name, 'Require both complete current-head V2 proofs to succeed');
  assert.deepEqual(guard.env, { V2_CI_NEEDS: '${{ toJSON(needs) }}' });
  const outcome = inlineProgram(guard, 'node --input-type=module');
  assertSource(aggregate, selectedSource);
  assert.equal(checkout.uses, 'actions/checkout@v6');
  assert.deepEqual(checkout.with, {
    ref: selectedSource,
    'fetch-depth': 0,
    'persist-credentials': false,
  });
  assert.deepEqual(source.env, { EXPECTED_SOURCE_SHA: selectedSource });
  assert.equal(
    source.run,
    sourceCheck +
      '\n' +
      'test "$(git rev-parse \'HEAD^{tree}\')" = "$(git show -s --format=%T HEAD)"\n',
  );
  assert.equal(
    directory.run,
    'test ! -e "$RUNNER_TEMP/r4-e2p-v2-receipts"\nmkdir "$RUNNER_TEMP/r4-e2p-v2-receipts"\n',
  );
  assert.equal(download.uses, 'actions/download-artifact@v8');
  assert.deepEqual(download.with, {
    pattern: artifactNamespace + '*',
    path: '${{ runner.temp }}/r4-e2p-v2-receipts',
    'merge-multiple': false,
    'skip-decompress': false,
    'digest-mismatch': 'error',
  });
  const evidence = inlineProgram(
    receipts,
    'node --input-type=module - \\\n' +
      '  "$RUNNER_TEMP/r4-e2p-v2-receipts" \\\n' +
      '  "${{ github.run_id }}" \\\n' +
      '  "${{ github.run_attempt }}" \\\n' +
      '  "$(git rev-parse HEAD)" \\\n' +
      '  "$(git rev-parse \'HEAD^{tree}\')"',
  );
  for (const field of [
    'source_commit',
    'source_tree',
    'compiled_payload_source_commit',
    'compiled_payload_source_tree',
  ]) {
    assert.ok(evidence.includes(`receipt.${field} !== source`));
  }
  assert.ok(!runs(aggregate).some((step) => step.run.includes('cmp ')));
  return { outcome, evidence };
}

export function verifyWorkflow(workflow) {
  assert.deepEqual(workflow.permissions, { contents: 'read' });
  assert.deepEqual(Object.keys(workflow.on).sort(), ['pull_request', 'push', 'workflow_dispatch']);
  const jobs = workflow.jobs;
  assert.deepEqual(
    Object.keys(jobs).sort(),
    [
      ...required,
      'runtime',
      'r5-evaluation-gate',
      'r6-economics-gate',
      'trusted-proof-payload',
      'trusted-proof-payload-v2',
      ...v2Workers,
      'integration',
    ].sort(),
  );
  assert.ok(!JSON.stringify(workflow).includes('secrets.'));
  for (const [id, job] of Object.entries(jobs)) {
    if (job.permissions !== undefined) assert.deepEqual(job.permissions, { contents: 'read' });
    assert.equal(job.environment, undefined);
    assert.ok(job['continue-on-error'] === undefined || job['continue-on-error'] === false);
    if (!['runtime', 'trusted-proof-payload-v2'].includes(id)) {
      assert.equal(job.needs, undefined, 'Existing gates remain independent');
      assert.equal(job.if, undefined);
    }
    for (const step of job.steps) {
      assert.ok(step['continue-on-error'] === undefined || step['continue-on-error'] === false);
      if (step.uses !== undefined) {
        assert.ok(
          ['actions/checkout@v6', 'actions/setup-node@v6', 'actions/setup-dotnet@v5'].includes(
            step.uses,
          ) ||
            (v2Workers.includes(id) && step.uses === 'actions/upload-artifact@v7') ||
            (id === 'trusted-proof-payload-v2' && step.uses === 'actions/download-artifact@v8'),
          'No compiled payload/proof artifact cache or reuse action is admitted',
        );
        assert.equal(step.if, undefined);
      }
      if (step.uses === 'actions/setup-node@v6' && step.with?.cache !== undefined) {
        assert.equal(step.with.cache, 'npm');
      }
    }
  }
  assertIsolatedLane(jobs['runtime-core'], coreProofs, true, true);
  requireRun(jobs['runtime-core'], 'node scripts/verify-runtime-ci-workflow.mjs --self-test');
  const admissions = runs(jobs['runtime-core']).filter(
    (step) => step.run.trim() === 'npm run r4:trusted-proof:fixture-admission',
  );
  assert.equal(admissions.length, 1);
  assert.equal(admissions[0].if, "github.event_name == 'pull_request'");
  assertIsolatedLane(jobs['r2-agent-loop'], [r2Proof], true);
  assertIsolatedLane(jobs['r4-action-host'], [r4Proof], false);
  const matrixJob = jobs['r4-action-host'];
  assert.deepEqual(matrixJob.strategy, {
    'fail-fast': false,
    'max-parallel': 4,
    matrix: { mode: ['framework', 'aot'], repetition: ['first', 'second'] },
  });
  assert.equal(matrixJob.name, 'R4 ActionHost (${{ matrix.mode }}, ${{ matrix.repetition }})');
  const lanes = matrixJob.strategy.matrix.mode.flatMap((mode) =>
    matrixJob.strategy.matrix.repetition.map((repetition) => `${mode}/${repetition}`),
  );
  assert.deepEqual(lanes, ['framework/first', 'framework/second', 'aot/first', 'aot/second']);
  const allRuns = Object.values(jobs).flatMap(runs);
  assert.equal(allRuns.filter((step) => step.run.includes('verify-agent-loop.sh')).length, 1);
  assert.equal(allRuns.filter((step) => step.run.includes('verify-action-host.sh')).length, 1);
  assert.deepEqual(
    runs(jobs['runtime-core'])
      .filter((step) => step.run.startsWith('bash '))
      .map((step) => step.run),
    coreProofs,
  );
  for (const id of [
    'r5-evaluation-gate',
    'r6-economics-gate',
    'trusted-proof-payload-v2',
    'integration',
  ]) {
    assertSource(jobs[id], selectedSource);
  }
  requireRun(jobs['r5-evaluation-gate'], 'npm run check');
  requireRun(jobs['r5-evaluation-gate'], 'npm run dist:check');
  requireRun(jobs['r5-evaluation-gate'], 'bash runtime/scripts/verify-r5-evaluation.sh all');
  requireRun(jobs['r6-economics-gate'], 'bash runtime/scripts/verify-r6-economics.sh all');
  assert.equal(jobs['r6-economics-gate']['timeout-minutes'], 45);
  requireRun(jobs.integration, 'bash runtime/scripts/verify-live-agent.sh live --dry-run');
  requireRun(jobs.integration, 'npm run runtime:integration');
  assertSource(jobs['trusted-proof-payload'], historicalSource);
  for (const [id, script, prefix] of [
    ['trusted-proof-payload', 'verify-r4-trusted-proof-payload.sh', 'r4-e2p'],
  ]) {
    for (const repetition of ['first', 'second']) {
      requireRun(
        jobs[id],
        `bash runtime/scripts/${script} > "$RUNNER_TEMP/${prefix}-${repetition}.log"`,
      );
    }
  }
  const v1Receipts = runs(jobs['trusted-proof-payload'])
    .map((step) => step.run)
    .join('\n');
  assert.ok(
    v1Receipts.includes(
      'cmp "$RUNNER_TEMP/r4-e2p-first.receipt" "$RUNNER_TEMP/r4-e2p-second.receipt"',
    ),
  );
  assert.equal(
    allRuns.filter((step) =>
      step.run.includes('bash runtime/scripts/verify-r4-trusted-proof-payload-v2.sh'),
    ).length,
    2,
  );
  assert.equal(workflow.concurrency, undefined, 'The workflow must not serialize proof workers');
  return { runtime: aggregateProgram(jobs.runtime), v2: v2Programs(jobs) };
}

function verifyResults(
  program,
  ids = required,
  variable = 'RUNTIME_CI_NEEDS',
  message = 'All required Runtime CI lanes succeeded.',
) {
  const success = Object.fromEntries(ids.map((id) => [id, { result: 'success', outputs: {} }]));
  let cases = 0;
  function execute(value, shouldPass, raw = false) {
    const result = spawnSync(process.execPath, ['--input-type=module', '--eval', program], {
      encoding: 'utf8',
      timeout: 10_000,
      env: { ...process.env, [variable]: raw ? value : JSON.stringify(value) },
    });
    assert.equal(result.error, undefined);
    assert.equal(result.signal, null);
    assert.equal(
      result.status === 0,
      shouldPass,
      'Actual aggregate admitted an invalid dependency outcome',
    );
    if (shouldPass) assert.ok(result.stdout.includes(message));
    cases++;
  }
  execute(success, true);
  execute(Object.fromEntries(Object.entries(success).reverse()), true);
  for (const id of ids) {
    for (const outcome of [
      'failure',
      'cancelled',
      'skipped',
      'queued',
      '',
      'Success',
      null,
      true,
    ]) {
      const value = structuredClone(success);
      value[id].result = outcome;
      execute(value, false);
    }
    for (const kind of ['missing-job', 'missing-result', 'null-job']) {
      const value = structuredClone(success);
      if (kind === 'missing-job') delete value[id];
      if (kind === 'missing-result') delete value[id].result;
      if (kind === 'null-job') value[id] = null;
      execute(value, false);
    }
  }
  for (const value of [
    null,
    [],
    {},
    true,
    'success',
    { ...success, unexpected: { result: 'success' } },
  ]) {
    execute(value, false);
  }
  execute('', false, true);
  execute('{broken', false, true);
  return cases;
}

function verifyV2Results(program) {
  return verifyResults(
    program,
    v2Workers,
    'V2_CI_NEEDS',
    'Both complete current-head V2 proofs succeeded.',
  );
}

function verifyReceipts(program) {
  const historical = JSON.parse(
    fs.readFileSync(
      path.join(
        root,
        'runtime/tests/fixtures/action-host/trusted-proof/trusted-proof-payload-receipt-v2.json',
      ),
      'utf8',
    ),
  );
  const sourceCommit = '1'.repeat(40);
  const sourceTree = '2'.repeat(40);
  const line = (receipt) => 'APR_R4_E2P_RECEIPT_V2 ' + JSON.stringify(receipt) + '\n';
  let cases = 0;
  function execute(mutate = () => {}, shouldPass = false) {
    const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-ci-v2-receipts-'));
    const receiptRoot = path.join(temporary, 'receipts');
    const names = ['r4-e2p-v2-77-1-first', 'r4-e2p-v2-77-1-second'];
    const directories = names.map((name) => path.join(receiptRoot, name));
    const paths = directories.map((directory, index) =>
      path.join(directory, `r4-e2p-v2-${index === 0 ? 'first' : 'second'}.receipt`),
    );
    const receipts = directories.map((_, index) => ({
      ...historical,
      source_commit: sourceCommit,
      source_tree: sourceTree,
      compiled_payload_source_commit: sourceCommit,
      compiled_payload_source_tree: sourceTree,
      payload_sha256: String(index + 3).repeat(64),
    }));
    const args = [receiptRoot, '77', '1', sourceCommit, sourceTree];
    try {
      directories.forEach((directory) => fs.mkdirSync(directory, { recursive: true }));
      paths.forEach((file, index) => fs.writeFileSync(file, line(receipts[index])));
      mutate({ temporary, receiptRoot, directories, paths, receipts, args });
      const result = spawnSync(process.execPath, ['--input-type=module', '-', ...args], {
        input: program,
        encoding: 'utf8',
        timeout: 10_000,
      });
      assert.equal(result.error, undefined);
      assert.equal(result.signal, null);
      assert.equal(
        result.status === 0,
        shouldPass,
        `Actual receipt aggregate admitted invalid evidence: ${result.stderr}`,
      );
      if (shouldPass) assert.ok(result.stdout.includes('Both bounded current-head V2 receipts'));
      cases++;
    } finally {
      assert.equal(path.dirname(temporary), path.resolve(os.tmpdir()));
      assert.ok(path.basename(temporary).startsWith('apr-ci-v2-receipts-'));
      fs.rmSync(temporary, { recursive: true, force: true });
    }
  }
  execute(undefined, true); // Different build bytes with the same source are valid.
  const padded = (receipt, length) => {
    const value = { ...receipt, padding: '' };
    value.padding = 'x'.repeat(length - Buffer.byteLength(line(value)));
    return line(value);
  };
  execute(({ paths, receipts }) => fs.writeFileSync(paths[0], padded(receipts[0], 32768)), true);
  execute(({ args }) => {
    args[1] = '78';
  });
  execute(({ args }) => {
    args[2] = '2';
  });
  execute(({ args }) => {
    args[3] = historical.source_commit;
  });
  execute(({ args }) => {
    args[4] = historical.source_tree;
  });
  execute(({ receiptRoot }) =>
    fs.writeFileSync(path.join(receiptRoot, 'unexpected.receipt'), 'extra'),
  );
  execute(({ receiptRoot }) => fs.mkdirSync(path.join(receiptRoot, 'r4-e2p-v2-77-1-third')));
  execute(({ receiptRoot, directories }) =>
    fs.renameSync(directories[1], path.join(receiptRoot, 'r4-e2p-v2-77-1-first-copy')),
  );
  execute(({ receiptRoot, temporary }) => {
    const target = path.join(temporary, 'actual');
    fs.renameSync(receiptRoot, target);
    fs.symlinkSync(target, receiptRoot, 'junction');
  });
  for (const index of [0, 1]) {
    execute(({ directories }) => fs.rmSync(directories[index], { recursive: true }));
    execute(({ paths }) => fs.unlinkSync(paths[index]));
    execute(({ directories }) =>
      fs.writeFileSync(path.join(directories[index], 'duplicate.receipt'), line(historical)),
    );
    execute(({ directories }) => fs.mkdirSync(path.join(directories[index], 'extra')));
    execute(({ paths }) => {
      fs.unlinkSync(paths[index]);
      fs.mkdirSync(paths[index]);
    });
    execute(({ directories, temporary }) => {
      const target = path.join(temporary, 'borrowed');
      fs.renameSync(directories[index], target);
      fs.symlinkSync(target, directories[index], 'junction');
    });
    execute(({ paths, directories }) => {
      fs.unlinkSync(paths[index]);
      fs.symlinkSync(directories[1 - index], paths[index], 'junction');
    });
    // File symlink creation requires a privilege absent on ordinary Windows hosts.
    // The actual Linux Runtime CI self-test exercises this additional case.
    if (process.platform !== 'win32') {
      execute(({ paths }) => {
        fs.unlinkSync(paths[index]);
        fs.symlinkSync(paths[1 - index], paths[index], 'file');
      });
    }
    for (const value of ['', 'APR_R4_E2P_RECEIPT_V2 {broken}\n', 'APR_R4_E2P_RECEIPT_V2 null\n']) {
      execute(({ paths }) => fs.writeFileSync(paths[index], value));
    }
    execute(({ paths, receipts }) =>
      fs.writeFileSync(paths[index], line(receipts[index]).trimEnd()),
    );
    execute(({ paths, receipts }) => fs.writeFileSync(paths[index], line(receipts[index]) + '\n'));
    execute(({ paths, receipts }) =>
      fs.writeFileSync(paths[index], line(receipts[index]).repeat(2)),
    );
    execute(({ paths, receipts }) =>
      fs.writeFileSync(paths[index], line(receipts[index]).replace('RECEIPT_V2', 'RECEIPT')),
    );
    execute(({ paths, receipts }) =>
      fs.writeFileSync(paths[index], '\uFEFF' + line(receipts[index])),
    );
    execute(({ paths, receipts }) =>
      fs.writeFileSync(paths[index], padded(receipts[index], 32769)),
    );
    execute(({ paths, receipts }) => {
      const valid = line({ ...receipts[index], padding: 'invalid-utf8' });
      fs.writeFileSync(
        paths[index],
        Buffer.from(valid).map((byte) => (byte === 118 ? 255 : byte)),
      );
    });
    for (const field of [
      'source_commit',
      'source_tree',
      'compiled_payload_source_commit',
      'compiled_payload_source_tree',
    ]) {
      execute(({ paths, receipts }) =>
        fs.writeFileSync(paths[index], line({ ...receipts[index], [field]: '0'.repeat(40) })),
      );
      execute(({ paths, receipts }) => {
        delete receipts[index][field];
        fs.writeFileSync(paths[index], line(receipts[index]));
      });
    }
  }
  execute(({ paths, receipts }) =>
    paths.forEach((file, index) =>
      fs.writeFileSync(
        file,
        line({
          ...receipts[index],
          source_commit: '0'.repeat(40),
          compiled_payload_source_commit: '0'.repeat(40),
        }),
      ),
    ),
  );
  return cases;
}

function verifyV2Mutations(workflow, programs) {
  const mutations = [];
  for (const id of v2Workers) {
    const modify = (change) => mutations.push((w) => change(w.jobs[id]));
    mutations.push((w) => {
      delete w.jobs[id];
    });
    modify((job) => {
      job.needs = [v2Workers.find((other) => other !== id)];
    });
    modify((job) => {
      job.concurrency = 'shared-v2';
    });
    modify((job) => {
      job.if = 'false';
    });
    modify((job) => {
      job.steps[0].with.ref = historicalSource;
    });
    modify((job) => {
      job.steps[0].with['persist-credentials'] = true;
    });
    modify((job) => {
      job.steps[1].env.EXPECTED_SOURCE_SHA = '${{ github.sha }}';
    });
    modify((job) => {
      job.steps[2].with['node-version'] = 22;
    });
    modify((job) => {
      job.steps[3].run = 'npm ci || true';
    });
    modify((job) => {
      job.steps[4].run = job.steps[4].run.replace('strace ', '');
    });
    modify((job) => {
      job.steps[5].with['dotnet-version'] = '10.0.100';
    });
    modify((job) => {
      job.steps[6].run = sourceCheck + '\n';
    });
    modify((job) => {
      job.steps[7].if = 'false';
    });
    modify((job) => {
      job.steps[7].run += ' || true';
    });
    modify((job) => {
      job.steps[7]['continue-on-error'] = true;
    });
    modify((job) => {
      job.steps[8].run = job.steps[8].run.replace('-eq 1', '-ge 1');
    });
    modify((job) => {
      job.steps[8].run = job.steps[8].run.replace('32768', '65536');
    });
    modify((job) => {
      job.steps[9].with.path = '${{ runner.temp }}/**';
    });
    modify((job) => {
      job.steps[9].with.name = 'shared-receipts';
    });
    modify((job) => {
      job.steps[9].with.overwrite = true;
    });
    modify((job) => {
      job.steps[9].with.archive = false;
    });
    modify((job) => {
      job.steps[9].with['if-no-files-found'] = 'warn';
    });
    modify((job) => {
      job.steps.push({ uses: 'actions/download-artifact@v8' });
    });
  }
  const aggregate = (change) => mutations.push((w) => change(w.jobs['trusted-proof-payload-v2']));
  aggregate((job) => {
    job.if = '${{ success() }}';
  });
  aggregate((job) => {
    job.needs.pop();
  });
  aggregate((job) => {
    job.needs.push('runtime');
  });
  aggregate((job) => {
    job.needs[1] = job.needs[0];
  });
  aggregate((job) => {
    job.steps[0].env.V2_CI_NEEDS = '${{ toJSON(needs.trusted-proof-payload-v2-first) }}';
  });
  aggregate((job) => {
    job.steps[0].run += 'true\n';
  });
  aggregate((job) => {
    job.steps[1].with.ref = historicalSource;
  });
  aggregate((job) => {
    job.steps[2].if = 'false';
  });
  aggregate((job) => {
    job.steps[2].env.EXPECTED_SOURCE_SHA = '${{ github.sha }}';
  });
  aggregate((job) => {
    job.steps[3].run = 'mkdir -p "$RUNNER_TEMP/r4-e2p-v2-receipts"\n';
  });
  aggregate((job) => {
    job.steps[4].with.pattern = 'r4-e2p-v2-*';
  });
  aggregate((job) => {
    job.steps[4].with['merge-multiple'] = true;
  });
  aggregate((job) => {
    job.steps[4].with['skip-decompress'] = true;
  });
  aggregate((job) => {
    job.steps[4].with['digest-mismatch'] = 'warn';
  });
  aggregate((job) => {
    job.steps[4].with['github-token'] = '${{ github.token }}';
  });
  aggregate((job) => {
    job.steps[4].with['run-id'] = '1';
  });
  aggregate((job) => {
    job.steps[5].run = job.steps[5].run.replace(
      '$(git rev-parse HEAD)',
      '${{ needs.trusted-proof-payload-v2-first.outputs.source }}',
    );
  });
  aggregate((job) => {
    job.steps[5].if = 'false';
  });
  for (const mutate of mutations) {
    const changed = structuredClone(workflow);
    mutate(changed);
    assert.throws(() => verifyWorkflow(changed));
  }
  const outcomes = [
    programs.outcome.replaceAll('process.exit(1)', 'process.exit(0)'),
    programs.outcome.replace(
      "results[id]?.result !== 'success'",
      "results[id]?.result === 'failure'",
    ),
  ];
  outcomes.forEach((program) => assert.throws(() => verifyV2Results(program)));
  const evidence = [
    ['stat.size > 32768', 'false'],
    ['fatal: true', 'fatal: false'],
    ["line.indexOf('\\n') !== line.length - 1", 'false'],
    ['JSON.stringify(fs.readdirSync(receiptRoot).sort()) !== JSON.stringify(expected)', 'false'],
    ['JSON.stringify(fs.readdirSync(directory)) !== JSON.stringify([file])', 'false'],
    ['!fs.lstatSync(receiptRoot).isDirectory()', 'false'],
    ['!fs.lstatSync(directory).isDirectory()', 'false'],
    ...(process.platform !== 'win32' ? [['!stat.isFile()', 'false']] : []),
    ...[
      'source_commit',
      'source_tree',
      'compiled_payload_source_commit',
      'compiled_payload_source_tree',
    ].map((field) => [
      `receipt.${field} !== ${field.endsWith('commit') ? 'sourceCommit' : 'sourceTree'}`,
      'false',
    ]),
  ];
  for (const [before, after] of evidence) {
    assert.ok(programs.evidence.includes(before));
    assert.throws(
      () => verifyReceipts(programs.evidence.replace(before, after)),
      `Receipt mutant survived: ${before}`,
    );
  }
  return mutations.length + outcomes.length + evidence.length;
}

function verifyMutations(workflow, program) {
  const mutations = [
    (w) => {
      const sdk = w.jobs['runtime-core'].steps.find(
        (step) => step.uses === 'actions/setup-dotnet@v5',
      );
      sdk.with['dotnet-version'] = '10.0.112';
    },
    (w) => {
      const sdk = w.jobs['runtime-core'].steps.find(
        (step) => step.uses === 'actions/setup-dotnet@v5',
      );
      sdk.with = { 'global-json-file': 'global.json' };
    },
    (w) => {
      const sdk = w.jobs['runtime-core'].steps.find(
        (step) => step.uses === 'actions/setup-dotnet@v5',
      );
      delete sdk.env.DOTNET_INSTALL_DIR;
    },
    (w) => {
      const sdk = w.jobs['runtime-core'].steps.find(
        (step) => step.uses === 'actions/setup-dotnet@v5',
      );
      sdk.env.DOTNET_INSTALL_DIR = '/usr/share/dotnet';
    },
    (w) => {
      const sdk = w.jobs['runtime-core'].steps.find(
        (step) => step.uses === 'actions/setup-dotnet@v5',
      );
      sdk.with['global-json-file'] = 'global.json';
    },
    (w) => {
      w.jobs['r4-action-host'].strategy.matrix.mode = ['framework'];
    },
    (w) => {
      w.jobs['r4-action-host'].strategy.matrix.mode = ['framework', 'framework', 'aot'];
    },
    (w) => {
      w.jobs['r4-action-host'].strategy.matrix.mode = ['framework', 'all'];
    },
    (w) => {
      w.jobs['r4-action-host'].strategy.matrix.repetition = ['first'];
    },
    (w) => {
      w.jobs['r4-action-host'].strategy.matrix.repetition = ['first', 'first'];
    },
    (w) => {
      w.jobs['r4-action-host'].strategy.matrix.exclude = [{ mode: 'aot', repetition: 'second' }];
    },
    (w) => {
      w.jobs['r4-action-host'].strategy.matrix.include = [{ mode: 'aot', repetition: 'third' }];
    },
    (w) => {
      w.jobs['r4-action-host'].strategy['fail-fast'] = true;
    },
    (w) => {
      w.jobs['r4-action-host'].strategy['max-parallel'] = 3;
    },
    (w) => {
      w.jobs['r4-action-host'].needs = ['runtime-core'];
    },
    (w) => {
      w.jobs['r2-agent-loop'].needs = ['runtime-core'];
    },
    (w) => {
      w.jobs['r4-action-host'].concurrency = 'shared';
    },
    (w) => {
      w.jobs['r2-agent-loop'].if = 'false';
    },
    (w) => {
      w.jobs['r4-action-host']['continue-on-error'] = true;
    },
    (w) => {
      requireRun(w.jobs['r4-action-host'], r4Proof).if = "matrix.repetition == 'first'";
    },
    (w) => {
      requireRun(w.jobs['r4-action-host'], r4Proof)['continue-on-error'] = true;
    },
    (w) => {
      requireRun(w.jobs['r2-agent-loop'], r2Proof).run =
        'bash runtime/scripts/verify-agent-loop.sh framework';
    },
    (w) => {
      requireRun(w.jobs['runtime-core'], coreProofs[0]).run += ' || true';
    },
    (w) => {
      w.jobs['r4-action-host'].name = 'R4 ActionHost';
    },
    (w) => {
      w.jobs.runtime.needs.pop();
    },
    (w) => {
      w.jobs.runtime.needs.push('r5-evaluation-gate');
    },
    (w) => {
      w.jobs.runtime.if = "${{ always() && needs.r2-agent-loop.result == 'success' }}";
    },
    (w) => {
      w.jobs.runtime.steps[0].run += 'true\n';
    },
    (w) => {
      w.jobs.runtime.steps[0]['continue-on-error'] = true;
    },
    (w) => {
      w.jobs.runtime.steps[0].env.RUNTIME_CI_NEEDS = '${{ toJSON(needs.r4-action-host) }}';
    },
    (w) => {
      w.jobs['r2-agent-loop'].steps[0].with.ref = '${{ github.sha }}';
    },
    (w) => {
      w.jobs.integration.steps[0].with.ref = '${{ github.sha }}';
    },
    (w) => {
      w.jobs['trusted-proof-payload-v2'].steps[1].with.ref = historicalSource;
    },
    (w) => {
      w.jobs['trusted-proof-payload'].steps[0].with.ref = selectedSource;
    },
    (w) => {
      w.jobs['r4-action-host'].steps[0].with['persist-credentials'] = true;
    },
    (w) => {
      w.jobs['r2-agent-loop'].permissions = { contents: 'write' };
    },
    (w) => {
      w.jobs['r4-action-host'].environment = 'production';
    },
    (w) => {
      w.jobs['r4-action-host'].env = { PROVIDER: '${{ secrets.PROVIDER }}' };
    },
    (w) => {
      w.jobs['r4-action-host'].steps.push({ uses: 'actions/download-artifact@v4' });
    },
    (w) => {
      w.jobs['r2-agent-loop'].steps = w.jobs['r2-agent-loop'].steps.filter(
        (s) => !s.run?.includes(cleanCheck),
      );
    },
    (w) => {
      delete w.jobs['r5-evaluation-gate'];
    },
    (w) => {
      requireRun(
        w.jobs['trusted-proof-payload-v2-second'],
        'bash runtime/scripts/verify-r4-trusted-proof-payload-v2.sh > "$RUNNER_TEMP/r4-e2p-v2-second.log"',
      ).if = 'false';
    },
  ];
  for (const mutate of mutations) {
    const changed = structuredClone(workflow);
    mutate(changed);
    assert.throws(() => verifyWorkflow(changed));
  }
  assert.throws(() => YAML.parse('jobs:\n  runtime: {}\n  runtime: {}\n'));
  assert.throws(() => verifyResults(program.replaceAll('process.exit(1)', 'process.exit(0)')));
  assert.throws(() =>
    verifyResults(
      program.replace("results[id]?.result !== 'success'", "results[id]?.result === 'failure'"),
    ),
  );
  return mutations.length + 3;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  assert.ok(
    process.argv.length === 2 || (process.argv.length === 3 && process.argv[2] === '--self-test'),
  );
  const workflowText = fs.readFileSync(path.join(root, '.github/workflows/runtime-ci.yml'), 'utf8');
  const workflow = YAML.parse(workflowText);
  const programs = verifyWorkflow(workflow);
  const resultCases = verifyResults(programs.runtime);
  const v2Results = verifyV2Results(programs.v2.outcome);
  const receiptCases = verifyReceipts(programs.v2.evidence);
  const selfTest = process.argv[2] === '--self-test';
  const mutations = selfTest ? verifyMutations(workflow, programs.runtime) : 0;
  const v2Mutations = selfTest ? verifyV2Mutations(workflow, programs.v2) : 0;
  console.log(
    `runtime_ci_workflow_verified lanes=4 result_cases=${resultCases} mutations=${mutations} ` +
      `v2_workers=2 v2_result_cases=${v2Results} receipt_cases=${receiptCases} v2_mutations=${v2Mutations}`,
  );
}
