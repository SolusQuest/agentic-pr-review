using System.Buffers.Binary;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime.Tests.Host.State;

public sealed class Cr1SolusStateEnvelopeTests
{
    // Independently authored Node Buffer/RFC4122 oracle, not C# round-trip output.
    private const string Golden = "QVBSU09MMDEBABAAMC4xLjAtaW50ZXJuYWwuMRcAY3IxLXNvbHVzLWludGVybmFsMS1kZXYBP1IPAjtZQx6Kz6YlRPKHYAEAAAABAAAAlUtRg7QWD4M4J8QQEU4QMaUZTf7qMeIbKgXnJm4gmO4RERERERERERERERERERERERERERERERERERERERERESIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiABEiM0RVZneImaq7zN3u/xAhMkNUZXaHmKm6y9zt/g//7t3Mu6qZiHdmVUQzIhEAVGNygZCrze8BI0VniavN7wgAAAAA/4DAQQp7fQ==";
    private const string GoldenHash = "4246a4165adb62db2cbc0e2c9c35d58191a40fa536d6e17113dda1d30701788b";
    private static readonly byte[] Payload = [0, 255, 128, 192, 65, 10, 123, 125];

    private static SolusRestrictedStateCompatibility Compatibility() => new(
        SolusRestrictedStateFormat.PackageVersion, SolusRestrictedStateFormat.Profile, 1,
        SolusRestrictedStateFormat.Implementation, 1, 1, SolusRestrictedStateFormat.PackageClosureSha256,
        new string('1', 64), new string('2', 64));

    private static SolusRestrictedStateCheckpoint Checkpoint() => new(
        new("00112233-4455-6677-8899-aabbccddeeff"), new("10213243-5465-7687-98a9-bacbdcedfe0f"),
        new("ffeeddcc-bbaa-9988-7766-554433221100"), new("54637281-90ab-cdef-0123-456789abcdef"));

    private static SolusRestrictedStateRecord Record(byte[]? payload = null)
    {
        Assert.True(SolusRestrictedStateRecord.TryCreate(Compatibility(), Checkpoint(), payload ?? Payload, out var value));
        return value!;
    }

    private static AcceptedLineage Lineage(RestrictedStateBinding binding, byte[] envelope, string hash) => new(
        binding.Scope, binding.Generation, hash, RestrictedStateEnvelope.EnvelopeSha256(envelope),
        binding.PredecessorEnvelopeSha256, binding.AcceptedAtUnixSeconds, binding.ExpiresAtUnixSeconds, true);

    private static (byte[] Envelope, AcceptedLineage Lineage) Seal(
        AuthorizedStateAccess access, RestrictedStateBinding binding, TestKeyResolver keys, byte[]? payload = null)
    {
        Assert.True(SolusRestrictedStateSnapshotCodec.TrySeal(access, binding, Record(payload), keys,
            out var envelope, out var hash, out var failure), failure.ToString());
        return (envelope!, Lineage(binding, envelope!, hash!));
    }

    [Fact]
    public void IndependentGoldenPinsEveryGuidFieldLengthAndDigest()
    {
        var literal = Convert.FromBase64String(Golden);
        Assert.Equal(250, literal.Length);
        Assert.Equal(242, SolusRestrictedStateSnapshotCodec.FramingBytes);
        Assert.True(SolusRestrictedStateSnapshotCodec.TryWrite(Record(), out var encoded));
        Assert.Equal(literal, encoded);
        Assert.Equal(GoldenHash, SolusRestrictedStateSnapshotCodec.RecordSha256(literal));
        Assert.True(SolusRestrictedStateSnapshotCodec.TryRead(literal, Compatibility(), out var read, out _));
        Assert.Equal(Checkpoint(), read!.Checkpoint);
        Assert.Equal(Payload, read.CopyRestrictedPayload());
        Assert.Equal(Convert.FromHexString("00112233445566778899aabbccddeeff"), literal.AsSpan(174, 16).ToArray());
    }

    [Fact]
    public void FreshReaderUsesOnlyTrustedLineageAndRecordAsCheckpointSource()
    {
        var access = RestrictedStateTestData.Access();
        var binding = RestrictedStateTestData.Binding();
        var sealedValue = Seal(access, binding, new());
        Assert.True(SolusRestrictedStateSnapshotCodec.TryRestore(access, binding, sealedValue.Lineage,
            sealedValue.Envelope, Compatibility(), RestrictedStateTestData.Now, new TestKeyResolver(),
            out var read, out var failure), failure.ToString());
        Assert.Equal(Checkpoint(), read!.Checkpoint);
        Assert.NotEqual(read.Checkpoint, read.Checkpoint with { CheckpointId = Guid.NewGuid() });
        Assert.Equal(Payload, read.CopyRestrictedPayload());
    }

    [Fact]
    public void PayloadCopiesAndDiagnosticsPreserveRestriction()
    {
        var bytes = Payload.ToArray();
        var record = Record(bytes);
        bytes.AsSpan().Fill(42);
        var copy = record.CopyRestrictedPayload();
        Assert.Equal(Payload, copy);
        copy.AsSpan().Fill(43);
        Assert.Equal(Payload, record.CopyRestrictedPayload());
        Assert.Equal(nameof(SolusRestrictedStateRecord), record.ToString());
        Assert.Equal(nameof(SolusRestrictedStateCompatibility), record.Compatibility.ToString());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    [InlineData(8)] [InlineData(9)]
    public void UnsupportedCurrentExpectationNeverReachesKeyResolution(int field)
    {
        var access = RestrictedStateTestData.Access(); var binding = RestrictedStateTestData.Binding();
        var value = Seal(access, binding, new()); var keys = new TestKeyResolver(); var expected = Compatibility();
        expected = field switch
        {
            0 => expected with { PackageVersion = "0.1.0-internal.2" },
            1 => expected with { Profile = "other" },
            2 => expected with { Mode = 2 },
            3 => expected with { ImplementationId = Guid.NewGuid() },
            4 => expected with { FormatVersion = 2 },
            5 => expected with { CompatibilityVersion = 2 },
            6 => expected with { PackageClosureSha256 = new string('a', 64) },
            7 => expected with { RuntimeConfigurationSha256 = "" },
            8 => expected with { ProviderConfigurationSha256 = new string('z', 64) },
            _ => expected with { PackageVersion = new string('x', 1_000_000) },
        };
        Assert.False(SolusRestrictedStateSnapshotCodec.TryRestore(access, binding, value.Lineage, value.Envelope,
            expected, RestrictedStateTestData.Now, keys, out var read, out var failure));
        Assert.Null(read); Assert.Equal(SolusRestrictedStateFailure.Incompatible, failure); Assert.Equal(0, keys.ReadCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(12)] [InlineData(29)] [InlineData(53)]
    [InlineData(54)] [InlineData(70)] [InlineData(74)] [InlineData(78)]
    [InlineData(110)] [InlineData(142)]
    public void CryptographicallyValidMisleadingMetadataIsRejected(int offset)
    {
        var bytes = Convert.FromBase64String(Golden); bytes[offset] ^= 1;
        var access = RestrictedStateTestData.Access(); var binding = RestrictedStateTestData.Binding(); var keys = new TestKeyResolver();
        Assert.True(RestrictedStateEnvelope.TryEncrypt(access, binding, bytes, keys, out var envelope, out _));
        var lineage = Lineage(binding, envelope!, SolusRestrictedStateSnapshotCodec.RecordSha256(bytes));
        Assert.False(SolusRestrictedStateSnapshotCodec.TryRestore(access, binding, lineage, envelope, Compatibility(),
            RestrictedStateTestData.Now, keys, out var read, out var failure));
        Assert.Null(read); Assert.Equal(SolusRestrictedStateFailure.Incompatible, failure);
    }

    [Theory]
    [InlineData(174)] [InlineData(190)] [InlineData(206)] [InlineData(222)]
    public void EmptyCheckpointGuidNeverAdmitsPayload(int offset)
    {
        var bytes = Convert.FromBase64String(Golden); bytes.AsSpan(offset, 16).Clear();
        Assert.False(SolusRestrictedStateSnapshotCodec.TryRead(bytes, Compatibility(), out var read, out _));
        Assert.Null(read);
    }

    [Fact]
    public void MixedEndianImplementationIsNotRewritten()
    {
        var bytes = Convert.FromBase64String(Golden);
        SolusRestrictedStateFormat.Implementation.ToByteArray().CopyTo(bytes, 54);
        Assert.False(SolusRestrictedStateSnapshotCodec.TryRead(bytes, Compatibility(), out _, out var failure));
        Assert.Equal(SolusRestrictedStateFailure.Incompatible, failure);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void InconsistentIndependentLineageIsRejectedBeforeKeys(int field)
    {
        var access = RestrictedStateTestData.Access(); var binding = RestrictedStateTestData.Binding();
        var value = Seal(access, binding, new()); var keys = new TestKeyResolver();
        var lineage = field switch
        {
            0 => value.Lineage with { TransitionAuthorized = false },
            1 => value.Lineage with { Generation = 1, ExpectedPredecessorEnvelopeSha256 = new string('a', 64) },
            2 => value.Lineage with { EnvelopeSha256 = new string('a', 64) },
            3 => value.Lineage with { AcceptedAtUnixSeconds = RestrictedStateTestData.Now + 1 },
            4 => value.Lineage with { Scope = binding.Scope with { ProviderId = "wrong" } },
            _ => value.Lineage with { ExpiresAtUnixSeconds = binding.ExpiresAtUnixSeconds - 1 },
        };
        Assert.False(SolusRestrictedStateSnapshotCodec.TryRestore(access, binding, lineage, value.Envelope, Compatibility(),
            RestrictedStateTestData.Now, keys, out var read, out var failure));
        Assert.Null(read); Assert.Equal(SolusRestrictedStateFailure.LineageMismatch, failure); Assert.Equal(0, keys.ReadCalls);
    }

    [Fact]
    public void ValidEnvelopeCannotRepairWrongIndependentRecordDigestOrCandidateHashes()
    {
        var access = RestrictedStateTestData.Access(); var binding = RestrictedStateTestData.Binding();
        var value = Seal(access, binding, new()); var wrong = value.Lineage with { SessionSha256 = new string('a', 64) };
        Assert.False(SolusRestrictedStateSnapshotCodec.TryRestore(access, binding, wrong, value.Envelope, Compatibility(),
            RestrictedStateTestData.Now, new TestKeyResolver(), out _, out var failure));
        Assert.Equal(SolusRestrictedStateFailure.LineageMismatch, failure);
        var candidate = new RestrictedStateCandidate(binding, wrong.SessionSha256, wrong.EnvelopeSha256,
            RestrictedStateEnvelope.ObjectIdentity(binding, wrong.SessionSha256, wrong.EnvelopeSha256), value.Envelope);
        Assert.False(SolusRestrictedStateSnapshotCodec.TryRestoreCandidate(access, binding, wrong, candidate,
            Compatibility(), RestrictedStateTestData.Now, new TestKeyResolver(), out _, out failure));
        Assert.Equal(SolusRestrictedStateFailure.LineageMismatch, failure);
        var valid = candidate with { SessionSha256 = value.Lineage.SessionSha256,
            ObjectIdentity = RestrictedStateEnvelope.ObjectIdentity(binding, value.Lineage.SessionSha256, wrong.EnvelopeSha256) };
        Assert.True(SolusRestrictedStateSnapshotCodec.TryRestoreCandidate(access, binding, value.Lineage, valid,
            Compatibility(), RestrictedStateTestData.Now, new TestKeyResolver(), out _, out _));
        Assert.False(SolusRestrictedStateSnapshotCodec.TryRestoreCandidate(access, binding, value.Lineage,
            valid with { ObjectIdentity = new string('a', 64) }, Compatibility(), RestrictedStateTestData.Now, new TestKeyResolver(), out _, out _));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    public void WrongScopeIsDeniedBeforeKeys(int field)
    {
        var binding = RestrictedStateTestData.Binding(); var access = RestrictedStateTestData.Access();
        var value = Seal(access, binding, new()); var scope = binding.Scope;
        scope = field switch
        {
            0 => scope with { RepositoryId = "wrong" }, 1 => scope with { WorkflowIdentity = "wrong" },
            2 => scope with { ReviewTarget = 2 }, 3 => scope with { SessionId = "other" },
            4 => scope with { ProviderId = "wrong" }, 5 => scope with { ModelId = "wrong" },
            6 => scope with { AdapterId = "wrong" }, 7 => scope with { PolicySha256 = new string('a', 64) },
            8 => scope with { LimitsSha256 = new string('a', 64) }, 9 => scope with { ToolsetSha256 = new string('a', 64) },
            _ => scope with { BuildId = "wrong" },
        };
        var keys = new TestKeyResolver();
        Assert.False(SolusRestrictedStateSnapshotCodec.TryRestore(RestrictedStateTestData.Access(scope), binding,
            value.Lineage, value.Envelope, Compatibility(), RestrictedStateTestData.Now, keys, out _, out var failure));
        Assert.Equal(SolusRestrictedStateFailure.AccessDenied, failure); Assert.Equal(0, keys.ReadCalls);
    }

    [Fact]
    public void ProvenanceTamperAndAuthenticatedOlderGenerationNeverFallBack()
    {
        var access = RestrictedStateTestData.Access(); var binding = RestrictedStateTestData.Binding();
        var old = Seal(access, binding, new());
        var nextBinding = RestrictedStateTestData.Binding(1, old.Lineage.EnvelopeSha256);
        var current = Seal(access, nextBinding, new()); var keys = new TestKeyResolver();
        Assert.False(SolusRestrictedStateSnapshotCodec.TryRestore(access, nextBinding, current.Lineage, old.Envelope,
            Compatibility(), RestrictedStateTestData.Now, keys, out _, out var failure));
        Assert.Equal(SolusRestrictedStateFailure.LineageMismatch, failure); Assert.Equal(0, keys.ReadCalls);
        var altered = binding with { ProducerHeadSha = new string('a', 40) };
        Assert.False(SolusRestrictedStateSnapshotCodec.TryRestore(access, altered, old.Lineage, old.Envelope,
            Compatibility(), RestrictedStateTestData.Now, keys, out _, out failure));
        Assert.Equal(SolusRestrictedStateFailure.AuthenticationFailed, failure);
    }

    [Theory]
    [InlineData(20)] [InlineData(71)] [InlineData(87)] [InlineData(-1)]
    public void HeaderAndTagTamperCannotBeRepairedByUnkeyedDigest(int offset)
    {
        var access = RestrictedStateTestData.Access(); var binding = RestrictedStateTestData.Binding(); var value = Seal(access, binding, new());
        var bytes = value.Envelope.ToArray(); bytes[offset < 0 ? bytes.Length - 1 : offset] ^= 1;
        var fake = value.Lineage with { EnvelopeSha256 = RestrictedStateEnvelope.EnvelopeSha256(bytes) };
        Assert.False(SolusRestrictedStateSnapshotCodec.TryRestore(access, binding, fake, bytes, Compatibility(),
            RestrictedStateTestData.Now, new TestKeyResolver(), out var read, out _)); Assert.Null(read);
    }

    [Fact]
    public void ExactExpiryInvalidClockAndNativeSelectedCurrentHaveNoBootstrap()
    {
        var access = RestrictedStateTestData.Access(); var binding = RestrictedStateTestData.Binding(); var value = Seal(access, binding, new());
        Assert.False(SolusRestrictedStateSnapshotCodec.TryRestore(access, binding, value.Lineage, value.Envelope,
            Compatibility(), binding.ExpiresAtUnixSeconds, new TestKeyResolver(), out _, out var failure));
        Assert.Equal(SolusRestrictedStateFailure.Expired, failure);
        Assert.False(SolusRestrictedStateSnapshotCodec.TryRestore(access, binding, value.Lineage, value.Envelope,
            Compatibility(), -1, new TestKeyResolver(), out _, out _));
        var nativeSession = new byte[300]; "SESSION"u8.CopyTo(nativeSession);
        Assert.True(RestrictedStateEnvelope.TryEncrypt(access, binding, nativeSession, new TestKeyResolver(), out var envelope, out _));
        Assert.False(SolusRestrictedStateSnapshotCodec.TryRestore(access, binding,
            Lineage(binding, envelope!, SolusRestrictedStateSnapshotCodec.RecordSha256(nativeSession)), envelope, Compatibility(),
            RestrictedStateTestData.Now, new TestKeyResolver(), out _, out failure));
        Assert.Equal(SolusRestrictedStateFailure.Incompatible, failure);
    }

    [Fact]
    public void ExactMaximumAndMalformedLengthsAreBoundedBeforePayloadAllocation()
    {
        var payload = new byte[SolusRestrictedStateFormat.MaximumPayloadBytes];
        var access = RestrictedStateTestData.Access(); var binding = RestrictedStateTestData.Binding();
        var value = Seal(access, binding, new TestKeyResolver(new string('k', 64)), payload);
        Assert.Equal(SolusRestrictedStateSnapshotCodec.MaximumEnvelopeBytes, value.Envelope.Length);
        Assert.True(SolusRestrictedStateSnapshotCodec.TryRestore(access, binding, value.Lineage, value.Envelope,
            Compatibility(), RestrictedStateTestData.Now, new TestKeyResolver(new string('k', 64)), out var read, out _));
        Assert.Equal(payload, read!.CopyRestrictedPayload());
        Assert.False(SolusRestrictedStateRecord.TryCreate(Compatibility(), Checkpoint(), new byte[payload.Length + 1], out _));
        Assert.False(SolusRestrictedStateRecord.TryCreate(Compatibility(), Checkpoint(), [], out _));
        var bytes = Convert.FromBase64String(Golden); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(238), uint.MaxValue);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.False(SolusRestrictedStateSnapshotCodec.TryRead(bytes, Compatibility(), out _, out _));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64 * 1024);
        Assert.False(SolusRestrictedStateSnapshotCodec.TryRead(Convert.FromBase64String(Golden).AsSpan(0, 249), Compatibility(), out _, out _));
        Assert.False(SolusRestrictedStateSnapshotCodec.TryRead([.. Convert.FromBase64String(Golden), 0], Compatibility(), out _, out _));
        var keys = new TestKeyResolver();
        Assert.False(SolusRestrictedStateSnapshotCodec.TryRestore(access, binding, value.Lineage,
            new byte[SolusRestrictedStateSnapshotCodec.MaximumEnvelopeBytes + 1], Compatibility(), RestrictedStateTestData.Now,
            keys, out _, out var failure));
        Assert.Equal(SolusRestrictedStateFailure.Oversized, failure); Assert.Equal(0, keys.ReadCalls);
        var encrypted = Seal(access, binding, new());
        BinaryPrimitives.WriteUInt32LittleEndian(encrypted.Envelope.AsSpan(83), uint.MaxValue);
        var claimed = encrypted.Lineage with { EnvelopeSha256 = RestrictedStateEnvelope.EnvelopeSha256(encrypted.Envelope) };
        Assert.False(SolusRestrictedStateSnapshotCodec.TryRestore(access, binding, claimed, encrypted.Envelope,
            Compatibility(), RestrictedStateTestData.Now, keys, out _, out failure));
        Assert.Equal(SolusRestrictedStateFailure.Oversized, failure); Assert.Equal(0, keys.ReadCalls);
    }

    [Fact]
    public void NullAndKeyFailurePathsExposeOnlyClosedFailures()
    {
        Assert.False(SolusRestrictedStateSnapshotCodec.TryWrite(null, out _));
        Assert.False(SolusRestrictedStateSnapshotCodec.TryRead([], null, out _, out _));
        Assert.False(SolusRestrictedStateSnapshotCodec.TryRestore(null, null, null, [], null, 0, null, out _, out _));
        Assert.False(SolusRestrictedStateSnapshotCodec.TrySeal(null, null, null, null, out _, out _, out _));
        var access = RestrictedStateTestData.Access(); var binding = RestrictedStateTestData.Binding();
        var value = Seal(access, binding, new());
        Assert.False(SolusRestrictedStateSnapshotCodec.TryRestore(access, binding, value.Lineage, value.Envelope,
            Compatibility(), RestrictedStateTestData.Now, new TestKeyResolver { ThrowOnRead = true }, out var record, out var failure));
        Assert.Null(record); Assert.Equal(SolusRestrictedStateFailure.KeyUnavailable, failure);
    }
}
