using System.Text.Json;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

internal static class TrustedProofControlService
{
    internal static Task<int> RunFromEnvironmentAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (!TrustedProofControlCoordinates.TryReadEnvironment(
                Environment.GetEnvironmentVariable,
                out var coordinates,
                out var token) ||
            coordinates is null || token is null)
        {
            return Task.FromResult(1);
        }

        return RunAsync(
            args,
            coordinates,
            TrustedProofControlTransport.Create(coordinates, token),
            cancellationToken);
    }

    internal static async Task<int> RunAsync(
        string[] args,
        TrustedProofControlCoordinates coordinates,
        TrustedProofControlTransport transport,
        CancellationToken cancellationToken)
    {
        using (transport)
        {
            if (args is ["hold"])
            {
                return await HoldAsync(
                    coordinates,
                    transport,
                    cancellationToken).ConfigureAwait(false);
            }

            if (args is ["verify-completed"])
            {
                return await VerifyCompletedAsync(
                    coordinates,
                    transport,
                    cancellationToken).ConfigureAwait(false);
            }

            if (args is ["cleanup"])
            {
                return await CleanupAsync(
                    coordinates,
                    transport,
                    cancellationToken).ConfigureAwait(false);
            }

            return 2;
        }
    }

    private static async Task<int> HoldAsync(
        TrustedProofControlCoordinates coordinates,
        TrustedProofControlTransport transport,
        CancellationToken cancellationToken)
    {
        var comments = await transport.ListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (comments is null ||
            !TrySelectCurrent(
                comments,
                coordinates,
                "ready",
                out var ready,
                out var invalid))
        {
            return 1;
        }

        if (invalid)
        {
            return 1;
        }

        if (!await transport.IsPullRequestCurrentAsync(cancellationToken)
                .ConfigureAwait(false))
        {
            return 1;
        }

        var body = TrustedProofControlMarker.CreateBody(
            "ready",
            coordinates,
            predecessorCommentId: null);
        if (ready is null)
        {
            if (!await transport.IsPullRequestCurrentAsync(cancellationToken)
                    .ConfigureAwait(false))
            {
                return 1;
            }
            var creation = await transport.CreateAsync(body, cancellationToken)
                .ConfigureAwait(false);
            ready = creation.Comment;
            if (creation.Outcome == TrustedProofMutationOutcome.KnownNotSent)
            {
                if (!await transport.IsPullRequestCurrentAsync(
                        cancellationToken).ConfigureAwait(false))
                {
                    return 1;
                }
                creation = await transport.CreateAsync(body, cancellationToken)
                    .ConfigureAwait(false);
                ready = creation.Comment;
            }

            if (ready is null)
            {
                if (!await transport.IsPullRequestCurrentAsync(
                        cancellationToken).ConfigureAwait(false))
                {
                    return 1;
                }
                comments = await transport.ListAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (comments is null ||
                    !TrySelectCurrent(
                        comments,
                        coordinates,
                        "ready",
                        out ready,
                        out invalid) ||
                    invalid ||
                    ready is null ||
                    !StringComparer.Ordinal.Equals(ready.Body, body))
                {
                    return 1;
                }
            }

        }

        comments = await transport.ListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (comments is null ||
            !TrySelectCurrent(
                comments,
                coordinates,
                "ready",
                out var confirmedReady,
                out invalid) ||
            invalid ||
            confirmedReady is null ||
            confirmedReady.Id != ready.Id ||
            !StringComparer.Ordinal.Equals(confirmedReady.Body, body) ||
            !await ReadBackExactAsync(
                confirmedReady,
                body,
                transport,
                cancellationToken).ConfigureAwait(false))
        {
            return 1;
        }

        ready = confirmedReady;

        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        while (!linked.IsCancellationRequested)
        {
            comments = await transport.ListAsync(linked.Token)
                .ConfigureAwait(false);
            if (comments is null ||
                !TrySelectCurrent(
                    comments,
                    coordinates,
                    "ready",
                    out var currentReady,
                    out invalid) ||
                invalid ||
                currentReady is null ||
                currentReady.Id != ready.Id ||
                !StringComparer.Ordinal.Equals(
                    currentReady.Body,
                    ready.Body) ||
                !TrySelectCurrent(
                    comments,
                    coordinates,
                    "release",
                    out var release,
                    out invalid) ||
                invalid)
            {
                return 1;
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
                await ReadBackExactAsync(
                    release,
                    release.Body!,
                    transport,
                    linked.Token).ConfigureAwait(false))
            {
                return 0;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), linked.Token)
                .ConfigureAwait(false);
        }

        return 1;
    }

    private static async Task<int> VerifyCompletedAsync(
        TrustedProofControlCoordinates coordinates,
        TrustedProofControlTransport transport,
        CancellationToken cancellationToken)
    {
        var comments = await transport.ListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (comments is null ||
            !TrySelectCompletedPair(
                comments,
                coordinates,
                out var ready,
                out var release) ||
            release.CreatedAt != release.UpdatedAt ||
            release.CreatedAt <= ready.CreatedAt ||
            !await ReadBackExactAsync(
                ready,
                ready.Body!,
                transport,
                cancellationToken).ConfigureAwait(false) ||
            !await ReadBackExactAsync(
                release,
                release.Body!,
                transport,
                cancellationToken).ConfigureAwait(false) ||
            !await transport.HasWritePermissionAsync(
                release.User.Login,
                cancellationToken).ConfigureAwait(false) ||
            !await transport.IsPullRequestCurrentAsync(cancellationToken)
                .ConfigureAwait(false))
        {
            return 1;
        }

        return 0;
    }

    private static async Task<int> CleanupAsync(
        TrustedProofControlCoordinates coordinates,
        TrustedProofControlTransport transport,
        CancellationToken cancellationToken)
    {
        var comments = await transport.ListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (comments is null)
        {
            return 1;
        }

        if (comments.Any(comment =>
                !TrustedProofControlMarker.TryParse(
                    comment.Body,
                    out var marker)
                    ? TrustedProofControlMarker.HasReservedPrefix(comment.Body)
                    : marker!.OperationId == coordinates.OperationId &&
                        !marker.MatchesFamily(coordinates)))
        {
            return 1;
        }

        var owned = comments.Where(comment =>
            TrustedProofControlMarker.TryParse(comment.Body, out var marker) &&
            marker!.MatchesFamily(coordinates)).ToArray();
        var outcomes = new List<TrustedProofCleanupOutcome>(owned.Length);
        foreach (var comment in owned)
        {
            var readback = await transport.GetAsync(comment.Id, cancellationToken)
                .ConfigureAwait(false);
            if (readback is null ||
                !StringComparer.Ordinal.Equals(readback.Body, comment.Body))
            {
                return 1;
            }

            var initialOutcome = await transport.DeleteAsync(
                comment.Id,
                cancellationToken).ConfigureAwait(false);
            TrustedProofMutationOutcome? retryOutcome = null;
            if (initialOutcome is TrustedProofMutationOutcome.KnownNotSent or
                TrustedProofMutationOutcome.OutcomeUnknown)
            {
                retryOutcome = await transport.DeleteAsync(
                    comment.Id,
                    cancellationToken).ConfigureAwait(false);
            }

            var reconciled = await transport.ListAsync(cancellationToken)
                .ConfigureAwait(false);
            var finalPresence = reconciled?.Any(
                candidate => candidate.Id == comment.Id);
            var outcomeName = ClassifyCleanupOutcome(
                initialOutcome,
                retryOutcome,
                finalPresence);
            if (outcomeName is null)
            {
                return 1;
            }
            outcomes.Add(new(comment.Id, outcomeName));
        }

        var remaining = await transport.ListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (remaining is null || remaining.Any(comment =>
                !TrustedProofControlMarker.TryParse(
                    comment.Body,
                    out var marker)
                    ? TrustedProofControlMarker.HasReservedPrefix(comment.Body)
                    : marker!.MatchesFamily(coordinates)))
        {
            return 1;
        }

        var receipt = new TrustedProofCleanupReceipt(
            "apr-r4-e2p-proof-control-cleanup-v1",
            coordinates.OperationId,
            outcomes.OrderBy(outcome => outcome.CommentId).ToArray(),
            "passed");
        Console.Out.WriteLine(JsonSerializer.Serialize(
            receipt,
            TrustedProofControlJsonContext.Default.TrustedProofCleanupReceipt));
        return 0;
    }

    internal static string? ClassifyCleanupOutcome(
        TrustedProofMutationOutcome initialOutcome,
        TrustedProofMutationOutcome? retryOutcome,
        bool? finalPresence)
    {
        if (finalPresence is not false)
        {
            return null;
        }

        return initialOutcome switch
        {
            TrustedProofMutationOutcome.Committed => "committed",
            TrustedProofMutationOutcome.MissingIdempotent =>
                "missing-idempotent",
            TrustedProofMutationOutcome.OutcomeUnknown =>
                "reconciled-committed",
            TrustedProofMutationOutcome.KnownNotSent
                when retryOutcome is TrustedProofMutationOutcome.Committed or
                    TrustedProofMutationOutcome.OutcomeUnknown =>
                "reconciled-committed",
            _ => "reconciled-missing",
        };
    }

    private static bool TrySelectCurrent(
        IReadOnlyList<TrustedProofIssueComment> comments,
        TrustedProofControlCoordinates coordinates,
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

    private static bool TrySelectCompletedPair(
        IReadOnlyList<TrustedProofIssueComment> comments,
        TrustedProofControlCoordinates coordinates,
        out TrustedProofIssueComment ready,
        out TrustedProofIssueComment release)
    {
        ready = null!;
        release = null!;
        TrustedProofControlMarker? readyMarker = null;
        TrustedProofControlMarker? releaseMarker = null;
        foreach (var comment in comments)
        {
            if (!TrustedProofControlMarker.TryParse(comment.Body, out var marker))
            {
                if (TrustedProofControlMarker.HasReservedPrefix(comment.Body))
                {
                    return false;
                }

                continue;
            }

            if (marker!.OperationId != coordinates.OperationId)
            {
                continue;
            }

            if (!marker.MatchesFamily(coordinates))
            {
                return false;
            }

            if (marker.Kind == "ready")
            {
                if (readyMarker is not null ||
                    comment.CreatedAt != comment.UpdatedAt)
                {
                    return false;
                }

                ready = comment;
                readyMarker = marker;
            }
            else if (marker.Kind == "release")
            {
                if (releaseMarker is not null ||
                    comment.CreatedAt != comment.UpdatedAt)
                {
                    return false;
                }

                release = comment;
                releaseMarker = marker;
            }
        }

        return readyMarker is not null &&
            releaseMarker is not null &&
            readyMarker.RunId != coordinates.RunId &&
            readyMarker.RunId > 0 &&
            readyMarker.RunAttempt > 0 &&
            releaseMarker.HasSameProducer(readyMarker) &&
            releaseMarker.PredecessorCommentId == ready.Id;
    }

    private static async Task<bool> ReadBackExactAsync(
        TrustedProofIssueComment comment,
        string body,
        TrustedProofControlTransport transport,
        CancellationToken cancellationToken)
    {
        var readback = await transport.GetAsync(comment.Id, cancellationToken)
            .ConfigureAwait(false);
        return readback is not null &&
            readback.Id == comment.Id &&
            readback.CreatedAt == readback.UpdatedAt &&
            StringComparer.Ordinal.Equals(readback.Body, body) &&
            StringComparer.Ordinal.Equals(readback.User.Login, comment.User.Login);
    }
}
