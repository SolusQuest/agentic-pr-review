import { describe, expect, it } from 'vitest';
import { validateReviewResultV1, type ReviewResultV1 } from './review-result.js';

const baseFinding = {
  severity: 'medium' as const,
  confidence: 'high' as const,
  category: 'correctness' as const,
  title: 'Issue title',
  body: 'Issue body',
  path: 'src/main.ts',
  startLine: 5,
  endLine: 5,
};

const validResult = {
  protocolVersion: 1,
  runtimeVersion: 'test-1.0.0',
  summary: 'Review summary',
  findings: [baseFinding],
  limitations: [],
  warnings: [],
  diagnostics: [],
} satisfies ReviewResultV1;

describe('ReviewResultV1', () => {
  it('accepts a valid result with findings', () => {
    expect(validateReviewResultV1(validResult).ok).toBe(true);
  });

  it('accepts a no-finding result', () => {
    expect(validateReviewResultV1({ ...validResult, findings: [] }).ok).toBe(true);
  });

  it('accepts a pathless finding', () => {
    const result = {
      ...validResult,
      findings: [{ ...baseFinding, path: null, startLine: null, endLine: null }],
    };
    expect(validateReviewResultV1(result).ok).toBe(true);
  });

  it('accepts usage, observedTurns, and trace', () => {
    const result = {
      ...validResult,
      usage: {
        inputTokens: 100,
        cacheReadInputTokens: 50,
        cacheCreationInputTokens: 10,
        outputTokens: 20,
        recordsObserved: 1,
      },
      observedTurns: 3,
      observedTurnSource: 'unique_assistant_message_ids' as const,
      trace: { path: 'trace.json', sha256: 'a'.repeat(64) },
    };
    expect(validateReviewResultV1(result).ok).toBe(true);
  });

  it('rejects low confidence', () => {
    const result = {
      ...validResult,
      findings: [{ ...baseFinding, confidence: 'low' }],
    };
    expect(validateReviewResultV1(result).ok).toBe(false);
  });

  it('rejects missing summary', () => {
    const { summary: _omit, ...rest } = validResult;
    expect(validateReviewResultV1(rest).ok).toBe(false);
  });

  it('rejects incompatible protocolVersion', () => {
    expect(validateReviewResultV1({ ...validResult, protocolVersion: 2 }).ok).toBe(false);
  });

  it('rejects an unsafe finding path', () => {
    const result = {
      ...validResult,
      findings: [{ ...baseFinding, path: '../secret.ts' }],
    };
    expect(validateReviewResultV1(result).ok).toBe(false);
  });

  it('rejects host-owned workflow facts via closed shapes', () => {
    const leaky = {
      ...validResult,
      phase: 'incremental',
      baseSha: 'sha',
      runtimeProvider: 'test',
      usageBudgetStatus: {},
    };
    expect(validateReviewResultV1(leaky).ok).toBe(false);
  });

  it('rejects a fingerprint field via closed shapes', () => {
    const result = {
      ...validResult,
      findings: [{ ...baseFinding, fingerprint: 'abc123' }],
    };
    expect(validateReviewResultV1(result).ok).toBe(false);
  });

  it('rejects endLine < startLine (semantic)', () => {
    const result = {
      ...validResult,
      findings: [{ ...baseFinding, startLine: 10, endLine: 3 }],
    };
    expect(validateReviewResultV1(result).ok).toBe(false);
  });

  it('rejects startLine set with endLine null (semantic)', () => {
    const result = {
      ...validResult,
      findings: [{ ...baseFinding, startLine: 5, endLine: null }],
    };
    expect(validateReviewResultV1(result).ok).toBe(false);
  });

  it('rejects startLine null with endLine set (semantic)', () => {
    const result = {
      ...validResult,
      findings: [{ ...baseFinding, startLine: null, endLine: 5 }],
    };
    expect(validateReviewResultV1(result).ok).toBe(false);
  });

  it('rejects path null with line values set (semantic)', () => {
    const result = {
      ...validResult,
      findings: [{ ...baseFinding, path: null, startLine: 5, endLine: 5 }],
    };
    expect(validateReviewResultV1(result).ok).toBe(false);
  });

  it('rejects an empty summary', () => {
    expect(validateReviewResultV1({ ...validResult, summary: '' }).ok).toBe(false);
  });

  it('rejects a blank finding title', () => {
    const result = {
      ...validResult,
      findings: [{ ...baseFinding, title: '   ' }],
    };
    expect(validateReviewResultV1(result).ok).toBe(false);
  });

  it('rejects an empty limitation entry', () => {
    expect(validateReviewResultV1({ ...validResult, limitations: [''] }).ok).toBe(false);
  });

  it('rejects an empty diagnostic message', () => {
    const result = {
      ...validResult,
      diagnostics: [{ code: 'E001', message: '', level: 'error' }],
    };
    expect(validateReviewResultV1(result).ok).toBe(false);
  });

  it.each(['/home/runner/trace.json', '../trace.json', 'file://trace.json', 'trace\\out.json'])(
    'rejects an unsafe trace path %s',
    (unsafePath) => {
      const result = {
        ...validResult,
        trace: { path: unsafePath, sha256: 'a'.repeat(64) },
      };
      expect(validateReviewResultV1(result).ok).toBe(false);
    },
  );

  it('rejects an invalid inputSha256 format', () => {
    expect(validateReviewResultV1({ ...validResult, inputSha256: 'nothex' }).ok).toBe(false);
  });
});
