using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using Microsoft.Win32.SafeHandles;

namespace AgenticPrReview.Runtime.Host.State;

internal sealed class LocalRestrictedStateStore : IRestrictedStateStore
{
    private static readonly ConcurrentDictionary<string, object> ScopeLocks =
        new(StringComparer.Ordinal);
    private readonly string configuredRoot;
    private readonly Action? beforeWriteTestHook;
    private readonly Action? afterTemporaryFlushTestHook;
    private readonly Action? afterFinalRootProofTestHook;
    private readonly Func<string, bool>? deleteTemporaryTestHook;
    private readonly Func<string, bool>? syncDirectoryTestHook;

    internal LocalRestrictedStateStore(
        string explicitTestOwnedRoot,
        Action? beforeWriteTestHook = null,
        Action? afterTemporaryFlushTestHook = null,
        Func<string, bool>? deleteTemporaryTestHook = null,
        Func<string, bool>? syncDirectoryTestHook = null,
        Action? afterFinalRootProofTestHook = null)
    {
        if (string.IsNullOrWhiteSpace(explicitTestOwnedRoot))
        {
            throw new ArgumentException(
                "An explicit test-owned state root is required.",
                nameof(explicitTestOwnedRoot));
        }

        configuredRoot = explicitTestOwnedRoot;
        this.beforeWriteTestHook = beforeWriteTestHook;
        this.afterTemporaryFlushTestHook =
            afterTemporaryFlushTestHook;
        this.afterFinalRootProofTestHook =
            afterFinalRootProofTestHook;
        this.deleteTemporaryTestHook = deleteTemporaryTestHook;
        this.syncDirectoryTestHook = syncDirectoryTestHook;
    }

    public RestrictedStateStoreRead Read(
        AuthorizedStateAccess access,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryResolveRoot(access, out var root) ||
            !TryResolveScopePath(access, root, out var path))
        {
            return ReadFailure(RestrictedStateStoreFailure.Invalid);
        }

        if (!TryCaptureRootProof(
                root,
                out var rootProof,
                out var rootFailure))
        {
            return ReadFailure(rootFailure);
        }

        var guardOpen = NativeRestrictedStateFiles.OpenRootGuardNoFollow(
            root,
            out var rootGuard);
        if (guardOpen != RestrictedStateOpenResult.Success ||
            rootGuard is null)
        {
            return ReadFailure(MapOpenFailure(guardOpen));
        }

        using var readRootGuard = rootGuard;
        if (!RootGuardMatchesProof(readRootGuard, rootProof) ||
            !TryResolveScopePath(
                access,
                NativeRestrictedStateFiles.AnchoredRoot(
                    root,
                    readRootGuard),
                out var operationPath))
        {
            return ReadFailure(RestrictedStateStoreFailure.Invalid);
        }

        lock (ScopeLocks.GetOrAdd(path, static _ => new object()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!RootProofIsCurrent(root, rootProof))
            {
                return ReadFailure(RestrictedStateStoreFailure.Invalid);
            }

            return ReadUnderLock(
                access,
                operationPath,
                cancellationToken);
        }
    }

    public RestrictedStateStoreWrite CompareExchange(
        AuthorizedStateAccess access,
        RestrictedStateSnapshotVersion expected,
        RestrictedStateSnapshot replacement,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expected is null ||
            replacement is null ||
            !RestrictedStateValidation.IsValidSnapshot(replacement) ||
            !TryResolveRoot(access, out var root) ||
            !TryResolveScopePath(access, root, out var path))
        {
            return WriteFailure(RestrictedStateStoreFailure.Invalid);
        }

        if (!TryCaptureRootProof(
                root,
                out var rootProof,
                out var rootFailure))
        {
            return WriteFailure(rootFailure);
        }

        var guardOpen = NativeRestrictedStateFiles.OpenRootGuardNoFollow(
            root,
            out var rootGuard);
        if (guardOpen != RestrictedStateOpenResult.Success ||
            rootGuard is null)
        {
            return WriteFailure(MapOpenFailure(guardOpen));
        }

        using var writeRootGuard = rootGuard;
        var operationRoot = NativeRestrictedStateFiles.AnchoredRoot(
            root,
            writeRootGuard);
        if (!RootGuardMatchesProof(writeRootGuard, rootProof) ||
            !TryResolveScopePath(
                access,
                operationRoot,
                out var operationPath))
        {
            return WriteFailure(RestrictedStateStoreFailure.Invalid);
        }

        lock (ScopeLocks.GetOrAdd(path, static _ => new object()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!RootProofIsCurrent(root, rootProof))
            {
                return WriteFailure(RestrictedStateStoreFailure.Invalid);
            }

            var current = ReadUnderLock(
                access,
                operationPath,
                cancellationToken);
            if (!current.Succeeded)
            {
                return WriteFailure(current.Failure);
            }

            if (current.Version != expected)
            {
                return WriteFailure(
                    RestrictedStateStoreFailure.Conflict);
            }

            if (!RestrictedStateSnapshotCodec.TryWrite(
                    replacement,
                    out var bytes))
            {
                return WriteFailure(
                    RestrictedStateStoreFailure.Invalid);
            }

            beforeWriteTestHook?.Invoke();
            if (!RootProofIsCurrent(root, rootProof))
            {
                return WriteFailure(RestrictedStateStoreFailure.Invalid);
            }

            var temporaryPath = Path.Join(
                operationRoot,
                $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            var committed = false;
            RestrictedStateStoreWrite result;
            try
            {
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 16 * 1024,
                    FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                afterTemporaryFlushTestHook?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                if (!RootProofIsCurrent(root, rootProof))
                {
                    result = WriteFailure(
                        RestrictedStateStoreFailure.Invalid);
                }
                else
                {
                    afterFinalRootProofTestHook?.Invoke();
                    var attributes = File.GetAttributes(temporaryPath);
                    if ((attributes &
                        (FileAttributes.Directory |
                            FileAttributes.ReparsePoint)) != 0)
                    {
                        result = WriteFailure(
                            RestrictedStateStoreFailure.Invalid);
                    }
                    else
                    {
                        var backupPath = expected.Exists
                            ? Path.Join(
                                operationRoot,
                                $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.rollback")
                            : null;
                        if (backupPath is null)
                        {
                            File.Move(
                                temporaryPath,
                                operationPath,
                                overwrite: false);
                        }
                        else
                        {
                            File.Replace(
                                temporaryPath,
                                operationPath,
                                backupPath);
                        }

                        committed = true;
                        if (!RootProofIsCurrent(root, rootProof))
                        {
                            if (TryRollbackReplacement(
                                    operationPath,
                                    backupPath))
                            {
                                committed = false;
                                result = WriteFailure(
                                    RestrictedStateStoreFailure.Invalid);
                            }
                            else
                            {
                                result = WriteFailure(
                                    RestrictedStateStoreFailure.Io);
                            }
                        }
                        else if (backupPath is not null &&
                            !TryDeleteTemporaryFile(backupPath))
                        {
                            if (TryRollbackReplacement(
                                    operationPath,
                                    backupPath))
                            {
                                committed = false;
                                result = WriteFailure(
                                    RestrictedStateStoreFailure.Cleanup);
                            }
                            else
                            {
                                result = WriteFailure(
                                    RestrictedStateStoreFailure.Io);
                            }
                        }
                        else if (!SyncDirectory(root, writeRootGuard))
                        {
                            result = WriteFailure(
                                RestrictedStateStoreFailure.Io,
                                committed: true);
                        }
                        else
                        {
                            var sha = SnapshotSha256(bytes);
                            result = new RestrictedStateStoreWrite(
                                RestrictedStateStoreFailure.None,
                                new RestrictedStateSnapshotVersion(
                                    sha,
                                    true),
                                Committed: true);
                        }
                    }
                }

            }
            catch (OperationCanceledException) when (!committed)
            {
                if (!TryDeleteTemporaryFile(temporaryPath))
                {
                    return WriteFailure(
                        RestrictedStateStoreFailure.Cleanup);
                }

                throw;
            }
            catch (IOException)
            {
                result = WriteFailure(
                    RestrictedStateStoreFailure.Io,
                    committed);
            }
            catch (UnauthorizedAccessException)
            {
                result = WriteFailure(
                    RestrictedStateStoreFailure.Io,
                    committed);
            }

            if (!committed &&
                !TryDeleteTemporaryFile(temporaryPath))
            {
                return WriteFailure(
                    RestrictedStateStoreFailure.Cleanup);
            }

            return result;
        }
    }

    public RestrictedStateStoreRawRead ReadRawVersion(
        AuthorizedStateAccess access,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryResolveRoot(access, out var root) ||
            !TryResolveScopePath(access, root, out var path))
        {
            return RawReadFailure(RestrictedStateStoreFailure.Invalid);
        }

        if (!TryCaptureRootProof(
                root,
                out var rootProof,
                out var rootFailure))
        {
            return RawReadFailure(rootFailure);
        }

        var guardOpen = NativeRestrictedStateFiles.OpenRootGuardNoFollow(
            root,
            out var rootGuard);
        if (guardOpen != RestrictedStateOpenResult.Success ||
            rootGuard is null)
        {
            return RawReadFailure(MapOpenFailure(guardOpen));
        }

        using var readRootGuard = rootGuard;
        if (!RootGuardMatchesProof(readRootGuard, rootProof) ||
            !TryResolveScopePath(
                access,
                NativeRestrictedStateFiles.AnchoredRoot(
                    root,
                    readRootGuard),
                out var operationPath))
        {
            return RawReadFailure(RestrictedStateStoreFailure.Invalid);
        }

        lock (ScopeLocks.GetOrAdd(path, static _ => new object()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!RootProofIsCurrent(root, rootProof))
            {
                return RawReadFailure(
                    RestrictedStateStoreFailure.Invalid);
            }

            return ReadRawVersionUnderLock(operationPath);
        }
    }

    public RestrictedStateStoreWrite CompareDelete(
        AuthorizedStateAccess access,
        RestrictedStateSnapshotVersion expected,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expected is null ||
            !TryResolveRoot(access, out var root) ||
            !TryResolveScopePath(access, root, out var path))
        {
            return WriteFailure(RestrictedStateStoreFailure.Invalid);
        }

        if (!TryCaptureRootProof(
                root,
                out var rootProof,
                out var rootFailure))
        {
            return WriteFailure(rootFailure);
        }

        var guardOpen = NativeRestrictedStateFiles.OpenRootGuardNoFollow(
            root,
            out var rootGuard);
        if (guardOpen != RestrictedStateOpenResult.Success ||
            rootGuard is null)
        {
            return WriteFailure(MapOpenFailure(guardOpen));
        }

        using var deleteRootGuard = rootGuard;
        var operationRoot = NativeRestrictedStateFiles.AnchoredRoot(
            root,
            deleteRootGuard);
        if (!RootGuardMatchesProof(deleteRootGuard, rootProof) ||
            !TryResolveScopePath(
                access,
                operationRoot,
                out var operationPath))
        {
            return WriteFailure(RestrictedStateStoreFailure.Invalid);
        }

        lock (ScopeLocks.GetOrAdd(path, static _ => new object()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!RootProofIsCurrent(root, rootProof))
            {
                return WriteFailure(RestrictedStateStoreFailure.Invalid);
            }

            var current = ReadUnderLock(
                access,
                operationPath,
                cancellationToken);
            if (!current.Succeeded)
            {
                return WriteFailure(current.Failure);
            }

            if (current.Version != expected)
            {
                return WriteFailure(
                    RestrictedStateStoreFailure.Conflict);
            }

            if (!expected.Exists)
            {
                return new RestrictedStateStoreWrite(
                    RestrictedStateStoreFailure.None,
                    RestrictedStateSnapshotVersion.Absent,
                    Committed: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var revalidated = ReadUnderLock(
                access,
                operationPath,
                cancellationToken);
            if (!revalidated.Succeeded ||
                revalidated.Version != expected)
            {
                return WriteFailure(
                    revalidated.Succeeded
                        ? RestrictedStateStoreFailure.Conflict
                        : revalidated.Failure);
            }

            return DeleteRegularFileUnderLock(
                root,
                rootProof,
                deleteRootGuard,
                operationPath,
                cancellationToken);
        }
    }

    public RestrictedStateStoreWrite CompareDeleteRaw(
        AuthorizedStateAccess access,
        RestrictedStateRawVersion expected,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expected is null ||
            !TryResolveRoot(access, out var root) ||
            !TryResolveScopePath(access, root, out var path))
        {
            return WriteFailure(RestrictedStateStoreFailure.Invalid);
        }

        if (!TryCaptureRootProof(
                root,
                out var rootProof,
                out var rootFailure))
        {
            return WriteFailure(rootFailure);
        }

        var guardOpen = NativeRestrictedStateFiles.OpenRootGuardNoFollow(
            root,
            out var rootGuard);
        if (guardOpen != RestrictedStateOpenResult.Success ||
            rootGuard is null)
        {
            return WriteFailure(MapOpenFailure(guardOpen));
        }

        using var deleteRootGuard = rootGuard;
        var operationRoot = NativeRestrictedStateFiles.AnchoredRoot(
            root,
            deleteRootGuard);
        if (!RootGuardMatchesProof(deleteRootGuard, rootProof) ||
            !TryResolveScopePath(
                access,
                operationRoot,
                out var operationPath))
        {
            return WriteFailure(RestrictedStateStoreFailure.Invalid);
        }

        lock (ScopeLocks.GetOrAdd(path, static _ => new object()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!RootProofIsCurrent(root, rootProof))
            {
                return WriteFailure(RestrictedStateStoreFailure.Invalid);
            }

            var current = ReadRawVersionUnderLock(operationPath);
            if (!current.Succeeded)
            {
                return WriteFailure(current.Failure);
            }

            if (current.Version != expected)
            {
                return WriteFailure(
                    RestrictedStateStoreFailure.Conflict);
            }

            if (!expected.Exists)
            {
                return new RestrictedStateStoreWrite(
                    RestrictedStateStoreFailure.None,
                    RestrictedStateSnapshotVersion.Absent,
                    Committed: true);
            }

            return DeleteRegularFileUnderLock(
                root,
                rootProof,
                deleteRootGuard,
                operationPath,
                cancellationToken);
        }
    }

    private RestrictedStateStoreRead ReadUnderLock(
        AuthorizedStateAccess access,
        string path,
        CancellationToken cancellationToken)
    {
        var openResult = TryOpenRead(path, out var firstHandle);
        if (openResult == RestrictedStateOpenResult.NotFound)
        {
            return new RestrictedStateStoreRead(
                RestrictedStateStoreFailure.None,
                RestrictedStateSnapshot.Empty,
                RestrictedStateSnapshotVersion.Absent);
        }

        if (openResult != RestrictedStateOpenResult.Success ||
            firstHandle is null)
        {
            return ReadFailure(
                openResult == RestrictedStateOpenResult.Io
                    ? RestrictedStateStoreFailure.Io
                    : RestrictedStateStoreFailure.Invalid);
        }

        using (firstHandle)
        {
            try
            {
                if (!TryInspectRegular(
                    firstHandle,
                    out var firstIdentity,
                    out var length) ||
                length is < 1 or >
                    RestrictedStateSnapshotCodec.MaximumSnapshotBytes)
            {
                return ReadFailure(
                    RestrictedStateStoreFailure.Invalid);
            }

            var bytes = GC.AllocateUninitializedArray<byte>(
                checked((int)length));
            var offset = 0;
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = RandomAccess.Read(
                    firstHandle,
                    bytes.AsSpan(offset),
                    offset);
                if (read == 0)
                {
                    return ReadFailure(
                        RestrictedStateStoreFailure.Invalid);
                }

                offset += read;
            }

            if (!TryInspectRegular(
                    firstHandle,
                    out var afterReadIdentity,
                    out var afterReadLength) ||
                afterReadIdentity != firstIdentity ||
                afterReadLength != length ||
                !TryPathStillNamesIdentity(path, firstIdentity) ||
                !RestrictedStateSnapshotCodec.TryRead(
                    bytes,
                    access.Scope,
                    out var snapshot))
            {
                return ReadFailure(
                    RestrictedStateStoreFailure.Invalid);
            }

                return new RestrictedStateStoreRead(
                    RestrictedStateStoreFailure.None,
                    snapshot,
                    new RestrictedStateSnapshotVersion(
                        SnapshotSha256(bytes),
                        true));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException)
            {
                return ReadFailure(RestrictedStateStoreFailure.Io);
            }
            catch (UnauthorizedAccessException)
            {
                return ReadFailure(RestrictedStateStoreFailure.Io);
            }
        }
    }

    private static RestrictedStateStoreRawRead ReadRawVersionUnderLock(
        string path)
    {
        var openResult = TryOpenRead(path, out var handle);
        if (openResult == RestrictedStateOpenResult.NotFound)
        {
            return new RestrictedStateStoreRawRead(
                RestrictedStateStoreFailure.None,
                RestrictedStateRawVersion.Absent);
        }

        if (openResult != RestrictedStateOpenResult.Success ||
            handle is null)
        {
            return RawReadFailure(
                openResult == RestrictedStateOpenResult.Io
                    ? RestrictedStateStoreFailure.Io
                    : RestrictedStateStoreFailure.Invalid);
        }

        using (handle)
        {
            if (!TryInspectRegular(
                    handle,
                    out var identity,
                    out var length) ||
                !TryPathStillNamesIdentity(path, identity))
            {
                return RawReadFailure(
                    RestrictedStateStoreFailure.Invalid);
            }

            return new RestrictedStateStoreRawRead(
                RestrictedStateStoreFailure.None,
                new RestrictedStateRawVersion(
                    $"{identity.Device:x16}{identity.File:x16}",
                    length,
                    Exists: true));
        }
    }

    private RestrictedStateStoreWrite DeleteRegularFileUnderLock(
        string root,
        ImmutableArray<RestrictedStateRootEntry> rootProof,
        SafeFileHandle rootGuard,
        string operationPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!RootProofIsCurrent(root, rootProof))
        {
            return WriteFailure(RestrictedStateStoreFailure.Invalid);
        }

        afterFinalRootProofTestHook?.Invoke();
        var tombstonePath = Path.Join(
            Path.GetDirectoryName(operationPath),
            $".{Path.GetFileName(operationPath)}.{Guid.NewGuid():N}.delete");
        try
        {
            File.Move(operationPath, tombstonePath, overwrite: false);
            if (!RootProofIsCurrent(root, rootProof))
            {
                return TryRestoreMovedFile(
                        tombstonePath,
                        operationPath)
                    ? WriteFailure(RestrictedStateStoreFailure.Invalid)
                    : WriteFailure(RestrictedStateStoreFailure.Io);
            }

            if (!TryDeleteTemporaryFile(tombstonePath))
            {
                return TryRestoreMovedFile(
                        tombstonePath,
                        operationPath)
                    ? WriteFailure(RestrictedStateStoreFailure.Cleanup)
                    : WriteFailure(RestrictedStateStoreFailure.Io);
            }

            if (!SyncDirectory(root, rootGuard))
            {
                return WriteFailure(
                    RestrictedStateStoreFailure.Io,
                    committed: true);
            }

            return new RestrictedStateStoreWrite(
                RestrictedStateStoreFailure.None,
                RestrictedStateSnapshotVersion.Absent,
                Committed: true);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException)
        {
            _ = TryRestoreMovedFile(
                tombstonePath,
                operationPath);

            return WriteFailure(RestrictedStateStoreFailure.Io);
        }
    }

    private bool TryRollbackReplacement(
        string operationPath,
        string? backupPath)
    {
        try
        {
            if (backupPath is null)
            {
                return TryDeleteTemporaryFile(operationPath);
            }

            File.Replace(
                backupPath,
                operationPath,
                destinationBackupFileName: null);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryRestoreMovedFile(
        string source,
        string destination)
    {
        try
        {
            File.Move(source, destination, overwrite: false);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool TryResolveRoot(
        AuthorizedStateAccess access,
        out string root)
    {
        root = string.Empty;
        if (access is null ||
            !RestrictedStateValidation.IsValidScope(access.Scope))
        {
            return false;
        }

        try
        {
            root = Path.GetFullPath(configuredRoot);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                IOException or
                NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryResolveScopePath(
        AuthorizedStateAccess access,
        string root,
        out string path)
    {
        path = string.Empty;
        var scopeBytes =
            RestrictedStateSnapshotCodec.WriteScopeIdentity(access.Scope);
        var scopeSha = AgentCanonical.HashDomain(
            "apr.state-scope.r2",
            scopeBytes);
        path = Path.Join(root, $"scope-{scopeSha}.aprstate");
        return StringComparer.Ordinal.Equals(
            Path.GetDirectoryName(path),
            root);
    }

    private static bool TryCaptureRootProof(
        string root,
        out ImmutableArray<RestrictedStateRootEntry> proof,
        out RestrictedStateStoreFailure failure)
    {
        failure = RestrictedStateStoreFailure.Invalid;
        try
        {
            var entries = ImmutableArray.CreateBuilder<
                RestrictedStateRootEntry>();
            var current = new DirectoryInfo(root);
            while (current is not null)
            {
                var open =
                    NativeRestrictedStateFiles.OpenDirectoryNoFollow(
                        current.FullName,
                        out var handle);
                if (open != RestrictedStateOpenResult.Success ||
                    handle is null)
                {
                    proof = [];
                    failure = open == RestrictedStateOpenResult.Io
                        ? RestrictedStateStoreFailure.Io
                        : RestrictedStateStoreFailure.Invalid;
                    return false;
                }

                using (handle)
                {
                    var attributes = File.GetAttributes(handle);
                    if ((attributes & FileAttributes.Directory) == 0 ||
                        (attributes &
                            FileAttributes.ReparsePoint) != 0 ||
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
            failure = RestrictedStateStoreFailure.None;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                ArgumentException)
        {
            proof = [];
            failure = RestrictedStateStoreFailure.Io;
            return false;
        }
    }

    private static bool RootProofIsCurrent(
        string root,
        ImmutableArray<RestrictedStateRootEntry> expected) =>
        TryCaptureRootProof(root, out var actual, out _) &&
        actual.SequenceEqual(expected);

    private static bool RootGuardMatchesProof(
        SafeFileHandle guard,
        ImmutableArray<RestrictedStateRootEntry> proof) =>
        !proof.IsDefaultOrEmpty &&
        NativeRestrictedStateFiles.TryGetIdentity(
            guard,
            expectDirectory: true,
            out var identity) &&
        identity == proof[0].Identity;

    private static bool TryPathStillNamesIdentity(
        string path,
        RestrictedStateFileIdentity expected)
    {
        var open = TryOpenRead(path, out var secondHandle);
        if (open != RestrictedStateOpenResult.Success ||
            secondHandle is null)
        {
            return false;
        }

        using (secondHandle)
        {
            return
                TryInspectRegular(
                    secondHandle,
                    out var actual,
                    out _) &&
                actual == expected;
        }
    }

    private static RestrictedStateOpenResult TryOpenRead(
        string path,
        out SafeFileHandle? handle)
    {
        handle = null;
        if (OperatingSystem.IsLinux() ||
            OperatingSystem.IsWindows())
        {
            return NativeRestrictedStateFiles.OpenFileNoFollow(
                path,
                out handle);
        }

        handle = null;
        return RestrictedStateOpenResult.Unsafe;
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
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string SnapshotSha256(ReadOnlySpan<byte> bytes) =>
        AgentCanonical.HashDomain(
            "apr.state-snapshot.r2",
            bytes);

    private bool TryDeleteTemporaryFile(string path)
    {
        if (deleteTemporaryTestHook is not null)
        {
            return deleteTemporaryTestHook(path);
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool SyncDirectory(
        string path,
        SafeFileHandle rootGuard) =>
        syncDirectoryTestHook?.Invoke(path) ??
        NativeRestrictedStateFiles.TrySyncDirectory(rootGuard);

    private static RestrictedStateStoreFailure MapOpenFailure(
        RestrictedStateOpenResult result) =>
        result == RestrictedStateOpenResult.Io
            ? RestrictedStateStoreFailure.Io
            : RestrictedStateStoreFailure.Invalid;

    private static RestrictedStateStoreRead ReadFailure(
        RestrictedStateStoreFailure failure) =>
        new(failure, null, null);

    private static RestrictedStateStoreRawRead RawReadFailure(
        RestrictedStateStoreFailure failure) =>
        new(failure, null);

    private static RestrictedStateStoreWrite WriteFailure(
        RestrictedStateStoreFailure failure,
        bool committed = false) =>
        new(failure, null, committed);
}

internal static class RestrictedStateSnapshotCodec
{
    private static readonly byte[] Magic =
        Encoding.ASCII.GetBytes("APRLOC01");
    private static readonly UTF8Encoding StrictUtf8 =
        new(false, true);
    internal const ushort Version = 1;
    internal const int FixedFramingAllowance = 4_096;
    internal const int MaximumSnapshotBytes =
        AgentLimits.StateScopeTotalBytes +
        AgentLimits.CandidateMetadataBytes +
        FixedFramingAllowance;

    internal static bool TryWrite(
        RestrictedStateSnapshot snapshot,
        out byte[] bytes)
    {
        bytes = [];
        if (!RestrictedStateValidation.IsValidSnapshot(snapshot))
        {
            return false;
        }

        try
        {
            var writer = new ArrayBufferWriter<byte>(
                Math.Min(MaximumSnapshotBytes, 16 * 1024));
            Write(writer, Magic);
            WriteUInt16(writer, Version);
            WriteByte(writer, checked((byte)snapshot.Accepted.Length));
            foreach (var candidate in snapshot.Accepted)
            {
                WriteCandidate(writer, candidate);
            }

            WriteByte(writer, snapshot.Staging is null ? (byte)0 : (byte)1);
            if (snapshot.Staging is not null)
            {
                WriteCandidate(writer, snapshot.Staging);
            }

            if (writer.WrittenCount > MaximumSnapshotBytes)
            {
                return false;
            }

            bytes = writer.WrittenMemory.ToArray();
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                EncoderFallbackException or
                FormatException or
                OverflowException)
        {
            return false;
        }
    }

    internal static bool TryRead(
        ReadOnlySpan<byte> bytes,
        RestrictedStateScope expectedScope,
        out RestrictedStateSnapshot? snapshot)
    {
        snapshot = null;
        if (bytes.Length is < 1 or > MaximumSnapshotBytes)
        {
            return false;
        }

        var offset = 0;
        if (!TryReadBytes(bytes, ref offset, Magic.Length, out var magic) ||
            !magic.SequenceEqual(Magic) ||
            !TryReadUInt16(bytes, ref offset, out var version) ||
            version != Version ||
            !TryReadByte(bytes, ref offset, out var acceptedCount) ||
            acceptedCount > AgentLimits.AcceptedCandidates)
        {
            return false;
        }

        var accepted = ImmutableArray.CreateBuilder<
            RestrictedStateCandidate>(acceptedCount);
        for (var index = 0; index < acceptedCount; index++)
        {
            if (!TryReadCandidate(
                    bytes,
                    ref offset,
                    expectedScope,
                    out var candidate))
            {
                return false;
            }

            accepted.Add(candidate!);
        }

        if (!TryReadByte(bytes, ref offset, out var hasStaging) ||
            hasStaging > 1)
        {
            return false;
        }

        RestrictedStateCandidate? staging = null;
        if (hasStaging == 1 &&
            !TryReadCandidate(
                bytes,
                ref offset,
                expectedScope,
                out staging))
        {
            return false;
        }

        if (offset != bytes.Length)
        {
            return false;
        }

        var result = new RestrictedStateSnapshot(
            accepted.MoveToImmutable(),
            staging);
        if (!RestrictedStateValidation.IsValidSnapshot(result))
        {
            return false;
        }

        snapshot = result;
        return true;
    }

    internal static byte[] WriteScopeIdentity(
        RestrictedStateScope scope)
    {
        var writer = new ArrayBufferWriter<byte>(512);
        WriteScope(writer, scope);
        return writer.WrittenMemory.ToArray();
    }

    internal static int CandidateMetadataBytes(
        RestrictedStateCandidate candidate)
    {
        var writer = new ArrayBufferWriter<byte>(512);
        WriteCandidateMetadata(writer, candidate);
        WriteUInt32(writer, checked((uint)candidate.Envelope.Length));
        return writer.WrittenCount;
    }

    private static void WriteCandidate(
        IBufferWriter<byte> writer,
        RestrictedStateCandidate candidate)
    {
        WriteCandidateMetadata(writer, candidate);
        WriteUInt32(writer, checked((uint)candidate.Envelope.Length));
        Write(writer, candidate.Envelope);
    }

    private static void WriteCandidateMetadata(
        IBufferWriter<byte> writer,
        RestrictedStateCandidate candidate)
    {
        WriteScope(writer, candidate.Binding.Scope);
        WriteHex(writer, candidate.Binding.ProducerBaseSha, 20);
        WriteHex(writer, candidate.Binding.ProducerHeadSha, 20);
        WriteInt64(writer, candidate.Binding.Generation);
        WriteOptionalHash(
            writer,
            candidate.Binding.PredecessorEnvelopeSha256);
        WriteInt64(writer, candidate.Binding.AcceptedAtUnixSeconds);
        WriteInt64(writer, candidate.Binding.ExpiresAtUnixSeconds);
        WriteHex(writer, candidate.SessionSha256, 32);
        WriteHex(writer, candidate.EnvelopeSha256, 32);
        WriteHex(writer, candidate.ObjectIdentity, 32);
    }

    private static bool TryReadCandidate(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        RestrictedStateScope expectedScope,
        out RestrictedStateCandidate? candidate)
    {
        candidate = null;
        if (!TryReadScope(bytes, ref offset, out var scope) ||
            scope != expectedScope ||
            !TryReadHex(bytes, ref offset, 20, out var producerBase) ||
            !TryReadHex(bytes, ref offset, 20, out var producerHead) ||
            !TryReadInt64(bytes, ref offset, out var generation) ||
            !TryReadOptionalHash(
                bytes,
                ref offset,
                out var predecessor) ||
            !TryReadInt64(bytes, ref offset, out var acceptedAt) ||
            !TryReadInt64(bytes, ref offset, out var expiresAt) ||
            !TryReadHex(bytes, ref offset, 32, out var sessionSha) ||
            !TryReadHex(bytes, ref offset, 32, out var envelopeSha) ||
            !TryReadHex(bytes, ref offset, 32, out var objectIdentity) ||
            !TryReadUInt32(
                bytes,
                ref offset,
                out var envelopeLength) ||
            envelopeLength is 0 or > AgentLimits.StateEnvelopeBytes ||
            !TryReadBytes(
                bytes,
                ref offset,
                checked((int)envelopeLength),
                out var envelope))
        {
            return false;
        }

        var binding = new RestrictedStateBinding(
            scope!,
            producerBase!,
            producerHead!,
            generation,
            predecessor,
            acceptedAt,
            expiresAt);
        if (!StringComparer.Ordinal.Equals(
                envelopeSha,
                RestrictedStateEnvelope.EnvelopeSha256(envelope)) ||
            !StringComparer.Ordinal.Equals(
                objectIdentity,
                RestrictedStateEnvelope.ObjectIdentity(
                    binding,
                    sessionSha!,
                    envelopeSha!)))
        {
            return false;
        }

        candidate = new RestrictedStateCandidate(
            binding,
            sessionSha!,
            envelopeSha!,
            objectIdentity!,
            envelope.ToArray());
        return RestrictedStateValidation.IsValidCandidate(candidate);
    }

    private static void WriteScope(
        IBufferWriter<byte> writer,
        RestrictedStateScope scope)
    {
        WriteUtf8(writer, scope.RepositoryId);
        WriteUtf8(writer, scope.WorkflowIdentity);
        WriteInt64(writer, scope.ReviewTarget);
        WriteAscii(writer, scope.SessionId);
        WriteUtf8(writer, scope.ProviderId);
        WriteUtf8(writer, scope.ModelId);
        WriteUtf8(writer, scope.AdapterId);
        WriteHex(writer, scope.PolicySha256, 32);
        WriteHex(writer, scope.LimitsSha256, 32);
        WriteHex(writer, scope.ToolsetSha256, 32);
        WriteUtf8(writer, scope.BuildId);
    }

    private static bool TryReadScope(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        out RestrictedStateScope? scope)
    {
        scope = null;
        if (!TryReadUtf8(bytes, ref offset, 1, 128, out var repository) ||
            !TryReadUtf8(bytes, ref offset, 1, 256, out var workflow) ||
            !TryReadInt64(bytes, ref offset, out var reviewTarget) ||
            !TryReadAscii(bytes, ref offset, 1, 64, out var session) ||
            !TryReadUtf8(bytes, ref offset, 1, 128, out var provider) ||
            !TryReadUtf8(bytes, ref offset, 1, 128, out var model) ||
            !TryReadUtf8(bytes, ref offset, 1, 128, out var adapter) ||
            !TryReadHex(bytes, ref offset, 32, out var policySha) ||
            !TryReadHex(bytes, ref offset, 32, out var limitsSha) ||
            !TryReadHex(bytes, ref offset, 32, out var toolsetSha) ||
            !TryReadUtf8(bytes, ref offset, 1, 256, out var build))
        {
            return false;
        }

        scope = new RestrictedStateScope(
            repository!,
            workflow!,
            reviewTarget,
            session!,
            provider!,
            model!,
            adapter!,
            policySha!,
            limitsSha!,
            toolsetSha!,
            build!);
        return RestrictedStateValidation.IsValidScope(scope);
    }

    private static void WriteOptionalHash(
        IBufferWriter<byte> writer,
        string? value)
    {
        if (value is null)
        {
            WriteByte(writer, 0);
        }
        else
        {
            WriteByte(writer, 1);
            WriteHex(writer, value, 32);
        }
    }

    private static bool TryReadOptionalHash(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        out string? value)
    {
        value = null;
        if (!TryReadByte(bytes, ref offset, out var present) ||
            present > 1)
        {
            return false;
        }

        return present == 0 ||
            TryReadHex(bytes, ref offset, 32, out value);
    }

    private static void WriteUtf8(
        IBufferWriter<byte> writer,
        string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        WriteUInt16(writer, checked((ushort)bytes.Length));
        Write(writer, bytes);
    }

    private static void WriteAscii(
        IBufferWriter<byte> writer,
        string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        WriteUInt16(writer, checked((ushort)bytes.Length));
        Write(writer, bytes);
    }

    private static void WriteHex(
        IBufferWriter<byte> writer,
        string value,
        int bytes)
    {
        var decoded = Convert.FromHexString(value);
        if (decoded.Length != bytes)
        {
            throw new FormatException("Invalid fixed hash.");
        }

        Write(writer, decoded);
    }

    private static void WriteByte(
        IBufferWriter<byte> writer,
        byte value)
    {
        var destination = writer.GetSpan(1);
        destination[0] = value;
        writer.Advance(1);
    }

    private static void WriteUInt16(
        IBufferWriter<byte> writer,
        ushort value)
    {
        var destination = writer.GetSpan(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(destination, value);
        writer.Advance(sizeof(ushort));
    }

    private static void WriteUInt32(
        IBufferWriter<byte> writer,
        uint value)
    {
        var destination = writer.GetSpan(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
        writer.Advance(sizeof(uint));
    }

    private static void WriteInt64(
        IBufferWriter<byte> writer,
        long value)
    {
        var destination = writer.GetSpan(sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(destination, value);
        writer.Advance(sizeof(long));
    }

    private static void Write(
        IBufferWriter<byte> writer,
        ReadOnlySpan<byte> bytes)
    {
        var destination = writer.GetSpan(bytes.Length);
        bytes.CopyTo(destination);
        writer.Advance(bytes.Length);
    }

    private static bool TryReadByte(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        out byte value)
    {
        value = 0;
        if (bytes.Length - offset < 1)
        {
            return false;
        }

        value = bytes[offset++];
        return true;
    }

    private static bool TryReadUInt16(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        out ushort value)
    {
        value = 0;
        if (bytes.Length - offset < sizeof(ushort))
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
        offset += sizeof(ushort);
        return true;
    }

    private static bool TryReadUInt32(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        out uint value)
    {
        value = 0;
        if (bytes.Length - offset < sizeof(uint))
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
        offset += sizeof(uint);
        return true;
    }

    private static bool TryReadInt64(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        out long value)
    {
        value = 0;
        if (bytes.Length - offset < sizeof(long))
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt64LittleEndian(bytes[offset..]);
        offset += sizeof(long);
        return true;
    }

    private static bool TryReadBytes(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        int length,
        out ReadOnlySpan<byte> value)
    {
        value = default;
        if (length < 0 || bytes.Length - offset < length)
        {
            return false;
        }

        value = bytes.Slice(offset, length);
        offset += length;
        return true;
    }

    private static bool TryReadUtf8(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        int minimum,
        int maximum,
        out string? value)
    {
        value = null;
        if (!TryReadUInt16(bytes, ref offset, out var length) ||
            length < minimum ||
            length > maximum ||
            !TryReadBytes(bytes, ref offset, length, out var encoded))
        {
            return false;
        }

        try
        {
            value = StrictUtf8.GetString(encoded);
            return StrictUtf8.GetByteCount(value) == length;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryReadAscii(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        int minimum,
        int maximum,
        out string? value)
    {
        value = null;
        if (!TryReadUInt16(bytes, ref offset, out var length) ||
            length < minimum ||
            length > maximum ||
            !TryReadBytes(bytes, ref offset, length, out var encoded))
        {
            return false;
        }

        foreach (var current in encoded)
        {
            if (current > 0x7f)
            {
                return false;
            }
        }

        value = Encoding.ASCII.GetString(encoded);
        return true;
    }

    private static bool TryReadHex(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        int length,
        out string? value)
    {
        value = null;
        if (!TryReadBytes(bytes, ref offset, length, out var decoded))
        {
            return false;
        }

        value = Convert.ToHexStringLower(decoded);
        return true;
    }
}

internal readonly record struct RestrictedStateFileIdentity(
    ulong Device,
    ulong File);

internal readonly record struct RestrictedStateRootEntry(
    string Path,
    RestrictedStateFileIdentity Identity);

internal enum RestrictedStateOpenResult
{
    Success,
    NotFound,
    Unsafe,
    Io,
}

internal static partial class NativeRestrictedStateFiles
{
    private const int OpenReadOnly = 0;
    private const int OpenNonBlocking = 0x800;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const int OpenDirectory = 0x10000;
    private const uint FileTypeMask = 0xF000;
    private const uint RegularFile = 0x8000;
    private const uint DirectoryFile = 0x4000;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagRandomAccess = 0x10000000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int LinuxNoEntry = 2;
    private const int LinuxSymbolicLinkLoop = 40;

    internal static RestrictedStateOpenResult OpenFileNoFollow(
        string path,
        out SafeFileHandle? handle)
    {
        if (OperatingSystem.IsWindows())
        {
            handle = CreateFile(
                path,
                GenericRead,
                FileShareRead | FileShareWrite | FileShareDelete,
                0,
                OpenExisting,
                FileFlagBackupSemantics |
                    FileFlagOpenReparsePoint |
                    FileFlagRandomAccess,
                0);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                handle = null;
                return MapOpenError(Marshal.GetLastPInvokeError());
            }

            return RestrictedStateOpenResult.Success;
        }

        var descriptor = Open(
            path,
            OpenReadOnly |
                OpenNonBlocking |
                OpenNoFollow |
                OpenCloseOnExec);
        if (descriptor < 0)
        {
            handle = null;
            return MapOpenError(Marshal.GetLastPInvokeError());
        }

        handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        return RestrictedStateOpenResult.Success;
    }

    internal static RestrictedStateOpenResult OpenDirectoryNoFollow(
        string path,
        out SafeFileHandle? handle)
    {
        if (OperatingSystem.IsWindows())
        {
            handle = CreateFile(
                path,
                0,
                FileShareRead | FileShareWrite | FileShareDelete,
                0,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                0);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                handle = null;
                return MapOpenError(Marshal.GetLastPInvokeError());
            }

            return RestrictedStateOpenResult.Success;
        }

        if (OperatingSystem.IsLinux())
        {
            var descriptor = Open(
                path,
                OpenReadOnly |
                    OpenDirectory |
                    OpenNoFollow |
                    OpenCloseOnExec);
            if (descriptor < 0)
            {
                handle = null;
                return MapOpenError(Marshal.GetLastPInvokeError());
            }

            handle = new SafeFileHandle(
                (nint)descriptor,
                ownsHandle: true);
            return RestrictedStateOpenResult.Success;
        }

        handle = null;
        return RestrictedStateOpenResult.Unsafe;
    }

    internal static RestrictedStateOpenResult OpenRootGuardNoFollow(
        string path,
        out SafeFileHandle? handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OpenDirectoryNoFollow(path, out handle);
        }

        handle = CreateFile(
            path,
            0,
            FileShareRead | FileShareWrite,
            0,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            0);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            handle = null;
            return MapOpenError(Marshal.GetLastPInvokeError());
        }

        return RestrictedStateOpenResult.Success;
    }

    internal static string AnchoredRoot(
        string configuredRoot,
        SafeFileHandle rootGuard) =>
        OperatingSystem.IsLinux()
            ? $"/proc/self/fd/{checked((int)rootGuard.DangerousGetHandle())}"
            : configuredRoot;

    internal static bool TryGetIdentity(
        SafeFileHandle handle,
        bool expectDirectory,
        out RestrictedStateFileIdentity identity)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var info))
            {
                identity = default;
                return false;
            }

            identity = new RestrictedStateFileIdentity(
                info.VolumeSerialNumber,
                ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
            return true;
        }

        if (OperatingSystem.IsLinux())
        {
            var descriptor = checked((int)handle.DangerousGetHandle());
            var expectedType = expectDirectory
                ? DirectoryFile
                : RegularFile;
            if (FStat(descriptor, out var info) != 0 ||
                (info.Mode & FileTypeMask) != expectedType)
            {
                identity = default;
                return false;
            }

            identity = new RestrictedStateFileIdentity(
                info.Device,
                info.Inode);
            return true;
        }

        identity = default;
        return false;
    }

    private static RestrictedStateOpenResult MapOpenError(int error) =>
        error is ErrorFileNotFound or ErrorPathNotFound
            ? RestrictedStateOpenResult.NotFound
            : error == LinuxSymbolicLinkLoop
                ? RestrictedStateOpenResult.Unsafe
                : RestrictedStateOpenResult.Io;

    internal static bool TrySyncDirectory(SafeFileHandle rootGuard)
    {
        if (!OperatingSystem.IsLinux())
        {
            return true;
        }

        var descriptor = checked(
            (int)rootGuard.DangerousGetHandle());
        return FSync(descriptor) == 0;
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandle",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(
        SafeFileHandle handle,
        out ByHandleFileInformation information);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int FStat(
        int fileDescriptor,
        out LinuxFileInformation information);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int FSync(int fileDescriptor);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal FileTime CreationTime;
        internal FileTime LastAccessTime;
        internal FileTime LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint Low;
        internal uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct LinuxFileInformation
    {
        internal ulong Device;
        internal ulong Inode;
        internal ulong HardLinkCount;
        internal uint Mode;
        internal uint UserId;
        internal uint GroupId;
        internal int Padding;
        internal ulong DeviceId;
        internal long Size;
        internal long BlockSize;
        internal long BlockCount;
        internal LinuxTimestamp AccessTime;
        internal LinuxTimestamp ModificationTime;
        internal LinuxTimestamp ChangeTime;
        internal fixed long Reserved[3];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxTimestamp
    {
        internal long Seconds;
        internal long Nanoseconds;
    }
}
