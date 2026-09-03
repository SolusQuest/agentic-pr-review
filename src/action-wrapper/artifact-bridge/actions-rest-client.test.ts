import { getOctokit } from '@actions/github';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { createArtifactActionsRestClient } from './actions-rest-client.js';
import {
  ArtifactRestRequestBudget,
  type ArtifactRestRequestBudgetProfile,
  type ArtifactRestSecondaryRateLimitOptions,
  TRUSTED_PROOF_ARTIFACT_REST_REQUEST_LIMITS,
} from './artifact-rest-request-budget.js';
import {
  TRUSTED_PROOF_FINAL_ARTIFACT_REST_REQUEST_BUDGET_PROFILE,
  TRUSTED_PROOF_FINAL_CONTINUATION_ARTIFACT_REST_REQUEST_BUDGET_PROFILE,
  TRUSTED_PROOF_MEASUREMENT_ARTIFACT_REST_REQUEST_BUDGET_PROFILE,
  TRUSTED_PROOF_NORMAL_PROCESS_PRIMARY_RESERVE,
  TRUSTED_PROOF_OPERATION_PRIMARY_RESERVE,
  TRUSTED_PROOF_UNCOORDINATED_PRIMARY_HEADROOM,
} from '../launcher/request-budget-profile.js';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('bounded artifact archive acquisition', () => {
  it('returns only an in-bound fixed-route archive', async () => {
    const bytes = Buffer.from('zip');
    const fetchMock = vi.fn(
      async () =>
        new Response(bytes, {
          status: 200,
          headers: { 'content-length': String(bytes.length) },
        }),
    );
    vi.stubGlobal('fetch', fetchMock);
    const client = createArtifactActionsRestClient(octokitWithRedirect(), nonProofBudget());

    const result = await client.downloadArtifactArchive(
      {
        owner: 'owner',
        repo: 'repo',
        artifact_id: 1,
        maximum_bytes: bytes.length,
      },
      new AbortController().signal,
    );

    expect(result).toEqual({ status: 200, data: bytes });
    expect(fetchMock).toHaveBeenCalledWith(
      'https://blob.invalid/archive',
      expect.objectContaining({ method: 'GET', redirect: 'error' }),
    );
  });

  it('cancels at cap plus one before any archive can be staged', async () => {
    const cancelled = vi.fn();
    const stream = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(Buffer.alloc(4, 1));
        controller.enqueue(Buffer.of(2));
      },
      cancel: cancelled,
    });
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(stream, { status: 200 })),
    );
    const client = createArtifactActionsRestClient(octokitWithRedirect(), nonProofBudget());

    await expect(
      client.downloadArtifactArchive(
        {
          owner: 'owner',
          repo: 'repo',
          artifact_id: 1,
          maximum_bytes: 4,
        },
        new AbortController().signal,
      ),
    ).rejects.toThrow('artifact_archive_download_failed');
    expect(cancelled).toHaveBeenCalledOnce();
  });
});

describe('trusted proof artifact REST budget', () => {
  it('admits the historical continuation tail and a changed response with live quota', async () => {
    let now = 0;
    let remaining = 1_000;
    let dispatched = 0;
    const budget = ArtifactRestRequestBudget.forVerifiedPreparedPayload({
      buildDiscriminator: 'r4-w2',
      identity: trustedProofIdentity(),
      profile: TRUSTED_PROOF_FINAL_CONTINUATION_ARTIFACT_REST_REQUEST_BUDGET_PROFILE,
      secondaryRateLimit: {
        now: () => now,
        sleep: async (milliseconds) => {
          now += milliseconds;
        },
      },
    });
    const observe = async (status: 200 | 304) =>
      await budget.runAuthenticatedApiCall(
        { signal: signal(), secondaryLimitPoints: 1, mutative: false },
        async () => {
          dispatched += 1;
          if (status === 200) remaining -= 1;
          return { status, headers: { 'x-ratelimit-remaining': String(remaining) } };
        },
      );

    for (let index = 0; index < 1_929; index += 1) await observe(304);
    for (let index = 0; index < 136; index += 1) await observe(200);
    expect(budget.receipt()).toMatchObject({
      total_authenticated_api_requests: 2_065,
      conditional_not_modified_requests: 1_929,
      primary_rate_limit_requests: 136,
      disposition: 'active',
    });

    for (let index = 0; index < 65; index += 1) await observe(304);
    await observe(200);
    expect(dispatched).toBe(2_131);
    expect(budget.receipt()).toMatchObject({
      total_authenticated_api_requests: 2_131,
      conditional_not_modified_requests: 1_994,
      primary_rate_limit_requests: 137,
      secondary_limit_points: 2_131,
      disposition: 'active',
    });
  });

  it('uses the r4-w2 measurement profile with explicit 2304 raw and 256 primary caps', () => {
    expect(TRUSTED_PROOF_ARTIFACT_REST_REQUEST_LIMITS).toEqual({
      maximumTotalAuthenticatedApiRequests: 256,
      maximumPrimaryRateLimitRequests: 256,
    });
    expect(trustedProofBudget().receipt()).toMatchObject({
      maximum_total_authenticated_api_requests: 2304,
      maximum_primary_rate_limit_requests: 256,
      measurement_only: true,
      remaining_tail_required: 0,
      remaining_tail_reserve: 1,
    });
  });

  it.each([
    { name: 'raw', limit: 4_096, status: 304, disposition: 'total_exhausted' },
    { name: 'primary', limit: 256, status: 200, disposition: 'primary_exhausted' },
  ])('enforces the rounded final $name ceiling before wire dispatch', async (testCase) => {
    let now = 0;
    let dispatched = 0;
    const budget = ArtifactRestRequestBudget.forVerifiedPreparedPayload({
      buildDiscriminator: 'r4-w2',
      identity: trustedProofIdentity(),
      profile: TRUSTED_PROOF_FINAL_ARTIFACT_REST_REQUEST_BUDGET_PROFILE,
      secondaryRateLimit: {
        now: () => now,
        sleep: async (milliseconds) => {
          now += milliseconds;
        },
      },
    });
    const observe = async () =>
      await budget.runAuthenticatedApiCall(
        { signal: signal(), secondaryLimitPoints: 1, mutative: false },
        async () => {
          dispatched += 1;
          return {
            status: testCase.status,
            headers: {
              'x-ratelimit-remaining': String(testCase.status === 304 ? 1_000 : 1_000 - dispatched),
            },
          };
        },
      );
    for (let index = 0; index < testCase.limit; index += 1) await observe();
    expect(budget.receipt().disposition).toBe('active');
    await expect(observe()).rejects.toThrow(
      `trusted_proof_artifact_rest_budget_${testCase.disposition}`,
    );
    expect(dispatched).toBe(testCase.limit);
    expect(budget.receipt()).toMatchObject({
      total_authenticated_api_requests: testCase.limit,
      disposition: testCase.disposition,
    });
  });

  it('uses the final safety ceilings and coordination margin without a measured Node tail', () => {
    const budget = ArtifactRestRequestBudget.forVerifiedPreparedPayload({
      buildDiscriminator: 'r4-w2',
      identity: trustedProofIdentity(),
      profile: TRUSTED_PROOF_FINAL_ARTIFACT_REST_REQUEST_BUDGET_PROFILE,
    });
    expect(budget.receipt()).toMatchObject({
      maximum_total_authenticated_api_requests: 4_096,
      maximum_primary_rate_limit_requests: 256,
      remaining_tail_required: 0,
      remaining_tail_reserve: 80,
      measurement_only: false,
    });
    expect(TRUSTED_PROOF_NORMAL_PROCESS_PRIMARY_RESERVE).toBe(
      TRUSTED_PROOF_OPERATION_PRIMARY_RESERVE + TRUSTED_PROOF_UNCOORDINATED_PRIMARY_HEADROOM,
    );
  });

  it('allows 2304 measured raw requests and rejects the 2305th before wire dispatch', async () => {
    let now = 0;
    const budget = trustedProofBudget(undefined, {
      now: () => now,
      sleep: async (milliseconds) => {
        now += milliseconds;
      },
    });
    for (let index = 0; index < 2304; index += 1) {
      await runNotModified(budget);
    }

    await expect(runNotModified(budget)).rejects.toThrow(
      'trusted_proof_artifact_rest_budget_total_exhausted',
    );
    expect(budget.receipt()).toMatchObject({
      maximum_total_authenticated_api_requests: 2304,
      total_authenticated_api_requests: 2304,
      maximum_primary_rate_limit_requests: 256,
      primary_rate_limit_requests: 0,
      conditional_not_modified_requests: 2304,
      remaining_tail_required: 0,
      remaining_tail_reserve: 1,
      disposition: 'total_exhausted',
    });
  });

  it('allows 256 primary charges and rejects the 257th before the raw cap', async () => {
    let now = 0;
    const budget = trustedProofBudget(undefined, {
      now: () => now,
      sleep: async (milliseconds) => {
        now += milliseconds;
      },
    });
    for (let index = 0; index < 256; index += 1) {
      await runGet(budget);
    }

    await expect(runGet(budget)).rejects.toThrow(
      'trusted_proof_artifact_rest_budget_primary_exhausted',
    );
    expect(budget.receipt()).toMatchObject({
      maximum_total_authenticated_api_requests: 2304,
      total_authenticated_api_requests: 256,
      maximum_primary_rate_limit_requests: 256,
      primary_rate_limit_requests: 256,
      remaining_tail_required: 0,
      remaining_tail_reserve: 1,
      disposition: 'primary_exhausted',
    });
  });

  it('advances a frozen suffix after charged responses and preserves the exact reserve', async () => {
    const profile = {
      ...TRUSTED_PROOF_MEASUREMENT_ARTIFACT_REST_REQUEST_BUDGET_PROFILE,
      remainingTailRequired: 3,
      remainingTailReserve: 5,
      measurementOnly: false,
    };
    const budget = () =>
      ArtifactRestRequestBudget.forVerifiedPreparedPayload({
        buildDiscriminator: 'r4-w2',
        identity: trustedProofIdentity(),
        profile,
      });
    const observeRemaining = async (value: ArtifactRestRequestBudget, remaining: number) => {
      await value.runAuthenticatedApiCall(
        { signal: signal(), secondaryLimitPoints: 1, mutative: false },
        async () => ({ status: 200, headers: { 'x-ratelimit-remaining': String(remaining) } }),
      );
    };

    const atBoundary = budget();
    await observeRemaining(atBoundary, 8);
    expect(atBoundary.receipt()).toMatchObject({ disposition: 'active' });
    await expect(runGet(atBoundary)).resolves.toMatchObject({ status: 200 });

    const belowBoundary = budget();
    await observeRemaining(belowBoundary, 7);
    expect(belowBoundary.receipt()).toMatchObject({ disposition: 'primary_exhausted' });
    await expect(runGet(belowBoundary)).rejects.toThrow(
      'trusted_proof_artifact_rest_budget_primary_exhausted',
    );

    const allocation = budget();
    await observeRemaining(allocation, 9);
    expect(() => allocation.requireObservedPrimaryAllocation(1)).not.toThrow();

    const mutation = budget();
    await observeRemaining(mutation, 9);
    expect(() =>
      mutation.reserveMutation({
        authenticatedRequests: 1,
        primaryRequests: 1,
        secondaryPoints: 1,
      }),
    ).not.toThrow();

    const dispatch = budget();
    await observeRemaining(dispatch, 9);
    await expect(runGet(dispatch)).resolves.toMatchObject({ status: 200 });
    expect(dispatch.receipt()).toMatchObject({
      measurement_only: false,
      remaining_tail_required: 3,
      remaining_tail_reserve: 5,
    });

    const frozenFinal = ArtifactRestRequestBudget.forVerifiedPreparedPayload({
      buildDiscriminator: 'r4-w2',
      identity: trustedProofIdentity(),
      profile: TRUSTED_PROOF_FINAL_ARTIFACT_REST_REQUEST_BUDGET_PROFILE,
    });
    await observeRemaining(frozenFinal, 743);
    expect(frozenFinal.receipt()).toMatchObject({ disposition: 'active' });
    await expect(runGet(frozenFinal)).resolves.toMatchObject({ status: 200 });

    const frozenFinalEquality = ArtifactRestRequestBudget.forVerifiedPreparedPayload({
      buildDiscriminator: 'r4-w2',
      identity: trustedProofIdentity(),
      profile: TRUSTED_PROOF_FINAL_ARTIFACT_REST_REQUEST_BUDGET_PROFILE,
    });
    await observeRemaining(frozenFinalEquality, 744);
    await expect(runGet(frozenFinalEquality)).resolves.toMatchObject({ status: 200 });
  });

  it('does not advance the protected primary suffix on a 304', async () => {
    const profile = {
      ...TRUSTED_PROOF_MEASUREMENT_ARTIFACT_REST_REQUEST_BUDGET_PROFILE,
      remainingTailRequired: 2,
      remainingTailReserve: 1,
      measurementOnly: false,
    };
    const budget = ArtifactRestRequestBudget.forVerifiedPreparedPayload({
      buildDiscriminator: 'r4-w2',
      identity: trustedProofIdentity(),
      profile,
    });
    await budget.runAuthenticatedApiCall(
      { signal: signal(), secondaryLimitPoints: 1, mutative: false },
      async () => ({ status: 200, headers: { 'x-ratelimit-remaining': '3' } }),
    );
    await budget.runAuthenticatedApiCall(
      { signal: signal(), secondaryLimitPoints: 1, mutative: false },
      async () => ({ status: 304, headers: { 'x-ratelimit-remaining': '3' } }),
    );

    await expect(runGet(budget)).resolves.toMatchObject({ status: 200 });
    await expect(runGet(budget)).resolves.toMatchObject({ status: 200 });
    await expect(runGet(budget)).rejects.toThrow(
      'trusted_proof_artifact_rest_budget_primary_exhausted',
    );
    expect(budget.receipt()).toMatchObject({
      primary_rate_limit_requests: 3,
      conditional_not_modified_requests: 1,
    });
  });

  it('uses a lower 304 header as shared-token progress and never lets a later higher header lift it', async () => {
    const profile = {
      ...TRUSTED_PROOF_MEASUREMENT_ARTIFACT_REST_REQUEST_BUDGET_PROFILE,
      remainingTailRequired: 9,
      remainingTailReserve: 2,
      measurementOnly: false,
    };
    const budget = ArtifactRestRequestBudget.forVerifiedPreparedPayload({
      buildDiscriminator: 'r4-w2',
      identity: trustedProofIdentity(),
      profile,
    });
    const dispatch = { signal: signal(), secondaryLimitPoints: 1, mutative: false } as const;

    await budget.runAuthenticatedApiCall(dispatch, async () => ({
      status: 200,
      headers: { 'x-ratelimit-remaining': '20' },
    }));
    await budget.runAuthenticatedApiCall(dispatch, async () => ({
      status: 304,
      headers: { 'x-ratelimit-remaining': '8' },
    }));
    await budget.runAuthenticatedApiCall(dispatch, async () => ({
      status: 200,
      headers: { 'x-ratelimit-remaining': '19' },
    }));

    expect(budget.receipt()).toMatchObject({ disposition: 'active' });
    await expect(runGet(budget)).resolves.toMatchObject({ status: 200 });
  });

  it.each([
    { maximumTotalAuthenticatedApiRequests: 0, maximumPrimaryRateLimitRequests: 1 },
    { maximumTotalAuthenticatedApiRequests: 1, maximumPrimaryRateLimitRequests: 0 },
    { maximumTotalAuthenticatedApiRequests: 1.5, maximumPrimaryRateLimitRequests: 1 },
  ])('rejects an invalid measurement limit configuration', (limits) => {
    expect(() =>
      ArtifactRestRequestBudget.forVerifiedPreparedPayload({
        buildDiscriminator: 'r4-w2',
        profile: {
          ...TRUSTED_PROOF_MEASUREMENT_ARTIFACT_REST_REQUEST_BUDGET_PROFILE,
          limits,
        },
      }),
    ).toThrow('artifact_rest_request_budget_limits_invalid');
  });

  it('rejects a protected caller limit override instead of reporting a mismatched profile', () => {
    expect(() =>
      ArtifactRestRequestBudget.forVerifiedPreparedPayload({
        buildDiscriminator: 'r4-w2',
        identity: trustedProofIdentity(),
        limits: { maximumTotalAuthenticatedApiRequests: 2, maximumPrimaryRateLimitRequests: 2 },
        profile: TRUSTED_PROOF_MEASUREMENT_ARTIFACT_REST_REQUEST_BUDGET_PROFILE,
      }),
    ).toThrow('artifact_rest_request_budget_profile_invalid');
  });

  it.each([
    {
      buildDiscriminator: 'r4-w2',
      identity: trustedProofIdentity(),
      profile: undefined,
    },
    {
      buildDiscriminator: 'r4-h1',
      identity: undefined,
      profile: TRUSTED_PROOF_MEASUREMENT_ARTIFACT_REST_REQUEST_BUDGET_PROFILE,
    },
  ])('admits a budget profile only for the verified protected route', (input) => {
    expect(() => ArtifactRestRequestBudget.forVerifiedPreparedPayload(input)).toThrow(
      'artifact_rest_request_budget_profile_invalid',
    );
  });

  it.each([
    {},
    { ...trustedProofIdentity(), payloadSha256: 'C'.repeat(64) },
    { ...trustedProofIdentity(), runAttempt: '0' },
  ])('requires exact verified identity for the protected receipt', (identity) => {
    expect(() =>
      ArtifactRestRequestBudget.forVerifiedPreparedPayload({
        buildDiscriminator: 'r4-w2',
        identity: identity as ReturnType<typeof trustedProofIdentity>,
        profile: TRUSTED_PROOF_MEASUREMENT_ARTIFACT_REST_REQUEST_BUDGET_PROFILE,
      }),
    ).toThrow('artifact_rest_request_budget_identity_invalid');
  });

  it.each([
    { maximumEntries: 0, maximumBytes: 1 },
    { maximumEntries: 1, maximumBytes: 0 },
    { maximumEntries: 1.5, maximumBytes: 1 },
  ])('rejects an invalid conditional cache limit configuration', (cacheLimits) => {
    expect(() =>
      createArtifactActionsRestClient(
        octokitWithArtifactMethods({}),
        nonProofBudget(),
        cacheLimits,
      ),
    ).toThrow('conditional_get_cache_limits_invalid');
  });

  it('fails closed at the raw authenticated request cap before dispatch', async () => {
    const listArtifactsForRepo = vi.fn(async () => ({
      status: 200,
      data: { artifacts: [] },
    }));
    const budget = trustedProofBudget({ total: 2, primary: 2 });
    const client = createArtifactActionsRestClient(
      octokitWithArtifactMethods({ listArtifactsForRepo }),
      budget,
    );

    for (let index = 0; index < 2; index += 1) {
      await client.listArtifactsForRepo(listInput(), signal());
    }
    await expect(client.listArtifactsForRepo(listInput(), signal())).rejects.toThrow(
      'trusted_proof_artifact_rest_budget_total_exhausted',
    );

    expect(listArtifactsForRepo).toHaveBeenCalledTimes(2);
    expect(budget.receipt()).toEqual({
      kind: 'apr-r4-trusted-proof-artifact-rest-budget-v2',
      protected_route: true,
      maximum_total_authenticated_api_requests: 2,
      total_authenticated_api_requests: 2,
      maximum_primary_rate_limit_requests: 2,
      primary_rate_limit_requests: 2,
      conditional_not_modified_requests: 0,
      secondary_limit_points: 2,
      permission_denied: 0,
      remaining_total_authenticated_api_requests: 0,
      remaining_primary_rate_limit_requests: 0,
      disposition: 'total_exhausted',
      repository: 'owner/repo',
      repository_id: '1',
      workflow_sha: 'a'.repeat(40),
      action_source_sha: 'b'.repeat(40),
      payload_sha256: 'c'.repeat(64),
      build_discriminator: 'r4-w2',
      run_id: '2',
      run_attempt: '1',
      cap_profile: 'apr-r4-artifact-rest-request-budget-v2',
      measurement_only: true,
      remaining_tail_required: 0,
      remaining_tail_reserve: 1,
    });
  });

  it('serializes concurrent protected dispatches FIFO through the shared executor budget', async () => {
    const observed: number[] = [];
    let active = 0;
    let maximumActive = 0;
    const getArtifact = vi.fn(async (input: { readonly artifact_id: number }) => {
      observed.push(input.artifact_id);
      active += 1;
      maximumActive = Math.max(maximumActive, active);
      await Promise.resolve();
      active -= 1;
      return { status: 200, data: artifact() };
    });
    const client = createArtifactActionsRestClient(
      octokitWithArtifactMethods({ getArtifact }),
      trustedProofBudget({ total: 8, primary: 8 }),
    );

    await Promise.all(
      [3, 1, 2].map(async (artifact_id) => {
        await client.getArtifact({ owner: 'owner', repo: 'repo', artifact_id }, signal());
      }),
    );

    expect(observed).toEqual([3, 1, 2]);
    expect(maximumActive).toBe(1);
  });

  it('enforces rolling secondary points and mutative spacing with an injectable clock', async () => {
    let now = 0;
    const waits: number[] = [];
    const rateLimit: ArtifactRestSecondaryRateLimitOptions = {
      now: () => now,
      sleep: async (milliseconds) => {
        waits.push(milliseconds);
        now += milliseconds;
      },
      maximumPointsPerRollingMinute: 5,
      minimumMutativeSpacingMs: 1_000,
    };
    const budget = trustedProofBudget({ total: 16, primary: 16 }, rateLimit);

    await runGet(budget);
    await runGet(budget);
    await runGet(budget);
    await runGet(budget);
    await runGet(budget);
    await runGet(budget);

    expect(waits).toEqual([100, 100, 100, 100, 59_600]);
    expect(budget.receipt().secondary_limit_points).toBe(6);

    const mutationBudget = trustedProofBudget(
      { total: 4, primary: 4 },
      {
        now: () => now,
        sleep: async (milliseconds) => {
          waits.push(milliseconds);
          now += milliseconds;
        },
        maximumPointsPerRollingMinute: 20,
        minimumMutativeSpacingMs: 1_000,
      },
    );
    await mutationBudget.runAuthenticatedApiCall(
      { signal: signal(), secondaryLimitPoints: 5, mutative: true },
      async () => ({ status: 204 }),
    );
    await mutationBudget.runAuthenticatedApiCall(
      { signal: signal(), secondaryLimitPoints: 5, mutative: true },
      async () => ({ status: 204 }),
    );

    expect(waits).toEqual([100, 100, 100, 100, 59_600, 1_000]);
    expect(mutationBudget.receipt().secondary_limit_points).toBe(10);
  });

  it('waits for rolling secondary capacity before dispatching a fully reserved mutation', async () => {
    let now = 0;
    const waits: number[] = [];
    const budget = trustedProofBudget(
      { total: 1_024, primary: 1_024 },
      {
        now: () => now,
        sleep: async (milliseconds) => {
          waits.push(milliseconds);
          now += milliseconds;
        },
        maximumPointsPerRollingMinute: 600,
      },
    );
    for (let index = 0; index < 599; index += 1) {
      await runGet(budget);
    }
    const reservation = budget.reserveMutation({
      authenticatedRequests: 3,
      primaryRequests: 3,
      secondaryPoints: 8,
    });
    let wireDispatches = 0;

    await budget.runReservedMutationDataPlaneCall(
      {
        signal: signal(),
        secondaryLimitPoints: 5,
        mutative: true,
        latestAttemptStartAt: 120_000,
      },
      async (markDispatched) => {
        markDispatched();
        wireDispatches += 1;
        return { status: 201 };
      },
    );
    reservation.release();

    expect(wireDispatches).toBe(1);
    expect(waits.at(-1)).toBe(500);
    expect(budget.receipt().disposition).toBe('active');
  });

  it('rejects a mutation before its wire dispatch when pacing misses the command deadline', async () => {
    let now = 0;
    const budget = trustedProofBudget(
      { total: 1_024, primary: 1_024 },
      {
        now: () => now,
        sleep: async (milliseconds) => {
          now += milliseconds;
        },
        maximumPointsPerRollingMinute: 600,
      },
    );
    for (let index = 0; index < 599; index += 1) {
      await runGet(budget);
    }
    const reservation = budget.reserveMutation({
      authenticatedRequests: 3,
      primaryRequests: 3,
      secondaryPoints: 8,
    });
    let wireDispatches = 0;

    await expect(
      budget.runReservedMutationDataPlaneCall(
        {
          signal: signal(),
          secondaryLimitPoints: 5,
          mutative: true,
          latestAttemptStartAt: now + 100,
        },
        async (markDispatched) => {
          markDispatched();
          wireDispatches += 1;
          return { status: 201 };
        },
      ),
    ).rejects.toThrow('artifact_rest_attempt_deadline_exceeded');
    reservation.release();

    expect(wireDispatches).toBe(0);
    expect(budget.receipt().disposition).toBe('active');
  });

  it('removes a cancelled FIFO waiter without recording a REST dispatch', async () => {
    let now = 0;
    const rateLimit: ArtifactRestSecondaryRateLimitOptions = {
      now: () => now,
      sleep: async (milliseconds, requestSignal) => {
        await new Promise<void>((_resolve, reject) => {
          requestSignal.addEventListener(
            'abort',
            () => reject(Object.assign(new Error('cancelled'), { name: 'AbortError' })),
            { once: true },
          );
        });
        now += milliseconds;
      },
      maximumPointsPerRollingMinute: 1,
    };
    const budget = trustedProofBudget({ total: 4, primary: 4 }, rateLimit);
    await runGet(budget);
    const controller = new AbortController();
    const pending = budget.runAuthenticatedApiCall(
      { signal: controller.signal, secondaryLimitPoints: 1, mutative: false },
      async () => ({ status: 200 }),
    );
    await Promise.resolve();
    controller.abort();

    await expect(pending).rejects.toMatchObject({ name: 'AbortError' });
    expect(budget.receipt()).toMatchObject({
      total_authenticated_api_requests: 1,
      secondary_limit_points: 1,
      disposition: 'active',
    });
  });

  it('shares one ledger across commands and excludes the anonymous signed download', async () => {
    const bytes = Buffer.from('zip');
    const fetchMock = vi.fn(async () => new Response(bytes, { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);
    const methods = {
      listArtifactsForRepo: vi.fn(async () => ({ status: 200, data: { artifacts: [] } })),
      getArtifact: vi.fn(async () => ({ status: 200, data: artifact() })),
      downloadArtifact: vi.fn(async () => ({
        status: 302,
        headers: { location: 'https://blob.invalid/archive?sig=opaque' },
      })),
      getWorkflowRunAttempt: vi.fn(async () => ({ status: 200, data: {} })),
      deleteArtifact: vi.fn(async () => ({ status: 204, data: undefined })),
    };
    const budget = trustedProofBudget();
    const client = createArtifactActionsRestClient(octokitWithArtifactMethods(methods), budget);

    await client.listArtifactsForRepo(listInput(), signal());
    await client.getArtifact(artifactInput(), signal());
    await client.downloadArtifactArchive({ ...artifactInput(), maximum_bytes: 16 }, signal());
    await client.getWorkflowRunAttempt(
      { owner: 'owner', repo: 'repo', run_id: 9, attempt_number: 1 },
      signal(),
    );
    await client.deleteArtifact(artifactInput(), signal());

    expect(methods.downloadArtifact).toHaveBeenCalledOnce();
    expect(fetchMock).toHaveBeenCalledWith(
      'https://blob.invalid/archive?sig=opaque',
      expect.not.objectContaining({ headers: expect.anything() }),
    );
    expect(budget.receipt()).toMatchObject({
      total_authenticated_api_requests: 5,
      primary_rate_limit_requests: 5,
      conditional_not_modified_requests: 0,
      remaining_total_authenticated_api_requests:
        TRUSTED_PROOF_MEASUREMENT_ARTIFACT_REST_REQUEST_BUDGET_PROFILE.limits
          .maximumTotalAuthenticatedApiRequests - 5,
      disposition: 'active',
    });
  });

  it('makes a real conditional GET, reuses only a 304 representation, and accounts it separately', async () => {
    const getArtifact = vi
      .fn()
      .mockResolvedValueOnce({
        status: 200,
        headers: { etag: '"artifact-v1"' },
        data: artifact(),
      })
      .mockRejectedValueOnce(
        Object.assign(new Error('not modified'), {
          status: 304,
          response: { status: 304, headers: {} },
        }),
      );
    const budget = trustedProofBudget({ total: 3, primary: 2 });
    const client = createArtifactActionsRestClient(
      octokitWithArtifactMethods({ getArtifact }),
      budget,
    );

    await expect(client.getArtifact(artifactInput(), signal())).resolves.toMatchObject({
      status: 200,
      data: artifact(),
    });
    await expect(client.getArtifact(artifactInput(), signal())).resolves.toMatchObject({
      status: 200,
      data: artifact(),
    });

    expect(getArtifact).toHaveBeenCalledTimes(2);
    expect(getArtifact.mock.calls[1]?.[0]).toMatchObject({
      headers: { 'if-none-match': '"artifact-v1"' },
      request: { signal: expect.anything() },
    });
    expect(budget.receipt()).toMatchObject({
      total_authenticated_api_requests: 2,
      primary_rate_limit_requests: 1,
      conditional_not_modified_requests: 1,
      remaining_total_authenticated_api_requests: 1,
      remaining_primary_rate_limit_requests: 1,
      disposition: 'active',
    });
  });

  it.each([
    ['wildcard', '*'],
    ['unquoted token', 'artifact-v1'],
    ['entity-tag list', '"artifact-v1", "artifact-v2"'],
    ['lowercase weak prefix', 'w/"artifact-v1"'],
    ['unterminated tag', '"artifact-v1'],
    ['duplicate field values', ['"artifact-v1"', '"artifact-v2"']],
  ])('never replays a malformed %s ETag as If-None-Match', async (_description, etag) => {
    const getArtifact = vi.fn(async (_request: { headers?: Record<string, string> }) => ({
      status: 200,
      headers: { etag },
      data: artifact(),
    }));
    const client = createArtifactActionsRestClient(
      octokitWithArtifactMethods({ getArtifact }),
      nonProofBudget(),
    );

    await client.getArtifact(artifactInput(), signal());
    await client.getArtifact(artifactInput(), signal());

    expect(getArtifact).toHaveBeenCalledTimes(2);
    expect(getArtifact.mock.calls[1]?.[0].headers).toBeUndefined();
  });

  it.each(['W/"artifact-v1"', '"artifact,opaque"', '""'])(
    'replays one exact RFC entity-tag validator: %s',
    async (etag) => {
      const getArtifact = vi
        .fn()
        .mockResolvedValueOnce({ status: 200, headers: { etag }, data: artifact() })
        .mockResolvedValueOnce({ status: 304, data: artifact() });
      const client = createArtifactActionsRestClient(
        octokitWithArtifactMethods({ getArtifact }),
        nonProofBudget(),
      );

      await client.getArtifact(artifactInput(), signal());
      await client.getArtifact(artifactInput(), signal());

      expect(getArtifact.mock.calls[1]?.[0]).toMatchObject({
        headers: { 'if-none-match': etag },
      });
    },
  );

  it('sends conditional headers on the real Octokit wire and reuses 304 data', async () => {
    const requests: Array<{ readonly url: string; readonly headers: HeadersInit | undefined }> = [];
    const counts = new Map<string, number>();
    const fetch: typeof globalThis.fetch = async (input, init) => {
      const url = String(input);
      const count = counts.get(url) ?? 0;
      counts.set(url, count + 1);
      requests.push({ url, headers: init?.headers });
      if (count > 0) return new Response(null, { status: 304 });
      const data = url.includes('/actions/artifacts/')
        ? artifact()
        : url.includes('/actions/artifacts')
          ? { total_count: 0, artifacts: [] }
          : { id: 900, run_attempt: 1 };
      return new Response(JSON.stringify(data), {
        status: 200,
        headers: { 'content-type': 'application/json', etag: '"fixture-v1"' },
      });
    };
    const octokit = getOctokit('synthetic-token', {
      baseUrl: 'https://api.fixture.invalid',
      request: { fetch },
    });
    const budget = trustedProofBudget({ total: 8, primary: 8 });
    const client = createArtifactActionsRestClient(octokit, budget);

    await client.listArtifactsForRepo(listInput(), signal());
    await client.listArtifactsForRepo(listInput(), signal());
    await client.getArtifact(artifactInput(), signal());
    await client.getArtifact(artifactInput(), signal());
    await client.getWorkflowRunAttempt(
      { owner: 'owner', repo: 'repo', run_id: 900, attempt_number: 1 },
      signal(),
    );
    await client.getWorkflowRunAttempt(
      { owner: 'owner', repo: 'repo', run_id: 900, attempt_number: 1 },
      signal(),
    );

    expect(requests).toHaveLength(6);
    expect(requests.slice(1).filter((_, index) => index % 2 === 0)).toEqual([
      expect.objectContaining({
        headers: expect.objectContaining({ 'if-none-match': '"fixture-v1"' }),
      }),
      expect.objectContaining({
        headers: expect.objectContaining({ 'if-none-match': '"fixture-v1"' }),
      }),
      expect.objectContaining({
        headers: expect.objectContaining({ 'if-none-match': '"fixture-v1"' }),
      }),
    ]);
    expect(budget.receipt()).toMatchObject({
      total_authenticated_api_requests: 6,
      primary_rate_limit_requests: 3,
      conditional_not_modified_requests: 3,
    });
  });

  it('does not dispatch a possibly charged conditional GET after the primary cap is reserved', async () => {
    const getArtifact = vi.fn(
      async (_input: {
        readonly headers?: unknown;
        readonly request?: { readonly headers?: unknown };
      }) => ({
        status: 200,
        headers: { etag: '"artifact-v1"' },
        data: artifact(),
      }),
    );
    const budget = trustedProofBudget({ total: 3, primary: 1 });
    const client = createArtifactActionsRestClient(
      octokitWithArtifactMethods({ getArtifact }),
      budget,
    );

    await client.getArtifact(artifactInput(), signal());
    await expect(client.getArtifact(artifactInput(), signal())).rejects.toThrow(
      'trusted_proof_artifact_rest_budget_primary_exhausted',
    );

    expect(getArtifact).toHaveBeenCalledOnce();
    expect(budget.receipt()).toMatchObject({
      total_authenticated_api_requests: 1,
      primary_rate_limit_requests: 1,
      conditional_not_modified_requests: 0,
      disposition: 'primary_exhausted',
    });
  });

  it('invalidates a cached GET after a remote 404 and does not reuse it on a later observation', async () => {
    const getArtifact = vi
      .fn()
      .mockResolvedValueOnce({
        status: 200,
        headers: { etag: '"artifact-v1"' },
        data: artifact(),
      })
      .mockRejectedValueOnce(
        Object.assign(new Error('not found'), {
          status: 404,
          response: { status: 404, headers: {} },
        }),
      )
      .mockResolvedValueOnce({
        status: 200,
        headers: { etag: '"artifact-v2"' },
        data: artifact(),
      });
    const client = createArtifactActionsRestClient(
      octokitWithArtifactMethods({ getArtifact }),
      trustedProofBudget({ total: 4, primary: 4 }),
    );

    await client.getArtifact(artifactInput(), signal());
    await expect(client.getArtifact(artifactInput(), signal())).rejects.toThrow('not found');
    await client.getArtifact(artifactInput(), signal());

    expect(getArtifact.mock.calls[1]?.[0]).toMatchObject({
      headers: { 'if-none-match': '"artifact-v1"' },
      request: { signal: expect.anything() },
    });
    expect(getArtifact.mock.calls[2]?.[0]).toMatchObject({
      request: { signal: expect.anything() },
    });
    expect(getArtifact.mock.calls[2]?.[0].headers).toBeUndefined();
  });

  it('invalidates only the mutated artifact name and id while preserving unrelated 304 validators', async () => {
    const listArtifactsForRepo = vi
      .fn()
      .mockResolvedValueOnce({
        status: 200,
        headers: { etag: '"target-page-1"' },
        data: { total_count: 1, artifacts: [artifact()] },
      })
      .mockResolvedValueOnce({
        status: 200,
        headers: { etag: '"target-page-2"' },
        data: { total_count: 1, artifacts: [] },
      })
      .mockResolvedValueOnce({
        status: 200,
        headers: { etag: '"other-page"' },
        data: { total_count: 1, artifacts: [artifact()] },
      })
      .mockResolvedValueOnce({
        status: 200,
        headers: { etag: '"target-page-1-fresh"' },
        data: { total_count: 1, artifacts: [artifact()] },
      })
      .mockResolvedValueOnce({
        status: 200,
        headers: { etag: '"target-page-2-fresh"' },
        data: { total_count: 1, artifacts: [] },
      })
      .mockResolvedValueOnce({ status: 304, data: { total_count: 1, artifacts: [artifact()] } });
    const getArtifact = vi
      .fn()
      .mockResolvedValueOnce({ status: 200, headers: { etag: '"target"' }, data: artifact() })
      .mockResolvedValueOnce({
        status: 200,
        headers: { etag: '"non-target"' },
        data: artifact(),
      })
      .mockResolvedValueOnce({
        status: 200,
        headers: { etag: '"target-fresh"' },
        data: artifact(),
      })
      .mockResolvedValueOnce({ status: 304, data: artifact() });
    const getWorkflowRunAttempt = vi
      .fn()
      .mockResolvedValueOnce({
        status: 200,
        headers: { etag: '"attempt"' },
        data: { id: 9, run_attempt: 1 },
      })
      .mockResolvedValueOnce({ status: 304, data: { id: 9, run_attempt: 1 } });
    const client = createArtifactActionsRestClient(
      octokitWithArtifactMethods({ listArtifactsForRepo, getArtifact, getWorkflowRunAttempt }),
      trustedProofBudget({ total: 16, primary: 16 }),
    );
    const targetPageOne = listInput();
    const targetPageTwo = { ...targetPageOne, page: 2 };
    const otherPage = { ...targetPageOne, name: 'other' };
    const target = artifactInput();
    const nonTarget = { ...target, artifact_id: 2 };
    const attempt = { owner: 'owner', repo: 'repo', run_id: 9, attempt_number: 1 };

    await client.listArtifactsForRepo(targetPageOne, signal());
    await client.listArtifactsForRepo(targetPageTwo, signal());
    await client.getArtifact(target, signal());
    await client.getArtifact(nonTarget, signal());
    await client.getWorkflowRunAttempt(attempt, signal());
    await client.listArtifactsForRepo(otherPage, signal());

    client.invalidateArtifactMutation?.({
      owner: 'owner',
      repo: 'repo',
      name: 'artifact',
      artifact_id: 1,
    });

    await client.listArtifactsForRepo(targetPageOne, signal());
    await client.listArtifactsForRepo(targetPageTwo, signal());
    await client.getArtifact(target, signal());
    await client.getArtifact(nonTarget, signal());
    await client.getWorkflowRunAttempt(attempt, signal());
    await client.listArtifactsForRepo(otherPage, signal());

    expect(listArtifactsForRepo.mock.calls[3]?.[0].headers).toBeUndefined();
    expect(listArtifactsForRepo.mock.calls[4]?.[0].headers).toBeUndefined();
    expect(getArtifact.mock.calls[2]?.[0].headers).toBeUndefined();
    expect(getArtifact.mock.calls[3]?.[0]).toMatchObject({
      headers: { 'if-none-match': '"non-target"' },
    });
    expect(getWorkflowRunAttempt.mock.calls[1]?.[0]).toMatchObject({
      headers: { 'if-none-match': '"attempt"' },
    });
    expect(listArtifactsForRepo.mock.calls[5]?.[0]).toMatchObject({
      headers: { 'if-none-match': '"other-page"' },
    });
  });

  it('evicts each semantically rejected representation by its exact cache identity', async () => {
    const listArtifactsForRepo = vi.fn(async (_request: { headers?: Record<string, string> }) => ({
      status: 200,
      headers: { etag: '"list"' },
      data: { total_count: 0, artifacts: [] },
    }));
    const getArtifact = vi.fn(async (_request: { headers?: Record<string, string> }) => ({
      status: 200,
      headers: { etag: '"artifact"' },
      data: artifact(),
    }));
    const getWorkflowRunAttempt = vi.fn(async (_request: { headers?: Record<string, string> }) => ({
      status: 200,
      headers: { etag: '"attempt"' },
      data: { id: 9, run_attempt: 1 },
    }));
    const client = createArtifactActionsRestClient(
      octokitWithArtifactMethods({ listArtifactsForRepo, getArtifact, getWorkflowRunAttempt }),
      nonProofBudget(),
    );
    const list = listInput();
    const descriptor = artifactInput();
    const attempt = { owner: 'owner', repo: 'repo', run_id: 9, attempt_number: 1 };

    await client.listArtifactsForRepo(list, signal());
    await client.getArtifact(descriptor, signal());
    await client.getWorkflowRunAttempt(attempt, signal());
    client.invalidateArtifactListRepresentation?.(list);
    client.invalidateArtifactRepresentation?.(descriptor);
    client.invalidateWorkflowRunAttemptRepresentation?.(attempt);
    await client.listArtifactsForRepo(list, signal());
    await client.getArtifact(descriptor, signal());
    await client.getWorkflowRunAttempt(attempt, signal());

    expect(listArtifactsForRepo.mock.calls[1]?.[0].headers).toBeUndefined();
    expect(getArtifact.mock.calls[1]?.[0].headers).toBeUndefined();
    expect(getWorkflowRunAttempt.mock.calls[1]?.[0].headers).toBeUndefined();
  });

  it('does not make deleteArtifact itself decide which named list pages are stale', async () => {
    const getArtifact = vi
      .fn()
      .mockResolvedValueOnce({
        status: 200,
        headers: { etag: '"artifact-v1"' },
        data: artifact(),
      })
      .mockResolvedValueOnce({
        status: 200,
        headers: { etag: '"artifact-v2"' },
        data: artifact(),
      });
    const deleteArtifact = vi.fn(async () => ({ status: 204, data: undefined }));
    const client = createArtifactActionsRestClient(
      octokitWithArtifactMethods({ getArtifact, deleteArtifact }),
      trustedProofBudget({ total: 4, primary: 4 }),
    );

    await client.getArtifact(artifactInput(), signal());
    await client.deleteArtifact(artifactInput(), signal());
    await client.getArtifact(artifactInput(), signal());

    expect(deleteArtifact).toHaveBeenCalledOnce();
    expect(getArtifact.mock.calls[1]?.[0]).toMatchObject({
      headers: { 'if-none-match': '"artifact-v1"' },
    });
  });

  it('marks deletion at the immediate pre-wire boundary', async () => {
    const events: string[] = [];
    const deleteArtifact = vi.fn(async () => {
      events.push('octokit');
      return { status: 204, data: undefined };
    });
    const client = createArtifactActionsRestClient(
      octokitWithArtifactMethods({ deleteArtifact }),
      trustedProofBudget({ total: 4, primary: 4 }),
    );

    await client.deleteArtifact(artifactInput(), signal(), undefined, () => {
      events.push('marker');
    });

    expect(events).toEqual(['marker', 'octokit']);
    expect(deleteArtifact).toHaveBeenCalledOnce();
  });

  it('does not mark or call deletion when FIFO pacing misses its command deadline', async () => {
    let now = 0;
    const deleteArtifact = vi.fn(async () => ({ status: 204, data: undefined }));
    const budget = trustedProofBudget(
      { total: 16, primary: 16 },
      {
        now: () => now,
        sleep: async (milliseconds) => {
          now += milliseconds;
        },
        maximumPointsPerRollingMinute: 5,
      },
    );
    const client = createArtifactActionsRestClient(
      octokitWithArtifactMethods({ deleteArtifact }),
      budget,
    );
    let markers = 0;

    await runGet(budget);
    await expect(
      client.deleteArtifact(artifactInput(), signal(), now + 100, () => {
        markers += 1;
      }),
    ).rejects.toThrow('artifact_rest_attempt_deadline_exceeded');

    expect(markers).toBe(0);
    expect(deleteArtifact).not.toHaveBeenCalled();
  });

  it('does not clone or retain an over-bound conditional representation', async () => {
    const clone = vi.fn((value) => value);
    vi.stubGlobal('structuredClone', clone);
    const oversized = { payload: 'x'.repeat(256 * 1024) };
    const getArtifact = vi.fn(
      async (_input: {
        readonly headers?: unknown;
        readonly request?: { readonly headers?: unknown };
      }) => ({
        status: 200,
        headers: { etag: '"oversized"' },
        data: oversized,
      }),
    );
    const client = createArtifactActionsRestClient(
      octokitWithArtifactMethods({ getArtifact }),
      nonProofBudget(),
    );

    await client.getArtifact(artifactInput(), signal());
    await client.getArtifact(artifactInput(), signal());

    expect(getArtifact).toHaveBeenCalledTimes(2);
    expect(getArtifact.mock.calls[1]?.[0]?.headers).toBeUndefined();
    expect(clone).not.toHaveBeenCalled();
  });

  it('evicts conditional representations to its aggregate byte bound', async () => {
    const getArtifact = vi.fn(
      async (input: {
        readonly artifact_id: number;
        readonly headers?: unknown;
        readonly request?: { readonly headers?: unknown };
      }) => ({
        status: 200,
        headers: { etag: '"artifact-' + input.artifact_id + '"' },
        data: { id: input.artifact_id, value: 'x'.repeat(24) },
      }),
    );
    const client = createArtifactActionsRestClient(
      octokitWithArtifactMethods({ getArtifact }),
      nonProofBudget(),
      { maximumEntries: 4, maximumBytes: 48 },
    );

    await client.getArtifact({ owner: 'owner', repo: 'repo', artifact_id: 1 }, signal());
    await client.getArtifact({ owner: 'owner', repo: 'repo', artifact_id: 2 }, signal());
    await client.getArtifact({ owner: 'owner', repo: 'repo', artifact_id: 1 }, signal());

    expect(getArtifact).toHaveBeenCalledTimes(3);
    expect(getArtifact.mock.calls[2]?.[0]?.headers).toBeUndefined();
  });

  it('makes a returned secondary rate limit sticky before later authenticated dispatches', async () => {
    const getArtifact = vi.fn(async () => ({
      status: 403,
      headers: { 'retry-after': '60', 'x-ratelimit-remaining': '999' },
      data: artifact(),
    }));
    const budget = trustedProofBudget();
    const client = createArtifactActionsRestClient(
      octokitWithArtifactMethods({ getArtifact }),
      budget,
    );

    await client.getArtifact(artifactInput(), signal());
    await expect(client.getArtifact(artifactInput(), signal())).rejects.toThrow(
      'trusted_proof_artifact_rest_budget_rate_limited',
    );

    expect(getArtifact).toHaveBeenCalledOnce();
    expect(budget.receipt()).toMatchObject({
      total_authenticated_api_requests: 1,
      primary_rate_limit_requests: 1,
      disposition: 'rate_limited',
    });
  });

  it('accounts an ordinary authorization 403 without poisoning the protected route', async () => {
    const getArtifact = vi
      .fn()
      .mockResolvedValueOnce({ status: 403, headers: {}, data: artifact() })
      .mockResolvedValueOnce({ status: 200, headers: { etag: '"ok"' }, data: artifact() });
    const budget = trustedProofBudget();
    const client = createArtifactActionsRestClient(
      octokitWithArtifactMethods({ getArtifact }),
      budget,
    );

    await client.getArtifact(artifactInput(), signal());
    await client.getArtifact(artifactInput(), signal());

    expect(getArtifact).toHaveBeenCalledTimes(2);
    expect(budget.receipt()).toMatchObject({
      total_authenticated_api_requests: 2,
      primary_rate_limit_requests: 2,
      permission_denied: 1,
      disposition: 'active',
    });
  });

  it.each([
    [
      '403 primary exhaustion',
      403,
      { 'x-ratelimit-remaining': '0', 'x-ratelimit-reset': '1893456000' },
      artifact(),
      'primary_exhausted',
    ],
    [
      '429 primary exhaustion',
      429,
      { 'x-ratelimit-remaining': '0', 'x-ratelimit-reset': '1893456000' },
      artifact(),
      'primary_exhausted',
    ],
    ['403 secondary Retry-After', 403, { 'retry-after': '60' }, artifact(), 'rate_limited'],
    [
      '429 secondary message',
      429,
      {},
      { message: 'You have exceeded a secondary rate limit. Please wait.' },
      'rate_limited',
    ],
    [
      '403 combined primary and secondary exhaustion',
      403,
      {
        'x-ratelimit-remaining': '0',
        'x-ratelimit-reset': '1893456000',
        'retry-after': '60',
      },
      artifact(),
      'primary_and_secondary_rate_limited',
    ],
    [
      '429 combined primary and secondary exhaustion',
      429,
      {
        'x-ratelimit-remaining': '0',
        'x-ratelimit-reset': '1893456000',
        'retry-after': '60',
      },
      { message: 'secondary rate limit' },
      'primary_and_secondary_rate_limited',
    ],
  ] as const)(
    '%s yields a sticky truthful taxonomy',
    async (_label, status, headers, data, disposition) => {
      const getArtifact = vi.fn(async () => ({ status, headers, data }));
      const budget = trustedProofBudget();
      const client = createArtifactActionsRestClient(
        octokitWithArtifactMethods({ getArtifact }),
        budget,
      );

      await client.getArtifact(artifactInput(), signal());
      await expect(client.getArtifact(artifactInput(), signal())).rejects.toThrow(
        `trusted_proof_artifact_rest_budget_${disposition}`,
      );

      expect(getArtifact).toHaveBeenCalledOnce();
      expect(budget.receipt()).toMatchObject({
        total_authenticated_api_requests: 1,
        primary_rate_limit_requests: 1,
        disposition,
      });
    },
  );

  it('rejects a compound mutation reservation one raw unit before any dispatch', () => {
    const budget = trustedProofBudget({ total: 2, primary: 2 });

    expect(() =>
      budget.reserveMutation({
        authenticatedRequests: 3,
        primaryRequests: 3,
        secondaryPoints: 7,
      }),
    ).toThrow('trusted_proof_artifact_rest_budget_total_exhausted');
    expect(budget.receipt()).toMatchObject({
      total_authenticated_api_requests: 0,
      primary_rate_limit_requests: 0,
      secondary_limit_points: 0,
      disposition: 'total_exhausted',
    });
  });

  it('fails closed for a 429 that supplies neither primary nor secondary evidence', async () => {
    const getArtifact = vi.fn(async () => {
      throw Object.assign(new Error('rate limited'), {
        status: 429,
        response: { status: 429, headers: {} },
      });
    });
    const budget = trustedProofBudget();
    const client = createArtifactActionsRestClient(
      octokitWithArtifactMethods({ getArtifact }),
      budget,
    );

    await expect(client.getArtifact(artifactInput(), signal())).rejects.toThrow(
      'artifact_rest_rate_limit_headers_invalid',
    );
    await expect(client.getArtifact(artifactInput(), signal())).rejects.toThrow(
      'artifact_rest_rate_limit_headers_invalid',
    );

    expect(getArtifact).toHaveBeenCalledOnce();
    expect(budget.receipt()).toMatchObject({
      total_authenticated_api_requests: 1,
      primary_rate_limit_requests: 1,
      disposition: 'invalid_rate_limit_headers',
    });
  });

  it('makes a thrown secondary rate-limit response sticky with primary capacity remaining', async () => {
    const getArtifact = vi.fn(async () => {
      throw Object.assign(new Error('secondary rate limited'), {
        status: 403,
        response: {
          status: 403,
          headers: { 'retry-after': '60', 'x-ratelimit-remaining': '999' },
        },
      });
    });
    const budget = trustedProofBudget();
    const client = createArtifactActionsRestClient(
      octokitWithArtifactMethods({ getArtifact }),
      budget,
    );

    await expect(client.getArtifact(artifactInput(), signal())).rejects.toThrow(
      'secondary rate limited',
    );
    await expect(client.getArtifact(artifactInput(), signal())).rejects.toThrow(
      'trusted_proof_artifact_rest_budget_rate_limited',
    );

    expect(getArtifact).toHaveBeenCalledOnce();
    expect(budget.receipt()).toMatchObject({
      total_authenticated_api_requests: 1,
      primary_rate_limit_requests: 1,
      disposition: 'rate_limited',
    });
  });

  it('accounts a plain 403 when only an Octokit error message mentions a secondary limit', async () => {
    const getArtifact = vi.fn(async () => {
      throw Object.assign(new Error('You have exceeded a secondary rate limit.'), {
        status: 403,
        response: { status: 403, headers: {} },
      });
    });
    const budget = trustedProofBudget();
    const client = createArtifactActionsRestClient(
      octokitWithArtifactMethods({ getArtifact }),
      budget,
    );

    await expect(client.getArtifact(artifactInput(), signal())).rejects.toThrow(
      'You have exceeded a secondary rate limit.',
    );

    expect(budget.receipt()).toMatchObject({
      disposition: 'active',
      total_authenticated_api_requests: 1,
      primary_rate_limit_requests: 1,
      permission_denied: 1,
    });
  });

  it('makes an ordinary successful zero remaining response immediately sticky', async () => {
    const getArtifact = vi.fn(async () => ({
      status: 200,
      headers: {
        etag: '"artifact"',
        'x-ratelimit-limit': '5000',
        'x-ratelimit-remaining': '0',
        'x-ratelimit-reset': '1893456000',
      },
      data: artifact(),
    }));
    const budget = trustedProofBudget();
    const client = createArtifactActionsRestClient(
      octokitWithArtifactMethods({ getArtifact }),
      budget,
    );

    await client.getArtifact(artifactInput(), signal());
    expect(budget.receipt().disposition).toBe('primary_exhausted');
    await expect(client.getArtifact(artifactInput(), signal())).rejects.toThrow(
      'trusted_proof_artifact_rest_budget_primary_exhausted',
    );

    expect(getArtifact).toHaveBeenCalledOnce();
    expect(budget.receipt()).toMatchObject({
      total_authenticated_api_requests: 1,
      primary_rate_limit_requests: 1,
      disposition: 'primary_exhausted',
    });
  });

  it('keeps normal 200 and 304 primary headers non-sticky', async () => {
    const getArtifact = vi
      .fn()
      .mockResolvedValueOnce({
        status: 200,
        headers: {
          etag: '"artifact"',
          'x-ratelimit-limit': '5000',
          'x-ratelimit-remaining': '4999',
          'x-ratelimit-reset': '1893456000',
        },
        data: artifact(),
      })
      .mockRejectedValueOnce(
        Object.assign(new Error('not modified'), {
          status: 304,
          response: {
            status: 304,
            headers: {
              'x-ratelimit-limit': '5000',
              'x-ratelimit-remaining': '4999',
              'x-ratelimit-reset': '1893456000',
            },
          },
        }),
      );
    const budget = trustedProofBudget({ total: 3, primary: 3 });
    const client = createArtifactActionsRestClient(
      octokitWithArtifactMethods({ getArtifact }),
      budget,
    );

    await client.getArtifact(artifactInput(), signal());
    await client.getArtifact(artifactInput(), signal());

    expect(budget.receipt()).toMatchObject({
      total_authenticated_api_requests: 2,
      primary_rate_limit_requests: 1,
      conditional_not_modified_requests: 1,
      disposition: 'active',
    });
  });

  it('never derives a secondary signal from a 200 or 304 JSON body', async () => {
    const budget = trustedProofBudget();
    for (const status of [200, 304]) {
      await budget.runAuthenticatedApiCall(
        { signal: signal(), secondaryLimitPoints: 1, mutative: false },
        async () => ({
          status,
          headers: {},
          data: { message: 'You have exceeded a secondary rate limit.' },
        }),
      );
    }

    expect(budget.receipt()).toMatchObject({
      disposition: 'active',
      total_authenticated_api_requests: 2,
      primary_rate_limit_requests: 1,
      conditional_not_modified_requests: 1,
    });
  });

  it.each([
    ['', 'invalid'],
    ['secondary rate\u0000limit', 'invalid'],
    ['secondary rate limit', 'secondary'],
    ['secondary rate limited', 'secondary'],
    ['secondary rate limits', 'secondary'],
    [`secondary rate limit ${'x'.repeat(491)}`, 'secondary'],
    [`secondary rate limit ${'x'.repeat(492)}`, 'invalid'],
    ['ésecondary rate limit', 'secondary'],
    ['secondary rate limité', 'secondary'],
    ['xsecondary rate limit', 'permission'],
    ['secondary rate limitx', 'permission'],
  ] as const)(
    'uses the exact bounded secondary-message predicate for %j',
    async (message, outcome) => {
      const budget = trustedProofBudget();
      const observe = () =>
        budget.runAuthenticatedApiCall(
          { signal: signal(), secondaryLimitPoints: 1, mutative: false },
          async () => ({ status: 403, headers: {}, data: { message } }),
        );

      if (outcome === 'invalid') {
        await expect(observe()).rejects.toThrow('artifact_rest_rate_limit_headers_invalid');
        return;
      }
      await observe();
      expect(budget.receipt().disposition).toBe(
        outcome === 'secondary' ? 'rate_limited' : 'active',
      );
      expect(budget.receipt().permission_denied).toBe(outcome === 'permission' ? 1 : 0);
    },
  );

  it.each(['not-json', null, ['secondary rate limit'], { message: { nested: true } }])(
    'fails closed for a non-object or malformed GitHub error payload',
    async (data) => {
      const budget = trustedProofBudget();

      await expect(
        budget.runAuthenticatedApiCall(
          { signal: signal(), secondaryLimitPoints: 1, mutative: false },
          async () => ({ status: 403, headers: {}, data }),
        ),
      ).rejects.toThrow('artifact_rest_rate_limit_headers_invalid');
    },
  );

  it.each([
    [-1, 'invalid'],
    [0, 'invalid'],
    [1, 'primary_exhausted'],
  ] as const)(
    'validates reset epochs against its injected clock at now%+d',
    async (delta, outcome) => {
      const now = 1_900_000_000;
      const budget = trustedProofBudget(undefined, { epochSeconds: () => now });
      const observe = () =>
        budget.runAuthenticatedApiCall(
          { signal: signal(), secondaryLimitPoints: 1, mutative: false },
          async () => ({
            status: 403,
            headers: {
              'x-ratelimit-remaining': '0',
              'x-ratelimit-reset': String(now + delta),
            },
            data: artifact(),
          }),
        );

      if (outcome === 'invalid') {
        await expect(observe()).rejects.toThrow('artifact_rest_rate_limit_headers_invalid');
        return;
      }
      await observe();
      expect(budget.receipt().disposition).toBe(outcome);
    },
  );

  it.each([
    { headers: { 'x-ratelimit-remaining': 'not-a-number' } },
    { headers: { 'x-ratelimit-remaining': '3', 'x-ratelimit-limit': '2' } },
    { headers: { 'x-ratelimit-reset': 'not-an-epoch' } },
    { headers: { 'x-ratelimit-reset': '1', 'x-ratelimit-remaining': '1' } },
    { headers: { 'x-ratelimit-reset': '1893456000' } },
    { headers: { 'x-ratelimit-reset': '4102444801' } },
    { headers: { 'x-ratelimit-reset': ['1893456000', '1893456001'] } },
    {
      headers: {
        'x-ratelimit-remaining': '1',
        'X-RateLimit-Remaining': '1',
      },
    },
    { headers: { 'retry-after': 'later' } },
    { headers: { 'retry-after': '1' } },
    { status: 403, headers: { 'retry-after': '1', 'x-ratelimit-remaining': '0' } },
    { status: 429, headers: {} },
    { status: 429, headers: {}, data: { message: 'ordinary failure' } },
    { status: 429, headers: {}, data: { message: 'x'.repeat(513) } },
  ])(
    'rejects malformed or contradictory rate-limit headers and poisons later dispatches',
    async ({ headers, status = 200, data = artifact() }) => {
      const getArtifact = vi
        .fn()
        .mockResolvedValueOnce({ status, headers, data })
        .mockResolvedValueOnce({ status: 200, headers: {}, data: artifact() });
      const budget = trustedProofBudget();
      const client = createArtifactActionsRestClient(
        octokitWithArtifactMethods({ getArtifact }),
        budget,
      );

      await expect(client.getArtifact(artifactInput(), signal())).rejects.toThrow(
        'artifact_rest_rate_limit_headers_invalid',
      );
      await expect(client.getArtifact(artifactInput(), signal())).rejects.toThrow(
        'artifact_rest_rate_limit_headers_invalid',
      );

      expect(getArtifact).toHaveBeenCalledOnce();
      expect(budget.receipt()).toMatchObject({
        total_authenticated_api_requests: 1,
        primary_rate_limit_requests: 1,
        disposition: 'invalid_rate_limit_headers',
      });
    },
  );

  it('rejects a compound mutation reservation against a verified zero remaining allocation', async () => {
    const getArtifact = vi.fn(async () => ({
      status: 200,
      headers: {
        'x-ratelimit-remaining': '0',
        'x-ratelimit-reset': '1893456000',
      },
      data: artifact(),
    }));
    const budget = trustedProofBudget();
    const client = createArtifactActionsRestClient(
      octokitWithArtifactMethods({ getArtifact }),
      budget,
    );

    await client.getArtifact(artifactInput(), signal());
    expect(() =>
      budget.reserveMutation({ authenticatedRequests: 3, primaryRequests: 3, secondaryPoints: 8 }),
    ).toThrow('trusted_proof_artifact_rest_budget_primary_exhausted');
    expect(getArtifact).toHaveBeenCalledOnce();
  });

  it('reserves the compound primary tail plus the final-profile reserve before mutation wire work', async () => {
    const getArtifact = vi.fn(async () => ({
      status: 200,
      headers: { 'x-ratelimit-remaining': '3' },
      data: artifact(),
    }));
    const budget = trustedProofBudget();
    const client = createArtifactActionsRestClient(
      octokitWithArtifactMethods({ getArtifact }),
      budget,
    );
    let mutationWireDispatches = 0;

    await client.getArtifact(artifactInput(), signal());
    expect(() => {
      budget.reserveMutation({ authenticatedRequests: 3, primaryRequests: 3, secondaryPoints: 8 });
      mutationWireDispatches += 1;
    }).toThrow('trusted_proof_artifact_rest_budget_primary_exhausted');

    expect(mutationWireDispatches).toBe(0);
    expect(getArtifact).toHaveBeenCalledOnce();
  });

  it('restores a provisional known allocation after a headerless 304', async () => {
    const getArtifact = vi
      .fn()
      .mockResolvedValueOnce({
        status: 200,
        headers: { etag: '"artifact-v1"', 'x-ratelimit-remaining': '3' },
        data: artifact(),
      })
      .mockRejectedValueOnce(
        Object.assign(new Error('not modified'), {
          status: 304,
          response: { status: 304, headers: {} },
        }),
      )
      .mockResolvedValueOnce({ status: 200, headers: { etag: '"artifact-v2"' }, data: artifact() });
    const budget = trustedProofBudget();
    const client = createArtifactActionsRestClient(
      octokitWithArtifactMethods({ getArtifact }),
      budget,
    );

    await client.getArtifact(artifactInput(), signal());
    await client.getArtifact(artifactInput(), signal());
    await client.getArtifact(artifactInput(), signal());

    expect(getArtifact).toHaveBeenCalledTimes(3);
    expect(budget.receipt()).toMatchObject({
      primary_rate_limit_requests: 2,
      conditional_not_modified_requests: 1,
      disposition: 'active',
    });
  });

  it('requires the known remaining allocation to cover a command tail as one unit', async () => {
    const getArtifact = vi.fn(async () => ({
      status: 200,
      headers: { 'x-ratelimit-remaining': '2' },
      data: artifact(),
    }));
    const budget = trustedProofBudget();
    const client = createArtifactActionsRestClient(
      octokitWithArtifactMethods({ getArtifact }),
      budget,
    );

    await client.getArtifact(artifactInput(), signal());
    expect(() => budget.requireObservedPrimaryAllocation(3)).toThrow(
      'trusted_proof_artifact_rest_budget_primary_exhausted',
    );
    expect(getArtifact).toHaveBeenCalledOnce();
  });

  it('leaves an authenticated non-proof route unaffected', async () => {
    const listArtifactsForRepo = vi.fn(async () => ({
      status: 200,
      data: { artifacts: [] },
    }));
    const budget = nonProofBudget({ total: 2, primary: 2 });
    const client = createArtifactActionsRestClient(
      octokitWithArtifactMethods({ listArtifactsForRepo }),
      budget,
    );

    for (let index = 0; index <= 2; index += 1) {
      await client.listArtifactsForRepo(listInput(), signal());
    }

    expect(listArtifactsForRepo).toHaveBeenCalledTimes(3);
    expect(budget.receipt()).toEqual({
      kind: 'apr-r4-trusted-proof-artifact-rest-budget-v2',
      protected_route: false,
      maximum_total_authenticated_api_requests: null,
      total_authenticated_api_requests: 0,
      maximum_primary_rate_limit_requests: null,
      primary_rate_limit_requests: 0,
      conditional_not_modified_requests: 0,
      secondary_limit_points: 0,
      permission_denied: 0,
      remaining_total_authenticated_api_requests: null,
      remaining_primary_rate_limit_requests: null,
      disposition: 'active',
      repository: null,
      repository_id: null,
      workflow_sha: null,
      action_source_sha: null,
      payload_sha256: null,
      build_discriminator: null,
      run_id: null,
      run_attempt: null,
      cap_profile: null,
      measurement_only: null,
      remaining_tail_required: null,
      remaining_tail_reserve: null,
    });
  });
});

function octokitWithRedirect() {
  return {
    rest: {
      actions: {
        downloadArtifact: vi.fn(async () => ({
          status: 302,
          headers: { location: 'https://blob.invalid/archive' },
        })),
      },
    },
  } as never;
}

function octokitWithArtifactMethods(methods: Record<string, unknown>) {
  return {
    rest: {
      actions: {
        listArtifactsForRepo: vi.fn(async () => ({
          status: 200,
          data: { artifacts: [] },
        })),
        getArtifact: vi.fn(async () => ({ status: 200, data: artifact() })),
        downloadArtifact: vi.fn(async () => ({ status: 302, headers: {} })),
        getWorkflowRunAttempt: vi.fn(async () => ({ status: 200, data: {} })),
        deleteArtifact: vi.fn(async () => ({ status: 204, data: undefined })),
        ...methods,
      },
    },
  } as never;
}

function trustedProofBudget(
  limits = {
    total:
      TRUSTED_PROOF_MEASUREMENT_ARTIFACT_REST_REQUEST_BUDGET_PROFILE.limits
        .maximumTotalAuthenticatedApiRequests,
    primary:
      TRUSTED_PROOF_MEASUREMENT_ARTIFACT_REST_REQUEST_BUDGET_PROFILE.limits
        .maximumPrimaryRateLimitRequests,
  },
  secondaryRateLimit?: ArtifactRestSecondaryRateLimitOptions,
): ArtifactRestRequestBudget {
  return ArtifactRestRequestBudget.forVerifiedPreparedPayload({
    buildDiscriminator: 'r4-w2',
    secondaryRateLimit,
    identity: trustedProofIdentity(),
    profile: trustedProofProfile(limits),
  });
}

function trustedProofProfile(limits: {
  readonly total: number;
  readonly primary: number;
}): ArtifactRestRequestBudgetProfile {
  return {
    ...TRUSTED_PROOF_MEASUREMENT_ARTIFACT_REST_REQUEST_BUDGET_PROFILE,
    limits: {
      maximumTotalAuthenticatedApiRequests: limits.total,
      maximumPrimaryRateLimitRequests: limits.primary,
    },
  };
}

function nonProofBudget(
  limits = {
    total: TRUSTED_PROOF_ARTIFACT_REST_REQUEST_LIMITS.maximumTotalAuthenticatedApiRequests,
    primary: TRUSTED_PROOF_ARTIFACT_REST_REQUEST_LIMITS.maximumPrimaryRateLimitRequests,
  },
): ArtifactRestRequestBudget {
  return ArtifactRestRequestBudget.forVerifiedPreparedPayload({
    buildDiscriminator: 'r4-h1',
    limits: {
      maximumTotalAuthenticatedApiRequests: limits.total,
      maximumPrimaryRateLimitRequests: limits.primary,
    },
  });
}

function listInput() {
  return { owner: 'owner', repo: 'repo', name: 'artifact', per_page: 100, page: 1 };
}

function artifactInput() {
  return { owner: 'owner', repo: 'repo', artifact_id: 1 };
}

function artifact() {
  return { id: 1, name: 'artifact', expired: false };
}

function signal(): AbortSignal {
  return new AbortController().signal;
}

function trustedProofIdentity() {
  return {
    repository: 'owner/repo',
    repositoryId: '1',
    workflowSha: 'a'.repeat(40),
    actionSourceSha: 'b'.repeat(40),
    payloadSha256: 'c'.repeat(64),
    buildDiscriminator: 'r4-w2',
    runId: '2',
    runAttempt: '1',
  };
}

async function runGet(budget: ArtifactRestRequestBudget) {
  return await budget.runAuthenticatedApiCall(
    { signal: signal(), secondaryLimitPoints: 1, mutative: false },
    async () => ({ status: 200 }),
  );
}

async function runNotModified(budget: ArtifactRestRequestBudget) {
  return await budget.runAuthenticatedApiCall(
    { signal: signal(), secondaryLimitPoints: 1, mutative: false },
    async () => ({ status: 304 }),
  );
}
