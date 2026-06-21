import {
  type ActionConfig,
  type Phase,
  type ReviewedRange,
  type ReviewTarget,
  type RuntimeLineageTotals,
  type RuntimeUsage,
  type StructuredFindingV1,
  type StructuredReviewEnvelopeV1,
} from './types.js';
import { sha256 } from './utils.js';

export type StructuredOutputStatus = 'valid' | 'extracted' | 'invalid_json' | 'schema_invalid';

export interface StructuredResultMetadata {
  inputFindingCount: number;
  postFindingCapCount: number;
  renderedFindingCount: number;
  findingsTruncated: boolean;
  truncationReason?: 'max_findings' | 'max_review_chars' | 'both';
  status: StructuredOutputStatus;
  validationError?: string;
}

export interface StructuredReviewNormalizationInput {
  modelJsonText: string;
  target: ReviewTarget;
  phase: Phase;
  previousReviewedHeadSha?: string;
  reviewedRange: ReviewedRange;
  config: Pick<ActionConfig, 'runtimeProvider' | 'toolMode'>;
  sessionId: string;
  usage: RuntimeUsage | null;
  observedTurns: number | null;
  observedTurnSource: string;
  lineageTotals: RuntimeLineageTotals;
  maxFindings: number;
}

const SEVERITIES = new Set(['low', 'medium', 'high']);
const CONFIDENCES = new Set(['medium', 'high']);
const CATEGORIES = new Set([
  'correctness',
  'security',
  'requirements',
  'test_coverage',
  'build',
  'performance',
  'maintainability',
  'documentation',
]);

export class StructuredReviewValidationError extends Error {
  constructor(
    readonly status: StructuredOutputStatus,
    readonly sanitizedDiagnostic: string,
  ) {
    super(`structured_output_${status}: ${sanitizedDiagnostic}`);
  }
}

export function normalizeStructuredReview(input: StructuredReviewNormalizationInput): {
  envelope: StructuredReviewEnvelopeV1;
  metadata: StructuredResultMetadata;
} {
  const parsed = parseModelJson(input.modelJsonText);
  const model = validateModelReviewContent(parsed.value);
  const normalizedFindings = model.findings.map((finding) => normalizeFinding(finding));
  const inputFindingCount = normalizedFindings.length;
  const cappedFindings = normalizedFindings.slice(0, input.maxFindings);
  const postFindingCapCount = cappedFindings.length;
  const findingsTruncated = inputFindingCount > cappedFindings.length;
  const truncationReason = findingsTruncated ? 'max_findings' : undefined;
  const envelope: StructuredReviewEnvelopeV1 = {
    schemaVersion: 1,
    phase: input.phase,
    baseSha: input.target.baseSha,
    headSha: input.target.headSha,
    previousReviewedHeadSha: input.previousReviewedHeadSha ?? null,
    reviewedRange: input.reviewedRange,
    toolMode: input.config.toolMode,
    runtimeProvider: input.config.runtimeProvider,
    sessionId: input.sessionId,
    summary: model.summary,
    findings: cappedFindings,
    limitations: model.limitations,
    usage: input.usage,
    observedTurns: input.observedTurns,
    observedTurnSource: input.observedTurnSource,
    lineageTotals: input.lineageTotals,
    result: {
      inputFindingCount,
      postFindingCapCount,
      renderedFindingCount: cappedFindings.length,
      findingsTruncated,
      truncationReason,
    },
  };
  return {
    envelope,
    metadata: {
      inputFindingCount,
      postFindingCapCount,
      renderedFindingCount: cappedFindings.length,
      findingsTruncated,
      truncationReason,
      status: parsed.status,
    },
  };
}

export function buildReviewedRange(input: {
  phase: Phase;
  target: ReviewTarget;
  previousReviewedHeadSha?: string;
}): ReviewedRange {
  return {
    kind: input.phase === 'incremental' ? 'incremental' : 'bootstrap',
    fromSha:
      input.phase === 'incremental'
        ? (input.previousReviewedHeadSha ?? input.target.baseSha)
        : null,
    toSha: input.target.headSha,
  };
}

function parseModelJson(text: string): { value: unknown; status: 'valid' | 'extracted' } {
  const candidates = deterministicJsonCandidates(text);
  for (const candidate of candidates) {
    try {
      return {
        value: JSON.parse(candidate.text) as unknown,
        status: candidate.status,
      };
    } catch {
      // Try the next deterministic local cleanup candidate.
    }
  }
  throw new StructuredReviewValidationError(
    'invalid_json',
    'model output is not valid JSON after deterministic cleanup',
  );
}

function deterministicJsonCandidates(
  text: string,
): Array<{ text: string; status: 'valid' | 'extracted' }> {
  const trimmed = text.trim();
  const candidates: Array<{ text: string; status: 'valid' | 'extracted' }> = trimmed
    ? [{ text: trimmed, status: 'valid' }]
    : [];
  const fenced = [...text.matchAll(/```(?:json)?\s*([\s\S]*?)```/gi)]
    .map((match) => match[1]?.trim())
    .filter((candidate): candidate is string => Boolean(candidate));
  for (const candidate of fenced) {
    candidates.push({ text: candidate, status: 'extracted' });
  }
  const seen = new Set<string>();
  return candidates.filter((candidate) => {
    if (seen.has(candidate.text)) {
      return false;
    }
    seen.add(candidate.text);
    return true;
  });
}

function validateModelReviewContent(value: unknown): {
  schemaVersion: 1;
  summary: string;
  findings: Array<Record<string, unknown>>;
  limitations: string[];
} {
  const root = requireObject(value, 'root');
  if (root.schemaVersion !== 1) {
    schemaError('schemaVersion must equal 1');
  }
  const summary = requireBoundedString(root.summary, 'summary', 4000);
  const findings = root.findings;
  if (!Array.isArray(findings)) {
    schemaError('findings must be an array');
  }
  const limitationsValue = root.limitations;
  if (!Array.isArray(limitationsValue)) {
    schemaError('limitations must be an array');
  }
  const limitations = limitationsValue.map((item, index) =>
    requireBoundedString(item, `limitations[${index}]`, 1200),
  );
  return {
    schemaVersion: 1,
    summary,
    findings: findings.map((item, index) => validateModelFinding(item, index)),
    limitations,
  };
}

function validateModelFinding(value: unknown, index: number): Record<string, unknown> {
  const finding = requireObject(value, `findings[${index}]`);
  requireEnum(finding.severity, `findings[${index}].severity`, SEVERITIES);
  requireEnum(finding.confidence, `findings[${index}].confidence`, CONFIDENCES);
  requireEnum(finding.category, `findings[${index}].category`, CATEGORIES);
  requireBoundedString(finding.title, `findings[${index}].title`, 240);
  requireBoundedString(finding.body, `findings[${index}].body`, 4000);
  validateRepoRelativePath(finding.path, `findings[${index}].path`, 500);
  requireNullablePositiveInteger(finding.startLine, `findings[${index}].startLine`);
  requireNullablePositiveInteger(finding.endLine, `findings[${index}].endLine`);
  validateLineRange(finding.startLine, finding.endLine, index);
  if (finding.suggestedAction !== undefined) {
    requireBoundedString(finding.suggestedAction, `findings[${index}].suggestedAction`, 1600);
  }
  return finding;
}

function normalizeFinding(finding: Record<string, unknown>): StructuredFindingV1 {
  const normalized = {
    severity: finding.severity as StructuredFindingV1['severity'],
    confidence: finding.confidence as StructuredFindingV1['confidence'],
    category: finding.category as StructuredFindingV1['category'],
    title: normalizeText(finding.title as string),
    body: normalizeText(finding.body as string),
    path: normalizePath(finding.path),
    startLine: finding.startLine as number | null,
    endLine: finding.endLine as number | null,
    suggestedAction:
      typeof finding.suggestedAction === 'string'
        ? normalizeText(finding.suggestedAction)
        : undefined,
  };
  return {
    ...normalized,
    fingerprint: findingFingerprint(normalized),
  };
}

function findingFingerprint(finding: Omit<StructuredFindingV1, 'fingerprint'>): string {
  return sha256(
    JSON.stringify({
      severity: finding.severity,
      confidence: finding.confidence,
      category: finding.category,
      title: finding.title,
      body: finding.body,
      path: finding.path,
      startLine: finding.startLine,
      endLine: finding.endLine,
      suggestedAction: finding.suggestedAction ?? null,
    }),
  ).slice(0, 16);
}

function normalizeText(value: string): string {
  return value.trim().replace(/\r\n/g, '\n');
}

function normalizePath(value: unknown): string | null {
  if (value === null) {
    return null;
  }
  return normalizePathText(String(value));
}

function requireObject(value: unknown, label: string): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    schemaError(`${label} must be an object`);
  }
  return value as Record<string, unknown>;
}

function requireEnum(value: unknown, label: string, values: Set<string>): void {
  if (typeof value !== 'string' || !values.has(value)) {
    schemaError(`${label} must be one of: ${[...values].join(', ')}`);
  }
}

function requireBoundedString(value: unknown, label: string, maxChars: number): string {
  if (typeof value !== 'string' || value.trim() === '') {
    schemaError(`${label} must be a non-empty string`);
  }
  const trimmed = value.trim();
  if (trimmed.length > maxChars) {
    schemaError(`${label} is too long`);
  }
  return trimmed;
}

function validateRepoRelativePath(value: unknown, label: string, maxChars: number): void {
  if (value === null) {
    return;
  }
  if (typeof value !== 'string' || value.trim().length === 0) {
    schemaError(`${label} must be a non-empty string or null`);
  }
  const normalized = normalizePathText(value);
  if (normalized.length > maxChars) {
    schemaError(`${label} is too long`);
  }
  if (
    isCurrentDirOnlyPath(normalized) ||
    normalized.startsWith('/') ||
    /^[A-Za-z][A-Za-z0-9+.-]*:/.test(normalized) ||
    normalized.split('/').includes('..')
  ) {
    schemaError(`${label} must be a safe repo-relative path or null`);
  }
}

function normalizePathText(value: string): string {
  return value.trim().replace(/\\/g, '/');
}

function isCurrentDirOnlyPath(value: string): boolean {
  return value
    .split('/')
    .filter((segment) => segment.length > 0)
    .every((segment) => segment === '.');
}

function requireNullablePositiveInteger(value: unknown, label: string): void {
  if (value === null) {
    return;
  }
  if (!Number.isInteger(value) || Number(value) <= 0) {
    schemaError(`${label} must be a positive integer or null`);
  }
}

function validateLineRange(startLine: unknown, endLine: unknown, index: number): void {
  if (typeof startLine === 'number' && typeof endLine === 'number' && endLine < startLine) {
    schemaError(`findings[${index}].endLine must be greater than or equal to startLine`);
  }
}

function schemaError(message: string): never {
  throw new StructuredReviewValidationError('schema_invalid', summarizeValidationError(message));
}

function summarizeValidationError(message: string): string {
  return message.replace(/\s+/g, ' ').slice(0, 240);
}
