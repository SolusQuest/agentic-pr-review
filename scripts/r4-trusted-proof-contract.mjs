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
]);

function invalid(code) {
  throw new Error(`APR_R4_E3_EVIDENCE_INVALID ${code}`);
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
      'payload receipt, execution authorization, capture manifest, oracle build receipt and result',
    ],
    [
      '/authorizations',
      'durable-maintainer-authorization',
      'authorization-readbacks',
      'issue-comment-readback',
      'none',
      'authorizations/*.json',
    ],
    [
      '/environment',
      'github-rest-and-ui-attestation',
      'environment-protection',
      'environment-and-ui',
      'none',
      'environment protection plus UI attestation',
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
      'run-concurrency-group',
      'complete-cursor',
      'normal and stale ahead_of_run',
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
      'cleanup-readback',
      'cleanup-readbacks',
      'cleanup-target-readbacks',
      'none',
      'all ordered readbacks',
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
      'public-leak-scan',
      'public-leak-scan',
      'repository-and-output-scan',
      'complete-cursor',
      'post-cleanup scan',
    ],
    [
      '/restricted_package',
      'package-inputs',
      'restricted-package-inputs',
      'capture-oracle-and-credential-absence',
      'none',
      'private package inputs; projection separately requires finalized manifest readback',
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
      !['none', 'complete-cursor'].includes(entry.pagination) ||
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
  uiAttestationSha256,
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
  const authorizationReferences = ['cleanup', 'execution', 'setup']
    .flatMap((phase) => {
      const source = host.authorizations[phase].source;
      const comment = captureReference(
        `authorization-${phase}-comment-${source.comment_id}:page:1`,
      );
      if (comment.sha256 !== source.capture_body_sha256) {
        invalid('source-evidence-authorization-digest');
      }
      return [comment, captureReference(`authorization-${phase}-permission-maintainer:page:1`)];
    })
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
      const reference = captureReference(`proof-control-comment-${comment.comment_id}:page:1`);
      if (reference.sha256 !== comment.capture_body_sha256) {
        invalid('source-evidence-proof-control-digest');
      }
      return comment.kind === 'release' || comment.kind === 'stale-release'
        ? [
            reference,
            captureReference(`proof-control-permission-${comment.comment_id}-maintainer:page:1`),
          ]
        : [reference];
    })
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
          `authorization-execution-comment-${host.authorizations.execution.source.comment_id}:page:1`,
        ),
        { source_id: 'capture-manifest', sha256: captureManifestSha256 },
        retainedReference('oracle-build-receipt'),
        { source_id: 'oracle-result', sha256: oracleResultSha256 },
        receiptReference,
      ].sort((a, b) => a.source_id.localeCompare(b.source_id)),
    ],
    ['/authorizations', authorizationReferences],
    [
      '/environment',
      [
        captureReference('environment-protection:page:1'),
        {
          source_id: 'environment-ui-attestation',
          sha256: uiAttestationSha256,
        },
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
    ['/cleanup', [retainedReference('cleanup-plan'), retainedReference('cleanup-readbacks')]],
    [
      '/canaries/live',
      [
        { source_id: 'capture-manifest', sha256: captureManifestSha256 },
        { source_id: 'oracle-result', sha256: oracleResultSha256 },
        ...Object.values(host.proof_control)
          .flatMap((family) => family.comments)
          .filter((comment) => comment.kind === 'ready' || comment.kind === 'stale-ready')
          .map((comment) => captureReference(`proof-control-comment-${comment.comment_id}:page:1`)),
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
  uiAttestation,
  uiAttestationSha256,
  authorizations,
) {
  const source = captureManifest.sources.find(
    (candidate) => candidate.source_id === 'environment-protection:page:1',
  );
  const retained = capturedSourceBodies.get('environment-protection:page:1');
  if (
    source === undefined ||
    retained === undefined ||
    typeof retained.text !== 'string' ||
    sha256(Buffer.from(retained.text, 'utf8')) !== source.body_sha256 ||
    String(Buffer.byteLength(retained.text, 'utf8')) !== source.body_size ||
    sha256(canonicalJson(uiAttestation)) !== uiAttestationSha256
  ) {
    invalid('environment-source-binding');
  }
  let response;
  try {
    response = JSON.parse(retained.text);
  } catch {
    invalid('environment-source-json');
  }
  exactKeys(
    response,
    ['id', 'name', 'protection_rules', 'deployment_branch_policy'],
    'environment-response-shape',
  );
  const reviewersRule = response.protection_rules?.find(
    (rule) => rule?.type === 'required_reviewers',
  );
  const branchPolicy = response.deployment_branch_policy;
  if (
    response.name !== 'r4-trusted-proof' ||
    !Array.isArray(response.protection_rules) ||
    reviewersRule === undefined ||
    !Array.isArray(reviewersRule.reviewers) ||
    branchPolicy === null ||
    typeof branchPolicy !== 'object' ||
    branchPolicy.protected_branches !== false ||
    branchPolicy.custom_branch_policies !== true
  ) {
    invalid('environment-response-values');
  }
  exactKeys(
    uiAttestation,
    [
      'repository',
      'environment',
      'source_kind',
      'observation',
      'capture_sha256',
      'maintainer_id',
      'prevent_self_review',
      'administrator_bypass',
    ],
    'environment-ui-source-shape',
  );
  interval(uiAttestation.observation, 'environment-ui-source-observation');
  const derived = {
    name: response.name,
    prevent_self_review: uiAttestation.prevent_self_review,
    protection_snapshot: {
      source_id: 'environment-protection',
      environment_id: String(response.id),
      deployment_branch_policy: 'main-only',
      required_reviewer_ids: reviewersRule.reviewers.map((reviewer) =>
        String(reviewer?.reviewer?.id),
      ),
      required_approvals: reviewersRule.required_approvals,
      secret_names: [],
      token_permissions: { actions: 'write', contents: 'read', pull_requests: 'write' },
      readback_sha256: source.body_sha256,
      observation: {
        request_started: source.request_started_unix_milliseconds,
        response_received: source.response_received_unix_milliseconds,
      },
    },
    ui_attestation: {
      repository: uiAttestation.repository,
      environment: uiAttestation.environment,
      source_kind: uiAttestation.source_kind,
      observation: uiAttestation.observation,
      capture_sha256: uiAttestation.capture_sha256,
      maintainer_id: uiAttestation.maintainer_id,
      administrator_bypass: uiAttestation.administrator_bypass,
    },
  };
  derived.protection_snapshot.secret_names = [
    'DEEPSEEK_API_KEY',
    'AGENTIC_PR_REVIEW_STATE_KEY',
    'AGENTIC_PR_REVIEW_PREVIOUS_STATE_KEY',
  ];
  if (
    uiAttestation.repository !== authorizations.execution.coordinates.repository ||
    uiAttestation.environment !== response.name ||
    uiAttestation.source_kind !== 'github-environment-ui' ||
    uiAttestation.maintainer_id !== '16307884' ||
    uiAttestation.prevent_self_review !== false ||
    uiAttestation.administrator_bypass !== false ||
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
  const normalOperation = execution.operation_runs.find(
    (run) => run.scope === 'normal',
  )?.operation_id;
  const staleOperation = execution.operation_runs.find(
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

function validateAuthorizationDerivation(host, captureManifest, capturedSourceBodies) {
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
      invalid('authorization-capture-binding');
    }
    let value;
    try {
      value = JSON.parse(retained.text);
    } catch {
      invalid('authorization-capture-json');
    }
    return { value, manifest };
  };

  for (const phase of ['setup', 'execution', 'cleanup']) {
    const expected = host.authorizations[phase];
    const commentSourceId = `authorization-${phase}-comment-${expected.source.comment_id}:page:1`;
    const permissionSourceId = `authorization-${phase}-permission-maintainer:page:1`;
    const commentCapture = read(commentSourceId);
    const permissionCapture = read(permissionSourceId);
    const response = commentCapture.value;
    const permission = permissionCapture.value;
    if (
      response === null ||
      typeof response !== 'object' ||
      String(response.id) !== expected.source.comment_id ||
      typeof response.body !== 'string' ||
      response.user === null ||
      typeof response.user !== 'object' ||
      String(response.user.id) !== expected.source.author_id ||
      typeof response.user.login !== 'string' ||
      permission === null ||
      typeof permission !== 'object' ||
      !['admin', 'maintain'].includes(permission.permission) ||
      permission.user === null ||
      typeof permission.user !== 'object' ||
      String(permission.user.id) !== expected.source.author_id ||
      permission.user.login !== response.user.login
    ) {
      invalid(`authorization-${phase}-raw-response`);
    }
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
      marker.repository !== host.identities.repository ||
      String(marker.issue_number) !== expected.source.issue_number ||
      response.body !== `${prefix}${JSON.stringify(marker)}${suffix}`
    ) {
      invalid(`authorization-${phase}-marker-values`);
    }
    const bodySha256 = sha256(Buffer.from(response.body, 'utf8'));
    const derived = {
      kind: marker.authorization.kind,
      phase: marker.authorization.phase,
      source: {
        kind: 'maintainer-comment-readback',
        phase,
        repository: marker.repository,
        issue_number: String(marker.issue_number),
        comment_id: String(response.id),
        author_id: String(response.user.id),
        author_permission: permission.permission,
        capture_body_sha256: commentCapture.manifest.body_sha256,
        body_sha256: bodySha256,
        readback_sha256: bodySha256,
        observation: {
          request_started: commentCapture.manifest.request_started_unix_milliseconds,
          response_received: commentCapture.manifest.response_received_unix_milliseconds,
        },
      },
      ...Object.fromEntries(
        Object.entries(marker.authorization).filter(([key]) => key !== 'kind' && key !== 'phase'),
      ),
    };
    if (canonicalJson(derived) !== canonicalJson(expected)) {
      invalid(`authorization-${phase}-derivation`);
    }
  }
}

function deriveAuthorizations(captureManifest, capturedSourceBodies) {
  const authorizations = {};
  const manifestById = new Map(captureManifest.sources.map((source) => [source.source_id, source]));
  for (const phase of ['setup', 'execution', 'cleanup']) {
    const pattern = new RegExp(`^authorization-${phase}-comment-([1-9][0-9]*):page:1$`, 'u');
    const matches = captureManifest.sources.filter((source) => pattern.test(source.source_id));
    if (matches.length !== 1) invalid(`authorization-${phase}-source-cardinality`);
    const source = matches[0];
    const retained = capturedSourceBodies.get(source.source_id);
    const permissionSourceId = `authorization-${phase}-permission-maintainer:page:1`;
    const permissionSource = manifestById.get(permissionSourceId);
    const permissionRetained = capturedSourceBodies.get(permissionSourceId);
    if (
      retained === undefined ||
      permissionSource === undefined ||
      permissionRetained === undefined ||
      sha256(Buffer.from(retained.text, 'utf8')) !== source.body_sha256 ||
      sha256(Buffer.from(permissionRetained.text, 'utf8')) !== permissionSource.body_sha256
    ) {
      invalid(`authorization-${phase}-source-binding`);
    }
    let response;
    let permission;
    try {
      response = JSON.parse(retained.text);
      permission = JSON.parse(permissionRetained.text);
    } catch {
      invalid(`authorization-${phase}-source-json`);
    }
    const prefix = '<!-- apr-r4-e3-authorization ';
    const suffix = ' -->';
    if (
      typeof response.body !== 'string' ||
      !response.body.startsWith(prefix) ||
      !response.body.endsWith(suffix)
    ) {
      invalid(`authorization-${phase}-marker`);
    }
    let marker;
    try {
      marker = JSON.parse(response.body.slice(prefix.length, -suffix.length));
    } catch {
      invalid(`authorization-${phase}-marker`);
    }
    authorizations[phase] = {
      kind: marker.authorization?.kind,
      phase: marker.authorization?.phase,
      source: {
        kind: 'maintainer-comment-readback',
        phase,
        repository: marker.repository,
        issue_number: String(marker.issue_number),
        comment_id: String(response.id),
        author_id: String(response.user?.id),
        author_permission: permission.permission,
        capture_body_sha256: source.body_sha256,
        body_sha256: sha256(Buffer.from(response.body, 'utf8')),
        readback_sha256: sha256(Buffer.from(response.body, 'utf8')),
        observation: {
          request_started: source.request_started_unix_milliseconds,
          response_received: source.response_received_unix_milliseconds,
        },
      },
      ...Object.fromEntries(
        Object.entries(marker.authorization ?? {}).filter(
          ([key]) => key !== 'kind' && key !== 'phase',
        ),
      ),
    };
  }
  const provisional = {
    authorizations,
    identities: { repository: authorizations.execution.coordinates?.repository },
  };
  validateAuthorizationDerivation(provisional, captureManifest, capturedSourceBodies);
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
      const capture = read(`proof-control-comment-${expected.comment_id}:page:1`);
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
        const permission = read(
          `proof-control-permission-${expected.comment_id}-maintainer:page:1`,
        ).value;
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
    /^proof-control-comment-[1-9][0-9]*:page:1$/u.test(source.source_id),
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
      const permissionSourceId = `proof-control-permission-${response.id}-maintainer:page:1`;
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
  const operationForScope = (scope) =>
    authorizations.execution.operation_runs.find((run) => run.scope === scope)?.operation_id;
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
    if (jobPages[0].value.total_count !== jobs.length || jobs.length !== 1) {
      invalid(`approval-${phase}-jobs-cardinality`);
    }
    const job = jobs[0];
    const reviewers = pending.reviewers?.map((reviewer) => String(reviewer.reviewer?.id));
    const environments = approval.environments;
    const started = Date.parse(job.started_at);
    if (
      pending.environment === null ||
      typeof pending.environment !== 'object' ||
      !Array.isArray(reviewers) ||
      reviewers.length !== 1 ||
      approval.state !== 'approved' ||
      String(approval.user?.id) !== '16307884' ||
      !Array.isArray(environments) ||
      environments.length !== 1 ||
      !Number.isSafeInteger(job.id) ||
      String(job.run_id) !== runId ||
      job.run_attempt !== 1 ||
      job.name !==
        (phase === 'continuation' ? 'workflow-dispatch-review' : 'workflow-run-review') ||
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
        if (!Array.isArray(first.ahead_of_run) || first.ahead_of_run.length !== 2) {
          invalid(`concurrency-${scope}-member-cardinality`);
        }
        return [
          scope,
          {
            terminal: {
              holder_run_id: holderRunId,
              waiter_run_id: String(first.ahead_of_run[1]?.run_id),
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
      pageSources.length === 0 ||
      pageSources.some(
        (source, index) =>
          source.page !== index + 1 ||
          (index === pageSources.length - 1
            ? source.next_route !== null
            : source.next_route !== pageSources[index + 1].route),
      )
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
          value.group !== first.group ||
          value.cancel_in_progress !== false ||
          !Array.isArray(value.ahead_of_run),
      )
    ) {
      invalid(`concurrency-${scope}-raw-values`);
    }
    const members = pageCaptures.flatMap(({ value }) => value.ahead_of_run);
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
      group: first.group,
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
    !['admin', 'maintain'].includes(value.author_permission) ||
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
      'operation_runs',
      'authorization_manifests',
      'environment_snapshot_sha256',
      'actors',
      'credential_files',
      'destination_identity',
      'commands',
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
    !Array.isArray(value.execution.operation_runs) ||
    value.execution.operation_runs.length !== 4 ||
    value.execution.operation_runs.some(
      (run, index) =>
        JSON.stringify(Object.keys(run)) !==
          JSON.stringify(['operation_id', 'scope', 'run_id', 'run_attempt']) ||
        run.operation_id !== value.execution.operation_ids[index < 2 ? 0 : 1] ||
        run.scope !== (index < 2 ? 'normal' : 'stale') ||
        !decimal.test(run.run_id) ||
        run.run_attempt !== 1,
    ) ||
    new Set(value.execution.operation_runs.map(({ run_id }) => run_id)).size !== 4 ||
    value.execution.authorization_manifests.length !== 2 ||
    value.execution.authorization_manifests.some((item) => !hex64.test(item)) ||
    !hex64.test(value.execution.environment_snapshot_sha256) ||
    JSON.stringify(value.execution.actors) !==
      JSON.stringify([{ id: '16307884', permission: 'admin' }]) ||
    !hex64.test(value.execution.destination_identity) ||
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
  exactArray(
    value.execution.credential_files.map(({ name }) => name),
    ['github-token', 'current-state-key', 'previous-state-key'],
    'execution-authorization-credentials',
  );
  for (const credential of value.execution.credential_files) {
    exactKeys(credential, ['name', 'file_identity_sha256'], 'execution-credential-shape');
    if (!hex64.test(credential.file_identity_sha256)) invalid('execution-credential-values');
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
        record.producing_run_attempt !== 1 ||
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
      family.comments.length !== expectedKinds.length
    ) {
      invalid('cleanup-plan-control-values');
    }
    const familyIds = [];
    for (let index = 0; index < expectedKinds.length; index += 1) {
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
  if (
    input.resources.authorization_variable !== 'R4_TRUSTED_PROOF_AUTHORIZATION' ||
    JSON.stringify(input.resources.secret_names) !==
      JSON.stringify([
        'DEEPSEEK_API_KEY',
        'AGENTIC_PR_REVIEW_STATE_KEY',
        'AGENTIC_PR_REVIEW_PREVIOUS_STATE_KEY',
      ]) ||
    input.resources.environment !== 'r4-trusted-proof' ||
    input.resources.fixture_refs.length !== 2 ||
    new Set(input.resources.fixture_refs).size !== 2 ||
    input.resources.fixture_refs.some((value) => !fixtureRef.test(value)) ||
    input.resources.fixture_pr_numbers.length !== 2 ||
    input.resources.fixture_pr_numbers.some((value) => !decimal.test(value)) ||
    new Set(input.resources.fixture_pr_numbers).size !== 2 ||
    JSON.stringify(input.resources.credential_copies) !==
      JSON.stringify(['github-token', 'current-state-key', 'previous-state-key']) ||
    !hex64.test(input.resources.environment_snapshot_sha256) ||
    input.resources.run_ids.length !== 4 ||
    input.resources.run_ids.some((value) => !decimal.test(value)) ||
    new Set(input.resources.run_ids).size !== 4
  ) {
    invalid('cleanup-plan-resource-values');
  }
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
        name,
        mutation: 'delete-actions-secret',
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
      sticky: {
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
  exactKeys(
    input.environment,
    ['name', 'prevent_self_review', 'protection_snapshot', 'ui_attestation'],
    'environment-shape',
  );
  exactKeys(
    input.environment.protection_snapshot,
    [
      'source_id',
      'environment_id',
      'deployment_branch_policy',
      'required_reviewer_ids',
      'required_approvals',
      'secret_names',
      'token_permissions',
      'readback_sha256',
      'observation',
    ],
    'environment-protection-shape',
  );
  interval(input.environment.protection_snapshot.observation, 'environment-protection-observation');
  if (
    input.environment.protection_snapshot.source_id !== 'environment-protection' ||
    !decimal.test(input.environment.protection_snapshot.environment_id) ||
    input.environment.protection_snapshot.deployment_branch_policy !== 'main-only' ||
    JSON.stringify(input.environment.protection_snapshot.required_reviewer_ids) !==
      JSON.stringify(['16307884']) ||
    input.environment.protection_snapshot.required_approvals !== 1 ||
    JSON.stringify(input.environment.protection_snapshot.secret_names) !==
      JSON.stringify([
        'DEEPSEEK_API_KEY',
        'AGENTIC_PR_REVIEW_STATE_KEY',
        'AGENTIC_PR_REVIEW_PREVIOUS_STATE_KEY',
      ]) ||
    JSON.stringify(input.environment.protection_snapshot.token_permissions) !==
      JSON.stringify({ actions: 'write', contents: 'read', pull_requests: 'write' }) ||
    !hex64.test(input.environment.protection_snapshot.readback_sha256)
  )
    invalid('environment-protection-values');
  exactKeys(
    input.environment.ui_attestation,
    [
      'repository',
      'environment',
      'source_kind',
      'observation',
      'capture_sha256',
      'maintainer_id',
      'administrator_bypass',
    ],
    'ui-attestation-shape',
  );
  interval(input.environment.ui_attestation.observation, 'ui-attestation-interval');
  if (
    input.environment.name !== 'r4-trusted-proof' ||
    input.environment.prevent_self_review !== false ||
    input.environment.ui_attestation.repository !== input.identities.repository ||
    input.environment.ui_attestation.environment !== input.environment.name ||
    input.environment.ui_attestation.source_kind !== 'github-environment-ui' ||
    !hex64.test(input.environment.ui_attestation.capture_sha256) ||
    input.environment.ui_attestation.maintainer_id !== '16307884' ||
    input.environment.ui_attestation.administrator_bypass !== false
  ) {
    invalid('ui-attestation-values');
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
    ['plan_sha256', 'resources', 'entry_gate', 'ordered_readbacks', 'projection_gate'],
    'cleanup-shape',
  );
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
  if (input.cleanup.ordered_readbacks.some((item) => item.complete !== true))
    invalid('cleanup-readback');
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
      'private_manifest_finalized',
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
  exactKeys(input.canaries.public_leak_scan, ['source', 'results'], 'canary-leak-shape');
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
    input.cleanup.projection_gate.private_manifest_finalized !== true
  ) {
    invalid('restricted-package-values');
  }
  return { cleanupPlan: generated.plan, inventory, projectionEligible };
}

function validateCaptureManifest(value, host, captureManifestSha256) {
  exactKeys(
    value,
    [
      'kind',
      'repository_id',
      'repository',
      'operation_ids',
      'operation_runs',
      'source_map_sha256',
      'destination_identity_sha256',
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
    JSON.stringify(value.operation_runs) !==
      JSON.stringify(
        host.authorizations.execution.operation_runs.map((run) => ({
          ...run,
          run_attempt: String(run.run_attempt),
        })),
      ) ||
    !Array.isArray(value.operation_runs) ||
    value.operation_runs.length !== 4 ||
    value.source_map_sha256 !== sha256(canonicalJson(host.source_map)) ||
    value.destination_identity_sha256 !== host.restricted_package.destination_identity_sha256 ||
    value.finalized !== true ||
    !Array.isArray(value.sources) ||
    value.sources.length === 0 ||
    !Array.isArray(value.artifacts) ||
    value.artifacts.length === 0 ||
    captureManifestSha256 !== host.restricted_package.capture_manifest_sha256
  ) {
    invalid('capture-manifest-values');
  }
  const operationByRun = new Map();
  for (const run of value.operation_runs) {
    exactKeys(
      run,
      ['operation_id', 'scope', 'run_id', 'run_attempt'],
      'capture-operation-run-shape',
    );
    if (
      operationByRun.has(run.run_id) ||
      !value.operation_ids.includes(run.operation_id) ||
      !['normal', 'stale'].includes(run.scope) ||
      !decimal.test(run.run_id) ||
      run.run_attempt !== '1'
    ) {
      invalid('capture-operation-run-values');
    }
    operationByRun.set(run.run_id, run);
  }
  for (const operationId of value.operation_ids) {
    const runs = value.operation_runs.filter(({ operation_id }) => operation_id === operationId);
    if (runs.length !== 2 || new Set(runs.map(({ scope }) => scope)).size !== 1) {
      invalid('capture-operation-run-values');
    }
  }
  if (
    value.operation_runs.filter(({ scope }) => scope === 'normal').length !== 2 ||
    value.operation_runs.filter(({ scope }) => scope === 'stale').length !== 2
  ) {
    invalid('capture-operation-run-values');
  }
  const sourceIds = new Set();
  const sourcePaths = new Set();
  for (const source of value.sources) {
    const sourceIdentity = /^(?<family>.+):page:(?<page>[1-9][0-9]*)$/u.exec(source.source_id);
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
      sourceIdentity === null ||
      Number(sourceIdentity.groups.page) !== source.page ||
      !source.route.startsWith(`/repos/${host.identities.repository}/`) ||
      !Number.isSafeInteger(source.page) ||
      source.page < 1 ||
      source.status !== 200 ||
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
  const artifactIds = new Set();
  const artifactNames = new Set();
  const artifactsById = new Map();
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
      !operationByRun.has(artifact.producing_run_id) ||
      operationByRun.get(artifact.producing_run_id).run_attempt !==
        artifact.producing_run_attempt ||
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
  const expectedIds = host.inventories.observed_cleanup.map(({ artifact_id }) => artifact_id);
  exactArray(
    [...artifactIds].sort((a, b) => a.localeCompare(b, 'en', { numeric: true })),
    [...expectedIds].sort((a, b) => a.localeCompare(b, 'en', { numeric: true })),
    'capture-artifact-inventory',
  );
  return artifactsById;
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

function validateOracleResult(
  value,
  host,
  captureManifestSha256,
  oracleResultSha256,
  capturedArtifacts,
) {
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
    value.exact_seven_success !==
      !host.inventories.observed_cleanup.some(
        ({ disposition }) => disposition === 'recovery-only-delete',
      ) ||
    value.recovery_only !==
      host.inventories.observed_cleanup.some(
        ({ disposition }) => disposition === 'recovery-only-delete',
      ) ||
    oracleResultSha256 !== host.restricted_package.oracle_result_sha256 ||
    !Array.isArray(value.records) ||
    value.records.length !== host.inventories.observed_cleanup.length
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
  exactArray(
    value.records.filter(({ role }) => expectedProductRoles.includes(role)).map(({ role }) => role),
    expectedProductRoles,
    'oracle-success-roles',
  );
  const derivedInventories = deriveInventoriesFromOracle(
    value,
    capturedArtifacts,
    host.identities.operation_ids,
  );
  if (canonicalJson(derivedInventories) !== canonicalJson(host.inventories)) {
    invalid('oracle-inventory-derivation');
  }
  return derivedInventories;
}

export function assembleTrustedProofEvidence({
  sourceMap,
  sourceBundle,
  captureManifest,
  captureManifestSha256,
  oracleResult,
  oracleResultSha256,
  capturedSourceBodies,
  uiAttestation,
  uiAttestationSha256,
  retainedDocuments,
  credentialCopiesAbsent,
}) {
  if (credentialCopiesAbsent !== true) invalid('assembler-credential-copies');
  if (
    sha256(canonicalJson(captureManifest)) !== captureManifestSha256 ||
    sha256(canonicalJson(oracleResult)) !== oracleResultSha256
  ) {
    invalid('assembler-input-digest');
  }
  if (!(retainedDocuments instanceof Map)) invalid('assembler-retained-documents');
  const documents = validateSourceBundle(sourceMap, sourceBundle);
  const authorizations = deriveAuthorizations(captureManifest, capturedSourceBodies);
  const approvalTransitions = validateApprovalDerivation(
    null,
    captureManifest,
    capturedSourceBodies,
    authorizations.execution.operation_runs,
  );
  const concurrency = validateConcurrencyDerivation(null, captureManifest, capturedSourceBodies);
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
      'oracle_assembly_sha256',
      'production_assembly_sha256',
      'result',
    ],
    'oracle-build-receipt-shape',
  );
  if (
    oracleBuildReceipt.kind !== 'apr-r4-e3-production-codec-oracle-build-receipt-v1' ||
    oracleBuildReceipt.source_commit !== oracleResult.oracle_source_sha ||
    oracleBuildReceipt.source_tree !== oracleResult.oracle_source_tree ||
    oracleBuildReceipt.oracle_assembly_sha256 !== oracleResult.oracle_assembly_sha256 ||
    oracleBuildReceipt.production_assembly_sha256 !== oracleResult.production_assembly_sha256 ||
    oracleBuildReceipt.result !== 'passed'
  ) {
    invalid('oracle-build-receipt-values');
  }
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
    uiAttestation,
    uiAttestationSha256,
    authorizations,
  );
  const restrictedPackage = retainedDocuments.get('restricted-package-readback');
  const cleanup = retainedDocuments.get('cleanup-readbacks');
  const publicLeakScan = retainedDocuments.get('public-leak-scan-result');
  const cleanupPlan = retainedDocuments.get('cleanup-plan');
  if (
    restrictedPackage === undefined ||
    cleanup === undefined ||
    publicLeakScan === undefined ||
    cleanupPlan === undefined
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
  const capturedArtifacts = validateCaptureManifest(captureManifest, host, captureManifestSha256);
  validateOracleResult(
    oracleResult,
    host,
    captureManifestSha256,
    oracleResultSha256,
    capturedArtifacts,
  );
  validateSourceEvidenceBindings(
    documents,
    captureManifest,
    captureManifestSha256,
    oracleResultSha256,
    uiAttestationSha256,
    retainedDocuments,
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
  };
}

export function buildFinalizedPrivatePackageManifest({
  host,
  sourceBundle,
  captureManifestSha256,
  oracleResultSha256,
  cleanupPlan,
}) {
  const validation = validateHostEvidence(host);
  if (
    captureManifestSha256 !== host.restricted_package.capture_manifest_sha256 ||
    oracleResultSha256 !== host.restricted_package.oracle_result_sha256 ||
    canonicalJson(cleanupPlan) !== canonicalJson(validation.cleanupPlan)
  ) {
    invalid('private-package-input-binding');
  }
  return {
    kind: 'apr-r4-e3-private-package-manifest-v1',
    destination_identity_sha256: host.restricted_package.destination_identity_sha256,
    host_evidence_sha256: sha256(canonicalJson(host)),
    source_bundle_sha256: sha256(canonicalJson(sourceBundle)),
    capture_manifest_sha256: captureManifestSha256,
    oracle_result_sha256: oracleResultSha256,
    cleanup_plan_sha256: sha256(canonicalJson(cleanupPlan)),
    credential_absence: {
      github_token: host.restricted_package.token_copy_absent,
      current_state_key: host.restricted_package.current_key_copy_absent,
      previous_state_key: host.restricted_package.previous_key_copy_absent,
    },
    projection_eligible: validation.projectionEligible,
    finalized: true,
  };
}

export function assertFinalizedPrivatePackage({
  host,
  sourceBundle,
  captureManifestSha256,
  oracleResultSha256,
  cleanupPlan,
  privatePackageManifest,
}) {
  const expected = buildFinalizedPrivatePackageManifest({
    host,
    sourceBundle,
    captureManifestSha256,
    oracleResultSha256,
    cleanupPlan,
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
