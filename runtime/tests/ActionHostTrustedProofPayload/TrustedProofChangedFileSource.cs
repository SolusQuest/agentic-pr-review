using System.Collections.Immutable;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.ActionHost.Snapshot.ChangedFiles;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

internal sealed class TrustedProofChangedFileSourceFactory :
    IReviewedChangedFileSourceFactory
{
    internal static TrustedProofChangedFileSourceFactory Instance { get; } =
        new();

    private TrustedProofChangedFileSourceFactory() { }

    public IReviewedChangedFileSource Create(
        IReviewedSnapshotTransportFactory transportFactory) =>
        new TrustedProofChangedFileSource(transportFactory);
}

internal sealed class TrustedProofChangedFileSource :
    IReviewedChangedFileSource
{
    internal const string CanaryPath = "proof/apr178-path-canary.txt";
    internal const string CanaryMode = "100644";
    internal const string CanaryBlobSha =
        "6fb1e09fc322bc85611172c171f4e3fce8bdee1c";
    private static readonly byte[] CanaryBytes =
        "APR178_TOOL_DATA_CANARY\n"u8.ToArray();

    private readonly IReviewedSnapshotTransportFactory transportFactory;

    internal TrustedProofChangedFileSource(
        IReviewedSnapshotTransportFactory transportFactory) =>
        this.transportFactory = transportFactory ??
            throw new ArgumentNullException(nameof(transportFactory));

    public async Task<ReviewedSnapshotReadResult<ReviewedChangedFileSet>>
        ReadAsync(
            ActionHostAuthorizer.AuthorizedInvocation invocation,
            ActionHostGitHubToken token,
            ReviewedTreeSnapshot tree,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(tree);
        var frozen = invocation.PullRequest;
        if (tree.RepositoryId != frozen.RepositoryId ||
            tree.PullRequestNumber != frozen.Number ||
            !StringComparer.Ordinal.Equals(tree.HeadSha, frozen.HeadSha) ||
            tree.ParentShas.IsDefault ||
            tree.ParentShas.Length != 2 ||
            !StringComparer.Ordinal.Equals(
                tree.ParentShas[0],
                frozen.BaseSha))
        {
            return Failed();
        }

        using var transport = transportFactory.Create(
            invocation,
            token,
            tree.Budget);
        var opening = await transport.GetCurrentPullRequestAsync(
            cancellationToken);
        if (opening.Value is null)
        {
            return ReviewedSnapshotReadResult<ReviewedChangedFileSet>.Failed(
                opening.Failure);
        }

        if (!MatchesFrozen(opening.Value, frozen))
        {
            return Failed();
        }

        var canary = tree.Records.SingleOrDefault(record =>
            StringComparer.Ordinal.Equals(record.Path, CanaryPath));
        if (canary is null ||
            canary.Kind != ReviewedTreeEntryKind.Regular ||
            !StringComparer.Ordinal.Equals(canary.Mode, CanaryMode) ||
            !StringComparer.Ordinal.Equals(
                canary.ObjectSha,
                CanaryBlobSha) ||
            canary.Size != CanaryBytes.LongLength ||
            canary.StagedBlob is null)
        {
            return Failed();
        }

        using (var destination = new MemoryStream(CanaryBytes.Length))
        {
            var copied = await canary.StagedBlob.CopyVerifiedDetailedAsync(
                destination,
                cancellationToken);
            if (copied != ReviewedStagedBlobCopyFailure.None ||
                !destination.ToArray().AsSpan().SequenceEqual(CanaryBytes))
            {
                return Failed();
            }
        }

        var closing = await transport.GetCurrentPullRequestAsync(
            cancellationToken);
        if (closing.Value is null)
        {
            return ReviewedSnapshotReadResult<ReviewedChangedFileSet>.Failed(
                closing.Failure);
        }

        if (!MatchesFrozen(closing.Value, frozen))
        {
            return Failed();
        }

        var files = ImmutableArray.Create(new ReviewedPullRequestFileFact(
            CanaryBlobSha,
            CanaryPath,
            null,
            "added",
            1,
            0,
            1,
            null,
            false));
        return ReviewedSnapshotReadResult<ReviewedChangedFileSet>.Success(
            new(
                files,
                ReviewedChangedFileIdentityWriter.Write(files),
                requireAddedBaseAbsence: true));
    }

    private static ReviewedSnapshotReadResult<ReviewedChangedFileSet> Failed() =>
        ReviewedSnapshotReadResult<ReviewedChangedFileSet>.Failed(
            ReviewedSnapshotReadFailure.IdentityMismatch);

    private static bool MatchesFrozen(
        ActionHostGitHubPullRequestFact current,
        ActionHostAuthorizer.FrozenPullRequest frozen) =>
        current.Number == frozen.Number &&
        StringComparer.Ordinal.Equals(current.State, "open") &&
        !current.Draft &&
        current.MergedAt is null &&
        current.BaseRepository.Id == frozen.BaseRepositoryId &&
        StringComparer.Ordinal.Equals(
            current.BaseRepository.FullName,
            frozen.BaseRepositoryName) &&
        current.HeadRepository.Id == frozen.HeadRepositoryId &&
        StringComparer.Ordinal.Equals(
            current.HeadRepository.FullName,
            frozen.HeadRepositoryName) &&
        StringComparer.Ordinal.Equals(
            current.BaseSha,
            frozen.ReportedBaseSha) &&
        StringComparer.Ordinal.Equals(current.HeadSha, frozen.HeadSha);
}
