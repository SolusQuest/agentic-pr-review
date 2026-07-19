import { describe, expect, it } from 'vitest';
import { invokeLiveRuntime, isWithinLedgerPathLength } from './invoke-live-runtime.js';

describe('live changed-file path bounds', () => {
  const fields = ['path', 'previousPath'] as const;

  for (const field of fields) {
    describe(field, () => {
      it('accepts 500 ASCII characters', () => {
        expect(isWithinLedgerPathLength('x'.repeat(500))).toBe(true);
      });

      it('rejects 501 ASCII characters', () => {
        expect(isWithinLedgerPathLength('x'.repeat(501))).toBe(false);
      });

      it('counts non-BMP characters as one JSON character', () => {
        const value = '😀'.repeat(300);
        expect(value.length).toBeGreaterThan(500);
        expect(isWithinLedgerPathLength(value)).toBe(true);
      });

      it('rejects more than 500 non-BMP characters', () => {
        expect(isWithinLedgerPathLength('😀'.repeat(501))).toBe(false);
      });
    });
  }

  it('rejects a malformed AbortSignal before any runtime work', async () => {
    await expect(
      invokeLiveRuntime({
        command: { executablePath: process.execPath },
        input: {} as never,
        context: {} as never,
        manifestInput: {} as never,
        timeoutMs: 1,
        signal: { aborted: false } as never,
      }),
    ).rejects.toMatchObject({ kind: 'options-invalid' });
  });
});
