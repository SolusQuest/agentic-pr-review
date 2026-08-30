using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.TrustedProof;

public sealed class TrustedProofGitHubRequestBudgetTests
{
    [Fact]
    public async Task AuthenticatedRestLimitIsSharedAcrossEveryCreatedHandler()
    {
        var sent = 0;
        var budget = new TrustedProofGitHubRequestBudget(
            maximumAuthenticatedRestRequests: 2,
            maximumAnonymousCodeloadRequests: 1,
            () => new RecordingHandler(() => Interlocked.Increment(ref sent)));
        using var first = new HttpClient(budget.CreateHandler());
        using var second = new HttpClient(budget.CreateHandler());

        using var acceptedOne = await first.SendAsync(ApiRequest("/repos/o/r"));
        using var acceptedTwo = await second.SendAsync(ApiRequest("/repos/o/r/pulls/1"));
        using var rejected = await first.SendAsync(ApiRequest("/repos/o/r/issues/1"));

        Assert.Equal(HttpStatusCode.OK, acceptedOne.StatusCode);
        Assert.Equal(HttpStatusCode.OK, acceptedTwo.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("0", Assert.Single(
            rejected.Headers.GetValues("x-ratelimit-remaining")));
        Assert.Equal(2, sent);
        Assert.Equal(
            new TrustedProofGitHubRequestBudgetReceipt(2, 2, 0, 1, 1),
            budget.Snapshot());
    }

    [Fact]
    public async Task CodeloadHasASeparateAnonymousOneRequestBoundary()
    {
        var sent = 0;
        var budget = new TrustedProofGitHubRequestBudget(
            maximumAuthenticatedRestRequests: 1,
            maximumAnonymousCodeloadRequests: 1,
            () => new RecordingHandler(() => Interlocked.Increment(ref sent)));
        using var client = new HttpClient(budget.CreateHandler());

        using var accepted = await client.GetAsync(
            "https://codeload.github.com/o/r/tar.gz/" + new string('a', 40));
        using var rejected = await client.GetAsync(
            "https://codeload.github.com/o/r/tar.gz/" + new string('b', 40));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(1, sent);
        Assert.Equal(
            new TrustedProofGitHubRequestBudgetReceipt(0, 1, 1, 1, 1),
            budget.Snapshot());
    }

    [Theory]
    [InlineData("https://api.github.com/repos/o/r", false)]
    [InlineData("https://codeload.github.com/o/r/tar.gz/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("https://example.test/repos/o/r", true)]
    public async Task CredentialAndOriginViolationsAreNeverSent(
        string uri,
        bool addBearer)
    {
        var sent = 0;
        var budget = new TrustedProofGitHubRequestBudget(
            1,
            1,
            () => new RecordingHandler(() => Interlocked.Increment(ref sent)));
        using var client = new HttpClient(budget.CreateHandler());
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (addBearer)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", "canary");
        }

        using var response = await client.SendAsync(request);

        Assert.Equal(0, sent);
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or
            HttpStatusCode.Unauthorized);
        Assert.Equal(0, budget.Snapshot().AuthenticatedRestRequests);
        Assert.Equal(0, budget.Snapshot().AnonymousCodeloadRequests);
    }

    [Fact]
    public void ReceiptIsStableAndContainsNoCredentialMaterial()
    {
        var budget = new TrustedProofGitHubRequestBudget(
            2, 1, static () => new RecordingHandler(static () => { }));
        using var output = new StringWriter(
            System.Globalization.CultureInfo.InvariantCulture);

        budget.WriteReceipt(output);

        var json = output.ToString()["APR_R4_E2P_GITHUB_REQUEST_BUDGET ".Length..].Trim();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.True(root.GetProperty("measurement_only").GetBoolean());
        Assert.False(root.GetProperty("invalid_remaining_header").GetBoolean());
        Assert.Equal(
        [
            "authenticated_rest_requests", "authenticated_rest_limit",
            "anonymous_codeload_requests", "anonymous_codeload_limit",
            "rejected_requests", "measurement_only", "invalid_remaining_header",
            "terminal_rate_limited", "low_remaining_guard", "remaining_tail_reserve",
            "host_head_source_rest",
            "host_other_github_rest",
        ], root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(0, root.GetProperty("host_head_source_rest").GetProperty("raw").GetInt32());
        Assert.Equal(0, root.GetProperty("host_other_github_rest").GetProperty("primary").GetInt32());
        Assert.Equal(0, root.GetProperty("host_head_source_rest").GetProperty("primary_rate_limited").GetInt32());
        Assert.Equal(0, root.GetProperty("host_other_github_rest").GetProperty("invalid_rate_headers").GetInt32());
    }

    [Fact]
    public void ControlReceiptIsSafeAndReportsTheNestedLimit()
    {
        var budget = new TrustedProofControlRequestBudget(maximumRequests: 2);
        Assert.True(budget.TryClaim());
        budget.MarkRateLimited();
        using var output = new StringWriter(
            System.Globalization.CultureInfo.InvariantCulture);

        budget.WriteReceipt(output);

        var json = output.ToString()["APR_R4_E2P_CONTROL_REQUEST_BUDGET ".Length..].Trim();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("consumed").GetInt32());
        Assert.Equal(2, root.GetProperty("limit").GetInt32());
        Assert.True(root.GetProperty("measurement_only").GetBoolean());
        Assert.True(root.GetProperty("rate_limited").GetBoolean());
        Assert.Equal(
        [
            "consumed", "limit", "primary", "not_modified", "secondary_points",
            "mutation_count", "remaining_tail_required", "remaining_tail_reserve",
            "permission_denied", "primary_rate_limited", "secondary_rate_limited",
            "combined_rate_limited", "invalid_remaining_header",
            "measurement_only", "rate_limited",
        ], root.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [Fact]
    public async Task AuthenticatedHostTrafficIsClassifiedByTheControlledTransportRole()
    {
        var witnessedDomains = new List<string?>();
        var budget = new TrustedProofGitHubRequestBudget(
            7, 1, () => new DomainRecordingHandler(witnessedDomains));
        using var headClient = new HttpClient(budget.CreateHeadSourceHandler());
        using var otherClient = new HttpClient(budget.CreateOtherGitHubHandler());

        using var reviewedCommit = await headClient.SendAsync(ApiRequest("/repos/o/r/git/commits/" + new string('a', 40)));
        using var head = await headClient.SendAsync(ApiRequest("/repos/o/r/git/trees/" + new string('a', 40)));
        using var tarball = await headClient.SendAsync(ApiRequest("/repos/o/r/tarball/" + new string('a', 40)));
        // A source workflow/action object uses the same Git object routes but
        // is not reviewed-head acquisition.  Its controlled ordinary handler
        // keeps it out of the exact-head receipt.
        using var sourceWorkflow = await otherClient.SendAsync(ApiRequest("/repos/o/r/git/trees/" + new string('b', 40)));
        using var sourceAction = await otherClient.SendAsync(ApiRequest("/repos/o/r/git/commits/" + new string('c', 40)));
        using var pulls = await otherClient.SendAsync(ApiRequest("/repos/o/r/commits/" + new string('a', 40) + "/pulls"));
        using var other = await otherClient.SendAsync(ApiRequest("/repos/o/r/issues/1/comments"));
        using var output = new StringWriter();
        budget.WriteReceipt(output);
        using var document = JsonDocument.Parse(output.ToString()[
            "APR_R4_E2P_GITHUB_REQUEST_BUDGET ".Length..].Trim());
        var root = document.RootElement;
        Assert.Equal(3, root.GetProperty("host_head_source_rest").GetProperty("raw").GetInt32());
        Assert.Equal(4, root.GetProperty("host_other_github_rest").GetProperty("raw").GetInt32());
        Assert.Equal(
            [
                "host_head_source_rest", "host_head_source_rest",
                "host_head_source_rest", "host_other_github_rest",
                "host_other_github_rest", "host_other_github_rest",
                "host_other_github_rest",
            ],
            witnessedDomains);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, null, "Permission")]
    [InlineData(HttpStatusCode.Forbidden, "0", "InvalidRemaining")]
    [InlineData(HttpStatusCode.TooManyRequests, null, "InvalidRemaining")]
    [InlineData(HttpStatusCode.OK, "0", "None")]
    [InlineData(HttpStatusCode.OK, "malformed", "InvalidRemaining")]
    public void RateClassificationDoesNotTreatPermissionAsAQuotaLimit(
        HttpStatusCode status,
        string? remaining,
        string expected)
    {
        using var response = new HttpResponseMessage(status);
        if (remaining is not null) response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", remaining);

        Assert.Equal(Enum.Parse<TrustedProofRateClassification>(expected),
            TrustedProofOperationRequestAccounting.RateClassify(response));
    }

    [Fact]
    public void RateClassificationUsesExactPrimarySecondaryAndCombinedSignals()
    {
        using var primary = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        primary.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
        primary.Headers.TryAddWithoutValidation("x-ratelimit-reset", "1900000000");
        Assert.Equal(TrustedProofRateClassification.Primary,
            TrustedProofOperationRequestAccounting.RateClassify(primary));

        using var secondary = new HttpResponseMessage(HttpStatusCode.Forbidden);
        secondary.Headers.TryAddWithoutValidation("retry-after", "1");
        Assert.Equal(TrustedProofRateClassification.Secondary,
            TrustedProofOperationRequestAccounting.RateClassify(secondary));

        using var bodyOnlySecondary = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            ReasonPhrase = "secondary rate limit",
            Content = JsonError("You have exceeded a secondary rate limit."),
        };
        Assert.Equal(TrustedProofRateClassification.Secondary,
            TrustedProofOperationRequestAccounting.RateClassify(bodyOnlySecondary));

        using var bodyOnlySecondary429 = new HttpResponseMessage(
            HttpStatusCode.TooManyRequests)
        {
            Content = JsonError("You have exceeded a secondary rate limit."),
        };
        Assert.Equal(TrustedProofRateClassification.Secondary,
            TrustedProofOperationRequestAccounting.RateClassify(bodyOnlySecondary429));

        using var reasonPhraseOnly = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            ReasonPhrase = "secondary rate limit",
            Content = new ByteArrayContent([]),
        };
        Assert.Equal(TrustedProofRateClassification.Permission,
            TrustedProofOperationRequestAccounting.RateClassify(reasonPhraseOnly));

        using var combined = new HttpResponseMessage(HttpStatusCode.Forbidden);
        combined.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
        combined.Headers.TryAddWithoutValidation("x-ratelimit-reset", "1900000000");
        combined.Headers.TryAddWithoutValidation("retry-after", "1");
        Assert.Equal(TrustedProofRateClassification.Combined,
            TrustedProofOperationRequestAccounting.RateClassify(combined));

        using var bodyCombined = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = JsonError("You have exceeded a secondary rate limit."),
        };
        bodyCombined.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
        bodyCombined.Headers.TryAddWithoutValidation("x-ratelimit-reset", "1900000000");
        Assert.Equal(TrustedProofRateClassification.Combined,
            TrustedProofOperationRequestAccounting.RateClassify(bodyCombined));

        using var ordinarySuccess = new HttpResponseMessage(HttpStatusCode.OK);
        ordinarySuccess.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
        ordinarySuccess.Headers.TryAddWithoutValidation("x-ratelimit-reset", "1900000000");
        Assert.Equal(TrustedProofRateClassification.None,
            TrustedProofOperationRequestAccounting.RateClassify(ordinarySuccess));

        using var successBodySecondary = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonError("You have exceeded a secondary rate limit."),
        };
        Assert.Equal(TrustedProofRateClassification.None,
            TrustedProofOperationRequestAccounting.RateClassify(successBodySecondary));

        using var ordinaryNotModified = new HttpResponseMessage(HttpStatusCode.NotModified);
        ordinaryNotModified.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
        ordinaryNotModified.Headers.TryAddWithoutValidation("x-ratelimit-reset", "1900000000");
        Assert.Equal(TrustedProofResponseClass.NotModified,
            TrustedProofOperationRequestAccounting.ResponseClassify(ordinaryNotModified));

        using var notModifiedBodySecondary = new HttpResponseMessage(HttpStatusCode.NotModified)
        {
            Content = JsonError("You have exceeded a secondary rate limit."),
        };
        Assert.Equal(TrustedProofResponseClass.NotModified,
            TrustedProofOperationRequestAccounting.ResponseClassify(notModifiedBodySecondary));

        using var malformed = new HttpResponseMessage(HttpStatusCode.OK);
        malformed.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "not-a-number");
        Assert.Equal(TrustedProofRateClassification.InvalidRemaining,
            TrustedProofOperationRequestAccounting.RateClassify(malformed));

        using var unrelatedRetryAfter = new HttpResponseMessage(HttpStatusCode.OK);
        unrelatedRetryAfter.Headers.TryAddWithoutValidation("retry-after", "1");
        Assert.Equal(TrustedProofRateClassification.InvalidRemaining,
            TrustedProofOperationRequestAccounting.RateClassify(unrelatedRetryAfter));

        using var malformedRetryAfter = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        malformedRetryAfter.Headers.TryAddWithoutValidation("retry-after", "not-a-delay");
        Assert.Equal(TrustedProofRateClassification.InvalidRemaining,
            TrustedProofOperationRequestAccounting.RateClassify(malformedRetryAfter));

        using var duplicateReset = new HttpResponseMessage(HttpStatusCode.Forbidden);
        duplicateReset.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
        duplicateReset.Headers.TryAddWithoutValidation("x-ratelimit-reset",
            new[] { "1900000000", "1900000001" });
        Assert.Equal(TrustedProofRateClassification.InvalidRemaining,
            TrustedProofOperationRequestAccounting.RateClassify(duplicateReset));

        using var overflowReset = new HttpResponseMessage(HttpStatusCode.Forbidden);
        overflowReset.Headers.TryAddWithoutValidation("x-ratelimit-reset",
            "999999999999999999999999999999999");
        Assert.Equal(TrustedProofRateClassification.InvalidRemaining,
            TrustedProofOperationRequestAccounting.RateClassify(overflowReset));

        using var pastReset = new HttpResponseMessage(HttpStatusCode.Forbidden);
        pastReset.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
        pastReset.Headers.TryAddWithoutValidation("x-ratelimit-reset", "1");
        Assert.Equal(TrustedProofRateClassification.InvalidRemaining,
            TrustedProofOperationRequestAccounting.RateClassify(pastReset));

        using var resetWithoutRemaining = new HttpResponseMessage(HttpStatusCode.OK);
        resetWithoutRemaining.Headers.TryAddWithoutValidation(
            "x-ratelimit-reset", "1900000000");
        Assert.Equal(TrustedProofRateClassification.InvalidRemaining,
            TrustedProofOperationRequestAccounting.RateClassify(
                resetWithoutRemaining));

        using var malformed304 = new HttpResponseMessage(HttpStatusCode.NotModified);
        malformed304.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "broken");
        Assert.Equal(TrustedProofResponseClass.InvalidRateHeaders,
            TrustedProofOperationRequestAccounting.ResponseClassify(malformed304));

        using var contradictory304 = new HttpResponseMessage(HttpStatusCode.NotModified);
        contradictory304.Headers.TryAddWithoutValidation("retry-after", "1");
        Assert.Equal(TrustedProofResponseClass.InvalidRateHeaders,
            TrustedProofOperationRequestAccounting.ResponseClassify(contradictory304));

        using var oversizedMessage = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = JsonError(new string('x', 513)),
        };
        Assert.Equal(TrustedProofRateClassification.InvalidRemaining,
            TrustedProofOperationRequestAccounting.RateClassify(oversizedMessage));
    }

    [Fact]
    public void RateClassificationRejectsContradictoryLimitAndUsesTheExactMessagePredicate()
    {
        using var contradictoryLimit = new HttpResponseMessage(HttpStatusCode.OK);
        contradictoryLimit.Headers.TryAddWithoutValidation("x-ratelimit-limit", "2");
        contradictoryLimit.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "3");
        Assert.Equal(TrustedProofRateClassification.InvalidRemaining,
            TrustedProofOperationRequestAccounting.RateClassify(contradictoryLimit));

        foreach (var (message, expected) in new[]
        {
            ("", TrustedProofRateClassification.InvalidRemaining),
            ("secondary rate\0limit", TrustedProofRateClassification.InvalidRemaining),
            ("secondary rate limit", TrustedProofRateClassification.Secondary),
            ("secondary rate limited", TrustedProofRateClassification.Secondary),
            ("secondary rate limits", TrustedProofRateClassification.Secondary),
            ("secondary rate limit " + new string('x', 491),
                TrustedProofRateClassification.Secondary),
            ("secondary rate limit " + new string('x', 492),
                TrustedProofRateClassification.InvalidRemaining),
            ("ésecondary rate limit", TrustedProofRateClassification.Secondary),
            ("secondary rate limité", TrustedProofRateClassification.Secondary),
            ("xsecondary rate limit", TrustedProofRateClassification.Permission),
            ("secondary rate limitx", TrustedProofRateClassification.Permission),
        })
        {
            using var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = JsonError(message),
            };
            Assert.Equal(expected,
                TrustedProofOperationRequestAccounting.RateClassify(response));
        }
    }

    [Fact]
    public async Task UnknownLengthOversizedRateLimitBodyIsNotConsumedOrTruncated()
    {
        var body = Encoding.UTF8.GetBytes("{\"message\":\"" +
            new string('x', 4 * 1024) + "\"}");
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new UnknownLengthContent(body),
        };

        Assert.Null(response.Content.Headers.ContentLength);
        Assert.Equal(TrustedProofRateClassification.Permission,
            TrustedProofOperationRequestAccounting.RateClassify(response));
        Assert.Equal(body, await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{\"message\":{}}")]
    [InlineData("{")]
    public void RateClassificationFailsClosedForMalformedErrorPayloads(string body)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        Assert.Equal(TrustedProofRateClassification.InvalidRemaining,
            TrustedProofOperationRequestAccounting.RateClassify(response));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{\"message\":{}}")]
    public async Task KnownLengthMalformed403BodyIsPreservedByteForByteAfterClassification(
        string body)
    {
        var original = Encoding.UTF8.GetBytes(body);
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new ByteArrayContent(original),
        };
        response.Content.Headers.ContentType = new("application/json");

        Assert.Equal(TrustedProofRateClassification.InvalidRemaining,
            TrustedProofOperationRequestAccounting.RateClassify(response));
        Assert.Equal(original, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public void InjectedEpochIsUsedAtTheExactResetBoundary()
    {
        const long now = 1_900_000_000;
        foreach (var (delta, expected) in new[]
        {
            (-1L, TrustedProofRateClassification.InvalidRemaining),
            (0L, TrustedProofRateClassification.InvalidRemaining),
            (1L, TrustedProofRateClassification.Primary),
        })
        {
            using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
            response.Headers.TryAddWithoutValidation("x-ratelimit-reset",
                (now + delta).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(expected,
                TrustedProofOperationRequestAccounting.RateClassify(response, now));
        }

        var control = new TrustedProofControlRequestBudget(
            epochSeconds: () => now);
        using var atBoundary = new HttpResponseMessage(HttpStatusCode.Forbidden);
        atBoundary.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
        atBoundary.Headers.TryAddWithoutValidation("x-ratelimit-reset", now.ToString());
        control.Observe(atBoundary, HttpMethod.Get);
        using var receipt = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        control.WriteReceipt(receipt);
        using var document = JsonDocument.Parse(receipt.ToString()[
            "APR_R4_E2P_CONTROL_REQUEST_BUDGET ".Length..]);
        Assert.True(document.RootElement.GetProperty("invalid_remaining_header").GetBoolean());
    }

    [Fact]
    public async Task GitHubBudgetPassesItsInjectedEpochToTheResponseConsumer()
    {
        const long now = 1_900_000_000;
        var budget = new TrustedProofGitHubRequestBudget(
            maximumAuthenticatedRestRequests: 2,
            maximumAnonymousCodeloadRequests: 1,
            innerFactory: () => new ResponseHandler(() =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
                response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
                response.Headers.TryAddWithoutValidation("x-ratelimit-reset", now.ToString());
                return response;
            }),
            epochSeconds: () => now);
        using var client = new HttpClient(budget.CreateHandler());
        using var response = await client.SendAsync(ApiRequest("/repos/o/r"));
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);

        budget.WriteReceipt(output);

        using var document = JsonDocument.Parse(output.ToString()[
            "APR_R4_E2P_GITHUB_REQUEST_BUDGET ".Length..]);
        Assert.True(document.RootElement.GetProperty("invalid_remaining_header").GetBoolean());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NotModified)]
    public async Task MalformedSuccessOr304ResponseIsReplacedByTerminalFailure(
        HttpStatusCode status)
    {
        var budget = new TrustedProofGitHubRequestBudget(
            maximumAuthenticatedRestRequests: 2,
            maximumAnonymousCodeloadRequests: 1,
            innerFactory: () => new ResponseHandler(() =>
            {
                var response = new HttpResponseMessage(status);
                response.Headers.TryAddWithoutValidation(
                    "x-ratelimit-remaining", "malformed");
                return response;
            }));
        using var client = new HttpClient(budget.CreateHandler());
        using var response = await client.SendAsync(ApiRequest("/repos/o/r"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(budget.IsRateLimited);
    }

    [Fact]
    public void MalformedRemainingHeaderClosesControlBudgetButPermissionDoesNot()
    {
        var permission = new TrustedProofControlRequestBudget(2);
        using var denied = new HttpResponseMessage(HttpStatusCode.Forbidden);
        permission.Observe(denied, HttpMethod.Get);
        Assert.False(permission.IsRateLimited);
        Assert.True(permission.TryClaim());

        var malformed = new TrustedProofControlRequestBudget(2);
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "not-a-number");
        malformed.Observe(response, HttpMethod.Get);
        Assert.True(malformed.IsRateLimited);
        Assert.False(malformed.TryClaim());

        var lowRemaining = new TrustedProofControlRequestBudget(2);
        using var success = new HttpResponseMessage(HttpStatusCode.OK);
        success.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "1");
        lowRemaining.Observe(success, HttpMethod.Get);
        Assert.False(lowRemaining.TryClaim());
    }

    [Fact]
    public void RemainingGuardUsesTheCrossRoleTailAndReserveNotOnlyTheNextRequest()
    {
        var tails = new Dictionary<TrustedProofRequestDomain, int>
        {
            [TrustedProofRequestDomain.NodeArtifactRest] = 3,
            [TrustedProofRequestDomain.HostHeadSourceRest] = 2,
            [TrustedProofRequestDomain.HostOtherGitHubRest] = 1,
            [TrustedProofRequestDomain.TrustedControlRest] = 4,
        };
        var guard = new TrustedProofRemainingTailGuard(tails, reserve: 5,
            measurementOnly: false);
        using var atTail = new HttpResponseMessage(HttpStatusCode.OK);
        atTail.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "7");
        using var aboveTail = new HttpResponseMessage(HttpStatusCode.OK);
        aboveTail.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "8");

        Assert.True(TrustedProofOperationRequestAccounting.RemainingRequiresFailClosed(
            atTail, guard, TrustedProofRequestDomain.HostHeadSourceRest));
        Assert.False(TrustedProofOperationRequestAccounting.RemainingRequiresFailClosed(
            aboveTail, guard, TrustedProofRequestDomain.HostHeadSourceRest));
    }

    [Fact]
    public void ProductionProfileCannotSilentlyUseMeasurementBeforeTheFreeze()
    {
        Assert.True(TrustedProofRequestBudgetProfile.TrySelectProduction(
            name => name == "AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE"
                ? "measurement" : null,
            out var measurement));
        Assert.True(measurement!.MeasurementOnly);

        Assert.False(TrustedProofRequestBudgetProfile.TrySelectProduction(
            name => name == "AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE"
                ? "final" : null,
            out var final));
        Assert.Null(final);
    }

    private static HttpRequestMessage ApiRequest(string path)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get, "https://api.github.com" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", "canary");
        return request;
    }

    private static StringContent JsonError(string message) => new(
        "{\"message\":\"" + message + "\"}", Encoding.UTF8,
        "application/json");

    private sealed class RecordingHandler(System.Action onSend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onSend();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent([]),
            });
        }
    }

    private sealed class DomainRecordingHandler(List<string?> domains) :
        HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.Options.TryGetValue(
                TrustedProofOperationRequestAccounting.WitnessDomainOption,
                out string? domain);
            domains.Add(domain);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class UnknownLengthContent(byte[] body) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream,
            TransportContext? context) => stream.WriteAsync(body).AsTask();
    }

    private sealed class ResponseHandler(Func<HttpResponseMessage> create) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = create();
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
