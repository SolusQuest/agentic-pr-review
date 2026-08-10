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

internal sealed class BoundedGitHubPublisherResult<T> where T : class
{
    private BoundedGitHubPublisherResult(T? value,
        BoundedGitHubPublisherOutcome outcome,
        BoundedGitHubPublisherReason reason,
        BoundedGitHubValidationEvidence? validationEvidence) =>
        (Value, Outcome, Reason, ValidationEvidence) =
        (value, outcome, reason, validationEvidence);

    internal T? Value { get; }
    internal BoundedGitHubPublisherOutcome Outcome { get; }
    internal BoundedGitHubPublisherReason Reason { get; }
    internal BoundedGitHubValidationEvidence? ValidationEvidence { get; }

    internal static BoundedGitHubPublisherResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value, BoundedGitHubPublisherOutcome.WrittenAndReadBack,
            BoundedGitHubPublisherReason.None, null);
    }

    internal static BoundedGitHubPublisherResult<T> Failed(
        BoundedGitHubPublisherOutcome outcome,
        BoundedGitHubPublisherReason reason,
        BoundedGitHubValidationEvidence? validationEvidence = null)
    {
        if (!IsValidFailure(outcome, reason, validationEvidence))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        return new(null, outcome, reason, validationEvidence);
    }

    private static bool IsValidFailure(BoundedGitHubPublisherOutcome outcome,
        BoundedGitHubPublisherReason reason,
        BoundedGitHubValidationEvidence? evidence) => outcome switch
    {
        BoundedGitHubPublisherOutcome.CancelledBeforeSend =>
            reason == BoundedGitHubPublisherReason.Deadline && evidence is null,
        BoundedGitHubPublisherOutcome.KnownNotWritten =>
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
        BoundedGitHubPublisherOutcome.OutcomeUnknown =>
            (reason is BoundedGitHubPublisherReason.ResponseLimit or
                BoundedGitHubPublisherReason.AggregateResponseLimit or
                BoundedGitHubPublisherReason.Deadline or
                BoundedGitHubPublisherReason.TransportFailure or
                BoundedGitHubPublisherReason.InvalidResponse) && evidence is null,
        BoundedGitHubPublisherOutcome.AuthorizationOrValidationFailure =>
            (reason is BoundedGitHubPublisherReason.AuthorizationDenied or
                BoundedGitHubPublisherReason.ValidationRejected or
                BoundedGitHubPublisherReason.RateLimited) && evidence is not null,
        _ => false,
    };
}

internal sealed record BoundedGitHubIssueComment(
    long Id, string ApiUrl, string HtmlUrl, string Body);

internal sealed record BoundedGitHubIssueCommentPage(
    IReadOnlyList<BoundedGitHubIssueComment> Comments, int? NextPage);

internal interface IBoundedGitHubPublisherTransport : IDisposable
{
    Task<BoundedGitHubPublisherResult<BoundedGitHubIssueCommentPage>>
        ListIssueCommentsAsync(int page, CancellationToken cancellationToken);
    Task<BoundedGitHubPublisherResult<BoundedGitHubIssueComment>>
        GetIssueCommentAsync(long commentId,
            CancellationToken cancellationToken);
}
