import { describe, expect, it } from 'vitest';

import {
  parseCompletionDocument,
  renderStepSummary,
  type ActionHostCompletionDocument,
  type ActionHostStatus,
} from './completion.js';

const build = 'r4-h1';
const reviewedSha = 'a'.repeat(40);

const failureAnnotations = {
  configuration_invalid: ['contract', 'configuration_invalid', 'Action configuration is invalid.'],
  authorization_failed: ['authorization', 'authorization_failed', 'Action authorization failed.'],
  credentials_missing: [
    'authorization',
    'credentials_missing',
    'Required credentials are unavailable.',
  ],
  snapshot_incomplete: [
    'snapshot',
    'snapshot_incomplete',
    'The reviewed snapshot could not be completed.',
  ],
  provider_failed: ['provider', 'provider_failed', 'The review provider failed.'],
  agent_result_invalid: ['agent', 'agent_result_invalid', 'The review result was invalid.'],
  stale_head: ['publication', 'stale_head', 'The pull request head changed before publication.'],
  state_conflict: ['state', 'state_conflict', 'Review state is in conflict.'],
  sticky_publication_failed: [
    'publication',
    'sticky_publication_failed',
    'The review summary could not be published.',
  ],
  outcome_ambiguous: [
    'reconciliation',
    'outcome_ambiguous',
    'A side effect could not be reconciled.',
  ],
  cancelled: ['cancellation', 'cancelled', 'The review was cancelled before a side effect began.'],
  internal_failure: ['internal', 'internal_failure', 'The review host failed.'],
} as const;

const successStatuses = [
  'reviewed',
  'reviewed_with_inline_warnings',
  'skipped_untrusted_event',
  'skipped_fork',
  'skipped_draft',
  'skipped_closed',
] as const;

describe('W1 H1 completion validation', () => {
  it.each(successStatuses)('accepts canonical success status %s', (status) => {
    const document = completion(status);
    expect(parse(document, 0)).toEqual(document);
  });

  it.each(Object.keys(failureAnnotations) as (keyof typeof failureAnnotations)[])(
    'accepts canonical failure status %s',
    (status) => {
      const document = completion(status);
      expect(parse(document, 1)).toEqual(document);
    },
  );

  it('accepts legal trailing JSON whitespace at exactly 16 KiB and rejects cap plus one', () => {
    const compact = Buffer.from(JSON.stringify(completion('reviewed')));
    const exact = Buffer.concat([compact, Buffer.alloc(16 * 1024 - compact.length, 0x20)]);
    expect(parseCompletionDocument(exact, build, 0).status).toBe('reviewed');
    expect(() =>
      parseCompletionDocument(Buffer.concat([exact, Buffer.of(0x20)]), build, 0),
    ).toThrow('wrapper_document_invalid');
  });

  it('rejects bytes after JSON, escaped duplicates, BOM, and malformed UTF-8', () => {
    const source = JSON.stringify(completion('reviewed'));
    expect(() => parseCompletionDocument(Buffer.from(`${source}{}`), build, 0)).toThrow(
      'wrapper_document_invalid',
    );
    expect(() =>
      parseCompletionDocument(
        Buffer.from(source.replace('{', '{"\\u0073tatus":"reviewed",')),
        build,
        0,
      ),
    ).toThrow('wrapper_document_invalid');
    expect(() =>
      parseCompletionDocument(
        Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from(source)]),
        build,
        0,
      ),
    ).toThrow('wrapper_document_invalid');
    expect(() => parseCompletionDocument(Buffer.from([0xff]), build, 0)).toThrow(
      'wrapper_document_invalid',
    );
  });

  it('rejects build, exit, summary, annotation, and workflow-command mismatches', () => {
    const reviewed = completion('reviewed');
    expect(() => parse({ ...reviewed, build_discriminator: 'other' }, 0)).toThrow(
      'wrapper_completion_invalid',
    );
    expect(() => parse({ ...reviewed, process_exit_code: 1 }, 0)).toThrow(
      'wrapper_completion_invalid',
    );
    expect(() => parse(reviewed, 1)).toThrow('wrapper_completion_invalid');
    expect(() =>
      parse(
        {
          ...reviewed,
          summary: { ...reviewed.summary, publication_url: 'https://github.com/a/b::error' },
        },
        0,
      ),
    ).toThrow('wrapper_completion_invalid');
    const warning = completion('reviewed_with_inline_warnings');
    expect(() =>
      parse(
        {
          ...warning,
          annotations: [{ ...warning.annotations[0]!, message: 'changed' }],
        },
        0,
      ),
    ).toThrow('wrapper_completion_invalid');
  });

  it('rejects integer fields encoded with floating-point JSON syntax', () => {
    const source = JSON.stringify(completion('reviewed'));
    expect(() =>
      parseCompletionDocument(
        Buffer.from(source.replace('"process_exit_code":0', '"process_exit_code":0.0')),
        build,
        0,
      ),
    ).toThrow('wrapper_document_invalid');
    expect(() =>
      parseCompletionDocument(
        Buffer.from(source.replace('"finding_count":2', '"finding_count":2e0')),
        build,
        0,
      ),
    ).toThrow('wrapper_document_invalid');
  });

  it('renders only the bounded H1 presentation fields', () => {
    const summary = renderStepSummary(completion('reviewed'));
    expect(summary).toContain(reviewedSha);
    expect(summary).toContain('https://github.com/SolusQuest/agentic-pr-review/pull/163');
    expect(summary).not.toContain('::');
    expect(summary).not.toContain('SESSION');
  });
});

function parse(document: unknown, exitCode: number): ActionHostCompletionDocument {
  return parseCompletionDocument(Buffer.from(JSON.stringify(document)), build, exitCode);
}

function completion(status: ActionHostStatus): ActionHostCompletionDocument {
  if (status === 'reviewed' || status === 'reviewed_with_inline_warnings') {
    return {
      build_discriminator: build,
      status,
      exit_class: 'success',
      process_exit_code: 0,
      summary: {
        reviewed_sha: reviewedSha,
        publication_url: 'https://github.com/SolusQuest/agentic-pr-review/pull/163',
        finding_count: 2,
        state_disposition: 'accepted',
      },
      annotations:
        status === 'reviewed'
          ? []
          : [
              {
                code: 'inline_publication_incomplete',
                severity: 'warning',
                message: 'Some inline annotations could not be published.',
              },
            ],
    };
  }
  if (status.startsWith('skipped_')) {
    return {
      build_discriminator: build,
      status,
      exit_class: 'success',
      process_exit_code: 0,
      summary: emptySummary('not_accessed'),
      annotations: [],
    };
  }
  const [exitClass, code, message] = failureAnnotations[status as keyof typeof failureAnnotations];
  return {
    build_discriminator: build,
    status,
    exit_class: exitClass,
    process_exit_code: 1,
    summary: emptySummary(status === 'state_conflict' ? 'conflict' : 'not_committed'),
    annotations: [{ code, severity: 'error', message }],
  };
}

function emptySummary(state_disposition: 'not_accessed' | 'not_committed' | 'conflict') {
  return {
    reviewed_sha: null,
    publication_url: null,
    finding_count: null,
    state_disposition,
  } as const;
}
