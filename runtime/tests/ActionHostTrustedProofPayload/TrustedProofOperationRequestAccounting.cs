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
    internal const int OperationPrimaryReserve = 1;
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

    internal static TrustedProofRateClassification RateClassify(
        HttpResponseMessage response,
        long? currentUnixSeconds = null) =>
        ActionHostGitHubRateLimitClassifier.Classify(response, currentUnixSeconds) switch
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
        return TryReadRemaining(response, out var remaining, out var present) &&
            present && remaining <= tailGuard.MinimumRemainingAfter(currentDomain);
    }

    internal static TrustedProofResponseClass ResponseClassify(
        HttpResponseMessage response,
        long? currentUnixSeconds = null) => RateClassify(response, currentUnixSeconds) switch
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
        TrustedProofOperationRequestAccounting.OperationPrimaryReserve,
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

internal sealed class TrustedProofRequestBudgetProfile(
    TrustedProofRemainingTailGuard remainingTailGuard)
{
    private const string ProfileEnvironment =
        "AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE";

    // These constants are intentionally empty until the reviewed AOT
    // measurement freezes both the six-role allocation and these four shared
    // token tails.  A normal production invocation therefore cannot silently
    // fall back to measurement mode.
    private static readonly IReadOnlyDictionary<TrustedProofRequestDomain, int>
        FrozenTailByDomain = new Dictionary<TrustedProofRequestDomain, int>();
    private const int FrozenReserve = -1;

    internal static readonly TrustedProofRequestBudgetProfile Measurement = new(
        TrustedProofRemainingTailGuard.Measurement);

    internal TrustedProofRemainingTailGuard RemainingTailGuard { get; } =
        remainingTailGuard;

    internal bool MeasurementOnly => RemainingTailGuard.MeasurementOnly;

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

        if (!StringComparer.Ordinal.Equals(requested, "final") ||
            !TryGetFrozenTailProfile(out var frozenTailByDomain, out var reserve))
        {
            profile = null;
            return false;
        }

        profile = new TrustedProofRequestBudgetProfile(
            new TrustedProofRemainingTailGuard(frozenTailByDomain, reserve,
                measurementOnly: false));
        return true;
    }

    internal static bool TryGetFrozenTailProfile(
        out IReadOnlyDictionary<TrustedProofRequestDomain, int> tailByDomain,
        out int reserve)
    {
        tailByDomain = FrozenTailByDomain;
        reserve = FrozenReserve;
        return reserve >= 0 && tailByDomain.Count == 4 &&
            new[]
            {
                TrustedProofRequestDomain.NodeArtifactRest,
                TrustedProofRequestDomain.HostHeadSourceRest,
                TrustedProofRequestDomain.HostOtherGitHubRest,
                TrustedProofRequestDomain.TrustedControlRest,
            }.All(tailByDomain.ContainsKey) && tailByDomain.Values.All(value => value >= 0);
    }
}
