using System.Diagnostics;

namespace AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;

internal static class BoundedGitHubPublisherPolicy
{
    internal const string Origin = "https://api.github.com";
    internal const string UserAgent = "agentic-pr-review-actionhost";
    internal const string Accept = "application/vnd.github+json";
    internal const string ApiVersion = "2026-03-10";
    internal const int PerPage = 100;
    internal const int MaximumPages = 50;
    internal const int MaximumRecords = 5_000;
    internal const int MaximumRequests = 128;
    internal const int MaximumResponseBytes = 2 * 1024 * 1024;
    internal const int MaximumAggregateResponseBytes = 128 * 1024 * 1024;
    internal const int MaximumStickyRequestBytes = 1_048_576;
    internal const int MaximumInlineBatchRequestBytes = 512 * 1024;
    internal const int MaximumIndividualInlineRequestBytes = 64 * 1024;
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(180);
}

internal enum BoundedGitHubPublisherOutcome
{
    CancelledBeforeSend = 1,
    KnownNotWritten,
    WrittenAndReadBack,
    OutcomeUnknown,
    AuthorizationOrValidationFailure,
}

internal enum BoundedGitHubHttpOutcome
{
    Success = 1,
    CancelledBeforeSend,
    KnownNotSent,
    OutcomeUnknown,
    AuthorizationOrValidationFailure,
}

internal enum BoundedGitHubPublisherReason
{
    None = 0,
    InvalidRequest,
    CredentialInvalid,
    RequestLimit,
    ResponseLimit,
    AggregateResponseLimit,
    Deadline,
    TransportFailure,
    InvalidResponse,
    InvalidPagination,
    AuthorizationDenied,
    ValidationRejected,
    RateLimited,
    BatchValidationRejected,
}

internal sealed record BoundedGitHubErrorItemEvidence(
    string? Resource, string? Field, string? Code);

internal sealed class BoundedGitHubValidationEvidence
{
    internal BoundedGitHubValidationEvidence(int statusCode,
        bool reviewIdentityReturned, string message,
        string? documentationUrl,
        IReadOnlyList<BoundedGitHubErrorItemEvidence> errors) =>
        (StatusCode, ReviewIdentityReturned, Message, DocumentationUrl, Errors) =
        (statusCode, reviewIdentityReturned, message, documentationUrl, errors);

    internal int StatusCode { get; }
    internal bool ReviewIdentityReturned { get; }
    internal string Message { get; }
    internal string? DocumentationUrl { get; }
    internal IReadOnlyList<BoundedGitHubErrorItemEvidence> Errors { get; }
}

internal sealed class BoundedGitHubHttpResult<T> where T : class
{
    private BoundedGitHubHttpResult(T? value,
        BoundedGitHubHttpOutcome outcome,
        BoundedGitHubPublisherReason reason,
        BoundedGitHubValidationEvidence? validationEvidence) =>
        (Value, Outcome, Reason, ValidationEvidence) =
        (value, outcome, reason, validationEvidence);

    internal T? Value { get; }
    internal BoundedGitHubHttpOutcome Outcome { get; }
    internal BoundedGitHubPublisherReason Reason { get; }
    internal BoundedGitHubValidationEvidence? ValidationEvidence { get; }

    internal static BoundedGitHubHttpResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value, BoundedGitHubHttpOutcome.Success,
            BoundedGitHubPublisherReason.None, null);
    }

    internal static BoundedGitHubHttpResult<T> Failed(
        BoundedGitHubHttpOutcome outcome,
        BoundedGitHubPublisherReason reason,
        BoundedGitHubValidationEvidence? validationEvidence = null)
    {
        if (!IsValidFailure(outcome, reason, validationEvidence))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        return new(null, outcome, reason, validationEvidence);
    }

    private static bool IsValidFailure(BoundedGitHubHttpOutcome outcome,
        BoundedGitHubPublisherReason reason,
        BoundedGitHubValidationEvidence? evidence) => outcome switch
    {
        BoundedGitHubHttpOutcome.CancelledBeforeSend =>
            reason == BoundedGitHubPublisherReason.Deadline && evidence is null,
        BoundedGitHubHttpOutcome.KnownNotSent =>
            (reason is BoundedGitHubPublisherReason.InvalidRequest or
                BoundedGitHubPublisherReason.CredentialInvalid or
                BoundedGitHubPublisherReason.RequestLimit or
                BoundedGitHubPublisherReason.ResponseLimit or
                BoundedGitHubPublisherReason.AggregateResponseLimit or
                BoundedGitHubPublisherReason.Deadline or
                BoundedGitHubPublisherReason.TransportFailure or
                BoundedGitHubPublisherReason.InvalidResponse or
                BoundedGitHubPublisherReason.InvalidPagination or
                BoundedGitHubPublisherReason.BatchValidationRejected) &&
            evidence is null,
        BoundedGitHubHttpOutcome.OutcomeUnknown =>
            (reason is BoundedGitHubPublisherReason.ResponseLimit or
                BoundedGitHubPublisherReason.AggregateResponseLimit or
                BoundedGitHubPublisherReason.Deadline or
                BoundedGitHubPublisherReason.TransportFailure or
                BoundedGitHubPublisherReason.InvalidResponse) && evidence is null,
        BoundedGitHubHttpOutcome.AuthorizationOrValidationFailure =>
            (reason is BoundedGitHubPublisherReason.AuthorizationDenied or
                BoundedGitHubPublisherReason.ValidationRejected or
                BoundedGitHubPublisherReason.RateLimited) && evidence is not null,
        _ => false,
    };
}

internal sealed record BoundedGitHubIssueComment(
    long Id, string ApiUrl, string HtmlUrl, string Body);

internal sealed record BoundedGitHubIssueCommentPage(
    IReadOnlyList<BoundedGitHubIssueComment> Comments, int? NextPage,
    int? LastPage);

internal sealed record BoundedGitHubReviewComment(
    long Id,
    long? ReviewId,
    string ApiUrl,
    string PullRequestUrl,
    string HtmlUrl,
    string Body,
    string? Path,
    int? Line,
    string? Side,
    string? CommitId);

internal sealed record BoundedGitHubReviewCommentPage(
    IReadOnlyList<BoundedGitHubReviewComment> Comments,
    int? NextPage,
    int? LastPage);

internal sealed record BoundedGitHubPullRequestReview(
    long Id,
    string ApiUrl,
    string PullRequestUrl,
    string HtmlUrl,
    string CommitId);

internal interface IBoundedGitHubOperationClock
{
    TimeSpan Elapsed { get; }
}

internal sealed class StopwatchBoundedGitHubOperationClock :
    IBoundedGitHubOperationClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public TimeSpan Elapsed => _stopwatch.Elapsed;
}

internal interface IBoundedGitHubPublisherTransport : IDisposable
{
    bool IsWithinOverallDeadline { get; }
    Task<BoundedGitHubHttpResult<BoundedGitHubIssueCommentPage>>
        ListIssueCommentsAsync(int page, CancellationToken cancellationToken);
    Task<BoundedGitHubHttpResult<BoundedGitHubIssueComment>>
        GetIssueCommentAsync(long commentId,
            CancellationToken cancellationToken);
}
