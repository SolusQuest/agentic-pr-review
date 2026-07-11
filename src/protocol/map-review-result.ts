/**
 * ReviewResultV1 -> runtime-owned content projection (M1 test-only; wired in M2 via #33).
 *
 * Pure function: no filesystem, no process invocation, no globals.
 *
 * This helper takes a runtime-produced `ReviewResultV1` and returns:
 * - `content`: the runtime-owned subset that a future host caller (#33) will combine
 *   with host-owned envelope facts to assemble `StructuredReviewEnvelopeV1`.
 * - `sideChannel`: `warnings`, `diagnostics`, `inputSha256`, `trace` — preserved for
 *   future publisher / metadata use, but explicitly NOT stored on the envelope.
 *
 * Scope (D5):
 * - Does NOT produce `StructuredReviewEnvelopeV1`.
 * - Does NOT accept or return host-owned facts (phase, baseSha, headSha, reviewedRange,
 *   runtimeProvider, sessionId, usageBudgetStatus, lineageTotals, stateKey, repository,
 *   toolMode).
 * - Does NOT compute fingerprints. `findingFingerprint` in `src/structured.ts` remains
 *   the sole owner; the M2 host caller invokes it.
 * - Does NOT apply `maxFindings` capping or current-review-scope filtering.
 * - Does NOT mutate the input `result`; passing through the same finding references
 *   is acceptable, but callers must not rely on object identity.
 * - Assumes the caller has already validated `result` with `validateReviewResultV1`.
 */

import type {
  ReviewResultDiagnosticV1,
  ReviewResultFindingV1,
  ReviewResultTraceV1,
  ReviewResultUsageV1,
  ReviewResultV1,
} from './review-result.js';

export interface RuntimeReviewContentV1 {
  summary: string;
  findings: ReviewResultFindingV1[];
  limitations: string[];
  usage?: ReviewResultUsageV1;
  observedTurns?: number | null;
  observedTurnSource?: 'unique_assistant_message_ids' | 'not_applicable' | 'unavailable';
}

export interface RuntimeReviewSideChannelV1 {
  /** Always an array; empty is preserved, not omitted. */
  warnings: string[];
  /** Always an array; empty is preserved, not omitted. */
  diagnostics: ReviewResultDiagnosticV1[];
  inputSha256?: string;
  trace?: ReviewResultTraceV1;
}

export interface RuntimeReviewProjectionV1 {
  content: RuntimeReviewContentV1;
  sideChannel: RuntimeReviewSideChannelV1;
}

/**
 * Project a validated `ReviewResultV1` into a runtime-owned content + side channel pair.
 * The caller is responsible for prior `validateReviewResultV1` validation.
 */
export function mapReviewResultV1ToRuntimeContent(
  result: ReviewResultV1,
): RuntimeReviewProjectionV1 {
  const content: RuntimeReviewContentV1 = {
    summary: result.summary,
    findings: result.findings,
    limitations: result.limitations,
  };
  if (result.usage !== undefined) {
    content.usage = result.usage;
  }
  if (result.observedTurns !== undefined) {
    content.observedTurns = result.observedTurns;
  }
  if (result.observedTurnSource !== undefined) {
    content.observedTurnSource = result.observedTurnSource;
  }

  const sideChannel: RuntimeReviewSideChannelV1 = {
    warnings: result.warnings,
    diagnostics: result.diagnostics,
  };
  if (result.inputSha256 !== undefined) {
    sideChannel.inputSha256 = result.inputSha256;
  }
  if (result.trace !== undefined) {
    sideChannel.trace = result.trace;
  }

  return { content, sideChannel };
}
