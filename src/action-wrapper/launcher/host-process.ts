import { spawn } from 'node:child_process';
import type { FileHandle } from 'node:fs/promises';
import type { Readable } from 'node:stream';
import { finished } from 'node:stream/promises';
import { TextDecoder } from 'node:util';

import { H1_MAXIMUM_COMPLETION_DOCUMENT_BYTES } from './contracts.js';
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

export class HostProcessTerminationUnconfirmedError extends Error {
  constructor() {
    super('wrapper_host_termination_unconfirmed');
    this.name = 'HostProcessTerminationUnconfirmedError';
  }
}

export interface HostProcessResult {
  readonly completionBytes: Buffer;
  readonly exitCode: number;
  /** Canonical, secret-free proof receipts admitted from otherwise private stderr. */
  readonly trustedProofBudgetReceiptLines: readonly string[];
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
  if (request.signal.aborted) fail('wrapper_cancelled_before_spawn');
  const cancellationKillGraceMs = duration(
    request.cancellationKillGraceMs ?? HOST_CANCELLATION_RECONCILIATION_GRACE_MS,
  );
  const postKillCloseGraceMs = duration(
    request.postKillCloseGraceMs ?? HOST_POST_KILL_CLOSE_GRACE_MS,
  );
  let child: ReturnType<typeof spawn>;
  try {
    child = spawn(`/proc/self/fd/${HOST_EXECUTABLE_FD}`, [], {
      cwd: request.tempRoot,
      env: closedChildEnvironment(request.tempRoot, request.requestBudgetProfile),
      shell: false,
      windowsHide: true,
      stdio: ['pipe', 'pipe', 'pipe', request.executableHandle.fd],
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
      () => rejectUnconfirmed(new HostProcessTerminationUnconfirmedError()),
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
  const outputPromise = readSingleFrame(child.stdout!, H1_MAXIMUM_COMPLETION_DOCUMENT_BYTES);
  const receiptPromise = readTrustedProofBudgetReceiptLines(
    child.stderr!,
    request.requestBudgetProfile,
  );
  try {
    child.stdin!.end(encodeFrame(request.launchBytes));
    const [, completionBytes, trustedProofBudgetReceiptLines, closed] = await Promise.race([
      Promise.all([finished(child.stdin!), outputPromise, receiptPromise, closePromise]),
      processErrorPromise,
      unconfirmedPromise,
    ]);
    if (closed.signal !== null || closed.code === null) fail('wrapper_host_process_failed');
    return { completionBytes, exitCode: closed.code, trustedProofBudgetReceiptLines };
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
 * request-budget records are admitted, and they are parsed and reserialized so
 * arbitrary Host text can never become public workflow output.
 */
export async function readTrustedProofBudgetReceiptLines(
  stream: Readable,
  profile: TrustedProofRequestBudgetProfile | undefined,
  maximumBytes = HOST_STDERR_CAPTURE_MAXIMUM_BYTES,
): Promise<readonly string[]> {
  if (!Number.isSafeInteger(maximumBytes) || maximumBytes < 1) return [];
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
  if (overflow || expected === undefined) return [];

  let decoded: string;
  try {
    decoded = new TextDecoder('utf-8', { fatal: true }).decode(Buffer.concat(chunks, total));
  } catch {
    return [];
  }

  let github: string | undefined;
  let control: string | undefined;
  for (const raw of decoded.split('\n')) {
    const line = raw.endsWith('\r') ? raw.slice(0, -1) : raw;
    if (line.startsWith(GITHUB_REQUEST_BUDGET_PREFIX)) {
      if (github !== undefined) return [];
      github = canonicalGitHubRequestBudget(line, expected);
      if (github === undefined) return [];
    } else if (line.startsWith(CONTROL_REQUEST_BUDGET_PREFIX)) {
      if (control !== undefined) return [];
      control = canonicalControlRequestBudget(line, expected);
      if (control === undefined) return [];
    }
  }

  return github === undefined || control === undefined ? [] : [`${github}\n`, `${control}\n`];
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
  Buffer.from(document).copy(frame, 4);
  return frame;
}

export async function readSingleFrame(stream: Readable, maximumBytes: number): Promise<Buffer> {
  const chunks: Buffer[] = [];
  let total = 0;
  for await (const value of stream) {
    const chunk = Buffer.from(value as Uint8Array);
    total += chunk.byteLength;
    if (total > maximumBytes + 4) fail('wrapper_frame_invalid');
    chunks.push(chunk);
  }
  const frame = Buffer.concat(chunks, total);
  if (frame.byteLength < 4) fail('wrapper_frame_invalid');
  const declared = frame.readUInt32BE(0);
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
