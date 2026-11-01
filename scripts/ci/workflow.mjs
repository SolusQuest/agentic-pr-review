import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';

export const selectedSource =
  "${{ github.event_name == 'pull_request' && github.event.pull_request.head.sha || github.sha }}";
export const sourceDependencies = [
  'format',
  'typecheck',
  'distribution',
  ...Array.from({ length: 6 }, (_, i) => `source-tests-${i + 1}`),
];
export const sourceCommands = {
  format: 'npm run format:check',
  typecheck: 'npm run typecheck',
  distribution: 'npm run dist:check',
  ...Object.fromEntries(
    Array.from({ length: 6 }, (_, i) => [
      `source-tests-${i + 1}`,
      `node scripts/ci/source-tests.mjs --shard ${i + 1}`,
    ]),
  ),
  'runtime-build-smoke':
    'dotnet build runtime/src/AgenticPrReview.Runtime/AgenticPrReview.Runtime.csproj --configuration Release --output "$RUNNER_TEMP/agentic-pr-review-runtime"',
  'retained-conformance':
    'npm test -- --run src/artifact-provenance-vectors.test.ts src/residual-reference-guard.test.ts',
};
const sourceCheck = 'test "$(git rev-parse HEAD)" = "$EXPECTED_SOURCE_SHA"';
const clean = 'test -z "$(git status --porcelain)"';

export function aggregateProgram(job, name, dependencies, variable) {
  assert.equal(job.name, name);
  assert.equal(job.if?.replaceAll(/\s/g, ''), '${{always()}}');
  assert.deepEqual([...job.needs].sort(), [...dependencies].sort());
  assert.equal(job['runs-on'], 'ubuntu-24.04');
  assert.equal(job['continue-on-error'], undefined);
  assert.equal(job.environment, undefined);
  assert.equal(job.steps.length, 1);
  const step = job.steps[0];
  assert.deepEqual(Object.keys(step).sort(), ['env', 'name', 'run']);
  assert.deepEqual(step.env, { [variable]: '${{ toJSON(needs) }}' });
  const program = /^node --input-type=module <<'NODE'\n([\s\S]+)\nNODE\n?$/.exec(step.run);
  assert.ok(program);
  return program[1];
}

export function verifySourceWorkflow(workflow) {
  assert.equal(workflow.name, 'CI');
  assert.deepEqual(Object.keys(workflow.on).sort(), ['pull_request', 'push']);
  assert.deepEqual(workflow.on.push, { branches: ['main'] });
  assert.deepEqual(workflow.permissions, { contents: 'read' });
  assert.equal(workflow.concurrency, undefined);
  assert.ok(!JSON.stringify(workflow).includes('secrets.'));
  const jobs = workflow.jobs;
  assert.deepEqual(Object.keys(jobs).sort(), [...Object.keys(sourceCommands), 'check'].sort());
  for (const [id, command] of Object.entries(sourceCommands)) {
    const job = jobs[id];
    assert.deepEqual(Object.keys(job).sort(), ['runs-on', 'steps']);
    assert.equal(job['runs-on'], 'ubuntu-24.04');
    const sdk = id.startsWith('source-tests-') || id === 'runtime-build-smoke';
    const expected = [
      {
        uses: 'actions/checkout@v6',
        with: { ref: selectedSource, 'fetch-depth': 0, 'persist-credentials': false },
      },
      { uses: 'actions/setup-node@v6', with: { 'node-version': 24, cache: 'npm' } },
      ...(sdk
        ? [{ uses: 'actions/setup-dotnet@v5', with: { 'global-json-file': 'global.json' } }]
        : []),
      { run: 'npm ci' },
      {
        name: 'Verify literal event-selected source',
        env: { EXPECTED_SOURCE_SHA: selectedSource },
        run: `${sourceCheck}\n${clean}\ngit rev-parse HEAD HEAD^{tree}\n`,
      },
      {
        name: 'Execute complete assigned check',
        ...(id.startsWith('source-tests-') ? { env: { EXPECTED_SOURCE_SHA: selectedSource } } : {}),
        run: command,
      },
      { name: 'Verify assigned check leaves source clean', run: clean },
    ];
    assert.deepEqual(job.steps, expected, `Complete source job contract: ${id}`);
  }
  assert.deepEqual(Object.keys(jobs.check).sort(), ['if', 'name', 'needs', 'runs-on', 'steps']);
  return aggregateProgram(jobs.check, 'check', sourceDependencies, 'SOURCE_CI_NEEDS');
}

export function verifySourceMutations(workflow) {
  let count = 0;
  const refuse = (change) => {
    const w = structuredClone(workflow);
    change(w);
    assert.throws(() => verifySourceWorkflow(w));
    count++;
  };
  for (const id of Object.keys(sourceCommands)) {
    refuse((w) => {
      delete w.jobs[id];
    });
    refuse((w) => {
      w.jobs[id].if = 'false';
    });
    refuse((w) => {
      w.jobs[id]['continue-on-error'] = true;
    });
    refuse((w) => {
      w.jobs[id].needs = ['format'];
    });
    refuse((w) => {
      w.jobs[id].steps[0].with.ref = '${{ github.sha }}';
    });
    refuse((w) => {
      w.jobs[id].steps[0].with['fetch-depth'] = 1;
    });
    refuse((w) => {
      w.jobs[id].steps[0].with['persist-credentials'] = true;
    });
    refuse((w) => {
      w.jobs[id].steps.find((s) => s.run === sourceCommands[id]).run += ' || true';
    });
    refuse((w) => {
      w.jobs[id].steps.find((s) => s.run === sourceCommands[id]).if = 'false';
    });
    refuse((w) => {
      w.jobs[id].steps.push({ uses: 'actions/download-artifact@v8' });
    });
  }
  for (const id of sourceDependencies)
    refuse((w) => {
      w.jobs.check.needs = w.jobs.check.needs.filter((x) => x !== id);
    });
  refuse((w) => {
    w.jobs['source-tests-6'].steps.find((s) => s.run?.includes('--shard')).run =
      sourceCommands['source-tests-1'];
  });
  refuse((w) => {
    w.jobs.check.if = '${{ success() }}';
  });
  refuse((w) => {
    w.jobs.check.needs.push('unexpected');
  });
  refuse((w) => {
    w.jobs.check.steps[0].env.SOURCE_CI_NEEDS = '${{ toJSON(needs.format) }}';
  });
  refuse((w) => {
    w.jobs.unexpected = structuredClone(w.jobs.format);
  });
  return count;
}

export function verifyAggregateOutcomes(program, ids, variable) {
  let count = 0;
  const success = Object.fromEntries(ids.map((id) => [id, { result: 'success', outputs: {} }]));
  const execute = (value, expected = false, raw = false) => {
    const result = spawnSync(process.execPath, ['--input-type=module', '-e', program], {
      encoding: 'utf8',
      env: { ...process.env, [variable]: raw ? value : JSON.stringify(value) },
      timeout: 5000,
    });
    assert.equal(result.error, undefined);
    assert.equal(result.signal, null);
    assert.equal(result.status === 0, expected, 'Actual source aggregate must fail closed');
    count++;
  };
  execute(success, true);
  execute(Object.fromEntries(Object.entries(success).reverse()), true);
  for (const id of ids) {
    for (const result of [
      'failure',
      'cancelled',
      'skipped',
      'queued',
      'unknown',
      'Success',
      '',
      null,
      true,
    ])
      execute({ ...success, [id]: { result } });
    const missing = { ...success };
    delete missing[id];
    execute(missing);
    execute({ ...success, [id]: null });
    execute({ ...success, [id]: {} });
  }
  for (const value of [
    null,
    [],
    {},
    true,
    'success',
    { ...success, unexpected: { result: 'success' } },
  ])
    execute(value);
  execute('{broken', false, true);
  execute('', false, true);
  return count;
}
