using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Tests.Host.State;

namespace AgenticPrReview.Runtime.Tests.Application;

public sealed class LiveAgentAdmissionBindingTests
{
    [Fact]
    public void MissingAdmissionRejectsConstructionBeforeStoreFactory()
    {
        var calls = 0;
        var failure = Assert.Throws<ArgumentNullException>(() => new LiveAgentStateTransactionFactory(
            TimeProvider.System, _ => { calls++; throw new InvalidOperationException("unexpected-store-factory"); }, null));
        Assert.Equal("admission", failure.ParamName);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void MissingAdmissionRejectsTransactionBeforeStoreOrKeyCalls()
    {
        var store = new UnusedStore();
        var keys = new TestKeyResolver();
        var failure = Assert.Throws<ArgumentNullException>(() =>
            new LiveAgentStateTransaction(store, keys, TimeProvider.System, null));
        Assert.Equal("admission", failure.ParamName);
        Assert.Equal(0, store.Calls);
        Assert.Equal(0, keys.ReadCalls);
        Assert.Equal(0, keys.WriteCalls);
    }

    // Generic APR store tripwire only; no fake state admission or SDK execution evidence.
    private sealed class UnusedStore : IRestrictedStateStore
    {
        internal int Calls { get; private set; }
        private Task<T> Touch<T>() { Calls++; throw new InvalidOperationException("unexpected-store-call"); }
        public Task<OpaqueStoreListResult> ListExactAsync(OpaqueStoreListRequest request, CancellationToken token) => Touch<OpaqueStoreListResult>();
        public Task<OpaqueStoreMetadataResult> ReadMetadataAsync(OpaqueStoreMetadataRequest request, CancellationToken token) => Touch<OpaqueStoreMetadataResult>();
        public Task<OpaqueStoreDownloadResult> DownloadAsync(OpaqueStoreDownloadRequest request, CancellationToken token) => Touch<OpaqueStoreDownloadResult>();
        public Task<OpaqueStoreUploadResult> UploadImmutableAsync(OpaqueStoreUploadRequest request, CancellationToken token) => Touch<OpaqueStoreUploadResult>();
        public Task<OpaqueStoreReadBackResult> ReadBackExactAsync(OpaqueStoreReadBackRequest request, CancellationToken token) => Touch<OpaqueStoreReadBackResult>();
        public Task<OpaqueStoreDeleteResult> DeleteExactAsync(OpaqueStoreDeleteRequest request, CancellationToken token) => Touch<OpaqueStoreDeleteResult>();
    }
}
