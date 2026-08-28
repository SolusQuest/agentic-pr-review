using System.Security.Cryptography;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

public sealed record CorrectionGateReadback(
    string SourceId,
    string BodyPath,
    string BodySha256,
    string BodyPhysicalIdentitySha256,
    long RequestStartedUnixMilliseconds,
    long ResponseReceivedUnixMilliseconds);

public sealed record CorrectionGateIdentity(string Component, string SourceSha256, string BuildSha256);

public sealed record CorrectionGateContract(string Component, string Sha256);

public sealed record CorrectionGateReceiptDocument(
    string Kind,
    string DestinationIdentitySha256,
    string ExecutionAuthorizationSha256,
    string CorrectionGateSha256,
    string Repository,
    string PullRequestNumber,
    string Branch,
    string Commit,
    string Tree,
    CorrectionGateReadback[] RemoteReadbacks,
    CorrectionGateIdentity[] AuthorityIdentities,
    CorrectionGateContract[] ContractDigests,
    bool WorktreeClean,
    bool Finalized);

public sealed record CorrectionGateReceiptMaterialization(
    CorrectionGateReceiptDocument Document,
    string Sha256,
    string PhysicalIdentitySha256);

public static class CorrectionGateReceipt
{
    public const string Kind = "apr-r4-e3-correction-gate-readiness-v1";

    public static CorrectionGateReceiptMaterialization MaterializeCreateNew(
        RestrictedEvidenceRoot root,
        string relativePath,
        CorrectionGateReceiptDocument document)
    {
        Validate(document, root.DestinationIdentitySha256);
        var bytes = CanonicalEvidence.Encode(document, EvidenceJson.Options);
        try
        {
            var identity = root.WritePinnedFileCreateNew(relativePath, bytes);
            return new(document, CanonicalEvidence.Sha256(bytes), identity);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static CorrectionGateReceiptMaterialization Read(
        RestrictedEvidenceRoot root,
        string relativePath,
        string executionAuthorizationSha256,
        string correctionGateSha256)
    {
        using var lease = root.AcquirePinnedFile(relativePath, EvidenceLimits.MaximumDocumentBytes);
        var document = System.Text.Json.JsonSerializer.Deserialize<CorrectionGateReceiptDocument>(
            lease.Bytes,
            EvidenceJson.Options) ?? throw new InvalidDataException("correction_gate_receipt_invalid");
        var canonical = CanonicalEvidence.Encode(document, EvidenceJson.Options);
        try
        {
            if (!lease.Bytes.AsSpan().SequenceEqual(canonical))
            {
                throw new InvalidDataException("correction_gate_receipt_invalid");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
        Validate(document, root.DestinationIdentitySha256);
        if (document.ExecutionAuthorizationSha256 != executionAuthorizationSha256 ||
            document.CorrectionGateSha256 != correctionGateSha256)
        {
            throw new InvalidDataException("correction_gate_receipt_invalid");
        }
        return new(document, CanonicalEvidence.Sha256(lease.Bytes), lease.Identity);
    }

    private static void Validate(CorrectionGateReceiptDocument value, string destinationIdentity)
    {
        var identityComponents = new[]
        {
            "capture", "credential-materializer", "producer-journal-materializer",
            "phase-fragment-materializer", "oracle", "assembler",
        };
        var contractComponents = new[]
        {
            "cleanup-generator", "projector", "static-checker", "source-map",
            "host-schema", "private-package-schema", "public-schema", "authorization-grammar",
        };
        if (value.Kind != Kind || !value.Finalized || !value.WorktreeClean ||
            value.DestinationIdentitySha256 != destinationIdentity ||
            !Sha256(value.ExecutionAuthorizationSha256) || !Sha256(value.CorrectionGateSha256) ||
            string.IsNullOrWhiteSpace(value.Repository) || !Decimal(value.PullRequestNumber) ||
            string.IsNullOrWhiteSpace(value.Branch) || !Hex40(value.Commit) || !Hex40(value.Tree) ||
            value.RemoteReadbacks.Length != 2 ||
            value.RemoteReadbacks.Select(item => item.SourceId).Distinct(StringComparer.Ordinal).Count() != 2 ||
            value.RemoteReadbacks.Any(item => !Sha256(item.BodySha256) ||
                !Sha256(item.BodyPhysicalIdentitySha256) ||
                item.ResponseReceivedUnixMilliseconds < item.RequestStartedUnixMilliseconds) ||
            !value.AuthorityIdentities.Select(item => item.Component)
                .SequenceEqual(identityComponents, StringComparer.Ordinal) ||
            value.AuthorityIdentities.Any(item => !Sha256(item.SourceSha256) || !Sha256(item.BuildSha256)) ||
            !value.ContractDigests.Select(item => item.Component)
                .SequenceEqual(contractComponents, StringComparer.Ordinal) ||
            value.ContractDigests.Any(item => !Sha256(item.Sha256)))
        {
            throw new InvalidDataException("correction_gate_receipt_invalid");
        }
    }

    private static bool Sha256(string value) => value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Hex40(string value) => value.Length == 40 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Decimal(string value) => value.Length > 0 &&
        value.All(character => character is >= '0' and <= '9') && (value.Length == 1 || value[0] != '0');
}
