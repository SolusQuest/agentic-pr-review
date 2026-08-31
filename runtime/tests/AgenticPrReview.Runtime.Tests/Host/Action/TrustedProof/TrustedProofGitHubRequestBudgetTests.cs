using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
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
        Assert.True(budget.TryClaim(out var lease));
        lease!.Ledger.AbortBeforeWire(lease);
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

    [Fact]
    public async Task ReviewedHeadSourceCapabilityCoversTheWholeFrozenHeadGraph()
    {
        var witnessedDomains = new List<string?>();
        var budget = new TrustedProofGitHubRequestBudget(
            maximumAuthenticatedRestRequests: 200,
            maximumAnonymousCodeloadRequests: 1,
            innerFactory: () => new DomainRecordingHandler(witnessedDomains));
        var ordinary = new ActionHostGitHubAuthorizationTransportFactory(
            budget.CreateOtherGitHubHandler);
        var reviewedHead = new ActionHostGitHubAuthorizationTransportFactory(
            budget.CreateHeadSourceHandler);
        Assert.True(ActionHostGitHubToken.TryCreate("token-canary", out var token));
        const string sha = "0123456789abcdef0123456789abcdef01234567";

        using (var transport = reviewedHead.CreateExactObjectTransport(token!))
        {
            _ = await transport.GetCommitObjectAsync("o/r", sha,
                CancellationToken.None);
            for (var index = 0; index < 178; index++)
            {
                _ = await transport.GetTreeObjectAsync("o/r", sha,
                    CancellationToken.None);
            }

            _ = await transport.GetHeadArchiveAsync("o/r", sha,
                CancellationToken.None);
        }

        // Exact-object routes alone do not define the proof domain: policy and
        // the base-diff snapshot reader use the ordinary capability.
        using (var policy = ordinary.CreateExactObjectTransport(token!))
        {
            _ = await policy.GetTreeObjectAsync("o/r", sha,
                CancellationToken.None);
        }

        using (var snapshot = ordinary.CreateReviewedSnapshotTransport(token!))
        {
            _ = await snapshot.GetCurrentPullRequestAsync("o/r", 1,
                CancellationToken.None);
            _ = await snapshot.GetCommitObjectAsync("o/r", sha,
                CancellationToken.None);
        }

        Assert.Equal(183, budget.Snapshot().AuthenticatedRestRequests);
        using var output = new StringWriter();
        budget.WriteReceipt(output);
        using var receipt = JsonDocument.Parse(output.ToString()[
            "APR_R4_E2P_GITHUB_REQUEST_BUDGET ".Length..].Trim());
        Assert.Equal(180, receipt.RootElement
            .GetProperty("host_head_source_rest").GetProperty("raw").GetInt32());
        Assert.Equal(3, receipt.RootElement
            .GetProperty("host_other_github_rest").GetProperty("raw").GetInt32());
        Assert.Equal(
            Enumerable.Repeat("host_head_source_rest", 180)
                .Concat(Enumerable.Repeat("host_other_github_rest", 3)),
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
            RateClassify(response));
    }

    [Fact]
    public void RateClassificationUsesExactPrimarySecondaryAndCombinedSignals()
    {
        using var primary = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        primary.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
        primary.Headers.TryAddWithoutValidation("x-ratelimit-reset", "1900000000");
        Assert.Equal(TrustedProofRateClassification.Primary,
            RateClassify(primary));

        using var secondary = new HttpResponseMessage(HttpStatusCode.Forbidden);
        secondary.Headers.TryAddWithoutValidation("retry-after", "1");
        Assert.Equal(TrustedProofRateClassification.Secondary,
            RateClassify(secondary));

        using var bodyOnlySecondary = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            ReasonPhrase = "secondary rate limit",
            Content = JsonError("You have exceeded a secondary rate limit."),
        };
        Assert.Equal(TrustedProofRateClassification.Secondary,
            RateClassify(bodyOnlySecondary));

        using var bodyOnlySecondary429 = new HttpResponseMessage(
            HttpStatusCode.TooManyRequests)
        {
            Content = JsonError("You have exceeded a secondary rate limit."),
        };
        Assert.Equal(TrustedProofRateClassification.Secondary,
            RateClassify(bodyOnlySecondary429));

        using var reasonPhraseOnly = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            ReasonPhrase = "secondary rate limit",
            Content = new ByteArrayContent([]),
        };
        Assert.Equal(TrustedProofRateClassification.Permission,
            RateClassify(reasonPhraseOnly));

        using var combined = new HttpResponseMessage(HttpStatusCode.Forbidden);
        combined.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
        combined.Headers.TryAddWithoutValidation("x-ratelimit-reset", "1900000000");
        combined.Headers.TryAddWithoutValidation("retry-after", "1");
        Assert.Equal(TrustedProofRateClassification.Combined,
            RateClassify(combined));

        using var bodyCombined = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = JsonError("You have exceeded a secondary rate limit."),
        };
        bodyCombined.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
        bodyCombined.Headers.TryAddWithoutValidation("x-ratelimit-reset", "1900000000");
        Assert.Equal(TrustedProofRateClassification.Combined,
            RateClassify(bodyCombined));

        using var ordinarySuccess = new HttpResponseMessage(HttpStatusCode.OK);
        ordinarySuccess.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
        ordinarySuccess.Headers.TryAddWithoutValidation("x-ratelimit-reset", "1900000000");
        Assert.Equal(TrustedProofRateClassification.None,
            RateClassify(ordinarySuccess));

        using var successBodySecondary = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonError("You have exceeded a secondary rate limit."),
        };
        Assert.Equal(TrustedProofRateClassification.None,
            RateClassify(successBodySecondary));

        using var ordinaryNotModified = new HttpResponseMessage(HttpStatusCode.NotModified);
        ordinaryNotModified.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
        ordinaryNotModified.Headers.TryAddWithoutValidation("x-ratelimit-reset", "1900000000");
        Assert.Equal(TrustedProofResponseClass.NotModified,
            ResponseClassify(ordinaryNotModified));

        using var notModifiedBodySecondary = new HttpResponseMessage(HttpStatusCode.NotModified)
        {
            Content = JsonError("You have exceeded a secondary rate limit."),
        };
        Assert.Equal(TrustedProofResponseClass.NotModified,
            ResponseClassify(notModifiedBodySecondary));

        using var malformed = new HttpResponseMessage(HttpStatusCode.OK);
        malformed.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "not-a-number");
        Assert.Equal(TrustedProofRateClassification.InvalidRemaining,
            RateClassify(malformed));

        using var unrelatedRetryAfter = new HttpResponseMessage(HttpStatusCode.OK);
        unrelatedRetryAfter.Headers.TryAddWithoutValidation("retry-after", "1");
        Assert.Equal(TrustedProofRateClassification.InvalidRemaining,
            RateClassify(unrelatedRetryAfter));

        using var malformedRetryAfter = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        malformedRetryAfter.Headers.TryAddWithoutValidation("retry-after", "not-a-delay");
        Assert.Equal(TrustedProofRateClassification.InvalidRemaining,
            RateClassify(malformedRetryAfter));

        using var duplicateReset = new HttpResponseMessage(HttpStatusCode.Forbidden);
        duplicateReset.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
        duplicateReset.Headers.TryAddWithoutValidation("x-ratelimit-reset",
            new[] { "1900000000", "1900000001" });
        Assert.Equal(TrustedProofRateClassification.InvalidRemaining,
            RateClassify(duplicateReset));

        using var overflowReset = new HttpResponseMessage(HttpStatusCode.Forbidden);
        overflowReset.Headers.TryAddWithoutValidation("x-ratelimit-reset",
            "999999999999999999999999999999999");
        Assert.Equal(TrustedProofRateClassification.InvalidRemaining,
            RateClassify(overflowReset));

        using var pastReset = new HttpResponseMessage(HttpStatusCode.Forbidden);
        pastReset.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
        pastReset.Headers.TryAddWithoutValidation("x-ratelimit-reset", "1");
        Assert.Equal(TrustedProofRateClassification.InvalidRemaining,
            RateClassify(pastReset));

        using var resetWithoutRemaining = new HttpResponseMessage(HttpStatusCode.OK);
        resetWithoutRemaining.Headers.TryAddWithoutValidation(
            "x-ratelimit-reset", "1900000000");
        Assert.Equal(TrustedProofRateClassification.InvalidRemaining,
            RateClassify(
                resetWithoutRemaining));

        using var malformed304 = new HttpResponseMessage(HttpStatusCode.NotModified);
        malformed304.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "broken");
        Assert.Equal(TrustedProofResponseClass.InvalidRateHeaders,
            ResponseClassify(malformed304));

        using var contradictory304 = new HttpResponseMessage(HttpStatusCode.NotModified);
        contradictory304.Headers.TryAddWithoutValidation("retry-after", "1");
        Assert.Equal(TrustedProofResponseClass.InvalidRateHeaders,
            ResponseClassify(contradictory304));

        using var oversizedMessage = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = JsonError(new string('x', 513)),
        };
        Assert.Equal(TrustedProofRateClassification.InvalidRemaining,
            RateClassify(oversizedMessage));
    }

    [Fact]
    public void RateClassificationRejectsContradictoryLimitAndUsesTheExactMessagePredicate()
    {
        using var contradictoryLimit = new HttpResponseMessage(HttpStatusCode.OK);
        contradictoryLimit.Headers.TryAddWithoutValidation("x-ratelimit-limit", "2");
        contradictoryLimit.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "3");
        Assert.Equal(TrustedProofRateClassification.InvalidRemaining,
            RateClassify(contradictoryLimit));

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
                RateClassify(response));
        }
    }

    [Fact]
    public async Task UnknownLengthOversizedRateLimitBodyIsReadOnlyToTheBoundAndRejected()
    {
        var body = Encoding.UTF8.GetBytes("{\"message\":\"" +
            new string('x', 4 * 1024) + "\"}");
        var stream = new NonSeekableCountingReadStream(body);
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StreamContent(stream),
        };

        Assert.Null(response.Content.Headers.ContentLength);
        Assert.Equal(ActionHostGitHubRateLimitClassification.Invalid,
            await ActionHostGitHubRateLimitClassifier.ClassifyAsync(
                response, CancellationToken.None));
        Assert.Equal(ActionHostGitHubRateLimitClassifier.MaximumErrorBodyBytes + 1,
            stream.BytesRead);
    }

    [Fact]
    public async Task UnknownLengthBoundedRateLimitBodyIsClassifiedAndPreserved()
    {
        var body = Encoding.UTF8.GetBytes(
            "{\"message\":\"You have exceeded a secondary rate limit.\"}");
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new UnknownLengthContent(body),
        };

        Assert.Equal(ActionHostGitHubRateLimitClassification.Secondary,
            await ActionHostGitHubRateLimitClassifier.ClassifyAsync(
                response, CancellationToken.None));
        Assert.Equal(body, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task DeclaredLengthTruncationIsRejectedWithoutReplacingContent()
    {
        var body = Encoding.UTF8.GetBytes("{\"message\":\"short\"}");
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new DeclaredLengthContent(body, body.Length + 1),
        };

        var original = response.Content;
        Assert.Equal(ActionHostGitHubRateLimitClassification.Invalid,
            await ActionHostGitHubRateLimitClassifier.ClassifyAsync(
                response, CancellationToken.None));
        Assert.Same(original, response.Content);
    }

    [Fact]
    public async Task RateLimitBodyReadHonorsOperationCancellation()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StreamContent(new CancellationAwareBlockingStream()),
        };
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ActionHostGitHubRateLimitClassifier.ClassifyAsync(
                response, cancellation.Token));
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
            RateClassify(response));
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
            RateClassify(response));
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
                RateClassify(response, now));
        }

        var control = new TrustedProofControlRequestBudget(
            epochSeconds: () => now);
        using var atBoundary = new HttpResponseMessage(HttpStatusCode.Forbidden);
        atBoundary.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
        atBoundary.Headers.TryAddWithoutValidation("x-ratelimit-reset", now.ToString());
        Observe(control, atBoundary, HttpMethod.Get);
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
        Observe(permission, denied, HttpMethod.Get);
        Assert.False(permission.IsRateLimited);
        Assert.True(permission.TryClaim(out var permissionLease));
        permissionLease!.Ledger.AbortBeforeWire(permissionLease);

        var malformed = new TrustedProofControlRequestBudget(2);
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "not-a-number");
        Observe(malformed, response, HttpMethod.Get);
        Assert.True(malformed.IsRateLimited);
        Assert.False(malformed.TryClaim(out _));

        var lowRemaining = new TrustedProofControlRequestBudget(2);
        using var success = new HttpResponseMessage(HttpStatusCode.OK);
        success.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "1");
        Observe(lowRemaining, success, HttpMethod.Get);
        Assert.False(lowRemaining.TryClaim(out _));

        var oneRequestAbove = new TrustedProofControlRequestBudget(2);
        using var sufficient = new HttpResponseMessage(HttpStatusCode.OK);
        sufficient.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "2");
        Observe(oneRequestAbove, sufficient, HttpMethod.Get);
        Assert.True(oneRequestAbove.TryClaim(out var sufficientLease));
        sufficientLease!.Ledger.AbortBeforeWire(sufficientLease);
    }

    [Fact]
    public async Task ExactRemainingTailIsAcceptedButTheNextHostDispatchIsRejectedBeforeWire()
    {
        var sent = 0;
        var budget = new TrustedProofGitHubRequestBudget(
            maximumAuthenticatedRestRequests: 2,
            maximumAnonymousCodeloadRequests: 1,
            innerFactory: () => new ResponseHandler(() =>
            {
                Interlocked.Increment(ref sent);
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.TryAddWithoutValidation(
                    "x-ratelimit-remaining", "1");
                return response;
            }));
        using var client = new HttpClient(budget.CreateOtherGitHubHandler());

        using var accepted = await client.SendAsync(ApiRequest("/repos/o/r"));
        using var rejected = await client.SendAsync(ApiRequest("/repos/o/r/issues/1"));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(1, sent);
        Assert.Equal(1, budget.Snapshot().AuthenticatedRestRequests);
        Assert.Equal(1, budget.Snapshot().RejectedRequests);
    }

    [Fact]
    public async Task OneRequestAboveRemainingTailAllowsTheNextHostDispatch()
    {
        var sent = 0;
        var budget = new TrustedProofGitHubRequestBudget(
            maximumAuthenticatedRestRequests: 2,
            maximumAnonymousCodeloadRequests: 1,
            innerFactory: () => new ResponseHandler(() =>
            {
                var remaining = Interlocked.Increment(ref sent) == 1 ? "2" : "1";
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.TryAddWithoutValidation(
                    "x-ratelimit-remaining", remaining);
                return response;
            }));
        using var client = new HttpClient(budget.CreateOtherGitHubHandler());

        using var first = await client.SendAsync(ApiRequest("/repos/o/r"));
        using var second = await client.SendAsync(ApiRequest("/repos/o/r/issues/1"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, sent);
        Assert.Equal(2, budget.Snapshot().AuthenticatedRestRequests);
        Assert.Equal(0, budget.Snapshot().RejectedRequests);
    }

    [Fact]
    public async Task FrozenTailAdvancesAcrossADecreasingPrimaryBucket()
    {
        var sent = 0;
        var remaining = 5;
        var guard = Guard(reserve: 1, otherTail: 3);
        var budget = new TrustedProofGitHubRequestBudget(4, 1,
            () => new ResponseHandler(() => RemainingResponse(
                Interlocked.Decrement(ref remaining).ToString(
                    System.Globalization.CultureInfo.InvariantCulture))),
            guard);
        using var client = new HttpClient(budget.CreateOtherGitHubHandler());

        for (var index = 0; index < 4; index++)
        {
            using var response = await client.SendAsync(
                ApiRequest($"/repos/o/r/issues/{index + 1}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            sent++;
        }

        Assert.Equal(4, sent);
        Assert.False(budget.IsRateLimited);
        Assert.Equal(0, budget.Snapshot().RejectedRequests);
    }

    [Fact]
    public async Task NotModifiedDoesNotAdvanceTheProtectedPrimaryTail()
    {
        var sent = 0;
        var guard = Guard(reserve: 1, otherTail: 2);
        var budget = new TrustedProofGitHubRequestBudget(4, 1,
            () => new ResponseHandler(() => Interlocked.Increment(ref sent) switch
            {
                1 => RemainingResponse("3"),
                2 => RemainingResponse("3", HttpStatusCode.NotModified),
                3 => RemainingResponse("2"),
                _ => RemainingResponse("1"),
            }), guard);
        using var client = new HttpClient(budget.CreateOtherGitHubHandler());

        using var first = await client.SendAsync(ApiRequest("/repos/o/r"));
        using var notModified = await client.SendAsync(ApiRequest("/repos/o/r/issues/1"));
        using var third = await client.SendAsync(ApiRequest("/repos/o/r/issues/2"));
        using var fourth = await client.SendAsync(ApiRequest("/repos/o/r/issues/3"));
        using var rejected = await client.SendAsync(ApiRequest("/repos/o/r/issues/4"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fourth.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(4, sent);
    }

    [Fact]
    public async Task LowRemainingClosesOnlyFutureDispatchAndPreservesCurrentSuccess()
    {
        var sent = 0;
        var guard = Guard(reserve: 1, otherTail: 3);
        var budget = new TrustedProofGitHubRequestBudget(2, 1,
            () => new ResponseHandler(() =>
            {
                Interlocked.Increment(ref sent);
                return RemainingResponse("3");
            }), guard);
        using var client = new HttpClient(budget.CreateOtherGitHubHandler());

        using var current = await client.SendAsync(ApiRequest("/repos/o/r"));
        using var next = await client.SendAsync(ApiRequest("/repos/o/r/issues/1"));

        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, next.StatusCode);
        Assert.Equal(1, sent);
        Assert.True(budget.IsRateLimited);
    }

    [Fact]
    public void RemainingGuardAllowsExactTailAndReserveButRejectsOneRequestBelow()
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
        using var belowTail = new HttpResponseMessage(HttpStatusCode.OK);
        belowTail.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "6");
        using var atTail = new HttpResponseMessage(HttpStatusCode.OK);
        atTail.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "7");

        Assert.True(TrustedProofOperationRequestAccounting.RemainingRequiresFailClosed(
            belowTail, guard, TrustedProofRequestDomain.HostHeadSourceRest));
        Assert.False(TrustedProofOperationRequestAccounting.RemainingRequiresFailClosed(
            atTail, guard, TrustedProofRequestDomain.HostHeadSourceRest));
    }

    [Fact]
    public async Task ExactRemainingEqualityIsSharedAcrossHostAndControlBeforeTheNextWire()
    {
        var guard = Guard(reserve: 1);
        var ledger = new TrustedProofPrimaryRemainingLedger();
        var host = new TrustedProofGitHubRequestBudget(3, 1,
            () => new ResponseHandler(() => RemainingResponse("2")), guard,
            remainingLedger: ledger);
        var control = new TrustedProofControlRequestBudget(3, guard,
            remainingLedger: ledger);
        using var client = new HttpClient(host.CreateOtherGitHubHandler());

        using var first = await client.SendAsync(ApiRequest("/repos/o/r"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(control.TryClaim(out var controlLease));
        using var controlResponse = RemainingResponse("1");
        Observe(control, controlResponse, HttpMethod.Get, controlLease);

        using var rejected = await client.SendAsync(ApiRequest("/repos/o/r/issues/1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(host.IsRateLimited);
        Assert.True(control.IsRateLimited);
    }

    [Fact]
    public async Task HeadOtherAndEmbeddedControlShareDynamicLowRemaining()
    {
        var guard = Guard(reserve: 1);
        var ledger = new TrustedProofPrimaryRemainingLedger();
        var remaining = 4;
        var host = new TrustedProofGitHubRequestBudget(4, 1,
            () => new ResponseHandler(() => RemainingResponse(
                Interlocked.Decrement(ref remaining).ToString(
                    System.Globalization.CultureInfo.InvariantCulture))),
            guard, remainingLedger: ledger);
        var control = new TrustedProofControlRequestBudget(4, guard,
            remainingLedger: ledger);
        using var headClient = new HttpClient(host.CreateHeadSourceHandler());
        using var otherClient = new HttpClient(host.CreateOtherGitHubHandler());

        using var head = await headClient.SendAsync(ApiRequest(
            "/repos/o/r/git/commits/" + new string('a', 40)));
        using var other = await otherClient.SendAsync(ApiRequest("/repos/o/r"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.True(control.TryClaim(out var controlLease));
        using var controlResponse = RemainingResponse("1");
        Observe(control, controlResponse, HttpMethod.Get, controlLease);

        using var rejected = await headClient.SendAsync(ApiRequest(
            "/repos/o/r/git/trees/" + new string('a', 40)));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(host.IsRateLimited);
        Assert.True(control.IsRateLimited);
    }

    [Fact]
    public async Task StandaloneControlTerminalDoesNotCloseAnIndependentHostLedger()
    {
        var guard = Guard(reserve: 1);
        var host = new TrustedProofGitHubRequestBudget(2, 1,
            () => new ResponseHandler(() => RemainingResponse("2")), guard);
        var standaloneControl = new TrustedProofControlRequestBudget(2, guard);
        using var client = new HttpClient(host.CreateOtherGitHubHandler());

        standaloneControl.MarkRateLimited();
        using var response = await client.SendAsync(ApiRequest("/repos/o/r"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(standaloneControl.IsRateLimited);
        Assert.False(host.IsRateLimited);
    }

    [Fact]
    public void Headerless304RefundsOnlyItsLeaseWhileOrdinaryHeaderlessResponseStaysDebited()
    {
        var guard = Guard();
        var normalLedger = new TrustedProofPrimaryRemainingLedger();
        using (var initial = RemainingResponse("1"))
        {
            normalLedger.Observe(null, initial, TrustedProofResponseClass.Success,
                TrustedProofRequestDomain.HostOtherGitHubRest, guard);
        }
        Assert.True(normalLedger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out var normalLease));
        using (var ordinary = new HttpResponseMessage(HttpStatusCode.OK))
        {
            normalLedger.Observe(normalLease, ordinary,
                TrustedProofResponseClass.Success,
                TrustedProofRequestDomain.HostOtherGitHubRest, guard);
        }
        Assert.False(normalLedger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out _));

        var notModifiedLedger = new TrustedProofPrimaryRemainingLedger();
        using (var initial = RemainingResponse("1"))
        {
            notModifiedLedger.Observe(null, initial,
                TrustedProofResponseClass.Success,
                TrustedProofRequestDomain.HostOtherGitHubRest, guard);
        }
        Assert.True(notModifiedLedger.TryLease(
            TrustedProofRequestDomain.HostOtherGitHubRest, guard,
            out var notModifiedLease));
        using (var notModified = new HttpResponseMessage(HttpStatusCode.NotModified))
        {
            notModifiedLedger.Observe(notModifiedLease, notModified,
                TrustedProofResponseClass.NotModified,
                TrustedProofRequestDomain.HostOtherGitHubRest, guard);
        }
        Assert.True(notModifiedLedger.TryLease(
            TrustedProofRequestDomain.HostOtherGitHubRest, guard, out _));
    }

    [Fact]
    public void NotModifiedWithRemainingHeaderRefundsItsKnownPreDebitBeforeWaveMinimum()
    {
        var guard = Guard();
        var ledger = new TrustedProofPrimaryRemainingLedger();
        using (var initial = RemainingResponse("1"))
        {
            ledger.Observe(null, initial, TrustedProofResponseClass.Success,
                TrustedProofRequestDomain.HostOtherGitHubRest, guard);
        }
        Assert.True(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out var lease));
        using (var notModified = RemainingResponse("1"))
        {
            notModified.StatusCode = HttpStatusCode.NotModified;
            ledger.Observe(lease, notModified,
                TrustedProofResponseClass.NotModified,
                TrustedProofRequestDomain.HostOtherGitHubRest, guard);
        }

        Assert.True(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out _));
        Assert.False(ledger.IsClosed);
    }

    [Fact]
    public void Headerless304RefundsAnEarlierHeaderConservativeConcurrentDebit()
    {
        var guard = Guard(otherTail: 4);
        var ledger = new TrustedProofPrimaryRemainingLedger();
        Assert.True(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out var first));
        Assert.True(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out var pending304));
        using (var observed = RemainingResponse("5"))
        {
            ledger.Observe(first, observed, TrustedProofResponseClass.Success,
                TrustedProofRequestDomain.HostOtherGitHubRest, guard);
        }
        using (var notModified = new HttpResponseMessage(HttpStatusCode.NotModified))
        {
            ledger.Observe(pending304, notModified,
                TrustedProofResponseClass.NotModified,
                TrustedProofRequestDomain.HostOtherGitHubRest, guard);
        }

        Assert.True(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out _));
        Assert.False(ledger.IsClosed);
    }

    [Fact]
    public void LeaseCannotBeSettledByAnotherLedgerOrDomain()
    {
        var guard = Guard();
        var owner = new TrustedProofPrimaryRemainingLedger();
        var other = new TrustedProofPrimaryRemainingLedger();
        Assert.True(owner.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out var lease));
        using var response = RemainingResponse("1");

        Assert.Throws<InvalidOperationException>(() => other.Observe(lease,
            response, TrustedProofResponseClass.Success,
            TrustedProofRequestDomain.HostOtherGitHubRest, guard));
        Assert.True(other.IsClosed);
        owner.AbortBeforeWire(lease!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignLeaseAbortOrOutcomeUnknownFailClosesTheReceivingLedger(
        bool outcomeUnknown)
    {
        var guard = Guard();
        var owner = new TrustedProofPrimaryRemainingLedger();
        var receiving = new TrustedProofPrimaryRemainingLedger();
        Assert.True(owner.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out var lease));

        Assert.Throws<InvalidOperationException>(() =>
        {
            if (outcomeUnknown) receiving.CloseOutcomeUnknown(lease!);
            else receiving.AbortBeforeWire(lease!);
        });

        Assert.True(receiving.IsClosed);
        Assert.Equal(TrustedProofPrimaryRemainingLedgerCloseReason.Terminal,
            receiving.CloseReason);
        owner.AbortBeforeWire(lease!);
    }

    [Fact]
    public async Task MalformedRemainingHeaderClosesTheSharedLedgerForControl()
    {
        var guard = Guard();
        var ledger = new TrustedProofPrimaryRemainingLedger();
        var host = new TrustedProofGitHubRequestBudget(2, 1,
            () => new ResponseHandler(() => RemainingResponse("malformed")), guard,
            remainingLedger: ledger);
        var control = new TrustedProofControlRequestBudget(2, guard,
            remainingLedger: ledger);
        using var client = new HttpClient(host.CreateOtherGitHubHandler());

        using var response = await client.SendAsync(ApiRequest("/repos/o/r"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(ledger.IsClosed);
        Assert.False(control.TryClaim(out _));
    }

    [Fact]
    public void LateHigherHeaderCannotLiftAnEarlierConcurrentLowerObservation()
    {
        var guard = Guard(otherTail: 4);
        var ledger = new TrustedProofPrimaryRemainingLedger();
        Assert.True(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out var first));
        Assert.True(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out var second));
        using (var lower = RemainingResponse("5"))
        {
            ledger.Observe(second, lower, TrustedProofResponseClass.Success,
                TrustedProofRequestDomain.HostOtherGitHubRest, guard);
        }
        using (var lateHigher = RemainingResponse("100"))
        {
            ledger.Observe(first, lateHigher, TrustedProofResponseClass.Success,
                TrustedProofRequestDomain.HostOtherGitHubRest, guard);
        }

        Assert.True(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out var third));
        Assert.True(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out var fourth));
        Assert.False(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out _));
        Assert.True(ledger.IsClosed);
        ledger.AbortBeforeWire(third!);
        ledger.AbortBeforeWire(fourth!);
    }

    [Fact]
    public void LateFirstHeaderCannotEraseAnEarlierHeaderlessPrimaryCharge()
    {
        var guard = Guard(otherTail: 4);
        var ledger = new TrustedProofPrimaryRemainingLedger();
        Assert.True(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out var headerless));
        Assert.True(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out var delayedHeader));
        using (var ordinary = new HttpResponseMessage(HttpStatusCode.OK))
        {
            ledger.Observe(headerless, ordinary, TrustedProofResponseClass.Success,
                TrustedProofRequestDomain.HostOtherGitHubRest, guard);
        }
        using (var later = RemainingResponse("5"))
        {
            ledger.Observe(delayedHeader, later, TrustedProofResponseClass.Success,
                TrustedProofRequestDomain.HostOtherGitHubRest, guard);
        }

        Assert.True(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out var third));
        Assert.True(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out var fourth));
        Assert.False(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out _));
        Assert.Equal(TrustedProofPrimaryRemainingLedgerCloseReason.LowRemaining,
            ledger.CloseReason);
        ledger.AbortBeforeWire(third!);
        ledger.AbortBeforeWire(fourth!);
    }

    [Fact]
    public async Task ThrownAuthenticatedSendClosesTheSharedLedgerAsOutcomeUnknown()
    {
        var guard = Guard();
        var ledger = new TrustedProofPrimaryRemainingLedger();
        var host = new TrustedProofGitHubRequestBudget(2, 1,
            static () => new ThrowingHandler(), guard, remainingLedger: ledger);
        var control = new TrustedProofControlRequestBudget(2, guard,
            remainingLedger: ledger);
        using var client = new HttpClient(host.CreateOtherGitHubHandler());

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.SendAsync(ApiRequest("/repos/o/r")));

        Assert.True(ledger.IsClosed);
        Assert.False(control.TryClaim(out _));
        using var output = new StringWriter(
            System.Globalization.CultureInfo.InvariantCulture);
        host.WriteReceipt(output);
        using var document = JsonDocument.Parse(output.ToString()[
            "APR_R4_E2P_GITHUB_REQUEST_BUDGET ".Length..].Trim());
        Assert.False(document.RootElement.GetProperty(
            "terminal_rate_limited").GetBoolean());
        Assert.False(document.RootElement.GetProperty(
            "low_remaining_guard").GetBoolean());
    }

    [Fact]
    public async Task LocalHostCapFailureRollsBackItsUnsentSharedLease()
    {
        var guard = Guard();
        var ledger = new TrustedProofPrimaryRemainingLedger();
        var host = new TrustedProofGitHubRequestBudget(1, 1,
            () => new ResponseHandler(() => RemainingResponse("2")), guard,
            remainingLedger: ledger);
        var control = new TrustedProofControlRequestBudget(2, guard,
            remainingLedger: ledger);
        using var client = new HttpClient(host.CreateOtherGitHubHandler());

        using var first = await client.SendAsync(ApiRequest("/repos/o/r"));
        using var rejected = await client.SendAsync(ApiRequest("/repos/o/r/issues/1"));

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(control.TryClaim(out var controlLease));
        controlLease!.Ledger.AbortBeforeWire(controlLease);
        Assert.False(ledger.IsClosed);
    }

    [Fact]
    public void AbortBeforeWireRefundsAnUnbackedLeaseCoveredByAConcurrentHeader()
    {
        var guard = Guard(otherTail: 1);
        var ledger = new TrustedProofPrimaryRemainingLedger();
        Assert.True(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out var dispatched));
        Assert.True(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out var unsent));
        using (var response = RemainingResponse("2"))
        {
            ledger.Observe(dispatched, response, TrustedProofResponseClass.Success,
                TrustedProofRequestDomain.HostOtherGitHubRest, guard);
        }

        ledger.AbortBeforeWire(unsent!);

        Assert.True(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out var next));
        ledger.AbortBeforeWire(next!);
        Assert.False(ledger.IsClosed);
    }

    [Fact]
    public async Task CancellationBeforeClaimDoesNotOccupyTheSharedLedger()
    {
        var guard = Guard();
        var ledger = new TrustedProofPrimaryRemainingLedger();
        var host = new TrustedProofGitHubRequestBudget(1, 1,
            static () => new RecordingHandler(static () => { }), guard,
            remainingLedger: ledger);
        var control = new TrustedProofControlRequestBudget(1, guard,
            remainingLedger: ledger);
        using var client = new HttpClient(host.CreateOtherGitHubHandler());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SendAsync(ApiRequest("/repos/o/r"), cancelled.Token));

        Assert.Equal(0, host.Snapshot().AuthenticatedRestRequests);
        Assert.True(control.TryClaim(out var controlLease));
        controlLease!.Ledger.AbortBeforeWire(controlLease);
        Assert.False(ledger.IsClosed);
    }

    [Fact]
    public void ProductionProfileSelectsOnlyTheExplicitMeasurementOrFrozenFinalProfile()
    {
        Assert.True(TrustedProofRequestBudgetProfile.TrySelectProduction(
            name => name == "AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE"
                ? "measurement" : null,
            out var measurement));
        Assert.True(measurement!.MeasurementOnly);
        Assert.Equal(TrustedProofOperationRequestAccounting.MeasurementPrimaryReserve,
            measurement.HostRemainingTailGuard.Reserve);
        Assert.All(new[]
        {
            TrustedProofRequestDomain.NodeArtifactRest,
            TrustedProofRequestDomain.HostHeadSourceRest,
            TrustedProofRequestDomain.HostOtherGitHubRest,
            TrustedProofRequestDomain.TrustedControlRest,
        }, domain => Assert.Equal(0,
            measurement.HostRemainingTailGuard.RequiredTail(domain)));

        Assert.True(TrustedProofRequestBudgetProfile.TrySelectProduction(
            name => name == "AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE"
                ? "final-bootstrap" : null,
            out var final));
        Assert.False(final!.MeasurementOnly);
        Assert.Equal(TrustedProofOperationRequestAccounting.OperationPrimaryReserve,
            final.HostRemainingTailGuard.Reserve);
        Assert.Equal(679, final.HostRemainingTailGuard.RequiredTail(
            TrustedProofRequestDomain.NodeArtifactRest));
        Assert.Equal(863, final.HostRemainingTailGuard.RequiredTail(
            TrustedProofRequestDomain.HostHeadSourceRest));
        Assert.Equal(878, final.HostRemainingTailGuard.RequiredTail(
            TrustedProofRequestDomain.HostOtherGitHubRest));
        Assert.Equal(879, final.HostRemainingTailGuard.RequiredTail(
            TrustedProofRequestDomain.TrustedControlRest));
        Assert.Equal(888, final.ExternalControlRemainingTailGuard.RequiredTail(
            TrustedProofRequestDomain.TrustedControlRest));

        Assert.False(TrustedProofRequestBudgetProfile.TrySelectProduction(
            _ => null,
            out var missing));
        Assert.Null(missing);
        Assert.False(TrustedProofRequestBudgetProfile.TrySelectProduction(
            name => name == "AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE"
                ? "FINAL" : null,
            out var invalid));
        Assert.Null(invalid);
    }

    [Theory]
    [InlineData("final-bootstrap", 679, 863, 878, 879, 888, 888)]
    [InlineData("final-continuation", 393, 577, 591, 592, 597, 242)]
    [InlineData("final-stale", 26, 210, 224, 225, 234, 234)]
    public void FrozenProfilesBindEveryStageAndProcessLane(
        string requested,
        int node,
        int head,
        int other,
        int embeddedControl,
        int externalControl,
        int cleanupControl)
    {
        Assert.True(TrustedProofRequestBudgetProfile.TrySelectProduction(
            name => name == "AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE"
                ? requested : null,
            out var profile));
        Assert.Equal(node, profile!.HostRemainingTailGuard.RequiredTail(
            TrustedProofRequestDomain.NodeArtifactRest));
        Assert.Equal(head, profile.HostRemainingTailGuard.RequiredTail(
            TrustedProofRequestDomain.HostHeadSourceRest));
        Assert.Equal(other, profile.HostRemainingTailGuard.RequiredTail(
            TrustedProofRequestDomain.HostOtherGitHubRest));
        Assert.Equal(embeddedControl, profile.HostRemainingTailGuard.RequiredTail(
            TrustedProofRequestDomain.TrustedControlRest));
        Assert.Equal(externalControl,
            profile.ExternalControlRemainingTailGuard.RequiredTail(
                TrustedProofRequestDomain.TrustedControlRest));
        Assert.Equal(cleanupControl,
            profile.CleanupControlRemainingTailGuard.RequiredTail(
                TrustedProofRequestDomain.TrustedControlRest));
    }

    [Theory]
    [InlineData("661", false)]
    [InlineData("660", true)]
    public void ContinuationExternalFirstResponsePreservesTheExactLowStartBoundary(
        string remaining,
        bool closes)
    {
        Assert.True(TrustedProofRequestBudgetProfile.TrySelectProduction(
            name => name == "AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE"
                ? "final-continuation" : null,
            out var profile));
        var guard = profile!.ExternalControlRemainingTailGuard;
        var ledger = new TrustedProofPrimaryRemainingLedger();
        Assert.True(ledger.TryLease(TrustedProofRequestDomain.TrustedControlRest,
            guard, out var lease));
        using var response = RemainingResponse(remaining);

        ledger.Observe(lease, response, TrustedProofResponseClass.Success,
            TrustedProofRequestDomain.TrustedControlRest, guard);

        Assert.Equal(closes, ledger.IsClosed);
        if (!closes)
        {
            Assert.True(ledger.TryLease(
                TrustedProofRequestDomain.TrustedControlRest, guard,
                out var next));
            ledger.AbortBeforeWire(next!);
        }
    }

    [Fact]
    public void Lower304HeaderAdvancesSharedProgressAndLateHigherCannotLiftIt()
    {
        var guard = Guard(reserve: 2, otherTail: 9);
        var ledger = new TrustedProofPrimaryRemainingLedger();
        Assert.True(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out var first));
        using (var initial = RemainingResponse("20"))
        {
            ledger.Observe(first, initial, TrustedProofResponseClass.Success,
                TrustedProofRequestDomain.HostOtherGitHubRest, guard);
        }
        Assert.True(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out var notModifiedLease));
        using (var notModified = RemainingResponse("8"))
        {
            notModified.StatusCode = HttpStatusCode.NotModified;
            ledger.Observe(notModifiedLease, notModified,
                TrustedProofResponseClass.NotModified,
                TrustedProofRequestDomain.HostOtherGitHubRest, guard);
        }
        Assert.True(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out var lateHigherLease));
        using (var lateHigher = RemainingResponse("19"))
        {
            ledger.Observe(lateHigherLease, lateHigher,
                TrustedProofResponseClass.Success,
                TrustedProofRequestDomain.HostOtherGitHubRest, guard);
        }

        Assert.False(ledger.IsClosed);
        Assert.True(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out var next));
        ledger.AbortBeforeWire(next!);
    }

    [Fact]
    public void SharedHeaderProgressDoesNotDoubleConsumeAPendingLocalLease()
    {
        var guard = Guard(reserve: 1, otherTail: 9);
        var ledger = new TrustedProofPrimaryRemainingLedger();
        using (var anchor = RemainingResponse("11"))
        {
            ledger.Observe(null, anchor, TrustedProofResponseClass.Success,
                TrustedProofRequestDomain.HostOtherGitHubRest, guard);
        }
        Assert.True(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out var first));
        Assert.True(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out var second));
        using (var lower = RemainingResponse("8"))
        {
            ledger.Observe(second, lower, TrustedProofResponseClass.Success,
                TrustedProofRequestDomain.HostOtherGitHubRest, guard);
        }
        using (var lateHigher = RemainingResponse("9"))
        {
            ledger.Observe(first, lateHigher, TrustedProofResponseClass.Success,
                TrustedProofRequestDomain.HostOtherGitHubRest, guard);
        }

        Assert.True(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out var firstExactBoundary));
        Assert.True(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out var secondExactBoundary));
        Assert.False(ledger.TryLease(TrustedProofRequestDomain.HostOtherGitHubRest,
            guard, out _));
        Assert.Equal(TrustedProofPrimaryRemainingLedgerCloseReason.LowRemaining,
            ledger.CloseReason);
        ledger.AbortBeforeWire(firstExactBoundary!);
        ledger.AbortBeforeWire(secondExactBoundary!);
    }

    private static TrustedProofRateClassification RateClassify(
        HttpResponseMessage response,
        long? currentUnixSeconds = null) =>
        TrustedProofOperationRequestAccounting.RateClassifyAsync(
            response, CancellationToken.None, currentUnixSeconds).AsTask()
            .GetAwaiter().GetResult();

    private static void Observe(
        TrustedProofControlRequestBudget budget,
        HttpResponseMessage response,
        HttpMethod method,
        TrustedProofPrimaryRemainingLease? lease = null) =>
        budget.ObserveAsync(response, method, CancellationToken.None, lease)
            .GetAwaiter().GetResult();

    private static TrustedProofResponseClass ResponseClassify(
        HttpResponseMessage response,
        long? currentUnixSeconds = null) =>
        TrustedProofOperationRequestAccounting.ResponseClassifyAsync(
            response, CancellationToken.None, currentUnixSeconds).AsTask()
            .GetAwaiter().GetResult();

    private static TrustedProofRemainingTailGuard Guard(
        int reserve = 0,
        int otherTail = 0) => new(
        new Dictionary<TrustedProofRequestDomain, int>
        {
            [TrustedProofRequestDomain.NodeArtifactRest] = 0,
            [TrustedProofRequestDomain.HostHeadSourceRest] = 0,
            [TrustedProofRequestDomain.HostOtherGitHubRest] = otherTail,
            [TrustedProofRequestDomain.TrustedControlRest] = 0,
        }, reserve, measurementOnly: false);

    private static HttpResponseMessage RemainingResponse(
        string remaining,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", remaining);
        return response;
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

    private sealed class DeclaredLengthContent(byte[] body, long declaredLength) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = declaredLength;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream,
            TransportContext? context) => stream.WriteAsync(body).AsTask();
    }

    private sealed class CancellationAwareBlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken) =>
            WaitForCancellationAsync(cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            new(WaitForCancellationAsync(cancellationToken));
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        private static async Task<int> WaitForCancellationAsync(
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class NonSeekableCountingReadStream(byte[] body) : Stream
    {
        private int position;

        internal int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadCore(buffer.AsSpan(offset, count));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ReadCore(buffer.AsSpan(offset, count)));
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReadCore(buffer.Span));
        }
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        private int ReadCore(Span<byte> destination)
        {
            var count = Math.Min(destination.Length, body.Length - position);
            if (count == 0) return 0;
            body.AsSpan(position, count).CopyTo(destination);
            position += count;
            BytesRead += count;
            return count;
        }
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

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => throw new HttpRequestException();
    }
}
