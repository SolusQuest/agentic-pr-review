import crypto from 'node:crypto';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const hex40 = /^[0-9a-f]{40}$/u;
const hex64 = /^[0-9a-f]{64}$/u;
const positiveDecimal = /^[1-9][0-9]*$/u;
const singleSegment = /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/u;
const roles = Object.freeze([
  'normal-bootstrap',
  'normal-continuation',
  'stale-protected',
  'stale-follow-on',
]);
const protectedRoles = new Set(['normal-bootstrap', 'normal-continuation', 'stale-protected']);
const receiptKeys = Object.freeze([
  'kind',
  'execution_authorization_sha256',
  'destination_identity_sha256',
  'materializer_source_sha256',
  'materializer_build_sha256',
  'operation_id',
  'role',
  'run_id',
  'run_attempt',
  'expected_event',
  'expected_run_head_sha',
  'sources',
  'finalized',
]);
const sourceKeys = Object.freeze([
  'source_id',
  'phase',
  'fragment_sha256',
  'fragment_physical_identity_sha256',
  'body_sha256',
  'body_size',
  'body_physical_identity_sha256',
]);

function fail(code) {
  throw new Error(`APR_R4_ENROLLMENT_OBSERVATION_INVALID ${code}`);
}
function exactKeys(value, expected, code) {
  if (
    value === null ||
    Array.isArray(value) ||
    typeof value !== 'object' ||
    JSON.stringify(Object.keys(value)) !== JSON.stringify(expected)
  ) {
    fail(code);
  }
}
function sha256(bytes) {
  return crypto.createHash('sha256').update(bytes).digest('hex');
}
function canonical(value) {
  return JSON.stringify(value);
}
function roleSourceContract(role, runId) {
  const prefix = `enrollment-${role}`;
  return Object.freeze({
    terminal: `${prefix}-run-${runId}:page:1`,
    terminalPhase: `${prefix}-terminal`,
    jobsPrefix: `${prefix}-jobs-run-${runId}:page:`,
    jobsPhase: `${prefix}-jobs`,
    discoveryPrefix: `${prefix}-discovery-run-${runId}:page:`,
    discoveryPhase: `${prefix}-discovery`,
    pull: `${prefix}-pull-run-${runId}:page:1`,
    pullPhase: `${prefix}-pull`,
    pending: `${prefix}-pending-run-${runId}:page:1`,
    pendingPhase: `${prefix}-pending`,
    approval: `${prefix}-approvals-run-${runId}:page:1`,
    approvalPhase: `${prefix}-approval`,
  });
}

export function validateEnrollmentObservationReceipt(receipt, expected) {
  exactKeys(receipt, receiptKeys, 'receipt-shape');
  exactKeys(
    expected,
    [
      'authority',
      'destination_identity_sha256',
      'operation_id',
      'role',
      'run_id',
      'expected_event',
      'expected_run_head_sha',
    ],
    'expected-shape',
  );
  if (
    receipt.kind !== 'apr-r4-e2p-enrollment-role-observation-v1' ||
    !roles.includes(expected.role) ||
    receipt.execution_authorization_sha256 !== expected.authority.execution_authorization_sha256 ||
    receipt.destination_identity_sha256 !== expected.destination_identity_sha256 ||
    receipt.materializer_source_sha256 !== expected.authority.phase_materializer_source_sha256 ||
    receipt.materializer_build_sha256 !== expected.authority.phase_materializer_build_sha256 ||
    receipt.operation_id !== expected.operation_id ||
    receipt.role !== expected.role ||
    receipt.run_id !== expected.run_id ||
    receipt.run_attempt !== '1' ||
    receipt.expected_event !== expected.expected_event ||
    receipt.expected_run_head_sha !== expected.expected_run_head_sha ||
    receipt.finalized !== true ||
    !hex64.test(receipt.operation_id) ||
    !positiveDecimal.test(receipt.run_id) ||
    !/^(?:workflow_run|workflow_dispatch)$/u.test(receipt.expected_event) ||
    !hex40.test(receipt.expected_run_head_sha) ||
    !Array.isArray(receipt.sources) ||
    receipt.sources.length < (protectedRoles.has(receipt.role) ? 6 : 4)
  ) {
    fail('receipt-values');
  }
  const fragments = new Set();
  const fragmentIdentities = new Set();
  const bodyIdentities = new Set();
  const sourceIds = new Set();
  for (const source of receipt.sources) {
    exactKeys(source, sourceKeys, 'source-shape');
    if (
      typeof source.source_id !== 'string' ||
      typeof source.phase !== 'string' ||
      ![
        source.fragment_sha256,
        source.fragment_physical_identity_sha256,
        source.body_sha256,
        source.body_physical_identity_sha256,
      ].every((value) => typeof value === 'string' && hex64.test(value)) ||
      typeof source.body_size !== 'string' ||
      !positiveDecimal.test(source.body_size) ||
      sourceIds.has(source.source_id) ||
      fragments.has(source.fragment_sha256) ||
      fragmentIdentities.has(source.fragment_physical_identity_sha256) ||
      bodyIdentities.has(source.body_physical_identity_sha256)
    ) {
      fail('source-values');
    }
    sourceIds.add(source.source_id);
    fragments.add(source.fragment_sha256);
    fragmentIdentities.add(source.fragment_physical_identity_sha256);
    bodyIdentities.add(source.body_physical_identity_sha256);
  }
  const contract = roleSourceContract(receipt.role, receipt.run_id);
  const terminal = receipt.sources.filter(
    (source) => source.source_id === contract.terminal && source.phase === contract.terminalPhase,
  );
  const jobs = receipt.sources
    .filter(
      (source) =>
        source.source_id.startsWith(contract.jobsPrefix) && source.phase === contract.jobsPhase,
    )
    .sort((left, right) => {
      const leftPage = Number(left.source_id.slice(contract.jobsPrefix.length));
      const rightPage = Number(right.source_id.slice(contract.jobsPrefix.length));
      return leftPage - rightPage;
    });
  const discovery = receipt.sources
    .filter(
      (source) =>
        source.source_id.startsWith(contract.discoveryPrefix) &&
        source.phase === contract.discoveryPhase,
    )
    .sort((left, right) => {
      const leftPage = Number(left.source_id.slice(contract.discoveryPrefix.length));
      const rightPage = Number(right.source_id.slice(contract.discoveryPrefix.length));
      return leftPage - rightPage;
    });
  const pull = receipt.sources.filter(
    (source) => source.source_id === contract.pull && source.phase === contract.pullPhase,
  );
  const protectedRole = protectedRoles.has(receipt.role);
  const pending = receipt.sources.filter(
    (source) => source.source_id === contract.pending && source.phase === contract.pendingPhase,
  );
  const approval = receipt.sources.filter(
    (source) => source.source_id === contract.approval && source.phase === contract.approvalPhase,
  );
  if (
    terminal.length !== 1 ||
    jobs.length === 0 ||
    jobs.some(
      (source, index) => source.source_id !== `${contract.jobsPrefix}${String(index + 1)}`,
    ) ||
    discovery.length === 0 ||
    discovery.some(
      (source, index) => source.source_id !== `${contract.discoveryPrefix}${String(index + 1)}`,
    ) ||
    pull.length !== 1 ||
    pending.length !== (protectedRole ? 1 : 0) ||
    approval.length !== (protectedRole ? 1 : 0) ||
    receipt.sources.length !== jobs.length + discovery.length + 2 + (protectedRole ? 2 : 0)
  ) {
    fail('source-contract');
  }
  return Object.freeze({ receipt: Object.freeze(receipt), sha256: sha256(canonical(receipt)) });
}

function safeEnvironment() {
  const environment = {};
  for (const name of [
    'PATH',
    'Path',
    'PATHEXT',
    'SYSTEMROOT',
    'SystemRoot',
    'WINDIR',
    'TEMP',
    'TMP',
    'TMPDIR',
    'HOME',
    'USERPROFILE',
    'DOTNET_ROOT',
    'DOTNET_ROOT_X64',
    'DOTNET_ROOT_X86',
    'DOTNET_CLI_HOME',
    'DOTNET_MULTILEVEL_LOOKUP',
    'DOTNET_SKIP_FIRST_TIME_EXPERIENCE',
    'LANG',
    'LC_ALL',
  ]) {
    if (process.env[name] !== undefined) environment[name] = process.env[name];
  }
  return environment;
}

function validateMaterializerOptions(options) {
  exactKeys(
    options,
    [
      'assembly_path',
      'dotnet_command',
      'restricted_root',
      'destination_identity_sha256',
      'repository_root',
      'worktree_root',
      'execution_authorization',
      'producer_journal_directory',
      'package_name',
      'authority',
      'run_process',
    ],
    'materializer-options-shape',
  );
  if (
    !path.isAbsolute(options.assembly_path) ||
    !path.isAbsolute(options.restricted_root) ||
    !path.isAbsolute(options.repository_root) ||
    !path.isAbsolute(options.worktree_root) ||
    typeof options.dotnet_command !== 'string' ||
    options.dotnet_command.length === 0 ||
    /[\r\n\0]/u.test(options.dotnet_command) ||
    !hex64.test(options.destination_identity_sha256) ||
    !singleSegment.test(options.producer_journal_directory) ||
    !singleSegment.test(options.package_name) ||
    typeof options.execution_authorization !== 'string' ||
    !/^[A-Za-z0-9][A-Za-z0-9._/-]{0,511}$/u.test(options.execution_authorization) ||
    options.execution_authorization.split('/').some((segment) => !singleSegment.test(segment)) ||
    typeof options.run_process !== 'function'
  ) {
    fail('materializer-options-values');
  }
  for (const field of [
    'execution_authorization_sha256',
    'phase_materializer_source_sha256',
    'phase_materializer_build_sha256',
  ]) {
    if (!hex64.test(options.authority?.[field] ?? '')) fail('materializer-authority');
  }
  let metadata;
  try {
    metadata = fs.lstatSync(options.assembly_path);
  } catch {
    fail('materializer-assembly');
  }
  if (
    !metadata.isFile() ||
    metadata.isSymbolicLink() ||
    metadata.size < 1 ||
    metadata.size > 64 * 1024 * 1024
  ) {
    fail('materializer-assembly');
  }
  const bytes = fs.readFileSync(options.assembly_path);
  try {
    if (sha256(bytes) !== options.authority.phase_materializer_build_sha256) {
      fail('materializer-build');
    }
  } finally {
    bytes.fill(0);
  }
}

export function createEnrollmentObservationMaterializer(options) {
  validateMaterializerOptions(options);
  const validate = (receipt, expected) =>
    validateEnrollmentObservationReceipt(receipt, {
      authority: options.authority,
      destination_identity_sha256: options.destination_identity_sha256,
      ...expected,
    });
  return Object.freeze({
    destination_identity_sha256: options.destination_identity_sha256,
    validate,
    materialize(expected) {
      exactKeys(
        expected,
        ['operation_id', 'role', 'run_id', 'expected_event', 'expected_run_head_sha'],
        'materialize-expected-shape',
      );
      if (
        !hex64.test(expected.operation_id) ||
        !roles.includes(expected.role) ||
        !positiveDecimal.test(expected.run_id) ||
        !/^(?:workflow_run|workflow_dispatch)$/u.test(expected.expected_event) ||
        !hex40.test(expected.expected_run_head_sha)
      ) {
        fail('materialize-expected-values');
      }
      const result = options.run_process(
        options.dotnet_command,
        [
          options.assembly_path,
          'enrollment-observation',
          '--restricted-root',
          options.restricted_root,
          '--destination-identity',
          options.destination_identity_sha256,
          '--repository-root',
          options.repository_root,
          '--worktree-root',
          options.worktree_root,
          '--execution-authorization',
          options.execution_authorization,
          '--execution-authorization-sha256',
          options.authority.execution_authorization_sha256,
          '--producer-journal-directory',
          options.producer_journal_directory,
          '--package-name',
          options.package_name,
          '--role',
          expected.role,
          '--run-id',
          expected.run_id,
          '--expected-event',
          expected.expected_event,
          '--expected-run-head',
          expected.expected_run_head_sha,
          '--operation-id',
          expected.operation_id,
        ],
        {
          encoding: 'utf8',
          env: safeEnvironment(),
          windowsHide: true,
          maxBuffer: 1024 * 1024,
        },
      );
      if (result.error || result.status !== 0 || result.signal !== null) {
        fail('materializer-process');
      }
      const stdout = String(result.stdout ?? '').replace(/\r/gu, '');
      let receipt;
      try {
        receipt = JSON.parse(stdout);
      } catch {
        fail('materializer-json');
      }
      if (stdout !== `${canonical(receipt)}\n`) fail('materializer-canonical');
      return validate(receipt, expected);
    },
  });
}

export const defaultEnrollmentObservationProcess = spawnSync;
