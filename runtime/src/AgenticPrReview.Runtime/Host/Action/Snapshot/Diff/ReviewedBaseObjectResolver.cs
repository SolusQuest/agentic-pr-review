using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Snapshot.ChangedFiles;

namespace AgenticPrReview.Runtime.ActionHost.Snapshot.Diff;

internal enum ReviewedBaseOperandKind
{
    Regular = 1,
    Symlink,
    Submodule,
    Missing,
}

internal sealed record ReviewedBaseOperand(
    string Path,
    ReviewedBaseOperandKind Kind,
    string? Sha,
    long Size,
    ReviewedBaseStagedBlob? Blob);

internal sealed class ReviewedBaseObjectResolver
{
    private readonly IReviewedSnapshotTransport _transport;
    private readonly ReviewedBaseBlobStagingLease _staging;
    private readonly Dictionary<string, ActionHostGitTreeObject> _trees =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReviewedBaseStagedBlob> _blobs =
        new(StringComparer.Ordinal);
    private readonly ReviewedBaseLogicalByteMeter _logicalMeter = new();
    private string? _rootTreeSha;

    internal ReviewedBaseObjectResolver(
        IReviewedSnapshotTransport transport,
        ReviewedBaseBlobStagingLease staging)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _staging = staging ?? throw new ArgumentNullException(nameof(staging));
    }

    internal long LogicalBytes => _logicalMeter.Bytes;

    internal async Task<ReviewedSnapshotReadFailure> InitializeAsync(
        CancellationToken cancellationToken)
    {
        var commit = await _transport.GetBaseCommitAsync(cancellationToken);
        if (commit.Value is null)
        {
            return commit.Failure;
        }

        _rootTreeSha = commit.Value.TreeSha;
        return ReviewedSnapshotReadFailure.None;
    }

    internal async Task<ReviewedSnapshotReadResult<ReviewedBaseOperand>>
        ResolveAsync(
            string path,
            CancellationToken cancellationToken)
    {
        if (_rootTreeSha is null || !ReviewedTreePath.IsValid(path))
        {
            return ReviewedSnapshotReadResult<ReviewedBaseOperand>.Failed(
                ReviewedSnapshotReadFailure.InvalidRequest);
        }

        var segments = path.Split('/');
        var treeSha = _rootTreeSha;
        ActionHostGitTreeEntryObject? selected = null;
        for (var index = 0; index < segments.Length; index++)
        {
            var treeResult = await GetTreeAsync(treeSha, cancellationToken);
            if (treeResult.Value is null)
            {
                return ReviewedSnapshotReadResult<ReviewedBaseOperand>.Failed(
                    treeResult.Failure);
            }

            selected = treeResult.Value.Entries.SingleOrDefault(entry =>
                StringComparer.Ordinal.Equals(entry.Path, segments[index]));
            if (selected is null)
            {
                return ReviewedSnapshotReadResult<ReviewedBaseOperand>.Success(
                    new(path, ReviewedBaseOperandKind.Missing, null, 0, null));
            }

            if (index < segments.Length - 1)
            {
                if (selected.Mode != "040000" || selected.Type != "tree")
                {
                    return ReviewedSnapshotReadResult<ReviewedBaseOperand>.Success(
                        new(path, ReviewedBaseOperandKind.Missing, null, 0, null));
                }

                treeSha = selected.Sha;
            }
        }

        if (selected is null)
        {
            return ReviewedSnapshotReadResult<ReviewedBaseOperand>.Success(
                new(path, ReviewedBaseOperandKind.Missing, null, 0, null));
        }

        var size = selected.Size ?? 0;
        if (!_logicalMeter.TryAdd(size))
        {
            return ReviewedSnapshotReadResult<ReviewedBaseOperand>.Failed(
                ReviewedSnapshotReadFailure.UnsupportedSize);
        }

        if (selected.Mode == "120000" && selected.Type == "blob")
        {
            return ReviewedSnapshotReadResult<ReviewedBaseOperand>.Success(
                new(
                    path,
                    ReviewedBaseOperandKind.Symlink,
                    selected.Sha,
                    size,
                    null));
        }

        if (selected.Mode == "160000" && selected.Type == "commit")
        {
            return ReviewedSnapshotReadResult<ReviewedBaseOperand>.Success(
                new(
                    path,
                    ReviewedBaseOperandKind.Submodule,
                    selected.Sha,
                    size,
                    null));
        }

        if (selected.Mode is not ("100644" or "100755") ||
            selected.Type != "blob" ||
            selected.Size is null ||
            selected.Size < 0)
        {
            return ReviewedSnapshotReadResult<ReviewedBaseOperand>.Failed(
                ReviewedSnapshotReadFailure.InvalidResponse);
        }

        if (selected.Size > ReviewedContentLimits.BaseBlobBytes)
        {
            return ReviewedSnapshotReadResult<ReviewedBaseOperand>.Failed(
                ReviewedSnapshotReadFailure.UnsupportedSize);
        }

        if (!_blobs.TryGetValue(selected.Sha, out var blob))
        {
            var staged = await _transport.StageBaseBlobAsync(
                selected.Sha,
                selected.Size.Value,
                _staging,
                cancellationToken);
            if (staged.Value is null)
            {
                return staged.Failure == ReviewedSnapshotReadFailure.NotFound
                    ? ReviewedSnapshotReadResult<ReviewedBaseOperand>.Success(
                        new(
                            path,
                            ReviewedBaseOperandKind.Missing,
                            selected.Sha,
                            selected.Size.Value,
                            null))
                    : ReviewedSnapshotReadResult<ReviewedBaseOperand>.Failed(
                        staged.Failure);
            }

            blob = staged.Value;
            _blobs.Add(selected.Sha, blob);
        }

        return ReviewedSnapshotReadResult<ReviewedBaseOperand>.Success(
            new(
                path,
                ReviewedBaseOperandKind.Regular,
                selected.Sha,
                selected.Size.Value,
                blob));
    }

    private async Task<ReviewedSnapshotReadResult<ActionHostGitTreeObject>>
        GetTreeAsync(
            string sha,
            CancellationToken cancellationToken)
    {
        if (_trees.TryGetValue(sha, out var cached))
        {
            return ReviewedSnapshotReadResult<ActionHostGitTreeObject>.Success(
                cached);
        }

        var result = await _transport.GetTreeAsync(sha, cancellationToken);
        if (result.Value is not null)
        {
            _trees.Add(sha, result.Value);
        }

        return result;
    }

}

internal sealed class ReviewedBaseLogicalByteMeter
{
    internal long Bytes { get; private set; }

    internal bool TryAdd(long size)
    {
        if (size < 0 ||
            Bytes > ReviewedContentLimits.AggregateBaseBlobBytes - size)
        {
            return false;
        }

        Bytes += size;
        return true;
    }
}
