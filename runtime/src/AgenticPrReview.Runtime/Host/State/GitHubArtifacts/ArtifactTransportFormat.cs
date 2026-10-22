using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.GitHubArtifacts;

internal sealed class ArtifactTransportException(OpaqueStoreFailure failure)
    : IOException("artifact_transport_refused")
{
    internal OpaqueStoreFailure Failure { get; } = failure;
}

internal sealed record ArtifactTransportEnvelope(
    string RunId, long Attempt, string Digest, byte[] Bytes);

// The persisted S2 carrier is unchanged. Parse the ZIP structure before opening
// a decompressor; ZipArchive alone does not enforce this deliberately narrow shape.
internal static class ArtifactTransportFormat
{
    internal const string Entry = "artifact-envelope.json";
    internal const string Discriminator = "apr.private-artifact-envelope.s2";
    private const long MaximumSafeInteger = 9_007_199_254_740_991;
    private static readonly uint[] CrcTable = CreateCrcTable();

    internal static bool PositiveDecimal(string? text, out long value) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
        value is > 0 and <= MaximumSafeInteger &&
        text == value.ToString(CultureInfo.InvariantCulture);

    internal static bool Digest(string? text) => text is { Length: 64 } &&
        text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static string Family(string name) => "apr-object-" +
        OpaqueStoreHash.Sha256(Encoding.UTF8.GetBytes("apr-artifact-family\0" + name)) + "-";

    internal static string PhysicalName(string name, string digest) => Family(name) + digest;

    internal static bool Member(string name, string? physical) => physical is not null &&
        physical.StartsWith(Family(name), StringComparison.Ordinal) &&
        Digest(physical[Family(name).Length..]);

    internal static int EnvelopeLimit(int maximumBytes) => checked(4 * ((maximumBytes + 2) / 3) + 290);

    internal static int ArchiveLimit(int maximumBytes)
    {
        var size = EnvelopeLimit(maximumBytes);
        return checked(size + (size + 4095) / 4096 + (size + 16383) / 16384 +
            (size + 33554431) / 33554432 + 13 + 158);
    }

    internal static JsonDocument ParseJson(ReadOnlyMemory<byte> bytes)
    {
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        try { Unique(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                Require(names.Add(property.Name));
                Unique(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) Unique(item);
    }

    internal static async Task<ArtifactTransportEnvelope> DecodeAsync(
        byte[] archive, string archiveDigest, int maximumBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Require(maximumBytes is > 0 and <= OpaqueStoreLimits.MaximumObjectBytes);
        Require(archive.Length is >= 22 && archive.Length <= ArchiveLimit(maximumBytes));
        if (OpaqueStoreHash.Sha256(archive) != archiveDigest)
            throw new ArtifactTransportException(OpaqueStoreFailure.DigestMismatch);
        var end = archive.Length - 22;
        Require(U32(archive, end) == 0x06054b50 && U16(archive, end + 4) == 0 &&
            U16(archive, end + 6) == 0 && U16(archive, end + 8) == 1 &&
            U16(archive, end + 10) == 1 && U16(archive, end + 20) == 0);
        var central = checked((int)U32(archive, end + 16));
        Require(central >= 30 && (long)central + U32(archive, end + 12) == end &&
            central + 46L <= end && U32(archive, central) == 0x02014b50);
        var flags = U16(archive, central + 8);
        var method = U16(archive, central + 10);
        var crc = U32(archive, central + 16);
        var compressed = checked((int)U32(archive, central + 20));
        var expanded = checked((int)U32(archive, central + 24));
        var nameLength = U16(archive, central + 28);
        var mode = U32(archive, central + 38) >> 16;
        Require(flags is 0 or 8 or 0x800 or 0x808 && method is 0 or 8 &&
            expanded is > 0 && expanded <= EnvelopeLimit(maximumBytes) &&
            compressed is > 0 && compressed <= ArchiveLimit(maximumBytes) &&
            (method != 0 || compressed == expanded) &&
            central + 46L + nameLength == end && U16(archive, central + 30) == 0 &&
            U16(archive, central + 32) == 0 && U16(archive, central + 34) == 0 &&
            U32(archive, central + 42) == 0 && (mode & 0xf000) is 0 or 0x8000 &&
            (mode & 0x49) == 0);
        Require(archive.AsSpan(central + 46, nameLength).SequenceEqual("artifact-envelope.json"u8));
        Require(U32(archive, 0) == 0x04034b50 && U16(archive, 6) == flags &&
            U16(archive, 8) == method && U16(archive, 26) == nameLength && U16(archive, 28) == 0);
        Require(archive.AsSpan(30, nameLength).SequenceEqual("artifact-envelope.json"u8));
        var start = 30 + nameLength;
        var dataEnd = checked(start + compressed);
        Require(dataEnd <= central);
        if ((flags & 8) != 0)
        {
            var descriptorLength = central - dataEnd;
            var signed = descriptorLength == 16 && U32(archive, dataEnd) == 0x08074b50;
            Require(signed || descriptorLength == 12);
            var offset = dataEnd + (signed ? 4 : 0);
            Require(U32(archive, offset) == crc && U32(archive, offset + 4) == compressed &&
                U32(archive, offset + 8) == expanded);
        }
        else Require(dataEnd == central && U32(archive, 14) == crc &&
            U32(archive, 18) == compressed && U32(archive, 22) == expanded);

        var envelope = new byte[expanded];
        try
        {
            if (method == 0) archive.AsMemory(start, compressed).CopyTo(envelope);
            else
            {
                using var input = new MemoryStream(archive, start, compressed, writable: false);
                using var inflater = new DeflateStream(input, CompressionMode.Decompress);
                await inflater.ReadExactlyAsync(envelope, token).ConfigureAwait(false);
                var extra = new byte[1];
                Require(await inflater.ReadAsync(extra, token).ConfigureAwait(false) == 0);
            }
            Require(Crc32(envelope, token) == crc);
            return DecodeEnvelope(envelope, maximumBytes, token);
        }
        finally { CryptographicOperations.ZeroMemory(envelope); }
    }

    private static ArtifactTransportEnvelope DecodeEnvelope(byte[] bytes, int maximumBytes, CancellationToken token)
    {
        // Reject unknown/duplicate fields before materializing their values. A
        // bounded carrier must not turn millions of tiny properties into an
        // unbounded DOM/property-name allocation.
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 2 });
        Require(reader.Read() && reader.TokenType == JsonTokenType.StartObject);
        var fields = new string[6];
        var seen = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            token.ThrowIfCancellationRequested();
            Require(reader.TokenType == JsonTokenType.PropertyName);
            var index = reader.ValueTextEquals("discriminator"u8) ? 0 :
                reader.ValueTextEquals("producing_run_id"u8) ? 1 :
                reader.ValueTextEquals("producing_run_attempt"u8) ? 2 :
                reader.ValueTextEquals("encrypted_object_digest"u8) ? 3 :
                reader.ValueTextEquals("encrypted_object_size"u8) ? 4 :
                reader.ValueTextEquals("encrypted_object_base64"u8) ? 5 : -1;
            Require(index >= 0 && (seen & (1 << index)) == 0);
            seen |= 1 << index;
            Require(reader.Read() && reader.TokenType == JsonTokenType.String);
            Require(reader.ValueSpan.Length <= (index == 5 ? bytes.Length : 384));
            fields[index] = reader.GetString()!;
        }
        Require(reader.TokenType == JsonTokenType.EndObject && seen == 63 && !reader.Read());
        Require(fields[0] == Discriminator);
        var attemptValid = PositiveDecimal(fields[2], out var attempt);
        var sizeValid = PositiveDecimal(fields[4], out var size);
        Require(PositiveDecimal(fields[1], out _) && attemptValid && sizeValid &&
            size <= maximumBytes && Digest(fields[3]));
        var encoded = fields[5];
        Require(encoded.Length == 4 * ((size + 2) / 3));
        var padding = (3 - size % 3) % 3;
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        for (var i = 0; i < encoded.Length; i++)
        {
            if ((i & 0xffff) == 0) token.ThrowIfCancellationRequested();
            Require(i >= encoded.Length - padding ? encoded[i] == '=' : alphabet.Contains(encoded[i]));
        }
        if (padding != 0)
            Require((alphabet.IndexOf(encoded[encoded.Length - (int)padding - 1]) & (padding == 2 ? 15 : 3)) == 0);
        var encrypted = Convert.FromBase64String(encoded);
        try
        {
            token.ThrowIfCancellationRequested();
            Require(encrypted.LongLength == size && OpaqueStoreHash.Sha256(encrypted) == fields[3]);
            return new(fields[1], attempt, fields[3], encrypted);
        }
        catch { CryptographicOperations.ZeroMemory(encrypted); throw; }
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    internal static void Require(bool condition)
    {
        if (!condition) throw new ArtifactTransportException(OpaqueStoreFailure.Invalid);
    }

    private static uint Crc32(byte[] bytes, CancellationToken token)
    {
        var crc = uint.MaxValue;
        for (var i = 0; i < bytes.Length; i++)
        {
            if ((i & 0xffff) == 0) token.ThrowIfCancellationRequested();
            crc = CrcTable[(crc ^ bytes[i]) & 255] ^ (crc >> 8);
        }
        return crc ^ uint.MaxValue;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320 ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }
}
