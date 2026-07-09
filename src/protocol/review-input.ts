/**
 * ReviewInputV1 - sanitized runtime input contract.
 *
 * Canonical source of truth: protocol/schemas/review-input.v1.json (JSON Schema draft-07).
 * This TypeScript interface is developer ergonomics only; the JSON Schema is authoritative.
 * Tests validate fixtures against the schema and use `satisfies ReviewInputV1` as a drift signal.
 *
 * Field-level mapping from existing action concepts (full builder is #18):
 *
 *   host.repository             <- ReviewTarget.headRepoFullName / GitHubContextLike.repo
 *   host.review.phase           <- Phase (src/types.ts) / executed phase
 *   host.review.baseSha         <- ReviewTarget.baseSha
 *   host.review.headSha         <- ReviewTarget.headSha
 *   host.review.stateKey        <- ActionConfig.stateKey
 *   host.review.runtimeProvider <- ActionConfig.runtimeProvider
 *   host.options.toolMode       <- ActionConfig.toolMode
 *   host.options.maxFindings    <- ActionConfig.maxFindings
 *   host.options.inlineComments <- InlineCommentsPolicy (derived from ActionConfig)
 *   subject.pullRequest         <- ReviewTarget (number, title, body, baseRef, headRef, draft)
 *   subject.changedFiles        <- ReviewTarget.changedFiles (ChangedFile -> changedFile)
 *   subject.contextDocuments    <- LoadedBlock[] (src/context-blocks.ts)
 *   subject.policyText          <- ActionConfig.instructions
 *   previousState               <- RestoredState (reviewedHeadSha, phase, fingerprints, lineage)
 *   commentEvidence             <- existing inline-comment finding fingerprints (runtime scan)
 */

import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Ajv, type ErrorObject } from 'ajv';

export interface ReviewInputBoundedPatchV1 {
  text: string;
  truncated: boolean;
  /** Lowercase hex SHA-256 digest of `text` (the bounded text the runtime receives). */
  sha256: string;
  maxChars: number;
}

export interface ReviewInputChangedFileV1 {
  /** Normalized POSIX repo-relative path. */
  path: string;
  /** Previous path for renamed files, or null. */
  previousPath?: string | null;
  status: string;
  additions: number;
  deletions: number;
  changes: number;
  /** Bounded patch context; omitted when no textual patch is available (e.g. binary files). */
  patch?: ReviewInputBoundedPatchV1;
}

export interface ReviewInputHostOptionsV1 {
  toolMode?: 'none' | 'readonly';
  maxFindings?: number;
  maxPatchChars?: number;
  maxContextChars?: number;
  maxReviewChars?: number;
  inlineComments?: {
    enabled?: boolean;
    maxComments?: number;
    minSeverity?: 'low' | 'medium' | 'high';
    minConfidence?: 'medium' | 'high';
  };
}

/** Trusted host-owned metadata. Runtime-provided facts cannot override these. */
export interface ReviewInputHostV1 {
  repository: { owner: string; name: string };
  review: {
    phase: 'bootstrap' | 'incremental';
    baseSha: string;
    headSha: string;
    stateKey?: string;
    runtimeProvider: 'test' | 'claude-code-cli';
  };
  /** Host-owned execution constraints, not runtime-provided facts. */
  options?: ReviewInputHostOptionsV1;
}

/** Untrusted review subject data. */
export interface ReviewInputSubjectV1 {
  pullRequest: {
    number: number;
    title: string;
    body: string;
    baseRef: string;
    headRef: string;
    draft: boolean;
  };
  changedFiles: ReviewInputChangedFileV1[];
  contextDocuments?: { name: string; text: string }[];
  policyText?: string;
}

/** Minimal previous review state summary. Excludes manifest paths, raw comments, provider responses, and debug files. */
export interface ReviewInputPreviousStateV1 {
  present: boolean;
  reviewedHeadSha?: string;
  phase?: 'bootstrap' | 'incremental';
  findingFingerprints: string[];
  lineage?: { reviewCount: number };
}

/** Minimal existing comment/duplicate-evidence summary for duplicate suppression. */
export interface ReviewInputCommentEvidenceV1 {
  existingFindingFingerprints: string[];
}

export interface ReviewInputV1 {
  /** Protocol-generation version, shared across input/result/trace. Exact match required. */
  protocolVersion: 1;
  /** Opaque runtime version request, or null when unspecified. */
  requestedRuntimeVersion: string | null;
  host: ReviewInputHostV1;
  subject: ReviewInputSubjectV1;
  previousState: ReviewInputPreviousStateV1;
  commentEvidence: ReviewInputCommentEvidenceV1;
}

export interface ReviewInputValidationResult {
  ok: boolean;
  errors?: string[];
}

const here = dirname(fileURLToPath(import.meta.url));
const schemaPath = join(here, '..', '..', 'protocol', 'schemas', 'review-input.v1.json');
const schema = JSON.parse(readFileSync(schemaPath, 'utf8')) as object;

const ajv = new Ajv({ strict: true, allErrors: true });
const validateSchema = ajv.compile<ReviewInputV1>(schema);

/**
 * Validate a value against the ReviewInputV1 JSON Schema.
 * JSON Schema is authoritative; this is the shared validation entry point.
 */
export function validateReviewInputV1(value: unknown): ReviewInputValidationResult {
  if (validateSchema(value)) {
    return { ok: true };
  }
  const errors = (validateSchema.errors ?? []).map(formatError);
  return { ok: false, errors };
}

function formatError(err: ErrorObject): string {
  const location = err.instancePath || '/';
  const additional = (err.params as { additionalProperty?: string } | undefined)
    ?.additionalProperty;
  const suffix = additional ? `: ${additional}` : '';
  return `${location} ${err.message ?? 'invalid'}${suffix}`;
}
