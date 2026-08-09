using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using OpaqueStateStore =
    AgenticPrReview.Runtime.Host.State.OpaqueStore.IRestrictedStateStore;

namespace AgenticPrReview.Runtime.Host.State.RestrictedStateTransactions;

internal class RestrictedStateOpaqueSnapshotStore
{
    private const int MaximumIndexObjects = 8;
    private const int MaximumCandidateObjects = 16;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim>
        TransactionGates = new(StringComparer.Ordinal);
    private readonly OpaqueStateStore store;
    private readonly IRestrictedStateKeyResolver keyResolver;

    internal RestrictedStateOpaqueSnapshotStore(
        OpaqueStateStore store,
        IRestrictedStateKeyResolver keyResolver)
    {
        this.store = store;
        this.keyResolver = keyResolver;
    }

    protected RestrictedStateOpaqueSnapshotStore()
    {
        store = null!;
        keyResolver = null!;
    }

    internal virtual async Task<RestrictedStateStoreRead> ReadAsync(
        AuthorizedStateAccess access,
        CancellationToken cancellationToken)
    {
        var result = await ReadCoreAsync(access, cancellationToken)
            .ConfigureAwait(false);
        return result.Succeeded
            ? new RestrictedStateStoreRead(
                RestrictedStateStoreFailure.None,
                result.Snapshot,
                result.Version)
            : new RestrictedStateStoreRead(result.Failure, null, null);
    }

    internal virtual async Task<RestrictedStateStoreWrite>
        CompareExchangeAsync(
        AuthorizedStateAccess access,
        RestrictedStateSnapshotVersion expected,
        RestrictedStateSnapshot replacement,
        CancellationToken cancellationToken)
    {
        if (access is null ||
            expected is null ||
            replacement is null ||
            !RestrictedStateValidation.IsValidScope(access.Scope) ||
            !RestrictedStateValidation.IsValidSnapshot(replacement) ||
            replacement == RestrictedStateSnapshot.Empty)
        {
            return WriteFailure(RestrictedStateStoreFailure.Invalid);
        }

        var names = Names(access.Scope);
        var gate = TransactionGates.GetOrAdd(
            names.Index.Value,
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadCoreAsync(access, cancellationToken)
                .ConfigureAwait(false);
            if (!current.Succeeded)
            {
                return WriteFailure(current.Failure);
            }

            if (current.Version != expected ||
                !TryLogicalVersion(replacement, out var replacementVersion))
            {
                return WriteFailure(
                    current.Version != expected
                        ? RestrictedStateStoreFailure.Conflict
                        : RestrictedStateStoreFailure.Invalid);
            }

            if (current.Version == replacementVersion)
            {
                return new RestrictedStateStoreWrite(
                    RestrictedStateStoreFailure.None,
                    replacementVersion,
                    Committed: true);
            }

            var operationIdentity = AgentCanonical.HashDomain(
                "apr.state-r3-opaque-operation.s1",
                Encoding.ASCII.GetBytes(string.Concat(
                    replacementVersion!.Sha256,
                    ":",
                    Guid.NewGuid().ToString("N"))));
            var newlyUploaded = ImmutableArray.CreateBuilder<
                OpaqueStoreObjectMetadata>();
            var accepted = ImmutableArray.CreateBuilder<
                RestrictedStateIndexedCandidate>(replacement.Accepted.Length);
            foreach (var candidate in replacement.Accepted)
            {
                var indexed = await ResolveCandidateAsync(
                        names.Candidate,
                        candidate,
                        current.Index,
                        operationIdentity,
                        newlyUploaded,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!indexed.Succeeded)
                {
                    var cleanup = await CleanupExactAsync(
                            newlyUploaded,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    return WriteFailure(
                        cleanup == RestrictedStateStoreFailure.None
                            ? indexed.Failure
                            : cleanup);
                }

                accepted.Add(indexed.Candidate!);
            }

            RestrictedStateIndexedCandidate? staging = null;
            if (replacement.Staging is not null)
            {
                var indexed = await ResolveCandidateAsync(
                        names.Candidate,
                        replacement.Staging,
                        current.Index,
                        operationIdentity,
                        newlyUploaded,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!indexed.Succeeded)
                {
                    var cleanup = await CleanupExactAsync(
                            newlyUploaded,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    return WriteFailure(
                        cleanup == RestrictedStateStoreFailure.None
                            ? indexed.Failure
                            : cleanup);
                }

                staging = indexed.Candidate;
            }

            var index = new RestrictedStateTransactionIndex(
                replacementVersion!,
                current.Version!,
                current.IndexMetadata,
                operationIdentity,
                RestrictedStateTransactionCommitState.ReadyForSelection,
                accepted.MoveToImmutable(),
                staging);
            if (!RestrictedStateTransactionIndexCodec.TryWrite(
                    index,
                    out var indexPlaintext))
            {
                var cleanup = await CleanupExactAsync(
                        newlyUploaded,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return WriteFailure(
                    cleanup == RestrictedStateStoreFailure.None
                        ? RestrictedStateStoreFailure.Invalid
                        : cleanup);
            }

            try
            {
                var minimumExpiry = MinimumTransportExpiry(replacement);
                if (!RestrictedStateTransactionIndexEnvelope.TryEncrypt(
                        access,
                        indexPlaintext,
                        minimumExpiry,
                        keyResolver,
                        out var indexEnvelope,
                        out var encryptionFailure))
                {
                    var cleanup = await CleanupExactAsync(
                            newlyUploaded,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    return WriteFailure(
                        cleanup == RestrictedStateStoreFailure.None
                            ? encryptionFailure
                            : cleanup);
                }

                try
                {
                    var upload = await UploadObjectAsync(
                            names.Index,
                            operationIdentity,
                            indexEnvelope!,
                            minimumExpiry,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!upload.Succeeded)
                    {
                        var cleanup = await CleanupExactAsync(
                                upload.Metadata is null
                                    ? newlyUploaded
                                    : newlyUploaded.Append(upload.Metadata),
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        return WriteFailure(
                            cleanup == RestrictedStateStoreFailure.None
                                ? upload.Failure
                                : cleanup);
                    }

                    newlyUploaded.Add(upload.Metadata!);
                    var observed = await ReadCoreAsync(
                            access,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!observed.Succeeded ||
                        observed.Index is null ||
                        !StringComparer.Ordinal.Equals(
                            observed.Index.OperationIdentity,
                            operationIdentity) ||
                        observed.Version != replacementVersion)
                    {
                        var rollback = await store.DeleteExactAsync(
                                new OpaqueStoreDeleteRequest(
                                    upload.Metadata!),
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        var cleanup = await CleanupExactAsync(
                                newlyUploaded.Where(item =>
                                    item != upload.Metadata),
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        var failure = observed.Succeeded
                            ? RestrictedStateStoreFailure.Conflict
                            : observed.Failure;
                        if (!rollback.Succeeded ||
                            cleanup != RestrictedStateStoreFailure.None)
                        {
                            failure = RestrictedStateStoreFailure.Cleanup;
                        }

                        return WriteFailure(failure);
                    }

                    var cleanupFailure = await CleanupSupersededAsync(
                            names,
                            observed,
                            cancellationToken: CancellationToken.None)
                        .ConfigureAwait(false);
                    return cleanupFailure ==
                        RestrictedStateStoreFailure.None
                        ? new RestrictedStateStoreWrite(
                            RestrictedStateStoreFailure.None,
                            replacementVersion,
                            Committed: true)
                        : new RestrictedStateStoreWrite(
                            cleanupFailure,
                            replacementVersion,
                            Committed: true);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(indexEnvelope!);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(indexPlaintext);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    internal virtual async Task<RestrictedStateStoreWrite> CompareDeleteAsync(
        AuthorizedStateAccess access,
        RestrictedStateSnapshotVersion expected,
        CancellationToken cancellationToken)
    {
        if (access is null || expected is null)
        {
            return WriteFailure(RestrictedStateStoreFailure.Invalid);
        }

        var names = Names(access.Scope);
        var gate = TransactionGates.GetOrAdd(
            names.Index.Value,
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadCoreAsync(access, cancellationToken)
                .ConfigureAwait(false);
            if (!current.Succeeded)
            {
                return WriteFailure(current.Failure);
            }

            if (current.Version != expected)
            {
                return WriteFailure(RestrictedStateStoreFailure.Conflict);
            }

            var raw = await ReadRawCoreAsync(access, cancellationToken)
                .ConfigureAwait(false);
            return await DeleteRawCoreAsync(raw, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal virtual async Task<RestrictedStateStoreRawRead>
        ReadRawVersionAsync(
        AuthorizedStateAccess access,
        CancellationToken cancellationToken)
    {
        var raw = await ReadRawCoreAsync(access, cancellationToken)
            .ConfigureAwait(false);
        return raw.Succeeded
            ? new RestrictedStateStoreRawRead(
                RestrictedStateStoreFailure.None,
                raw.Version)
            : new RestrictedStateStoreRawRead(raw.Failure, null);
    }

    internal virtual async Task<RestrictedStateStoreWrite>
        CompareDeleteRawAsync(
        AuthorizedStateAccess access,
        RestrictedStateRawVersion expected,
        CancellationToken cancellationToken)
    {
        if (access is null || expected is null)
        {
            return WriteFailure(RestrictedStateStoreFailure.Invalid);
        }

        var names = Names(access.Scope);
        var gate = TransactionGates.GetOrAdd(
            names.Index.Value,
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var raw = await ReadRawCoreAsync(access, cancellationToken)
                .ConfigureAwait(false);
            if (!raw.Succeeded)
            {
                return WriteFailure(raw.Failure);
            }

            if (raw.Version != expected)
            {
                return WriteFailure(RestrictedStateStoreFailure.Conflict);
            }

            return await DeleteRawCoreAsync(raw, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RestrictedStateSnapshotReadCore> ReadCoreAsync(
        AuthorizedStateAccess access,
        CancellationToken cancellationToken)
    {
        if (access is null ||
            !RestrictedStateValidation.IsValidScope(access.Scope))
        {
            return RestrictedStateSnapshotReadCore.Fail(
                RestrictedStateStoreFailure.Invalid);
        }

        var names = Names(access.Scope);
        var listed = await store.ListExactAsync(
                new OpaqueStoreListRequest(
                    names.Index,
                    MaximumIndexObjects),
                cancellationToken)
            .ConfigureAwait(false);
        if (!listed.Succeeded ||
            listed.Objects.Any(item => item.Name != names.Index))
        {
            return RestrictedStateSnapshotReadCore.Fail(
                listed.Succeeded
                    ? RestrictedStateStoreFailure.Invalid
                    : MapFailure(listed.Failure));
        }

        if (listed.Objects.Length == 0)
        {
            return RestrictedStateSnapshotReadCore.Empty();
        }

        var nodes = ImmutableArray.CreateBuilder<
            RestrictedStateIndexNode>(listed.Objects.Length);
        foreach (var reference in listed.Objects)
        {
            var metadata = await store.ReadMetadataAsync(
                    new OpaqueStoreMetadataRequest(reference),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!metadata.Succeeded ||
                metadata.Metadata!.Reference != reference)
            {
                return RestrictedStateSnapshotReadCore.Fail(
                    metadata.Succeeded
                        ? RestrictedStateStoreFailure.Invalid
                        : MapFailure(metadata.Failure));
            }

            var download = await store.DownloadAsync(
                    new OpaqueStoreDownloadRequest(
                        metadata.Metadata!,
                        OpaqueStoreLimits.MaximumObjectBytes),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!download.Succeeded ||
                download.Metadata != metadata.Metadata)
            {
                return RestrictedStateSnapshotReadCore.Fail(
                    download.Succeeded
                        ? RestrictedStateStoreFailure.Invalid
                        : MapFailure(download.Failure));
            }

            if (!RestrictedStateTransactionIndexEnvelope.TryDecrypt(
                    access,
                    download.EncryptedBytes.Span,
                    keyResolver,
                    out var plaintext,
                    out _,
                    out var decryptionFailure))
            {
                return RestrictedStateSnapshotReadCore.Fail(
                    decryptionFailure);
            }

            try
            {
                if (!RestrictedStateTransactionIndexCodec.TryRead(
                        plaintext!,
                        out var index) ||
                    !IndexMatchesScopeAndNames(
                        index!,
                        access.Scope,
                        names))
                {
                    return RestrictedStateSnapshotReadCore.Fail(
                        RestrictedStateStoreFailure.Invalid);
                }

                nodes.Add(new RestrictedStateIndexNode(
                    metadata.Metadata!,
                    index!));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext!);
            }
        }

        if (!TrySelectLeaf(nodes.ToImmutable(), out var leaf))
        {
            return RestrictedStateSnapshotReadCore.Fail(
                RestrictedStateStoreFailure.Conflict);
        }

        var accepted = ImmutableArray.CreateBuilder<
            RestrictedStateCandidate>(leaf!.Index.Accepted.Length);
        foreach (var candidate in leaf.Index.Accepted)
        {
            var loaded = await LoadCandidateAsync(
                    candidate,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!loaded.Succeeded)
            {
                return RestrictedStateSnapshotReadCore.Fail(loaded.Failure);
            }

            accepted.Add(loaded.Candidate!);
        }

        RestrictedStateCandidate? staging = null;
        if (leaf.Index.Staging is not null)
        {
            var loaded = await LoadCandidateAsync(
                    leaf.Index.Staging,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!loaded.Succeeded)
            {
                return RestrictedStateSnapshotReadCore.Fail(loaded.Failure);
            }

            staging = loaded.Candidate;
        }

        var snapshot = new RestrictedStateSnapshot(
            accepted.MoveToImmutable(),
            staging);
        if (!RestrictedStateValidation.IsValidSnapshot(snapshot) ||
            !TryLogicalVersion(snapshot, out var logicalVersion) ||
            logicalVersion != leaf.Index.LogicalVersion)
        {
            return RestrictedStateSnapshotReadCore.Fail(
                RestrictedStateStoreFailure.Invalid);
        }

        return new RestrictedStateSnapshotReadCore(
            RestrictedStateStoreFailure.None,
            snapshot,
            logicalVersion,
            leaf.Index,
            leaf.Metadata,
            nodes.ToImmutable());
    }

    private async Task<RestrictedStateIndexedCandidateResult>
        ResolveCandidateAsync(
            OpaqueStoreName candidateName,
            RestrictedStateCandidate candidate,
            RestrictedStateTransactionIndex? current,
            string operationIdentity,
            ImmutableArray<OpaqueStoreObjectMetadata>.Builder newlyUploaded,
            CancellationToken cancellationToken)
    {
        var existing = current is null
            ? null
            : current.Accepted
                .Append(current.Staging)
                .Where(item => item is not null)
                .FirstOrDefault(item =>
                    StringComparer.Ordinal.Equals(
                        item!.ObjectIdentity,
                        candidate.ObjectIdentity) &&
                    StringComparer.Ordinal.Equals(
                        item.EnvelopeSha256,
                        candidate.EnvelopeSha256));
        if (existing is not null)
        {
            return RestrictedStateIndexedCandidateResult.Success(existing);
        }

        var minimumExpiry = MinimumTransportExpiry(candidate);
        var upload = await UploadObjectAsync(
                candidateName,
                operationIdentity,
                candidate.Envelope,
                minimumExpiry,
                cancellationToken)
            .ConfigureAwait(false);
        if (!upload.Succeeded ||
            upload.Metadata!.ExpiresAtUnixSeconds <
                candidate.Binding.ExpiresAtUnixSeconds)
        {
            var cleanup = RestrictedStateStoreFailure.None;
            if (upload.Metadata is not null)
            {
                cleanup = await CleanupExactAsync(
                        [upload.Metadata],
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            return RestrictedStateIndexedCandidateResult.Fail(
                cleanup != RestrictedStateStoreFailure.None
                    ? cleanup
                    : upload.Succeeded
                    ? RestrictedStateStoreFailure.Invalid
                    : upload.Failure);
        }

        newlyUploaded.Add(upload.Metadata);
        return RestrictedStateIndexedCandidateResult.Success(
            new RestrictedStateIndexedCandidate(
                candidate.Binding,
                candidate.SessionSha256,
                candidate.EnvelopeSha256,
                candidate.ObjectIdentity,
                upload.Metadata));
    }

    private async Task<RestrictedStateObjectUpload> UploadObjectAsync(
        OpaqueStoreName name,
        string operationIdentity,
        ReadOnlyMemory<byte> encryptedBytes,
        long minimumExpiry,
        CancellationToken cancellationToken)
    {
        var encryptedDigest = new OpaqueStoreEncryptedObjectDigest(
            OpaqueStoreHash.Sha256(encryptedBytes.Span));
        var upload = await store.UploadImmutableAsync(
                new OpaqueStoreUploadRequest(
                    name,
                    new OpaqueStoreCorrelationId(operationIdentity),
                    encryptedBytes,
                    encryptedDigest,
                    minimumExpiry),
                cancellationToken)
            .ConfigureAwait(false);
        if (upload.Metadata is null ||
            upload.MutationState == OpaqueStoreMutationState.NotCommitted)
        {
            return RestrictedStateObjectUpload.Fail(
                MapFailure(upload.Failure));
        }

        if (!OpaqueStoreValidation.IsValid(upload.Metadata) ||
            upload.Metadata.Reference.Name != name ||
            upload.Metadata.EncryptedObjectDigest != encryptedDigest ||
            upload.Metadata.Size != encryptedBytes.Length ||
            upload.Metadata.ExpiresAtUnixSeconds < minimumExpiry)
        {
            return RestrictedStateObjectUpload.Fail(
                RestrictedStateStoreFailure.Invalid,
                upload.Metadata);
        }

        var readBack = await store.ReadBackExactAsync(
                new OpaqueStoreReadBackRequest(upload.Metadata),
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!readBack.Succeeded || readBack.Metadata != upload.Metadata)
        {
            return RestrictedStateObjectUpload.Fail(
                readBack.Succeeded
                    ? RestrictedStateStoreFailure.Invalid
                    : MapFailure(readBack.Failure),
                upload.Metadata);
        }

        return RestrictedStateObjectUpload.Success(upload.Metadata);
    }

    private async Task<RestrictedStateCandidateLoad> LoadCandidateAsync(
        RestrictedStateIndexedCandidate indexed,
        CancellationToken cancellationToken)
    {
        var download = await store.DownloadAsync(
                new OpaqueStoreDownloadRequest(
                    indexed.Transport,
                    OpaqueStoreLimits.MaximumObjectBytes),
                cancellationToken)
            .ConfigureAwait(false);
        if (!download.Succeeded ||
            download.Metadata != indexed.Transport)
        {
            return RestrictedStateCandidateLoad.Fail(
                download.Succeeded
                    ? RestrictedStateStoreFailure.Invalid
                    : MapFailure(download.Failure));
        }

        var envelope = download.EncryptedBytes.ToArray();
        var candidate = new RestrictedStateCandidate(
            indexed.Binding,
            indexed.SessionSha256,
            indexed.EnvelopeSha256,
            indexed.ObjectIdentity,
            envelope);
        if (!RestrictedStateValidation.IsValidCandidate(candidate))
        {
            CryptographicOperations.ZeroMemory(envelope);
            return RestrictedStateCandidateLoad.Fail(
                RestrictedStateStoreFailure.Invalid);
        }

        return RestrictedStateCandidateLoad.Success(candidate);
    }

    private async Task<RestrictedStateStoreFailure> CleanupSupersededAsync(
        RestrictedStateStoreNames names,
        RestrictedStateSnapshotReadCore selected,
        CancellationToken cancellationToken)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal)
        {
            selected.IndexMetadata!.Reference.ObjectId.Value,
        };
        foreach (var candidate in selected.Index!.Accepted)
        {
            keep.Add(candidate.Transport.Reference.ObjectId.Value);
        }

        if (selected.Index.Staging is not null)
        {
            keep.Add(selected.Index.Staging.Transport.Reference.ObjectId.Value);
        }

        var failure = RestrictedStateStoreFailure.None;
        foreach (var node in selected.Nodes)
        {
            if (node.Metadata != selected.IndexMetadata)
            {
                var deleted = await store.DeleteExactAsync(
                        new OpaqueStoreDeleteRequest(node.Metadata),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!deleted.Succeeded)
                {
                    failure = RestrictedStateStoreFailure.Cleanup;
                }
            }
        }

        var candidates = await store.ListExactAsync(
                new OpaqueStoreListRequest(
                    names.Candidate,
                    MaximumCandidateObjects),
                cancellationToken)
            .ConfigureAwait(false);
        if (!candidates.Succeeded ||
            candidates.Objects.Any(item => item.Name != names.Candidate))
        {
            return failure == RestrictedStateStoreFailure.None
                ? candidates.Succeeded
                    ? RestrictedStateStoreFailure.Invalid
                    : MapFailure(candidates.Failure)
                : failure;
        }

        foreach (var reference in candidates.Objects)
        {
            if (keep.Contains(reference.ObjectId.Value))
            {
                continue;
            }

            var metadata = await store.ReadMetadataAsync(
                    new OpaqueStoreMetadataRequest(reference),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!metadata.Succeeded ||
                metadata.Metadata!.Reference != reference)
            {
                failure = RestrictedStateStoreFailure.Cleanup;
                continue;
            }

            var deleted = await store.DeleteExactAsync(
                    new OpaqueStoreDeleteRequest(metadata.Metadata!),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!deleted.Succeeded)
            {
                failure = RestrictedStateStoreFailure.Cleanup;
            }
        }

        return failure;
    }

    private async Task<RestrictedStateRawReadCore> ReadRawCoreAsync(
        AuthorizedStateAccess access,
        CancellationToken cancellationToken)
    {
        if (access is null ||
            !RestrictedStateValidation.IsValidScope(access.Scope))
        {
            return RestrictedStateRawReadCore.Fail(
                RestrictedStateStoreFailure.Invalid);
        }

        var names = Names(access.Scope);
        var metadata = ImmutableArray.CreateBuilder<
            OpaqueStoreObjectMetadata>();
        foreach (var (name, limit) in new[]
        {
            (names.Index, MaximumIndexObjects),
            (names.Candidate, MaximumCandidateObjects),
        })
        {
            var listed = await store.ListExactAsync(
                    new OpaqueStoreListRequest(name, limit),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!listed.Succeeded ||
                listed.Objects.Any(item => item.Name != name))
            {
                return RestrictedStateRawReadCore.Fail(
                    listed.Succeeded
                        ? RestrictedStateStoreFailure.Invalid
                        : MapFailure(listed.Failure));
            }

            foreach (var reference in listed.Objects)
            {
                var read = await store.ReadMetadataAsync(
                        new OpaqueStoreMetadataRequest(reference),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!read.Succeeded ||
                    read.Metadata!.Reference != reference)
                {
                    return RestrictedStateRawReadCore.Fail(
                        read.Succeeded
                            ? RestrictedStateStoreFailure.Invalid
                            : MapFailure(read.Failure));
                }

                metadata.Add(read.Metadata!);
            }
        }

        if (metadata.Count == 0)
        {
            return new RestrictedStateRawReadCore(
                RestrictedStateStoreFailure.None,
                RestrictedStateRawVersion.Absent,
                []);
        }

        var canonical = string.Join(
            '\n',
            metadata
                .OrderBy(
                    item => item.Reference.Name.Value,
                    StringComparer.Ordinal)
                .ThenBy(
                    item => item.Reference.ObjectId.Value,
                    StringComparer.Ordinal)
                .Select(item => string.Join(
                    ':',
                    item.Reference.Name.Value,
                    item.Reference.ObjectId.Value,
                    item.ProducingRun.Identity,
                    item.ProducingRun.Attempt,
                    item.ArchiveDigest.Sha256,
                    item.EncryptedObjectDigest.Sha256,
                    item.ExpiresAtUnixSeconds,
                    item.Size)));
        return new RestrictedStateRawReadCore(
            RestrictedStateStoreFailure.None,
            new RestrictedStateRawVersion(
                AgentCanonical.HashDomain(
                    "apr.state-r3-opaque-raw.s1",
                    Encoding.UTF8.GetBytes(canonical)),
                metadata.Sum(item => item.Size),
                Exists: true),
            metadata.ToImmutable());
    }

    private async Task<RestrictedStateStoreWrite> DeleteRawCoreAsync(
        RestrictedStateRawReadCore raw,
        CancellationToken cancellationToken)
    {
        if (!raw.Succeeded)
        {
            return WriteFailure(raw.Failure);
        }

        if (!raw.Version!.Exists)
        {
            return new RestrictedStateStoreWrite(
                RestrictedStateStoreFailure.None,
                RestrictedStateSnapshotVersion.Absent,
                Committed: true);
        }

        var firstMutation = true;
        foreach (var metadata in raw.Metadata
            .OrderBy(item => item.Reference.Name.Value, StringComparer.Ordinal)
            .ThenBy(
                item => item.Reference.ObjectId.Value,
                StringComparer.Ordinal))
        {
            var token = firstMutation
                ? cancellationToken
                : CancellationToken.None;
            var deleted = await store.DeleteExactAsync(
                    new OpaqueStoreDeleteRequest(metadata),
                    token)
                .ConfigureAwait(false);
            if (!deleted.Succeeded)
            {
                return new RestrictedStateStoreWrite(
                    MapFailure(deleted.Failure),
                    null,
                    Committed: !firstMutation);
            }

            firstMutation = false;
        }

        return new RestrictedStateStoreWrite(
            RestrictedStateStoreFailure.None,
            RestrictedStateSnapshotVersion.Absent,
            Committed: true);
    }

    private async Task<RestrictedStateStoreFailure> CleanupExactAsync(
        IEnumerable<OpaqueStoreObjectMetadata> metadata,
        CancellationToken cancellationToken)
    {
        var failure = RestrictedStateStoreFailure.None;
        foreach (var item in metadata.Distinct())
        {
            var deleted = await store.DeleteExactAsync(
                    new OpaqueStoreDeleteRequest(item),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!deleted.Succeeded)
            {
                failure = RestrictedStateStoreFailure.Cleanup;
            }
        }

        return failure;
    }

    private static bool TrySelectLeaf(
        ImmutableArray<RestrictedStateIndexNode> nodes,
        out RestrictedStateIndexNode? leaf)
    {
        leaf = null;
        var byIdentity = new Dictionary<
            string,
            RestrictedStateIndexNode>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (!byIdentity.TryAdd(
                    node.Metadata.Reference.ObjectId.Value,
                    node))
            {
                return false;
            }
        }

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var predecessor = node.Index.PredecessorIndex;
            if (predecessor is null)
            {
                if (node.Index.PredecessorVersion.Exists)
                {
                    return false;
                }

                continue;
            }

            referenced.Add(predecessor.Reference.ObjectId.Value);
            if (byIdentity.TryGetValue(
                    predecessor.Reference.ObjectId.Value,
                    out var present) &&
                (present.Metadata != predecessor ||
                    present.Index.LogicalVersion !=
                        node.Index.PredecessorVersion))
            {
                return false;
            }
        }

        var leaves = nodes.Where(node =>
            !referenced.Contains(node.Metadata.Reference.ObjectId.Value))
            .ToArray();
        if (leaves.Length != 1)
        {
            return false;
        }

        leaf = leaves[0];
        return true;
    }

    private static bool IndexMatchesScopeAndNames(
        RestrictedStateTransactionIndex index,
        RestrictedStateScope scope,
        RestrictedStateStoreNames names)
    {
        if (!RestrictedStateTransactionIndexCodec.IsValid(index) ||
            (index.PredecessorIndex is not null &&
                index.PredecessorIndex.Reference.Name != names.Index))
        {
            return false;
        }

        return index.Accepted
            .Append(index.Staging)
            .Where(candidate => candidate is not null)
            .All(candidate =>
                candidate!.Binding.Scope == scope &&
                candidate.Transport.Reference.Name == names.Candidate);
    }

    private static bool TryLogicalVersion(
        RestrictedStateSnapshot snapshot,
        out RestrictedStateSnapshotVersion? version)
    {
        version = null;
        if (!RestrictedStateSnapshotCodec.TryWrite(snapshot, out var bytes))
        {
            return false;
        }

        version = new RestrictedStateSnapshotVersion(
            AgentCanonical.HashDomain("apr.state-snapshot.r2", bytes),
            Exists: true);
        return true;
    }

    private static long MinimumTransportExpiry(
        RestrictedStateSnapshot snapshot) =>
        ExtendTransportExpiry(
            snapshot.Accepted
                .Append(snapshot.Staging)
                .Where(candidate => candidate is not null)
                .Max(candidate => candidate!.Binding.ExpiresAtUnixSeconds));

    private static long MinimumTransportExpiry(
        RestrictedStateCandidate candidate) =>
        ExtendTransportExpiry(candidate.Binding.ExpiresAtUnixSeconds);

    private static long ExtendTransportExpiry(long stateExpiry) =>
        stateExpiry > RestrictedStateFormat.MaximumUnixSeconds -
            RestrictedStateFormat.MaximumRetentionSeconds
            ? RestrictedStateFormat.MaximumUnixSeconds
            : stateExpiry + RestrictedStateFormat.MaximumRetentionSeconds;

    private static RestrictedStateStoreNames Names(
        RestrictedStateScope scope)
    {
        var bytes = RestrictedStateSnapshotCodec.WriteScopeIdentity(scope);
        return new RestrictedStateStoreNames(
            new OpaqueStoreName(AgentCanonical.HashDomain(
                "apr.state-r3-opaque-index-name.s1",
                bytes)),
            new OpaqueStoreName(AgentCanonical.HashDomain(
                "apr.state-r3-opaque-candidate-name.s1",
                bytes)));
    }

    private static RestrictedStateStoreFailure MapFailure(
        OpaqueStoreFailure failure) => failure switch
        {
            OpaqueStoreFailure.Cancelled =>
                RestrictedStateStoreFailure.Cancelled,
            OpaqueStoreFailure.Conflict =>
                RestrictedStateStoreFailure.Conflict,
            OpaqueStoreFailure.Cleanup =>
                RestrictedStateStoreFailure.Cleanup,
            OpaqueStoreFailure.Invalid or
                OpaqueStoreFailure.NotFound or
                OpaqueStoreFailure.Incomplete or
                OpaqueStoreFailure.Duplicate or
                OpaqueStoreFailure.Expired or
                OpaqueStoreFailure.DigestMismatch =>
                RestrictedStateStoreFailure.Invalid,
            _ => RestrictedStateStoreFailure.Io,
        };

    private static RestrictedStateStoreWrite WriteFailure(
        RestrictedStateStoreFailure failure) =>
        new(failure, null, Committed: false);
}

internal sealed record RestrictedStateStoreNames(
    OpaqueStoreName Index,
    OpaqueStoreName Candidate);

internal sealed record RestrictedStateIndexNode(
    OpaqueStoreObjectMetadata Metadata,
    RestrictedStateTransactionIndex Index);

internal sealed record RestrictedStateSnapshotReadCore(
    RestrictedStateStoreFailure Failure,
    RestrictedStateSnapshot? Snapshot,
    RestrictedStateSnapshotVersion? Version,
    RestrictedStateTransactionIndex? Index,
    OpaqueStoreObjectMetadata? IndexMetadata,
    ImmutableArray<RestrictedStateIndexNode> Nodes)
{
    internal bool Succeeded =>
        Failure == RestrictedStateStoreFailure.None &&
        Snapshot is not null &&
        Version is not null;

    internal static RestrictedStateSnapshotReadCore Empty() =>
        new(
            RestrictedStateStoreFailure.None,
            RestrictedStateSnapshot.Empty,
            RestrictedStateSnapshotVersion.Absent,
            null,
            null,
            []);

    internal static RestrictedStateSnapshotReadCore Fail(
        RestrictedStateStoreFailure failure) =>
        new(failure, null, null, null, null, []);
}

internal sealed record RestrictedStateObjectUpload(
    RestrictedStateStoreFailure Failure,
    OpaqueStoreObjectMetadata? Metadata)
{
    internal bool Succeeded =>
        Failure == RestrictedStateStoreFailure.None && Metadata is not null;

    internal static RestrictedStateObjectUpload Success(
        OpaqueStoreObjectMetadata metadata) =>
        new(RestrictedStateStoreFailure.None, metadata);

    internal static RestrictedStateObjectUpload Fail(
        RestrictedStateStoreFailure failure,
        OpaqueStoreObjectMetadata? metadata = null) =>
        new(failure, metadata);
}

internal sealed record RestrictedStateIndexedCandidateResult(
    RestrictedStateStoreFailure Failure,
    RestrictedStateIndexedCandidate? Candidate)
{
    internal bool Succeeded =>
        Failure == RestrictedStateStoreFailure.None && Candidate is not null;

    internal static RestrictedStateIndexedCandidateResult Success(
        RestrictedStateIndexedCandidate candidate) =>
        new(RestrictedStateStoreFailure.None, candidate);

    internal static RestrictedStateIndexedCandidateResult Fail(
        RestrictedStateStoreFailure failure) =>
        new(failure, null);
}

internal sealed record RestrictedStateCandidateLoad(
    RestrictedStateStoreFailure Failure,
    RestrictedStateCandidate? Candidate)
{
    internal bool Succeeded =>
        Failure == RestrictedStateStoreFailure.None && Candidate is not null;

    internal static RestrictedStateCandidateLoad Success(
        RestrictedStateCandidate candidate) =>
        new(RestrictedStateStoreFailure.None, candidate);

    internal static RestrictedStateCandidateLoad Fail(
        RestrictedStateStoreFailure failure) =>
        new(failure, null);
}

internal sealed record RestrictedStateRawReadCore(
    RestrictedStateStoreFailure Failure,
    RestrictedStateRawVersion? Version,
    ImmutableArray<OpaqueStoreObjectMetadata> Metadata)
{
    internal bool Succeeded =>
        Failure == RestrictedStateStoreFailure.None && Version is not null;

    internal static RestrictedStateRawReadCore Fail(
        RestrictedStateStoreFailure failure) =>
        new(failure, null, []);
}
