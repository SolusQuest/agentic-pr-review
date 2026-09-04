using System.Globalization;
using System.Net;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

internal sealed class TrustedProofGitHubRequestBudget
{
    internal const int MaximumAuthenticatedRestRequests = 256;
    internal const int MaximumAnonymousCodeloadRequests = 1;

    private readonly int _maximumAuthenticatedRestRequests;
    private readonly int _maximumAnonymousCodeloadRequests;
    private readonly Func<HttpMessageHandler> _innerFactory;
    private readonly TrustedProofRemainingTailGuard _remainingTailGuard;
    private readonly TrustedProofPrimaryRemainingLedger _remainingLedger;
    private readonly Func<long> _epochSeconds;
    private int _authenticatedRestRequests;
    private int _anonymousCodeloadRequests;
    private int _rejectedRequests;
    private int _headSourceRaw;
    private int _otherGitHubRaw;
    private int _headSourcePrimary;
    private int _otherGitHubPrimary;
    private int _headSourceNotModified;
    private int _otherGitHubNotModified;
    private int _headSourcePermission;
    private int _otherGitHubPermission;
    private int _headSourcePrimaryRateLimited;
    private int _otherGitHubPrimaryRateLimited;
    private int _headSourceSecondaryRateLimited;
    private int _otherGitHubSecondaryRateLimited;
    private int _headSourceCombinedRateLimited;
    private int _otherGitHubCombinedRateLimited;
    private int _headSourceInvalidRateHeaders;
    private int _otherGitHubInvalidRateHeaders;
    private int _headSourceSecondaryPoints;
    private int _otherGitHubSecondaryPoints;
    private int _invalidRemainingHeader;
    private int _rateLimited;
    private int _lowRemainingGuard;

    internal TrustedProofGitHubRequestBudget()
        : this(
            MaximumAuthenticatedRestRequests,
            MaximumAnonymousCodeloadRequests,
            static () => CreateInnerHandler(),
            TrustedProofRemainingTailGuard.Measurement,
            remainingLedger: new TrustedProofPrimaryRemainingLedger())
    {
    }

    internal TrustedProofGitHubRequestBudget(
        int maximumAuthenticatedRestRequests,
        int maximumAnonymousCodeloadRequests,
        Func<HttpMessageHandler> innerFactory,
        TrustedProofRemainingTailGuard? remainingTailGuard = null,
        Func<long>? epochSeconds = null,
        TrustedProofPrimaryRemainingLedger? remainingLedger = null)
    {
        _maximumAuthenticatedRestRequests =
            maximumAuthenticatedRestRequests > 0
                ? maximumAuthenticatedRestRequests
                : throw new ArgumentOutOfRangeException(
                    nameof(maximumAuthenticatedRestRequests));
        _maximumAnonymousCodeloadRequests =
            maximumAnonymousCodeloadRequests > 0
                ? maximumAnonymousCodeloadRequests
                : throw new ArgumentOutOfRangeException(
                    nameof(maximumAnonymousCodeloadRequests));
        _innerFactory = innerFactory ??
            throw new ArgumentNullException(nameof(innerFactory));
        _remainingTailGuard = remainingTailGuard ??
            TrustedProofRemainingTailGuard.Measurement;
        _remainingLedger = remainingLedger ?? new TrustedProofPrimaryRemainingLedger();
        _epochSeconds = epochSeconds ??
            (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    // Keep CreateHandler as the ordinary route for the coordinator and older
    // focused tests.  Reviewed-head acquisition must opt into the distinct
    // handler below; a 40-character path segment alone is not semantic
    // evidence of the reviewed head.
    internal HttpMessageHandler CreateHandler() => CreateOtherGitHubHandler();

    internal HttpMessageHandler CreateOtherGitHubHandler() =>
        new BudgetHandler(
            this,
            _innerFactory(),
            TrustedProofRequestDomain.HostOtherGitHubRest);

    internal HttpMessageHandler CreateHeadSourceHandler() =>
        new BudgetHandler(
            this,
            _innerFactory(),
            TrustedProofRequestDomain.HostHeadSourceRest);

    internal bool IsRateLimited => Volatile.Read(ref _rateLimited) != 0 ||
        _remainingLedger.IsClosed;

    internal TrustedProofGitHubRequestBudgetReceipt Snapshot() => new(
        Volatile.Read(ref _authenticatedRestRequests),
        _maximumAuthenticatedRestRequests,
        Volatile.Read(ref _anonymousCodeloadRequests),
        _maximumAnonymousCodeloadRequests,
        Volatile.Read(ref _rejectedRequests));

    internal void WriteReceipt(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var receipt = Snapshot();
        output.WriteLine(
            "APR_R4_E2P_GITHUB_REQUEST_BUDGET " +
            "{\"authenticated_rest_requests\":" +
            receipt.AuthenticatedRestRequests.ToString(
                CultureInfo.InvariantCulture) +
            ",\"authenticated_rest_limit\":" +
            receipt.AuthenticatedRestLimit.ToString(
                CultureInfo.InvariantCulture) +
            ",\"anonymous_codeload_requests\":" +
            receipt.AnonymousCodeloadRequests.ToString(
                CultureInfo.InvariantCulture) +
            ",\"anonymous_codeload_limit\":" +
            receipt.AnonymousCodeloadLimit.ToString(
                CultureInfo.InvariantCulture) +
            ",\"rejected_requests\":" +
            receipt.RejectedRequests.ToString(
                CultureInfo.InvariantCulture) +
            ",\"measurement_only\":" +
            (_remainingTailGuard.MeasurementOnly ? "true" : "false") +
            ",\"invalid_remaining_header\":" +
            (Volatile.Read(ref _invalidRemainingHeader) != 0 ? "true" : "false") +
            ",\"terminal_rate_limited\":" +
            (Volatile.Read(ref _rateLimited) != 0 ? "true" : "false") +
            ",\"low_remaining_guard\":" +
            (Volatile.Read(ref _lowRemainingGuard) != 0 ? "true" : "false") +
            ",\"remaining_tail_reserve\":" + _remainingTailGuard.Reserve.ToString(
                CultureInfo.InvariantCulture) +
            ",\"host_head_source_rest\":{" +
            "\"raw\":" + Volatile.Read(ref _headSourceRaw).ToString(CultureInfo.InvariantCulture) +
            ",\"primary\":" + Volatile.Read(ref _headSourcePrimary).ToString(CultureInfo.InvariantCulture) +
            ",\"not_modified\":" + Volatile.Read(ref _headSourceNotModified).ToString(CultureInfo.InvariantCulture) +
            ",\"secondary_points\":" + Volatile.Read(ref _headSourceSecondaryPoints).ToString(CultureInfo.InvariantCulture) +
            ",\"permission\":" + Volatile.Read(ref _headSourcePermission).ToString(CultureInfo.InvariantCulture) +
            ",\"primary_rate_limited\":" + Volatile.Read(ref _headSourcePrimaryRateLimited).ToString(CultureInfo.InvariantCulture) +
            ",\"secondary_rate_limited\":" + Volatile.Read(ref _headSourceSecondaryRateLimited).ToString(CultureInfo.InvariantCulture) +
            ",\"combined_rate_limited\":" + Volatile.Read(ref _headSourceCombinedRateLimited).ToString(CultureInfo.InvariantCulture) +
            ",\"invalid_rate_headers\":" + Volatile.Read(ref _headSourceInvalidRateHeaders).ToString(CultureInfo.InvariantCulture) +
            ",\"remaining_tail_required\":" + _remainingTailGuard.RequiredTail(
                TrustedProofRequestDomain.HostHeadSourceRest).ToString(
                    CultureInfo.InvariantCulture) + "}" +
            ",\"host_other_github_rest\":{" +
            "\"raw\":" + Volatile.Read(ref _otherGitHubRaw).ToString(CultureInfo.InvariantCulture) +
            ",\"primary\":" + Volatile.Read(ref _otherGitHubPrimary).ToString(CultureInfo.InvariantCulture) +
            ",\"not_modified\":" + Volatile.Read(ref _otherGitHubNotModified).ToString(CultureInfo.InvariantCulture) +
            ",\"secondary_points\":" + Volatile.Read(ref _otherGitHubSecondaryPoints).ToString(CultureInfo.InvariantCulture) +
            ",\"permission\":" + Volatile.Read(ref _otherGitHubPermission).ToString(CultureInfo.InvariantCulture) +
            ",\"primary_rate_limited\":" + Volatile.Read(ref _otherGitHubPrimaryRateLimited).ToString(CultureInfo.InvariantCulture) +
            ",\"secondary_rate_limited\":" + Volatile.Read(ref _otherGitHubSecondaryRateLimited).ToString(CultureInfo.InvariantCulture) +
            ",\"combined_rate_limited\":" + Volatile.Read(ref _otherGitHubCombinedRateLimited).ToString(CultureInfo.InvariantCulture) +
            ",\"invalid_rate_headers\":" + Volatile.Read(ref _otherGitHubInvalidRateHeaders).ToString(CultureInfo.InvariantCulture) +
            ",\"remaining_tail_required\":" + _remainingTailGuard.RequiredTail(
                TrustedProofRequestDomain.HostOtherGitHubRest).ToString(
                    CultureInfo.InvariantCulture) + "}" +
            "}");
    }

    private bool TryClaim(ref int count, int maximum)
    {
        while (true)
        {
            if (Volatile.Read(ref _rateLimited) != 0)
            {
                Interlocked.Increment(ref _rejectedRequests);
                return false;
            }
            var observed = Volatile.Read(ref count);
            if (observed >= maximum)
            {
                Interlocked.Increment(ref _rejectedRequests);
                return false;
            }

            if (Interlocked.CompareExchange(
                    ref count, observed + 1, observed) == observed)
            {
                return true;
            }
        }
    }

    private bool TryClaimAuthenticated(
        TrustedProofRequestDomain domain,
        out TrustedProofPrimaryRemainingLease? lease)
    {
        if (!_remainingLedger.TryLease(domain, _remainingTailGuard, out lease))
        {
            if (_remainingLedger.CloseReason ==
                TrustedProofPrimaryRemainingLedgerCloseReason.LowRemaining)
            {
                Interlocked.Exchange(ref _lowRemainingGuard, 1);
            }
            Interlocked.Exchange(ref _rateLimited, 1);
            Interlocked.Increment(ref _rejectedRequests);
            return false;
        }

        if (TryClaim(ref _authenticatedRestRequests,
                _maximumAuthenticatedRestRequests))
        {
            return true;
        }

        _remainingLedger.AbortBeforeWire(lease!);
        lease = null;
        return false;
    }

    private static SocketsHttpHandler CreateInnerHandler() => new()
    {
        ActivityHeadersPropagator =
            System.Diagnostics.DistributedContextPropagator
                .CreateNoOutputPropagator(),
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        Credentials = null,
        MaxResponseDrainSize = 0,
        PreAuthenticate = false,
        UseCookies = false,
        UseProxy = false,
    };

    private async Task ObserveAsync(
        TrustedProofRequestDomain domain,
        HttpRequestMessage request,
        HttpResponseMessage response,
        TrustedProofPrimaryRemainingLease lease,
        CancellationToken cancellationToken)
    {
        var head = domain == TrustedProofRequestDomain.HostHeadSourceRest;
        if (head) Interlocked.Increment(ref _headSourceRaw);
        else Interlocked.Increment(ref _otherGitHubRaw);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            if (head) Interlocked.Increment(ref _headSourceNotModified);
            else Interlocked.Increment(ref _otherGitHubNotModified);
        }
        else
        {
            if (head) Interlocked.Increment(ref _headSourcePrimary);
            else Interlocked.Increment(ref _otherGitHubPrimary);
        }
        if (head) Interlocked.Add(ref _headSourceSecondaryPoints,
            IsRead(request.Method) ? 1 : 5);
        else Interlocked.Add(ref _otherGitHubSecondaryPoints,
            IsRead(request.Method) ? 1 : 5);

        var responseClass = await TrustedProofOperationRequestAccounting
            .ResponseClassifyAsync(response, cancellationToken, _epochSeconds())
            .ConfigureAwait(false);
        switch (responseClass)
        {
            case TrustedProofResponseClass.PermissionDenied:
                if (head) Interlocked.Increment(ref _headSourcePermission);
                else Interlocked.Increment(ref _otherGitHubPermission);
                break;
            case TrustedProofResponseClass.InvalidRateHeaders:
                if (head) Interlocked.Increment(ref _headSourceInvalidRateHeaders);
                else Interlocked.Increment(ref _otherGitHubInvalidRateHeaders);
                Interlocked.Exchange(ref _invalidRemainingHeader, 1);
                Interlocked.Exchange(ref _rateLimited, 1);
                break;
            case TrustedProofResponseClass.PrimaryRateLimited:
                if (head) Interlocked.Increment(ref _headSourcePrimaryRateLimited);
                else Interlocked.Increment(ref _otherGitHubPrimaryRateLimited);
                Interlocked.Exchange(ref _rateLimited, 1);
                break;
            case TrustedProofResponseClass.SecondaryRateLimited:
                if (head) Interlocked.Increment(ref _headSourceSecondaryRateLimited);
                else Interlocked.Increment(ref _otherGitHubSecondaryRateLimited);
                Interlocked.Exchange(ref _rateLimited, 1);
                break;
            case TrustedProofResponseClass.CombinedRateLimited:
                if (head) Interlocked.Increment(ref _headSourceCombinedRateLimited);
                else Interlocked.Increment(ref _otherGitHubCombinedRateLimited);
                Interlocked.Exchange(ref _rateLimited, 1);
                break;
        }

        _remainingLedger.Observe(lease, response, responseClass, domain,
            _remainingTailGuard);
        if (_remainingLedger.CloseReason ==
            TrustedProofPrimaryRemainingLedgerCloseReason.LowRemaining)
        {
            Interlocked.Exchange(ref _lowRemainingGuard, 1);
            Interlocked.Exchange(ref _rateLimited, 1);
        }
        else if (_remainingLedger.IsClosed)
        {
            Interlocked.Exchange(ref _rateLimited, 1);
        }
    }

    private static bool IsRead(HttpMethod method) => method == HttpMethod.Get ||
        method == HttpMethod.Head || method == HttpMethod.Options;

    private sealed class BudgetHandler(
        TrustedProofGitHubRequestBudget budget,
        HttpMessageHandler inner,
        TrustedProofRequestDomain domain) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri;
            if (uri is null || uri.Scheme != Uri.UriSchemeHttps ||
                uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo))
            {
                return budget.ProtocolRejected(request, HttpStatusCode.BadRequest);
            }

            if (StringComparer.OrdinalIgnoreCase.Equals(
                    uri.Host, "api.github.com"))
            {
                if (request.Headers.Authorization?.Scheme != "Bearer" ||
                    string.IsNullOrEmpty(
                        request.Headers.Authorization.Parameter))
                {
                    return budget.ProtocolRejected(request, HttpStatusCode.Unauthorized);
                }

                if (!budget.TryClaimAuthenticated(domain, out var lease))
                {
                    return budget.RateLimited(request);
                }

                request.Options.Set(
                    TrustedProofOperationRequestAccounting.WitnessDomainOption,
                    TrustedProofOperationRequestAccounting.WitnessDomain(domain));
                HttpResponseMessage response;
                try
                {
                    response = await base.SendAsync(request, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    budget._remainingLedger.CloseOutcomeUnknown(lease!);
                    throw;
                }
                try
                {
                    await budget.ObserveAsync(domain, request, response, lease!,
                        cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    budget._remainingLedger.CloseOutcomeUnknown(lease!);
                    response.Dispose();
                    throw;
                }
                if (!budget.IsRateLimited || budget._remainingLedger.CloseReason ==
                        TrustedProofPrimaryRemainingLedgerCloseReason.LowRemaining)
                {
                    return response;
                }

                response.Dispose();
                return budget.RateLimited(request);
            }

            if (StringComparer.OrdinalIgnoreCase.Equals(
                    uri.Host, "codeload.github.com"))
            {
                if (request.Headers.Authorization is not null)
                {
                    return budget.ProtocolRejected(request, HttpStatusCode.Unauthorized);
                }

                return budget.TryClaim(ref budget._anonymousCodeloadRequests,
                        budget._maximumAnonymousCodeloadRequests)
                    ? await base.SendAsync(request, cancellationToken).ConfigureAwait(false)
                    : budget.RateLimited(request);
            }

            return budget.ProtocolRejected(request, HttpStatusCode.BadRequest);
        }

        internal static HttpResponseMessage Rejected(
            HttpRequestMessage request,
            HttpStatusCode status) => new(status)
            {
                RequestMessage = request,
                Content = new ByteArrayContent([]),
            };

    }

    private HttpResponseMessage RateLimited(
            HttpRequestMessage request)
    {
        var response = BudgetHandler.Rejected(
            request, HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation(
            "x-ratelimit-remaining", "0");
        response.Headers.TryAddWithoutValidation("x-ratelimit-reset",
            checked(_epochSeconds() + 1).ToString(CultureInfo.InvariantCulture));
        return response;
    }

    private HttpResponseMessage ProtocolRejected(
        HttpRequestMessage request,
        HttpStatusCode status)
    {
        Interlocked.Increment(ref _rejectedRequests);
        return BudgetHandler.Rejected(request, status);
    }
}

internal sealed record TrustedProofGitHubRequestBudgetReceipt(
    int AuthenticatedRestRequests,
    int AuthenticatedRestLimit,
    int AnonymousCodeloadRequests,
    int AnonymousCodeloadLimit,
    int RejectedRequests);
