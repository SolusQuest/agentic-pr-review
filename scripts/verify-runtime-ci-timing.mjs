import assert from 'node:assert/strict';
import fs from 'node:fs';

const round = (value) => Math.round(value * 100) / 100;
function timestamp(value) {
  const parsed = Date.parse(value);
  assert.ok(Number.isFinite(parsed), 'Timing evidence requires valid timestamps');
  return parsed;
}

function peakConcurrency(jobs) {
  const events = jobs
    .filter((job) => timestamp(job.completed_at) > timestamp(job.started_at))
    .flatMap((job) => [
      [timestamp(job.started_at), 1],
      [timestamp(job.completed_at), -1],
    ])
    .sort((a, b) => a[0] - b[0] || a[1] - b[1]);
  let active = 0;
  let peak = 0;
  for (const [, delta] of events) {
    active += delta;
    peak = Math.max(peak, active);
  }
  return peak;
}

function isProofStep(name) {
  return (
    [
      'Runtime tests',
      'Complete R2 Agent verification',
      'Run isolated ActionHost proof',
      'Trusted-live policy and dry-run',
      'Require all former Runtime lanes to succeed',
    ].includes(name) ||
    /^(R4 E2P first |Current-head R4 E2P first |R5 deterministic evaluation gate|R6 offline economics gate)/.test(
      name,
    )
  );
}

function measure(run, pages) {
  assert.equal(run.event, 'pull_request', 'Compare ordinary PR runs');
  assert.equal(run.status, 'completed');
  assert.equal(run.conclusion, 'success');
  const jobs = (Array.isArray(pages) ? pages : [pages]).flatMap((page) => page.jobs);
  assert.ok(
    jobs.length > 0 &&
      jobs.every((job) => job.status === 'completed' && job.conclusion === 'success'),
  );
  assert.equal(
    new Set(jobs.map((job) => job.id)).size,
    jobs.length,
    'Do not count duplicate job pages',
  );
  const created = timestamp(run.created_at);
  const rows = jobs.map((job) => {
    const start = timestamp(job.started_at);
    const end = timestamp(job.completed_at);
    assert.ok(end >= start && start >= timestamp(job.created_at) && start >= created);
    const firstProof = job.steps?.find((step) => isProofStep(step.name));
    return {
      id: job.id,
      name: job.name,
      runnerId: job.runner_id,
      activeMinutes: (end - start) / 60_000,
      queueSeconds: (start - timestamp(job.created_at)) / 1000,
      workflowToStartSeconds: (start - created) / 1000,
      setupSeconds: firstProof ? (timestamp(firstProof.started_at) - start) / 1000 : null,
    };
  });
  const isR4 = (row) => /^R4 ActionHost \((framework|aot), (first|second)\)$/.test(row.name);
  const parallel = rows.some((row) => row.name === 'runtime-core');
  const aggregate = parallel ? rows.find((row) => row.name === 'runtime') : undefined;
  const splitLeaves = rows.filter(
    (row) => ['runtime-core', 'r2-agent-loop'].includes(row.name) || isR4(row),
  );
  if (parallel) {
    assert.ok(aggregate, 'Parallel timing must include the terminal runtime aggregate');
    assert.equal(splitLeaves.length, 6, 'Parallel timing must retain core, R2 and four R4 workers');
    assert.equal(
      new Set(rows.filter(isR4).map((row) => row.runnerId)).size,
      4,
      'R4 workers must use distinct runners',
    );
  }
  const independent = rows.filter((row) => !parallel || row.name !== 'runtime');
  const longest = independent.reduce((a, b) => (a.activeMinutes >= b.activeMinutes ? a : b));
  const runtimeCritical = parallel
    ? Math.max(...splitLeaves.map((row) => row.activeMinutes)) + aggregate.activeMinutes
    : rows.find((row) => row.name === 'runtime')?.activeMinutes;
  assert.ok(Number.isFinite(runtimeCritical));
  const elapsed = (Math.max(...jobs.map((job) => timestamp(job.completed_at))) - created) / 60_000;
  return {
    runId: run.id,
    url: run.html_url,
    headSha: run.head_sha,
    runAttempt: run.run_attempt,
    topology: parallel ? 'parallel' : 'serial',
    totalElapsedMinutes: round(elapsed),
    longestActiveJob: { name: longest.name, minutes: round(longest.activeMinutes) },
    runtimeActiveCriticalPathMinutes: round(runtimeCritical),
    workflowActiveCriticalPathMinutes: round(Math.max(longest.activeMinutes, runtimeCritical)),
    activeSpanMinutes: round(
      (Math.max(...jobs.map((job) => timestamp(job.completed_at))) -
        Math.min(...jobs.map((job) => timestamp(job.started_at)))) /
        60_000,
    ),
    totalRunnerMinutes: round(rows.reduce((sum, row) => sum + row.activeMinutes, 0)),
    maximumJobQueueSeconds: Math.max(...rows.map((row) => row.queueSeconds)),
    observedPeakConcurrency: peakConcurrency(jobs),
    observedR4Concurrency: peakConcurrency(jobs.filter((job) => isR4(job))),
    targetAtMost35MinutesAchieved: elapsed <= 35,
    jobs: rows.map((row) => ({ ...row, activeMinutes: round(row.activeMinutes) })),
  };
}

function selfTest() {
  const date = (seconds) => new Date(Date.UTC(2026, 0, 1) + seconds * 1000).toISOString();
  const run = {
    id: 1,
    event: 'pull_request',
    status: 'completed',
    conclusion: 'success',
    created_at: date(0),
  };
  function job(id, name, start, end, admitted = 0) {
    return {
      id,
      name,
      runner_id: id,
      status: 'completed',
      conclusion: 'success',
      created_at: date(admitted),
      started_at: date(start),
      completed_at: date(end),
    };
  }
  const serial = measure(run, { jobs: [job(1, 'runtime', 2, 602), job(2, 'integration', 3, 363)] });
  assert.equal(serial.totalElapsedMinutes, 10.03);
  assert.equal(serial.totalRunnerMinutes, 16);
  assert.equal(serial.maximumJobQueueSeconds, 3);
  assert.equal(serial.observedPeakConcurrency, 2);
  assert.equal(serial.workflowActiveCriticalPathMinutes, 10);
  const jobs = [
    job(1, 'runtime-core', 0, 480),
    job(2, 'r2-agent-loop', 0, 420),
    job(3, 'R4 ActionHost (framework, first)', 0, 180),
    job(4, 'R4 ActionHost (framework, second)', 0, 180),
    job(5, 'R4 ActionHost (aot, first)', 0, 180),
    job(6, 'R4 ActionHost (aot, second)', 0, 180),
    job(7, 'trusted-proof-payload-v2', 0, 720),
    job(8, 'runtime', 480, 540, 480),
  ];
  const parallel = measure(run, [{ jobs }]);
  assert.equal(parallel.totalElapsedMinutes, 12);
  assert.equal(parallel.totalRunnerMinutes, 40);
  assert.equal(parallel.runtimeActiveCriticalPathMinutes, 9);
  assert.equal(parallel.workflowActiveCriticalPathMinutes, 12);
  assert.equal(parallel.observedR4Concurrency, 4);
  assert.equal(parallel.jobs.at(-1).queueSeconds, 0);
  assert.equal(parallel.jobs.at(-1).workflowToStartSeconds, 480);
  assert.throws(() => measure(run, { jobs: [...jobs, jobs[0]] }));
  assert.throws(() => measure(run, { jobs: jobs.slice(0, -1) }));
  assert.throws(() => measure({ ...run, conclusion: 'failure' }, { jobs }));
  console.log('runtime_ci_timing_verified serial=passed parallel=passed refusals=passed');
}

if (process.argv.length === 3 && process.argv[2] === '--self-test') {
  selfTest();
} else {
  assert.equal(
    process.argv.length,
    4,
    'Usage: node scripts/verify-runtime-ci-timing.mjs <run.json> <jobs.json>',
  );
  const read = (file) => JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
  console.log(JSON.stringify(measure(read(process.argv[2]), read(process.argv[3])), null, 2));
}
