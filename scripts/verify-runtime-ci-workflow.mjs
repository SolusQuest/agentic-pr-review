import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import YAML from 'yaml';

const root = fileURLToPath(new URL('../', import.meta.url));
const workflowText = fs.readFileSync(path.join(root, '.github/workflows/runtime-ci.yml'), 'utf8');
const selectedSource =
  "${{ github.event_name == 'pull_request' && github.event.pull_request.head.sha || github.sha }}";
const required = ['runtime-core', 'r2-agent-loop', 'r4-action-host'];
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

function assertIsolatedLane(job, proofCommands, terminalClean) {
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
  assert.equal(dotnet[0].with?.['global-json-file'], 'global.json');
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

function verifyWorkflow(workflow) {
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
      'integration',
    ].sort(),
  );
  assert.ok(!JSON.stringify(workflow).includes('secrets.'));
  for (const [id, job] of Object.entries(jobs)) {
    if (job.permissions !== undefined) assert.deepEqual(job.permissions, { contents: 'read' });
    assert.equal(job.environment, undefined);
    assert.ok(job['continue-on-error'] === undefined || job['continue-on-error'] === false);
    if (id !== 'runtime') {
      assert.equal(job.needs, undefined, 'Existing gates remain independent');
      assert.equal(job.if, undefined);
    }
    for (const step of job.steps) {
      assert.ok(step['continue-on-error'] === undefined || step['continue-on-error'] === false);
      if (step.uses !== undefined) {
        assert.ok(
          ['actions/checkout@v6', 'actions/setup-node@v6', 'actions/setup-dotnet@v5'].includes(
            step.uses,
          ),
          'No compiled payload/proof artifact cache or reuse action is admitted',
        );
        assert.equal(step.if, undefined);
      }
      if (step.uses === 'actions/setup-node@v6' && step.with?.cache !== undefined) {
        assert.equal(step.with.cache, 'npm');
      }
    }
  }
  assertIsolatedLane(jobs['runtime-core'], coreProofs, true);
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
    ['trusted-proof-payload-v2', 'verify-r4-trusted-proof-payload-v2.sh', 'r4-e2p-v2'],
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
  const v2Receipts = runs(jobs['trusted-proof-payload-v2'])
    .map((step) => step.run)
    .join('\n');
  for (const field of [
    'source_commit',
    'source_tree',
    'compiled_payload_source_commit',
    'compiled_payload_source_tree',
  ]) {
    assert.ok(v2Receipts.includes(`receipt.${field} !== source`));
  }
  assert.ok(!v2Receipts.includes('cmp '));
  return aggregateProgram(jobs.runtime);
}

function verifyResults(program) {
  const success = Object.fromEntries(
    required.map((id) => [id, { result: 'success', outputs: {} }]),
  );
  let cases = 0;
  function execute(value, shouldPass, raw = false) {
    const result = spawnSync(process.execPath, ['--input-type=module', '--eval', program], {
      encoding: 'utf8',
      timeout: 10_000,
      env: { ...process.env, RUNTIME_CI_NEEDS: raw ? value : JSON.stringify(value) },
    });
    assert.equal(result.error, undefined);
    assert.equal(result.signal, null);
    assert.equal(
      result.status === 0,
      shouldPass,
      'Actual aggregate admitted an invalid dependency outcome',
    );
    if (shouldPass) assert.ok(result.stdout.includes('All required Runtime CI lanes succeeded.'));
    cases++;
  }
  execute(success, true);
  execute(Object.fromEntries(Object.entries(success).reverse()), true);
  for (const id of required) {
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

function verifyMutations(workflow, program) {
  const mutations = [
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
      w.jobs['trusted-proof-payload-v2'].steps[0].with.ref = historicalSource;
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
        w.jobs['trusted-proof-payload-v2'],
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

assert.ok(
  process.argv.length === 2 || (process.argv.length === 3 && process.argv[2] === '--self-test'),
);
const workflow = YAML.parse(workflowText);
const program = verifyWorkflow(workflow);
const resultCases = verifyResults(program);
const mutations = process.argv[2] === '--self-test' ? verifyMutations(workflow, program) : 0;
console.log(
  `runtime_ci_workflow_verified lanes=4 result_cases=${resultCases} mutations=${mutations}`,
);
