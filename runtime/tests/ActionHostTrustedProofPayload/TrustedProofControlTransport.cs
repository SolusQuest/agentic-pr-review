using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

internal enum TrustedProofMutationOutcome
{
    Committed,
    MissingIdempotent,
    KnownNotSent,
    RequestBudgetExhausted,
    RateLimited,
    OutcomeUnknown,
}

internal sealed record TrustedProofCreateResult(
    TrustedProofMutationOutcome Outcome,
    TrustedProofIssueComment? Comment);

internal sealed class TrustedProofControlRequestBudget
{
    internal const int MaximumRequests = 64;
    private readonly int maximumRequests;
    private readonly TrustedProofRemainingTailGuard remainingTailGuard;
    private readonly Func<long> epochSeconds;
    private int consumed;
    private int rateLimited;
    private int primary;
    private int notModified;
    private int permissionDenied;
    private int invalidRemainingHeader;
    private int primaryRateLimited;
    private int secondaryRateLimited;
    private int combinedRateLimited;
    private int secondaryPoints;
    private int mutationCount;

    internal TrustedProofControlRequestBudget(int maximumRequests = MaximumRequests,
        TrustedProofRemainingTailGuard? remainingTailGuard = null,
        Func<long>? epochSeconds = null)
    {
        if (maximumRequests <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRequests));
        }

        this.maximumRequests = maximumRequests;
        this.remainingTailGuard = remainingTailGuard ??
            TrustedProofRemainingTailGuard.Measurement;
        this.epochSeconds = epochSeconds ??
            (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    internal int Consumed => Volatile.Read(ref consumed);

    internal bool IsExhausted => Consumed >= maximumRequests;

    internal bool IsRateLimited => Volatile.Read(ref rateLimited) != 0;

    internal void MarkRateLimited() => Volatile.Write(ref rateLimited, 1);

    // A malformed remaining header is not a benign observability gap: accepting
    // another authenticated request would make the proof's rate accounting
    // unverifiable, so it closes this control budget just like a real limit.
    internal void Observe(HttpResponseMessage response, HttpMethod method)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(method);
        var mutation = method != HttpMethod.Get && method != HttpMethod.Head &&
            method != HttpMethod.Options;
        Interlocked.Add(ref secondaryPoints, mutation ? 5 : 1);
        if (mutation) Interlocked.Increment(ref mutationCount);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            Interlocked.Increment(ref notModified);
        }
        else
        {
            Interlocked.Increment(ref primary);
        }

        switch (TrustedProofOperationRequestAccounting.ResponseClassify(
            response, epochSeconds()))
        {
            case TrustedProofResponseClass.PermissionDenied:
                Interlocked.Increment(ref permissionDenied);
                break;
            case TrustedProofResponseClass.InvalidRateHeaders:
                Interlocked.Exchange(ref invalidRemainingHeader, 1);
                MarkRateLimited();
                break;
            case TrustedProofResponseClass.PrimaryRateLimited:
                Interlocked.Increment(ref primaryRateLimited);
                MarkRateLimited();
                break;
            case TrustedProofResponseClass.SecondaryRateLimited:
                Interlocked.Increment(ref secondaryRateLimited);
                MarkRateLimited();
                break;
            case TrustedProofResponseClass.CombinedRateLimited:
                Interlocked.Increment(ref combinedRateLimited);
                MarkRateLimited();
                break;
        }

        if (TrustedProofOperationRequestAccounting.RemainingRequiresFailClosed(response,
                remainingTailGuard,
                TrustedProofRequestDomain.TrustedControlRest))
        {
            MarkRateLimited();
        }
    }

    internal long CurrentUnixSeconds => epochSeconds();

    internal bool TryClaim()
    {
        while (true)
        {
            if (IsRateLimited)
            {
                return false;
            }

            var current = Volatile.Read(ref consumed);
            if (current >= maximumRequests)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref consumed, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    internal void WriteReceipt(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.WriteLine(
            "APR_R4_E2P_CONTROL_REQUEST_BUDGET " +
            "{\"consumed\":" + Consumed.ToString(
                System.Globalization.CultureInfo.InvariantCulture) +
            ",\"limit\":" + maximumRequests.ToString(
                System.Globalization.CultureInfo.InvariantCulture) +
            ",\"primary\":" + Volatile.Read(ref primary).ToString(
                System.Globalization.CultureInfo.InvariantCulture) +
            ",\"not_modified\":" + Volatile.Read(ref notModified).ToString(
                System.Globalization.CultureInfo.InvariantCulture) +
            ",\"secondary_points\":" + Volatile.Read(ref secondaryPoints).ToString(
                System.Globalization.CultureInfo.InvariantCulture) +
            ",\"mutation_count\":" + Volatile.Read(ref mutationCount).ToString(
                System.Globalization.CultureInfo.InvariantCulture) +
            ",\"remaining_tail_required\":" + remainingTailGuard.RequiredTail(
                TrustedProofRequestDomain.TrustedControlRest).ToString(
                    System.Globalization.CultureInfo.InvariantCulture) +
            ",\"remaining_tail_reserve\":" + remainingTailGuard.Reserve.ToString(
                System.Globalization.CultureInfo.InvariantCulture) +
            ",\"permission_denied\":" + Volatile.Read(ref permissionDenied)
                .ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\"primary_rate_limited\":" + Volatile.Read(ref primaryRateLimited)
                .ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\"secondary_rate_limited\":" + Volatile.Read(ref secondaryRateLimited)
                .ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\"combined_rate_limited\":" + Volatile.Read(ref combinedRateLimited)
                .ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\"invalid_remaining_header\":" +
            (Volatile.Read(ref invalidRemainingHeader) != 0 ? "true" : "false") +
            ",\"measurement_only\":" +
            (remainingTailGuard.MeasurementOnly ? "true" : "false") +
            ",\"rate_limited\":" +
            (IsRateLimited ? "true" : "false") + "}");
    }
}

internal sealed class TrustedProofControlTransport : IDisposable
{
    private const int MaximumPages = 10;
    private const int MaximumResponseBytes = 512 * 1024;
    private const int MaximumAggregateBytes = 2 * 1024 * 1024;
    private readonly HttpClient client;
    private readonly TrustedProofControlCoordinates coordinates;
    private readonly TrustedProofControlRequestBudget requestBudget;

    private TrustedProofControlTransport(
        HttpClient client,
        TrustedProofControlCoordinates coordinates,
        TrustedProofControlRequestBudget requestBudget)
    {
        this.client = client;
        this.coordinates = coordinates;
        this.requestBudget = requestBudget;
    }

    internal static TrustedProofControlTransport Create(
        TrustedProofControlCoordinates coordinates,
        string token,
        HttpMessageHandler? handler = null,
        TrustedProofControlRequestBudget? requestBudget = null)
    {
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            Credentials = null,
            PreAuthenticate = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        var client = CreateClient(handler, token, disposeHandler: true);
        return new(client, coordinates,
            requestBudget ?? new TrustedProofControlRequestBudget());
    }

    internal static async Task<byte[]?> ReadFixturePullRequestAsync(
        string repository,
        long pullRequestNumber,
        string token,
        HttpMessageHandler handler,
        TrustedProofControlRequestBudget requestBudget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        using var client = CreateClient(handler, token, disposeHandler: true);
        if (!requestBudget.TryClaim())
        {
            return null;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"repos/{repository}/pulls/{pullRequestNumber}");
        using var response = await client.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        requestBudget.Observe(response, HttpMethod.Get);
        if (requestBudget.IsRateLimited) return null;

        return response.IsSuccessStatusCode
            ? await ReadBoundedBytesAsync(response, 64 * 1024, cancellationToken)
                .ConfigureAwait(false)
            : null;
    }

    internal async Task<IReadOnlyList<TrustedProofIssueComment>?> ListAsync(
        CancellationToken cancellationToken)
    {
        var result = new List<TrustedProofIssueComment>();
        var aggregateBytes = 0;
        for (var page = 1; page <= MaximumPages; page++)
        {
            using var response = await SendAsync(
                HttpMethod.Get,
                $"repos/{coordinates.Repository}/issues/" +
                $"{coordinates.PullRequestNumber}/comments?per_page=100&page={page}",
                cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var read = await ReadPlatformAsync(
                response,
                TrustedProofGitHubJsonContext.Default
                    .TrustedProofIssueCommentArray,
                cancellationToken).ConfigureAwait(false);
            aggregateBytes += read.BytesRead;
            var items = read.Value;
            if (items is null || aggregateBytes > MaximumAggregateBytes ||
                items.Any(comment => !IsValid(comment)))
            {
                return null;
            }

            result.AddRange(items);
            if (items.Length < 100)
            {
                return result;
            }
        }

        return null;
    }

    internal async Task<TrustedProofIssueComment?> GetAsync(
        long commentId,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"repos/{coordinates.Repository}/issues/comments/{commentId}",
            cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var read = await ReadPlatformAsync(
            response,
            TrustedProofGitHubJsonContext.Default.TrustedProofIssueComment,
            cancellationToken).ConfigureAwait(false);
        return read.Value is { } comment && IsValid(comment) ? comment : null;
    }

    internal async Task<TrustedProofCreateResult> CreateAsync(
        string body,
        CancellationToken cancellationToken)
    {
        try
        {
            using var content = new ByteArrayContent(
                JsonSerializer.SerializeToUtf8Bytes(
                    new TrustedProofCreateComment(body),
                    TrustedProofControlJsonContext.Default
                        .TrustedProofCreateComment));
            content.Headers.ContentType = new("application/json")
            {
                CharSet = "utf-8",
            };
            using var response = await SendAsync(
                HttpMethod.Post,
                $"repos/{coordinates.Repository}/issues/" +
                $"{coordinates.PullRequestNumber}/comments",
                content,
                cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                return new(TerminalMutationOutcome, null);
            }
            if (!response.IsSuccessStatusCode)
            {
                return new(
                    IsRateLimited(response, requestBudget.CurrentUnixSeconds)
                        ? TrustedProofMutationOutcome.RateLimited
                        : IsKnownNotSent(response.StatusCode)
                        ? TrustedProofMutationOutcome.KnownNotSent
                        : TrustedProofMutationOutcome.OutcomeUnknown,
                    null);
            }

            var read = await ReadPlatformAsync(
                response,
                TrustedProofGitHubJsonContext.Default.TrustedProofIssueComment,
                cancellationToken).ConfigureAwait(false);
            var comment = read.Value;
            return comment is null || !IsValid(comment)
                ? new(TrustedProofMutationOutcome.OutcomeUnknown, null)
                : new(TrustedProofMutationOutcome.Committed, comment);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or
            OperationCanceledException or JsonException)
        {
            return new(TrustedProofMutationOutcome.OutcomeUnknown, null);
        }
    }

    internal async Task<bool> HasWritePermissionAsync(
        string login,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"repos/{coordinates.Repository}/collaborators/" +
            $"{Uri.EscapeDataString(login)}/permission",
            cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return false;
        }
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var read = await ReadPlatformAsync(
            response,
            TrustedProofGitHubJsonContext.Default.TrustedProofPermission,
            cancellationToken).ConfigureAwait(false);
        return read.Value?.Permission is "write" or "admin";
    }

    internal async Task<bool> IsPullRequestCurrentAsync(
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"repos/{coordinates.Repository}/pulls/" +
            coordinates.PullRequestNumber.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return false;
        }
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var read = await ReadPlatformAsync(
            response,
            TrustedProofGitHubJsonContext.Default.TrustedProofPullRequest,
            cancellationToken).ConfigureAwait(false);
        var pull = read.Value;
        return pull is not null &&
            pull.Number == coordinates.PullRequestNumber &&
            StringComparer.Ordinal.Equals(pull.State, "open") &&
            !pull.Draft &&
            pull.MergedAt is null &&
            pull.Base is not null &&
            StringComparer.Ordinal.Equals(pull.Base.Ref, "main") &&
            StringComparer.Ordinal.Equals(
                pull.Base.Sha,
                coordinates.WorkflowSha) &&
            MatchesRepository(pull.Base.Repository) &&
            pull.Head is not null &&
            StringComparer.Ordinal.Equals(
                pull.Head.Ref,
                "r4-trusted-proof/" + coordinates.OperationId) &&
            StringComparer.Ordinal.Equals(
                pull.Head.Sha,
                coordinates.FixtureHeadSha) &&
            MatchesRepository(pull.Head.Repository);
    }

    internal async Task<TrustedProofMutationOutcome> DeleteAsync(
        long commentId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(
                HttpMethod.Delete,
                $"repos/{coordinates.Repository}/issues/comments/{commentId}",
                cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                return TerminalMutationOutcome;
            }
            if (response.IsSuccessStatusCode)
            {
                return TrustedProofMutationOutcome.Committed;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return TrustedProofMutationOutcome.MissingIdempotent;
            }

            return IsRateLimited(response, requestBudget.CurrentUnixSeconds)
                ? TrustedProofMutationOutcome.RateLimited
                : IsKnownNotSent(response.StatusCode)
                ? TrustedProofMutationOutcome.KnownNotSent
                : TrustedProofMutationOutcome.OutcomeUnknown;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or
            OperationCanceledException)
        {
            return TrustedProofMutationOutcome.OutcomeUnknown;
        }
    }

    public void Dispose() => client.Dispose();

    internal bool HasTerminalFailure =>
        requestBudget.IsRateLimited || requestBudget.IsExhausted;

    private TrustedProofMutationOutcome TerminalMutationOutcome =>
        requestBudget.IsRateLimited
            ? TrustedProofMutationOutcome.RateLimited
            : TrustedProofMutationOutcome.RequestBudgetExhausted;

    private static HttpClient CreateClient(
        HttpMessageHandler handler,
        string token,
        bool disposeHandler)
    {
        var client = new HttpClient(handler, disposeHandler)
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("agentic-pr-review-r4-e2p");
        return client;
    }

    private async Task<HttpResponseMessage?> SendAsync(
        HttpMethod method,
        string requestUri,
        CancellationToken cancellationToken) =>
        await SendAsync(method, requestUri, content: null, cancellationToken)
            .ConfigureAwait(false);

    private async Task<HttpResponseMessage?> SendAsync(
        HttpMethod method,
        string requestUri,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        if (!requestBudget.TryClaim())
        {
            return null;
        }

        using var request = new HttpRequestMessage(method, requestUri)
        {
            Content = content,
        };
        var response = await client.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        requestBudget.Observe(response, method);
        if (requestBudget.IsRateLimited)
        {
            response.Dispose();
            return null;
        }
        return response;
    }

    private static bool IsValid(TrustedProofIssueComment comment) =>
        comment.Id > 0 &&
        comment.User is not null &&
        !string.IsNullOrWhiteSpace(comment.User.Login) &&
        comment.User.Login.Length <= 100 &&
        comment.CreatedAt != default &&
        comment.UpdatedAt != default;

    private bool MatchesRepository(TrustedProofRepositoryIdentity? repository) =>
        repository is not null &&
        repository.Id == coordinates.RepositoryId &&
        StringComparer.Ordinal.Equals(
            repository.FullName,
            coordinates.Repository);

    private static async Task<(T? Value, int BytesRead)> ReadPlatformAsync<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        where T : class
    {
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
        {
            return (null, 0);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > MaximumResponseBytes)
            {
                return (null, checked((int)output.Length + read));
            }

            output.Write(buffer, 0, read);
        }

        try
        {
            return (JsonSerializer.Deserialize(output.ToArray(), typeInfo),
                checked((int)output.Length));
        }
        catch (JsonException)
        {
            return (null, checked((int)output.Length));
        }
    }

    private static async Task<byte[]?> ReadBoundedBytesAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } contentLength &&
            contentLength > maximumBytes)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return output.Length == 0 ? null : output.ToArray();
            }

            if (output.Length + read > maximumBytes)
            {
                return null;
            }

            output.Write(buffer, 0, read);
        }
    }

    private static bool IsRateLimited(HttpResponseMessage response,
        long currentUnixSeconds) =>
        TrustedProofOperationRequestAccounting.RateClassify(response, currentUnixSeconds) is
            TrustedProofRateClassification.Primary or
            TrustedProofRateClassification.Secondary or
            TrustedProofRateClassification.Combined or
            TrustedProofRateClassification.InvalidRemaining;

    private static bool IsKnownNotSent(HttpStatusCode statusCode)
    {
        var numericStatus = (int)statusCode;
        return numericStatus is >= 400 and < 500 &&
            statusCode != HttpStatusCode.RequestTimeout &&
            numericStatus is not 409 and not 429;
    }
}
