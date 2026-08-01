using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime;

internal sealed record LiveAgentFreshProcessSnapshotInput(
    string[] TrackedFiles,
    ReviewedChangedFile[] ChangedFiles,
    ReviewedDiffSource[] DiffSources,
    IR3LiveAgentReviewedFileAccessFactory FileAccessFactory);

internal static class LiveAgentFreshProcessManifest
{
    internal static bool TryAdmit(
        LiveAgentFreshProcessSnapshotManifestDocument document,
        ReviewedIdentity identity,
        string root,
        out LiveAgentFreshProcessSnapshotInput? input)
    {
        input = null;
        if (document is null ||
            !StringComparer.Ordinal.Equals(
                document.Kind,
                LiveAgentFreshProcessDomain.SnapshotManifestKind) ||
            !LiveAgentFreshProcessDomain.TryMapReviewedIdentity(
                document.ReviewedIdentity,
                out var manifestIdentity) ||
            manifestIdentity != identity ||
            document.TrackedFiles is null ||
            document.Files is null ||
            document.ChangedFiles is null ||
            document.DiffSources is null ||
            document.TrackedFiles.Length > AgentLimits.TrackedFiles ||
            document.Files.Length != document.TrackedFiles.Length)
        {
            return false;
        }

        var tracked = document.TrackedFiles.ToArray();
        if (!IsStrictlyOrdered(tracked) ||
            tracked.Any(path => !RepositoryPath.IsValid(path)))
        {
            return false;
        }

        var files = ImmutableDictionary.CreateBuilder<
            string,
            LiveAgentFreshProcessManifestFile>(StringComparer.Ordinal);
        for (var index = 0; index < document.Files.Length; index++)
        {
            var current = document.Files[index];
            if (current is null ||
                !StringComparer.Ordinal.Equals(current.Path, tracked[index]) ||
                current.Length is < 0 or > AgentLimits.SearchFileBytes ||
                !LiveAgentFreshProcessDomain.IsSha256(current.Sha256) ||
                current.ContentBase64 is null)
            {
                return false;
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(current.ContentBase64);
            }
            catch (FormatException)
            {
                return false;
            }

            if (bytes.LongLength != current.Length ||
                !StringComparer.Ordinal.Equals(
                    Convert.ToBase64String(bytes),
                    current.ContentBase64) ||
                !StringComparer.Ordinal.Equals(
                    LiveAgentFreshProcessDomain.RawSha256(bytes),
                    current.Sha256) ||
                !files.TryAdd(
                    current.Path,
                    new LiveAgentFreshProcessManifestFile(
                        current.Path,
                        bytes)))
            {
                CryptographicOperations.ZeroMemory(bytes);
                return false;
            }
        }

        ReviewedChangedFile[] changed;
        ReviewedDiffSource[] diffs;
        try
        {
            changed = document.ChangedFiles.Select(value => new ReviewedChangedFile(
                value.Path,
                value.PreviousPath,
                value.Status,
                value.Additions,
                value.Deletions,
                value.Changes,
                value.PatchStatus,
                value.PatchSha256,
                value.SourceTruncated)).ToArray();
            diffs = document.DiffSources.Select(value => new ReviewedDiffSource(
                identity,
                value.Path,
                value.PreviousPath,
                value.Status,
                value.SourceTruncated,
                value.Hunks.Select(hunk => new ReviewedDiffHunk(
                    hunk.OldStart,
                    hunk.OldCount,
                    hunk.NewStart,
                    hunk.NewCount,
                    hunk.Lines.Select(line => new ReviewedDiffLine(
                        line.Kind,
                        line.OldLine,
                        line.NewLine,
                        line.Text)))))).ToArray();

            _ = new ReviewedSnapshot(
                identity,
                root,
                tracked,
                changed,
                diffs);
        }
        catch (Exception exception) when (exception is ArgumentException or
            InvalidOperationException or
            OverflowException or
            NullReferenceException)
        {
            Zero(files.Values);
            return false;
        }

        input = new LiveAgentFreshProcessSnapshotInput(
            tracked,
            changed,
            diffs,
            new LiveAgentFreshProcessManifestFileAccessFactory(
                identity,
                root,
                files.ToImmutable()));
        return true;
    }

    private static bool IsStrictlyOrdered(string[] values)
    {
        for (var index = 1; index < values.Length; index++)
        {
            if (StringComparer.Ordinal.Compare(
                    values[index - 1],
                    values[index]) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static void Zero(
        IEnumerable<LiveAgentFreshProcessManifestFile> files)
    {
        foreach (var file in files)
        {
            file.Zero();
        }
    }
}

internal sealed class LiveAgentFreshProcessManifestFile(
    string path,
    byte[] bytes)
{
    private byte[] contents = bytes;

    internal string Path { get; } = path;

    internal long Length => contents.LongLength;

    internal ReviewedFileIdentity Identity
    {
        get
        {
            var hash = SHA256.HashData(contents);
            try
            {
                return new ReviewedFileIdentity(
                    BinaryPrimitives.ReadUInt64LittleEndian(hash),
                    BinaryPrimitives.ReadUInt64LittleEndian(hash.AsSpan(8)));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(hash);
            }
        }
    }

    internal byte[] Copy() => contents.ToArray();

    internal void Zero() => CryptographicOperations.ZeroMemory(contents);

    public override string ToString() => "r3_manifest_file";
}

internal sealed class LiveAgentFreshProcessManifestFileAccessFactory(
    ReviewedIdentity identity,
    string root,
    ImmutableDictionary<string, LiveAgentFreshProcessManifestFile> files)
    : IR3LiveAgentReviewedFileAccessFactory,
    IDisposable
{
    private ImmutableDictionary<string, LiveAgentFreshProcessManifestFile>?
        current = files;

    public IReviewedFileAccess Create()
    {
        var snapshot = current ?? throw new ObjectDisposedException(
            nameof(LiveAgentFreshProcessManifestFileAccessFactory));
        return new LiveAgentFreshProcessManifestFileAccess(
            identity,
            root,
            snapshot);
    }

    public void Dispose()
    {
        var snapshot = Interlocked.Exchange(ref current, null);
        if (snapshot is not null)
        {
            foreach (var file in snapshot.Values)
            {
                file.Zero();
            }
        }
    }
}

internal sealed class LiveAgentFreshProcessManifestFileAccess(
    ReviewedIdentity identity,
    string root,
    ImmutableDictionary<string, LiveAgentFreshProcessManifestFile> files)
    : IReviewedFileAccess
{
    public ReviewedFileMetadata InspectMetadata(
        ReviewedSnapshot snapshot,
        string path) => TryGet(snapshot, path, out var file)
        ? new ReviewedFileMetadata(
            ReviewedFileAccessStatus.Success,
            file!.Length)
        : ReviewedFileMetadata.Unsafe();

    public ReviewedFileProbe Probe(
        ReviewedSnapshot snapshot,
        string path) => TryGet(snapshot, path, out var file)
        ? new ReviewedFileProbe(
            ReviewedFileAccessStatus.Success,
            file!.Length,
            file.Identity)
        : ReviewedFileProbe.Unsafe();

    public ValueTask<ReviewedFileRead> ReadAsync(
        ReviewedSnapshot snapshot,
        string path,
        ReviewedFileProbe expected,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<ReviewedFileRead>(
                cancellationToken);
        }

        if (!TryGet(snapshot, path, out var file) ||
            expected.Status != ReviewedFileAccessStatus.Success ||
            expected.Length != file!.Length ||
            expected.Identity != file.Identity)
        {
            return ValueTask.FromResult(ReviewedFileRead.Unsafe());
        }

        return ValueTask.FromResult(new ReviewedFileRead(
            ReviewedFileAccessStatus.Success,
            file.Copy()));
    }

    private bool TryGet(
        ReviewedSnapshot snapshot,
        string path,
        out LiveAgentFreshProcessManifestFile? file)
    {
        file = null;
        return snapshot is not null &&
            snapshot.Identity == identity &&
            StringComparer.Ordinal.Equals(snapshot.AbsoluteRoot, root) &&
            RepositoryPath.IsValid(path) &&
            files.TryGetValue(path, out file);
    }
}
