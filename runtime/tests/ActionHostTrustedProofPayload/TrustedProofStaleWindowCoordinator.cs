using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost.Contracts;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

internal sealed class TrustedProofStaleWindowCoordinator : IDisposable
{
    internal const string StaleOperationId =
        "7777777777777777777777777777777777777777777777777777777777777777";

    private readonly TrustedProofControlCoordinates coordinates;
    private readonly TrustedProofControlTransport transport;

    private TrustedProofStaleWindowCoordinator(
        TrustedProofControlCoordinates coordinates,
        TrustedProofControlTransport transport)
    {
        this.coordinates = coordinates;
        this.transport = transport;
        Signal = new TrustedProofStaleSignal();
    }

    internal TrustedProofStaleSignal Signal { get; }

    internal static TrustedProofStaleWindowCoordinator CreateForVerifier(
        TrustedProofControlCoordinates coordinates,
        TrustedProofControlTransport transport)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        ArgumentNullException.ThrowIfNull(transport);
        return new(coordinates, transport);
    }

    internal static async Task<TrustedProofStaleWindowCoordinator?> ResolveAsync(
        ActionHostLaunchContract launch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (launch.Inputs.GitHubToken is null)
        {
            throw new InvalidOperationException("The GitHub token is missing.");
        }

        var pullRequestNumber = launch.Inputs.PullRequestNumber ??
            ReadWorkflowRunPullRequestNumber(launch.EventJsonPath);
        if (pullRequestNumber <= 0)
        {
            throw new InvalidOperationException("The pull request is missing.");
        }

        var token = launch.Inputs.GitHubToken.ExportForPrivateLaunch();
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            Credentials = null,
            PreAuthenticate = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        using var client = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue(
                "application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "agentic-pr-review-r4-e2p");
        using var response = await client.GetAsync(
            $"repos/{launch.RepositoryName}/pulls/{pullRequestNumber}",
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("The fixture PR is unavailable.");
        }

        var responseBytes = await ReadBoundedAsync(
            response,
            64 * 1024,
            cancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException("The fixture PR is oversized.");
        using var document = JsonDocument.Parse(
            responseBytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
        var root = document.RootElement;
        var head = root.GetProperty("head");
        var headRef = head.GetProperty("ref").GetString();
        var headSha = head.GetProperty("sha").GetString();
        var headRepository = head.GetProperty("repo");
        var baseRepository = root.GetProperty("base").GetProperty("repo");
        if (root.GetProperty("number").GetInt64() != pullRequestNumber ||
            root.GetProperty("state").GetString() != "open" ||
            root.GetProperty("draft").GetBoolean() ||
            headRepository.GetProperty("id").GetInt64() != launch.RepositoryId ||
            baseRepository.GetProperty("id").GetInt64() != launch.RepositoryId ||
            headRepository.GetProperty("full_name").GetString() !=
                launch.RepositoryName ||
            baseRepository.GetProperty("full_name").GetString() !=
                launch.RepositoryName ||
            headSha is null || headSha.Length != 40 ||
            headRef is null ||
            !headRef.StartsWith("r4-trusted-proof/", StringComparison.Ordinal) ||
            headRef.Length != "r4-trusted-proof/".Length + 64)
        {
            throw new InvalidOperationException("The fixture PR is invalid.");
        }

        var operationId = headRef["r4-trusted-proof/".Length..];
        if (!operationId.All(static character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new InvalidOperationException("The operation ID is invalid.");
        }

        if (!StringComparer.Ordinal.Equals(operationId, StaleOperationId))
        {
            return null;
        }

        var coordinates = new TrustedProofControlCoordinates(
            launch.RepositoryName,
            launch.RepositoryId,
            pullRequestNumber,
            headSha,
            operationId,
            launch.WorkflowSha,
            launch.ActionSourceSha,
            launch.PayloadSha256,
            launch.RunId,
            launch.RunAttempt);
        return new(
            coordinates,
            TrustedProofControlTransport.Create(coordinates, token));
    }

    internal async Task<bool> CoordinateAsync(
        CancellationToken cancellationToken)
    {
        var succeeded = false;
        try
        {
            await Signal.Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
            var comments = await transport.ListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (comments is null ||
                !TrySelectOwned(
                    comments,
                    "stale-ready",
                    out var ready,
                    out var invalid) ||
                invalid || ready is not null ||
                !TrySelectOwned(
                    comments,
                    "stale-release",
                    out var earlyRelease,
                    out invalid) ||
                invalid || earlyRelease is not null)
            {
                return false;
            }

            var readyBody = TrustedProofControlMarker.CreateBody(
                "stale-ready",
                coordinates,
                predecessorCommentId: null);
            if (!await transport.IsPullRequestCurrentAsync(cancellationToken)
                    .ConfigureAwait(false))
            {
                return false;
            }
            var creation = await transport.CreateAsync(
                readyBody,
                cancellationToken).ConfigureAwait(false);
            ready = creation.Comment;
            if (creation.Outcome == TrustedProofMutationOutcome.KnownNotSent)
            {
                if (!await transport.IsPullRequestCurrentAsync(
                        cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
                creation = await transport.CreateAsync(
                    readyBody,
                    cancellationToken).ConfigureAwait(false);
                ready = creation.Comment;
            }

            if (!await transport.IsPullRequestCurrentAsync(cancellationToken)
                    .ConfigureAwait(false))
            {
                return false;
            }
            comments = await transport.ListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (comments is null ||
                !TrySelectOwned(
                    comments,
                    "stale-ready",
                    out var confirmedReady,
                    out invalid) ||
                invalid || confirmedReady is null ||
                !StringComparer.Ordinal.Equals(
                    confirmedReady.Body,
                    readyBody) ||
                (ready is not null && ready.Id != confirmedReady.Id) ||
                (await transport.GetAsync(confirmedReady.Id, cancellationToken)
                    .ConfigureAwait(false)) is not { } readback ||
                readback.Id != confirmedReady.Id ||
                readback.CreatedAt != readback.UpdatedAt ||
                !StringComparer.Ordinal.Equals(readback.Body, readyBody))
            {
                return false;
            }

            ready = confirmedReady;

            using var deadline = new CancellationTokenSource(
                TimeSpan.FromMinutes(15));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token);
            while (!linked.IsCancellationRequested)
            {
                comments = await transport.ListAsync(linked.Token)
                    .ConfigureAwait(false);
                if (comments is null ||
                    !TrySelectOwned(
                        comments,
                        "stale-ready",
                        out var currentReady,
                        out invalid) ||
                    invalid || currentReady is null ||
                    currentReady.Id != ready.Id ||
                    !StringComparer.Ordinal.Equals(
                        currentReady.Body,
                        readyBody) ||
                    !TrySelectOwned(
                        comments,
                        "stale-release",
                        out var release,
                        out invalid) ||
                    invalid)
                {
                    return false;
                }

                if (release is not null &&
                    TrustedProofControlMarker.TryParse(
                        release.Body,
                        out var releaseMarker) &&
                    releaseMarker!.PredecessorCommentId == ready.Id &&
                    release.CreatedAt > ready.CreatedAt &&
                    release.CreatedAt == release.UpdatedAt &&
                    await transport.HasWritePermissionAsync(
                        release.User.Login,
                        linked.Token).ConfigureAwait(false) &&
                    (await transport.GetAsync(release.Id, linked.Token)
                        .ConfigureAwait(false)) is { } releaseReadback &&
                    releaseReadback.Id == release.Id &&
                    releaseReadback.CreatedAt == releaseReadback.UpdatedAt &&
                    StringComparer.Ordinal.Equals(
                        releaseReadback.Body,
                        release.Body))
                {
                    Signal.Release();
                    succeeded = true;
                    return succeeded;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), linked.Token)
                    .ConfigureAwait(false);
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (!succeeded)
            {
                Signal.Cancel(new CancellationToken(canceled: true));
            }
        }
    }

    public void Dispose() => transport.Dispose();

    private bool TrySelectOwned(
        IEnumerable<TrustedProofIssueComment> comments,
        string kind,
        out TrustedProofIssueComment? selected,
        out bool invalid)
    {
        selected = null;
        invalid = false;
        foreach (var comment in comments)
        {
            if (!TrustedProofControlMarker.TryParse(comment.Body, out var marker))
            {
                if (TrustedProofControlMarker.HasReservedPrefix(comment.Body))
                {
                    invalid = true;
                    return true;
                }

                continue;
            }

            if (marker!.OperationId != coordinates.OperationId)
            {
                continue;
            }

            if (!marker.MatchesFamily(coordinates) ||
                marker.Kind == kind && !marker.Matches(coordinates))
            {
                invalid = true;
                return true;
            }

            if (marker.Kind != kind)
            {
                continue;
            }

            if (comment.CreatedAt != comment.UpdatedAt || selected is not null)
            {
                invalid = true;
                return true;
            }

            selected = comment;
        }

        return true;
    }

    private static long ReadWorkflowRunPullRequestNumber(string eventPath)
    {
        using var stream = File.OpenRead(eventPath);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16,
        });
        var pullRequests = document.RootElement
            .GetProperty("workflow_run")
            .GetProperty("pull_requests");
        return pullRequests.GetArrayLength() == 1
            ? pullRequests[0].GetProperty("number").GetInt64()
            : 0;
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maximumBytes)
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
}
