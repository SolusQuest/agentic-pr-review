/**
 * ReviewResultV1 - runtime result contract.
 *
 * Canonical source of truth: protocol/schemas/review-result.v1.json (JSON Schema draft-07).
 * This TypeScript interface is developer ergonomics only; the JSON Schema is authoritative.
 *
 * The result carries runtime-proposed content only. The host assembles the full
 * StructuredReviewEnvelopeV1 by combining the result with host-owned metadata.
 *
 * Field-level mapping from ReviewResultV1 to StructuredReviewEnvelopeV1 (host assembles):
 *
 *   result.summary            -> envelope.summary
 *   result.findings           -> envelope.findings (host adds fingerprint via findingFingerprint in src/structured.ts)
 *   result.limitations        -> envelope.limitations
 *   result.usage              -> envelope.usage
 *   result.observedTurns      -> envelope.observedTurns when present; null when absent
 *   result.observedTurnSource -> envelope.observedTurnSource when present; host supplies fallback when absent
 *   result.warnings           -> surfaced in host metadata (not stored in envelope)
 *   result.diagnostics        -> surfaced in host metadata (not stored in envelope)
 *   result.trace              -> host stores trace reference separately
 *   result.protocolVersion    -> validated, not stored in envelope
 *   result.runtimeVersion     -> validated; envelope uses host-owned runtimeProvider
 *   result.inputSha256        -> validated for round-trip integrity, not stored
 *
 * Host-owned (NOT from result; injected by host when assembling the envelope):
 *   schemaVersion, phase, baseSha, headSha, previousReviewedHeadSha, reviewedRange,
 *   toolMode, runtimeProvider, sessionId, lineageTotals, result.result (counts/truncation),
 *   usageBudgetStatus
 *
 * Findings do not carry fingerprint; the host computes it (consistent with #14).
 */

import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Ajv, type ErrorObject } from 'ajv';

export interface ReviewResultUsageV1 {
  inputTokens: number;
  cacheReadInputTokens: number;
  cacheCreationInputTokens: number;
  outputTokens: number;
  recordsObserved: number;
}

export interface ReviewResultDiagnosticV1 {
  code: string;
  message: string;
  level: 'info' | 'warning' | 'error';
}

export interface ReviewResultTraceV1 {
  /** Artifact-relative POSIX output reference. */
  path?: string;
  sha256?: string;
}

export interface ReviewResultFindingV1 {
  severity: 'low' | 'medium' | 'high';
  /** medium|high only; low-confidence observations are omitted by design. */
  confidence: 'medium' | 'high';
  category:
    | 'correctness'
    | 'security'
    | 'requirements'
    | 'test_coverage'
    | 'build'
    | 'performance'
    | 'maintainability'
    | 'documentation';
  title: string;
  body: string;
  evidence?: string;
  /** Repo-relative path or null for pathless findings. */
  path: string | null;
  startLine: number | null;
  endLine: number | null;
  suggestedAction?: string;
  /** Runtime preference; the publisher owns the final inline vs sticky decision. */
  inlinePreference?: 'allowed' | 'preferred' | 'avoid';
}

export interface ReviewResultV1 {
  /** Protocol-generation version, shared across input/result/trace. Exact match required. */
  protocolVersion: 1;
  /** Opaque runtime version supplied by the runtime. */
  runtimeVersion: string;
  /** Optional non-authoritative lowercase hex SHA-256 echo of the consumed input. */
  inputSha256?: string;
  summary: string;
  findings: ReviewResultFindingV1[];
  limitations: string[];
  usage?: ReviewResultUsageV1;
  observedTurns?: number | null;
  observedTurnSource?: 'unique_assistant_message_ids' | 'not_applicable' | 'unavailable';
  warnings: string[];
  diagnostics: ReviewResultDiagnosticV1[];
  trace?: ReviewResultTraceV1;
}

export interface ReviewResultValidationResult {
  ok: boolean;
  errors?: string[];
}

const here = dirname(fileURLToPath(import.meta.url));
const schemaPath = join(here, '..', '..', 'protocol', 'schemas', 'review-result.v1.json');
const schema = JSON.parse(readFileSync(schemaPath, 'utf8')) as object;

const ajv = new Ajv({ strict: true, allErrors: true });
const validateSchema = ajv.compile<ReviewResultV1>(schema);

/**
 * Validate a value against the ReviewResultV1 JSON Schema plus post-schema semantic
 * checks for cross-field location rules that JSON Schema draft-07 cannot express.
 * JSON Schema is authoritative for shape; semantic validation enforces location invariants.
 */
export function validateReviewResultV1(value: unknown): ReviewResultValidationResult {
  if (!validateSchema(value)) {
    const errors = (validateSchema.errors ?? []).map(formatError);
    return { ok: false, errors };
  }
  const semanticErrors = validateFindingLocations(value);
  if (semanticErrors.length > 0) {
    return { ok: false, errors: semanticErrors };
  }
  return { ok: true };
}

/**
 * Post-schema semantic validation for finding location cross-field rules:
 * - startLine/endLine are both-null or both-present
 * - if line values are present, path must be non-null (line requires path)
 * - when both present, startLine <= endLine
 */
function validateFindingLocations(result: ReviewResultV1): string[] {
  const errors: string[] = [];
  result.findings.forEach((finding, index) => {
    const { startLine, endLine, path } = finding;
    const hasStart = startLine !== null;
    const hasEnd = endLine !== null;
    if (hasStart !== hasEnd) {
      errors.push(`findings[${index}] startLine/endLine must be both null or both present`);
    }
    if ((hasStart || hasEnd) && path === null) {
      errors.push(`findings[${index}] path must be non-null when line values are present`);
    }
    if (startLine !== null && endLine !== null && endLine < startLine) {
      errors.push(`findings[${index}] endLine must be greater than or equal to startLine`);
    }
  });
  return errors;
}

function formatError(err: ErrorObject): string {
  const location = err.instancePath || '/';
  const additional = (err.params as { additionalProperty?: string } | undefined)
    ?.additionalProperty;
  const suffix = additional ? `: ${additional}` : '';
  return `${location} ${err.message ?? 'invalid'}${suffix}`;
}
