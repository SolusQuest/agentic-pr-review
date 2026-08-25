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
const stagedTemplateRelative =
  'runtime/tests/fixtures/action-host/trusted-proof-payload/workflow/r4-trusted-proof-v2.yml.template';
const stagedPreflightRelative =
  'runtime/tests/fixtures/action-host/trusted-proof-payload/workflow/preflight-contract-v2.json';
const stagedPreparationRelative =
  'runtime/tests/fixtures/action-host/trusted-proof-payload/preparation-contract-v2.json';
const stagedPreparationScriptRelative = 'runtime/scripts/prepare-r4-trusted-proof-payload-v2.sh';
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
    '87d0050fa7e2171b38eba4035e8c176fcf14299a0152b54efcb5ddde06d7e73e',
  ],
  ['cleanup-contract.json', '5d7a26d40d9e41d3d195e8dfb8496703fd40084c1fd687f22306c51575c4cdce'],
  ['fixture-pr-contract.json', '347a2cdf30bd4a28e15f74d939d6c61519d6694022be03c4d5c3cb1c05224efc'],
  ['receipt-provenance.json', 'e0e83ca4c461197c4f4cd3ed37cd5fdafb137398c188068025179664943665b2'],
  [
    'schemas/host-restricted-evidence.schema.json',
    '354faff22e2976efa450a9e6a8dc8b6b2ec8bdc07c827026af8775e5540352f5',
  ],
  [
    'schemas/public-safe-evidence.schema.json',
    'cfd733c71312ddd9e4d6539f512eef2c3c631b6d785e8c610d16f90aeac3f47d',
  ],
  [
    'templates/host-restricted-evidence.json',
    'ab71e1e7f8d201bb7af32a3a628391db9058a2402bc87d7b9f45930949050bbd',
  ],
  [
    'templates/public-safe-evidence.json',
    'dd768d3cde2ef180b2ec0f73b41ccf17484d53d089660ba59ed9bb3afb3bb4e3',
  ],
  [
    'traces/normal-two-run.json',
    '7e946753efd19a1483e29306966a8d9f783d4c8b3136ddc8af39c88a898be2a8',
  ],
  ['traces/stale-head.json', '8a2a34659c0c9593bf48132d7728c98340019b85d4bad20d8bf58579b58b7a40'],
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

function workflowExpressions(value) {
  const expressions = [];
  let offset = 0;
  while (offset < value.length) {
    const opening = value.indexOf('${{', offset);
    if (opening < 0) break;
    let cursor = opening + 3;
    let quote = null;
    let closed = false;
    while (cursor < value.length) {
      const character = value[cursor];
      if (quote !== null) {
        if (character === quote) {
          if (quote === "'" && value[cursor + 1] === "'") {
            cursor += 2;
            continue;
          }
          quote = null;
        }
        cursor += 1;
        continue;
      }
      if (character === "'" || character === '"') {
        quote = character;
        cursor += 1;
        continue;
      }
      if (character === '}' && value[cursor + 1] === '}') {
        expressions.push(value.slice(opening + 3, cursor));
        offset = cursor + 2;
        closed = true;
        break;
      }
      cursor += 1;
    }
    if (!closed) {
      expressions.push(value.slice(opening + 3));
      break;
    }
  }
  return expressions;
}

function workflowExpressionTokens(source) {
  const tokens = [];
  let offset = 0;
  while (offset < source.length) {
    const character = source[offset];
    if (/\s/u.test(character)) {
      offset += 1;
      continue;
    }
    if (/[A-Za-z_]/u.test(character)) {
      const start = offset;
      offset += 1;
      while (offset < source.length && /[A-Za-z0-9_-]/u.test(source[offset])) offset += 1;
      tokens.push({ kind: 'identifier', value: source.slice(start, offset).toLowerCase() });
      continue;
    }
    if (character === "'" || character === '"') {
      const quote = character;
      let value = '';
      offset += 1;
      while (offset < source.length) {
        const current = source[offset];
        if (current === quote) {
          if (quote === "'" && source[offset + 1] === "'") {
            value += "'";
            offset += 2;
            continue;
          }
          offset += 1;
          break;
        }
        if (quote === '"' && current === '\\' && offset + 1 < source.length) {
          value += source[offset + 1];
          offset += 2;
          continue;
        }
        value += current;
        offset += 1;
      }
      tokens.push({ kind: 'string', value: value.toLowerCase() });
      continue;
    }
    if ('.[](),'.includes(character)) {
      tokens.push({ kind: character });
      offset += 1;
      continue;
    }
    tokens.push({ kind: 'operator', value: character });
    offset += 1;
  }
  return tokens;
}

function unwrapExpression(node) {
  let current = node;
  while (
    current?.kind === 'group' ||
    (current?.kind === 'sequence' && current.nodes.length === 1)
  ) {
    current = current.kind === 'group' ? current.expression : current.nodes[0];
  }
  return current;
}

function staticString(node) {
  const current = unwrapExpression(node);
  return current?.kind === 'string' ? current.value : null;
}

function parseWorkflowExpression(source) {
  const tokens = workflowExpressionTokens(source);
  let offset = 0;

  function parseSequence(stops = new Set()) {
    const nodes = [];
    while (offset < tokens.length && !stops.has(tokens[offset].kind)) {
      const before = offset;
      const node = parsePostfix();
      if (node !== null) nodes.push(node);
      if (offset === before) offset += 1;
    }
    return { kind: 'sequence', nodes };
  }

  function parsePrimary() {
    const token = tokens[offset];
    if (token?.kind === 'identifier') {
      offset += 1;
      return { kind: 'identifier', value: token.value };
    }
    if (token?.kind === 'string') {
      offset += 1;
      return { kind: 'string', value: token.value };
    }
    if (token?.kind === '(') {
      offset += 1;
      const expression = parseSequence(new Set([')']));
      if (tokens[offset]?.kind === ')') offset += 1;
      return { kind: 'group', expression };
    }
    return null;
  }

  function parsePostfix() {
    let node = parsePrimary();
    if (node === null) return null;
    while (offset < tokens.length) {
      if (tokens[offset].kind === '.') {
        offset += 1;
        const property = tokens[offset]?.kind === 'identifier' ? tokens[offset].value : null;
        if (property !== null) offset += 1;
        node = { kind: 'access', base: node, property, key: null };
        continue;
      }
      if (tokens[offset].kind === '[') {
        offset += 1;
        const key = parseSequence(new Set([']']));
        if (tokens[offset]?.kind === ']') offset += 1;
        node = { kind: 'access', base: node, property: staticString(key), key };
        continue;
      }
      if (tokens[offset].kind === '(') {
        offset += 1;
        const argumentsList = [];
        while (offset < tokens.length && tokens[offset].kind !== ')') {
          argumentsList.push(parseSequence(new Set([',', ')'])));
          if (tokens[offset]?.kind === ',') offset += 1;
        }
        if (tokens[offset]?.kind === ')') offset += 1;
        node = { kind: 'call', callee: node, arguments: argumentsList };
        continue;
      }
      break;
    }
    return node;
  }

  return parseSequence();
}

function isIdentifier(node, value) {
  const current = unwrapExpression(node);
  return current?.kind === 'identifier' && current.value === value;
}

function containsBareGithubRoot(node) {
  const current = unwrapExpression(node);
  if (current === undefined || current === null) return false;
  if (current.kind === 'identifier') return current.value === 'github';
  if (current.kind === 'access') return containsBareGithubRoot(current.key);
  if (current.kind === 'call') {
    return current.arguments.some((argument) => containsBareGithubRoot(argument));
  }
  if (current.kind === 'sequence') {
    return current.nodes.some((item) => containsBareGithubRoot(item));
  }
  return false;
}

function containsCredentialNode(node) {
  const current = unwrapExpression(node);
  if (current === undefined || current === null) return false;
  if (current.kind === 'identifier') return current.value === 'secrets';
  if (current.kind === 'access') {
    if (
      containsBareGithubRoot(current.base) &&
      (current.property === null || current.property === 'token')
    ) {
      return true;
    }
    return containsCredentialNode(current.base) || containsCredentialNode(current.key);
  }
  if (current.kind === 'call') {
    if (
      isIdentifier(current.callee, 'tojson') &&
      current.arguments.some((argument) => containsBareGithubRoot(argument))
    ) {
      return true;
    }
    return (
      containsCredentialNode(current.callee) ||
      current.arguments.some((argument) => containsCredentialNode(argument))
    );
  }
  if (current.kind === 'sequence') {
    return current.nodes.some((item) => containsCredentialNode(item));
  }
  return false;
}

function collectCredentialExpressions(value, result) {
  if (typeof value === 'string') {
    if (
      workflowExpressions(value).some((expression) =>
        containsCredentialNode(parseWorkflowExpression(expression)),
      )
    ) {
      result.push(value);
    }
    return;
  }
  if (Array.isArray(value)) {
    for (const item of value) collectCredentialExpressions(item, result);
    return;
  }
  if (value !== null && typeof value === 'object') {
    for (const item of Object.values(value)) collectCredentialExpressions(item, result);
  }
}

const reviewedWriteActionRoutes = new Map([
  [
    'r4-trusted-proof.yml\0workflow-run-review',
    [
      'checkout-control-root\0actions/checkout@d23441a48e516b6c34aea4fa41551a30e30af803',
      'checkout-payload-source\0actions/checkout@d23441a48e516b6c34aea4fa41551a30e30af803',
      'setup-node\0actions/setup-node@249970729cb0ef3589644e2896645e5dc5ba9c38',
      'setup-dotnet\0actions/setup-dotnet@26b0ec14cb23fa6904739307f278c14f94c95bf1',
      'review\0SolusQuest/agentic-pr-review/.github/actions/agentic-pr-review@5b5769753653bb3fd3e68cf8b7bb88a1bd350613',
    ],
  ],
  [
    'r4-trusted-proof.yml\0workflow-dispatch-review',
    [
      'checkout-control-root\0actions/checkout@d23441a48e516b6c34aea4fa41551a30e30af803',
      'checkout-payload-source\0actions/checkout@d23441a48e516b6c34aea4fa41551a30e30af803',
      'setup-node\0actions/setup-node@249970729cb0ef3589644e2896645e5dc5ba9c38',
      'setup-dotnet\0actions/setup-dotnet@26b0ec14cb23fa6904739307f278c14f94c95bf1',
      'review\0SolusQuest/agentic-pr-review/.github/actions/agentic-pr-review@5b5769753653bb3fd3e68cf8b7bb88a1bd350613',
    ],
  ],
]);

const proofMutationPermissions = new Set(['actions', 'contents', 'issues', 'pull-requests']);
const reviewedWritePermissionIdentity = JSON.stringify([
  ['actions', 'write'],
  ['contents', 'read'],
  ['pull-requests', 'write'],
]);

function permissionIdentity(permissions) {
  if (permissions === null || Array.isArray(permissions) || typeof permissions !== 'object') {
    return null;
  }
  return JSON.stringify(
    Object.entries(permissions).sort(([left], [right]) => left.localeCompare(right)),
  );
}

function proofTokenAuthority(permissions) {
  if (permissions === undefined) return 'unbounded';
  if (permissions === 'write-all') return 'write';
  if (permissions === 'read-all') return 'read';
  if (permissions === null || Array.isArray(permissions) || typeof permissions !== 'object') {
    return 'unbounded';
  }
  let authority = 'none';
  for (const [permission, access] of Object.entries(permissions)) {
    if (access !== 'none' && access !== 'read' && access !== 'write') return 'unbounded';
    if (!proofMutationPermissions.has(permission)) continue;
    if (access === 'write') return 'write';
    if (access === 'read') authority = 'read';
  }
  return authority;
}

function jobActionInvocations(job) {
  const invocations = [];
  if (Object.hasOwn(job, 'uses')) {
    invocations.push(`<job>\0${typeof job.uses === 'string' ? job.uses : '<dynamic>'}`);
  }
  if (Array.isArray(job.steps)) {
    for (const [index, step] of job.steps.entries()) {
      if (step === null || Array.isArray(step) || typeof step !== 'object') continue;
      if (!Object.hasOwn(step, 'uses')) continue;
      const id = typeof step.id === 'string' ? step.id : `<step:${index}>`;
      const uses = typeof step.uses === 'string' ? step.uses : '<dynamic>';
      invocations.push(`${id}\0${uses}`);
    }
  }
  return invocations;
}

function validateActionTokenRoutes(name, workflow) {
  if (workflow.jobs === null || Array.isArray(workflow.jobs) || typeof workflow.jobs !== 'object') {
    fail('repository-action-token-routes');
  }
  for (const [jobId, job] of Object.entries(workflow.jobs)) {
    if (job === null || Array.isArray(job) || typeof job !== 'object') {
      fail('repository-action-token-routes');
    }
    const permissions = Object.hasOwn(job, 'permissions') ? job.permissions : workflow.permissions;
    const authority = proofTokenAuthority(permissions);
    const invocations = jobActionInvocations(job);
    if (invocations.length === 0 || authority === 'none' || authority === 'read') continue;
    const expected = reviewedWriteActionRoutes.get(`${name}\0${jobId}`);
    if (
      expected === undefined ||
      permissionIdentity(permissions) !== reviewedWritePermissionIdentity ||
      JSON.stringify(invocations) !== JSON.stringify(expected)
    ) {
      fail('repository-action-token-routes');
    }
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
    authorization.authorization_variable?.manifest_kind !==
      'apr-r4-e2p-authorization-manifest-v1' ||
    authorization.authorization_variable?.normal_and_stale_phases_independently_observed !== true ||
    authorization.environment?.name !== 'r4-trusted-proof' ||
    authorization.environment?.must_preexist !== true ||
    authorization.environment?.deployment_branch !== 'main-only' ||
    authorization.environment?.required_reviewer_rule_enabled !== true ||
    JSON.stringify(authorization.environment?.required_reviewers) !==
      JSON.stringify([{ type: 'User', id: '16307884' }]) ||
    authorization.environment?.required_maintainer_approvals_minimum !== 1 ||
    authorization.environment?.prevent_self_review !== false ||
    authorization.environment?.administrator_bypass !== false ||
    authorization.environment?.per_protected_job_approval_required !== true ||
    authorization.environment?.phase_order !==
      'exact-snapshot-readback < reviewer-approval <= protected-job-start' ||
    authorization.environment?.normal_and_stale_phases_independently_observed !== true ||
    authorization.environment?.required_evidence?.includes('required_reviewer_rule_enabled') !==
      true ||
    authorization.environment?.required_evidence?.includes('exact_required_reviewer_set') !==
      true ||
    authorization.environment?.required_evidence?.includes(
      'required_maintainer_approvals_minimum',
    ) !== true ||
    authorization.environment?.required_evidence?.includes(
      'bootstrap_required_reviewer_approval',
    ) !== true ||
    authorization.environment?.required_evidence?.includes(
      'continuation_required_reviewer_approval',
    ) !== true ||
    authorization.environment?.required_evidence?.includes('stale_required_reviewer_approval') !==
      true ||
    authorization.canary_matrix_requires_exact_authorized_credential_per_sink !== true ||
    authorization.host_restricted_destination_requires_kind_and_identity_sha256 !== true ||
    authorization.live_mutation_owner !== 'issue-181'
  ) {
    fail('authorization-environment-contract');
  }
  const cleanup = documents.get('cleanup-contract.json').value;
  if (
    cleanup.pre_state?.required !== 'empty' ||
    cleanup.pre_state?.complete_pagination !== true ||
    cleanup.pre_state?.required_scopes?.length !== 3 ||
    cleanup.pre_state?.scope_digests_must_be_distinct !== true ||
    cleanup.operation_state?.reject_unowned_addition !== true ||
    cleanup.pre_state?.repository_root_family !== 'locator_root' ||
    cleanup.pre_state?.scoped_families?.length !== 9 ||
    cleanup.pre_state?.scoped_families?.includes('locator_root') !== false ||
    cleanup.operation_state?.creation_phases?.includes('stale-setup') !== true ||
    cleanup.operation_state?.physical_record_exact_fields?.includes('scope') !== true ||
    cleanup.operation_state?.physical_record_exact_fields?.includes('scope_digest') !== true ||
    cleanup.operation_state?.physical_record_exact_fields?.includes('archive_sha256') !== true ||
    cleanup.operation_state?.physical_record_exact_fields?.includes('encrypted_object_sha256') !==
      true ||
    cleanup.operation_state?.physical_record_exact_fields?.includes('expires_at_unix_seconds') !==
      true ||
    cleanup.operation_state?.physical_record_exact_fields?.includes('size') !== true ||
    cleanup.operation_state?.physical_record_exact_fields?.includes('decoded_record') !== true ||
    cleanup.operation_state?.physical_record_exact_fields?.includes('terminal_disposition') !==
      true ||
    cleanup.operation_state?.physical_record_exact_fields?.includes('terminal_phase') !== true ||
    cleanup.operation_state?.physical_record_exact_fields?.includes('terminal_at_unix_seconds') !==
      true ||
    cleanup.operation_state?.archive_and_encrypted_digests_independent !== true ||
    cleanup.operation_state?.complete_created_physical_artifact_count !== 35 ||
    cleanup.operation_state?.transient_record_contract?.opaque_write_anchors !== 6 ||
    cleanup.operation_state?.transient_record_contract?.p5_anchor_cleanup_records !== 6 ||
    cleanup.operation_state?.transient_record_contract?.p5_completed_record_cleanup_records !== 6 ||
    cleanup.operation_state?.transient_record_contract?.predecessor_copy_candidates !== 1 ||
    cleanup.operation_state?.transient_record_contract?.s6_internal_cleanup_records !== 2 ||
    cleanup.operation_state?.transient_record_contract?.s6_final_cleanup_records !== 1 ||
    cleanup.operation_state?.canonical_scoped_envelope_required !== true ||
    cleanup.operation_state?.cleanup_inventory_binding !==
      'exact-active-physical-inventory-before-cleanup-record-creation' ||
    cleanup.operation_state?.scoped_header_exact_fields?.includes('epoch') !== true ||
    cleanup.operation_state?.scoped_header_exact_fields?.includes('session_id') !== true ||
    cleanup.operation_state?.decoded_record_contract !==
      'exact-class-specific-production-fields-with-distinct-session-digests-and-content-derived-cleanup-identity' ||
    cleanup.operation_state?.normal_lineage_head_rule !==
      'single-initialized-head-reused-across-accepted-generations-unless-reset-or-expiry' ||
    JSON.stringify(cleanup.operation_state?.successful_publication_recovery_subtypes) !==
      JSON.stringify(['initial_intent', 'sticky_readback', 'acceptance_recovery']) ||
    JSON.stringify(cleanup.operation_state?.terminal_disposition_partition) !==
      JSON.stringify(['internally-reconciled-deleted', 'e4-deleted', 'cleanup-self-deleted']) ||
    cleanup.operation_state?.artifact_id_contract !==
      'canonical-positive-decimal-javascript-safe-github-artifact-id' ||
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
    normalTrace.concurrency_group !== 'agentic-pr-review-r4-42-pr-1001' ||
    normalTrace.run_one?.pr_number !== '1001' ||
    normalTrace.run_two?.pr_number !== '1001' ||
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
    staleTrace.authorized_stale_run?.pr_number !== '1002' ||
    staleTrace.unauthorized_follow_on_run?.pr_number !== '1002' ||
    staleTrace.authorized_stale_run?.concurrency_group !== 'agentic-pr-review-r4-42-pr-1002' ||
    staleTrace.unauthorized_follow_on_run?.concurrency_group !==
      'agentic-pr-review-r4-42-pr-1002' ||
    staleTrace.authorized_stale_run?.value_free_signal_count !== 1 ||
    staleTrace.authorized_stale_run?.host_exact_head_result !== 'stale-head-rejected' ||
    staleTrace.unauthorized_follow_on_run?.old_authorization_matches !== false ||
    staleTrace.unauthorized_follow_on_run?.privileged_job_allocated !== false ||
    staleTrace.unauthorized_follow_on_run?.pending_observation?.job_allocated !== false ||
    !(
      staleTrace.unauthorized_follow_on_run?.created_at <=
        staleTrace.unauthorized_follow_on_run?.pending_observation?.observed_at &&
      staleTrace.unauthorized_follow_on_run?.pending_observation?.observed_at <
        staleTrace.authorized_stale_run?.completed_at &&
      staleTrace.authorized_stale_run?.completed_at <
        staleTrace.unauthorized_follow_on_run?.workflow_started_at &&
      staleTrace.unauthorized_follow_on_run?.workflow_started_at <=
        staleTrace.unauthorized_follow_on_run?.completed_at
    ) ||
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
    collectCredentialExpressions(value, expressions);
    for (const expression of expressions) observed.push(`${name}\0${expression}`);
    validateActionTokenRoutes(name, value);
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
  const stagedTemplate = readBounded(path.join(root, ...stagedTemplateRelative.split('/')));
  const stagedSource = stagedTemplate.toString('utf8');
  const stagedPreflight = parseJsonDocument(
    path.join(root, ...stagedPreflightRelative.split('/')),
    16_384,
  ).value;
  const stagedInline = extractPreflight(stagedTemplate);
  if (
    stagedTemplate.includes(0x0d) ||
    count(stagedSource, '__ACTION_SOURCE_SHA__') !== 5 ||
    count(stagedSource, '__PAYLOAD_SOURCE_SHA__') !== 5 ||
    count(stagedSource, '__PAYLOAD_SHA256__') !== 3 ||
    !stagedInline.includes("'apr-r4-e2p-authorization-manifest-v2'") ||
    !stagedInline.includes('pull.merged_at !== null') ||
    !stagedInline.includes("pull.base?.ref !== 'main'") ||
    !stagedInline.includes('pull.base?.sha !== workflowSha')
  ) {
    fail('staged-v2-preflight');
  }
  exactKeys(
    stagedPreflight,
    [
      'kind',
      'origin',
      'route',
      'accept',
      'api_version',
      'authentication',
      'redirect',
      'timeout_ms',
      'maximum_response_bytes',
      'branch_pattern',
      'base_ref',
      'base_sha',
      'payload_source_identity',
      'workflow_run',
      'authorization_variable',
      'authorization_manifest_kind',
      'authorization_manifest_order',
      'outputs',
    ],
    'staged-v2-preflight-contract-shape',
  );
  if (
    stagedPreflight.kind !== 'apr-r4-e2p-public-pr-preflight-v2' ||
    stagedPreflight.authentication !== 'none' ||
    stagedPreflight.redirect !== 'error' ||
    stagedPreflight.base_ref !== 'main' ||
    stagedPreflight.base_sha !== 'exact-workflow-sha' ||
    stagedPreflight.payload_source_identity !== 'exact-compiled-payload-source-commit' ||
    stagedPreflight.authorization_manifest_kind !== 'apr-r4-e2p-authorization-manifest-v2'
  ) {
    fail('staged-v2-preflight-contract');
  }
  const stagedPreparation = parseJsonDocument(
    path.join(root, ...stagedPreparationRelative.split('/')),
    16_384,
  ).value;
  const stagedPreparationScript = readBounded(
    path.join(root, ...stagedPreparationScriptRelative.split('/')),
  ).toString('utf8');
  if (
    stagedPreparation.kind !== 'apr-r4-e2p-preparation-contract-v2' ||
    JSON.stringify(stagedPreparation.outputs) !==
      JSON.stringify([
        'prepared_root',
        'prepared_executable',
        'prepared_payload_sha256',
        'action_source_sha',
        'payload_build_discriminator',
      ]) ||
    stagedPreparation.action_source_sha !== actionSourceSha ||
    stagedPreparation.payload_build_discriminator !== 'r4-w2' ||
    count(stagedPreparationScript, 'payload_source_sha=') !== 0 ||
    !stagedPreparationScript.includes('-p:PayloadSourceCommit=$source_commit') ||
    !stagedPreparationScript.includes('-p:PayloadSourceTree=$source_tree') ||
    !stagedPreparationScript.includes('"$(wc -l < "$output_lines")" -eq 5')
  ) {
    fail('staged-v2-preparation-contract');
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
