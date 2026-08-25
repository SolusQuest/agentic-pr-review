using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofCapture;

public sealed record CapturePlanSource(string SourceId, string Route);

public sealed record CapturePlanArtifact(
    string ArtifactId,
    string ArtifactName,
    string ExpectedRole,
    string Scope,
    string OpaqueName,
    string ProducingRunId,
    string ProducingRunAttempt,
    string DownloadRoute);

public sealed record CapturePlanDocument(
    string Kind,
    string RepositoryId,
    string Repository,
    string[] OperationIds,
    string SourceMapSha256,
    string PackageName,
    CapturePlanSource[] Sources,
    CapturePlanArtifact[] Artifacts);

public static class CapturePlan
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static CapturePlanDocument Read(RestrictedEvidenceRoot root, string relativePath)
    {
        var path = root.ResolveExistingFile(relativePath, EvidenceLimits.MaximumDocumentBytes);
        var bytes = File.ReadAllBytes(path);
        try
        {
            var value = JsonSerializer.Deserialize<CapturePlanDocument>(bytes, EvidenceJson.Options) ??
                throw new InvalidDataException("capture_plan_invalid");
            var canonical = CanonicalEvidence.Encode(value, EvidenceJson.Options);
            try
            {
                if (!bytes.AsSpan().SequenceEqual(canonical) ||
                    value.Kind != "apr-r4-e3-capture-plan-v1" ||
                    !PositiveDecimal(value.RepositoryId) ||
                    value.Repository.Split('/').Length != 2 ||
                    value.Repository.Split('/').Any(part => !BoundedText(part, EvidenceLimits.MaximumNameBytes)) ||
                    value.OperationIds.Length != 2 ||
                    value.OperationIds.Distinct(StringComparer.Ordinal).Count() != 2 ||
                    value.OperationIds.Any(item => !Sha256(item)) ||
                    !Sha256(value.SourceMapSha256) ||
                    !BoundedText(value.PackageName, EvidenceLimits.MaximumNameBytes) ||
                    !RestrictedEvidenceRoot.IsSinglePathSegment(value.PackageName) ||
                    value.Sources.Length == 0 ||
                    value.Sources.Length > EvidenceLimits.MaximumRecords ||
                    value.Artifacts.Length == 0 ||
                    value.Artifacts.Length > EvidenceLimits.MaximumRecords ||
                    value.Sources.Select(item => item.SourceId).Distinct(StringComparer.Ordinal).Count() != value.Sources.Length ||
                    value.Sources.Any(item =>
                        !BoundedText(item.SourceId, EvidenceLimits.MaximumNameBytes) ||
                        !RepositoryRoute(item.Route, value.Repository)) ||
                    value.Artifacts.Select(item => item.ArtifactId).Distinct(StringComparer.Ordinal).Count() != value.Artifacts.Length ||
                    value.Artifacts.Any(item =>
                        !PositiveDecimal(item.ArtifactId) ||
                        !BoundedText(item.ArtifactName, EvidenceLimits.MaximumNameBytes) ||
                        !BoundedText(item.ExpectedRole, EvidenceLimits.MaximumNameBytes) ||
                        !new[] { "repository", "normal", "stale" }.Contains(item.Scope, StringComparer.Ordinal) ||
                        !BoundedText(item.OpaqueName, EvidenceLimits.MaximumNameBytes) ||
                        !PositiveDecimal(item.ProducingRunId) ||
                        !PositiveDecimal(item.ProducingRunAttempt) ||
                        !RepositoryRoute(item.DownloadRoute, value.Repository)))
                {
                    throw new InvalidDataException("capture_plan_invalid");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonical);
            }

            return value;
        }
        catch (JsonException)
        {
            throw new InvalidDataException("capture_plan_invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool PositiveDecimal(string value) =>
        value.Length > 0 && value.Length <= 20 &&
        value.All(character => character is >= '0' and <= '9') &&
        (value.Length == 1 || value[0] != '0') &&
        ulong.TryParse(value, out var parsed) && parsed > 0;

    private static bool Sha256(string value) =>
        value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool RepositoryRoute(string route, string repository) =>
        BoundedText(route, EvidenceLimits.MaximumRelativePathBytes) &&
        route.StartsWith($"/repos/{repository}/", StringComparison.Ordinal) &&
        !route.Contains("//", StringComparison.Ordinal);

    private static bool BoundedText(string? value, int maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(character => char.IsControl(character)))
        {
            return false;
        }

        try
        {
            return StrictUtf8.GetByteCount(value) <= maximumBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}
