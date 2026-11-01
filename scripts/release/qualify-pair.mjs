// Runtime CI's separately authorized exact-pair qualifier. Read-only remotely,
// except for the workflow's official bounded evidence artifact upload.
import { execFileSync } from 'node:child_process';
import { appendFile, mkdir, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { canonicalJson, readBoundedFile } from './build-payload.format.mjs';
import { admitOutput } from './build-payload.mjs';
import {
  decimal,
  githubReader,
  hex,
  QUALIFICATION_WORKFLOW,
  REPOSITORY,
  REPOSITORY_ID,
  requireThat,
  validateQualificationProducer,
  verifyStorage,
} from './candidate.mjs';
import {
  CHECK_MANIFEST_PATH,
  PAIR_LIMITS,
  parseFinalizedPair,
  parseFinalizationRequest,
  parseExpectedChecks,
} from './finalized-pair.format.mjs';
import { verifyFinalizedPair } from './finalize-candidate.mjs';
import { qualificationEvidence, writeQualificationEvidence } from './qualification-evidence.mjs';
import {
  executeOriginalPair,
  gitBlob,
  originalPairObservations,
  runnerEvidence,
  supportsRuntime,
} from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/qualify-original-pair.mjs';
import { qualificationManifest } from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/qualification-cases.mjs';

const controlRepo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
function git(repo, args) {
  return execFileSync('git', ['--no-replace-objects', ...args], {
    cwd: repo,
    env: { PATH: process.env.PATH },
    encoding: 'utf8',
    timeout: 30_000,
    maxBuffer: 65536,
    stdio: ['ignore', 'pipe', 'pipe'],
  }).trim();
}
export function admitQualifier(repo, expectedQ, env) {
  requireThat(
    hex(expectedQ, 40) &&
      env.GITHUB_EVENT_NAME === 'workflow_dispatch' &&
      env.GITHUB_REPOSITORY === REPOSITORY &&
      env.GITHUB_REPOSITORY_ID === REPOSITORY_ID &&
      env.GITHUB_REF === 'refs/heads/main' &&
      env.GITHUB_WORKFLOW_REF === `${REPOSITORY}/${QUALIFICATION_WORKFLOW}@refs/heads/main` &&
      env.GITHUB_WORKFLOW_SHA === expectedQ &&
      env.GITHUB_SHA === expectedQ &&
      env.RUNNER_ENVIRONMENT === 'github-hosted' &&
      env.RUNNER_OS === 'Linux' &&
      env.RUNNER_ARCH === 'X64',
    'qualification_workflow_denied',
  );
  requireThat(
    git(repo, ['rev-parse', 'HEAD']) === expectedQ && git(repo, ['status', '--porcelain']) === '',
    'qualification_checkout_drift',
  );
  const producer = {
    repository: REPOSITORY,
    repositoryId: REPOSITORY_ID,
    workflowPath: QUALIFICATION_WORKFLOW,
    workflowCommit: expectedQ,
    runId: decimal(env.GITHUB_RUN_ID),
    runAttempt: decimal(env.GITHUB_RUN_ATTEMPT),
    runner: 'github-hosted-ubuntu-24.04-x64',
  };
  validateQualificationProducer(producer);
  return producer;
}
export function matchRequest(pair, request) {
  const expected = {
    candidateSha256: pair.payload.candidateSha256,
    workflowCommit: pair.producer.workflowCommit,
    actionCommit: pair.action.commit,
    bindingPr: pair.action.bindingPr,
    ...Object.fromEntries(
      ['payload', 'metadata', 'provenance'].flatMap((role) => [
        [role + 'Id', pair.storage[role].artifactId],
        [role + 'Sha256', pair.storage[role].sha256],
      ]),
    ),
  };
  requireThat(canonicalJson(request) === canonicalJson(expected), 'qualification_request_drift');
  // The official download adapter represents these IDs as JS numbers.
  for (const value of [
    pair.producer.runId,
    ...Object.values(pair.storage).map((s) => s.artifactId),
  ])
    requireThat(
      Number.isSafeInteger(Number(value)) && String(Number(value)) === value,
      'qualification_download_identity_invalid',
    );
}
async function context(repo, pairBytes, pairSha256, requestBytes, env) {
  const producer = admitQualifier(repo, env.QUALIFIER_COMMIT, env);
  const pair = parseFinalizedPair(pairBytes, pairSha256);
  const request = parseFinalizationRequest(requestBytes);
  matchRequest(pair, request);
  requireThat(
    ![
      pair.payload.identity.sourceCommit,
      pair.producer.workflowCommit,
      pair.action.commit,
    ].includes(producer.workflowCommit),
    'qualification_revision_cycle',
  );
  const get = githubReader(env.GITHUB_TOKEN);
  const main = await get(`/repos/${REPOSITORY}/git/ref/heads/main`);
  requireThat(
    main?.ref === 'refs/heads/main' && main.object?.type === 'commit' && hex(main.object.sha, 40),
    'qualification_main_unknown',
  );
  git(repo, ['merge-base', '--is-ancestor', producer.workflowCommit, main.object.sha]);
  const expectedChecksBytes = gitBlob(
    repo,
    producer.workflowCommit,
    CHECK_MANIFEST_PATH,
    PAIR_LIMITS.expectedChecks,
  );
  parseExpectedChecks(expectedChecksBytes);
  requireThat(
    expectedChecksBytes.equals(
      await readBoundedFile(join(repo, CHECK_MANIFEST_PATH), PAIR_LIMITS.expectedChecks),
    ) && expectedChecksBytes.toString() === canonicalJson(qualificationManifest),
    'qualification_manifest_drift',
  );
  return { producer, pair, request, get, expectedChecksBytes };
}
export async function admitQualificationRequest(repo, output, env) {
  const pairBytes = Buffer.from(env.QUALIFICATION_PAIR ?? '');
  const requestBytes = Buffer.from(env.QUALIFICATION_REQUEST ?? '');
  const c = await context(repo, pairBytes, env.QUALIFICATION_PAIR_SHA256, requestBytes, env);
  for (const role of ['payload', 'metadata', 'provenance'])
    await verifyStorage(c.pair.producer, c.pair.storage[role], c.get, { role });
  output = admitOutput(repo, output);
  await mkdir(output, { mode: 0o700 });
  await writeFile(join(output, 'pair.json'), pairBytes, { flag: 'wx', mode: 0o600 });
  await writeFile(join(output, 'request.json'), requestBytes, { flag: 'wx', mode: 0o600 });
  if (env.GITHUB_OUTPUT)
    await appendFile(
      env.GITHUB_OUTPUT,
      `payload_id=${c.request.payloadId}\nmetadata_id=${c.request.metadataId}\nprovenance_id=${c.request.provenanceId}\nproducer_run_id=${c.pair.producer.runId}\n`,
    );
  return { admitted: true, qualified: false };
}
export async function qualifyPair(repo, inputDirectory, artifacts, output, env) {
  const pairBytes = await readBoundedFile(join(inputDirectory, 'pair.json'), PAIR_LIMITS.pair);
  const requestBytes = await readBoundedFile(
    join(inputDirectory, 'request.json'),
    PAIR_LIMITS.request,
  );
  const pairSha256 = env.QUALIFICATION_PAIR_SHA256;
  const c = await context(repo, pairBytes, pairSha256, requestBytes, env);
  const input = {
    repo,
    pairPath: join(inputDirectory, 'pair.json'),
    pairSha256,
    sourceCommit: c.pair.payload.identity.sourceCommit,
    releaseVersion: c.pair.payload.identity.releaseVersion,
    requestBytes,
    archivePath: join(artifacts, 'payload', c.pair.payload.archiveName),
    receiptPath: join(artifacts, 'payload/receipt.json'),
    candidatePath: join(artifacts, 'metadata/candidate.json'),
    mapPath: join(artifacts, 'metadata/proposed-action-map.json'),
    bundlePath: join(artifacts, 'provenance/bundle.json'),
  };
  const verified = await verifyFinalizedPair(input, env);
  const observations = new Map([
    [
      'pair-authentication',
      {
        status: 'pass',
        observation: { pairSha256, provenanceSha256: c.pair.provenance.bundleSha256 },
      },
    ],
  ]);
  const runner = await runnerEvidence(repo, c.pair);
  observations.set('composite-runner-reuse', {
    status: runner.reusable ? 'pass' : 'refused',
    observation: runner,
  });
  let refusalReason = runner.reusable ? null : 'check-refused';
  if (runner.reusable && !supportsRuntime(c.pair)) {
    observations.set('runtime-only', {
      status: 'refused',
      observation: { runtimePolicySha256: c.pair.action.runtimePolicySha256, supported: false },
    });
    refusalReason = 'runtime-refused';
  } else if (runner.reusable) {
    // Exceptions/unknown closure deliberately escape without an evidence
    // directory. An interrupted run cannot truthfully claim all cases not-run.
    const facts = await executeOriginalPair(input, verified);
    for (const [id, observed] of originalPairObservations(facts, c.pair, pairSha256))
      observations.set(id, observed);
  }
  const content = qualificationEvidence({
    pairBytes,
    pairSha256,
    expectedChecksBytes: c.expectedChecksBytes,
    producer: c.producer,
    executionKind: 'original-candidate',
    observations,
    refusalReason,
  });
  await writeQualificationEvidence(output, repo, content);
  if (env.GITHUB_OUTPUT)
    await appendFile(
      env.GITHUB_OUTPUT,
      `evidence_ready=true\nreceipt_sha256=${content.receiptSha256}\nqualification_result=${content.passed ? 'pass' : 'refusal'}\n`,
    );
  if (env.GITHUB_STEP_SUMMARY)
    await appendFile(
      env.GITHUB_STEP_SUMMARY,
      `Exact pair qualification: ${content.passed ? 'PASS' : 'REFUSAL'}\n\nPair SHA-256: ${pairSha256}\nReceipt SHA-256: ${content.receiptSha256}\nQ: ${c.producer.workflowCommit}\n\nOperational qualification still requires the completed successful workflow and authenticated original artifact ZIP.\n`,
    );
  return { passed: content.passed, qualified: false, receiptSha256: content.receiptSha256 };
}
function argumentsFor(args, names) {
  requireThat(args.length === names.length * 2, 'qualification_arguments_invalid');
  const values = {};
  for (let n = 0; n < args.length; n += 2) {
    requireThat(
      names.includes(args[n]) && !Object.hasOwn(values, args[n]) && args[n + 1],
      'qualification_arguments_invalid',
    );
    values[args[n]] = args[n + 1];
  }
  return values;
}
export async function main(argv = process.argv.slice(2), env = process.env) {
  const [command, ...args] = argv;
  if (command === 'admit') {
    const p = argumentsFor(args, ['--output']);
    return admitQualificationRequest(controlRepo, p['--output'], env);
  }
  requireThat(command === 'run', 'qualification_arguments_invalid');
  const p = argumentsFor(args, ['--inputs', '--artifacts', '--output']);
  return qualifyPair(controlRepo, p['--inputs'], p['--artifacts'], p['--output'], env);
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main()
    .then((result) => {
      process.stdout.write(canonicalJson(result) + '\n');
      if (result.passed === false) process.exitCode = 1;
    })
    .catch(() => {
      process.stderr.write('qualification_failed\n');
      process.exitCode = 1;
    });
}
