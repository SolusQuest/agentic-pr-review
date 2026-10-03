using System.Collections.Immutable;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.GitHubArtifacts;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;
using AgenticPrReview.Runtime.Tests.Host.State.Lineage;

namespace AgenticPrReview.Runtime.Tests.Host.State;

public sealed class OpaqueTransportCapacityTests
{
    [Fact]
    public void CarrierCoversActivatedSessionCapacityAndNestedCleanupAnchor()
    {
        Assert.Equal(34_049_552, OpaqueStoreLimits.MaximumObjectBytes);
        Assert.Equal(OpaqueStoreLimits.MaximumObjectBytes, ArtifactBridgeLimits.MaximumEncryptedObjectBytes);
        Assert.Equal(45_399_404, OpaqueStoreCapacity.MaximumBase64Bytes);
        Assert.Equal(45_399_694, ArtifactBridgeLimits.MaximumStagingFileBytes);
        Assert.Equal(45_413_722, ArtifactBridgeLimits.MaximumArchiveBytes);
        Assert.Equal(OpaqueStoreCapacity.TargetEnvelopeBytes + 2048, OpaqueStoreCapacity.AnchorBytes);
        Assert.Equal(OpaqueStoreCapacity.AnchorBytes + OpaqueStoreCapacity.ControlWrapperBytes,
            OpaqueStoreLimits.MaximumObjectBytes);
        Assert.Equal(32 * 1024 * 1024, AgentLimits.StateEnvelopeBytes);
        Assert.Equal(16 * 1024 * 1024, AgentLimits.SessionPlaintextBytes);
        Assert.Equal(33_832_960, AcceptedStateFormat.MaximumGenerationPayloadBytes);
        Assert.Equal(33_849_344, AcceptedStateFormat.MaximumPhysicalCopyPayloadBytes);
        Assert.Equal(64 * 1024, AcceptedStateFormat.MaximumAcceptancePayloadBytes);
        Assert.Equal(1024 * 1024, LineageFormat.MaximumPayloadBytes);
        Assert.Equal(34_033_036, LineageFormat.MaximumReaderPayloadBytes);
        Assert.Equal(34_049_552, LineageFormat.MaximumEnvelopeBytes);
        // Structural upper bounds, not newly configurable semantic policies.
        Assert.Equal(2_450_378_592L, (long)LineageFormat.MaximumScopedObjects * LineageFormat.MaximumReaderPayloadBytes);
        Assert.Equal(2_451_567_744L, (long)LineageFormat.MaximumScopedObjects * LineageFormat.MaximumEnvelopeBytes);
        Assert.False(RestrictedStateEnvelope.TryEncrypt(RestrictedStateTestData.Access(),
            RestrictedStateTestData.Binding(), new byte[AgentLimits.SessionPlaintextBytes + 1],
            new TestKeyResolver(), out _, out _));
    }

    [Fact]
    public void BinaryWriterWidthsAndRealAnchorFitTheDerivedWrappingAllowances()
    {
        var plaintext = new LineageBinaryWriter();
        plaintext.WriteBytes(new byte[LineageFormat.MaximumHeaderBytes]);
        plaintext.WriteBytes([1]);
        var outer = new LineageBinaryWriter();
        outer.WriteString(LineageFormat.EnvelopeMagic);
        outer.WriteUInt16(LineageFormat.Version);
        outer.WriteUInt16(LineageFormat.Aes256GcmAlgorithm);
        outer.WriteString(new string('a', 64));
        outer.WriteBytes(new byte[LineageFormat.NonceBytes]);
        outer.WriteBytes(plaintext.ToArray());
        outer.WriteBytes(new byte[LineageFormat.TagBytes]);
        Assert.Equal(OpaqueStoreCapacity.ControlWrapperBytes, outer.ToArray().Length - 1);

        var anchor = new RetainedStateOpaqueWriteAnchor(new('a', 64), new('b', 64),
            StateObjectClass.PublicationFailure, new('a', 64), new('c', 64),
            LineageTestData.LogicalExpiry, LineageTestData.SentinelExpiry, new('r', 256), 1,
            new OpaqueStoreName(new('n', 256)), new('d', 64), ImmutableArray.Create<byte>(1),
            OpaqueStoreHash.Sha256([1]), RetainedStateOpaqueWriteAnchorPhase.PreparedBeforeTargetDispatch,
            new('e', 64));
        Assert.True(RetainedStateOpaqueWriteAnchorCodec.TryEncode(anchor, out var encoded));
        Assert.InRange(encoded.Length - 1, 1, 2048);
        Assert.True(RetainedStateOpaqueWriteAnchorCodec.TryDecode(encoded, out _));
    }

    [Fact]
    public void ReturnedMetadataAndLineageEvidenceShareTheCarrierBoundary()
    {
        var metadata = Metadata(new OpaqueStoreName("opaque"), OpaqueStoreLimits.MaximumObjectBytes);
        Assert.True(new OpaqueStoreMetadataResult(OpaqueStoreFailure.None, metadata).Succeeded);
        Assert.False(new OpaqueStoreMetadataResult(OpaqueStoreFailure.None,
            metadata with { Size = metadata.Size + 1 }).Succeeded);
        var evidence = new LineageArtifactEvidence("opaque", "id", "run", 1,
            new('a', 64), new('b', 64), LineageTestData.SentinelExpiry, metadata.Size);
        Assert.True(LineageValidation.IsValid(evidence));
        Assert.False(LineageValidation.IsValid(evidence with { Size = evidence.Size + 1 }));
        Assert.False(OpaqueStoreValidation.IsValid(new OpaqueStoreDownloadRequest(metadata,
            AgentLimits.StateEnvelopeBytes)));
    }

    [Theory]
    [InlineData((int)StateObjectClass.Candidate)]
    [InlineData((int)StateObjectClass.Acceptance)]
    [InlineData((int)StateObjectClass.PublicationIntent)]
    public void ExistingClassPayloadLimitRoundTripsAndRejectsPlusOne(int classValue)
    {
        var objectClass = (StateObjectClass)classValue;
        using var lease = LineageTestData.Context();
        var name = new OpaqueStoreName("opaque");
        var maximum = LineageFormat.MaximumPayloadBytesForClass(objectClass);
        var payload = new byte[maximum];
        if (objectClass == StateObjectClass.PublicationIntent)
        {
            var prefix = new LineageBinaryWriter();
            prefix.WriteString("APR5RC01");
            prefix.WriteUInt16(1);
            prefix.WriteUInt16(5);
            prefix.ToArray().CopyTo(payload, 0);
        }
        var draft = Draft(objectClass);
        Assert.True(StateControlEnvelopeV1Codec.TryEncrypt(lease.Context, lease.Access, name,
            draft, payload, out var envelope, out _, out _));
        Assert.True(envelope.Length <= LineageFormat.MaximumEnvelopeBytesForClass(objectClass));
        Assert.True(StateControlEnvelopeV1Codec.TryDecrypt(lease.Context, lease.Access, name,
            envelope, out _, out var restored, out _));
        Assert.True(payload.AsSpan().SequenceEqual(restored));
        Assert.False(StateControlEnvelopeV1Codec.TryEncrypt(lease.Context, lease.Access, name,
            draft, new byte[maximum + 1], out _, out _, out _));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public async Task KnownClassMetadataIsPreflightedBeforeDownload(int extra, int expectedDownloads)
    {
        using var lease = LineageTestData.Context();
        var scope = LineageTestData.Scope();
        Assert.True(LineageBaseScopeCodec.TryEncode(scope, out var scopeBytes));
        Assert.True(LineageBaseScopeCodec.TryDigest(scope, out var scopeDigest));
        Assert.True(lease.Context.TryDeriveOpaqueName(lease.Access, "publication_intent",
            scopeBytes, out var name));
        var store = new MetadataOnlyStore(Metadata(name!,
            LineageFormat.MaximumEnvelopeBytesForClass(StateObjectClass.PublicationIntent) + extra));
        Assert.False((await new ScopedStateInventory(store).ReadAsync(lease.Context, lease.Access,
            scope, scopeDigest, CancellationToken.None)).Succeeded);
        Assert.Equal(expectedDownloads, store.Downloads);
    }

    [Fact]
    public void AuthenticatedWrongClassDoesNotAllocateAnOversizedPayloadCopy()
    {
        using var lease = LineageTestData.Context();
        var name = new OpaqueStoreName("opaque");
        var payload = new byte[LineageFormat.MaximumPayloadBytes + 1];
        Span<byte> key = stackalloc byte[32];
        Assert.True(lease.Context.TryCopyCurrentStateKey(lease.Access, key, out var keyId));
        Assert.True(StateControlHeaderV1Codec.TryCreate(Draft(StateObjectClass.Candidate), keyId,
            payload, out var header));
        Assert.True(StateControlHeaderV1Codec.TryEncode(header! with
            { ObjectClass = StateObjectClass.PublicationIntent }, out var headerBytes));
        var plainWriter = new LineageBinaryWriter();
        plainWriter.WriteBytes(headerBytes);
        plainWriter.WriteBytes(payload);
        var plaintext = plainWriter.ToArray();
        var nonce = new byte[12];
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        var aad = new LineageBinaryWriter();
        aad.WriteString(LineageFormat.EnvelopeAadPrefix);
        aad.WriteString(name.Value);
        aad.WriteString(keyId);
        aad.WriteUInt16(LineageFormat.Version);
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plaintext, ciphertext, tag, aad.ToArray());
        var writer = new LineageBinaryWriter();
        writer.WriteString(LineageFormat.EnvelopeMagic);
        writer.WriteUInt16(LineageFormat.Version);
        writer.WriteUInt16(LineageFormat.Aes256GcmAlgorithm);
        writer.WriteString(keyId);
        writer.WriteBytes(nonce);
        writer.WriteBytes(ciphertext);
        writer.WriteBytes(tag);
        var envelope = writer.ToArray();
        // Warm the crypto/parser route before measuring its owned managed buffers.
        Assert.False(StateControlEnvelopeV1Codec.TryDecrypt(lease.Context, lease.Access, name,
            envelope, out _, out _, out _));
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.False(StateControlEnvelopeV1Codec.TryDecrypt(lease.Context, lease.Access, name,
            envelope, out _, out var rejected, out _));
        Assert.Empty(rejected);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0,
            2L * envelope.Length + 64 * 1024); // ciphertext + authenticated plaintext, no payload copy
    }

    private static StateControlHeaderDraft Draft(StateObjectClass objectClass) => new(
        new('a', 64), new('b', 64), new('c', 64), objectClass, null, null, "run", 1,
        LineageTestData.Now, LineageTestData.LogicalExpiry, LineageTestData.SentinelExpiry);

    private static OpaqueStoreObjectMetadata Metadata(OpaqueStoreName name, long size) => new(
        new(name, new("id")), new("run", 1), new(new('a', 64)), new(new('b', 64)),
        LineageTestData.SentinelExpiry, size);

    private sealed class MetadataOnlyStore(OpaqueStoreObjectMetadata metadata) : IRestrictedStateStore
    {
        internal int Downloads { get; private set; }
        public Task<OpaqueStoreListResult> ListExactAsync(OpaqueStoreListRequest request, CancellationToken token) =>
            Task.FromResult(new OpaqueStoreListResult(OpaqueStoreFailure.None,
                request.Name == metadata.Reference.Name ? [metadata.Reference] : [], true));
        public Task<OpaqueStoreMetadataResult> ReadMetadataAsync(OpaqueStoreMetadataRequest request, CancellationToken token) =>
            Task.FromResult(new OpaqueStoreMetadataResult(OpaqueStoreFailure.None, metadata));
        public Task<OpaqueStoreDownloadResult> DownloadAsync(OpaqueStoreDownloadRequest request, CancellationToken token)
        {
            Downloads++;
            return Task.FromResult(OpaqueStoreDownloadResult.Fail(OpaqueStoreFailure.Invalid));
        }
        public Task<OpaqueStoreUploadResult> UploadImmutableAsync(OpaqueStoreUploadRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<OpaqueStoreReadBackResult> ReadBackExactAsync(OpaqueStoreReadBackRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<OpaqueStoreDeleteResult> DeleteExactAsync(OpaqueStoreDeleteRequest request, CancellationToken token) => throw new NotSupportedException();
    }
}
