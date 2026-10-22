using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Host.State.GitHubArtifacts;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Tests.Host.State.GitHubArtifacts;

public sealed class ArtifactTransportFormatTests
{
    [Fact]
    public async Task ReadsBothStoredAndDeflatedCarrierAndKeepsArchiveDigestSeparate()
    {
        foreach (var compression in new[] { CompressionLevel.NoCompression, CompressionLevel.Optimal })
        {
            var body = Envelope();
            var archive = Zip(body, compression: compression);
            var admitted = await ArtifactTransportFormat.DecodeAsync(archive, OpaqueStoreHash.Sha256(archive), 1024, CancellationToken.None);
            Assert.Equal(new byte[] { 0, 7, 255 }, admitted.Bytes);
            Assert.Equal("7001", admitted.RunId);
            Assert.Equal(2, admitted.Attempt);
            Assert.NotEqual(OpaqueStoreHash.Sha256(archive), admitted.Digest);
        }
    }

    [Theory]
    [InlineData("../artifact-envelope.json", 0)]
    [InlineData("nested/artifact-envelope.json", 0)]
    [InlineData("artifact-envelope.json", 0xa1ff)]
    [InlineData("artifact-envelope.json", 0x81c0)]
    [InlineData("artifact-envelope.json", 0x2180)]
    public async Task RejectsPathsSpecialAndExecutableEntries(string name, int mode)
    {
        var archive = Zip(Envelope(), name, mode);
        await Assert.ThrowsAnyAsync<IOException>(() => Decode(archive));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("zero-run")]
    [InlineData("unsafe-run")]
    [InlineData("leading-attempt")]
    [InlineData("leading-size")]
    [InlineData("digest")]
    [InlineData("base64")]
    [InlineData("base64-padding")]
    [InlineData("bom")]
    public async Task RejectsMalformedEnvelopeWithoutConversion(string fault)
    {
        var text = Encoding.UTF8.GetString(Envelope());
        text = fault switch
        {
            "duplicate" => text.Replace("\"producing_run_id\":\"7001\"", "\"producing_run_id\":\"7001\",\"producing_run_id\":\"7001\""),
            "unknown" => text[..^1] + ",\"unexpected\":\"x\"}",
            "zero-run" => text.Replace("\"7001\"", "\"0\""),
            "unsafe-run" => text.Replace("\"7001\"", "\"9007199254740992\""),
            "leading-attempt" => text.Replace("\"producing_run_attempt\":\"2\"", "\"producing_run_attempt\":\"02\""),
            "leading-size" => text.Replace("\"encrypted_object_size\":\"3\"", "\"encrypted_object_size\":\"03\""),
            "digest" => text.Replace(OpaqueStoreHash.Sha256(new byte[] { 0, 7, 255 }), new string('0', 64)),
            "base64" => text.Replace("AAf/", "AAf!"),
            "base64-padding" => text.Replace("\"encrypted_object_size\":\"3\"", "\"encrypted_object_size\":\"1\"").Replace("AAf/", "AB=="),
            "bom" => "\ufeff" + text,
            _ => throw new InvalidOperationException(),
        };
        await Assert.ThrowsAnyAsync<Exception>(() => Decode(Zip(Encoding.UTF8.GetBytes(text))));
    }

    [Theory]
    [InlineData("crc")]
    [InlineData("flags")]
    [InlineData("offset")]
    [InlineData("local-extra")]
    [InlineData("expanded")]
    [InlineData("duplicate-entry")]
    public async Task RejectsInconsistentZipBeforeAdmittingBytes(string fault)
    {
        var archive = Zip(Envelope(), duplicate: fault == "duplicate-entry");
        var central = (int)BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(archive.Length - 6));
        switch (fault)
        {
            case "crc": archive[14] ^= 1; break;
            case "flags": BinaryPrimitives.WriteUInt16LittleEndian(archive.AsSpan(central + 8), 1); break;
            case "offset": BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(central + 42), 1); break;
            case "local-extra": BinaryPrimitives.WriteUInt16LittleEndian(archive.AsSpan(28), 1); break;
            case "expanded": BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(central + 24), (uint)ArtifactTransportFormat.EnvelopeLimit(1024) + 1); break;
        }
        await Assert.ThrowsAnyAsync<IOException>(() => Decode(archive));
    }

    [Fact]
    public async Task RejectsWrongArchiveHashAndOperationSpecificExpansionLimit()
    {
        var archive = Zip(Envelope());
        var failure = await Assert.ThrowsAsync<ArtifactTransportException>(() =>
            ArtifactTransportFormat.DecodeAsync(archive, new string('0', 64), 1024, CancellationToken.None));
        Assert.Equal(OpaqueStoreFailure.DigestMismatch, failure.Failure);
        await Assert.ThrowsAnyAsync<IOException>(() =>
            ArtifactTransportFormat.DecodeAsync(archive, OpaqueStoreHash.Sha256(archive), 2, CancellationToken.None));
    }

    private static Task<ArtifactTransportEnvelope> Decode(byte[] archive) =>
        ArtifactTransportFormat.DecodeAsync(archive, OpaqueStoreHash.Sha256(archive), 1024, CancellationToken.None);
    private static byte[] Envelope() => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
    {
        ["discriminator"] = ArtifactTransportFormat.Discriminator, ["producing_run_id"] = "7001", ["producing_run_attempt"] = "2",
        ["encrypted_object_digest"] = OpaqueStoreHash.Sha256(new byte[] { 0, 7, 255 }), ["encrypted_object_size"] = "3", ["encrypted_object_base64"] = "AAf/",
    });
    private static byte[] Zip(byte[] body, string name = ArtifactTransportFormat.Entry, int mode = 0,
        CompressionLevel compression = CompressionLevel.NoCompression, bool duplicate = false)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            for (var i = 0; i < (duplicate ? 2 : 1); i++)
            {
                var entry = zip.CreateEntry(name, compression); entry.ExternalAttributes = mode << 16;
                using var stream = entry.Open(); stream.Write(body);
            }
        return output.ToArray();
    }
}
