using System.Globalization;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Snapshot.ChangedFiles;
using AgenticPrReview.Runtime.ActionHost.Snapshot.Diff;
using AgenticPrReview.Runtime.ActionHost.Snapshot.Materialization;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.ActionHost.Snapshot;

internal sealed record ReviewedSnapshotIdentities(
    long RepositoryId,
    long PullRequestNumber,
    string BaseSha,
    string HeadSha,
    string ReviewedTreeSha256,
    string ChangedFilesSha256,
    string DiffSha256,
    string MaterializationSha256);

internal sealed class BoundedReviewedSnapshotLease : IAsyncDisposable
{
    private readonly ReviewedRootLease _root;

    internal BoundedReviewedSnapshotLease(
        ReviewedSnapshot snapshot,
        ReviewedSnapshotIdentities identities,
        ReviewedRootLease root)
    {
        Snapshot = snapshot;
        Identities = identities;
        _root = root;
    }

    internal ReviewedSnapshot Snapshot { get; }

    internal ReviewedSnapshotIdentities Identities { get; }

    internal bool CleanupIncomplete => _root.CleanupIncomplete;

    public ValueTask DisposeAsync() => _root.DisposeAsync();
}

internal sealed class BoundedReviewedSnapshotResult
{
    private BoundedReviewedSnapshotResult(
        BoundedReviewedSnapshotLease? lease,
        ReviewedSnapshotReadFailure failure,
        bool cleanupIncomplete)
    {
        Lease = lease;
        Failure = failure;
        CleanupIncomplete = cleanupIncomplete;
    }

    internal BoundedReviewedSnapshotLease? Lease { get; }

    internal ReviewedSnapshotReadFailure Failure { get; }

    internal bool CleanupIncomplete { get; }

    internal static BoundedReviewedSnapshotResult Success(
        BoundedReviewedSnapshotLease lease) => new(
            lease,
            ReviewedSnapshotReadFailure.None,
            false);

    internal static BoundedReviewedSnapshotResult Failed(
        ReviewedSnapshotReadFailure failure,
        bool cleanupIncomplete = false) => new(
            null,
            failure,
            cleanupIncomplete);
}

internal sealed class BoundedReviewedSnapshotBuilder
{
    private readonly IReviewedSnapshotTransportFactory _transportFactory;

    internal BoundedReviewedSnapshotBuilder(
        IActionHostReviewedSnapshotTransportFactory transportFactory)
        : this(new ReviewedSnapshotTransportFactory(transportFactory))
    {
    }

    internal BoundedReviewedSnapshotBuilder(
        IReviewedSnapshotTransportFactory transportFactory)
    {
        _transportFactory = transportFactory ??
            throw new ArgumentNullException(nameof(transportFactory));
    }

    internal async Task<BoundedReviewedSnapshotResult> BuildAsync(
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        ActionHostGitHubToken token,
        ReviewedTreeSnapshot tree,
        string absoluteStagingParent,
        CancellationToken cancellationToken,
        ReviewedRootMaterializationHooks? rootHooks = null,
        long maximumChangedMetadataBytes =
            AgentLimits.ChangedFilesMetadataBytes,
        Action? beforeFinalAdmission = null)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(tree);

        ReviewedRootLease? root = null;
        try
        {
            var changed = await new ReviewedChangedFileReader(
                    _transportFactory)
                .ReadAsync(invocation, token, tree, cancellationToken);
            if (changed.Value is null)
            {
                return BoundedReviewedSnapshotResult.Failed(changed.Failure);
            }

            using var baseStaging = ReviewedBaseBlobStagingLease.TryCreate(
                absoluteStagingParent,
                tree.Budget);
            if (baseStaging is null)
            {
                return BoundedReviewedSnapshotResult.Failed(
                    ReviewedSnapshotReadFailure.StagingFailure);
            }

            using var transport = _transportFactory.Create(
                invocation,
                token,
                tree.Budget);
            var baseResolver = new ReviewedBaseObjectResolver(
                transport,
                baseStaging);
            var initialized = await baseResolver.InitializeAsync(
                cancellationToken);
            if (initialized != ReviewedSnapshotReadFailure.None)
            {
                return BoundedReviewedSnapshotResult.Failed(initialized);
            }

            var frozen = invocation.PullRequest;
            var reviewedIdentity = new ReviewedIdentity(
                frozen.RepositoryId.ToString(CultureInfo.InvariantCulture),
                frozen.Number,
                frozen.BaseSha,
                frozen.HeadSha);
            var built = await new ReviewedExactDiffBuilder(tree.Budget)
                .BuildAsync(
                    reviewedIdentity,
                    changed.Value,
                    tree,
                    baseResolver,
                    cancellationToken);
            if (built.Value is null)
            {
                return BoundedReviewedSnapshotResult.Failed(built.Failure);
            }

            if (!tree.Budget.TryContinue(cancellationToken) ||
                !ChangedMetadataFits(
                    built.Value.Changes,
                    maximumChangedMetadataBytes))
            {
                return BoundedReviewedSnapshotResult.Failed(
                    ReviewedSnapshotReadFailure.UnsupportedSize);
            }

            var materialized = await ReviewedRootMaterializer.MaterializeAsync(
                tree,
                absoluteStagingParent,
                cancellationToken,
                rootHooks);
            if (materialized.Lease is null)
            {
                return BoundedReviewedSnapshotResult.Failed(
                    Map(materialized.Failure),
                    materialized.CleanupIncomplete);
            }

            root = materialized.Lease;
            if (!tree.Budget.TryContinue(cancellationToken))
            {
                await root.DisposeAsync();
                var cleanupIncomplete = root.CleanupIncomplete;
                root = null;
                return BoundedReviewedSnapshotResult.Failed(
                    ReviewedSnapshotReadFailure.UnsupportedSize,
                    cleanupIncomplete);
            }

            var changes = built.Value.Changes;
            ReviewedSnapshot agentSnapshot;
            try
            {
                agentSnapshot = new ReviewedSnapshot(
                    reviewedIdentity,
                    root.AbsoluteRoot,
                    root.RegularPaths,
                    tree.Records.Select(static record => record.Path),
                    changes.Select(static item => item.Change),
                    changes
                        .Where(static item => item.Source is not null)
                        .Select(static item => item.Source!));
            }
            catch (ArgumentException)
            {
                await root.DisposeAsync();
                var cleanupIncomplete = root.CleanupIncomplete;
                root = null;
                return BoundedReviewedSnapshotResult.Failed(
                    ReviewedSnapshotReadFailure.IdentityMismatch,
                    cleanupIncomplete);
            }

            var identities = new ReviewedSnapshotIdentities(
                frozen.RepositoryId,
                frozen.Number,
                frozen.BaseSha,
                frozen.HeadSha,
                tree.Identity.Sha256,
                built.Value.ChangedFileIdentity.Sha256,
                built.Value.Identity.Sha256,
                root.Identity.Sha256);
            beforeFinalAdmission?.Invoke();
            if (!tree.Budget.TryContinue(cancellationToken))
            {
                await root.DisposeAsync();
                var cleanupIncomplete = root.CleanupIncomplete;
                root = null;
                return BoundedReviewedSnapshotResult.Failed(
                    ReviewedSnapshotReadFailure.UnsupportedSize,
                    cleanupIncomplete);
            }

            var lease = new BoundedReviewedSnapshotLease(
                agentSnapshot,
                identities,
                root);
            root = null;
            return BoundedReviewedSnapshotResult.Success(lease);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            if (root is not null)
            {
                await root.DisposeAsync();
            }

            return BoundedReviewedSnapshotResult.Failed(
                ReviewedSnapshotReadFailure.Cancelled,
                root?.CleanupIncomplete ?? false);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            if (root is not null)
            {
                await root.DisposeAsync();
            }

            return BoundedReviewedSnapshotResult.Failed(
                ReviewedSnapshotReadFailure.StagingFailure,
                root?.CleanupIncomplete ?? false);
        }
        finally
        {
            await tree.DisposeAsync();
        }
    }

    private static ReviewedSnapshotReadFailure Map(
        ReviewedRootFailure failure) => failure switch
        {
            ReviewedRootFailure.UnsupportedSize =>
                ReviewedSnapshotReadFailure.UnsupportedSize,
            ReviewedRootFailure.IdentityMismatch =>
                ReviewedSnapshotReadFailure.IdentityMismatch,
            ReviewedRootFailure.Cancelled =>
                ReviewedSnapshotReadFailure.Cancelled,
            _ => ReviewedSnapshotReadFailure.StagingFailure,
        };

    private static bool ChangedMetadataFits(
        IEnumerable<ReviewedBuiltChange> changes,
        long maximumBytes)
    {
        if (maximumBytes < 0 ||
            maximumBytes > AgentLimits.ChangedFilesMetadataBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        long bytes = 0;
        foreach (var change in changes)
        {
            var length = ReviewedChangedFileWriter.Write(change.Change).Length;
            if (bytes > maximumBytes - length)
            {
                return false;
            }

            bytes += length;
        }

        return true;
    }
}
