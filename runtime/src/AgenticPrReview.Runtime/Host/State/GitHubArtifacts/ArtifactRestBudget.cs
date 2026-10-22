using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Buffers;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.GitHubArtifacts;

internal sealed record ArtifactRestBudgetPolicy(int? MaximumRequests, int? MaximumPrimary, int Reserve)
{
    internal static ArtifactRestBudgetPolicy ForBuild(string build, ArtifactRestBudgetPolicy? admitted = null)
    {
        if (admitted is not null) return admitted;
        if (build != "r4-w2") return new(null, null, 0);
        throw new ArtifactTransportException(OpaqueStoreFailure.Invalid);
    }

    internal static ArtifactRestBudgetPolicy Protected(bool measurementOnly) =>
        measurementOnly ? new(2304, 256, 1) : new(4096, 256, 80);
}

internal sealed class ArtifactRestClock
{
    internal Func<double> Milliseconds { get; init; } = () => Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;
    internal Func<long> UnixSeconds { get; init; } = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
}

internal sealed class ArtifactRestOperation : IDisposable
{
    private readonly CancellationTokenSource cancellation;
    private readonly ArtifactRestClock clock;
    private readonly double started;
    internal ArtifactRestOperation(CancellationToken caller, ArtifactRestClock clock)
    {
        this.clock = clock;
        started = clock.Milliseconds();
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(caller);
        cancellation.CancelAfter(ArtifactBridgeLimits.LogicalOperationTimeout);
    }
    internal CancellationToken Token => cancellation.Token;
    internal double LatestStart => started + 90_000;
    internal void Check()
    {
        Token.ThrowIfCancellationRequested();
        if (clock.Milliseconds() - started >= 120_000) throw new OperationCanceledException(Token);
    }
    internal void CheckStart()
    {
        Check();
        if (clock.Milliseconds() > LatestStart) throw new OperationCanceledException(Token);
    }
    public void Dispose() => cancellation.Dispose();
}

// Owned by one invocation's store, including every repeated restore in that
// invocation. The store's single lane makes compound reservations indivisible.
internal sealed class ArtifactRestBudget(ArtifactRestBudgetPolicy policy, ArtifactRestClock clock)
{
    private readonly Queue<(double At, int Points)> points = new();
    private double? lastDispatch;
    private double? lastMutation;
    private long? observedRemaining;
    private bool closed;
    internal int Requests { get; private set; }
    internal int Primary { get; private set; }
    internal int NotModified { get; private set; }
    internal int SecondaryPoints { get; private set; }
    internal int SdkMutationPoints { get; private set; }
    internal int PermissionDenied { get; private set; }
    internal ActionHostGitHubRateLimitClassification LastClassification { get; private set; }
    internal string Disposition { get; private set; } = "active";

    internal void Preflight(int required)
    {
        if (closed || required < 1) throw new ArtifactTransportException(OpaqueStoreFailure.Io);
        if (policy.MaximumRequests is { } maximum && Requests + required > maximum) Close("total_exhausted");
        if (policy.MaximumPrimary is { } primary && Primary + required > primary ||
            observedRemaining is { } remaining && remaining < required + policy.Reserve) Close("primary_exhausted");
    }

    internal void RequireObservedAllocation(int required)
    {
        if (closed) throw new ArtifactTransportException(OpaqueStoreFailure.Io);
        if (observedRemaining is { } remaining && remaining < required + policy.Reserve) Close("primary_exhausted");
    }

    internal async Task ReserveMutationAsync(int requests, int secondaryPoints, ArtifactRestOperation operation)
    {
        Preflight(requests);
        await PaceAsync(secondaryPoints, mutative: true, operation).ConfigureAwait(false);
        Preflight(requests);
    }

    internal async Task DispatchSdkAsync(ArtifactRestOperation operation)
    {
        await PaceAsync(5, mutative: true, operation).ConfigureAwait(false);
        operation.CheckStart();
        ChargePoints(5, mutative: true, authenticated: false);
    }

    internal async Task DispatchRestAsync(bool mutation, ArtifactRestOperation operation)
    {
        Preflight(1);
        await PaceAsync(mutation ? 5 : 1, mutation, operation).ConfigureAwait(false);
        operation.CheckStart();
        Preflight(1);
        Requests++;
        Primary++; // An absent/failed response remains conservatively charged.
        if (observedRemaining is not null) observedRemaining--;
        ChargePoints(mutation ? 5 : 1, mutation, authenticated: true);
    }

    internal async Task ObserveAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            Primary--;
            NotModified++;
            if (observedRemaining is not null) observedRemaining++;
        }
        var invalidCanonicalHeader = new[] { "x-ratelimit-limit", "x-ratelimit-remaining", "x-ratelimit-reset", "retry-after" }
            .Any(name => response.Headers.TryGetValues(name, out var values) && !CanonicalHeader(values));
        LastClassification = invalidCanonicalHeader ? ActionHostGitHubRateLimitClassification.Invalid :
            await ActionHostGitHubRateLimitClassifier.ClassifyAsync(response, token, clock.UnixSeconds()).ConfigureAwait(false);
        if (response.Headers.TryGetValues("x-ratelimit-remaining", out var raw) &&
            raw.Count() == 1 && long.TryParse(raw.Single(), NumberStyles.None, CultureInfo.InvariantCulture, out var remaining))
            observedRemaining = observedRemaining is null ? remaining : Math.Min(observedRemaining.Value, remaining);
        if (LastClassification == ActionHostGitHubRateLimitClassification.Permission) PermissionDenied++;
        if (LastClassification is ActionHostGitHubRateLimitClassification.Primary or
            ActionHostGitHubRateLimitClassification.Secondary or ActionHostGitHubRateLimitClassification.Combined or
            ActionHostGitHubRateLimitClassification.Invalid)
        {
            Close(LastClassification switch {
                ActionHostGitHubRateLimitClassification.Primary => "primary_exhausted",
                ActionHostGitHubRateLimitClassification.Secondary => "rate_limited",
                ActionHostGitHubRateLimitClassification.Combined => "primary_and_secondary_rate_limited",
                _ => "invalid_rate_limit_headers" });
        }
        // A successful final response stays usable, but its terminal receipt
        // must already reflect exhausted shared allocation. Frozen proof
        // profiles require no further tail requests, only their retained reserve.
        if (!closed && observedRemaining is { } available && available < Math.Max(1, policy.Reserve))
        {
            closed = true;
            Disposition = "primary_exhausted";
        }
    }

    private static bool CanonicalHeader(IEnumerable<string> values)
    {
        var array = values.ToArray();
        return array.Length == 1 && long.TryParse(array[0], NumberStyles.None, CultureInfo.InvariantCulture, out var value) &&
            value >= 0 && array[0] == value.ToString(CultureInfo.InvariantCulture);
    }

    private async Task PaceAsync(int requiredPoints, bool mutative, ArtifactRestOperation operation)
    {
        while (true)
        {
            operation.CheckStart();
            var now = clock.Milliseconds();
            while (points.TryPeek(out var oldest) && oldest.At + 60_000 <= now) points.Dequeue();
            var wait = points.Sum(item => item.Points) + requiredPoints > 600 ? points.Peek().At + 60_000 - now : 0;
            if (lastDispatch is { } previous) wait = Math.Max(wait, previous + requiredPoints * 100d - now);
            if (mutative && lastMutation is { } mutation) wait = Math.Max(wait, mutation + 1000 - now);
            if (now + wait > operation.LatestStart) throw new OperationCanceledException(operation.Token);
            if (wait <= 0) return;
            await clock.Delay(TimeSpan.FromMilliseconds(wait), operation.Token).ConfigureAwait(false);
        }
    }

    private void ChargePoints(int count, bool mutative, bool authenticated)
    {
        lastDispatch = clock.Milliseconds();
        if (mutative) lastMutation = lastDispatch;
        points.Enqueue((lastDispatch.Value, count));
        if (authenticated) SecondaryPoints += count;
        else SdkMutationPoints += count;
    }

    private void Close(string disposition)
    {
        closed = true; Disposition = disposition;
        throw new ArtifactTransportException(OpaqueStoreFailure.Io);
    }

    internal string? Receipt(ActionHostLaunchContract launch)
    {
        if (launch.BuildDiscriminator != "r4-w2") return null;
        if (policy.MaximumRequests is not { } maximum || policy.MaximumPrimary is not { } primary)
            throw new ArtifactTransportException(OpaqueStoreFailure.Invalid);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "apr-r4-trusted-proof-artifact-rest-budget-v2");
            writer.WriteBoolean("protected_route", true);
            writer.WriteNumber("maximum_total_authenticated_api_requests", maximum);
            writer.WriteNumber("total_authenticated_api_requests", Requests);
            writer.WriteNumber("maximum_primary_rate_limit_requests", primary);
            writer.WriteNumber("primary_rate_limit_requests", Primary);
            writer.WriteNumber("conditional_not_modified_requests", NotModified);
            writer.WriteNumber("secondary_limit_points", SecondaryPoints);
            writer.WriteNumber("permission_denied", PermissionDenied);
            writer.WriteNumber("remaining_total_authenticated_api_requests", maximum - Requests);
            writer.WriteNumber("remaining_primary_rate_limit_requests", primary - Primary);
            writer.WriteString("disposition", Disposition);
            writer.WriteString("repository", launch.RepositoryName);
            writer.WriteString("repository_id", launch.RepositoryId.ToString(CultureInfo.InvariantCulture));
            writer.WriteString("workflow_sha", launch.WorkflowSha);
            writer.WriteString("action_source_sha", launch.ActionSourceSha);
            writer.WriteString("payload_sha256", launch.PayloadSha256);
            writer.WriteString("build_discriminator", launch.BuildDiscriminator);
            writer.WriteString("run_id", launch.RunId.ToString(CultureInfo.InvariantCulture));
            writer.WriteString("run_attempt", launch.RunAttempt.ToString(CultureInfo.InvariantCulture));
            writer.WriteString("cap_profile", "apr-r4-artifact-rest-request-budget-v2");
            writer.WriteBoolean("measurement_only", maximum == 2304);
            writer.WriteNumber("remaining_tail_required", 0);
            writer.WriteNumber("remaining_tail_reserve", policy.Reserve);
            writer.WriteEndObject();
        }
        return "APR_R4_E2P_ARTIFACT_REST_BUDGET " + Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
