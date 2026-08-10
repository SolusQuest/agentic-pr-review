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
