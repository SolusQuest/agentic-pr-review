using System.Collections.Immutable;
using System.Globalization;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.Inline;

namespace AgenticPrReview.Runtime.Host.Publishing.GitHub.Inline;

internal sealed class AuthorizedInlinePublicationRequest
{
    private AuthorizedInlinePublicationRequest(
        ActionHostCoordinator.PostAcceptanceInlineOperation operation)
    {
        Operation = operation;
    }

    private ActionHostCoordinator.PostAcceptanceInlineOperation Operation
    {
        get;
    }

    internal ActionHostAuthorizer.AuthorizedInvocation Authorization =>
        Operation.Invocation;

    internal InlineCandidateMap CandidateMap => Operation.CandidateMap;

    internal ActionHostGitHubToken Token => Operation.Token;

    internal Task<ExactHeadRevalidationResult> RevalidateHeadAsync(
        CancellationToken cancellationToken) =>
        ExactHeadRevalidator.RevalidateAsync(
            Operation.Invocation.PullRequest,
            Operation.Token,
            Operation.RevalidationFactory,
            cancellationToken);

    internal static bool TryCreate(
        ActionHostCoordinator.PostAcceptanceInlineOperation? operation,
        out AuthorizedInlinePublicationRequest? request)
    {
        request = null;
        try
        {
            if (operation is null || !IsValid(operation))
            {
                return false;
            }

            request = new(operation);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException)
        {
            return false;
        }
    }

    private static bool IsValid(
        ActionHostCoordinator.PostAcceptanceInlineOperation operation)
    {
        var authorization = operation.Invocation;
        var pullRequest = authorization.PullRequest;
        var map = operation.CandidateMap;
        if (!map.ReviewedIdentity.IsValid() ||
            !long.TryParse(
                map.ReviewedIdentity.RepositoryId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var repositoryId) ||
            repositoryId != pullRequest.RepositoryId ||
            map.ReviewedIdentity.ReviewTarget != pullRequest.Number ||
            !StringComparer.Ordinal.Equals(
                map.ReviewedIdentity.BaseSha,
                pullRequest.BaseSha) ||
            !StringComparer.Ordinal.Equals(
                map.ReviewedIdentity.HeadSha,
                pullRequest.HeadSha) ||
            !IsLowerHexSha256(map.PolicySha256) ||
            !IsLowerHexSha256(map.DiffSha256) ||
            map.Candidates.IsDefault ||
            map.Candidates.Length is < 1 or > 5)
        {
            return false;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in map.Candidates)
        {
            if (candidate is null ||
                !IsLowerHexSha256(candidate.InlineKey) ||
                !IsLowerHexSha256(
                    candidate.FindingIdentity.FingerprintSha256) ||
                !RepositoryPath.IsValid(candidate.Path) ||
                candidate.Line < 1 ||
                !keys.Add(candidate.InlineKey) ||
                !fingerprints.Add(
                    candidate.FindingIdentity.FingerprintSha256))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLowerHexSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!(character is >= '0' and <= '9') &&
                !(character is >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed record RenderedInlineComment(
    InlineCandidate Candidate,
    string Body,
    ReadOnlyMemory<byte> IndividualRequest);

internal sealed record InlinePublicationReasonCounts(
    int ExistingDuplicate,
    int ConcurrentDuplicate,
    int BatchPublished,
    int ReconciledPublished,
    int IndividualPublished,
    int AuthorityRejected,
    int InvalidCandidate,
    int ListingIncomplete,
    int HeadNotExact,
    int BatchKnownFailure,
    int BatchOutcomeUnknown,
    int ReadbackIncomplete,
    int FallbackExcluded,
    int IndividualKnownFailure,
    int IndividualOutcomeUnknown,
    int IndividualReadbackIncomplete,
    int Cancelled)
{
    internal int SuccessCount => checked(
        BatchPublished + ReconciledPublished + IndividualPublished);

    internal int SkipCount => checked(
        ExistingDuplicate + ConcurrentDuplicate);

    internal int FailureCount => checked(
        AuthorityRejected + InvalidCandidate + ListingIncomplete +
        HeadNotExact + BatchKnownFailure + BatchOutcomeUnknown +
        ReadbackIncomplete + FallbackExcluded + IndividualKnownFailure +
        IndividualOutcomeUnknown + IndividualReadbackIncomplete + Cancelled);
}

internal sealed class InlinePublicationResult
{
    internal InlinePublicationResult(
        BoundedGitHubPublisherOutcome outcome,
        int candidateCount,
        int batchAttempts,
        int individualAttempts,
        InlinePublicationReasonCounts reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        if (candidateCount is < 0 or > 5 ||
            batchAttempts is < 0 or > 1 ||
            individualAttempts is < 0 or > 5 ||
            individualAttempts > candidateCount ||
            !CountsWithinRange(reasons) ||
            checked(reasons.SuccessCount + reasons.SkipCount +
                reasons.FailureCount) != candidateCount ||
            (outcome == BoundedGitHubPublisherOutcome.WrittenAndReadBack) !=
                (reasons.FailureCount == 0))
        {
            throw new ArgumentException(
                "Inline publication result counts are invalid.",
                nameof(reasons));
        }

        Outcome = outcome;
        CandidateCount = candidateCount;
        BatchAttempts = batchAttempts;
        IndividualAttempts = individualAttempts;
        Reasons = reasons;
    }

    internal BoundedGitHubPublisherOutcome Outcome { get; }

    internal int CandidateCount { get; }

    internal int BatchAttempts { get; }

    internal int IndividualAttempts { get; }

    internal InlinePublicationReasonCounts Reasons { get; }

    internal bool IsComplete =>
        Outcome == BoundedGitHubPublisherOutcome.WrittenAndReadBack;

    internal static InlinePublicationResult AuthorityRejected(
        int candidateCount) => new(
        BoundedGitHubPublisherOutcome.AuthorizationOrValidationFailure,
        candidateCount,
        0,
        0,
        new InlinePublicationReasonCounts(
            0, 0, 0, 0, 0, candidateCount, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0));

    private static bool CountsWithinRange(
        InlinePublicationReasonCounts value) =>
        value.ExistingDuplicate is >= 0 and <= 5 &&
        value.ConcurrentDuplicate is >= 0 and <= 5 &&
        value.BatchPublished is >= 0 and <= 5 &&
        value.ReconciledPublished is >= 0 and <= 5 &&
        value.IndividualPublished is >= 0 and <= 5 &&
        value.AuthorityRejected is >= 0 and <= 5 &&
        value.InvalidCandidate is >= 0 and <= 5 &&
        value.ListingIncomplete is >= 0 and <= 5 &&
        value.HeadNotExact is >= 0 and <= 5 &&
        value.BatchKnownFailure is >= 0 and <= 5 &&
        value.BatchOutcomeUnknown is >= 0 and <= 5 &&
        value.ReadbackIncomplete is >= 0 and <= 5 &&
        value.FallbackExcluded is >= 0 and <= 5 &&
        value.IndividualKnownFailure is >= 0 and <= 5 &&
        value.IndividualOutcomeUnknown is >= 0 and <= 5 &&
        value.IndividualReadbackIncomplete is >= 0 and <= 5 &&
        value.Cancelled is >= 0 and <= 5;
}

internal interface IInlineGitHubPublisherTransportFactory
{
    IInlineGitHubPublisherTransport Create(
        AuthorizedInlinePublicationRequest request);
}

internal interface IInlineGitHubPublisherTransport : IDisposable
{
    bool IsWithinOverallDeadline { get; }

    Task<BoundedGitHubHttpResult<BoundedGitHubReviewCommentPage>>
        ListReviewCommentsAsync(int page,
            CancellationToken cancellationToken);

    Task<BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>>
        CreateBatchReviewAsync(ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken);

    Task<BoundedGitHubHttpResult<BoundedGitHubReviewComment>>
        CreateReviewCommentAsync(ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken);

    Task<BoundedGitHubHttpResult<BoundedGitHubReviewComment>>
        GetReviewCommentAsync(long commentId,
            CancellationToken cancellationToken);
}
