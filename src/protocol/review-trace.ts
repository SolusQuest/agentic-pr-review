/**
 * ReviewTraceV1 - minimal sanitized trace contract.
 *
 * Canonical source of truth: protocol/schemas/review-trace.v1.json (JSON Schema draft-07).
 * This TypeScript interface is developer ergonomics only; the JSON Schema is authoritative.
 *
 * The trace carries sanitized execution evidence for deterministic validation and future
 * replay. It is runtime-produced; the host stores, uploads, and verifies trace files but
 * does not author their content. The trace is optional - a review can complete without one.
 *
 * Hash chain:
 *   ReviewInputV1  --(inputSha256)-->  ReviewTraceV1
 *   ReviewResultV1 --(resultSha256)--> ReviewTraceV1
 *   ReviewResultV1.trace.sha256 = SHA-256 of trace file bytes (result points to trace)
 *   ReviewTraceV1.resultSha256  = SHA-256 of result file bytes (trace points back to result)
 *
 * Privacy: trace payload contains no raw provider bodies, secrets, auth headers, raw prompts,
 * or unbounded tool output. Closed shapes reject credential-shaped fields. JSON Schema cannot
 * guarantee arbitrary secret-value detection inside allowed strings; producer-side sanitization
 * is the runtime's responsibility (#18+).
 */

import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Ajv, type ErrorObject } from 'ajv';

export type ReviewTraceModeV1 = 'deterministic-fixture' | 'live-provider' | 'skipped';

export interface ReviewTraceProviderV1 {
  name?: string;
  /** Bounded sanitized metadata. Producers must not put endpoints, tokens, deployment URLs, or private runner paths here. */
  model?: string;
  requestCount?: number;
}

export interface ReviewTraceUsageV1 {
  inputTokens: number;
  cacheReadInputTokens: number;
  cacheCreationInputTokens: number;
  outputTokens: number;
  recordsObserved: number;
}

export interface ReviewTraceToolCallV1 {
  name: string;
  status: 'ok' | 'error' | 'skipped';
  durationMs?: number;
  errorCode?: string;
}

export interface ReviewTraceDiagnosticV1 {
  code: string;
  message: string;
  level: 'info' | 'warning' | 'error';
}

export interface ReviewTraceV1 {
  /** Protocol-generation version, shared across input/result/trace. Exact match required. */
  protocolVersion: 1;
  /** Opaque runtime version supplied by the runtime. */
  runtimeVersion: string;
  /** Required. Lowercase hex SHA-256 of the exact ReviewInputV1 file bytes consumed by the runtime. */
  inputSha256: string;
  /** Optional. Lowercase hex SHA-256 of the exact ReviewResultV1 file bytes produced by the runtime. Absent on failure path. */
  resultSha256?: string;
  /** Execution context. */
  mode: ReviewTraceModeV1;
  /** Optional metadata expected only for deterministic-fixture mode. */
  fixture?: string;
  /** Optional sanitized provider metadata. */
  provider?: ReviewTraceProviderV1;
  /** Optional ISO-8601 timestamp. No format validation. */
  startedAt?: string;
  /** Optional ISO-8601 timestamp. No format validation. */
  completedAt?: string;
  /** Optional current-run usage. Excludes lineage and budget status. */
  usage?: ReviewTraceUsageV1;
  /** Required array of sanitized tool-call summaries. Empty array = no summaries present. */
  toolCalls: ReviewTraceToolCallV1[];
  /** Required array of sanitized non-blocking notes. */
  warnings: string[];
  /** Required array of sanitized, bounded diagnostics. */
  diagnostics: ReviewTraceDiagnosticV1[];
}

export interface ReviewTraceValidationResult {
  ok: boolean;
  errors?: string[];
}

const here = dirname(fileURLToPath(import.meta.url));
const schemaPath = join(here, '..', '..', 'protocol', 'schemas', 'review-trace.v1.json');
const schema = JSON.parse(readFileSync(schemaPath, 'utf8')) as object;

const ajv = new Ajv({ strict: true, allErrors: true });
const validateSchema = ajv.compile<ReviewTraceV1>(schema);

/**
 * Validate a value against the ReviewTraceV1 JSON Schema.
 * JSON Schema is authoritative for shape. No post-schema semantic checks are needed
 * in V1 (no cross-field constraints beyond what the schema expresses).
 */
export function validateReviewTraceV1(value: unknown): ReviewTraceValidationResult {
  if (!validateSchema(value)) {
    const errors = (validateSchema.errors ?? []).map(formatError);
    return { ok: false, errors };
  }
  return { ok: true };
}

function formatError(err: ErrorObject): string {
  const location = err.instancePath || '/';
  return `${location} ${err.message ?? 'invalid'}`;
}
