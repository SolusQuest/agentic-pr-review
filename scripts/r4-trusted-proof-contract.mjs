import crypto from 'node:crypto';

export const expectedProductRoles = Object.freeze([
  'repository-locator-root',
  'normal-lineage-head',
  'stale-lineage-head',
  'bootstrap-candidate',
  'continuation-candidate',
  'bootstrap-acceptance',
  'continuation-acceptance',
]);

export const cleanupPhases = Object.freeze([
  'settle-runs',
  'remove-proof-control',
  'delete-observed-state',
  'enumerate-empty-state',
  'remove-authorization-and-secrets',
  'restore-environment',
  'retire-fixtures',
  'read-back-sticky',
  'remove-local-credentials',
  'finalize-private-manifest',
]);

const expectedProductRoleShape = Object.freeze([
  ['repository-locator-root', 'repository', 'locator_root'],
  ['normal-lineage-head', 'normal', 'lineage_head'],
  ['stale-lineage-head', 'stale', 'lineage_head'],
  ['bootstrap-candidate', 'normal', 'candidate'],
  ['continuation-candidate', 'normal', 'candidate'],
  ['bootstrap-acceptance', 'normal', 'acceptance'],
  ['continuation-acceptance', 'normal', 'acceptance'],
]);

const expectedCleanupShape = Object.freeze([
  ['locator_root', 'repository', 0],
  ['lineage_head', 'normal', 0],
  ['lineage_head', 'stale', 1],
  ['candidate', 'normal', 0],
  ['candidate', 'normal', 0],
  ['acceptance', 'normal', 0],
  ['acceptance', 'normal', 0],
  ['publication_intent', 'normal', 0],
  ['publication_intent', 'normal', 0],
  ['publication_intent', 'normal', 0],
  ['publication_intent', 'normal', 0],
  ['cleanup', 'normal', 0],
  ['cleanup', 'normal', 0],
  ['cleanup', 'normal', 0],
  ['cleanup', 'normal', 0],
]);

const hex40 = /^[0-9a-f]{40}$/u;
const hex64 = /^[0-9a-f]{64}$/u;
const decimal = /^[1-9][0-9]*$/u;
const fixtureRef = /^refs\/heads\/r4-trusted-proof\/[0-9a-f]{64}$/u;
const sourceKinds = new Set([
  'github-rest',
  'github-ui-attestation',
  'descendant-receipt',
  'production-codec',
  'cleanup-readback',
  'public-leak-scan',
  'pinned-code-order',
  'durable-maintainer-authorization',
  'github-rest-and-ui-attestation',
  'multi-source-identity',
  'production-derived',
  'package-inputs',
  'post-cleanup-capture',
  'public-candidate-scan',
  'independent-package-manifest',
]);

function invalid(code) {
  throw new Error(`APR_R4_E3_EVIDENCE_INVALID ${code}`);
}

function proofControlPhase(host, comment) {
  return comment.operation_id === host.identities.operation_ids[0] ? 'bootstrap' : 'stale';
}

function proofControlCommentSourceId(host, comment) {
  return `proof-control-${proofControlPhase(host, comment)}-comment-${comment.comment_id}:page:1`;
}

function proofControlPermissionSourceId(host, comment) {
  return `proof-control-${proofControlPhase(host, comment)}-permission-${comment.comment_id}-maintainer:page:1`;
}

export function sha256(value) {
  return crypto.createHash('sha256').update(value).digest('hex');
}

export function canonicalJson(value) {
  return `${JSON.stringify(value)}\n`;
}

function exactKeys(value, keys, code) {
  if (
    value === null ||
    Array.isArray(value) ||
    typeof value !== 'object' ||
    JSON.stringify(Object.keys(value)) !== JSON.stringify(keys)
  ) {
    invalid(code);
  }
}

function exactArray(value, expected, code) {
  if (!Array.isArray(value) || JSON.stringify(value) !== JSON.stringify(expected)) invalid(code);
}

function interval(value, code) {
  exactKeys(value, ['request_started', 'response_received'], code);
  if (
    !Number.isSafeInteger(value.request_started) ||
    !Number.isSafeInteger(value.response_received) ||
    value.request_started < 0 ||
    value.response_received < value.request_started
  ) {
    invalid(code);
  }
}

function sourceTimestamp(value, code) {
  exactKeys(value, ['kind', 'value'], code);
  if (value.kind !== 'source-emitted' || !Number.isSafeInteger(value.value) || value.value < 0) {
    invalid(code);
  }
}

function expectedCaptureSourcePhase(sourceId, operationId, operationIds) {
  const family = sourceId.replace(/:page:[1-9][0-9]*$/u, '');
  let match = /^authorization-(setup|execution)-(?:comment|permission)-(normal|stale)-/u.exec(
    family,
  );
  if (match) return `baseline-${match[2]}`;
  match = /^(baseline-(?:normal|stale))-/u.exec(family);
  if (match) return match[1];
  match = /^readiness-(bootstrap|continuation|stale)-/u.exec(family);
  if (match) return `${match[1]}-readiness`;
  match = /^transition-(bootstrap|continuation|stale)-(pending|approvals|jobs)-/u.exec(family);
  if (match) return `${match[1]}-${match[2] === 'approvals' ? 'approval' : match[2]}`;
  match = /^proof-control-comments-(normal|stale)-pr-/u.exec(family);
  if (match) return `terminal-${match[1]}`;
  match = /^proof-control-(bootstrap|stale)-(?:comment|permission)-/u.exec(family);
  if (match) return `${match[1]}-approval`;
  match = /^concurrency-(normal|stale)-run-/u.exec(family);
  if (match) return match[1] === 'normal' ? 'bootstrap-concurrency' : 'stale-concurrency';
  if (family === 'producer-discovery-final') return 'producer-discovery';
  if (/^(?:artifacts-run|run-terminal)-[1-9][0-9]*$/u.test(family)) {
    return operationId === operationIds[1] ? 'terminal-stale' : 'terminal-normal';
  }
  return null;
}

function validateSourceMap(value) {
  exactKeys(value, ['kind', 'api_version', 'entries'], 'source-map-shape');
  if (
    value.kind !== 'apr-r4-e3-source-map-v1' ||
    value.api_version !== '2026-03-10' ||
    !Array.isArray(value.entries)
  ) {
    invalid('source-map-values');
  }
  const expected = [
    [
      '/identities',
      'multi-source-identity',
      'identity-inputs',
      'receipt-capture-oracle',
      'none',
      'payload receipt, execution authorization, capture manifest, independent oracle build receipt, pinned oracle/runtime binaries and oracle result',
    ],
    [
      '/authorizations',
      'durable-maintainer-authorization',
      'authorization-readbacks',
      'paired-baseline-comments-permissions-and-post-plan-cleanup-readback',
      'none',
      'paired setup/execution baseline phase fragments and checked post-plan cleanup authorization readback',
    ],
    [
      '/environment',
      'github-rest',
      'environment-readiness',
      'environment-branch-policies-environment-secrets',
      'complete-cursor',
      'environment, deployment branch policies, and environment secret inventory',
    ],
    [
      '/approval_transitions',
      'github-rest',
      'deployment-transitions',
      'pending-approvals-jobs',
      'none',
      'three exact run transitions',
    ],
    [
      '/concurrency',
      'github-rest',
      'concurrency-groups',
      'active-repository-concurrency-group',
      'none',
      'normal and stale active group_members and ahead_of_run',
    ],
    [
      '/proof_control',
      'github-rest',
      'proof-control-comments',
      'issue-comments',
      'none',
      'six exact comment readbacks',
    ],
    [
      '/inventories',
      'production-codec',
      'production-inventory',
      'artifact-metadata-and-codec',
      'complete-cursor',
      'all authenticated artifacts',
    ],
    [
      '/cleanup',
      'post-cleanup-capture',
      'post-cleanup-readbacks',
      'exact-cleanup-target-get-readbacks',
      'complete-cursor',
      'cleanup execution journal, plan, post-cleanup capture manifest, and phase-specific raw readbacks',
    ],
    [
      '/canaries/live',
      'production-derived',
      'live-observations',
      'capture-proof-control-and-codec',
      'none',
      'derived GitHub, provider and state route observations',
    ],
    [
      '/canaries/cross_sink',
      'descendant-receipt',
      'cross-sink-canary',
      'checked-receipt',
      'none',
      'receipt isolation proof',
    ],
    [
      '/canaries/public_leak_scan',
      'public-candidate-scan',
      'public-candidate-scan',
      'candidate-repository-worktree-log-corpus',
      'complete-enumeration',
      'actual candidate digest plus enumerated public-surface corpus',
    ],
    [
      '/restricted_package',
      'independent-package-manifest',
      'restricted-package-inputs',
      'capture-oracle-binaries-scan-and-manifest-readback',
      'none',
      'pinned package inputs and finalized private-manifest readback',
    ],
  ];
  const observed = value.entries.map((entry) => {
    exactKeys(
      entry,
      [
        'destination_pointer',
        'source_kind',
        'source_id',
        'endpoint_family',
        'pagination',
        'source_pointer_or_file',
        'derivation',
        'source_contract_sha256',
      ],
      'source-map-entry-shape',
    );
    if (
      !sourceKinds.has(entry.source_kind) ||
      typeof entry.source_id !== 'string' ||
      entry.source_id.length === 0 ||
      !['none', 'complete-cursor', 'complete-enumeration'].includes(entry.pagination) ||
      typeof entry.endpoint_family !== 'string' ||
      typeof entry.source_pointer_or_file !== 'string' ||
      entry.derivation !== 'source-derived-canonical' ||
      !hex64.test(entry.source_contract_sha256)
    ) {
      invalid('source-map-source');
    }
    return [
      entry.destination_pointer,
      entry.source_kind,
      entry.source_id,
      entry.endpoint_family,
      entry.pagination,
      entry.source_pointer_or_file,
    ];
  });
  if (JSON.stringify(observed) !== JSON.stringify(expected)) invalid('source-map-coverage');
}

function validateSourceBindings(host) {
  for (const entry of host.source_map.entries) {
    const segments = entry.destination_pointer.split('/').slice(1);
    const value = segments.reduce((current, segment) => current?.[segment], host);
    const contract = Object.fromEntries(
      Object.entries(entry).filter(([name]) => name !== 'source_contract_sha256'),
    );
    if (value === undefined || sha256(canonicalJson(contract)) !== entry.source_contract_sha256) {
      invalid('source-map-contract-digest');
    }
  }
}

function validateSourceBundle(sourceMap, sourceBundle) {
  validateSourceMap(sourceMap);
  exactKeys(sourceBundle, ['kind', 'source_map_sha256', 'documents'], 'source-bundle-shape');
  if (
    sourceBundle.kind !== 'apr-r4-e3-closed-source-bundle-v1' ||
    sourceBundle.source_map_sha256 !== sha256(canonicalJson(sourceMap)) ||
    !Array.isArray(sourceBundle.documents) ||
    sourceBundle.documents.length !== sourceMap.entries.length
  ) {
    invalid('source-bundle-values');
  }

  for (let index = 0; index < sourceMap.entries.length; index += 1) {
    const expected = sourceMap.entries[index];
    const document = sourceBundle.documents[index];
    exactKeys(
      document,
      ['source_id', 'destination_pointer', 'source_contract_sha256', 'evidence'],
      'source-bundle-document-shape',
    );
    if (
      document.source_id !== expected.source_id ||
      document.destination_pointer !== expected.destination_pointer ||
      document.source_contract_sha256 !== expected.source_contract_sha256
    ) {
      invalid('source-bundle-document-binding');
    }
    exactKeys(
      document.evidence,
      ['kind', 'references', 'set_sha256'],
      'source-bundle-evidence-shape',
    );
    if (
      document.evidence.kind !== expected.source_kind ||
      !Array.isArray(document.evidence.references) ||
      document.evidence.references.length === 0 ||
      document.evidence.set_sha256 !==
        sha256(
          canonicalJson({
            kind: document.evidence.kind,
            references: document.evidence.references,
          }),
        )
    ) {
      invalid('source-bundle-evidence-values');
    }
    const evidenceIds = new Set();
    for (const reference of document.evidence.references) {
      exactKeys(reference, ['source_id', 'sha256'], 'source-bundle-evidence-reference-shape');
      if (
        evidenceIds.has(reference.source_id) ||
        typeof reference.source_id !== 'string' ||
        reference.source_id.length === 0 ||
        !hex64.test(reference.sha256)
      ) {
        invalid('source-bundle-evidence-reference-values');
      }
      evidenceIds.add(reference.source_id);
    }
    if (
      JSON.stringify(document.evidence.references.map(({ source_id }) => source_id)) !==
      JSON.stringify([...evidenceIds].sort())
    ) {
      invalid('source-bundle-evidence-reference-order');
    }
  }
  return sourceBundle.documents;
}

function validateSourceEvidenceBindings(
  documents,
  captureManifest,
  captureManifestSha256,
  oracleResultSha256,
  retainedDocuments,
  host,
) {
  const captured = new Map(
    captureManifest.sources.map((source) => [source.source_id, source.body_sha256]),
  );
  const captureReference = (sourceId) => {
    const digest = captured.get(sourceId);
    if (digest === undefined) invalid('source-evidence-capture-missing');
    return { source_id: sourceId, sha256: digest };
  };
  const retainedReference = (sourceId) => {
    const value = retainedDocuments.get(sourceId);
    if (value === undefined) invalid('source-evidence-retained-missing');
    return { source_id: sourceId, sha256: sha256(canonicalJson(value)) };
  };
  const authorizationReferences = ['execution', 'setup']
    .flatMap((phase) => {
      const source = host.authorizations[phase].source;
      return ['normal', 'stale'].flatMap((scope) => {
        const comment = captureReference(
          `authorization-${phase}-comment-${scope}-${source.comment_id}:page:1`,
        );
        if (scope === 'normal' && comment.sha256 !== source.capture_body_sha256) {
          invalid('source-evidence-authorization-digest');
        }
        return [
          comment,
          captureReference(`authorization-${phase}-permission-${scope}-maintainer:page:1`),
        ];
      });
    })
    .concat(retainedReference('cleanup-authorization-readback'))
    .sort((a, b) => a.source_id.localeCompare(b.source_id));
  const approvalReferences = Object.entries(host.approval_transitions)
    .flatMap(([phase, transition]) => [
      captureReference(`transition-${phase}-approvals-run-${transition.run_id}:page:1`),
      captureReference(`transition-${phase}-pending-run-${transition.run_id}:page:1`),
      captureReference(`transition-${phase}-jobs-run-${transition.run_id}:page:1`),
    ])
    .sort((a, b) => a.source_id.localeCompare(b.source_id));
  const proofReferences = Object.values(host.proof_control)
    .flatMap((family) => family.comments)
    .flatMap((comment) => {
      const reference = captureReference(proofControlCommentSourceId(host, comment));
      if (reference.sha256 !== comment.capture_body_sha256) {
        invalid('source-evidence-proof-control-digest');
      }
      return comment.kind === 'release' || comment.kind === 'stale-release'
        ? [reference, captureReference(proofControlPermissionSourceId(host, comment))]
        : [reference];
    })
    .concat(
      captureManifest.sources
        .filter((source) =>
          /^proof-control-comments-(normal|stale)-pr-[1-9][0-9]*:page:[1-9][0-9]*$/u.test(
            source.source_id,
          ),
        )
        .map((source) => captureReference(source.source_id)),
    )
    .sort((a, b) => a.source_id.localeCompare(b.source_id));
  const receiptReference = {
    source_id: 'trusted-proof-payload-receipt-v2',
    sha256: '3556512b430867b41086938f55b6553f5f289fae3a1bb3a62d5755a01f9551e1',
  };
  const expected = new Map([
    [
      '/identities',
      [
        captureReference(
          `authorization-execution-comment-normal-${host.authorizations.execution.source.comment_id}:page:1`,
        ),
        { source_id: 'capture-manifest', sha256: captureManifestSha256 },
        retainedReference('correction-gate-receipt'),
        retainedReference('oracle-build-receipt'),
        { source_id: 'oracle-result', sha256: oracleResultSha256 },
        retainedReference('producer-journal-seal'),
        receiptReference,
      ].sort((a, b) => a.source_id.localeCompare(b.source_id)),
    ],
    ['/authorizations', authorizationReferences],
    [
      '/environment',
      [
        captureReference('readiness-stale-environment-branch-policies:page:1'),
        captureReference('readiness-stale-environment-protection:page:1'),
        captureReference('readiness-stale-environment-secret-inventory:page:1'),
      ].sort((a, b) => a.source_id.localeCompare(b.source_id)),
    ],
    ['/approval_transitions', approvalReferences],
    [
      '/concurrency',
      Object.entries(host.concurrency)
        .flatMap(([scope, concurrency]) => [
          captureReference(`concurrency-${scope}-run-${concurrency.terminal.holder_run_id}:page:1`),
          captureReference(`run-terminal-${concurrency.terminal.holder_run_id}:page:1`),
          captureReference(`run-terminal-${concurrency.terminal.waiter_run_id}:page:1`),
        ])
        .sort((a, b) => a.source_id.localeCompare(b.source_id)),
    ],
    ['/proof_control', proofReferences],
    ['/inventories', [{ source_id: 'production-codec-oracle-result', sha256: oracleResultSha256 }]],
    [
      '/cleanup',
      [
        retainedReference('cleanup-execution'),
        retainedReference('cleanup-plan'),
        retainedReference('post-cleanup-capture-manifest'),
      ],
    ],
    [
      '/canaries/live',
      [
        { source_id: 'capture-manifest', sha256: captureManifestSha256 },
        retainedReference('credential-admission-receipt'),
        retainedReference('credential-disposition-receipt'),
        { source_id: 'oracle-result', sha256: oracleResultSha256 },
        ...Object.values(host.proof_control)
          .flatMap((family) => family.comments)
          .filter((comment) => comment.kind === 'ready' || comment.kind === 'stale-ready')
          .map((comment) => captureReference(proofControlCommentSourceId(host, comment))),
      ].sort((a, b) => a.source_id.localeCompare(b.source_id)),
    ],
    ['/canaries/cross_sink', [receiptReference]],
    [
      '/canaries/public_leak_scan',
      [
        {
          source_id: 'public-leak-scan-result',
          sha256: retainedReference('public-leak-scan-result').sha256,
        },
      ],
    ],
    [
      '/restricted_package',
      [
        { source_id: 'capture-manifest', sha256: captureManifestSha256 },
        { source_id: 'oracle-result', sha256: oracleResultSha256 },
        retainedReference('restricted-package-readback'),
      ],
    ],
  ]);
  for (const document of documents) {
    const references = expected.get(document.destination_pointer);
    if (
      references === undefined ||
      JSON.stringify(document.evidence.references) !== JSON.stringify(references)
    ) {
      invalid('source-evidence-binding');
    }
  }
}

function validateEnvironmentDerivation(
  host,
  captureManifest,
  capturedSourceBodies,
  authorizations,
) {
  const read = (sourceId) => {
    const source = captureManifest.sources.find((candidate) => candidate.source_id === sourceId);
    const retained = capturedSourceBodies.get(sourceId);
    if (
      source === undefined ||
      retained === undefined ||
      typeof retained.text !== 'string' ||
      sha256(Buffer.from(retained.text, 'utf8')) !== source.body_sha256 ||
      String(Buffer.byteLength(retained.text, 'utf8')) !== source.body_size
    ) {
      invalid('environment-source-binding');
    }
    try {
      return { value: JSON.parse(retained.text), source };
    } catch {
      invalid('environment-source-json');
    }
  };
  const environment = read('readiness-stale-environment-protection:page:1');
  const branches = read('readiness-stale-environment-branch-policies:page:1');
  const secrets = read('readiness-stale-environment-secret-inventory:page:1');
  const response = environment.value;
  const reviewersRule = response.protection_rules?.find(
    (rule) => rule?.type === 'required_reviewers',
  );
  const branchPolicy = response.deployment_branch_policy;
  if (
    response.name !== 'r4-trusted-proof' ||
    String(response.id) !== authorizations.execution.environment_baseline.environment_id ||
    response.can_admins_bypass !== false ||
    !Array.isArray(response.protection_rules) ||
    reviewersRule === undefined ||
    reviewersRule.prevent_self_review !== false ||
    !Array.isArray(reviewersRule.reviewers) ||
    branchPolicy === null ||
    typeof branchPolicy !== 'object' ||
    branchPolicy.protected_branches !== false ||
    branchPolicy.custom_branch_policies !== true
  ) {
    invalid('environment-response-values');
  }
  exactKeys(branches.value, ['total_count', 'branch_policies'], 'environment-branches-shape');
  exactKeys(secrets.value, ['total_count', 'secrets'], 'environment-secrets-shape');
  if (
    !Number.isSafeInteger(branches.value.total_count) ||
    branches.value.total_count !== branches.value.branch_policies?.length ||
    branches.value.total_count !== 1 ||
    String(branches.value.branch_policies[0]?.id) !== '58463845' ||
    branches.value.branch_policies[0]?.name !== 'main' ||
    branches.value.branch_policies[0]?.type !== 'branch' ||
    !Number.isSafeInteger(secrets.value.total_count) ||
    secrets.value.total_count !== secrets.value.secrets?.length
  ) {
    invalid('environment-inventory-values');
  }
  const unorderedSecretNames = secrets.value.secrets.map((secret) => secret?.name);
  const selectedSecretSet = authorizations.execution.active_secret_profile.environment_secret_names;
  if (
    selectedSecretSet.length !== unorderedSecretNames.length ||
    !selectedSecretSet.every((name) => unorderedSecretNames.includes(name)) ||
    new Set(unorderedSecretNames).size !== unorderedSecretNames.length
  ) {
    invalid('environment-secret-profile');
  }
  const secretNames = selectedSecretSet;
  const derived = {
    name: response.name,
    readiness_snapshot: {
      source_ids: {
        environment: 'readiness-stale-environment-protection',
        branch_policies: 'readiness-stale-environment-branch-policies',
        environment_secrets: 'readiness-stale-environment-secret-inventory',
      },
      environment_id: String(response.id),
      can_admins_bypass: response.can_admins_bypass,
      deployment_branch_policy: branchPolicy,
      required_reviewer_ids: reviewersRule.reviewers.map((reviewer) =>
        String(reviewer?.reviewer?.id),
      ),
      prevent_self_review: reviewersRule.prevent_self_review,
      branch_policies: branches.value.branch_policies.map((policy) => ({
        id: String(policy.id),
        name: policy.name,
        type: policy.type,
      })),
      secret_names: secretNames,
      token_permissions: { actions: 'write', contents: 'read', pull_requests: 'write' },
      readback_sha256: sha256(
        canonicalJson({
          environment: environment.source.body_sha256,
          branch_policies: branches.source.body_sha256,
          environment_secrets: secrets.source.body_sha256,
        }),
      ),
      observation: {
        request_started: environment.source.request_started_unix_milliseconds,
        response_received: secrets.source.response_received_unix_milliseconds,
      },
    },
  };
  if (
    authorizations.execution.coordinates.repository !== captureManifest.repository ||
    (host && canonicalJson(derived) !== canonicalJson(host.environment))
  ) {
    invalid('environment-derivation');
  }
  return derived;
}

function deriveIdentities(
  payloadReceipt,
  authorizations,
  captureManifest,
  capturedSourceBodies,
  oracleResult,
  concurrency,
) {
  const execution = authorizations.execution;
  const normalOperation = captureManifest.expected_roles.find(
    (run) => run.scope === 'normal',
  )?.operation_id;
  const staleOperation = captureManifest.expected_roles.find(
    (run) => run.scope === 'stale',
  )?.operation_id;
  const normalFixture = execution.fixture_prs.find((fixture) =>
    fixture.ref.endsWith(normalOperation),
  );
  const staleFixture = execution.fixture_prs.find((fixture) =>
    fixture.ref.endsWith(staleOperation),
  );
  const followOnRunId = concurrency.stale.terminal.waiter_run_id;
  const followOnSource = captureManifest.sources.find(
    (source) => source.source_id === `run-terminal-${followOnRunId}:page:1`,
  );
  const followOnRetained = capturedSourceBodies.get(`run-terminal-${followOnRunId}:page:1`);
  let followOn;
  try {
    followOn = JSON.parse(followOnRetained?.text ?? '');
  } catch {
    invalid('identity-follow-on-json');
  }
  if (
    payloadReceipt.kind !== 'apr-r4-e2p-trusted-proof-payload-v2' ||
    payloadReceipt.source_commit !== 'edc594c29a8a6b5fdacfab48643bf221277af200' ||
    payloadReceipt.source_tree !== '8bf475a02a4f7307cdce2bbc29dd2bc6c6cf9089' ||
    payloadReceipt.action_source_sha !== '5b5769753653bb3fd3e68cf8b7bb88a1bd350613' ||
    payloadReceipt.wrapper_build_discriminator !== 'r4-w2' ||
    payloadReceipt.payload_build_discriminator !== 'r4-w2' ||
    payloadReceipt.result !== 'passed' ||
    followOnSource === undefined ||
    followOnRetained === undefined ||
    sha256(Buffer.from(followOnRetained.text, 'utf8')) !== followOnSource.body_sha256 ||
    followOn.event !== 'workflow_run' ||
    followOn.status !== 'completed' ||
    followOn.conclusion !== 'failure' ||
    !hex40.test(followOn.head_sha)
  ) {
    invalid('identity-source-values');
  }
  return {
    workflow_sha: execution.coordinates.workflow_sha,
    action_source_sha: payloadReceipt.action_source_sha,
    payload_source_sha: payloadReceipt.source_commit,
    payload_sha256: payloadReceipt.payload_sha256,
    oracle_source_sha: oracleResult.oracle_source_sha,
    oracle_source_tree: oracleResult.oracle_source_tree,
    repository_id: captureManifest.repository_id,
    repository: captureManifest.repository,
    normal_pr_number: normalFixture.number,
    stale_pr_number: staleFixture.number,
    unauthorized_follow_on: {
      run_id: followOnRunId,
      run_attempt: 1,
      event: followOn.event,
      pr_number: staleFixture.number,
      advanced_head_sha: followOn.head_sha,
      terminal_result: 'inert-unauthorized',
    },
    operation_ids: [normalOperation, staleOperation],
  };
}

function deriveAuthorizationReadback(phase, response, permission, captureBodySha256, observation) {
  if (
    response === null ||
    typeof response !== 'object' ||
    !Number.isSafeInteger(response.id) ||
    response.id < 1 ||
    typeof response.body !== 'string' ||
    response.user === null ||
    typeof response.user !== 'object' ||
    !Number.isSafeInteger(response.user.id) ||
    response.user.id < 1 ||
    typeof response.user.login !== 'string' ||
    permission === null ||
    typeof permission !== 'object' ||
    !['admin', 'write'].includes(permission.permission) ||
    permission.user === null ||
    typeof permission.user !== 'object' ||
    String(permission.user.id) !== String(response.user.id) ||
    permission.user.login !== response.user.login
  ) {
    invalid(`authorization-${phase}-raw-response`);
  }
  interval(observation, `authorization-${phase}-observation`);
  const prefix = '<!-- apr-r4-e3-authorization ';
  const suffix = ' -->';
  if (!response.body.startsWith(prefix) || !response.body.endsWith(suffix)) {
    invalid(`authorization-${phase}-marker`);
  }
  let marker;
  try {
    marker = JSON.parse(response.body.slice(prefix.length, -suffix.length));
  } catch {
    invalid(`authorization-${phase}-marker`);
  }
  exactKeys(
    marker,
    ['contract', 'phase', 'repository', 'issue_number', 'authorization'],
    `authorization-${phase}-marker-shape`,
  );
  if (
    marker.contract !== 'apr-r4-e3-maintainer-authorization-v1' ||
    marker.phase !== phase ||
    typeof marker.repository !== 'string' ||
    !Number.isSafeInteger(marker.issue_number) ||
    marker.issue_number < 1 ||
    response.body !== `${prefix}${JSON.stringify(marker)}${suffix}` ||
    !hex64.test(captureBodySha256)
  ) {
    invalid(`authorization-${phase}-marker-values`);
  }
  const bodySha256 = sha256(Buffer.from(response.body, 'utf8'));
  return {
    kind: marker.authorization?.kind,
    phase: marker.authorization?.phase,
    source: {
      kind: 'maintainer-comment-readback',
      phase,
      repository: marker.repository,
      issue_number: String(marker.issue_number),
      comment_id: String(response.id),
      author_id: String(response.user.id),
      author_permission: permission.permission,
      capture_body_sha256: captureBodySha256,
      body_sha256: bodySha256,
      readback_sha256: bodySha256,
      observation,
    },
    ...Object.fromEntries(
      Object.entries(marker.authorization ?? {}).filter(
        ([key]) => key !== 'kind' && key !== 'phase',
      ),
    ),
  };
}

function deriveAuthorizations(captureManifest, capturedSourceBodies, retainedDocuments) {
  const authorizations = {};
  const read = (source) => {
    const retained = capturedSourceBodies.get(source.source_id);
    if (
      retained === undefined ||
      typeof retained.text !== 'string' ||
      sha256(Buffer.from(retained.text, 'utf8')) !== source.body_sha256 ||
      String(Buffer.byteLength(retained.text, 'utf8')) !== source.body_size
    ) {
      invalid('authorization-capture-binding');
    }
    try {
      return JSON.parse(retained.text);
    } catch {
      invalid('authorization-capture-json');
    }
  };
  for (const phase of ['setup', 'execution']) {
    const derivedByScope = [];
    for (const scope of ['normal', 'stale']) {
      const comments = captureManifest.sources.filter((source) =>
        new RegExp(`^authorization-${phase}-comment-${scope}-[1-9][0-9]*:page:1$`, 'u').test(
          source.source_id,
        ),
      );
      const permissions = captureManifest.sources.filter((source) =>
        new RegExp(`^authorization-${phase}-permission-${scope}-[A-Za-z0-9-]+:page:1$`, 'u').test(
          source.source_id,
        ),
      );
      if (comments.length !== 1 || permissions.length !== 1) {
        invalid(`authorization-${phase}-${scope}-source-cardinality`);
      }
      const comment = comments[0];
      const permission = permissions[0];
      derivedByScope.push({
        response: read(comment),
        permission: read(permission),
        authorization: deriveAuthorizationReadback(
          phase,
          read(comment),
          read(permission),
          comment.body_sha256,
          {
            request_started: comment.request_started_unix_milliseconds,
            response_received: comment.response_received_unix_milliseconds,
          },
        ),
      });
    }
    if (
      canonicalJson(derivedByScope[0].response) !== canonicalJson(derivedByScope[1].response) ||
      canonicalJson(derivedByScope[0].permission) !== canonicalJson(derivedByScope[1].permission)
    ) {
      invalid(`authorization-${phase}-paired-baseline-drift`);
    }
    authorizations[phase] = derivedByScope[0].authorization;
  }
  const cleanup = retainedDocuments.get('cleanup-authorization-readback');
  exactKeys(
    cleanup,
    ['kind', 'comment', 'permission', 'observation'],
    'cleanup-authorization-readback-shape',
  );
  if (cleanup.kind !== 'apr-r4-e3-cleanup-authorization-readback-v1') {
    invalid('cleanup-authorization-readback-values');
  }
  authorizations.cleanup = deriveAuthorizationReadback(
    'cleanup',
    cleanup.comment,
    cleanup.permission,
    sha256(canonicalJson(cleanup.comment)),
    cleanup.observation,
  );
  return authorizations;
}

function validateProofControlDerivation(host, captureManifest, capturedSourceBodies) {
  if (!(capturedSourceBodies instanceof Map)) invalid('captured-source-bodies');
  const manifestById = new Map(captureManifest.sources.map((source) => [source.source_id, source]));
  const read = (sourceId) => {
    const retained = capturedSourceBodies.get(sourceId);
    const manifest = manifestById.get(sourceId);
    if (
      retained === undefined ||
      manifest === undefined ||
      typeof retained.text !== 'string' ||
      sha256(Buffer.from(retained.text, 'utf8')) !== manifest.body_sha256 ||
      String(Buffer.byteLength(retained.text, 'utf8')) !== manifest.body_size
    ) {
      invalid('proof-control-capture-binding');
    }
    try {
      return { value: JSON.parse(retained.text), manifest };
    } catch {
      invalid('proof-control-capture-json');
    }
  };

  for (const family of Object.values(host.proof_control)) {
    for (const expected of family.comments) {
      const capture = read(proofControlCommentSourceId(host, expected));
      const response = capture.value;
      if (
        response === null ||
        typeof response !== 'object' ||
        String(response.id) !== expected.comment_id ||
        typeof response.body !== 'string' ||
        response.user === null ||
        typeof response.user !== 'object' ||
        !decimal.test(String(response.user.id)) ||
        typeof response.user.login !== 'string'
      ) {
        invalid('proof-control-comment-response');
      }
      const prefix = '<!-- apr-r4-e2p-control ';
      const suffix = ' -->';
      if (!response.body.startsWith(prefix) || !response.body.endsWith(suffix)) {
        invalid('proof-control-marker');
      }
      let marker;
      try {
        marker = JSON.parse(response.body.slice(prefix.length, -suffix.length));
      } catch {
        invalid('proof-control-marker');
      }
      exactKeys(
        marker,
        [
          'contract',
          'kind',
          'operation_id',
          'repository_id',
          'repository',
          'pr_number',
          'fixture_head_sha',
          'workflow_sha',
          'action_source_sha',
          'payload_sha256',
          'run_id',
          'run_attempt',
          'predecessor_comment_id',
          'body_sha256',
        ],
        'proof-control-marker-shape',
      );
      const preimage = JSON.stringify({ ...marker, body_sha256: '' });
      if (
        marker.contract !== 'apr-r4-e2p-proof-control-v1' ||
        !['ready', 'release', 'stale-ready', 'stale-release'].includes(marker.kind) ||
        !hex64.test(marker.operation_id) ||
        marker.repository_id !== Number(host.identities.repository_id) ||
        marker.repository !== host.identities.repository ||
        !Number.isSafeInteger(marker.pr_number) ||
        !hex40.test(marker.fixture_head_sha) ||
        !hex40.test(marker.workflow_sha) ||
        !hex40.test(marker.action_source_sha) ||
        !hex64.test(marker.payload_sha256) ||
        !Number.isSafeInteger(marker.run_id) ||
        marker.run_id <= 0 ||
        marker.run_attempt !== 1 ||
        marker.body_sha256 !== sha256(Buffer.from(preimage, 'utf8')) ||
        response.body !== `${prefix}${JSON.stringify(marker)}${suffix}`
      ) {
        invalid('proof-control-marker-values');
      }
      const readyActor = marker.kind === 'ready' || marker.kind === 'stale-ready';
      let actorPermission = 'workflow-token';
      if (readyActor) {
        if (
          String(response.user.id) !== '41898282' ||
          response.user.login !== 'github-actions[bot]'
        ) {
          invalid('proof-control-ready-actor');
        }
      } else {
        const permission = read(proofControlPermissionSourceId(host, expected)).value;
        if (
          String(response.user.id) !== '16307884' ||
          response.user.login !== 'maintainer' ||
          permission === null ||
          typeof permission !== 'object' ||
          !['admin', 'write'].includes(permission.permission) ||
          String(permission.user?.id) !== String(response.user.id) ||
          permission.user?.login !== response.user.login
        ) {
          invalid('proof-control-release-actor');
        }
        actorPermission = permission.permission;
      }
      const derived = {
        kind: marker.kind,
        comment_id: String(response.id),
        predecessor_comment_id:
          marker.predecessor_comment_id === null ? null : String(marker.predecessor_comment_id),
        operation_id: marker.operation_id,
        run_id: String(marker.run_id),
        run_attempt: marker.run_attempt,
        repository: marker.repository,
        pr_number: String(marker.pr_number),
        fixture_head_sha: marker.fixture_head_sha,
        workflow_sha: marker.workflow_sha,
        action_source_sha: marker.action_source_sha,
        payload_sha256: marker.payload_sha256,
        actor_id: String(response.user.id),
        actor_permission: actorPermission,
        observation: {
          request_started: capture.manifest.request_started_unix_milliseconds,
          response_received: capture.manifest.response_received_unix_milliseconds,
        },
        body_preimage: preimage,
        capture_body_sha256: capture.manifest.body_sha256,
        body_sha256: marker.body_sha256,
        readback_sha256: sha256(Buffer.from(response.body, 'utf8')),
      };
      if (canonicalJson(derived) !== canonicalJson(expected)) {
        invalid('proof-control-derivation');
      }
    }
  }
}

function deriveProofControl(authorizations, identities, captureManifest, capturedSourceBodies) {
  const byOperation = new Map(authorizations.execution.operation_ids.map((id) => [id, []]));
  const manifestById = new Map(captureManifest.sources.map((source) => [source.source_id, source]));
  const sources = captureManifest.sources.filter((source) =>
    /^proof-control-(bootstrap|stale)-comment-[1-9][0-9]*:page:1$/u.test(source.source_id),
  );
  for (const source of sources) {
    const retained = capturedSourceBodies.get(source.source_id);
    if (
      retained === undefined ||
      sha256(Buffer.from(retained.text, 'utf8')) !== source.body_sha256
    ) {
      invalid('proof-control-source-binding');
    }
    let response;
    let marker;
    try {
      response = JSON.parse(retained.text);
      const prefix = '<!-- apr-r4-e2p-control ';
      const suffix = ' -->';
      if (!response.body.startsWith(prefix) || !response.body.endsWith(suffix)) {
        invalid('proof-control-source-marker');
      }
      marker = JSON.parse(response.body.slice(prefix.length, -suffix.length));
    } catch {
      invalid('proof-control-source-json');
    }
    const readyActor = marker.kind === 'ready' || marker.kind === 'stale-ready';
    let actorPermission = 'workflow-token';
    if (!readyActor) {
      const phase = source.source_id.split('-')[2];
      const permissionSourceId = `proof-control-${phase}-permission-${response.id}-maintainer:page:1`;
      const permissionSource = manifestById.get(permissionSourceId);
      const permissionRetained = capturedSourceBodies.get(permissionSourceId);
      if (
        permissionSource === undefined ||
        permissionRetained === undefined ||
        sha256(Buffer.from(permissionRetained.text, 'utf8')) !== permissionSource.body_sha256
      ) {
        invalid('proof-control-permission-binding');
      }
      try {
        actorPermission = JSON.parse(permissionRetained.text).permission;
      } catch {
        invalid('proof-control-permission-json');
      }
    }
    const preimage = JSON.stringify({ ...marker, body_sha256: '' });
    const comments = byOperation.get(marker.operation_id);
    if (comments === undefined) invalid('proof-control-operation');
    comments.push({
      kind: marker.kind,
      comment_id: String(response.id),
      predecessor_comment_id:
        marker.predecessor_comment_id === null ? null : String(marker.predecessor_comment_id),
      operation_id: marker.operation_id,
      run_id: String(marker.run_id),
      run_attempt: marker.run_attempt,
      repository: marker.repository,
      pr_number: String(marker.pr_number),
      fixture_head_sha: marker.fixture_head_sha,
      workflow_sha: marker.workflow_sha,
      action_source_sha: marker.action_source_sha,
      payload_sha256: marker.payload_sha256,
      actor_id: String(response.user?.id),
      actor_permission: actorPermission,
      observation: {
        request_started: source.request_started_unix_milliseconds,
        response_received: source.response_received_unix_milliseconds,
      },
      body_preimage: preimage,
      capture_body_sha256: source.body_sha256,
      body_sha256: marker.body_sha256,
      readback_sha256: sha256(Buffer.from(response.body, 'utf8')),
    });
  }
  for (const scope of ['normal', 'stale']) {
    const pattern = new RegExp(`^proof-control-comments-${scope}-pr-[1-9][0-9]*:page:`, 'u');
    const inventorySources = captureManifest.sources
      .filter((source) => pattern.test(source.source_id))
      .sort((left, right) => left.page - right.page);
    if (
      inventorySources.length === 0 ||
      inventorySources.some(
        (source, index) =>
          source.page !== index + 1 ||
          (index === inventorySources.length - 1
            ? source.next_route !== null
            : source.next_route !== inventorySources[index + 1].route),
      )
    ) {
      invalid('proof-control-inventory-pagination');
    }
    const operationId = authorizations.execution.operation_ids[scope === 'normal' ? 0 : 1];
    const inventoryIds = [];
    for (const inventorySource of inventorySources) {
      const retained = capturedSourceBodies.get(inventorySource.source_id);
      if (
        retained === undefined ||
        sha256(Buffer.from(retained.text, 'utf8')) !== inventorySource.body_sha256
      ) {
        invalid('proof-control-inventory-binding');
      }
      let responses;
      try {
        responses = JSON.parse(retained.text);
      } catch {
        invalid('proof-control-inventory-json');
      }
      if (!Array.isArray(responses)) invalid('proof-control-inventory-json');
      for (const response of responses) {
        if (
          typeof response?.body !== 'string' ||
          !response.body.startsWith('<!-- apr-r4-e2p-control ')
        ) {
          continue;
        }
        try {
          const marker = JSON.parse(response.body.slice('<!-- apr-r4-e2p-control '.length, -4));
          if (marker.operation_id === operationId) inventoryIds.push(String(response.id));
        } catch {
          invalid('proof-control-inventory-marker');
        }
      }
    }
    const derivedIds = (byOperation.get(operationId) ?? []).map((comment) => comment.comment_id);
    if (
      new Set(inventoryIds).size !== inventoryIds.length ||
      canonicalJson([...inventoryIds].sort()) !== canonicalJson([...derivedIds].sort())
    ) {
      invalid('proof-control-inventory-completeness');
    }
  }
  const operationForScope = (scope) =>
    captureManifest.expected_roles.find((run) => run.scope === scope)?.operation_id;
  const makeFamily = (scope, kinds) => {
    const operationId = operationForScope(scope);
    const comments = byOperation.get(operationId) ?? [];
    comments.sort((left, right) => kinds.indexOf(left.kind) - kinds.indexOf(right.kind));
    return {
      operation_id: operationId,
      comments,
      cleanup_outcomes: comments.map((comment) => ({
        comment_id: comment.comment_id,
        outcome: 'deleted-absent',
      })),
    };
  };
  const proofControl = {
    normal: makeFamily('normal', ['ready', 'release']),
    stale: makeFamily('stale', ['ready', 'release', 'stale-ready', 'stale-release']),
  };
  validateProofControlDerivation(
    { proof_control: proofControl, identities },
    captureManifest,
    capturedSourceBodies,
  );
  return proofControl;
}

function validateApprovalDerivation(host, captureManifest, capturedSourceBodies, authorizedRuns) {
  const manifestById = new Map(captureManifest.sources.map((source) => [source.source_id, source]));
  const read = (sourceId) => {
    const retained = capturedSourceBodies.get(sourceId);
    const manifest = manifestById.get(sourceId);
    if (
      retained === undefined ||
      manifest === undefined ||
      typeof retained.text !== 'string' ||
      sha256(Buffer.from(retained.text, 'utf8')) !== manifest.body_sha256 ||
      String(Buffer.byteLength(retained.text, 'utf8')) !== manifest.body_size
    ) {
      invalid('approval-capture-binding');
    }
    try {
      return { value: JSON.parse(retained.text), manifest };
    } catch {
      invalid('approval-capture-json');
    }
  };
  const pages = (baseSourceId) => {
    const matches = captureManifest.sources
      .filter((source) => source.source_id.startsWith(`${baseSourceId}:page:`))
      .sort((left, right) => left.page - right.page);
    if (
      matches.length === 0 ||
      matches.some(
        (source, index) =>
          source.page !== index + 1 ||
          (index === matches.length - 1
            ? source.next_route !== null
            : source.next_route !== matches[index + 1].route),
      )
    ) {
      invalid('approval-pagination');
    }
    return matches.map((source) => read(source.source_id));
  };

  const phaseEntries = host
    ? Object.entries(host.approval_transitions)
    : [
        ['bootstrap', { run_id: authorizedRuns.find((run) => run.scope === 'normal').run_id }],
        [
          'continuation',
          { run_id: authorizedRuns.filter((run) => run.scope === 'normal')[1].run_id },
        ],
        ['stale', { run_id: authorizedRuns.find((run) => run.scope === 'stale').run_id }],
      ];
  const derivedTransitions = {};
  for (const [phase, expected] of phaseEntries) {
    const runId = expected.run_id;
    const pendingCapture = read(`transition-${phase}-pending-run-${runId}:page:1`);
    const approvalCapture = read(`transition-${phase}-approvals-run-${runId}:page:1`);
    const jobPages = pages(`transition-${phase}-jobs-run-${runId}`);
    if (
      !Array.isArray(pendingCapture.value) ||
      pendingCapture.value.length !== 1 ||
      !Array.isArray(approvalCapture.value) ||
      approvalCapture.value.length !== 1
    ) {
      invalid(`approval-${phase}-raw-cardinality`);
    }
    const pending = pendingCapture.value[0];
    const approval = approvalCapture.value[0];
    const jobs = jobPages.flatMap((page) => {
      if (
        page.value === null ||
        typeof page.value !== 'object' ||
        !Number.isSafeInteger(page.value.total_count) ||
        !Array.isArray(page.value.jobs)
      ) {
        invalid(`approval-${phase}-jobs-response`);
      }
      return page.value.jobs;
    });
    if (
      jobPages.some((page) => page.value.total_count !== jobs.length) ||
      jobs.length !== 3 ||
      new Set(jobs.map((job) => job?.name)).size !== jobs.length
    ) {
      invalid(`approval-${phase}-jobs-cardinality`);
    }
    const selectedName =
      phase === 'continuation' ? 'workflow-dispatch-review' : 'workflow-run-review';
    const otherProtectedName =
      selectedName === 'workflow-run-review' ? 'workflow-dispatch-review' : 'workflow-run-review';
    const expectedJobs = new Map([
      ['authorization-preflight', 'success'],
      [selectedName, 'success'],
      [otherProtectedName, 'skipped'],
    ]);
    for (const candidate of jobs) {
      if (
        candidate === null ||
        typeof candidate !== 'object' ||
        !Number.isSafeInteger(candidate.id) ||
        String(candidate.run_id) !== runId ||
        candidate.run_attempt !== 1 ||
        candidate.status !== 'completed' ||
        expectedJobs.get(candidate.name) !== candidate.conclusion
      ) {
        invalid(`approval-${phase}-jobs-topology`);
      }
    }
    if (!jobs.every((candidate) => expectedJobs.has(candidate.name))) {
      invalid(`approval-${phase}-jobs-topology`);
    }
    const job = jobs.find((candidate) => candidate.name === selectedName);
    const reviewers = pending.reviewers?.map((reviewer) => String(reviewer.reviewer?.id));
    const environments = approval.environments;
    const started = Date.parse(job?.started_at);
    if (
      pending.environment === null ||
      typeof pending.environment !== 'object' ||
      !Array.isArray(reviewers) ||
      reviewers.length !== 1 ||
      approval.state !== 'approved' ||
      String(approval.user?.id) !== '16307884' ||
      !Array.isArray(environments) ||
      environments.length !== 1 ||
      job === undefined ||
      !Number.isSafeInteger(started)
    ) {
      invalid(`approval-${phase}-raw-values`);
    }
    const derived = {
      phase,
      run_id: runId,
      run_attempt: 1,
      pending: {
        run_id: runId,
        environment_id: String(pending.environment.id),
        environment_name: pending.environment.name,
        reviewer_ids: reviewers,
        state: 'pending',
        source_id: `${phase}-pending`,
        observation: {
          request_started: pendingCapture.manifest.request_started_unix_milliseconds,
          response_received: pendingCapture.manifest.response_received_unix_milliseconds,
        },
      },
      approval: {
        run_id: runId,
        environment_id: String(environments[0].id),
        environment_name: environments[0].name,
        approving_user_id: String(approval.user.id),
        state: approval.state,
        source_id: `${phase}-approval`,
        observation: {
          request_started: approvalCapture.manifest.request_started_unix_milliseconds,
          response_received: approvalCapture.manifest.response_received_unix_milliseconds,
        },
      },
      protected_job: {
        run_id: runId,
        run_attempt: job.run_attempt,
        name: job.name,
        started: { kind: 'source-emitted', value: started },
      },
    };
    derivedTransitions[phase] = derived;
    if (host && canonicalJson(derived) !== canonicalJson(expected)) {
      invalid(`approval-${phase}-derivation`);
    }
  }
  return derivedTransitions;
}

function validateConcurrencyDerivation(host, captureManifest, capturedSourceBodies) {
  const manifestById = new Map(captureManifest.sources.map((source) => [source.source_id, source]));
  const read = (sourceId) => {
    const retained = capturedSourceBodies.get(sourceId);
    const manifest = manifestById.get(sourceId);
    if (
      retained === undefined ||
      manifest === undefined ||
      typeof retained.text !== 'string' ||
      sha256(Buffer.from(retained.text, 'utf8')) !== manifest.body_sha256 ||
      String(Buffer.byteLength(retained.text, 'utf8')) !== manifest.body_size
    ) {
      invalid('concurrency-capture-binding');
    }
    try {
      return { value: JSON.parse(retained.text), manifest };
    } catch {
      invalid('concurrency-capture-json');
    }
  };

  const scopeEntries = host
    ? Object.entries(host.concurrency)
    : ['normal', 'stale'].map((scope) => {
        const pattern = new RegExp(`^concurrency-${scope}-run-([1-9][0-9]*):page:1$`, 'u');
        const matches = captureManifest.sources.filter((source) => pattern.test(source.source_id));
        if (matches.length !== 1) invalid(`concurrency-${scope}-source-cardinality`);
        const holderRunId = matches[0].source_id.match(pattern)[1];
        const first = read(matches[0].source_id).value;
        if (!Array.isArray(first.group_members) || first.group_members.length !== 2) {
          invalid(`concurrency-${scope}-member-cardinality`);
        }
        return [
          scope,
          {
            terminal: {
              holder_run_id: holderRunId,
              waiter_run_id: String(first.group_members[1]?.run_id),
            },
          },
        ];
      });
  const derivedConcurrency = {};
  for (const [scope, expected] of scopeEntries) {
    const holderRunId = expected.terminal.holder_run_id;
    const base = `concurrency-${scope}-run-${holderRunId}`;
    const pageSources = captureManifest.sources
      .filter((source) => source.source_id.startsWith(`${base}:page:`))
      .sort((left, right) => left.page - right.page);
    if (
      pageSources.length !== 1 ||
      pageSources.some((source, index) => source.page !== index + 1 || source.next_route !== null)
    ) {
      invalid(`concurrency-${scope}-pagination`);
    }
    const pageCaptures = pageSources.map((source) => read(source.source_id));
    const first = pageCaptures[0].value;
    if (
      pageCaptures.some(
        ({ value }) =>
          value === null ||
          typeof value !== 'object' ||
          value.group_name !== first.group_name ||
          !Number.isSafeInteger(value.total_count) ||
          !Array.isArray(value.group_members) ||
          value.total_count !== value.group_members.length,
      )
    ) {
      invalid(`concurrency-${scope}-raw-values`);
    }
    if (
      pageSources[0].route !==
      `/repos/${captureManifest.repository}/actions/concurrency_groups/${encodeURIComponent(first.group_name)}?ahead_of_run=${expected.terminal.waiter_run_id}`
    ) {
      invalid(`concurrency-${scope}-route`);
    }
    const members = pageCaptures.flatMap(({ value }) => value.group_members);
    const holderCapture = read(`run-terminal-${expected.terminal.holder_run_id}:page:1`).value;
    const waiterCapture = read(`run-terminal-${expected.terminal.waiter_run_id}:page:1`).value;
    const holderCompleted = Date.parse(holderCapture.updated_at);
    const waiterStarted = Date.parse(waiterCapture.run_started_at);
    if (
      String(holderCapture.id) !== expected.terminal.holder_run_id ||
      holderCapture.status !== 'completed' ||
      String(waiterCapture.id) !== expected.terminal.waiter_run_id ||
      waiterCapture.status !== 'completed' ||
      !Number.isSafeInteger(holderCompleted) ||
      !Number.isSafeInteger(waiterStarted)
    ) {
      invalid(`concurrency-${scope}-terminal-response`);
    }
    const derived = {
      api_version: '2026-03-10',
      group: first.group_name,
      pagination_complete: true,
      observation: {
        request_started: pageSources[0].request_started_unix_milliseconds,
        response_received: pageSources.at(-1).response_received_unix_milliseconds,
      },
      ahead_of_run: members.map((member) => ({
        run_id: String(member.run_id),
        position: member.position,
        status: member.status,
      })),
      terminal: {
        holder_run_id: String(holderCapture.id),
        waiter_run_id: String(waiterCapture.id),
        holder_completed: { kind: 'source-emitted', value: holderCompleted },
        waiter_started: { kind: 'source-emitted', value: waiterStarted },
        holder_cancelled: holderCapture.conclusion === 'cancelled',
      },
    };
    derivedConcurrency[scope] = derived;
    if (host && canonicalJson(derived) !== canonicalJson(expected)) {
      invalid(`concurrency-${scope}-derivation`);
    }
  }
  return derivedConcurrency;
}

function validateAuthorizationSource(value, phase) {
  exactKeys(
    value,
    [
      'kind',
      'phase',
      'repository',
      'issue_number',
      'comment_id',
      'author_id',
      'author_permission',
      'capture_body_sha256',
      'body_sha256',
      'readback_sha256',
      'observation',
    ],
    `${phase}-authorization-source-shape`,
  );
  interval(value.observation, `${phase}-authorization-source-observation`);
  if (
    value.kind !== 'maintainer-comment-readback' ||
    value.phase !== phase ||
    value.repository !== 'SolusQuest/agentic-pr-review' ||
    value.issue_number !== '181' ||
    !decimal.test(value.comment_id) ||
    value.author_id !== '16307884' ||
    !['admin', 'write'].includes(value.author_permission) ||
    !hex64.test(value.capture_body_sha256) ||
    !hex64.test(value.body_sha256) ||
    value.readback_sha256 !== value.body_sha256
  ) {
    invalid(`${phase}-authorization-source-values`);
  }
}

function validateAuthorization(value, identities) {
  exactKeys(value, ['setup', 'execution', 'cleanup'], 'authorization-shape');
  exactKeys(
    value.setup,
    ['kind', 'phase', 'source', 'coordinates', 'capabilities', 'branches'],
    'setup-authorization-shape',
  );
  validateAuthorizationSource(value.setup.source, 'setup');
  exactKeys(
    value.setup.coordinates,
    ['repository', 'workflow_sha', 'action_source_sha', 'payload_source_sha', 'payload_sha256'],
    'setup-authorization-coordinate-shape',
  );
  if (
    value.setup.kind !== 'apr-r4-e3-setup-authorization-v1' ||
    value.setup.phase !== 'setup' ||
    !Array.isArray(value.setup.branches) ||
    value.setup.branches.length !== 2 ||
    value.setup.branches.some((branch) => {
      exactKeys(
        branch,
        ['ref', 'head_sha', 'parent_sha', 'tree_sha'],
        'setup-authorization-branch-shape',
      );
      return (
        !hex40.test(branch.head_sha) ||
        !hex40.test(branch.parent_sha) ||
        !hex40.test(branch.tree_sha) ||
        !fixtureRef.test(branch.ref)
      );
    }) ||
    JSON.stringify(value.setup.coordinates) !==
      JSON.stringify({
        repository: identities.repository,
        workflow_sha: identities.workflow_sha,
        action_source_sha: identities.action_source_sha,
        payload_source_sha: identities.payload_source_sha,
        payload_sha256: identities.payload_sha256,
      })
  ) {
    invalid('setup-authorization-values');
  }
  exactArray(
    value.setup.capabilities,
    [
      'configure-environment-baseline',
      'push-two-precomputed-heads',
      'open-two-frozen-fixture-prs',
      'observe-secret-free-ci-and-inert-preflight',
      'bounded-setup-rollback',
    ],
    'setup-authorization-capabilities',
  );

  exactKeys(
    value.execution,
    [
      'kind',
      'phase',
      'source',
      'coordinates',
      'fixture_prs',
      'operation_ids',
      'trigger_plan',
      'authorization_manifests',
      'environment_baseline',
      'authorization_variable_baseline',
      'actors',
      'credential_slots',
      'active_secret_profile',
      'credential_materializer',
      'correction_gate',
      'destinations',
      'provider_mode',
      'oracle_build',
      'commands',
      'protected_scan_input',
    ],
    'execution-authorization-shape',
  );
  validateAuthorizationSource(value.execution.source, 'execution');
  if (JSON.stringify(value.execution.coordinates) !== JSON.stringify(value.setup.coordinates)) {
    invalid('execution-authorization-coordinates');
  }
  if (
    value.execution.kind !== 'apr-r4-e3-execution-authorization-v1' ||
    value.execution.phase !== 'execution' ||
    value.execution.fixture_prs.length !== 2 ||
    value.execution.fixture_prs.some((fixture) => {
      exactKeys(
        fixture,
        [
          'id',
          'number',
          'ref',
          'head_sha',
          'parent_sha',
          'tree_sha',
          'base_ref',
          'base_sha',
          'base_tree_sha',
        ],
        'execution-fixture-shape',
      );
      return (
        !decimal.test(fixture.id) ||
        !decimal.test(fixture.number) ||
        !fixtureRef.test(fixture.ref) ||
        !hex40.test(fixture.head_sha) ||
        !hex40.test(fixture.parent_sha) ||
        !hex40.test(fixture.tree_sha) ||
        fixture.base_ref !== 'main' ||
        !hex40.test(fixture.base_sha) ||
        !hex40.test(fixture.base_tree_sha)
      );
    }) ||
    value.execution.operation_ids.length !== 2 ||
    value.execution.operation_ids.some((item) => !hex64.test(item)) ||
    value.execution.authorization_manifests.length !== 2 ||
    value.execution.authorization_manifests.some((item) => !hex64.test(item)) ||
    JSON.stringify(value.execution.actors) !==
      JSON.stringify([{ id: '16307884', permission: 'admin' }]) ||
    JSON.stringify(Object.keys(value.execution.oracle_build)) !==
      JSON.stringify([
        'source_commit',
        'source_tree',
        'build_receipt_sha256',
        'oracle_assembly_sha256',
        'production_assembly_sha256',
      ]) ||
    !hex40.test(value.execution.oracle_build.source_commit) ||
    !hex40.test(value.execution.oracle_build.source_tree) ||
    !hex64.test(value.execution.oracle_build.build_receipt_sha256) ||
    !hex64.test(value.execution.oracle_build.oracle_assembly_sha256) ||
    !hex64.test(value.execution.oracle_build.production_assembly_sha256) ||
    JSON.stringify(value.execution.commands) !==
      JSON.stringify([
        'place-operation-secrets',
        'enable-exact-authorization-variable',
        'rerun-normal-bootstrap',
        'dispatch-normal-continuation',
        'approve-three-deployments',
        'write-proof-control-comments',
        'run-product-and-state-route',
      ])
  ) {
    invalid('execution-authorization-values');
  }
  const triggerRoles = [
    'normal-bootstrap',
    'normal-continuation',
    'stale-protected',
    'stale-follow-on',
  ];
  if (!Array.isArray(value.execution.trigger_plan) || value.execution.trigger_plan.length !== 4) {
    invalid('execution-trigger-plan');
  }
  value.execution.trigger_plan.forEach((trigger, index) => {
    exactKeys(
      trigger,
      [
        'role',
        'operation_id',
        'scope',
        'producer',
        'expected_event',
        'pr_number',
        'ref',
        'authorized_head_sha',
        'source_coordinate',
      ],
      'execution-trigger-shape',
    );
    const fixture = value.execution.fixture_prs[index < 2 ? 0 : 1];
    const expectedProducer =
      index === 1
        ? 'dispatch-proof-workflow'
        : index === 3
          ? 'advance-stale-ref'
          : 'rerun-upstream-ci';
    const expectedEvent = index === 1 ? 'workflow_dispatch' : 'workflow_run';
    const coordinateKeys = index === 0 || index === 2 ? ['kind', 'id'] : ['kind', 'value'];
    exactKeys(
      trigger.source_coordinate,
      coordinateKeys,
      'execution-trigger-source-coordinate-shape',
    );
    if (
      trigger.role !== triggerRoles[index] ||
      trigger.operation_id !== value.execution.operation_ids[index < 2 ? 0 : 1] ||
      trigger.scope !== (index < 2 ? 'normal' : 'stale') ||
      trigger.producer !== expectedProducer ||
      trigger.expected_event !== expectedEvent ||
      trigger.pr_number !== fixture.number ||
      trigger.ref !== fixture.ref ||
      trigger.authorized_head_sha !== fixture.head_sha ||
      (index === 0 || index === 2
        ? trigger.source_coordinate.kind !== 'upstream-workflow-run' ||
          !decimal.test(trigger.source_coordinate.id)
        : index === 1
          ? trigger.source_coordinate.kind !== 'workflow-dispatch-ref' ||
            trigger.source_coordinate.value !== 'main'
          : trigger.source_coordinate.kind !== 'advanced-ref-head' ||
            !hex40.test(trigger.source_coordinate.value))
    ) {
      invalid('execution-trigger-values');
    }
  });
  exactKeys(
    value.execution.environment_baseline,
    [
      'environment_id',
      'name',
      'secret_names',
      'deployment_branch_policy',
      'required_reviewer_ids',
      'prevent_self_review',
      'can_admins_bypass',
    ],
    'execution-environment-baseline-shape',
  );
  if (
    value.execution.environment_baseline.environment_id !== '20766359842' ||
    value.execution.environment_baseline.name !== 'r4-trusted-proof' ||
    JSON.stringify(value.execution.environment_baseline.secret_names) !== '[]' ||
    value.execution.environment_baseline.deployment_branch_policy !== 'main-only' ||
    JSON.stringify(value.execution.environment_baseline.required_reviewer_ids) !== '["16307884"]' ||
    value.execution.environment_baseline.prevent_self_review !== false ||
    value.execution.environment_baseline.can_admins_bypass !== false
  ) {
    invalid('execution-environment-baseline-values');
  }
  exactKeys(
    value.execution.authorization_variable_baseline,
    ['name', 'state'],
    'execution-variable-baseline-shape',
  );
  if (
    value.execution.authorization_variable_baseline.name !== 'R4_TRUSTED_PROOF_AUTHORIZATION' ||
    value.execution.authorization_variable_baseline.state !== 'absent'
  ) {
    invalid('execution-variable-baseline-values');
  }
  const expectedSlots = [
    ['github-token', true, false],
    ['current-state-key', true, true],
    ['previous-state-key', false, true],
  ];
  if (
    !Array.isArray(value.execution.credential_slots) ||
    value.execution.credential_slots.length !== 3
  ) {
    invalid('execution-credential-slots');
  }
  value.execution.credential_slots.forEach((slot, index) => {
    exactKeys(slot, ['name', 'required', 'base64_key'], 'execution-credential-slot-shape');
    if (
      canonicalJson([slot.name, slot.required, slot.base64_key]) !==
      canonicalJson(expectedSlots[index])
    ) {
      invalid('execution-credential-slot-values');
    }
  });
  exactKeys(
    value.execution.active_secret_profile,
    ['environment_secret_names', 'credential_slot_names'],
    'execution-active-secret-profile-shape',
  );
  const allowedEnvironmentProfiles = [
    ['DEEPSEEK_API_KEY', 'AGENTIC_PR_REVIEW_STATE_KEY'],
    ['DEEPSEEK_API_KEY', 'AGENTIC_PR_REVIEW_STATE_KEY', 'AGENTIC_PR_REVIEW_PREVIOUS_STATE_KEY'],
  ];
  const allowedCredentialProfiles = [
    ['github-token', 'current-state-key'],
    ['github-token', 'current-state-key', 'previous-state-key'],
  ];
  const selectedProfile = allowedEnvironmentProfiles.findIndex(
    (profile) =>
      canonicalJson(profile) ===
      canonicalJson(value.execution.active_secret_profile.environment_secret_names),
  );
  if (
    selectedProfile < 0 ||
    canonicalJson(value.execution.active_secret_profile.credential_slot_names) !==
      canonicalJson(allowedCredentialProfiles[selectedProfile])
  ) {
    invalid('execution-active-secret-profile-values');
  }
  exactKeys(
    value.execution.credential_materializer,
    [
      'kind',
      'source_sha256',
      'build_sha256',
      'input_transport',
      'admission_receipt_kind',
      'disposition_receipt_kind',
    ],
    'execution-credential-materializer-shape',
  );
  if (
    value.execution.credential_materializer.kind !==
      'apr-r4-e3-credential-guardian-materializer-v1' ||
    !hex64.test(value.execution.credential_materializer.source_sha256) ||
    !hex64.test(value.execution.credential_materializer.build_sha256) ||
    value.execution.credential_materializer.input_transport !== 'private-stdin' ||
    value.execution.credential_materializer.admission_receipt_kind !==
      'apr-r4-e3-credential-admission-v1' ||
    value.execution.credential_materializer.disposition_receipt_kind !==
      'apr-r4-e3-credential-disposition-v1'
  ) {
    invalid('execution-credential-materializer-values');
  }
  exactKeys(
    value.execution.correction_gate,
    [
      'repository',
      'pull_request_number',
      'branch',
      'commit',
      'tree',
      'authority_identities',
      'contract_digests',
    ],
    'execution-correction-gate-shape',
  );
  const authorityComponents = [
    'capture',
    'credential-materializer',
    'producer-journal-materializer',
    'phase-fragment-materializer',
    'oracle',
    'assembler',
  ];
  const contractComponents = [
    'cleanup-generator',
    'projector',
    'static-checker',
    'source-map',
    'host-schema',
    'private-package-schema',
    'public-schema',
    'authorization-grammar',
  ];
  if (
    value.execution.correction_gate.repository !== value.execution.coordinates.repository ||
    !decimal.test(value.execution.correction_gate.pull_request_number) ||
    value.execution.correction_gate.branch !== 'codex/issue-181-two-run-product-proof' ||
    !hex40.test(value.execution.correction_gate.commit) ||
    !hex40.test(value.execution.correction_gate.tree) ||
    !Array.isArray(value.execution.correction_gate.authority_identities) ||
    value.execution.correction_gate.authority_identities.length !== authorityComponents.length ||
    value.execution.correction_gate.authority_identities.some((identity, index) => {
      exactKeys(
        identity,
        ['component', 'source_sha256', 'build_sha256'],
        'execution-correction-authority-shape',
      );
      return (
        identity.component !== authorityComponents[index] ||
        !hex64.test(identity.source_sha256) ||
        !hex64.test(identity.build_sha256)
      );
    }) ||
    !Array.isArray(value.execution.correction_gate.contract_digests) ||
    value.execution.correction_gate.contract_digests.length !== contractComponents.length ||
    value.execution.correction_gate.contract_digests.some((identity, index) => {
      exactKeys(identity, ['component', 'sha256'], 'execution-correction-contract-shape');
      return identity.component !== contractComponents[index] || !hex64.test(identity.sha256);
    })
  ) {
    invalid('execution-correction-gate-values');
  }
  exactKeys(value.execution.destinations, ['private', 'public'], 'execution-destinations-shape');
  exactKeys(
    value.execution.destinations.private,
    ['kind', 'identity_sha256'],
    'execution-private-destination-shape',
  );
  exactKeys(
    value.execution.destinations.public,
    ['repository', 'branch', 'pull_request_number', 'worktree_identity_sha256', 'allowed_paths'],
    'execution-public-destination-shape',
  );
  if (
    value.execution.destinations.private.kind !== 'maintainer-approved-host-restricted-location' ||
    !hex64.test(value.execution.destinations.private.identity_sha256) ||
    value.execution.destinations.public.repository !== identities.repository ||
    value.execution.destinations.public.branch !== 'codex/issue-181-two-run-product-proof' ||
    !decimal.test(value.execution.destinations.public.pull_request_number) ||
    value.execution.destinations.public.repository !== value.execution.correction_gate.repository ||
    value.execution.destinations.public.branch !== value.execution.correction_gate.branch ||
    value.execution.destinations.public.pull_request_number !==
      value.execution.correction_gate.pull_request_number ||
    !hex64.test(value.execution.destinations.public.worktree_identity_sha256) ||
    canonicalJson(value.execution.destinations.public.allowed_paths) !==
      canonicalJson([
        'docs/20_architecture/r4-product-proof.md',
        'runtime/tests/fixtures/action-host/trusted-proof/r4-product-proof-public-safe.json',
        'docs/20_architecture/r4-actionhost-wrapper-plan.md',
        'docs/90_roadmap/roadmap-seed.md',
      ])
  ) {
    invalid('execution-destination-values');
  }
  exactKeys(
    value.execution.provider_mode,
    ['provider', 'model', 'adapter', 'endpoint', 'transport', 'canary_contract_sha256'],
    'execution-provider-mode-shape',
  );
  if (
    value.execution.provider_mode.provider !== 'deepseek' ||
    value.execution.provider_mode.model !== 'deepseek-v4-flash' ||
    value.execution.provider_mode.adapter !==
      '968abd371badaa785056ee783553d71763b8a8a6d0d07031f47acc3cfa24d502' ||
    value.execution.provider_mode.endpoint !== 'https://api.deepseek.com/chat/completions' ||
    value.execution.provider_mode.transport !== 'production-https-json' ||
    value.execution.provider_mode.canary_contract_sha256 !==
      value.execution.protected_scan_input.sha256
  ) {
    invalid('execution-provider-mode-values');
  }
  exactKeys(
    value.execution.protected_scan_input,
    ['kind', 'repository', 'operation_ids', 'categories', 'sha256'],
    'execution-protected-scan-input-shape',
  );
  if (
    value.execution.protected_scan_input.kind !== 'apr-r4-e3-operation-canary-binding-v1' ||
    value.execution.protected_scan_input.repository !== identities.repository ||
    canonicalJson(value.execution.protected_scan_input.operation_ids) !==
      canonicalJson(value.execution.operation_ids) ||
    canonicalJson(value.execution.protected_scan_input.categories) !==
      canonicalJson([
        'authorization',
        'state_keys',
        'session_plaintext',
        'provider_content',
        'tool_data',
        'host_evidence',
      ]) ||
    value.execution.protected_scan_input.sha256 !==
      sha256(canonicalJson(buildProtectedScanInput(value, identities.repository)))
  ) {
    invalid('execution-protected-scan-input-values');
  }

  exactKeys(
    value.cleanup,
    ['kind', 'phase', 'source', 'coordinates', 'plan_sha256'],
    'cleanup-authorization-shape',
  );
  validateAuthorizationSource(value.cleanup.source, 'cleanup');
  if (
    value.cleanup.kind !== 'apr-r4-e3-cleanup-authorization-v1' ||
    value.cleanup.phase !== 'cleanup' ||
    !hex64.test(value.cleanup.plan_sha256) ||
    JSON.stringify(value.cleanup.coordinates) !== JSON.stringify(value.setup.coordinates)
  ) {
    invalid('cleanup-authorization-values');
  }
}

function validateApproval(approval, phase) {
  exactKeys(
    approval,
    ['phase', 'run_id', 'run_attempt', 'pending', 'approval', 'protected_job'],
    `approval-${phase}-shape`,
  );
  if (approval.phase !== phase || !decimal.test(approval.run_id) || approval.run_attempt !== 1) {
    invalid(`approval-${phase}-identity`);
  }
  exactKeys(
    approval.pending,
    [
      'run_id',
      'environment_id',
      'environment_name',
      'reviewer_ids',
      'state',
      'source_id',
      'observation',
    ],
    `approval-${phase}-pending-shape`,
  );
  exactKeys(
    approval.approval,
    [
      'run_id',
      'environment_id',
      'environment_name',
      'approving_user_id',
      'state',
      'source_id',
      'observation',
    ],
    `approval-${phase}-approval-shape`,
  );
  for (const [kind, capture] of [
    ['pending', approval.pending],
    ['approval', approval.approval],
  ]) {
    interval(capture.observation, `approval-${phase}-${kind}-interval`);
    if (
      capture.run_id !== approval.run_id ||
      !decimal.test(capture.environment_id) ||
      capture.environment_name !== 'r4-trusted-proof' ||
      capture.state !== (kind === 'pending' ? 'pending' : 'approved') ||
      capture.source_id !== `${phase}-${kind}`
    )
      invalid(`approval-${phase}-${kind}-values`);
  }
  if (
    JSON.stringify(approval.pending.reviewer_ids) !== JSON.stringify(['16307884']) ||
    approval.approval.approving_user_id !== '16307884'
  )
    invalid(`approval-${phase}-actor`);
  if (approval.pending.environment_id !== approval.approval.environment_id) {
    invalid(`approval-${phase}-environment`);
  }
  exactKeys(
    approval.protected_job,
    ['run_id', 'run_attempt', 'name', 'started'],
    `approval-${phase}-job-shape`,
  );
  sourceTimestamp(approval.protected_job.started, `approval-${phase}-job-started`);
  const expectedJobName =
    phase === 'continuation' ? 'workflow-dispatch-review' : 'workflow-run-review';
  if (
    approval.protected_job.run_id !== approval.run_id ||
    approval.protected_job.run_attempt !== 1 ||
    approval.protected_job.name !== expectedJobName ||
    approval.pending.observation.response_received >=
      approval.approval.observation.request_started ||
    approval.approval.observation.response_received >= approval.protected_job.started.value
  ) {
    invalid(`approval-${phase}-job-values`);
  }
}

function validateConcurrency(value, label, expectedIds, expectedGroup) {
  exactKeys(
    value,
    ['api_version', 'group', 'pagination_complete', 'observation', 'ahead_of_run', 'terminal'],
    `concurrency-${label}-shape`,
  );
  interval(value.observation, `concurrency-${label}-interval`);
  if (
    value.api_version !== '2026-03-10' ||
    value.group !== expectedGroup ||
    value.pagination_complete !== true ||
    value.ahead_of_run.length !== 2
  ) {
    invalid(`concurrency-${label}-values`);
  }
  const expected = [
    { run_id: expectedIds[0], position: 0, status: 'in_progress' },
    { run_id: expectedIds[1], position: 1, status: 'pending' },
  ];
  if (JSON.stringify(value.ahead_of_run) !== JSON.stringify(expected)) {
    invalid(`concurrency-${label}-members`);
  }
  exactKeys(
    value.terminal,
    ['holder_run_id', 'waiter_run_id', 'holder_completed', 'waiter_started', 'holder_cancelled'],
    `concurrency-${label}-terminal-shape`,
  );
  sourceTimestamp(value.terminal.holder_completed, `concurrency-${label}-holder-terminal`);
  sourceTimestamp(value.terminal.waiter_started, `concurrency-${label}-waiter-terminal`);
  if (
    value.terminal.holder_run_id !== expectedIds[0] ||
    value.terminal.waiter_run_id !== expectedIds[1] ||
    value.terminal.holder_cancelled !== false ||
    value.terminal.holder_completed.value >= value.terminal.waiter_started.value
  ) {
    invalid(`concurrency-${label}-terminal-order`);
  }
}

function validateControls(value, operationId, expectedKinds, coordinates, code) {
  exactKeys(value, ['operation_id', 'comments', 'cleanup_outcomes'], `${code}-shape`);
  if (value.operation_id !== operationId || value.comments.length !== expectedKinds.length) {
    invalid(`${code}-identity`);
  }
  const ids = [];
  for (let index = 0; index < expectedKinds.length; index += 1) {
    const comment = value.comments[index];
    exactKeys(
      comment,
      [
        'kind',
        'comment_id',
        'predecessor_comment_id',
        'operation_id',
        'run_id',
        'run_attempt',
        'repository',
        'pr_number',
        'fixture_head_sha',
        'workflow_sha',
        'action_source_sha',
        'payload_sha256',
        'actor_id',
        'actor_permission',
        'observation',
        'body_preimage',
        'capture_body_sha256',
        'body_sha256',
        'readback_sha256',
      ],
      `${code}-comment-shape`,
    );
    const expectedPredecessor = index % 2 === 1 ? ids[index - 1] : null;
    interval(comment.observation, `${code}-comment-observation`);
    const marker = {
      contract: 'apr-r4-e2p-proof-control-v1',
      kind: comment.kind,
      operation_id: operationId,
      repository_id: Number(coordinates.repositoryId),
      repository: coordinates.repository,
      pr_number: Number(coordinates.prNumber),
      fixture_head_sha: coordinates.fixtureHeadSha,
      workflow_sha: coordinates.workflowSha,
      action_source_sha: coordinates.actionSourceSha,
      payload_sha256: coordinates.payloadSha256,
      run_id: Number(comment.run_id),
      run_attempt: comment.run_attempt,
      predecessor_comment_id: expectedPredecessor === null ? null : Number(expectedPredecessor),
      body_sha256: '',
    };
    const expectedPreimage = JSON.stringify(marker);
    const markerSha256 = sha256(Buffer.from(expectedPreimage, 'utf8'));
    const body = `<!-- apr-r4-e2p-control ${JSON.stringify({
      ...marker,
      body_sha256: markerSha256,
    })} -->`;
    const readyActor = comment.kind === 'ready' || comment.kind === 'stale-ready';
    if (
      comment.kind !== expectedKinds[index] ||
      !decimal.test(comment.comment_id) ||
      ids.includes(comment.comment_id) ||
      comment.predecessor_comment_id !== expectedPredecessor ||
      comment.operation_id !== operationId ||
      !decimal.test(comment.run_id) ||
      comment.run_attempt !== 1 ||
      comment.repository !== coordinates.repository ||
      comment.pr_number !== coordinates.prNumber ||
      comment.fixture_head_sha !== coordinates.fixtureHeadSha ||
      comment.workflow_sha !== coordinates.workflowSha ||
      comment.action_source_sha !== coordinates.actionSourceSha ||
      comment.payload_sha256 !== coordinates.payloadSha256 ||
      (readyActor
        ? comment.actor_id !== '41898282' || comment.actor_permission !== 'workflow-token'
        : comment.actor_id !== '16307884' ||
          !['admin', 'write'].includes(comment.actor_permission)) ||
      comment.body_preimage !== expectedPreimage ||
      !hex64.test(comment.capture_body_sha256) ||
      comment.body_sha256 !== markerSha256 ||
      !hex64.test(comment.body_sha256) ||
      comment.readback_sha256 !== sha256(Buffer.from(body, 'utf8'))
    ) {
      invalid(`${code}-comment-values`);
    }
    ids.push(comment.comment_id);
  }
  const outcomes = value.cleanup_outcomes.map((item) => {
    exactKeys(item, ['comment_id', 'outcome'], `${code}-outcome-shape`);
    return [item.comment_id, item.outcome];
  });
  if (JSON.stringify(outcomes) !== JSON.stringify(ids.map((id) => [id, 'deleted-absent']))) {
    invalid(`${code}-cleanup`);
  }
}

function validateInventories(value, operationIds) {
  exactKeys(value, ['expected_success', 'observed_cleanup'], 'inventory-shape');
  if (value.expected_success.length !== expectedProductRoles.length)
    invalid('success-inventory-count');
  const roles = value.expected_success.map((record) => record.role);
  exactArray(roles, expectedProductRoles, 'success-inventory-roles');
  const successIds = new Set();
  for (const [index, record] of value.expected_success.entries()) {
    exactKeys(
      record,
      ['artifact_id', 'role', 'scope', 'object_class', 'authenticated', 'operation_owned'],
      'success-record-shape',
    );
    if (
      !decimal.test(record.artifact_id) ||
      successIds.has(record.artifact_id) ||
      !['repository', 'normal', 'stale'].includes(record.scope) ||
      JSON.stringify([record.role, record.scope, record.object_class]) !==
        JSON.stringify(expectedProductRoleShape[index]) ||
      record.authenticated !== true ||
      record.operation_owned !== true
    ) {
      invalid('success-record-values');
    }
    successIds.add(record.artifact_id);
  }
  const observedIds = new Set();
  for (const record of value.observed_cleanup) {
    exactKeys(
      record,
      [
        'artifact_id',
        'object_class',
        'scope',
        'operation_id',
        'artifact_name',
        'producing_run_id',
        'producing_run_attempt',
        'archive_sha256',
        'encrypted_object_sha256',
        'encrypted_object_size',
        'ownership_evidence_sha256',
        'authenticated',
        'operation_owned',
        'disposition',
      ],
      'observed-record-shape',
    );
    if (
      !decimal.test(record.artifact_id) ||
      observedIds.has(record.artifact_id) ||
      !['repository', 'normal', 'stale'].includes(record.scope) ||
      !operationIds.includes(record.operation_id) ||
      typeof record.artifact_name !== 'string' ||
      record.artifact_name.length === 0 ||
      !decimal.test(record.producing_run_id) ||
      record.producing_run_attempt !== 1 ||
      !hex64.test(record.archive_sha256) ||
      !hex64.test(record.encrypted_object_sha256) ||
      !decimal.test(record.encrypted_object_size) ||
      !hex64.test(record.ownership_evidence_sha256) ||
      record.authenticated !== true ||
      record.operation_owned !== true ||
      !['delete', 'recovery-only-delete'].includes(record.disposition)
    ) {
      invalid('observed-record-values');
    }
    observedIds.add(record.artifact_id);
  }
  if ([...successIds].some((id) => !observedIds.has(id))) invalid('inventory-success-not-observed');
  const recovery = value.observed_cleanup.filter(
    (record) => record.disposition === 'recovery-only-delete',
  );
  const ordinary = value.observed_cleanup.filter((record) => record.disposition === 'delete');
  if (
    ordinary.length !== expectedCleanupShape.length ||
    JSON.stringify(
      ordinary.map((record) => [
        record.object_class,
        record.scope,
        operationIds.indexOf(record.operation_id),
      ]),
    ) !== JSON.stringify(expectedCleanupShape)
  ) {
    invalid('observed-inventory-shape');
  }
  for (const expected of value.expected_success) {
    const observed = value.observed_cleanup.find(
      ({ artifact_id }) => artifact_id === expected.artifact_id,
    );
    if (
      !observed ||
      observed.object_class !== expected.object_class ||
      observed.scope !== expected.scope ||
      observed.disposition !== 'delete'
    ) {
      invalid('inventory-success-mismatch');
    }
  }
  return { successIds, recoveryOnly: recovery.length > 0, observedIds };
}

export function generateCleanupPlan(input) {
  exactKeys(
    input,
    ['operation_ids', 'proof_control', 'observed_cleanup', 'resources'],
    'cleanup-plan-input-shape',
  );
  if (
    input.operation_ids.length !== 2 ||
    input.operation_ids.some((id) => !hex64.test(id)) ||
    new Set(input.operation_ids).size !== 2 ||
    !Array.isArray(input.observed_cleanup) ||
    input.observed_cleanup.some(
      (record) =>
        JSON.stringify(Object.keys(record)) !==
          JSON.stringify([
            'artifact_id',
            'object_class',
            'scope',
            'operation_id',
            'artifact_name',
            'producing_run_id',
            'producing_run_attempt',
            'archive_sha256',
            'encrypted_object_sha256',
            'encrypted_object_size',
            'ownership_evidence_sha256',
            'authenticated',
            'operation_owned',
            'disposition',
          ]) ||
        record.authenticated !== true ||
        record.operation_owned !== true ||
        !input.operation_ids.includes(record.operation_id) ||
        !decimal.test(record.artifact_id) ||
        !['repository', 'normal', 'stale'].includes(record.scope) ||
        typeof record.artifact_name !== 'string' ||
        !decimal.test(record.producing_run_id) ||
        !Number.isSafeInteger(record.producing_run_attempt) ||
        record.producing_run_attempt <= 0 ||
        !hex64.test(record.archive_sha256) ||
        !hex64.test(record.encrypted_object_sha256) ||
        !decimal.test(record.encrypted_object_size) ||
        !hex64.test(record.ownership_evidence_sha256) ||
        !['delete', 'recovery-only-delete'].includes(record.disposition),
    )
  ) {
    invalid('cleanup-plan-ownership');
  }
  if (
    new Set(input.observed_cleanup.map(({ artifact_id }) => artifact_id)).size !==
    input.observed_cleanup.length
  ) {
    invalid('cleanup-plan-ownership');
  }
  exactKeys(input.proof_control, ['normal', 'stale'], 'cleanup-plan-control-shape');
  const controlIds = [];
  for (const [family, operationId, expectedKinds] of [
    [input.proof_control.normal, input.operation_ids[0], ['ready', 'release']],
    [
      input.proof_control.stale,
      input.operation_ids[1],
      ['ready', 'release', 'stale-ready', 'stale-release'],
    ],
  ]) {
    exactKeys(
      family,
      ['operation_id', 'comments', 'cleanup_outcomes'],
      'cleanup-plan-control-family-shape',
    );
    if (
      family.operation_id !== operationId ||
      !Array.isArray(family.comments) ||
      family.comments.length > expectedKinds.length ||
      !Array.isArray(family.cleanup_outcomes) ||
      family.cleanup_outcomes.length !== family.comments.length
    ) {
      invalid('cleanup-plan-control-values');
    }
    const familyIds = [];
    for (let index = 0; index < family.comments.length; index += 1) {
      const comment = family.comments[index];
      const expectedPredecessor = index % 2 === 1 ? familyIds[index - 1] : null;
      if (
        comment.kind !== expectedKinds[index] ||
        !decimal.test(comment.comment_id) ||
        comment.operation_id !== operationId ||
        comment.predecessor_comment_id !== expectedPredecessor
      ) {
        invalid('cleanup-plan-control-values');
      }
      familyIds.push(comment.comment_id);
      controlIds.push(comment.comment_id);
      if (family.cleanup_outcomes[index]?.comment_id !== comment.comment_id) {
        invalid('cleanup-plan-control-values');
      }
    }
  }
  if (new Set(controlIds).size !== controlIds.length) invalid('cleanup-plan-control-values');
  exactKeys(
    input.resources,
    [
      'authorization_variable',
      'secret_names',
      'environment',
      'fixture_refs',
      'fixture_pr_numbers',
      'credential_copies',
      'environment_snapshot_sha256',
      'run_ids',
      'sticky',
    ],
    'cleanup-plan-resource-shape',
  );
  const allowedSecretProfiles = [
    ['DEEPSEEK_API_KEY', 'AGENTIC_PR_REVIEW_STATE_KEY'],
    ['DEEPSEEK_API_KEY', 'AGENTIC_PR_REVIEW_STATE_KEY', 'AGENTIC_PR_REVIEW_PREVIOUS_STATE_KEY'],
  ];
  const selectedSecretProfile = allowedSecretProfiles.find(
    (profile) => canonicalJson(profile) === canonicalJson(input.resources.secret_names),
  );
  const expectedCredentialCopies =
    selectedSecretProfile?.length === 2
      ? ['github-token', 'current-state-key']
      : ['github-token', 'current-state-key', 'previous-state-key'];
  if (
    input.resources.authorization_variable !== 'R4_TRUSTED_PROOF_AUTHORIZATION' ||
    selectedSecretProfile === undefined ||
    input.resources.environment !== 'r4-trusted-proof' ||
    input.resources.fixture_refs.length !== 2 ||
    new Set(input.resources.fixture_refs).size !== 2 ||
    input.resources.fixture_refs.some((value) => !fixtureRef.test(value)) ||
    input.resources.fixture_pr_numbers.length !== 2 ||
    input.resources.fixture_pr_numbers.some((value) => !decimal.test(value)) ||
    new Set(input.resources.fixture_pr_numbers).size !== 2 ||
    JSON.stringify(input.resources.credential_copies) !==
      JSON.stringify(expectedCredentialCopies) ||
    !hex64.test(input.resources.environment_snapshot_sha256) ||
    input.resources.run_ids.length > 64 ||
    input.resources.run_ids.some((value) => !decimal.test(value)) ||
    new Set(input.resources.run_ids).size !== input.resources.run_ids.length
  ) {
    invalid('cleanup-plan-resource-values');
  }
  if (input.resources.sticky !== null) {
    exactKeys(
      input.resources.sticky,
      ['pr_number', 'comment_id', 'body_sha256', 'marker_sha256'],
      'cleanup-sticky-shape',
    );
    if (
      !decimal.test(input.resources.sticky.pr_number) ||
      !decimal.test(input.resources.sticky.comment_id) ||
      !hex64.test(input.resources.sticky.body_sha256) ||
      !hex64.test(input.resources.sticky.marker_sha256)
    )
      invalid('cleanup-sticky-values');
  }
  const stateTargets = input.observed_cleanup
    .map((record) => ({
      artifact_id: record.artifact_id,
      artifact_name: record.artifact_name,
      object_class: record.object_class,
      scope: record.scope,
      operation_id: record.operation_id,
      producing_run_id: record.producing_run_id,
      producing_run_attempt: record.producing_run_attempt,
      archive_sha256: record.archive_sha256,
      encrypted_object_sha256: record.encrypted_object_sha256,
      encrypted_object_size: record.encrypted_object_size,
      ownership_evidence_sha256: record.ownership_evidence_sha256,
      disposition: record.disposition,
      mutation: 'delete-action-artifact',
      expected_response: '204-or-404-after-reconciliation',
      outcome_unknown: 're-read-exact-artifact-id-before-retry',
      post_readback: 'artifact-id-absent',
    }))
    .sort((a, b) => a.artifact_id.localeCompare(b.artifact_id, 'en', { numeric: true }));
  const plan = {
    kind: 'apr-r4-e3-cleanup-plan-v1',
    operation_ids: [...input.operation_ids],
    phases: cleanupPhases.map((phase) => ({
      phase,
      precondition: 'exact-readback-required',
      postcondition: 'exact-readback-required',
    })),
    targets: {
      control_comment_ids: controlIds,
      control_comments: controlIds.map((comment_id) => ({
        comment_id,
        mutation: 'delete-issue-comment',
        expected_response: '204-or-404-after-reconciliation',
        outcome_unknown: 're-read-exact-comment-id-before-retry',
        post_readback: 'comment-id-absent',
      })),
      state_artifacts: stateTargets,
      authorization_variable: {
        name: input.resources.authorization_variable,
        mutation: 'delete-actions-variable',
        expected_response: '204-or-404-after-reconciliation',
        outcome_unknown: 'read-exact-variable-name-before-retry',
        post_readback: 'variable-name-absent',
      },
      secrets: input.resources.secret_names.map((name) => ({
        environment: input.resources.environment,
        name,
        mutation: 'delete-environment-secret',
        expected_response: '204-or-404-after-reconciliation',
        outcome_unknown: 'read-exact-secret-name-before-retry',
        post_readback: 'secret-name-absent',
      })),
      environment: {
        name: input.resources.environment,
        restore_snapshot_sha256: input.resources.environment_snapshot_sha256,
        mutation: 'restore-environment-protection',
        expected_response: '200-or-readback-reconciliation',
        outcome_unknown: 'read-current-environment-before-retry',
        post_readback: 'snapshot-digest-equals',
      },
      fixture_refs: input.resources.fixture_refs.map((ref) => ({
        ref,
        mutation: 'delete-git-ref',
        expected_response: '204-or-422-after-reconciliation',
        outcome_unknown: 'read-exact-ref-before-retry',
        post_readback: 'ref-absent',
      })),
      fixture_prs: input.resources.fixture_pr_numbers.map((number) => ({
        number,
        mutation: 'close-pull-request',
        expected_response: '200-or-readback-reconciliation',
        outcome_unknown: 'read-exact-pr-state-before-retry',
        post_readback: 'closed',
      })),
      credential_copies: input.resources.credential_copies.map((name) => ({
        name,
        mutation: 'delete-local-file',
        expected_response: 'deleted-or-already-absent',
        outcome_unknown: 'reopen-approved-root-name-before-retry',
        post_readback: 'file-name-absent',
      })),
      runs: [...input.resources.run_ids]
        .sort((a, b) => a.localeCompare(b, 'en', { numeric: true }))
        .map((run_id) => ({
          run_id,
          mutation: 'none',
          precondition: 'terminal-before-cleanup-entry',
          post_readback: 'terminal-and-not-queued-or-active',
        })),
      final_state_enumeration: ['repository-root', 'normal', 'stale'].map((scope) => ({
        scope,
        mutation: 'none',
        pagination: 'complete-cursor',
        post_readback: 'empty',
      })),
      sticky:
        input.resources.sticky === null
          ? null
          : {
              ...input.resources.sticky,
              mutation: 'none-retain',
              outcome_unknown: 'read-exact-comment-before-any-retry',
              post_readback: 'exact-body-and-marker-retained',
            },
    },
  };
  return { plan, canonical: canonicalJson(plan), digest: sha256(canonicalJson(plan)) };
}

export function validateHostEvidence(input) {
  exactKeys(
    input,
    [
      'kind',
      'identities',
      'source_map',
      'authorizations',
      'environment',
      'approval_transitions',
      'concurrency',
      'proof_control',
      'inventories',
      'cleanup',
      'canaries',
      'restricted_package',
    ],
    'host-shape',
  );
  if (input.kind !== 'apr-r4-e3-host-restricted-evidence-v1') invalid('host-kind');
  exactKeys(
    input.identities,
    [
      'workflow_sha',
      'action_source_sha',
      'payload_source_sha',
      'payload_sha256',
      'oracle_source_sha',
      'oracle_source_tree',
      'repository_id',
      'repository',
      'normal_pr_number',
      'stale_pr_number',
      'unauthorized_follow_on',
      'operation_ids',
    ],
    'identity-shape',
  );
  if (
    !hex40.test(input.identities.workflow_sha) ||
    input.identities.action_source_sha !== '5b5769753653bb3fd3e68cf8b7bb88a1bd350613' ||
    input.identities.payload_source_sha !== 'edc594c29a8a6b5fdacfab48643bf221277af200' ||
    input.identities.payload_sha256 !==
      'b6405d21987a549540b071215f215cf15339729cb3905ad3294c88bc2edf8c0e' ||
    !hex40.test(input.identities.oracle_source_sha) ||
    !hex40.test(input.identities.oracle_source_tree) ||
    !decimal.test(input.identities.repository_id) ||
    input.identities.repository !== 'SolusQuest/agentic-pr-review' ||
    !decimal.test(input.identities.normal_pr_number) ||
    !decimal.test(input.identities.stale_pr_number) ||
    JSON.stringify(Object.keys(input.identities.unauthorized_follow_on)) !==
      JSON.stringify([
        'run_id',
        'run_attempt',
        'event',
        'pr_number',
        'advanced_head_sha',
        'terminal_result',
      ]) ||
    !decimal.test(input.identities.unauthorized_follow_on.run_id) ||
    input.identities.unauthorized_follow_on.run_attempt !== 1 ||
    input.identities.unauthorized_follow_on.event !== 'workflow_run' ||
    input.identities.unauthorized_follow_on.pr_number !== input.identities.stale_pr_number ||
    !hex40.test(input.identities.unauthorized_follow_on.advanced_head_sha) ||
    input.identities.unauthorized_follow_on.terminal_result !== 'inert-unauthorized' ||
    input.identities.operation_ids.length !== 2 ||
    input.identities.operation_ids.some((id) => !hex64.test(id))
  ) {
    invalid('identity-values');
  }
  validateSourceMap(input.source_map);
  validateSourceBindings(input);
  validateAuthorization(input.authorizations, input.identities);
  exactKeys(input.environment, ['name', 'readiness_snapshot'], 'environment-shape');
  const readiness = input.environment.readiness_snapshot;
  exactKeys(
    readiness,
    [
      'source_ids',
      'environment_id',
      'can_admins_bypass',
      'deployment_branch_policy',
      'required_reviewer_ids',
      'prevent_self_review',
      'branch_policies',
      'secret_names',
      'token_permissions',
      'readback_sha256',
      'observation',
    ],
    'environment-readiness-shape',
  );
  exactKeys(
    readiness.source_ids,
    ['environment', 'branch_policies', 'environment_secrets'],
    'environment-source-ids-shape',
  );
  exactKeys(
    readiness.deployment_branch_policy,
    ['protected_branches', 'custom_branch_policies'],
    'environment-deployment-policy-shape',
  );
  interval(readiness.observation, 'environment-readiness-observation');
  if (
    input.environment.name !== 'r4-trusted-proof' ||
    canonicalJson(readiness.source_ids) !==
      canonicalJson({
        environment: 'readiness-stale-environment-protection',
        branch_policies: 'readiness-stale-environment-branch-policies',
        environment_secrets: 'readiness-stale-environment-secret-inventory',
      }) ||
    !decimal.test(readiness.environment_id) ||
    readiness.can_admins_bypass !== false ||
    readiness.deployment_branch_policy.protected_branches !== false ||
    readiness.deployment_branch_policy.custom_branch_policies !== true ||
    JSON.stringify(readiness.required_reviewer_ids) !== '["16307884"]' ||
    readiness.prevent_self_review !== false ||
    canonicalJson(readiness.branch_policies) !==
      canonicalJson([{ id: '58463845', name: 'main', type: 'branch' }]) ||
    JSON.stringify(readiness.secret_names) !==
      JSON.stringify(
        input.authorizations.execution.active_secret_profile.environment_secret_names,
      ) ||
    canonicalJson(readiness.token_permissions) !==
      canonicalJson({ actions: 'write', contents: 'read', pull_requests: 'write' }) ||
    !hex64.test(readiness.readback_sha256)
  ) {
    invalid('environment-readiness-values');
  }
  exactKeys(input.approval_transitions, ['bootstrap', 'continuation', 'stale'], 'approval-shape');
  validateApproval(input.approval_transitions.bootstrap, 'bootstrap');
  validateApproval(input.approval_transitions.continuation, 'continuation');
  validateApproval(input.approval_transitions.stale, 'stale');
  const runIds = [
    input.approval_transitions.bootstrap.run_id,
    input.approval_transitions.continuation.run_id,
    input.approval_transitions.stale.run_id,
  ];
  if (new Set(runIds).size !== runIds.length) invalid('approval-run-reuse');
  exactKeys(input.concurrency, ['normal', 'stale'], 'concurrency-shape');
  validateConcurrency(
    input.concurrency.normal,
    'normal',
    [runIds[0], runIds[1]],
    `agentic-pr-review-r4-${input.identities.repository_id}-pr-${input.identities.normal_pr_number}`,
  );
  const staleFollowOn = input.identities.unauthorized_follow_on.run_id;
  validateConcurrency(
    input.concurrency.stale,
    'stale',
    [runIds[2], staleFollowOn],
    `agentic-pr-review-r4-${input.identities.repository_id}-pr-${input.identities.stale_pr_number}`,
  );
  if (
    input.concurrency.normal.group === input.concurrency.stale.group ||
    input.concurrency.normal.ahead_of_run.some((member) =>
      input.concurrency.stale.ahead_of_run.some((other) => other.run_id === member.run_id),
    )
  ) {
    invalid('concurrency-cross-group');
  }
  exactKeys(input.proof_control, ['normal', 'stale'], 'proof-control-shape');
  validateControls(
    input.proof_control.normal,
    input.identities.operation_ids[0],
    ['ready', 'release'],
    {
      repositoryId: input.identities.repository_id,
      repository: input.identities.repository,
      prNumber: input.identities.normal_pr_number,
      fixtureHeadSha: input.authorizations.execution.fixture_prs[0].head_sha,
      workflowSha: input.identities.workflow_sha,
      actionSourceSha: input.identities.action_source_sha,
      payloadSha256: input.identities.payload_sha256,
    },
    'proof-control-normal',
  );
  validateControls(
    input.proof_control.stale,
    input.identities.operation_ids[1],
    ['ready', 'release', 'stale-ready', 'stale-release'],
    {
      repositoryId: input.identities.repository_id,
      repository: input.identities.repository,
      prNumber: input.identities.stale_pr_number,
      fixtureHeadSha: input.authorizations.execution.fixture_prs[1].head_sha,
      workflowSha: input.identities.workflow_sha,
      actionSourceSha: input.identities.action_source_sha,
      payloadSha256: input.identities.payload_sha256,
    },
    'proof-control-stale',
  );
  const inventory = validateInventories(input.inventories, input.identities.operation_ids);
  const generated = generateCleanupPlan({
    operation_ids: input.identities.operation_ids,
    proof_control: input.proof_control,
    observed_cleanup: input.inventories.observed_cleanup,
    resources: input.cleanup.resources,
  });
  if (
    generated.digest !== input.authorizations.cleanup.plan_sha256 ||
    generated.digest !== input.cleanup.plan_sha256
  )
    invalid('cleanup-plan-digest');
  exactKeys(
    input.cleanup,
    [
      'plan_sha256',
      'execution_journal_sha256',
      'entry_observation',
      'resources',
      'entry_gate',
      'ordered_readbacks',
      'projection_gate',
    ],
    'cleanup-shape',
  );
  if (!hex64.test(input.cleanup.execution_journal_sha256)) invalid('cleanup-execution-digest');
  interval(input.cleanup.entry_observation, 'cleanup-entry-observation');
  exactKeys(
    input.cleanup.entry_gate,
    [
      'all_runs_terminal',
      'no_runs_queued_or_active',
      'captures_complete',
      'inventory_sealed',
      'artifacts_captured',
      'plan_approved',
    ],
    'cleanup-entry-shape',
  );
  if (Object.values(input.cleanup.entry_gate).some((value) => value !== true))
    invalid('cleanup-entry');
  exactArray(
    input.cleanup.ordered_readbacks.map((item) => item.phase),
    cleanupPhases,
    'cleanup-order',
  );
  if (
    input.cleanup.ordered_readbacks.some((item) => {
      exactKeys(
        item,
        ['phase', 'observation', 'mutations', 'source_ids'],
        'cleanup-readback-shape',
      );
      interval(item.observation, 'cleanup-readback-observation');
      return (
        !Array.isArray(item.source_ids) ||
        item.source_ids.some(
          (sourceId) =>
            typeof sourceId !== 'string' ||
            (!/^post-cleanup-[a-z0-9-]+:page:[1-9][0-9]*$/u.test(sourceId) &&
              sourceId !== 'cleanup-execution:local-credential-absence'),
        ) ||
        !Array.isArray(item.mutations) ||
        item.mutations.some((mutation) => {
          exactKeys(mutation, ['target_id', 'outcome', 'source_ids'], 'cleanup-mutation-shape');
          return (
            typeof mutation.target_id !== 'string' ||
            mutation.target_id.length === 0 ||
            ![
              'committed',
              'known-not-sent-absent',
              'missing-idempotent',
              'reconciled-outcome-unknown',
            ].includes(mutation.outcome) ||
            !Array.isArray(mutation.source_ids) ||
            mutation.source_ids.length === 0 ||
            mutation.source_ids.some((sourceId) => !item.source_ids.includes(sourceId))
          );
        })
      );
    })
  ) {
    invalid('cleanup-readback');
  }
  exactKeys(
    input.cleanup.projection_gate,
    [
      'exact_seven_success',
      'control_absent',
      'state_empty_complete',
      'authorization_absent',
      'secret_names_absent',
      'environment_restored',
      'fixtures_terminal',
      'all_runs_terminal',
      'sticky_exact',
      'credential_copies_absent',
      'private_manifest_inputs_sealed',
    ],
    'projection-gate-shape',
  );
  const projectionEligible = !inventory.recoveryOnly;
  if (
    input.cleanup.projection_gate.exact_seven_success !== projectionEligible ||
    Object.entries(input.cleanup.projection_gate)
      .filter(([name]) => name !== 'exact_seven_success')
      .some(([, value]) => value !== true)
  )
    invalid('projection-gate');
  exactKeys(input.canaries, ['live', 'cross_sink', 'public_leak_scan'], 'canary-shape');
  exactKeys(input.canaries.live, ['source', 'facts'], 'canary-live-shape');
  exactKeys(input.canaries.cross_sink, ['source', 'result'], 'canary-cross-sink-shape');
  exactKeys(
    input.canaries.public_leak_scan,
    ['source', 'candidate_sha256', 'corpus_sha256', 'scanned_files', 'results'],
    'canary-leak-shape',
  );
  exactArray(
    input.canaries.live.facts,
    ['github-route-observed', 'provider-route-observed', 'state-route-observed'],
    'canary-live-facts',
  );
  exactKeys(
    input.canaries.public_leak_scan.results,
    [
      'authorization',
      'state_keys',
      'session_plaintext',
      'provider_content',
      'tool_data',
      'host_evidence',
    ],
    'canary-leak-results-shape',
  );
  if (
    input.canaries.live.source !== 'checked-runtime-observations' ||
    input.canaries.cross_sink.source !== 'descendant-v2-receipt' ||
    input.canaries.cross_sink.result !== 'isolated' ||
    input.canaries.public_leak_scan.source !== 'post-cleanup-repository-and-output-scan' ||
    !hex64.test(input.canaries.public_leak_scan.candidate_sha256) ||
    !hex64.test(input.canaries.public_leak_scan.corpus_sha256) ||
    !Number.isInteger(input.canaries.public_leak_scan.scanned_files) ||
    input.canaries.public_leak_scan.scanned_files < 1 ||
    Object.values(input.canaries.public_leak_scan.results).some((value) => value !== 'absent')
  ) {
    invalid('canary-values');
  }
  exactKeys(
    input.restricted_package,
    [
      'destination_kind',
      'destination_identity_sha256',
      'capture_manifest_sha256',
      'oracle_result_sha256',
      'token_copy_absent',
      'current_key_copy_absent',
      'previous_key_copy_absent',
      'manifest_finalized',
    ],
    'restricted-package-shape',
  );
  if (
    input.restricted_package.destination_kind !== 'maintainer-approved-host-restricted-location' ||
    [
      input.restricted_package.destination_identity_sha256,
      input.restricted_package.capture_manifest_sha256,
      input.restricted_package.oracle_result_sha256,
    ].some((item) => !hex64.test(item)) ||
    input.restricted_package.token_copy_absent !== true ||
    input.restricted_package.current_key_copy_absent !== true ||
    input.restricted_package.previous_key_copy_absent !== true ||
    input.restricted_package.manifest_finalized !== true ||
    input.cleanup.projection_gate.private_manifest_inputs_sealed !== true
  ) {
    invalid('restricted-package-values');
  }
  return { cleanupPlan: generated.plan, inventory, projectionEligible };
}

export function validateCaptureManifest(value, host, captureManifestSha256) {
  exactKeys(
    value,
    [
      'kind',
      'repository_id',
      'repository',
      'operation_ids',
      'execution_authorization_sha256',
      'producer_journal_directory',
      'producer_journal_seal_sha256',
      'producer_journal_seal_file_identity',
      'disposition',
      'expected_roles',
      'observed_runs',
      'source_map_sha256',
      'destination_identity_sha256',
      'phase_fragment_journal_path',
      'phase_fragment_journal_sha256',
      'phase_fragment_journal_file_identity',
      'sources',
      'artifacts',
      'finalized',
    ],
    'capture-manifest-shape',
  );
  if (
    value.kind !== 'apr-r4-e3-capture-manifest-v1' ||
    value.repository_id !== host.identities.repository_id ||
    value.repository !== host.identities.repository ||
    JSON.stringify(value.operation_ids) !== JSON.stringify(host.identities.operation_ids) ||
    !hex64.test(value.execution_authorization_sha256) ||
    !/^producer-journal-[a-z0-9-]+$/u.test(value.producer_journal_directory) ||
    !hex64.test(value.producer_journal_seal_sha256) ||
    !hex64.test(value.producer_journal_seal_file_identity) ||
    !['success-candidate', 'recovery-only'].includes(value.disposition) ||
    !Array.isArray(value.expected_roles) ||
    value.expected_roles.length > 4 ||
    !Array.isArray(value.observed_runs) ||
    value.observed_runs.length < value.expected_roles.length ||
    value.source_map_sha256 !== sha256(canonicalJson(host.source_map)) ||
    value.destination_identity_sha256 !== host.restricted_package.destination_identity_sha256 ||
    value.phase_fragment_journal_path !== 'phase-fragment-journal.json' ||
    !hex64.test(value.phase_fragment_journal_sha256) ||
    !hex64.test(value.phase_fragment_journal_file_identity) ||
    value.finalized !== true ||
    !Array.isArray(value.sources) ||
    value.sources.length === 0 ||
    !Array.isArray(value.artifacts) ||
    captureManifestSha256 !== host.restricted_package.capture_manifest_sha256
  ) {
    invalid('capture-manifest-values');
  }
  const successCandidate = value.disposition === 'success-candidate';
  if (
    (successCandidate && value.expected_roles.length !== 4) ||
    (successCandidate && (value.observed_runs.length !== 4 || value.artifacts.length === 0))
  ) {
    invalid('capture-manifest-disposition');
  }
  const expectedRoleNames = [
    'normal-bootstrap',
    'normal-continuation',
    'stale-protected',
    'stale-follow-on',
  ];
  const operationByRun = new Map();
  for (const run of value.observed_runs) {
    exactKeys(
      run,
      ['operation_id', 'scope', 'run_id', 'run_attempt'],
      'capture-observed-run-shape',
    );
    if (
      operationByRun.has(run.run_id) ||
      !value.operation_ids.includes(run.operation_id) ||
      !['normal', 'stale'].includes(run.scope) ||
      !decimal.test(run.run_id) ||
      !decimal.test(run.run_attempt)
    ) {
      invalid('capture-observed-run-values');
    }
    operationByRun.set(run.run_id, run);
  }
  const successfulRunIds = new Set();
  value.expected_roles.forEach((role, index) => {
    exactKeys(
      role,
      ['role', 'operation_id', 'scope', 'run_id', 'run_attempt', 'producer_source_ids'],
      'capture-expected-role-shape',
    );
    const triggerIndex = expectedRoleNames.indexOf(role.role);
    const trigger = host.authorizations.execution.trigger_plan[triggerIndex];
    if (
      triggerIndex < 0 ||
      (index > 0 &&
        triggerIndex <= expectedRoleNames.indexOf(value.expected_roles[index - 1].role)) ||
      role.operation_id !== trigger.operation_id ||
      role.scope !== trigger.scope ||
      role.run_attempt !== '1' ||
      !decimal.test(role.run_id) ||
      successfulRunIds.has(role.run_id) ||
      !Array.isArray(role.producer_source_ids) ||
      role.producer_source_ids.length === 0 ||
      new Set(role.producer_source_ids).size !== role.producer_source_ids.length ||
      !value.observed_runs.some(
        (run) =>
          run.operation_id === role.operation_id &&
          run.scope === role.scope &&
          run.run_id === role.run_id &&
          run.run_attempt === role.run_attempt,
      )
    ) {
      invalid('capture-expected-role-values');
    }
    successfulRunIds.add(role.run_id);
  });
  if (
    value.operation_ids.some(
      (operationId) =>
        value.expected_roles.filter(({ operation_id }) => operation_id === operationId).length > 2,
    ) ||
    (successCandidate &&
      (value.expected_roles.filter(({ scope }) => scope === 'normal').length !== 2 ||
        value.expected_roles.filter(({ scope }) => scope === 'stale').length !== 2))
  ) {
    invalid('capture-expected-role-values');
  }
  const sourceIds = new Set();
  const sourcePaths = new Set();
  for (const source of value.sources) {
    const sourceIdentity = /^(?<family>.+):page:(?<page>[1-9][0-9]*)$/u.exec(source.source_id);
    const expectedVariableAbsence =
      /^baseline-(normal|stale)-authorization-variable:page:1$/u.test(source.source_id) &&
      source.status === 404;
    exactKeys(
      source,
      [
        'source_id',
        'operation_id',
        'phase',
        'route',
        'page',
        'status',
        'body_path',
        'body_sha256',
        'body_size',
        'body_file_identity',
        'safe_headers_sha256',
        'request_started_unix_milliseconds',
        'response_received_unix_milliseconds',
        'next_route',
      ],
      'capture-source-shape',
    );
    if (
      sourceIds.has(source.source_id) ||
      sourcePaths.has(source.body_path) ||
      !value.operation_ids.includes(source.operation_id) ||
      source.phase !==
        expectedCaptureSourcePhase(source.source_id, source.operation_id, value.operation_ids) ||
      sourceIdentity === null ||
      Number(sourceIdentity.groups.page) !== source.page ||
      !source.route.startsWith(`/repos/${host.identities.repository}/`) ||
      !Number.isSafeInteger(source.page) ||
      source.page < 1 ||
      (source.status !== 200 && !expectedVariableAbsence) ||
      !/^source-[0-9]{4}\.json$/u.test(source.body_path) ||
      !decimal.test(source.body_size) ||
      !hex64.test(source.body_sha256) ||
      !hex64.test(source.body_file_identity) ||
      !hex64.test(source.safe_headers_sha256) ||
      !Number.isSafeInteger(source.request_started_unix_milliseconds) ||
      !Number.isSafeInteger(source.response_received_unix_milliseconds) ||
      source.response_received_unix_milliseconds < source.request_started_unix_milliseconds ||
      (source.next_route !== null &&
        !source.next_route.startsWith(`/repos/${host.identities.repository}/`))
    ) {
      invalid('capture-source-values');
    }
    sourceIds.add(source.source_id);
    sourcePaths.add(source.body_path);
  }
  if (
    value.expected_roles.some((role) =>
      role.producer_source_ids.some((sourceId) => !sourceIds.has(sourceId)),
    )
  ) {
    invalid('capture-expected-role-source');
  }
  const artifactIds = new Set();
  const artifactNames = new Set();
  const artifactsById = new Map();
  const handoffArtifactIds = new Set(
    host.capture_disposition === 'recovery-only'
      ? host.inventories.maintainer_handoff.map(({ artifact_id }) => artifact_id)
      : [],
  );
  for (const artifact of value.artifacts) {
    exactKeys(
      artifact,
      [
        'artifact_id',
        'artifact_name',
        'metadata_source_id',
        'metadata_body_sha256',
        'producing_run_id',
        'producing_run_attempt',
        'download_route',
        'download_safe_headers_sha256',
        'download_request_started_unix_milliseconds',
        'download_response_received_unix_milliseconds',
        'archive_path',
        'archive_sha256',
        'archive_size',
        'archive_file_identity',
        'encrypted_object_path',
        'encrypted_object_sha256',
        'encrypted_object_size',
        'encrypted_object_file_identity',
      ],
      'capture-artifact-shape',
    );
    const metadataEndpoint = `/repos/${host.identities.repository}/actions/runs/${artifact.producing_run_id}/artifacts`;
    const metadataSources = value.sources.filter((source) =>
      source.source_id.startsWith(`${artifact.metadata_source_id}:page:`),
    );
    if (
      artifactIds.has(artifact.artifact_id) ||
      artifactNames.has(artifact.artifact_name.toLowerCase()) ||
      !decimal.test(artifact.artifact_id) ||
      !decimal.test(artifact.producing_run_id) ||
      !decimal.test(artifact.producing_run_attempt) ||
      (!handoffArtifactIds.has(artifact.artifact_id) &&
        (!operationByRun.has(artifact.producing_run_id) ||
          operationByRun.get(artifact.producing_run_id).run_attempt !==
            artifact.producing_run_attempt)) ||
      !sourceIds.has(`${artifact.metadata_source_id}:page:1`) ||
      metadataSources.length === 0 ||
      metadataSources.some(
        (source, index) =>
          source.page !== index + 1 ||
          !(source.route === metadataEndpoint || source.route.startsWith(`${metadataEndpoint}?`)) ||
          (index < metadataSources.length - 1
            ? source.next_route === null ||
              !(
                source.next_route === metadataEndpoint ||
                source.next_route.startsWith(`${metadataEndpoint}?`)
              )
            : source.next_route !== null),
      ) ||
      !hex64.test(artifact.metadata_body_sha256) ||
      !value.sources.some(
        (source) =>
          source.source_id.startsWith(`${artifact.metadata_source_id}:page:`) &&
          source.body_sha256 === artifact.metadata_body_sha256,
      ) ||
      artifact.download_route !==
        `/repos/${host.identities.repository}/actions/artifacts/${artifact.artifact_id}/zip` ||
      !hex64.test(artifact.download_safe_headers_sha256) ||
      !Number.isSafeInteger(artifact.download_request_started_unix_milliseconds) ||
      !Number.isSafeInteger(artifact.download_response_received_unix_milliseconds) ||
      artifact.download_response_received_unix_milliseconds <
        artifact.download_request_started_unix_milliseconds ||
      artifact.archive_path !== `artifact-${artifact.artifact_id}.zip` ||
      artifact.encrypted_object_path !== `artifact-${artifact.artifact_id}.bin` ||
      !hex64.test(artifact.archive_sha256) ||
      !hex64.test(artifact.archive_file_identity) ||
      !hex64.test(artifact.encrypted_object_sha256) ||
      !hex64.test(artifact.encrypted_object_file_identity) ||
      !decimal.test(artifact.archive_size) ||
      !decimal.test(artifact.encrypted_object_size)
    ) {
      invalid('capture-artifact-values');
    }
    artifactIds.add(artifact.artifact_id);
    artifactNames.add(artifact.artifact_name.toLowerCase());
    artifactsById.set(artifact.artifact_id, artifact);
  }
  const expectedIds = [
    ...host.inventories.observed_cleanup.map(({ artifact_id }) => artifact_id),
    ...handoffArtifactIds,
  ];
  exactArray(
    [...artifactIds].sort((a, b) => a.localeCompare(b, 'en', { numeric: true })),
    [...expectedIds].sort((a, b) => a.localeCompare(b, 'en', { numeric: true })),
    'capture-artifact-inventory',
  );
  return artifactsById;
}

function validateProducerJournalSeal(value, captureManifest) {
  exactKeys(
    value,
    [
      'kind',
      'destination_identity_sha256',
      'execution_authorization_sha256',
      'authority_sha256',
      'authority_physical_identity_sha256',
      'operation_ids',
      'entry_sha256s',
      'discovery_sources',
      'derived_roles',
      'observed_runs',
      'disposition',
      'finalized',
    ],
    'producer-journal-seal-shape',
  );
  if (
    value.kind !== 'apr-r4-e3-producer-outcome-journal-seal-v1' ||
    value.destination_identity_sha256 !== captureManifest.destination_identity_sha256 ||
    value.execution_authorization_sha256 !== captureManifest.execution_authorization_sha256 ||
    !hex64.test(value.authority_sha256) ||
    !hex64.test(value.authority_physical_identity_sha256) ||
    canonicalJson(value.operation_ids) !== canonicalJson(captureManifest.operation_ids) ||
    !Array.isArray(value.entry_sha256s) ||
    value.entry_sha256s.some((digest) => !hex64.test(digest)) ||
    !Array.isArray(value.discovery_sources) ||
    value.discovery_sources.length === 0 ||
    canonicalJson(value.derived_roles) !== canonicalJson(captureManifest.expected_roles) ||
    canonicalJson(value.observed_runs) !== canonicalJson(captureManifest.observed_runs) ||
    value.disposition !== captureManifest.disposition ||
    value.finalized !== true
  ) {
    invalid('producer-journal-seal-values');
  }
  const captureSources = new Map(
    captureManifest.sources.map((source) => [source.source_id, source]),
  );
  const seen = new Set();
  const discoveryEndpoint = `/repos/${captureManifest.repository}/actions/workflows/r4-trusted-proof.yml/runs`;
  value.discovery_sources.forEach((source, index) => {
    exactKeys(
      source,
      [
        'source_id',
        'route',
        'page',
        'status',
        'body_path',
        'body_sha256',
        'body_size',
        'body_physical_identity_sha256',
        'safe_headers_sha256',
        'request_started_unix_milliseconds',
        'response_received_unix_milliseconds',
        'next_route',
      ],
      'producer-discovery-source-shape',
    );
    const captured = captureSources.get(source.source_id);
    const prior = index === 0 ? null : value.discovery_sources[index - 1];
    const expectedNext =
      index + 1 < value.discovery_sources.length ? value.discovery_sources[index + 1].route : null;
    if (
      seen.has(source.source_id) ||
      source.source_id !== `producer-discovery-final:page:${index + 1}` ||
      source.page !== index + 1 ||
      source.status !== 200 ||
      (index === 0
        ? source.route !== `${discoveryEndpoint}?per_page=100`
        : source.route !== prior.next_route) ||
      source.next_route !== expectedNext ||
      !/^producer-journal-[a-z0-9-]+\/discovery-final-page-[0-9]{4}\.json$/u.test(
        source.body_path,
      ) ||
      !hex64.test(source.body_sha256) ||
      !decimal.test(source.body_size) ||
      !hex64.test(source.body_physical_identity_sha256) ||
      !hex64.test(source.safe_headers_sha256) ||
      !Number.isSafeInteger(source.request_started_unix_milliseconds) ||
      !Number.isSafeInteger(source.response_received_unix_milliseconds) ||
      source.response_received_unix_milliseconds < source.request_started_unix_milliseconds ||
      captured === undefined ||
      captured.phase !== 'producer-discovery' ||
      captured.route !== source.route ||
      captured.page !== source.page ||
      captured.status !== source.status ||
      captured.body_sha256 !== source.body_sha256 ||
      captured.body_size !== source.body_size ||
      captured.safe_headers_sha256 !== source.safe_headers_sha256 ||
      captured.request_started_unix_milliseconds !== source.request_started_unix_milliseconds ||
      captured.response_received_unix_milliseconds !== source.response_received_unix_milliseconds ||
      captured.next_route !== source.next_route
    ) {
      invalid('producer-discovery-source-values');
    }
    seen.add(source.source_id);
  });
}

function deriveInventoriesFromOracle(value, capturedArtifacts, operationIds) {
  const expectedSuccess = value.records
    .filter(({ role }) => expectedProductRoles.includes(role))
    .map((record) => ({
      artifact_id: record.artifact_id,
      role: record.role,
      scope: record.scope,
      object_class: record.object_class,
      authenticated: true,
      operation_owned: true,
    }));
  const remainingOrdinary = expectedCleanupShape.map(([objectClass, scope, operationIndex]) =>
    JSON.stringify([objectClass, scope, operationIds[operationIndex]]),
  );
  const observedCleanup = value.records.map((record) => {
    const captured = capturedArtifacts.get(record.artifact_id);
    if (captured === undefined) invalid('oracle-capture-record-missing');
    const signature = JSON.stringify([record.object_class, record.scope, record.operation_id]);
    const ordinaryIndex = remainingOrdinary.indexOf(signature);
    if (ordinaryIndex >= 0) remainingOrdinary.splice(ordinaryIndex, 1);
    return {
      artifact_id: record.artifact_id,
      object_class: record.object_class,
      scope: record.scope,
      operation_id: record.operation_id,
      artifact_name: captured.artifact_name,
      producing_run_id: captured.producing_run_id,
      producing_run_attempt: Number(captured.producing_run_attempt),
      archive_sha256: captured.archive_sha256,
      encrypted_object_sha256: captured.encrypted_object_sha256,
      encrypted_object_size: captured.encrypted_object_size,
      ownership_evidence_sha256: record.ownership_evidence_sha256,
      authenticated: true,
      operation_owned: true,
      disposition: ordinaryIndex >= 0 ? 'delete' : 'recovery-only-delete',
    };
  });
  if (remainingOrdinary.length !== 0) invalid('oracle-ordinary-inventory-missing');
  return { expected_success: expectedSuccess, observed_cleanup: observedCleanup };
}

function deriveRecoveryInventoriesFromOracle(value, capturedArtifacts, operationIds) {
  return {
    expected_success: [],
    observed_cleanup: value.records.map((record) => {
      const captured = capturedArtifacts.get(record.artifact_id);
      if (captured === undefined || !operationIds.includes(record.operation_id)) {
        invalid('oracle-capture-record-missing');
      }
      return {
        artifact_id: record.artifact_id,
        object_class: record.object_class,
        scope: record.scope,
        operation_id: record.operation_id,
        artifact_name: captured.artifact_name,
        producing_run_id: captured.producing_run_id,
        producing_run_attempt: Number(captured.producing_run_attempt),
        archive_sha256: captured.archive_sha256,
        encrypted_object_sha256: captured.encrypted_object_sha256,
        encrypted_object_size: captured.encrypted_object_size,
        ownership_evidence_sha256: record.ownership_evidence_sha256,
        authenticated: true,
        operation_owned: true,
        disposition: 'recovery-only-delete',
      };
    }),
    maintainer_handoff: value.maintainer_handoff.map((item) => {
      const captured = capturedArtifacts.get(item.artifact_id);
      if (captured === undefined) invalid('oracle-handoff-capture-missing');
      return {
        artifact_id: item.artifact_id,
        artifact_name: captured.artifact_name,
        producing_run_id: captured.producing_run_id,
        producing_run_attempt: Number(captured.producing_run_attempt),
        archive_sha256: captured.archive_sha256,
        encrypted_object_sha256: captured.encrypted_object_sha256,
        encrypted_object_size: captured.encrypted_object_size,
        disposition: item.disposition,
        reason: item.reason,
      };
    }),
  };
}

function validateOracleResult(
  value,
  host,
  captureManifestSha256,
  oracleResultSha256,
  capturedArtifacts,
) {
  const recoveryCapture = host.capture_disposition === 'recovery-only';
  exactKeys(
    value,
    [
      'kind',
      'capture_manifest_sha256',
      'oracle_source_sha',
      'oracle_source_tree',
      'oracle_assembly_sha256',
      'production_assembly_sha256',
      'exact_seven_success',
      'recovery_only',
      'records',
      'maintainer_handoff',
    ],
    'oracle-result-shape',
  );
  if (
    value.kind !== 'apr-r4-e3-production-codec-oracle-result-v1' ||
    value.capture_manifest_sha256 !== captureManifestSha256 ||
    value.oracle_source_sha !== host.identities.oracle_source_sha ||
    value.oracle_source_tree !== host.identities.oracle_source_tree ||
    !hex64.test(value.oracle_assembly_sha256) ||
    !hex64.test(value.production_assembly_sha256) ||
    value.exact_seven_success !== !recoveryCapture ||
    value.recovery_only !== recoveryCapture ||
    oracleResultSha256 !== host.restricted_package.oracle_result_sha256 ||
    !Array.isArray(value.records) ||
    value.records.length !== host.inventories.observed_cleanup.length ||
    !Array.isArray(value.maintainer_handoff)
  ) {
    invalid('oracle-result-values');
  }
  const byId = new Map();
  for (const record of value.records) {
    exactKeys(
      record,
      [
        'artifact_id',
        'role',
        'scope',
        'base_scope_digest',
        'object_class',
        'object_identity',
        'producing_run_identity',
        'producing_run_attempt',
        'operation_id',
        'ownership_evidence_sha256',
        'payload_sha256',
      ],
      'oracle-record-shape',
    );
    if (
      byId.has(record.artifact_id) ||
      !decimal.test(record.artifact_id) ||
      !decimal.test(record.producing_run_identity) ||
      !decimal.test(record.producing_run_attempt) ||
      !hex64.test(record.operation_id) ||
      !hex64.test(record.ownership_evidence_sha256) ||
      (record.object_class === 'locator_root'
        ? record.base_scope_digest !== ''
        : !hex64.test(record.base_scope_digest)) ||
      !hex64.test(record.object_identity) ||
      !hex64.test(record.payload_sha256)
    ) {
      invalid('oracle-record-values');
    }
    byId.set(record.artifact_id, record);
  }
  const handoffById = new Map();
  for (const item of value.maintainer_handoff) {
    exactKeys(item, ['artifact_id', 'disposition', 'reason'], 'oracle-handoff-shape');
    if (
      handoffById.has(item.artifact_id) ||
      byId.has(item.artifact_id) ||
      !decimal.test(item.artifact_id) ||
      item.disposition !== 'non-deletable-maintainer-handoff' ||
      ![
        'codec-authentication-failed',
        'codec-payload-invalid',
        'locator-context-unavailable',
        'operation-ownership-unverified',
      ].includes(item.reason) ||
      !capturedArtifacts.has(item.artifact_id)
    ) {
      invalid('oracle-handoff-values');
    }
    handoffById.set(item.artifact_id, item);
  }
  for (const expected of host.inventories.observed_cleanup) {
    const observed = byId.get(expected.artifact_id);
    const captured = capturedArtifacts.get(expected.artifact_id);
    const ownershipEvidenceSha256 = captured
      ? sha256(
          canonicalJson({
            artifact_id: captured.artifact_id,
            artifact_name: captured.artifact_name,
            scope: observed?.scope,
            object_class: observed?.object_class,
            operation_id: observed?.operation_id,
            producing_run_id: captured.producing_run_id,
            producing_run_attempt: captured.producing_run_attempt,
            archive_sha256: captured.archive_sha256,
            encrypted_object_sha256: captured.encrypted_object_sha256,
            encrypted_object_size: captured.encrypted_object_size,
          }),
        )
      : null;
    if (
      !observed ||
      !captured ||
      observed.scope !== expected.scope ||
      observed.object_class !== expected.object_class ||
      observed.operation_id !== expected.operation_id ||
      observed.producing_run_identity !== expected.producing_run_id ||
      Number(observed.producing_run_attempt) !== expected.producing_run_attempt ||
      captured.artifact_name !== expected.artifact_name ||
      captured.producing_run_id !== expected.producing_run_id ||
      Number(captured.producing_run_attempt) !== expected.producing_run_attempt ||
      captured.archive_sha256 !== expected.archive_sha256 ||
      captured.encrypted_object_sha256 !== expected.encrypted_object_sha256 ||
      captured.encrypted_object_size !== expected.encrypted_object_size ||
      observed.ownership_evidence_sha256 !== ownershipEvidenceSha256 ||
      expected.ownership_evidence_sha256 !== ownershipEvidenceSha256
    ) {
      invalid('oracle-cleanup-inventory');
    }
  }
  for (const expected of host.inventories.expected_success) {
    const observed = byId.get(expected.artifact_id);
    if (!observed || observed.role !== expected.role) invalid('oracle-success-inventory');
  }
  const expectedHandoff = recoveryCapture
    ? host.inventories.maintainer_handoff.map(({ artifact_id, disposition, reason }) => ({
        artifact_id,
        disposition,
        reason,
      }))
    : [];
  if (canonicalJson(value.maintainer_handoff) !== canonicalJson(expectedHandoff)) {
    invalid('oracle-handoff-inventory');
  }
  if (
    recoveryCapture &&
    [...capturedArtifacts.keys()].some(
      (artifactId) => !byId.has(artifactId) && !handoffById.has(artifactId),
    )
  ) {
    invalid('oracle-recovery-inventory-incomplete');
  }
  if (!recoveryCapture && value.maintainer_handoff.length !== 0) {
    invalid('oracle-success-handoff');
  }
  if (!recoveryCapture) {
    exactArray(
      value.records
        .filter(({ role }) => expectedProductRoles.includes(role))
        .map(({ role }) => role),
      expectedProductRoles,
      'oracle-success-roles',
    );
  }
  const derivedInventories = recoveryCapture
    ? deriveRecoveryInventoriesFromOracle(value, capturedArtifacts, host.identities.operation_ids)
    : deriveInventoriesFromOracle(value, capturedArtifacts, host.identities.operation_ids);
  if (canonicalJson(derivedInventories) !== canonicalJson(host.inventories)) {
    invalid('oracle-inventory-derivation');
  }
  return derivedInventories;
}

function capturedPages(manifest, bodies, sourceId, route, pagination) {
  const prefix = `${sourceId}:page:`;
  const sources = manifest.sources
    .filter((source) => source.source_id.startsWith(prefix))
    .sort((left, right) => left.page - right.page);
  if (
    sources.length === 0 ||
    (pagination === 'none' && sources.length !== 1) ||
    sources.some((source, index) => {
      const retained = bodies.get(source.source_id);
      return (
        source.source_id !== `${prefix}${index + 1}` ||
        source.page !== index + 1 ||
        source.status !== 200 ||
        !(source.route === route || source.route.startsWith(`${route}?`)) ||
        source.request_started_unix_milliseconds > source.response_received_unix_milliseconds ||
        retained === undefined ||
        typeof retained.text !== 'string' ||
        sha256(Buffer.from(retained.text, 'utf8')) !== source.body_sha256 ||
        String(Buffer.byteLength(retained.text, 'utf8')) !== source.body_size ||
        (index + 1 < sources.length
          ? source.next_route !== sources[index + 1].route
          : source.next_route !== null)
      );
    })
  ) {
    invalid('post-cleanup-capture-pages');
  }
  return {
    values: sources.map((source) => {
      try {
        return JSON.parse(bodies.get(source.source_id).text);
      } catch {
        invalid('post-cleanup-capture-json');
      }
    }),
    references: sources.map((source) => ({
      source_id: source.source_id,
      sha256: source.body_sha256,
    })),
    observation: {
      request_started: sources[0].request_started_unix_milliseconds,
      response_received: sources.at(-1).response_received_unix_milliseconds,
    },
  };
}

function cleanupMutationTargets(cleanupPlan) {
  return new Map([
    [
      'remove-proof-control',
      cleanupPlan.targets.control_comments.map(({ comment_id }) => `comment:${comment_id}`),
    ],
    [
      'delete-observed-state',
      cleanupPlan.targets.state_artifacts.map(({ artifact_id }) => `artifact:${artifact_id}`),
    ],
    [
      'remove-authorization-and-secrets',
      [
        `variable:${cleanupPlan.targets.authorization_variable.name}`,
        ...cleanupPlan.targets.secrets.map(({ name }) => `secret:${name}`),
      ],
    ],
    ['restore-environment', [`environment:${cleanupPlan.targets.environment.name}`]],
    [
      'retire-fixtures',
      [
        ...cleanupPlan.targets.fixture_refs.map(({ ref }) => `ref:${ref}`),
        ...cleanupPlan.targets.fixture_prs.map(({ number }) => `pr:${number}`),
      ],
    ],
    [
      'remove-local-credentials',
      cleanupPlan.targets.credential_copies.map(({ name }) => `credential:${name}`),
    ],
  ]);
}

function validateCleanupExecution({
  cleanupExecution,
  cleanupPlan,
  authorizations,
  captureManifest,
  captureManifestSha256,
  postCleanupCaptureManifest,
  postCleanupCaptureManifestSha256,
  oracleResultSha256,
  credentialCopiesAbsent,
}) {
  exactKeys(
    cleanupExecution,
    ['kind', 'repository', 'operation_ids', 'plan_sha256', 'entry', 'phases', 'seal'],
    'cleanup-execution-shape',
  );
  exactKeys(
    cleanupExecution.entry,
    [
      'cleanup_authorization_source_id',
      'capture_manifest_sha256',
      'oracle_result_sha256',
      'run_terminal_source_ids',
      'observation',
    ],
    'cleanup-execution-entry-shape',
  );
  interval(cleanupExecution.entry.observation, 'cleanup-execution-entry-observation');
  const expectedRunSources = captureManifest.observed_runs.map(
    ({ run_id }) => `run-terminal-${run_id}:page:1`,
  );
  const captureSources = new Map(
    captureManifest.sources.map((source) => [source.source_id, source]),
  );
  if (
    cleanupExecution.kind !== 'apr-r4-e3-cleanup-execution-v1' ||
    cleanupExecution.repository !== captureManifest.repository ||
    canonicalJson(cleanupExecution.operation_ids) !==
      canonicalJson(captureManifest.operation_ids) ||
    cleanupExecution.plan_sha256 !== sha256(canonicalJson(cleanupPlan)) ||
    cleanupExecution.entry.cleanup_authorization_source_id !== 'cleanup-authorization-readback' ||
    cleanupExecution.entry.capture_manifest_sha256 !== captureManifestSha256 ||
    cleanupExecution.entry.oracle_result_sha256 !== oracleResultSha256 ||
    canonicalJson(cleanupExecution.entry.run_terminal_source_ids) !==
      canonicalJson(expectedRunSources) ||
    authorizations.cleanup.source.observation.response_received >=
      cleanupExecution.entry.observation.request_started ||
    expectedRunSources.some((sourceId) => {
      const source = captureSources.get(sourceId);
      return (
        source === undefined ||
        source.response_received_unix_milliseconds >=
          cleanupExecution.entry.observation.request_started
      );
    })
  ) {
    invalid('cleanup-execution-entry');
  }

  if (
    !Array.isArray(cleanupExecution.phases) ||
    cleanupExecution.phases.length !== cleanupPhases.length
  ) {
    invalid('cleanup-execution-phases');
  }
  const postSources = new Map(
    postCleanupCaptureManifest.sources.map((source) => [source.source_id, source]),
  );
  const targets = cleanupMutationTargets(cleanupPlan);
  const usedReadbacks = new Set();
  const localCredentialReadback = 'cleanup-execution:local-credential-absence';
  let previousResponse = cleanupExecution.entry.observation.response_received;
  for (let index = 0; index < cleanupPhases.length; index += 1) {
    const phase = cleanupExecution.phases[index];
    exactKeys(
      phase,
      ['phase', 'observation', 'mutations', 'readback_source_ids'],
      'cleanup-execution-phase-shape',
    );
    interval(phase.observation, 'cleanup-execution-phase-observation');
    if (
      phase.phase !== cleanupPhases[index] ||
      phase.observation.request_started <= previousResponse ||
      !Array.isArray(phase.readback_source_ids) ||
      new Set(phase.readback_source_ids).size !== phase.readback_source_ids.length ||
      phase.readback_source_ids.some((sourceId) => {
        if (sourceId === localCredentialReadback) {
          return phase.phase !== 'remove-local-credentials' || usedReadbacks.has(sourceId);
        }
        const source = postSources.get(sourceId);
        return (
          usedReadbacks.has(sourceId) ||
          source === undefined ||
          source.request_started_unix_milliseconds < phase.observation.request_started ||
          source.response_received_unix_milliseconds > phase.observation.response_received
        );
      })
    ) {
      invalid('cleanup-execution-phase-order');
    }
    phase.readback_source_ids.forEach((sourceId) => usedReadbacks.add(sourceId));
    const expectedTargets = targets.get(phase.phase) ?? [];
    if (!Array.isArray(phase.mutations) || phase.mutations.length !== expectedTargets.length) {
      invalid('cleanup-execution-mutation-cardinality');
    }
    const observedTargets = [];
    for (const mutation of phase.mutations) {
      exactKeys(
        mutation,
        ['target_id', 'outcome', 'request', 'post_readback_source_ids'],
        'cleanup-execution-mutation-shape',
      );
      interval(mutation.request, 'cleanup-execution-mutation-request');
      if (
        ![
          'committed',
          'known-not-sent-absent',
          'missing-idempotent',
          'reconciled-outcome-unknown',
        ].includes(mutation.outcome) ||
        mutation.request.request_started < phase.observation.request_started ||
        mutation.request.response_received > phase.observation.response_received ||
        !Array.isArray(mutation.post_readback_source_ids) ||
        mutation.post_readback_source_ids.length === 0 ||
        mutation.post_readback_source_ids.some((sourceId) => {
          if (sourceId === localCredentialReadback) {
            return phase.phase !== 'remove-local-credentials';
          }
          const source = postSources.get(sourceId);
          return (
            !phase.readback_source_ids.includes(sourceId) ||
            source.request_started_unix_milliseconds <= mutation.request.response_received
          );
        })
      ) {
        invalid('cleanup-execution-mutation-outcome');
      }
      observedTargets.push(mutation.target_id);
    }
    if (canonicalJson(observedTargets) !== canonicalJson(expectedTargets)) {
      invalid('cleanup-execution-mutation-targets');
    }
    previousResponse = phase.observation.response_received;
  }
  exactKeys(
    cleanupExecution.seal,
    [
      'observation',
      'post_cleanup_capture_manifest_sha256',
      'credential_copies_absent',
      'private_manifest_inputs_sealed',
    ],
    'cleanup-execution-seal-shape',
  );
  interval(cleanupExecution.seal.observation, 'cleanup-execution-seal-observation');
  if (
    cleanupExecution.seal.observation.request_started <= previousResponse ||
    cleanupExecution.seal.post_cleanup_capture_manifest_sha256 !==
      postCleanupCaptureManifestSha256 ||
    cleanupExecution.seal.credential_copies_absent !== credentialCopiesAbsent ||
    cleanupExecution.seal.private_manifest_inputs_sealed !== true ||
    [...usedReadbacks].filter((sourceId) => sourceId !== localCredentialReadback).length !==
      postCleanupCaptureManifest.sources.length
  ) {
    invalid('cleanup-execution-seal');
  }
  return cleanupExecution.phases;
}

export function derivePostCleanupEvidence({
  captureManifest,
  postCleanupCaptureManifest,
  postCleanupCapturedSourceBodies,
  authorizations,
  proofControl,
  cleanupPlan,
  exactSevenSuccess,
  credentialCopiesAbsent,
  cleanupExecution,
  captureManifestSha256,
  postCleanupCaptureManifestSha256,
  oracleResultSha256,
}) {
  if (
    !(postCleanupCapturedSourceBodies instanceof Map) ||
    postCleanupCaptureManifest.kind !== 'apr-r4-e3-post-cleanup-capture-manifest-v1' ||
    postCleanupCaptureManifest.finalized !== true ||
    postCleanupCaptureManifest.repository_id !== captureManifest.repository_id ||
    postCleanupCaptureManifest.repository !== captureManifest.repository ||
    canonicalJson(postCleanupCaptureManifest.operation_ids) !==
      canonicalJson(captureManifest.operation_ids) ||
    postCleanupCaptureManifest.execution_authorization_sha256 !==
      captureManifest.execution_authorization_sha256 ||
    postCleanupCaptureManifest.producer_journal_directory !==
      captureManifest.producer_journal_directory ||
    postCleanupCaptureManifest.producer_journal_seal_sha256 !==
      captureManifest.producer_journal_seal_sha256 ||
    postCleanupCaptureManifest.producer_journal_seal_file_identity !==
      captureManifest.producer_journal_seal_file_identity ||
    postCleanupCaptureManifest.disposition !== captureManifest.disposition ||
    canonicalJson(postCleanupCaptureManifest.expected_roles) !==
      canonicalJson(captureManifest.expected_roles) ||
    canonicalJson(postCleanupCaptureManifest.observed_runs) !==
      canonicalJson(captureManifest.observed_runs) ||
    postCleanupCaptureManifest.source_map_sha256 !== captureManifest.source_map_sha256 ||
    postCleanupCaptureManifest.destination_identity_sha256 !==
      captureManifest.destination_identity_sha256 ||
    postCleanupCaptureManifest.artifacts.length !== 0
  ) {
    invalid('post-cleanup-capture-manifest');
  }
  const repositoryRoute = `/repos/${captureManifest.repository}`;
  const fixtures = authorizations.execution.fixture_prs;
  const runs = captureManifest.observed_runs.map(({ run_id }) => run_id);
  const operations = authorizations.execution.operation_ids;
  const expectedSourceIds = new Set();
  const read = (sourceId, route, pagination = 'none') => {
    expectedSourceIds.add(sourceId);
    return capturedPages(
      postCleanupCaptureManifest,
      postCleanupCapturedSourceBodies,
      sourceId,
      route,
      pagination,
    );
  };
  const normalComments = read(
    `post-cleanup-control-comments-normal-pr-${fixtures[0].number}`,
    `${repositoryRoute}/issues/${fixtures[0].number}/comments`,
    'complete-cursor',
  );
  const staleComments = read(
    `post-cleanup-control-comments-stale-pr-${fixtures[1].number}`,
    `${repositoryRoute}/issues/${fixtures[1].number}/comments`,
    'complete-cursor',
  );
  const commentValues = [...normalComments.values, ...staleComments.values].flat();
  const deletedCommentIds = new Set(
    Object.values(proofControl)
      .flatMap(({ comments }) => comments)
      .map(({ comment_id }) => comment_id),
  );
  const sticky = cleanupPlan.targets.sticky;
  const stickyMatches = commentValues.filter(({ id }) => String(id) === sticky.comment_id);
  if (
    commentValues.some(({ id }) => deletedCommentIds.has(String(id))) ||
    stickyMatches.length !== 1 ||
    typeof stickyMatches[0].body !== 'string' ||
    sha256(Buffer.from(stickyMatches[0].body, 'utf8')) !== sticky.body_sha256
  ) {
    invalid('post-cleanup-proof-control');
  }
  const stateDeleteReads = runs.map((run) =>
    read(
      `post-cleanup-state-delete-run-${run}`,
      `${repositoryRoute}/actions/runs/${run}/artifacts`,
      'complete-cursor',
    ),
  );
  const artifactReads = runs.map((run) =>
    read(
      `post-cleanup-state-empty-run-${run}`,
      `${repositoryRoute}/actions/runs/${run}/artifacts`,
      'complete-cursor',
    ),
  );
  if (
    [...stateDeleteReads, ...artifactReads].some(({ values }) =>
      values.some(
        (page) =>
          page.total_count !== 0 || !Array.isArray(page.artifacts) || page.artifacts.length !== 0,
      ),
    )
  ) {
    invalid('post-cleanup-state-inventory');
  }
  const variables = read(
    'post-cleanup-variables',
    `${repositoryRoute}/actions/variables`,
    'complete-cursor',
  );
  const variableNames = variables.values
    .flatMap((page) => page.variables ?? [])
    .map(({ name }) => name);
  if (variableNames.includes(cleanupPlan.targets.authorization_variable.name)) {
    invalid('post-cleanup-authorization');
  }
  const secrets = read(
    'post-cleanup-secrets',
    `${repositoryRoute}/environments/${cleanupPlan.targets.environment.name}/secrets`,
    'complete-cursor',
  );
  if (
    cleanupPlan.targets.secrets.some(
      ({ environment: targetEnvironment }) =>
        targetEnvironment !== cleanupPlan.targets.environment.name,
    )
  ) {
    invalid('post-cleanup-secret-scope');
  }
  const secretNames = new Set(
    secrets.values.flatMap((page) => page.secrets ?? []).map(({ name }) => name),
  );
  if (cleanupPlan.targets.secrets.some(({ name }) => secretNames.has(name))) {
    invalid('post-cleanup-secrets');
  }
  const environment = read(
    'post-cleanup-environment',
    `${repositoryRoute}/environments/${cleanupPlan.targets.environment.name}`,
  );
  if (
    sha256(canonicalJson(environment.values[0])) !==
    cleanupPlan.targets.environment.restore_snapshot_sha256
  ) {
    invalid('post-cleanup-environment');
  }
  const refs = operations.map((operation, index) =>
    read(
      `post-cleanup-ref-${index === 0 ? 'normal' : 'stale'}`,
      `${repositoryRoute}/git/matching-refs/heads/r4-trusted-proof/${operation}`,
      'complete-cursor',
    ),
  );
  if (refs.some(({ values }) => values.some((page) => !Array.isArray(page) || page.length !== 0))) {
    invalid('post-cleanup-fixture-refs');
  }
  const pulls = fixtures.map((fixture, index) =>
    read(
      `post-cleanup-pr-${index === 0 ? 'normal' : 'stale'}-${fixture.number}`,
      `${repositoryRoute}/pulls/${fixture.number}`,
    ),
  );
  if (
    pulls.some(
      ({ values }, index) =>
        String(values[0].number) !== fixtures[index].number || values[0].state !== 'closed',
    )
  ) {
    invalid('post-cleanup-fixture-prs');
  }
  const runReads = runs.map((run) =>
    read(`post-cleanup-final-run-${run}`, `${repositoryRoute}/actions/runs/${run}`),
  );
  if (
    runReads.some(
      ({ values }, index) =>
        String(values[0].id) !== runs[index] ||
        values[0].status !== 'completed' ||
        typeof values[0].conclusion !== 'string' ||
        values[0].conclusion.length === 0,
    )
  ) {
    invalid('post-cleanup-runs');
  }
  const stickyNormalComments = read(
    `post-cleanup-sticky-comments-normal-pr-${fixtures[0].number}`,
    `${repositoryRoute}/issues/${fixtures[0].number}/comments`,
    'complete-cursor',
  );
  const stickyStaleComments = read(
    `post-cleanup-sticky-comments-stale-pr-${fixtures[1].number}`,
    `${repositoryRoute}/issues/${fixtures[1].number}/comments`,
    'complete-cursor',
  );
  const stickyValues = [...stickyNormalComments.values, ...stickyStaleComments.values].flat();
  const finalStickyMatches = stickyValues.filter(({ id }) => String(id) === sticky.comment_id);
  if (
    stickyValues.some(({ id }) => deletedCommentIds.has(String(id))) ||
    finalStickyMatches.length !== 1 ||
    typeof finalStickyMatches[0].body !== 'string' ||
    sha256(Buffer.from(finalStickyMatches[0].body, 'utf8')) !== sticky.body_sha256
  ) {
    invalid('post-cleanup-sticky');
  }
  const observedBaseIds = new Set(
    postCleanupCaptureManifest.sources.map(({ source_id }) =>
      source_id.replace(/:page:[1-9][0-9]*$/u, ''),
    ),
  );
  if (
    observedBaseIds.size !== expectedSourceIds.size ||
    [...observedBaseIds].some((sourceId) => !expectedSourceIds.has(sourceId))
  ) {
    invalid('post-cleanup-source-set');
  }
  const references = [
    ...normalComments.references,
    ...staleComments.references,
    ...artifactReads.flatMap(({ references: value }) => value),
    ...stateDeleteReads.flatMap(({ references: value }) => value),
    ...variables.references,
    ...secrets.references,
    ...environment.references,
    ...refs.flatMap(({ references: value }) => value),
    ...pulls.flatMap(({ references: value }) => value),
    ...runReads.flatMap(({ references: value }) => value),
    ...stickyNormalComments.references,
    ...stickyStaleComments.references,
  ].sort((left, right) => left.source_id.localeCompare(right.source_id));
  const phases = validateCleanupExecution({
    cleanupExecution,
    cleanupPlan,
    authorizations,
    captureManifest,
    captureManifestSha256,
    postCleanupCaptureManifest,
    postCleanupCaptureManifestSha256,
    oracleResultSha256,
    credentialCopiesAbsent,
  });
  return {
    cleanup: {
      plan_sha256: sha256(canonicalJson(cleanupPlan)),
      execution_journal_sha256: sha256(canonicalJson(cleanupExecution)),
      entry_observation: cleanupExecution.entry.observation,
      resources: {
        authorization_variable: cleanupPlan.targets.authorization_variable.name,
        secret_names: cleanupPlan.targets.secrets.map(({ name }) => name),
        environment: cleanupPlan.targets.environment.name,
        fixture_refs: cleanupPlan.targets.fixture_refs.map(({ ref }) => ref),
        fixture_pr_numbers: cleanupPlan.targets.fixture_prs.map(({ number }) => number),
        credential_copies: cleanupPlan.targets.credential_copies.map(({ name }) => name),
        environment_snapshot_sha256: cleanupPlan.targets.environment.restore_snapshot_sha256,
        run_ids: cleanupPlan.targets.runs.map(({ run_id }) => run_id),
        sticky: {
          pr_number: sticky.pr_number,
          comment_id: sticky.comment_id,
          body_sha256: sticky.body_sha256,
          marker_sha256: sticky.marker_sha256,
        },
      },
      entry_gate: {
        all_runs_terminal: true,
        no_runs_queued_or_active: true,
        captures_complete: true,
        inventory_sealed: true,
        artifacts_captured: true,
        plan_approved: true,
      },
      ordered_readbacks: phases.map((phase) => ({
        phase: phase.phase,
        observation: phase.observation,
        mutations: phase.mutations.map((mutation) => ({
          target_id: mutation.target_id,
          outcome: mutation.outcome,
          source_ids: mutation.post_readback_source_ids,
        })),
        source_ids: phase.readback_source_ids,
      })),
      projection_gate: {
        exact_seven_success: exactSevenSuccess,
        control_absent: true,
        state_empty_complete: true,
        authorization_absent: true,
        secret_names_absent: true,
        environment_restored: true,
        fixtures_terminal: true,
        all_runs_terminal: true,
        sticky_exact: true,
        credential_copies_absent: credentialCopiesAbsent,
        private_manifest_inputs_sealed: cleanupExecution.seal.private_manifest_inputs_sealed,
      },
    },
    references,
  };
}

export function buildProtectedScanInput(authorizations, repository) {
  const operation = authorizations.execution.operation_ids[0];
  const rawStateCanary = Buffer.from(`APR_R4_E4_STATE_KEY_${operation}`, 'utf8');
  const values = new Map([
    [
      'authorization',
      [
        Buffer.from(`APR_R4_E4_AUTHORIZATION_${operation}`, 'utf8'),
        Buffer.from(`Bearer APR_R4_E4_AUTHORIZATION_${operation}`, 'utf8'),
      ],
    ],
    ['state_keys', [rawStateCanary, Buffer.from(rawStateCanary.toString('base64'), 'utf8')]],
    ['session_plaintext', [Buffer.from(`APR_R4_E4_SESSION_PLAINTEXT_${operation}`, 'utf8')]],
    ['provider_content', [Buffer.from(`APR_R4_E4_PROVIDER_CONTENT_${operation}`, 'utf8')]],
    ['tool_data', [Buffer.from(`APR_R4_E4_TOOL_DATA_${operation}`, 'utf8')]],
    ['host_evidence', [Buffer.from(`APR_R4_E4_HOST_EVIDENCE_${operation}`, 'utf8')]],
  ]);
  return {
    kind: 'apr-r4-e3-public-scan-memory-input-v2',
    repository,
    operation_ids: authorizations.execution.operation_ids,
    categories: Object.fromEntries(
      [...values].map(([name, category]) => [
        name,
        category.map((value) => value.toString('base64')),
      ]),
    ),
  };
}

export function protectedCanaryCategories(authorizations, repository) {
  const input = buildProtectedScanInput(authorizations, repository);
  return new Map([
    ...Object.entries(input.categories).map(([name, values]) => [
      name,
      values.map((value) => Buffer.from(value, 'base64')),
    ]),
  ]);
}

export function scanPublicCandidate({
  candidate,
  corpus,
  protectedDocuments,
  protectedCategories,
}) {
  const categoryNames = [
    'authorization',
    'state_keys',
    'session_plaintext',
    'provider_content',
    'tool_data',
    'host_evidence',
  ];
  if (
    !(corpus instanceof Map) ||
    !(protectedDocuments instanceof Map) ||
    !(protectedCategories instanceof Map) ||
    JSON.stringify([...protectedCategories.keys()]) !== JSON.stringify(categoryNames) ||
    [...protectedCategories.values()].some(
      (values) =>
        !Array.isArray(values) ||
        values.length === 0 ||
        values.some((value) => !Buffer.isBuffer(value) || value.length < 16),
    )
  ) {
    invalid('public-scan-input');
  }
  const candidateBytes = Buffer.from(canonicalJson(candidate), 'utf8');
  const protectedBytes = [...protectedDocuments.values()].map((value) =>
    Buffer.isBuffer(value) ? value : Buffer.from(String(value), 'utf8'),
  );
  const surfaces = new Map(corpus);
  surfaces.set('public-candidate', candidateBytes);
  const entries = [];
  const results = Object.fromEntries(
    categoryNames.map((name) => {
      const present = [...surfaces.values()].some((raw) => {
        const bytes = Buffer.isBuffer(raw) ? raw : Buffer.from(String(raw), 'utf8');
        return protectedCategories
          .get(name)
          .some((protectedValue) => bytes.indexOf(protectedValue) !== -1);
      });
      return [name, present ? 'present' : 'absent'];
    }),
  );
  if (Object.values(results).some((value) => value !== 'absent')) invalid('public-scan-leak');
  for (const [surfaceId, raw] of [...surfaces].sort(([left], [right]) =>
    left.localeCompare(right),
  )) {
    const bytes = Buffer.isBuffer(raw) ? raw : Buffer.from(String(raw), 'utf8');
    if (
      typeof surfaceId !== 'string' ||
      surfaceId.length === 0 ||
      protectedBytes.some(
        (protectedValue) => protectedValue.length !== 0 && bytes.indexOf(protectedValue) !== -1,
      )
    ) {
      invalid('public-scan-leak');
    }
    entries.push({ surface_id: surfaceId, sha256: sha256(bytes), size: String(bytes.length) });
  }
  const scan = {
    kind: 'apr-r4-e3-public-candidate-scan-v1',
    candidate_sha256: sha256(candidateBytes),
    corpus: entries,
    results,
  };
  return scan;
}

function validateCredentialLifecycle(
  correctionGateReceipt,
  admission,
  disposition,
  authorizations,
  captureManifest,
) {
  exactKeys(
    admission,
    [
      'kind',
      'destination_identity_sha256',
      'operation_ids',
      'execution_authorization_sha256',
      'correction_gate_sha256',
      'correction_gate_receipt_sha256',
      'correction_gate_receipt_physical_identity_sha256',
      'materializer_source_sha256',
      'materializer_build_sha256',
      'consumers',
      'created_slots',
      'omitted_slots',
      'admission_observation',
      'finalized',
    ],
    'credential-admission-shape',
  );
  const execution = authorizations.execution;
  const gate = execution.correction_gate;
  const identities = new Map(
    gate.authority_identities.map((identity) => [identity.component, identity]),
  );
  const selectedSlots = execution.active_secret_profile.credential_slot_names;
  const expectedConsumers = [
    'producer-journal-materializer',
    'phase-fragment-materializer',
    'capture',
    'oracle',
    'assembler',
  ];
  const admissionObservation = admission.admission_observation;
  exactKeys(
    correctionGateReceipt,
    [
      'kind',
      'destination_identity_sha256',
      'execution_authorization_sha256',
      'correction_gate_sha256',
      'repository',
      'pull_request_number',
      'branch',
      'commit',
      'tree',
      'worktree_identity_sha256',
      'gate_assembly_sha256',
      'remote_readbacks',
      'authority_identities',
      'contract_digests',
      'worktree_clean',
      'finalized',
    ],
    'correction-gate-receipt-shape',
  );
  if (
    correctionGateReceipt.kind !== 'apr-r4-e3-correction-gate-readiness-v1' ||
    correctionGateReceipt.destination_identity_sha256 !==
      captureManifest.destination_identity_sha256 ||
    correctionGateReceipt.execution_authorization_sha256 !==
      captureManifest.execution_authorization_sha256 ||
    correctionGateReceipt.correction_gate_sha256 !== sha256(canonicalJson(gate)) ||
    correctionGateReceipt.repository !== gate.repository ||
    correctionGateReceipt.pull_request_number !== gate.pull_request_number ||
    correctionGateReceipt.branch !== gate.branch ||
    correctionGateReceipt.commit !== gate.commit ||
    correctionGateReceipt.tree !== gate.tree ||
    correctionGateReceipt.worktree_identity_sha256 !==
      execution.destinations.public.worktree_identity_sha256 ||
    correctionGateReceipt.gate_assembly_sha256 !== identities.get('capture')?.build_sha256 ||
    canonicalJson(correctionGateReceipt.authority_identities) !==
      canonicalJson(gate.authority_identities) ||
    canonicalJson(correctionGateReceipt.contract_digests) !==
      canonicalJson(gate.contract_digests) ||
    !Array.isArray(correctionGateReceipt.remote_readbacks) ||
    correctionGateReceipt.remote_readbacks.length !== 4 ||
    correctionGateReceipt.worktree_clean !== true ||
    correctionGateReceipt.finalized !== true
  ) {
    invalid('correction-gate-receipt-values');
  }
  if (
    admission.kind !== 'apr-r4-e3-credential-admission-v1' ||
    admission.destination_identity_sha256 !== captureManifest.destination_identity_sha256 ||
    canonicalJson(admission.operation_ids) !== canonicalJson(captureManifest.operation_ids) ||
    admission.execution_authorization_sha256 !== captureManifest.execution_authorization_sha256 ||
    admission.correction_gate_sha256 !== sha256(canonicalJson(gate)) ||
    admission.correction_gate_receipt_sha256 !== sha256(canonicalJson(correctionGateReceipt)) ||
    !hex64.test(admission.correction_gate_receipt_physical_identity_sha256) ||
    admission.materializer_source_sha256 !== execution.credential_materializer.source_sha256 ||
    admission.materializer_build_sha256 !== execution.credential_materializer.build_sha256 ||
    admission.finalized !== true ||
    !Number.isSafeInteger(admissionObservation?.request_started_unix_milliseconds) ||
    !Number.isSafeInteger(admissionObservation?.response_received_unix_milliseconds) ||
    admissionObservation.request_started_unix_milliseconds < 0 ||
    admissionObservation.response_received_unix_milliseconds <
      admissionObservation.request_started_unix_milliseconds ||
    !Array.isArray(admission.consumers) ||
    canonicalJson(admission.consumers.map(({ component }) => component)) !==
      canonicalJson(expectedConsumers) ||
    admission.consumers.some(
      ({ component, build_sha256 }) => identities.get(component)?.build_sha256 !== build_sha256,
    ) ||
    !Array.isArray(admission.created_slots) ||
    canonicalJson(admission.created_slots.map(({ name }) => name)) !==
      canonicalJson(selectedSlots) ||
    admission.created_slots.some(
      (slot) =>
        !hex64.test(slot.physical_identity_sha256) ||
        slot.required !== (slot.name !== 'previous-state-key') ||
        slot.base64_key !== (slot.name !== 'github-token') ||
        slot.initial_state !== 'created',
    )
  ) {
    invalid('credential-admission-values');
  }
  const expectsPrevious = selectedSlots.includes('previous-state-key');
  if (
    !Array.isArray(admission.omitted_slots) ||
    (expectsPrevious
      ? admission.omitted_slots.length !== 0
      : canonicalJson(admission.omitted_slots) !==
        canonicalJson([{ name: 'previous-state-key', final_state: 'not-created' }]))
  ) {
    invalid('credential-admission-omission');
  }

  exactKeys(
    disposition,
    [
      'kind',
      'destination_identity_sha256',
      'operation_ids',
      'admission_receipt_sha256',
      'admission_receipt_physical_identity_sha256',
      'created_slots',
      'omitted_slots',
      'absence_observation',
      'absence_source_ids',
      'finalized',
    ],
    'credential-disposition-shape',
  );
  const absenceObservation = disposition.absence_observation;
  if (
    disposition.kind !== 'apr-r4-e3-credential-disposition-v1' ||
    disposition.destination_identity_sha256 !== admission.destination_identity_sha256 ||
    canonicalJson(disposition.operation_ids) !== canonicalJson(admission.operation_ids) ||
    disposition.admission_receipt_sha256 !== sha256(canonicalJson(admission)) ||
    !hex64.test(disposition.admission_receipt_physical_identity_sha256) ||
    disposition.finalized !== true ||
    canonicalJson(disposition.omitted_slots) !== canonicalJson(admission.omitted_slots) ||
    !Array.isArray(disposition.created_slots) ||
    disposition.created_slots.length !== admission.created_slots.length ||
    disposition.created_slots.some(
      (slot, index) =>
        slot.name !== admission.created_slots[index].name ||
        slot.physical_identity_sha256 !== admission.created_slots[index].physical_identity_sha256 ||
        slot.final_state !== 'created-then-deleted',
    ) ||
    !Number.isSafeInteger(absenceObservation?.request_started_unix_milliseconds) ||
    !Number.isSafeInteger(absenceObservation?.response_received_unix_milliseconds) ||
    absenceObservation.request_started_unix_milliseconds < 0 ||
    absenceObservation.response_received_unix_milliseconds <
      absenceObservation.request_started_unix_milliseconds ||
    !Array.isArray(disposition.absence_source_ids) ||
    disposition.absence_source_ids.length === 0 ||
    new Set(disposition.absence_source_ids).size !== disposition.absence_source_ids.length ||
    disposition.absence_source_ids.some(
      (sourceId) => typeof sourceId !== 'string' || sourceId.length === 0,
    )
  ) {
    invalid('credential-disposition-values');
  }
  return {
    admissionSha256: sha256(canonicalJson(admission)),
    dispositionSha256: sha256(canonicalJson(disposition)),
  };
}

function deriveRecoveryProofControl(authorizations, captureManifest, capturedSourceBodies) {
  const kinds = new Map([
    [authorizations.execution.operation_ids[0], ['ready', 'release']],
    [
      authorizations.execution.operation_ids[1],
      ['ready', 'release', 'stale-ready', 'stale-release'],
    ],
  ]);
  const grouped = new Map([...kinds.keys()].map((operationId) => [operationId, []]));
  for (const source of captureManifest.sources.filter(({ source_id }) =>
    /^proof-control-(bootstrap|stale)-comment-[1-9][0-9]*:page:1$/u.test(source_id),
  )) {
    const retained = capturedSourceBodies.get(source.source_id);
    if (
      retained === undefined ||
      sha256(Buffer.from(retained.text, 'utf8')) !== source.body_sha256
    ) {
      invalid('recovery-proof-control-source');
    }
    let response;
    let marker;
    try {
      response = JSON.parse(retained.text);
      const prefix = '<!-- apr-r4-e2p-control ';
      const suffix = ' -->';
      if (!response.body.startsWith(prefix) || !response.body.endsWith(suffix)) {
        invalid('recovery-proof-control-marker');
      }
      marker = JSON.parse(response.body.slice(prefix.length, -suffix.length));
    } catch {
      invalid('recovery-proof-control-json');
    }
    const allowedKinds = kinds.get(marker.operation_id);
    const preimage = JSON.stringify({ ...marker, body_sha256: '' });
    if (
      allowedKinds === undefined ||
      source.operation_id !== marker.operation_id ||
      marker.contract !== 'apr-r4-e2p-proof-control-v1' ||
      marker.repository !== captureManifest.repository ||
      String(response.id) !==
        /^proof-control-(?:bootstrap|stale)-comment-([1-9][0-9]*):page:1$/u.exec(
          source.source_id,
        )?.[1] ||
      marker.body_sha256 !== sha256(Buffer.from(preimage, 'utf8')) ||
      response.body !== `<!-- apr-r4-e2p-control ${JSON.stringify(marker)} -->`
    ) {
      invalid('recovery-proof-control-values');
    }
    grouped.get(marker.operation_id).push({
      kind: marker.kind,
      comment_id: String(response.id),
      predecessor_comment_id:
        marker.predecessor_comment_id === null ? null : String(marker.predecessor_comment_id),
      operation_id: marker.operation_id,
    });
  }
  const family = (operationId) => {
    const allowed = kinds.get(operationId);
    const comments = grouped
      .get(operationId)
      .sort((left, right) => allowed.indexOf(left.kind) - allowed.indexOf(right.kind));
    return {
      operation_id: operationId,
      comments,
      cleanup_outcomes: comments.map(({ comment_id }) => ({
        comment_id,
        outcome: 'deleted-absent',
      })),
    };
  };
  return {
    normal: family(authorizations.execution.operation_ids[0]),
    stale: family(authorizations.execution.operation_ids[1]),
  };
}

function validateRecoveryPostCleanupArtifactInventory({
  captureManifest,
  postCleanupCaptureManifest,
  postCleanupCapturedSourceBodies,
  inventories,
  cleanupPlan,
}) {
  const repositoryRoute = `/repos/${captureManifest.repository}`;
  const deletedIds = new Set(
    cleanupPlan.targets.state_artifacts.map(({ artifact_id }) => artifact_id),
  );
  const expectedByRun = new Map(captureManifest.observed_runs.map(({ run_id }) => [run_id, []]));
  for (const item of inventories.maintainer_handoff) {
    const expected = expectedByRun.get(item.producing_run_id);
    if (expected === undefined) invalid('recovery-handoff-run-unobserved');
    expected.push(item);
  }
  for (const [runId, expected] of expectedByRun) {
    const expectedIds = expected
      .map(({ artifact_id }) => artifact_id)
      .sort((left, right) => left.localeCompare(right, 'en', { numeric: true }));
    for (const phase of ['delete', 'empty']) {
      const pages = capturedPages(
        postCleanupCaptureManifest,
        postCleanupCapturedSourceBodies,
        `post-cleanup-state-${phase}-run-${runId}`,
        `${repositoryRoute}/actions/runs/${runId}/artifacts`,
        'complete-cursor',
      ).values;
      if (
        pages.some(
          (page) =>
            page.total_count !== expected.length ||
            !Array.isArray(page.artifacts) ||
            page.artifacts.some(
              (artifact) =>
                !decimal.test(String(artifact.id)) ||
                typeof artifact.name !== 'string' ||
                deletedIds.has(String(artifact.id)),
            ),
        )
      ) {
        invalid('recovery-post-cleanup-state-inventory');
      }
      const observed = pages.flatMap(({ artifacts }) => artifacts);
      const observedIds = observed
        .map(({ id }) => String(id))
        .sort((left, right) => left.localeCompare(right, 'en', { numeric: true }));
      if (
        canonicalJson(observedIds) !== canonicalJson(expectedIds) ||
        expected.some((item) => {
          const artifact = observed.find(({ id }) => String(id) === item.artifact_id);
          return artifact === undefined || artifact.name !== item.artifact_name;
        })
      ) {
        invalid('recovery-post-cleanup-handoff-inventory');
      }
    }
  }
}

function assembleRecoveryEvidence({
  sourceMap,
  sourceBundle,
  documents,
  captureManifest,
  captureManifestSha256,
  postCleanupCaptureManifest,
  postCleanupCaptureManifestSha256,
  oracleResult,
  oracleResultSha256,
  capturedSourceBodies,
  postCleanupCapturedSourceBodies,
  retainedDocuments,
  authorizations,
  credentialAdmission,
  credentialDisposition,
  producerJournalSeal,
  oracleBinaries,
  publicSurfaceCorpus,
  credentialCopiesAbsent,
}) {
  const cleanupPlan = retainedDocuments.get('cleanup-plan');
  const cleanupExecution = retainedDocuments.get('cleanup-execution');
  const restrictedPackage = retainedDocuments.get('restricted-package-readback');
  const publicLeakScan = retainedDocuments.get('public-leak-scan-result');
  if (
    [cleanupPlan, cleanupExecution, restrictedPackage, publicLeakScan].some(
      (value) => value === undefined,
    )
  ) {
    invalid('recovery-retained-document-set');
  }
  const unverifiedArtifacts = new Map(
    captureManifest.artifacts.map((artifact) => [artifact.artifact_id, artifact]),
  );
  const inventories = deriveRecoveryInventoriesFromOracle(
    oracleResult,
    unverifiedArtifacts,
    captureManifest.operation_ids,
  );
  const authorityHost = {
    capture_disposition: 'recovery-only',
    identities: {
      repository_id: captureManifest.repository_id,
      repository: authorizations.execution.coordinates.repository,
      operation_ids: authorizations.execution.operation_ids,
      oracle_source_sha: authorizations.execution.oracle_build.source_commit,
      oracle_source_tree: authorizations.execution.oracle_build.source_tree,
    },
    source_map: sourceMap,
    authorizations,
    inventories,
    restricted_package: {
      destination_identity_sha256: captureManifest.destination_identity_sha256,
      capture_manifest_sha256: captureManifestSha256,
      oracle_result_sha256: oracleResultSha256,
    },
  };
  const capturedArtifacts = validateCaptureManifest(
    captureManifest,
    authorityHost,
    captureManifestSha256,
  );
  validateOracleResult(
    oracleResult,
    authorityHost,
    captureManifestSha256,
    oracleResultSha256,
    capturedArtifacts,
  );
  const proofControl = deriveRecoveryProofControl(
    authorizations,
    captureManifest,
    capturedSourceBodies,
  );
  const sticky =
    cleanupPlan.targets?.sticky === null
      ? null
      : {
          pr_number: cleanupPlan.targets?.sticky?.pr_number,
          comment_id: cleanupPlan.targets?.sticky?.comment_id,
          body_sha256: cleanupPlan.targets?.sticky?.body_sha256,
          marker_sha256: cleanupPlan.targets?.sticky?.marker_sha256,
        };
  const regenerated = generateCleanupPlan({
    operation_ids: captureManifest.operation_ids,
    proof_control: proofControl,
    observed_cleanup: inventories.observed_cleanup,
    resources: {
      authorization_variable: authorizations.execution.authorization_variable_baseline.name,
      secret_names: authorizations.execution.active_secret_profile.environment_secret_names,
      environment: authorizations.execution.environment_baseline.name,
      fixture_refs: authorizations.execution.fixture_prs.map(({ ref }) => ref),
      fixture_pr_numbers: authorizations.execution.fixture_prs.map(({ number }) => number),
      credential_copies: authorizations.execution.active_secret_profile.credential_slot_names,
      environment_snapshot_sha256: cleanupPlan.targets?.environment?.restore_snapshot_sha256,
      run_ids: captureManifest.observed_runs.map(({ run_id }) => run_id),
      sticky,
    },
  });
  if (
    canonicalJson(regenerated.plan) !== canonicalJson(cleanupPlan) ||
    authorizations.cleanup.plan_sha256 !== regenerated.digest
  ) {
    invalid('recovery-cleanup-plan-derivation');
  }
  if (
    postCleanupCaptureManifest.kind !== 'apr-r4-e3-post-cleanup-capture-manifest-v1' ||
    postCleanupCaptureManifest.disposition !== 'recovery-only' ||
    postCleanupCaptureManifest.repository !== captureManifest.repository ||
    postCleanupCaptureManifest.repository_id !== captureManifest.repository_id ||
    canonicalJson(postCleanupCaptureManifest.operation_ids) !==
      canonicalJson(captureManifest.operation_ids) ||
    postCleanupCaptureManifest.execution_authorization_sha256 !==
      captureManifest.execution_authorization_sha256 ||
    postCleanupCaptureManifest.producer_journal_directory !==
      captureManifest.producer_journal_directory ||
    postCleanupCaptureManifest.producer_journal_seal_sha256 !==
      captureManifest.producer_journal_seal_sha256 ||
    postCleanupCaptureManifest.producer_journal_seal_file_identity !==
      captureManifest.producer_journal_seal_file_identity ||
    canonicalJson(postCleanupCaptureManifest.expected_roles) !==
      canonicalJson(captureManifest.expected_roles) ||
    canonicalJson(postCleanupCaptureManifest.observed_runs) !==
      canonicalJson(captureManifest.observed_runs) ||
    postCleanupCaptureManifest.source_map_sha256 !== captureManifest.source_map_sha256 ||
    postCleanupCaptureManifest.destination_identity_sha256 !==
      captureManifest.destination_identity_sha256 ||
    postCleanupCaptureManifest.finalized !== true ||
    postCleanupCaptureManifest.artifacts.length !== 0
  ) {
    invalid('recovery-post-cleanup-capture');
  }
  validateRecoveryPostCleanupArtifactInventory({
    captureManifest,
    postCleanupCaptureManifest,
    postCleanupCapturedSourceBodies,
    inventories,
    cleanupPlan,
  });
  validateCleanupExecution({
    cleanupExecution,
    cleanupPlan,
    authorizations,
    captureManifest,
    captureManifestSha256,
    postCleanupCaptureManifest,
    postCleanupCaptureManifestSha256,
    oracleResultSha256,
    credentialCopiesAbsent,
  });
  const requiredBindings = new Map([
    ['capture-manifest', captureManifestSha256],
    ['oracle-result', oracleResultSha256],
    ['producer-journal-seal', captureManifest.producer_journal_seal_sha256],
    ['credential-admission-receipt', sha256(canonicalJson(credentialAdmission))],
    ['credential-disposition-receipt', sha256(canonicalJson(credentialDisposition))],
    ['cleanup-plan', sha256(canonicalJson(cleanupPlan))],
    ['cleanup-execution', sha256(canonicalJson(cleanupExecution))],
    ['post-cleanup-capture-manifest', postCleanupCaptureManifestSha256],
  ]);
  const actualBindings = new Map(
    documents.flatMap(({ evidence }) =>
      evidence.references.map(({ source_id, sha256 }) => [source_id, sha256]),
    ),
  );
  if ([...requiredBindings].some(([sourceId, digest]) => actualBindings.get(sourceId) !== digest)) {
    invalid('recovery-source-evidence-binding');
  }
  const expectedRestrictedPackage = {
    destination_kind: 'maintainer-approved-host-restricted-location',
    destination_identity_sha256: captureManifest.destination_identity_sha256,
    capture_manifest_sha256: captureManifestSha256,
    oracle_result_sha256: oracleResultSha256,
    token_copy_absent: true,
    current_key_copy_absent: true,
    previous_key_copy_absent: true,
    manifest_finalized: true,
  };
  if (
    Object.keys(restrictedPackage).length !== Object.keys(expectedRestrictedPackage).length ||
    Object.entries(expectedRestrictedPackage).some(
      ([key, value]) => restrictedPackage[key] !== value,
    )
  ) {
    invalid('recovery-restricted-package');
  }
  const publicCandidate = {
    kind: 'apr-r4-e3-public-projection-closed-v1',
    operation_ids: captureManifest.operation_ids,
    reason: 'recovery-only',
  };
  const publicScanManifest = scanPublicCandidate({
    candidate: publicCandidate,
    corpus: publicSurfaceCorpus,
    protectedDocuments: new Map([
      ['source-bundle', canonicalJson(sourceBundle)],
      ['oracle-result', canonicalJson(oracleResult)],
      ['cleanup-plan', canonicalJson(cleanupPlan)],
      [
        'cleanup-authorization-readback',
        canonicalJson(retainedDocuments.get('cleanup-authorization-readback')),
      ],
      ['credential-admission-receipt', canonicalJson(credentialAdmission)],
      ['credential-disposition-receipt', canonicalJson(credentialDisposition)],
      ['producer-journal-seal', canonicalJson(producerJournalSeal)],
    ]),
    protectedCategories: protectedCanaryCategories(authorizations, captureManifest.repository),
  });
  const expectedPublicLeakScan = {
    source: 'post-cleanup-repository-and-output-scan',
    candidate_sha256: publicScanManifest.candidate_sha256,
    corpus_sha256: sha256(canonicalJson(publicScanManifest.corpus)),
    scanned_files: publicScanManifest.corpus.length,
    results: publicScanManifest.results,
  };
  if (canonicalJson(publicLeakScan) !== canonicalJson(expectedPublicLeakScan)) {
    invalid('recovery-public-scan-derived');
  }
  const host = {
    kind: 'apr-r4-e3-host-restricted-recovery-v1',
    repository_id: captureManifest.repository_id,
    repository: captureManifest.repository,
    operation_ids: captureManifest.operation_ids,
    disposition: 'recovery-only',
    destination_identity_sha256: captureManifest.destination_identity_sha256,
    execution_authorization_sha256: captureManifest.execution_authorization_sha256,
    producer_journal_seal_sha256: captureManifest.producer_journal_seal_sha256,
    producer_journal_seal_file_identity: captureManifest.producer_journal_seal_file_identity,
    capture_manifest_sha256: captureManifestSha256,
    post_cleanup_capture_manifest_sha256: postCleanupCaptureManifestSha256,
    oracle_result_sha256: oracleResultSha256,
    oracle_build_receipt_sha256: sha256(
      canonicalJson(retainedDocuments.get('oracle-build-receipt')),
    ),
    oracle_assembly_sha256: oracleBinaries.oracle_assembly_sha256,
    production_assembly_sha256: oracleBinaries.production_assembly_sha256,
    inventories,
    cleanup_plan_sha256: regenerated.digest,
    cleanup_execution_sha256: sha256(canonicalJson(cleanupExecution)),
    credential_admission_receipt_sha256: sha256(canonicalJson(credentialAdmission)),
    credential_disposition_receipt_sha256: sha256(canonicalJson(credentialDisposition)),
    correction_gate_receipt_sha256: sha256(
      canonicalJson(retainedDocuments.get('correction-gate-receipt')),
    ),
    public_candidate_sha256: sha256(canonicalJson(publicCandidate)),
    public_scan_manifest_sha256: sha256(canonicalJson(publicScanManifest)),
    projection_eligible: false,
    finalized: true,
  };
  return {
    host,
    publicEvidence: null,
    cleanupPlan,
    recoveryOnly: true,
    projectionEligible: false,
    publicCandidate,
    publicScanManifest,
  };
}

export function assembleTrustedProofEvidence({
  sourceMap,
  sourceBundle,
  captureManifest,
  captureManifestSha256,
  postCleanupCaptureManifest,
  postCleanupCaptureManifestSha256,
  oracleResult,
  oracleResultSha256,
  capturedSourceBodies,
  postCleanupCapturedSourceBodies,
  retainedDocuments,
  oracleBinaries,
  publicSurfaceCorpus,
  credentialCopiesAbsent,
}) {
  if (credentialCopiesAbsent !== true) invalid('assembler-credential-copies');
  if (
    sha256(canonicalJson(captureManifest)) !== captureManifestSha256 ||
    sha256(canonicalJson(postCleanupCaptureManifest)) !== postCleanupCaptureManifestSha256 ||
    sha256(canonicalJson(oracleResult)) !== oracleResultSha256
  ) {
    invalid('assembler-input-digest');
  }
  if (!(retainedDocuments instanceof Map)) invalid('assembler-retained-documents');
  exactKeys(
    oracleBinaries,
    [
      'oracle_assembly_path',
      'oracle_assembly_sha256',
      'production_assembly_path',
      'production_assembly_sha256',
    ],
    'assembler-oracle-binaries-shape',
  );
  if (
    !hex64.test(oracleBinaries.oracle_assembly_sha256) ||
    !hex64.test(oracleBinaries.production_assembly_sha256)
  ) {
    invalid('assembler-oracle-binaries-values');
  }
  const documents = validateSourceBundle(sourceMap, sourceBundle);
  const authorizations = deriveAuthorizations(
    captureManifest,
    capturedSourceBodies,
    retainedDocuments,
  );
  const credentialAdmission = retainedDocuments.get('credential-admission-receipt');
  const credentialDisposition = retainedDocuments.get('credential-disposition-receipt');
  const correctionGateReceipt = retainedDocuments.get('correction-gate-receipt');
  const producerJournalSeal = retainedDocuments.get('producer-journal-seal');
  if (
    credentialAdmission === undefined ||
    credentialDisposition === undefined ||
    correctionGateReceipt === undefined ||
    producerJournalSeal === undefined ||
    sha256(canonicalJson(producerJournalSeal)) !== captureManifest.producer_journal_seal_sha256
  ) {
    invalid('assembler-credential-lifecycle');
  }
  validateProducerJournalSeal(producerJournalSeal, captureManifest);
  validateCredentialLifecycle(
    correctionGateReceipt,
    credentialAdmission,
    credentialDisposition,
    authorizations,
    captureManifest,
  );
  const payloadReceipt = retainedDocuments.get('trusted-proof-payload-receipt-v2');
  const oracleBuildReceipt = retainedDocuments.get('oracle-build-receipt');
  if (payloadReceipt === undefined || oracleBuildReceipt === undefined) {
    invalid('assembler-build-receipts');
  }
  exactKeys(
    oracleBuildReceipt,
    [
      'kind',
      'source_commit',
      'source_tree',
      'oracle_assembly_path',
      'oracle_assembly_sha256',
      'production_assembly_path',
      'production_assembly_sha256',
      'result',
    ],
    'oracle-build-receipt-shape',
  );
  if (
    oracleBuildReceipt.kind !== 'apr-r4-e3-independent-oracle-build-receipt-v2' ||
    oracleBuildReceipt.source_commit !== oracleResult.oracle_source_sha ||
    oracleBuildReceipt.source_tree !== oracleResult.oracle_source_tree ||
    oracleBuildReceipt.oracle_assembly_path !== oracleBinaries.oracle_assembly_path ||
    oracleBuildReceipt.oracle_assembly_sha256 !== oracleResult.oracle_assembly_sha256 ||
    oracleBuildReceipt.oracle_assembly_sha256 !== oracleBinaries.oracle_assembly_sha256 ||
    oracleBuildReceipt.production_assembly_path !== oracleBinaries.production_assembly_path ||
    oracleBuildReceipt.production_assembly_sha256 !== oracleResult.production_assembly_sha256 ||
    oracleBuildReceipt.production_assembly_sha256 !== oracleBinaries.production_assembly_sha256 ||
    authorizations.execution.oracle_build.source_commit !== oracleBuildReceipt.source_commit ||
    authorizations.execution.oracle_build.source_tree !== oracleBuildReceipt.source_tree ||
    authorizations.execution.oracle_build.build_receipt_sha256 !==
      sha256(canonicalJson(oracleBuildReceipt)) ||
    authorizations.execution.oracle_build.oracle_assembly_sha256 !==
      oracleBuildReceipt.oracle_assembly_sha256 ||
    authorizations.execution.oracle_build.production_assembly_sha256 !==
      oracleBuildReceipt.production_assembly_sha256 ||
    oracleBuildReceipt.result !== 'passed'
  ) {
    invalid('oracle-build-receipt-values');
  }
  if (captureManifest.disposition === 'recovery-only') {
    return assembleRecoveryEvidence({
      sourceMap,
      sourceBundle,
      documents,
      captureManifest,
      captureManifestSha256,
      postCleanupCaptureManifest,
      postCleanupCaptureManifestSha256,
      oracleResult,
      oracleResultSha256,
      capturedSourceBodies,
      postCleanupCapturedSourceBodies,
      retainedDocuments,
      authorizations,
      credentialAdmission,
      credentialDisposition,
      producerJournalSeal,
      oracleBinaries,
      publicSurfaceCorpus,
      credentialCopiesAbsent,
    });
  }
  const approvalTransitions = validateApprovalDerivation(
    null,
    captureManifest,
    capturedSourceBodies,
    captureManifest.expected_roles,
  );
  const concurrency = validateConcurrencyDerivation(null, captureManifest, capturedSourceBodies);
  const identities = deriveIdentities(
    payloadReceipt,
    authorizations,
    captureManifest,
    capturedSourceBodies,
    oracleResult,
    concurrency,
  );
  const proofControl = deriveProofControl(
    authorizations,
    identities,
    captureManifest,
    capturedSourceBodies,
  );
  const environment = validateEnvironmentDerivation(
    null,
    captureManifest,
    capturedSourceBodies,
    authorizations,
  );
  const restrictedPackage = retainedDocuments.get('restricted-package-readback');
  const publicLeakScan = retainedDocuments.get('public-leak-scan-result');
  const cleanupPlan = retainedDocuments.get('cleanup-plan');
  const cleanupExecution = retainedDocuments.get('cleanup-execution');
  if (
    restrictedPackage === undefined ||
    publicLeakScan === undefined ||
    cleanupPlan === undefined ||
    cleanupExecution === undefined
  ) {
    invalid('assembler-retained-document-set');
  }
  const unverifiedArtifacts = new Map(
    captureManifest.artifacts.map((artifact) => [artifact.artifact_id, artifact]),
  );
  const inventories = deriveInventoriesFromOracle(
    oracleResult,
    unverifiedArtifacts,
    identities.operation_ids,
  );
  const postCleanup = derivePostCleanupEvidence({
    captureManifest,
    postCleanupCaptureManifest,
    postCleanupCapturedSourceBodies,
    authorizations,
    proofControl,
    cleanupPlan,
    exactSevenSuccess: !inventories.observed_cleanup.some(
      ({ disposition }) => disposition === 'recovery-only-delete',
    ),
    credentialCopiesAbsent,
    cleanupExecution,
    captureManifestSha256,
    postCleanupCaptureManifestSha256,
    oracleResultSha256,
  });
  const cleanup = postCleanup.cleanup;
  const expectedRestrictedPackage = {
    destination_kind: 'maintainer-approved-host-restricted-location',
    destination_identity_sha256: captureManifest.destination_identity_sha256,
    capture_manifest_sha256: captureManifestSha256,
    oracle_result_sha256: oracleResultSha256,
    token_copy_absent: credentialCopiesAbsent,
    current_key_copy_absent: credentialCopiesAbsent,
    previous_key_copy_absent: credentialCopiesAbsent,
    manifest_finalized: true,
  };
  if (canonicalJson(restrictedPackage) !== canonicalJson(expectedRestrictedPackage)) {
    invalid('assembler-restricted-package-derived');
  }
  const host = {
    kind: 'apr-r4-e3-host-restricted-evidence-v1',
    identities,
    source_map: sourceMap,
    authorizations,
    environment,
    approval_transitions: approvalTransitions,
    concurrency,
    proof_control: proofControl,
    inventories,
    cleanup,
    canaries: {
      live: {
        source: 'checked-runtime-observations',
        facts: ['github-route-observed', 'provider-route-observed', 'state-route-observed'],
      },
      cross_sink: { source: 'descendant-v2-receipt', result: 'isolated' },
      public_leak_scan: publicLeakScan,
    },
    restricted_package: restrictedPackage,
  };
  const publicCandidate = projectTrustedProofEvidenceUnchecked(host);
  const publicScanManifest = scanPublicCandidate({
    candidate: publicCandidate,
    corpus: publicSurfaceCorpus,
    protectedDocuments: new Map([
      ['source-bundle', canonicalJson(sourceBundle)],
      ['oracle-result', canonicalJson(oracleResult)],
      ['cleanup-plan', canonicalJson(cleanupPlan)],
      [
        'cleanup-authorization-readback',
        canonicalJson(retainedDocuments.get('cleanup-authorization-readback')),
      ],
      ['credential-admission-receipt', canonicalJson(credentialAdmission)],
      ['credential-disposition-receipt', canonicalJson(credentialDisposition)],
      ['correction-gate-receipt', canonicalJson(correctionGateReceipt)],
      ['producer-journal-seal', canonicalJson(producerJournalSeal)],
      ...[...capturedSourceBodies].map(([key, value]) => [`capture:${key}`, value.text]),
      ...[...postCleanupCapturedSourceBodies].map(([key, value]) => [
        `post-cleanup:${key}`,
        value.text,
      ]),
    ]),
    protectedCategories: protectedCanaryCategories(authorizations, captureManifest.repository),
  });
  const expectedPublicLeakScan = {
    source: 'post-cleanup-repository-and-output-scan',
    candidate_sha256: publicScanManifest.candidate_sha256,
    corpus_sha256: sha256(canonicalJson(publicScanManifest.corpus)),
    scanned_files: publicScanManifest.corpus.length,
    results: publicScanManifest.results,
  };
  if (canonicalJson(publicLeakScan) !== canonicalJson(expectedPublicLeakScan)) {
    invalid('assembler-public-scan-derived');
  }
  const capturedArtifacts = validateCaptureManifest(captureManifest, host, captureManifestSha256);
  validateOracleResult(
    oracleResult,
    host,
    captureManifestSha256,
    oracleResultSha256,
    capturedArtifacts,
  );
  const bindingDocuments = new Map(retainedDocuments);
  bindingDocuments.set('post-cleanup-capture-manifest', postCleanupCaptureManifest);
  validateSourceEvidenceBindings(
    documents,
    captureManifest,
    captureManifestSha256,
    oracleResultSha256,
    bindingDocuments,
    host,
  );
  const generatedCleanup = generateCleanupPlan({
    operation_ids: identities.operation_ids,
    proof_control: proofControl,
    observed_cleanup: inventories.observed_cleanup,
    resources: cleanup.resources,
  });
  if (canonicalJson(generatedCleanup.plan) !== canonicalJson(cleanupPlan)) {
    invalid('assembler-cleanup-plan-derivation');
  }
  const validation = validateHostEvidence(host);
  return {
    host,
    publicEvidence: null,
    cleanupPlan: validation.cleanupPlan,
    recoveryOnly: !validation.projectionEligible,
    projectionEligible: validation.projectionEligible,
    publicCandidate,
    publicScanManifest,
  };
}

export function buildFinalizedPrivatePackageManifest({
  host,
  sourceBundle,
  captureManifestSha256,
  postCleanupCaptureManifestSha256,
  oracleResultSha256,
  cleanupPlan,
  credentialAdmissionReceiptSha256,
  credentialDispositionReceiptSha256,
  correctionGateReceiptSha256,
  producerJournalSealSha256,
  producerJournalSealFileIdentity,
  oracleBuildReceiptSha256,
  oracleAssemblySha256,
  productionAssemblySha256,
  publicCandidateSha256,
  publicScanManifestSha256,
}) {
  const recovery = host.kind === 'apr-r4-e3-host-restricted-recovery-v1';
  const validation = recovery
    ? { projectionEligible: false, cleanupPlan }
    : validateHostEvidence(host);
  if (recovery) {
    exactKeys(
      host,
      [
        'kind',
        'repository_id',
        'repository',
        'operation_ids',
        'disposition',
        'destination_identity_sha256',
        'execution_authorization_sha256',
        'producer_journal_seal_sha256',
        'producer_journal_seal_file_identity',
        'capture_manifest_sha256',
        'post_cleanup_capture_manifest_sha256',
        'oracle_result_sha256',
        'oracle_build_receipt_sha256',
        'oracle_assembly_sha256',
        'production_assembly_sha256',
        'inventories',
        'cleanup_plan_sha256',
        'cleanup_execution_sha256',
        'credential_admission_receipt_sha256',
        'credential_disposition_receipt_sha256',
        'correction_gate_receipt_sha256',
        'public_candidate_sha256',
        'public_scan_manifest_sha256',
        'projection_eligible',
        'finalized',
      ],
      'recovery-private-evidence-shape',
    );
  }
  if (
    captureManifestSha256 !==
      (recovery ? host.capture_manifest_sha256 : host.restricted_package.capture_manifest_sha256) ||
    !hex64.test(postCleanupCaptureManifestSha256) ||
    postCleanupCaptureManifestSha256 !==
      (recovery ? host.post_cleanup_capture_manifest_sha256 : postCleanupCaptureManifestSha256) ||
    oracleResultSha256 !==
      (recovery ? host.oracle_result_sha256 : host.restricted_package.oracle_result_sha256) ||
    !hex64.test(credentialAdmissionReceiptSha256) ||
    !hex64.test(credentialDispositionReceiptSha256) ||
    !hex64.test(correctionGateReceiptSha256) ||
    !hex64.test(producerJournalSealSha256) ||
    !hex64.test(producerJournalSealFileIdentity) ||
    oracleBuildReceiptSha256 !==
      (recovery
        ? host.oracle_build_receipt_sha256
        : host.authorizations.execution.oracle_build.build_receipt_sha256) ||
    oracleAssemblySha256 !==
      (recovery
        ? host.oracle_assembly_sha256
        : host.authorizations.execution.oracle_build.oracle_assembly_sha256) ||
    productionAssemblySha256 !==
      (recovery
        ? host.production_assembly_sha256
        : host.authorizations.execution.oracle_build.production_assembly_sha256) ||
    (recovery
      ? publicCandidateSha256 !== host.public_candidate_sha256 ||
        publicScanManifestSha256 !== host.public_scan_manifest_sha256 ||
        credentialAdmissionReceiptSha256 !== host.credential_admission_receipt_sha256 ||
        credentialDispositionReceiptSha256 !== host.credential_disposition_receipt_sha256 ||
        correctionGateReceiptSha256 !== host.correction_gate_receipt_sha256 ||
        producerJournalSealSha256 !== host.producer_journal_seal_sha256 ||
        producerJournalSealFileIdentity !== host.producer_journal_seal_file_identity ||
        host.cleanup_plan_sha256 !== sha256(canonicalJson(cleanupPlan)) ||
        host.projection_eligible !== false ||
        host.finalized !== true
      : publicCandidateSha256 !== host.canaries.public_leak_scan.candidate_sha256) ||
    !hex64.test(publicScanManifestSha256) ||
    canonicalJson(cleanupPlan) !== canonicalJson(validation.cleanupPlan)
  ) {
    invalid('private-package-input-binding');
  }
  return {
    kind: 'apr-r4-e3-private-package-manifest-v1',
    destination_identity_sha256: recovery
      ? host.destination_identity_sha256
      : host.restricted_package.destination_identity_sha256,
    host_evidence_sha256: sha256(canonicalJson(host)),
    source_bundle_sha256: sha256(canonicalJson(sourceBundle)),
    capture_manifest_sha256: captureManifestSha256,
    post_cleanup_capture_manifest_sha256: postCleanupCaptureManifestSha256,
    oracle_result_sha256: oracleResultSha256,
    oracle_build_receipt_sha256: oracleBuildReceiptSha256,
    oracle_assembly_sha256: oracleAssemblySha256,
    production_assembly_sha256: productionAssemblySha256,
    public_candidate_sha256: publicCandidateSha256,
    public_scan_manifest_sha256: publicScanManifestSha256,
    cleanup_plan_sha256: sha256(canonicalJson(cleanupPlan)),
    credential_admission_receipt_sha256: credentialAdmissionReceiptSha256,
    credential_disposition_receipt_sha256: credentialDispositionReceiptSha256,
    correction_gate_receipt_sha256: correctionGateReceiptSha256,
    producer_journal_seal_sha256: producerJournalSealSha256,
    producer_journal_seal_file_identity: producerJournalSealFileIdentity,
    credential_absence: {
      github_token: recovery ? true : host.restricted_package.token_copy_absent,
      current_state_key: recovery ? true : host.restricted_package.current_key_copy_absent,
      previous_state_key: recovery ? true : host.restricted_package.previous_key_copy_absent,
    },
    projection_eligible: validation.projectionEligible,
    finalized: true,
  };
}

export function assertFinalizedPrivatePackage({
  host,
  sourceBundle,
  captureManifestSha256,
  postCleanupCaptureManifestSha256,
  oracleResultSha256,
  cleanupPlan,
  credentialAdmissionReceiptSha256,
  credentialDispositionReceiptSha256,
  correctionGateReceiptSha256,
  producerJournalSealSha256,
  producerJournalSealFileIdentity,
  oracleBuildReceiptSha256,
  oracleAssemblySha256,
  productionAssemblySha256,
  publicCandidateSha256,
  publicScanManifestSha256,
  privatePackageManifest,
}) {
  const expected = buildFinalizedPrivatePackageManifest({
    host,
    sourceBundle,
    captureManifestSha256,
    postCleanupCaptureManifestSha256,
    oracleResultSha256,
    cleanupPlan,
    credentialAdmissionReceiptSha256,
    credentialDispositionReceiptSha256,
    correctionGateReceiptSha256,
    producerJournalSealSha256,
    producerJournalSealFileIdentity,
    oracleBuildReceiptSha256,
    oracleAssemblySha256,
    productionAssemblySha256,
    publicCandidateSha256,
    publicScanManifestSha256,
  });
  if (canonicalJson(privatePackageManifest) !== canonicalJson(expected)) {
    invalid('private-package-finalized-readback');
  }
  return expected.projection_eligible;
}

export function projectFinalizedTrustedProofEvidence(input) {
  const eligible = assertFinalizedPrivatePackage(input);
  const { host } = input;
  if (!eligible) invalid('recovery-only-no-projection');
  const publicEvidence = projectTrustedProofEvidence(host);
  assertPublicSafeEvidence(publicEvidence);
  return publicEvidence;
}

export function projectTrustedProofEvidence(input) {
  const validation = validateHostEvidence(input);
  if (!validation.projectionEligible) invalid('recovery-only-no-projection');
  return projectTrustedProofEvidenceUnchecked(input);
}

function projectTrustedProofEvidenceUnchecked(input) {
  return {
    kind: 'apr-r4-e3-public-safe-evidence-v1',
    identities: {
      workflow_sha: input.identities.workflow_sha,
      action_source_sha: input.identities.action_source_sha,
      payload_source_sha: input.identities.payload_source_sha,
      payload_sha256: input.identities.payload_sha256,
    },
    participating_run_ids: [
      input.approval_transitions.bootstrap.run_id,
      input.approval_transitions.continuation.run_id,
      input.approval_transitions.stale.run_id,
      input.identities.unauthorized_follow_on.run_id,
    ].sort((a, b) => a.localeCompare(b, 'en', { numeric: true })),
    scheduling: {
      distinct_groups: true,
      holder_waiter_pairs_observed: 2,
      holders_uncancelled: true,
      waiters_started_after_holders: true,
    },
    state_outcomes: {
      bootstrap: 'passed',
      continuation: 'passed',
      stale_rejection: 'passed',
      accepted_generations: 2,
      product_anchor_count: expectedProductRoles.length,
    },
    cleanup: {
      complete: true,
      final_state_inventory_count: 0,
      authorization_absent: true,
      operation_created_secrets_absent: true,
      environment_restored: true,
      fixture_resources_terminal: true,
      credential_copies_absent: true,
      all_runs_terminal: true,
    },
    canaries: {
      public_surfaces: 'clear',
      nested_session_plaintext: 'absent',
      provider_content: 'absent',
      tool_data: 'absent',
      protected_digests: 'absent',
    },
  };
}

export function assertPublicSafeEvidence(value) {
  exactKeys(
    value,
    [
      'kind',
      'identities',
      'participating_run_ids',
      'scheduling',
      'state_outcomes',
      'cleanup',
      'canaries',
    ],
    'public-shape',
  );
  if (value.kind !== 'apr-r4-e3-public-safe-evidence-v1') invalid('public-kind');
  exactKeys(
    value.identities,
    ['workflow_sha', 'action_source_sha', 'payload_source_sha', 'payload_sha256'],
    'public-identities-shape',
  );
  exactKeys(
    value.scheduling,
    [
      'distinct_groups',
      'holder_waiter_pairs_observed',
      'holders_uncancelled',
      'waiters_started_after_holders',
    ],
    'public-scheduling-shape',
  );
  exactKeys(
    value.state_outcomes,
    [
      'bootstrap',
      'continuation',
      'stale_rejection',
      'accepted_generations',
      'product_anchor_count',
    ],
    'public-state-shape',
  );
  exactKeys(
    value.cleanup,
    [
      'complete',
      'final_state_inventory_count',
      'authorization_absent',
      'operation_created_secrets_absent',
      'environment_restored',
      'fixture_resources_terminal',
      'credential_copies_absent',
      'all_runs_terminal',
    ],
    'public-cleanup-shape',
  );
  exactKeys(
    value.canaries,
    [
      'public_surfaces',
      'nested_session_plaintext',
      'provider_content',
      'tool_data',
      'protected_digests',
    ],
    'public-canary-shape',
  );
  const serialized = JSON.stringify(value);
  const forbidden = [
    'artifact_id',
    'operation_id',
    'comment_id',
    'environment_id',
    'reviewer',
    'approval',
    'capture_sha256',
    'manifest_sha256',
    'plan_sha256',
    'recovery-only',
    'lineage',
    'candidate',
    'acceptance',
    'archive',
    'encrypted',
  ];
  if (forbidden.some((token) => serialized.includes(token))) invalid('public-forbidden-data');
  if (
    !Array.isArray(value.participating_run_ids) ||
    value.participating_run_ids.length !== 4 ||
    new Set(value.participating_run_ids).size !== 4 ||
    value.participating_run_ids.some((item) => !decimal.test(item)) ||
    JSON.stringify(value.participating_run_ids) !==
      JSON.stringify(
        [...value.participating_run_ids].sort((a, b) =>
          a.localeCompare(b, 'en', { numeric: true }),
        ),
      ) ||
    value.state_outcomes.product_anchor_count !== 7 ||
    value.cleanup.complete !== true ||
    value.cleanup.final_state_inventory_count !== 0 ||
    Object.values(value.canaries).some((item) => !['clear', 'absent'].includes(item))
  ) {
    invalid('public-values');
  }
  return true;
}
