import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import {
  chmod,
  lstat,
  mkdir,
  mkdtemp,
  readFile,
  realpath,
  rename,
  rm,
  writeFile,
} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import type { ReviewInputV1 } from '../protocol/review-input.js';
import type { ReviewResultV1 } from '../protocol/review-result.js';
import type { ReviewTraceV1 } from '../protocol/review-trace.js';
import { canonicalJsonBytes } from '../canonical-json/index.js';
import { buildStateBundleV2, type StateManifestV2Input } from '../state-v2/index.js';
import {
  computeMetadataSemanticSha256,
  identityAgrees,
  parseProviderRunMetadata,
  type ValidatedProviderRunMetadataV1,
} from '../provider-metadata/index.js';
import {
  defaultFsSeams,
  isExecutableFile,
  serializeInputBytes,
  sha256Hex,
  type FsSeams,
} from '../runtime-invocation/runtime-files.js';
import { validateSuccessAndBuildResult } from '../runtime-invocation/success-validator.js';
import type { RuntimeCommand } from '../runtime-invocation/runtime-command.js';
import {
  LIVE_CONTEXT_FILENAME,
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

export interface InvokeLiveRuntimeOptions {
  readonly command: RuntimeCommand;
  readonly input: ReviewInputV1;
  readonly context: LiveRuntimeInvocationContextV1;
  readonly manifestInput: StateManifestV2Input;
  readonly timeoutMs: number;
  readonly signal?: AbortSignal;
  readonly trustedRoot?: string;
  readonly sensitiveValues?: readonly string[];
  readonly predecessorLedgerBytes?: Uint8Array;
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

  const sensitive = copySensitiveValues(options.sensitiveValues);
  const inputBytes = serializeInputBytes(options.input);
  const contextBytes = new Uint8Array(canonicalJsonBytes(options.context));
  const parsedContext = parseLiveRuntimeInvocationContext(contextBytes);
  if (!parsedContext.valid)
    throw new LiveRuntimeInvocationError({
      kind: 'context-invalid',
      message: `Live context rejected (${parsedContext.code}).`,
    });
  const inputSha256 = sha256Hex(inputBytes);
  if (options.context.currentInteraction.consumedInputSha256 !== inputSha256)
    throw new LiveRuntimeInvocationError({
      kind: 'binding-mismatch',
      message: 'Context consumed-input hash does not match the exact input snapshot.',
    });

  const trustedRoot = await resolveTrustedRoot(options.trustedRoot);
  const invocationDirectory = await makePrivateDirectory(trustedRoot, 'agentic-pr-review-live-');
  let leaseContainer: string | undefined;
  try {
    const inputPath = path.join(invocationDirectory, LIVE_OUTPUT_FILENAMES.input);
    const contextPath = path.join(invocationDirectory, LIVE_CONTEXT_FILENAME);
    const predecessorPath =
      options.context.transition.kind === 'bootstrap' ||
      options.context.transition.kind === 'recovery_root'
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
        ...(options.command.prefixArgs?.map((arg) => new TextEncoder().encode(arg)) ?? []),
        new TextEncoder().encode(options.command.executablePath),
        ...cliArgs.map((arg) => new TextEncoder().encode(arg)),
      ],
      sensitive,
    );
    await writePrivate(inputPath, inputBytes);
    await writePrivate(contextPath, contextBytes);
    if (predecessorPath) {
      const predecessorBytes = options.predecessorLedgerBytes;
      if (!predecessorBytes)
        throw new LiveRuntimeInvocationError({
          kind: 'options-invalid',
          message: 'A non-root live transition requires predecessor ledger bytes.',
        });
      assertPrivateBytes([predecessorBytes], sensitive);
      await writePrivate(predecessorPath, predecessorBytes);
    }
    if (!(await isExecutableFile(options.command.executablePath, fs)))
      throw new LiveRuntimeInvocationError({
        kind: 'executable-invalid',
        message: 'The trusted runtime executable is not executable.',
      });
    const processResult = await runProcess(
      options.command,
      cliArgs,
      options.timeoutMs,
      options.signal,
    );
    assertPrivateBytes([processResult.stdout, processResult.stderr], sensitive);
    if (processResult.exitCode !== 0)
      throw new LiveRuntimeInvocationError({
        kind: processResult.exitCode === null ? 'unknown-exit' : 'runtime-exit',
        message: 'review-live did not complete successfully.',
        exitCode: processResult.exitCode ?? undefined,
      });
    if (processResult.stdout.byteLength !== 0)
      throw new LiveRuntimeInvocationError({
        kind: 'runtime-exit',
        message: 'review-live stdout was not empty.',
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
    let success: Awaited<ReturnType<typeof validateSuccessAndBuildResult>>;
    try {
      success = await validateSuccessAndBuildResult({
        resultPath: outputPaths.result,
        tracePath: outputPaths.trace,
        input: options.input,
        inputSha256,
        seams: fs,
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
    if (success.trace.mode !== 'live-provider' || success.trace.fixture !== null)
      throw new LiveRuntimeInvocationError({
        kind: 'trace-invalid',
        message: 'Live trace must use live-provider mode without a fixture.',
      });
    const ledger = validateCandidateLedgerForHost(ledgerBytes);
    const ledgerHeader = ledger.header as { kind?: unknown };
    if (ledgerHeader.kind !== options.context.transition.kind)
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
      options.context,
      metadata,
      inputSha256,
      success.resultBytes,
      success.traceBytes,
      ledgerBytes,
    );
    const metadataSemanticSha256 = computeMetadataSemanticSha256(metadata);
    const manifestInput = withHostTransaction(
      options.manifestInput,
      options.context,
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
    if (options.signal?.aborted)
      throw new LiveRuntimeInvocationError({
        kind: 'cancelled',
        message: 'Live invocation was cancelled before local commit.',
      });
    await rename(staging, bundle);
    const warnings: string[] = [];
    const lease: ValidatedLocalCandidateLease = {
      bundleDirectory: bundle,
      manifest: structuredClone(built.manifest),
      manifestBytes: new Uint8Array(built.manifestBytes),
      ledgerBytes: new Uint8Array(built.ledgerBytes),
      providerRunMetadataBytes: new Uint8Array(built.providerRunMetadataBytes),
      result: success.result,
      trace: success.trace,
      resultBytes: new Uint8Array(success.resultBytes),
      traceBytes: new Uint8Array(success.traceBytes),
      inputSha256,
      resultSha256: sha256Hex(success.resultBytes),
      traceSha256: sha256Hex(success.traceBytes),
      candidateLedgerSha256: sha256Hex(ledgerBytes),
      metadataSemanticSha256,
      cleanupWarnings: warnings,
      release: async () => {
        await rm(leaseContainer!, { recursive: true, force: true });
      },
    };
    return lease;
  } catch (error) {
    if (leaseContainer)
      await rm(leaseContainer, { recursive: true, force: true }).catch(() => undefined);
    if (error instanceof LiveRuntimeInvocationError) throw error;
    throw new LiveRuntimeInvocationError({
      kind: 'local-commit-failed',
      message: 'Live candidate publication failed.',
    });
  } finally {
    await rm(invocationDirectory, { recursive: true, force: true }).catch(() => undefined);
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

async function writePrivate(file: string, bytes: Uint8Array): Promise<void> {
  await writeFile(file, bytes, { flag: 'wx', mode: 0o600 });
}

async function readOutput(file: string, kind: LiveRuntimeErrorKind): Promise<Uint8Array> {
  let stats;
  try {
    stats = await lstat(file);
  } catch {
    throw new LiveRuntimeInvocationError({
      kind: 'missing-output',
      message: 'review-live output is missing.',
    });
  }
  if (!stats.isFile() || stats.isSymbolicLink() || stats.size > 8 * 1024 * 1024)
    throw new LiveRuntimeInvocationError({
      kind: 'unsafe-output-file',
      message: 'review-live output is not a bounded regular file.',
    });
  try {
    return new Uint8Array(await readFile(file));
  } catch {
    throw new LiveRuntimeInvocationError({
      kind,
      message: 'review-live output could not be read.',
    });
  }
}

function validateBindings(
  context: LiveRuntimeInvocationContextV1,
  metadata: ValidatedProviderRunMetadataV1,
  inputHash: string,
  resultBytes: Uint8Array,
  traceBytes: Uint8Array,
  ledgerBytes: Uint8Array,
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
    metadata.candidateLedgerSha256 !== sha256Hex(ledgerBytes)
  )
    throw new LiveRuntimeInvocationError({
      kind: 'binding-mismatch',
      message: 'Live sidecar transaction bindings disagree.',
    });
}

function withHostTransaction(
  input: StateManifestV2Input,
  context: LiveRuntimeInvocationContextV1,
  inputHash: string,
  success: Awaited<ReturnType<typeof validateSuccessAndBuildResult>>,
  metadataSemanticSha256: string,
): StateManifestV2Input {
  return {
    ...input,
    stateKey: context.stateKey as unknown as StateManifestV2Input['stateKey'],
    sessionEpoch: context.sessionEpoch as StateManifestV2Input['sessionEpoch'],
    cacheContractIdentity:
      context.cacheContractIdentity as unknown as StateManifestV2Input['cacheContractIdentity'],
    generation: context.generation as unknown as StateManifestV2Input['generation'],
    transition: context.transition as unknown as StateManifestV2Input['transition'],
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

interface ProcessResult {
  readonly exitCode: number | null;
  readonly stdout: Uint8Array;
  readonly stderr: Uint8Array;
}

function runProcess(
  command: RuntimeCommand,
  args: readonly string[],
  timeoutMs: number,
  signal: AbortSignal | undefined,
): Promise<ProcessResult> {
  return new Promise((resolve, reject) => {
    let child;
    try {
      child = spawn(command.executablePath, [...(command.prefixArgs ?? []), ...args], {
        stdio: ['ignore', 'pipe', 'pipe'],
        env: {
          PATH: process.env.PATH ?? '',
          SystemRoot: process.env.SystemRoot ?? '',
          NO_COLOR: '1',
          DOTNET_NOLOGO: '1',
          DOTNET_CLI_TELEMETRY_OPTOUT: '1',
        },
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
      settled = false;
    const finish = (result: ProcessResult) => {
      if (!settled) {
        settled = true;
        clearTimeout(timer);
        signal?.removeEventListener('abort', abort);
        resolve(result);
      }
    };
    const fail = (error: LiveRuntimeInvocationError) => {
      if (!settled) {
        settled = true;
        clearTimeout(timer);
        signal?.removeEventListener('abort', abort);
        child.kill('SIGKILL');
        reject(error);
      }
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
    child.on('close', (code: number | null) =>
      finish({ exitCode: code, stdout: Buffer.concat(stdout), stderr: Buffer.concat(stderr) }),
    );
    const abort = () => {
      child.kill('SIGKILL');
      fail(
        new LiveRuntimeInvocationError({
          kind: 'cancelled',
          message: 'Live runtime invocation was cancelled.',
        }),
      );
    };
    signal?.addEventListener('abort', abort, { once: true });
    const timer = setTimeout(() => {
      child.kill('SIGKILL');
      fail(
        new LiveRuntimeInvocationError({
          kind: 'timed-out',
          message: 'Live runtime invocation timed out.',
        }),
      );
    }, timeoutMs);
  });
}
