import { H1_MAXIMUM_COMPLETION_DOCUMENT_BYTES } from '../launcher/contracts.js';
import { parseStrictJson } from '../launcher/strict-json.js';
import {
  buildDiscriminator,
  exactRecord,
  fail,
  isWorkflowPresentationText,
  lowerHex,
  utf8Length,
} from '../launcher/validation.js';

const ROOT_KEYS = Object.freeze([
  'build_discriminator',
  'status',
  'exit_class',
  'process_exit_code',
  'summary',
  'annotations',
]);
const SUMMARY_KEYS = Object.freeze([
  'reviewed_sha',
  'publication_url',
  'finding_count',
  'state_disposition',
]);
const ANNOTATION_KEYS = Object.freeze(['code', 'severity', 'message']);

export type ActionHostStatus = keyof typeof STATUS_RULES;
export type ActionHostExitClass =
  | 'success'
  | 'contract'
  | 'authorization'
  | 'snapshot'
  | 'provider'
  | 'agent'
  | 'state'
  | 'publication'
  | 'reconciliation'
  | 'cancellation'
  | 'internal';
export type ActionHostStateDisposition = 'accepted' | 'not_accessed' | 'not_committed' | 'conflict';

export interface ActionHostAnnotationDocument {
  readonly code: string;
  readonly severity: 'warning' | 'error';
  readonly message: string;
}

export interface ActionHostCompletionDocument {
  readonly build_discriminator: string;
  readonly status: ActionHostStatus;
  readonly exit_class: ActionHostExitClass;
  readonly process_exit_code: 0 | 1;
  readonly summary: {
    readonly reviewed_sha: string | null;
    readonly publication_url: string | null;
    readonly finding_count: number | null;
    readonly state_disposition: ActionHostStateDisposition;
  };
  readonly annotations: readonly ActionHostAnnotationDocument[];
}

interface StatusRule {
  readonly exitClass: ActionHostExitClass;
  readonly processExitCode: 0 | 1;
  readonly summaryKind: 'reviewed' | 'skipped' | 'failure' | 'conflict';
  readonly annotation: ActionHostAnnotationDocument | null;
}

const warning = (code: string, message: string): ActionHostAnnotationDocument => ({
  code,
  severity: 'warning',
  message,
});
const error = (code: string, message: string): ActionHostAnnotationDocument => ({
  code,
  severity: 'error',
  message,
});

export const STATUS_RULES = Object.freeze({
  reviewed: rule('success', 0, 'reviewed'),
  reviewed_with_inline_warnings: rule(
    'success',
    0,
    'reviewed',
    warning('inline_publication_incomplete', 'Some inline annotations could not be published.'),
  ),
  skipped_untrusted_event: rule('success', 0, 'skipped'),
  skipped_fork: rule('success', 0, 'skipped'),
  skipped_draft: rule('success', 0, 'skipped'),
  skipped_closed: rule('success', 0, 'skipped'),
  configuration_invalid: rule(
    'contract',
    1,
    'failure',
    error('configuration_invalid', 'Action configuration is invalid.'),
  ),
  authorization_failed: rule(
    'authorization',
    1,
    'failure',
    error('authorization_failed', 'Action authorization failed.'),
  ),
  credentials_missing: rule(
    'authorization',
    1,
    'failure',
    error('credentials_missing', 'Required credentials are unavailable.'),
  ),
  snapshot_incomplete: rule(
    'snapshot',
    1,
    'failure',
    error('snapshot_incomplete', 'The reviewed snapshot could not be completed.'),
  ),
  provider_failed: rule(
    'provider',
    1,
    'failure',
    error('provider_failed', 'The review provider failed.'),
  ),
  agent_result_invalid: rule(
    'agent',
    1,
    'failure',
    error('agent_result_invalid', 'The review result was invalid.'),
  ),
  stale_head: rule(
    'publication',
    1,
    'failure',
    error('stale_head', 'The pull request head changed before publication.'),
  ),
  state_conflict: rule(
    'state',
    1,
    'conflict',
    error('state_conflict', 'Review state is in conflict.'),
  ),
  sticky_publication_failed: rule(
    'publication',
    1,
    'failure',
    error('sticky_publication_failed', 'The review summary could not be published.'),
  ),
  outcome_ambiguous: rule(
    'reconciliation',
    1,
    'failure',
    error('outcome_ambiguous', 'A side effect could not be reconciled.'),
  ),
  cancelled: rule(
    'cancellation',
    1,
    'failure',
    error('cancelled', 'The review was cancelled before a side effect began.'),
  ),
  internal_failure: rule(
    'internal',
    1,
    'failure',
    error('internal_failure', 'The review host failed.'),
  ),
} satisfies Record<string, StatusRule>);

export function parseCompletionDocument(
  bytes: Uint8Array,
  expectedBuildDiscriminator: string,
  actualProcessExitCode: number,
): ActionHostCompletionDocument {
  const root = exactRecord(
    parseStrictJson(bytes, H1_MAXIMUM_COMPLETION_DOCUMENT_BYTES),
    ROOT_KEYS,
    'wrapper_completion_invalid',
  );
  const summary = exactRecord(root.summary, SUMMARY_KEYS, 'wrapper_completion_invalid');
  if (!Array.isArray(root.annotations) || root.annotations.length > 1) {
    fail('wrapper_completion_invalid');
  }
  if (
    !buildDiscriminator(root.build_discriminator) ||
    root.build_discriminator !== expectedBuildDiscriminator ||
    typeof root.status !== 'string' ||
    !Object.hasOwn(STATUS_RULES, root.status)
  ) {
    fail('wrapper_completion_invalid');
  }
  const status = root.status as ActionHostStatus;
  const rule = STATUS_RULES[status];
  if (
    root.exit_class !== rule.exitClass ||
    root.process_exit_code !== rule.processExitCode ||
    actualProcessExitCode !== rule.processExitCode
  ) {
    fail('wrapper_completion_invalid');
  }
  const parsedSummary = validateSummary(summary, rule.summaryKind);
  const annotations = validateAnnotations(root.annotations, rule.annotation);
  return {
    build_discriminator: root.build_discriminator,
    status,
    exit_class: rule.exitClass,
    process_exit_code: rule.processExitCode,
    summary: parsedSummary,
    annotations,
  };
}

export function renderStepSummary(completion: ActionHostCompletionDocument): string {
  const rows = [
    ['Status', completion.status],
    ['Reviewed SHA', completion.summary.reviewed_sha ?? 'Not available'],
    ['Publication URL', completion.summary.publication_url ?? 'Not available'],
    [
      'Finding count',
      completion.summary.finding_count === null
        ? 'Not available'
        : String(completion.summary.finding_count),
    ],
    ['State disposition', completion.summary.state_disposition],
  ];
  const summary = [
    '## Agentic PR Review',
    '',
    '| Field | Value |',
    '| --- | --- |',
    ...rows.map(([label, value]) => `| ${label} | ${escapeMarkdown(value)} |`),
    '',
  ].join('\n');
  if (utf8Length(summary) > 8 * 1024 || summary.includes('::')) {
    fail('wrapper_presentation_invalid');
  }
  return summary;
}

function validateSummary(
  summary: Record<string, unknown>,
  kind: StatusRule['summaryKind'],
): ActionHostCompletionDocument['summary'] {
  const reviewedSha = summary.reviewed_sha;
  const publicationUrl = summary.publication_url;
  const findingCount = summary.finding_count;
  const stateDisposition = summary.state_disposition;
  if (
    (reviewedSha !== null && !lowerHex(reviewedSha, 40)) ||
    (publicationUrl !== null && !publicationUrlValue(publicationUrl)) ||
    (findingCount !== null &&
      (typeof findingCount !== 'number' ||
        !Number.isInteger(findingCount) ||
        findingCount < 0 ||
        findingCount > 20))
  ) {
    fail('wrapper_completion_invalid');
  }
  const noReviewFields = reviewedSha === null && publicationUrl === null && findingCount === null;
  if (
    (kind === 'reviewed' &&
      (!lowerHex(reviewedSha, 40) ||
        !publicationUrlValue(publicationUrl) ||
        typeof findingCount !== 'number' ||
        stateDisposition !== 'accepted')) ||
    (kind === 'skipped' && (!noReviewFields || stateDisposition !== 'not_accessed')) ||
    (kind === 'failure' && (!noReviewFields || stateDisposition !== 'not_committed')) ||
    (kind === 'conflict' && (!noReviewFields || stateDisposition !== 'conflict'))
  ) {
    fail('wrapper_completion_invalid');
  }
  return {
    reviewed_sha: reviewedSha as string | null,
    publication_url: publicationUrl as string | null,
    finding_count: findingCount as number | null,
    state_disposition: stateDisposition as ActionHostStateDisposition,
  };
}

function validateAnnotations(
  values: unknown[],
  expected: ActionHostAnnotationDocument | null,
): readonly ActionHostAnnotationDocument[] {
  if (expected === null) {
    if (values.length !== 0) fail('wrapper_completion_invalid');
    return [];
  }
  if (values.length !== 1) fail('wrapper_completion_invalid');
  const annotation = exactRecord(values[0], ANNOTATION_KEYS, 'wrapper_completion_invalid');
  if (
    annotation.code !== expected.code ||
    annotation.severity !== expected.severity ||
    annotation.message !== expected.message ||
    !isWorkflowPresentationText(annotation.message, 256)
  ) {
    fail('wrapper_completion_invalid');
  }
  return [expected];
}

function publicationUrlValue(value: unknown): value is string {
  if (!isWorkflowPresentationText(value, 2 * 1024)) return false;
  try {
    const url = new URL(value);
    return (
      url.protocol === 'https:' &&
      url.hostname.toLowerCase() === 'github.com' &&
      url.username.length === 0 &&
      url.password.length === 0 &&
      url.search.length === 0 &&
      url.port.length === 0 &&
      url.pathname !== '/'
    );
  } catch {
    return false;
  }
}

function rule(
  exitClass: ActionHostExitClass,
  processExitCode: 0 | 1,
  summaryKind: StatusRule['summaryKind'],
  annotation: ActionHostAnnotationDocument | null = null,
): StatusRule {
  return { exitClass, processExitCode, summaryKind, annotation };
}

function escapeMarkdown(value: string): string {
  return value
    .replaceAll('\\', '\\\\')
    .replaceAll('|', '\\|')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;');
}
