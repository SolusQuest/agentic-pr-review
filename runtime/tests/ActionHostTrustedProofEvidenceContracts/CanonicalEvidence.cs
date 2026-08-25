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
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 4_096,
            Options = FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using var stream = new FileStream(path, options);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
