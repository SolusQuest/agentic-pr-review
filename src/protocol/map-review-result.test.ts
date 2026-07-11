import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { mapReviewResultV1ToRuntimeContent } from './map-review-result.js';
import { validateReviewResultV1, type ReviewResultV1 } from './review-result.js';
import type { StructuredFindingV1 } from '../types.js';

const here = dirname(fileURLToPath(import.meta.url));
const FIXTURES_DIR = join(here, '..', '..', 'protocol', 'fixtures', 'v1');

function loadResult(name: string): ReviewResultV1 {
  const parsed = JSON.parse(readFileSync(join(FIXTURES_DIR, name), 'utf8')) as ReviewResultV1;
  const validation = validateReviewResultV1(parsed);
  if (!validation.ok) {
    throw new Error(`fixture ${name} failed schema validation: ${validation.errors?.join('; ')}`);
  }
  return parsed;
}

const CONTENT_KEYS = new Set([
  'summary',
  'findings',
  'limitations',
  'usage',
  'observedTurns',
  'observedTurnSource',
]);
const SIDE_CHANNEL_KEYS = new Set(['warnings', 'diagnostics', 'inputSha256', 'trace']);
const HOST_OWNED_KEYS = [
  'phase',
  'baseSha',
  'headSha',
  'reviewedRange',
  'runtimeProvider',
  'sessionId',
  'usageBudgetStatus',
  'lineageTotals',
  'stateKey',
  'repository',
  'toolMode',
] as const;

describe('mapReviewResultV1ToRuntimeContent', () => {
  it('projects a full result while preserving content and side-channel fields', () => {
    const result = loadResult('valid-result-full.json');
    const projection = mapReviewResultV1ToRuntimeContent(result);

    expect(projection.content.summary).toBe(result.summary);
    expect(projection.content.findings).toHaveLength(result.findings.length);
    expect(projection.content.limitations).toEqual(result.limitations);
    expect(projection.content.usage).toEqual(result.usage);
    expect(projection.content.observedTurns).toBe(result.observedTurns);
    expect(projection.content.observedTurnSource).toBe(result.observedTurnSource);

    const first = projection.content.findings[0];
    expect(first.severity).toBe(result.findings[0].severity);
    expect(first.confidence).toBe(result.findings[0].confidence);
    expect(first.category).toBe(result.findings[0].category);
    expect(first.path).toBe(result.findings[0].path);
    expect(first.startLine).toBe(result.findings[0].startLine);
    expect(first.endLine).toBe(result.findings[0].endLine);
    expect(first.inlinePreference).toBe(result.findings[0].inlinePreference);

    expect(projection.sideChannel.warnings).toEqual(result.warnings);
    expect(projection.sideChannel.diagnostics).toEqual(result.diagnostics);
    expect(projection.sideChannel.inputSha256).toBe(result.inputSha256);
    expect(projection.sideChannel.trace).toEqual(result.trace);
  });

  it('projects a no-findings result to an empty findings array', () => {
    const result = loadResult('valid-result-no-findings.json');
    const projection = mapReviewResultV1ToRuntimeContent(result);
    expect(projection.content.findings).toEqual([]);
    expect(projection.sideChannel.warnings).toEqual([]);
    expect(projection.sideChannel.diagnostics).toEqual([]);
  });

  it('preserves a pathless finding with null path and line values', () => {
    const result = loadResult('valid-result-pathless.json');
    const projection = mapReviewResultV1ToRuntimeContent(result);
    expect(projection.content.findings).toHaveLength(1);
    const finding = projection.content.findings[0];
    expect(finding.path).toBeNull();
    expect(finding.startLine).toBeNull();
    expect(finding.endLine).toBeNull();
  });

  it('projects the paired bootstrap fixture and preserves inputSha256 and trace side-channel', () => {
    const parsed = JSON.parse(
      readFileSync(join(FIXTURES_DIR, 'cases', 'bootstrap', 'result.json'), 'utf8'),
    ) as ReviewResultV1;
    expect(validateReviewResultV1(parsed).ok).toBe(true);
    const projection = mapReviewResultV1ToRuntimeContent(parsed);

    expect(projection.sideChannel.inputSha256).toBe(parsed.inputSha256);
    expect(projection.sideChannel.warnings).toEqual(parsed.warnings);
    expect(projection.sideChannel.diagnostics).toEqual(parsed.diagnostics);
    expect(projection.sideChannel.trace).toEqual(parsed.trace);
  });

  it('does not carry host-owned facts in either content or side channel', () => {
    const result = loadResult('valid-result-full.json');
    const projection = mapReviewResultV1ToRuntimeContent(result);

    for (const key of Object.keys(projection.content)) {
      expect(CONTENT_KEYS.has(key)).toBe(true);
    }
    for (const key of Object.keys(projection.sideChannel)) {
      expect(SIDE_CHANNEL_KEYS.has(key)).toBe(true);
    }
    for (const banned of HOST_OWNED_KEYS) {
      expect(banned in projection.content).toBe(false);
      expect(banned in projection.sideChannel).toBe(false);
    }
  });

  it('produces findings shape-compatible with the existing StructuredFindingV1 fingerprint input', () => {
    // The existing `findingFingerprint` in `src/structured.ts` accepts
    // `Omit<StructuredFindingV1, 'fingerprint'>`. Projected findings from a
    // `ReviewResultV1` must fit that shape so the M2 host caller can invoke it
    // without re-shaping. We prove compatibility via TypeScript's `satisfies`
    // (compile-time) plus a runtime field presence check; the helper itself is
    // module-private and this test does not exercise its runtime output (which
    // would require modifying `structured.ts`).
    const result = loadResult('valid-result-full.json');
    const projection = mapReviewResultV1ToRuntimeContent(result);
    const finding = projection.content.findings[0];
    const asFingerprintInput = {
      severity: finding.severity,
      confidence: finding.confidence,
      category: finding.category,
      title: finding.title,
      body: finding.body,
      path: finding.path,
      startLine: finding.startLine,
      endLine: finding.endLine,
      suggestedAction: finding.suggestedAction,
    } satisfies Omit<StructuredFindingV1, 'fingerprint'>;
    // Runtime sanity: every required key is defined (`suggestedAction` may be undefined).
    for (const key of [
      'severity',
      'confidence',
      'category',
      'title',
      'body',
      'path',
      'startLine',
      'endLine',
    ] as const) {
      expect(key in asFingerprintInput).toBe(true);
    }
  });

  it('preserves empty warnings and diagnostics arrays instead of omitting them', () => {
    const result = loadResult('valid-result-no-findings.json');
    const projection = mapReviewResultV1ToRuntimeContent(result);
    expect(projection.sideChannel.warnings).toEqual([]);
    expect(projection.sideChannel.diagnostics).toEqual([]);
    expect('warnings' in projection.sideChannel).toBe(true);
    expect('diagnostics' in projection.sideChannel).toBe(true);
  });
});
