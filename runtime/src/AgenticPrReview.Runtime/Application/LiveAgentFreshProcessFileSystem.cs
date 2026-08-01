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

internal sealed class LiveAgentFreshProcessAuthorizationRead
{
    private readonly object owner;

    private LiveAgentFreshProcessAuthorizationRead(
        object owner,
        byte[] bytes,
        LiveAgentFreshProcessFileVersion version,
        ImmutableArray<RestrictedStateRootEntry> rootProof)
    {
        this.owner = owner;
        Bytes = bytes;
        Version = version;
        RootProof = rootProof;
    }

    internal byte[] Bytes { get; }

    internal LiveAgentFreshProcessFileVersion Version { get; }

    internal ImmutableArray<RestrictedStateRootEntry> RootProof { get; }

    internal bool BelongsTo(object candidate) => ReferenceEquals(owner, candidate);

    internal static LiveAgentFreshProcessAuthorizationRead Create(
        object owner,
        LiveAgentFreshProcessRead read,
        ImmutableArray<RestrictedStateRootEntry> rootProof) => new(
            owner,
            read.Bytes,
            read.Version,
            rootProof);
}

internal sealed record LiveAgentFreshProcessAtomicWriteReceipt(
    LiveAgentFreshProcessFileVersion Version);

internal sealed class LiveAgentFreshProcessAuthorizedRoot : IDisposable
{
    private readonly object owner;
    private IDisposable? guard;

    private LiveAgentFreshProcessAuthorizedRoot(
        object owner,
        string rootPath,
        string inputRootPath,
        string hostRootPath,
        string outputRootPath,
        string stateRootPath,
        AuthorizedStateAccess access,
        IDisposable guard)
    {
        this.owner = owner;
        this.guard = guard;
        RootPath = rootPath;
        InputRootPath = inputRootPath;
        HostRootPath = hostRootPath;
        OutputRootPath = outputRootPath;
        StateRootPath = stateRootPath;
        Access = access;
    }

    internal string RootPath { get; }

    internal string InputRootPath { get; }

    internal string HostRootPath { get; }

    internal string OutputRootPath { get; }

    internal string StateRootPath { get; }

    internal AuthorizedStateAccess Access { get; }

    internal bool BelongsTo(object candidate) => ReferenceEquals(owner, candidate);

    internal object? Guard => guard;

    public void Dispose() => Interlocked.Exchange(ref guard, null)?.Dispose();

    internal static LiveAgentFreshProcessAuthorizedRoot Create(
        object owner,
        string rootPath,
        string inputRootPath,
        string hostRootPath,
        string outputRootPath,
        string stateRootPath,
        AuthorizedStateAccess access,
        IDisposable guard) => new(
            owner,
            rootPath,
            inputRootPath,
            hostRootPath,
            outputRootPath,
            stateRootPath,
            access,
            guard);
}

internal interface ILiveAgentFreshProcessFileSystem
{
    LiveAgentFreshProcessAuthorizationRead? ReadAuthorization();

    bool TryAuthorizeLayout(
        LiveAgentFreshProcessAuthorizationRead authorization,
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

    public LiveAgentFreshProcessAuthorizationRead? ReadAuthorization()
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
            ? LiveAgentFreshProcessAuthorizationRead.Create(this, read, proof)
            : null;
    }

    public bool TryAuthorizeLayout(
        LiveAgentFreshProcessAuthorizationRead authorization,
        AuthorizedStateAccess access,
        bool lineageExpected,
        out LiveAgentFreshProcessAuthorizedRoot? authorizedRoot)
    {
        authorizedRoot = null;
        if (authorization is null ||
            !authorization.BelongsTo(this) ||
            access is null ||
            !RestrictedStateValidation.IsValidScope(access.Scope) ||
            authorization.RootProof.IsDefaultOrEmpty ||
            !TryOpenAuthorizedLayout(
                authorization,
                lineageExpected,
                out var guard,
                out var rootPath,
                out var inputPath,
                out var hostPath,
                out var outputPath,
                out var statePath))
        {
            return false;
        }

        authorizedRoot = LiveAgentFreshProcessAuthorizedRoot.Create(
            this,
            rootPath!,
            inputPath!,
            hostPath!,
            outputPath!,
            statePath!,
            access,
            guard!);
        return true;
    }

    public LiveAgentFreshProcessRead? ReadReviewedInput(
        LiveAgentFreshProcessAuthorizedRoot rootCapability) => ReadAuthorized(
            rootCapability,
            Path.Join(
                rootCapability.InputRootPath,
                "reviewed-input.json"),
            LiveAgentFreshProcessCodec.ReviewedInputBytes);

    public LiveAgentFreshProcessRead? ReadSnapshotManifest(
        LiveAgentFreshProcessAuthorizedRoot rootCapability) => ReadAuthorized(
            rootCapability,
            Path.Join(
                rootCapability.InputRootPath,
                "snapshot-manifest.json"),
            LiveAgentFreshProcessCodec.SnapshotManifestBytes);

    public LiveAgentFreshProcessRead? ReadLineage(
        LiveAgentFreshProcessAuthorizedRoot rootCapability) => ReadAuthorized(
            rootCapability,
            Path.Join(
                rootCapability.HostRootPath,
                "accepted-lineage.json"),
            LiveAgentFreshProcessCodec.LineageBytes);

    public LiveAgentFreshProcessAtomicWriteReceipt? PublishLineage(
        LiveAgentFreshProcessAuthorizedRoot rootCapability,
        byte[] bytes,
        LiveAgentFreshProcessFileVersion? expectedPrior) => WriteAuthorized(
            rootCapability,
            Path.Join(
                rootCapability.HostRootPath,
                "accepted-lineage.json"),
            bytes,
            LiveAgentFreshProcessCodec.LineageBytes,
            expectedPrior,
            allowReplace: expectedPrior is not null);

    public LiveAgentFreshProcessAtomicWriteReceipt? PublishResult(
        LiveAgentFreshProcessAuthorizedRoot rootCapability,
        byte[] bytes) => WriteAuthorized(
            rootCapability,
            Path.Join(rootCapability.OutputRootPath, "result.json"),
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
            if (capability.Guard is not AuthorizedLayoutGuard guard ||
                !guard.TrySyncParent(path))
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
        RestrictedStateValidation.IsValidScope(capability.Access.Scope) &&
        capability.Guard is AuthorizedLayoutGuard guard &&
        guard.IsCurrent;

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

    private static bool TryInspectDirectory(string path) =>
        TryInspectDirectory(path, out _);

    private static bool TryInspectDirectory(
        string path,
        out RestrictedStateFileIdentity identity)
    {
        identity = default;
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
                        out identity);
            }
            catch (Exception exception) when (exception is IOException or
                UnauthorizedAccessException)
            {
                return false;
            }
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

    private bool TryOpenAuthorizedLayout(
        LiveAgentFreshProcessAuthorizationRead authorization,
        bool lineageExpected,
        out AuthorizedLayoutGuard? guard,
        out string? rootPath,
        out string? inputPath,
        out string? hostPath,
        out string? outputPath,
        out string? statePath)
    {
        guard = null;
        rootPath = null;
        inputPath = null;
        hostPath = null;
        outputPath = null;
        statePath = null;
        string? privatePath = null;
        var handles = ImmutableArray.CreateBuilder<SafeFileHandle>();
        var identities = ImmutableArray.CreateBuilder<
            RestrictedStateFileIdentity>();
        var paths = ImmutableArray.CreateBuilder<string>();
        var pathChecks = ImmutableArray.CreateBuilder<bool>();
        try
        {
            if (!PathComparer.Equals(
                    authorization.RootProof[0].Path,
                    root))
            {
                return false;
            }

            foreach (var expected in authorization.RootProof)
            {
                if (!TryOpenDirectoryGuard(
                        expected.Path,
                        expected.Identity,
                        out var handle,
                        out var identity))
                {
                    return false;
                }

                handles.Add(handle!);
                identities.Add(identity);
                paths.Add(expected.Path);
                pathChecks.Add(true);
            }

            rootPath = NativeRestrictedStateFiles.AnchoredRoot(
                root,
                handles[0]);
            if (!MatchesEntries(rootPath, RootEntries))
            {
                return false;
            }

            foreach (var name in RootEntries)
            {
                var configured = Path.Join(rootPath, name);
                if (!TryOpenDirectoryGuard(
                        configured,
                        expected: null,
                        out var handle,
                        out var identity))
                {
                    return false;
                }

                var anchored = NativeRestrictedStateFiles.AnchoredRoot(
                    configured,
                    handle!);
                handles.Add(handle!);
                identities.Add(identity);
                paths.Add(anchored);
                pathChecks.Add(OperatingSystem.IsWindows());
                switch (name)
                {
                    case "input":
                        inputPath = anchored;
                        break;
                    case "host":
                        hostPath = anchored;
                        break;
                    case "output":
                        outputPath = anchored;
                        break;
                    case "private":
                        privatePath = anchored;
                        break;
                    case "state":
                        statePath = anchored;
                        break;
                }
            }

            if (inputPath is null ||
                hostPath is null ||
                outputPath is null ||
                privatePath is null ||
                statePath is null ||
                !MatchesEntries(inputPath, InputEntries) ||
                !MatchesEntries(
                    hostPath,
                    lineageExpected
                        ? ["accepted-lineage.json", "authorization.json"]
                        : ["authorization.json"]) ||
                !MatchesEntries(privatePath, []) ||
                !MatchesEntries(outputPath, []))
            {
                return false;
            }

            var currentAuthorization = ReadFixed(
                Path.Join(hostPath, "authorization.json"),
                LiveAgentFreshProcessCodec.AuthorizationBytes);
            if (currentAuthorization is null ||
                currentAuthorization.Version != authorization.Version ||
                !currentAuthorization.Bytes.SequenceEqual(
                    authorization.Bytes))
            {
                return false;
            }

            var candidate = new AuthorizedLayoutGuard(
                handles.ToImmutable(),
                identities.ToImmutable(),
                paths.ToImmutable(),
                pathChecks.ToImmutable());
            if (!candidate.IsCurrent)
            {
                candidate.Dispose();
                return false;
            }

            guard = candidate;
            return true;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            return false;
        }
        finally
        {
            if (guard is null)
            {
                foreach (var handle in handles)
                {
                    handle.Dispose();
                }
            }
        }
    }

    private static bool TryOpenDirectoryGuard(
        string path,
        RestrictedStateFileIdentity? expected,
        out SafeFileHandle? handle,
        out RestrictedStateFileIdentity identity)
    {
        identity = default;
        var open = NativeRestrictedStateFiles.OpenRootGuardNoFollow(
            path,
            out handle);
        if (open != RestrictedStateOpenResult.Success ||
            handle is null ||
            !TryGetDirectoryIdentity(handle, out identity) ||
            expected is { } required && identity != required)
        {
            handle?.Dispose();
            handle = null;
            return false;
        }

        return true;
    }

    private static bool TryGetDirectoryIdentity(
        SafeFileHandle handle,
        out RestrictedStateFileIdentity identity)
    {
        identity = default;
        try
        {
            var attributes = File.GetAttributes(handle);
            return (attributes & FileAttributes.Directory) != 0 &&
                (attributes & FileAttributes.ReparsePoint) == 0 &&
                NativeRestrictedStateFiles.TryGetIdentity(
                    handle,
                    expectDirectory: true,
                    out identity);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class AuthorizedLayoutGuard(
        ImmutableArray<SafeFileHandle> handles,
        ImmutableArray<RestrictedStateFileIdentity> identities,
        ImmutableArray<string> paths,
        ImmutableArray<bool> pathChecks) : IDisposable
    {
        private int disposed;

        internal bool IsCurrent
        {
            get
            {
                if (Volatile.Read(ref disposed) != 0 ||
                    handles.Length != identities.Length ||
                    handles.Length != paths.Length ||
                    handles.Length != pathChecks.Length)
                {
                    return false;
                }

                for (var index = 0; index < handles.Length; index++)
                {
                    if (!TryGetDirectoryIdentity(
                            handles[index],
                            out var identity) ||
                        identity != identities[index] ||
                        pathChecks[index] &&
                            (!TryInspectDirectory(
                                paths[index],
                                out var pathIdentity) ||
                             pathIdentity != identities[index]))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        internal bool TrySyncParent(string path)
        {
            var parent = Path.GetDirectoryName(path);
            if (parent is null || Volatile.Read(ref disposed) != 0)
            {
                return false;
            }

            for (var index = 0; index < paths.Length; index++)
            {
                if (PathComparer.Equals(paths[index], parent) &&
                    TryGetDirectoryIdentity(
                        handles[index],
                        out var identity) &&
                    identity == identities[index])
                {
                    return NativeRestrictedStateFiles.TrySyncDirectory(
                        handles[index]);
                }
            }

            return false;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            foreach (var handle in handles)
            {
                handle.Dispose();
            }
        }
    }

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
