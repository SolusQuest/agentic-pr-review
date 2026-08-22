import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import Ajv from 'ajv';
import { parseDocument } from 'yaml';
import { extractPreflight } from './check-r4-e2p-preflight.mjs';
import { verifyReceipt } from './check-r4-e2p-receipt.mjs';

const repositoryRoot = path.resolve(import.meta.dirname, '..');
const fixtureRelative = 'runtime/tests/fixtures/action-host/trusted-proof';
const templateRelative =
  'runtime/tests/fixtures/action-host/trusted-proof-payload/workflow/r4-trusted-proof.yml.template';
const workflowRelative = '.github/workflows/r4-trusted-proof.yml';
const actionSourceSha = '5b5769753653bb3fd3e68cf8b7bb88a1bd350613';
const payloadSha256 = '97af2b7b0160e333862e74e5e421b2e802f3962d1bb6405c909301971a0130fc';
const templateSha256 = 'fa458399a93c0dc71d0f071eeea3abb7670382c5545f58dae90ca1cf9649c03a';
const renderedWorkflowSha256 = 'd62625f4fd4cb0c3e327a80a9dcd3dfb0aca957f9e63171fa0f12a553033b603';
const receiptLineSha256 = '3fa55211baa43da955a2eb083b2188a1fde193e6684cb129ec99f5f35374ad49';
const receiptJsonSha256 = '9b95a87e5f40d7b506e25426e3905aaaf0510ad28d79c8a7ca3737a3952a7b34';
const normalCanarySha256 = 'bc58613e9f389b973f1e44de64021a7acc748fafd857b7fa99466493670db446';
const staleCanarySha256 = '20580dc6193f607a3a1d1b6949013796f8a8c9714d3e5586e4e509a473f1382a';
const fixtureInventory = [
  'authorization-environment-contract.json',
  'cleanup-contract.json',
  'fixture-pr-contract.json',
  'receipt-provenance.json',
  'schemas/host-restricted-evidence.schema.json',
  'schemas/public-safe-evidence.schema.json',
  'templates/host-restricted-evidence.json',
  'templates/public-safe-evidence.json',
  'traces/normal-two-run.json',
  'traces/stale-head.json',
  'trusted-proof-payload-receipt.json',
];
const fixtureDigests = new Map([
  [
    'authorization-environment-contract.json',
    '344e1f6a4c7ab1ea446c3343102a4a92cf06e5a16b7bfab64a5d9cfd59771e9f',
  ],
  ['cleanup-contract.json', '2458949e77631d001360dbe9405cc5566360a9fd6e0557a916e61f1fdf5c4459'],
  ['fixture-pr-contract.json', '347a2cdf30bd4a28e15f74d939d6c61519d6694022be03c4d5c3cb1c05224efc'],
  ['receipt-provenance.json', 'e0e83ca4c461197c4f4cd3ed37cd5fdafb137398c188068025179664943665b2'],
  [
    'schemas/host-restricted-evidence.schema.json',
    '1223884566151e03d94ebf54df0a83e9dbde1caa2ad733f0d1fbfb2f56efa4e5',
  ],
  [
    'schemas/public-safe-evidence.schema.json',
    'cfd733c71312ddd9e4d6539f512eef2c3c631b6d785e8c610d16f90aeac3f47d',
  ],
  [
    'templates/host-restricted-evidence.json',
    '6797d23f4960bc91f6dea7a7a9787278b8272c4c4ccc544585ef76942ee404dc',
  ],
  [
    'templates/public-safe-evidence.json',
    'd80500fdee7f553933345222101477b284e8ce8e0f34507a1de18ab455bb48e7',
  ],
  [
    'traces/normal-two-run.json',
    '8a55feff519e885c33101b08ef011edd1a97b1caee2f284b4963df342ad6b94e',
  ],
  ['traces/stale-head.json', 'd2057b969ad255ef0773029ea78376845ab14bfbdd754d3e92d6af3cb121c47d'],
  ['trusted-proof-payload-receipt.json', receiptJsonSha256],
]);

function fail(code) {
  throw new Error(`APR_R4_E3_POLICY_INVALID ${code}`);
}

function sha256(bytes) {
  return crypto.createHash('sha256').update(bytes).digest('hex');
}

function readBounded(pathname, maximum = 1024 * 1024) {
  const bytes = fs.readFileSync(pathname);
  if (bytes.length === 0 || bytes.length > maximum) fail('file-size');
  return bytes;
}

function parseCanonical(pathname, maximum = 1024 * 1024) {
  const bytes = readBounded(pathname, maximum);
  if (bytes.at(-1) !== 0x0a || bytes.includes(0x0d)) fail('canonical-encoding');
  let value;
  try {
    value = JSON.parse(bytes.toString('utf8'));
  } catch {
    fail('canonical-json');
  }
  if (`${JSON.stringify(value)}\n` !== bytes.toString('utf8')) fail('canonical-json');
  return { bytes, value };
}

function parseJsonDocument(pathname, maximum = 1024 * 1024) {
  const bytes = readBounded(pathname, maximum);
  if (bytes.at(-1) !== 0x0a || bytes.includes(0x0d)) fail('json-encoding');
  try {
    return { bytes, value: JSON.parse(bytes.toString('utf8')) };
  } catch {
    fail('json');
  }
}

function exactKeys(value, keys, code) {
  if (
    value === null ||
    Array.isArray(value) ||
    typeof value !== 'object' ||
    JSON.stringify(Object.keys(value)) !== JSON.stringify(keys)
  ) {
    fail(code);
  }
}

function count(source, value) {
  return source.split(value).length - 1;
}

function listFiles(root) {
  const result = [];
  const visit = (directory) => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const absolute = path.join(directory, entry.name);
      if (entry.isDirectory()) visit(absolute);
      else if (entry.isFile()) result.push(path.relative(root, absolute).replaceAll('\\', '/'));
      else fail('fixture-non-file');
    }
  };
  visit(root);
  return result.sort();
}

function parseWorkflow(pathname) {
  const source = readBounded(pathname).toString('utf8');
  const document = parseDocument(source, { schema: 'core', strict: true, uniqueKeys: true });
  if (document.errors.length !== 0 || document.warnings.length !== 0) fail('workflow-yaml');
  const value = document.toJS({ maxAliasCount: 0 });
  if (value === null || Array.isArray(value) || typeof value !== 'object') fail('workflow-shape');
  return { source, value };
}

function collectSecretExpressions(value, result) {
  if (typeof value === 'string') {
    if (/\$\{\{[\s\S]*\bsecrets\b[\s\S]*\}\}/u.test(value)) result.push(value);
    return;
  }
  if (Array.isArray(value)) {
    for (const item of value) collectSecretExpressions(item, result);
    return;
  }
  if (value !== null && typeof value === 'object') {
    for (const item of Object.values(value)) collectSecretExpressions(item, result);
  }
}

function validateFixtureContracts(fixtureRoot) {
  if (JSON.stringify(listFiles(fixtureRoot)) !== JSON.stringify(fixtureInventory)) {
    fail('fixture-inventory');
  }
  const documents = new Map(
    fixtureInventory.map((relative) => [
      relative,
      relative.startsWith('schemas/')
        ? parseJsonDocument(path.join(fixtureRoot, ...relative.split('/')))
        : parseCanonical(path.join(fixtureRoot, ...relative.split('/'))),
    ]),
  );
  for (const [relative, expected] of fixtureDigests) {
    if (sha256(documents.get(relative).bytes) !== expected) fail('fixture-digest');
  }
  const fixture = documents.get('fixture-pr-contract.json').value;
  const normalBytes = Buffer.concat([
    Buffer.from(fixture.normal.content_utf8, 'utf8'),
    Buffer.of(10),
  ]);
  const staleBytes = Buffer.concat([
    Buffer.from(fixture.stale.advanced_content_utf8, 'utf8'),
    Buffer.of(10),
  ]);
  if (
    fixture.base_source !== 'exact-authorized-default-branch-workflow-commit' ||
    fixture.base_requires_canary_absent !== true ||
    fixture.inherited_repository_tree !== 'unchanged' ||
    fixture.only_changed_path !== 'proof/apr178-path-canary.txt' ||
    fixture.normal.terminator_hex !== '0a' ||
    fixture.stale.advanced_terminator_hex !== '0a' ||
    sha256(normalBytes) !== normalCanarySha256 ||
    fixture.normal.content_sha256 !== normalCanarySha256 ||
    sha256(staleBytes) !== staleCanarySha256 ||
    fixture.stale.advanced_content_sha256 !== staleCanarySha256
  ) {
    fail('fixture-byte-identity');
  }
  const authorization = documents.get('authorization-environment-contract.json').value;
  if (
    authorization.authorization_variable?.name !== 'R4_TRUSTED_PROOF_AUTHORIZATION' ||
    authorization.authorization_variable?.default !== 'absent' ||
    authorization.environment?.name !== 'r4-trusted-proof' ||
    authorization.environment?.must_preexist !== true ||
    authorization.live_mutation_owner !== 'issue-181'
  ) {
    fail('authorization-environment-contract');
  }
  const cleanup = documents.get('cleanup-contract.json').value;
  if (
    cleanup.pre_state?.required !== 'empty' ||
    cleanup.pre_state?.complete_pagination !== true ||
    cleanup.operation_state?.reject_unowned_addition !== true ||
    cleanup.pre_state?.families?.length !== 10 ||
    cleanup.operation_state?.creation_phases?.includes('stale-setup') !== true ||
    cleanup.public_projection_gate !==
      'exact-empty-final-state-complete-resource-readback-and-proof-control-cleanup' ||
    cleanup.invalid_terminal_states?.includes('delete-or-retain') !== true
  ) {
    fail('cleanup-contract');
  }
  const normalTrace = documents.get('traces/normal-two-run.json').value;
  if (
    normalTrace.run_one?.run_id === normalTrace.run_two?.run_id ||
    normalTrace.run_one?.run_attempt < 1 ||
    normalTrace.run_two?.run_attempt < 1 ||
    !(
      normalTrace.workflow_sha === normalTrace.reviewed_base_sha &&
      normalTrace.normal_parent_sha === normalTrace.reviewed_base_sha &&
      normalTrace.run_one.protected_job_started_at < normalTrace.run_one.barrier_ready_at &&
      normalTrace.run_one.barrier_ready_at <= normalTrace.run_two.created_at &&
      normalTrace.run_two.created_at <= normalTrace.observation.observed_at &&
      normalTrace.observation.observed_at < normalTrace.run_one.barrier_released_at &&
      normalTrace.run_one.barrier_released_at < normalTrace.run_one.completed_at &&
      normalTrace.run_one.completed_at < normalTrace.run_two.protected_job_started_at
    ) ||
    normalTrace.run_two.privileged_job_allocated_at_observation !== false ||
    normalTrace.run_two.environment_admission_started_at_observation !== false ||
    normalTrace.run_two.protected_step_started_at_observation !== null
  ) {
    fail('normal-trace');
  }
  const staleTrace = documents.get('traces/stale-head.json').value;
  if (
    staleTrace.operation_id !== '7'.repeat(64) ||
    staleTrace.workflow_sha !== staleTrace.reviewed_base_sha ||
    staleTrace.admitted_parent_sha !== staleTrace.reviewed_base_sha ||
    staleTrace.advanced_parent_sha !== staleTrace.admitted_head_sha ||
    staleTrace.changed_paths?.length !== 1 ||
    staleTrace.changed_paths[0] !== 'proof/apr178-path-canary.txt' ||
    staleTrace.advanced_content_sha256 !== staleCanarySha256 ||
    staleTrace.authorized_stale_run?.privileged_job_allocated !== true ||
    staleTrace.authorized_stale_run?.provider_constructed !== true ||
    staleTrace.authorized_stale_run?.value_free_signal_count !== 1 ||
    staleTrace.authorized_stale_run?.host_exact_head_result !== 'stale-head-rejected' ||
    staleTrace.unauthorized_follow_on_run?.old_authorization_matches !== false ||
    staleTrace.unauthorized_follow_on_run?.privileged_job_allocated !== false ||
    staleTrace.unauthorized_follow_on_run?.state_mutated !== false ||
    staleTrace.unauthorized_follow_on_run?.status !== 'completed-inert' ||
    staleTrace.all_follow_on_runs_terminal !== true
  ) {
    fail('stale-trace');
  }
  const ajv = new Ajv({ allErrors: true, strict: true });
  const validateHost = ajv.compile(
    documents.get('schemas/host-restricted-evidence.schema.json').value,
  );
  const validatePublic = ajv.compile(
    documents.get('schemas/public-safe-evidence.schema.json').value,
  );
  if (!validateHost(documents.get('templates/host-restricted-evidence.json').value)) {
    fail('host-template-schema');
  }
  if (!validatePublic(documents.get('templates/public-safe-evidence.json').value)) {
    fail('public-template-schema');
  }
  return documents;
}

function validateRepositorySecretRoutes(workflowsRoot) {
  const workflowFiles = fs
    .readdirSync(workflowsRoot)
    .filter((name) => /\.ya?ml$/u.test(name))
    .sort();
  const observed = [];
  let allSource = '';
  const proofAnchors = [
    'R4_TRUSTED_PROOF_AUTHORIZATION',
    'environment: r4-trusted-proof',
    'prepare-r4-trusted-proof-payload.sh',
    'SolusQuest/agentic-pr-review/.github/actions/agentic-pr-review@5b5769753653bb3fd3e68cf8b7bb88a1bd350613',
    'AGENTIC_PR_REVIEW_PREPARED_',
    'AGENTIC_PR_REVIEW_ACTION_SOURCE_SHA',
    'AGENTIC_PR_REVIEW_PAYLOAD_BUILD_DISCRIMINATOR',
    'barrier hold',
    'barrier verify-completed',
    'barrier cleanup',
  ];
  for (const name of workflowFiles) {
    const { source, value } = parseWorkflow(path.join(workflowsRoot, name));
    allSource += `${source}\n`;
    if (name !== 'r4-trusted-proof.yml' && proofAnchors.some((anchor) => source.includes(anchor))) {
      fail('repository-proof-route-owner');
    }
    const expressions = [];
    collectSecretExpressions(value, expressions);
    for (const expression of expressions) observed.push(`${name}\0${expression}`);
  }
  const expected = [
    `r3-live-proof.yml\0\${{ secrets.R3_LIVE_PROOF_DEEPSEEK_API_KEY }}`,
    ...Array.from({ length: 4 }, () => `r4-trusted-proof.yml\0\${{ secrets.GITHUB_TOKEN }}`),
    ...Array.from({ length: 2 }, () => `r4-trusted-proof.yml\0\${{ secrets.DEEPSEEK_API_KEY }}`),
    ...Array.from(
      { length: 2 },
      () => `r4-trusted-proof.yml\0\${{ secrets.AGENTIC_PR_REVIEW_STATE_KEY }}`,
    ),
    ...Array.from(
      { length: 2 },
      () => `r4-trusted-proof.yml\0\${{ secrets.AGENTIC_PR_REVIEW_PREVIOUS_STATE_KEY || '' }}`,
    ),
  ];
  if (JSON.stringify(observed.sort()) !== JSON.stringify(expected.sort())) {
    fail('repository-secret-routes');
  }
  if (
    /secrets\s*:\s*inherit/u.test(allSource) ||
    allSource.includes('secrets.AGENTIC_REVIEW_DEEPSEEK_API_KEY') ||
    /pull_request_target|actions\/(?:upload|download)-artifact|actions\/cache/u.test(allSource)
  ) {
    fail('repository-alternate-route');
  }
}

export function checkR4TrustedProof(options = {}) {
  const root = options.root ?? repositoryRoot;
  const fixtureRoot = options.fixtureRoot ?? path.join(root, ...fixtureRelative.split('/'));
  const workflowPath = options.workflowPath ?? path.join(root, ...workflowRelative.split('/'));
  const templatePath = options.templatePath ?? path.join(root, ...templateRelative.split('/'));
  const workflowsRoot = options.workflowsRoot ?? path.join(root, '.github', 'workflows');
  const templateBytes = readBounded(templatePath);
  const workflowBytes = readBounded(workflowPath);
  if (
    templateBytes.includes(0x0d) ||
    workflowBytes.includes(0x0d) ||
    sha256(templateBytes) !== templateSha256 ||
    count(templateBytes.toString('utf8'), '__ACTION_SOURCE_SHA__') !== 7 ||
    count(templateBytes.toString('utf8'), '__PAYLOAD_SHA256__') !== 3
  ) {
    fail('template-identity');
  }
  const rendered = templateBytes
    .toString('utf8')
    .replaceAll('__ACTION_SOURCE_SHA__', actionSourceSha)
    .replaceAll('__PAYLOAD_SHA256__', payloadSha256);
  if (
    workflowBytes.toString('utf8') !== rendered ||
    sha256(workflowBytes) !== renderedWorkflowSha256 ||
    extractPreflight(workflowBytes) !== extractPreflight(templateBytes)
  ) {
    fail('rendered-workflow');
  }
  parseWorkflow(workflowPath);
  const documents = validateFixtureContracts(fixtureRoot);
  const receiptPath = path.join(fixtureRoot, 'trusted-proof-payload-receipt.json');
  const receipt = verifyReceipt({ receiptPath, sourceRoot: root });
  const receiptBytes = documents.get('trusted-proof-payload-receipt.json').bytes;
  if (
    sha256(receiptBytes) !== receiptJsonSha256 ||
    sha256(Buffer.concat([Buffer.from('APR_R4_E2P_RECEIPT '), receiptBytes])) !==
      receiptLineSha256 ||
    receipt.source_commit !== actionSourceSha ||
    receipt.source_tree !== '9e1f7fbd9d0924331aeef4defe12ec7b47021742' ||
    receipt.payload_sha256 !== payloadSha256 ||
    receipt.workflow_topology_sha256 !== templateSha256 ||
    receipt.result !== 'passed'
  ) {
    fail('receipt-identity');
  }
  const provenance = documents.get('receipt-provenance.json').value;
  exactKeys(
    provenance,
    [
      'kind',
      'issue',
      'comment_id',
      'comment_url',
      'merge_sha',
      'source_tree',
      'receipt_line_sha256',
      'receipt_json_sha256',
      'workflow_template_sha256',
      'rendered_workflow_sha256',
      'action_source_sha',
      'payload_sha256',
      'result',
    ],
    'receipt-provenance-shape',
  );
  if (
    provenance.comment_id !== 5380622921 ||
    provenance.merge_sha !== actionSourceSha ||
    provenance.receipt_line_sha256 !== receiptLineSha256 ||
    provenance.receipt_json_sha256 !== receiptJsonSha256 ||
    provenance.workflow_template_sha256 !== templateSha256 ||
    provenance.rendered_workflow_sha256 !== renderedWorkflowSha256 ||
    provenance.payload_sha256 !== payloadSha256 ||
    provenance.result !== 'passed'
  ) {
    fail('receipt-provenance-values');
  }
  validateRepositorySecretRoutes(workflowsRoot);
  return true;
}

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(import.meta.filename)) {
  try {
    checkR4TrustedProof();
    process.stdout.write('APR_R4_E3_POLICY_OK\n');
  } catch (error) {
    process.stderr.write(
      `${error instanceof Error ? error.message : 'APR_R4_E3_POLICY_INVALID'}\n`,
    );
    process.exitCode = 1;
  }
}
