using System.Reflection;
using System.Runtime.CompilerServices;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.ActionHost.Snapshot.GitObjects;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Snapshot;

internal static class ReviewedSnapshotTestAccess
{
    internal static ReviewedContentBudget ProductionBudget() => Budget(
        ReviewedContentLimits.GitObjectRequests,
        ReviewedContentLimits.GitObjectResponseBytes,
        ReviewedContentLimits.AggregateResponseBytes,
        ReviewedContentLimits.AcquisitionAndMaterializationTimeout,
        TimeProvider.System);

    internal static ReviewedContentBudget Budget(
        int maximumRequests,
        long maximumResponseBytes,
        long maximumAggregateResponseBytes,
        TimeSpan timeout,
        TimeProvider timeProvider) => CreateBudget(
            maximumRequests,
            maximumResponseBytes,
            maximumAggregateResponseBytes,
            timeout,
            timeProvider);

    internal static ReviewedTreeReader Reader(
        IReviewedGitObjectTransportFactory transportFactory,
        TimeProvider timeProvider) => CreateReader(
            transportFactory,
            timeProvider);

    internal static ReviewedGitObjectTransport Transport(
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        ActionHostGitHubToken token,
        ReviewedContentBudget budget,
        HttpMessageHandler handler)
    {
        if (!ReviewedGitObjectTransport.TryAuthorizedSource(
                invocation,
                out var repositoryName,
                out var headSha))
        {
            throw new InvalidOperationException(
                "The test invocation is not an authorized source.");
        }

        var shared = ActionHostGitObjectTransport.CreateForTesting(
            token.ExportForPrivateLaunch(),
            handler);
        return CreateTransport(
            repositoryName,
            headSha,
            budget,
            shared);
    }

    internal static ReviewedBlobStagingLease Staging(
        string parent,
        ReviewedContentBudget budget)
    {
        var authority = typeof(ReviewedTreeReader).GetField(
            "MintAuthority",
            BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) ??
            throw new InvalidOperationException(
                "Reviewed-tree mint authority was not found.");
        return ReviewedBlobStagingLease.TryCreate(
                authority,
                parent,
                budget) ??
            throw new InvalidOperationException(
                "Test staging lease could not be created.");
    }

    internal static string StagedPath(ReviewedStagedBlob blob) =>
        typeof(ReviewedStagedBlob).GetField(
            "_path",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(blob)
            as string ??
        throw new InvalidOperationException(
            "The staged blob path was not found.");

    internal static FileStream StagedStream(ReviewedStagedBlob blob) =>
        typeof(ReviewedStagedBlob).GetField(
            "_source",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(blob)
            as FileStream ??
        throw new InvalidOperationException(
            "The staged blob source was not found.");

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern ReviewedContentBudget CreateBudget(
        int maximumRequests,
        long maximumResponseBytes,
        long maximumAggregateResponseBytes,
        TimeSpan timeout,
        TimeProvider timeProvider);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern ReviewedTreeReader CreateReader(
        IReviewedGitObjectTransportFactory transportFactory,
        TimeProvider timeProvider);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern ReviewedGitObjectTransport CreateTransport(
        string repositoryName,
        string headSha,
        ReviewedContentBudget budget,
        IActionHostGitObjectTransport sharedTransport);
}
