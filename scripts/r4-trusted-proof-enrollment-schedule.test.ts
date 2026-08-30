import { describe, expect, test } from 'vitest';
import {
  fixedEnrollmentSchedule,
  R4_TRUSTED_PROOF_ENROLLMENT_ROLES,
  R4_TRUSTED_PROOF_FIXED_PRODUCER_JOURNAL_FINAL_PHASES,
  R4_TRUSTED_PROOF_FIXED_PRODUCER_JOURNAL_LIFECYCLE_ACTIONS,
  R4_TRUSTED_PROOF_FIXED_PRODUCER_TARGET_STEP_IDS,
  R4_TRUSTED_PROOF_ROLE_CAPTURE_REQUIREMENTS,
  rehearseFixedEnrollmentSchedule,
} from './r4-trusted-proof-enrollment-schedule.mjs';

function fakePrimitiveTrace() {
  const runtimeRunIds: Record<string, string> = {
    'normal-bootstrap': '8101',
    'normal-continuation': '8102',
    'stale-protected': '8103',
    'stale-follow-on': '8104',
  };
  return fixedEnrollmentSchedule().map((step) =>
    step.role
      ? {
          id: step.id,
          owner: step.owner,
          primitive: step.primitive,
          runtime_run_id: runtimeRunIds[step.role],
          attempt: '1',
        }
      : { id: step.id, owner: step.owner, primitive: step.primitive },
  );
}

describe('R4 fixed four-role enrollment schedule', () => {
  test('keeps every authority seam through final discovery, seal, and cleanup', () => {
    const schedule = fixedEnrollmentSchedule();
    expect(schedule.map((step) => step.id)).toEqual([
      'prepare',
      'refresh',
      'producer-journal-create',
      'baseline-secret-variable-capture',
      'authorize-normal',
      'normal-variable-readiness-capture',
      'normal-bootstrap-produce',
      'normal-continuation-produce',
      'normal-bootstrap-capture',
      'normal-bootstrap-observe',
      'normal-continuation-capture',
      'normal-continuation-observe',
      'authorize-stale',
      'stale-variable-readiness-capture',
      'stale-protected-produce',
      'stale-protected-capture',
      'stale-protected-observe',
      'advance-stale-ref',
      'advance-stale-readback',
      'stale-follow-on-capture',
      'stale-follow-on-observe',
      'final-discovery-and-producer-journal-seal',
      'cleanup',
    ]);
    expect(schedule.filter((step) => step.stage === 'produce').map((step) => step.role)).toEqual(
      R4_TRUSTED_PROOF_ENROLLMENT_ROLES,
    );
    expect(schedule.filter((step) => step.stage === 'capture').map((step) => step.role)).toEqual(
      R4_TRUSTED_PROOF_ENROLLMENT_ROLES,
    );
    expect(schedule.filter((step) => step.stage === 'observe').map((step) => step.role)).toEqual(
      R4_TRUSTED_PROOF_ENROLLMENT_ROLES,
    );
    expect(schedule.find((step) => step.id === 'producer-journal-create')).toMatchObject({
      owner: 'producer-journal',
      requires: ['refresh'],
    });
    expect(schedule.find((step) => step.id === 'baseline-secret-variable-capture')).toMatchObject({
      owner: 'phase-fragment-materializer',
      phases: ['baseline-normal', 'baseline-stale'],
    });
    expect(schedule.find((step) => step.id === 'normal-variable-readiness-capture')).toMatchObject({
      phases: ['normal-variable-readback', 'bootstrap-readiness', 'continuation-readiness'],
      requires: ['authorize-normal'],
    });
    expect(schedule.find((step) => step.id === 'stale-variable-readiness-capture')).toMatchObject({
      phases: ['stale-variable-readback', 'stale-readiness'],
      requires: ['authorize-stale'],
    });
    expect(schedule.filter((step) => step.mutation === 'advance-stale-ref')).toEqual([
      expect.objectContaining({
        id: 'advance-stale-ref',
        owner: 'producer-journal',
        produced_role: 'stale-follow-on',
        run_binding: 'producer-observed-runtime-run-id',
      }),
    ]);
    expect(schedule.some((step) => step.id === 'stale-follow-on-produce')).toBe(false);
    expect(schedule.find((step) => step.id === 'normal-continuation-produce')).toMatchObject({
      prerequisite_semantics: 'bootstrap-producer-command-committed-not-runtime-terminal',
      requires: ['normal-bootstrap-produce'],
    });
    expect(schedule.find((step) => step.id === 'normal-bootstrap-produce')).toMatchObject({
      result: 'producer-journal-command-committed',
    });
    expect(schedule.find((step) => step.id === 'advance-stale-readback')).toMatchObject({
      owner: 'node-executor',
      mode: 'readback-only',
      requires: ['advance-stale-ref'],
    });
    expect(schedule.at(-1)).toMatchObject({
      id: 'cleanup',
      requires: ['final-discovery-and-producer-journal-seal'],
    });
    expect(schedule.at(-2)).toMatchObject({
      id: 'final-discovery-and-producer-journal-seal',
      owner: 'producer-journal',
      phases: R4_TRUSTED_PROOF_FIXED_PRODUCER_JOURNAL_FINAL_PHASES,
    });
    expect(Object.isFrozen(schedule)).toBe(true);
    expect(Object.isFrozen(schedule[0])).toBe(true);
  });

  test('assigns every Node/C# authority action once and fixes each role capture surface', () => {
    const schedule = fixedEnrollmentSchedule();
    const owners = new Map<string, string>();
    for (const step of schedule) {
      expect(owners.has(step.authority_action)).toBe(false);
      owners.set(step.authority_action, step.owner);
    }
    expect(owners.get('advance-stale-ref')).toBe('producer-journal');
    expect(owners.get('advance-stale-readback')).toBe('node-executor');
    expect(schedule.filter((step) => step.stage === 'produce').map((step) => step.id)).toEqual(
      R4_TRUSTED_PROOF_FIXED_PRODUCER_TARGET_STEP_IDS,
    );
    expect(
      schedule
        .filter((step) =>
          R4_TRUSTED_PROOF_FIXED_PRODUCER_JOURNAL_LIFECYCLE_ACTIONS.includes(step.id),
        )
        .map((step) => step.authority_action),
    ).toEqual(R4_TRUSTED_PROOF_FIXED_PRODUCER_JOURNAL_LIFECYCLE_ACTIONS);
    for (const role of R4_TRUSTED_PROOF_ENROLLMENT_ROLES) {
      const capture = schedule.find((step) => step.id === `${role}-capture`)!;
      expect(capture.owner).toBe('phase-fragment-materializer');
      expect(capture.capture_requirements).toEqual(
        R4_TRUSTED_PROOF_ROLE_CAPTURE_REQUIREMENTS[role],
      );
    }
  });

  test('locally rehearses fake existing primitive outcomes in the one allowed order', () => {
    const receipt = rehearseFixedEnrollmentSchedule({ completed: fakePrimitiveTrace() });
    expect(receipt).toMatchObject({
      kind: 'apr-r4-e2p-fixed-four-role-schedule-v2',
      recovery: false,
      role_run_ids: {
        'normal-bootstrap': '8101',
        'normal-continuation': '8102',
        'stale-protected': '8103',
        'stale-follow-on': '8104',
      },
    });
    expect(receipt.steps).toHaveLength(fixedEnrollmentSchedule().length);
  });

  test('fails closed on missing capture, wrong owner, early advance/seal/cleanup, and a replay', () => {
    const trace = fakePrimitiveTrace();
    expect(() =>
      rehearseFixedEnrollmentSchedule({ completed: trace.filter((_, index) => index !== 8) }),
    ).toThrow(/recovery-required/u);

    const wrongOwner = fakePrimitiveTrace();
    wrongOwner[17] = { ...wrongOwner[17], owner: 'node-executor' };
    expect(() => rehearseFixedEnrollmentSchedule({ completed: wrongOwner })).toThrow(/trace-step/u);

    const earlyAdvance = fakePrimitiveTrace();
    [earlyAdvance[16], earlyAdvance[17]] = [earlyAdvance[17], earlyAdvance[16]];
    expect(() => rehearseFixedEnrollmentSchedule({ completed: earlyAdvance })).toThrow(
      /trace-(shape|step)/u,
    );

    const earlySeal = fakePrimitiveTrace();
    [earlySeal[20], earlySeal[21]] = [earlySeal[21], earlySeal[20]];
    expect(() => rehearseFixedEnrollmentSchedule({ completed: earlySeal })).toThrow(
      /trace-(shape|step)/u,
    );

    const earlyCleanup = fakePrimitiveTrace();
    [earlyCleanup[21], earlyCleanup[22]] = [earlyCleanup[22], earlyCleanup[21]];
    expect(() => rehearseFixedEnrollmentSchedule({ completed: earlyCleanup })).toThrow(
      /trace-(shape|step)/u,
    );

    const replay = fakePrimitiveTrace();
    expect(() =>
      rehearseFixedEnrollmentSchedule({ completed: trace.slice(0, 14), resumed: replay.slice(13) }),
    ).toThrow(/trace-shape/u);
  });

  test('permits only an exact-prefix recovery, never a partial restart or replay', () => {
    const trace = fakePrimitiveTrace();
    expect(() => rehearseFixedEnrollmentSchedule({ completed: trace.slice(0, 17) })).toThrow(
      /recovery-required/u,
    );
    expect(() => rehearseFixedEnrollmentSchedule({ completed: [], resumed: trace })).toThrow(
      /recovery-prefix/u,
    );
    const recovery = rehearseFixedEnrollmentSchedule({
      completed: trace.slice(0, 17),
      resumed: trace.slice(17),
    });
    expect(recovery.recovery).toBe(true);

    const replay = fakePrimitiveTrace();
    expect(() =>
      rehearseFixedEnrollmentSchedule({ completed: trace.slice(0, 17), resumed: replay.slice(16) }),
    ).toThrow(/trace-step/u);

    const wrongRun = fakePrimitiveTrace();
    wrongRun[20] = { ...wrongRun[20], runtime_run_id: '9999' };
    expect(() => rehearseFixedEnrollmentSchedule({ completed: wrongRun })).toThrow(
      /trace-role-readback/u,
    );
  });
});
