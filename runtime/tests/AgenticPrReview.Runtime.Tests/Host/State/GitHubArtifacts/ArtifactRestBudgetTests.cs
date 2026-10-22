using System.Net;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Host.State.GitHubArtifacts;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;

namespace AgenticPrReview.Runtime.Tests.Host.State.GitHubArtifacts;

public sealed class ArtifactRestBudgetTests
{
    [Theory]
    [InlineData(200, true, 0, true)]
    [InlineData(304, true, 0, true)]
    [InlineData(200, false, 79, true)]
    [InlineData(304, false, 79, true)]
    [InlineData(200, false, 80, false)]
    [InlineData(304, false, 80, false)]
    public async Task SuccessfulFinalObservationImmediatelyReportsInsufficientPrimaryReserveWithoutFailingResponse(
        int status, bool measurement, int remaining, bool exhausted)
    {
        var clock = Clock();
        var budget = new ArtifactRestBudget(ArtifactRestBudgetPolicy.Protected(measurement), clock);
        using var operation = new ArtifactRestOperation(default, clock);
        await budget.DispatchRestAsync(false, operation);
        using var response = new HttpResponseMessage((HttpStatusCode)status);
        response.Headers.Add("x-ratelimit-limit", "5000");
        response.Headers.Add("x-ratelimit-remaining", remaining.ToString(System.Globalization.CultureInfo.InvariantCulture));
        response.Headers.Add("x-ratelimit-reset", "1700000100");
        // Exhaustion closes future admission, not this otherwise valid response.
        await budget.ObserveAsync(response, default);
        var original = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch).Launch;
        Assert.True(ActionHostLaunchContract.TryCreate(original.Inputs, original.EventJsonPath, original.EventJsonSha256,
            original.RepositoryName, original.RepositoryId, original.RunId, original.RunAttempt, original.WorkflowPath,
            original.WorkflowRef, original.WorkflowSha, original.ActionSourceSha, original.PayloadSha256, "r4-w2",
            original.Cancellation, original.ArtifactBridgeEndpoint, out var launch));
        const string prefix = "APR_R4_E2P_ARTIFACT_REST_BUDGET ";
        using var receipt = JsonDocument.Parse(budget.Receipt(launch!)![prefix.Length..]);
        Assert.Equal(exhausted ? "primary_exhausted" : "active", receipt.RootElement.GetProperty("disposition").GetString());
        Assert.Equal(1, receipt.RootElement.GetProperty("total_authenticated_api_requests").GetInt32());
        Assert.Equal(status == 304 ? 0 : 1, budget.Primary);
        if (exhausted)
        {
            await Assert.ThrowsAsync<ArtifactTransportException>(() => budget.DispatchRestAsync(false, operation));
            Assert.Equal(1, budget.Requests);
        }
    }

    [Theory]
    [InlineData(2304, 1, true)]
    [InlineData(4096, 80, false)]
    public async Task ProtectedReceiptContainsActualRestObservationsAndBoundLaunchIdentity(int maximum, int reserve, bool measurement)
    {
        var original = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch).Launch;
        Assert.True(ActionHostLaunchContract.TryCreate(original.Inputs, original.EventJsonPath, original.EventJsonSha256,
            original.RepositoryName, original.RepositoryId, original.RunId, original.RunAttempt, original.WorkflowPath,
            original.WorkflowRef, original.WorkflowSha, original.ActionSourceSha, original.PayloadSha256, "r4-w2",
            original.Cancellation, original.ArtifactBridgeEndpoint, out var launch));
        var clock = Clock(); var budget = new ArtifactRestBudget(new(maximum, 256, reserve), clock);
        using var operation = new ArtifactRestOperation(default, clock);
        await budget.DispatchSdkAsync(operation);
        await budget.DispatchRestAsync(false, operation);
        using var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
        await budget.ObserveAsync(notModified, default);
        await budget.DispatchRestAsync(true, operation);
        var receipt = budget.Receipt(launch!)!;
        const string prefix = "APR_R4_E2P_ARTIFACT_REST_BUDGET ";
        Assert.StartsWith(prefix, receipt);
        using var document = JsonDocument.Parse(receipt[prefix.Length..]);
        var root = document.RootElement;
        Assert.Equal(24, root.EnumerateObject().Count());
        Assert.Equal(2, root.GetProperty("total_authenticated_api_requests").GetInt32());
        Assert.Equal(1, root.GetProperty("primary_rate_limit_requests").GetInt32());
        Assert.Equal(1, root.GetProperty("conditional_not_modified_requests").GetInt32());
        Assert.Equal(6, root.GetProperty("secondary_limit_points").GetInt32());
        Assert.Equal(maximum - 2, root.GetProperty("remaining_total_authenticated_api_requests").GetInt32());
        Assert.Equal(255, root.GetProperty("remaining_primary_rate_limit_requests").GetInt32());
        Assert.Equal(reserve, root.GetProperty("remaining_tail_reserve").GetInt32());
        Assert.Equal(measurement, root.GetProperty("measurement_only").GetBoolean());
        Assert.Equal(original.RepositoryName, root.GetProperty("repository").GetString());
        Assert.Equal(original.PayloadSha256, root.GetProperty("payload_sha256").GetString());
        Assert.DoesNotContain(original.Inputs.GitHubToken!.ExportForPrivateLaunch(), receipt, StringComparison.Ordinal);
        Assert.Null(budget.Receipt(original));
    }

    [Fact]
    public async Task SdkCostsMutationPointsAndConditional304DoesNotConsumePrimary()
    {
        var clock = Clock();
        var budget = new ArtifactRestBudget(new(4, 1, 0), clock);
        using var operation = new ArtifactRestOperation(default, clock);
        await budget.DispatchRestAsync(false, operation);
        using var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
        await budget.ObserveAsync(notModified, default);
        Assert.Equal(0, budget.Primary);
        Assert.Equal(1, budget.NotModified);
        await budget.DispatchRestAsync(false, operation);
        using var ok = new HttpResponseMessage(HttpStatusCode.OK);
        await budget.ObserveAsync(ok, default);
        Assert.Equal(2, budget.Requests);
        Assert.Equal(1, budget.Primary);
        await Assert.ThrowsAsync<ArtifactTransportException>(() => budget.DispatchRestAsync(false, operation));
        var sdk = new ArtifactRestBudget(new(3, 3, 0), clock);
        await sdk.ReserveMutationAsync(3, 8, operation);
        await sdk.DispatchSdkAsync(operation);
        Assert.Equal(0, sdk.Requests);
        Assert.Equal(0, sdk.Primary);
        Assert.Equal(0, sdk.SecondaryPoints);
        Assert.Equal(5, sdk.SdkMutationPoints);
    }

    [Theory]
    [InlineData(200, "01", null, null, "", (int)ActionHostGitHubRateLimitClassification.Invalid)]
    [InlineData(200, "10", null, "1", "", (int)ActionHostGitHubRateLimitClassification.Invalid)]
    [InlineData(429, null, null, null, "", (int)ActionHostGitHubRateLimitClassification.Invalid)]
    [InlineData(403, "0", "1700000100", null, "", (int)ActionHostGitHubRateLimitClassification.Primary)]
    [InlineData(429, "10", null, "2", "secondary rate limit", (int)ActionHostGitHubRateLimitClassification.Secondary)]
    [InlineData(403, "0", "1700000100", "2", "secondary rate limit", (int)ActionHostGitHubRateLimitClassification.Combined)]
    public async Task InvalidOrRateLimitedEvidenceClosesTheInvocationWithoutRetry(int status, string? remaining, string? reset,
        string? retry, string message, int classification)
    {
        var clock = Clock();
        var budget = new ArtifactRestBudget(new(null, null, 0), clock);
        using var operation = new ArtifactRestOperation(default, clock);
        await budget.DispatchRestAsync(false, operation);
        using var response = new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent(message.Length == 0 ? "{}" : "{\"message\":\"" + message + "\"}", Encoding.UTF8, "application/json") };
        if (remaining is not null) response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", remaining);
        if (reset is not null) response.Headers.TryAddWithoutValidation("x-ratelimit-reset", reset);
        if (retry is not null) response.Headers.TryAddWithoutValidation("retry-after", retry);
        await Assert.ThrowsAsync<ArtifactTransportException>(() => budget.ObserveAsync(response, default));
        Assert.Equal((ActionHostGitHubRateLimitClassification)classification, budget.LastClassification);
        await Assert.ThrowsAsync<ArtifactTransportException>(() => budget.DispatchRestAsync(false, operation));
        Assert.Equal(1, budget.Requests);
    }

    [Fact]
    public async Task DuplicateHeadersAreStickyAndNeverEscapeAsUnclassifiedExceptions()
    {
        var clock = Clock(); var budget = new ArtifactRestBudget(new(null, null, 0), clock);
        using var operation = new ArtifactRestOperation(default, clock);
        await budget.DispatchRestAsync(false, operation);
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", new[] { "10", "20" });
        await Assert.ThrowsAsync<ArtifactTransportException>(() => budget.ObserveAsync(response, default));
        await Assert.ThrowsAsync<ArtifactTransportException>(() => budget.DispatchRestAsync(false, operation));
        Assert.Equal(1, budget.Requests);
    }

    [Fact]
    public async Task LowerSharedQuotaOn304CannotBeRaisedByALaterHeader()
    {
        var clock = Clock(); var budget = new ArtifactRestBudget(new(null, null, 0), clock);
        using var operation = new ArtifactRestOperation(default, clock);
        foreach (var (status, remaining) in new[] { (200, "5"), (304, "3"), (200, "100") })
        {
            await budget.DispatchRestAsync(false, operation);
            using var response = new HttpResponseMessage((HttpStatusCode)status);
            response.Headers.Add("x-ratelimit-remaining", remaining);
            await budget.ObserveAsync(response, default);
        }
        Assert.Throws<ArtifactTransportException>(() => budget.RequireObservedAllocation(3));
        await Assert.ThrowsAsync<ArtifactTransportException>(() => budget.ReserveMutationAsync(3, 8, operation));
        Assert.Equal(3, budget.Requests);
        Assert.Equal(2, budget.Primary);
    }

    [Fact]
    public async Task CancelledOrTooLatePacingDoesNotChargeARequestOrMutation()
    {
        double milliseconds = 0;
        using var cancellation = new CancellationTokenSource();
        var clock = new ArtifactRestClock { Milliseconds = () => milliseconds, UnixSeconds = () => 1_700_000_000,
            Delay = (_, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; } };
        var budget = new ArtifactRestBudget(new(null, null, 0), clock);
        using var first = new ArtifactRestOperation(default, clock);
        await budget.DispatchRestAsync(false, first);
        using var next = new ArtifactRestOperation(cancellation.Token, clock);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => budget.ReserveMutationAsync(3, 8, next));
        Assert.Equal(1, budget.Requests);
        Assert.Equal(1, budget.SecondaryPoints);
        milliseconds = 90_001;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => budget.DispatchRestAsync(false, first));
        Assert.Equal(1, budget.Requests);
    }

    private static ArtifactRestClock Clock()
    {
        double now = 0;
        return new() { Milliseconds = () => now, UnixSeconds = () => 1_700_000_000,
            Delay = (duration, token) => { token.ThrowIfCancellationRequested(); now += duration.TotalMilliseconds; return Task.CompletedTask; } };
    }
}
