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
            !TrySelect(comments, coordinates, "ready", out var ready, out var invalid))
        {
            return 1;
        }

        if (invalid)
        {
            return 1;
        }

        var body = TrustedProofControlMarker.CreateBody(
            "ready",
            coordinates,
            predecessorCommentId: null);
        if (ready is null)
        {
            var creation = await transport.CreateAsync(body, cancellationToken)
                .ConfigureAwait(false);
            ready = creation.Comment;
            if (creation.Outcome == TrustedProofMutationOutcome.KnownNotSent)
            {
                creation = await transport.CreateAsync(body, cancellationToken)
                    .ConfigureAwait(false);
                ready = creation.Comment;
            }

            if (ready is null)
            {
                comments = await transport.ListAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (comments is null ||
                    !TrySelect(
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
            !TrySelect(
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
                !TrySelect(
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
                !TrySelect(
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
            !TrySelect(comments, coordinates, "ready", out var ready, out var invalid) ||
            invalid || ready is null ||
            !TrySelect(comments, coordinates, "release", out var release, out invalid) ||
            invalid || release is null ||
            !TrustedProofControlMarker.TryParse(release.Body, out var marker) ||
            marker!.PredecessorCommentId != ready.Id ||
            release.CreatedAt != release.UpdatedAt ||
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
                cancellationToken).ConfigureAwait(false))
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

        var owned = comments.Where(comment =>
            TrustedProofControlMarker.TryParse(comment.Body, out var marker) &&
            marker!.Matches(coordinates)).ToArray();
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

            var outcome = await transport.DeleteAsync(
                comment.Id,
                cancellationToken).ConfigureAwait(false);
            if (outcome is TrustedProofMutationOutcome.KnownNotSent or
                TrustedProofMutationOutcome.OutcomeUnknown)
            {
                outcome = await transport.DeleteAsync(
                    comment.Id,
                    cancellationToken).ConfigureAwait(false);
            }

            var reconciled = await transport.ListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (reconciled is null ||
                reconciled.Any(candidate => candidate.Id == comment.Id))
            {
                return 1;
            }

            var outcomeName = outcome switch
            {
                TrustedProofMutationOutcome.Committed => "committed",
                TrustedProofMutationOutcome.MissingIdempotent =>
                    "missing-idempotent",
                TrustedProofMutationOutcome.OutcomeUnknown =>
                    "reconciled-committed",
                _ => "reconciled-missing",
            };
            outcomes.Add(new(comment.Id, outcomeName));
        }

        var remaining = await transport.ListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (remaining is null || remaining.Any(comment =>
                TrustedProofControlMarker.TryParse(comment.Body, out var marker) &&
                marker!.Matches(coordinates)))
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

    private static bool TrySelect(
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
            if (!TrustedProofControlMarker.TryParse(comment.Body, out var marker) ||
                marker!.Kind != kind ||
                !marker.Matches(coordinates))
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
