using System.Collections.Immutable;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;

namespace AgenticPrReview.Runtime.ActionHost.Snapshot.GitObjects;

internal sealed class ReviewedTreeReader
{
    private static readonly object MintAuthority = new();

    private readonly IReviewedGitObjectTransportFactory _transportFactory;
    private readonly TimeProvider _timeProvider;

    internal ReviewedTreeReader()
        : this(
            new ReviewedGitObjectTransportFactory(
                new ActionHostGitHubAuthorizationTransportFactory()),
            TimeProvider.System)
    {
    }

    internal ReviewedTreeReader(
        IReviewedGitObjectTransportFactory transportFactory,
        TimeProvider timeProvider)
    {
        _transportFactory = transportFactory ??
            throw new ArgumentNullException(nameof(transportFactory));
        _timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));
    }

    internal static bool HasMintAuthority(object authority) =>
        ReferenceEquals(authority, MintAuthority);

    internal async Task<ReviewedTreeMaterializationResult> MaterializeAsync(
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        ActionHostGitHubToken token,
        string stagingParent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(token);
        if (cancellationToken.IsCancellationRequested)
        {
            return ReviewedTreeMaterializationResult.Failed(
                ReviewedTreeFailure.Cancelled);
        }

        if (!ReviewedGitObjectTransport.TryAuthorizedSource(
                invocation,
                out _,
                out _))
        {
            return ReviewedTreeMaterializationResult.Failed(
                ReviewedTreeFailure.InvalidGraph);
        }

        var budget = ReviewedContentBudget.Mint(
            MintAuthority,
            _timeProvider);
        var staging = ReviewedBlobStagingLease.TryCreate(
            MintAuthority,
            stagingParent,
            budget);
        if (staging is null)
        {
            budget.Invalidate();
            return ReviewedTreeMaterializationResult.Failed(
                ReviewedTreeFailure.InternalFailure);
        }

        try
        {
            using var transport = _transportFactory.Create(
                invocation,
                token,
                budget);
            var core = await MaterializeCoreAsync(
                invocation,
                transport,
                budget,
                staging,
                cancellationToken);
            if (core.Snapshot is not null)
            {
                return ReviewedTreeMaterializationResult.Success(
                    core.Snapshot);
            }

            return Fail(budget, staging, core.Failure);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return Fail(budget, staging, ReviewedTreeFailure.Cancelled);
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            return Fail(budget, staging, ReviewedTreeFailure.InternalFailure);
        }
    }

    private static async Task<CoreResult> MaterializeCoreAsync(
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        IReviewedGitObjectTransport transport,
        ReviewedContentBudget budget,
        ReviewedBlobStagingLease staging,
        CancellationToken cancellationToken)
    {
        var pullRequest = invocation.PullRequest;
        var commitResult = await transport.GetCommitAsync(cancellationToken);
        if (commitResult.Value is null)
        {
            return CoreResult.Failed(MapFailure(commitResult.Failure));
        }

        var commit = commitResult.Value;
        var meter = new ReviewedTreeTraversalMeter();
        var claims = new Dictionary<string, ObjectClaim>(StringComparer.Ordinal)
        {
            [commit.Sha] = new(ObjectKind.Commit, null),
        };
        var rootClaim = TryClaim(
                claims,
                meter,
                commit.TreeSha,
                ObjectKind.Tree,
                null,
                countTowardLimit: true);
        if (rootClaim != AdmissionResult.Success)
        {
            return CoreResult.Failed(
                AdmissionFailure(rootClaim));
        }

        var treeCache = new Dictionary<
            string,
            ImmutableArray<ReviewedGitTreeEntryFact>>(
            StringComparer.Ordinal);
        var leaves = new HashSet<string>(StringComparer.Ordinal);
        var drafts = new List<EntryDraft>();
        var rootTreeResult = await transport.GetTreeAsync(
            commit.TreeSha,
            cancellationToken);
        if (rootTreeResult.Value is null)
        {
            return CoreResult.Failed(MapFailure(rootTreeResult.Failure));
        }

        var rootEntries = OrderedEntries(rootTreeResult.Value.Entries);
        if (HasDuplicateEntryPaths(rootEntries) ||
            HasConflictingObjectKinds(rootEntries, claims))
        {
            return CoreResult.Failed(ReviewedTreeFailure.InvalidGraph);
        }

        treeCache.Add(commit.TreeSha, rootEntries);
        var frames = new Stack<TreeFrame>();
        frames.Push(new TreeFrame(
            commit.TreeSha,
            string.Empty,
            0,
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                commit.TreeSha),
            rootEntries));

        while (frames.Count > 0)
        {
            if (!budget.TryContinue(cancellationToken))
            {
                return CoreResult.Failed(
                    ReviewedTreeFailure.UnsupportedSize);
            }

            var frame = frames.Peek();
            if (frame.Index >= frame.Entries.Length)
            {
                frames.Pop();
                continue;
            }

            var entry = frame.Entries[frame.Index++];
            if (StringComparer.Ordinal.Equals(
                    frame.PreviousEntryPath,
                    entry.Path))
            {
                return CoreResult.Failed(ReviewedTreeFailure.InvalidGraph);
            }

            frame.PreviousEntryPath = entry.Path;
            var entryDepth = frame.Depth + 1;
            if (entryDepth > ReviewedContentLimits.TreeDepth)
            {
                return CoreResult.Failed(
                    ReviewedTreeFailure.UnsupportedSize);
            }

            if (!ReviewedTreePath.TryAppend(
                    frame.Prefix,
                    entry.Path,
                    out var path,
                    out var pathBytes))
            {
                return CoreResult.Failed(
                    ReviewedTreeFailure.InvalidGraph);
            }

            if (!meter.TryAddLogicalEntry(pathBytes))
            {
                return CoreResult.Failed(
                    ReviewedTreeFailure.UnsupportedSize);
            }

            switch (EntryShape(entry))
            {
                case EntryShapeKind.Tree:
                    if (leaves.Contains(path) ||
                        frame.Ancestors.Contains(entry.Sha))
                    {
                        return CoreResult.Failed(
                            ReviewedTreeFailure.InvalidGraph);
                    }

                    var treeClaim = TryClaim(
                            claims,
                            meter,
                            entry.Sha,
                            ObjectKind.Tree,
                            null,
                            countTowardLimit: true);
                    if (treeClaim != AdmissionResult.Success)
                    {
                        return CoreResult.Failed(
                            AdmissionFailure(treeClaim));
                    }

                    if (!treeCache.TryGetValue(
                            entry.Sha,
                            out var childEntries))
                    {
                        var treeResult = await transport.GetTreeAsync(
                            entry.Sha,
                            cancellationToken);
                        if (treeResult.Value is null)
                        {
                            return CoreResult.Failed(
                                MapFailure(treeResult.Failure));
                        }

                        childEntries = OrderedEntries(
                            treeResult.Value.Entries);
                        if (HasDuplicateEntryPaths(childEntries) ||
                            HasConflictingObjectKinds(childEntries, claims))
                        {
                            return CoreResult.Failed(
                                ReviewedTreeFailure.InvalidGraph);
                        }

                        treeCache.Add(entry.Sha, childEntries);
                    }

                    frames.Push(new TreeFrame(
                        entry.Sha,
                        path,
                        entryDepth,
                        frame.Ancestors.Add(entry.Sha),
                        childEntries));
                    break;

                case EntryShapeKind.Regular:
                    if (entry.Size is not { } regularSize || regularSize < 0)
                    {
                        return CoreResult.Failed(
                            ReviewedTreeFailure.InvalidGraph);
                    }

                    if (regularSize > ReviewedContentLimits.HeadBlobBytes ||
                        !meter.TryAddLogicalHeadBlobBytes(regularSize))
                    {
                        return CoreResult.Failed(
                            ReviewedTreeFailure.UnsupportedSize);
                    }

                    var regularLeaf = TryAddLeaf(path, leaves, meter);
                    if (regularLeaf != AdmissionResult.Success)
                    {
                        return CoreResult.Failed(
                            AdmissionFailure(regularLeaf));
                    }

                    var regularClaim = TryClaim(
                            claims,
                            meter,
                            entry.Sha,
                            ObjectKind.Blob,
                            regularSize,
                            countTowardLimit: true);
                    if (regularClaim != AdmissionResult.Success)
                    {
                        return CoreResult.Failed(
                            AdmissionFailure(regularClaim));
                    }

                    drafts.Add(new EntryDraft(
                        path,
                        entry.Mode,
                        ReviewedTreeEntryKind.Regular,
                        entry.Sha,
                        regularSize));
                    break;

                case EntryShapeKind.Symlink:
                    if (entry.Size is < 0)
                    {
                        return CoreResult.Failed(
                            ReviewedTreeFailure.InvalidGraph);
                    }

                    var symlinkLeaf = TryAddLeaf(path, leaves, meter);
                    if (symlinkLeaf != AdmissionResult.Success)
                    {
                        return CoreResult.Failed(
                            AdmissionFailure(symlinkLeaf));
                    }

                    var symlinkClaim = TryClaim(
                            claims,
                            meter,
                            entry.Sha,
                            ObjectKind.Blob,
                            entry.Size,
                            countTowardLimit: true);
                    if (symlinkClaim != AdmissionResult.Success)
                    {
                        return CoreResult.Failed(
                            AdmissionFailure(symlinkClaim));
                    }

                    drafts.Add(new EntryDraft(
                        path,
                        entry.Mode,
                        ReviewedTreeEntryKind.Symlink,
                        entry.Sha,
                        null));
                    break;

                case EntryShapeKind.Submodule:
                    var submoduleLeaf = TryAddLeaf(path, leaves, meter);
                    if (submoduleLeaf != AdmissionResult.Success)
                    {
                        return CoreResult.Failed(
                            AdmissionFailure(submoduleLeaf));
                    }

                    var submoduleClaim = TryClaim(
                            claims,
                            meter,
                            entry.Sha,
                            ObjectKind.Commit,
                            null,
                            countTowardLimit: false);
                    if (submoduleClaim != AdmissionResult.Success)
                    {
                        return CoreResult.Failed(
                            AdmissionFailure(submoduleClaim));
                    }

                    drafts.Add(new EntryDraft(
                        path,
                        entry.Mode,
                        ReviewedTreeEntryKind.Submodule,
                        entry.Sha,
                        null));
                    break;

                default:
                    return CoreResult.Failed(
                        ReviewedTreeFailure.InvalidGraph);
            }
        }

        var stagedBySha = new Dictionary<string, ReviewedStagedBlob>(
            StringComparer.Ordinal);
        foreach (var regular in drafts
                     .Where(static item =>
                         item.Kind == ReviewedTreeEntryKind.Regular)
                     .GroupBy(static item => item.Sha, StringComparer.Ordinal)
                     .Select(static group => group.First())
                     .OrderBy(static item => item.Sha, StringComparer.Ordinal))
        {
            if (!budget.TryContinue(cancellationToken))
            {
                return CoreResult.Failed(
                    ReviewedTreeFailure.UnsupportedSize);
            }

            var blobResult = await transport.StageBlobAsync(
                regular.Sha,
                regular.Size!.Value,
                staging,
                cancellationToken);
            if (blobResult.Value is null)
            {
                return CoreResult.Failed(MapFailure(blobResult.Failure));
            }

            stagedBySha.Add(regular.Sha, blobResult.Value);
        }

        if (!budget.TryContinue(cancellationToken))
        {
            return CoreResult.Failed(ReviewedTreeFailure.UnsupportedSize);
        }

        var records = ImmutableArray.CreateBuilder<ReviewedTreePathRecord>(
            drafts.Count);
        foreach (var draft in drafts)
        {
            if (!budget.TryContinue(cancellationToken))
            {
                return CoreResult.Failed(
                    ReviewedTreeFailure.UnsupportedSize);
            }

            records.Add(ReviewedTreePathRecord.Mint(
                MintAuthority,
                draft.Path,
                draft.Mode,
                draft.Kind,
                draft.Sha,
                draft.Size,
                draft.Kind == ReviewedTreeEntryKind.Regular
                    ? stagedBySha[draft.Sha]
                    : null));
        }

        if (!budget.TryContinue(cancellationToken))
        {
            return CoreResult.Failed(ReviewedTreeFailure.UnsupportedSize);
        }

        var snapshot = ReviewedTreeSnapshot.Mint(
            MintAuthority,
            pullRequest.RepositoryId,
            pullRequest.Number,
            commit.Sha,
            commit.TreeSha,
            records.MoveToImmutable(),
            budget,
            staging);
        return CoreResult.Success(snapshot);
    }

    private static ImmutableArray<ReviewedGitTreeEntryFact> OrderedEntries(
        IReadOnlyList<ReviewedGitTreeEntryFact> entries) => entries
            .OrderBy(static item => item.Path, StringComparer.Ordinal)
            .ThenBy(static item => item.Mode, StringComparer.Ordinal)
            .ThenBy(static item => item.Type, StringComparer.Ordinal)
            .ThenBy(static item => item.Sha, StringComparer.Ordinal)
            .ToImmutableArray();

    private static bool HasDuplicateEntryPaths(
        ImmutableArray<ReviewedGitTreeEntryFact> entries)
    {
        for (var index = 1; index < entries.Length; index++)
        {
            if (StringComparer.Ordinal.Equals(
                    entries[index - 1].Path,
                    entries[index].Path))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasConflictingObjectKinds(
        ImmutableArray<ReviewedGitTreeEntryFact> entries,
        IReadOnlyDictionary<string, ObjectClaim> claims)
    {
        var localClaims = new Dictionary<string, ObjectKind>(
            StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var kind = EntryShape(entry) switch
            {
                EntryShapeKind.Tree => ObjectKind.Tree,
                EntryShapeKind.Regular or EntryShapeKind.Symlink =>
                    ObjectKind.Blob,
                EntryShapeKind.Submodule => ObjectKind.Commit,
                _ => (ObjectKind?)null,
            };
            if (kind is null)
            {
                continue;
            }

            if (claims.TryGetValue(entry.Sha, out var existing) &&
                existing.Kind != kind ||
                localClaims.TryGetValue(entry.Sha, out var local) &&
                local != kind)
            {
                return true;
            }

            localClaims.TryAdd(entry.Sha, kind.Value);
        }

        return false;
    }

    private static EntryShapeKind EntryShape(ReviewedGitTreeEntryFact entry)
    {
        if (!ReviewedGitObjectValidation.IsSha(entry.Sha))
        {
            return EntryShapeKind.Invalid;
        }

        return (entry.Mode, entry.Type, entry.Size) switch
        {
            ("040000", "tree", null) => EntryShapeKind.Tree,
            ("100644" or "100755", "blob", not null) =>
                EntryShapeKind.Regular,
            ("120000", "blob", _) => EntryShapeKind.Symlink,
            ("160000", "commit", null) => EntryShapeKind.Submodule,
            _ => EntryShapeKind.Invalid,
        };
    }

    private static AdmissionResult TryAddLeaf(
        string path,
        HashSet<string> leaves,
        ReviewedTreeTraversalMeter meter)
    {
        if (!leaves.Add(path))
        {
            return AdmissionResult.Invalid;
        }

        return meter.TryAddLeafPath()
            ? AdmissionResult.Success
            : AdmissionResult.UnsupportedSize;
    }

    private static AdmissionResult TryClaim(
        Dictionary<string, ObjectClaim> claims,
        ReviewedTreeTraversalMeter meter,
        string sha,
        ObjectKind kind,
        long? declaredSize,
        bool countTowardLimit)
    {
        if (!ReviewedGitObjectValidation.IsSha(sha) || declaredSize is < 0)
        {
            return AdmissionResult.Invalid;
        }

        if (claims.TryGetValue(sha, out var existing))
        {
            if (existing.Kind != kind)
            {
                return AdmissionResult.Invalid;
            }

            if (kind == ObjectKind.Blob)
            {
                if (existing.DeclaredSize is { } existingSize &&
                    declaredSize is { } candidateSize &&
                    existingSize != candidateSize)
                {
                    return AdmissionResult.Invalid;
                }

                existing.DeclaredSize ??= declaredSize;
            }

            return AdmissionResult.Success;
        }

        if (countTowardLimit && !meter.TryAddUniqueObject())
        {
            return AdmissionResult.UnsupportedSize;
        }

        claims.Add(sha, new ObjectClaim(kind, declaredSize));
        return AdmissionResult.Success;
    }

    private static ReviewedTreeFailure AdmissionFailure(
        AdmissionResult result) => result switch
    {
        AdmissionResult.UnsupportedSize => ReviewedTreeFailure.UnsupportedSize,
        _ => ReviewedTreeFailure.InvalidGraph,
    };

    private static ReviewedTreeFailure MapFailure(
        ReviewedGitObjectFailure failure) => failure switch
    {
        ReviewedGitObjectFailure.UnsupportedSize =>
            ReviewedTreeFailure.UnsupportedSize,
        ReviewedGitObjectFailure.NotFound => ReviewedTreeFailure.MissingObject,
        ReviewedGitObjectFailure.IdentityMismatch =>
            ReviewedTreeFailure.IdentityMismatch,
        ReviewedGitObjectFailure.InvalidRequest or
        ReviewedGitObjectFailure.InvalidResponse =>
            ReviewedTreeFailure.InvalidGraph,
        ReviewedGitObjectFailure.Unauthorized or
        ReviewedGitObjectFailure.Forbidden or
        ReviewedGitObjectFailure.RateLimited or
        ReviewedGitObjectFailure.UpstreamFailure or
        ReviewedGitObjectFailure.TransportFailure =>
            ReviewedTreeFailure.GitHubUnavailable,
        ReviewedGitObjectFailure.StagingFailure =>
            ReviewedTreeFailure.InternalFailure,
        _ => ReviewedTreeFailure.InternalFailure,
    };

    private static ReviewedTreeMaterializationResult Fail(
        ReviewedContentBudget budget,
        ReviewedBlobStagingLease staging,
        ReviewedTreeFailure failure)
    {
        budget.Invalidate();
        var cleanupIncomplete = !staging.Cleanup();
        return ReviewedTreeMaterializationResult.Failed(
            failure,
            cleanupIncomplete);
    }

    private static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;

    private sealed record CoreResult(
        ReviewedTreeSnapshot? Snapshot,
        ReviewedTreeFailure Failure)
    {
        internal static CoreResult Success(ReviewedTreeSnapshot snapshot) =>
            new(snapshot, ReviewedTreeFailure.None);

        internal static CoreResult Failed(ReviewedTreeFailure failure) =>
            new(null, failure);
    }

    private sealed class TreeFrame
    {
        internal TreeFrame(
            string sha,
            string prefix,
            int depth,
            ImmutableHashSet<string> ancestors,
            ImmutableArray<ReviewedGitTreeEntryFact> entries)
        {
            Sha = sha;
            Prefix = prefix;
            Depth = depth;
            Ancestors = ancestors;
            Entries = entries;
        }

        internal string Sha { get; }

        internal string Prefix { get; }

        internal int Depth { get; }

        internal ImmutableHashSet<string> Ancestors { get; }

        internal ImmutableArray<ReviewedGitTreeEntryFact> Entries { get; }

        internal int Index { get; set; }

        internal string? PreviousEntryPath { get; set; }
    }

    private sealed record EntryDraft(
        string Path,
        string Mode,
        ReviewedTreeEntryKind Kind,
        string Sha,
        long? Size);

    private sealed class ObjectClaim
    {
        internal ObjectClaim(ObjectKind kind, long? declaredSize)
        {
            Kind = kind;
            DeclaredSize = declaredSize;
        }

        internal ObjectKind Kind { get; }

        internal long? DeclaredSize { get; set; }
    }

    private enum ObjectKind
    {
        Tree = 1,
        Blob,
        Commit,
    }

    private enum EntryShapeKind
    {
        Invalid = 0,
        Tree,
        Regular,
        Symlink,
        Submodule,
    }

    private enum AdmissionResult
    {
        Success = 1,
        Invalid,
        UnsupportedSize,
    }
}
