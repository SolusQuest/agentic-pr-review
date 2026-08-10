using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;

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

internal enum BoundedGitHubPublisherFailure
{
    None = 0,
    CancelledBeforeSend,
    AuthorizationOrValidationFailure,
    Unavailable,
    OutcomeUnknown,
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

internal sealed class BoundedGitHubPublisherResult<T> where T : class
{
    private BoundedGitHubPublisherResult(T? value,
        BoundedGitHubPublisherFailure failure,
        BoundedGitHubPublisherReason reason) =>
        (Value, Failure, Reason) = (value, failure, reason);

    internal T? Value { get; }
    internal BoundedGitHubPublisherFailure Failure { get; }
    internal BoundedGitHubPublisherReason Reason { get; }

    internal static BoundedGitHubPublisherResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value, BoundedGitHubPublisherFailure.None,
            BoundedGitHubPublisherReason.None);
    }

    internal static BoundedGitHubPublisherResult<T> Failed(
        BoundedGitHubPublisherFailure failure,
        BoundedGitHubPublisherReason reason)
    {
        if (failure == BoundedGitHubPublisherFailure.None ||
            reason == BoundedGitHubPublisherReason.None)
            throw new ArgumentOutOfRangeException(nameof(failure));
        return new(null, failure, reason);
    }
}

internal sealed record BoundedGitHubIssueComment(
    long Id, string ApiUrl, string HtmlUrl, string Body);

internal sealed record BoundedGitHubIssueCommentPage(
    IReadOnlyList<BoundedGitHubIssueComment> Comments, int? NextPage);

internal interface IBoundedGitHubPublisherTransportFactory
{
    IBoundedGitHubPublisherTransport Create(ActionHostGitHubToken token,
        ActionHostAuthorizer.AuthorizedInvocation authorization);
}

internal interface IBoundedGitHubPublisherTransport : IDisposable
{
    Task<BoundedGitHubPublisherResult<BoundedGitHubIssueCommentPage>>
        ListIssueCommentsAsync(int page, CancellationToken cancellationToken);
    Task<BoundedGitHubPublisherResult<BoundedGitHubIssueComment>>
        CreateIssueCommentAsync(ReadOnlyMemory<byte> requestBody,
            CancellationToken cancellationToken);
    Task<BoundedGitHubPublisherResult<BoundedGitHubIssueComment>>
        UpdateIssueCommentAsync(long commentId,
            ReadOnlyMemory<byte> requestBody,
            CancellationToken cancellationToken);
    Task<BoundedGitHubPublisherResult<BoundedGitHubIssueComment>>
        GetIssueCommentAsync(long commentId,
            CancellationToken cancellationToken);
}
