import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { parseDocument } from 'yaml';

import { extractPreflight, runExtractedPreflight } from './check-r4-e2p-preflight.mjs';

const expectedJobs = ['authorization-preflight', 'workflow-run-review', 'workflow-dispatch-review'];

function fail(code) {
  throw new Error(code);
}

function readWorkflow(workflowPath) {
  const source = fs.readFileSync(workflowPath, 'utf8');
  if (Buffer.byteLength(source, 'utf8') > 256 * 1024 || source.includes('\r')) {
    fail('APR_R4_E2P_NO_LAUNCH_WORKFLOW_INVALID');
  }
  const document = parseDocument(source, {
    schema: 'core',
    strict: true,
    uniqueKeys: true,
  });
  if (document.errors.length !== 0 || document.warnings.length !== 0) {
    fail('APR_R4_E2P_NO_LAUNCH_WORKFLOW_INVALID');
  }
  const workflow = document.toJS({ maxAliasCount: 0 });
  if (
    workflow === null ||
    Array.isArray(workflow) ||
    typeof workflow !== 'object' ||
    workflow.jobs === null ||
    Array.isArray(workflow.jobs) ||
    typeof workflow.jobs !== 'object' ||
    JSON.stringify(Object.keys(workflow.jobs)) !== JSON.stringify(expectedJobs)
  ) {
    fail('APR_R4_E2P_NO_LAUNCH_WORKFLOW_INVALID');
  }
  return { source, jobs: workflow.jobs };
}

function verifyProtectedJobGates(jobs) {
  const expected = [
    [
      'workflow-run-review',
      "${{ github.event_name == 'workflow_run' && needs.authorization-preflight.outputs.authorized == 'true' }}",
    ],
    [
      'workflow-dispatch-review',
      "${{ github.event_name == 'workflow_dispatch' && needs.authorization-preflight.outputs.authorized == 'true' }}",
    ],
  ];
  for (const [name, condition] of expected) {
    const job = jobs[name];
    if (
      job === null ||
      Array.isArray(job) ||
      typeof job !== 'object' ||
      job.needs !== 'authorization-preflight' ||
      job.if !== condition
    ) {
      fail('APR_R4_E2P_NO_LAUNCH_GATE_INVALID');
    }
  }
}

function unauthorizedEnvironment() {
  const repository = 'SolusQuest/agentic-pr-review';
  const repositoryId = '42';
  const prNumber = '147';
  const workflowSha = 'a'.repeat(40);
  const fixtureHeadSha = 'e'.repeat(40);
  const operationId = '1'.repeat(64);
  const revokedAuthorization = JSON.stringify({
    kind: 'apr-r4-e2p-authorization-manifest-v2',
    repository_id: repositoryId,
    repository,
    pr_number: prNumber,
    fixture_head_sha: fixtureHeadSha,
    operation_id: '2'.repeat(64),
    workflow_sha: workflowSha,
    action_source_sha: workflowSha,
    payload_source_sha: workflowSha,
    payload_sha256: 'f'.repeat(64),
  });
  return {
    EVENT_NAME: 'workflow_run',
    EVENT_ACTION: 'completed',
    EVENT_CONCLUSION: 'success',
    EVENT_PR_NUMBER: prNumber,
    EVENT_HEAD_SHA: fixtureHeadSha,
    EVENT_PULL_REQUESTS_JSON: JSON.stringify([{ number: 147, head: { sha: fixtureHeadSha } }]),
    EVENT_WORKFLOW_ID: '294554742',
    EVENT_WORKFLOW_NAME: 'CI',
    EVENT_WORKFLOW_PATH: '.github/workflows/ci.yml',
    EVENT_TRIGGER_EVENT: 'pull_request',
    EVENT_REPOSITORY: repository,
    EVENT_REPOSITORY_ID: repositoryId,
    EVENT_HEAD_REPOSITORY: repository,
    EVENT_HEAD_REPOSITORY_ID: repositoryId,
    INPUT_PR_NUMBER: '',
    REPOSITORY: repository,
    REPOSITORY_ID: repositoryId,
    WORKFLOW_SHA: workflowSha,
    ACTION_SOURCE_SHA: workflowSha,
    PAYLOAD_SOURCE_SHA: workflowSha,
    PAYLOAD_SHA256: 'f'.repeat(64),
    // Deliberately revoked after strict canonical parsing: the manifest's
    // operation id does not match the admitted fixture branch.
    R4_TRUSTED_PROOF_AUTHORIZATION: revokedAuthorization,
    __pull: {
      number: 147,
      state: 'open',
      draft: false,
      merged_at: null,
      base: { ref: 'main', repo: { id: 42, full_name: repository }, sha: workflowSha },
      head: {
        ref: `r4-trusted-proof/${operationId}`,
        repo: { id: 42, full_name: repository },
        sha: fixtureHeadSha,
      },
    },
  };
}

/**
 * Executes the exact inline preflight from the rendered workflow with a
 * revoked authorization. The protected-job predicates are then checked from
 * that same workflow before a launch spy could be invoked.
 */
export async function probeUnauthorizedNoLaunch(workflowPath) {
  const { source, jobs } = readWorkflow(workflowPath);
  verifyProtectedJobGates(jobs);
  const environment = unauthorizedEnvironment();
  let fetches = 0;
  let authorizationHeaderPresent = false;
  const preflight = await runExtractedPreflight({
    source: extractPreflight(source),
    environment,
    fetchImpl: async (_url, init) => {
      fetches += 1;
      authorizationHeaderPresent =
        authorizationHeaderPresent ||
        Object.keys(init?.headers ?? {}).some((name) => /authorization|token|secret/iu.test(name));
      return new Response(JSON.stringify(environment.__pull), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      });
    },
  });
  const admitted = preflight.stdout.startsWith('authorized=true\n');
  const workflowRunEligible = environment.EVENT_NAME === 'workflow_run' && admitted;
  const workflowDispatchEligible = environment.EVENT_NAME === 'workflow_dispatch' && admitted;
  const starts = {
    payload: 0,
    wrapper: 0,
    provider: 0,
    state: 0,
    publisher: 0,
    csharp_payload_receipt: 0,
    node_artifact_receipt: 0,
    embedded_control_receipt: 0,
    external_control_receipt: 0,
  };
  const launchProtectedRoute = () => {
    // This branch is intentionally the only payload authority in the probe.
    // A revoked preflight must leave every observable launch sink at zero.
    for (const name of Object.keys(starts)) starts[name] += 1;
  };
  if (workflowRunEligible || workflowDispatchEligible) {
    launchProtectedRoute();
  }
  if (
    admitted ||
    fetches !== 1 ||
    authorizationHeaderPresent ||
    preflight.stdout !==
      'authorized=false\npr-number=\nfixture-head-sha=\noperation-id=\nauthorization-manifest-digest=\nexpected-payload-sha256=\n' ||
    !preflight.stderr.startsWith('APR_R4_E2P_PREFLIGHT_REJECTED authorization-mismatch\n') ||
    workflowRunEligible ||
    workflowDispatchEligible ||
    Object.values(starts).some((value) => value !== 0)
  ) {
    fail('APR_R4_E2P_NO_LAUNCH_PROBE_FAILED');
  }
  return {
    schema: 'apr.r4.e2p.unauthorized-no-launch.v1',
    preflight_admitted: admitted,
    public_preflight_requests: fetches,
    preflight_authorization_header_present: authorizationHeaderPresent,
    workflow_run_review_eligible: workflowRunEligible,
    workflow_dispatch_review_eligible: workflowDispatchEligible,
    starts,
  };
}

function main() {
  const workflowPath = process.argv[2];
  if (process.argv.length !== 3 || !workflowPath) {
    fail('APR_R4_E2P_NO_LAUNCH_USAGE');
  }
  return probeUnauthorizedNoLaunch(path.resolve(workflowPath)).then((result) => {
    process.stdout.write(`${JSON.stringify(result)}\n`);
  });
}

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(import.meta.filename)) {
  main().catch((error) => {
    process.stderr.write(
      `${error instanceof Error ? error.message : 'APR_R4_E2P_NO_LAUNCH_FAILED'}\n`,
    );
    process.exitCode = 1;
  });
}
