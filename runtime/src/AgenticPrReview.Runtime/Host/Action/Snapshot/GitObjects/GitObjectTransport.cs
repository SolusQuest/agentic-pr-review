using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using System.Security.Cryptography;
using System.Text;

namespace AgenticPrReview.Runtime.ActionHost.Snapshot.GitObjects;

internal sealed class ReviewedGitObjectTransportFactory :
    IReviewedGitObjectTransportFactory
{
    private static readonly object FactoryAuthority = new();
    private readonly IActionHostGitObjectTransportFactory _sharedFactory;

    internal ReviewedGitObjectTransportFactory(
        IActionHostGitObjectTransportFactory sharedFactory)
    {
        _sharedFactory = sharedFactory ??
            throw new ArgumentNullException(nameof(sharedFactory));
    }

    public IReviewedGitObjectTransport Create(
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        ActionHostGitHubToken token,
        ReviewedContentBudget budget)
    {
        ArgumentNullException.ThrowIfNull(token);
        return ReviewedGitObjectTransport.Mint(
            FactoryAuthority,
            invocation,
            budget,
            _sharedFactory.CreateExactObjectTransport(token));
    }

    internal static bool HasFactoryAuthority(object authority) =>
        ReferenceEquals(authority, FactoryAuthority);
}

internal sealed class ReviewedGitObjectCredentialException : Exception
{
    internal ReviewedGitObjectCredentialException()
        : base("The reviewed Git-object authority is invalid.")
    {
    }
}

internal sealed class ReviewedGitObjectTransport :
    IReviewedGitObjectTransport
{
    private readonly string _repositoryName;
    private readonly string _headSha;
    private readonly ReviewedContentBudget _budget;
    private readonly IActionHostGitObjectTransport _sharedTransport;
    private bool _disposed;

    private ReviewedGitObjectTransport(
        string repositoryName,
        string headSha,
        ReviewedContentBudget budget,
        IActionHostGitObjectTransport sharedTransport)
    {
        _repositoryName = repositoryName;
        _headSha = headSha;
        _budget = budget;
        _sharedTransport = sharedTransport;
    }

    internal static ReviewedGitObjectTransport Mint(
        object authority,
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        ReviewedContentBudget budget,
        IActionHostGitObjectTransport sharedTransport)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(sharedTransport);
        if (!ReviewedGitObjectTransportFactory.HasFactoryAuthority(authority) ||
            !TryAuthorizedSource(
                invocation,
                out var repositoryName,
                out var headSha))
        {
            sharedTransport.Dispose();
            throw new ReviewedGitObjectCredentialException();
        }

        return new ReviewedGitObjectTransport(
            repositoryName,
            headSha,
            budget,
            sharedTransport);
    }

    public async Task<ReviewedGitObjectResult<ReviewedGitCommitFact>>
        GetCommitAsync(CancellationToken cancellationToken)
    {
        if (!TryBeginRequest(cancellationToken, out var operation))
        {
            return ReviewedGitObjectResult<ReviewedGitCommitFact>.Failed(
                ReviewedGitObjectFailure.UnsupportedSize);
        }

        using (operation)
        {
            try
            {
                var result = await _sharedTransport.GetCommitObjectAsync(
                    _repositoryName,
                    _headSha,
                    operation!.Token);
                if (!TryCharge(result.CapturedResponseBytes, operation.Token))
                {
                    return ReviewedGitObjectResult<ReviewedGitCommitFact>
                        .Failed(ReviewedGitObjectFailure.UnsupportedSize);
                }

                return result.Value is { } value
                    ? ReviewedGitObjectResult<ReviewedGitCommitFact>.Success(
                        new(value.Sha, value.TreeSha, value.ParentShas))
                    : ReviewedGitObjectResult<ReviewedGitCommitFact>.Failed(
                        MapFailure(result.Failure));
            }
            catch (OperationCanceledException)
                when (operation!.DeadlineExpired)
            {
                return ReviewedGitObjectResult<ReviewedGitCommitFact>.Failed(
                    ReviewedGitObjectFailure.UnsupportedSize);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsNonFatal(exception))
            {
                return ReviewedGitObjectResult<ReviewedGitCommitFact>.Failed(
                    ReviewedGitObjectFailure.TransportFailure);
            }
        }
    }

    public async Task<ReviewedGitObjectResult<ReviewedGitTreeFact>>
        GetTreeAsync(
            string treeSha,
            CancellationToken cancellationToken)
    {
        if (!ReviewedGitObjectValidation.IsSha(treeSha))
        {
            return ReviewedGitObjectResult<ReviewedGitTreeFact>.Failed(
                ReviewedGitObjectFailure.InvalidRequest);
        }

        if (!TryBeginRequest(cancellationToken, out var operation))
        {
            return ReviewedGitObjectResult<ReviewedGitTreeFact>.Failed(
                ReviewedGitObjectFailure.UnsupportedSize);
        }

        using (operation)
        {
            try
            {
                var result = await _sharedTransport.GetTreeObjectAsync(
                    _repositoryName,
                    treeSha,
                    operation!.Token);
                if (!TryCharge(result.CapturedResponseBytes, operation.Token))
                {
                    return ReviewedGitObjectResult<ReviewedGitTreeFact>.Failed(
                        ReviewedGitObjectFailure.UnsupportedSize);
                }

                if (result.Value is null)
                {
                    return ReviewedGitObjectResult<ReviewedGitTreeFact>.Failed(
                        MapFailure(result.Failure));
                }

                var entries = result.Value.Entries.Select(static entry =>
                    new ReviewedGitTreeEntryFact(
                        entry.Path,
                        entry.Mode,
                        entry.Type,
                        entry.Sha,
                        entry.Size)).ToArray();
                return ReviewedGitObjectResult<ReviewedGitTreeFact>.Success(
                    new(result.Value.Sha, entries));
            }
            catch (OperationCanceledException)
                when (operation!.DeadlineExpired)
            {
                return ReviewedGitObjectResult<ReviewedGitTreeFact>.Failed(
                    ReviewedGitObjectFailure.UnsupportedSize);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsNonFatal(exception))
            {
                return ReviewedGitObjectResult<ReviewedGitTreeFact>.Failed(
                    ReviewedGitObjectFailure.TransportFailure);
            }
        }
    }

    public async Task<ReviewedGitObjectResult<ReviewedStagedBlob>>
        StageBlobAsync(
            string blobSha,
            long declaredSize,
            ReviewedBlobStagingLease staging,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staging);
        if (!ReviewedGitObjectValidation.IsSha(blobSha) || declaredSize < 0)
        {
            return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                ReviewedGitObjectFailure.InvalidRequest);
        }

        if (declaredSize > ReviewedContentLimits.HeadBlobBytes)
        {
            return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                ReviewedGitObjectFailure.UnsupportedSize);
        }

        if (!TryBeginRequest(cancellationToken, out var operation))
        {
            return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                ReviewedGitObjectFailure.UnsupportedSize);
        }

        using (operation)
        {
            try
            {
                var result = await _sharedTransport.GetBlobObjectAsync(
                    _repositoryName,
                    blobSha,
                    ActionHostGitBlobReadBudget.MaximumSupported,
                    operation!.Token);
                if (!TryCharge(result.CapturedResponseBytes, operation.Token))
                {
                    return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                        ReviewedGitObjectFailure.UnsupportedSize);
                }

                if (result.Value is null)
                {
                    return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                        MapBlobFailure(result.Failure));
                }

                if (result.Value.Bytes.LongLength != declaredSize)
                {
                    return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                        ReviewedGitObjectFailure.IdentityMismatch);
                }

                await using var writer = staging.TryCreateWriter(
                    blobSha,
                    declaredSize);
                if (writer is null)
                {
                    return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                        ReviewedGitObjectFailure.StagingFailure);
                }

                if (!await writer.WriteAsync(
                        result.Value.Bytes,
                        operation.Token))
                {
                    return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                        ReviewedGitObjectFailure.IdentityMismatch);
                }

                var staged = await writer.CompleteAsync(operation.Token);
                return staged is null
                    ? ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                        ReviewedGitObjectFailure.IdentityMismatch)
                    : ReviewedGitObjectResult<ReviewedStagedBlob>.Success(
                        staged);
            }
            catch (OperationCanceledException)
                when (operation!.DeadlineExpired)
            {
                return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                    ReviewedGitObjectFailure.UnsupportedSize);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsNonFatal(exception))
            {
                return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                    ReviewedGitObjectFailure.TransportFailure);
            }
        }
    }

    public async Task<ReviewedGitObjectResult<ReviewedHeadArchiveBatch>>
        StageHeadRegularBlobsAsync(
            IReadOnlyList<ReviewedHeadArchiveEntry> entries,
            ReviewedBlobStagingLease staging,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(staging);
        var expectedInventory = TryExpectedEntries(entries, out var expected,
            out var expectedDirectories);
        if (entries.Count == 0 || expectedInventory ==
                ExpectedArchiveInventory.Invalid)
        {
            return ReviewedGitObjectResult<ReviewedHeadArchiveBatch>.Failed(
                ReviewedGitObjectFailure.InvalidRequest);
        }

        if (expectedInventory == ExpectedArchiveInventory.UnsupportedSize)
        {
            return ReviewedGitObjectResult<ReviewedHeadArchiveBatch>.Failed(
                ReviewedGitObjectFailure.UnsupportedSize);
        }

        // Reserve the authenticated GitHub redirect and its anonymous
        // codeload follow-up separately.  Both belong to the Snapshot
        // acquisition ceiling; neither is part of the trusted-proof runtime
        // authenticated-request receipt.
        if (!TryBeginRequest(cancellationToken, out var operation) ||
            !_budget.TryReserveRequest(cancellationToken))
        {
            return ReviewedGitObjectResult<ReviewedHeadArchiveBatch>.Failed(
                ReviewedGitObjectFailure.UnsupportedSize);
        }

        using (operation)
        {
            ActionHostGitArchiveReader? archive = null;
            try
            {
                var opened = await _sharedTransport.GetHeadArchiveAsync(
                    _repositoryName, _headSha, operation!.Token);
                if (opened.Value is null)
                {
                    return ReviewedGitObjectResult<ReviewedHeadArchiveBatch>.Failed(
                        MapFailure(opened.Failure));
                }

                archive = opened.Value;
                var result = await StageArchiveAsync(
                    archive, expected, expectedDirectories, staging,
                    operation.Token);
                if (!TryCharge(archive.CapturedResponseBytes, operation.Token))
                {
                    return ReviewedGitObjectResult<ReviewedHeadArchiveBatch>.Failed(
                        ReviewedGitObjectFailure.UnsupportedSize);
                }

                return result;
            }
            catch (OperationCanceledException) when (operation!.DeadlineExpired)
            {
                return ReviewedGitObjectResult<ReviewedHeadArchiveBatch>.Failed(
                    ReviewedGitObjectFailure.UnsupportedSize);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsNonFatal(exception))
            {
                return ReviewedGitObjectResult<ReviewedHeadArchiveBatch>.Failed(
                    exception is ActionHostGitArchiveReadException
                    {
                        Failure: ActionHostGitArchiveReadFailure
                            .CompressedLimitExceeded or
                            ActionHostGitArchiveReadFailure.DecodedLimitExceeded,
                    }
                        ? ReviewedGitObjectFailure.UnsupportedSize
                    : exception is InvalidDataException
                            ? ReviewedGitObjectFailure.IdentityMismatch
                        : ReviewedGitObjectFailure.TransportFailure);
            }
            finally
            {
                archive?.Dispose();
            }
        }
    }

    private async Task<ReviewedGitObjectResult<ReviewedHeadArchiveBatch>>
        StageArchiveAsync(
            ActionHostGitArchiveReader archive,
            IReadOnlyDictionary<string, ReviewedHeadArchiveEntry> expected,
            IReadOnlySet<string> expectedDirectories,
            ReviewedBlobStagingLease staging,
            CancellationToken cancellationToken)
    {
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var seenDirectories = new HashSet<string>(StringComparer.Ordinal);
        var stagedBySha = new Dictionary<string, ReviewedStagedBlob>(
            StringComparer.Ordinal);
        var prefix = (string?)null;
        long inflated = 0;
        var members = 0;
        ActionHostGitArchiveEntry? member;
        while ((member = await archive.GetNextEntryAsync(
            cancellationToken)) is not null)
        {
            if (members >= ReviewedContentLimits.HeadArchiveMembers)
            {
                return ReviewedGitObjectResult<ReviewedHeadArchiveBatch>.Failed(
                    ReviewedGitObjectFailure.UnsupportedSize);
            }

            members++;
            if (!TryArchivePath(member.Name, ref prefix, out var path,
                    out var hasTrailingSlash))
            {
                return ReviewedGitObjectResult<ReviewedHeadArchiveBatch>.Failed(
                    ReviewedGitObjectFailure.IdentityMismatch);
            }

            if (member.EntryType == ActionHostGitArchiveEntryType.Directory)
            {
                // Directory payload is not part of the admitted head-byte
                // aggregate.  A nonempty directory therefore has to fail
                // before the reader can advance and silently inflate it.
                if (!hasTrailingSlash || member.Length != 0 ||
                    !seenDirectories.Add(path) ||
                    !expectedDirectories.Contains(path))
                {
                    return ReviewedGitObjectResult<ReviewedHeadArchiveBatch>.Failed(
                        ReviewedGitObjectFailure.IdentityMismatch);
                }

                continue;
            }

            if (hasTrailingSlash || path.Length == 0 ||
                !expected.TryGetValue(path, out var entry) ||
                !seenPaths.Add(path) || !ArchiveModeMatches(member, entry.Mode))
            {
                return ReviewedGitObjectResult<ReviewedHeadArchiveBatch>.Failed(
                    ReviewedGitObjectFailure.IdentityMismatch);
            }

            if (inflated > ReviewedContentLimits.AggregateHeadBlobBytes -
                entry.Size)
            {
                return ReviewedGitObjectResult<ReviewedHeadArchiveBatch>.Failed(
                    ReviewedGitObjectFailure.UnsupportedSize);
            }

            inflated += entry.Size;
            if (entry.Mode == "120000")
            {
                if (!VerifySymlink(member, entry))
                {
                    return ReviewedGitObjectResult<ReviewedHeadArchiveBatch>.Failed(
                        ReviewedGitObjectFailure.IdentityMismatch);
                }

                continue;
            }

            if (member.EntryType != ActionHostGitArchiveEntryType.RegularFile ||
                member.Length != entry.Size ||
                member.DataStream is null)
            {
                return ReviewedGitObjectResult<ReviewedHeadArchiveBatch>.Failed(
                    ReviewedGitObjectFailure.IdentityMismatch);
            }

            if (stagedBySha.TryGetValue(entry.Sha, out _))
            {
                if (!await VerifyMemberAsync(member.DataStream, entry,
                        cancellationToken))
                {
                    return ReviewedGitObjectResult<ReviewedHeadArchiveBatch>.Failed(
                        ReviewedGitObjectFailure.IdentityMismatch);
                }
                continue;
            }

            await using var writer = staging.TryCreateWriter(entry.Sha, entry.Size);
            if (writer is null || !await CopyMemberAsync(member.DataStream, writer,
                    entry.Size, cancellationToken))
            {
                return ReviewedGitObjectResult<ReviewedHeadArchiveBatch>.Failed(
                    ReviewedGitObjectFailure.IdentityMismatch);
            }

            var staged = await writer.CompleteAsync(cancellationToken);
            if (staged is null)
            {
                return ReviewedGitObjectResult<ReviewedHeadArchiveBatch>.Failed(
                    ReviewedGitObjectFailure.IdentityMismatch);
            }

            stagedBySha.Add(entry.Sha, staged);
        }

        if (prefix is null || seenPaths.Count != expected.Count ||
            seenDirectories.Count != expectedDirectories.Count ||
            !expectedDirectories.All(seenDirectories.Contains) ||
            stagedBySha.Count != expected.Values
                .Where(static item => item.Mode != "120000")
                .Select(static item => item.Sha)
                .Distinct(StringComparer.Ordinal).Count())
        {
            return ReviewedGitObjectResult<ReviewedHeadArchiveBatch>.Failed(
                ReviewedGitObjectFailure.IdentityMismatch);
        }

        return ReviewedGitObjectResult<ReviewedHeadArchiveBatch>.Success(
            new ReviewedHeadArchiveBatch(stagedBySha));
    }

    private static ExpectedArchiveInventory TryExpectedEntries(
        IReadOnlyList<ReviewedHeadArchiveEntry> entries,
        out IReadOnlyDictionary<string, ReviewedHeadArchiveEntry> expected,
        out IReadOnlySet<string> expectedDirectories)
    {
        var map = new Dictionary<string, ReviewedHeadArchiveEntry>(
            StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal)
        {
            string.Empty,
        };
        if (entries.Count > ReviewedContentLimits.TrackedPaths)
        {
            expected = null!;
            expectedDirectories = null!;
            return ExpectedArchiveInventory.UnsupportedSize;
        }

        foreach (var entry in entries)
        {
            if (!ReviewedTreePath.IsValid(entry.Path) ||
                entry.Mode is not "100644" and not "100755" and not "120000" ||
                !ReviewedGitObjectValidation.IsSha(entry.Sha) ||
                entry.Size is < 0 or > ReviewedContentLimits.HeadBlobBytes ||
                !map.TryAdd(entry.Path, entry))
            {
                expected = null!;
                expectedDirectories = null!;
                return ExpectedArchiveInventory.Invalid;
            }

            var separator = entry.Path.IndexOf('/');
            while (separator >= 0)
            {
                directories.Add(entry.Path[..separator]);

                separator = entry.Path.IndexOf('/', separator + 1);
            }
        }

        if ((long)map.Count + directories.Count >
            ReviewedContentLimits.HeadArchiveMembers)
        {
            expected = null!;
            expectedDirectories = null!;
            return ExpectedArchiveInventory.UnsupportedSize;
        }

        expected = map;
        expectedDirectories = directories;
        return ExpectedArchiveInventory.Valid;
    }

    private static bool ArchiveModeMatches(
        ActionHostGitArchiveEntry member,
        string expectedMode)
    {
        // git archive normalizes regular permissions to 0664/0775 and
        // serializes symbolic links as 0777.  These are archive metadata,
        // not the Git-tree modes (100644/100755/120000).
        var expected = expectedMode switch
        {
            "100644" => 0x1b4,
            "100755" => 0x1fd,
            "120000" => 0x1ff,
            _ => -1,
        };
        return ((int)member.Mode & 0x1ff) == expected;
    }

    private static bool VerifySymlink(
        ActionHostGitArchiveEntry member,
        ReviewedHeadArchiveEntry entry)
    {
        if (member.EntryType != ActionHostGitArchiveEntryType.SymbolicLink ||
            member.Length != 0 || member.DataStream is not null ||
            member.LinkName is null)
        {
            return false;
        }

        byte[] target;
        try
        {
            target = new UTF8Encoding(false, true).GetBytes(member.LinkName);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        return target.LongLength == entry.Size &&
            StringComparer.Ordinal.Equals(GitBlobSha(target), entry.Sha);
    }

    private static string GitBlobSha(byte[] bytes)
    {
        var header = System.Text.Encoding.ASCII.GetBytes(
            "blob " + bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            "\0");
        return Convert.ToHexString(SHA1.HashData([.. header, .. bytes]))
            .ToLowerInvariant();
    }

    private static bool TryArchivePath(
        string? name,
        ref string? prefix,
        out string path,
        out bool hasTrailingSlash)
    {
        path = string.Empty;
        hasTrailingSlash = false;
        if (string.IsNullOrEmpty(name) || name.IndexOf('\\') >= 0 ||
            name[0] == '/' || name.Contains('\0'))
        {
            return false;
        }

        hasTrailingSlash = name.EndsWith("/", StringComparison.Ordinal);
        var canonicalName = hasTrailingSlash ? name[..^1] : name;
        if (canonicalName.Length == 0)
        {
            return false;
        }

        var parts = canonicalName.Split('/');
        if (parts.Any(static part => part.Length == 0 || part is "." or "..") ||
            !ReviewedTreePath.IsValid(parts[0]))
        {
            return false;
        }

        if (prefix is null)
        {
            prefix = parts[0];
        }
        else if (!StringComparer.Ordinal.Equals(prefix, parts[0]))
        {
            return false;
        }

        path = string.Join('/', parts.Skip(1));
        return path.Length == 0 || ReviewedTreePath.IsValid(path);
    }

    private enum ExpectedArchiveInventory
    {
        Valid,
        Invalid,
        UnsupportedSize,
    }

    private static async Task<bool> CopyMemberAsync(
        Stream source,
        ReviewedBlobStageWriter writer,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[ReviewedContentLimits.StreamBufferBytes];
        long copied = 0;
        while (copied < expectedSize)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, checked((int)
                Math.Min(buffer.Length, expectedSize - copied))), cancellationToken);
            if (read == 0 || !await writer.WriteAsync(buffer.AsMemory(0, read),
                    cancellationToken)) return false;
            copied += read;
        }
        return await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) == 0;
    }

    private static async Task<bool> VerifyMemberAsync(
        Stream source,
        ReviewedHeadArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[ReviewedContentLimits.StreamBufferBytes];
        long copied = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(ReviewedStagedBlob.GitBlobHeader(entry.Size));
        while (copied < entry.Size)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, checked((int)
                Math.Min(buffer.Length, entry.Size - copied))), cancellationToken);
            if (read == 0) return false;
            hash.AppendData(buffer.AsSpan(0, read));
            copied += read;
        }

        if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0)
        {
            return false;
        }

        return StringComparer.Ordinal.Equals(
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            entry.Sha);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sharedTransport.Dispose();
    }

    internal static bool TryAuthorizedSource(
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        out string repositoryName,
        out string headSha)
    {
        repositoryName = string.Empty;
        headSha = string.Empty;
        var pullRequest = invocation.PullRequest;
        if (pullRequest.RepositoryId <= 0 || pullRequest.Number <= 0 ||
            pullRequest.BaseRepositoryId != pullRequest.RepositoryId ||
            pullRequest.HeadRepositoryId != pullRequest.RepositoryId ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                pullRequest.BaseRepositoryName,
                pullRequest.HeadRepositoryName) ||
            !ReviewedGitObjectValidation.IsRepositoryName(
                pullRequest.HeadRepositoryName) ||
            !ReviewedGitObjectValidation.IsSha(pullRequest.HeadSha))
        {
            return false;
        }

        repositoryName = pullRequest.HeadRepositoryName;
        headSha = pullRequest.HeadSha;
        return true;
    }

    private bool TryBeginRequest(
        CancellationToken cancellationToken,
        out ReviewedContentBudget.OperationLease? operation)
    {
        operation = null;
        return !_disposed &&
            _budget.TryReserveRequest(cancellationToken) &&
            _budget.TryBeginOperation(cancellationToken, out operation);
    }

    private bool TryCharge(
        int capturedResponseBytes,
        CancellationToken cancellationToken)
    {
        long responseBytes = 0;
        return _budget.TryConsumeResponseBytes(
            ref responseBytes,
            capturedResponseBytes,
            cancellationToken);
    }

    private static ReviewedGitObjectFailure MapBlobFailure(
        ActionHostGitObjectFailure failure) => failure switch
        {
            ActionHostGitObjectFailure.InvalidResponse =>
                ReviewedGitObjectFailure.IdentityMismatch,
            _ => MapFailure(failure),
        };

    private static ReviewedGitObjectFailure MapFailure(
        ActionHostGitObjectFailure failure) => failure switch
        {
            ActionHostGitObjectFailure.InvalidRequest =>
                ReviewedGitObjectFailure.InvalidRequest,
            ActionHostGitObjectFailure.NotFound =>
                ReviewedGitObjectFailure.NotFound,
            ActionHostGitObjectFailure.Unauthorized =>
                ReviewedGitObjectFailure.Unauthorized,
            ActionHostGitObjectFailure.Forbidden =>
                ReviewedGitObjectFailure.Forbidden,
            ActionHostGitObjectFailure.RateLimited =>
                ReviewedGitObjectFailure.RateLimited,
            ActionHostGitObjectFailure.UpstreamFailure =>
                ReviewedGitObjectFailure.UpstreamFailure,
            ActionHostGitObjectFailure.InvalidResponse =>
                ReviewedGitObjectFailure.InvalidResponse,
            ActionHostGitObjectFailure.ResponseTooLarge =>
                ReviewedGitObjectFailure.UnsupportedSize,
            _ => ReviewedGitObjectFailure.TransportFailure,
        };

    private static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;
}
