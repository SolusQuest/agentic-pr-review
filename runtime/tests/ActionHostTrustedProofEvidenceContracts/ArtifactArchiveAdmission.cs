using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

public sealed record AdmittedArtifact(
    string ProducingRunId,
    string ProducingRunAttempt,
    string ArchiveSha256,
    string EnvelopeSha256,
    string EncryptedObjectSha256,
    byte[] EncryptedObject);

public static class ArtifactArchiveAdmission
{
    public const string EntryName = "artifact-envelope.json";
    public const string Discriminator = "apr.private-artifact-envelope.s2";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static AdmittedArtifact Admit(
        ReadOnlySpan<byte> archiveBytes,
        string expectedArchiveSha256,
        string expectedRunId,
        string expectedRunAttempt)
    {
        if (archiveBytes.Length is < 1 or > EvidenceLimits.MaximumArchiveBytes ||
            !StringComparer.Ordinal.Equals(
                CanonicalEvidence.Sha256(archiveBytes),
                expectedArchiveSha256))
        {
            throw new InvalidDataException("artifact_archive_invalid");
        }

        using var memory = new MemoryStream(archiveBytes.ToArray(), writable: false);
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count != 1)
        {
            throw new InvalidDataException("artifact_archive_shape_invalid");
        }

        var entry = archive.Entries[0];
        var unixMode = (entry.ExternalAttributes >> 16) & 0xffff;
        var fileType = unixMode & 0xf000;
        if (!StringComparer.Ordinal.Equals(entry.FullName, EntryName) ||
            entry.FullName.Contains('/') ||
            entry.FullName.Contains('\\') ||
            entry.Name.Length == 0 ||
            entry.Length is < 1 or > EvidenceLimits.MaximumArchiveBytes ||
            entry.CompressedLength < 0 ||
            entry.CompressedLength > EvidenceLimits.MaximumArchiveBytes ||
            entry.Length > Math.Max(1, entry.CompressedLength) *
                EvidenceLimits.MaximumCompressionRatio ||
            (fileType != 0 && fileType != 0x8000) ||
            (unixMode & 0x49) != 0)
        {
            throw new InvalidDataException("artifact_archive_shape_invalid");
        }

        byte[] envelopeBytes;
        using (var stream = entry.Open())
        using (var bounded = new MemoryStream())
        {
            stream.CopyTo(bounded);
            if (bounded.Length != entry.Length ||
                bounded.Length > EvidenceLimits.MaximumArchiveBytes)
            {
                throw new InvalidDataException("artifact_archive_truncated");
            }

            envelopeBytes = bounded.ToArray();
        }

        try
        {
            using var document = JsonDocument.Parse(envelopeBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var root = document.RootElement;
            var properties = root.EnumerateObject().ToArray();
            var expectedNames = new[]
            {
                "discriminator",
                "producing_run_id",
                "producing_run_attempt",
                "encrypted_object_digest",
                "encrypted_object_size",
                "encrypted_object_base64",
            };
            if (root.ValueKind != JsonValueKind.Object ||
                !properties.Select(property => property.Name).SequenceEqual(expectedNames))
            {
                throw new InvalidDataException("artifact_envelope_invalid");
            }

            var discriminator = root.GetProperty("discriminator").GetString();
            var runId = root.GetProperty("producing_run_id").GetString();
            var runAttempt = root.GetProperty("producing_run_attempt").GetString();
            var objectDigest = root.GetProperty("encrypted_object_digest").GetString();
            var sizeText = root.GetProperty("encrypted_object_size").GetString();
            var encoded = root.GetProperty("encrypted_object_base64").GetString();
            if (!StringComparer.Ordinal.Equals(discriminator, Discriminator) ||
                !StringComparer.Ordinal.Equals(runId, expectedRunId) ||
                !StringComparer.Ordinal.Equals(runAttempt, expectedRunAttempt) ||
                !PositiveDecimal(runId) || !PositiveDecimal(runAttempt) ||
                !PositiveDecimal(sizeText) || objectDigest is null ||
                objectDigest.Length != 64 || encoded is null)
            {
                throw new InvalidDataException("artifact_envelope_invalid");
            }

            var encrypted = Convert.FromBase64String(encoded);
            if (!long.TryParse(sizeText, out var size) ||
                size != encrypted.Length ||
                encrypted.Length is < 1 or > EvidenceLimits.MaximumEncryptedObjectBytes ||
                !StringComparer.Ordinal.Equals(Convert.ToBase64String(encrypted), encoded) ||
                !StringComparer.Ordinal.Equals(CanonicalEvidence.Sha256(encrypted), objectDigest))
            {
                CryptographicOperations.ZeroMemory(encrypted);
                throw new InvalidDataException("artifact_envelope_invalid");
            }

            return new AdmittedArtifact(
                runId!,
                runAttempt!,
                expectedArchiveSha256,
                CanonicalEvidence.Sha256(envelopeBytes),
                objectDigest,
                encrypted);
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or InvalidOperationException)
        {
            throw new InvalidDataException("artifact_envelope_invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelopeBytes);
        }
    }

    private static bool PositiveDecimal(string? value) =>
        value is not null &&
        value.Length > 0 && value.Length <= 20 &&
        value.All(character => character is >= '0' and <= '9') &&
        (value.Length == 1 || value[0] != '0') &&
        ulong.TryParse(value, out var parsed) && parsed > 0;
}
