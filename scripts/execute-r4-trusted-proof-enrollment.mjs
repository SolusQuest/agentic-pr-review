import crypto from 'node:crypto';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import {
  createEnrollmentObservationMaterializer,
  defaultEnrollmentObservationProcess,
} from './materialize-r4-enrollment-observation.mjs';
import { validateEnrollmentExecutionAuthorityPackage } from './r4-trusted-proof-contract.mjs';

const hex40 = /^[0-9a-f]{40}$/u;
const hex64 = /^[0-9a-f]{64}$/u;
const manifestKeys = Object.freeze([
  'kind',
  'repository_id',
  'repository',
  'pr_number',
  'fixture_head_sha',
  'operation_id',
  'workflow_sha',
  'action_source_sha',
  'payload_source_sha',
  'payload_sha256',
]);

export const ENROLLMENT_CONTRACT = Object.freeze({
  kind: 'apr-r4-e2p-post-merge-enrollment-v1',
  hostRecordKind: 'apr-r4-e2p-host-restricted-enrollment-v1',
  authorizationKind: 'apr-r4-e2p-authorization-manifest-v2',
  repository: 'SolusQuest/agentic-pr-review',
  variable: 'R4_TRUSTED_PROOF_AUTHORIZATION',
  canaryPath: 'proof/apr178-path-canary.txt',
  canaryMode: '100644',
  initialCanary: 'APR178_TOOL_DATA_CANARY\n',
  advancedCanary: 'APR178_TOOL_DATA_CANARY_STALE\n',
  normal: Object.freeze({
    prNumber: '225',
    oldHead: '1dcec1b90429643338787fdb36fe33dfcac7dfa9',
    operationId: 'e4becee2ba102d93994b0b80f1c62739d657ca9438cd8c97288cf6a2155f1044',
  }),
  stale: Object.freeze({
    prNumber: '226',
    oldHead: '5dbda94d459e140aac5d18d2c0405287c62c5682',
    operationId: '7'.repeat(64),
  }),
  commitMetadata: Object.freeze({
    identity: Object.freeze({ name: 'Codex', email: 'codex@solusquest.local' }),
    normal: Object.freeze({
      message: 'test: refresh normal trusted-proof fixture\n',
      date: '2026-08-30T00:00:00+00:00',
    }),
    stale: Object.freeze({
      message: 'test: refresh stale trusted-proof fixture\n',
      date: '2026-08-30T00:00:01+00:00',
    }),
    advance: Object.freeze({
      message: 'test: advance stale trusted-proof fixture canary\n',
      date: '2026-08-30T00:00:02+00:00',
    }),
  }),
  phases: Object.freeze([
    'prepare',
    'refresh',
    'authorize-normal',
    'authorize-stale',
    'advance-stale',
    'cleanup',
  ]),
});

function fail(code) {
  throw new Error(`APR_R4_TRUSTED_PROOF_ENROLLMENT_INVALID ${code}`);
}
function json(value) {
  return JSON.stringify(value);
}
function hash(value) {
  return crypto.createHash('sha256').update(value).digest('hex');
}
function keys(value, expected, code) {
  if (
    !value ||
    typeof value !== 'object' ||
    Array.isArray(value) ||
    json(Object.keys(value)) !== json(expected)
  ) {
    fail(code);
  }
}
function sha(value, code) {
  if (typeof value !== 'string' || !hex40.test(value)) fail(code);
  return value;
}
function operation(value, code) {
  if (typeof value !== 'string' || !hex64.test(value)) fail(code);
  return value;
}
function positive(value, code) {
  if (typeof value !== 'string' || !/^[1-9][0-9]*$/u.test(value)) fail(code);
  return value;
}
function objectSha(type, bytes) {
  const body = Buffer.isBuffer(bytes) ? bytes : Buffer.from(bytes, 'utf8');
  return crypto
    .createHash('sha1')
    .update(Buffer.from(`${type} ${body.length}\0`, 'utf8'))
    .update(body)
    .digest('hex');
}
function commitPayload({ tree, parents, metadata }) {
  const identity = ENROLLMENT_CONTRACT.commitMetadata.identity;
  const timestamp = Math.floor(Date.parse(metadata.date) / 1000);
  if (!Number.isSafeInteger(timestamp)) fail('commit-date');
  return [
    `tree ${tree}`,
    ...parents.map((parent) => `parent ${parent}`),
    `author ${identity.name} <${identity.email}> ${timestamp} +0000`,
    `committer ${identity.name} <${identity.email}> ${timestamp} +0000`,
    '',
    metadata.message,
  ].join('\n');
}
function fixtureRef(operationId) {
  return `refs/heads/r4-trusted-proof/${operationId}`;
}
function frozenFixture(scope) {
  return ENROLLMENT_CONTRACT[scope];
}

export function canonicalAuthorizationManifest(value) {
  keys(value, manifestKeys, 'authorization-keys');
  const frozen = value.pr_number === '225' ? ENROLLMENT_CONTRACT.normal : ENROLLMENT_CONTRACT.stale;
  if (
    value.kind !== ENROLLMENT_CONTRACT.authorizationKind ||
    !/^[1-9][0-9]*$/u.test(value.repository_id) ||
    value.repository !== ENROLLMENT_CONTRACT.repository ||
    !/^(?:225|226)$/u.test(value.pr_number) ||
    !hex40.test(value.fixture_head_sha) ||
    !hex64.test(value.operation_id) ||
    value.operation_id !== frozen.operationId ||
    !hex40.test(value.workflow_sha) ||
    value.action_source_sha !== value.workflow_sha ||
    value.payload_source_sha !== value.workflow_sha ||
    !hex64.test(value.payload_sha256)
  ) {
    fail('authorization-values');
  }
  return json(value);
}

function objectRecord(kind, objectId, body) {
  return Object.freeze({ kind, sha: objectId, body: Object.freeze(body) });
}

function rolePlan(fixtures, workflowSha) {
  const role = (name, scope, route, event, head, manifestSha256, authorized, protectedJobs) => {
    const fixture = fixtures[scope];
    return Object.freeze({
      name,
      scope,
      route,
      event,
      repository: ENROLLMENT_CONTRACT.repository,
      workflow_sha: workflowSha,
      pr_number: fixture.pr_number,
      head_sha: head,
      operation_id: fixture.operation_id,
      authorization_manifest_sha256: manifestSha256,
      conclusion: 'success',
      preflight_authorized: authorized,
      protected_jobs_created: protectedJobs,
    });
  };
  return Object.freeze([
    role(
      'normal-bootstrap',
      'normal',
      'rerun-upstream-ci',
      'workflow_run',
      fixtures.normal.new_head,
      fixtures.normal.manifest_sha256,
      true,
      true,
    ),
    role(
      'normal-continuation',
      'normal',
      'dispatch-proof-workflow',
      'workflow_dispatch',
      fixtures.normal.new_head,
      fixtures.normal.manifest_sha256,
      true,
      true,
    ),
    role(
      'stale-protected',
      'stale',
      'rerun-upstream-ci',
      'workflow_run',
      fixtures.stale.new_head,
      fixtures.stale.manifest_sha256,
      true,
      true,
    ),
    role(
      'stale-follow-on',
      'stale',
      'advanced-ref-head',
      'workflow_run',
      fixtures.stale.advanced_head,
      fixtures.stale.manifest_sha256,
      false,
      false,
    ),
  ]);
}

function buildEnrollmentPlan({ coordinates, objects }) {
  keys(
    coordinates,
    ['repository_id', 'workflow_sha', 'workflow_tree_sha', 'payload_sha256'],
    'coordinate-keys',
  );
  keys(
    objects,
    [
      'initial_blob',
      'advanced_blob',
      'initial_tree',
      'advanced_tree',
      'normal_commit',
      'stale_commit',
      'advanced_commit',
    ],
    'object-keys',
  );
  const repositoryId = positive(coordinates.repository_id, 'repository-id');
  const workflowSha = sha(coordinates.workflow_sha, 'workflow-sha');
  const workflowTreeSha = sha(coordinates.workflow_tree_sha, 'workflow-tree-sha');
  const payloadSha256 = operation(coordinates.payload_sha256, 'payload-sha256');
  const ids = Object.fromEntries(
    Object.entries(objects).map(([name, value]) => [name, sha(value, `object-${name}`)]),
  );
  if (
    ids.initial_blob !== objectSha('blob', ENROLLMENT_CONTRACT.initialCanary) ||
    ids.advanced_blob !== objectSha('blob', ENROLLMENT_CONTRACT.advancedCanary) ||
    ids.initial_tree === workflowTreeSha ||
    ids.advanced_tree === workflowTreeSha ||
    ids.initial_tree === ids.advanced_tree
  ) {
    fail('object-identity');
  }
  const normalParents = [workflowSha, ENROLLMENT_CONTRACT.normal.oldHead];
  const staleParents = [workflowSha, ENROLLMENT_CONTRACT.stale.oldHead];
  const advancedParents = [ids.stale_commit];
  const commits = [
    [ids.normal_commit, ids.initial_tree, normalParents, ENROLLMENT_CONTRACT.commitMetadata.normal],
    [ids.stale_commit, ids.initial_tree, staleParents, ENROLLMENT_CONTRACT.commitMetadata.stale],
    [
      ids.advanced_commit,
      ids.advanced_tree,
      advancedParents,
      ENROLLMENT_CONTRACT.commitMetadata.advance,
    ],
  ];
  for (const [expected, tree, parents, metadata] of commits) {
    if (objectSha('commit', commitPayload({ tree, parents, metadata })) !== expected) {
      fail('commit-object-identity');
    }
  }
  const manifest = (scope, head) => {
    const fixture = frozenFixture(scope);
    return canonicalAuthorizationManifest({
      kind: ENROLLMENT_CONTRACT.authorizationKind,
      repository_id: repositoryId,
      repository: ENROLLMENT_CONTRACT.repository,
      pr_number: fixture.prNumber,
      fixture_head_sha: head,
      operation_id: fixture.operationId,
      workflow_sha: workflowSha,
      action_source_sha: workflowSha,
      payload_source_sha: workflowSha,
      payload_sha256: payloadSha256,
    });
  };
  const normalManifest = manifest('normal', ids.normal_commit);
  const staleManifest = manifest('stale', ids.stale_commit);
  const fixture = (scope, head, extra = {}) => {
    const frozen = frozenFixture(scope);
    const manifestText = scope === 'normal' ? normalManifest : staleManifest;
    return Object.freeze({
      pr_number: frozen.prNumber,
      ref: fixtureRef(frozen.operationId),
      operation_id: frozen.operationId,
      old_head: frozen.oldHead,
      new_head: head,
      tree: ids.initial_tree,
      parents: scope === 'normal' ? normalParents : staleParents,
      manifest: manifestText,
      manifest_sha256: hash(manifestText),
      ...extra,
    });
  };
  const fixtures = Object.freeze({
    normal: fixture('normal', ids.normal_commit),
    stale: fixture('stale', ids.stale_commit, {
      advanced_head: ids.advanced_commit,
      advanced_tree: ids.advanced_tree,
      advanced_parents: advancedParents,
    }),
  });
  return Object.freeze({
    kind: ENROLLMENT_CONTRACT.hostRecordKind,
    contract: ENROLLMENT_CONTRACT.kind,
    coordinates: Object.freeze({
      repository_id: repositoryId,
      repository: ENROLLMENT_CONTRACT.repository,
      workflow_sha: workflowSha,
      workflow_tree_sha: workflowTreeSha,
      action_source_sha: workflowSha,
      payload_source_sha: workflowSha,
      payload_sha256: payloadSha256,
    }),
    canary: Object.freeze({
      path: ENROLLMENT_CONTRACT.canaryPath,
      mode: ENROLLMENT_CONTRACT.canaryMode,
      initial_sha256: hash(ENROLLMENT_CONTRACT.initialCanary),
      advanced_sha256: hash(ENROLLMENT_CONTRACT.advancedCanary),
    }),
    objects: Object.freeze({
      initial_blob: objectRecord('blob', ids.initial_blob, {
        content: ENROLLMENT_CONTRACT.initialCanary,
        encoding: 'utf-8',
      }),
      advanced_blob: objectRecord('blob', ids.advanced_blob, {
        content: ENROLLMENT_CONTRACT.advancedCanary,
        encoding: 'utf-8',
      }),
      initial_tree: objectRecord('tree', ids.initial_tree, {
        base_tree: workflowTreeSha,
        tree: [
          {
            path: ENROLLMENT_CONTRACT.canaryPath,
            mode: ENROLLMENT_CONTRACT.canaryMode,
            type: 'blob',
            sha: ids.initial_blob,
          },
        ],
      }),
      advanced_tree: objectRecord('tree', ids.advanced_tree, {
        base_tree: workflowTreeSha,
        tree: [
          {
            path: ENROLLMENT_CONTRACT.canaryPath,
            mode: ENROLLMENT_CONTRACT.canaryMode,
            type: 'blob',
            sha: ids.advanced_blob,
          },
        ],
      }),
      normal_commit: objectRecord('commit', ids.normal_commit, {
        message: ENROLLMENT_CONTRACT.commitMetadata.normal.message,
        tree: ids.initial_tree,
        parents: normalParents,
        author: {
          ...ENROLLMENT_CONTRACT.commitMetadata.identity,
          date: ENROLLMENT_CONTRACT.commitMetadata.normal.date,
        },
        committer: {
          ...ENROLLMENT_CONTRACT.commitMetadata.identity,
          date: ENROLLMENT_CONTRACT.commitMetadata.normal.date,
        },
      }),
      stale_commit: objectRecord('commit', ids.stale_commit, {
        message: ENROLLMENT_CONTRACT.commitMetadata.stale.message,
        tree: ids.initial_tree,
        parents: staleParents,
        author: {
          ...ENROLLMENT_CONTRACT.commitMetadata.identity,
          date: ENROLLMENT_CONTRACT.commitMetadata.stale.date,
        },
        committer: {
          ...ENROLLMENT_CONTRACT.commitMetadata.identity,
          date: ENROLLMENT_CONTRACT.commitMetadata.stale.date,
        },
      }),
      advanced_commit: objectRecord('commit', ids.advanced_commit, {
        message: ENROLLMENT_CONTRACT.commitMetadata.advance.message,
        tree: ids.advanced_tree,
        parents: advancedParents,
        author: {
          ...ENROLLMENT_CONTRACT.commitMetadata.identity,
          date: ENROLLMENT_CONTRACT.commitMetadata.advance.date,
        },
        committer: {
          ...ENROLLMENT_CONTRACT.commitMetadata.identity,
          date: ENROLLMENT_CONTRACT.commitMetadata.advance.date,
        },
      }),
    }),
    fixtures,
    role_plan: rolePlan(fixtures, workflowSha),
    mutation_envelope: Object.freeze({
      phases: ENROLLMENT_CONTRACT.phases,
      ref_method: 'PATCH',
      ref_force: false,
      variable_methods: ['POST', 'PATCH', 'DELETE'],
      cleanup: 'delete-variable-after-four-terminal-roles-and-rejected-follow-on',
    }),
  });
}

/** Consume only the deterministic post-merge materializer's exact object package. */
export function bindEnrollmentRecord(input) {
  keys(
    input,
    ['repository_id', 'payload_sha256', 'materialized', 'execution_authority'],
    'binding-keys',
  );
  const plan = enrollmentPlanFromMaterialized(input);
  const authority = input.execution_authority;
  keys(
    authority,
    [
      'kind',
      'execution_authorization_sha256',
      'enrollment_record_sha256',
      'capture_source_set_sha256',
      'capture_source_sha256',
      'capture_build_sha256',
      'phase_materializer_source_sha256',
      'phase_materializer_build_sha256',
    ],
    'execution-authority-keys',
  );
  if (
    authority.kind !== 'apr-r4-e2p-enrollment-authority-binding-v1' ||
    ![
      authority.execution_authorization_sha256,
      authority.enrollment_record_sha256,
      authority.capture_source_set_sha256,
      authority.capture_source_sha256,
      authority.capture_build_sha256,
      authority.phase_materializer_source_sha256,
      authority.phase_materializer_build_sha256,
    ].every((value) => typeof value === 'string' && hex64.test(value)) ||
    authority.enrollment_record_sha256 !== hash(json(plan))
  ) {
    fail('execution-authority-values');
  }
  return Object.freeze({ ...plan, authority: Object.freeze({ ...authority }) });
}

function enrollmentPlanFromMaterialized(input) {
  const { materialized } = input;
  keys(materialized, ['merge_sha', 'merge_tree', 'normal', 'stale', 'canary'], 'materialized-keys');
  keys(materialized.normal, ['prior_head', 'head', 'tree', 'parents'], 'materialized-normal');
  keys(
    materialized.stale,
    ['prior_head', 'head', 'tree', 'parents', 'advanced_head', 'advanced_tree', 'advanced_parents'],
    'materialized-stale',
  );
  keys(materialized.canary, ['path', 'initial_blob', 'advanced_blob'], 'materialized-canary');
  if (
    materialized.normal.prior_head !== ENROLLMENT_CONTRACT.normal.oldHead ||
    materialized.stale.prior_head !== ENROLLMENT_CONTRACT.stale.oldHead ||
    json(materialized.normal.parents) !==
      json([materialized.merge_sha, ENROLLMENT_CONTRACT.normal.oldHead]) ||
    json(materialized.stale.parents) !==
      json([materialized.merge_sha, ENROLLMENT_CONTRACT.stale.oldHead]) ||
    materialized.normal.tree !== materialized.stale.tree ||
    json(materialized.stale.advanced_parents) !== json([materialized.stale.head]) ||
    materialized.canary.path !== ENROLLMENT_CONTRACT.canaryPath
  ) {
    fail('materialized-binding');
  }
  return buildEnrollmentPlan({
    coordinates: {
      repository_id: input.repository_id,
      workflow_sha: materialized.merge_sha,
      workflow_tree_sha: materialized.merge_tree,
      payload_sha256: input.payload_sha256,
    },
    objects: {
      initial_blob: materialized.canary.initial_blob,
      advanced_blob: materialized.canary.advanced_blob,
      initial_tree: materialized.normal.tree,
      advanced_tree: materialized.stale.advanced_tree,
      normal_commit: materialized.normal.head,
      stale_commit: materialized.stale.head,
      advanced_commit: materialized.stale.advanced_head,
    },
  });
}

/** Prepare the exact unsigned plan digest that the maintainer execution record must authorize. */
export function prepareEnrollmentRecord(input) {
  keys(input, ['repository_id', 'payload_sha256', 'materialized'], 'preparation-binding-keys');
  const plan = enrollmentPlanFromMaterialized(input);
  return Object.freeze({ plan, sha256: hash(json(plan)) });
}

export function validateEnrollmentRecord(record) {
  keys(
    record,
    [
      'kind',
      'contract',
      'coordinates',
      'canary',
      'objects',
      'fixtures',
      'role_plan',
      'mutation_envelope',
      'authority',
    ],
    'record-keys',
  );
  if (
    record.kind !== ENROLLMENT_CONTRACT.hostRecordKind ||
    record.contract !== ENROLLMENT_CONTRACT.kind
  ) {
    fail('record-kind');
  }
  const rebuiltPlan = buildEnrollmentPlan({
    coordinates: {
      repository_id: record.coordinates?.repository_id,
      workflow_sha: record.coordinates?.workflow_sha,
      workflow_tree_sha: record.coordinates?.workflow_tree_sha,
      payload_sha256: record.coordinates?.payload_sha256,
    },
    objects: Object.fromEntries(
      [
        'initial_blob',
        'advanced_blob',
        'initial_tree',
        'advanced_tree',
        'normal_commit',
        'stale_commit',
        'advanced_commit',
      ].map((name) => [name, record.objects?.[name]?.sha]),
    ),
  });
  const rebuilt = Object.freeze({ ...rebuiltPlan, authority: record.authority });
  keys(
    record.authority,
    [
      'kind',
      'execution_authorization_sha256',
      'enrollment_record_sha256',
      'capture_source_set_sha256',
      'capture_source_sha256',
      'capture_build_sha256',
      'phase_materializer_source_sha256',
      'phase_materializer_build_sha256',
    ],
    'record-authority-keys',
  );
  if (
    record.authority.kind !== 'apr-r4-e2p-enrollment-authority-binding-v1' ||
    record.authority.enrollment_record_sha256 !== hash(json(rebuiltPlan)) ||
    !Object.values(record.authority)
      .slice(1)
      .every((value) => typeof value === 'string' && hex64.test(value)) ||
    json(record) !== json(rebuilt)
  ) {
    fail('record-canonical');
  }
  return true;
}

function notFound(error) {
  return error?.status === 404 || error?.notFound === true;
}
function uncertain(error) {
  return error?.uncertain === true;
}
function objectTarget(object) {
  return `object:${object.kind}:${object.sha}`;
}
function commitTreeTarget(commit) {
  return `commit-tree:${commit}`;
}
function variableTarget() {
  return `variable:${ENROLLMENT_CONTRACT.variable}`;
}
function pullBinding(record, fixture, head) {
  return json({
    repository: ENROLLMENT_CONTRACT.repository,
    number: fixture.pr_number,
    state: 'open',
    draft: false,
    base_ref: 'main',
    base_sha: record.coordinates.workflow_sha,
    head_ref: fixture.ref.slice('refs/heads/'.length),
    head_sha: head,
  });
}
function liveEndpoint(repository, method, target) {
  const commitTreeMatch = /^commit-tree:([0-9a-f]{40})$/u.exec(target);
  if (commitTreeMatch) {
    if (method !== 'GET') fail('live-target-method');
    return `/repos/${repository}/git/commits/${commitTreeMatch[1]}`;
  }
  const objectMatch = /^object:(blob|tree|commit):([0-9a-f]{40})$/u.exec(target);
  if (objectMatch) {
    const [, kind, objectId] = objectMatch;
    const family = `${kind}s`;
    return `/repos/${repository}/git/${family}${method === 'GET' ? `/${objectId}` : ''}`;
  }
  if (target.startsWith('variable:')) {
    const name = target.slice('variable:'.length);
    return method === 'POST'
      ? `/repos/${repository}/actions/variables`
      : `/repos/${repository}/actions/variables/${name}`;
  }
  if (target.startsWith('ref:')) {
    const ref = target.slice('ref:'.length).replace(/^refs\//u, '');
    return `/repos/${repository}/git/${method === 'GET' ? 'ref' : 'refs'}/${ref}`;
  }
  if (target.startsWith('pull:') && method === 'GET') {
    return `/repos/${repository}/pulls/${target.slice('pull:'.length)}`;
  }
  fail('live-target');
}
function responseBody(stdout) {
  const normalized = stdout.replace(/\r/gu, '');
  const statusLines = [...normalized.matchAll(/^HTTP\/\S+\s+(\d{3})[^\n]*$/gmu)];
  const last = statusLines.at(-1);
  if (!last) fail('gh-http-status');
  const separator = normalized.indexOf('\n\n', last.index);
  return {
    status: Number(last[1]),
    body: separator < 0 ? '' : normalized.slice(separator + 2).trim(),
  };
}
function ghError(result) {
  const output = `${result.stdout ?? ''}\n${result.stderr ?? ''}`.replace(/\r/gu, '');
  const statuses = [...output.matchAll(/(?:^HTTP\/\S+\s+|\(HTTP\s+)(\d{3})(?:\s|\))/gmu)];
  const status = statuses.length === 0 ? null : Number(statuses.at(-1)[1]);
  const error = new Error('gh api request failed');
  if (status !== null) {
    error.status = status;
    return error;
  }
  const code = String(result.error?.code ?? '');
  if (
    /^(?:ETIMEDOUT|ECONNRESET|ECONNREFUSED|ENETUNREACH|EHOSTUNREACH|EPIPE)$/u.test(code) ||
    /(?:timed? out|connection (?:reset|refused)|network is unreachable|could not resolve|temporary failure|tls handshake timeout|unexpected eof)/iu.test(
      output,
    )
  ) {
    error.uncertain = true;
  }
  error.causeCode = code || null;
  return error;
}
function normalizeLiveBinding(target, response) {
  if (target.startsWith('commit-tree:')) {
    return json({ commit_sha: response?.sha ?? null, tree_sha: response?.tree?.sha ?? null });
  }
  if (/^object:(?:blob|tree|commit):/u.test(target)) return response?.sha ?? null;
  if (target.startsWith('ref:')) return response?.object?.sha ?? null;
  if (target.startsWith('variable:')) return response?.value ?? null;
  if (target.startsWith('pull:')) {
    return json({
      repository: response?.base?.repo?.full_name,
      number: String(response?.number ?? ''),
      state: response?.state,
      draft: response?.draft,
      base_ref: response?.base?.ref,
      base_sha: response?.base?.sha,
      head_ref: response?.head?.ref,
      head_sha: response?.head?.sha,
    });
  }
  fail('live-binding');
}

/** Live GitHub is constructed only by the explicit execution CLI. */
export function createGhTransport({ repository, runGh = spawnSync }) {
  if (repository !== ENROLLMENT_CONTRACT.repository || typeof runGh !== 'function') {
    fail('repository');
  }
  const call = (method, target, body) => {
    const args = ['api', '-X', method, liveEndpoint(repository, method, target), '--include'];
    if (body !== undefined) args.push('--input', '-');
    const result = runGh('gh', args, {
      input: body === undefined ? undefined : json(body),
      encoding: 'utf8',
      windowsHide: true,
      maxBuffer: 16 * 1024 * 1024,
    });
    if (result.error || result.status !== 0) throw ghError(result);
    const parsed = responseBody(result.stdout ?? '');
    if (parsed.status < 200 || parsed.status >= 300) {
      const error = new Error('gh api request failed');
      error.status = parsed.status;
      throw error;
    }
    let response = {};
    if (parsed.body !== '') {
      try {
        response = JSON.parse(parsed.body);
      } catch {
        fail('gh-response-json');
      }
    }
    return normalizeLiveBinding(target, response);
  };
  return Object.freeze({
    async read(target) {
      try {
        return call('GET', target);
      } catch (error) {
        if (notFound(error)) return null;
        throw error;
      }
    },
    async mutate(method, target, body) {
      return call(method, target, body);
    },
  });
}

const journalKinds = Object.freeze([
  'phase-start',
  'intent',
  'wire-start',
  'wire-result',
  'readback',
  'observation',
  'action-complete',
  'phase-complete',
]);
const phaseActions = Object.freeze({
  prepare: Object.freeze([
    'read-merged-commit-tree',
    'read-merged-tree',
    'upload-initial-blob',
    'upload-advanced-blob',
    'upload-initial-tree',
    'upload-advanced-tree',
    'upload-normal-commit',
    'upload-stale-commit',
    'upload-advanced-commit',
  ]),
  refresh: Object.freeze([
    'baseline-variable-absent',
    'read-stale-old-ref',
    'read-normal-old-ref',
    'refresh-stale-ref',
    'read-stale-pull',
    'refresh-normal-ref',
    'read-normal-pull',
  ]),
  'authorize-normal': Object.freeze(['normal-variable-still-absent', 'write-normal-manifest']),
  'authorize-stale': Object.freeze([
    'normal-bootstrap',
    'normal-continuation',
    'normal-manifest-retained',
    'replace-stale-manifest',
    'stale-manifest-before-advance',
  ]),
  'advance-stale': Object.freeze([
    'stale-protected',
    'read-advanced-stale-ref',
    'read-advanced-stale-pull',
    'stale-manifest-after-advance',
  ]),
  cleanup: Object.freeze([
    'stale-follow-on',
    'stale-manifest-before-cleanup',
    'delete-authorization-variable',
    'final-variable-absent',
  ]),
});
const roleObservationActions = new Set([
  'normal-bootstrap',
  'normal-continuation',
  'stale-protected',
  'stale-follow-on',
]);
const journalKeys = Object.freeze([
  'ordinal',
  'kind',
  'phase',
  'action',
  'attempt',
  'target',
  'method',
  'expected',
  'observed',
  'outcome',
  'detail',
  'previous_sha256',
  'record_sha256',
]);

export function canonicalRecordText(record) {
  validateEnrollmentRecord(record);
  return json(record);
}
export function recordSha256(record) {
  return hash(canonicalRecordText(record));
}
const phaseLockKind = 'apr-r4-e2p-enrollment-phase-lock-v1';
const phaseLockLeaseMilliseconds = 15 * 60 * 1000;
// A PID alone is reusable after a process exits. This process-local identity is
// deliberately never persisted outside a lock and therefore distinguishes a
// prior process that happened to receive the same PID from this executor.
const processInstanceId = crypto.randomBytes(32).toString('hex');
const currentProcessLeaseOwners = new Map();
const phaseLockKeys = Object.freeze([
  'kind',
  'pid',
  'instance_id',
  'token',
  'record_sha256',
  'phase',
  'acquired_at',
  'expires_at',
]);
function syncDirectory(directory) {
  let descriptor;
  try {
    descriptor = fs.openSync(directory, 'r');
    fs.fsyncSync(descriptor);
  } catch (error) {
    if (
      process.platform !== 'win32' ||
      !['EPERM', 'EINVAL', 'ENOTSUP', 'EBADF'].includes(error?.code)
    ) {
      fail('durable-directory-sync');
    }
  } finally {
    if (descriptor !== undefined) fs.closeSync(descriptor);
  }
}
function durableCreate(destination, output, stagingDirectory = path.dirname(destination)) {
  const parent = path.dirname(destination);
  fs.mkdirSync(parent, { recursive: true });
  fs.mkdirSync(stagingDirectory, { recursive: true });
  const temporary = path.join(
    stagingDirectory,
    `.${path.basename(destination)}-${crypto.randomBytes(16).toString('hex')}.tmp`,
  );
  let descriptor;
  try {
    descriptor = fs.openSync(temporary, 'wx', 0o600);
    fs.writeFileSync(descriptor, output, 'utf8');
    fs.fsyncSync(descriptor);
    fs.closeSync(descriptor);
    descriptor = undefined;
    fs.linkSync(temporary, destination);
    fs.unlinkSync(temporary);
    syncDirectory(parent);
    if (path.resolve(stagingDirectory) !== path.resolve(parent)) syncDirectory(stagingDirectory);
  } catch (error) {
    if (descriptor !== undefined) fs.closeSync(descriptor);
    try {
      fs.unlinkSync(temporary);
    } catch {
      // A successful unlink already removed the temporary hard-link source.
    }
    throw error;
  }
}
function durableReplace(destination, output, stagingDirectory = path.dirname(destination)) {
  const parent = path.dirname(destination);
  fs.mkdirSync(parent, { recursive: true });
  fs.mkdirSync(stagingDirectory, { recursive: true });
  const temporary = path.join(
    stagingDirectory,
    `.${path.basename(destination)}-${crypto.randomBytes(16).toString('hex')}.tmp`,
  );
  let descriptor;
  try {
    descriptor = fs.openSync(temporary, 'wx', 0o600);
    fs.writeFileSync(descriptor, output, 'utf8');
    fs.fsyncSync(descriptor);
    fs.closeSync(descriptor);
    descriptor = undefined;
    fs.renameSync(temporary, destination);
    syncDirectory(parent);
    if (path.resolve(stagingDirectory) !== path.resolve(parent)) syncDirectory(stagingDirectory);
  } catch (error) {
    if (descriptor !== undefined) fs.closeSync(descriptor);
    try {
      fs.unlinkSync(temporary);
    } catch {
      // A successful rename already removed the staged source.
    }
    throw error;
  }
}
function phaseLockPath(directory) {
  return `${path.resolve(directory)}.lock`;
}
function parsePhaseLock(text) {
  let value;
  try {
    value = JSON.parse(text);
  } catch {
    fail('phase-lock-json');
  }
  keys(value, phaseLockKeys, 'phase-lock-keys');
  if (
    value.kind !== phaseLockKind ||
    !Number.isSafeInteger(value.pid) ||
    value.pid < 1 ||
    !hex64.test(value.instance_id) ||
    !hex64.test(value.token) ||
    !hex64.test(value.record_sha256) ||
    !ENROLLMENT_CONTRACT.phases.includes(value.phase) ||
    !Number.isSafeInteger(value.acquired_at) ||
    !Number.isSafeInteger(value.expires_at) ||
    value.expires_at - value.acquired_at !== phaseLockLeaseMilliseconds ||
    text !== `${json(value)}\n`
  ) {
    fail('phase-lock-values');
  }
  return Object.freeze(value);
}
function processIsAlive(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return error?.code !== 'ESRCH';
  }
}
function lockOwnedByThisProcessInstance(lock) {
  return lock.pid === process.pid && lock.instance_id === processInstanceId;
}
function reclaimablePhaseLock(lock, now) {
  const state = lockOwnedByThisProcessInstance(lock)
    ? currentProcessLeaseOwners.get(lock.token)
    : undefined;
  // A failed release is recoverable by this exact process instance after the
  // protected operation has returned. Active/acquiring owners are never
  // reclaimed, even if a clock has advanced past their lease.
  if (state === 'release-pending') return true;
  if (lock.expires_at > now) return false;
  if (lockOwnedByThisProcessInstance(lock)) return state !== 'active' && state !== 'acquiring';
  // Same PID with a different process-instance identity is PID reuse. A
  // foreign live process remains authoritative only when it has a different
  // PID and an unexpired lease.
  if (lock.pid === process.pid) return true;
  return !processIsAlive(lock.pid);
}
function quarantineLock(destination, text, code) {
  const quarantine = `${destination}.stale-${hash(text)}`;
  try {
    fs.linkSync(destination, quarantine);
  } catch (error) {
    if (error?.code !== 'EEXIST') fail(code);
    let preserved;
    try {
      preserved = fs.readFileSync(quarantine, 'utf8');
    } catch {
      fail(code);
    }
    if (preserved !== text) fail(code);
  }
  let current;
  try {
    current = fs.readFileSync(destination, 'utf8');
  } catch {
    fail(code);
  }
  if (current !== text) fail('phase-lock-race');
  syncDirectory(path.dirname(destination));
}
function releaseAcquisitionGuard(acquisitionGuard, owner) {
  let text;
  try {
    text = fs.readFileSync(acquisitionGuard, 'utf8');
  } catch {
    fail('phase-lock-acquisition-release');
  }
  const current = parsePhaseLock(text);
  if (json(current) !== json(owner)) fail('phase-lock-acquisition-release-owner');
  try {
    fs.unlinkSync(acquisitionGuard);
    syncDirectory(path.dirname(acquisitionGuard));
  } catch {
    fail('phase-lock-acquisition-release');
  }
}
/**
 * This endpoint is host-local (and, for containers, local to the host's network/PID namespace).
 * It fences only cooperating enrollment executors; it is never a repository or network lease.
 */
export function recoveryEndpointForDirectory(directory) {
  if (typeof directory !== 'string' || directory.length === 0) fail('journal-directory');
  const destination = phaseLockPath(directory);
  const parent = path.dirname(destination);
  let physicalParent;
  try {
    physicalParent = fs.realpathSync.native(parent);
  } catch {
    fail('phase-lock-recovery-parent');
  }
  if (typeof physicalParent !== 'string' || physicalParent.length === 0) {
    fail('phase-lock-recovery-parent');
  }
  const basename = path.basename(destination);
  if (basename.length === 0) fail('phase-lock-recovery-basename');
  const identity =
    process.platform === 'win32'
      ? `${physicalParent.replace(/\//gu, '\\').toLocaleLowerCase('en-US')}\0${basename.toLocaleLowerCase(
          'en-US',
        )}`
      : `${physicalParent}\0${basename}`;
  const endpointName = `apr-r4-e2p-${hash(identity)}`;
  if (process.platform === 'win32') return `\\\\.\\pipe\\${endpointName}`;
  if (process.platform === 'linux') return `\0${endpointName}`;
  fail('phase-lock-recovery-endpoint');
}
async function acquireRecoveryMutex(endpoint) {
  const server = net.createServer();
  try {
    await new Promise((resolve, reject) => {
      server.once('error', reject);
      server.listen({ path: endpoint, exclusive: true }, resolve);
    });
  } catch (error) {
    try {
      server.close();
    } catch {
      // The failed listener did not acquire a handle to release.
    }
    if (error?.code === 'EADDRINUSE') fail('phase-lock-acquisition-busy');
    fail('phase-lock-recovery-mutex');
  }
  return Object.freeze({ server, endpoint });
}
async function releaseRecoveryMutex(mutex) {
  await new Promise((resolve, reject) => {
    mutex.server.close((error) => (error ? reject(error) : resolve()));
  }).catch(() => fail('phase-lock-recovery-release'));
}
function clearStalePriorReclamationGuard(acquisitionGuard, now) {
  const priorGuard = `${acquisitionGuard}.reclaim`;
  if (!fs.existsSync(priorGuard)) return;
  let text;
  let lock;
  try {
    text = fs.readFileSync(priorGuard, 'utf8');
    lock = parsePhaseLock(text);
  } catch {
    fail('phase-lock-acquisition-reclaim-read');
  }
  if (!reclaimablePhaseLock(lock, now)) fail('phase-lock-acquisition-busy');
  quarantineLock(priorGuard, text, 'phase-lock-acquisition-reclaim-quarantine');
  try {
    fs.unlinkSync(priorGuard);
    syncDirectory(path.dirname(priorGuard));
  } catch {
    fail('phase-lock-acquisition-reclaim');
  }
}
async function acquirePhaseLock({ directory, record, phase }) {
  const destination = phaseLockPath(directory);
  const acquisitionGuard = `${destination}.acquire`;
  const recoveryEndpoint = recoveryEndpointForDirectory(directory);
  const now = Date.now();
  const owner = Object.freeze({
    kind: phaseLockKind,
    pid: process.pid,
    instance_id: processInstanceId,
    token: crypto.randomBytes(32).toString('hex'),
    record_sha256: recordSha256(record),
    phase,
    acquired_at: now,
    expires_at: now + phaseLockLeaseMilliseconds,
  });
  const ownerText = `${json(owner)}\n`;
  let recoveryMutex = null;
  let acquisitionGuardOwned = false;
  try {
    durableCreate(acquisitionGuard, ownerText);
    acquisitionGuardOwned = true;
    currentProcessLeaseOwners.set(owner.token, 'acquiring');
  } catch (error) {
    if (error?.code !== 'EEXIST') fail('phase-lock-acquisition-create');
    recoveryMutex = await acquireRecoveryMutex(recoveryEndpoint);
    try {
      let staleText;
      let stale;
      try {
        staleText = fs.readFileSync(acquisitionGuard, 'utf8');
        stale = parsePhaseLock(staleText);
      } catch {
        fail('phase-lock-acquisition-read');
      }
      if (!reclaimablePhaseLock(stale, now)) fail('phase-lock-acquisition-busy');
      clearStalePriorReclamationGuard(acquisitionGuard, now);
      let currentText;
      let current;
      try {
        currentText = fs.readFileSync(acquisitionGuard, 'utf8');
        current = parsePhaseLock(currentText);
      } catch {
        fail('phase-lock-acquisition-read');
      }
      if (currentText !== staleText || !reclaimablePhaseLock(current, now)) {
        fail('phase-lock-acquisition-busy');
      }
      quarantineLock(acquisitionGuard, currentText, 'phase-lock-acquisition-quarantine');
      try {
        durableReplace(acquisitionGuard, ownerText);
        acquisitionGuardOwned = true;
        currentProcessLeaseOwners.set(owner.token, 'acquiring');
      } catch {
        fail('phase-lock-acquisition-reclaim');
      }
    } catch (recoveryError) {
      currentProcessLeaseOwners.delete(owner.token);
      await releaseRecoveryMutex(recoveryMutex);
      recoveryMutex = null;
      throw recoveryError;
    }
  }
  let phaseLockCreated = false;
  let acquisitionReleaseError;
  let recoveryReleaseError;
  try {
    try {
      durableCreate(destination, `${json(owner)}\n`);
      phaseLockCreated = true;
    } catch (error) {
      if (error?.code !== 'EEXIST') fail('phase-lock-create');
      let existingText;
      try {
        existingText = fs.readFileSync(destination, 'utf8');
      } catch {
        fail('phase-lock-read');
      }
      const existing = parsePhaseLock(existingText);
      if (
        existing.record_sha256 !== owner.record_sha256 ||
        existing.phase !== phase ||
        !reclaimablePhaseLock(existing, now)
      ) {
        fail('phase-lock-busy');
      }
      try {
        quarantineLock(destination, existingText, 'phase-lock-reclaim');
        fs.unlinkSync(destination);
        durableCreate(destination, `${json(owner)}\n`);
        syncDirectory(path.dirname(destination));
        phaseLockCreated = true;
      } catch {
        fail('phase-lock-reclaim');
      }
    }
  } finally {
    try {
      if (acquisitionGuardOwned) releaseAcquisitionGuard(acquisitionGuard, owner);
    } catch (error) {
      acquisitionReleaseError = error;
    }
    try {
      if (recoveryMutex !== null) await releaseRecoveryMutex(recoveryMutex);
    } catch (error) {
      recoveryReleaseError = error;
    }
    if (phaseLockCreated) {
      currentProcessLeaseOwners.set(
        owner.token,
        acquisitionReleaseError ? 'release-pending' : 'active',
      );
    } else if (acquisitionReleaseError) {
      currentProcessLeaseOwners.set(owner.token, 'release-pending');
    } else {
      currentProcessLeaseOwners.delete(owner.token);
    }
    if (acquisitionReleaseError) throw acquisitionReleaseError;
    if (recoveryReleaseError) throw recoveryReleaseError;
  }
  return Object.freeze({ destination, owner });
}
function releasePhaseLock(lock) {
  let text;
  try {
    text = fs.readFileSync(lock.destination, 'utf8');
  } catch {
    currentProcessLeaseOwners.set(lock.owner.token, 'release-pending');
    fail('phase-lock-release-read');
  }
  const current = parsePhaseLock(text);
  if (json(current) !== json(lock.owner)) {
    currentProcessLeaseOwners.set(lock.owner.token, 'release-pending');
    fail('phase-lock-release-owner');
  }
  try {
    fs.unlinkSync(lock.destination);
    syncDirectory(path.dirname(lock.destination));
  } catch {
    currentProcessLeaseOwners.set(lock.owner.token, 'release-pending');
    fail('phase-lock-release');
  }
  currentProcessLeaseOwners.delete(lock.owner.token);
}
function journalFileName(fragment) {
  const safe = fragment.action.replace(/[^a-z0-9-]/gu, '-');
  return `${String(fragment.ordinal).padStart(6, '0')}-${fragment.kind}-${safe}.json`;
}
function validateFragment(fragment, index, previous, expectedRecord, name, text) {
  keys(fragment, journalKeys, 'journal-fragment-keys');
  if (
    fragment.ordinal !== index + 1 ||
    !journalKinds.includes(fragment.kind) ||
    !ENROLLMENT_CONTRACT.phases.includes(fragment.phase) ||
    typeof fragment.action !== 'string' ||
    !/^[a-z0-9][a-z0-9-]{0,95}$/u.test(fragment.action) ||
    (fragment.attempt !== null &&
      (!Number.isSafeInteger(fragment.attempt) || fragment.attempt < 1 || fragment.attempt > 2)) ||
    (fragment.target !== null && typeof fragment.target !== 'string') ||
    (fragment.method !== null && !/^(?:GET|POST|PATCH|DELETE)$/u.test(fragment.method)) ||
    (fragment.expected !== null && typeof fragment.expected !== 'string') ||
    (fragment.observed !== null && typeof fragment.observed !== 'string') ||
    typeof fragment.outcome !== 'string' ||
    fragment.outcome.length > 96 ||
    (fragment.detail !== null &&
      (!fragment.detail ||
        typeof fragment.detail !== 'object' ||
        Array.isArray(fragment.detail))) ||
    fragment.previous_sha256 !== previous ||
    fragment.record_sha256 !== expectedRecord ||
    name !== journalFileName(fragment) ||
    text !== `${json(fragment)}\n`
  ) {
    fail('journal-fragment');
  }
}
function validateActionEvents(events, complete) {
  if (events.length === 0) fail('journal-action-empty');
  const intents = events.filter((entry) => entry.kind === 'intent');
  const observations = events.filter((entry) => entry.kind === 'observation');
  const completions = events.filter((entry) => entry.kind === 'action-complete');
  const action = events[0].action;
  const requiresRoleObservation = roleObservationActions.has(action);
  if (
    (requiresRoleObservation
      ? intents.length !== 0 || observations.length !== 1
      : intents.length !== 1 || observations.length !== 0) ||
    completions.length > 1 ||
    (complete && (completions.length !== 1 || events.at(-1).kind !== 'action-complete')) ||
    (!complete && completions.length !== 0)
  ) {
    fail('journal-action-shape');
  }
  if (observations.length === 1) {
    const observation = observations[0];
    if (
      observation.method !== 'GET' ||
      observation.target !== `workflow-run:${String(observation.detail?.run_id ?? '')}` ||
      observation.expected !== hash(json(observation.detail)) ||
      observation.observed !== observation.expected ||
      observation.outcome !== 'exact-external-readback' ||
      events.some((entry) => !['observation', 'action-complete'].includes(entry.kind)) ||
      completions.some(
        (entry) =>
          entry.target !== observation.target ||
          entry.method !== observation.method ||
          entry.expected !== observation.expected ||
          entry.observed !== observation.observed ||
          entry.outcome !== observation.outcome,
      )
    ) {
      fail('journal-observation-shape');
    }
    return;
  }
  const intent = intents[0];
  if (events[0] !== intent) fail('journal-action-shape');
  const readbacks = events.filter((entry) => entry.kind === 'readback');
  const starts = events.filter((entry) => entry.kind === 'wire-start');
  const results = events.filter((entry) => entry.kind === 'wire-result');
  const completion = completions[0];
  const exactCompletion = (readback, outcome) =>
    completion &&
    completion.target === readback.target &&
    completion.method === intent.method &&
    completion.expected === readback.expected &&
    completion.observed === readback.observed &&
    completion.outcome === outcome;

  if (intent.method === 'GET') {
    if (
      starts.length !== 0 ||
      results.length !== 0 ||
      events.some((entry) => !['intent', 'readback', 'action-complete'].includes(entry.kind)) ||
      readbacks.some(
        (entry) =>
          entry.target !== intent.target ||
          entry.method !== 'GET' ||
          entry.expected !== intent.expected ||
          !['exact-readback', 'uncertain-read-error', 'definitive-read-error'].includes(
            entry.outcome,
          ),
      )
    ) {
      fail('journal-readback-shape');
    }
    if (!complete) return;
    const terminal = events.at(-2);
    if (
      terminal?.kind !== 'readback' ||
      terminal.outcome !== 'exact-readback' ||
      terminal.observed !== intent.expected ||
      !exactCompletion(terminal, 'exact-readback')
    ) {
      fail('journal-completion-binding');
    }
    return;
  }

  if (
    !['POST', 'PATCH', 'DELETE'].includes(intent.method) ||
    !intent.detail ||
    json(Object.keys(intent.detail)) !== json(['old']) ||
    events.some(
      (entry) =>
        !['intent', 'wire-start', 'wire-result', 'readback', 'action-complete'].includes(
          entry.kind,
        ),
    ) ||
    readbacks.some(
      (entry) =>
        entry.target !== intent.target ||
        entry.method !== 'GET' ||
        entry.expected !== intent.expected ||
        ![
          'pre-mutation-reconcile',
          'post-mutation-readback',
          'uncertain-readback',
          'uncertain-read-error',
          'definitive-read-error',
        ].includes(entry.outcome),
    ) ||
    starts.length > 2 ||
    results.length > starts.length ||
    starts.some((entry, index) => {
      const previous = events[events.indexOf(entry) - 1];
      return (
        entry.attempt !== index + 1 ||
        entry.target !== intent.target ||
        entry.method !== intent.method ||
        entry.expected !== intent.expected ||
        entry.outcome !== 'wire-started' ||
        previous?.kind !== 'readback' ||
        previous.outcome !== 'pre-mutation-reconcile' ||
        previous.observed !== intent.detail.old ||
        entry.observed !== intent.detail.old
      );
    }) ||
    results.some((entry, index) => {
      const start = starts[index];
      const previous = events[events.indexOf(entry) - 1];
      return (
        !start ||
        entry.attempt !== start.attempt ||
        entry.target !== intent.target ||
        entry.method !== intent.method ||
        entry.expected !== intent.expected ||
        !['definitive-response', 'uncertain-mutation', 'definitive-mutation-error'].includes(
          entry.outcome,
        ) ||
        previous !== start
      );
    })
  ) {
    fail('journal-wire-shape');
  }
  if (!complete || !completion || results.length !== starts.length || readbacks.length === 0) {
    return;
  }
  const terminal = events.at(-2);
  if (terminal?.kind !== 'readback' || terminal.observed !== intent.expected) {
    fail('journal-completion-binding');
  }
  if (
    completion.outcome === 'reconciled-expected' &&
    starts.length === 0 &&
    terminal.outcome === 'pre-mutation-reconcile' &&
    exactCompletion(terminal, 'reconciled-expected')
  ) {
    return;
  }
  if (
    completion.outcome === 'exact-readback' &&
    starts.length >= 1 &&
    terminal.outcome === 'post-mutation-readback' &&
    results.at(-1)?.outcome === 'definitive-response' &&
    results.slice(0, -1).every((entry) => entry.outcome === 'uncertain-mutation') &&
    exactCompletion(terminal, 'exact-readback')
  ) {
    return;
  }
  if (
    completion.outcome === 'uncertain-reconciled' &&
    starts.length === 1 &&
    terminal.outcome === 'uncertain-readback' &&
    results[0]?.outcome === 'uncertain-mutation' &&
    exactCompletion(terminal, 'uncertain-reconciled')
  ) {
    return;
  }
  fail('journal-mutation-shape');
}
function validatePhaseMarker(entry, kind) {
  const expectedOutcome = kind === 'phase-start' ? 'phase-started' : 'phase-complete';
  if (
    entry.action !== entry.phase ||
    entry.attempt !== null ||
    entry.target !== null ||
    entry.method !== null ||
    entry.expected !== null ||
    entry.observed !== null ||
    entry.outcome !== expectedOutcome ||
    entry.detail !== null
  ) {
    fail('journal-phase-marker');
  }
}
function validateJournalOrder(entries) {
  const completed = [];
  let active = null;
  let actionIndex = 0;
  let events = [];
  for (const entry of entries) {
    if (entry.kind === 'phase-start') {
      validatePhaseMarker(entry, 'phase-start');
      if (
        active !== null ||
        entry.action !== entry.phase ||
        entry.phase !== ENROLLMENT_CONTRACT.phases[completed.length]
      ) {
        fail('journal-phase-order');
      }
      active = entry.phase;
      actionIndex = 0;
      events = [];
      continue;
    }
    if (entry.phase !== active) {
      fail('journal-phase-order');
    }
    if (entry.kind === 'phase-complete') {
      validatePhaseMarker(entry, 'phase-complete');
      if (
        entry.action !== entry.phase ||
        events.length !== 0 ||
        actionIndex !== phaseActions[entry.phase].length
      ) {
        fail('journal-phase-incomplete');
      }
      completed.push(entry.phase);
      active = null;
      continue;
    }
    const expectedAction = phaseActions[entry.phase][actionIndex];
    if (entry.action !== expectedAction) fail('journal-action-order');
    events.push(entry);
    if (entry.kind === 'action-complete') {
      validateActionEvents(events, true);
      actionIndex += 1;
      events = [];
    } else {
      validateActionEvents(events, false);
    }
  }
  return { completed, active };
}
export function readEnrollmentJournal({ directory, record }) {
  const expectedRecord = recordSha256(record);
  if (!fs.existsSync(directory)) return [];
  if (!fs.lstatSync(directory).isDirectory()) fail('journal-directory');
  const directoryEntries = fs.readdirSync(directory, { withFileTypes: true });
  if (directoryEntries.some((entry) => !entry.isFile())) fail('journal-extra-entry');
  const names = directoryEntries.map((entry) => entry.name).sort();
  if (names.some((name) => !/^\d{6}-[a-z0-9-]+-[a-z0-9-]+\.json$/u.test(name))) {
    fail('journal-extra-entry');
  }
  let previous = '0'.repeat(64);
  const entries = names.map((name, index) => {
    const text = fs.readFileSync(path.join(directory, name), 'utf8');
    let fragment;
    try {
      fragment = JSON.parse(text);
    } catch {
      fail('journal-json');
    }
    validateFragment(fragment, index, previous, expectedRecord, name, text);
    previous = hash(text);
    return Object.freeze(fragment);
  });
  validateJournalOrder(entries);
  return entries;
}
function appendJournalFragment({ directory, record, fragment }) {
  const entries = readEnrollmentJournal({ directory, record });
  fs.mkdirSync(directory, { recursive: true });
  const previous = entries.length === 0 ? '0'.repeat(64) : hash(`${json(entries.at(-1))}\n`);
  const complete = {
    ordinal: entries.length + 1,
    kind: fragment.kind,
    phase: fragment.phase,
    action: fragment.action,
    attempt: fragment.attempt ?? null,
    target: fragment.target ?? null,
    method: fragment.method ?? null,
    expected: fragment.expected ?? null,
    observed: fragment.observed ?? null,
    outcome: fragment.outcome,
    detail: fragment.detail ?? null,
    previous_sha256: previous,
    record_sha256: recordSha256(record),
  };
  const output = `${json(complete)}\n`;
  const destination = path.join(directory, journalFileName(complete));
  try {
    durableCreate(destination, output, path.dirname(directory));
  } catch {
    fail('journal-create-new');
  }
  return Object.freeze(complete);
}
function phaseEntries(directory, record, phase) {
  return readEnrollmentJournal({ directory, record }).filter((entry) => entry.phase === phase);
}
function actionEntries(directory, record, phase, action) {
  return phaseEntries(directory, record, phase).filter((entry) => entry.action === action);
}

const observationKeys = Object.freeze(['name', 'run_id']);
function observationMap(record, observations) {
  if (!Array.isArray(observations)) fail('observations');
  const plans = new Map(record.role_plan.map((entry) => [entry.name, entry]));
  const result = new Map();
  const runIds = new Set();
  for (const entry of observations) {
    keys(entry, observationKeys, 'observation-keys');
    const plan = plans.get(entry.name);
    if (
      !plan ||
      result.has(entry.name) ||
      !/^[1-9][0-9]*$/u.test(entry.run_id) ||
      runIds.has(entry.run_id)
    ) {
      fail('observation-binding');
    }
    runIds.add(entry.run_id);
    result.set(entry.name, Object.freeze({ plan, run_id: entry.run_id }));
  }
  return result;
}
function completed(entries) {
  return entries.some((entry) => entry.kind === 'action-complete');
}
function appendActionComplete({
  directory,
  record,
  phase,
  action,
  target,
  method,
  expected,
  observed,
  outcome,
}) {
  return appendJournalFragment({
    directory,
    record,
    fragment: {
      kind: 'action-complete',
      phase,
      action,
      target,
      method,
      expected,
      observed,
      outcome,
    },
  });
}
async function readRemote({
  directory,
  record,
  phase,
  action,
  transport,
  target,
  expected,
  outcome,
}) {
  try {
    const value = await transport.read(target);
    appendJournalFragment({
      directory,
      record,
      fragment: {
        kind: 'readback',
        phase,
        action,
        target,
        method: 'GET',
        expected,
        observed: value,
        outcome,
      },
    });
    return value;
  } catch (error) {
    appendJournalFragment({
      directory,
      record,
      fragment: {
        kind: 'readback',
        phase,
        action,
        target,
        method: 'GET',
        expected,
        observed: null,
        outcome: uncertain(error) ? 'uncertain-read-error' : 'definitive-read-error',
        detail: {
          status: Number.isSafeInteger(error?.status) ? error.status : null,
          cause_code: typeof error?.causeCode === 'string' ? error.causeCode : null,
        },
      },
    });
    throw error;
  }
}
async function runReadAction({ directory, record, phase, action, transport, target, expected }) {
  let entries = actionEntries(directory, record, phase, action);
  if (completed(entries)) return;
  const intent = entries.find((entry) => entry.kind === 'intent');
  if (
    intent &&
    (intent.target !== target || intent.method !== 'GET' || intent.expected !== expected)
  ) {
    fail('journal-intent-binding');
  }
  if (!intent) {
    appendJournalFragment({
      directory,
      record,
      fragment: {
        kind: 'intent',
        phase,
        action,
        target,
        method: 'GET',
        expected,
        outcome: 'read-intent',
      },
    });
  }
  const observed = await readRemote({
    directory,
    record,
    phase,
    action,
    transport,
    target,
    expected,
    outcome: 'exact-readback',
  });
  if (observed !== expected) fail(`readback-${action}`);
  appendActionComplete({
    directory,
    record,
    phase,
    action,
    target,
    method: 'GET',
    expected,
    observed,
    outcome: 'exact-readback',
  });
}
async function runMutationAction({
  directory,
  record,
  phase,
  action,
  transport,
  method,
  target,
  body,
  old,
  expected,
}) {
  let entries = actionEntries(directory, record, phase, action);
  if (completed(entries)) return;
  const intent = entries.find((entry) => entry.kind === 'intent');
  if (
    intent &&
    (intent.target !== target ||
      intent.method !== method ||
      intent.expected !== expected ||
      json(intent.detail) !== json({ old }))
  ) {
    fail('journal-intent-binding');
  }
  if (!intent) {
    appendJournalFragment({
      directory,
      record,
      fragment: {
        kind: 'intent',
        phase,
        action,
        target,
        method,
        expected,
        outcome: 'mutation-intent',
        detail: { old },
      },
    });
  }
  for (;;) {
    entries = actionEntries(directory, record, phase, action);
    const current = await readRemote({
      directory,
      record,
      phase,
      action,
      transport,
      target,
      expected,
      outcome: 'pre-mutation-reconcile',
    });
    if (current === expected) {
      appendActionComplete({
        directory,
        record,
        phase,
        action,
        target,
        method,
        expected,
        observed: current,
        outcome: 'reconciled-expected',
      });
      return;
    }
    if (current !== old) fail(`mutation-old-${action}`);
    const starts = entries.filter((entry) => entry.kind === 'wire-start');
    const results = entries.filter((entry) => entry.kind === 'wire-result');
    const lastStart = starts.at(-1);
    const lastResult = results.at(-1);
    const priorUncertain =
      lastStart &&
      (!lastResult ||
        lastResult.ordinal < lastStart.ordinal ||
        lastResult.outcome === 'uncertain-mutation');
    if (starts.length >= 2 || (starts.length === 1 && !priorUncertain)) {
      fail(`mutation-retry-${action}`);
    }
    const attempt = starts.length + 1;
    appendJournalFragment({
      directory,
      record,
      fragment: {
        kind: 'wire-start',
        phase,
        action,
        attempt,
        target,
        method,
        expected,
        observed: current,
        outcome: 'wire-started',
      },
    });
    let response = null;
    let mutationUncertain = false;
    try {
      response = await transport.mutate(method, target, body);
      appendJournalFragment({
        directory,
        record,
        fragment: {
          kind: 'wire-result',
          phase,
          action,
          attempt,
          target,
          method,
          expected,
          observed: response,
          outcome: 'definitive-response',
        },
      });
      if (response !== null && response !== expected) fail(`mutation-response-${action}`);
    } catch (error) {
      mutationUncertain = uncertain(error);
      appendJournalFragment({
        directory,
        record,
        fragment: {
          kind: 'wire-result',
          phase,
          action,
          attempt,
          target,
          method,
          expected,
          observed: null,
          outcome: mutationUncertain ? 'uncertain-mutation' : 'definitive-mutation-error',
          detail: {
            status: Number.isSafeInteger(error?.status) ? error.status : null,
            cause_code: typeof error?.causeCode === 'string' ? error.causeCode : null,
          },
        },
      });
      if (!mutationUncertain) throw error;
    }
    const observed = await readRemote({
      directory,
      record,
      phase,
      action,
      transport,
      target,
      expected,
      outcome: mutationUncertain ? 'uncertain-readback' : 'post-mutation-readback',
    });
    if (observed === expected) {
      appendActionComplete({
        directory,
        record,
        phase,
        action,
        target,
        method,
        expected,
        observed,
        outcome: mutationUncertain ? 'uncertain-reconciled' : 'exact-readback',
      });
      return;
    }
    if (!(mutationUncertain && observed === old && attempt === 1)) {
      fail(`mutation-readback-${action}`);
    }
  }
}
function assertObservationMaterializer(materializer) {
  if (
    !materializer ||
    typeof materializer.materialize !== 'function' ||
    typeof materializer.validate !== 'function' ||
    typeof materializer.destination_identity_sha256 !== 'string' ||
    !hex64.test(materializer.destination_identity_sha256)
  ) {
    fail('observation-materializer');
  }
}
function expectedObservation(record, locator) {
  return Object.freeze({
    operation_id: locator.plan.operation_id,
    role: locator.plan.name,
    run_id: locator.run_id,
    expected_event: locator.plan.event,
    // GitHub's run transport executes the trusted workflow from main. The fixture head remains
    // independently bound by the manifest/ref suffix and exact run title.
    expected_run_head_sha: locator.plan.workflow_sha,
  });
}
function validateMaterializedObservation(materializer, materialized, expected) {
  keys(materialized, ['receipt', 'sha256'], 'observation-materialized-shape');
  const validated = materializer.validate(materialized.receipt, expected);
  keys(validated, ['receipt', 'sha256'], 'observation-validated-shape');
  if (
    validated.receipt !== materialized.receipt ||
    validated.sha256 !== materialized.sha256 ||
    validated.sha256 !== hash(json(validated.receipt))
  ) {
    fail('observation-materialized-binding');
  }
  return validated;
}
function validateJournalObservations({ directory, record, materializer }) {
  const plans = new Map(record.role_plan.map((entry) => [entry.name, entry]));
  const runIds = new Set();
  const journal = readEnrollmentJournal({ directory, record });
  const completedRoleActions = new Set(
    journal
      .filter(
        (fragment) =>
          fragment.kind === 'action-complete' && roleObservationActions.has(fragment.action),
      )
      .map((fragment) => fragment.action),
  );
  const observations = journal.filter((fragment) => fragment.kind === 'observation');
  for (const action of completedRoleActions) {
    if (observations.filter((entry) => entry.action === action).length !== 1) {
      fail('observation-journal-topology');
    }
  }
  for (const entry of observations) {
    const plan = plans.get(entry.action);
    const runId = String(entry.detail?.run_id ?? '');
    if (!plan || !/^[1-9][0-9]*$/u.test(runId) || runIds.has(runId)) {
      fail('observation-journal-binding');
    }
    const validated = materializer.validate(
      entry.detail,
      expectedObservation(record, { plan, run_id: runId }),
    );
    if (
      validated.receipt !== entry.detail ||
      validated.sha256 !== entry.expected ||
      entry.observed !== entry.expected
    ) {
      fail('observation-journal-authority');
    }
    runIds.add(runId);
  }
}
function runObservationAction({
  directory,
  record,
  phase,
  action,
  observations,
  observationMaterializer,
}) {
  const entries = actionEntries(directory, record, phase, action);
  if (completed(entries)) return;
  const locator = observations.get(action);
  if (!locator) fail(`observation-missing-${action}`);
  const expected = expectedObservation(record, locator);
  const priorObservations = readEnrollmentJournal({ directory, record })
    .filter((entry) => entry.kind === 'observation' && entry.action !== action)
    .map((entry) => entry.detail);
  if (priorObservations.some((entry) => entry.run_id === locator.run_id)) {
    fail('observation-run-replay');
  }
  const prior = entries.find((entry) => entry.kind === 'observation');
  let observation;
  let observationSha256;
  if (prior) {
    const validated = observationMaterializer.validate(prior.detail, expected);
    if (validated.receipt !== prior.detail || validated.sha256 !== prior.expected) {
      fail('observation-replay');
    }
    observation = validated.receipt;
    observationSha256 = validated.sha256;
  } else {
    const materialized = validateMaterializedObservation(
      observationMaterializer,
      observationMaterializer.materialize(expected),
      expected,
    );
    observation = materialized.receipt;
    observationSha256 = materialized.sha256;
  }
  if (!prior) {
    appendJournalFragment({
      directory,
      record,
      fragment: {
        kind: 'observation',
        phase,
        action,
        target: `workflow-run:${locator.run_id}`,
        method: 'GET',
        expected: observationSha256,
        observed: observationSha256,
        outcome: 'exact-external-readback',
        detail: observation,
      },
    });
  }
  appendActionComplete({
    directory,
    record,
    phase,
    action,
    target: `workflow-run:${locator.run_id}`,
    method: 'GET',
    expected: observationSha256,
    observed: observationSha256,
    outcome: 'exact-external-readback',
  });
}

function assertTransport(transport) {
  if (
    !transport ||
    typeof transport.read !== 'function' ||
    typeof transport.mutate !== 'function'
  ) {
    fail('transport');
  }
}
function startPhase({ directory, record, phase }) {
  const entries = readEnrollmentJournal({ directory, record });
  const state = validateJournalOrder(entries);
  const expected = ENROLLMENT_CONTRACT.phases[state.completed.length];
  if (phase !== expected || (state.active !== null && state.active !== phase)) {
    fail('phase-order');
  }
  if (state.active === null) {
    appendJournalFragment({
      directory,
      record,
      fragment: {
        kind: 'phase-start',
        phase,
        action: phase,
        outcome: 'phase-started',
      },
    });
  }
}
function completePhase({ directory, record, phase }) {
  appendJournalFragment({
    directory,
    record,
    fragment: {
      kind: 'phase-complete',
      phase,
      action: phase,
      outcome: 'phase-complete',
    },
  });
  return readEnrollmentJournal({ directory, record }).at(-1);
}

/** Execute exactly one recoverable phase. No direct non-journaled mutation API exists. */
async function executeLockedEnrollmentPhase({
  directory,
  record,
  phase,
  transport,
  observations = [],
  observationMaterializer,
}) {
  validateEnrollmentRecord(record);
  assertTransport(transport);
  assertObservationMaterializer(observationMaterializer);
  if (typeof directory !== 'string' || directory.length === 0) fail('journal-directory');
  if (!ENROLLMENT_CONTRACT.phases.includes(phase)) fail('phase');
  const observedRuns = observationMap(record, observations);
  validateJournalObservations({ directory, record, materializer: observationMaterializer });
  startPhase({ directory, record, phase });
  const variable = variableTarget();
  const normal = record.fixtures.normal;
  const stale = record.fixtures.stale;
  const read = (action, target, expected) =>
    runReadAction({ directory, record, phase, action, transport, target, expected });
  const mutate = (action, method, target, body, old, expected) =>
    runMutationAction({
      directory,
      record,
      phase,
      action,
      transport,
      method,
      target,
      body,
      old,
      expected,
    });
  const observe = (action) =>
    runObservationAction({
      directory,
      record,
      phase,
      action,
      observations: observedRuns,
      observationMaterializer,
    });

  if (phase === 'prepare') {
    await read(
      'read-merged-commit-tree',
      commitTreeTarget(record.coordinates.workflow_sha),
      json({
        commit_sha: record.coordinates.workflow_sha,
        tree_sha: record.coordinates.workflow_tree_sha,
      }),
    );
    await read(
      'read-merged-tree',
      `object:tree:${record.coordinates.workflow_tree_sha}`,
      record.coordinates.workflow_tree_sha,
    );
    for (const name of [
      'initial_blob',
      'advanced_blob',
      'initial_tree',
      'advanced_tree',
      'normal_commit',
      'stale_commit',
      'advanced_commit',
    ]) {
      const object = record.objects[name];
      await mutate(
        `upload-${name.replace(/_/gu, '-')}`,
        'POST',
        objectTarget(object),
        object.body,
        null,
        object.sha,
      );
    }
  } else if (phase === 'refresh') {
    await read('baseline-variable-absent', variable, null);
    await read('read-stale-old-ref', `ref:${stale.ref}`, stale.old_head);
    await read('read-normal-old-ref', `ref:${normal.ref}`, normal.old_head);
    await mutate(
      'refresh-stale-ref',
      'PATCH',
      `ref:${stale.ref}`,
      { sha: stale.new_head, force: false },
      stale.old_head,
      stale.new_head,
    );
    await read(
      'read-stale-pull',
      `pull:${stale.pr_number}`,
      pullBinding(record, stale, stale.new_head),
    );
    await mutate(
      'refresh-normal-ref',
      'PATCH',
      `ref:${normal.ref}`,
      { sha: normal.new_head, force: false },
      normal.old_head,
      normal.new_head,
    );
    await read(
      'read-normal-pull',
      `pull:${normal.pr_number}`,
      pullBinding(record, normal, normal.new_head),
    );
  } else if (phase === 'authorize-normal') {
    await read('normal-variable-still-absent', variable, null);
    await mutate(
      'write-normal-manifest',
      'POST',
      variable,
      { name: ENROLLMENT_CONTRACT.variable, value: normal.manifest },
      null,
      normal.manifest,
    );
  } else if (phase === 'authorize-stale') {
    observe('normal-bootstrap');
    observe('normal-continuation');
    await read('normal-manifest-retained', variable, normal.manifest);
    await mutate(
      'replace-stale-manifest',
      'PATCH',
      variable,
      { name: ENROLLMENT_CONTRACT.variable, value: stale.manifest },
      normal.manifest,
      stale.manifest,
    );
    await read('stale-manifest-before-advance', variable, stale.manifest);
  } else if (phase === 'advance-stale') {
    observe('stale-protected');
    // The C# producer journal is the only writer of this ref. This executor
    // observes the producer-owned advancement and never races it with a PATCH.
    await read('read-advanced-stale-ref', `ref:${stale.ref}`, stale.advanced_head);
    await read(
      'read-advanced-stale-pull',
      `pull:${stale.pr_number}`,
      pullBinding(record, stale, stale.advanced_head),
    );
    await read('stale-manifest-after-advance', variable, stale.manifest);
  } else {
    observe('stale-follow-on');
    await read('stale-manifest-before-cleanup', variable, stale.manifest);
    await mutate(
      'delete-authorization-variable',
      'DELETE',
      variable,
      undefined,
      stale.manifest,
      null,
    );
    await read('final-variable-absent', variable, null);
  }
  return completePhase({ directory, record, phase });
}

/** Hold one same-host/process-namespace lease for the entire phase and every GitHub mutation. */
export async function executeDurableEnrollmentPhase(argumentsObject) {
  const { directory, record, phase, transport, observationMaterializer } = argumentsObject;
  validateEnrollmentRecord(record);
  assertTransport(transport);
  assertObservationMaterializer(observationMaterializer);
  if (typeof directory !== 'string' || directory.length === 0) fail('journal-directory');
  if (!ENROLLMENT_CONTRACT.phases.includes(phase)) fail('phase');
  const lock = await acquirePhaseLock({ directory, record, phase });
  let result;
  let executionError;
  try {
    result = await executeLockedEnrollmentPhase(argumentsObject);
  } catch (error) {
    executionError = error;
  }
  try {
    releasePhaseLock(lock);
  } catch (releaseError) {
    // The business failure remains the actionable error. The failed release is
    // retained as its cause, while the owner registry makes its exact lease
    // reclaimable by a subsequent invocation of this process instance.
    if (executionError) {
      if (
        executionError &&
        typeof executionError === 'object' &&
        executionError.cause === undefined
      ) {
        executionError.cause = releaseError;
      }
    } else {
      throw releaseError;
    }
  }
  if (executionError) throw executionError;
  return result;
}

const cliFlags = Object.freeze([
  '--record',
  '--record-sha256',
  '--execution-authority',
  '--observation-context',
  '--journal-dir',
  '--phase',
  '--observations',
]);
function parseCli(argumentsList) {
  if (argumentsList.length === 0) return null;
  if (argumentsList.length !== 1 + cliFlags.length * 2 || argumentsList[0] !== '--execute') {
    fail('usage');
  }
  const values = new Map();
  for (let index = 1; index < argumentsList.length; index += 2) {
    const name = argumentsList[index];
    const value = argumentsList[index + 1];
    if (!cliFlags.includes(name) || values.has(name) || value.length === 0) fail('usage');
    values.set(name, value);
  }
  if (cliFlags.some((flag) => !values.has(flag))) fail('usage');
  return values;
}
function readCanonicalJson(file, code) {
  let text;
  let value;
  try {
    text = fs.readFileSync(file, 'utf8');
    value = JSON.parse(text);
  } catch {
    fail(code);
  }
  if (text !== `${json(value)}\n`) fail(`${code}-canonical`);
  return value;
}

/** No arguments are inert; only the exact --execute form constructs live GitHub transport. */
export async function runEnrollmentCli(argumentsList = process.argv.slice(2), adapters = {}) {
  const values = parseCli(argumentsList);
  if (values === null) return { usage: true };
  const record = readCanonicalJson(values.get('--record'), 'cli-record');
  const authorityPackage = readCanonicalJson(
    values.get('--execution-authority'),
    'cli-execution-authority',
  );
  const observationContext = readCanonicalJson(
    values.get('--observation-context'),
    'cli-observation-context',
  );
  const observations = readCanonicalJson(values.get('--observations'), 'cli-observations');
  const expectedRecordSha = values.get('--record-sha256');
  if (!hex64.test(expectedRecordSha) || recordSha256(record) !== expectedRecordSha) {
    fail('cli-record-sha256');
  }
  let authority;
  try {
    authority = validateEnrollmentExecutionAuthorityPackage(authorityPackage);
  } catch {
    fail('cli-execution-authority');
  }
  if (json(authority) !== json(record.authority)) fail('cli-execution-authority-binding');
  const observationMaterializer =
    adapters.observationMaterializer ??
    createEnrollmentObservationMaterializer({
      ...observationContext,
      authority,
      run_process: adapters.runObservationProcess ?? defaultEnrollmentObservationProcess,
    });
  const transport =
    adapters.transport ??
    (adapters.createTransport ?? createGhTransport)({
      repository: record.coordinates.repository,
    });
  return executeDurableEnrollmentPhase({
    directory: values.get('--journal-dir'),
    record,
    phase: values.get('--phase'),
    transport,
    observations,
    observationMaterializer,
  });
}

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(import.meta.filename)) {
  runEnrollmentCli()
    .then((result) => {
      if (result.usage) {
        process.stdout.write(
          'usage: --execute --record <canonical-B> --record-sha256 <sha256> --execution-authority <canonical-host-restricted-package> --observation-context <canonical-private-context> --journal-dir <dir> --phase <next> --observations <canonical-run-locators>\n',
        );
      }
    })
    .catch((error) => {
      process.stderr.write(
        `${error instanceof Error ? error.message : 'APR_R4_TRUSTED_PROOF_ENROLLMENT_INVALID'}\n`,
      );
      process.exitCode = 1;
    });
}
