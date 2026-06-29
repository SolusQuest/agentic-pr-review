import { describe, expect, it } from 'vitest';
import {
  buildReviewedRange,
  normalizeStructuredReview,
  StructuredReviewValidationError,
} from './structured.js';
import {
  type ActionConfig,
  type ReviewTarget,
  type RuntimeLineageTotals,
  type RuntimeUsage,
} from './types.js';

const target: ReviewTarget = {
  mode: 'synthetic-fixture',
  title: 'Synthetic',
  body: '',
  baseRef: 'main',
  baseSha: 'trusted-base',
  headRef: 'branch',
  headSha: 'trusted-head',
  draft: false,
  changedFiles: [],
};

const pullRequestTarget: ReviewTarget = {
  mode: 'pull-request',
  prNumber: 1,
  title: 'Synthetic PR',
  body: '',
  baseRef: 'main',
  baseSha: 'trusted-base',
  headRef: 'branch',
  headSha: 'trusted-head',
  draft: false,
  changedFiles: [
    {
      filename: 'docs/current-change.md',
      status: 'modified',
      additions: 1,
      deletions: 0,
      changes: 1,
      patch: '@@ -1 +1 @@\n+change',
    },
    {
      filename: 'src/deleted-current-file.ts',
      status: 'removed',
      additions: 0,
      deletions: 1,
      changes: 1,
      patch: '@@ -1 +0,0 @@\n-old',
    },
  ],
};

const config: Pick<ActionConfig, 'runtimeProvider' | 'toolMode'> = {
  runtimeProvider: 'test',
  toolMode: 'readonly',
};

const lineageTotals: RuntimeLineageTotals = {
  observedTurns: 2,
  usage: {
    inputTokens: 10,
    cacheReadInputTokens: 5,
    cacheCreationInputTokens: 1,
    outputTokens: 3,
  },
  source: 'current_run_only',
  partial: false,
};

const usage: RuntimeUsage = {
  inputTokens: 10,
  cacheReadInputTokens: 5,
  cacheCreationInputTokens: 1,
  outputTokens: 3,
  recordsObserved: 1,
};

function normalize(modelJsonText: string, maxFindings = 10) {
  return normalizeStructuredReview({
    modelJsonText,
    target,
    phase: 'incremental',
    previousReviewedHeadSha: 'trusted-prior',
    reviewedRange: buildReviewedRange({
      phase: 'incremental',
      target,
      previousReviewedHeadSha: 'trusted-prior',
    }),
    config,
    sessionId: 'session-1',
    usage,
    observedTurns: 2,
    observedTurnSource: 'unique_assistant_message_ids',
    lineageTotals,
    maxFindings,
  });
}

function normalizeForTarget(reviewTarget: ReviewTarget, modelJsonText: string, maxFindings = 10) {
  return normalizeStructuredReview({
    modelJsonText,
    target: reviewTarget,
    phase: 'incremental',
    previousReviewedHeadSha: 'trusted-prior',
    reviewedRange: buildReviewedRange({
      phase: 'incremental',
      target: reviewTarget,
      previousReviewedHeadSha: 'trusted-prior',
    }),
    config,
    sessionId: 'session-1',
    usage,
    observedTurns: 2,
    observedTurnSource: 'unique_assistant_message_ids',
    lineageTotals,
    maxFindings,
  });
}

function validModel(overrides: Record<string, unknown> = {}): string {
  return JSON.stringify({
    schemaVersion: 1,
    summary: 'Structured summary.',
    findings: [
      {
        severity: 'medium',
        confidence: 'high',
        category: 'correctness',
        title: 'Finding title',
        body: 'Finding body.',
        path: 'src/file.ts',
        startLine: 10,
        endLine: 12,
        suggestedAction: 'Adjust the implementation.',
        fingerprint: 'model-controlled-fingerprint',
      },
    ],
    limitations: ['Synthetic limitation.'],
    phase: 'model-controlled-phase',
    headSha: 'model-controlled-head',
    reviewedRange: 'model-controlled-range',
    runtimeProvider: 'model-controlled-runtime',
    ...overrides,
  });
}

describe('structured review normalization', () => {
  it('validates model JSON and injects trusted action-owned metadata', () => {
    const { envelope, metadata } = normalize(validModel());

    expect(envelope.schemaVersion).toBe(1);
    expect(envelope.phase).toBe('incremental');
    expect(envelope.baseSha).toBe('trusted-base');
    expect(envelope.headSha).toBe('trusted-head');
    expect(envelope.previousReviewedHeadSha).toBe('trusted-prior');
    expect(envelope.reviewedRange).toEqual({
      kind: 'incremental',
      fromSha: 'trusted-prior',
      toSha: 'trusted-head',
    });
    expect(envelope.toolMode).toBe('readonly');
    expect(envelope.runtimeProvider).toBe('test');
    expect(envelope.usage).toEqual(usage);
    expect(envelope.observedTurns).toBe(2);
    expect(envelope.lineageTotals).toEqual(lineageTotals);
    expect(envelope.findings[0].fingerprint).not.toBe('model-controlled-fingerprint');
    expect(metadata).toMatchObject({
      status: 'valid',
      inputFindingCount: 1,
      postFindingCapCount: 1,
      renderedFindingCount: 1,
      findingsTruncated: false,
    });
  });

  it('uses null fromSha for bootstrap reviewed ranges', () => {
    expect(
      buildReviewedRange({
        phase: 'bootstrap',
        target,
      }),
    ).toEqual({
      kind: 'bootstrap',
      fromSha: null,
      toSha: 'trusted-head',
    });
  });

  it('extracts fenced JSON deterministically without model repair', () => {
    const fenced = `\n\n\`\`\`json\n${validModel()}\n\`\`\`\n`;
    const { envelope, metadata } = normalize(fenced);
    expect(envelope.summary).toBe('Structured summary.');
    expect(metadata.status).toBe('extracted');
  });

  it('fails closed for invalid JSON with sanitized diagnostics', () => {
    expect(() => normalize('private raw model body')).toThrow(StructuredReviewValidationError);
    try {
      normalize('private raw model body');
    } catch (error) {
      expect(error).toBeInstanceOf(StructuredReviewValidationError);
      expect((error as StructuredReviewValidationError).status).toBe('invalid_json');
      expect((error as Error).message).not.toContain('private raw model body');
    }
  });

  it('rejects schema-invalid confidence instead of representing low confidence findings', () => {
    expect(() =>
      normalize(
        validModel({
          findings: [
            {
              severity: 'medium',
              confidence: 'low',
              category: 'correctness',
              title: 'Low confidence',
              body: 'Should be omitted by the model instead.',
              path: 'src/file.ts',
              startLine: 1,
              endLine: 1,
            },
          ],
        }),
      ),
    ).toThrow(/confidence/);
  });

  it('rejects findings with endLine before startLine', () => {
    expect(() =>
      normalize(
        validModel({
          findings: [
            {
              severity: 'medium',
              confidence: 'high',
              category: 'correctness',
              title: 'Reversed range',
              body: 'Line ranges must be ordered.',
              path: 'src/file.ts',
              startLine: 12,
              endLine: 10,
            },
          ],
        }),
      ),
    ).toThrow(/endLine must be greater than or equal to startLine/);
  });

  it('keeps findings without path or line and renders null location fields', () => {
    const { envelope } = normalize(
      validModel({
        findings: [
          {
            severity: 'low',
            confidence: 'medium',
            category: 'documentation',
            title: 'Repository-level note',
            body: 'No precise location.',
            path: null,
            startLine: null,
            endLine: null,
          },
        ],
      }),
    );
    expect(envelope.findings[0]).toMatchObject({
      path: null,
      startLine: null,
      endLine: null,
    });
  });

  it('normalizes safe repo-relative finding paths', () => {
    const { envelope } = normalize(
      validModel({
        findings: [
          {
            severity: 'medium',
            confidence: 'high',
            category: 'correctness',
            title: 'Windows separators',
            body: 'Backslashes normalize to slashes.',
            path: 'src\\folder\\file.ts',
            startLine: 1,
            endLine: 1,
          },
        ],
      }),
    );
    expect(envelope.findings[0].path).toBe('src/folder/file.ts');
  });

  it('rejects unsafe non-relative finding paths', () => {
    const invalidPaths = [
      '',
      '.',
      './',
      '/absolute/file.ts',
      'C:\\absolute\\file.ts',
      'C:/absolute/file.ts',
      'https://example.com/file.ts',
      'file:src/file.ts',
      '../outside.ts',
      'src/../outside.ts',
    ];

    for (const path of invalidPaths) {
      expect(() =>
        normalize(
          validModel({
            findings: [
              {
                severity: 'medium',
                confidence: 'high',
                category: 'correctness',
                title: 'Unsafe path',
                body: 'Path should be rejected.',
                path,
                startLine: 1,
                endLine: 1,
              },
            ],
          }),
        ),
      ).toThrow(/path/);
    }
  });

  it('caps normalized findings and records truncation before rendering/artifacts', () => {
    const findings = Array.from({ length: 5 }, (_, index) => ({
      severity: 'medium',
      confidence: 'high',
      category: 'maintainability',
      title: `Finding ${index}`,
      body: `Body ${index}`,
      path: 'src/file.ts',
      startLine: index + 1,
      endLine: index + 1,
    }));
    const { envelope, metadata } = normalize(validModel({ findings }), 2);
    expect(envelope.findings).toHaveLength(2);
    expect(envelope.result).toEqual({
      inputFindingCount: 5,
      postFindingCapCount: 2,
      renderedFindingCount: 2,
      findingsTruncated: true,
      truncationReason: 'max_findings',
    });
    expect(metadata.findingsTruncated).toBe(true);
  });

  it('generates stable action-owned fingerprints for equivalent normalized findings', () => {
    const first = normalize(validModel()).envelope.findings[0].fingerprint;
    const second = normalize(validModel()).envelope.findings[0].fingerprint;
    expect(first).toBe(second);
    expect(first).toMatch(/^[a-f0-9]{16}$/);
  });

  it('drops file findings outside the current PR files before rendering and artifacts', () => {
    const { envelope } = normalizeForTarget(
      pullRequestTarget,
      validModel({
        findings: [
          {
            severity: 'medium',
            confidence: 'high',
            category: 'correctness',
            title: 'Current file',
            body: 'Allowed.',
            path: 'docs/current-change.md',
            startLine: 1,
            endLine: 1,
          },
          {
            severity: 'high',
            confidence: 'high',
            category: 'security',
            title: 'Base-only file',
            body: 'Should be dropped.',
            path: 'src/base-only.ts',
            startLine: 1,
            endLine: 1,
          },
        ],
      }),
    );

    expect(envelope.findings.map((finding) => finding.path)).toEqual(['docs/current-change.md']);
    expect(envelope.result.inputFindingCount).toBe(1);
  });

  it('keeps findings for removed current PR files and pathless PR-level findings', () => {
    const { envelope } = normalizeForTarget(
      pullRequestTarget,
      validModel({
        findings: [
          {
            severity: 'medium',
            confidence: 'high',
            category: 'correctness',
            title: 'Deleted current file',
            body: 'Removed files are still in the current PR diff.',
            path: 'src/deleted-current-file.ts',
            startLine: 1,
            endLine: 1,
          },
          {
            severity: 'low',
            confidence: 'medium',
            category: 'documentation',
            title: 'PR-level note',
            body: 'Allowed without file path.',
            path: null,
            startLine: null,
            endLine: null,
          },
        ],
      }),
    );

    expect(envelope.findings.map((finding) => finding.path)).toEqual([
      'src/deleted-current-file.ts',
      null,
    ]);
  });

  it('does not let unsafe paths bypass current PR file membership checks', () => {
    expect(() =>
      normalizeForTarget(
        pullRequestTarget,
        validModel({
          findings: [
            {
              severity: 'medium',
              confidence: 'high',
              category: 'correctness',
              title: 'Unsafe path',
              body: 'Should fail validation.',
              path: 'src/../docs/current-change.md',
              startLine: 1,
              endLine: 1,
            },
          ],
        }),
      ),
    ).toThrow(/path/);
  });
});
