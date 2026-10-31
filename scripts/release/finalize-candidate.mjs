import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { appendFile, mkdir, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';
import {
  canonicalJson,
  sha256,
  MANAGED_LIMITS,
  MANAGED_TARGET,
  readBoundedFile,
  validateReleaseVersion,
} from './build-payload.format.mjs';
import { admitOutput, managedDependencyPolicy } from './build-payload.mjs';
import {
  CAPS,
  REPOSITORY,
  REPOSITORY_ID,
  WORKFLOW,
  decimal,
  hex,
  githubReader,
  inspectCandidate,
  requireThat,
  timestamp,
  validateProducer,
  verifyStorage,
} from './candidate.mjs';
import { admitWorkflow } from './prepare-candidate.mjs';
import { verifyCandidateProvenance } from './verify-provenance.mjs';
import {
  ACTION_ASSETS,
  managedArchiveName,
  ACTION_ROOT,
  CHECK_MANIFEST_PATH,
  PAIR_LIMITS,
  QUALIFICATION_SCOPE,
  inspectQualificationArchive,
  inspectQualificationContent,
  parseExpectedChecks,
  parseFinalizationRequest,
  parseFinalizedPair,
  validatePair,
  validateRuntimePolicy,
} from './finalized-pair.format.mjs';

const RUNTIME_SOURCE = 'src/action-wrapper/launcher/runtime-prerequisite.ts';
function git(repo, args, maximum = 1024 * 1024) {
  try {
    return execFileSync('git', ['--no-replace-objects', '--no-pager', '-C', repo, ...args], {
      env: { PATH: process.env.PATH },
      timeout: 30000,
      maxBuffer: maximum,
      stdio: ['ignore', 'pipe', 'pipe'],
    });
  } catch {
    throw new Error('finalization_git_admission_failed');
  }
}
function text(repo, args) {
  return git(repo, args).toString('utf8').trim();
}
function ancestor(repo, before, after) {
  requireThat(hex(before, 40) && hex(after, 40), 'finalization_revision_invalid');
  git(repo, ['merge-base', '--is-ancestor', before, after]);
}
function blob(repo, revision, name, cap) {
  requireThat(hex(revision, 40), 'finalization_revision_invalid');
  const row = git(repo, ['ls-tree', '-z', revision, '--', name], 8192).toString('utf8');
  const match = /^100644 blob ([a-f0-9]{40})\t([^\0]+)\0$/.exec(row);
  requireThat(match && match[2] === name, 'finalization_git_blob_invalid');
  const bytes = git(repo, ['cat-file', 'blob', `${revision}:${name}`], cap);
  requireThat(
    bytes.length > 0 &&
      bytes.length <= cap &&
      createHash('sha1').update(`blob ${bytes.length}\0`).update(bytes).digest('hex') === match[1],
    'finalization_git_blob_invalid',
  );
  return { bytes, gitBlobSha: match[1] };
}

// Parse the existing literal policy without importing or executing code at T.
function runtimeLiteral(bytes, name, required = true) {
  const source = ts.createSourceFile(
    name,
    bytes.toString('utf8'),
    ts.ScriptTarget.Latest,
    false,
    name.endsWith('.ts') ? ts.ScriptKind.TS : ts.ScriptKind.JS,
  );
  requireThat(source.parseDiagnostics.length === 0, 'finalization_runtime_policy_invalid');
  const declarations = [];
  function visit(node) {
    if (
      ts.isVariableDeclaration(node) &&
      ts.isIdentifier(node.name) &&
      node.name.text === 'PINNED_MANAGED_RUNTIME'
    )
      declarations.push(node);
    ts.forEachChild(node, visit);
  }
  visit(source);
  if (!required && declarations.length === 0) return null;
  requireThat(declarations.length === 1, 'finalization_runtime_policy_invalid');
  const call = declarations[0].initializer;
  requireThat(
    call &&
      ts.isCallExpression(call) &&
      ts.isPropertyAccessExpression(call.expression) &&
      ts.isIdentifier(call.expression.expression) &&
      call.expression.expression.text === 'Object' &&
      call.expression.name.text === 'freeze' &&
      call.arguments.length === 1 &&
      ts.isObjectLiteralExpression(call.arguments[0]),
    'finalization_runtime_policy_invalid',
  );
  const value = {};
  for (const property of call.arguments[0].properties) {
    requireThat(
      ts.isPropertyAssignment(property) &&
        (ts.isIdentifier(property.name) || ts.isStringLiteral(property.name)) &&
        (ts.isStringLiteral(property.initializer) || ts.isNumericLiteral(property.initializer)),
      'finalization_runtime_policy_invalid',
    );
    const key = property.name.text;
    requireThat(!Object.hasOwn(value, key), 'finalization_runtime_policy_invalid');
    value[key] = ts.isStringLiteral(property.initializer)
      ? property.initializer.text
      : Number(property.initializer.text);
  }
  return validateRuntimePolicy(value);
}
function actionIdentity(repo, request, mapBytes) {
  const revision = request.actionCommit;
  const names = git(
    repo,
    ['ls-tree', '-r', '-z', '--name-only', revision, '--', ACTION_ROOT],
    16384,
  )
    .toString('utf8')
    .split('\0')
    .filter(Boolean)
    .sort();
  requireThat(
    canonicalJson(names) ===
      canonicalJson(ACTION_ASSETS.map(([name]) => `${ACTION_ROOT}/${name}`).sort()),
    'finalization_action_inventory_drift',
  );
  const contents = new Map();
  const assets = ACTION_ASSETS.map(([path, cap]) => {
    const item = blob(repo, revision, `${ACTION_ROOT}/${path}`, cap);
    contents.set(path, item.bytes);
    return {
      path,
      mode: '100644',
      size: item.bytes.length,
      sha256: sha256(item.bytes),
      gitBlobSha: item.gitBlobSha,
    };
  });
  requireThat(contents.get('payload-map.json').equals(mapBytes), 'finalization_map_drift');
  const runtimeSource = blob(repo, revision, RUNTIME_SOURCE, 64 * 1024).bytes;
  const policy = runtimeLiteral(runtimeSource, RUNTIME_SOURCE);
  for (const name of ['index', 'prepare', 'cleanup', 'sdk-upload']) {
    const embedded = runtimeLiteral(
      contents.get(`internal/dist/${name}.js`),
      name + '.js',
      name !== 'sdk-upload',
    );
    requireThat(
      embedded === null || canonicalJson(embedded) === canonicalJson(policy),
      'finalization_runtime_bundle_drift',
    );
  }
  const runtime = { policy, sourceSha256: sha256(runtimeSource) };
  const tree = text(repo, ['rev-parse', `${revision}^{tree}`]);
  requireThat(hex(tree, 40), 'finalization_action_invalid');
  return {
    commit: revision,
    tree,
    bindingPr: request.bindingPr,
    assets,
    assetsSha256: sha256(canonicalJson(assets)),
    runtime,
    runtimePolicySha256: sha256(canonicalJson(runtime)),
  };
}
function sourceClosure(repo, c, sourceCommit, request) {
  requireThat(
    sourceCommit !== request.actionCommit &&
      c.receipt.identity.sourceCommit === sourceCommit &&
      c.producer.workflowCommit === request.workflowCommit,
    'finalization_source_drift',
  );
  ancestor(repo, sourceCommit, request.workflowCommit);
  ancestor(repo, request.workflowCommit, request.actionCommit);
  requireThat(
    text(repo, ['rev-parse', `${sourceCommit}^{tree}`]) === c.receipt.identity.sourceTree,
    'finalization_source_tree_drift',
  );
  const policy = blob(
    repo,
    sourceCommit,
    'scripts/release/build-payload.inputs.json',
    64 * 1024,
  ).bytes;
  const lock = blob(
    repo,
    sourceCommit,
    'runtime/src/AgenticPrReview.Runtime/packages.managed.lock.json',
    256 * 1024,
  ).bytes;
  const selected = managedDependencyPolicy(JSON.parse(policy.toString('utf8')));
  requireThat(
    sha256(policy) === c.buildInputs.policySha256 &&
      sha256(lock) === c.buildInputs.lockSha256 &&
      canonicalJson(selected.dependencies) === canonicalJson(c.buildInputs.dependencies) &&
      canonicalJson(c.buildInputs.target) === canonicalJson(MANAGED_TARGET),
    'finalization_build_inputs_drift',
  );
}
async function originals(input) {
  validateReleaseVersion(input.releaseVersion);
  requireThat(hex(input.sourceCommit, 40), 'finalization_source_invalid');
  const request = parseFinalizationRequest(input.requestBytes);
  const archive = await readBoundedFile(input.archivePath, MANAGED_LIMITS.archive);
  const candidateBytes = await readBoundedFile(input.candidatePath, CAPS.metadata);
  const mapBytes = await readBoundedFile(input.mapPath, CAPS.map);
  const bundle = await readBoundedFile(input.bundlePath, 4 * 1024 * 1024);
  const c = inspectCandidate(archive, candidateBytes, mapBytes, request.candidateSha256);
  requireThat(
    c.receipt.identity.releaseVersion === input.releaseVersion &&
      c.storage.artifactId === request.payloadId &&
      c.storage.sha256 === request.payloadSha256,
    'finalization_candidate_drift',
  );
  sourceClosure(input.repo, c, input.sourceCommit, request);
  const action = actionIdentity(input.repo, request, mapBytes);
  return { c, candidateBytes, mapBytes, bundle, action, request };
}
async function mainAuthority(repo, request, get) {
  const root = `/repos/${REPOSITORY}`;
  const [ref, pr] = await Promise.all([
    get(root + '/git/ref/heads/main'),
    get(root + '/pulls/' + request.bindingPr),
  ]);
  requireThat(
    ref?.ref === 'refs/heads/main' && ref.object?.type === 'commit' && hex(ref.object.sha, 40),
    'finalization_main_unknown',
  );
  const repository = (value) =>
    value?.full_name === REPOSITORY && decimal(value.id) === REPOSITORY_ID;
  requireThat(
    pr?.merged === true &&
      pr.state === 'closed' &&
      decimal(pr.number) === request.bindingPr &&
      pr.merge_commit_sha === request.actionCommit &&
      pr.base?.ref === 'main' &&
      repository(pr.base.repo) &&
      repository(pr.head?.repo) &&
      timestamp(pr.merged_at) <= Date.now(),
    'finalization_binding_not_merged',
  );
  ancestor(repo, request.actionCommit, ref.object.sha);
  return ref.object.sha;
}
function pairFor(data, metadata, provenance) {
  const { c, action, bundle, request } = data;
  const pair = {
    formatVersion: 1,
    kind: 'r7-finalized-pair',
    repository: REPOSITORY,
    qualification: { state: 'unqualified', ...QUALIFICATION_SCOPE },
    payload: {
      identity: c.receipt.identity,
      archiveName: c.receipt.archiveName,
      archiveSize: c.receipt.archiveSize,
      archiveSha256: c.receipt.archiveSha256,
      candidateSha256: request.candidateSha256,
      proposedMapSha256: c.proposedMapSha256,
      inventorySha256: sha256(canonicalJson(c.receipt.members)),
      buildInputsSha256: sha256(canonicalJson(c.buildInputs)),
    },
    producer: c.producer,
    storage: { payload: c.storage, metadata, provenance },
    provenance: {
      bundleSha256: sha256(bundle),
      signer: data.report.signer,
      predicateType: data.report.predicateType,
      verifier: data.report.verifier,
    },
    action,
  };
  validatePair(pair);
  requireThat(
    Buffer.byteLength(canonicalJson(pair)) <= PAIR_LIMITS.pair,
    'finalization_pair_too_large',
  );
  return pair;
}
async function authenticatedPair(input, env) {
  const data = await originals(input);
  const get = githubReader(env.GITHUB_TOKEN);
  const mainCommit = await mainAuthority(input.repo, data.request, get);
  data.report = await verifyCandidateProvenance(
    {
      ...input,
      candidateSha256: data.request.candidateSha256,
      workflowCommit: data.request.workflowCommit,
      metadataId: data.request.metadataId,
      metadataSha256: data.request.metadataSha256,
    },
    env,
  );
  requireThat(
    data.report.completedRunVerified === true && data.report.bundleSha256 === sha256(data.bundle),
    'finalization_provenance_denied',
  );
  // Re-observe storage after the cryptographic tool; no in-progress exception.
  const metadata = await verifyStorage(
    data.c.producer,
    { artifactId: data.request.metadataId, sha256: data.request.metadataSha256 },
    get,
    { role: 'metadata' },
  );
  const provenance = await verifyStorage(
    data.c.producer,
    { artifactId: data.request.provenanceId, sha256: data.request.provenanceSha256 },
    get,
    { role: 'provenance' },
  );
  requireThat(
    Object.values({ payload: data.c.storage, metadata, provenance }).every(
      (s) => timestamp(s.expiresAt) > Date.now(),
    ),
    'candidate_storage_expired_reprepare',
  );
  const pair = pairFor(data, metadata, provenance);
  return { pair, mainCommit, get };
}
export async function finalizeCandidate(input, env = process.env) {
  const output = admitOutput(input.repo, input.output);
  const { pair } = await authenticatedPair(input, env);
  const bytes = Buffer.from(canonicalJson(pair)),
    pairSha256 = sha256(bytes);
  await mkdir(output, { mode: 0o700 });
  await writeFile(join(output, 'finalized-pair.json'), bytes, { flag: 'wx', mode: 0o600 });
  await writeFile(join(output, 'finalized-pair.sha256'), pairSha256 + '\n', {
    flag: 'wx',
    mode: 0o600,
  });
  const summary = `Finalized pair (UNQUALIFIED)\nSHA256: ${pairSha256}\nS: ${pair.payload.identity.sourceCommit}\nW: ${pair.producer.workflowCommit}\nT: ${pair.action.commit}\nNo release mutation or qualification occurred.\n`;
  await writeFile(join(output, 'summary.txt'), summary, { flag: 'wx', mode: 0o600 });
  requireThat(
    sha256(await readBoundedFile(join(output, 'finalized-pair.json'), PAIR_LIMITS.pair)) ===
      pairSha256,
    'finalization_write_readback_failed',
  );
  if (env.GITHUB_OUTPUT)
    await appendFile(
      env.GITHUB_OUTPUT,
      `pair_sha256=${pairSha256}\npair_json=${bytes.toString('utf8')}\n`,
    );
  if (env.GITHUB_STEP_SUMMARY)
    await appendFile(
      env.GITHUB_STEP_SUMMARY,
      summary + '\n```json\n' + bytes.toString('utf8') + '\n```\n',
    );
  return {
    pairSha256,
    qualified: false,
    contentVerified: true,
    storageVerified: true,
    provenanceVerified: true,
  };
}
export async function verifyFinalizedPair(input, env = process.env) {
  const bytes = await readBoundedFile(input.pairPath, PAIR_LIMITS.pair);
  const pair = parseFinalizedPair(bytes, input.pairSha256);
  const authenticated = await authenticatedPair(input, env);
  requireThat(
    canonicalJson(authenticated.pair) === bytes.toString('utf8'),
    'finalization_pair_drift',
  );
  return { ...authenticated, pair, pairBytes: bytes };
}
export async function verifyQualifiedPair(input, env = process.env) {
  requireThat(hex(input.qualifierCommit, 40), 'qualification_revision_invalid');
  const verified = await verifyFinalizedPair(input, env);
  ancestor(input.repo, input.qualifierCommit, verified.mainCommit);
  const expectedChecksBytes = blob(
    input.repo,
    input.qualifierCommit,
    CHECK_MANIFEST_PATH,
    PAIR_LIMITS.expectedChecks,
  ).bytes;
  parseExpectedChecks(expectedChecksBytes);
  const archive = await readBoundedFile(
    input.qualificationArchivePath,
    PAIR_LIMITS.qualificationArtifact,
  );
  const files = inspectQualificationArchive(archive, input.qualificationSha256);
  const inspected = inspectQualificationContent({
    pairBytes: verified.pairBytes,
    pairSha256: input.pairSha256,
    receiptSha256: input.receiptSha256,
    expectedChecksBytes,
    ...files,
  });
  const r = inspected.receipt;
  requireThat(
    r.result === 'pass' &&
      r.executionKind === 'original-candidate' &&
      r.producer.workflowCommit === input.qualifierCommit,
    'qualification_refused',
  );
  const storage = await verifyStorage(
    r.producer,
    { artifactId: input.qualificationId, sha256: input.qualificationSha256 },
    verified.get,
    { role: 'qualification' },
  );
  requireThat(storage.size === archive.length, 'qualification_archive_size_drift');
  return {
    qualified: true,
    pairSha256: input.pairSha256,
    receiptSha256: input.receiptSha256,
    qualifierCommit: input.qualifierCommit,
    expectedChecksSha256: r.expectedChecksSha256,
    caseResultsSha256: r.caseResultsSha256,
    checkCount: r.checkCount,
  };
}
function argumentsFor(args, required) {
  requireThat(args.length === required.length * 2, 'finalization_arguments_invalid');
  const p = {};
  for (let n = 0; n < args.length; n += 2) {
    requireThat(
      required.includes(args[n]) && !Object.hasOwn(p, args[n]) && args[n + 1],
      'finalization_arguments_invalid',
    );
    p[args[n]] = args[n + 1];
  }
  return p;
}
const baseArguments = [
  '--repo',
  '--source',
  '--version',
  '--request',
  '--archive',
  '--candidate',
  '--map',
  '--bundle',
];
async function inputs(p) {
  return {
    repo: resolve(p['--repo']),
    sourceCommit: p['--source'],
    releaseVersion: p['--version'],
    requestBytes: await readBoundedFile(p['--request'], PAIR_LIMITS.request),
    archivePath: p['--archive'],
    candidatePath: p['--candidate'],
    mapPath: p['--map'],
    bundlePath: p['--bundle'],
  };
}
async function admitDownloadProducer(request, env) {
  // The v8 download adapter converts run/artifact IDs to JS numbers.
  const safeId = (value) =>
    Number.isSafeInteger(Number(decimal(value))) && String(Number(value)) === value;
  requireThat(
    ['payloadId', 'metadataId', 'provenanceId'].every((key) => safeId(request[key])),
    'finalization_download_identity_invalid',
  );
  const get = githubReader(env.GITHUB_TOKEN);
  const artifact = await get(`/repos/${REPOSITORY}/actions/artifacts/${request.payloadId}`);
  const match =
    typeof artifact?.name === 'string' &&
    /^r7-p1-payload-([1-9][0-9]{0,19})-([1-9][0-9]{0,19})$/.exec(artifact.name);
  requireThat(
    match && safeId(match[1]) && decimal(artifact.workflow_run?.id) === match[1],
    'finalization_download_identity_invalid',
  );
  const producer = {
    repository: REPOSITORY,
    repositoryId: REPOSITORY_ID,
    workflowPath: WORKFLOW,
    workflowCommit: request.workflowCommit,
    runId: match[1],
    runAttempt: match[2],
    runner: 'github-hosted-ubuntu-24.04-x64',
  };
  validateProducer(producer);
  for (const role of ['payload', 'metadata', 'provenance'])
    await verifyStorage(
      producer,
      { artifactId: request[role + 'Id'], sha256: request[role + 'Sha256'] },
      get,
      { role },
    );
  return producer;
}
export async function main(argv = process.argv.slice(2), env = process.env) {
  const [command, ...args] = argv;
  const controlRepo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
  if (command === 'admit-request') {
    const p = argumentsFor(args, ['--source', '--version', '--output']);
    requireThat(
      env.RELEASE_OPERATION === 'finalize' && hex(p['--source'], 40),
      'finalization_workflow_denied',
    );
    validateReleaseVersion(p['--version']);
    admitWorkflow(env, controlRepo);
    const bytes = Buffer.from(env.FINALIZE_REQUEST ?? '', 'utf8'),
      r = parseFinalizationRequest(bytes);
    requireThat(p['--source'] !== r.actionCommit, 'finalization_source_invalid');
    const producer = await admitDownloadProducer(r, env);
    await writeFile(p['--output'], bytes, { flag: 'wx', mode: 0o600 });
    if (env.GITHUB_OUTPUT)
      await appendFile(
        env.GITHUB_OUTPUT,
        `payload_id=${r.payloadId}\nmetadata_id=${r.metadataId}\nprovenance_id=${r.provenanceId}\nproducer_run_id=${producer.runId}\narchive_name=${managedArchiveName(p['--version'])}\n`,
      );
    return { requestAdmitted: true };
  }
  if (command === 'finalize' || command === 'workflow-finalize') {
    const p = argumentsFor(args, [...baseArguments, '--output']);
    if (command === 'workflow-finalize') {
      requireThat(
        env.RELEASE_OPERATION === 'finalize' && resolve(p['--repo']) === controlRepo,
        'finalization_workflow_denied',
      );
      admitWorkflow(env, controlRepo);
    }
    return finalizeCandidate({ ...(await inputs(p)), output: p['--output'] }, env);
  }
  if (command === 'inspect' || command === 'verify-pair' || command === 'verify-qualification') {
    const extras =
      command === 'verify-qualification'
        ? [
            '--qualifier',
            '--receipt-sha256',
            '--qualification-id',
            '--qualification-sha256',
            '--qualification-archive',
          ]
        : [];
    const p = argumentsFor(args, [...baseArguments, '--pair', '--sha256', ...extras]);
    const input = { ...(await inputs(p)), pairPath: p['--pair'], pairSha256: p['--sha256'] };
    if (command === 'verify-qualification')
      return verifyQualifiedPair(
        {
          ...input,
          qualifierCommit: p['--qualifier'],
          receiptSha256: p['--receipt-sha256'],
          qualificationId: p['--qualification-id'],
          qualificationSha256: p['--qualification-sha256'],
          qualificationArchivePath: p['--qualification-archive'],
        },
        env,
      );
    if (command === 'verify-pair') {
      await verifyFinalizedPair(input, env);
      return {
        contentVerified: true,
        storageVerified: true,
        provenanceVerified: true,
        qualified: false,
      };
    }
    const bytes = await readBoundedFile(input.pairPath, PAIR_LIMITS.pair),
      pair = parseFinalizedPair(bytes, input.pairSha256),
      data = await originals(input);
    data.report = pair.provenance;
    requireThat(
      canonicalJson(pairFor(data, pair.storage.metadata, pair.storage.provenance)) ===
        bytes.toString('utf8'),
      'finalization_pair_drift',
    );
    return {
      contentVerified: true,
      storageVerified: false,
      provenanceVerified: false,
      qualified: false,
    };
  }
  throw new Error('finalization_arguments_invalid');
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main()
    .then((value) => process.stdout.write(canonicalJson(value)))
    .catch((error) => {
      const reprepare = [
        'candidate_storage_expired_reprepare',
        'candidate_storage_missing_reprepare',
      ].includes(error?.message);
      process.stderr.write(
        reprepare ? 'candidate_reprepare_required\n' : 'candidate_finalization_denied\n',
      );
      process.exitCode = 1;
    });
}
