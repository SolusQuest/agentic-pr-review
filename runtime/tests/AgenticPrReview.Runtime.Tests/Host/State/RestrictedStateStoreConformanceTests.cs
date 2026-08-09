using System.Collections.Immutable;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.RestrictedStateTransactions;

namespace AgenticPrReview.Runtime.Tests.Host.State;

public sealed class RestrictedStateStoreConformanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteListMetadataDownloadReadbackAndDeleteConform(
        bool synthetic)
    {
        await WithStoreAsync(synthetic, async store =>
        {
            var bytes = new byte[] { 0, 7, 0xff, 0, 9 };
            var name = new OpaqueStoreName("conformance");
            var upload = new OpaqueStoreUploadRequest(
                name,
                new OpaqueStoreCorrelationId("operation-1"),
                bytes,
                new OpaqueStoreEncryptedObjectDigest(
                    OpaqueStoreHash.Sha256(bytes)),
                RestrictedStateTestData.Now + 600);

            var uploaded = await store.UploadImmutableAsync(
                upload,
                CancellationToken.None);
            Assert.True(uploaded.Succeeded);
            Assert.Equal(bytes.Length, uploaded.Metadata!.Size);
            Assert.True(uploaded.Metadata.ExpiresAtUnixSeconds >=
                upload.MinimumExpiresAtUnixSeconds);

            var listed = await store.ListExactAsync(
                new OpaqueStoreListRequest(name, 8),
                CancellationToken.None);
            Assert.True(listed.Complete);
            Assert.True(listed.Succeeded);
            Assert.Equal(uploaded.Metadata.Reference, Assert.Single(listed.Objects));

            var metadata = await store.ReadMetadataAsync(
                new OpaqueStoreMetadataRequest(uploaded.Metadata.Reference),
                CancellationToken.None);
            Assert.True(metadata.Succeeded);
            Assert.Equal(uploaded.Metadata, metadata.Metadata);

            var download = await store.DownloadAsync(
                new OpaqueStoreDownloadRequest(metadata.Metadata!, bytes.Length),
                CancellationToken.None);
            Assert.True(download.Succeeded);
            Assert.Equal(bytes, download.EncryptedBytes.ToArray());
            Assert.Equal(
                upload.EncryptedObjectDigest,
                download.Metadata!.EncryptedObjectDigest);

            var readBack = await store.ReadBackExactAsync(
                new OpaqueStoreReadBackRequest(uploaded.Metadata),
                CancellationToken.None);
            Assert.True(readBack.Succeeded);
            Assert.Equal(metadata.Metadata, readBack.Metadata);

            var second = await store.UploadImmutableAsync(
                upload with
                {
                    CorrelationId = new OpaqueStoreCorrelationId("operation-2"),
                },
                CancellationToken.None);
            Assert.True(second.Succeeded);
            Assert.NotEqual(
                uploaded.Metadata.Reference.ObjectId,
                second.Metadata!.Reference.ObjectId);
            var two = await store.ListExactAsync(
                new OpaqueStoreListRequest(name, 8),
                CancellationToken.None);
            Assert.Equal(2, two.Objects.Length);

            var deleted = await store.DeleteExactAsync(
                new OpaqueStoreDeleteRequest(uploaded.Metadata),
                CancellationToken.None);
            Assert.True(deleted.Succeeded);
            var missing = await store.ReadBackExactAsync(
                new OpaqueStoreReadBackRequest(uploaded.Metadata),
                CancellationToken.None);
            Assert.Equal(OpaqueStoreFailure.NotFound, missing.Failure);
        });
    }

    [Fact]
    public void ResultValidationRejectsPartialDuplicateAndAuthorityDefects()
    {
        var name = new OpaqueStoreName("name");
        var reference = new OpaqueStoreObjectReference(
            name,
            new OpaqueStoreObjectId("id"));
        var metadata = Metadata(reference, [1]);

        Assert.False(new OpaqueStoreListResult(
            OpaqueStoreFailure.None,
            [reference],
            Complete: false).Succeeded);
        Assert.False(new OpaqueStoreListResult(
            OpaqueStoreFailure.None,
            [reference, reference],
            Complete: true).Succeeded);
        Assert.False(new OpaqueStoreMetadataResult(
            OpaqueStoreFailure.None,
            metadata with
            {
                ProducingRun = metadata.ProducingRun with { Attempt = -1 },
            }).Succeeded);
        Assert.False(new OpaqueStoreDownloadResult(
            OpaqueStoreFailure.None,
            metadata,
            new byte[] { 1, 2 }).Succeeded);
        Assert.False(new OpaqueStoreDownloadResult(
            OpaqueStoreFailure.None,
            metadata,
            new byte[] { 2 }).Succeeded);
        Assert.False(new OpaqueStoreUploadResult(
            OpaqueStoreFailure.None,
            OpaqueStoreMutationState.OutcomeUnknown,
            metadata).Succeeded);
        Assert.False(new OpaqueStoreDeleteResult(
            OpaqueStoreFailure.None,
            OpaqueStoreMutationState.OutcomeUnknown).Succeeded);
    }

    [Fact]
    public async Task SyntheticAdapterProvesArchiveAndEncryptedDigestsAreIndependent()
    {
        var store = new SyntheticRestrictedStateStore();
        var bytes = new byte[] { 1, 2, 3 };
        var uploaded = await store.UploadImmutableAsync(
            new OpaqueStoreUploadRequest(
                new OpaqueStoreName("digests"),
                new OpaqueStoreCorrelationId("operation"),
                bytes,
                new OpaqueStoreEncryptedObjectDigest(
                    OpaqueStoreHash.Sha256(bytes)),
                RestrictedStateTestData.Expires),
            CancellationToken.None);

        Assert.True(uploaded.Succeeded);
        Assert.NotEqual(
            uploaded.Metadata!.ArchiveDigest.Sha256,
            uploaded.Metadata.EncryptedObjectDigest.Sha256);
        Assert.NotEqual(
            typeof(OpaqueStoreArchiveDigest),
            typeof(OpaqueStoreEncryptedObjectDigest));
        var download = await store.DownloadAsync(
            new OpaqueStoreDownloadRequest(uploaded.Metadata, bytes.Length),
            CancellationToken.None);
        Assert.True(download.Succeeded);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("digest")]
    [InlineData("size")]
    [InlineData("expiry")]
    public async Task CoordinatorRejectsPersistedUploadAuthorityMismatch(
        string field)
    {
        var store = new SyntheticRestrictedStateStore((metadata, request) =>
            field switch
            {
                "name" => metadata with
                {
                    Reference = metadata.Reference with
                    {
                        Name = new OpaqueStoreName("wrong-name"),
                    },
                },
                "digest" => metadata with
                {
                    EncryptedObjectDigest =
                        new OpaqueStoreEncryptedObjectDigest(
                            new string('0', 64)),
                },
                "size" => metadata with { Size = metadata.Size + 1 },
                "expiry" => metadata with
                {
                    ExpiresAtUnixSeconds =
                        request.MinimumExpiresAtUnixSeconds - 1,
                },
                _ => throw new InvalidOperationException(),
            });
        var access = RestrictedStateTestData.Access();
        var keys = new TestKeyResolver();
        var coordinator = new RestrictedStateOpaqueSnapshotStore(store, keys);
        var candidate = RestrictedStateTestData.Candidate(access, keys);

        var result = await coordinator.CompareExchangeAsync(
            access,
            RestrictedStateSnapshotVersion.Absent,
            new RestrictedStateSnapshot([candidate], null),
            CancellationToken.None);

        Assert.False(result.Committed);
        Assert.Equal(RestrictedStateStoreFailure.Invalid, result.Failure);
        Assert.Equal(0, store.ObjectCount);
    }

    [Fact]
    public async Task RollbackAmbiguityIsReportedAsCleanupFailure()
    {
        var store = new SyntheticRestrictedStateStore(
            (metadata, _) => metadata with
            {
                Reference = metadata.Reference with
                {
                    Name = new OpaqueStoreName("wrong-name"),
                },
            },
            failDelete: true);
        var access = RestrictedStateTestData.Access();
        var keys = new TestKeyResolver();
        var coordinator = new RestrictedStateOpaqueSnapshotStore(store, keys);
        var candidate = RestrictedStateTestData.Candidate(access, keys);

        var result = await coordinator.CompareExchangeAsync(
            access,
            RestrictedStateSnapshotVersion.Absent,
            new RestrictedStateSnapshot([candidate], null),
            CancellationToken.None);

        Assert.False(result.Committed);
        Assert.Equal(RestrictedStateStoreFailure.Cleanup, result.Failure);
        Assert.Equal(1, store.ObjectCount);
    }

    private static OpaqueStoreObjectMetadata Metadata(
        OpaqueStoreObjectReference reference,
        byte[] bytes) =>
        new(
            reference,
            new OpaqueStoreProducingRun("run", 1),
            new OpaqueStoreArchiveDigest(OpaqueStoreHash.Sha256(bytes)),
            new OpaqueStoreEncryptedObjectDigest(OpaqueStoreHash.Sha256(bytes)),
            RestrictedStateTestData.Expires,
            bytes.Length);

    private static async Task WithStoreAsync(
        bool synthetic,
        Func<IRestrictedStateStore, Task> action)
    {
        if (synthetic)
        {
            await action(new SyntheticRestrictedStateStore());
            return;
        }

        var root = Path.Join(
            Path.GetTempPath(),
            $"apr-state-conformance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await action(new LocalRestrictedStateStore(
                root,
                timeProvider: new FrozenTimeProvider()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FrozenTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.FromUnixTimeSeconds(RestrictedStateTestData.Now);
    }

    private sealed class SyntheticRestrictedStateStore
        : IRestrictedStateStore
    {
        private readonly Func<
            OpaqueStoreObjectMetadata,
            OpaqueStoreUploadRequest,
            OpaqueStoreObjectMetadata>? mutateUploadMetadata;
        private readonly bool failDelete;
        private readonly Dictionary<string, (OpaqueStoreObjectMetadata, byte[])>
            objects = new(StringComparer.Ordinal);

        internal SyntheticRestrictedStateStore(
            Func<
                OpaqueStoreObjectMetadata,
                OpaqueStoreUploadRequest,
                OpaqueStoreObjectMetadata>? mutateUploadMetadata = null,
            bool failDelete = false)
        {
            this.mutateUploadMetadata = mutateUploadMetadata;
            this.failDelete = failDelete;
        }

        internal int ObjectCount => objects.Count;

        public Task<OpaqueStoreListResult> ListExactAsync(
            OpaqueStoreListRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!OpaqueStoreValidation.IsValid(request))
            {
                return Task.FromResult(OpaqueStoreListResult.Fail(
                    OpaqueStoreFailure.Invalid));
            }

            var matches = objects.Values
                .Select(item => item.Item1.Reference)
                .Where(item => item.Name == request.Name)
                .OrderBy(item => item.ObjectId.Value, StringComparer.Ordinal)
                .ToImmutableArray();
            return Task.FromResult(matches.Length <= request.MaximumObjects
                ? new OpaqueStoreListResult(
                    OpaqueStoreFailure.None,
                    matches,
                    Complete: true)
                : OpaqueStoreListResult.Fail(OpaqueStoreFailure.Incomplete));
        }

        public Task<OpaqueStoreMetadataResult> ReadMetadataAsync(
            OpaqueStoreMetadataRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(TryGet(request.Reference, out var value)
                ? new OpaqueStoreMetadataResult(
                    OpaqueStoreFailure.None,
                    value.Metadata)
                : OpaqueStoreMetadataResult.Fail(OpaqueStoreFailure.NotFound));
        }

        public Task<OpaqueStoreDownloadResult> DownloadAsync(
            OpaqueStoreDownloadRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGet(request.Expected.Reference, out var value))
            {
                return Task.FromResult(OpaqueStoreDownloadResult.Fail(
                    OpaqueStoreFailure.NotFound));
            }

            if (value.Metadata != request.Expected)
            {
                return Task.FromResult(OpaqueStoreDownloadResult.Fail(
                    OpaqueStoreFailure.Conflict));
            }

            return Task.FromResult(new OpaqueStoreDownloadResult(
                OpaqueStoreFailure.None,
                value.Metadata,
                value.Bytes));
        }

        public Task<OpaqueStoreUploadResult> UploadImmutableAsync(
            OpaqueStoreUploadRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!OpaqueStoreValidation.IsValid(request) ||
                OpaqueStoreHash.Sha256(request.EncryptedBytes.Span) !=
                    request.EncryptedObjectDigest.Sha256)
            {
                return Task.FromResult(OpaqueStoreUploadResult.Fail(
                    OpaqueStoreFailure.Invalid));
            }

            var bytes = request.EncryptedBytes.ToArray();
            var objectId = new OpaqueStoreObjectId(Guid.NewGuid().ToString("N"));
            var reference = new OpaqueStoreObjectReference(
                request.Name,
                objectId);
            var archiveInput = new byte[bytes.Length + 1];
            bytes.CopyTo(archiveInput, 1);
            var metadata = new OpaqueStoreObjectMetadata(
                reference,
                new OpaqueStoreProducingRun("synthetic-run", 7),
                new OpaqueStoreArchiveDigest(
                    OpaqueStoreHash.Sha256(archiveInput)),
                request.EncryptedObjectDigest,
                request.MinimumExpiresAtUnixSeconds + 1,
                bytes.Length);
            metadata = mutateUploadMetadata?.Invoke(metadata, request) ??
                metadata;
            objects.Add(objectId.Value, (metadata, bytes));
            return Task.FromResult(new OpaqueStoreUploadResult(
                OpaqueStoreFailure.None,
                OpaqueStoreMutationState.Committed,
                metadata));
        }

        public Task<OpaqueStoreReadBackResult> ReadBackExactAsync(
            OpaqueStoreReadBackRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                TryGet(request.Expected.Reference, out var value) &&
                value.Metadata == request.Expected
                    ? new OpaqueStoreReadBackResult(
                        OpaqueStoreFailure.None,
                        value.Metadata)
                    : OpaqueStoreReadBackResult.Fail(
                        OpaqueStoreFailure.NotFound));
        }

        public Task<OpaqueStoreDeleteResult> DeleteExactAsync(
            OpaqueStoreDeleteRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGet(request.Expected.Reference, out var value))
            {
                return Task.FromResult(OpaqueStoreDeleteResult.Fail(
                    OpaqueStoreFailure.NotFound));
            }

            if (value.Metadata != request.Expected)
            {
                return Task.FromResult(OpaqueStoreDeleteResult.Fail(
                    OpaqueStoreFailure.Conflict));
            }

            if (failDelete)
            {
                return Task.FromResult(OpaqueStoreDeleteResult.Fail(
                    OpaqueStoreFailure.OutcomeUnknown,
                    OpaqueStoreMutationState.OutcomeUnknown));
            }

            objects.Remove(request.Expected.Reference.ObjectId.Value);
            return Task.FromResult(new OpaqueStoreDeleteResult(
                OpaqueStoreFailure.None,
                OpaqueStoreMutationState.Committed));
        }

        private bool TryGet(
            OpaqueStoreObjectReference reference,
            out (OpaqueStoreObjectMetadata Metadata, byte[] Bytes) value) =>
            objects.TryGetValue(reference.ObjectId.Value, out value) &&
            value.Metadata.Reference == reference;
    }
}
