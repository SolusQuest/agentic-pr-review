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
    'd0971e9458d6383c7b77a23a083db00c50cff929c1d7efe0671d8bb560ddfa6e',
  ],
  ['cleanup-contract.json', '507a5bc42524cff403f465485bc76beb0bb87f1cbe73cc9fdb3d25e84634a61b'],
  ['fixture-pr-contract.json', '04300f82f29b8f14aec1d9458f44d09680abd90faceef08aafad50b48ced3912'],
  ['receipt-provenance.json', 'e0e83ca4c461197c4f4cd3ed37cd5fdafb137398c188068025179664943665b2'],
  [
    'schemas/host-restricted-evidence.schema.json',
    '9bf41bbfd780b868ffd098b2417fac83757026c8bca5bf7710eed2d12dd583c7',
  ],
  [
    'schemas/public-safe-evidence.schema.json',
    '8b1cf642f8a44354f177fcf7a6c6b37a61e10e71733f5e2d284450a42a5624f7',
  ],
  [
    'templates/host-restricted-evidence.json',
    '4b2404a4fcb8281915fc6045aba3fe1ae33d12ef3e0342179b2c1baaee92e21e',
  ],
  [
    'templates/public-safe-evidence.json',
    '4e99054dfda705c366de6a816db47535cfab9f5fff1fad6b8deace0a5daaf890',
  ],
  [
    'traces/normal-two-run.json',
    'ba3c06f2cbd169b474465c4382b3402d04e1aef6b02752a5c5a40ebc2bfee060',
  ],
  ['traces/stale-head.json', '3b73595e98ff08c10c5f43bec36a31d7d23c22e5bc93d15d8f6d31b0a78f77ae'],
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
    cleanup.public_projection_gate !== 'exact-empty-final-state-and-complete-resource-readback' ||
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
      normalTrace.run_one.barrier_ready_at <= normalTrace.run_two.created_at &&
      normalTrace.run_two.created_at <= normalTrace.observation.observed_at &&
      normalTrace.observation.observed_at < normalTrace.run_one.completed_at &&
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
    staleTrace.advanced_parent_sha !== staleTrace.admitted_head_sha ||
    staleTrace.changed_paths?.length !== 1 ||
    staleTrace.changed_paths[0] !== 'proof/apr178-path-canary.txt' ||
    staleTrace.advanced_content_sha256 !== staleCanarySha256 ||
    staleTrace.old_authorization_matches !== false ||
    staleTrace.privileged_job_allocated !== false ||
    staleTrace.state_mutated !== false ||
    staleTrace.follow_on_status !== 'completed-inert'
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
  for (const name of workflowFiles) {
    const { source, value } = parseWorkflow(path.join(workflowsRoot, name));
    allSource += `${source}\n`;
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
