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
        using var handle = EvidenceFileHandle.CreateNewNoFollow(path);
        var before = EvidenceFileHandle.Identity(handle);
        if (before.Links != 1 || before.Size != 0)
        {
            throw new InvalidDataException("restricted_file_create_invalid");
        }
        using var stream = new FileStream(handle, FileAccess.Write, bufferSize: 4_096, isAsync: false);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        var after = EvidenceFileHandle.Identity(stream.SafeFileHandle);
        if (after with { Size = before.Size } != before || after.Size != bytes.Length)
        {
            throw new InvalidDataException("restricted_file_create_invalid");
        }
    }

    public static PinnedEvidenceFile ReadPinnedAbsolute(string path, int maximumBytes)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || maximumBytes < 1)
        {
            throw new InvalidDataException("restricted_file_invalid");
        }
        using var handle = EvidenceFileHandle.OpenNoFollow(System.IO.Path.GetFullPath(path));
        var before = EvidenceFileHandle.Identity(handle);
        if (before.Links != 1 || before.Size is < 1 || before.Size > maximumBytes)
        {
            throw new InvalidDataException("restricted_file_identity_invalid");
        }
        var bytes = new byte[checked((int)before.Size)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
            if (read == 0)
            {
                CryptographicOperations.ZeroMemory(bytes);
                throw new InvalidDataException("restricted_file_replaced");
            }
            offset += read;
        }
        Span<byte> extra = stackalloc byte[1];
        if (RandomAccess.Read(handle, extra, before.Size) != 0 ||
            EvidenceFileHandle.Identity(handle) != before)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidDataException("restricted_file_replaced");
        }
        return new PinnedEvidenceFile(
            bytes,
            Sha256(Encoding.UTF8.GetBytes(before.Canonical)));
    }
}
