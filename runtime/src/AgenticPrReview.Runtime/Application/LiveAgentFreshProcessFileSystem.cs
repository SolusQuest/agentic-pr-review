using System.Collections.Immutable;
using Microsoft.Win32.SafeHandles;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime;

internal sealed record LiveAgentFreshProcessFileVersion(
    ulong Device,
    ulong File,
    long Length);

internal sealed record LiveAgentFreshProcessRead(
    byte[] Bytes,
    LiveAgentFreshProcessFileVersion Version);

internal sealed record LiveAgentFreshProcessAtomicWriteReceipt(
    LiveAgentFreshProcessFileVersion Version);

internal sealed class LiveAgentFreshProcessAuthorizedRoot
{
    private readonly object owner;

    private LiveAgentFreshProcessAuthorizedRoot(
        object owner,
        string rootPath,
        string stateRootPath,
        AuthorizedStateAccess access,
        ImmutableArray<RestrictedStateRootEntry> proof)
    {
        this.owner = owner;
        RootPath = rootPath;
        StateRootPath = stateRootPath;
        Access = access;
        Proof = proof;
    }

    internal string RootPath { get; }

    internal string StateRootPath { get; }

    internal AuthorizedStateAccess Access { get; }

    internal ImmutableArray<RestrictedStateRootEntry> Proof { get; }

    internal bool BelongsTo(object candidate) => ReferenceEquals(owner, candidate);

    internal static LiveAgentFreshProcessAuthorizedRoot Create(
        object owner,
        string rootPath,
        string stateRootPath,
        AuthorizedStateAccess access,
        ImmutableArray<RestrictedStateRootEntry> proof) =>
        new(owner, rootPath, stateRootPath, access, proof);
}

internal interface ILiveAgentFreshProcessFileSystem
{
    LiveAgentFreshProcessRead? ReadAuthorization();

    bool TryAuthorizeLayout(
        AuthorizedStateAccess access,
        bool lineageExpected,
        out LiveAgentFreshProcessAuthorizedRoot? authorizedRoot);

    LiveAgentFreshProcessRead? ReadReviewedInput(
        LiveAgentFreshProcessAuthorizedRoot root);

    LiveAgentFreshProcessRead? ReadSnapshotManifest(
        LiveAgentFreshProcessAuthorizedRoot root);

    LiveAgentFreshProcessRead? ReadLineage(
        LiveAgentFreshProcessAuthorizedRoot root);

    LiveAgentFreshProcessAtomicWriteReceipt? PublishLineage(
        LiveAgentFreshProcessAuthorizedRoot root,
        byte[] bytes,
        LiveAgentFreshProcessFileVersion? expectedPrior);

    LiveAgentFreshProcessAtomicWriteReceipt? PublishResult(
        LiveAgentFreshProcessAuthorizedRoot root,
        byte[] bytes);
}

internal sealed class LiveAgentFreshProcessFileSystem :
    ILiveAgentFreshProcessFileSystem
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    private static readonly string[] RootEntries =
        ["host", "input", "output", "private", "state"];
    private static readonly string[] InputEntries =
        ["reviewed-input.json", "snapshot-manifest.json"];
    private readonly string root;

    private LiveAgentFreshProcessFileSystem(string root)
    {
        this.root = root;
    }

    internal static bool TryCreate(
        string? value,
        out LiveAgentFreshProcessFileSystem? fileSystem)
    {
        fileSystem = null;
        if (value is null ||
            !Path.IsPathFullyQualified(value))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(value);
            var trimmed = Path.TrimEndingDirectorySeparator(full);
            if (!PathComparer.Equals(value, trimmed) ||
                trimmed.Length > 4_096)
            {
                return false;
            }

            fileSystem = new LiveAgentFreshProcessFileSystem(trimmed);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or
            IOException or
            NotSupportedException or
            System.Security.SecurityException)
        {
            return false;
        }
    }

    public LiveAgentFreshProcessRead? ReadAuthorization()
    {
        if (!TryCaptureRootProof(root, out var proof) ||
            !TryInspectDirectory(Path.Join(root, "host")) ||
            !RootProofIsCurrent(root, proof))
        {
            return null;
        }

        var read = ReadFixed(
            Path.Join(root, "host", "authorization.json"),
            LiveAgentFreshProcessCodec.AuthorizationBytes);
        return read is not null && RootProofIsCurrent(root, proof)
            ? read
            : null;
    }

    public bool TryAuthorizeLayout(
        AuthorizedStateAccess access,
        bool lineageExpected,
        out LiveAgentFreshProcessAuthorizedRoot? authorizedRoot)
    {
        authorizedRoot = null;
        if (access is null ||
            !RestrictedStateValidation.IsValidScope(access.Scope) ||
            !TryCaptureRootProof(root, out var proof) ||
            !MatchesEntries(root, RootEntries) ||
            !TryInspectDirectory(Path.Join(root, "input")) ||
            !TryInspectDirectory(Path.Join(root, "host")) ||
            !TryInspectDirectory(Path.Join(root, "state")) ||
            !TryInspectDirectory(Path.Join(root, "private")) ||
            !TryInspectDirectory(Path.Join(root, "output")) ||
            !MatchesEntries(Path.Join(root, "input"), InputEntries) ||
            !MatchesEntries(
                Path.Join(root, "host"),
                lineageExpected
                    ? ["accepted-lineage.json", "authorization.json"]
                    : ["authorization.json"]) ||
            !MatchesEntries(Path.Join(root, "private"), []) ||
            !MatchesEntries(Path.Join(root, "output"), []) ||
            !RootProofIsCurrent(root, proof))
        {
            return false;
        }

        authorizedRoot = LiveAgentFreshProcessAuthorizedRoot.Create(
            this,
            root,
            Path.Join(root, "state"),
            access,
            proof);
        return true;
    }

    public LiveAgentFreshProcessRead? ReadReviewedInput(
        LiveAgentFreshProcessAuthorizedRoot rootCapability) => ReadAuthorized(
            rootCapability,
            Path.Join(root, "input", "reviewed-input.json"),
            LiveAgentFreshProcessCodec.ReviewedInputBytes);

    public LiveAgentFreshProcessRead? ReadSnapshotManifest(
        LiveAgentFreshProcessAuthorizedRoot rootCapability) => ReadAuthorized(
            rootCapability,
            Path.Join(root, "input", "snapshot-manifest.json"),
            LiveAgentFreshProcessCodec.SnapshotManifestBytes);

    public LiveAgentFreshProcessRead? ReadLineage(
        LiveAgentFreshProcessAuthorizedRoot rootCapability) => ReadAuthorized(
            rootCapability,
            Path.Join(root, "host", "accepted-lineage.json"),
            LiveAgentFreshProcessCodec.LineageBytes);

    public LiveAgentFreshProcessAtomicWriteReceipt? PublishLineage(
        LiveAgentFreshProcessAuthorizedRoot rootCapability,
        byte[] bytes,
        LiveAgentFreshProcessFileVersion? expectedPrior) => WriteAuthorized(
            rootCapability,
            Path.Join(root, "host", "accepted-lineage.json"),
            bytes,
            LiveAgentFreshProcessCodec.LineageBytes,
            expectedPrior,
            allowReplace: expectedPrior is not null);

    public LiveAgentFreshProcessAtomicWriteReceipt? PublishResult(
        LiveAgentFreshProcessAuthorizedRoot rootCapability,
        byte[] bytes) => WriteAuthorized(
            rootCapability,
            Path.Join(root, "output", "result.json"),
            bytes,
            LiveAgentFreshProcessCodec.ResultBytes,
            expectedPrior: null,
            allowReplace: false);

    private LiveAgentFreshProcessRead? ReadAuthorized(
        LiveAgentFreshProcessAuthorizedRoot capability,
        string path,
        int maximumBytes)
    {
        if (!IsAuthorized(capability))
        {
            return null;
        }

        var read = ReadFixed(path, maximumBytes);
        return read is not null && IsAuthorized(capability)
            ? read
            : null;
    }

    private LiveAgentFreshProcessAtomicWriteReceipt? WriteAuthorized(
        LiveAgentFreshProcessAuthorizedRoot capability,
        string path,
        byte[] bytes,
        int maximumBytes,
        LiveAgentFreshProcessFileVersion? expectedPrior,
        bool allowReplace)
    {
        if (!IsAuthorized(capability) ||
            bytes is null ||
            bytes.Length is 0 ||
            bytes.Length > maximumBytes)
        {
            return null;
        }

        var existing = InspectRegularPath(path);
        if (expectedPrior is null && existing is not null ||
            expectedPrior is not null && existing != expectedPrior)
        {
            return null;
        }

        var temporary = string.Concat(
            path,
            ".",
            Guid.NewGuid().ToString("N"),
            ".tmp");
        var moved = false;
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4_096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (!IsAuthorized(capability) ||
                (expectedPrior is null
                    ? InspectRegularPath(path) is not null
                    : InspectRegularPath(path) != expectedPrior))
            {
                return null;
            }

            File.Move(temporary, path, overwrite: allowReplace);
            moved = true;
            if (!TrySyncParent(path))
            {
                return null;
            }

            var version = InspectRegularPath(path);
            return version is not null && IsAuthorized(capability)
                ? new LiveAgentFreshProcessAtomicWriteReceipt(version)
                : null;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            return null;
        }
        finally
        {
            if (!moved)
            {
                TryDelete(temporary);
            }
        }
    }

    private bool IsAuthorized(LiveAgentFreshProcessAuthorizedRoot capability) =>
        capability is not null &&
        capability.BelongsTo(this) &&
        PathComparer.Equals(capability.RootPath, root) &&
        RestrictedStateValidation.IsValidScope(capability.Access.Scope) &&
        RootProofIsCurrent(root, capability.Proof);

    private static LiveAgentFreshProcessRead? ReadFixed(
        string path,
        int maximumBytes)
    {
        var open = NativeRestrictedStateFiles.OpenFileNoFollow(
            path,
            out var handle);
        if (open != RestrictedStateOpenResult.Success || handle is null)
        {
            return null;
        }

        using (handle)
        {
            if (!TryInspectRegular(handle, out var identity, out var length) ||
                length is <= 0 ||
                length > maximumBytes ||
                length > int.MaxValue)
            {
                return null;
            }

            var bytes = new byte[(int)length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = RandomAccess.Read(
                    handle,
                    bytes.AsSpan(offset),
                    offset);
                if (read <= 0)
                {
                    return null;
                }

                offset += read;
            }

            if (!TryInspectRegular(handle, out var after, out var afterLength) ||
                after != identity ||
                afterLength != length ||
                InspectRegularPath(path) is not { } current ||
                current.Device != identity.Device ||
                current.File != identity.File ||
                current.Length != length)
            {
                return null;
            }

            return new LiveAgentFreshProcessRead(bytes, current);
        }
    }

    private static LiveAgentFreshProcessFileVersion? InspectRegularPath(
        string path)
    {
        var open = NativeRestrictedStateFiles.OpenFileNoFollow(
            path,
            out var handle);
        if (open != RestrictedStateOpenResult.Success || handle is null)
        {
            return null;
        }

        using (handle)
        {
            return TryInspectRegular(handle, out var identity, out var length)
                ? new LiveAgentFreshProcessFileVersion(
                    identity.Device,
                    identity.File,
                    length)
                : null;
        }
    }

    private static bool TryInspectRegular(
        SafeFileHandle handle,
        out RestrictedStateFileIdentity identity,
        out long length)
    {
        identity = default;
        length = 0;
        try
        {
            var attributes = File.GetAttributes(handle);
            if ((attributes &
                    (FileAttributes.Directory |
                        FileAttributes.ReparsePoint)) != 0 ||
                !NativeRestrictedStateFiles.TryGetIdentity(
                    handle,
                    expectDirectory: false,
                    out identity))
            {
                return false;
            }

            length = RandomAccess.GetLength(handle);
            return length >= 0;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryInspectDirectory(string path)
    {
        var open = NativeRestrictedStateFiles.OpenDirectoryNoFollow(
            path,
            out var handle);
        if (open != RestrictedStateOpenResult.Success || handle is null)
        {
            return false;
        }

        using (handle)
        {
            try
            {
                var attributes = File.GetAttributes(handle);
                return (attributes & FileAttributes.Directory) != 0 &&
                    (attributes & FileAttributes.ReparsePoint) == 0 &&
                    NativeRestrictedStateFiles.TryGetIdentity(
                        handle,
                        expectDirectory: true,
                        out _);
            }
            catch (Exception exception) when (exception is IOException or
                UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private static bool TrySyncParent(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent is null)
        {
            return false;
        }

        var open = NativeRestrictedStateFiles.OpenDirectoryNoFollow(
            parent,
            out var handle);
        if (open != RestrictedStateOpenResult.Success || handle is null)
        {
            return false;
        }

        using (handle)
        {
            return NativeRestrictedStateFiles.TrySyncDirectory(handle);
        }
    }

    private static bool MatchesEntries(string directory, string[] expected)
    {
        try
        {
            var actual = Directory.EnumerateFileSystemEntries(directory)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray();
            return actual.SequenceEqual(expected, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or
            ArgumentException)
        {
            return false;
        }
    }

    private static bool TryCaptureRootProof(
        string rootPath,
        out ImmutableArray<RestrictedStateRootEntry> proof)
    {
        try
        {
            var entries = ImmutableArray.CreateBuilder<
                RestrictedStateRootEntry>();
            var current = new DirectoryInfo(rootPath);
            while (current is not null)
            {
                var open = NativeRestrictedStateFiles.OpenDirectoryNoFollow(
                    current.FullName,
                    out var handle);
                if (open != RestrictedStateOpenResult.Success ||
                    handle is null)
                {
                    proof = [];
                    return false;
                }

                using (handle)
                {
                    var attributes = File.GetAttributes(handle);
                    if ((attributes & FileAttributes.Directory) == 0 ||
                        (attributes & FileAttributes.ReparsePoint) != 0 ||
                        !NativeRestrictedStateFiles.TryGetIdentity(
                            handle,
                            expectDirectory: true,
                            out var identity))
                    {
                        proof = [];
                        return false;
                    }

                    entries.Add(new RestrictedStateRootEntry(
                        current.FullName,
                        identity));
                }

                current = current.Parent;
            }

            proof = entries.ToImmutable();
            return true;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or
            ArgumentException)
        {
            proof = [];
            return false;
        }
    }

    private static bool RootProofIsCurrent(
        string rootPath,
        ImmutableArray<RestrictedStateRootEntry> expected) =>
        TryCaptureRootProof(rootPath, out var actual) &&
        actual.SequenceEqual(expected);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or
            ArgumentException)
        {
            // The primary write failure remains authoritative.
        }
    }
}
