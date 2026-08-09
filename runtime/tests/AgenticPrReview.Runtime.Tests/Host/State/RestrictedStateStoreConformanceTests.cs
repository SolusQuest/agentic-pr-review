using System.Collections.Immutable;
using AgenticPrReview.Runtime.Agent.Core;
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
        await RestrictedStateStoreConformanceHarness.VerifyAsync(
            action => WithStoreAsync(synthetic, action));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareAndAcceptIgnoreCancellationAfterDurableIndexReadback(
        bool accept)
    {
        var store = new SyntheticRestrictedStateStore();
        var keys = new TestKeyResolver();
        var access = RestrictedStateTestData.Access();
        var service = new RestrictedStateService(
            store,
            keys,
            new TestSessionAdmission(),
            () => RestrictedStateTestData.Now);
        RestrictedStatePrepareResult? prepared = null;
        if (accept)
        {
            prepared = await service.PrepareAsync(
                access,
                new RestrictedStatePrepareRequest(
                    null,
                    new byte[] { 1 },
                    RestrictedStateTestData.SessionContext()),
                CancellationToken.None);
            Assert.Equal(StateAction.Prepared, prepared.Result.Action);
        }

        using var cancellation = new CancellationTokenSource();
        var cancelAtReadBack = store.ReadBackCalls + (accept ? 1 : 2);
        store.AfterReadBack = (call, _) =>
        {
            if (call == cancelAtReadBack)
            {
                cancellation.Cancel();
            }
        };

        var action = accept
            ? await service.AcceptAsync(
                access,
                null,
                prepared!.Receipt!,
                RestrictedStateTestData.SessionContext(),
                cancellation.Token)
            : (await service.PrepareAsync(
                access,
                new RestrictedStatePrepareRequest(
                    null,
                    new byte[] { 1 },
                    RestrictedStateTestData.SessionContext()),
                cancellation.Token)).Result;

        Assert.Equal(
            accept ? StateAction.Accepted : StateAction.Prepared,
            action.Action);
        store.AfterReadBack = null;
        var enumerated = await service.EnumerateAsync(
            access,
            CancellationToken.None);
        Assert.True(enumerated.Result.Action is
            StateAction.Enumerated or StateAction.Bootstrap);
    }

    [Fact]
    public async Task AmbiguousIndexRollbackAfterReadbackMapsToIo()
    {
        var store = new SyntheticRestrictedStateStore(failDelete: true);
        store.AfterReadBack = (call, _) =>
        {
            if (call == 2)
            {
                store.FailLists = true;
            }
        };
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
        Assert.Equal(RestrictedStateStoreFailure.Io, result.Failure);
        Assert.Equal(2, store.ObjectCount);
    }

    [Fact]
    public async Task PartialRawDeletionCannotReportSuccessfulReset()
    {
        var store = new SyntheticRestrictedStateStore
        {
            FailDeleteOnCall = 2,
        };
        var access = RestrictedStateTestData.Access();
        var bytes = new byte[] { 1 };
        store.Seed(Metadata(
            new OpaqueStoreObjectReference(
                RawName(access, index: true),
                new OpaqueStoreObjectId("index")),
            bytes), bytes);
        store.Seed(Metadata(
            new OpaqueStoreObjectReference(
                RawName(access, index: false),
                new OpaqueStoreObjectId("candidate")),
            bytes), bytes);
        var service = new RestrictedStateService(
            store,
            new TestKeyResolver(),
            new TestSessionAdmission(),
            () => RestrictedStateTestData.Now);

        var result = await service.ResetAsync(
            access,
            CancellationToken.None);

        Assert.Equal(StateAction.Failed, result.Action);
        Assert.NotEqual(RestrictedStateCodes.Reset, result.Code);
        Assert.Equal(1, store.ObjectCount);
    }

    [Fact]
    public async Task RawVersionUsesInjectiveNominalFieldFraming()
    {
        var store = new SyntheticRestrictedStateStore();
        var access = RestrictedStateTestData.Access();
        var name = RawName(access, index: false);
        var bytes = new byte[] { 1 };
        var first = Metadata(
            new OpaqueStoreObjectReference(
                name,
                new OpaqueStoreObjectId("a:b")),
            bytes) with
        {
            ProducingRun = new OpaqueStoreProducingRun("c", 1),
        };
        var second = Metadata(
            new OpaqueStoreObjectReference(
                name,
                new OpaqueStoreObjectId("a")),
            bytes) with
        {
            ProducingRun = new OpaqueStoreProducingRun("b:c", 1),
        };
        var coordinator = new RestrictedStateOpaqueSnapshotStore(
            store,
            new TestKeyResolver());
        store.Seed(first, bytes);
        var firstRead = await coordinator.ReadRawVersionAsync(
            access,
            CancellationToken.None);
        store.Clear();
        store.Seed(second, bytes);
        var secondRead = await coordinator.ReadRawVersionAsync(
            access,
            CancellationToken.None);

        Assert.True(firstRead.Succeeded);
        Assert.True(secondRead.Succeeded);
        Assert.NotEqual(firstRead.Version, secondRead.Version);
        var staleDelete = await coordinator.CompareDeleteRawAsync(
            access,
            firstRead.Version!,
            CancellationToken.None);
        Assert.Equal(RestrictedStateStoreFailure.Conflict, staleDelete.Failure);
        Assert.False(staleDelete.Committed);
        Assert.Equal(1, store.ObjectCount);
    }

    [Fact]
    public async Task DuplicateAdapterResultsCannotReportCompleteSuccess()
    {
        var store = new SyntheticRestrictedStateStore
        {
            DuplicateListResults = true,
        };
        var bytes = new byte[] { 1 };
        var upload = await store.UploadImmutableAsync(
            new OpaqueStoreUploadRequest(
                new OpaqueStoreName("duplicates"),
                new OpaqueStoreCorrelationId("operation"),
                bytes,
                new OpaqueStoreEncryptedObjectDigest(
                    OpaqueStoreHash.Sha256(bytes)),
                RestrictedStateTestData.Expires),
            CancellationToken.None);
        Assert.True(upload.Succeeded);

        var listed = await store.ListExactAsync(
            new OpaqueStoreListRequest(
                upload.Metadata!.Reference.Name,
                8),
            CancellationToken.None);

        Assert.True(listed.Complete);
        Assert.Equal(2, listed.Objects.Length);
        Assert.False(listed.Succeeded);
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

    private static OpaqueStoreName RawName(
        AuthorizedStateAccess access,
        bool index)
    {
        var scope = RestrictedStateSnapshotCodec.WriteScopeIdentity(
            access.Scope);
        return new OpaqueStoreName(AgentCanonical.HashDomain(
            index
                ? "apr.state-r3-opaque-index-name.s1"
                : "apr.state-r3-opaque-candidate-name.s1",
            scope));
    }

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
        private int deleteCalls;

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

        internal int ReadBackCalls { get; private set; }

        internal int FailDeleteOnCall { get; init; }

        internal bool FailLists { get; set; }

        internal bool DuplicateListResults { get; init; }

        internal Action<int, OpaqueStoreObjectMetadata>? AfterReadBack
        {
            get;
            set;
        }

        public Task<OpaqueStoreListResult> ListExactAsync(
            OpaqueStoreListRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailLists)
            {
                return Task.FromResult(OpaqueStoreListResult.Fail(
                    OpaqueStoreFailure.Io));
            }

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
            if (DuplicateListResults && matches.Length > 0)
            {
                return Task.FromResult(new OpaqueStoreListResult(
                    OpaqueStoreFailure.None,
                    [matches[0], matches[0]],
                    Complete: true));
            }

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
            var result =
                TryGet(request.Expected.Reference, out var value) &&
                value.Metadata == request.Expected
                    ? new OpaqueStoreReadBackResult(
                        OpaqueStoreFailure.None,
                        value.Metadata)
                    : OpaqueStoreReadBackResult.Fail(
                        OpaqueStoreFailure.NotFound);
            if (result.Succeeded)
            {
                ReadBackCalls++;
                AfterReadBack?.Invoke(ReadBackCalls, result.Metadata!);
            }

            return Task.FromResult(result);
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

            deleteCalls++;
            if (failDelete || deleteCalls == FailDeleteOnCall)
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

        internal void Seed(
            OpaqueStoreObjectMetadata metadata,
            byte[] bytes) =>
            objects.Add(
                metadata.Reference.ObjectId.Value,
                (metadata, bytes.ToArray()));

        internal void Clear() => objects.Clear();

        private bool TryGet(
            OpaqueStoreObjectReference reference,
            out (OpaqueStoreObjectMetadata Metadata, byte[] Bytes) value) =>
            objects.TryGetValue(reference.ObjectId.Value, out value) &&
            value.Metadata.Reference == reference;
    }
}

internal static class RestrictedStateStoreConformanceHarness
{
    internal static async Task VerifyAsync(
        Func<Func<IRestrictedStateStore, Task>, Task> withStore)
    {
        await withStore(async store =>
        {
            var bytes = new byte[] { 0, 7, 0xff, 0, 9 };
            var name = new OpaqueStoreName("conformance");
            var upload = Request(name, "operation-1", bytes);
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
            Assert.True(listed.Succeeded);
            Assert.True(listed.Complete);
            Assert.Equal(
                uploaded.Metadata.Reference,
                Assert.Single(listed.Objects));

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

            var wrongAuthority = uploaded.Metadata with
            {
                ProducingRun = uploaded.Metadata.ProducingRun with
                {
                    Attempt = uploaded.Metadata.ProducingRun.Attempt + 1,
                },
            };
            var wrongReadBack = await store.ReadBackExactAsync(
                new OpaqueStoreReadBackRequest(wrongAuthority),
                CancellationToken.None);
            var wrongArchive = await store.ReadBackExactAsync(
                new OpaqueStoreReadBackRequest(uploaded.Metadata with
                {
                    ArchiveDigest = new OpaqueStoreArchiveDigest(
                        new string('0', 64)),
                }),
                CancellationToken.None);
            var wrongDownload = await store.DownloadAsync(
                new OpaqueStoreDownloadRequest(
                    uploaded.Metadata with
                    {
                        EncryptedObjectDigest =
                            new OpaqueStoreEncryptedObjectDigest(
                                new string('0', 64)),
                    },
                    bytes.Length),
                CancellationToken.None);
            Assert.False(wrongReadBack.Succeeded);
            Assert.False(wrongArchive.Succeeded);
            Assert.False(wrongDownload.Succeeded);

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
            var incomplete = await store.ListExactAsync(
                new OpaqueStoreListRequest(name, 1),
                CancellationToken.None);
            Assert.Equal(OpaqueStoreFailure.Incomplete, incomplete.Failure);
            Assert.False(incomplete.Succeeded);

            var invalidDigest = await store.UploadImmutableAsync(
                upload with
                {
                    CorrelationId = new OpaqueStoreCorrelationId("invalid"),
                    EncryptedObjectDigest =
                        new OpaqueStoreEncryptedObjectDigest(
                            new string('0', 64)),
                },
                CancellationToken.None);
            var invalidBound = await store.ListExactAsync(
                new OpaqueStoreListRequest(
                    name,
                    OpaqueStoreLimits.MaximumObjects + 1),
                CancellationToken.None);
            Assert.Equal(OpaqueStoreFailure.Invalid, invalidDigest.Failure);
            Assert.Equal(OpaqueStoreFailure.Invalid, invalidBound.Failure);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.ListExactAsync(
                    new OpaqueStoreListRequest(name, 8),
                    cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.ReadMetadataAsync(
                    new OpaqueStoreMetadataRequest(uploaded.Metadata.Reference),
                    cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.DownloadAsync(
                    new OpaqueStoreDownloadRequest(uploaded.Metadata, bytes.Length),
                    cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.UploadImmutableAsync(
                    upload with
                    {
                        CorrelationId = new OpaqueStoreCorrelationId("cancelled"),
                    },
                    cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.ReadBackExactAsync(
                    new OpaqueStoreReadBackRequest(uploaded.Metadata),
                    cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.DeleteExactAsync(
                    new OpaqueStoreDeleteRequest(uploaded.Metadata),
                    cancelled.Token));

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

    private static OpaqueStoreUploadRequest Request(
        OpaqueStoreName name,
        string correlation,
        byte[] bytes) =>
        new(
            name,
            new OpaqueStoreCorrelationId(correlation),
            bytes,
            new OpaqueStoreEncryptedObjectDigest(
                OpaqueStoreHash.Sha256(bytes)),
            RestrictedStateTestData.Now + 600);
}
