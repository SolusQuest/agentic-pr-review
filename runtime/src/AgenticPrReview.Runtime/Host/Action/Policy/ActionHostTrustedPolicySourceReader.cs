using AgenticPrReview.Runtime.ActionHost.GitHub;

namespace AgenticPrReview.Runtime.ActionHost.Policy;

internal sealed class ActionHostTrustedPolicySourceReader
{
    private const int MaximumPolicyFiles = 2;
    private const int MaximumRequests = 1 +
        MaximumPolicyFiles *
        (ActionHostTrustedPolicyPath.MaximumSegmentsByUtf8Limit + 1);
    private const int MaximumAggregateResponseBytes = 8 * 1024 * 1024;
    private const int MaximumTreeEntries = 10_000;
    private readonly ActionHostTrustedPolicyRequest _request;
    private readonly IActionHostGitObjectTransport _transport;
    private int _requests;
    private int _aggregateResponseBytes;
    private string? _rootTreeSha;

    internal ActionHostTrustedPolicySourceReader(
        ActionHostTrustedPolicyRequest request,
        IActionHostGitObjectTransport transport)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transport);
        _request = request;
        _transport = transport;
    }

    internal async Task<SourceReadResult> ReadRegularBlobAsync(
        ActionHostTrustedPolicyPath path,
        ActionHostGitBlobReadBudget budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(budget);
        var initialized = await EnsureRootAsync(cancellationToken);
        if (initialized != ActionHostTrustedPolicyFailure.None)
        {
            return SourceReadResult.Failed(initialized);
        }

        var treeSha = _rootTreeSha!;
        for (var index = 0; index < path.Segments.Length; index++)
        {
            if (!TryBeginRequest())
            {
                return SourceReadResult.Failed(
                    ActionHostTrustedPolicyFailure.RequestLimit);
            }

            var tree = await _transport.GetTreeObjectAsync(
                _request.RepositoryName,
                treeSha,
                cancellationToken);
            var failure = Consume(tree.CapturedResponseBytes, tree.Failure);
            if (failure != ActionHostTrustedPolicyFailure.None)
            {
                return SourceReadResult.Failed(failure);
            }

            if (tree.Value is not { } treeValue ||
                !StringComparer.Ordinal.Equals(treeValue.Sha, treeSha))
            {
                return SourceReadResult.Failed(
                    ActionHostTrustedPolicyFailure.SourceIdentityMismatch);
            }

            if (treeValue.Entries.Count > MaximumTreeEntries)
            {
                return SourceReadResult.Failed(
                    ActionHostTrustedPolicyFailure.SourceIncomplete);
            }

            var matches = treeValue.Entries.Where(entry =>
                StringComparer.Ordinal.Equals(
                    entry.Path,
                    path.Segments[index])).ToArray();
            if (matches.Length == 0)
            {
                return SourceReadResult.Failed(
                    ActionHostTrustedPolicyFailure.SourceMissing);
            }

            if (matches.Length != 1)
            {
                return SourceReadResult.Failed(
                    ActionHostTrustedPolicyFailure.SourceIncomplete);
            }

            var entry = matches[0];
            if (index < path.Segments.Length - 1)
            {
                if (!StringComparer.Ordinal.Equals(entry.Mode, "040000") ||
                    !StringComparer.Ordinal.Equals(entry.Type, "tree"))
                {
                    return SourceReadResult.Failed(
                        ActionHostTrustedPolicyFailure.SourceNonRegular);
                }

                treeSha = entry.Sha;
                continue;
            }

            if (!(StringComparer.Ordinal.Equals(entry.Mode, "100644") ||
                    StringComparer.Ordinal.Equals(entry.Mode, "100755")) ||
                !StringComparer.Ordinal.Equals(entry.Type, "blob"))
            {
                return SourceReadResult.Failed(
                    ActionHostTrustedPolicyFailure.SourceNonRegular);
            }

            return await ReadBlobAsync(entry.Sha, budget, cancellationToken);
        }

        return SourceReadResult.Failed(
            ActionHostTrustedPolicyFailure.InternalInvariant);
    }

    private async Task<ActionHostTrustedPolicyFailure> EnsureRootAsync(
        CancellationToken cancellationToken)
    {
        if (_rootTreeSha is not null)
        {
            return ActionHostTrustedPolicyFailure.None;
        }

        if (!TryBeginRequest())
        {
            return ActionHostTrustedPolicyFailure.RequestLimit;
        }

        var commit = await _transport.GetCommitObjectAsync(
            _request.RepositoryName,
            _request.WorkflowCommitSha,
            cancellationToken);
        var failure = Consume(
            commit.CapturedResponseBytes,
            commit.Failure);
        if (failure != ActionHostTrustedPolicyFailure.None)
        {
            return failure;
        }

        if (commit.Value is not { } value ||
            !StringComparer.Ordinal.Equals(
                value.Sha,
                _request.WorkflowCommitSha))
        {
            return ActionHostTrustedPolicyFailure.SourceIdentityMismatch;
        }

        _rootTreeSha = value.TreeSha;
        return ActionHostTrustedPolicyFailure.None;
    }

    private async Task<SourceReadResult> ReadBlobAsync(
        string blobSha,
        ActionHostGitBlobReadBudget budget,
        CancellationToken cancellationToken)
    {
        if (!TryBeginRequest())
        {
            return SourceReadResult.Failed(
                ActionHostTrustedPolicyFailure.RequestLimit);
        }

        var blob = await _transport.GetBlobObjectAsync(
            _request.RepositoryName,
            blobSha,
            budget,
            cancellationToken);
        var failure = Consume(blob.CapturedResponseBytes, blob.Failure);
        if (failure != ActionHostTrustedPolicyFailure.None)
        {
            return SourceReadResult.Failed(failure);
        }

        if (blob.Value is not { } value ||
            !StringComparer.Ordinal.Equals(value.Sha, blobSha))
        {
            return SourceReadResult.Failed(
                ActionHostTrustedPolicyFailure.SourceIdentityMismatch);
        }

        if (value.Bytes.Length > budget.MaximumDecodedBytes)
        {
            return SourceReadResult.Failed(
                ActionHostTrustedPolicyFailure.SourceIncomplete);
        }

        return SourceReadResult.Success(
            new SourceBlob(blobSha, (byte[])value.Bytes.Clone()));
    }

    private bool TryBeginRequest()
    {
        try
        {
            _requests = checked(_requests + 1);
            return _requests <= MaximumRequests;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private ActionHostTrustedPolicyFailure Consume(
        int capturedResponseBytes,
        ActionHostGitObjectFailure failure)
    {
        try
        {
            _aggregateResponseBytes = checked(
                _aggregateResponseBytes + capturedResponseBytes);
        }
        catch (OverflowException)
        {
            return ActionHostTrustedPolicyFailure.AggregateLimit;
        }

        if (_aggregateResponseBytes > MaximumAggregateResponseBytes)
        {
            return ActionHostTrustedPolicyFailure.AggregateLimit;
        }

        return failure switch
        {
            ActionHostGitObjectFailure.None =>
                ActionHostTrustedPolicyFailure.None,
            ActionHostGitObjectFailure.NotFound =>
                ActionHostTrustedPolicyFailure.SourceMissing,
            ActionHostGitObjectFailure.Unauthorized or
            ActionHostGitObjectFailure.Forbidden =>
                ActionHostTrustedPolicyFailure.CredentialDenied,
            ActionHostGitObjectFailure.InvalidRequest or
            ActionHostGitObjectFailure.InvalidResponse or
            ActionHostGitObjectFailure.ResponseTooLarge =>
                ActionHostTrustedPolicyFailure.SourceIncomplete,
            ActionHostGitObjectFailure.RateLimited or
            ActionHostGitObjectFailure.UpstreamFailure or
            ActionHostGitObjectFailure.TransportFailure =>
                ActionHostTrustedPolicyFailure.TransportFailure,
            _ => ActionHostTrustedPolicyFailure.InternalInvariant,
        };
    }

    internal sealed record SourceBlob(string BlobSha, byte[] Bytes);

    internal sealed class SourceReadResult
    {
        private SourceReadResult(
            SourceBlob? blob,
            ActionHostTrustedPolicyFailure failure)
        {
            Blob = blob;
            Failure = failure;
        }

        internal SourceBlob? Blob { get; }
        internal ActionHostTrustedPolicyFailure Failure { get; }

        internal static SourceReadResult Success(SourceBlob blob) =>
            new(blob, ActionHostTrustedPolicyFailure.None);

        internal static SourceReadResult Failed(
            ActionHostTrustedPolicyFailure failure) => new(null, failure);
    }
}
