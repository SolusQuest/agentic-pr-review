import { describe, it, expect } from 'vitest';
import { validateReviewTraceV1, type ReviewTraceV1 } from './review-trace.js';

function minimalTrace(): ReviewTraceV1 {
  return {
    protocolVersion: 1,
    runtimeVersion: 'test-runtime-1.0.0',
    inputSha256: 'a'.repeat(64),
    mode: 'deterministic-fixture',
    toolCalls: [],
    warnings: [],
    diagnostics: [],
  };
}

function fullTrace(): ReviewTraceV1 {
  return {
    protocolVersion: 1,
    runtimeVersion: 'claude-code-cli-1.0.0',
    inputSha256: 'b'.repeat(64),
    resultSha256: 'c'.repeat(64),
    mode: 'live-provider',
    fixture: undefined,
    provider: {
      name: 'anthropic',
      model: 'claude-sonnet-4-20250514',
      requestCount: 3,
    },
    startedAt: '2026-07-09T10:00:00Z',
    completedAt: '2026-07-09T10:02:30Z',
    usage: {
      inputTokens: 12000,
      cacheReadInputTokens: 8000,
      cacheCreationInputTokens: 4000,
      outputTokens: 3500,
      recordsObserved: 3,
    },
    toolCalls: [
      { name: 'Read', status: 'ok', durationMs: 120 },
      { name: 'Grep', status: 'error', durationMs: 50, errorCode: 'pattern_too_complex' },
      { name: 'Glob', status: 'skipped' },
    ],
    warnings: ['Cache hit ratio was lower than expected.'],
    diagnostics: [
      { code: 'CACHE_MISS', message: 'Prefix cache miss on turn 2.', level: 'warning' },
      { code: 'BUDGET_OK', message: 'Usage budget within limits.', level: 'info' },
    ],
  };
}

describe('ReviewTraceV1 - valid traces', () => {
  it('accepts a minimal valid trace', () => {
    const result = validateReviewTraceV1(minimalTrace());
    expect(result.ok).toBe(true);
  });

  it.each(['x'.repeat(120), 'runtime v1.2.3'])(
    'accepts bounded runtimeVersion %s',
    (runtimeVersion) => {
      expect(validateReviewTraceV1({ ...minimalTrace(), runtimeVersion }).ok).toBe(true);
    },
  );

  it.each(['x'.repeat(121), 'runtime\nversion', 'runtime\u0000version'])(
    'rejects unsafe runtimeVersion %s',
    (runtimeVersion) => {
      expect(validateReviewTraceV1({ ...minimalTrace(), runtimeVersion }).ok).toBe(false);
    },
  );

  it('accepts a full trace with all optional fields', () => {
    const result = validateReviewTraceV1(fullTrace());
    expect(result.ok).toBe(true);
  });

  it('accepts a skipped-mode trace without resultSha256', () => {
    const trace = { ...minimalTrace(), mode: 'skipped' as const };
    const result = validateReviewTraceV1(trace);
    expect(result.ok).toBe(true);
  });

  it('accepts a trace with empty toolCalls array', () => {
    const trace = { ...minimalTrace(), toolCalls: [] };
    const result = validateReviewTraceV1(trace);
    expect(result.ok).toBe(true);
  });

  it('accepts a trace with fixture for deterministic-fixture mode', () => {
    const trace = { ...minimalTrace(), fixture: 'no_findings' };
    const result = validateReviewTraceV1(trace);
    expect(result.ok).toBe(true);
  });

  it('accepts a toolCall with only name and status', () => {
    const trace = {
      ...minimalTrace(),
      toolCalls: [{ name: 'Read', status: 'ok' }],
    };
    const result = validateReviewTraceV1(trace);
    expect(result.ok).toBe(true);
  });

  it('accepts a toolCall with errorCode when status is error', () => {
    const trace = {
      ...minimalTrace(),
      toolCalls: [{ name: 'Grep', status: 'error', errorCode: 'timeout' }],
    };
    const result = validateReviewTraceV1(trace);
    expect(result.ok).toBe(true);
  });

  it('accepts provider with only requestCount', () => {
    const trace = { ...minimalTrace(), provider: { requestCount: 5 } };
    const result = validateReviewTraceV1(trace);
    expect(result.ok).toBe(true);
  });

  it('accepts usage with zero values', () => {
    const trace = {
      ...minimalTrace(),
      usage: {
        inputTokens: 0,
        cacheReadInputTokens: 0,
        cacheCreationInputTokens: 0,
        outputTokens: 0,
        recordsObserved: 0,
      },
    };
    const result = validateReviewTraceV1(trace);
    expect(result.ok).toBe(true);
  });

  it('accepts timestamps as plain strings', () => {
    const trace = {
      ...minimalTrace(),
      startedAt: '2026-07-09T10:00:00Z',
      completedAt: '2026-07-09T10:05:00Z',
    };
    const result = validateReviewTraceV1(trace);
    expect(result.ok).toBe(true);
  });
});

describe('ReviewTraceV1 - required field violations', () => {
  it('rejects missing protocolVersion', () => {
    const { protocolVersion: _, ...trace } = minimalTrace();
    const result = validateReviewTraceV1(trace);
    expect(result.ok).toBe(false);
  });

  it('rejects missing runtimeVersion', () => {
    const { runtimeVersion: _, ...trace } = minimalTrace();
    const result = validateReviewTraceV1(trace);
    expect(result.ok).toBe(false);
  });

  it('rejects missing inputSha256', () => {
    const { inputSha256: _, ...trace } = minimalTrace();
    const result = validateReviewTraceV1(trace);
    expect(result.ok).toBe(false);
  });

  it('rejects missing mode', () => {
    const { mode: _, ...trace } = minimalTrace();
    const result = validateReviewTraceV1(trace);
    expect(result.ok).toBe(false);
  });

  it('rejects missing toolCalls', () => {
    const { toolCalls: _, ...trace } = minimalTrace();
    const result = validateReviewTraceV1(trace);
    expect(result.ok).toBe(false);
  });

  it('rejects missing warnings', () => {
    const { warnings: _, ...trace } = minimalTrace();
    const result = validateReviewTraceV1(trace);
    expect(result.ok).toBe(false);
  });

  it('rejects missing diagnostics', () => {
    const { diagnostics: _, ...trace } = minimalTrace();
    const result = validateReviewTraceV1(trace);
    expect(result.ok).toBe(false);
  });
});

describe('ReviewTraceV1 - protocolVersion violations', () => {
  it('rejects protocolVersion 0', () => {
    const result = validateReviewTraceV1({ ...minimalTrace(), protocolVersion: 0 });
    expect(result.ok).toBe(false);
  });

  it('rejects protocolVersion 2', () => {
    const result = validateReviewTraceV1({ ...minimalTrace(), protocolVersion: 2 });
    expect(result.ok).toBe(false);
  });

  it('rejects string protocolVersion', () => {
    const result = validateReviewTraceV1({ ...minimalTrace(), protocolVersion: '1' });
    expect(result.ok).toBe(false);
  });
});

describe('ReviewTraceV1 - hash format violations', () => {
  it('rejects inputSha256 with uppercase hex', () => {
    const result = validateReviewTraceV1({ ...minimalTrace(), inputSha256: 'A'.repeat(64) });
    expect(result.ok).toBe(false);
  });

  it('rejects inputSha256 with wrong length', () => {
    const result = validateReviewTraceV1({ ...minimalTrace(), inputSha256: 'a'.repeat(32) });
    expect(result.ok).toBe(false);
  });

  it('rejects inputSha256 with non-hex characters', () => {
    const result = validateReviewTraceV1({ ...minimalTrace(), inputSha256: 'g'.repeat(64) });
    expect(result.ok).toBe(false);
  });

  it('rejects resultSha256 with wrong length', () => {
    const result = validateReviewTraceV1({ ...minimalTrace(), resultSha256: 'b'.repeat(63) });
    expect(result.ok).toBe(false);
  });
});

describe('ReviewTraceV1 - mode violations', () => {
  it('rejects invalid mode string', () => {
    const result = validateReviewTraceV1({ ...minimalTrace(), mode: 'debug' });
    expect(result.ok).toBe(false);
  });

  it('rejects validation-failed mode', () => {
    const result = validateReviewTraceV1({ ...minimalTrace(), mode: 'validation-failed' });
    expect(result.ok).toBe(false);
  });

  it('rejects numeric mode', () => {
    const result = validateReviewTraceV1({ ...minimalTrace(), mode: 0 });
    expect(result.ok).toBe(false);
  });
});

describe('ReviewTraceV1 - toolCall violations', () => {
  it('rejects toolCall missing name', () => {
    const trace = {
      ...minimalTrace(),
      toolCalls: [{ status: 'ok' }],
    };
    const result = validateReviewTraceV1(trace);
    expect(result.ok).toBe(false);
  });

  it('rejects toolCall missing status', () => {
    const trace = {
      ...minimalTrace(),
      toolCalls: [{ name: 'Read' }],
    };
    const result = validateReviewTraceV1(trace);
    expect(result.ok).toBe(false);
  });

  it('rejects toolCall with invalid status', () => {
    const trace = {
      ...minimalTrace(),
      toolCalls: [{ name: 'Read', status: 'success' }],
    };
    const result = validateReviewTraceV1(trace);
    expect(result.ok).toBe(false);
  });

  it('rejects toolCall with negative durationMs', () => {
    const trace = {
      ...minimalTrace(),
      toolCalls: [{ name: 'Read', status: 'ok', durationMs: -1 }],
    };
    const result = validateReviewTraceV1(trace);
    expect(result.ok).toBe(false);
  });

  it('rejects toolCall with name exceeding maxLength', () => {
    const trace = {
      ...minimalTrace(),
      toolCalls: [{ name: 'x'.repeat(121), status: 'ok' }],
    };
    const result = validateReviewTraceV1(trace);
    expect(result.ok).toBe(false);
  });

  it('rejects toolCall with blank name', () => {
    const trace = {
      ...minimalTrace(),
      toolCalls: [{ name: '   ', status: 'ok' }],
    };
    const result = validateReviewTraceV1(trace);
    expect(result.ok).toBe(false);
  });
});

describe('ReviewTraceV1 - credential/raw-shaped field rejection', () => {
  it('rejects apiKey field', () => {
    const result = validateReviewTraceV1({ ...minimalTrace(), apiKey: 'sk-xxx' });
    expect(result.ok).toBe(false);
  });

  it('rejects authHeader field', () => {
    const result = validateReviewTraceV1({ ...minimalTrace(), authHeader: 'Bearer xxx' });
    expect(result.ok).toBe(false);
  });

  it('rejects rawRequest field', () => {
    const result = validateReviewTraceV1({ ...minimalTrace(), rawRequest: '{}' });
    expect(result.ok).toBe(false);
  });

  it('rejects rawResponse field', () => {
    const result = validateReviewTraceV1({ ...minimalTrace(), rawResponse: '{}' });
    expect(result.ok).toBe(false);
  });

  it('rejects prompt field', () => {
    const result = validateReviewTraceV1({ ...minimalTrace(), prompt: 'system prompt' });
    expect(result.ok).toBe(false);
  });

  it('rejects rawPrompt field', () => {
    const result = validateReviewTraceV1({ ...minimalTrace(), rawPrompt: 'system prompt' });
    expect(result.ok).toBe(false);
  });

  it('rejects extra unknown field', () => {
    const result = validateReviewTraceV1({ ...minimalTrace(), unexpectedField: true });
    expect(result.ok).toBe(false);
  });

  it('rejects toolCall with inputSummary field', () => {
    const trace = {
      ...minimalTrace(),
      toolCalls: [{ name: 'Read', status: 'ok', inputSummary: 'file contents' }],
    };
    const result = validateReviewTraceV1(trace);
    expect(result.ok).toBe(false);
  });

  it('rejects toolCall with outputSummary field', () => {
    const trace = {
      ...minimalTrace(),
      toolCalls: [{ name: 'Read', status: 'ok', outputSummary: 'search results' }],
    };
    const result = validateReviewTraceV1(trace);
    expect(result.ok).toBe(false);
  });
});

describe('ReviewTraceV1 - string bound violations', () => {
  it('rejects blank runtimeVersion', () => {
    const result = validateReviewTraceV1({ ...minimalTrace(), runtimeVersion: '   ' });
    expect(result.ok).toBe(false);
  });

  it('rejects blank warning string', () => {
    const result = validateReviewTraceV1({ ...minimalTrace(), warnings: ['   '] });
    expect(result.ok).toBe(false);
  });

  it('rejects warning exceeding maxLength', () => {
    const result = validateReviewTraceV1({ ...minimalTrace(), warnings: ['x'.repeat(1001)] });
    expect(result.ok).toBe(false);
  });

  it('rejects diagnostic message exceeding maxLength', () => {
    const result = validateReviewTraceV1({
      ...minimalTrace(),
      diagnostics: [{ code: 'ERR', message: 'x'.repeat(1001), level: 'error' }],
    });
    expect(result.ok).toBe(false);
  });

  it('rejects diagnostic with blank code', () => {
    const result = validateReviewTraceV1({
      ...minimalTrace(),
      diagnostics: [{ code: '  ', message: 'msg', level: 'error' }],
    });
    expect(result.ok).toBe(false);
  });

  it('rejects fixture exceeding maxLength', () => {
    const result = validateReviewTraceV1({ ...minimalTrace(), fixture: 'x'.repeat(121) });
    expect(result.ok).toBe(false);
  });

  it('rejects provider.model exceeding maxLength', () => {
    const result = validateReviewTraceV1({
      ...minimalTrace(),
      provider: { model: 'x'.repeat(121) },
    });
    expect(result.ok).toBe(false);
  });
});

describe('ReviewTraceV1 - usage violations', () => {
  it('rejects usage with negative inputTokens', () => {
    const result = validateReviewTraceV1({
      ...minimalTrace(),
      usage: {
        inputTokens: -1,
        cacheReadInputTokens: 0,
        cacheCreationInputTokens: 0,
        outputTokens: 0,
        recordsObserved: 0,
      },
    });
    expect(result.ok).toBe(false);
  });

  it('rejects usage with missing field', () => {
    const result = validateReviewTraceV1({
      ...minimalTrace(),
      usage: {
        inputTokens: 100,
        cacheReadInputTokens: 0,
        cacheCreationInputTokens: 0,
        outputTokens: 0,
      },
    });
    expect(result.ok).toBe(false);
  });

  it('rejects usage with lineageTotals field', () => {
    const result = validateReviewTraceV1({
      ...minimalTrace(),
      usage: {
        inputTokens: 100,
        cacheReadInputTokens: 0,
        cacheCreationInputTokens: 0,
        outputTokens: 0,
        recordsObserved: 1,
        lineageTotals: { observedTurns: 5 },
      },
    });
    expect(result.ok).toBe(false);
  });

  it('rejects usage with usageBudgetStatus field', () => {
    const result = validateReviewTraceV1({
      ...minimalTrace(),
      usage: {
        inputTokens: 100,
        cacheReadInputTokens: 0,
        cacheCreationInputTokens: 0,
        outputTokens: 0,
        recordsObserved: 1,
        usageBudgetStatus: 'within_limit',
      },
    });
    expect(result.ok).toBe(false);
  });
});

describe('ReviewTraceV1 - non-object input', () => {
  it('rejects null', () => {
    const result = validateReviewTraceV1(null);
    expect(result.ok).toBe(false);
  });

  it('rejects undefined', () => {
    const result = validateReviewTraceV1(undefined);
    expect(result.ok).toBe(false);
  });

  it('rejects array', () => {
    const result = validateReviewTraceV1([]);
    expect(result.ok).toBe(false);
  });

  it('rejects string', () => {
    const result = validateReviewTraceV1('not a trace');
    expect(result.ok).toBe(false);
  });
});
