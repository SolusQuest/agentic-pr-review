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

    internal static async Task<TrustedProofStaleWindowCoordinator?> ResolveAsync(
        ActionHostLaunchContract launch,
        Func<HttpMessageHandler> handlerFactory,
        TrustedProofControlRequestBudget requestBudget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(handlerFactory);
        ArgumentNullException.ThrowIfNull(requestBudget);
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
        var responseBytes = await TrustedProofControlTransport
            .ReadFixturePullRequestAsync(
                launch.RepositoryName,
                pullRequestNumber,
                token,
                handlerFactory(),
                requestBudget,
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
            TrustedProofControlCoordinates.FrozenRepository,
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
            TrustedProofControlTransport.Create(
                coordinates,
                token,
                handler: handlerFactory(),
                requestBudget: requestBudget));
    }

    internal async Task<bool> CoordinateAsync(
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        delayAsync ??= Task.Delay;
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
            var pollDelay = TimeSpan.FromSeconds(2);
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
                    TrustedProofControlMarker.TryParse(
                        ready.Body,
                        out var readyMarker) &&
                    releaseMarker!.MatchesProducerPair(readyMarker!) &&
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

                if (transport.HasTerminalFailure)
                {
                    return false;
                }

                await delayAsync(pollDelay, linked.Token)
                    .ConfigureAwait(false);
                pollDelay = TrustedProofControlService.NextPollDelay(pollDelay);
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

}
