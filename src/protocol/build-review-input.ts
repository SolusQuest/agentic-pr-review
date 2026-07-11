/**
 * ReviewInputV1 builder (M1 test-only; wired in M2 via #33).
 *
 * Pure function: no filesystem, no process invocation, no globals.
 * Produces a `ReviewInputV1` from the existing host structures documented in
 * `src/protocol/review-input.ts` (ReviewTarget / ActionConfig / LoadedBlock / RestoredState).
 *
 * See issue #18 for scope and design decisions. Notable rules:
 * - `config` is a `Pick<>` subset that excludes credential- and debug-control-shaped fields.
 *   Builder receives already-resolved config values; it does not compute defaults.
 * - `previousFindingFingerprints` and `existingCommentFingerprints` are two independent
 *   inputs and MUST NOT be reused for each other's field.
 * - `repository` is the authoritative host repository; the builder does not parse
 *   `target.headRepoFullName`.
 * - Bounded patch: truncation is strict `>` (equality is not truncated); missing patch
 *   is omitted (never emitted as `{}`).
 * - Path safety is fail-closed: unsafe paths in `ChangedFile.filename` propagate as-is
 *   and are rejected by `validateReviewInputV1`; the builder does not silently normalize.
 */

import {
  type ActionConfig,
  type LoadedBlock,
  type Phase,
  type ReviewTarget,
  type RestoredState,
} from '../types.js';
import { sha256 } from '../utils.js';
import type {
  ReviewInputBoundedPatchV1,
  ReviewInputChangedFileV1,
  ReviewInputHostOptionsV1,
  ReviewInputV1,
} from './review-input.js';

/**
 * Host-safe subset of `ActionConfig` accepted by `buildReviewInputV1`.
 *
 * Excludes credential- and debug-control-shaped fields (`githubToken`, `apiKey`,
 * `debugAcknowledgement`, `debugCaptureRawApiBodies`, etc.). Callers must pass
 * only these fields; sensitive fields must not enter the builder at the type layer.
 */
export type BuildReviewInputConfig = Pick<
  ActionConfig,
  | 'runtimeProvider'
  | 'toolMode'
  | 'maxFindings'
  | 'maxPatchChars'
  | 'maxContextChars'
  | 'maxReviewChars'
  | 'stateKey'
  | 'inlineComments'
  | 'maxInlineComments'
  | 'inlineMinSeverity'
  | 'inlineMinConfidence'
  | 'instructions'
>;

export interface BuildReviewInputParams {
  target: ReviewTarget;
  config: BuildReviewInputConfig;
  phase: Phase;
  blocks: LoadedBlock[];
  restoredState: RestoredState | null;
  /**
   * Previous review state summary (D10). #18 does not claim `RestoredState`
   * currently persists such a list; callers pass `[]` when unavailable.
   * MUST NOT be reused as `existingCommentFingerprints`.
   */
  previousFindingFingerprints: string[];
  /**
   * Existing inline comment / duplicate-evidence summary. MUST NOT be reused
   * as `previousFindingFingerprints`.
   */
  existingCommentFingerprints: string[];
  /** Authoritative host repository; not parsed from `target.headRepoFullName`. */
  repository: { owner: string; name: string };
  /** Opaque runtime version request; null when unspecified. */
  requestedRuntimeVersion?: string | null;
}

/**
 * Build a sanitized `ReviewInputV1` from existing host structures.
 *
 * The result is intended to be validated with `validateReviewInputV1` before use;
 * schema validation is authoritative for shape and path safety. This builder never
 * rewrites unsafe paths — it lets the schema reject them (fail-closed).
 */
export function buildReviewInputV1(params: BuildReviewInputParams): ReviewInputV1 {
  const {
    target,
    config,
    phase,
    blocks,
    restoredState,
    previousFindingFingerprints,
    existingCommentFingerprints,
    repository,
    requestedRuntimeVersion = null,
  } = params;

  const options: ReviewInputHostOptionsV1 = {
    toolMode: config.toolMode,
    maxFindings: config.maxFindings,
    maxPatchChars: config.maxPatchChars,
    maxContextChars: config.maxContextChars,
    maxReviewChars: config.maxReviewChars,
    inlineComments: {
      enabled: config.inlineComments,
      maxComments: config.maxInlineComments,
      minSeverity: config.inlineMinSeverity,
      minConfidence: config.inlineMinConfidence,
    },
  };

  const changedFiles: ReviewInputChangedFileV1[] = target.changedFiles.map((file) => {
    const changedFile: ReviewInputChangedFileV1 = {
      path: file.filename,
      status: file.status,
      additions: file.additions,
      deletions: file.deletions,
      changes: file.changes,
    };
    if (file.previousFilename !== undefined) {
      changedFile.previousPath = file.previousFilename;
    }
    if (typeof file.patch === 'string') {
      changedFile.patch = buildBoundedPatch(file.patch, config.maxPatchChars);
    }
    return changedFile;
  });

  const contextDocuments = blocks.map((block) => ({ name: block.name, text: block.text }));

  const input: ReviewInputV1 = {
    protocolVersion: 1,
    requestedRuntimeVersion,
    host: {
      repository,
      review: {
        phase,
        baseSha: target.baseSha,
        headSha: target.headSha,
        runtimeProvider: config.runtimeProvider,
        ...(config.stateKey !== undefined ? { stateKey: config.stateKey } : {}),
      },
      options,
    },
    subject: {
      pullRequest: {
        // Schema requires >= 1; synthetic-fixture targets carry no PR number, so we
        // emit 1 as a documented synthetic placeholder rather than fail validation.
        number: target.prNumber ?? 1,
        title: target.title,
        body: target.body,
        baseRef: target.baseRef,
        headRef: target.headRef,
        draft: target.draft,
      },
      changedFiles,
      ...(contextDocuments.length > 0 ? { contextDocuments } : {}),
      ...(config.instructions !== undefined ? { policyText: config.instructions } : {}),
    },
    previousState: buildPreviousState(restoredState, previousFindingFingerprints, phase),
    commentEvidence: { existingFindingFingerprints: [...existingCommentFingerprints] },
  };

  return input;
}

function buildPreviousState(
  restoredState: RestoredState | null,
  previousFindingFingerprints: string[],
  currentPhase: Phase,
) {
  if (restoredState === null) {
    return {
      present: false,
      findingFingerprints: [...previousFindingFingerprints],
    };
  }
  const state: ReviewInputV1['previousState'] = {
    present: true,
    findingFingerprints: [...previousFindingFingerprints],
  };
  if (restoredState.reviewedHeadSha !== undefined) {
    state.reviewedHeadSha = restoredState.reviewedHeadSha;
  }
  // `previousState.phase` (the phase that produced the prior review) is not
  // exposed by `RestoredState` today, so it is omitted. #18 does not invent a
  // mapping (D10).
  void currentPhase;
  // previousState.lineage is intentionally omitted (D10): RestoredState.lineageTotals
  // does not currently expose a stable review-count source.
  return state;
}

function buildBoundedPatch(rawPatch: string, maxPatchChars: number): ReviewInputBoundedPatchV1 {
  const truncated = rawPatch.length > maxPatchChars;
  const text = truncated ? rawPatch.slice(0, maxPatchChars) : rawPatch;
  return {
    text,
    truncated,
    sha256: sha256(text),
    maxChars: maxPatchChars,
  };
}
