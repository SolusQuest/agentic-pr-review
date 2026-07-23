import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { constants as fsConstants } from 'node:fs';
import {
  chmod,
  open,
  mkdir,
  mkdtemp,
  readdir,
  realpath,
  rename,
  rm,
  writeFile,
} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { validateReviewInputV1, type ReviewInputV1 } from '../protocol/review-input.js';
import type { ReviewResultV1 } from '../protocol/review-result.js';
import type { ReviewTraceV1 } from '../protocol/review-trace.js';
import { canonicalJsonBytes } from '../canonical-json/index.js';
import {
  buildStateBundleV2,
  classifyStateBundleV2,
  LEDGER_MAX_BYTES,
  MANIFEST_MAX_BYTES,
  METADATA_MAX_BYTES,
  serializeStateManifestV2,
  type StateManifestV2,
  type StateManifestV2Input,
  type StateManifestV2Transition,
  validateStateManifestV2,
} from '../state-v2/index.js';
import {
  computeMetadataSemanticSha256,
  identityAgrees,
  parseProviderRunMetadata,
  type ValidatedProviderRunMetadataV1,
} from '../provider-metadata/index.js';
import {
  defaultFsSeams,
  BYTE_LIMITS,
  isExecutableFile,
  serializeInputBytes,
  sha256Hex,
  type FsSeams,
} from '../runtime-invocation/runtime-files.js';
import { validateSuccessAndBuildResultFromSnapshots } from './live-success-validator.js';
import type { RuntimeCommand } from '../runtime-invocation/runtime-command.js';
import { isValidAbortSignal } from '../runtime-invocation/options-validation.js';
import { computeSubjectDigest } from '../prefix-contract/digest.js';
import { deriveInteractionId } from '../prefix-contract/interaction-id.js';
import {
  LIVE_CONTEXT_FILENAME,
  LIVE_CONTEXT_MAX_BYTES,
  LIVE_CLOSE_DEADLINE_MS,
  LIVE_OUTPUT_FILENAMES,
  LIVE_STREAM_MAX_BYTES,
} from './constants.js';
import { LiveRuntimeInvocationError, type LiveRuntimeErrorKind } from './errors.js';
import { validateCandidateLedgerForHost } from './ledger-validator.js';
import { assertPrivateBytes, copySensitiveValues } from './privacy.js';
import {
  parseLiveRuntimeInvocationContext,
  type LiveRuntimeInvocationContextV1,
} from './context.js';

const MAX_LEDGER_CHANGED_FILES = 200 as const;
const MAX_LEDGER_CHANGED_FILE_PATH_LENGTH = 500 as const;
const MAX_LEDGER_CHANGED_FILE_VALUE = 1_000_000 as const;
const LEDGER_CHANGED_FILE_STATUSES = new Set([
  'added',
  'removed',
  'modified',
  'renamed',
  'copied',
  'changed',
  'unchanged',
]);
const CACHE_CONTRACT_IDENTITY_HEADER_KEYS = [
  'providerId',
  'modelId',
  'adapterId',
  'templateId',
  'policyId',
  'toolDefinitionId',
  'cacheConfigId',
] as const;

export function isWithinLedgerPathLength(value: string): boolean {
  return Array.from(value).length <= MAX_LEDGER_CHANGED_FILE_PATH_LENGTH;
}

export interface InvokeLiveRuntimeOptions {
  readonly command: RuntimeCommand;
  readonly input: ReviewInputV1;
  readonly context: LiveRuntimeInvocationContextV1;
  readonly manifestInput: StateManifestV2Input;
  readonly timeoutMs: number;
  readonly signal?: AbortSignal;
  readonly trustedRoot?: string;
  /** Values that may be supplied through serialized or diagnostic channels. */
  readonly sensitiveValues?: readonly string[];
  readonly predecessorLedgerBytes?: Uint8Array;
  readonly predecessorManifestBytes?: Uint8Array;
  readonly predecessorProviderRunMetadataBytes?: Uint8Array;
  readonly fs?: FsSeams;
}

export interface ValidatedLocalCandidateLease {
  readonly bundleDirectory: string;
  readonly manifest: ReturnType<typeof buildStateBundleV2>['manifest'];
  readonly manifestBytes: Uint8Array;
  readonly ledgerBytes: Uint8Array;
  readonly providerRunMetadataBytes: Uint8Array;
  readonly result: ReviewResultV1;
  readonly trace: ReviewTraceV1;
  readonly resultBytes: Uint8Array;
  readonly traceBytes: Uint8Array;
  readonly inputSha256: string;
  readonly resultSha256: string;
  readonly traceSha256: string;
  readonly candidateLedgerSha256: string;
  readonly metadataSemanticSha256: string;
  readonly cleanupWarnings: readonly string[];
  release(): Promise<void>;
}

export async function invokeLiveRuntime(
  options: InvokeLiveRuntimeOptions,
): Promise<ValidatedLocalCandidateLease> {
  const fs = options.fs ?? defaultFsSeams;
  validateOptions(options);
  if (process.platform !== 'linux')
    throw new LiveRuntimeInvocationError({
      kind: 'platform-unsupported',
      message: 'The experimental live sidecar seam is Linux-only.',
    });
  if (options.signal?.aborted)
    throw new LiveRuntimeInvocationError({
      kind: 'cancelled',
      message: 'Live runtime invocation was cancelled before preflight.',
    });

  const commandSnapshot: RuntimeCommand = {
    executablePath: options.command.executablePath,
    prefixArgs: options.command.prefixArgs ? [...options.command.prefixArgs] : undefined,
  };
  const inputValidation = validateReviewInputV1(options.input);
  if (!inputValidation.ok) {
    const count = inputValidation.errors?.length ?? 0;
    throw new LiveRuntimeInvocationError({
      kind: 'options-invalid',
      message:
        count > 0
          ? `ReviewInputV1 schema validation failed (${count} errors).`
          : 'ReviewInputV1 schema validation failed.',
    });
  }
  const inputBytes = serializeInputBytes(options.input);
  if (inputBytes.length > BYTE_LIMITS.input)
    throw new LiveRuntimeInvocationError({
      kind: 'options-invalid',
      message: 'Serialized input exceeds host byte cap.',
    });
  const inputSnapshot = JSON.parse(new TextDecoder().decode(inputBytes)) as ReviewInputV1;
  if (inputSnapshot.subject.changedFiles.length > MAX_LEDGER_CHANGED_FILES)
    throw new LiveRuntimeInvocationError({
      kind: 'options-invalid',
      message: `Live runtime accepts at most ${MAX_LEDGER_CHANGED_FILES} changed files.`,
    });
  if (
    inputSnapshot.subject.changedFiles.some(
      (file) =>
        !isWithinLedgerPathLength(file.path) ||
        (file.previousPath != null && !isWithinLedgerPathLength(file.previousPath)) ||
        !LEDGER_CHANGED_FILE_STATUSES.has(file.status) ||
        file.additions > MAX_LEDGER_CHANGED_FILE_VALUE ||
        file.deletions > MAX_LEDGER_CHANGED_FILE_VALUE ||
        file.changes > MAX_LEDGER_CHANGED_FILE_VALUE ||
        (file.patch?.maxChars ?? 0) > MAX_LEDGER_CHANGED_FILE_VALUE,
    )
  )
    throw new LiveRuntimeInvocationError({
      kind: 'options-invalid',
      message: 'Live changed-file metadata exceeds its ledger bounds.',
    });
  let suppliedManifestInputSnapshot: StateManifestV2Input;
  try {
    suppliedManifestInputSnapshot = JSON.parse(
      new TextDecoder().decode(canonicalJsonBytes(options.manifestInput, MANIFEST_MAX_BYTES)),
    ) as StateManifestV2Input;
  } catch {
    throw new LiveRuntimeInvocationError({
      kind: 'options-invalid',
      message: 'Manifest input could not be safely canonicalized within its byte cap.',
    });
  }
  const sensitive = copySensitiveValues(options.sensitiveValues);
  let suppliedContextBytes: Uint8Array;
  try {
    suppliedContextBytes = new Uint8Array(
      canonicalJsonBytes(options.context, LIVE_CONTEXT_MAX_BYTES),
    );
  } catch {
    throw new LiveRuntimeInvocationError({
      kind: 'context-invalid',
      message: 'Live context could not be safely canonicalized within its byte cap.',
    });
  }
  const parsedContext = parseLiveRuntimeInvocationContext(suppliedContextBytes);
  if (!parsedContext.valid)
    throw new LiveRuntimeInvocationError({
      kind: 'context-invalid',
      message: `Live context rejected (${parsedContext.code}).`,
    });
  const inputSha256 = sha256Hex(inputBytes);
  const context = materializeHostEpochs(parsedContext.context);
  const contextBytes = new Uint8Array(canonicalJsonBytes(context));
  const manifestInputSnapshot = alignManifestEpochs(suppliedManifestInputSnapshot, context);
  const inputRepository = `${inputSnapshot.host.repository.owner}/${inputSnapshot.host.repository.name}`;
  if (
    context.stateKey.repository !== inputRepository ||
    context.stateKey.headRepository !== inputRepository ||
    context.stateKey.pullRequest !== inputSnapshot.subject.pullRequest.number
  )
    throw new LiveRuntimeInvocationError({
      kind: 'binding-mismatch',
      message: 'Context state key does not match the exact review input scope.',
    });
  const isRootTransition =
    context.transition.kind === 'bootstrap' || context.transition.kind === 'recovery_root';
  if (isRootTransition && options.predecessorLedgerBytes)
    throw new LiveRuntimeInvocationError({
      kind: 'options-invalid',
      message: 'Root live transitions must not receive predecessor ledger bytes.',
    });
  if (isRootTransition && options.predecessorManifestBytes)
    throw new LiveRuntimeInvocationError({
      kind: 'options-invalid',
      message: 'Root live transitions must not receive predecessor manifest bytes.',
    });
  if (isRootTransition && options.predecessorProviderRunMetadataBytes)
    throw new LiveRuntimeInvocationError({
      kind: 'options-invalid',
      message: 'Root live transitions must not receive predecessor metadata bytes.',
    });
  if (
    !isRootTransition &&
    (!options.predecessorLedgerBytes ||
      !options.predecessorManifestBytes ||
      !options.predecessorProviderRunMetadataBytes)
  )
    throw new LiveRuntimeInvocationError({
      kind: 'options-invalid',
      message:
        'A non-root live transition requires predecessor ledger, manifest, and metadata bytes.',
    });
  if (
    (options.predecessorLedgerBytes?.byteLength ?? 0) > LEDGER_MAX_BYTES ||
    (options.predecessorManifestBytes?.byteLength ?? 0) > MANIFEST_MAX_BYTES ||
    (options.predecessorProviderRunMetadataBytes?.byteLength ?? 0) > METADATA_MAX_BYTES
  )
    throw new LiveRuntimeInvocationError({
      kind: 'restore-plan-invalid',
      message: 'Predecessor restore artifacts exceed their byte caps.',
    });
  const predecessorSnapshot = options.predecessorLedgerBytes
    ? new Uint8Array(options.predecessorLedgerBytes)
    : undefined;
  const predecessorManifestSnapshot = options.predecessorManifestBytes
    ? new Uint8Array(options.predecessorManifestBytes)
    : undefined;
  const predecessorMetadataSnapshot = options.predecessorProviderRunMetadataBytes
    ? new Uint8Array(options.predecessorProviderRunMetadataBytes)
    : undefined;
  if (inputSnapshot.host.review.runtimeProvider !== 'test')
    throw new LiveRuntimeInvocationError({
      kind: 'options-invalid',
      message: 'The #55 live seam requires the host test runtime provider.',
    });
  if (context.providerMode !== 'synthetic' && context.providerMode !== 'live')
    throw new LiveRuntimeInvocationError({
      kind: 'options-invalid',
      message: 'The live runtime provider mode is unsupported.',
    });
  if (context.currentInteraction.consumedInputSha256 !== inputSha256)
    throw new LiveRuntimeInvocationError({
      kind: 'binding-mismatch',
      message: 'Context consumed-input hash does not match the exact input snapshot.',
    });
  const subjectDigest = computeSubjectDigest(inputSnapshot.subject);
  if (!subjectDigest.ok || subjectDigest.value !== context.currentInteraction.subjectDigest)
    throw new LiveRuntimeInvocationError({
      kind: 'binding-mismatch',
      message: 'Context subject digest does not match the exact input snapshot.',
    });
  validateManifestPlan(manifestInputSnapshot, context);
  validateManifestProvenance(manifestInputSnapshot, context, inputSnapshot);
  validateRestorePlan(
    context,
    predecessorSnapshot,
    predecessorManifestSnapshot,
    predecessorMetadataSnapshot,
  );
  const derivedInteraction = deriveInteractionId(
    isRootTransition
      ? { kind: 'bootstrap' }
      : { kind: 'ledger', sha256Hex: sha256Hex(predecessorSnapshot!) },
    inputSha256,
    inputSnapshot.host.review.headSha,
    context.currentInteraction.interactionOrdinal,
  );
  if (
    !derivedInteraction.ok ||
    derivedInteraction.value !== context.currentInteraction.interactionId
  )
    throw new LiveRuntimeInvocationError({
      kind: 'binding-mismatch',
      message: 'Context interaction id does not match the exact host facts.',
    });

  const trustedRoot = await resolveTrustedRoot(options.trustedRoot);
  const invocationDirectory = await makePrivateDirectory(trustedRoot, 'agentic-pr-review-live-');
  let leaseContainer: string | undefined;
  let retainInvocationDirectory = false;
  try {
    const inputPath = path.join(invocationDirectory, LIVE_OUTPUT_FILENAMES.input);
    const contextPath = path.join(invocationDirectory, LIVE_CONTEXT_FILENAME);
    const predecessorPath =
      context.transition.kind === 'bootstrap' || context.transition.kind === 'recovery_root'
        ? undefined
        : path.join(invocationDirectory, LIVE_OUTPUT_FILENAMES.predecessorLedger);
    const outputPaths = {
      result: path.join(invocationDirectory, LIVE_OUTPUT_FILENAMES.result),
      trace: path.join(invocationDirectory, LIVE_OUTPUT_FILENAMES.trace),
      candidateLedger: path.join(invocationDirectory, LIVE_OUTPUT_FILENAMES.candidateLedger),
      providerRunMetadata: path.join(
        invocationDirectory,
        LIVE_OUTPUT_FILENAMES.providerRunMetadata,
      ),
    };
    const cliArgs = [
      'review-live',
      '--input',
      inputPath,
      '--context',
      contextPath,
      ...(predecessorPath ? ['--predecessor-ledger', predecessorPath] : []),
      '--output',
      outputPaths.result,
      '--trace',
      outputPaths.trace,
      '--candidate-ledger',
      outputPaths.candidateLedger,
      '--provider-run-metadata',
      outputPaths.providerRunMetadata,
    ];
    assertPrivateBytes([inputBytes, contextBytes], sensitive);
    assertPrivateBytes(
      [
        ...(commandSnapshot.prefixArgs?.map((arg) => new TextEncoder().encode(arg)) ?? []),
        new TextEncoder().encode(commandSnapshot.executablePath),
        ...cliArgs.map((arg) => new TextEncoder().encode(arg)),
      ],
      sensitive,
    );
    await writePrivate(inputPath, inputBytes);
    await writePrivate(contextPath, contextBytes);
    if (predecessorPath) {
      const predecessorBytes = predecessorSnapshot;
      if (!predecessorBytes)
        throw new LiveRuntimeInvocationError({
          kind: 'options-invalid',
          message: 'A non-root live transition requires predecessor ledger bytes.',
        });
      assertPrivateBytes([predecessorBytes], sensitive);
      await writePrivate(predecessorPath, predecessorBytes);
    }
    if (predecessorManifestSnapshot) assertPrivateBytes([predecessorManifestSnapshot], sensitive);
    if (predecessorMetadataSnapshot) assertPrivateBytes([predecessorMetadataSnapshot], sensitive);
    if (!(await isExecutableFile(commandSnapshot.executablePath, fs)))
      throw new LiveRuntimeInvocationError({
        kind: 'executable-invalid',
        message: 'The trusted runtime executable is not executable.',
      });
    // The provider key is deliberately environment-only. It is not included in
    // sensitiveValues because arbitrary contract-valid keys can collide with
    // ordinary JSON literals (for example, "null"). The trusted child env is
    // the only host-to-child channel that receives this value.
    const processResult = await runProcess(
      commandSnapshot,
      cliArgs,
      options.timeoutMs,
      options.signal,
      invocationDirectory,
      spawn,
      LIVE_CLOSE_DEADLINE_MS,
      context.providerMode === 'live' && process.env.AGENTIC_REVIEW_DEEPSEEK_API_KEY
        ? { AGENTIC_REVIEW_DEEPSEEK_API_KEY: process.env.AGENTIC_REVIEW_DEEPSEEK_API_KEY }
        : undefined,
    );
    assertPrivateBytes([processResult.stdout, processResult.stderr], sensitive);
    if (processResult.exitCode !== 0) {
      const providerFailure = classifyProviderFailure(processResult.stderr, processResult.exitCode);
      if (providerFailure) throw providerFailure;
      throw new LiveRuntimeInvocationError({
        kind:
          processResult.signal !== null
            ? 'host-terminated'
            : processResult.exitCode === null
              ? 'unknown-exit'
              : 'runtime-exit',
        message: 'review-live did not complete successfully.',
        exitCode: processResult.exitCode ?? undefined,
      });
    }
    if (processResult.stdout.byteLength !== 0)
      throw new LiveRuntimeInvocationError({
        kind: 'runtime-exit',
        message: 'review-live stdout was not empty.',
        exitCode: 0,
      });
    if (processResult.stderr.byteLength !== 0)
      throw new LiveRuntimeInvocationError({
        kind: 'runtime-exit',
        message: 'review-live stderr was not empty.',
        exitCode: 0,
      });

    const resultBytes = await readOutput(outputPaths.result, 'result-invalid');
    const traceBytes = await readOutput(outputPaths.trace, 'trace-invalid');
    const ledgerBytes = await readOutput(outputPaths.candidateLedger, 'candidate-ledger-invalid');
    const metadataBytes = await readOutput(
      outputPaths.providerRunMetadata,
      'provider-metadata-invalid',
    );
    assertPrivateBytes([resultBytes, traceBytes, ledgerBytes, metadataBytes], sensitive);
    let success: Awaited<ReturnType<typeof validateSuccessAndBuildResultFromSnapshots>>;
    try {
      success = await validateSuccessAndBuildResultFromSnapshots({
        resultPath: outputPaths.result,
        tracePath: outputPaths.trace,
        input: inputSnapshot,
        inputSha256,
        seams: fs,
        resultBytesSnapshot: resultBytes,
        traceBytesSnapshot: traceBytes,
      });
    } catch (error) {
      const kind =
        error instanceof Error && 'kind' in error
          ? String((error as { kind?: unknown }).kind)
          : 'result-invalid';
      const mapped: LiveRuntimeErrorKind =
        kind === 'trace-invalid'
          ? 'trace-invalid'
          : kind === 'missing-output'
            ? 'missing-output'
            : kind === 'unsafe-output-file'
              ? 'unsafe-output-file'
              : kind === 'hash-mismatch' || kind === 'process-contract-violation'
                ? 'binding-mismatch'
                : 'result-invalid';
      throw new LiveRuntimeInvocationError({
        kind: mapped,
        message: 'Live result/trace validation failed.',
      });
    }
    if (success.trace.mode !== 'live-provider' || success.trace.fixture !== undefined)
      throw new LiveRuntimeInvocationError({
        kind: 'trace-invalid',
        message: 'Live trace must use live-provider mode without a fixture.',
      });
    const ledger = validateCandidateLedgerForHost(
      ledgerBytes,
      {
        ...context,
        currentInteraction: {
          ...context.currentInteraction,
          reviewedHeadSha: inputSnapshot.host.review.headSha,
          reviewedBaseSha: inputSnapshot.host.review.baseSha,
          changedFiles: projectChangedFiles(inputSnapshot.subject.changedFiles),
        },
        outcome: success.result,
      },
      predecessorSnapshot,
    );
    const ledgerHeader = ledger.header as { kind?: unknown };
    if (ledgerHeader.kind !== context.transition.kind)
      throw new LiveRuntimeInvocationError({
        kind: 'binding-mismatch',
        message: 'Candidate ledger transition does not match the host context.',
      });
    const metadataResult = parseProviderRunMetadata(metadataBytes);
    if (!metadataResult.valid)
      throw new LiveRuntimeInvocationError({
        kind: 'provider-metadata-invalid',
        message: 'Provider metadata failed host validation.',
      });
    const metadata = metadataResult.metadata;
    validateBindings(
      context,
      metadata,
      inputSha256,
      success.resultBytes,
      success.traceBytes,
      ledgerBytes,
      predecessorSnapshot,
    );
    const metadataSemanticSha256 = computeMetadataSemanticSha256(metadata);
    const manifestInput = withHostTransaction(
      manifestInputSnapshot,
      context,
      inputSnapshot,
      inputSha256,
      success,
      metadataSemanticSha256,
    );
    const built = buildStateBundleV2(manifestInput, ledgerBytes, metadataBytes);
    assertPrivateBytes([built.manifestBytes], sensitive);
    leaseContainer = await makePrivateDirectory(
      trustedRoot,
      `agentic-pr-review-candidate-${randomBytes(16).toString('hex')}-`,
    );
    const staging = path.join(leaseContainer, '.staging');
    const bundle = path.join(leaseContainer, 'bundle');
    await mkdir(staging, { mode: 0o700 });
    await writePrivate(path.join(staging, 'ledger.json'), built.ledgerBytes);
    await writePrivate(
      path.join(staging, 'provider-run-metadata.json'),
      built.providerRunMetadataBytes,
    );
    await writePrivate(path.join(staging, 'manifest.json'), built.manifestBytes);
    const entries = await readdir(staging, { withFileTypes: true });
    const stagedLedger = await readOutput(
      path.join(staging, 'ledger.json'),
      'candidate-ledger-invalid',
    );
    const stagedMetadata = await readOutput(
      path.join(staging, 'provider-run-metadata.json'),
      'provider-metadata-invalid',
    );
    const stagedManifest = await readOutput(path.join(staging, 'manifest.json'), 'result-invalid');
    if (
      !equalBytes(stagedLedger, built.ledgerBytes) ||
      !equalBytes(stagedMetadata, built.providerRunMetadataBytes) ||
      !equalBytes(stagedManifest, built.manifestBytes)
    )
      throw new LiveRuntimeInvocationError({
        kind: 'local-commit-failed',
        message: 'Staged state-bundle bytes changed before publication.',
      });
    const classification = classifyStateBundleV2({
      entryListing: entries.map((entry) => ({ name: entry.name, isRegularFile: entry.isFile() })),
      manifestBytes: stagedManifest,
      ledgerBytes: stagedLedger,
      providerRunMetadataBytes: stagedMetadata,
    });
    if (classification.kind !== 'valid')
      throw new LiveRuntimeInvocationError({
        kind: 'local-commit-failed',
        message: 'Staged state bundle failed final classification.',
      });
    if (options.signal?.aborted)
      throw new LiveRuntimeInvocationError({
        kind: 'cancelled',
        message: 'Live invocation was cancelled before local commit.',
      });
    await rename(staging, bundle);
    return new LocalCandidateLease({
      bundleDirectory: bundle,
      manifest: built.manifest,
      manifestBytes: built.manifestBytes,
      ledgerBytes: built.ledgerBytes,
      providerRunMetadataBytes: built.providerRunMetadataBytes,
      result: success.result,
      trace: success.trace,
      resultBytes: success.resultBytes,
      traceBytes: success.traceBytes,
      inputSha256,
      resultSha256: sha256Hex(success.resultBytes),
      traceSha256: sha256Hex(success.traceBytes),
      candidateLedgerSha256: sha256Hex(ledgerBytes),
      metadataSemanticSha256,
      releaseDirectory: leaseContainer!,
    });
  } catch (error) {
    if (leaseContainer)
      await rm(leaseContainer, { recursive: true, force: true }).catch(() => undefined);
    if (error instanceof LiveRuntimeInvocationError) {
      retainInvocationDirectory = error.closeObserved === false;
      if (retainInvocationDirectory) scheduleInvocationDirectoryCleanup(invocationDirectory);
      throw error;
    }
    throw new LiveRuntimeInvocationError({
      kind: 'local-commit-failed',
      message: 'Live candidate publication failed.',
    });
  } finally {
    if (!retainInvocationDirectory)
      await rm(invocationDirectory, { recursive: true, force: true }).catch(() => undefined);
  }
}

function equalBytes(a: Uint8Array, b: Uint8Array): boolean {
  return a.byteLength === b.byteLength && a.every((value, index) => value === b[index]);
}

class LocalCandidateLease implements ValidatedLocalCandidateLease {
  private readonly manifestValue: ValidatedLocalCandidateLease['manifest'];
  private readonly manifestBytesValue: Uint8Array;
  private readonly ledgerBytesValue: Uint8Array;
  private readonly metadataBytesValue: Uint8Array;
  private readonly resultValue: ReviewResultV1;
  private readonly traceValue: ReviewTraceV1;
  private readonly resultBytesValue: Uint8Array;
  private readonly traceBytesValue: Uint8Array;
  private readonly releaseDirectory: string;
  private releasePromise: Promise<void> | undefined;
  private readonly warnings: string[] = [];

  constructor(args: {
    bundleDirectory: string;
    manifest: ValidatedLocalCandidateLease['manifest'];
    manifestBytes: Uint8Array;
    ledgerBytes: Uint8Array;
    providerRunMetadataBytes: Uint8Array;
    result: ReviewResultV1;
    trace: ReviewTraceV1;
    resultBytes: Uint8Array;
    traceBytes: Uint8Array;
    inputSha256: string;
    resultSha256: string;
    traceSha256: string;
    candidateLedgerSha256: string;
    metadataSemanticSha256: string;
    releaseDirectory: string;
  }) {
    this.bundleDirectory = args.bundleDirectory;
    this.manifestValue = structuredClone(args.manifest);
    this.manifestBytesValue = new Uint8Array(args.manifestBytes);
    this.ledgerBytesValue = new Uint8Array(args.ledgerBytes);
    this.metadataBytesValue = new Uint8Array(args.providerRunMetadataBytes);
    this.resultValue = structuredClone(args.result);
    this.traceValue = structuredClone(args.trace);
    this.resultBytesValue = new Uint8Array(args.resultBytes);
    this.traceBytesValue = new Uint8Array(args.traceBytes);
    this.inputSha256 = args.inputSha256;
    this.resultSha256 = args.resultSha256;
    this.traceSha256 = args.traceSha256;
    this.candidateLedgerSha256 = args.candidateLedgerSha256;
    this.metadataSemanticSha256 = args.metadataSemanticSha256;
    this.releaseDirectory = args.releaseDirectory;
  }

  readonly bundleDirectory: string;
  readonly inputSha256: string;
  readonly resultSha256: string;
  readonly traceSha256: string;
  readonly candidateLedgerSha256: string;
  readonly metadataSemanticSha256: string;
  get manifest() {
    return structuredClone(this.manifestValue);
  }
  get manifestBytes() {
    return new Uint8Array(this.manifestBytesValue);
  }
  get ledgerBytes() {
    return new Uint8Array(this.ledgerBytesValue);
  }
  get providerRunMetadataBytes() {
    return new Uint8Array(this.metadataBytesValue);
  }
  get result() {
    return structuredClone(this.resultValue);
  }
  get trace() {
    return structuredClone(this.traceValue);
  }
  get resultBytes() {
    return new Uint8Array(this.resultBytesValue);
  }
  get traceBytes() {
    return new Uint8Array(this.traceBytesValue);
  }
  get cleanupWarnings() {
    return [...this.warnings];
  }

  release(): Promise<void> {
    this.releasePromise ??= rm(this.releaseDirectory, { recursive: true, force: true }).catch(
      () => {
        this.warnings.push('candidate cleanup failed');
      },
    );
    return this.releasePromise;
  }
}

function validateOptions(options: InvokeLiveRuntimeOptions): void {
  if (
    !path.isAbsolute(options.command.executablePath) ||
    !Number.isSafeInteger(options.timeoutMs) ||
    options.timeoutMs <= 0
  )
    throw new LiveRuntimeInvocationError({
      kind: 'options-invalid',
      message: 'Live runtime options are invalid.',
    });
  if (options.signal !== undefined && !isValidAbortSignal(options.signal))
    throw new LiveRuntimeInvocationError({
      kind: 'options-invalid',
      message: 'options.signal must be a valid AbortSignal.',
    });
}

async function resolveTrustedRoot(root: string | undefined): Promise<string> {
  const candidate = root ?? os.tmpdir();
  if (!path.isAbsolute(candidate))
    throw new LiveRuntimeInvocationError({
      kind: 'options-invalid',
      message: 'trustedRoot must be absolute.',
    });
  const resolved = await realpath(candidate).catch(() => {
    throw new LiveRuntimeInvocationError({
      kind: 'options-invalid',
      message: 'trustedRoot must resolve to a trusted directory.',
    });
  });
  const workspace = process.env.GITHUB_WORKSPACE
    ? await realpath(process.env.GITHUB_WORKSPACE).catch(() => undefined)
    : undefined;
  if (workspace && (resolved === workspace || resolved.startsWith(`${workspace}${path.sep}`)))
    throw new LiveRuntimeInvocationError({
      kind: 'options-invalid',
      message: 'trustedRoot must be outside the workspace.',
    });
  return resolved;
}

async function makePrivateDirectory(root: string, prefix: string): Promise<string> {
  const directory = await mkdtemp(path.join(root, prefix));
  await chmod(directory, 0o700);
  return directory;
}

function scheduleInvocationDirectoryCleanup(directory: string): void {
  const cleanupTimer = setTimeout(() => {
    void rm(directory, { recursive: true, force: true }).catch(() => undefined);
  }, LIVE_CLOSE_DEADLINE_MS);
  cleanupTimer.unref?.();
}

async function writePrivate(file: string, bytes: Uint8Array): Promise<void> {
  await writeFile(file, bytes, { flag: 'wx', mode: 0o600 });
}

export async function readOutput(
  file: string,
  kind: LiveRuntimeErrorKind,
  beforeRead?: () => Promise<void>,
): Promise<Uint8Array> {
  let handle;
  try {
    handle = await open(
      file,
      fsConstants.O_RDONLY | fsConstants.O_NOFOLLOW | fsConstants.O_NONBLOCK,
    );
    const before = await handle.stat();
    if (!before.isFile() || before.size > 8 * 1024 * 1024) throw new Error('unsafe');
    await beforeRead?.();
    const expectedSize = before.size;
    const bytes = Buffer.allocUnsafe(expectedSize);
    let offset = 0;
    while (offset < expectedSize) {
      const { bytesRead } = await handle.read(bytes, offset, expectedSize - offset, offset);
      if (bytesRead === 0) throw new Error('unstable');
      offset += bytesRead;
    }
    const probe = Buffer.allocUnsafe(1);
    const { bytesRead: probeBytes } = await handle.read(probe, 0, 1, expectedSize);
    if (probeBytes !== 0) throw new Error('unstable');
    const after = await handle.stat();
    if (
      !after.isFile() ||
      after.size !== before.size ||
      after.dev !== before.dev ||
      after.ino !== before.ino ||
      after.mtimeMs !== before.mtimeMs
    )
      throw new Error('unstable');
    return bytes;
  } catch (error) {
    const code = (error as NodeJS.ErrnoException)?.code;
    const classifiedKind: LiveRuntimeErrorKind =
      code === 'ENOENT'
        ? 'missing-output'
        : code === 'ELOOP' || (error instanceof Error && /unsafe|unstable/i.test(error.message))
          ? 'unsafe-output-file'
          : kind;
    throw new LiveRuntimeInvocationError({
      kind: classifiedKind,
      message: 'review-live output could not be safely read.',
    });
  } finally {
    await handle?.close().catch(() => undefined);
  }
}

function validateBindings(
  context: LiveRuntimeInvocationContextV1,
  metadata: ValidatedProviderRunMetadataV1,
  inputHash: string,
  resultBytes: Uint8Array,
  traceBytes: Uint8Array,
  ledgerBytes: Uint8Array,
  predecessorBytes: Uint8Array | undefined,
): void {
  const expected = {
    providerId: context.cacheContractIdentity.providerId as string,
    resolvedModelId: context.cacheContractIdentity.modelId as string,
    adapterId: context.cacheContractIdentity.adapterId as string,
  };
  if (
    !identityAgrees(metadata, expected) ||
    metadata.producingRunId !== String(context.producingRun.producingRunId) ||
    metadata.runAttempt !== context.producingRun.runAttempt ||
    metadata.interactionId !== context.currentInteraction.interactionId ||
    metadata.consumedInputSha256 !== inputHash ||
    metadata.resultSha256 !== sha256Hex(resultBytes) ||
    metadata.traceSha256 !== sha256Hex(traceBytes) ||
    metadata.candidateLedgerSha256 !== sha256Hex(ledgerBytes) ||
    metadata.predecessorLedgerSha256 !==
      (context.transition.kind === 'bootstrap' || context.transition.kind === 'recovery_root'
        ? 'bootstrap'
        : sha256Hex(predecessorBytes!)) ||
    /^0+$/.test(metadata.logicalPrefixSha256) ||
    /^0+$/.test(metadata.prefixSha256)
  )
    throw new LiveRuntimeInvocationError({
      kind: 'binding-mismatch',
      message: 'Live sidecar transaction bindings disagree.',
    });
}

function materializeHostEpochs(
  context: LiveRuntimeInvocationContextV1,
): LiveRuntimeInvocationContextV1 {
  if (context.transition.kind === 'continuation') return context;
  const predecessorEpoch =
    context.transition.kind === 'reset'
      ? String(context.transition.predecessorLedgerEpoch)
      : undefined;
  let ledgerEpoch: string;
  do {
    ledgerEpoch = freshEpochId();
  } while (ledgerEpoch === predecessorEpoch);
  return {
    ...context,
    sessionEpoch: context.transition.kind === 'reset' ? context.sessionEpoch : freshEpochId(),
    generation: {
      ...context.generation,
      ledgerEpoch,
    },
  };
}

function freshEpochId(): string {
  return randomBytes(16).toString('base64url');
}

function alignManifestEpochs(
  manifestInput: StateManifestV2Input,
  context: LiveRuntimeInvocationContextV1,
): StateManifestV2Input {
  return {
    ...manifestInput,
    sessionEpoch: context.sessionEpoch as StateManifestV2Input['sessionEpoch'],
    generation: context.generation as unknown as StateManifestV2Input['generation'],
    providerRunMetadata: {
      ...manifestInput.providerRunMetadata,
      producingGeneration: {
        ...manifestInput.providerRunMetadata.producingGeneration,
        sessionEpoch: context.sessionEpoch as StateManifestV2Input['sessionEpoch'],
        stateGeneration: Number(context.generation.stateGeneration),
        ledgerEpoch: String(
          context.generation.ledgerEpoch,
        ) as StateManifestV2Input['generation']['ledgerEpoch'],
      },
    },
  };
}

function withHostTransaction(
  input: StateManifestV2Input,
  context: LiveRuntimeInvocationContextV1,
  reviewInput: ReviewInputV1,
  inputHash: string,
  success: Awaited<ReturnType<typeof validateSuccessAndBuildResultFromSnapshots>>,
  metadataSemanticSha256: string,
): StateManifestV2Input {
  const provenance = input.provenance;
  validateManifestProvenance(input, context, reviewInput);
  validateManifestPlan(input, context);
  return {
    ...input,
    stateKey: context.stateKey as unknown as StateManifestV2Input['stateKey'],
    sessionEpoch: context.sessionEpoch as StateManifestV2Input['sessionEpoch'],
    cacheContractIdentity:
      context.cacheContractIdentity as unknown as StateManifestV2Input['cacheContractIdentity'],
    generation: context.generation as unknown as StateManifestV2Input['generation'],
    transition: context.transition as unknown as StateManifestV2Input['transition'],
    provenance,
    transaction: {
      ...input.transaction,
      interactionId: context.currentInteraction
        .interactionId as StateManifestV2Input['transaction']['interactionId'],
      interactionOrdinal: context.currentInteraction.interactionOrdinal,
      consumedInputSha256: inputHash as StateManifestV2Input['transaction']['consumedInputSha256'],
      resultSha256: sha256Hex(
        success.resultBytes,
      ) as StateManifestV2Input['transaction']['resultSha256'],
      traceSha256: sha256Hex(
        success.traceBytes,
      ) as StateManifestV2Input['transaction']['traceSha256'],
      metadataSemanticSha256:
        metadataSemanticSha256 as StateManifestV2Input['transaction']['metadataSemanticSha256'],
    },
  };
}

function validateManifestProvenance(
  input: StateManifestV2Input,
  context: LiveRuntimeInvocationContextV1,
  reviewInput: ReviewInputV1,
): void {
  const hostHead = reviewInput.host.review.headSha;
  const hostBase = reviewInput.host.review.baseSha;
  const hostBaseRef = canonicalBaseRef(reviewInput.subject.pullRequest.baseRef);
  const provenance = input.provenance;
  if (
    provenance.producingRunId !== String(context.producingRun.producingRunId) ||
    provenance.producingRunAttempt !== context.producingRun.runAttempt ||
    provenance.reviewedHeadSha !== hostHead ||
    provenance.currentHeadSha !== hostHead ||
    provenance.reviewedBaseSha !== hostBase ||
    provenance.currentBaseSha !== hostBase ||
    provenance.reviewedBaseRef !== hostBaseRef ||
    provenance.currentBaseRef !== hostBaseRef
  )
    throw new LiveRuntimeInvocationError({
      kind: 'binding-mismatch',
      message: 'Manifest provenance does not match the host context and input facts.',
    });
}

function canonicalBaseRef(baseRef: string): string {
  return baseRef.startsWith('refs/') ? baseRef : `refs/heads/${baseRef}`;
}

function validateManifestPlan(
  input: StateManifestV2Input,
  context: LiveRuntimeInvocationContextV1,
): void {
  if (
    !equalJson(input.stateKey, context.stateKey) ||
    !equalJson(input.sessionEpoch, context.sessionEpoch) ||
    !equalJson(input.cacheContractIdentity, context.cacheContractIdentity) ||
    !equalJson(input.generation, context.generation) ||
    !equalJson(input.transition, context.transition)
  )
    throw new LiveRuntimeInvocationError({
      kind: 'options-invalid',
      message: 'Manifest restore plan does not match the frozen live context.',
    });
}

function validateRestorePlan(
  context: LiveRuntimeInvocationContextV1,
  predecessorBytes: Uint8Array | undefined,
  predecessorManifestBytes: Uint8Array | undefined,
  predecessorMetadataBytes: Uint8Array | undefined,
): void {
  const transition = context.transition;
  if (transition.kind === 'bootstrap' || transition.kind === 'recovery_root') {
    if (context.currentInteraction.interactionOrdinal !== 0)
      throw new LiveRuntimeInvocationError({
        kind: 'options-invalid',
        message: 'Root restore plans must use interaction ordinal zero.',
      });
    return;
  }
  if (!predecessorBytes || !predecessorManifestBytes || !predecessorMetadataBytes)
    throw new LiveRuntimeInvocationError({
      kind: 'options-invalid',
      message:
        'A non-root live transition requires predecessor ledger, manifest, and metadata bytes.',
    });
  const predecessor = validateCandidateLedgerForHost(predecessorBytes);
  validatePredecessorManifest(
    predecessorManifestBytes,
    context,
    predecessorBytes,
    predecessorMetadataBytes,
    predecessor,
  );
  const header = predecessor.header as Record<string, unknown>;
  const generation = context.generation;
  const state = context.stateKey;
  const identity = context.cacheContractIdentity;
  const predecessorHash = sha256Hex(predecessorBytes);
  if (
    transition.predecessorLedgerSha256 !== predecessorHash ||
    header.sessionEpoch !== context.sessionEpoch ||
    header.ledgerEpoch !== transition.predecessorLedgerEpoch ||
    header.stateGeneration !== transition.predecessorStateGeneration ||
    header.repository !== state.repository ||
    header.headRepository !== state.headRepository ||
    header.pullRequest !== state.pullRequest ||
    header.workflowIdentity !== state.workflowIdentity ||
    header.trustedExecutionDomain !== state.trustedExecutionDomain
  )
    throw new LiveRuntimeInvocationError({
      kind: 'predecessor-ledger-invalid',
      message: 'Predecessor ledger does not satisfy the host restore plan.',
    });
  const predecessorRecords = predecessor.records as unknown[];
  const predecessorOrdinal = predecessorRecords.length / 2;
  if (generation.stateGeneration !== Number(transition.predecessorStateGeneration) + 1)
    throw new LiveRuntimeInvocationError({
      kind: 'options-invalid',
      message: 'Non-root state generation must advance exactly one step.',
    });
  if (transition.kind === 'continuation') {
    if (
      header.providerId !== identity.providerId ||
      header.modelId !== identity.modelId ||
      header.adapterId !== identity.adapterId ||
      header.templateId !== identity.templateId ||
      header.policyId !== identity.policyId ||
      header.toolDefinitionId !== identity.toolDefinitionId ||
      header.cacheConfigId !== identity.cacheConfigId ||
      generation.ledgerEpoch !== header.ledgerEpoch ||
      context.currentInteraction.interactionOrdinal !== predecessorOrdinal
    )
      throw new LiveRuntimeInvocationError({
        kind: 'options-invalid',
        message: 'Continuation restore plan does not preserve session identity or ordinal.',
      });
  } else if (
    generation.ledgerEpoch === header.ledgerEpoch ||
    context.currentInteraction.interactionOrdinal !== 0
  )
    throw new LiveRuntimeInvocationError({
      kind: 'options-invalid',
      message: 'Reset restore plan must use a fresh epoch and ordinal zero.',
    });
  if (
    transition.kind === 'reset' &&
    transition.reason !== 'cache_contract_change' &&
    (header.providerId !== identity.providerId ||
      header.modelId !== identity.modelId ||
      header.adapterId !== identity.adapterId ||
      header.templateId !== identity.templateId ||
      header.policyId !== identity.policyId ||
      header.toolDefinitionId !== identity.toolDefinitionId ||
      header.cacheConfigId !== identity.cacheConfigId)
  )
    throw new LiveRuntimeInvocationError({
      kind: 'options-invalid',
      message: 'Reset restore plan changed cache identity without cache_contract_change.',
    });
}

function projectChangedFiles(
  files: ReviewInputV1['subject']['changedFiles'],
): Record<string, unknown>[] {
  return files.map((file) => ({
    path: file.path,
    ...(file.previousPath == null ? {} : { previousPath: file.previousPath }),
    status: file.status,
    additions: file.additions,
    deletions: file.deletions,
    changes: file.changes,
    ...(file.patch === undefined
      ? {}
      : {
          patch: {
            sha256: file.patch.sha256,
            truncated: file.patch.truncated,
            maxChars: file.patch.maxChars,
          },
        }),
  }));
}

function equalJson(left: unknown, right: unknown): boolean {
  try {
    return equalBytes(canonicalJsonBytes(left), canonicalJsonBytes(right));
  } catch {
    return false;
  }
}

function validatePredecessorManifest(
  bytes: Uint8Array,
  context: LiveRuntimeInvocationContextV1,
  predecessorLedgerBytes: Uint8Array,
  predecessorMetadataBytes: Uint8Array,
  predecessorLedger: Record<string, unknown>,
): void {
  if (bytes.byteLength > MANIFEST_MAX_BYTES)
    throw new LiveRuntimeInvocationError({
      kind: 'restore-plan-invalid',
      message: 'Predecessor manifest exceeds its byte cap.',
    });
  let parsed: unknown;
  try {
    parsed = JSON.parse(new TextDecoder('utf-8', { fatal: true, ignoreBOM: false }).decode(bytes));
  } catch {
    throw new LiveRuntimeInvocationError({
      kind: 'restore-plan-invalid',
      message: 'Predecessor manifest is not valid UTF-8 JSON.',
    });
  }
  const validation = validateStateManifestV2(parsed);
  if (!validation.ok)
    throw new LiveRuntimeInvocationError({
      kind: 'restore-plan-invalid',
      message: 'Predecessor manifest failed host validation.',
    });
  let canonical: Uint8Array;
  try {
    canonical = serializeStateManifestV2(validation.manifest);
  } catch {
    throw new LiveRuntimeInvocationError({
      kind: 'restore-plan-invalid',
      message: 'Predecessor manifest could not be canonically serialized.',
    });
  }
  const transition = context.transition;
  const manifest = validation.manifest;
  const metadata = parseProviderRunMetadata(predecessorMetadataBytes);
  const predecessorLedgerHash = sha256Hex(predecessorLedgerBytes);
  const predecessorHeader = predecessorLedger.header as Record<string, unknown>;
  const predecessorRecords = predecessorLedger.records as Array<Record<string, unknown>>;
  const predecessorContextRecord = predecessorRecords.at(-2);
  const predecessorOutcomeRecord = predecessorRecords.at(-1);
  if (
    !equalBytes(canonical, bytes) ||
    sha256Hex(bytes) !== transition.predecessorManifestSha256 ||
    !equalJson(manifest.stateKey, context.stateKey) ||
    manifest.sessionEpoch !== context.sessionEpoch ||
    manifest.generation.stateGeneration !== transition.predecessorStateGeneration ||
    manifest.generation.ledgerEpoch !== transition.predecessorLedgerEpoch ||
    manifest.ledger.sha256 !== predecessorLedgerHash ||
    manifest.ledger.bytes !== predecessorLedgerBytes.byteLength ||
    manifest.providerRunMetadata.bytes !== predecessorMetadataBytes.byteLength ||
    manifest.providerRunMetadata.sha256 !== sha256Hex(predecessorMetadataBytes) ||
    manifest.transaction.candidateLedgerSha256 !== predecessorLedgerHash ||
    !CACHE_CONTRACT_IDENTITY_HEADER_KEYS.every(
      (key) => manifest.cacheContractIdentity[key] === predecessorHeader[key],
    ) ||
    !predecessorContextRecord ||
    !predecessorOutcomeRecord ||
    manifest.transaction.interactionId !== predecessorContextRecord.interactionId ||
    manifest.transaction.interactionId !== predecessorOutcomeRecord.interactionId ||
    manifest.transaction.interactionOrdinal !== predecessorContextRecord.interactionOrdinal ||
    manifest.transaction.interactionOrdinal !== predecessorOutcomeRecord.interactionOrdinal ||
    manifest.provenance.reviewedHeadSha !== predecessorContextRecord.reviewedHeadSha ||
    manifest.provenance.reviewedBaseSha !== predecessorContextRecord.reviewedBaseSha ||
    !predecessorMetadataAgrees(metadata, manifest, predecessorLedgerHash, predecessorHeader)
  )
    throw new LiveRuntimeInvocationError({
      kind: 'restore-plan-invalid',
      message: 'Predecessor manifest does not match the accepted predecessor ledger and context.',
    });
}

function predecessorMetadataAgrees(
  result: ReturnType<typeof parseProviderRunMetadata>,
  manifest: StateManifestV2,
  predecessorLedgerHash: string,
  predecessorHeader: Record<string, unknown>,
): boolean {
  if (!result.valid) return false;
  try {
    const metadata = result.metadata;
    return (
      identityAgrees(metadata, {
        providerId: manifest.cacheContractIdentity.providerId,
        resolvedModelId: manifest.cacheContractIdentity.modelId,
        adapterId: manifest.cacheContractIdentity.adapterId,
      }) &&
      metadata.producingRunId === manifest.provenance.producingRunId &&
      metadata.runAttempt === manifest.provenance.producingRunAttempt &&
      metadata.interactionId === manifest.transaction.interactionId &&
      metadata.consumedInputSha256 === manifest.transaction.consumedInputSha256 &&
      metadata.resultSha256 === manifest.transaction.resultSha256 &&
      metadata.traceSha256 === manifest.transaction.traceSha256 &&
      metadata.candidateLedgerSha256 === manifest.transaction.candidateLedgerSha256 &&
      metadata.candidateLedgerSha256 === predecessorLedgerHash &&
      metadata.predecessorLedgerSha256 === manifest.transition.predecessorLedgerSha256 &&
      metadata.predecessorLedgerSha256 === predecessorHeader.predecessorLedgerSha256 &&
      predecessorTransitionAgrees(manifest.transition, predecessorHeader) &&
      computeMetadataSemanticSha256(metadata) === manifest.transaction.metadataSemanticSha256 &&
      !/^0+$/.test(metadata.logicalPrefixSha256) &&
      !/^0+$/.test(metadata.prefixSha256)
    );
  } catch {
    return false;
  }
}

function predecessorTransitionAgrees(
  transition: StateManifestV2Transition,
  header: Record<string, unknown>,
): boolean {
  if (
    header.kind !== transition.kind ||
    header.predecessorLedgerSha256 !== transition.predecessorLedgerSha256
  )
    return false;
  switch (transition.kind) {
    case 'bootstrap':
      return header.stateGeneration === 0;
    case 'recovery_root':
      return header.stateGeneration === 0 && header.recoveryReason === transition.reason;
    case 'continuation':
      return (
        header.predecessorLedgerEpoch === transition.predecessorLedgerEpoch &&
        header.predecessorStateGeneration === transition.predecessorStateGeneration
      );
    case 'reset':
      return (
        header.predecessorManifestSha256 === transition.predecessorManifestSha256 &&
        header.predecessorLedgerEpoch === transition.predecessorLedgerEpoch &&
        header.predecessorStateGeneration === transition.predecessorStateGeneration &&
        header.resetReason === transition.reason
      );
  }
}

interface ProcessResult {
  readonly exitCode: number | null;
  readonly signal: NodeJS.Signals | null;
  readonly stdout: Uint8Array;
  readonly stderr: Uint8Array;
}

export function classifyProviderFailure(
  stderr: Uint8Array,
  exitCode: number | null,
): LiveRuntimeInvocationError | undefined {
  let text: string;
  try {
    text = new TextDecoder('utf-8', { fatal: true }).decode(stderr);
  } catch {
    return undefined;
  }
  const match = /^((?:APR_PROVIDER_[A-Z0-9_]+)): Provider invocation failed\.\r?\n?$/.exec(text);
  if (!match) return undefined;
  const mapping: Record<string, { kind: LiveRuntimeErrorKind; exitCode: number }> = {
    APR_PROVIDER_TIMEOUT: { kind: 'provider-timeout', exitCode: 30 },
    APR_PROVIDER_CANCELLED: { kind: 'provider-cancelled', exitCode: 30 },
    APR_PROVIDER_RATE_LIMITED: { kind: 'provider-rate-limited', exitCode: 30 },
    APR_PROVIDER_4XX: { kind: 'provider-4xx', exitCode: 30 },
    APR_PROVIDER_5XX: { kind: 'provider-5xx', exitCode: 30 },
    APR_PROVIDER_TRANSPORT: { kind: 'provider-transport', exitCode: 30 },
    APR_PROVIDER_RESPONSE: { kind: 'provider-response', exitCode: 20 },
    APR_PROVIDER_CONFIG: { kind: 'provider-config', exitCode: 20 },
    APR_PROVIDER_PERSISTENCE: { kind: 'provider-persistence', exitCode: 40 },
  };
  const expected = mapping[match[1]!];
  if (!expected || exitCode !== expected.exitCode) return undefined;
  return new LiveRuntimeInvocationError({
    kind: expected.kind,
    message: `Live provider failed (${match[1]}).`,
    exitCode,
  });
}

export function runProcess(
  command: RuntimeCommand,
  args: readonly string[],
  timeoutMs: number,
  signal: AbortSignal | undefined,
  cwd: string,
  spawnFn: typeof spawn = spawn,
  closeDeadlineMs: number = LIVE_CLOSE_DEADLINE_MS,
  environment?: Record<string, string>,
): Promise<ProcessResult> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(
        new LiveRuntimeInvocationError({
          kind: 'cancelled',
          message: 'Live runtime invocation was cancelled before spawn.',
        }),
      );
      return;
    }
    let child;
    try {
      child = spawnFn(command.executablePath, [...(command.prefixArgs ?? []), ...args], {
        stdio: ['ignore', 'pipe', 'pipe'],
        env: {
          PATH: process.env.PATH ?? '',
          SystemRoot: process.env.SystemRoot ?? '',
          NO_COLOR: '1',
          DOTNET_NOLOGO: '1',
          DOTNET_CLI_TELEMETRY_OPTOUT: '1',
          ...(environment ?? {}),
        },
        cwd,
      });
    } catch {
      reject(
        new LiveRuntimeInvocationError({
          kind: 'spawn-failed',
          message: 'The live runtime could not be spawned.',
        }),
      );
      return;
    }
    const stdout: Buffer[] = [],
      stderr: Buffer[] = [];
    let stdoutBytes = 0,
      stderrBytes = 0,
      closeObserved = false,
      naturalExitObserved = false,
      exitSignal: NodeJS.Signals | null = null,
      terminalError: LiveRuntimeInvocationError | undefined,
      completed = false,
      postKillTimer: ReturnType<typeof setTimeout> | undefined,
      postExitTimer: ReturnType<typeof setTimeout> | undefined;
    const killChild = () => {
      try {
        child.kill('SIGTERM');
      } catch {
        /* process may already be gone */
      }
      setTimeout(() => {
        if (!closeObserved) {
          try {
            child.kill('SIGKILL');
          } catch {
            /* process may already be gone */
          }
        }
      }, 100);
    };
    const closeTimeout = () => {
      if (completed || closeObserved) return;
      completed = true;
      clearTimeout(timer);
      signal?.removeEventListener('abort', abort);
      const error = terminalError;
      reject(
        new LiveRuntimeInvocationError({
          kind: error?.kind ?? 'runtime-exit',
          message: `${error?.message ?? 'Live runtime child close was not observed.'} (child close was not observed).`,
          exitCode: error?.exitCode ?? exitCode ?? undefined,
          closeObserved: false,
        }),
      );
    };
    const startCloseDeadline = () => {
      postExitTimer ??= setTimeout(closeTimeout, closeDeadlineMs);
    };
    const fail = (error: LiveRuntimeInvocationError, allowAfterNaturalExit = false) => {
      if (completed || terminalError || (naturalExitObserved && !allowAfterNaturalExit)) return;
      terminalError = error;
      if (!naturalExitObserved) {
        killChild();
        postKillTimer ??= setTimeout(closeTimeout, 2_000);
      }
      if (closeObserved) finish();
    };
    const finish = () => {
      if (completed || !closeObserved) return;
      completed = true;
      clearTimeout(timer);
      if (postKillTimer) clearTimeout(postKillTimer);
      if (postExitTimer) clearTimeout(postExitTimer);
      signal?.removeEventListener('abort', abort);
      if (terminalError) reject(terminalError);
      else
        resolve({
          exitCode,
          signal: exitSignal,
          stdout: Buffer.concat(stdout),
          stderr: Buffer.concat(stderr),
        });
    };
    const append = (target: Buffer[], chunk: Buffer, stream: 'stdout' | 'stderr') => {
      const bytes =
        stream === 'stdout' ? stdoutBytes + chunk.byteLength : stderrBytes + chunk.byteLength;
      if (bytes > LIVE_STREAM_MAX_BYTES) {
        fail(
          new LiveRuntimeInvocationError({
            kind: 'stream-limit-exceeded',
            message: 'Live runtime stream exceeded its cap.',
          }),
          true,
        );
        return;
      }
      target.push(chunk);
      if (stream === 'stdout') stdoutBytes = bytes;
      else stderrBytes = bytes;
    };
    child.stdout.on('data', (chunk: Buffer) => append(stdout, chunk, 'stdout'));
    child.stderr.on('data', (chunk: Buffer) => append(stderr, chunk, 'stderr'));
    child.on('error', () =>
      fail(
        new LiveRuntimeInvocationError({
          kind: 'spawn-failed',
          message: 'The live runtime could not be spawned.',
        }),
      ),
    );
    let exitCode: number | null = null;
    child.on('exit', (code: number | null, signal: NodeJS.Signals | null) => {
      if (!terminalError && !naturalExitObserved) {
        naturalExitObserved = true;
        exitCode = code;
        exitSignal = signal;
        startCloseDeadline();
      }
    });
    child.on('close', (code: number | null, signal: NodeJS.Signals | null) => {
      if (!naturalExitObserved) {
        exitCode = code;
        exitSignal = signal;
      }
      closeObserved = true;
      finish();
    });
    const abort = () => {
      fail(
        new LiveRuntimeInvocationError({
          kind: 'cancelled',
          message: 'Live runtime invocation was cancelled.',
        }),
      );
    };
    signal?.addEventListener('abort', abort, { once: true });
    if (signal?.aborted) abort();
    const timer = setTimeout(() => {
      fail(
        new LiveRuntimeInvocationError({
          kind: 'timed-out',
          message: 'Live runtime invocation timed out.',
        }),
      );
    }, timeoutMs);
  });
}
