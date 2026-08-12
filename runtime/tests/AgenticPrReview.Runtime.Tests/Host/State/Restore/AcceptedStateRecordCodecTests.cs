using AgenticPrReview.Runtime.Host.State.Restore;

namespace AgenticPrReview.Runtime.Tests.Host.State.Restore;

public sealed class AcceptedStateRecordCodecTests
{
    [Fact]
    public void PublicationPayloadRoundTripsAndRejectsTrailingBytes()
    {
        var expected = AcceptedStateTestData.Publication(out var bytes);

        Assert.True(AcceptedStatePublicationPayloadCodec.TryDecode(
            bytes,
            out var decoded));
        Assert.NotNull(decoded);
        Assert.Equal(expected.RepositoryId, decoded.RepositoryId);
        Assert.Equal(expected.RepositoryName, decoded.RepositoryName);
        Assert.Equal(expected.ScopeSha256, decoded.ScopeSha256);
        Assert.Equal(expected.BodySha256, decoded.BodySha256);
        Assert.Equal(expected.ReviewedHeadSha, decoded.ReviewedHeadSha);
        Assert.True(expected.FinalizedCommentUtf8.AsSpan().SequenceEqual(
            decoded.FinalizedCommentUtf8.AsSpan()));

        Assert.False(AcceptedStatePublicationPayloadCodec.TryDecode(
            [.. bytes, 0],
            out _));
    }

    [Fact]
    public void GenerationRoundTripsRealEncryptedStateAndRejectsTampering()
    {
        var expected = AcceptedStateTestData.Generation(out var bytes);

        Assert.True(AcceptedStateGenerationRecordCodec.TryDecode(
            bytes,
            out var decoded));
        Assert.NotNull(decoded);
        Assert.Equal(expected.StateEnvelopeSha256, decoded.StateEnvelopeSha256);
        Assert.Equal(expected.SessionSha256, decoded.SessionSha256);
        Assert.Equal(expected.ProducerBaseSha, decoded.ProducerBaseSha);
        Assert.Equal(expected.ProducerHeadSha, decoded.ProducerHeadSha);
        Assert.True(expected.EncryptedStateEnvelope.AsSpan().SequenceEqual(
            decoded.EncryptedStateEnvelope.AsSpan()));

        var tampered = bytes.ToArray();
        const int generationEnvelopeOffset = 4 + 8 + 2 + 4;
        tampered[generationEnvelopeOffset + 20] ^= 0x01;
        Assert.False(AcceptedStateGenerationRecordCodec.TryDecode(
            tampered,
            out _));
        Assert.False(AcceptedStateGenerationRecordCodec.TryDecode(
            [.. bytes, 0],
            out _));
    }

    [Fact]
    public void AcceptanceReceiptRoundTripsAndRejectsTrailingBytes()
    {
        var expected = AcceptedStateTestData.Receipt(
            new string('c', 64),
            AcceptedStateTestData.OriginalCandidateIdentity,
            out var bytes);

        Assert.True(AcceptedStateAcceptanceReceiptCodec.TryDecode(
            bytes,
            out var decoded));
        Assert.Equal(expected, decoded);
        Assert.False(AcceptedStateAcceptanceReceiptCodec.TryDecode(
            [.. bytes, 0],
            out _));
    }

    [Fact]
    public void PhysicalCopyRoundTripsCanonicalGenerationAndRejectsTrailingBytes()
    {
        var expected = AcceptedStateTestData.PhysicalCopy(out var bytes);

        Assert.True(AcceptedStatePhysicalCopyCodec.TryDecode(
            bytes,
            out var decoded));
        Assert.NotNull(decoded);
        Assert.Equal(
            expected.LogicalGenerationIdentity,
            decoded.LogicalGenerationIdentity);
        Assert.Equal(
            expected.OriginalCandidateObjectIdentity,
            decoded.OriginalCandidateObjectIdentity);
        Assert.Equal(expected.SourceArtifactId, decoded.SourceArtifactId);
        Assert.Equal(
            expected.SourceArchiveSha256,
            decoded.SourceArchiveSha256);
        Assert.True(expected.CanonicalGenerationBytes.AsSpan().SequenceEqual(
            decoded.CanonicalGenerationBytes.AsSpan()));
        Assert.False(AcceptedStatePhysicalCopyCodec.TryDecode(
            [.. bytes, 0],
            out _));
    }

    [Fact]
    public void PhysicalCopySourceArtifactIdUsesExactJavaScriptSafeBoundary()
    {
        var copy = AcceptedStateTestData.PhysicalCopy(out _);
        var maximum = copy with
        {
            SourceArtifactId = "9007199254740991",
        };

        Assert.True(AcceptedStatePhysicalCopyCodec.TryEncode(
            maximum,
            out var encoded));
        Assert.True(AcceptedStatePhysicalCopyCodec.TryDecode(
            encoded,
            out var decoded));
        Assert.Equal(maximum.SourceArtifactId, decoded!.SourceArtifactId);

        foreach (var invalid in new[]
        {
            "9007199254740992",
            "9223372036854775808",
            "18446744073709551616",
        })
        {
            Assert.False(AcceptedStatePhysicalCopyCodec.TryEncode(
                copy with { SourceArtifactId = invalid },
                out _));
        }
    }

    [Fact]
    public void EveryCodecRejectsWrongMagicAndVersion()
    {
        _ = AcceptedStateTestData.Publication(out var publication);
        _ = AcceptedStateTestData.Generation(out var generation);
        _ = AcceptedStateTestData.Receipt(
            new string('c', 64),
            AcceptedStateTestData.OriginalCandidateIdentity,
            out var receipt);
        _ = AcceptedStateTestData.PhysicalCopy(out var copy);

        AssertClosed<ValidatedPublicationPayloadV1>(publication,
            AcceptedStatePublicationPayloadCodec.TryDecode);
        AssertClosed<StateGenerationRecordV1>(generation,
            AcceptedStateGenerationRecordCodec.TryDecode);
        AssertClosed<AcceptanceReceiptV1>(receipt,
            AcceptedStateAcceptanceReceiptCodec.TryDecode);
        AssertClosed<AcceptedStatePhysicalCopyV1>(copy,
            AcceptedStatePhysicalCopyCodec.TryDecode);
    }

    private static void AssertClosed<T>(
        byte[] bytes,
        TryDecode<T> decode)
        where T : class
    {
        var wrongMagic = bytes.ToArray();
        wrongMagic[4] ^= 0x20;
        Assert.False(decode(wrongMagic, out _));

        var wrongVersion = bytes.ToArray();
        var magicBytes = BitConverter.ToInt32(wrongVersion, 0);
        var versionOffset = 4 + magicBytes;
        wrongVersion[versionOffset] ^= 0x01;
        Assert.False(decode(wrongVersion, out _));
    }

    private delegate bool TryDecode<T>(
        ReadOnlySpan<byte> bytes,
        out T? value)
        where T : class;
}
