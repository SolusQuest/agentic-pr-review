export type ArtifactVectorDisposition = 'port' | 'obsolete';

export interface ArtifactProvenanceVector {
  readonly id: `APV-${string}`;
  readonly inputMetadata: readonly string[];
  readonly selectionContext: string;
  readonly expectedOutcome: string;
  readonly securityInvariant: string;
  readonly disposition: ArtifactVectorDisposition;
  readonly futureConsumer?: 'R4 artifact bridge';
  readonly deletionGate?: string;
  readonly deletionRationale?: string;
}

const r4Gate = 'delete after equivalent R4 artifact-bridge conformance coverage exists';

function port(
  id: ArtifactProvenanceVector['id'],
  inputMetadata: readonly string[],
  selectionContext: string,
  expectedOutcome: string,
  securityInvariant: string,
): ArtifactProvenanceVector {
  return {
    id,
    inputMetadata,
    selectionContext,
    expectedOutcome,
    securityInvariant,
    disposition: 'port',
    futureConsumer: 'R4 artifact bridge',
    deletionGate: r4Gate,
  };
}

function obsolete(
  id: ArtifactProvenanceVector['id'],
  inputMetadata: readonly string[],
  selectionContext: string,
  expectedOutcome: string,
  securityInvariant: string,
  deletionRationale: string,
): ArtifactProvenanceVector {
  return {
    id,
    inputMetadata,
    selectionContext,
    expectedOutcome,
    securityInvariant,
    disposition: 'obsolete',
    deletionRationale,
  };
}

/**
 * Provider-independent security inputs preserved from the retired R1 artifact adapter.
 * These vectors intentionally do not select an R4 transport or ordering algorithm.
 */
export const artifactProvenanceVectors = [
  port(
    'APV-001',
    ['artifact.name'],
    'repository or explicit-run lookup',
    'ignore a candidate whose name is not the requested state artifact',
    'cross-purpose artifacts cannot substitute for state',
  ),
  port(
    'APV-002',
    ['artifact.expired'],
    'candidate eligibility',
    'ignore an expired candidate',
    'retention-invalid artifacts cannot be restored',
  ),
  port(
    'APV-003',
    ['artifact.id'],
    'candidate eligibility',
    'ignore a missing or zero artifact id',
    'every download has an unambiguous positive artifact identity',
  ),
  port(
    'APV-004',
    ['artifact.workflow_run.id', 'explicitRunId'],
    'candidate eligibility',
    'ignore a missing or zero producing run id',
    'state cannot be restored without producer provenance',
  ),
  port(
    'APV-005',
    ['current workflow-run response'],
    'trust-anchor construction',
    'fail lookup when current-run metadata cannot be read',
    'selection cannot proceed without a current trust anchor',
  ),
  port(
    'APV-006',
    ['candidate workflow-run response'],
    'candidate trust evaluation',
    'skip the unreadable candidate and continue',
    'unreadable producer metadata cannot become trusted',
  ),
  port(
    'APV-007',
    ['currentRun.id'],
    'trust-anchor validation',
    'reject a non-positive or non-safe-integer current run id',
    'the trust anchor has a canonical producer identity',
  ),
  port(
    'APV-008',
    ['candidateRun.id'],
    'candidate trust evaluation',
    'reject or skip a malformed candidate run id',
    'candidate producer identities cannot alias',
  ),
  port(
    'APV-009',
    ['workflow_id'],
    'strict provenance',
    'reject missing, invalid, or mismatched workflow identity',
    'state cannot cross workflow identities',
  ),
  port(
    'APV-010',
    ['workflow path'],
    'strict provenance',
    'reject missing or mismatched workflow path',
    'state cannot cross workflow definitions',
  ),
  port(
    'APV-011',
    ['event'],
    'strict provenance',
    'reject missing or mismatched event',
    'state cannot cross event trust domains',
  ),
  port(
    'APV-012',
    ['head_sha'],
    'strict provenance',
    'reject missing producer head sha',
    'restored state remains bound to source provenance',
  ),
  port(
    'APV-013',
    ['head_repository.full_name'],
    'strict provenance',
    'reject missing or mismatched head repository',
    'fork and upstream origins cannot be confused',
  ),
  port(
    'APV-014',
    ['candidate.conclusion', 'current.conclusion'],
    'candidate trust evaluation',
    'require only the candidate producer conclusion to equal success',
    'failed or cancelled producers are rejected without requiring an in-flight current run to finish',
  ),
  port(
    'APV-015',
    ['candidate.workflow_id', 'current.workflow_id'],
    'candidate trust evaluation',
    'reject workflow-id mismatch',
    'state cannot cross workflow identities',
  ),
  port(
    'APV-016',
    ['candidate.path', 'current.path'],
    'candidate trust evaluation',
    'reject workflow-path mismatch',
    'state cannot cross workflow definitions',
  ),
  port(
    'APV-017',
    ['candidate.event', 'current.event'],
    'candidate trust evaluation',
    'reject event mismatch',
    'state cannot cross execution domains',
  ),
  port(
    'APV-018',
    ['candidate.head_repository', 'current.head_repository'],
    'candidate trust evaluation',
    'reject head-repository mismatch',
    'fork-origin substitution is rejected',
  ),
  port(
    'APV-019',
    ['candidate.event', 'targetMode'],
    'pull-request target lookup',
    'require the candidate event to be pull_request',
    'non-PR runs cannot feed PR state',
  ),
  port(
    'APV-020',
    ['candidate.pull_requests', 'target prNumber'],
    'pull-request target lookup',
    'require association with the target pull request',
    'state cannot cross pull requests',
  ),
  port(
    'APV-021',
    ['explicitRunId', 'candidate provenance'],
    'explicit-run lookup',
    'apply every trust check despite explicit selection',
    'an explicit id cannot bypass provenance',
  ),
  port(
    'APV-022',
    ['created_at', 'candidate provenance'],
    'multiple candidates',
    'filter trust before ordering newer candidates',
    'a newer untrusted artifact cannot shadow older trusted state',
  ),
  port(
    'APV-023',
    ['distinct valid created_at values'],
    'multiple trusted candidates',
    'select descending timestamp under the retired adapter',
    'R4 must explicitly define timestamp semantics',
  ),
  port(
    'APV-024',
    ['equal created_at values', 'upstream API order'],
    'multiple trusted candidates',
    'record that the retired adapter preserved upstream order without a tie-break',
    'R4 must define and test a deterministic total order',
  ),
  port(
    'APV-025',
    ['missing or malformed created_at'],
    'multiple trusted candidates',
    'record that the retired adapter coerced the value to a string',
    'R4 must validate or explicitly order malformed timestamps',
  ),
  port(
    'APV-026',
    ['more than 100 same-name artifacts', 'per_page=100'],
    'repository or explicit-run listing',
    'record that the retired adapter observed only the first page',
    'R4 must define complete enumeration',
  ),
  port(
    'APV-027',
    ['trusted candidate outside first page'],
    'truncated candidate listing',
    'treat current observed absence as incomplete evidence',
    'R4 cannot equate unobserved trusted state with absence',
  ),
  port(
    'APV-028',
    ['one or more observed pages', 'later-page API failure'],
    'future paginated listing',
    'require a distinct incomplete-enumeration failure',
    'partial enumeration cannot masquerade as complete',
  ),
  port(
    'APV-029',
    ['fully observed candidate set'],
    'candidate selection',
    'return no state when no trusted candidate exists',
    'the best untrusted candidate is never selected',
  ),
  port(
    'APV-030',
    ['artifact-list API failure'],
    'candidate discovery',
    'propagate lookup failure',
    'an unobserved list cannot be treated as trusted absence',
  ),
  obsolete(
    'APV-031',
    ['legacy backend', 'partial workflow metadata', 'explicitRunId'],
    'legacy explicit restore',
    'retire permissive partial-provenance selection',
    'no compatibility bypass survives',
    'The R1 legacy Action, reader, and converter are deleted with no continuation promise.',
  ),
  obsolete(
    'APV-032',
    ['artifact upload/download transport'],
    'Action state transport',
    'remove the transport implementation',
    'transport choice is separate from the preserved provenance invariants',
    'R4 selects its own artifact bridge; the @actions/artifact transport has no R1 consumer.',
  ),
] as const satisfies readonly ArtifactProvenanceVector[];
