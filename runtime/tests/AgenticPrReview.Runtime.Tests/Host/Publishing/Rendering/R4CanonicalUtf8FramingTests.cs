using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Publishing.Rendering;

public sealed class R4CanonicalUtf8FramingTests
{
    private const string FindingPreimageHex =
        "6167656e7469632d70722d7265766965772f72342f66696e64696e672f7631" +
        "000000000000000468696768" +
        "00000000000000055469746c65" +
        "00000000000000074d657373616765" +
        "0000000000000040" +
        "6161616161616161616161616161616161616161616161616161616161616161" +
        "6161616161616161616161616161616161616161616161616161616161616161" +
        "000000000000000a7372632f6170702e7473" +
        "000000000000000137" +
        "000000000000000139";

    [Fact]
    public void FindingPreimageMatchesIndependentLiteralBytesAndDigest()
    {
        var fields = new[]
        {
            "high",
            "Title",
            "Message",
            new string('a', 64),
            "src/app.ts",
            "7",
            "9",
        };

        var preimage = R4CanonicalUtf8Framing.BuildPreimage(
            R4PublicationIdentityV1.FindingDomain,
            fields);

        Assert.Equal(Convert.FromHexString(FindingPreimageHex), preimage);
        Assert.Equal(
            "43390faa4068833717f38a47230155790bed811f1e00a3d3caeef6d8b682e1fc",
            R4CanonicalUtf8Framing.Hash(
                R4PublicationIdentityV1.FindingDomain,
                fields));
    }

    [Fact]
    public void Uint64LengthsPreventConcatenationAmbiguity()
    {
        var first = R4CanonicalUtf8Framing.BuildPreimage(
            "example/v1",
            ["ab", "c"]);
        var second = R4CanonicalUtf8Framing.BuildPreimage(
            "example/v1",
            ["a", "bc"]);

        Assert.NotEqual(first, second);
        Assert.Equal(0UL, System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(
            first.AsSpan("example/v1".Length, 8)) >> 32);
        Assert.Equal(2UL, System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(
            first.AsSpan("example/v1".Length, 8)));
    }

    [Fact]
    public void InvalidUnicodeAndNonAsciiDomainsFailClosed()
    {
        var invalidUnicode = new string(['\ud800']);

        var unicode = Assert.Throws<R4PublicationException>(() =>
            R4CanonicalUtf8Framing.BuildPreimage("example/v1", [invalidUnicode]));
        var domain = Assert.Throws<R4PublicationException>(() =>
            R4CanonicalUtf8Framing.BuildPreimage("exämple/v1", ["value"]));

        Assert.Equal(R4PublicationFailureCodes.IdentityInvalid, unicode.Code);
        Assert.Equal(R4PublicationFailureCodes.IdentityInvalid, domain.Code);
    }
}
