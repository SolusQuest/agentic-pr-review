using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.GitHub;

namespace AgenticPrReview.Runtime.ActionHost.Snapshot.ChangedFiles;

internal sealed record ReviewedPullRequestFileFact(
    string Sha,
    string Path,
    string? PreviousPath,
    string Status,
    int Additions,
    int Deletions,
    int Changes,
    string? Patch,
    bool PatchIncomplete);

internal sealed class ReviewedChangedFileIdentity
{
    internal ReviewedChangedFileIdentity(
        string sha256,
        ImmutableArray<byte> canonicalPreimage)
    {
        Sha256 = sha256;
        CanonicalPreimage = canonicalPreimage;
    }

    internal string Sha256 { get; }
    internal ImmutableArray<byte> CanonicalPreimage { get; }
}

internal sealed class ReviewedChangedFileSet
{
    internal ReviewedChangedFileSet(
        IEnumerable<ReviewedPullRequestFileFact> files,
        ReviewedChangedFileIdentity identity)
    {
        Files = files.OrderBy(static file => file.Path, StringComparer.Ordinal)
            .ToImmutableArray();
        Identity = identity;
    }

    internal ImmutableArray<ReviewedPullRequestFileFact> Files { get; }
    internal ReviewedChangedFileIdentity Identity { get; }
}

internal sealed class ReviewedChangedFileReader
{
    private const int MaximumPatchEvidenceBytes = 512 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IReviewedSnapshotTransportFactory _transportFactory;

    internal ReviewedChangedFileReader(
        IReviewedSnapshotTransportFactory transportFactory)
    {
        _transportFactory = transportFactory ??
            throw new ArgumentNullException(nameof(transportFactory));
    }

    internal async Task<ReviewedSnapshotReadResult<ReviewedChangedFileSet>>
        ReadAsync(
            ActionHostAuthorizer.AuthorizedInvocation invocation,
            Contracts.ActionHostGitHubToken token,
            ReviewedTreeSnapshot tree,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(tree);
        var frozen = invocation.PullRequest;
        if (tree.RepositoryId != frozen.RepositoryId ||
            tree.PullRequestNumber != frozen.Number ||
            !StringComparer.Ordinal.Equals(tree.HeadSha, frozen.HeadSha))
        {
            return ReviewedSnapshotReadResult<ReviewedChangedFileSet>.Failed(
                ReviewedSnapshotReadFailure.IdentityMismatch);
        }

        using var transport = _transportFactory.Create(
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
            return ReviewedSnapshotReadResult<ReviewedChangedFileSet>.Failed(
                ReviewedSnapshotReadFailure.IdentityMismatch);
        }

        var raw = new List<ActionHostPullRequestFileObject>();
        var page = 1;
        while (true)
        {
            var result = await transport.GetPullRequestFilesAsync(
                page,
                cancellationToken);
            if (result.Value is null)
            {
                return ReviewedSnapshotReadResult<ReviewedChangedFileSet>
                    .Failed(result.Failure);
            }

            if (raw.Count > ReviewedContentLimits.ChangedFiles -
                    result.Value.Files.Count)
            {
                return ReviewedSnapshotReadResult<ReviewedChangedFileSet>
                    .Failed(ReviewedSnapshotReadFailure.UnsupportedSize);
            }

            raw.AddRange(result.Value.Files);
            if (result.Value.IsComplete)
            {
                break;
            }

            page = checked(page + 1);
            if (raw.Count == ReviewedContentLimits.ChangedFiles)
            {
                var probe = await transport.GetPullRequestFilesAsync(
                    page,
                    cancellationToken);
                if (probe.Value is null)
                {
                    return ReviewedSnapshotReadResult<ReviewedChangedFileSet>
                        .Failed(probe.Failure);
                }

                if (probe.Value.Files.Count != 0 || !probe.Value.IsComplete)
                {
                    return ReviewedSnapshotReadResult<ReviewedChangedFileSet>
                        .Failed(ReviewedSnapshotReadFailure.UnsupportedSize);
                }

                break;
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
            return ReviewedSnapshotReadResult<ReviewedChangedFileSet>.Failed(
                ReviewedSnapshotReadFailure.IdentityMismatch);
        }

        return TryValidate(raw, tree, out var files)
            ? ReviewedSnapshotReadResult<ReviewedChangedFileSet>.Success(
                new(files, ReviewedChangedFileIdentityWriter.Write(files)))
            : ReviewedSnapshotReadResult<ReviewedChangedFileSet>.Failed(
                ReviewedSnapshotReadFailure.IdentityMismatch);
    }

    private static bool TryValidate(
        IReadOnlyList<ActionHostPullRequestFileObject> raw,
        ReviewedTreeSnapshot tree,
        out ImmutableArray<ReviewedPullRequestFileFact> files)
    {
        files = default;
        var headByPath = tree.Records.ToDictionary(
            static record => record.Path,
            StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var builder = ImmutableArray.CreateBuilder<ReviewedPullRequestFileFact>(
            raw.Count);
        foreach (var item in raw)
        {
            if (!ReviewedTreePath.IsValid(item.Path) ||
                !paths.Add(item.Path) ||
                item.Additions is < 0 or > 1_000_000 ||
                item.Deletions is < 0 or > 1_000_000 ||
                item.Changes != (long)item.Additions + item.Deletions ||
                item.Changes > 1_000_000 ||
                !LifecycleIsValid(item))
            {
                return false;
            }

            var removed = StringComparer.Ordinal.Equals(item.Status, "removed");
            if (removed)
            {
                if (headByPath.ContainsKey(item.Path))
                {
                    return false;
                }
            }
            else if (!headByPath.TryGetValue(item.Path, out var head) ||
                !StringComparer.Ordinal.Equals(head.ObjectSha, item.Sha))
            {
                return false;
            }

            string? patch = null;
            var patchIncomplete = false;
            if (item.Patch is not null)
            {
                try
                {
                    patchIncomplete = StrictUtf8.GetByteCount(item.Patch) >
                        MaximumPatchEvidenceBytes;
                    if (!patchIncomplete)
                    {
                        patch = item.Patch;
                    }
                }
                catch (EncoderFallbackException)
                {
                    patchIncomplete = true;
                }
            }

            builder.Add(new(
                item.Sha,
                item.Path,
                item.PreviousPath,
                item.Status,
                item.Additions,
                item.Deletions,
                item.Changes,
                patch,
                patchIncomplete));
        }

        files = builder
            .OrderBy(static file => file.Path, StringComparer.Ordinal)
            .ToImmutableArray();
        return true;
    }

    private static bool LifecycleIsValid(
        ActionHostPullRequestFileObject item) => item.Status switch
        {
            "added" or "removed" or "modified" or "changed" =>
                item.PreviousPath is null,
            "renamed" or "copied" =>
                ReviewedTreePath.IsValid(item.PreviousPath!) &&
                !StringComparer.Ordinal.Equals(
                    item.Path,
                    item.PreviousPath),
            _ => false,
        };

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
        StringComparer.Ordinal.Equals(current.BaseSha, frozen.BaseSha) &&
        StringComparer.Ordinal.Equals(current.HeadSha, frozen.HeadSha);
}

internal static class ReviewedChangedFileIdentityWriter
{
    private static readonly byte[] Domain =
        "agentic-pr-review.changed-files.v1"u8.ToArray();

    internal static ReviewedChangedFileIdentity Write(
        ImmutableArray<ReviewedPullRequestFileFact> files)
    {
        using var stream = new MemoryStream();
        WriteFrame(stream, Domain);
        WriteInt32(stream, files.Length);
        foreach (var file in files.OrderBy(
                     static file => file.Path,
                     StringComparer.Ordinal))
        {
            WriteFrame(stream, Encoding.ASCII.GetBytes(file.Sha));
            WriteFrame(stream, Encoding.UTF8.GetBytes(file.Path));
            WriteNullable(stream, file.PreviousPath);
            WriteFrame(stream, Encoding.ASCII.GetBytes(file.Status));
            WriteInt32(stream, file.Additions);
            WriteInt32(stream, file.Deletions);
            WriteInt32(stream, file.Changes);
            stream.WriteByte(file.PatchIncomplete ? (byte)1 : (byte)0);
            if (file.Patch is null)
            {
                WriteInt32(stream, -1);
            }
            else
            {
                WriteFrame(
                    stream,
                    SHA256.HashData(Encoding.UTF8.GetBytes(file.Patch)));
            }
        }

        var preimage = stream.ToArray();
        return new(
            Convert.ToHexString(SHA256.HashData(preimage)).ToLowerInvariant(),
            ImmutableArray.CreateRange(preimage));
    }

    private static void WriteNullable(Stream stream, string? value)
    {
        if (value is null)
        {
            WriteInt32(stream, -1);
        }
        else
        {
            WriteFrame(stream, Encoding.UTF8.GetBytes(value));
        }
    }

    private static void WriteFrame(Stream stream, ReadOnlySpan<byte> value)
    {
        WriteInt32(stream, value.Length);
        stream.Write(value);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
