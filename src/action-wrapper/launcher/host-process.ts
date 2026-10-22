import { spawn } from 'node:child_process';
import type { FileHandle } from 'node:fs/promises';
import type { Readable, Writable } from 'node:stream';
import { finished } from 'node:stream/promises';
import { TextDecoder } from 'node:util';

import {
  H1_MAXIMUM_COMPLETION_DOCUMENT_BYTES,
  H1_MAXIMUM_LAUNCH_DOCUMENT_BYTES,
} from './contracts.js';
import {
  admitManagedRuntime,
  validateManagedLoadingConfiguration,
  ManagedRuntimePrerequisiteError,
  type AdmittedManagedRuntime,
} from './managed-runtime.js';
import {
  acquireVerifiedManagedTreeLease,
  type VerifiedManagedTree,
  type VerifiedManagedTreeLease,
} from './payload-staging.js';
import {
  trustedProofHostReceiptProfile,
  type TrustedProofRequestBudgetProfile,
} from './request-budget-profile.js';
import { fail } from './validation.js';

export const HOST_CANCELLATION_RECONCILIATION_GRACE_MS = 130_000;
export const HOST_POST_KILL_CLOSE_GRACE_MS = 5_000;
export const HOST_STDERR_CAPTURE_MAXIMUM_BYTES = 8 * 1024;
const HOST_EXECUTABLE_FD = 3;
const GITHUB_REQUEST_BUDGET_PREFIX = 'APR_R4_E2P_GITHUB_REQUEST_BUDGET ';
const CONTROL_REQUEST_BUDGET_PREFIX = 'APR_R4_E2P_CONTROL_REQUEST_BUDGET ';
export const STATE_RECONCILIATION_DIAGNOSTIC_PREFIX = 'APR_R4_E2P_STATE_RECONCILIATION ';

export class HostProcessTerminationUnconfirmedError extends Error {
  constructor(readonly terminationConfirmed?: Promise<void>) {
    super('wrapper_host_termination_unconfirmed');
    this.name = 'HostProcessTerminationUnconfirmedError';
  }
}

export interface HostProcessResult {
  readonly completionBytes: Buffer;
  readonly exitCode: number;
  /** Canonical, secret-free proof receipts admitted from otherwise private stderr. */
  readonly trustedProofBudgetReceiptLines: readonly string[];
  /** One canonical, secret-free reconciliation diagnostic for a verified r4-w2 proof. */
  readonly trustedProofStateReconciliationDiagnosticLine?: string;
}

export interface HostProcessRequest {
  readonly executableHandle: FileHandle;
  readonly launchBytes: Uint8Array;
  readonly tempRoot: string;
  readonly signal: AbortSignal;
  /** Present only for a verified r4-w2 proof. */
  readonly requestBudgetProfile?: TrustedProofRequestBudgetProfile;
  readonly cancellationKillGraceMs?: number;
  readonly postKillCloseGraceMs?: number;
}

export type HostProcessRunner = (request: HostProcessRequest) => Promise<HostProcessResult>;

export async function runHostProcess(request: HostProcessRequest): Promise<HostProcessResult> {
  return runProcess(request, {});
}

/** Explicit current CLI route for the native/framework executable supervisor. */
export async function runCliHostProcess(request: HostProcessRequest): Promise<HostProcessResult> {
  return runProcess(request, { args: ['run'] });
}

export interface ManagedHostProcessRequest extends Omit<HostProcessRequest, 'executableHandle'> {
  readonly tree: VerifiedManagedTree;
  /** Trusted runner/setup input, never supplied by the reviewed workspace or ambient PATH. */
  readonly trustedRuntimeRoot: string;
  readonly workspace: string;
}

export class ManagedHostProcessTerminationUnconfirmedError extends HostProcessTerminationUnconfirmedError {
  constructor(
    terminationConfirmed: Promise<void>,
    readonly resourcesReleased: Promise<void>,
  ) {
    super(terminationConfirmed);
    this.name = 'ManagedHostProcessTerminationUnconfirmedError';
    // Observability is retained for callers without creating an unhandled late cleanup rejection.
    void resourcesReleased.catch(() => {});
  }
}

export async function runManagedHostProcess(
  request: ManagedHostProcessRequest,
): Promise<HostProcessResult> {
  if (
    !(request.launchBytes instanceof Uint8Array) ||
    request.launchBytes.byteLength < 1 ||
    request.launchBytes.byteLength > H1_MAXIMUM_LAUNCH_DOCUMENT_BYTES
  )
    fail('wrapper_launch_invalid');
  const captured = { ...request, launchBytes: Buffer.from(request.launchBytes) };
  try {
    if (captured.signal.aborted) fail('wrapper_cancelled_before_spawn');
    if (
      captured.requestBudgetProfile !== undefined &&
      trustedProofHostReceiptProfile(captured.requestBudgetProfile) === undefined
    )
      fail('wrapper_host_process_failed');
    duration(captured.cancellationKillGraceMs ?? HOST_CANCELLATION_RECONCILIATION_GRACE_MS);
    duration(captured.postKillCloseGraceMs ?? HOST_POST_KILL_CLOSE_GRACE_MS);
    let runtime: AdmittedManagedRuntime | undefined;
    let lease: VerifiedManagedTreeLease | undefined;
    let probing = true;
    const release = async (): Promise<void> => {
      lease?.release();
      lease = undefined;
      await runtime?.release();
    };
    let result: HostProcessResult;
    try {
      runtime = await admitManagedRuntime(captured);
      const probeCancellation = new AbortController();
      const timeout = setTimeout(() => probeCancellation.abort(), 10_000);
      try {
        const probe = await runProcess(
          {
            executableHandle: runtime.executableHandle,
            launchBytes: Buffer.alloc(0),
            tempRoot: runtime.tempRoot,
            signal: AbortSignal.any([captured.signal, probeCancellation.signal]),
            cancellationKillGraceMs: 100,
            postKillCloseGraceMs: HOST_POST_KILL_CLOSE_GRACE_MS,
          },
          {
            args: ['--list-runtimes'],
            argv0: runtime.executablePath,
            environment: managedChildEnvironment(runtime.tempRoot),
            probe: true,
          },
        );
        if (probe.exitCode !== 0 || probeCancellation.signal.aborted)
          throw new ManagedRuntimePrerequisiteError();
        runtime.validateProbe(probe.completionBytes);
      } finally {
        clearTimeout(timeout);
      }
      probing = false;
      lease = await acquireVerifiedManagedTreeLease(captured.tree, captured.signal);
      await validateManagedLoadingConfiguration(lease);
      await runtime.validate();
      const stem = '/proc/self/fd/4/agentic-pr-review/AgenticPrReview.Runtime';
      result = await runProcess(
        { ...captured, executableHandle: runtime.executableHandle, tempRoot: runtime.tempRoot },
        {
          args: [
            'exec',
            '--runtimeconfig',
            stem + '.runtimeconfig.json',
            '--depsfile',
            stem + '.deps.json',
            '--fx-version',
            runtime.frameworkVersion,
            '--roll-forward',
            'Disable',
            stem + '.dll',
            'run',
          ],
          argv0: runtime.executablePath,
          environment: managedChildEnvironment(runtime.tempRoot, captured.requestBudgetProfile),
          directoryFd: lease.directoryFd,
        },
      );
    } catch (caught) {
      if (caught instanceof HostProcessTerminationUnconfirmedError && caught.terminationConfirmed) {
        const settlement = caught.terminationConfirmed.then(release);
        throw new ManagedHostProcessTerminationUnconfirmedError(
          caught.terminationConfirmed,
          settlement,
        );
      }
      const error =
        probing && !captured.signal.aborted && !(caught instanceof ManagedRuntimePrerequisiteError)
          ? new ManagedRuntimePrerequisiteError()
          : caught;
      try {
        await release();
      } catch (cleanupError) {
        if (error instanceof Error)
          Object.defineProperty(error, 'cleanupError', { value: cleanupError });
      }
      throw error;
    }
    await release();
    return result;
  } finally {
    captured.launchBytes.fill(0);
  }
}

export function managedChildEnvironment(
  tempRoot: string,
  profile?: TrustedProofRequestBudgetProfile,
): NodeJS.ProcessEnv {
  return { ...closedChildEnvironment(tempRoot, profile), DOTNET_EnableDiagnostics: '0' };
}

interface ProcessLaunch {
  readonly args?: readonly string[];
  readonly argv0?: string;
  readonly environment?: NodeJS.ProcessEnv;
  readonly directoryFd?: number;
  readonly probe?: boolean;
}

async function runProcess(
  request: HostProcessRequest,
  launch: ProcessLaunch,
): Promise<HostProcessResult> {
  if (request.signal.aborted) fail('wrapper_cancelled_before_spawn');
  const cancellationKillGraceMs = duration(
    request.cancellationKillGraceMs ?? HOST_CANCELLATION_RECONCILIATION_GRACE_MS,
  );
  const postKillCloseGraceMs = duration(
    request.postKillCloseGraceMs ?? HOST_POST_KILL_CLOSE_GRACE_MS,
  );
  let child: ReturnType<typeof spawn>;
  try {
    child = spawn(`/proc/self/fd/${HOST_EXECUTABLE_FD}`, [...(launch.args ?? [])], {
      ...(launch.argv0 === undefined ? {} : { argv0: launch.argv0 }),
      cwd: request.tempRoot,
      env:
        launch.environment ??
        closedChildEnvironment(request.tempRoot, request.requestBudgetProfile),
      shell: false,
      windowsHide: true,
      stdio: [
        'pipe',
        'pipe',
        'pipe',
        request.executableHandle.fd,
        ...(launch.directoryFd === undefined ? [] : [launch.directoryFd]),
      ],
    });
  } catch {
    return fail('wrapper_host_process_failed');
  }
  let spawned = false;
  let closeObserved = false;
  let cancellationForwarded = false;
  let escalation: NodeJS.Timeout | undefined;
  let closeDeadline: NodeJS.Timeout | undefined;
  let rejectUnconfirmed!: (error: HostProcessTerminationUnconfirmedError) => void;
  const unconfirmedPromise = new Promise<never>((_resolve, reject) => {
    rejectUnconfirmed = reject;
  });
  const closePromise = new Promise<{
    readonly code: number | null;
    readonly signal: NodeJS.Signals | null;
  }>((resolve) => {
    child.once('close', (code, signal) => {
      closeObserved = true;
      resolve({ code, signal });
    });
  });
  let rejectProcessError!: (error: Error) => void;
  const processErrorPromise = new Promise<never>((_resolve, reject) => {
    rejectProcessError = reject;
  });
  const onSpawn = (): void => {
    spawned = true;
  };
  const onError = (): void => {
    rejectProcessError(new Error('wrapper_host_process_failed'));
  };
  child.once('spawn', onSpawn);
  child.once('error', onError);
  const armCloseDeadline = (): void => {
    if (closeDeadline || closeObserved) return;
    closeDeadline = setTimeout(
      () =>
        rejectUnconfirmed(new HostProcessTerminationUnconfirmedError(closePromise.then(() => {}))),
      postKillCloseGraceMs,
    );
  };
  const finalKill = (): void => {
    if (closeObserved) return;
    if (child.exitCode === null && child.signalCode === null) {
      try {
        child.kill('SIGKILL');
      } catch {
        // The bounded close deadline remains authoritative when signal delivery fails.
      }
    }
    armCloseDeadline();
  };
  const forwardCancellation = (): void => {
    if (cancellationForwarded) return;
    cancellationForwarded = true;
    try {
      child.kill('SIGTERM');
    } catch {
      // Final escalation and its close deadline still provide the fixed liveness bound.
    }
    escalation = setTimeout(finalKill, cancellationKillGraceMs);
  };
  request.signal.addEventListener('abort', forwardCancellation, { once: true });
  if (request.signal.aborted) forwardCancellation();
  const outputPromise = launch.probe
    ? readProbeOutput(child.stdout!)
    : readSingleFrame(child.stdout!, H1_MAXIMUM_COMPLETION_DOCUMENT_BYTES);
  const stderrPromise = readTrustedProofStderr(child.stderr!, request.requestBudgetProfile);
  try {
    const inputPromise = launch.probe
      ? finished(child.stdin!.end())
      : writePrivateLaunchFrame(child.stdin!, request.launchBytes);
    const [, completionBytes, admittedStderr, closed] = await Promise.race([
      Promise.all([inputPromise, outputPromise, stderrPromise, closePromise]),
      processErrorPromise,
      unconfirmedPromise,
    ]);
    if (closed.signal !== null || closed.code === null) fail('wrapper_host_process_failed');
    return {
      completionBytes,
      exitCode: closed.code,
      trustedProofBudgetReceiptLines: admittedStderr.budgetReceiptLines,
      ...(admittedStderr.stateReconciliationDiagnosticLine === undefined
        ? {}
        : {
            trustedProofStateReconciliationDiagnosticLine:
              admittedStderr.stateReconciliationDiagnosticLine,
          }),
    };
  } catch (error) {
    if (error instanceof HostProcessTerminationUnconfirmedError) throw error;
    if (!spawned) return fail('wrapper_host_process_failed');
    if (!closeObserved) {
      finalKill();
      await Promise.race([closePromise, unconfirmedPromise]);
    }
    return fail('wrapper_host_process_failed');
  } finally {
    if (escalation) clearTimeout(escalation);
    if (closeDeadline) clearTimeout(closeDeadline);
    request.signal.removeEventListener('abort', forwardCancellation);
    child.off('spawn', onSpawn);
    child.off('error', onError);
  }
}

/**
 * Drains private Host stderr without forwarding it. Only the two fixed R4
 * request-budget records and the bounded state-reconciliation diagnostic are
 * admitted. Every record is parsed and reserialized so arbitrary Host text can
 * never become public workflow output.
 */
export async function readTrustedProofBudgetReceiptLines(
  stream: Readable,
  profile: TrustedProofRequestBudgetProfile | undefined,
  maximumBytes = HOST_STDERR_CAPTURE_MAXIMUM_BYTES,
): Promise<readonly string[]> {
  return (await readTrustedProofStderr(stream, profile, maximumBytes)).budgetReceiptLines;
}

interface TrustedProofStderr {
  readonly budgetReceiptLines: readonly string[];
  readonly stateReconciliationDiagnosticLine?: string;
}

export async function readTrustedProofStderr(
  stream: Readable,
  profile: TrustedProofRequestBudgetProfile | undefined,
  maximumBytes = HOST_STDERR_CAPTURE_MAXIMUM_BYTES,
): Promise<TrustedProofStderr> {
  const empty: TrustedProofStderr = { budgetReceiptLines: [] };
  if (!Number.isSafeInteger(maximumBytes) || maximumBytes < 1) return empty;
  const expected = profile === undefined ? undefined : trustedProofHostReceiptProfile(profile);
  const chunks: Buffer[] = [];
  let total = 0;
  let overflow = false;
  for await (const value of stream) {
    const chunk = Buffer.from(value as Uint8Array);
    total += chunk.byteLength;
    if (total <= maximumBytes) chunks.push(chunk);
    else overflow = true;
  }
  if (overflow || expected === undefined) return empty;

  let decoded: string;
  try {
    decoded = new TextDecoder('utf-8', { fatal: true }).decode(Buffer.concat(chunks, total));
  } catch {
    return empty;
  }

  let github: string | undefined;
  let control: string | undefined;
  let budgetInvalid = false;
  let diagnostic: string | undefined;
  let diagnosticInvalid = false;
  for (const raw of decoded.split('\n')) {
    const line = raw.endsWith('\r') ? raw.slice(0, -1) : raw;
    if (line.startsWith(GITHUB_REQUEST_BUDGET_PREFIX)) {
      if (github !== undefined) {
        budgetInvalid = true;
        continue;
      }
      github = canonicalGitHubRequestBudget(line, expected);
      if (github === undefined) budgetInvalid = true;
    } else if (line.startsWith(CONTROL_REQUEST_BUDGET_PREFIX)) {
      if (control !== undefined) {
        budgetInvalid = true;
        continue;
      }
      control = canonicalControlRequestBudget(line, expected);
      if (control === undefined) budgetInvalid = true;
    } else if (line.startsWith(STATE_RECONCILIATION_DIAGNOSTIC_PREFIX)) {
      if (diagnostic !== undefined || diagnosticInvalid) {
        diagnostic = undefined;
        diagnosticInvalid = true;
        continue;
      }
      diagnostic = canonicalStateReconciliationDiagnostic(line);
      if (diagnostic === undefined) diagnosticInvalid = true;
    }
  }

  return {
    budgetReceiptLines:
      budgetInvalid || github === undefined || control === undefined
        ? []
        : [`${github}\n`, `${control}\n`],
    ...(diagnosticInvalid || diagnostic === undefined
      ? {}
      : { stateReconciliationDiagnosticLine: `${diagnostic}\n` }),
  };
}

export function canonicalStateReconciliationDiagnostic(line: string): string | undefined {
  const value = parseRecord(line.slice(STATE_RECONCILIATION_DIAGNOSTIC_PREFIX.length));
  if (
    value === undefined ||
    !hasExactKeys(value, [
      'owner',
      'outcome',
      'exact_readback',
      'observations',
      'terminal',
      'schedule_index',
    ]) ||
    ![
      'locator_root',
      'lineage_head',
      'lineage_intent',
      'candidate',
      'publication_intent',
      'acceptance',
      'publication_failure',
      'abandonment',
      'reset',
      'expiry_transition',
      'cleanup',
    ].includes(value.owner as string) ||
    !['committed', 'outcome_unknown', 'not_committed', 'reconcile_only'].includes(
      value.outcome as string,
    ) ||
    !['matched', 'failed', 'not_available', 'not_applicable'].includes(
      value.exact_readback as string,
    ) ||
    !boundedInteger(value.observations, 0, 32) ||
    ![
      'not_committed',
      'target_absent',
      'unavailable',
      'incomplete',
      'conflict',
      'authentication_failed',
      'key_unavailable',
      'retention_failed',
      'cleanup_failed',
      'cancelled',
      'invalid',
    ].includes(value.terminal as string) ||
    !boundedInteger(value.schedule_index, 0, 2)
  ) {
    return undefined;
  }
  return (
    STATE_RECONCILIATION_DIAGNOSTIC_PREFIX +
    JSON.stringify({
      owner: value.owner,
      outcome: value.outcome,
      exact_readback: value.exact_readback,
      observations: value.observations,
      terminal: value.terminal,
      schedule_index: value.schedule_index,
    })
  );
}

function canonicalGitHubRequestBudget(
  line: string,
  expected: ReturnType<typeof trustedProofHostReceiptProfile>,
): string | undefined {
  const value = parseRecord(line.slice(GITHUB_REQUEST_BUDGET_PREFIX.length));
  if (
    value === undefined ||
    !hasExactKeys(value, [
      'authenticated_rest_requests',
      'authenticated_rest_limit',
      'anonymous_codeload_requests',
      'anonymous_codeload_limit',
      'rejected_requests',
      'measurement_only',
      'invalid_remaining_header',
      'terminal_rate_limited',
      'low_remaining_guard',
      'remaining_tail_reserve',
      'host_head_source_rest',
      'host_other_github_rest',
    ]) ||
    !boundedInteger(value.authenticated_rest_requests, 0, 256) ||
    value.authenticated_rest_limit !== 256 ||
    !boundedInteger(value.anonymous_codeload_requests, 0, 1) ||
    value.anonymous_codeload_limit !== 1 ||
    value.rejected_requests !== 0 ||
    value.measurement_only !== expected.measurementOnly ||
    value.invalid_remaining_header !== false ||
    value.terminal_rate_limited !== false ||
    value.low_remaining_guard !== false ||
    value.remaining_tail_reserve !== expected.remainingTailReserve ||
    !validGitHubBudgetRole(value.host_head_source_rest, expected.hostHeadSourceRestTail) ||
    !validGitHubBudgetRole(value.host_other_github_rest, expected.hostOtherGitHubRestTail) ||
    value.host_head_source_rest.raw + value.host_other_github_rest.raw !==
      value.authenticated_rest_requests ||
    value.host_head_source_rest.primary_rate_limited !== 0 ||
    value.host_head_source_rest.secondary_rate_limited !== 0 ||
    value.host_head_source_rest.combined_rate_limited !== 0 ||
    value.host_head_source_rest.invalid_rate_headers !== 0 ||
    value.host_other_github_rest.primary_rate_limited !== 0 ||
    value.host_other_github_rest.secondary_rate_limited !== 0 ||
    value.host_other_github_rest.combined_rate_limited !== 0 ||
    value.host_other_github_rest.invalid_rate_headers !== 0
  ) {
    return undefined;
  }
  return (
    GITHUB_REQUEST_BUDGET_PREFIX +
    JSON.stringify({
      authenticated_rest_requests: value.authenticated_rest_requests,
      authenticated_rest_limit: value.authenticated_rest_limit,
      anonymous_codeload_requests: value.anonymous_codeload_requests,
      anonymous_codeload_limit: value.anonymous_codeload_limit,
      rejected_requests: value.rejected_requests,
      measurement_only: value.measurement_only,
      invalid_remaining_header: value.invalid_remaining_header,
      terminal_rate_limited: value.terminal_rate_limited,
      low_remaining_guard: value.low_remaining_guard,
      remaining_tail_reserve: value.remaining_tail_reserve,
      host_head_source_rest: canonicalGitHubBudgetRole(value.host_head_source_rest),
      host_other_github_rest: canonicalGitHubBudgetRole(value.host_other_github_rest),
    })
  );
}

interface GitHubBudgetRole {
  readonly raw: number;
  readonly primary: number;
  readonly not_modified: number;
  readonly secondary_points: number;
  readonly permission: number;
  readonly primary_rate_limited: number;
  readonly secondary_rate_limited: number;
  readonly combined_rate_limited: number;
  readonly invalid_rate_headers: number;
  readonly remaining_tail_required: number;
}

function validGitHubBudgetRole(value: unknown, expectedTail: number): value is GitHubBudgetRole {
  if (
    value === null ||
    typeof value !== 'object' ||
    Array.isArray(value) ||
    !hasExactKeys(value as Record<string, unknown>, [
      'raw',
      'primary',
      'not_modified',
      'secondary_points',
      'permission',
      'primary_rate_limited',
      'secondary_rate_limited',
      'combined_rate_limited',
      'invalid_rate_headers',
      'remaining_tail_required',
    ])
  ) {
    return false;
  }
  const role = value as Record<string, unknown>;
  return (
    boundedInteger(role.raw, 0, 256) &&
    boundedInteger(role.primary, 0, 256) &&
    boundedInteger(role.not_modified, 0, 256) &&
    boundedInteger(role.secondary_points, 0, 256 * 5) &&
    boundedInteger(role.permission, 0, 256) &&
    boundedInteger(role.primary_rate_limited, 0, 256) &&
    boundedInteger(role.secondary_rate_limited, 0, 256) &&
    boundedInteger(role.combined_rate_limited, 0, 256) &&
    boundedInteger(role.invalid_rate_headers, 0, 256) &&
    role.remaining_tail_required === expectedTail &&
    role.raw === role.primary + role.not_modified &&
    role.secondary_points >= role.raw &&
    role.secondary_points <= role.raw * 5 &&
    (role.secondary_points - role.raw) % 4 === 0 &&
    role.permission +
      role.primary_rate_limited +
      role.secondary_rate_limited +
      role.combined_rate_limited +
      role.invalid_rate_headers <=
      role.primary
  );
}

function canonicalGitHubBudgetRole(value: GitHubBudgetRole): GitHubBudgetRole {
  return {
    raw: value.raw,
    primary: value.primary,
    not_modified: value.not_modified,
    secondary_points: value.secondary_points,
    permission: value.permission,
    primary_rate_limited: value.primary_rate_limited,
    secondary_rate_limited: value.secondary_rate_limited,
    combined_rate_limited: value.combined_rate_limited,
    invalid_rate_headers: value.invalid_rate_headers,
    remaining_tail_required: value.remaining_tail_required,
  };
}

function canonicalControlRequestBudget(
  line: string,
  expected: ReturnType<typeof trustedProofHostReceiptProfile>,
): string | undefined {
  const value = parseRecord(line.slice(CONTROL_REQUEST_BUDGET_PREFIX.length));
  if (
    value === undefined ||
    !hasExactKeys(value, [
      'consumed',
      'limit',
      'primary',
      'not_modified',
      'secondary_points',
      'mutation_count',
      'remaining_tail_required',
      'remaining_tail_reserve',
      'permission_denied',
      'primary_rate_limited',
      'secondary_rate_limited',
      'combined_rate_limited',
      'invalid_remaining_header',
      'measurement_only',
      'rate_limited',
    ]) ||
    !boundedInteger(value.consumed, 0, 64) ||
    value.limit !== 64 ||
    !boundedInteger(value.primary, 0, 64) ||
    !boundedInteger(value.not_modified, 0, 64) ||
    !boundedInteger(value.secondary_points, 0, 64 * 5) ||
    !boundedInteger(value.mutation_count, 0, 64) ||
    value.remaining_tail_required !== expected.trustedControlRestTail ||
    value.remaining_tail_reserve !== expected.remainingTailReserve ||
    !boundedInteger(value.permission_denied, 0, 64) ||
    !boundedInteger(value.primary_rate_limited, 0, 64) ||
    !boundedInteger(value.secondary_rate_limited, 0, 64) ||
    !boundedInteger(value.combined_rate_limited, 0, 64) ||
    value.consumed !== value.primary + value.not_modified ||
    value.secondary_points !== value.consumed + 4 * value.mutation_count ||
    value.mutation_count > value.consumed ||
    value.permission_denied > value.primary ||
    value.primary_rate_limited +
      value.secondary_rate_limited +
      value.combined_rate_limited +
      value.permission_denied >
      value.primary ||
    value.invalid_remaining_header !== false ||
    value.measurement_only !== expected.measurementOnly ||
    value.rate_limited !== false ||
    value.primary_rate_limited !== 0 ||
    value.secondary_rate_limited !== 0 ||
    value.combined_rate_limited !== 0
  ) {
    return undefined;
  }
  return (
    CONTROL_REQUEST_BUDGET_PREFIX +
    JSON.stringify({
      consumed: value.consumed,
      limit: value.limit,
      primary: value.primary,
      not_modified: value.not_modified,
      secondary_points: value.secondary_points,
      mutation_count: value.mutation_count,
      remaining_tail_required: value.remaining_tail_required,
      remaining_tail_reserve: value.remaining_tail_reserve,
      permission_denied: value.permission_denied,
      primary_rate_limited: value.primary_rate_limited,
      secondary_rate_limited: value.secondary_rate_limited,
      combined_rate_limited: value.combined_rate_limited,
      invalid_remaining_header: value.invalid_remaining_header,
      measurement_only: value.measurement_only,
      rate_limited: value.rate_limited,
    })
  );
}

function parseRecord(text: string): Record<string, unknown> | undefined {
  try {
    const value: unknown = JSON.parse(text);
    return value !== null && typeof value === 'object' && !Array.isArray(value)
      ? (value as Record<string, unknown>)
      : undefined;
  } catch {
    return undefined;
  }
}

function hasExactKeys(value: Record<string, unknown>, expected: readonly string[]): boolean {
  return Object.keys(value).sort().join('\0') === [...expected].sort().join('\0');
}

function boundedInteger(value: unknown, minimum: number, maximum: number): value is number {
  return (
    Number.isSafeInteger(value) && (value as number) >= minimum && (value as number) <= maximum
  );
}

export function encodeFrame(document: Uint8Array): Buffer {
  if (document.byteLength < 1 || document.byteLength > 0xffff_ffff) {
    fail('wrapper_frame_invalid');
  }
  const frame = Buffer.allocUnsafe(4 + document.byteLength);
  frame.writeUInt32BE(document.byteLength, 0);
  frame.set(document, 4);
  return frame;
}

/** The write callback, including late failure after an unconfirmed close, owns the frame. */
export async function writePrivateLaunchFrame(
  stream: Writable,
  document: Uint8Array,
): Promise<void> {
  const frame = encodeFrame(document);
  const completion = finished(stream);
  const writing = new Promise<void>((resolve, reject) => {
    try {
      stream.write(frame, (error) => {
        frame.fill(0);
        if (error) reject(error);
        else resolve();
      });
    } catch (error) {
      frame.fill(0);
      reject(error);
    }
  });
  const settlement = Promise.all([writing, completion]);
  // end() can throw while an admitted write is still pending. Keep its cleanup alive.
  void settlement.catch(() => {});
  stream.end();
  await settlement;
}

export async function readSingleFrame(stream: Readable, maximumBytes: number): Promise<Buffer> {
  const chunks: Buffer[] = [];
  let total = 0;
  const header = Buffer.alloc(4);
  let headerBytes = 0;
  let declared: number | undefined;
  for await (const value of stream) {
    const chunk = Buffer.from(value as Uint8Array);
    total += chunk.byteLength;
    if (total > maximumBytes + 4) fail('wrapper_frame_invalid');
    if (headerBytes < 4) {
      const take = Math.min(4 - headerBytes, chunk.length);
      chunk.copy(header, headerBytes, 0, take);
      headerBytes += take;
      if (headerBytes === 4) {
        declared = header.readUInt32BE(0);
        if (declared < 1 || declared > maximumBytes) fail('wrapper_frame_invalid');
      }
    }
    if (declared !== undefined && total > declared + 4) fail('wrapper_frame_invalid');
    chunks.push(chunk);
  }
  const frame = Buffer.concat(chunks, total);
  if (frame.byteLength < 4) fail('wrapper_frame_invalid');
  declared = frame.readUInt32BE(0);
  if (declared < 1 || declared > maximumBytes || frame.byteLength !== declared + 4) {
    fail('wrapper_frame_invalid');
  }
  return frame.subarray(4);
}

export function closedChildEnvironment(
  tempRoot: string,
  requestBudgetProfile?: TrustedProofRequestBudgetProfile,
): NodeJS.ProcessEnv {
  return {
    TMPDIR: tempRoot,
    NO_COLOR: '1',
    DOTNET_NOLOGO: '1',
    DOTNET_CLI_TELEMETRY_OPTOUT: '1',
    ...(requestBudgetProfile === undefined
      ? {}
      : { AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE: requestBudgetProfile }),
  };
}

function duration(value: number): number {
  if (!Number.isSafeInteger(value) || value < 1) fail('wrapper_host_process_failed');
  return value;
}

async function readProbeOutput(stream: Readable): Promise<Buffer> {
  const chunks: Buffer[] = [];
  let total = 0;
  for await (const value of stream) {
    const chunk = Buffer.from(value as Uint8Array);
    if ((total += chunk.length) > 64 * 1024) throw new ManagedRuntimePrerequisiteError();
    chunks.push(chunk);
  }
  return Buffer.concat(chunks, total);
}
