const TRUSTED_PROOF_PREPARED_PAYLOAD_BUILD_DISCRIMINATOR = 'r4-w2';

/**
 * The r4-w2 bridge has a fixed per-route authenticated REST budget.  The
 * receipt remains measurement-only until the complete cross-role evidence is
 * accepted. The measurement cap is intentionally explicit and is selected
 * only through the verified r4-w2 profile.
 */
export interface ArtifactRestRequestBudgetLimits {
  readonly maximumTotalAuthenticatedApiRequests: number;
  readonly maximumPrimaryRateLimitRequests: number;
}

export const TRUSTED_PROOF_ARTIFACT_REST_REQUEST_LIMITS: ArtifactRestRequestBudgetLimits =
  Object.freeze({
    maximumTotalAuthenticatedApiRequests: 256,
    maximumPrimaryRateLimitRequests: 256,
  });

export interface ArtifactRestRequestBudgetProfile {
  readonly capProfile: string;
  readonly limits: ArtifactRestRequestBudgetLimits;
  readonly remainingTailRequired: number;
  readonly remainingTailReserve: number;
  readonly measurementOnly: boolean;
}

export interface ArtifactRestSecondaryRateLimitOptions {
  readonly now?: () => number;
  /** Wall-clock Unix seconds used only to validate absolute reset epochs. */
  readonly epochSeconds?: () => number;
  readonly sleep?: (milliseconds: number, signal: AbortSignal) => Promise<void>;
  readonly maximumPointsPerRollingMinute?: number;
  readonly minimumMutativeSpacingMs?: number;
}

export interface ArtifactRestRequestDispatch {
  readonly signal: AbortSignal;
  readonly secondaryLimitPoints: 1 | 5;
  readonly mutative: boolean;
  /** Monotonic latest instant at which a 30s HTTP attempt may start. */
  readonly latestAttemptStartAt?: number;
}

const DEFAULT_MAXIMUM_SECONDARY_POINTS_PER_ROLLING_MINUTE = 600;
const DEFAULT_MINIMUM_MUTATIVE_SPACING_MS = 1_000;
const ROLLING_MINUTE_MS = 60_000;

/**
 * A single stderr record, emitted only after the bridge has stopped accepting
 * work and the official-call tracker has observed quiescence.  The Framework
 * fixture deliberately captures this stable prefix rather than parsing other
 * wrapper diagnostics.
 */
export const TRUSTED_PROOF_ARTIFACT_REST_REQUEST_RECEIPT_PREFIX =
  'APR_R4_E2P_ARTIFACT_REST_BUDGET ';

type ArtifactRestRequestBudgetDisposition =
  | 'active'
  | 'total_exhausted'
  | 'primary_exhausted'
  | 'rate_limited'
  | 'primary_and_secondary_rate_limited'
  | 'invalid_rate_limit_headers';

export interface ArtifactRestRequestReceipt {
  readonly kind: 'apr-r4-trusted-proof-artifact-rest-budget-v2';
  readonly protected_route: boolean;
  readonly maximum_total_authenticated_api_requests: number | null;
  readonly total_authenticated_api_requests: number;
  readonly maximum_primary_rate_limit_requests: number | null;
  readonly primary_rate_limit_requests: number;
  readonly conditional_not_modified_requests: number;
  readonly secondary_limit_points: number;
  /** Plain 403 responses with no primary or secondary rate-limit signal. */
  readonly permission_denied: number;
  readonly remaining_total_authenticated_api_requests: number | null;
  readonly remaining_primary_rate_limit_requests: number | null;
  readonly disposition: ArtifactRestRequestBudgetDisposition;
  readonly repository: string | null;
  readonly repository_id: string | null;
  readonly workflow_sha: string | null;
  readonly action_source_sha: string | null;
  readonly payload_sha256: string | null;
  readonly build_discriminator: string | null;
  readonly run_id: string | null;
  readonly run_attempt: string | null;
  readonly cap_profile: string | null;
  readonly measurement_only: boolean | null;
  readonly remaining_tail_required: number | null;
  readonly remaining_tail_reserve: number | null;
}

export interface ArtifactRestReceiptIdentity {
  readonly repository: string;
  readonly repositoryId: string;
  readonly workflowSha: string;
  readonly actionSourceSha: string;
  readonly payloadSha256: string;
  readonly buildDiscriminator: string;
  readonly runId: string;
  readonly runAttempt: string;
}

const TRUSTED_PROOF_ARTIFACT_REST_CAP_PROFILE = 'apr-r4-artifact-rest-request-budget-v2';

export class ArtifactRestRequestBudgetError extends Error {
  constructor(
    readonly disposition: Exclude<
      ArtifactRestRequestBudgetDisposition,
      'active' | 'invalid_rate_limit_headers'
    >,
  ) {
    super(`trusted_proof_artifact_rest_budget_${disposition}`);
    this.name = 'ArtifactRestRequestBudgetError';
  }
}

export class ArtifactRestRateLimitHeadersError extends Error {
  constructor() {
    super('artifact_rest_rate_limit_headers_invalid');
    this.name = 'ArtifactRestRateLimitHeadersError';
  }
}

export interface ArtifactRestRequestReservation {
  readonly protectedRoute: boolean;
  readonly primaryReserved: boolean;
}

export interface ArtifactRestMutationReservation {
  readonly release: () => void;
}

/**
 * Process-wide authenticated api.github.com ledger for one bridge wrapper.
 *
 * Every request is first accounted as raw traffic. A conditional GET keeps a
 * one-unit primary-rate-limit reservation until its response establishes
 * whether GitHub returned 304. Therefore a concurrent caller cannot spend a
 * primary unit that has only been provisionally reserved by another caller,
 * while an actual 304 releases that reservation without consuming a unit.
 */
export class ArtifactRestRequestBudget {
  private totalRequests = 0;
  private primaryRateLimitRequests = 0;
  private primaryReservations = 0;
  private reservedMutationRequests = 0;
  private reservedMutationPrimaryRequests = 0;
  private reservedMutationSecondaryPoints = 0;
  private activeMutationReservation: ActiveMutationReservation | undefined;
  private conditionalNotModifiedRequests = 0;
  private secondaryLimitPoints = 0;
  private permissionDenied = 0;
  /**
   * The last verified GitHub primary allocation.  It is deliberately a
   * separate ceiling from our local measurement cap: a successful response
   * with zero remaining is not itself a rate-limit response, but it makes a
   * later charged request impossible and therefore must block before wire
   * dispatch.
   */
  private observedPrimaryRemaining: number | undefined;
  private disposition: ArtifactRestRequestBudgetDisposition = 'active';
  private sealedReceiptLine: string | undefined;
  private readonly secondaryRateLimiter: ArtifactRestSecondaryRateLimiter | undefined;
  private readonly epochSeconds: () => number;

  private constructor(
    readonly protectedRoute: boolean,
    private readonly limits: ArtifactRestRequestBudgetLimits,
    secondaryRateLimit: ArtifactRestSecondaryRateLimitOptions | undefined,
    private readonly identity: ArtifactRestReceiptIdentity | undefined,
    private readonly profile: ArtifactRestRequestBudgetProfile | undefined,
  ) {
    this.epochSeconds = secondaryRateLimit?.epochSeconds ?? (() => Math.floor(Date.now() / 1_000));
    if (protectedRoute) {
      this.secondaryRateLimiter = new ArtifactRestSecondaryRateLimiter(secondaryRateLimit);
    }
  }

  static forVerifiedPreparedPayload(input: {
    readonly buildDiscriminator: string;
    readonly limits?: ArtifactRestRequestBudgetLimits;
    readonly secondaryRateLimit?: ArtifactRestSecondaryRateLimitOptions;
    readonly identity?: ArtifactRestReceiptIdentity;
    readonly profile?: ArtifactRestRequestBudgetProfile;
  }): ArtifactRestRequestBudget {
    const protectedRoute =
      input.buildDiscriminator === TRUSTED_PROOF_PREPARED_PAYLOAD_BUILD_DISCRIMINATOR;
    if (protectedRoute && !validProfile(input.profile)) {
      throw new Error('artifact_rest_request_budget_profile_invalid');
    }
    if (!protectedRoute && input.profile !== undefined) {
      throw new Error('artifact_rest_request_budget_profile_invalid');
    }
    if (protectedRoute && input.limits !== undefined) {
      // A protected receipt must never report one profile while the ledger
      // enforces another. Test cases that need a narrower cap construct the
      // corresponding profile instead of overriding it here.
      throw new Error('artifact_rest_request_budget_profile_invalid');
    }
    const limits = protectedRoute
      ? input.profile!.limits
      : (input.limits ?? TRUSTED_PROOF_ARTIFACT_REST_REQUEST_LIMITS);
    if (
      !positiveInteger(limits.maximumTotalAuthenticatedApiRequests) ||
      !positiveInteger(limits.maximumPrimaryRateLimitRequests)
    ) {
      throw new Error('artifact_rest_request_budget_limits_invalid');
    }
    if (protectedRoute && !validIdentity(input.identity)) {
      throw new Error('artifact_rest_request_budget_identity_invalid');
    }
    return new ArtifactRestRequestBudget(
      protectedRoute,
      limits,
      input.secondaryRateLimit,
      input.identity,
      input.profile,
    );
  }

  async runAuthenticatedApiCall<T extends ResponseLike>(
    dispatch: ArtifactRestRequestDispatch,
    call: () => Promise<T>,
  ): Promise<T> {
    if (!this.protectedRoute) return await call();
    return await this.secondaryRateLimiter!.run(dispatch, async (markDispatched) => {
      const reservation = this.authorizeDispatch(dispatch);
      markDispatched();
      let response: T;
      try {
        response = await call();
      } catch (error) {
        this.observeAuthenticatedApiFailure(reservation, error);
        throw error;
      }
      this.observeAuthenticatedApiResponse(reservation, response);
      return response;
    });
  }

  /**
   * Reserve every authenticated observation a mutation will require before
   * its irreversible dispatch.  Callers run in the lifecycle coordinator's
   * single lane, so nested REST calls consume this lease in order.  A one-unit
   * short ledger fails here, before upload/delete is dispatched.
   */
  reserveMutation(input: {
    readonly authenticatedRequests: number;
    readonly primaryRequests: number;
    readonly secondaryPoints: number;
  }): ArtifactRestMutationReservation {
    if (!this.protectedRoute) return { release: () => undefined };
    if (
      !positiveInteger(input.authenticatedRequests) ||
      !positiveInteger(input.primaryRequests) ||
      !positiveInteger(input.secondaryPoints) ||
      input.primaryRequests > input.authenticatedRequests ||
      this.activeMutationReservation !== undefined
    ) {
      throw new Error('artifact_rest_mutation_reservation_invalid');
    }
    if (this.disposition === 'invalid_rate_limit_headers') {
      throw new ArtifactRestRateLimitHeadersError();
    }
    if (this.disposition !== 'active') throw new ArtifactRestRequestBudgetError(this.disposition);
    if (
      this.totalRequests + this.reservedMutationRequests + input.authenticatedRequests >
      this.limits.maximumTotalAuthenticatedApiRequests
    ) {
      this.disposition = 'total_exhausted';
      throw new ArtifactRestRequestBudgetError(this.disposition);
    }
    if (
      this.primaryRateLimitRequests +
        this.primaryReservations +
        this.reservedMutationPrimaryRequests +
        input.primaryRequests >
      this.limits.maximumPrimaryRateLimitRequests
    ) {
      this.disposition = 'primary_exhausted';
      throw new ArtifactRestRequestBudgetError(this.disposition);
    }
    if (
      this.observedPrimaryRemaining !== undefined &&
      this.observedPrimaryRemaining <= input.primaryRequests + this.requiredTailAndReserve()
    ) {
      this.disposition = 'primary_exhausted';
      throw new ArtifactRestRequestBudgetError(this.disposition);
    }
    const reservation: ActiveMutationReservation = {
      remainingRequests: input.authenticatedRequests,
      remainingPrimaryRequests: input.primaryRequests,
      remainingSecondaryPoints: input.secondaryPoints,
      released: false,
    };
    this.reservedMutationRequests += input.authenticatedRequests;
    this.reservedMutationPrimaryRequests += input.primaryRequests;
    this.reservedMutationSecondaryPoints += input.secondaryPoints;
    this.activeMutationReservation = reservation;
    return {
      release: () => this.releaseMutationReservation(reservation),
    };
  }

  /**
   * A completed response may reveal a lower primary allocation than our local
   * measurement cap. Before a command starts, its known mandatory tail must
   * fit in that allocation as one unit; missing headers remain conservatively
   * charged by the local ledger instead of being treated as free capacity.
   */
  requireObservedPrimaryAllocation(required: number): void {
    if (!this.protectedRoute) return;
    if (!positiveInteger(required)) {
      throw new Error('artifact_rest_primary_allocation_invalid');
    }
    if (this.disposition === 'invalid_rate_limit_headers') {
      throw new ArtifactRestRateLimitHeadersError();
    }
    if (this.disposition !== 'active') throw new ArtifactRestRequestBudgetError(this.disposition);
    const requiredWithFinalProfileTail = required + this.requiredTailAndReserve();
    if (
      this.observedPrimaryRemaining !== undefined &&
      this.observedPrimaryRemaining <= requiredWithFinalProfileTail
    ) {
      this.disposition = 'primary_exhausted';
      throw new ArtifactRestRequestBudgetError(this.disposition);
    }
  }

  /**
   * The artifact data-plane upload is an irreversible mutative dispatch but
   * is not an Octokit REST call, so it consumes only the five secondary
   * points already reserved by the enclosing mutation.  The same FIFO
   * limiter still paces it and rejects it before the command's final HTTP
   * attempt-start boundary; no wire call occurs until `markDispatched` runs.
   */
  async runReservedMutationDataPlaneCall<T>(
    dispatch: ArtifactRestRequestDispatch,
    call: (markDispatched: () => void) => Promise<T>,
  ): Promise<T> {
    if (!this.protectedRoute) return await call(() => undefined);
    if (!dispatch.mutative || dispatch.secondaryLimitPoints !== 5) {
      throw new Error('artifact_rest_mutation_data_plane_dispatch_invalid');
    }
    return await this.secondaryRateLimiter!.run(dispatch, async (markDispatched) => {
      let marked = false;
      return await call(() => {
        if (marked) throw new Error('artifact_rest_mutation_data_plane_marked_twice');
        const mutation = this.activeMutationReservation;
        if (!mutation || mutation.remainingSecondaryPoints < dispatch.secondaryLimitPoints) {
          throw new Error('artifact_rest_mutation_reservation_overconsumed');
        }
        marked = true;
        mutation.remainingSecondaryPoints -= dispatch.secondaryLimitPoints;
        this.reservedMutationSecondaryPoints -= dispatch.secondaryLimitPoints;
        markDispatched();
      });
    });
  }

  observeAuthenticatedApiResponse(
    reservation: ArtifactRestRequestReservation,
    input: { readonly status: number; readonly headers?: HeadersLike; readonly data?: unknown },
  ): void {
    this.completeReservation(
      reservation,
      input.status,
      input.headers,
      messageFromPayload(input.data),
    );
  }

  observeAuthenticatedApiFailure(
    reservation: ArtifactRestRequestReservation,
    error: unknown,
  ): void {
    const response = asResponseLike(error);
    this.completeReservation(reservation, response?.status, response?.headers, response?.message);
  }

  receipt(): ArtifactRestRequestReceipt {
    const maximumTotal = this.protectedRoute
      ? this.limits.maximumTotalAuthenticatedApiRequests
      : null;
    const maximumPrimary = this.protectedRoute ? this.limits.maximumPrimaryRateLimitRequests : null;
    return {
      kind: 'apr-r4-trusted-proof-artifact-rest-budget-v2',
      protected_route: this.protectedRoute,
      maximum_total_authenticated_api_requests: maximumTotal,
      total_authenticated_api_requests: this.totalRequests,
      maximum_primary_rate_limit_requests: maximumPrimary,
      primary_rate_limit_requests: this.primaryRateLimitRequests,
      conditional_not_modified_requests: this.conditionalNotModifiedRequests,
      secondary_limit_points: this.secondaryLimitPoints,
      permission_denied: this.permissionDenied,
      remaining_total_authenticated_api_requests:
        maximumTotal === null ? null : maximumTotal - this.totalRequests,
      remaining_primary_rate_limit_requests:
        maximumPrimary === null ? null : maximumPrimary - this.primaryRateLimitRequests,
      disposition: this.disposition,
      repository: this.identity?.repository ?? null,
      repository_id: this.identity?.repositoryId ?? null,
      workflow_sha: this.identity?.workflowSha ?? null,
      action_source_sha: this.identity?.actionSourceSha ?? null,
      payload_sha256: this.identity?.payloadSha256 ?? null,
      build_discriminator: this.identity?.buildDiscriminator ?? null,
      run_id: this.identity?.runId ?? null,
      run_attempt: this.identity?.runAttempt ?? null,
      cap_profile: this.profile?.capProfile ?? null,
      measurement_only: this.profile?.measurementOnly ?? null,
      remaining_tail_required: this.profile?.remainingTailRequired ?? null,
      remaining_tail_reserve: this.profile?.remainingTailReserve ?? null,
    };
  }

  /** A protected-route receipt is safe to forward to the wrapper stderr sink. */
  receiptLine(): string | undefined {
    const receipt = this.receipt();
    return receipt.protected_route
      ? `${TRUSTED_PROOF_ARTIFACT_REST_REQUEST_RECEIPT_PREFIX}${JSON.stringify(receipt)}\n`
      : undefined;
  }

  /**
   * Called only after bridge intake has stopped and official calls are quiet.
   * It freezes the one canonical receipt and prevents a late authenticated
   * request from racing the evidence emitted to the Host fixture.
   */
  sealAndCreateReceipt(): string | undefined {
    if (!this.protectedRoute) return undefined;
    if (this.sealedReceiptLine !== undefined) return this.sealedReceiptLine;
    if (this.primaryReservations !== 0 || this.activeMutationReservation !== undefined) {
      throw new Error('artifact_rest_request_budget_not_quiescent');
    }
    this.sealedReceiptLine = this.receiptLine();
    return this.sealedReceiptLine;
  }
  private completeReservation(
    reservation: ArtifactRestRequestReservation,
    status: number | undefined,
    headers: HeadersLike | undefined,
    message: unknown,
  ): void {
    if (!reservation.protectedRoute || !reservation.primaryReserved) return;
    this.primaryReservations -= 1;
    if (status === 304) {
      const suppliedRemaining = this.observeRateLimitHeaders(
        status,
        headers,
        message,
      ).suppliedRemaining;
      if (!suppliedRemaining && this.observedPrimaryRemaining !== undefined) {
        this.observedPrimaryRemaining += 1;
      }
      this.conditionalNotModifiedRequests += 1;
      return;
    }
    this.primaryRateLimitRequests += 1;
    const rateLimit = this.observeRateLimitHeaders(status, headers, message);
    if (rateLimit.permissionDenied) this.permissionDenied += 1;
    if (this.disposition === 'active' && rateLimit.disposition !== undefined) {
      this.disposition = rateLimit.disposition;
    }
  }

  private authorizeDispatch(dispatch: ArtifactRestRequestDispatch): ArtifactRestRequestReservation {
    if (dispatch.signal.aborted) throw abortError();
    if (this.sealedReceiptLine !== undefined) {
      throw new Error('trusted_proof_artifact_rest_budget_sealed');
    }
    if (this.disposition === 'invalid_rate_limit_headers') {
      throw new ArtifactRestRateLimitHeadersError();
    }
    if (this.disposition !== 'active') {
      throw new ArtifactRestRequestBudgetError(this.disposition);
    }
    const mutation = this.activeMutationReservation;
    if (mutation && mutation.remainingRequests < 1) {
      throw new Error('artifact_rest_mutation_reservation_overconsumed');
    }
    if (
      this.totalRequests + this.reservedMutationRequests - (mutation ? 1 : 0) >=
      this.limits.maximumTotalAuthenticatedApiRequests
    ) {
      this.disposition = 'total_exhausted';
      throw new ArtifactRestRequestBudgetError(this.disposition);
    }
    if (
      this.observedPrimaryRemaining !== undefined &&
      this.observedPrimaryRemaining <= 1 + this.requiredTailAndReserve()
    ) {
      this.disposition = 'primary_exhausted';
      throw new ArtifactRestRequestBudgetError(this.disposition);
    }
    if (
      this.primaryRateLimitRequests +
        this.primaryReservations +
        this.reservedMutationPrimaryRequests -
        (mutation ? 1 : 0) >=
      this.limits.maximumPrimaryRateLimitRequests
    ) {
      this.disposition = 'primary_exhausted';
      throw new ArtifactRestRequestBudgetError(this.disposition);
    }
    if (mutation) {
      mutation.remainingRequests -= 1;
      mutation.remainingPrimaryRequests -= 1;
      mutation.remainingSecondaryPoints -= dispatch.secondaryLimitPoints;
      this.reservedMutationRequests -= 1;
      this.reservedMutationPrimaryRequests -= 1;
      this.reservedMutationSecondaryPoints -= dispatch.secondaryLimitPoints;
      if (mutation.remainingPrimaryRequests < 0 || mutation.remainingSecondaryPoints < 0) {
        throw new Error('artifact_rest_mutation_reservation_overconsumed');
      }
    }
    this.totalRequests += 1;
    this.primaryReservations += 1;
    this.secondaryLimitPoints += dispatch.secondaryLimitPoints;
    if (this.observedPrimaryRemaining !== undefined) this.observedPrimaryRemaining -= 1;
    return { protectedRoute: true, primaryReserved: true };
  }

  /**
   * GitHub's rate-limit headers are untrusted transport input.  A malformed
   * or self-contradictory value poisons the protected route rather than
   * allowing a later request to claim an unknowable allocation.  Retry-After
   * is only coherent on a rate-limited status; a success carrying it is also
   * rejected as contradictory.
   */
  private observeRateLimitHeaders(
    status: number | undefined,
    headers: HeadersLike | undefined,
    message: unknown,
  ): RateLimitObservation {
    if (status === undefined) {
      return { suppliedRemaining: false, permissionDenied: false };
    }
    const remaining = parseRateLimitInteger(header(headers, 'x-ratelimit-remaining'), 0);
    const limit = parseRateLimitInteger(header(headers, 'x-ratelimit-limit'), 1);
    const reset = parseRateLimitReset(header(headers, 'x-ratelimit-reset'));
    const retryAfter = parseRetryAfter(header(headers, 'retry-after'));
    const rateLimitStatus = status === 403 || status === 429;
    // A JSON error body is an optional secondary signal only on GitHub's two
    // rate-limit response forms. A success or 304 payload belongs to its
    // ordinary consumer and must not alter the budget taxonomy.
    const secondaryMessage = rateLimitStatus
      ? parseSecondaryRateLimitPayload(message)
      : { secondary: false, invalid: false };
    const primarySignalled = remaining.value === 0;
    const resetIsFuture = reset.value === undefined || reset.value > this.epochSeconds();
    if (
      remaining.invalid ||
      limit.invalid ||
      reset.invalid ||
      retryAfter.invalid ||
      secondaryMessage.invalid ||
      (remaining.value !== undefined &&
        limit.value !== undefined &&
        remaining.value > limit.value) ||
      (reset.value !== undefined && remaining.value === undefined) ||
      !resetIsFuture ||
      (retryAfter.value !== undefined && !rateLimitStatus) ||
      (rateLimitStatus && primarySignalled && reset.value === undefined)
    ) {
      this.disposition = 'invalid_rate_limit_headers';
      throw new ArtifactRestRateLimitHeadersError();
    }
    const primary = rateLimitStatus && primarySignalled && reset.value !== undefined;
    const secondary =
      rateLimitStatus && (retryAfter.value !== undefined || secondaryMessage.secondary);
    if (status === 429 && !primary && !secondary) {
      this.disposition = 'invalid_rate_limit_headers';
      throw new ArtifactRestRateLimitHeadersError();
    }
    const disposition =
      primary && secondary
        ? 'primary_and_secondary_rate_limited'
        : primary
          ? 'primary_exhausted'
          : secondary
            ? 'rate_limited'
            : undefined;
    if (remaining.value !== undefined) {
      this.observedPrimaryRemaining = remaining.value;
      if (remaining.value <= this.requiredTailAndReserve() && disposition === undefined) {
        this.disposition = 'primary_exhausted';
      }
    }
    return {
      suppliedRemaining: remaining.value !== undefined,
      permissionDenied: status === 403 && !primary && !secondary,
      disposition,
    };
  }

  private releaseMutationReservation(reservation: ActiveMutationReservation): void {
    if (reservation.released) return;
    reservation.released = true;
    if (this.activeMutationReservation !== reservation) return;
    this.reservedMutationRequests -= reservation.remainingRequests;
    this.reservedMutationPrimaryRequests -= reservation.remainingPrimaryRequests;
    this.reservedMutationSecondaryPoints -= reservation.remainingSecondaryPoints;
    this.activeMutationReservation = undefined;
  }

  private requiredTailAndReserve(): number {
    return (this.profile?.remainingTailRequired ?? 0) + (this.profile?.remainingTailReserve ?? 0);
  }
}

interface ActiveMutationReservation {
  remainingRequests: number;
  remainingPrimaryRequests: number;
  remainingSecondaryPoints: number;
  released: boolean;
}

interface ResponseLike {
  readonly status: number;
  readonly headers?: HeadersLike;
  readonly message?: unknown;
}

interface RateLimitObservation {
  readonly suppliedRemaining: boolean;
  readonly permissionDenied: boolean;
  readonly disposition?: Extract<
    ArtifactRestRequestBudgetDisposition,
    'primary_exhausted' | 'rate_limited' | 'primary_and_secondary_rate_limited'
  >;
}

type HeadersLike =
  | Headers
  | Readonly<Record<string, string | string[] | undefined>>
  | { readonly get: (name: string) => string | null | undefined };

function header(headers: HeadersLike | undefined, name: string): string | undefined {
  if (!headers) return undefined;
  const getter = (headers as { readonly get?: unknown }).get;
  if (typeof getter === 'function') {
    return (getter as (headerName: string) => string | null | undefined)(name) ?? undefined;
  }
  const record = headers as Readonly<Record<string, string | string[] | undefined>>;
  const expected = name.toLowerCase();
  const matches = Object.entries(record).filter(([key]) => key.toLowerCase() === expected);
  if (matches.length > 1) return '\u0000';
  const value = matches[0]?.[1];
  return Array.isArray(value) ? (value.length === 1 ? value[0] : '\u0000') : value;
}

interface ParsedRateLimitInteger {
  readonly value: number | undefined;
  readonly invalid: boolean;
}

const MAXIMUM_RATE_LIMIT_HEADER_VALUE = 1_000_000;
const MAXIMUM_RATE_LIMIT_RESET_EPOCH_SECONDS = 4_102_444_800;
const MAXIMUM_RATE_LIMIT_MESSAGE_LENGTH = 512;

function parseRateLimitInteger(value: string | undefined, minimum: number): ParsedRateLimitInteger {
  if (value === undefined) return { value: undefined, invalid: false };
  if (!/^(?:0|[1-9][0-9]*)$/.test(value)) return { value: undefined, invalid: true };
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) &&
    parsed >= minimum &&
    parsed <= MAXIMUM_RATE_LIMIT_HEADER_VALUE
    ? { value: parsed, invalid: false }
    : { value: undefined, invalid: true };
}

function parseRetryAfter(value: string | undefined): ParsedRateLimitInteger {
  // The Actions REST transport admits only delta-seconds.  A date-form value
  // cannot be related safely to the wrapper's monotonic operation clock.
  return parseRateLimitInteger(value, 0);
}

function parseRateLimitReset(value: string | undefined): ParsedRateLimitInteger {
  if (value === undefined) return { value: undefined, invalid: false };
  if (!/^[1-9][0-9]*$/.test(value)) return { value: undefined, invalid: true };
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) && parsed <= MAXIMUM_RATE_LIMIT_RESET_EPOCH_SECONDS
    ? { value: parsed, invalid: false }
    : { value: undefined, invalid: true };
}

function parseSecondaryRateLimitPayload(value: unknown): {
  readonly secondary: boolean;
  readonly invalid: boolean;
} {
  if (value === undefined) return { secondary: false, invalid: false };
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    return { secondary: false, invalid: true };
  }
  const entries = Object.entries(value).filter(([key]) => key === 'message');
  if (entries.length === 0) return { secondary: false, invalid: false };
  if (entries.length !== 1) return { secondary: false, invalid: true };
  return parseSecondaryRateLimitMessage(entries[0]![1]);
}

function parseSecondaryRateLimitMessage(value: unknown): {
  readonly secondary: boolean;
  readonly invalid: boolean;
} {
  if (
    typeof value !== 'string' ||
    value.length === 0 ||
    value.length > MAXIMUM_RATE_LIMIT_MESSAGE_LENGTH ||
    /[\u0000-\u001f\u007f]/u.test(value)
  ) {
    return { secondary: false, invalid: true };
  }
  return { secondary: /\bsecondary rate limit(?:ed|s)?\b/iu.test(value), invalid: false };
}

function messageFromPayload(value: unknown): unknown {
  return value;
}

function asResponseLike(error: unknown): ResponseLike | undefined {
  if (!error || typeof error !== 'object') return undefined;
  const candidate = error as {
    readonly status?: unknown;
    readonly headers?: unknown;
    readonly data?: unknown;
    readonly message?: unknown;
    readonly response?: {
      readonly status?: unknown;
      readonly headers?: unknown;
      readonly data?: unknown;
      readonly message?: unknown;
    };
  };
  const response = candidate.response ?? candidate;
  return typeof response.status === 'number'
    ? {
        status: response.status,
        headers: response.headers as HeadersLike | undefined,
        // Only GitHub's bounded JSON response `message` is authoritative. An
        // Octokit/Error message may be a local reason phrase or synthesized
        // diagnostic and must not classify the server response.
        message: messageFromPayload(response.data),
      }
    : undefined;
}

class ArtifactRestSecondaryRateLimiter {
  private readonly now: () => number;
  private readonly sleep: (milliseconds: number, signal: AbortSignal) => Promise<void>;
  private readonly maximumPointsPerRollingMinute: number;
  private readonly minimumMutativeSpacingMs: number;
  private readonly queue: SecondaryRateLimitTicket[] = [];
  private readonly dispatched: Array<{ readonly at: number; readonly points: number }> = [];
  private lastMutativeDispatchAt: number | undefined;
  private draining = false;

  constructor(options: ArtifactRestSecondaryRateLimitOptions | undefined) {
    this.now = options?.now ?? monotonicNow;
    this.sleep = options?.sleep ?? sleepWithAbort;
    this.maximumPointsPerRollingMinute =
      options?.maximumPointsPerRollingMinute ?? DEFAULT_MAXIMUM_SECONDARY_POINTS_PER_ROLLING_MINUTE;
    this.minimumMutativeSpacingMs =
      options?.minimumMutativeSpacingMs ?? DEFAULT_MINIMUM_MUTATIVE_SPACING_MS;
    if (
      !positiveInteger(this.maximumPointsPerRollingMinute) ||
      !nonNegativeInteger(this.minimumMutativeSpacingMs)
    ) {
      throw new Error('artifact_rest_secondary_rate_limit_options_invalid');
    }
  }

  async run<T>(
    dispatch: ArtifactRestRequestDispatch,
    call: (markDispatched: () => void) => Promise<T>,
  ): Promise<T> {
    if (dispatch.signal.aborted) throw abortError();
    if (dispatch.secondaryLimitPoints !== (dispatch.mutative ? 5 : 1)) {
      throw new Error('artifact_rest_secondary_rate_limit_dispatch_invalid');
    }
    return await new Promise<T>((resolve, reject) => {
      let ticket: SecondaryRateLimitTicket;
      ticket = {
        dispatch,
        call: async (markDispatched) => await call(markDispatched),
        resolve: (result) => resolve(result as T),
        reject,
        started: false,
        onAbort: () => {
          if (ticket.started) return;
          this.remove(ticket);
          reject(abortError());
          this.startDraining();
        },
      };
      dispatch.signal.addEventListener('abort', ticket.onAbort, { once: true });
      this.queue.push(ticket);
      this.startDraining();
    });
  }

  private startDraining(): void {
    if (this.draining) return;
    this.draining = true;
    void this.drain();
  }

  private async drain(): Promise<void> {
    try {
      for (;;) {
        const ticket = this.queue[0];
        if (!ticket) return;
        if (ticket.dispatch.signal.aborted) {
          this.remove(ticket);
          ticket.reject(abortError());
          continue;
        }
        if (!this.canStartBeforeDeadline(ticket.dispatch)) {
          this.remove(ticket);
          ticket.reject(new ArtifactRestAttemptDeadlineError());
          continue;
        }
        const waitMs = this.waitMilliseconds(ticket.dispatch);
        if (
          ticket.dispatch.latestAttemptStartAt !== undefined &&
          this.now() + waitMs > ticket.dispatch.latestAttemptStartAt
        ) {
          this.remove(ticket);
          ticket.reject(new ArtifactRestAttemptDeadlineError());
          continue;
        }
        if (waitMs > 0) {
          try {
            await this.sleep(waitMs, ticket.dispatch.signal);
          } catch (error) {
            if (ticket.dispatch.signal.aborted) {
              this.remove(ticket);
              ticket.reject(abortError());
              continue;
            }
            this.remove(ticket);
            ticket.reject(error);
            continue;
          }
          continue;
        }
        this.queue.shift();
        ticket.dispatch.signal.removeEventListener('abort', ticket.onAbort);
        ticket.started = true;
        let marked = false;
        const markDispatched = (): void => {
          if (marked) throw new Error('artifact_rest_secondary_rate_limit_marked_twice');
          marked = true;
          const at = this.now();
          this.dispatched.push({ at, points: ticket.dispatch.secondaryLimitPoints });
          if (ticket.dispatch.mutative) this.lastMutativeDispatchAt = at;
        };
        try {
          const result = await ticket.call(markDispatched);
          if (!marked) throw new Error('artifact_rest_secondary_rate_limit_unmarked_call');
          ticket.resolve(result);
        } catch (error) {
          ticket.reject(error);
        }
      }
    } finally {
      this.draining = false;
      if (this.queue.length > 0) this.startDraining();
    }
  }

  private waitMilliseconds(dispatch: ArtifactRestRequestDispatch): number {
    const now = this.now();
    this.evictExpired(now);
    const usedPoints = this.dispatched.reduce((total, entry) => total + entry.points, 0);
    const rollingWait =
      usedPoints + dispatch.secondaryLimitPoints > this.maximumPointsPerRollingMinute
        ? this.waitForRollingCapacity(now)
        : 0;
    const pointPacing =
      this.dispatched.length === 0
        ? 0
        : this.dispatched[this.dispatched.length - 1]!.at +
          dispatch.secondaryLimitPoints * 100 -
          now;
    const mutativeSpacing =
      dispatch.mutative && this.lastMutativeDispatchAt !== undefined
        ? this.lastMutativeDispatchAt + this.minimumMutativeSpacingMs - now
        : 0;
    return Math.max(0, rollingWait, pointPacing, mutativeSpacing);
  }

  private canStartBeforeDeadline(dispatch: ArtifactRestRequestDispatch): boolean {
    return (
      dispatch.latestAttemptStartAt === undefined || this.now() <= dispatch.latestAttemptStartAt
    );
  }

  private waitForRollingCapacity(now: number): number {
    const first = this.dispatched[0];
    if (!first) throw new Error('artifact_rest_secondary_rate_limit_state_invalid');
    return Math.max(1, first.at + ROLLING_MINUTE_MS - now);
  }

  private evictExpired(now: number): void {
    while (this.dispatched[0] && this.dispatched[0].at + ROLLING_MINUTE_MS <= now) {
      this.dispatched.shift();
    }
  }

  private remove(ticket: SecondaryRateLimitTicket): void {
    const index = this.queue.indexOf(ticket);
    if (index >= 0) this.queue.splice(index, 1);
    ticket.dispatch.signal.removeEventListener('abort', ticket.onAbort);
  }
}

interface SecondaryRateLimitTicket {
  readonly dispatch: ArtifactRestRequestDispatch;
  readonly call: (markDispatched: () => void) => Promise<unknown>;
  readonly resolve: (result: unknown) => void;
  readonly reject: (error: unknown) => void;
  started: boolean;
  readonly onAbort: () => void;
}

function sleepWithAbort(milliseconds: number, signal: AbortSignal): Promise<void> {
  return awaitableTimeout(milliseconds, signal);
}

function awaitableTimeout(milliseconds: number, signal: AbortSignal): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    if (signal.aborted) {
      reject(abortError());
      return;
    }
    const timer = setTimeout(() => {
      signal.removeEventListener('abort', onAbort);
      resolve();
    }, milliseconds);
    const onAbort = () => {
      clearTimeout(timer);
      reject(abortError());
    };
    signal.addEventListener('abort', onAbort, { once: true });
  });
}

function abortError(): Error {
  const error = new Error('artifact_rest_secondary_rate_limit_cancelled');
  error.name = 'AbortError';
  return error;
}

export class ArtifactRestAttemptDeadlineError extends Error {
  constructor() {
    super('artifact_rest_attempt_deadline_exceeded');
    this.name = 'ArtifactRestAttemptDeadlineError';
  }
}

function monotonicNow(): number {
  return performance.now();
}

function validIdentity(
  value: ArtifactRestReceiptIdentity | undefined,
): value is ArtifactRestReceiptIdentity {
  return (
    value !== undefined &&
    /^[A-Za-z0-9._-]+\/[A-Za-z0-9._-]+$/u.test(value.repository) &&
    positiveDecimal(value.repositoryId, 19) &&
    lowerHex(value.workflowSha, 40) &&
    lowerHex(value.actionSourceSha, 40) &&
    lowerHex(value.payloadSha256, 64) &&
    value.buildDiscriminator === TRUSTED_PROOF_PREPARED_PAYLOAD_BUILD_DISCRIMINATOR &&
    positiveDecimal(value.runId, 19) &&
    positiveDecimal(value.runAttempt, 10)
  );
}

function positiveDecimal(value: string, maximumLength: number): boolean {
  return new RegExp(`^[1-9][0-9]{0,${maximumLength - 1}}$`, 'u').test(value);
}

function lowerHex(value: string, length: number): boolean {
  return new RegExp(`^[0-9a-f]{${length}}$`, 'u').test(value);
}

function positiveInteger(value: number): boolean {
  return Number.isSafeInteger(value) && value > 0;
}

function validProfile(
  value: ArtifactRestRequestBudgetProfile | undefined,
): value is ArtifactRestRequestBudgetProfile {
  return (
    value !== undefined &&
    value.capProfile === TRUSTED_PROOF_ARTIFACT_REST_CAP_PROFILE &&
    typeof value.limits === 'object' &&
    value.limits !== null &&
    nonNegativeInteger(value.remainingTailRequired) &&
    positiveInteger(value.remainingTailReserve) &&
    typeof value.measurementOnly === 'boolean'
  );
}

function nonNegativeInteger(value: number): boolean {
  return Number.isSafeInteger(value) && value >= 0;
}
