import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import Ajv from 'ajv';
import { parseDocument } from 'yaml';
import { extractPreflight } from './check-r4-e2p-preflight.mjs';
import { verifyReceipt } from './check-r4-e2p-receipt.mjs';
import { verifyReceiptV2 } from './check-r4-e2p-receipt-v2.mjs';
import { generateCleanupPlan, projectTrustedProofEvidence } from './r4-trusted-proof-contract.mjs';

const repositoryRoot = path.resolve(import.meta.dirname, '..');
const fixtureRelative = 'runtime/tests/fixtures/action-host/trusted-proof';
const templateRelative =
  'runtime/tests/fixtures/action-host/trusted-proof-payload/workflow/r4-trusted-proof-v2.yml.template';
const workflowRelative = '.github/workflows/r4-trusted-proof.yml';
const stagedTemplateRelative =
  'runtime/tests/fixtures/action-host/trusted-proof-payload/workflow/r4-trusted-proof-v2.yml.template';
const stagedPreflightRelative =
  'runtime/tests/fixtures/action-host/trusted-proof-payload/workflow/preflight-contract-v2.json';
const stagedPreparationRelative =
  'runtime/tests/fixtures/action-host/trusted-proof-payload/preparation-contract-v2.json';
const stagedPreparationScriptRelative = 'runtime/scripts/prepare-r4-trusted-proof-payload-v2.sh';
const actionSourceSha = '5b5769753653bb3fd3e68cf8b7bb88a1bd350613';
const payloadSourceSha = 'edc594c29a8a6b5fdacfab48643bf221277af200';
const payloadSourceTree = '8bf475a02a4f7307cdce2bbc29dd2bc6c6cf9089';
const payloadSha256 = 'b6405d21987a549540b071215f215cf15339729cb3905ad3294c88bc2edf8c0e';
const templateSha256 = '46ff02fc0e107bdff5d4d4fbe185d8a4f97b8cb8059b99485a285c8d11a45768';
const renderedWorkflowSha256 = '1dcf42e6c3890614d13ef1a0e6f98ca35e0029c2c78e85d4afdd4f32e29aebd9';
const receiptLineSha256 = '346cf753a0657ba4e25c5271df21afb1a95d2574c3ca8eb2a1f2e772ec776242';
const receiptJsonSha256 = '3556512b430867b41086938f55b6553f5f289fae3a1bb3a62d5755a01f9551e1';
const normalCanarySha256 = 'bc58613e9f389b973f1e44de64021a7acc748fafd857b7fa99466493670db446';
const staleCanarySha256 = '20580dc6193f607a3a1d1b6949013796f8a8c9714d3e5586e4e509a473f1382a';
const fixtureInventory = [
  'authorization-environment-contract.json',
  'authorizations/cleanup.json',
  'authorizations/execution.json',
  'authorizations/setup.json',
  'cleanup-contract.json',
  'cleanup-plan.json',
  'expected-success-role-contract.json',
  'fixture-pr-contract.json',
  'historical/v1/receipt-provenance.json',
  'historical/v1/trusted-proof-payload-receipt.json',
  'receipt-provenance-v2.json',
  'schemas/host-restricted-evidence.schema.json',
  'schemas/private-package-manifest.schema.json',
  'schemas/public-safe-evidence.schema.json',
  'source-map.json',
  'templates/host-restricted-evidence.json',
  'templates/public-safe-evidence.json',
  'traces/normal-two-run.json',
  'traces/stale-head.json',
  'trusted-proof-payload-receipt-v2.json',
];
const fixtureDigests = new Map([
  [
    'authorization-environment-contract.json',
    '0eabf9d8f706ea54ac28e901d3810839ed1dd70ab98688958db85d535c8bceb4',
  ],
  [
    'authorizations/cleanup.json',
    '541f8015e85dc88e9d8e0b58a0aff80687313f2df54cd03540e7d8289c0d82bd',
  ],
  [
    'authorizations/execution.json',
    '07bb9b554a95407c9f8793afce85731168702c0f21a212550b14df569ac08f42',
  ],
  ['authorizations/setup.json', 'a7cb429e66a51ebc6006929c16b07216efc15620bd0b9b509b599c668db5ac3f'],
  ['cleanup-contract.json', '6322a614c3118aac9e0684b00b371e1440a5046a8a0fef19df1132759bfea969'],
  ['cleanup-plan.json', '011c681b6b5105c46440d80dd67017a71ea13f01003fc499997b31f4d50b107e'],
  [
    'expected-success-role-contract.json',
    '8d601c863d184f8f8cf3cdae5e1b447eb9fa7aea25fd94d00390a3ac786a92ba',
  ],
  ['fixture-pr-contract.json', '347a2cdf30bd4a28e15f74d939d6c61519d6694022be03c4d5c3cb1c05224efc'],
  [
    'historical/v1/receipt-provenance.json',
    'dbcdf90d09de0d65e8dc6129e8c847fba23c4b1fef0f5379ace40b25063ff80b',
  ],
  [
    'historical/v1/trusted-proof-payload-receipt.json',
    '9b95a87e5f40d7b506e25426e3905aaaf0510ad28d79c8a7ca3737a3952a7b34',
  ],
  [
    'receipt-provenance-v2.json',
    '4dbb79b76dbfe41d8a7e8402e1b58b49af3e74620d75472b868cb07623016bc2',
  ],
  [
    'schemas/host-restricted-evidence.schema.json',
    '7d0ca7869f1886a290735376501c936808b3ee0bdf6292ba96b5bb9802f0b804',
  ],
  [
    'schemas/public-safe-evidence.schema.json',
    'fab487f9274857335cd976414f070957c812d528f69841caa52c8ab31e836594',
  ],
  [
    'schemas/private-package-manifest.schema.json',
    '01d1ede4e23398a88f5080fca175157363c8cf639aed8cb61185d1e41d878b8f',
  ],
  ['source-map.json', '90d3c02ae36f4472a8d8a15b49481c5c6eb4162f0da827a1efdd32ba1bc02d47'],
  [
    'templates/host-restricted-evidence.json',
    '270e813da23cd540d994c9255c9da1de6842bccbb2a2c8ef1c6dda66fe2aa382',
  ],
  [
    'templates/public-safe-evidence.json',
    '1e92721e6d49168f114a9313cb369e6c562c6ae7ac322c5ecb86929a08e32956',
  ],
  [
    'traces/normal-two-run.json',
    '802c3c3392455babd7a54236eb1fca531c40fa81e5ad695a1ed519343c12c28c',
  ],
  ['traces/stale-head.json', '7f8b47b419df4a2a134a703e40bc8b966ba7e15fa5d789b734a389f61b51d539'],
  [
    'trusted-proof-payload-receipt-v2.json',
    '3556512b430867b41086938f55b6553f5f289fae3a1bb3a62d5755a01f9551e1',
  ],
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
    fixture.only_changed_path !== 'proof/apr178-path-canary.txt' ||
    fixture.normal.terminator_hex !== '0a' ||
    fixture.stale.advanced_terminator_hex !== '0a' ||
    sha256(normalBytes) !== normalCanarySha256 ||
    sha256(staleBytes) !== staleCanarySha256
  ) {
    fail('fixture-byte-identity');
  }

  const authorization = documents.get('authorization-environment-contract.json').value;
  if (
    authorization.checkpoints?.map((item) => item.phase).join(',') !== 'setup,execution,cleanup' ||
    authorization.environment?.prevent_self_review !== false ||
    authorization.environment?.administrator_bypass_source !== 'closed-ui-attestation' ||
    authorization.approval_transition?.run_attempt !== 1 ||
    authorization.approval_transition?.approved_at_available !== false ||
    authorization.concurrency?.api_version !== '2026-03-10' ||
    authorization.live_mutation_owner !== 'issue-181'
  ) {
    fail('authorization-environment-contract');
  }
  const cleanup = documents.get('cleanup-contract.json').value;
  if (
    cleanup.successful_inventory?.exact_product_anchor_count !== 7 ||
    cleanup.successful_inventory?.synthetic_inventory_is_authority !== false ||
    cleanup.observed_cleanup_inventory?.authenticated_operation_owned_extra !==
      'recovery-only-delete' ||
    cleanup.observed_cleanup_inventory?.ambiguous_or_cross_operation !==
      'non-deletable-maintainer-handoff' ||
    cleanup.recovery_only_public_projection !== false ||
    cleanup.execution_capability !== 'none'
  ) {
    fail('cleanup-contract');
  }

  const host = documents.get('templates/host-restricted-evidence.json').value;
  const expectedPublic = documents.get('templates/public-safe-evidence.json').value;
  if (
    JSON.stringify(documents.get('source-map.json').value) !== JSON.stringify(host.source_map) ||
    JSON.stringify(documents.get('authorizations/setup.json').value) !==
      JSON.stringify(host.authorizations.setup) ||
    JSON.stringify(documents.get('authorizations/execution.json').value) !==
      JSON.stringify(host.authorizations.execution) ||
    JSON.stringify(documents.get('authorizations/cleanup.json').value) !==
      JSON.stringify(host.authorizations.cleanup)
  ) {
    fail('fixture-cross-binding');
  }
  const roleContract = documents.get('expected-success-role-contract.json').value;
  if (
    roleContract.exact_count !== 7 ||
    roleContract.synthetic_fixture_authority !== false ||
    JSON.stringify(roleContract.roles) !==
      JSON.stringify(host.inventories.expected_success.map((item) => item.role))
  ) {
    fail('success-role-contract');
  }
  const generated = generateCleanupPlan({
    operation_ids: host.identities.operation_ids,
    proof_control: host.proof_control,
    observed_cleanup: host.inventories.observed_cleanup,
    resources: host.cleanup.resources,
  });
  if (
    generated.digest !== host.cleanup.plan_sha256 ||
    generated.canonical !== documents.get('cleanup-plan.json').bytes.toString('utf8')
  ) {
    fail('cleanup-plan-contract');
  }
  if (JSON.stringify(host).includes('approved_at')) fail('unobservable-approval-time');

  const normalTrace = documents.get('traces/normal-two-run.json').value;
  const staleTrace = documents.get('traces/stale-head.json').value;
  if (
    normalTrace.api_version !== '2026-03-10' ||
    normalTrace.pagination_complete !== true ||
    JSON.stringify(normalTrace.ahead_of_run) !==
      JSON.stringify(host.concurrency.normal.ahead_of_run) ||
    staleTrace.api_version !== '2026-03-10' ||
    staleTrace.pagination_complete !== true ||
    staleTrace.proof_control.comments.length !== 4 ||
    staleTrace.follow_on_terminal_inert !== true ||
    JSON.stringify(staleTrace.ahead_of_run) !== JSON.stringify(host.concurrency.stale.ahead_of_run)
  ) {
    fail('trace-contract');
  }

  const ajv = new Ajv({ allErrors: true, strict: true });
  const validateHost = ajv.compile(
    documents.get('schemas/host-restricted-evidence.schema.json').value,
  );
  const validatePublic = ajv.compile(
    documents.get('schemas/public-safe-evidence.schema.json').value,
  );
  ajv.compile(documents.get('schemas/private-package-manifest.schema.json').value);
  if (!validateHost(host)) fail('host-template-schema');
  let projected;
  try {
    projected = projectTrustedProofEvidence(host);
  } catch {
    fail('host-template-contract');
  }
  if (!validatePublic(projected) || JSON.stringify(projected) !== JSON.stringify(expectedPublic)) {
    fail('public-template-contract');
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
    count(templateBytes.toString('utf8'), '__ACTION_SOURCE_SHA__') !== 5 ||
    count(templateBytes.toString('utf8'), '__PAYLOAD_SOURCE_SHA__') !== 5 ||
    count(templateBytes.toString('utf8'), '__PAYLOAD_SHA256__') !== 3
  ) {
    fail('template-identity');
  }
  const rendered = templateBytes
    .toString('utf8')
    .replaceAll('__ACTION_SOURCE_SHA__', actionSourceSha)
    .replaceAll('__PAYLOAD_SOURCE_SHA__', payloadSourceSha)
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
  const capturePlanSource = fs.readFileSync(
    path.join(root, 'runtime', 'tests', 'ActionHostTrustedProofCapture', 'CapturePlan.cs'),
    'utf8',
  );
  if (!capturePlanSource.includes(fixtureDigests.get('source-map.json'))) {
    fail('capture-plan-source-map-identity');
  }
  const oracleProject = fs.readFileSync(
    path.join(
      root,
      'runtime',
      'tests',
      'ActionHostTrustedProofEvidenceOracle',
      'AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceOracle.csproj',
    ),
    'utf8',
  );
  const oracleSource = fs.readFileSync(
    path.join(root, 'runtime', 'tests', 'ActionHostTrustedProofEvidenceOracle', 'Program.cs'),
    'utf8',
  );
  const oracleBuildProject = fs.readFileSync(
    path.join(
      root,
      'runtime',
      'tests',
      'ActionHostTrustedProofOracleBuild',
      'AgenticPrReview.Runtime.ActionHostTrustedProofOracleBuild.csproj',
    ),
    'utf8',
  );
  const oracleBuildSource = fs.readFileSync(
    path.join(root, 'runtime', 'tests', 'ActionHostTrustedProofOracleBuild', 'Program.cs'),
    'utf8',
  );
  const authorizedSnapshotSource = fs.readFileSync(
    path.join(
      root,
      'runtime',
      'tests',
      'ActionHostTrustedProofOracleBuild',
      'AuthorizedGitSnapshot.cs',
    ),
    'utf8',
  );
  const assemblerSource = fs.readFileSync(
    path.join(root, 'scripts', 'assemble-r4-trusted-proof-evidence.mjs'),
    'utf8',
  );
  const assemblerBoundarySource = fs.readFileSync(
    path.join(root, 'runtime', 'tests', 'ActionHostTrustedProofEvidenceAssembler', 'Program.cs'),
    'utf8',
  );
  const publicCorpusSource = fs.readFileSync(
    path.join(
      root,
      'runtime',
      'tests',
      'ActionHostTrustedProofEvidenceAssembler',
      'PublicSurfaceCorpusLease.cs',
    ),
    'utf8',
  );
  if (
    !oracleProject.includes('TrustedProofOracleSourceSha') ||
    !oracleProject.includes('TrustedProofOracleSourceTree') ||
    oracleProject.includes('TrustedProofOracleBuildReceiptArgument') ||
    oracleSource.includes('OracleBuildReceipt') ||
    !oracleBuildProject.includes('TrustedProofOracleBuildSourceRootArgument') ||
    !oracleBuildProject.includes('TrustedProofOracleBuildSourceTreeCommand') ||
    !oracleBuildSource.includes('--source-commit') ||
    !oracleBuildSource.includes('--source-tree') ||
    !oracleBuildSource.includes('--git-executable') ||
    !oracleBuildSource.includes('--dotnet-executable') ||
    !oracleBuildSource.includes('--build-receipt-output') ||
    !oracleBuildSource.includes('--snapshot-directory') ||
    !oracleBuildSource.includes('--intermediate-directory') ||
    !oracleBuildSource.includes('AuthorizedGitSnapshot.Materialize') ||
    !oracleBuildSource.includes('CreateFreshBuildDirectory') ||
    !oracleBuildSource.includes('snapshot.Validate()') ||
    !oracleBuildSource.includes('"--artifacts-path"') ||
    !oracleBuildSource.includes('"--configfile"') ||
    !oracleBuildSource.includes('"-p:ActionHostVerifierFrameworkReference=true"') ||
    !oracleBuildSource.includes('start.Environment.Clear()') ||
    !oracleBuildSource.includes('DOTNET_CLI_HOME') ||
    !oracleBuildSource.includes('USERPROFILE') ||
    !authorizedSnapshotSource.includes('"ls-tree", "-rz", "--full-tree"') ||
    !authorizedSnapshotSource.includes('"cat-file", "--batch"') ||
    !authorizedSnapshotSource.includes('FileMode.CreateNew') ||
    !authorizedSnapshotSource.includes('lease.Validate()') ||
    !authorizedSnapshotSource.includes('GIT_NO_REPLACE_OBJECTS') ||
    !authorizedSnapshotSource.includes('IncrementalHash.CreateHash(HashAlgorithmName.SHA1)') ||
    !authorizedSnapshotSource.includes('SetWindowsAccess') ||
    !oracleBuildSource.includes('"apr-r4-e3-independent-oracle-build-receipt-v2"') ||
    assemblerSource.includes('--assembly-input') ||
    !assemblerSource.includes('--source-bundle') ||
    !assemblerSource.includes('--post-cleanup-capture-manifest') ||
    !assemblerSource.includes('--cleanup-authorization-readback') ||
    !assemblerSource.includes('--cleanup-execution') ||
    !assemblerSource.includes('--oracle-assembly') ||
    !assemblerSource.includes('--production-assembly') ||
    !assemblerSource.includes('--public-scan-output') ||
    !assemblerSource.includes('--public-candidate-output') ||
    !assemblerSource.includes('--public-log-root') ||
    !assemblerBoundarySource.includes('AcquirePinnedFile') ||
    !assemblerBoundarySource.includes('lease.Validate()') ||
    !assemblerBoundarySource.includes('AssertCredentialCopiesAbsent') ||
    !assemblerBoundarySource.includes('Console.OpenStandardInput()') ||
    !assemblerBoundarySource.includes('ReadProtectedScanInput') ||
    !assemblerBoundarySource.includes('RedirectStandardInput = true') ||
    !assemblerBoundarySource.includes('process.StandardInput.Close()') ||
    !assemblerBoundarySource.includes('PublicSurfaceCorpusLease.Open') ||
    !assemblerBoundarySource.includes('publicCorpus.AssertAbsent') ||
    !assemblerBoundarySource.includes('publicCorpus.AssertExactDocumentAbsent') ||
    !assemblerBoundarySource.includes('publicCorpus.ValidateComplete') ||
    !assemblerBoundarySource.includes('WritePublicCreateNew') ||
    !assemblerBoundarySource.includes('CreatedEvidenceFileReceipt') ||
    publicCorpusSource.includes('ContainsProtectedFragment') ||
    !publicCorpusSource.includes('IndexOf(protectedValue)') ||
    !publicCorpusSource.includes('EnumerateDigests') ||
    !assemblerBoundarySource.includes('ValidatePrivateManifest') ||
    !assemblerBoundarySource.includes('assemble-r4-trusted-proof-evidence.mjs')
  ) {
    fail('evidence-authority-chain');
  }
  const historicalReceiptPath = path.join(
    fixtureRoot,
    'historical',
    'v1',
    'trusted-proof-payload-receipt.json',
  );
  verifyReceipt({ receiptPath: historicalReceiptPath, sourceRoot: root });
  const receiptPath = path.join(fixtureRoot, 'trusted-proof-payload-receipt-v2.json');
  const receipt = verifyReceiptV2({ receiptPath, sourceRoot: root });
  const receiptBytes = documents.get('trusted-proof-payload-receipt-v2.json').bytes;
  if (
    sha256(receiptBytes) !== receiptJsonSha256 ||
    sha256(Buffer.concat([Buffer.from('APR_R4_E2P_RECEIPT_V2 '), receiptBytes])) !==
      receiptLineSha256 ||
    receipt.source_commit !== payloadSourceSha ||
    receipt.source_tree !== payloadSourceTree ||
    receipt.compiled_payload_source_commit !== payloadSourceSha ||
    receipt.compiled_payload_source_tree !== payloadSourceTree ||
    receipt.action_source_sha !== actionSourceSha ||
    receipt.payload_sha256 !== payloadSha256 ||
    receipt.workflow_topology_sha256 !== templateSha256 ||
    receipt.result !== 'passed'
  ) {
    fail('receipt-identity');
  }
  const provenance = documents.get('receipt-provenance-v2.json').value;
  exactKeys(
    provenance,
    [
      'kind',
      'issue',
      'predecessor_issue',
      'predecessor_pr',
      'source_commit',
      'source_tree',
      'materialization_run_id',
      'materialization_job_id',
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
    provenance.kind !== 'apr-r4-e3-receipt-provenance-v2' ||
    provenance.issue !== 222 ||
    provenance.predecessor_issue !== 221 ||
    provenance.predecessor_pr !== 223 ||
    provenance.source_commit !== payloadSourceSha ||
    provenance.source_tree !== payloadSourceTree ||
    provenance.materialization_run_id !== '32846692929' ||
    provenance.materialization_job_id !== '97797924152' ||
    provenance.receipt_line_sha256 !== receiptLineSha256 ||
    provenance.receipt_json_sha256 !== receiptJsonSha256 ||
    provenance.workflow_template_sha256 !== templateSha256 ||
    provenance.rendered_workflow_sha256 !== renderedWorkflowSha256 ||
    provenance.action_source_sha !== actionSourceSha ||
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
