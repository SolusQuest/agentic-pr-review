import {
  LIMITS,
  canonicalJson,
  inspectPackage,
  sha256,
  validateReleaseVersion,
} from './build-payload.format.mjs';

export const REPOSITORY = 'SolusQuest/agentic-pr-review';
export const REPOSITORY_ID = '1263978368';
export const WORKFLOW = '.github/workflows/release.yml';
// SHA-256 of the complete canonical parsed P1 workflow, not its filename or a
// permissive artifact-action allowlist. Any topology/permission/command/input
// change requires review of this admission identity together with the workflow.
export function verifyCandidateWorkflow(workflow) {
  requireThat(
    sha256(canonicalJson(workflow)) ===
      '01945f4a35caa3424b91966f40249fa5fdcdaeaca5264b2428492d1df38ca957',
    'candidate_workflow_topology_drift',
  );
  return true;
}
export const CAPS = Object.freeze({
  metadata: 65536,
  map: 16384,
  summary: 4096,
  api: 1048576,
  pages: 5,
  page: 100,
});
export function requireThat(condition, code) {
  if (!condition) throw new Error(code);
}
function keys(value, names, code) {
  requireThat(
    value &&
      typeof value === 'object' &&
      !Array.isArray(value) &&
      Object.keys(value).sort().join('|') === [...names].sort().join('|'),
    code,
  );
}
export function hex(value, length) {
  return typeof value === 'string' && value.length === length && /^[a-f0-9]+$/.test(value);
}
export function decimal(value) {
  if (typeof value === 'number') {
    requireThat(Number.isSafeInteger(value) && value > 0, 'unsafe_remote_id');
    value = String(value);
  }
  requireThat(
    typeof value === 'string' &&
      /^[1-9][0-9]{0,19}$/.test(value) &&
      BigInt(value) <= 18446744073709551615n,
    'invalid_id',
  );
  return value;
}
export function timestamp(value) {
  requireThat(
    typeof value === 'string' && /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{3})?Z$/.test(value),
    'invalid_timestamp',
  );
  const time = Date.parse(value);
  requireThat(
    Number.isFinite(time) &&
      new Date(time).toISOString().replace('.000Z', 'Z') === value.replace('.000Z', 'Z'),
    'invalid_timestamp',
  );
  return time;
}
export function artifactName(role, producer) {
  requireThat(['payload', 'metadata'].includes(role), 'invalid_artifact_role');
  return `r7-p1-${role}-${decimal(producer.runId)}-${decimal(producer.runAttempt)}`;
}
export function validateProducer(producer) {
  keys(
    producer,
    [
      'repository',
      'repositoryId',
      'workflowPath',
      'workflowCommit',
      'runId',
      'runAttempt',
      'runner',
    ],
    'invalid_producer',
  );
  requireThat(
    producer.repository === REPOSITORY &&
      producer.repositoryId === REPOSITORY_ID &&
      producer.workflowPath === WORKFLOW &&
      hex(producer.workflowCommit, 40) &&
      producer.runner === 'github-hosted-ubuntu-24.04-x64',
    'invalid_producer',
  );
  for (const field of ['runId', 'runAttempt'])
    requireThat(
      typeof producer[field] === 'string' && decimal(producer[field]) === producer[field],
      'invalid_producer',
    );
}
export function validateStorage(storage, producer, role = 'payload') {
  keys(
    storage,
    ['artifactId', 'name', 'size', 'sha256', 'createdAt', 'expiresAt'],
    'invalid_storage',
  );
  requireThat(
    typeof storage.artifactId === 'string' &&
      decimal(storage.artifactId) === storage.artifactId &&
      storage.name === artifactName(role, producer) &&
      hex(storage.sha256, 64) &&
      Number.isSafeInteger(storage.size) &&
      storage.size > 0 &&
      storage.size <=
        (role === 'payload'
          ? LIMITS.archive + CAPS.metadata + 65536
          : CAPS.metadata + CAPS.map + CAPS.summary + 65536),
    'invalid_storage',
  );
  requireThat(timestamp(storage.expiresAt) > timestamp(storage.createdAt), 'invalid_storage');
}
export function proposedMap(receipt, producer) {
  validateProducer(producer);
  return {
    formatVersion: 1,
    repository: REPOSITORY,
    tag: 'payload-' + receipt.identity.releaseVersion,
    builder: {
      repository: producer.repository,
      workflowPath: producer.workflowPath,
      workflowCommit: producer.workflowCommit,
    },
    payload: receipt,
  };
}
export function createCandidate(archive, receipt, producer, storage, expected) {
  validateProducer(producer);
  validateStorage(storage, producer);
  validateReleaseVersion(expected.releaseVersion);
  requireThat(
    hex(expected.sourceCommit, 40) &&
      hex(expected.sourceTree, 40) &&
      hex(expected.archiveSha256, 64),
    'invalid_expectation',
  );
  requireThat(
    receipt.identity.sourceCommit === expected.sourceCommit &&
      receipt.identity.sourceTree === expected.sourceTree &&
      receipt.identity.releaseVersion === expected.releaseVersion &&
      receipt.archiveSha256 === expected.archiveSha256,
    'candidate_input_drift',
  );
  const { manifest } = inspectPackage(archive, receipt);
  const map = proposedMap(receipt, producer);
  const mapBytes = Buffer.from(canonicalJson(map));
  const candidate = {
    formatVersion: 1,
    receipt,
    buildInputs: manifest.buildInputs,
    producer,
    storage,
    proposedMapSha256: sha256(mapBytes),
  };
  const bytes = Buffer.from(canonicalJson(candidate));
  requireThat(bytes.length <= CAPS.metadata && mapBytes.length <= CAPS.map, 'candidate_too_large');
  return {
    candidate,
    bytes,
    mapBytes,
    candidateSha256: sha256(bytes),
    mapSha256: sha256(mapBytes),
  };
}
export function parseCanonical(bytes, cap) {
  requireThat(
    Buffer.isBuffer(bytes) && bytes.length > 0 && bytes.length <= cap,
    'metadata_too_large',
  );
  let value;
  try {
    value = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
  } catch {
    throw new Error('invalid_metadata_json');
  }
  requireThat(canonicalJson(value) === bytes.toString('utf8'), 'noncanonical_metadata');
  return value;
}
export function inspectCandidate(archive, bytes, mapBytes, expectedSha256) {
  requireThat(
    Buffer.isBuffer(bytes) &&
      bytes.length > 0 &&
      bytes.length <= CAPS.metadata &&
      Buffer.isBuffer(mapBytes) &&
      mapBytes.length > 0 &&
      mapBytes.length <= CAPS.map,
    'metadata_too_large',
  );
  requireThat(
    hex(expectedSha256, 64) && sha256(bytes) === expectedSha256,
    'candidate_digest_mismatch',
  );
  const candidate = parseCanonical(bytes, CAPS.metadata);
  keys(
    candidate,
    ['formatVersion', 'receipt', 'buildInputs', 'producer', 'storage', 'proposedMapSha256'],
    'invalid_candidate',
  );
  requireThat(candidate.formatVersion === 1, 'unsupported_candidate');
  parseCanonical(mapBytes, CAPS.map);
  const identity = candidate.receipt?.identity;
  requireThat(identity, 'invalid_candidate');
  const recreated = createCandidate(
    archive,
    candidate.receipt,
    candidate.producer,
    candidate.storage,
    { ...identity, archiveSha256: candidate.receipt.archiveSha256 },
  );
  requireThat(
    recreated.bytes.equals(bytes) && recreated.mapBytes.equals(mapBytes),
    'candidate_map_or_inputs_drift',
  );
  return candidate;
}
export function humanSummary(record) {
  const c = parseCanonical(record.bytes, CAPS.metadata);
  requireThat(
    sha256(record.bytes) === record.candidateSha256 &&
      sha256(record.mapBytes) === record.mapSha256 &&
      c.proposedMapSha256 === record.mapSha256,
    'candidate_digest_mismatch',
  );
  validateProducer(c.producer);
  validateStorage(c.storage, c.producer);
  const text = `Prepared candidate (unqualified, unsigned)\nVersion: ${c.receipt.identity.releaseVersion}\nPayload source S: ${c.receipt.identity.sourceCommit}\nBuilder W: ${c.producer.workflowCommit}\nProducer: ${REPOSITORY} / ${WORKFLOW} / run ${c.producer.runId} attempt ${c.producer.runAttempt}\nPayload artifact: ${c.storage.artifactId} / sha256:${c.storage.sha256}\nOriginal archive: ${c.receipt.archiveName} / ${c.receipt.archiveSize} bytes / sha256:${c.receipt.archiveSha256}\nCandidate SHA-256: ${record.candidateSha256}\nProposed map SHA-256: ${record.mapSha256}\nPayload expiry: ${c.storage.expiresAt}\nFinal Action T, attestation, qualification and publication remain separate gates.\n`;
  requireThat(Buffer.byteLength(text) <= CAPS.summary, 'summary_too_large');
  return text;
}

// The only production endpoint is GitHub. Tests inject a fetch implementation,
// never a credential-bearing CLI endpoint or a remotely supplied pagination URL.
export function githubReader(token, fetchImpl = fetch) {
  return async (path) => {
    requireThat(
      /^\/repos\/SolusQuest\/agentic-pr-review\/actions\/(?:artifacts\/[1-9][0-9]{0,19}|runs\/[1-9][0-9]{0,19}\/(?:attempts\/[1-9][0-9]{0,19}|artifacts\?per_page=100&page=[1-5]))$/.test(
        path,
      ),
      'invalid_api_path',
    );
    let response;
    try {
      response = await fetchImpl('https://api.github.com' + path, {
        method: 'GET',
        redirect: 'error',
        signal: AbortSignal.timeout(15000),
        headers: {
          'User-Agent': 'agentic-pr-review-candidate/1',
          Accept: 'application/vnd.github+json',
          'X-GitHub-Api-Version': '2022-11-28',
          ...(token ? { Authorization: 'Bearer ' + token } : {}),
        },
      });
    } catch {
      throw new Error('candidate_storage_unknown_stop');
    }
    requireThat(
      response.status !== 404 && response.status !== 410,
      'candidate_storage_missing_reprepare',
    );
    requireThat(response.status === 200 && response.body, 'candidate_storage_unknown_stop');
    const declared = response.headers.get('content-length');
    if (declared !== null && (!/^[0-9]+$/.test(declared) || Number(declared) > CAPS.api)) {
      await response.body.cancel().catch(() => {});
      throw new Error('api_response_too_large');
    }
    const reader = response.body.getReader();
    let size = 0;
    const chunks = [];
    try {
      for (;;) {
        const part = await reader.read();
        if (part.done) break;
        size += part.value.length;
        requireThat(size <= CAPS.api, 'api_response_too_large');
        chunks.push(Buffer.from(part.value));
      }
    } catch (error) {
      throw new Error(
        error instanceof Error && error.message === 'api_response_too_large'
          ? 'api_response_too_large'
          : 'candidate_storage_unknown_stop',
      );
    } finally {
      await reader.cancel().catch(() => {});
    }
    try {
      return JSON.parse(
        new TextDecoder('utf-8', { fatal: true }).decode(Buffer.concat(chunks, size)),
      );
    } catch {
      throw new Error('invalid_api_response');
    }
  };
}
function remoteStorage(artifact, producer, role, now) {
  requireThat(
    artifact && artifact.expired === false && timestamp(artifact.expires_at) > now,
    'candidate_storage_expired_reprepare',
  );
  const run = artifact.workflow_run;
  requireThat(
    run &&
      decimal(run.id) === producer.runId &&
      decimal(run.repository_id) === REPOSITORY_ID &&
      decimal(run.head_repository_id) === REPOSITORY_ID &&
      run.head_branch === 'main' &&
      run.head_sha === producer.workflowCommit &&
      typeof artifact.digest === 'string' &&
      /^sha256:[a-f0-9]{64}$/.test(artifact.digest),
    'candidate_storage_identity_drift',
  );
  const storage = {
    artifactId: decimal(artifact.id),
    name: artifact.name,
    size: artifact.size_in_bytes,
    sha256: artifact.digest.slice(7),
    createdAt: artifact.created_at,
    expiresAt: artifact.expires_at,
  };
  validateStorage(storage, producer, role);
  requireThat(timestamp(storage.createdAt) <= now, 'candidate_storage_identity_drift');
  return storage;
}
export async function verifyStorage(
  producer,
  expectedStorage,
  get,
  { now = Date.now(), currentProducer = null, role = 'payload' } = {},
) {
  validateProducer(producer);
  const root = `/repos/${REPOSITORY}/actions`;
  const run = await get(`${root}/runs/${producer.runId}/attempts/${producer.runAttempt}`);
  requireThat(
    run &&
      decimal(run.id) === producer.runId &&
      decimal(run.run_attempt) === producer.runAttempt &&
      run.repository?.full_name === REPOSITORY &&
      decimal(run.repository.id) === REPOSITORY_ID &&
      run.head_repository?.full_name === REPOSITORY &&
      decimal(run.head_repository.id) === REPOSITORY_ID &&
      run.path === WORKFLOW &&
      run.head_sha === producer.workflowCommit &&
      run.head_branch === 'main' &&
      run.event === 'workflow_dispatch',
    'candidate_run_identity_drift',
  );
  const preparing =
    currentProducer !== null && canonicalJson(currentProducer) === canonicalJson(producer);
  requireThat(
    (run.status === 'completed' && run.conclusion === 'success') ||
      (preparing && run.status === 'in_progress' && run.conclusion === null),
    'candidate_run_incomplete_stop',
  );
  const started = timestamp(run.run_started_at);
  const ended = run.status === 'completed' ? timestamp(run.updated_at) : now;
  requireThat(started <= ended && ended <= now, 'candidate_run_identity_drift');
  const artifact = await get(`${root}/artifacts/${decimal(expectedStorage.artifactId)}`);
  const observed = remoteStorage(artifact, producer, role, now);
  requireThat(
    observed.artifactId === expectedStorage.artifactId &&
      observed.sha256 === expectedStorage.sha256 &&
      (!expectedStorage.name || canonicalJson(observed) === canonicalJson(expectedStorage)),
    'candidate_storage_identity_drift',
  );
  requireThat(
    timestamp(observed.createdAt) >= started && timestamp(observed.createdAt) <= ended,
    'candidate_storage_attempt_drift',
  );
  const all = new Map();
  let total;
  for (let page = 1; page <= CAPS.pages; page++) {
    const result = await get(
      `${root}/runs/${producer.runId}/artifacts?per_page=${CAPS.page}&page=${page}`,
    );
    requireThat(
      result &&
        Number.isSafeInteger(result.total_count) &&
        result.total_count >= 0 &&
        result.total_count <= CAPS.page * CAPS.pages &&
        Array.isArray(result.artifacts) &&
        result.artifacts.length <= CAPS.page &&
        (total === undefined || total === result.total_count),
      'candidate_storage_listing_incomplete_stop',
    );
    total = result.total_count;
    for (const item of result.artifacts) {
      const id = decimal(item.id);
      requireThat(!all.has(id), 'candidate_storage_listing_ambiguous_stop');
      all.set(id, item);
    }
    requireThat(all.size <= total, 'candidate_storage_listing_incomplete_stop');
    if (all.size === total) break;
    requireThat(
      result.artifacts.length === CAPS.page && page < CAPS.pages,
      'candidate_storage_listing_incomplete_stop',
    );
  }
  const matches = [...all.values()].filter((item) => item.name === artifactName(role, producer));
  requireThat(
    matches.length === 1 &&
      decimal(matches[0].id) === observed.artifactId &&
      canonicalJson(remoteStorage(matches[0], producer, role, now)) === canonicalJson(observed),
    'candidate_storage_listing_ambiguous_stop',
  );
  return observed;
}
