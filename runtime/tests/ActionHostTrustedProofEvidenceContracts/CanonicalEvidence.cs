using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

public static class CanonicalEvidence
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static byte[] Encode<T>(T value, JsonSerializerOptions options)
    {
        var json = JsonSerializer.Serialize(value, options);
        var bytes = StrictUtf8.GetBytes(json + "\n");
        if (bytes.Length is < 1 or > EvidenceLimits.MaximumDocumentBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidDataException("evidence_document_invalid");
        }

        return bytes;
    }

    public static void WriteCreateNew(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4_096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
