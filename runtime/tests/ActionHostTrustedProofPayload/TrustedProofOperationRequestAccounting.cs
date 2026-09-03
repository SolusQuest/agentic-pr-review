using System.Globalization;
using System.Net;
using AgenticPrReview.Runtime.ActionHost.GitHub;

#if ACTION_HOST_VERIFIER_FIXTURE
namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;
#else
namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
#endif

// The allocation is deliberately a measurement contract until the first
// complete synthetic AOT run supplies the reviewed role maxima.  It must not
// be mistaken for a production primary-rate-limit bound.
internal enum TrustedProofRequestDomain
{
    NodeArtifactRest,
    HostHeadSourceRest,
    HostOtherGitHubRest,
    TrustedControlRest,
    ActionsResultsService,
    AnonymousTransfers,
}

internal enum TrustedProofRateClassification
{
    None,
    Permission,
    Primary,
    Secondary,
    Combined,
    InvalidRemaining,
}

internal enum TrustedProofResponseClass
{
    Success,
    NotModified,
    PermissionDenied,
    PrimaryRateLimited,
    SecondaryRateLimited,
    CombinedRateLimited,
    InvalidRateHeaders,
    OtherFailure,
}

// The operation-wide witness merges platform-owned Node/data-plane dispatches
// with verifier-owned Host/control dispatch traces after every case is quiet.
// This type owns the shared response taxonomy and tail interpretation; it does
// not own another event queue, which would omit one of those producer paths.
internal static class TrustedProofOperationRequestAccounting
{
    internal const int OperationPrimaryBudget = 1000;
    // Measurement is an explicitly non-final diagnostic profile.
    internal const int MeasurementPrimaryReserve = 1;
    // Normal work leaves this safety/cleanup reserve. Cleanup alone may spend
    // it under its separate 64-request ceiling; successful proof still leaves 64.
    internal const int OperationPrimaryReserve = 64;
    // The trusted payload attaches this secret-free semantic route label before
    // its in-process verifier handler dispatches.  The independent verifier
    // witness must consume this value rather than infer meaning from a SHA in
    // an otherwise ambiguous GitHub URI.
    internal static readonly HttpRequestOptionsKey<string> WitnessDomainOption =
        new("agentic-pr-review.trusted-proof.request-domain");
    private const int MaximumRateLimitHeaderValue = 1_000_000;

    internal static string WitnessDomain(TrustedProofRequestDomain domain) =>
        domain switch
        {
            TrustedProofRequestDomain.HostHeadSourceRest =>
                "host_head_source_rest",
            TrustedProofRequestDomain.HostOtherGitHubRest =>
                "host_other_github_rest",
            TrustedProofRequestDomain.TrustedControlRest =>
                "trusted_control_rest",
            _ => throw new ArgumentOutOfRangeException(nameof(domain)),
        };

    internal static async ValueTask<TrustedProofRateClassification> RateClassifyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        long? currentUnixSeconds = null) =>
        (await ActionHostGitHubRateLimitClassifier.ClassifyAsync(
            response, cancellationToken, currentUnixSeconds).ConfigureAwait(false)) switch
        {
            ActionHostGitHubRateLimitClassification.Permission =>
                TrustedProofRateClassification.Permission,
            ActionHostGitHubRateLimitClassification.Primary =>
                TrustedProofRateClassification.Primary,
            ActionHostGitHubRateLimitClassification.Secondary =>
                TrustedProofRateClassification.Secondary,
            ActionHostGitHubRateLimitClassification.Combined =>
                TrustedProofRateClassification.Combined,
            ActionHostGitHubRateLimitClassification.Invalid =>
                TrustedProofRateClassification.InvalidRemaining,
            _ => TrustedProofRateClassification.None,
        };

    internal static bool RemainingRequiresFailClosed(
        HttpResponseMessage response,
        TrustedProofRemainingTailGuard tailGuard,
        TrustedProofRequestDomain currentDomain)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(tailGuard);
        // The current response has already consumed its request.  Equality
        // leaves exactly the protected future tail plus reserve, so only a
        // strictly smaller remaining value is unsafe.
        return TryGetRemaining(response, out var remaining) &&
            remaining < tailGuard.MinimumRemainingAfter(currentDomain);
    }

    internal static bool TryGetRemaining(
        HttpResponseMessage response,
        out int remaining)
    {
        ArgumentNullException.ThrowIfNull(response);
        return TryReadRemaining(response, out remaining, out var present) && present;
    }

    internal static async ValueTask<TrustedProofResponseClass> ResponseClassifyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        long? currentUnixSeconds = null) =>
        (await RateClassifyAsync(response, cancellationToken, currentUnixSeconds)
            .ConfigureAwait(false)) switch
        {
            TrustedProofRateClassification.Permission => TrustedProofResponseClass.PermissionDenied,
            TrustedProofRateClassification.Primary => TrustedProofResponseClass.PrimaryRateLimited,
            TrustedProofRateClassification.Secondary => TrustedProofResponseClass.SecondaryRateLimited,
            TrustedProofRateClassification.Combined => TrustedProofResponseClass.CombinedRateLimited,
            TrustedProofRateClassification.InvalidRemaining =>
                TrustedProofResponseClass.InvalidRateHeaders,
            _ when response.StatusCode == HttpStatusCode.NotModified =>
                TrustedProofResponseClass.NotModified,
            _ when response.IsSuccessStatusCode => TrustedProofResponseClass.Success,
            _ => TrustedProofResponseClass.OtherFailure,
        };

    // SyntheticOfficialPlatform records this normalized vocabulary after it
    // forwards an official response.  Keeping the mapping here makes the
    // producer receipt and the independent witness agree on the same parsed
    // response signal without retaining the response body in evidence.
    internal static string WitnessResponseClass(
        TrustedProofResponseClass responseClass) => responseClass switch
        {
            TrustedProofResponseClass.Success => "success",
            TrustedProofResponseClass.NotModified => "not_modified",
            TrustedProofResponseClass.PermissionDenied => "permission_denied",
            TrustedProofResponseClass.PrimaryRateLimited => "primary_rate_limited",
            TrustedProofResponseClass.SecondaryRateLimited => "secondary_rate_limited",
            TrustedProofResponseClass.CombinedRateLimited => "combined_rate_limited",
            TrustedProofResponseClass.InvalidRateHeaders => "invalid_rate_headers",
            _ => "other_failure",
        };

    private static bool TryReadRemaining(
        HttpResponseMessage response,
        out int remaining,
        out bool present)
    {
        remaining = 0;
        present = response.Headers.TryGetValues("x-ratelimit-remaining", out var raw);
        if (!present) return true;
        var values = raw!.ToArray();
        return values.Length == 1 && int.TryParse(values[0], NumberStyles.None,
            CultureInfo.InvariantCulture, out remaining) && remaining >= 0 &&
            remaining <= MaximumRateLimitHeaderValue;
    }

}

// A remaining header is a shared-token observation, not a local transport
// quota.  The eventual AOT freeze supplies the primary requests still needed
// by every protected role after each domain's observation, plus an explicit
// reserve.  The measurement profile deliberately has no tail allocations and
// is therefore evidence-only; Framework refuses it as a final/live verdict.
internal sealed class TrustedProofRemainingTailGuard(
    IReadOnlyDictionary<TrustedProofRequestDomain, int> remainingTailByDomain,
    int reserve,
    bool measurementOnly)
{
    internal static readonly TrustedProofRemainingTailGuard Measurement = new(
        new Dictionary<TrustedProofRequestDomain, int>
        {
            [TrustedProofRequestDomain.NodeArtifactRest] = 0,
            [TrustedProofRequestDomain.HostHeadSourceRest] = 0,
            [TrustedProofRequestDomain.HostOtherGitHubRest] = 0,
            [TrustedProofRequestDomain.TrustedControlRest] = 0,
        },
        TrustedProofOperationRequestAccounting.MeasurementPrimaryReserve,
        measurementOnly: true);

    internal bool MeasurementOnly { get; } = measurementOnly;

    internal int Reserve => reserve;

    internal int RequiredTail(TrustedProofRequestDomain currentDomain)
    {
        if (!remainingTailByDomain.TryGetValue(currentDomain, out var required) ||
            required < 0)
        {
            throw new InvalidOperationException("trusted_proof_remaining_tail_unfrozen");
        }

        return required;
    }

    internal int MinimumRemainingAfter(TrustedProofRequestDomain currentDomain)
    {
        if (reserve < 0)
        {
            throw new InvalidOperationException("trusted_proof_remaining_tail_unfrozen");
        }

        return checked(RequiredTail(currentDomain) + reserve);
    }
}

// GitHub's primary-rate-limit bucket belongs to the token, rather than to a
// particular HttpClient or proof role.  A proof composes several transports
// over that one token, so each admitted authenticated wire request obtains a
// one-shot lease from this operation-wide ledger.  The lease is intentionally
// pessimistic: a normal response without a remaining header keeps its debit;
// a 304 (with or without a remaining header) can prove that its pre-debit did
// not consume primary quota.  A late higher header is never allowed to undo a
// lower observation.
internal sealed class TrustedProofPrimaryRemainingLedger
{
    private readonly object _gate = new();
    private int? _remaining;
    // Local completions and shared-header progress are independent estimates.
    // Keeping them separate prevents a lower header that already includes a
    // pending local request from consuming that request again on settlement.
    private int? _localFuturePrimaryRequests;
    private int? _sharedFuturePrimaryRequests;
    private int? _primaryProgressAnchorRemaining;
    private int? _primaryProgressAnchorFutureRequests;
    private readonly HashSet<TrustedProofPrimaryRemainingLease> _unbackedLeases = [];
    private int _pendingUnobservedPrimaryCharges;
    private TrustedProofPrimaryRemainingLedgerCloseReason _closeReason;

    internal bool IsClosed
    {
        get
        {
            lock (_gate) return _closeReason !=
                TrustedProofPrimaryRemainingLedgerCloseReason.None;
        }
    }

    internal TrustedProofPrimaryRemainingLedgerCloseReason CloseReason
    {
        get
        {
            lock (_gate) return _closeReason;
        }
    }

    internal bool TryLease(
        TrustedProofRequestDomain domain,
        TrustedProofRemainingTailGuard tailGuard,
        out TrustedProofPrimaryRemainingLease? lease)
    {
        ArgumentNullException.ThrowIfNull(tailGuard);
        lock (_gate)
        {
            if (_closeReason != TrustedProofPrimaryRemainingLedgerCloseReason.None)
            {
                lease = null;
                return false;
            }

            var futureBefore = CurrentFuturePrimaryRequests(domain, tailGuard);
            var localFutureBefore = CurrentLocalFuturePrimaryRequests(domain,
                tailGuard);
            var requiredBefore = checked(futureBefore + tailGuard.Reserve);
            if (_remaining is { } remaining)
            {
                if (remaining < requiredBefore)
                {
                    _closeReason =
                        TrustedProofPrimaryRemainingLedgerCloseReason.LowRemaining;
                    lease = null;
                    return false;
                }

                _remaining = remaining - 1;
                lease = new TrustedProofPrimaryRemainingLease(this,
                    domain, tailGuard, Math.Max(0, localFutureBefore - 1),
                    preDebited: true);
                return true;
            }

            lease = new TrustedProofPrimaryRemainingLease(this,
                domain, tailGuard, Math.Max(0, localFutureBefore - 1),
                preDebited: false);
            _unbackedLeases.Add(lease);
            return true;
        }
    }

    internal void Observe(
        TrustedProofPrimaryRemainingLease? lease,
        HttpResponseMessage response,
        TrustedProofResponseClass responseClass,
        TrustedProofRequestDomain domain,
        TrustedProofRemainingTailGuard tailGuard)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(tailGuard);
        ValidateLease(lease, domain, tailGuard);
        if (lease is not null && !lease.TrySettle()) return;

        lock (_gate)
        {
            var unbacked = lease is { PreDebited: false };
            var localFutureAfterResponse = lease?.LocalFutureAfterResponse ??
                Math.Max(0, CurrentLocalFuturePrimaryRequests(domain,
                    tailGuard) - 1);
            if (unbacked) _unbackedLeases.Remove(lease!);

            if (responseClass is TrustedProofResponseClass.InvalidRateHeaders or
                TrustedProofResponseClass.PrimaryRateLimited or
                TrustedProofResponseClass.SecondaryRateLimited or
                TrustedProofResponseClass.CombinedRateLimited)
            {
                _closeReason = TrustedProofPrimaryRemainingLedgerCloseReason.Terminal;
                return;
            }

            if (_closeReason != TrustedProofPrimaryRemainingLedgerCloseReason.None) return;

            // A 304 is the sole response class that proves this physical
            // request did not consume primary quota.  Restore its prior debit
            // before any header is folded into the concurrent-wave minimum.
            // This covers both a known pre-debit and an earlier observation's
            // conservative charge for an initially headerless concurrent
            // lease.
            var refund304 = response.StatusCode == HttpStatusCode.NotModified &&
                lease is { } && (lease.PreDebited || lease.CoveredByObservedHeader);
            if (refund304 && _remaining is { } refundCurrent)
            {
                _remaining = checked(refundCurrent + 1);
            }

            if (response.StatusCode != HttpStatusCode.NotModified)
            {
                _localFuturePrimaryRequests =
                    _localFuturePrimaryRequests is { } currentFuture
                        ? Math.Max(0, currentFuture - 1)
                        : localFutureAfterResponse;
            }

            var hasObservedRemaining =
                TrustedProofOperationRequestAccounting.TryGetRemaining(
                    response, out var observed);
            if (!hasObservedRemaining && unbacked && lease is { } &&
                !lease.CoveredByObservedHeader &&
                response.StatusCode != HttpStatusCode.NotModified)
            {
                // No earlier header backed this initially concurrent lease.
                // Preserve its pessimistic primary charge so a later, delayed
                // higher header cannot erase a headerless request that already
                // completed.
                _pendingUnobservedPrimaryCharges++;
            }

            if (hasObservedRemaining)
            {
                // A response can arrive before another already-dispatched
                // headerless request.  Charge those unsettled unknown leases
                // before publishing the first observation, and remember that
                // their eventual headerless 304 must refund this debit.
                foreach (var unsettled in _unbackedLeases)
                {
                    unsettled.MarkCoveredByObservedHeader();
                }
                var conservative = checked(observed - _unbackedLeases.Count -
                    _pendingUnobservedPrimaryCharges);
                if (conservative < 0)
                {
                    _closeReason =
                        TrustedProofPrimaryRemainingLedgerCloseReason.Terminal;
                    return;
                }

                _remaining = _remaining is { } current
                    ? Math.Min(current, conservative)
                    : conservative;
                _pendingUnobservedPrimaryCharges = 0;
                ObserveSharedPrimaryProgress(observed, domain, tailGuard);
            }
            var future = CurrentFuturePrimaryRequests(domain, tailGuard,
                protectUnexpectedDispatch: false);
            if (_remaining is { } remaining && remaining < checked(
                    future + tailGuard.Reserve))
            {
                _closeReason =
                    TrustedProofPrimaryRemainingLedgerCloseReason.LowRemaining;
            }
        }
    }

    internal void AbortBeforeWire(TrustedProofPrimaryRemainingLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateLease(lease, lease.Domain, lease.TailGuard);
        if (!lease.TrySettle()) return;
        lock (_gate)
        {
            if (lease.PreDebited && _remaining is { } current)
            {
                _remaining = checked(current + 1);
            }
            else if (!lease.PreDebited)
            {
                _unbackedLeases.Remove(lease);
                if (lease.CoveredByObservedHeader &&
                    _remaining is { } coveredRemaining)
                {
                    // A concurrent response already charged this lease in its
                    // conservative wave minimum.  A local rejection before
                    // wire must undo that phantom charge just like an ordinary
                    // known-balance pre-debit.
                    _remaining = checked(coveredRemaining + 1);
                }
            }
        }
    }

    internal void CloseOutcomeUnknown(TrustedProofPrimaryRemainingLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateLease(lease, lease.Domain, lease.TailGuard);
        if (!lease.TrySettle()) return;
        lock (_gate)
        {
            if (!lease.PreDebited) _unbackedLeases.Remove(lease);
            _closeReason = TrustedProofPrimaryRemainingLedgerCloseReason.Terminal;
        }
    }

    internal void Close()
    {
        lock (_gate) _closeReason = TrustedProofPrimaryRemainingLedgerCloseReason.Terminal;
    }

    private int CurrentFuturePrimaryRequests(
        TrustedProofRequestDomain domain,
        TrustedProofRemainingTailGuard tailGuard,
        bool protectUnexpectedDispatch = true)
    {
        var local = CurrentLocalFuturePrimaryRequests(domain, tailGuard);
        var future = _sharedFuturePrimaryRequests is { } shared
            ? Math.Min(local, shared)
            : local;
        return protectUnexpectedDispatch ? Math.Max(1, future) : future;
    }

    private int CurrentLocalFuturePrimaryRequests(
        TrustedProofRequestDomain domain,
        TrustedProofRemainingTailGuard tailGuard)
    {
        var frozen = checked(1 + tailGuard.RequiredTail(domain));
        return _localFuturePrimaryRequests is { } local
            ? Math.Min(frozen, local)
            : frozen;
    }

    private void ObserveSharedPrimaryProgress(
        int observed,
        TrustedProofRequestDomain domain,
        TrustedProofRemainingTailGuard tailGuard)
    {
        var future = CurrentFuturePrimaryRequests(domain, tailGuard,
            protectUnexpectedDispatch: false);
        if (_primaryProgressAnchorRemaining is null ||
            _primaryProgressAnchorFutureRequests is null)
        {
            _primaryProgressAnchorRemaining = observed;
            _primaryProgressAnchorFutureRequests = future;
            return;
        }

        var globallyCharged = Math.Max(0,
            _primaryProgressAnchorRemaining.Value - observed);
        var sharedFuture = Math.Max(0,
            _primaryProgressAnchorFutureRequests.Value - globallyCharged);
        _sharedFuturePrimaryRequests = _sharedFuturePrimaryRequests is { } current
            ? Math.Min(current, sharedFuture)
            : sharedFuture;
    }

    private void ValidateLease(
        TrustedProofPrimaryRemainingLease? lease,
        TrustedProofRequestDomain domain,
        TrustedProofRemainingTailGuard tailGuard)
    {
        if (lease is null) return;
        if (ReferenceEquals(lease.Ledger, this) && lease.Domain == domain &&
            ReferenceEquals(lease.TailGuard, tailGuard)) return;

        Close();
        throw new InvalidOperationException("trusted_proof_primary_remaining_lease_mismatch");
    }
}

internal enum TrustedProofPrimaryRemainingLedgerCloseReason
{
    None,
    LowRemaining,
    Terminal,
}

internal sealed class TrustedProofPrimaryRemainingLease
{
    private int _settled;

    internal TrustedProofPrimaryRemainingLease(
        TrustedProofPrimaryRemainingLedger ledger,
        TrustedProofRequestDomain domain,
        TrustedProofRemainingTailGuard tailGuard,
        int localFutureAfterResponse,
        bool preDebited)
    {
        Ledger = ledger;
        Domain = domain;
        TailGuard = tailGuard;
        LocalFutureAfterResponse = localFutureAfterResponse;
        PreDebited = preDebited;
    }

    internal TrustedProofPrimaryRemainingLedger Ledger { get; }
    internal TrustedProofRequestDomain Domain { get; }
    internal TrustedProofRemainingTailGuard TailGuard { get; }
    internal int LocalFutureAfterResponse { get; }
    internal bool PreDebited { get; }
    internal bool CoveredByObservedHeader { get; private set; }
    internal void MarkCoveredByObservedHeader() => CoveredByObservedHeader = true;
    internal bool TrySettle() => Interlocked.Exchange(ref _settled, 1) == 0;
}

internal enum TrustedProofRequestBudgetLane
{
    Host,
    ExternalControl,
    CleanupControl,
}

internal sealed class TrustedProofRequestBudgetProfile(
    TrustedProofRemainingTailGuard hostRemainingTailGuard,
    TrustedProofRemainingTailGuard externalControlRemainingTailGuard,
    TrustedProofRemainingTailGuard cleanupControlRemainingTailGuard)
{
    private const string ProfileEnvironment =
        "AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE";

    private const int SafetyReserve =
        TrustedProofOperationRequestAccounting.OperationPrimaryReserve;

    internal static readonly TrustedProofRequestBudgetProfile Measurement = new(
        TrustedProofRemainingTailGuard.Measurement,
        TrustedProofRemainingTailGuard.Measurement,
        TrustedProofRemainingTailGuard.Measurement);

    internal TrustedProofRemainingTailGuard HostRemainingTailGuard { get; } =
        hostRemainingTailGuard;

    internal TrustedProofRemainingTailGuard ExternalControlRemainingTailGuard { get; } =
        externalControlRemainingTailGuard;

    internal TrustedProofRemainingTailGuard CleanupControlRemainingTailGuard { get; } =
        cleanupControlRemainingTailGuard;

    internal bool MeasurementOnly => HostRemainingTailGuard.MeasurementOnly;

    internal TrustedProofRemainingTailGuard ControlRemainingTailGuard(string[] args) =>
        args is ["cleanup"]
            ? CleanupControlRemainingTailGuard
            : ExternalControlRemainingTailGuard;

    internal static bool TrySelectProduction(
        Func<string, string?> getEnvironment,
        out TrustedProofRequestBudgetProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(getEnvironment);
        var requested = getEnvironment(ProfileEnvironment);
        if (StringComparer.Ordinal.Equals(requested, "measurement"))
        {
            profile = Measurement;
            return true;
        }

        if (requested is not ("final-bootstrap" or "final-continuation" or
            "final-stale") || !TryGetFinalSafetyProfile(requested,
                TrustedProofRequestBudgetLane.Host, out var host, out var reserve) ||
            !TryGetFinalSafetyProfile(requested,
                TrustedProofRequestBudgetLane.ExternalControl, out var external,
                out var externalReserve) ||
            !TryGetFinalSafetyProfile(requested,
                TrustedProofRequestBudgetLane.CleanupControl, out var cleanup,
                out var cleanupReserve))
        {
            profile = null;
            return false;
        }

        profile = new TrustedProofRequestBudgetProfile(
            new TrustedProofRemainingTailGuard(host, reserve, measurementOnly: false),
            new TrustedProofRemainingTailGuard(external, externalReserve, measurementOnly: false),
            new TrustedProofRemainingTailGuard(cleanup, cleanupReserve, measurementOnly: false));
        return true;
    }

    internal static bool TryGetFinalSafetyProfile(
        string requested,
        TrustedProofRequestBudgetLane lane,
        out IReadOnlyDictionary<TrustedProofRequestDomain, int> tailByDomain,
        out int reserve)
    {
        // The labels select proof scenarios, not measured future allocations.
        // Current requests and compound mutations still obtain real leases.
        tailByDomain = new Dictionary<TrustedProofRequestDomain, int>
        {
            [TrustedProofRequestDomain.NodeArtifactRest] = 0,
            [TrustedProofRequestDomain.HostHeadSourceRest] = 0,
            [TrustedProofRequestDomain.HostOtherGitHubRest] = 0,
            [TrustedProofRequestDomain.TrustedControlRest] = 0,
        };
        reserve = lane == TrustedProofRequestBudgetLane.CleanupControl
            ? 0 : SafetyReserve;
        return requested is "final-bootstrap" or "final-continuation" or "final-stale" &&
            lane is TrustedProofRequestBudgetLane.Host or
                TrustedProofRequestBudgetLane.ExternalControl or
                TrustedProofRequestBudgetLane.CleanupControl;
    }
}
