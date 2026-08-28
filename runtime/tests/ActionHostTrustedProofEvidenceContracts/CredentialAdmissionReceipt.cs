using System.Security.Cryptography;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

public sealed record CredentialAdmissionObservation(
    long RequestStartedUnixMilliseconds,
    long ResponseReceivedUnixMilliseconds);

public sealed record CredentialAdmissionSlot(
    string Name,
    bool Required,
    bool Base64Key,
    string PhysicalIdentitySha256,
    CredentialAdmissionObservation CreationObservation,
    string FinalState);

public sealed record CredentialAdmissionOmission(
    string Name,
    string FinalState);

public sealed record CredentialAdmissionDocument(
    string Kind,
    string DestinationIdentitySha256,
    string[] OperationIds,
    CredentialAdmissionSlot[] CreatedSlots,
    CredentialAdmissionOmission[] OmittedSlots,
    bool Finalized);

public static class CredentialAdmissionReceipt
{
    public const string Kind = "apr-r4-e3-credential-admission-v1";
    private static readonly string[] RequiredNames = ["github-token", "current-state-key"];

    public static CredentialAdmissionDocument MaterializeCreateNew(
        RestrictedEvidenceRoot root,
        string relativePath,
        IReadOnlyList<string> operationIds,
        IReadOnlyDictionary<string, CredentialFileRepresentations> created,
        long requestStartedUnixMilliseconds,
        long responseReceivedUnixMilliseconds)
    {
        var byName = created;
        var hasPrevious = byName.ContainsKey("previous-state-key");
        var expectedNames = hasPrevious
            ? RequiredNames.Append("previous-state-key").ToArray()
            : RequiredNames;
        if (byName.Count != expectedNames.Length || expectedNames.Any(name => !byName.ContainsKey(name)))
        {
            throw new InvalidDataException("credential_admission_invalid");
        }

        var slots = expectedNames.Select(name =>
        {
            var credential = byName[name];
            return new CredentialAdmissionSlot(
                name,
                name != "previous-state-key",
                name != "github-token",
                credential.PhysicalIdentitySha256,
                new CredentialAdmissionObservation(
                    requestStartedUnixMilliseconds,
                    responseReceivedUnixMilliseconds),
                "created-then-deleted");
        }).ToArray();
        var omitted = hasPrevious
            ? []
            : new[] { new CredentialAdmissionOmission("previous-state-key", "not-created") };
        var document = new CredentialAdmissionDocument(
            Kind,
            root.DestinationIdentitySha256,
            [.. operationIds],
            slots,
            omitted,
            Finalized: true);
        Validate(document, root.DestinationIdentitySha256, operationIds);
        var bytes = CanonicalEvidence.Encode(document, EvidenceJson.Options);
        try
        {
            root.WritePinnedFileCreateNew(relativePath, bytes);
            return Read(root, relativePath, operationIds);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static CredentialAdmissionDocument Read(
        RestrictedEvidenceRoot root,
        string relativePath,
        IReadOnlyList<string> operationIds)
    {
        using var pinned = root.AcquirePinnedFile(relativePath, EvidenceLimits.MaximumDocumentBytes);
        try
        {
            var value = System.Text.Json.JsonSerializer.Deserialize<CredentialAdmissionDocument>(
                pinned.Bytes,
                EvidenceJson.Options) ?? throw new InvalidDataException("credential_admission_invalid");
            var canonical = CanonicalEvidence.Encode(value, EvidenceJson.Options);
            try
            {
                if (!pinned.Bytes.AsSpan().SequenceEqual(canonical))
                {
                    throw new InvalidDataException("credential_admission_invalid");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonical);
            }
            Validate(value, root.DestinationIdentitySha256, operationIds);
            return value;
        }
        catch (System.Text.Json.JsonException)
        {
            throw new InvalidDataException("credential_admission_invalid");
        }
    }

    public static IReadOnlyDictionary<string, string> AuthorizedIdentities(
        CredentialAdmissionDocument value) =>
        value.CreatedSlots.ToDictionary(
            slot => slot.Name,
            slot => slot.PhysicalIdentitySha256,
            StringComparer.Ordinal);

    private static void Validate(
        CredentialAdmissionDocument value,
        string destinationIdentitySha256,
        IReadOnlyList<string> operationIds)
    {
        var createdNames = value.CreatedSlots.Select(slot => slot.Name).ToArray();
        var hasPrevious = createdNames.Length == 3;
        var expectedNames = hasPrevious
            ? RequiredNames.Append("previous-state-key").ToArray()
            : RequiredNames;
        if (value.Kind != Kind || !value.Finalized ||
            value.DestinationIdentitySha256 != destinationIdentitySha256 ||
            value.OperationIds.Length != 2 ||
            !value.OperationIds.SequenceEqual(operationIds, StringComparer.Ordinal) ||
            value.OperationIds.Distinct(StringComparer.Ordinal).Count() != 2 ||
            value.OperationIds.Any(value => !Sha256(value)) ||
            !createdNames.SequenceEqual(expectedNames, StringComparer.Ordinal) ||
            value.CreatedSlots.Any(slot =>
                slot.Required != (slot.Name != "previous-state-key") ||
                slot.Base64Key != (slot.Name != "github-token") ||
                !Sha256(slot.PhysicalIdentitySha256) ||
                slot.CreationObservation.RequestStartedUnixMilliseconds < 0 ||
                slot.CreationObservation.ResponseReceivedUnixMilliseconds <
                    slot.CreationObservation.RequestStartedUnixMilliseconds ||
                slot.FinalState != "created-then-deleted") ||
            (hasPrevious
                ? value.OmittedSlots.Length != 0
                : value.OmittedSlots.Length != 1 ||
                    value.OmittedSlots[0].Name != "previous-state-key" ||
                    value.OmittedSlots[0].FinalState != "not-created"))
        {
            throw new InvalidDataException("credential_admission_invalid");
        }
    }

    private static bool Sha256(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
