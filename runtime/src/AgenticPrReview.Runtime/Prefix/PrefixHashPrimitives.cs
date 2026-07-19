using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace AgenticPrReview.Runtime.Prefix;

/// <summary>
/// Hash-framing primitives: identity encoding, big-endian uint32 framing, and
/// lowercase SHA-256 hex, exactly as specified under ## Prefix Contract of the
/// design document.
/// </summary>
internal static class PrefixHashPrimitives
{
    /// <summary>encodeIdentity(x) = uint32be(byteLength(UTF8(x))) || UTF8(x).</summary>
    internal static void WriteIdentity(ArrayBufferWriter<byte> writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteUInt32BigEndian(writer, (uint)bytes.Length);
        writer.Write(bytes);
    }

    internal static void WriteIdentity(ArrayBufferWriter<byte> writer, long asciiDecimalValue)
    {
        WriteIdentity(writer, asciiDecimalValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    internal static void WriteUInt32BigEndian(ArrayBufferWriter<byte> writer, uint value)
    {
        var span = writer.GetSpan(4);
        span[0] = (byte)(value >> 24);
        span[1] = (byte)(value >> 16);
        span[2] = (byte)(value >> 8);
        span[3] = (byte)value;
        writer.Advance(4);
    }

    internal static string Sha256Hex(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>digestId(tag, canonicalEnvelopeBytes) per ## Prefix Contract.</summary>
    internal static string DigestId(byte[] tagWithNul, ReadOnlySpan<byte> canonicalEnvelopeBytes)
    {
        var preimage = new ArrayBufferWriter<byte>(tagWithNul.Length + canonicalEnvelopeBytes.Length);
        preimage.Write(tagWithNul);
        preimage.Write(canonicalEnvelopeBytes);
        return Sha256Hex(preimage.WrittenSpan);
    }

    internal static ImmutableArray<byte> FrameSegment(ReadOnlySpan<byte> segmentPayload)
    {
        var framed = new ArrayBufferWriter<byte>(segmentPayload.Length + 4);
        WriteUInt32BigEndian(framed, checked((uint)segmentPayload.Length));
        framed.Write(segmentPayload);
        return framed.WrittenSpan.ToArray().ToImmutableArray();
    }
}
