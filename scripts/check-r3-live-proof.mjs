import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { parseDocument } from 'yaml';

const root = path.resolve(import.meta.dirname, '..');
const workflowPath = process.env.APR_R3_LIVE_POLICY_WORKFLOW
  ? path.resolve(process.env.APR_R3_LIVE_POLICY_WORKFLOW)
  : path.join(root, '.github', 'workflows', 'r3-live-proof.yml');
const workflowsRoot = path.dirname(workflowPath);
const dedicatedSecret = 'secrets.R3_LIVE_PROOF_DEEPSEEK_API_KEY';
const retiredSecret = 'secrets.AGENTIC_REVIEW_DEEPSEEK_API_KEY';
const checkoutSha = 'd23441a48e516b6c34aea4fa41551a30e30af803';
const setupDotnetSha = '26b0ec14cb23fa6904739307f278c14f94c95bf1';

function fail(code) {
  throw new Error(code);
}

function readYaml(file) {
  const source = fs.readFileSync(file, 'utf8');
  const document = parseDocument(source, {
    schema: 'core',
    strict: true,
    uniqueKeys: true,
  });
  if (document.errors.length !== 0 || document.warnings.length !== 0) {
    fail('APR_R3_LIVE_POLICY_YAML_INVALID');
  }
  const value = document.toJS({ maxAliasCount: 0 });
  if (value === null || Array.isArray(value) || typeof value !== 'object') {
    fail('APR_R3_LIVE_POLICY_SHAPE_INVALID');
  }
  return { source, value };
}

function exactKeys(value, expected, code) {
  if (
    value === null ||
    Array.isArray(value) ||
    typeof value !== 'object' ||
    Object.keys(value).sort().join('\n') !== [...expected].sort().join('\n')
  ) {
    fail(code);
  }
}

function requireExactStep(steps, name, run) {
  const matches = steps.filter((step) => step?.name === name);
  if (matches.length !== 1 || matches[0].run !== run) {
    fail('APR_R3_LIVE_POLICY_STEP_INVALID');
  }
  return matches[0];
}

export function checkTrustedLivePolicy() {
  const { source, value } = readYaml(workflowPath);
  exactKeys(
    value,
    ['name', 'on', 'permissions', 'concurrency', 'jobs'],
    'APR_R3_LIVE_POLICY_TOP_LEVEL_INVALID',
  );
  exactKeys(value.on, ['workflow_dispatch'], 'APR_R3_LIVE_POLICY_TRIGGER_INVALID');
  if (
    value.on.workflow_dispatch !== null &&
    (typeof value.on.workflow_dispatch !== 'object' ||
      Object.keys(value.on.workflow_dispatch).length !== 0)
  ) {
    fail('APR_R3_LIVE_POLICY_INPUTS_INVALID');
  }
  exactKeys(value.permissions, ['contents'], 'APR_R3_LIVE_POLICY_PERMISSIONS_INVALID');
  if (value.permissions.contents !== 'read') {
    fail('APR_R3_LIVE_POLICY_PERMISSIONS_INVALID');
  }
  if (
    value.concurrency?.group !== 'r3-trusted-live-proof' ||
    value.concurrency?.['cancel-in-progress'] !== false
  ) {
    fail('APR_R3_LIVE_POLICY_CONCURRENCY_INVALID');
  }
  exactKeys(
    value.concurrency,
    ['group', 'cancel-in-progress'],
    'APR_R3_LIVE_POLICY_CONCURRENCY_INVALID',
  );
  exactKeys(value.jobs, ['live-proof'], 'APR_R3_LIVE_POLICY_JOB_INVALID');
  const job = value.jobs['live-proof'];
  exactKeys(
    job,
    ['if', 'runs-on', 'timeout-minutes', 'environment', 'steps'],
    'APR_R3_LIVE_POLICY_JOB_INVALID',
  );
  if (
    job.if !==
      "github.repository == 'SolusQuest/agentic-pr-review' && github.ref == 'refs/heads/main'" ||
    job['runs-on'] !== 'ubuntu-24.04' ||
    job['timeout-minutes'] !== 45 ||
    job.environment?.name !== 'r3-live-proof' ||
    job.environment?.deployment !== false ||
    !Array.isArray(job.steps)
  ) {
    fail('APR_R3_LIVE_POLICY_JOB_INVALID');
  }
  exactKeys(job.environment, ['name', 'deployment'], 'APR_R3_LIVE_POLICY_ENVIRONMENT_INVALID');
  const expectedStepNames = [
    'Admit the workflow lineage',
    'Checkout the exact authorized commit',
    'Set up the reviewed .NET SDK',
    'Prove exact source provenance',
    'Prepare the exact trusted-live artifact without secrets',
    'Run the deterministic preflight without secrets',
    'Re-admit source and the prepared artifact without secrets',
    'Run the bounded trusted live proof',
  ];
  if (job.steps.map((step) => step?.name).join('\n') !== expectedStepNames.join('\n')) {
    fail('APR_R3_LIVE_POLICY_STEP_ORDER_INVALID');
  }

  const lineage = job.steps[0];
  exactKeys(lineage, ['name', 'env', 'run'], 'APR_R3_LIVE_POLICY_LINEAGE_INVALID');
  exactKeys(
    lineage.env,
    ['ACTUAL_SHA', 'WORKFLOW_REF', 'WORKFLOW_SHA'],
    'APR_R3_LIVE_POLICY_LINEAGE_INVALID',
  );
  if (
    lineage.env.ACTUAL_SHA !== '${{ github.sha }}' ||
    lineage.env.WORKFLOW_REF !== '${{ github.workflow_ref }}' ||
    lineage.env.WORKFLOW_SHA !== '${{ github.workflow_sha }}' ||
    !lineage.run.includes(
      'SolusQuest/agentic-pr-review/.github/workflows/r3-live-proof.yml@refs/heads/main',
    ) ||
    !lineage.run.includes('[[ "${WORKFLOW_SHA}" == "${ACTUAL_SHA}" ]]')
  ) {
    fail('APR_R3_LIVE_POLICY_LINEAGE_INVALID');
  }

  const externalUses = job.steps.filter((step) => typeof step?.uses === 'string');
  if (
    externalUses.length !== 2 ||
    externalUses[0].uses !== `actions/checkout@${checkoutSha}` ||
    externalUses[1].uses !== `actions/setup-dotnet@${setupDotnetSha}`
  ) {
    fail('APR_R3_LIVE_POLICY_ACTION_INVALID');
  }
  for (const step of externalUses) {
    if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+@[0-9a-f]{40}$/.test(step.uses)) {
      fail('APR_R3_LIVE_POLICY_ACTION_MUTABLE');
    }
  }
  const checkout = externalUses[0];
  exactKeys(checkout, ['name', 'uses', 'with'], 'APR_R3_LIVE_POLICY_CHECKOUT_INVALID');
  exactKeys(
    checkout.with,
    ['fetch-depth', 'persist-credentials', 'ref'],
    'APR_R3_LIVE_POLICY_CHECKOUT_INVALID',
  );
  if (
    checkout.with?.['fetch-depth'] !== 0 ||
    checkout.with?.['persist-credentials'] !== false ||
    checkout.with?.ref !== '${{ github.sha }}'
  ) {
    fail('APR_R3_LIVE_POLICY_CHECKOUT_INVALID');
  }
  const setupDotnet = externalUses[1];
  exactKeys(setupDotnet, ['name', 'uses', 'with'], 'APR_R3_LIVE_POLICY_SETUP_INVALID');
  exactKeys(setupDotnet.with, ['global-json-file'], 'APR_R3_LIVE_POLICY_SETUP_INVALID');
  if (setupDotnet.with['global-json-file'] !== 'global.json') {
    fail('APR_R3_LIVE_POLICY_SETUP_INVALID');
  }

  if (
    job.steps[4].run !== 'bash runtime/scripts/verify-live-agent.sh live --prepare' ||
    job.steps[5].run !== 'bash runtime/scripts/verify-live-agent.sh deterministic' ||
    !job.steps[6].run.endsWith('bash runtime/scripts/verify-live-agent.sh live --verify-prepared\n')
  ) {
    fail('APR_R3_LIVE_POLICY_PREFLIGHT_INVALID');
  }

  const live = requireExactStep(
    job.steps,
    'Run the bounded trusted live proof',
    'exec runtime/scripts/verify-live-agent.sh live',
  );
  exactKeys(live, ['name', 'env', 'run'], 'APR_R3_LIVE_POLICY_SECRET_SCOPE_INVALID');
  exactKeys(
    live.env,
    ['AGENTIC_REVIEW_DEEPSEEK_API_KEY'],
    'APR_R3_LIVE_POLICY_SECRET_SCOPE_INVALID',
  );
  if (
    live.env.AGENTIC_REVIEW_DEEPSEEK_API_KEY !== '${{ secrets.R3_LIVE_PROOF_DEEPSEEK_API_KEY }}'
  ) {
    fail('APR_R3_LIVE_POLICY_SECRET_SCOPE_INVALID');
  }
  if ((source.match(new RegExp(dedicatedSecret, 'g')) ?? []).length !== 1) {
    fail('APR_R3_LIVE_POLICY_SECRET_ROUTE_INVALID');
  }

  const allWorkflowFiles = fs
    .readdirSync(workflowsRoot)
    .filter((name) => /\.ya?ml$/u.test(name))
    .map((name) => path.join(workflowsRoot, name));
  const allWorkflowSource = allWorkflowFiles
    .map((file) => fs.readFileSync(file, 'utf8'))
    .join('\n');
  if (
    (allWorkflowSource.match(new RegExp(dedicatedSecret, 'g')) ?? []).length !== 1 ||
    allWorkflowSource.includes(retiredSecret) ||
    /secrets\s*:\s*inherit/u.test(source) ||
    /probe-deepseek-request|DeepSeekCompatibilityProbe/u.test(allWorkflowSource)
  ) {
    fail('APR_R3_LIVE_POLICY_PROVIDER_ROUTE_INVALID');
  }

  const forbidden = [
    'pull_request_target',
    'actions/upload-artifact',
    'actions/cache',
    'GITHUB_ENV',
    'GITHUB_OUTPUT',
    'GITHUB_STEP_SUMMARY',
    '::notice',
    '::warning',
    '::error',
    'gh api',
    'gh pr',
    'git push',
    'curl ',
    'wget ',
  ];
  if (forbidden.some((token) => source.includes(token))) {
    fail('APR_R3_LIVE_POLICY_PUBLICATION_INVALID');
  }
  if (
    !source.includes('github.workflow_ref') ||
    !source.includes('github.workflow_sha') ||
    !source.includes('git merge-base --is-ancestor') ||
    !source.includes('git status --porcelain=v1 --untracked-files=all')
  ) {
    fail('APR_R3_LIVE_POLICY_PROVENANCE_INVALID');
  }
  const runtimeReadme = fs.readFileSync(path.join(root, 'runtime', 'README.md'), 'utf8');
  const prerequisite = [
    'selected branches and tags deployment policy allowing only branch `main`',
    'no other branch pattern and no tag pattern',
    'built-in required-reviewer rule',
    'environment-only `R3_LIVE_PROOF_DEEPSEEK_API_KEY`',
    '`Protected branches only` is not an acceptable substitute',
  ];
  if (prerequisite.some((text) => !runtimeReadme.includes(text))) {
    fail('APR_R3_LIVE_POLICY_EXTERNAL_PREREQUISITE_INVALID');
  }
  return true;
}

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(import.meta.filename)) {
  try {
    checkTrustedLivePolicy();
    process.stdout.write('APR_R3_LIVE_POLICY_OK\n');
  } catch (error) {
    process.stderr.write(
      `${error instanceof Error ? error.message : 'APR_R3_LIVE_POLICY_FAILED'}\n`,
    );
    process.exitCode = 1;
  }
}
