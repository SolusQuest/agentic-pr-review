using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Tests.Host.State.Locator;

public sealed class LocalLocatorRootLifecycleTests
{
    [Fact]
    public async Task FreshInstancesSelectThenRotateOneDurableSentinel()
    {
        await WithRootAsync(async root =>
        {
            using var access = LocatorTestData.Access();
            using var oldKeys = LocatorTestData.KeyRing(
                access,
                currentBase64: LocatorTestData.PreviousBase64);
            var time = new FrozenLocatorTimeProvider(LocatorTestData.Now);
            var initialized = await new LocatorRootService(
                    new LocalRestrictedStateStore(root, timeProvider: time),
                    oldKeys,
                    time)
                .ResolveAsync(access, 0, CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            Assert.True(initialized.Context!.TryDeriveOpaqueName(
                access,
                "state",
                [1, 2, 3],
                out var oldName));

            var fresh = await new LocatorRootService(
                    new LocalRestrictedStateStore(root, timeProvider: time),
                    oldKeys,
                    time)
                .ResolveAsync(access, 0, CancellationToken.None);
            Assert.True(fresh.Succeeded, fresh.Code);

            using var rotatedKeys = LocatorTestData.KeyRing(
                access,
                LocatorTestData.PreviousBase64);
            var rotated = await new LocatorRootService(
                    new LocalRestrictedStateStore(root, timeProvider: time),
                    rotatedKeys,
                    time)
                .ResolveAsync(access, 0, CancellationToken.None);
            Assert.True(rotated.Succeeded, rotated.Code);
            Assert.True(rotated.Context!.TryDeriveOpaqueName(
                access,
                "state",
                [1, 2, 3],
                out var rotatedName));
            Assert.Equal(oldName, rotatedName);

            var store = new LocalRestrictedStateStore(
                root,
                timeProvider: time);
            var list = await store.ListExactAsync(
                new OpaqueStoreListRequest(
                    new OpaqueStoreName(LocatorRootFormat.StoreName),
                    LocatorRootFormat.MaximumPhysicalSentinels),
                CancellationToken.None);
            Assert.True(list.Succeeded);
            var reference = Assert.Single(list.Objects);
            var metadata = await store.ReadMetadataAsync(
                new OpaqueStoreMetadataRequest(reference),
                CancellationToken.None);
            Assert.True(metadata.Succeeded);
            var download = await store.DownloadAsync(
                new OpaqueStoreDownloadRequest(
                    metadata.Metadata!,
                    LocatorRootFormat.MaximumEnvelopeBytes),
                CancellationToken.None);
            Assert.True(download.Succeeded);
            Assert.True(LocatorRootSentinelCodec.TryDecrypt(
                access,
                rotatedKeys,
                download.EncryptedBytes.Span,
                out var sentinel,
                out var code),
                code);
            Assert.Equal<ulong>(1, sentinel!.Generation);
            Assert.Equal(rotatedKeys.CurrentKeyId, sentinel.WriterKeyId);
        });
    }

    [Fact]
    public async Task CommittedDirectorySyncFailureIsRecoveredByRealRelist()
    {
        await WithRootAsync(async root =>
        {
            using var access = LocatorTestData.Access();
            using var keys = LocatorTestData.KeyRing(access);
            var time = new FrozenLocatorTimeProvider(LocatorTestData.Now);
            var store = new LocalRestrictedStateStore(
                root,
                syncDirectoryTestHook: _ => false,
                timeProvider: time);

            var result = await new LocatorRootService(store, keys, time)
                .ResolveAsync(access, 0, CancellationToken.None);

            Assert.True(result.Succeeded, result.Code);
            var fresh = new LocalRestrictedStateStore(
                root,
                timeProvider: time);
            var list = await fresh.ListExactAsync(
                new OpaqueStoreListRequest(
                    new OpaqueStoreName(LocatorRootFormat.StoreName),
                    LocatorRootFormat.MaximumPhysicalSentinels),
                CancellationToken.None);
            Assert.True(list.Succeeded);
            Assert.Single(list.Objects);
        });
    }

    [Fact]
    public async Task DuplicateRefreshAndEightRecordHeadroomConvergeLocally()
    {
        await WithRootAsync(async root =>
        {
            using var access = LocatorTestData.Access();
            using var keys = LocatorTestData.KeyRing(access);
            var time = new FrozenLocatorTimeProvider(LocatorTestData.Now);
            var store = new LocalRestrictedStateStore(
                root,
                timeProvider: time);
            var service = new LocatorRootService(store, keys, time);
            var initialized = await service.ResolveAsync(
                access,
                0,
                CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            initialized.Context!.Dispose();

            var current = await ReadSingleAsync(store);
            Assert.True(LocatorRootSentinelCodec.TryDecrypt(
                access,
                keys,
                current.Bytes,
                out var sentinel,
                out var code),
                code);
            for (var index = 0; index < 7; index++)
            {
                _ = await UploadAsync(
                    store,
                    current.Bytes,
                    sentinel!.RequiredExpiresAtUnixSeconds,
                    $"duplicate-{index}");
            }

            Assert.Equal(
                LocatorRootFormat.MaximumPhysicalSentinels,
                (await ListAsync(store)).Objects.Length);
            var checkedHeadroom = false;
            var guardedStore = new LocalRestrictedStateStore(
                root,
                beforeWriteTestHook: () =>
                {
                    checkedHeadroom = true;
                    Assert.True(CountObjectFiles(root) <=
                        LocatorRootFormat.MaximumPhysicalSentinels - 1);
                },
                timeProvider: time);
            var refreshed = await new LocatorRootService(
                    guardedStore,
                    keys,
                    time)
                .ResolveAsync(
                    access,
                    sentinel!.RequiredExpiresAtUnixSeconds -
                        StateRetentionRequirements
                            .SentinelDependentMarginSeconds + 1,
                    CancellationToken.None);

            Assert.True(refreshed.Succeeded, refreshed.Code);
            refreshed.Context!.Dispose();
            Assert.True(checkedHeadroom);
            Assert.Single((await ListAsync(guardedStore)).Objects);
            Assert.DoesNotContain(
                Directory.EnumerateFiles(root),
                path => path.EndsWith(".delete", StringComparison.Ordinal));
            CryptographicOperations.ZeroMemory(sentinel.Root);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteOutcomeUnknownReconcilesBothPhysicalOutcomes(
        bool objectRemains)
    {
        await WithRootAsync(async root =>
        {
            using var access = LocatorTestData.Access();
            using var keys = LocatorTestData.KeyRing(access);
            var time = new FrozenLocatorTimeProvider(LocatorTestData.Now);
            var seedStore = new LocalRestrictedStateStore(
                root,
                timeProvider: time);
            var initialized = await new LocatorRootService(
                    seedStore,
                    keys,
                    time)
                .ResolveAsync(access, 0, CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            initialized.Context!.Dispose();
            var current = await ReadSingleAsync(seedStore);
            _ = await UploadAsync(
                seedStore,
                current.Bytes,
                current.Metadata.ExpiresAtUnixSeconds,
                "ambiguous-delete-duplicate");

            var ambiguousStore = new LocalRestrictedStateStore(
                root,
                deleteTemporaryTestHook:
                    AmbiguousDeleteHook(objectRemains),
                timeProvider: time);
            var result = await new LocatorRootService(
                    ambiguousStore,
                    keys,
                    time)
                .ResolveAsync(access, 0, CancellationToken.None);

            if (objectRemains)
            {
                Assert.Equal(LocatorCodes.CleanupFailed, result.Code);
                Assert.Equal(2, (await ListAsync(
                    ambiguousStore)).Objects.Length);
                var retried = await new LocatorRootService(
                        new LocalRestrictedStateStore(
                            root,
                            timeProvider: time),
                        keys,
                        time)
                    .ResolveAsync(access, 0, CancellationToken.None);
                Assert.True(retried.Succeeded, retried.Code);
                retried.Context!.Dispose();
            }
            else
            {
                Assert.True(result.Succeeded, result.Code);
                result.Context!.Dispose();
            }

            Assert.Single((await ListAsync(
                new LocalRestrictedStateStore(
                    root,
                    timeProvider: time))).Objects);
        });
    }

    [Fact]
    public async Task ControlledTimeRefreshProvesActualExpiryAndDeletesPredecessor()
    {
        await WithRootAsync(async root =>
        {
            using var access = LocatorTestData.Access();
            using var keys = LocatorTestData.KeyRing(access);
            var initialTime = new FrozenLocatorTimeProvider(
                LocatorTestData.Now);
            var initialStore = new LocalRestrictedStateStore(
                root,
                timeProvider: initialTime);
            var initialized = await new LocatorRootService(
                    initialStore,
                    keys,
                    initialTime)
                .ResolveAsync(access, 0, CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            initialized.Context!.Dispose();
            var old = await ReadSingleAsync(initialStore);

            var refreshTime = new FrozenLocatorTimeProvider(
                LocatorTestData.Now + 2 * 24 * 60 * 60);
            var refreshStore = new LocalRestrictedStateStore(
                root,
                timeProvider: refreshTime);
            var refreshed = await new LocatorRootService(
                    refreshStore,
                    keys,
                    refreshTime)
                .ResolveAsync(access, 0, CancellationToken.None);
            Assert.True(refreshed.Succeeded, refreshed.Code);
            refreshed.Context!.Dispose();

            var current = await ReadSingleAsync(refreshStore);
            Assert.NotEqual(
                old.Metadata.Reference.ObjectId,
                current.Metadata.Reference.ObjectId);
            Assert.True(LocatorRootSentinelCodec.TryDecrypt(
                access,
                keys,
                current.Bytes,
                out var sentinel,
                out var code),
                code);
            Assert.Equal<ulong>(1, sentinel!.Generation);
            Assert.True(current.Metadata.ExpiresAtUnixSeconds >=
                sentinel.RequiredExpiresAtUnixSeconds);
            Assert.DoesNotContain(
                (await ListAsync(refreshStore)).Objects,
                item => item.ObjectId == old.Metadata.Reference.ObjectId);
            CryptographicOperations.ZeroMemory(sentinel.Root);
        });
    }

    [Fact]
    public async Task ExpiredExactSupersededObjectIsRecoveredLocally()
    {
        await WithRootAsync(async root =>
        {
            using var access = LocatorTestData.Access();
            using var keys = LocatorTestData.KeyRing(access);
            var initialTime = new FrozenLocatorTimeProvider(
                LocatorTestData.Now);
            var store = new LocalRestrictedStateStore(
                root,
                timeProvider: initialTime);
            var stableRoot = Enumerable.Repeat((byte)0x64, 32).ToArray();
            var old = LocatorTestData.Sentinel(
                keys,
                root: stableRoot.ToArray(),
                requiredExpiry: LocatorTestData.Now + 24 * 60 * 60);
            Assert.True(LocatorRootSentinelCodec.TryEncrypt(
                access,
                keys,
                old,
                out var oldEnvelope,
                out var oldCode),
                oldCode);
            var oldMetadata = await UploadAsync(
                store,
                oldEnvelope!,
                LocatorTestData.Now + 2 * 24 * 60 * 60,
                "expired-local-old");

            var absentPredecessor = LocatorRootSentinelCodec.Identity(
                LocatorTestData.Metadata(
                    "pruned-local-predecessor",
                    LocatorTestData.Now + 20 * 24 * 60 * 60));
            var head = LocatorTestData.Sentinel(
                keys,
                root: stableRoot.ToArray(),
                generation: 1,
                requiredExpiry:
                    LocatorTestData.Now + 15 * 24 * 60 * 60,
                predecessors: [absentPredecessor],
                superseded:
                [
                    LocatorRootSentinelCodec.Identity(oldMetadata),
                ]);
            Assert.True(LocatorRootSentinelCodec.TryEncrypt(
                access,
                keys,
                head,
                out var headEnvelope,
                out var headCode),
                headCode);
            var headMetadata = await UploadAsync(
                store,
                headEnvelope!,
                LocatorTestData.Now + 20 * 24 * 60 * 60,
                "live-local-head");

            var recoveryTime = new FrozenLocatorTimeProvider(
                LocatorTestData.Now + 3 * 24 * 60 * 60);
            var recoveryStore = new LocalRestrictedStateStore(
                root,
                timeProvider: recoveryTime);
            var recovered = await new LocatorRootService(
                    recoveryStore,
                    keys,
                    recoveryTime)
                .ResolveAsync(access, 0, CancellationToken.None);

            Assert.True(recovered.Succeeded, recovered.Code);
            recovered.Context!.Dispose();
            var survivor = Assert.Single(
                (await ListAsync(recoveryStore)).Objects);
            Assert.Equal(headMetadata.Reference, survivor);
            CryptographicOperations.ZeroMemory(stableRoot);
            CryptographicOperations.ZeroMemory(old.Root);
            CryptographicOperations.ZeroMemory(head.Root);
        });
    }

    [Fact]
    public async Task ExpiredImmediatePredecessorIsRecoveredLocally()
    {
        await WithRootAsync(async root =>
        {
            using var access = LocatorTestData.Access();
            using var keys = LocatorTestData.KeyRing(access);
            var initialTime = new FrozenLocatorTimeProvider(
                LocatorTestData.Now);
            var initialStore = new LocalRestrictedStateStore(
                root,
                timeProvider: initialTime);
            var initialized = await new LocatorRootService(
                    initialStore,
                    keys,
                    initialTime)
                .ResolveAsync(access, 0, CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            initialized.Context!.Dispose();
            var predecessor = await ReadSingleAsync(initialStore);

            var refreshTime = new FrozenLocatorTimeProvider(
                LocatorTestData.Now + 2 * 24 * 60 * 60);
            var interruptedStore = new LocalRestrictedStateStore(
                root,
                deleteTemporaryTestHook: AmbiguousDeleteHook(
                    objectRemains: true),
                timeProvider: refreshTime);
            var interrupted = await new LocatorRootService(
                    interruptedStore,
                    keys,
                    refreshTime)
                .ResolveAsync(
                    access,
                    LocatorTestData.Now + 20 * 24 * 60 * 60,
                    CancellationToken.None);
            Assert.Equal(LocatorCodes.CleanupFailed, interrupted.Code);
            Assert.Equal(2, (await ListAsync(
                interruptedStore)).Objects.Length);

            var recoveryTime = new FrozenLocatorTimeProvider(
                LocatorTestData.Now + 11 * 24 * 60 * 60);
            var recoveryStore = new LocalRestrictedStateStore(
                root,
                timeProvider: recoveryTime);
            var recovered = await new LocatorRootService(
                    recoveryStore,
                    keys,
                    recoveryTime)
                .ResolveAsync(access, 0, CancellationToken.None);

            Assert.True(recovered.Succeeded, recovered.Code);
            recovered.Context!.Dispose();
            var survivor = Assert.Single(
                (await ListAsync(recoveryStore)).Objects);
            Assert.NotEqual(predecessor.Metadata.Reference, survivor);
            var current = await ReadSingleAsync(recoveryStore);
            Assert.True(LocatorRootSentinelCodec.TryDecrypt(
                access,
                keys,
                current.Bytes,
                out var sentinel,
                out var code),
                code);
            Assert.Equal<ulong>(1, sentinel!.Generation);
            CryptographicOperations.ZeroMemory(sentinel.Root);
        });
    }

    [Fact]
    public async Task UnderFloorRecordIsCleanedAndReinitializedLocally()
    {
        await WithRootAsync(async root =>
        {
            using var access = LocatorTestData.Access();
            using var keys = LocatorTestData.KeyRing(access);
            var time = new FrozenLocatorTimeProvider(LocatorTestData.Now);
            var store = new LocalRestrictedStateStore(
                root,
                timeProvider: time);
            var initialized = await new LocatorRootService(
                    store,
                    keys,
                    time)
                .ResolveAsync(access, 0, CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            initialized.Context!.Dispose();
            var current = await ReadSingleAsync(store);
            Assert.True(LocatorRootSentinelCodec.TryDecrypt(
                access,
                keys,
                current.Bytes,
                out var sentinel,
                out var code),
                code);
            var underRetained = current.Metadata with
            {
                ExpiresAtUnixSeconds =
                    sentinel!.RequiredExpiresAtUnixSeconds - 1,
            };
            Assert.True(LocalOpaqueStoreRecordCodec.TryWrite(
                underRetained,
                current.Bytes,
                out var record));
            var path = Assert.Single(Directory.EnumerateFiles(
                root,
                "opaque-*.aprobject",
                SearchOption.TopDirectoryOnly));
            await File.WriteAllBytesAsync(path, record);

            var rejected = await new LocatorRootService(
                    new LocalRestrictedStateStore(
                        root,
                        timeProvider: time),
                    keys,
                    time)
                .ResolveAsync(access, 0, CancellationToken.None);

            Assert.True(rejected.Succeeded, rejected.Code);
            rejected.Context!.Dispose();
            var replacement = Assert.Single(
                (await ListAsync(new LocalRestrictedStateStore(
                    root,
                    timeProvider: time))).Objects);
            Assert.NotEqual(current.Metadata.Reference, replacement);
            CryptographicOperations.ZeroMemory(sentinel.Root);
            CryptographicOperations.ZeroMemory(record);
        });
    }

    private static async Task<OpaqueStoreListResult> ListAsync(
        IRestrictedStateStore store) =>
        await store.ListExactAsync(
            new OpaqueStoreListRequest(
                new OpaqueStoreName(LocatorRootFormat.StoreName),
                LocatorRootFormat.MaximumPhysicalSentinels),
            CancellationToken.None);

    private static async Task<LocalObject> ReadSingleAsync(
        IRestrictedStateStore store)
    {
        var reference = Assert.Single((await ListAsync(store)).Objects);
        var metadata = await store.ReadMetadataAsync(
            new OpaqueStoreMetadataRequest(reference),
            CancellationToken.None);
        Assert.True(metadata.Succeeded);
        var download = await store.DownloadAsync(
            new OpaqueStoreDownloadRequest(
                metadata.Metadata!,
                LocatorRootFormat.MaximumEnvelopeBytes),
            CancellationToken.None);
        Assert.True(download.Succeeded);
        return new LocalObject(
            metadata.Metadata!,
            download.EncryptedBytes.ToArray());
    }

    private static async Task<OpaqueStoreObjectMetadata> UploadAsync(
        IRestrictedStateStore store,
        byte[] envelope,
        long minimumExpiry,
        string correlation)
    {
        var digest = OpaqueStoreHash.Sha256(envelope);
        var upload = await store.UploadImmutableAsync(
            new OpaqueStoreUploadRequest(
                new OpaqueStoreName(LocatorRootFormat.StoreName),
                new OpaqueStoreCorrelationId(correlation),
                envelope,
                new OpaqueStoreEncryptedObjectDigest(digest),
                minimumExpiry),
            CancellationToken.None);
        Assert.True(upload.Succeeded, upload.Failure.ToString());
        return upload.Metadata!;
    }

    private static int CountObjectFiles(string root) =>
        Directory.EnumerateFiles(
            root,
            "opaque-*.aprobject",
            SearchOption.TopDirectoryOnly).Count();

    private static Func<string, bool> AmbiguousDeleteHook(
        bool objectRemains)
    {
        var triggered = false;
        return path =>
        {
            if (!triggered &&
                path.EndsWith(".delete", StringComparison.Ordinal))
            {
                triggered = true;
                if (objectRemains)
                {
                    var name = Path.GetFileName(path);
                    var marker = name.IndexOf(
                        ".aprobject",
                        StringComparison.Ordinal);
                    Assert.True(marker > 1);
                    var originalName = name[1..(
                        marker + ".aprobject".Length)];
                    File.Copy(
                        path,
                        Path.Join(Path.GetDirectoryName(path), originalName),
                        overwrite: false);
                }
                else
                {
                    File.Delete(path);
                }

                return false;
            }

            File.Delete(path);
            return true;
        };
    }

    private sealed record LocalObject(
        OpaqueStoreObjectMetadata Metadata,
        byte[] Bytes);

    private static async Task WithRootAsync(Func<string, Task> action)
    {
        var root = Path.Join(
            Path.GetTempPath(),
            $"apr-locator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await action(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
