import { describe, expect, it } from 'vitest';
import { validateReviewInputV1, type ReviewInputV1 } from './review-input.js';
import { sha256 } from '../utils.js';

function validPatch(
  text: string,
  maxChars = 60000,
): {
  text: string;
  truncated: boolean;
  sha256: string;
  maxChars: number;
} {
  return {
    text,
    truncated: text.length > maxChars,
    sha256: sha256(text),
    maxChars,
  };
}

const baseHost = {
  repository: { owner: 'SolusQuest', name: 'agentic-pr-review' },
  review: {
    phase: 'bootstrap' as const,
    baseSha: 'base-sha-0001',
    headSha: 'head-sha-0001',
    runtimeProvider: 'test' as const,
  },
};

const baseSubject = {
  pullRequest: {
    number: 42,
    title: 'Add feature',
    body: 'body text',
    baseRef: 'main',
    headRef: 'feature',
    draft: false,
  },
  changedFiles: [
    {
      path: 'src/main.ts',
      status: 'modified',
      additions: 3,
      deletions: 1,
      changes: 4,
      patch: validPatch('@@ -1 +1,3 @@\n+change'),
    },
  ],
};

const bootstrapInput = {
  protocolVersion: 1,
  requestedRuntimeVersion: null,
  host: baseHost,
  subject: baseSubject,
  previousState: { present: false, findingFingerprints: [] },
  commentEvidence: { existingFindingFingerprints: [] },
} satisfies ReviewInputV1;

const incrementalInput = {
  protocolVersion: 1,
  requestedRuntimeVersion: null,
  host: {
    ...baseHost,
    review: { ...baseHost.review, phase: 'incremental' as const, stateKey: 'pr-42' },
  },
  subject: baseSubject,
  previousState: {
    present: true,
    reviewedHeadSha: 'prev-head-sha',
    phase: 'incremental' as const,
    findingFingerprints: ['fp-1', 'fp-2'],
    lineage: { reviewCount: 3 },
  },
  commentEvidence: { existingFindingFingerprints: ['fp-1'] },
} satisfies ReviewInputV1;

describe('ReviewInputV1', () => {
  it('accepts a valid bootstrap input', () => {
    expect(validateReviewInputV1(bootstrapInput).ok).toBe(true);
  });

  it('accepts a valid incremental input', () => {
    expect(validateReviewInputV1(incrementalInput).ok).toBe(true);
  });

  it('rejects missing protocolVersion', () => {
    const { protocolVersion: _omit, ...rest } = bootstrapInput;
    expect(validateReviewInputV1(rest).ok).toBe(false);
  });

  it('rejects incompatible protocolVersion', () => {
    expect(validateReviewInputV1({ ...bootstrapInput, protocolVersion: 2 }).ok).toBe(false);
  });

  it('rejects credential-shaped fields via closed object shapes (secret-negative fixture)', () => {
    const leaky = {
      ...bootstrapInput,
      host: { ...baseHost, githubToken: 'ghp_secret', apiKey: 'sk-secret' },
    };
    expect(validateReviewInputV1(leaky).ok).toBe(false);
  });

  it('rejects unsafe parent-dir paths', () => {
    const result = validateReviewInputV1({
      ...bootstrapInput,
      subject: {
        ...baseSubject,
        changedFiles: [{ ...baseSubject.changedFiles[0], path: '../secret.ts' }],
      },
    });
    expect(result.ok).toBe(false);
  });

  it('rejects backslash-containing paths', () => {
    const result = validateReviewInputV1({
      ...bootstrapInput,
      subject: {
        ...baseSubject,
        changedFiles: [{ ...baseSubject.changedFiles[0], path: 'src\\main.ts' }],
      },
    });
    expect(result.ok).toBe(false);
  });

  it('rejects an invalid patch sha256', () => {
    const result = validateReviewInputV1({
      ...bootstrapInput,
      subject: {
        ...baseSubject,
        changedFiles: [
          { ...baseSubject.changedFiles[0], patch: { ...validPatch('text'), sha256: 'nothex' } },
        ],
      },
    });
    expect(result.ok).toBe(false);
  });

  it('allows a changed file without patch context (binary/unavailable)', () => {
    const { patch: _omit, ...fileWithoutPatch } = baseSubject.changedFiles[0];
    const result = validateReviewInputV1({
      ...bootstrapInput,
      subject: { ...baseSubject, changedFiles: [fileWithoutPatch] },
    });
    expect(result.ok).toBe(true);
  });
});
