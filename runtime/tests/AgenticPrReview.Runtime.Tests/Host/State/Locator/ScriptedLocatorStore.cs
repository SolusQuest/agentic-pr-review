using System.Collections.Immutable;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Tests.Host.State.Locator;

internal sealed class ScriptedLocatorStore : IRestrictedStateStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, StoredObject> objects =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> expiredObjectIds =
        new(StringComparer.Ordinal);
    private int nextObjectId;

    internal bool ListComplete { get; set; } = true;
    internal OpaqueStoreFailure ListFailure { get; set; }
    internal OpaqueStoreFailure MetadataFailure { get; set; }
    internal OpaqueStoreFailure DownloadFailure { get; set; }
    internal OpaqueStoreFailure ReadBackFailure { get; set; }
    internal OpaqueStoreFailure NextUploadFailure { get; set; }
    internal OpaqueStoreMutationState NextUploadMutationState { get; set; }
        = OpaqueStoreMutationState.NotCommitted;
    internal bool PersistFailedUpload { get; set; }
    internal OpaqueStoreFailure NextDeleteFailure { get; set; }
    internal OpaqueStoreMutationState NextDeleteMutationState { get; set; }
        = OpaqueStoreMutationState.NotCommitted;
    internal int DeleteFailuresRemaining { get; set; }
    internal bool RemoveOnDeleteFailure { get; set; }
    internal long ExtraRetentionSeconds { get; set; } = 3_600;
    internal int HideExistingObjectsForNextLists { get; set; }
    internal int HideNewestObjectForNextLists { get; set; }
    internal int ListCalls { get; private set; }
    internal int MetadataCalls { get; private set; }
    internal int DownloadCalls { get; private set; }
    internal int UploadCalls { get; private set; }
    internal int ReadBackCalls { get; private set; }
    internal int DeleteCalls { get; private set; }
    internal int MaximumObservedObjects { get; private set; }

    internal ImmutableArray<OpaqueStoreObjectMetadata> Objects
    {
        get
        {
            lock (gate)
            {
                return objects.Values
                    .Select(item => item.Metadata)
                    .OrderBy(
                        item => item.Reference.ObjectId.Value,
                        StringComparer.Ordinal)
                    .ToImmutableArray();
            }
        }
    }

    internal byte[] Bytes(OpaqueStoreObjectMetadata metadata)
    {
        lock (gate)
        {
            return objects[metadata.Reference.ObjectId.Value]
                .Bytes.ToArray();
        }
    }

    internal OpaqueStoreObjectMetadata Add(
        byte[] bytes,
        long expiresAtUnixSeconds,
        string? objectId = null)
    {
        lock (gate)
        {
            var id = objectId ?? NextId();
            var digest = OpaqueStoreHash.Sha256(bytes);
            var metadata = new OpaqueStoreObjectMetadata(
                new OpaqueStoreObjectReference(
                    new OpaqueStoreName(LocatorRootFormat.StoreName),
                    new OpaqueStoreObjectId(id)),
                new OpaqueStoreProducingRun("scripted", 1),
                new OpaqueStoreArchiveDigest(digest),
                new OpaqueStoreEncryptedObjectDigest(digest),
                expiresAtUnixSeconds,
                bytes.Length);
            objects.Add(id, new StoredObject(metadata, bytes.ToArray()));
            MaximumObservedObjects = Math.Max(
                MaximumObservedObjects,
                objects.Count);
            return metadata;
        }
    }

    internal void MarkExpired(OpaqueStoreObjectMetadata metadata)
    {
        lock (gate)
        {
            expiredObjectIds.Add(metadata.Reference.ObjectId.Value);
        }
    }

    internal void ResetCounts()
    {
        ListCalls = 0;
        MetadataCalls = 0;
        DownloadCalls = 0;
        UploadCalls = 0;
        ReadBackCalls = 0;
        DeleteCalls = 0;
        MaximumObservedObjects = Objects.Length;
    }

    public Task<OpaqueStoreListResult> ListExactAsync(
        OpaqueStoreListRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ListCalls++;
            if (ListFailure != OpaqueStoreFailure.None)
            {
                return Task.FromResult(
                    OpaqueStoreListResult.Fail(ListFailure));
            }

            var references = objects.Values
                .Select(item => item.Metadata.Reference)
                .OrderBy(item => item.ObjectId.Value, StringComparer.Ordinal)
                .ToImmutableArray();
            if (!references.IsEmpty &&
                HideExistingObjectsForNextLists > 0)
            {
                HideExistingObjectsForNextLists--;
                return Task.FromResult(new OpaqueStoreListResult(
                    OpaqueStoreFailure.None,
                    [],
                    Complete: true));
            }

            if (references.Length > 1 &&
                HideNewestObjectForNextLists > 0)
            {
                HideNewestObjectForNextLists--;
                references = references[..^1];
            }

            if (!ListComplete || references.Length > request.MaximumObjects)
            {
                return Task.FromResult(new OpaqueStoreListResult(
                    OpaqueStoreFailure.Incomplete,
                    references.Take(request.MaximumObjects)
                        .ToImmutableArray(),
                    Complete: false));
            }

            return Task.FromResult(new OpaqueStoreListResult(
                OpaqueStoreFailure.None,
                references,
                Complete: true));
        }
    }

    public Task<OpaqueStoreMetadataResult> ReadMetadataAsync(
        OpaqueStoreMetadataRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            MetadataCalls++;
            if (MetadataFailure != OpaqueStoreFailure.None)
            {
                return Task.FromResult(
                    OpaqueStoreMetadataResult.Fail(MetadataFailure));
            }

            return Task.FromResult(objects.TryGetValue(
                request.Reference.ObjectId.Value,
                out var stored)
                ? new OpaqueStoreMetadataResult(
                    OpaqueStoreFailure.None,
                    stored.Metadata)
                : OpaqueStoreMetadataResult.Fail(
                    OpaqueStoreFailure.NotFound));
        }
    }

    public Task<OpaqueStoreDownloadResult> DownloadAsync(
        OpaqueStoreDownloadRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            DownloadCalls++;
            if (DownloadFailure != OpaqueStoreFailure.None)
            {
                return Task.FromResult(
                    OpaqueStoreDownloadResult.Fail(DownloadFailure));
            }

            if (expiredObjectIds.Contains(
                    request.Expected.Reference.ObjectId.Value))
            {
                return Task.FromResult(
                    OpaqueStoreDownloadResult.Fail(
                        OpaqueStoreFailure.Expired));
            }

            return Task.FromResult(objects.TryGetValue(
                    request.Expected.Reference.ObjectId.Value,
                    out var stored) &&
                stored.Metadata == request.Expected
                ? new OpaqueStoreDownloadResult(
                    OpaqueStoreFailure.None,
                    stored.Metadata,
                    stored.Bytes)
                : OpaqueStoreDownloadResult.Fail(
                    OpaqueStoreFailure.NotFound));
        }
    }

    public Task<OpaqueStoreUploadResult> UploadImmutableAsync(
        OpaqueStoreUploadRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            UploadCalls++;
            var id = NextId();
            var digest = OpaqueStoreHash.Sha256(
                request.EncryptedBytes.Span);
            var metadata = new OpaqueStoreObjectMetadata(
                new OpaqueStoreObjectReference(
                    request.Name,
                    new OpaqueStoreObjectId(id)),
                new OpaqueStoreProducingRun("scripted", UploadCalls),
                new OpaqueStoreArchiveDigest(digest),
                request.EncryptedObjectDigest,
                checked(
                    request.MinimumExpiresAtUnixSeconds +
                    ExtraRetentionSeconds),
                request.EncryptedBytes.Length);
            var failure = NextUploadFailure;
            var mutationState = NextUploadMutationState;
            var persist = failure == OpaqueStoreFailure.None ||
                PersistFailedUpload ||
                mutationState != OpaqueStoreMutationState.NotCommitted;
            if (persist)
            {
                objects.Add(
                    id,
                    new StoredObject(
                        metadata,
                        request.EncryptedBytes.ToArray()));
                MaximumObservedObjects = Math.Max(
                    MaximumObservedObjects,
                    objects.Count);
            }

            NextUploadFailure = OpaqueStoreFailure.None;
            NextUploadMutationState =
                OpaqueStoreMutationState.NotCommitted;
            PersistFailedUpload = false;
            return Task.FromResult(failure == OpaqueStoreFailure.None
                ? new OpaqueStoreUploadResult(
                    OpaqueStoreFailure.None,
                    OpaqueStoreMutationState.Committed,
                    metadata)
                : OpaqueStoreUploadResult.Fail(
                    failure,
                    mutationState,
                    mutationState == OpaqueStoreMutationState.NotCommitted
                        ? null
                        : metadata));
        }
    }

    public Task<OpaqueStoreReadBackResult> ReadBackExactAsync(
        OpaqueStoreReadBackRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ReadBackCalls++;
            if (ReadBackFailure != OpaqueStoreFailure.None)
            {
                return Task.FromResult(
                    OpaqueStoreReadBackResult.Fail(ReadBackFailure));
            }

            return Task.FromResult(objects.TryGetValue(
                    request.Expected.Reference.ObjectId.Value,
                    out var stored) &&
                stored.Metadata == request.Expected
                ? new OpaqueStoreReadBackResult(
                    OpaqueStoreFailure.None,
                    stored.Metadata)
                : OpaqueStoreReadBackResult.Fail(
                    OpaqueStoreFailure.NotFound));
        }
    }

    public Task<OpaqueStoreDeleteResult> DeleteExactAsync(
        OpaqueStoreDeleteRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            DeleteCalls++;
            if (!objects.TryGetValue(
                    request.Expected.Reference.ObjectId.Value,
                    out var stored) ||
                stored.Metadata != request.Expected)
            {
                return Task.FromResult(OpaqueStoreDeleteResult.Fail(
                    OpaqueStoreFailure.NotFound));
            }

            var failure = NextDeleteFailure;
            var mutationState = NextDeleteMutationState;
            if (failure == OpaqueStoreFailure.None ||
                RemoveOnDeleteFailure ||
                mutationState == OpaqueStoreMutationState.Committed)
            {
                objects.Remove(request.Expected.Reference.ObjectId.Value);
                expiredObjectIds.Remove(
                    request.Expected.Reference.ObjectId.Value);
            }

            if (DeleteFailuresRemaining > 0)
            {
                DeleteFailuresRemaining--;
            }

            if (DeleteFailuresRemaining == 0)
            {
                NextDeleteFailure = OpaqueStoreFailure.None;
                NextDeleteMutationState =
                    OpaqueStoreMutationState.NotCommitted;
                RemoveOnDeleteFailure = false;
            }

            return Task.FromResult(failure == OpaqueStoreFailure.None
                ? new OpaqueStoreDeleteResult(
                    OpaqueStoreFailure.None,
                    OpaqueStoreMutationState.Committed)
                : OpaqueStoreDeleteResult.Fail(failure, mutationState));
        }
    }

    private string NextId() =>
        string.Concat("object-", nextObjectId++.ToString("D4"));

    private sealed record StoredObject(
        OpaqueStoreObjectMetadata Metadata,
        byte[] Bytes);
}

internal sealed class ConcurrentInitializationLocatorStore
    : IRestrictedStateStore, IDisposable
{
    private readonly Barrier listBarrier = new(2);
    private readonly Barrier uploadBarrier = new(2);
    private int initialLists;
    private int initialUploads;

    internal ScriptedLocatorStore Inner { get; } = new();

    public async Task<OpaqueStoreListResult> ListExactAsync(
        OpaqueStoreListRequest request,
        CancellationToken cancellationToken)
    {
        var result = await Inner.ListExactAsync(request, cancellationToken);
        if (Interlocked.Increment(ref initialLists) <= 2)
        {
            Assert.True(listBarrier.SignalAndWait(
                TimeSpan.FromSeconds(10),
                cancellationToken));
        }

        return result;
    }

    public Task<OpaqueStoreMetadataResult> ReadMetadataAsync(
        OpaqueStoreMetadataRequest request,
        CancellationToken cancellationToken) =>
        Inner.ReadMetadataAsync(request, cancellationToken);

    public Task<OpaqueStoreDownloadResult> DownloadAsync(
        OpaqueStoreDownloadRequest request,
        CancellationToken cancellationToken) =>
        Inner.DownloadAsync(request, cancellationToken);

    public async Task<OpaqueStoreUploadResult> UploadImmutableAsync(
        OpaqueStoreUploadRequest request,
        CancellationToken cancellationToken)
    {
        var result = await Inner.UploadImmutableAsync(
            request,
            cancellationToken);
        if (Interlocked.Increment(ref initialUploads) <= 2)
        {
            Assert.True(uploadBarrier.SignalAndWait(
                TimeSpan.FromSeconds(10),
                cancellationToken));
        }

        return result;
    }

    public Task<OpaqueStoreReadBackResult> ReadBackExactAsync(
        OpaqueStoreReadBackRequest request,
        CancellationToken cancellationToken) =>
        Inner.ReadBackExactAsync(request, cancellationToken);

    public Task<OpaqueStoreDeleteResult> DeleteExactAsync(
        OpaqueStoreDeleteRequest request,
        CancellationToken cancellationToken) =>
        Inner.DeleteExactAsync(request, cancellationToken);

    public void Dispose()
    {
        listBarrier.Dispose();
        uploadBarrier.Dispose();
    }
}
