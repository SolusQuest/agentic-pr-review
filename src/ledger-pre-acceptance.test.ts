import { describe, expect, it } from 'vitest';
import { runLedgerPreAcceptanceStage } from './ledger-csharp.js';
import { acceptLocalCandidate } from './state-acceptance/index.js';

type WriteCounters = {
  candidate: number;
  registration: number;
  marker: number;
  selector: number;
  receipt: number;
  sticky: number;
  githubComment: number;
  githubReview: number;
};

function counters(): WriteCounters {
  return {
    candidate: 0,
    registration: 0,
    marker: 0,
    selector: 0,
    receipt: 0,
    sticky: 0,
    githubComment: 0,
    githubReview: 0,
  };
}

function expectZeroWrites(observed: WriteCounters): void {
  expect(observed).toEqual(counters());
}

describe('ledger pre-acceptance composition boundary', () => {
  for (const [label, failure] of [
    ['runtime execution failure', new Error('runtime execution failed')],
    ['result-invalid', Object.assign(new Error('result invalid'), { kind: 'result-invalid' })],
    ['trace-invalid', Object.assign(new Error('trace invalid'), { kind: 'trace-invalid' })],
  ] as const) {
    it(`keeps every state and publication capability untouched after ${label}`, async () => {
      const writes = counters();

      await expect(
        runLedgerPreAcceptanceStage({
          executeRuntime: async () => {
            throw failure;
          },
          prepareResult: () => 'prepared',
          revalidateTarget: async () => 'matching',
          enterAcceptance: async () => {
            writes.candidate += 1;
            writes.registration += 1;
            writes.marker += 1;
            writes.selector += 1;
            writes.receipt += 1;
            writes.sticky += 1;
            writes.githubComment += 1;
            writes.githubReview += 1;
          },
        }),
      ).rejects.toBe(failure);

      expectZeroWrites(writes);
    });
  }

  it('passes an actually aborted signal into acceptance before any candidate write', async () => {
    const writes = counters();
    const controller = new AbortController();
    const store = {
      uploadCandidate: async () => {
        writes.candidate += 1;
      },
      createRegistration: async () => {
        writes.registration += 1;
      },
      writeMarker: async () => {
        writes.marker += 1;
      },
      casSelector: async () => {
        writes.selector += 1;
      },
      writePublicationReceipt: async () => {
        writes.receipt += 1;
      },
    };

    const outcome = await runLedgerPreAcceptanceStage({
      executeRuntime: async () => ({ lease: true }),
      prepareResult: () => {
        controller.abort();
        return { prepared: true };
      },
      revalidateTarget: async () => 'matching',
      enterAcceptance: async () =>
        acceptLocalCandidate(
          store as never,
          {
            signal: controller.signal,
            publishSticky: async () => {
              writes.sticky += 1;
              writes.githubComment += 1;
              writes.githubReview += 1;
            },
          } as never,
        ),
    });

    expect(outcome).toMatchObject({
      kind: 'acceptance_entered',
      accepted: {
        acceptance: 'not_accepted',
        reason: 'cancelled_before_acceptance',
        publication: { status: 'not_attempted' },
      },
    });
    expectZeroWrites(writes);
  });
});
