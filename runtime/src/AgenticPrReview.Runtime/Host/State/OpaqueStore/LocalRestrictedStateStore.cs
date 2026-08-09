using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using Microsoft.Win32.SafeHandles;

namespace AgenticPrReview.Runtime.Host.State;

internal sealed class LocalRestrictedStateStore
    : AgenticPrReview.Runtime.Host.State.OpaqueStore.IRestrictedStateStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim>
        NameGates = new(StringComparer.Ordinal);
    private readonly string configuredRoot;
    private readonly string producingRunIdentity = Guid.NewGuid().ToString("N");
    private readonly TimeProvider timeProvider;
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
        Action? afterFinalRootProofTestHook = null,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(explicitTestOwnedRoot))
        {
            throw new ArgumentException(
                "An explicit test-owned state root is required.",
                nameof(explicitTestOwnedRoot));
        }

        configuredRoot = explicitTestOwnedRoot;
        this.beforeWriteTestHook = beforeWriteTestHook;
        this.afterTemporaryFlushTestHook = afterTemporaryFlushTestHook;
        this.afterFinalRootProofTestHook = afterFinalRootProofTestHook;
        this.deleteTemporaryTestHook = deleteTemporaryTestHook;
        this.syncDirectoryTestHook = syncDirectoryTestHook;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<OpaqueStoreListResult> ListExactAsync(
        OpaqueStoreListRequest request,
        CancellationToken cancellationToken)
    {
        if (!OpaqueStoreValidation.IsValid(request))
        {
            return OpaqueStoreListResult.Fail(OpaqueStoreFailure.Invalid);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!TryPrepareOperation(
                request.Name,
                out var operation,
                out var failure))
        {
            return OpaqueStoreListResult.Fail(failure);
        }

        using (operation)
        {
            var gate = NameGates.GetOrAdd(
                operation.LockKey,
                static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!OperationIsCurrent(operation))
                {
                    return OpaqueStoreListResult.Fail(
                        OpaqueStoreFailure.Invalid);
                }

                var references = ImmutableArray.CreateBuilder<
                    OpaqueStoreObjectReference>();
                var identities = new HashSet<string>(StringComparer.Ordinal);
                foreach (var path in Directory.EnumerateFiles(
                    operation.OperationRoot,
                    string.Concat(operation.FilePrefix, "*.aprobject"),
                    SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (references.Count >= request.MaximumObjects)
                    {
                        return OpaqueStoreListResult.Fail(
                            OpaqueStoreFailure.Incomplete);
                    }

                    var read = await ReadRecordAsync(
                            operation,
                            path,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (read.Failure != OpaqueStoreFailure.None ||
                        read.Metadata is null ||
                        read.Metadata.Reference.Name != request.Name)
                    {
                        return OpaqueStoreListResult.Fail(
                            read.Failure == OpaqueStoreFailure.None
                                ? OpaqueStoreFailure.Invalid
                                : read.Failure);
                    }

                    if (!identities.Add(
                            read.Metadata.Reference.ObjectId.Value))
                    {
                        return OpaqueStoreListResult.Fail(
                            OpaqueStoreFailure.Duplicate);
                    }

                    references.Add(read.Metadata.Reference);
                }

                return new OpaqueStoreListResult(
                    OpaqueStoreFailure.None,
                    references
                        .OrderBy(
                            item => item.ObjectId.Value,
                            StringComparer.Ordinal)
                        .ToImmutableArray(),
                    Complete: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsFileFailure(exception))
            {
                return OpaqueStoreListResult.Fail(OpaqueStoreFailure.Io);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    public async Task<OpaqueStoreMetadataResult> ReadMetadataAsync(
        OpaqueStoreMetadataRequest request,
        CancellationToken cancellationToken)
    {
        if (!OpaqueStoreValidation.IsValid(request))
        {
            return OpaqueStoreMetadataResult.Fail(OpaqueStoreFailure.Invalid);
        }

        var read = await ReadExpectedAsync(
                request.Reference,
                expected: null,
                cancellationToken)
            .ConfigureAwait(false);
        return read.Failure == OpaqueStoreFailure.None
            ? new OpaqueStoreMetadataResult(
                OpaqueStoreFailure.None,
                read.Metadata)
            : OpaqueStoreMetadataResult.Fail(read.Failure);
    }

    public async Task<OpaqueStoreDownloadResult> DownloadAsync(
        OpaqueStoreDownloadRequest request,
        CancellationToken cancellationToken)
    {
        if (!OpaqueStoreValidation.IsValid(request))
        {
            return OpaqueStoreDownloadResult.Fail(OpaqueStoreFailure.Invalid);
        }

        var read = await ReadExpectedAsync(
                request.Expected.Reference,
                request.Expected,
                cancellationToken)
            .ConfigureAwait(false);
        if (read.Failure != OpaqueStoreFailure.None ||
            read.Metadata is null)
        {
            return OpaqueStoreDownloadResult.Fail(read.Failure);
        }

        if (read.Metadata.ExpiresAtUnixSeconds <=
            timeProvider.GetUtcNow().ToUnixTimeSeconds())
        {
            return OpaqueStoreDownloadResult.Fail(OpaqueStoreFailure.Expired);
        }

        if (read.Payload.Length > request.MaximumBytes ||
            !PayloadMatchesMetadata(read.Payload.Span, read.Metadata))
        {
            return OpaqueStoreDownloadResult.Fail(
                OpaqueStoreFailure.DigestMismatch);
        }

        return new OpaqueStoreDownloadResult(
            OpaqueStoreFailure.None,
            read.Metadata,
            read.Payload);
    }

    public async Task<OpaqueStoreUploadResult> UploadImmutableAsync(
        OpaqueStoreUploadRequest request,
        CancellationToken cancellationToken)
    {
        if (!OpaqueStoreValidation.IsValid(request) ||
            !StringComparer.Ordinal.Equals(
                OpaqueStoreHash.Sha256(request.EncryptedBytes.Span),
                request.EncryptedObjectDigest.Sha256))
        {
            return OpaqueStoreUploadResult.Fail(OpaqueStoreFailure.Invalid);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!TryPrepareOperation(
                request.Name,
                out var operation,
                out var failure))
        {
            return OpaqueStoreUploadResult.Fail(failure);
        }

        using (operation)
        {
            var gate = NameGates.GetOrAdd(
                operation.LockKey,
                static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!OperationIsCurrent(operation))
                {
                    return OpaqueStoreUploadResult.Fail(
                        OpaqueStoreFailure.Invalid);
                }

                var objectId = new OpaqueStoreObjectId(
                    Guid.NewGuid().ToString("N"));
                var reference = new OpaqueStoreObjectReference(
                    request.Name,
                    objectId);
                var actualExpiry = request.MinimumExpiresAtUnixSeconds >
                    RestrictedStateFormat.MaximumUnixSeconds - 3_600
                    ? RestrictedStateFormat.MaximumUnixSeconds
                    : request.MinimumExpiresAtUnixSeconds + 3_600;
                var digest = OpaqueStoreHash.Sha256(
                    request.EncryptedBytes.Span);
                var metadata = new OpaqueStoreObjectMetadata(
                    reference,
                    new OpaqueStoreProducingRun(
                        producingRunIdentity,
                        Attempt: 1),
                    new OpaqueStoreArchiveDigest(digest),
                    request.EncryptedObjectDigest,
                    actualExpiry,
                    request.EncryptedBytes.Length);
                if (!LocalOpaqueStoreRecordCodec.TryWrite(
                        metadata,
                        request.EncryptedBytes.Span,
                        out var record))
                {
                    return OpaqueStoreUploadResult.Fail(
                        OpaqueStoreFailure.Invalid);
                }

                var finalPath = ResolveObjectPath(operation, objectId);
                var temporaryPath = Path.Join(
                    operation.OperationRoot,
                    string.Concat(
                        ".",
                        Path.GetFileName(finalPath),
                        ".",
                        Guid.NewGuid().ToString("N"),
                        ".tmp"));
                var committed = false;
                try
                {
                    beforeWriteTestHook?.Invoke();
                    if (!OperationIsCurrent(operation))
                    {
                        return OpaqueStoreUploadResult.Fail(
                            OpaqueStoreFailure.Invalid);
                    }

                    await using (var stream = new FileStream(
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 16 * 1024,
                        FileOptions.Asynchronous |
                            FileOptions.WriteThrough))
                    {
                        await stream.WriteAsync(record, cancellationToken)
                            .ConfigureAwait(false);
                        await stream.FlushAsync(cancellationToken)
                            .ConfigureAwait(false);
                        stream.Flush(flushToDisk: true);
                    }

                    afterTemporaryFlushTestHook?.Invoke();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!OperationIsCurrent(operation))
                    {
                        return OpaqueStoreUploadResult.Fail(
                            OpaqueStoreFailure.Invalid);
                    }

                    afterFinalRootProofTestHook?.Invoke();
                    if (!OperationIsCurrent(operation))
                    {
                        return OpaqueStoreUploadResult.Fail(
                            OpaqueStoreFailure.Invalid);
                    }

                    var attributes = File.GetAttributes(temporaryPath);
                    if ((attributes &
                        (FileAttributes.Directory |
                            FileAttributes.ReparsePoint)) != 0)
                    {
                        return OpaqueStoreUploadResult.Fail(
                            OpaqueStoreFailure.Invalid);
                    }

                    File.Move(temporaryPath, finalPath, overwrite: false);
                    committed = true;
                    if (!OperationIsCurrent(operation))
                    {
                        if (TryDeleteTemporaryFile(finalPath))
                        {
                            committed = false;
                            return OpaqueStoreUploadResult.Fail(
                                OpaqueStoreFailure.Invalid);
                        }

                        return OpaqueStoreUploadResult.Fail(
                            OpaqueStoreFailure.OutcomeUnknown,
                            OpaqueStoreMutationState.OutcomeUnknown,
                            metadata);
                    }

                    if (!SyncDirectory(operation))
                    {
                        return OpaqueStoreUploadResult.Fail(
                            OpaqueStoreFailure.Io,
                            OpaqueStoreMutationState.Committed,
                            metadata);
                    }

                    return new OpaqueStoreUploadResult(
                        OpaqueStoreFailure.None,
                        OpaqueStoreMutationState.Committed,
                        metadata);
                }
                catch (OperationCanceledException) when (!committed)
                {
                    if (!TryDeleteTemporaryFile(temporaryPath))
                    {
                        return OpaqueStoreUploadResult.Fail(
                            OpaqueStoreFailure.Cleanup);
                    }

                    throw;
                }
                catch (IOException)
                {
                    return OpaqueStoreUploadResult.Fail(
                        OpaqueStoreFailure.Io,
                        committed
                            ? OpaqueStoreMutationState.OutcomeUnknown
                            : OpaqueStoreMutationState.NotCommitted,
                        committed ? metadata : null);
                }
                catch (UnauthorizedAccessException)
                {
                    return OpaqueStoreUploadResult.Fail(
                        OpaqueStoreFailure.Io,
                        committed
                            ? OpaqueStoreMutationState.OutcomeUnknown
                            : OpaqueStoreMutationState.NotCommitted,
                        committed ? metadata : null);
                }
                finally
                {
                    if (!committed)
                    {
                        TryDeleteTemporaryFile(temporaryPath);
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }

    public async Task<OpaqueStoreReadBackResult> ReadBackExactAsync(
        OpaqueStoreReadBackRequest request,
        CancellationToken cancellationToken)
    {
        if (!OpaqueStoreValidation.IsValid(request))
        {
            return OpaqueStoreReadBackResult.Fail(
                OpaqueStoreFailure.Invalid);
        }

        var read = await ReadExpectedAsync(
                request.Expected.Reference,
                request.Expected,
                cancellationToken)
            .ConfigureAwait(false);
        if (read.Failure != OpaqueStoreFailure.None ||
            read.Metadata is null)
        {
            return OpaqueStoreReadBackResult.Fail(read.Failure);
        }

        if (!PayloadMatchesMetadata(read.Payload.Span, read.Metadata))
        {
            return OpaqueStoreReadBackResult.Fail(
                OpaqueStoreFailure.DigestMismatch);
        }

        return new OpaqueStoreReadBackResult(
            OpaqueStoreFailure.None,
            read.Metadata);
    }

    public async Task<OpaqueStoreDeleteResult> DeleteExactAsync(
        OpaqueStoreDeleteRequest request,
        CancellationToken cancellationToken)
    {
        if (!OpaqueStoreValidation.IsValid(request))
        {
            return OpaqueStoreDeleteResult.Fail(OpaqueStoreFailure.Invalid);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!TryPrepareOperation(
                request.Expected.Reference.Name,
                out var operation,
                out var failure))
        {
            return OpaqueStoreDeleteResult.Fail(failure);
        }

        using (operation)
        {
            var gate = NameGates.GetOrAdd(
                operation.LockKey,
                static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var path = ResolveObjectPath(
                    operation,
                    request.Expected.Reference.ObjectId);
                var read = await ReadRecordAsync(
                        operation,
                        path,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read.Failure != OpaqueStoreFailure.None ||
                    read.Metadata is null)
                {
                    return OpaqueStoreDeleteResult.Fail(read.Failure);
                }

                if (read.Metadata != request.Expected ||
                    !PayloadMatchesMetadata(
                        read.Payload.Span,
                        read.Metadata))
                {
                    return OpaqueStoreDeleteResult.Fail(
                        OpaqueStoreFailure.Conflict);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!OperationIsCurrent(operation))
                {
                    return OpaqueStoreDeleteResult.Fail(
                        OpaqueStoreFailure.Invalid);
                }

                afterFinalRootProofTestHook?.Invoke();
                if (!OperationIsCurrent(operation))
                {
                    return OpaqueStoreDeleteResult.Fail(
                        OpaqueStoreFailure.Invalid);
                }

                var tombstonePath = Path.Join(
                    operation.OperationRoot,
                    string.Concat(
                        ".",
                        Path.GetFileName(path),
                        ".",
                        Guid.NewGuid().ToString("N"),
                        ".delete"));
                var moved = false;
                try
                {
                    File.Move(path, tombstonePath, overwrite: false);
                    moved = true;
                    if (!OperationIsCurrent(operation))
                    {
                        if (TryRestoreMovedFile(tombstonePath, path))
                        {
                            moved = false;
                            return OpaqueStoreDeleteResult.Fail(
                                OpaqueStoreFailure.Invalid);
                        }

                        return OpaqueStoreDeleteResult.Fail(
                            OpaqueStoreFailure.OutcomeUnknown,
                            OpaqueStoreMutationState.OutcomeUnknown);
                    }

                    if (!TryDeleteTemporaryFile(tombstonePath))
                    {
                        if (TryRestoreMovedFile(tombstonePath, path))
                        {
                            return OpaqueStoreDeleteResult.Fail(
                                OpaqueStoreFailure.Cleanup);
                        }

                        return OpaqueStoreDeleteResult.Fail(
                            OpaqueStoreFailure.OutcomeUnknown,
                            OpaqueStoreMutationState.OutcomeUnknown);
                    }

                    moved = false;
                    if (!SyncDirectory(operation) ||
                        File.Exists(path) ||
                        File.Exists(tombstonePath))
                    {
                        return OpaqueStoreDeleteResult.Fail(
                            OpaqueStoreFailure.Io,
                            OpaqueStoreMutationState.Committed);
                    }

                    return new OpaqueStoreDeleteResult(
                        OpaqueStoreFailure.None,
                        OpaqueStoreMutationState.Committed);
                }
                catch (Exception exception) when (IsFileFailure(exception))
                {
                    if (moved && TryRestoreMovedFile(tombstonePath, path))
                    {
                        return OpaqueStoreDeleteResult.Fail(
                            OpaqueStoreFailure.Io);
                    }

                    return OpaqueStoreDeleteResult.Fail(
                        OpaqueStoreFailure.OutcomeUnknown,
                        OpaqueStoreMutationState.OutcomeUnknown);
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task<LocalOpaqueStoreRead> ReadExpectedAsync(
        OpaqueStoreObjectReference reference,
        OpaqueStoreObjectMetadata? expected,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryPrepareOperation(
                reference.Name,
                out var operation,
                out var failure))
        {
            return LocalOpaqueStoreRead.Fail(failure);
        }

        using (operation)
        {
            var gate = NameGates.GetOrAdd(
                operation.LockKey,
                static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var path = ResolveObjectPath(operation, reference.ObjectId);
                var read = await ReadRecordAsync(
                        operation,
                        path,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read.Failure != OpaqueStoreFailure.None ||
                    read.Metadata is null)
                {
                    return read;
                }

                if (read.Metadata.Reference != reference ||
                    (expected is not null && read.Metadata != expected))
                {
                    return LocalOpaqueStoreRead.Fail(
                        OpaqueStoreFailure.Conflict);
                }

                return read;
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private static async Task<LocalOpaqueStoreRead> ReadRecordAsync(
        LocalOpaqueStoreOperation operation,
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperationIsCurrent(operation))
        {
            return LocalOpaqueStoreRead.Fail(OpaqueStoreFailure.Invalid);
        }

        var open = NativeRestrictedStateFiles.OpenFileNoFollow(
            path,
            out var handle);
        if (open == RestrictedStateOpenResult.NotFound)
        {
            return LocalOpaqueStoreRead.Fail(OpaqueStoreFailure.NotFound);
        }

        if (open != RestrictedStateOpenResult.Success || handle is null)
        {
            return LocalOpaqueStoreRead.Fail(
                open == RestrictedStateOpenResult.Unsafe
                    ? OpaqueStoreFailure.Invalid
                    : OpaqueStoreFailure.Io);
        }

        using (handle)
        {
            try
            {
                var attributes = File.GetAttributes(handle);
                if ((attributes &
                        (FileAttributes.Directory |
                            FileAttributes.ReparsePoint)) != 0 ||
                    !NativeRestrictedStateFiles.TryGetIdentity(
                        handle,
                        expectDirectory: false,
                        out var identity))
                {
                    return LocalOpaqueStoreRead.Fail(
                        OpaqueStoreFailure.Invalid);
                }

                var length = RandomAccess.GetLength(handle);
                if (length is < 1 or >
                    LocalOpaqueStoreRecordCodec.MaximumRecordBytes)
                {
                    return LocalOpaqueStoreRead.Fail(
                        OpaqueStoreFailure.Invalid);
                }

                var bytes = new byte[checked((int)length)];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var read = await RandomAccess.ReadAsync(
                            handle,
                            bytes.AsMemory(offset),
                            offset,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        return LocalOpaqueStoreRead.Fail(
                            OpaqueStoreFailure.Invalid);
                    }

                    offset += read;
                }

                if (RandomAccess.GetLength(handle) != length ||
                    !PathStillNamesIdentity(path, identity, length) ||
                    !OperationIsCurrent(operation) ||
                    !LocalOpaqueStoreRecordCodec.TryRead(
                        bytes,
                        out var metadata,
                        out var payload))
                {
                    return LocalOpaqueStoreRead.Fail(
                        OpaqueStoreFailure.Invalid);
                }

                return new LocalOpaqueStoreRead(
                    OpaqueStoreFailure.None,
                    metadata,
                    payload);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsFileFailure(exception))
            {
                return LocalOpaqueStoreRead.Fail(OpaqueStoreFailure.Io);
            }
        }
    }

    private bool TryPrepareOperation(
        OpaqueStoreName name,
        out LocalOpaqueStoreOperation operation,
        out OpaqueStoreFailure failure)
    {
        operation = null!;
        failure = OpaqueStoreFailure.Invalid;
        if (!TryResolveRoot(out var root) ||
            !TryCaptureRootProof(root, out var proof, out failure))
        {
            return false;
        }

        var open = NativeRestrictedStateFiles.OpenRootGuardNoFollow(
            root,
            out var guard);
        if (open != RestrictedStateOpenResult.Success || guard is null)
        {
            failure = open == RestrictedStateOpenResult.Unsafe
                ? OpaqueStoreFailure.Invalid
                : OpaqueStoreFailure.Io;
            return false;
        }

        var operationRoot = NativeRestrictedStateFiles.AnchoredRoot(
            root,
            guard);
        if (!RootGuardMatchesProof(guard, proof) ||
            !TryResolveFilePrefix(name, operationRoot, out var prefix))
        {
            guard.Dispose();
            return false;
        }

        operation = new LocalOpaqueStoreOperation(
            root,
            operationRoot,
            prefix,
            Path.Join(root, prefix),
            proof,
            guard);
        failure = OpaqueStoreFailure.None;
        return true;
    }

    private bool TryResolveRoot(out string root)
    {
        root = string.Empty;
        try
        {
            root = Path.GetFullPath(configuredRoot);
            return Directory.Exists(root);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            return false;
        }
    }

    private static bool TryResolveFilePrefix(
        OpaqueStoreName name,
        string root,
        out string prefix)
    {
        prefix = string.Empty;
        if (!OpaqueStoreValidation.IsValid(name))
        {
            return false;
        }

        var nameBytes = Encoding.UTF8.GetBytes(name.Value);
        var nameHash = OpaqueStoreHash.Sha256(nameBytes);
        prefix = string.Concat("opaque-", nameHash, "-");
        var probe = Path.Join(root, string.Concat(prefix, "probe.aprobject"));
        return StringComparer.Ordinal.Equals(
            Path.GetDirectoryName(probe),
            root);
    }

    private static string ResolveObjectPath(
        LocalOpaqueStoreOperation operation,
        OpaqueStoreObjectId objectId) =>
        Path.Join(
            operation.OperationRoot,
            string.Concat(
                operation.FilePrefix,
                objectId.Value,
                ".aprobject"));

    private static bool OperationIsCurrent(
        LocalOpaqueStoreOperation operation) =>
        RootGuardMatchesProof(operation.RootGuard, operation.RootProof) &&
        RootProofIsCurrent(operation.Root, operation.RootProof);

    private static bool TryCaptureRootProof(
        string root,
        out ImmutableArray<RestrictedStateRootEntry> proof,
        out OpaqueStoreFailure failure)
    {
        failure = OpaqueStoreFailure.Invalid;
        try
        {
            var entries = ImmutableArray.CreateBuilder<
                RestrictedStateRootEntry>();
            var current = new DirectoryInfo(root);
            var anchoredRoot =
                NativeRestrictedStateFiles.IsLinuxAnchoredRoot(root);
            while (current is not null)
            {
                var open = NativeRestrictedStateFiles.OpenDirectoryNoFollow(
                    current.FullName,
                    out var handle);
                if (open != RestrictedStateOpenResult.Success ||
                    handle is null)
                {
                    proof = [];
                    failure = open == RestrictedStateOpenResult.Io
                        ? OpaqueStoreFailure.Io
                        : OpaqueStoreFailure.Invalid;
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

                if (anchoredRoot)
                {
                    break;
                }

                current = current.Parent;
            }

            proof = entries.ToImmutable();
            failure = OpaqueStoreFailure.None;
            return true;
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            proof = [];
            failure = OpaqueStoreFailure.Io;
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

    private static bool PathStillNamesIdentity(
        string path,
        RestrictedStateFileIdentity expectedIdentity,
        long expectedLength)
    {
        var open = NativeRestrictedStateFiles.OpenFileNoFollow(
            path,
            out var handle);
        if (open != RestrictedStateOpenResult.Success || handle is null)
        {
            return false;
        }

        using (handle)
        {
            return NativeRestrictedStateFiles.TryGetIdentity(
                    handle,
                    expectDirectory: false,
                    out var identity) &&
                identity == expectedIdentity &&
                RandomAccess.GetLength(handle) == expectedLength;
        }
    }

    private bool SyncDirectory(LocalOpaqueStoreOperation operation)
    {
        if (syncDirectoryTestHook is not null)
        {
            return syncDirectoryTestHook(operation.Root);
        }

        return NativeRestrictedStateFiles.TrySyncDirectory(
            operation.RootGuard);
    }

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
        catch (Exception exception) when (IsFileFailure(exception))
        {
            return false;
        }
    }

    private static bool TryRestoreMovedFile(string source, string destination)
    {
        try
        {
            if (!File.Exists(source) || File.Exists(destination))
            {
                return false;
            }

            File.Move(source, destination, overwrite: false);
            return true;
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            return false;
        }
    }

    private static bool PayloadMatchesMetadata(
        ReadOnlySpan<byte> payload,
        OpaqueStoreObjectMetadata metadata)
    {
        var digest = OpaqueStoreHash.Sha256(payload);
        return payload.Length == metadata.Size &&
            StringComparer.Ordinal.Equals(
                digest,
                metadata.EncryptedObjectDigest.Sha256) &&
            StringComparer.Ordinal.Equals(
                digest,
                metadata.ArchiveDigest.Sha256);
    }

    private static bool IsFileFailure(Exception exception) =>
        exception is ArgumentException or
            IOException or
            NotSupportedException or
            UnauthorizedAccessException;
}

internal sealed class LocalOpaqueStoreOperation(
    string root,
    string operationRoot,
    string filePrefix,
    string lockKey,
    ImmutableArray<RestrictedStateRootEntry> rootProof,
    SafeFileHandle rootGuard) : IDisposable
{
    internal string Root { get; } = root;
    internal string OperationRoot { get; } = operationRoot;
    internal string FilePrefix { get; } = filePrefix;
    internal string LockKey { get; } = lockKey;
    internal ImmutableArray<RestrictedStateRootEntry> RootProof { get; } =
        rootProof;
    internal SafeFileHandle RootGuard { get; } = rootGuard;

    public void Dispose() => RootGuard.Dispose();
}

internal sealed record LocalOpaqueStoreRead(
    OpaqueStoreFailure Failure,
    OpaqueStoreObjectMetadata? Metadata,
    ReadOnlyMemory<byte> Payload)
{
    internal static LocalOpaqueStoreRead Fail(OpaqueStoreFailure failure) =>
        new(failure, null, ReadOnlyMemory<byte>.Empty);
}

internal static class LocalOpaqueStoreRecordCodec
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("APROBJ01");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal const ushort Version = 1;
    internal const int MaximumHeaderBytes = 2_048;
    internal const int MaximumRecordBytes =
        OpaqueStoreLimits.MaximumObjectBytes + MaximumHeaderBytes;

    internal static bool TryWrite(
        OpaqueStoreObjectMetadata metadata,
        ReadOnlySpan<byte> payload,
        out byte[] bytes)
    {
        bytes = [];
        if (!OpaqueStoreValidation.IsValid(metadata) ||
            payload.Length != metadata.Size ||
            payload.Length is < 1 or > OpaqueStoreLimits.MaximumObjectBytes)
        {
            return false;
        }

        try
        {
            var writer = new ArrayBufferWriter<byte>(
                checked(payload.Length + MaximumHeaderBytes));
            Write(writer, Magic);
            WriteUInt16(writer, Version);
            WriteUtf8(writer, metadata.Reference.Name.Value);
            WriteUtf8(writer, metadata.Reference.ObjectId.Value);
            WriteUtf8(writer, metadata.ProducingRun.Identity);
            WriteInt64(writer, metadata.ProducingRun.Attempt);
            WriteHex(writer, metadata.ArchiveDigest.Sha256);
            WriteHex(writer, metadata.EncryptedObjectDigest.Sha256);
            WriteInt64(writer, metadata.ExpiresAtUnixSeconds);
            WriteInt64(writer, metadata.Size);
            Write(writer, payload);
            if (writer.WrittenCount > MaximumRecordBytes)
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
        out OpaqueStoreObjectMetadata? metadata,
        out ReadOnlyMemory<byte> payload)
    {
        metadata = null;
        payload = ReadOnlyMemory<byte>.Empty;
        if (bytes.Length is < 1 or > MaximumRecordBytes)
        {
            return false;
        }

        var offset = 0;
        if (!TryReadBytes(bytes, ref offset, Magic.Length, out var magic) ||
            !magic.SequenceEqual(Magic) ||
            !TryReadUInt16(bytes, ref offset, out var version) ||
            version != Version ||
            !TryReadUtf8(
                bytes,
                ref offset,
                OpaqueStoreLimits.MaximumNameBytes,
                out var name) ||
            !TryReadUtf8(
                bytes,
                ref offset,
                OpaqueStoreLimits.MaximumIdentityBytes,
                out var objectId) ||
            !TryReadUtf8(
                bytes,
                ref offset,
                OpaqueStoreLimits.MaximumIdentityBytes,
                out var runIdentity) ||
            !TryReadInt64(bytes, ref offset, out var runAttempt) ||
            !TryReadHex(bytes, ref offset, out var archiveDigest) ||
            !TryReadHex(bytes, ref offset, out var encryptedDigest) ||
            !TryReadInt64(bytes, ref offset, out var expiresAt) ||
            !TryReadInt64(bytes, ref offset, out var size) ||
            size is < 1 or > OpaqueStoreLimits.MaximumObjectBytes ||
            bytes.Length - offset != size)
        {
            return false;
        }

        var parsed = new OpaqueStoreObjectMetadata(
            new OpaqueStoreObjectReference(
                new OpaqueStoreName(name!),
                new OpaqueStoreObjectId(objectId!)),
            new OpaqueStoreProducingRun(runIdentity!, runAttempt),
            new OpaqueStoreArchiveDigest(archiveDigest!),
            new OpaqueStoreEncryptedObjectDigest(encryptedDigest!),
            expiresAt,
            size);
        if (!OpaqueStoreValidation.IsValid(parsed))
        {
            return false;
        }

        metadata = parsed;
        payload = bytes[offset..].ToArray();
        return true;
    }

    private static void WriteUtf8(IBufferWriter<byte> writer, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        WriteUInt16(writer, checked((ushort)bytes.Length));
        Write(writer, bytes);
    }

    private static void WriteHex(IBufferWriter<byte> writer, string value) =>
        Write(writer, Convert.FromHexString(value));

    private static void WriteUInt16(IBufferWriter<byte> writer, ushort value)
    {
        var span = writer.GetSpan(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(span, value);
        writer.Advance(sizeof(ushort));
    }

    private static void WriteInt64(IBufferWriter<byte> writer, long value)
    {
        var span = writer.GetSpan(sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(span, value);
        writer.Advance(sizeof(long));
    }

    private static void Write(
        IBufferWriter<byte> writer,
        ReadOnlySpan<byte> value)
    {
        value.CopyTo(writer.GetSpan(value.Length));
        writer.Advance(value.Length);
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

    private static bool TryReadUtf8(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        int maximum,
        out string? value)
    {
        value = null;
        if (!TryReadUInt16(bytes, ref offset, out var length) ||
            length is 0 || length > maximum ||
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

    private static bool TryReadHex(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        out string? value)
    {
        value = null;
        if (!TryReadBytes(bytes, ref offset, 32, out var encoded))
        {
            return false;
        }

        value = Convert.ToHexString(encoded).ToLowerInvariant();
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
}
