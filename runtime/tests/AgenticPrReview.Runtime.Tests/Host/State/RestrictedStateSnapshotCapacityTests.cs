using System.Buffers;
using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.RestrictedStateTransactions;

namespace AgenticPrReview.Runtime.Tests.Host.State;

public sealed class RestrictedStateSnapshotCapacityTests
{
    private const int IndexMaximum = 256 * 1024 + 122;

    [Theory]
    [InlineData(IndexMaximum - 1, true)]
    [InlineData(IndexMaximum, true)]
    [InlineData(IndexMaximum + 1, false)]
    [InlineData(2 * 1024 * 1024, false)]
    [InlineData(OpaqueStoreLimits.MaximumObjectBytes, false)]
    public async Task IndexMetadataIsBoundedBeforeDownload(int size, bool downloads)
    {
        var fixture = new Fixture();
        fixture.Store.Index = fixture.Store.Index with { Size = size };
        var result = await fixture.ReadAsync();
        Assert.False(result.Succeeded);
        Assert.Equal(IndexMaximum, fixture.Store.ListMaximum);
        Assert.Equal(IndexMaximum, fixture.Store.MetadataMaximum);
        Assert.Equal(downloads ? 1 : 0, fixture.Store.Downloads.Count);
        if (downloads)
        {
            Assert.Equal(IndexMaximum, fixture.Store.Downloads[0].MaximumBytes);
        }
    }

    [Theory]
    [InlineData(false, AgentLimits.StateEnvelopeBytes - 1)]
    [InlineData(false, AgentLimits.StateEnvelopeBytes)]
    [InlineData(true, AgentLimits.StateEnvelopeBytes - 1)]
    [InlineData(true, AgentLimits.StateEnvelopeBytes)]
    public async Task AcceptedAndStagingBoundaryCandidatesStillRestore(bool staging, int size)
    {
        var fixture = new Fixture(staging, size);
        var result = await fixture.ReadAsync();
        Assert.True(result.Succeeded);
        Assert.Equal(2, fixture.Store.Downloads.Count);
        Assert.Equal(AgentLimits.StateEnvelopeBytes, fixture.Store.Downloads[1].MaximumBytes);
        var candidate = staging ? result.Snapshot!.Staging : Assert.Single(result.Snapshot!.Accepted);
        Assert.Equal(fixture.Store.CandidateBytes, candidate!.Envelope);
    }

    [Theory]
    [InlineData(false, AgentLimits.StateEnvelopeBytes + 1)]
    [InlineData(true, AgentLimits.StateEnvelopeBytes + 1)]
    [InlineData(false, OpaqueStoreLimits.MaximumObjectBytes)]
    [InlineData(true, OpaqueStoreLimits.MaximumObjectBytes)]
    public async Task AuthenticatedOversizedCandidateMetadataNeverDownloads(bool staging, int size)
    {
        var fixture = new Fixture(staging, advertisedCandidateSize: size);
        var candidate = staging ? fixture.Index.Staging! : fixture.Index.Accepted[0];
        var oversized = candidate with { Transport = candidate.Transport with { Size = size } };
        Assert.False(RestrictedStateTransactionIndexCodec.TryWrite(fixture.Index with
        {
            Accepted = staging ? [] : [oversized],
            Staging = staging ? oversized : null,
        }, out _));
        var result = await fixture.ReadAsync();
        Assert.False(result.Succeeded);
        Assert.Single(fixture.Store.Downloads);
        Assert.Equal(fixture.Store.Index.Reference, fixture.Store.Downloads[0].Expected.Reference);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OversizedReturnedBodyIsRejectedBeforeHashDecodeOrCopy(bool candidate, bool staging)
    {
        var fixture = new Fixture(staging);
        fixture.Store.OversizedReturnIsCandidate = candidate;
        var result = await fixture.ReadAsync();
        Assert.False(result.Succeeded);
        Assert.Equal(candidate ? 2 : 1, fixture.Store.Downloads.Count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void MaximumIndexEnvelopeHasExistingPlaintextAndFramingBounds(int delta)
    {
        var access = RestrictedStateTestData.Access();
        var keys = new TestKeyResolver(new string('k', 64));
        var plaintext = new byte[256 * 1024 + delta];
        Assert.True(RestrictedStateTransactionIndexEnvelope.TryEncrypt(
            access, plaintext, RestrictedStateTestData.Expires, keys,
            out var envelope, out _));
        Assert.Equal(IndexMaximum + delta, envelope!.Length);
        Assert.True(RestrictedStateTransactionIndexEnvelope.TryDecrypt(
            access, envelope, keys, out var restored, out _, out _));
        Assert.Equal(plaintext, restored);
        Assert.False(RestrictedStateTransactionIndexEnvelope.TryEncrypt(
            access, new byte[256 * 1024 + 1], RestrictedStateTestData.Expires,
            keys, out _, out _));
        var readCalls = keys.ReadCalls;
        Assert.False(RestrictedStateTransactionIndexEnvelope.TryDecrypt(
            access, new byte[IndexMaximum + 1], keys, out _, out _, out _));
        Assert.Equal(readCalls, keys.ReadCalls);
    }

    [Theory]
    [InlineData(IndexMaximum - 1)]
    [InlineData(IndexMaximum)]
    public void PredecessorIndexMetadataUsesIndexBoundInWriterAndReader(int size)
    {
        var fixture = new Fixture();
        var index = fixture.Index with
        {
            PredecessorVersion = new RestrictedStateSnapshotVersion(new string('1', 64), true),
            PredecessorIndex = fixture.Store.Index with { Size = size },
        };
        Assert.True(RestrictedStateTransactionIndexCodec.TryWrite(index, out var bytes));
        Assert.True(RestrictedStateTransactionIndexCodec.TryRead(bytes, out _));
        Assert.False(RestrictedStateTransactionIndexCodec.TryWrite(index with
        {
            PredecessorIndex = index.PredecessorIndex with { Size = IndexMaximum + 1 },
        }, out _));
        var malformed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes)
            .Replace("\"size\":" + size, "\"size\":" + (IndexMaximum + 1), StringComparison.Ordinal));
        Assert.False(RestrictedStateTransactionIndexCodec.TryRead(malformed, out _));
    }

    private sealed class Fixture
    {
        private readonly AuthorizedStateAccess access = RestrictedStateTestData.Access();
        private readonly TestKeyResolver keys = new();
        internal readonly ObservedStore Store;
        internal readonly RestrictedStateTransactionIndex Index;

        internal Fixture(bool staging = false, int candidateSize = 16, int? advertisedCandidateSize = null)
        {
            var bytes = new byte[candidateSize];
            var binding = RestrictedStateTestData.Binding();
            var sessionHash = new string('1', 64);
            var envelopeHash = RestrictedStateEnvelope.EnvelopeSha256(bytes);
            var candidate = new RestrictedStateCandidate(binding, sessionHash, envelopeHash,
                RestrictedStateEnvelope.ObjectIdentity(binding, sessionHash, envelopeHash), bytes);
            var snapshot = new RestrictedStateSnapshot(staging ? [] : [candidate], staging ? candidate : null);
            Assert.True(RestrictedStateSnapshotCodec.TryWrite(snapshot, out var snapshotBytes));
            var metadata = Metadata(Name(false), "candidate", bytes);
            var indexed = new RestrictedStateIndexedCandidate(binding, sessionHash,
                envelopeHash, candidate.ObjectIdentity, metadata);
            var index = new RestrictedStateTransactionIndex(
                new RestrictedStateSnapshotVersion(AgentCanonical.HashDomain("apr.state-snapshot.r2", snapshotBytes), true),
                new RestrictedStateSnapshotVersion(string.Empty, false), null, "operation",
                RestrictedStateTransactionCommitState.ReadyForSelection,
                staging ? [] : [indexed], staging ? indexed : null);
            Index = index;
            Assert.True(RestrictedStateTransactionIndexCodec.TryWrite(index, out var plaintext));
            if (advertisedCandidateSize is int size)
            {
                // Authenticate malformed persisted metadata without using the current writer's admission.
                plaintext = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(plaintext)
                    .Replace("\"size\":" + candidateSize, "\"size\":" + size, StringComparison.Ordinal));
            }
            Assert.True(RestrictedStateTransactionIndexEnvelope.TryEncrypt(access, plaintext,
                RestrictedStateTestData.Expires, keys, out var envelope, out _));
            Store = new ObservedStore(Metadata(Name(true), "index", envelope!), envelope!, metadata, bytes);
        }

        internal Task<RestrictedStateStoreRead> ReadAsync() =>
            new RestrictedStateOpaqueSnapshotStore(Store, keys).ReadAsync(access, CancellationToken.None);

        private OpaqueStoreName Name(bool index) => new(AgentCanonical.HashDomain(
            index ? "apr.state-r3-opaque-index-name.s1" : "apr.state-r3-opaque-candidate-name.s1",
            RestrictedStateSnapshotCodec.WriteScopeIdentity(access.Scope)));
    }

    private static OpaqueStoreObjectMetadata Metadata(OpaqueStoreName name, string id, byte[] bytes) =>
        new(new(name, new(id)), new("run", 1), new(new string('a', 64)),
            new(OpaqueStoreHash.Sha256(bytes)), RestrictedStateTestData.Expires, bytes.Length);

    private sealed class ObservedStore(
        OpaqueStoreObjectMetadata index, byte[] indexBytes,
        OpaqueStoreObjectMetadata candidate, byte[] candidateBytes) : IRestrictedStateStore
    {
        internal OpaqueStoreObjectMetadata Index = index;
        internal byte[] CandidateBytes => candidateBytes;
        internal readonly List<OpaqueStoreDownloadRequest> Downloads = [];
        internal bool? OversizedReturnIsCandidate;
        internal int ListMaximum;
        internal int MetadataMaximum;

        public Task<OpaqueStoreListResult> ListExactAsync(OpaqueStoreListRequest request, CancellationToken token)
        {
            ListMaximum = request.MaximumBytes;
            return Task.FromResult(new OpaqueStoreListResult(OpaqueStoreFailure.None, [Index.Reference], true));
        }
        public Task<OpaqueStoreMetadataResult> ReadMetadataAsync(OpaqueStoreMetadataRequest request, CancellationToken token)
        {
            MetadataMaximum = request.MaximumBytes;
            return Task.FromResult(new OpaqueStoreMetadataResult(OpaqueStoreFailure.None, Index));
        }
        public Task<OpaqueStoreDownloadResult> DownloadAsync(OpaqueStoreDownloadRequest request, CancellationToken token)
        {
            Downloads.Add(request);
            var isCandidate = request.Expected.Reference == candidate.Reference;
            if (OversizedReturnIsCandidate == isCandidate)
            {
                var size = (isCandidate ? AgentLimits.StateEnvelopeBytes : IndexMaximum) + 1;
                return Task.FromResult(new OpaqueStoreDownloadResult(OpaqueStoreFailure.None,
                    request.Expected with { Size = size }, new UnreadableMemory(size).Bytes));
            }
            return Task.FromResult(new OpaqueStoreDownloadResult(OpaqueStoreFailure.None,
                request.Expected, isCandidate ? candidateBytes : indexBytes));
        }
        public Task<OpaqueStoreUploadResult> UploadImmutableAsync(OpaqueStoreUploadRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<OpaqueStoreReadBackResult> ReadBackExactAsync(OpaqueStoreReadBackRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<OpaqueStoreDeleteResult> DeleteExactAsync(OpaqueStoreDeleteRequest request, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class UnreadableMemory(int length) : MemoryManager<byte>
    {
        internal Memory<byte> Bytes => CreateMemory(length);
        public override Span<byte> GetSpan() => throw new InvalidOperationException("Oversized body was accessed");
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}
