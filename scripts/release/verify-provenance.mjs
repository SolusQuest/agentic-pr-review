import { execFileSync } from 'node:child_process';
import { appendFile, mkdir, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  MANAGED_LIMITS,
  canonicalJson,
  readBoundedFile,
  sha256,
  validateReleaseVersion,
} from './build-payload.format.mjs';
import {
  CAPS,
  REPOSITORY,
  REPOSITORY_ID,
  WORKFLOW,
  githubReader,
  hex,
  inspectCandidate,
  requireThat,
  verifyStorage,
} from './candidate.mjs';
import { admitPreparation } from './prepare-candidate.mjs';

export const GH_VERSION = '2.103.3';
export const PREDICATE_TYPE =
  'https://github.com/SolusQuest/agentic-pr-review/attestations/candidate/v1';
export const SIGNER = `https://github.com/${REPOSITORY}/${WORKFLOW}@refs/heads/main`;
const ISSUER = 'https://token.actions.githubusercontent.com';
const EVIDENCE_CAP = 4 * 1024 * 1024;
const common = [
  '--archive',
  '--candidate',
  '--map',
  '--sha256',
  '--source',
  '--workflow',
  '--version',
  '--metadata-id',
  '--metadata-sha256',
];

function argumentsFor(args, names) {
  requireThat(args.length === names.length * 2, 'provenance_arguments_invalid');
  const p = {};
  for (let i = 0; i < args.length; i += 2) {
    requireThat(
      names.includes(args[i]) && !Object.hasOwn(p, args[i]) && args[i + 1],
      'provenance_arguments_invalid',
    );
    p[args[i]] = args[i + 1];
  }
  return p;
}

function predicate(candidate, candidateSha256) {
  return {
    candidateSha256,
    proposedMapSha256: candidate.proposedMapSha256,
    source: candidate.receipt.identity,
    producer: candidate.producer,
  };
}

// Only successful output from the pinned cryptographic verifier reaches here.
// This is deliberately private: production callers cannot submit "verified JSON".
function checkVerifiedResult(bytes, candidate, candidateSha256) {
  let result;
  try {
    result = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
  } catch {
    throw new Error('provenance_result_invalid');
  }
  requireThat(Array.isArray(result) && result.length === 1, 'provenance_result_ambiguous');
  const verified = result[0]?.verificationResult;
  const cert = verified?.signature?.certificate;
  const w = candidate.producer.workflowCommit;
  requireThat(
    cert &&
      cert.issuer === ISSUER &&
      cert.subjectAlternativeName === SIGNER &&
      cert.buildSignerURI === SIGNER &&
      cert.buildSignerDigest === w &&
      cert.sourceRepositoryURI === `https://github.com/${REPOSITORY}` &&
      cert.sourceRepositoryIdentifier === REPOSITORY_ID &&
      cert.sourceRepositoryRef === 'refs/heads/main' &&
      cert.sourceRepositoryDigest === w &&
      cert.buildConfigURI === SIGNER &&
      cert.buildConfigDigest === w &&
      cert.runnerEnvironment === 'github-hosted' &&
      cert.buildTrigger === 'workflow_dispatch' &&
      cert.runInvocationURI ===
        `https://github.com/${REPOSITORY}/actions/runs/${candidate.producer.runId}/attempts/${candidate.producer.runAttempt}` &&
      cert.sourceRepositoryVisibilityAtSigning === 'public',
    'provenance_signer_denied',
  );
  requireThat(
    Array.isArray(verified.verifiedTimestamps) && verified.verifiedTimestamps.length > 0,
    'provenance_timestamp_missing',
  );
  const statement = verified.statement;
  requireThat(
    statement?._type === 'https://in-toto.io/Statement/v1' &&
      statement.predicateType === PREDICATE_TYPE &&
      canonicalJson(statement.subject) ===
        canonicalJson([
          {
            name: candidate.receipt.archiveName,
            digest: { sha256: candidate.receipt.archiveSha256 },
          },
        ]) &&
      canonicalJson(statement.predicate) === canonicalJson(predicate(candidate, candidateSha256)),
    'provenance_association_denied',
  );
}

function gh(args) {
  try {
    return execFileSync('gh', args, {
      timeout: 120000,
      maxBuffer: EVIDENCE_CAP,
      stdio: ['ignore', 'pipe', 'pipe'],
      env: { ...process.env, GH_HOST: 'github.com' },
    });
  } catch {
    // Never publish tool stderr, environment, local paths or raw bundle content.
    throw new Error('provenance_tool_failed');
  }
}

async function inspect(p, env, preparing) {
  requireThat(hex(p['--source'], 40) && hex(p['--workflow'], 40), 'provenance_expectation_invalid');
  validateReleaseVersion(p['--version']);
  let admitted = null;
  if (preparing) {
    const control = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
    admitted = admitPreparation(control, p['--source'], p['--version'], env);
    requireThat(admitted.producer.workflowCommit === p['--workflow'], 'provenance_workflow_drift');
  }
  const archive = await readBoundedFile(p['--archive'], MANAGED_LIMITS.archive);
  const bytes = await readBoundedFile(p['--candidate'], CAPS.metadata);
  const map = await readBoundedFile(p['--map'], CAPS.map);
  const c = inspectCandidate(archive, bytes, map, p['--sha256']);
  requireThat(
    c.receipt.identity.sourceCommit === p['--source'] &&
      c.receipt.identity.releaseVersion === p['--version'] &&
      c.producer.workflowCommit === p['--workflow'],
    'provenance_candidate_drift',
  );
  if (admitted) {
    requireThat(
      canonicalJson(c.producer) === canonicalJson(admitted.producer) &&
        c.receipt.identity.sourceTree === admitted.sourceTree,
      'provenance_producer_drift',
    );
  }
  const get = githubReader(env.GITHUB_TOKEN);
  const options = { currentProducer: admitted?.producer ?? null };
  await verifyStorage(c.producer, c.storage, get, options);
  await verifyStorage(
    c.producer,
    { artifactId: p['--metadata-id'], sha256: p['--metadata-sha256'] },
    get,
    { ...options, role: 'metadata' },
  );
  return c;
}

async function verify(p, env, preparing) {
  const c = await inspect(p, env, preparing);
  const bundle = await readBoundedFile(p['--bundle'], EVIDENCE_CAP);
  requireThat(bundle.length > 0, 'provenance_bundle_missing');
  requireThat(
    gh(['--version']).toString('utf8').split('\n')[0].startsWith(`gh version ${GH_VERSION} (`),
    'provenance_tool_version_denied',
  );
  const result = gh([
    'attestation',
    'verify',
    resolve(p['--archive']),
    '--bundle',
    resolve(p['--bundle']),
    '--repo',
    REPOSITORY,
    '--cert-identity',
    SIGNER,
    '--cert-oidc-issuer',
    ISSUER,
    '--signer-digest',
    p['--workflow'],
    '--source-digest',
    p['--workflow'],
    '--source-ref',
    'refs/heads/main',
    '--deny-self-hosted-runners',
    '--predicate-type',
    PREDICATE_TYPE,
    '--hostname',
    'github.com',
    '--format',
    'json',
  ]);
  checkVerifiedResult(result, c, p['--sha256']);
  // The same original files must survive the tool invocation unchanged.
  requireThat(
    sha256(await readBoundedFile(p['--archive'], MANAGED_LIMITS.archive)) ===
      c.receipt.archiveSha256 &&
      sha256(await readBoundedFile(p['--candidate'], CAPS.metadata)) === p['--sha256'] &&
      sha256(await readBoundedFile(p['--map'], CAPS.map)) === c.proposedMapSha256 &&
      sha256(await readBoundedFile(p['--bundle'], EVIDENCE_CAP)) === sha256(bundle),
    'provenance_input_changed',
  );
  const report = {
    provenanceVerified: true,
    candidateSha256: p['--sha256'],
    proposedMapSha256: c.proposedMapSha256,
    archiveSha256: c.receipt.archiveSha256,
    bundleSha256: sha256(bundle),
    source: c.receipt.identity,
    producer: c.producer,
    signer: SIGNER,
    predicateType: PREDICATE_TYPE,
    verifier: `gh-${GH_VERSION}`,
    // Same-run preparation success does not establish completed-run handoff.
    completedRunVerified: !preparing,
  };
  const reportBytes = canonicalJson(report);
  requireThat(Buffer.byteLength(reportBytes) <= CAPS.summary, 'provenance_report_too_large');
  return report;
}

// Completed-run-only composition API. It always executes the cryptographic tool;
// no supplied result or in-progress option is accepted by this interface.
export async function verifyCandidateProvenance(input, env = process.env) {
  return verify(
    {
      '--archive': input.archivePath,
      '--candidate': input.candidatePath,
      '--map': input.mapPath,
      '--sha256': input.candidateSha256,
      '--source': input.sourceCommit,
      '--workflow': input.workflowCommit,
      '--version': input.releaseVersion,
      '--metadata-id': input.metadataId,
      '--metadata-sha256': input.metadataSha256,
      '--bundle': input.bundlePath,
    },
    env,
    false,
  );
}

export async function main(argv = process.argv.slice(2), env = process.env) {
  const [command, ...args] = argv;
  if (command === 'workflow-predicate') {
    const p = argumentsFor(args, [...common, '--receipt', '--output']);
    const c = await inspect(p, env, true);
    const receipt = await readBoundedFile(p['--receipt'], CAPS.metadata);
    requireThat(
      hex(env.ARCHIVE_SHA256, 64) &&
        c.receipt.archiveSha256 === env.ARCHIVE_SHA256 &&
        hex(env.RECEIPT_SHA256, 64) &&
        sha256(receipt) === env.RECEIPT_SHA256 &&
        canonicalJson(JSON.parse(receipt.toString('utf8'))) === canonicalJson(c.receipt) &&
        c.proposedMapSha256 === env.MAP_SHA256 &&
        c.storage.artifactId === env.PAYLOAD_ID &&
        c.storage.sha256 === env.PAYLOAD_SHA256,
      'provenance_build_output_drift',
    );
    await mkdir(p['--output'], { mode: 0o700 });
    await writeFile(
      resolve(p['--output'], 'predicate.json'),
      canonicalJson(predicate(c, p['--sha256'])),
      { flag: 'wx', mode: 0o600 },
    );
    return { predicatePrepared: true };
  }
  if (command === 'verify' || command === 'workflow-verify') {
    const p = argumentsFor(args, [...common, '--bundle', '--output']);
    const report = await verify(p, env, command === 'workflow-verify');
    await writeFile(p['--output'], canonicalJson(report), { flag: 'wx', mode: 0o600 });
    return report;
  }
  if (command === 'locator') {
    const p = argumentsFor(args, ['--artifact-id', '--artifact-sha256', '--attestation-id']);
    requireThat(
      /^[1-9][0-9]{0,19}$/.test(p['--artifact-id']) &&
        /^[1-9][0-9]{0,19}$/.test(p['--attestation-id']) &&
        hex(p['--artifact-sha256'], 64),
      'provenance_locator_invalid',
    );
    const text = `Provenance artifact: ${p['--artifact-id']} / sha256:${p['--artifact-sha256']}\nAttestation ID: ${p['--attestation-id']}\nRetain with the P1 candidate locator; rerun completed-run provenance verification before finalization/staging/promotion.\n`;
    if (env.GITHUB_STEP_SUMMARY) await appendFile(env.GITHUB_STEP_SUMMARY, text);
    return { locator: text };
  }
  throw new Error('provenance_arguments_invalid');
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main()
    .then((result) => process.stdout.write(canonicalJson(result)))
    .catch(() => {
      process.stderr.write('candidate_provenance_denied\n');
      process.exitCode = 1;
    });
}
