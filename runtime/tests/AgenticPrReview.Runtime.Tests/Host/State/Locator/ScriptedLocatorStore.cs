using System.Collections.Immutable;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Tests.Host.State.Locator;

internal sealed class ConcurrentInitializationLocatorStore
    : IRestrictedStateStore, IDisposable
{
    private readonly Barrier listBarrier = new(2);
    private readonly Barrier uploadBarrier = new(2);
    private readonly Barrier cleanupBarrier = new(2);
    private readonly TaskCompletionSource duplicateDeleted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int initialLists;
    private int initialUploads;
    private int initialDeletes;

    internal bool SynchronizeCleanup { get; init; }
    internal bool DelayDuplicateUploadReceipt { get; init; }

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

        // The scripted store allocates ascending IDs; equal-retention roots
        // retain object-0000 and prune object-0001.
        if (DelayDuplicateUploadReceipt &&
            result.Metadata!.Reference.ObjectId.Value == "object-0001")
        {
            await duplicateDeleted.Task.WaitAsync(
                TimeSpan.FromSeconds(10), cancellationToken);
        }

        return result;
    }

    public Task<OpaqueStoreReadBackResult> ReadBackExactAsync(
        OpaqueStoreReadBackRequest request,
        CancellationToken cancellationToken) =>
        Inner.ReadBackExactAsync(request, cancellationToken);

    public async Task<OpaqueStoreDeleteResult> DeleteExactAsync(
        OpaqueStoreDeleteRequest request,
        CancellationToken cancellationToken)
    {
        if (SynchronizeCleanup &&
            Interlocked.Increment(ref initialDeletes) <= 2)
        {
            Assert.True(cleanupBarrier.SignalAndWait(
                TimeSpan.FromSeconds(10), cancellationToken));
        }

        var result = await Inner.DeleteExactAsync(request, cancellationToken);
        if (request.Expected.Reference.ObjectId.Value == "object-0001")
        {
            duplicateDeleted.TrySetResult();
        }

        return result;
    }

    public void Dispose()
    {
        listBarrier.Dispose();
        uploadBarrier.Dispose();
        cleanupBarrier.Dispose();
    }
}
