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
      'Require both complete current-head V2 proofs to succeed',
    ].includes(name) ||
    /^(R4 E2P (first|second) |Current-head R4 E2P (first|second) |R5 deterministic evaluation gate|R6 offline economics gate)/.test(
      name,
    )
  );
}

function measure(run, pages, main = false) {
  assert.equal(
    run.event,
    main ? 'push' : 'pull_request',
    'Retain the actual PR or push event identity',
  );
  if (main) assert.equal(run.head_branch, 'main', 'Main evidence must be a real push/main run');
  assert.match(run.head_sha, /^[a-f0-9]{40}$/, 'Timing evidence must identify its exact head');
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
  assert.equal(
    new Set(jobs.map((job) => job.name)).size,
    jobs.length,
    'Do not admit duplicate worker names',
  );
  const created = timestamp(run.created_at);
  const rows = jobs.map((job) => {
    const start = timestamp(job.started_at);
    const end = timestamp(job.completed_at);
    assert.ok(end >= start && start >= timestamp(job.created_at) && start >= created);
    const proofs = (job.steps ?? [])
      .filter((step) => isProofStep(step.name))
      .map((step) => {
        assert.equal(step.status, 'completed');
        assert.equal(
          step.conclusion,
          'success',
          'Each recorded proof must have executed successfully',
        );
        const proofStart = timestamp(step.started_at);
        const proofEnd = timestamp(step.completed_at);
        assert.ok(proofStart >= start && proofEnd >= proofStart && proofEnd <= end);
        return {
          name: step.name,
          startedAt: step.started_at,
          completedAt: step.completed_at,
          activeMinutes: (proofEnd - proofStart) / 60_000,
        };
      });
    const firstProof = proofs[0];
    return {
      id: job.id,
      name: job.name,
      runnerId: job.runner_id,
      activeMinutes: (end - start) / 60_000,
      queueSeconds: (start - timestamp(job.created_at)) / 1000,
      workflowToStartSeconds: (start - created) / 1000,
      startedAt: job.started_at,
      completedAt: job.completed_at,
      setupSeconds: firstProof ? (timestamp(firstProof.startedAt) - start) / 1000 : null,
      proofs,
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
    assert.equal(rows.filter(isR4).length, 4, 'All four R4 workers must remain present');
    assert.ok(rows.some((row) => row.name === 'r2-agent-loop'));
    assert.equal(
      new Set(rows.filter(isR4).map((row) => row.runnerId)).size,
      4,
      'R4 workers must use distinct runners',
    );
  }
  const v2Ids = ['trusted-proof-payload-v2-first', 'trusted-proof-payload-v2-second'];
  const v2Workers = rows.filter((row) => v2Ids.includes(row.name));
  const v2Aggregate = rows.find((row) => row.name === 'trusted-proof-payload-v2');
  const splitV2 =
    v2Workers.length > 0 ||
    v2Aggregate?.proofs.some(
      (proof) => proof.name === 'Require both complete current-head V2 proofs to succeed',
    );
  let v2;
  if (splitV2) {
    assert.ok(parallel && v2Aggregate, 'V2 split evidence must retain both stable aggregates');
    assert.equal(v2Workers.length, 2, 'V2 split evidence must contain both independent workers');
    assert.deepEqual(v2Workers.map((row) => row.name).sort(), v2Ids);
    assert.ok(v2Workers.every((row) => Number.isInteger(row.runnerId) && row.runnerId > 0));
    assert.equal(
      new Set(v2Workers.map((row) => row.runnerId)).size,
      2,
      'V2 workers must use distinct runners',
    );
    const proofs = v2Ids.map((id, index) => {
      const row = v2Workers.find((worker) => worker.name === id);
      const name = `Current-head R4 E2P ${index === 0 ? 'first' : 'second'} clean production and verifier Native AOT build`;
      assert.equal(row.proofs.length, 1, 'Each V2 worker must execute exactly its one whole proof');
      assert.equal(row.proofs[0].name, name);
      assert.ok(row.proofs[0].activeMinutes > 0);
      return row.proofs[0];
    });
    const overlap =
      (Math.min(...proofs.map((proof) => timestamp(proof.completedAt))) -
        Math.max(...proofs.map((proof) => timestamp(proof.startedAt)))) /
      1000;
    assert.ok(overlap > 0, 'Successful CI2 evidence must show actual whole-proof overlap');
    assert.ok(
      timestamp(v2Aggregate.startedAt) >=
        Math.max(...v2Workers.map((row) => timestamp(row.completedAt))),
      'V2 aggregate must follow both complete workers',
    );
    v2 = {
      topology: 'parallel',
      workers: v2Ids,
      distinctRunners: 2,
      proofOverlapSeconds: overlap,
      observedWorkerConcurrency: peakConcurrency(jobs.filter((job) => v2Ids.includes(job.name))),
      activeCriticalPathMinutes:
        Math.max(...v2Workers.map((row) => row.activeMinutes)) + v2Aggregate.activeMinutes,
      aggregateActiveMinutes: v2Aggregate.activeMinutes,
      aggregateCompletedAt: v2Aggregate.completedAt,
      workflowToAggregateCompletionMinutes: (timestamp(v2Aggregate.completedAt) - created) / 60_000,
    };
  } else if (v2Aggregate) {
    v2 = {
      topology: 'serial',
      activeCriticalPathMinutes: v2Aggregate.activeMinutes,
      aggregateCompletedAt: v2Aggregate.completedAt,
      workflowToAggregateCompletionMinutes: (timestamp(v2Aggregate.completedAt) - created) / 60_000,
    };
  }
  const independent = rows.filter(
    (row) =>
      (!parallel || row.name !== 'runtime') &&
      (!splitV2 || row.name !== 'trusted-proof-payload-v2'),
  );
  const longest = independent.reduce((a, b) => (a.activeMinutes >= b.activeMinutes ? a : b));
  const runtimeCritical = parallel
    ? Math.max(...splitLeaves.map((row) => row.activeMinutes)) + aggregate.activeMinutes
    : rows.find((row) => row.name === 'runtime')?.activeMinutes;
  assert.ok(Number.isFinite(runtimeCritical));
  if (parallel)
    assert.ok(
      timestamp(aggregate.startedAt) >=
        Math.max(...splitLeaves.map((row) => timestamp(row.completedAt))),
    );
  const elapsed = (Math.max(...jobs.map((job) => timestamp(job.completed_at))) - created) / 60_000;
  return {
    runId: run.id,
    url: run.html_url,
    headSha: run.head_sha,
    runAttempt: run.run_attempt,
    event: run.event,
    branch: run.head_branch,
    evidenceKind: main ? 'post-merge-main-push' : 'ordinary-pr',
    topology: parallel ? 'parallel' : 'serial',
    totalElapsedMinutes: round(elapsed),
    longestActiveJob: { name: longest.name, minutes: round(longest.activeMinutes) },
    runtimeActiveCriticalPathMinutes: round(runtimeCritical),
    workflowActiveCriticalPathMinutes: round(
      Math.max(longest.activeMinutes, runtimeCritical, v2?.activeCriticalPathMinutes ?? 0),
    ),
    v2: v2
      ? Object.fromEntries(
          Object.entries(v2).map(([key, value]) => [
            key,
            key.endsWith('Minutes') ? round(value) : value,
          ]),
        )
      : null,
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
    estimatedV2AtMost14MinutesObserved: splitV2
      ? v2.workflowToAggregateCompletionMinutes <= 14
      : null,
    estimatedTotalAtMost20MinutesObserved: splitV2 ? elapsed <= 20 : null,
    jobs: rows.map((row) => ({
      ...row,
      activeMinutes: round(row.activeMinutes),
      proofs: row.proofs.map((proof) => ({ ...proof, activeMinutes: round(proof.activeMinutes) })),
    })),
  };
}

function selfTest() {
  const date = (seconds) => new Date(Date.UTC(2026, 0, 1) + seconds * 1000).toISOString();
  const run = {
    id: 1,
    event: 'pull_request',
    status: 'completed',
    conclusion: 'success',
    head_sha: '1'.repeat(40),
    head_branch: 'codex/example',
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
  const proof = (name, start, end) => ({
    name,
    status: 'completed',
    conclusion: 'success',
    started_at: date(start),
    completed_at: date(end),
  });
  const v2Jobs = [
    ...jobs.filter((job) => job.name !== 'trusted-proof-payload-v2'),
    {
      ...job(9, 'trusted-proof-payload-v2-first', 10, 250),
      steps: [
        proof('Current-head R4 E2P first clean production and verifier Native AOT build', 30, 240),
      ],
    },
    {
      ...job(10, 'trusted-proof-payload-v2-second', 20, 290),
      steps: [
        proof('Current-head R4 E2P second clean production and verifier Native AOT build', 40, 280),
      ],
    },
    {
      ...job(11, 'trusted-proof-payload-v2', 290, 320, 290),
      steps: [proof('Require both complete current-head V2 proofs to succeed', 292, 294)],
    },
  ];
  const split = measure(run, { jobs: v2Jobs });
  assert.equal(split.totalElapsedMinutes, 9);
  assert.equal(split.totalRunnerMinutes, 37);
  assert.equal(split.v2.activeCriticalPathMinutes, 5);
  assert.equal(split.v2.aggregateActiveMinutes, 0.5);
  assert.equal(split.v2.proofOverlapSeconds, 200);
  assert.equal(split.v2.observedWorkerConcurrency, 2);
  assert.equal(split.workflowActiveCriticalPathMinutes, 9);
  assert.equal(
    split.jobs.find((row) => row.name === 'trusted-proof-payload-v2-first').setupSeconds,
    20,
  );
  assert.equal(
    split.jobs.find((row) => row.name === 'trusted-proof-payload-v2-second').proofs[0]
      .activeMinutes,
    4,
  );
  const main = { ...run, event: 'push', head_branch: 'main' };
  assert.equal(measure(main, { jobs: v2Jobs }, true).event, 'push');
  assert.equal(measure(main, { jobs }, true).v2.topology, 'serial');
  assert.throws(() => measure(main, { jobs: v2Jobs }));
  assert.throws(() => measure({ ...main, head_branch: 'other' }, { jobs: v2Jobs }, true));
  assert.throws(() => measure({ ...main, event: 'workflow_dispatch' }, { jobs: v2Jobs }, true));
  assert.throws(() => measure({ ...run, head_sha: 'unknown' }, { jobs: v2Jobs }));
  assert.throws(() =>
    measure(run, { jobs: v2Jobs.filter((job) => job.name !== 'trusted-proof-payload-v2-first') }),
  );
  assert.throws(() =>
    measure(run, { jobs: v2Jobs.filter((job) => job.name !== 'trusted-proof-payload-v2') }),
  );
  assert.throws(() =>
    measure(run, { jobs: v2Jobs.filter((job) => !/v2-(first|second)$/.test(job.name)) }),
  );
  for (const change of [
    (job) => {
      job.runner_id = 9;
    },
    (job) => {
      job.steps[0].conclusion = 'skipped';
    },
    (job) => {
      job.steps = [];
    },
    (job) => {
      job.name = 'trusted-proof-payload-v2-first';
    },
    (job) => {
      job.steps[0].started_at = date(0);
    },
    (job) => {
      job.started_at = date(240);
      job.completed_at = date(500);
      job.steps[0].started_at = date(250);
      job.steps[0].completed_at = date(490);
    },
  ]) {
    const changed = structuredClone(v2Jobs);
    change(changed.find((job) => job.name === 'trusted-proof-payload-v2-second'));
    assert.throws(() => measure(run, { jobs: changed }));
  }
  console.log(
    'runtime_ci_timing_verified serial=passed parallel=passed v2_parallel=passed main_push=passed refusals=passed',
  );
}

if (process.argv.length === 3 && process.argv[2] === '--self-test') {
  selfTest();
} else {
  const main = process.argv[2] === '--main';
  const files = process.argv.slice(main ? 3 : 2);
  assert.equal(
    files.length,
    2,
    'Usage: node scripts/verify-runtime-ci-timing.mjs [--main] <run.json> <jobs.json>',
  );
  const read = (file) => JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
  console.log(JSON.stringify(measure(read(files[0]), read(files[1]), main), null, 2));
}
