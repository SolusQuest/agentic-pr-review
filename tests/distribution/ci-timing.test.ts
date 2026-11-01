import { expect, test } from 'vitest';
import { measure } from '../../scripts/verify-runtime-ci-timing.mjs';
import { sourceCommands } from '../../scripts/ci/workflow.mjs';

const date = (seconds: number) => new Date(Date.UTC(2026, 0, 1) + seconds * 1000).toISOString();
const run = {
  id: 22,
  event: 'pull_request',
  status: 'completed',
  conclusion: 'success',
  head_sha: '1'.repeat(40),
  head_branch: 'codex/ci',
  created_at: date(0),
  run_attempt: 1,
};
function job(
  name: string,
  index: number,
  start = 10,
  end = 100,
  step = 'Execute complete assigned check',
) {
  return {
    id: index + 1,
    run_id: 22,
    run_attempt: 1,
    head_sha: run.head_sha,
    name,
    runner_id: index + 1,
    status: 'completed',
    conclusion: 'success',
    created_at: date(start - 2),
    started_at: date(start),
    completed_at: date(end),
    steps: [
      {
        name: step,
        status: 'completed',
        conclusion: 'success',
        started_at: date(start + 1),
        completed_at: date(end - 1),
      },
    ],
  };
}
function sourceJobs() {
  return [
    ...Object.keys(sourceCommands).map((name, i) => job(name, i)),
    job('check', 20, 110, 115, 'Require all check dependencies to succeed'),
  ];
}
function runtimeJobs() {
  const names = [
    'runtime-core',
    'runtime-lifecycle',
    'r2-agent-loop',
    'R4 ActionHost (framework, first)',
    'R4 ActionHost (framework, second)',
    'r5-proof',
    'r7-capacity',
    'r6-economics-gate',
    'trusted-proof-payload',
    'integration',
  ];
  const steps = [
    'Runtime tests',
    'R3 deterministic live-agent replacement proof',
    'Complete R2 Agent verification',
    'Run isolated ActionHost proof',
    'Run isolated ActionHost proof',
    'R5 deterministic evaluation gate in the retained framework supporting fixture',
    'R7 capacity gate in the retained framework and AOT supporting fixture',
    'R6 offline economics gate in the retained framework supporting fixture',
    'R4 E2P first clean production and verifier Native AOT build',
    'Trusted-live policy and dry-run',
  ];
  return [
    ...names.map((name, i) => job(name, i, 10, 100, steps[i])),
    job(
      'trusted-proof-payload-v2-first',
      10,
      10,
      140,
      'Current-head D5 first independent managed V2 production/verifier proof',
    ),
    job(
      'trusted-proof-payload-v2-second',
      11,
      10,
      145,
      'Current-head D5 second independent managed V2 production/verifier proof',
    ),
    job(
      'trusted-proof-payload-v2',
      12,
      150,
      155,
      'Require both complete current-head V2 proofs to succeed',
    ),
    job('runtime', 13, 110, 115, 'Require all former Runtime lanes to succeed'),
    job(
      'r5-evaluation-gate',
      14,
      110,
      115,
      'Require all r5-evaluation-gate dependencies to succeed',
    ),
  ];
}

test('reports actual complete source overlap, setup, queue and aggregate critical path', () => {
  const result = measure(run, { jobs: sourceJobs() }, false, true);
  expect(result.topology).toBe('source-shards');
  expect(result.runtimeActiveCriticalPathMinutes).toBeNull();
  expect(result.sourceActiveCriticalPathMinutes).toBe(1.58);
  expect(result.totalElapsedMinutes).toBe(1.92);
  expect(result.workflowActiveCriticalPathMinutes).toBe(1.58);
  expect(result.maximumJobQueueSeconds).toBe(2);
  expect(result.observedPeakConcurrency).toBe(11);
  expect(result.jobs[0].setupSeconds).toBe(1);
  for (const mutate of [
    (j) => j.pop(),
    (j) => j.splice(3, 1),
    (j) => j.push(j[0]),
    (j) => {
      j[0].conclusion = 'skipped';
    },
    (j) => {
      j[0].steps = [];
    },
    (j) => {
      j.at(-1).started_at = date(50);
    },
    (j) => {
      j[0].head_sha = '2'.repeat(40);
    },
    (j) => {
      j[0].run_attempt = 2;
    },
    (j) => {
      j[0].run_id = 99;
    },
  ]) {
    const jobs = sourceJobs();
    mutate(jobs);
    expect(() => measure(run, { jobs }, false, true)).toThrow();
  }
});

test('requires the complete split Runtime DAG and keeps proof overlap and aggregates distinct', () => {
  const result = measure(run, { jobs: runtimeJobs() });
  expect(result.topology).toBe('parallel-split-gates');
  expect(result.r5ActiveCriticalPathMinutes).toBe(1.58);
  expect(result.v2.proofOverlapSeconds).toBeGreaterThan(0);
  expect(() =>
    measure(run, {
      jobs: runtimeJobs().filter(
        (j) => !['runtime-lifecycle', 'r5-proof', 'r7-capacity'].includes(j.name),
      ),
    }),
  ).toThrow();
  expect(() =>
    measure(run, { total_count: runtimeJobs().length + 1, jobs: runtimeJobs() }),
  ).toThrow();
  for (const name of runtimeJobs().map((j) => j.name))
    expect(() => measure(run, { jobs: runtimeJobs().filter((j) => j.name !== name) })).toThrow();
  const early = runtimeJobs();
  early.find((j) => j.name === 'r5-evaluation-gate').started_at = date(40);
  expect(() => measure(run, { jobs: early })).toThrow();
});

test('excludes only the dormant zero-step qualifier and refuses missing/failed/cancelled proof evidence', () => {
  const skipped = { ...job('qualify-original-pair', 30, 0, 0), conclusion: 'skipped', steps: [] };
  expect(measure(run, { jobs: [...runtimeJobs(), skipped] }).excludedDormantQualifierJobs).toBe(1);
  for (const change of [
    { conclusion: 'success' },
    { conclusion: 'cancelled' },
    { steps: [{ name: 'unexpected' }] },
  ])
    expect(() => measure(run, { jobs: [...runtimeJobs(), { ...skipped, ...change }] })).toThrow();
  for (const conclusion of ['failure', 'cancelled', 'skipped']) {
    const jobs = runtimeJobs();
    jobs.find((j) => j.name === 'r7-capacity').conclusion = conclusion;
    expect(() => measure(run, { jobs })).toThrow();
  }
  expect(() => measure({ ...run, conclusion: 'failure' }, { jobs: runtimeJobs() })).toThrow();
  expect(() =>
    measure({ ...run, event: 'push', head_branch: 'main' }, { jobs: runtimeJobs() }),
  ).toThrow();
  expect(
    measure({ ...run, event: 'push', head_branch: 'main' }, { jobs: runtimeJobs() }, true).event,
  ).toBe('push');
});
