using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Tests.Host.State.Lineage;

public sealed class LineageConcurrencyTests
{
    [Fact]
    public async Task ExactConcurrentInitialClaimsAreIdempotent()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            $"apr-lineage-concurrent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var firstLease = LineageTestData.Context();
            using var secondLease = LineageTestData.Context();
            var lineageName = ResolveName(
                firstLease,
                StateObjectClass.LineageHead);
            var cleanupName = ResolveName(
                firstLease,
                StateObjectClass.Cleanup);
            using var store = new ConcurrentInitialLineageStore(
                new LocalRestrictedStateStore(
                    root,
                    timeProvider: firstLease.Time),
                lineageName,
                cleanupName);
            var first = new LineageService(store, firstLease.Time)
                .ResolveAsync(
                    firstLease.Context,
                    LineageTestData.Request(firstLease.Access),
                    CancellationToken.None);
            var second = new LineageService(store, secondLease.Time)
                .ResolveAsync(
                    secondLease.Context,
                    LineageTestData.Request(secondLease.Access),
                    CancellationToken.None);

            var results = await Task.WhenAll(first, second);
            Assert.All(results, result =>
                Assert.True(result.Succeeded, result.Code));
            SelectedLineageSnapshot[] snapshots = new SelectedLineageSnapshot[2];
            for (var index = 0; index < results.Length; index++)
            {
                using (results[index].Context)
                {
                    var access = index == 0
                        ? firstLease.Access
                        : secondLease.Access;
                    Assert.True(results[index].Context!.TryGetSnapshot(
                        access,
                        out var snapshot));
                    snapshots[index] = snapshot!;
                }
            }

            Assert.Equal(snapshots[0], snapshots[1]);
            Assert.Single(Directory.GetFiles(root, "*.aprobject"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static OpaqueStoreName ResolveName(
        LineageTestData.ContextLease lease,
        StateObjectClass objectClass)
    {
        Assert.True(LineageBaseScopeCodec.TryEncode(
            LineageTestData.Scope(),
            out var canonical));
        try
        {
            Assert.True(lease.Context.TryDeriveOpaqueName(
                lease.Access,
                StateObjectClasses.ToWireName(objectClass),
                canonical,
                out var name));
            return name!;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(
                canonical);
        }
    }

    private sealed class ConcurrentInitialLineageStore(
        IRestrictedStateStore inner,
        OpaqueStoreName lineageName,
        OpaqueStoreName finalEnumerationName) :
        IRestrictedStateStore,
        IDisposable
    {
        private readonly TaskCompletionSource enumerationGate = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource uploadGate = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource loserDeletedGate = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int initialEnumerations;
        private int initialUploads;
        private OpaqueStoreObjectMetadata? firstUpload;
        private OpaqueStoreObjectMetadata? loserUpload;

        public async Task<OpaqueStoreListResult> ListExactAsync(
            OpaqueStoreListRequest request,
            CancellationToken cancellationToken)
        {
            var result = await inner.ListExactAsync(request, cancellationToken);
            if (request.Name == finalEnumerationName)
            {
                var count = Interlocked.Increment(ref initialEnumerations);
                if (count <= 2)
                {
                    if (count == 2)
                    {
                        enumerationGate.TrySetResult();
                    }

                    await enumerationGate.Task.WaitAsync(
                        TimeSpan.FromSeconds(10),
                        cancellationToken);
                }
            }

            return result;
        }

        public Task<OpaqueStoreMetadataResult> ReadMetadataAsync(
            OpaqueStoreMetadataRequest request,
            CancellationToken cancellationToken) =>
            inner.ReadMetadataAsync(request, cancellationToken);

        public Task<OpaqueStoreDownloadResult> DownloadAsync(
            OpaqueStoreDownloadRequest request,
            CancellationToken cancellationToken) =>
            inner.DownloadAsync(request, cancellationToken);

        public async Task<OpaqueStoreUploadResult> UploadImmutableAsync(
            OpaqueStoreUploadRequest request,
            CancellationToken cancellationToken)
        {
            var result = await inner.UploadImmutableAsync(
                request,
                cancellationToken);
            if (request.Name == lineageName)
            {
                var count = Interlocked.Increment(ref initialUploads);
                if (count <= 2)
                {
                    Assert.NotNull(result.Metadata);
                    if (count == 1)
                    {
                        firstUpload = result.Metadata;
                    }

                    if (count == 2)
                    {
                        Assert.NotNull(firstUpload);
                        loserUpload = StringComparer.Ordinal.Compare(
                            firstUpload.Reference.ObjectId.Value,
                            result.Metadata!.Reference.ObjectId.Value) > 0
                            ? firstUpload
                            : result.Metadata;
                        uploadGate.TrySetResult();
                    }

                    await uploadGate.Task.WaitAsync(
                        TimeSpan.FromSeconds(10),
                        cancellationToken);
                }
            }

            return result;
        }

        public async Task<OpaqueStoreReadBackResult> ReadBackExactAsync(
            OpaqueStoreReadBackRequest request,
            CancellationToken cancellationToken)
        {
            if (request.Expected == loserUpload)
            {
                await loserDeletedGate.Task.WaitAsync(
                    TimeSpan.FromSeconds(10),
                    cancellationToken);
            }

            return await inner.ReadBackExactAsync(request, cancellationToken);
        }

        public async Task<OpaqueStoreDeleteResult> DeleteExactAsync(
            OpaqueStoreDeleteRequest request,
            CancellationToken cancellationToken)
        {
            var result = await inner.DeleteExactAsync(
                request,
                cancellationToken);
            if (request.Expected == loserUpload)
            {
                loserDeletedGate.TrySetResult();
            }

            return result;
        }

        public void Dispose()
        {
        }
    }
}
