using System.Security.Cryptography;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

public sealed record CredentialObservation(
    long RequestStartedUnixMilliseconds,
    long ResponseReceivedUnixMilliseconds);

public sealed record CredentialConsumerIdentity(
    string Component,
    string BuildSha256);

public sealed record CredentialAdmissionSlot(
    string Name,
    bool Required,
    bool Base64Key,
    string PhysicalIdentitySha256,
    string InitialState);

public sealed record CredentialAdmissionOmission(
    string Name,
    string FinalState);

public sealed record CredentialAdmissionDocument(
    string Kind,
    string DestinationIdentitySha256,
    string[] OperationIds,
    string ExecutionAuthorizationSha256,
    string CorrectionGateSha256,
    string CorrectionGateReceiptSha256,
    string CorrectionGateReceiptPhysicalIdentitySha256,
    string MaterializerSourceSha256,
    string MaterializerBuildSha256,
    CredentialConsumerIdentity[] Consumers,
    CredentialAdmissionSlot[] CreatedSlots,
    CredentialAdmissionOmission[] OmittedSlots,
    CredentialObservation AdmissionObservation,
    bool Finalized);

public sealed record CredentialAdmissionMaterialization(
    CredentialAdmissionDocument Document,
    string Sha256,
    string PhysicalIdentitySha256);

public sealed record CredentialDispositionSlot(
    string Name,
    string PhysicalIdentitySha256,
    string FinalState);

public sealed record CredentialDispositionDocument(
    string Kind,
    string DestinationIdentitySha256,
    string[] OperationIds,
    string AdmissionReceiptSha256,
    string AdmissionReceiptPhysicalIdentitySha256,
    CredentialDispositionSlot[] CreatedSlots,
    CredentialAdmissionOmission[] OmittedSlots,
    CredentialObservation AbsenceObservation,
    string[] AbsenceSourceIds,
    bool Finalized);

public sealed record CredentialDispositionMaterialization(
    CredentialDispositionDocument Document,
    string Sha256,
    string PhysicalIdentitySha256);

public static class CredentialAdmissionReceipt
{
    public const string Kind = "apr-r4-e3-credential-admission-v1";
    public const string DispositionKind = "apr-r4-e3-credential-disposition-v1";
    private static readonly string[] RequiredNames = ["github-token", "current-state-key"];
    private static readonly string[] ConsumerComponents =
    [
        "producer-journal-materializer",
        "phase-fragment-materializer",
        "capture",
        "oracle",
        "assembler",
    ];

    public static CredentialAdmissionMaterialization MaterializeCreateNew(
        RestrictedEvidenceRoot root,
        string relativePath,
        IReadOnlyList<string> operationIds,
        IReadOnlyDictionary<string, CredentialFileRepresentations> created,
        string executionAuthorizationSha256,
        string correctionGateSha256,
        string correctionGateReceiptSha256,
        string correctionGateReceiptPhysicalIdentitySha256,
        string materializerSourceSha256,
        string materializerBuildSha256,
        IReadOnlyList<CredentialConsumerIdentity> consumers,
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
                "created");
        }).ToArray();
        var omitted = hasPrevious
            ? []
            : new[] { new CredentialAdmissionOmission("previous-state-key", "not-created") };
        var document = new CredentialAdmissionDocument(
            Kind,
            root.DestinationIdentitySha256,
            [.. operationIds],
            executionAuthorizationSha256,
            correctionGateSha256,
            correctionGateReceiptSha256,
            correctionGateReceiptPhysicalIdentitySha256,
            materializerSourceSha256,
            materializerBuildSha256,
            [.. consumers],
            slots,
            omitted,
            new CredentialObservation(
                requestStartedUnixMilliseconds,
                responseReceivedUnixMilliseconds),
            Finalized: true);
        Validate(document, root.DestinationIdentitySha256, operationIds);
        var bytes = CanonicalEvidence.Encode(document, EvidenceJson.Options);
        try
        {
            var identity = root.WritePinnedFileCreateNew(relativePath, bytes);
            return new CredentialAdmissionMaterialization(
                document,
                CanonicalEvidence.Sha256(bytes),
                identity);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static CredentialAdmissionMaterialization Read(
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
            return new CredentialAdmissionMaterialization(
                value,
                CanonicalEvidence.Sha256(pinned.Bytes),
                pinned.Identity);
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

    public static CredentialDispositionMaterialization MaterializeDispositionCreateNew(
        RestrictedEvidenceRoot root,
        string relativePath,
        IReadOnlyList<string> operationIds,
        CredentialAdmissionMaterialization admission,
        long requestStartedUnixMilliseconds,
        long responseReceivedUnixMilliseconds,
        IReadOnlyList<string> absenceSourceIds)
    {
        foreach (var name in RequiredNames.Append("previous-state-key"))
        {
            if (EvidenceFileHandle.PathEntryExists(
                    RestrictedEvidenceRoot.ResolveChildPath(root.Path, name)))
            {
                throw new InvalidDataException("credential_disposition_invalid");
            }
        }
        var document = new CredentialDispositionDocument(
            DispositionKind,
            root.DestinationIdentitySha256,
            [.. operationIds],
            admission.Sha256,
            admission.PhysicalIdentitySha256,
            admission.Document.CreatedSlots.Select(slot => new CredentialDispositionSlot(
                slot.Name,
                slot.PhysicalIdentitySha256,
                "created-then-deleted")).ToArray(),
            admission.Document.OmittedSlots,
            new CredentialObservation(
                requestStartedUnixMilliseconds,
                responseReceivedUnixMilliseconds),
            [.. absenceSourceIds],
            Finalized: true);
        ValidateDisposition(document, root.DestinationIdentitySha256, operationIds, admission);
        var bytes = CanonicalEvidence.Encode(document, EvidenceJson.Options);
        try
        {
            var identity = root.WritePinnedFileCreateNew(relativePath, bytes);
            return new CredentialDispositionMaterialization(
                document,
                CanonicalEvidence.Sha256(bytes),
                identity);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

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
            !Sha256(value.ExecutionAuthorizationSha256) ||
            !Sha256(value.CorrectionGateSha256) ||
            !Sha256(value.CorrectionGateReceiptSha256) ||
            !Sha256(value.CorrectionGateReceiptPhysicalIdentitySha256) ||
            !Sha256(value.MaterializerSourceSha256) ||
            !Sha256(value.MaterializerBuildSha256) ||
            value.Consumers.Length != ConsumerComponents.Length ||
            !value.Consumers.Select(item => item.Component)
                .SequenceEqual(ConsumerComponents, StringComparer.Ordinal) ||
            value.Consumers.Any(item => !Sha256(item.BuildSha256)) ||
            !createdNames.SequenceEqual(expectedNames, StringComparer.Ordinal) ||
            value.CreatedSlots.Any(slot =>
                slot.Required != (slot.Name != "previous-state-key") ||
                slot.Base64Key != (slot.Name != "github-token") ||
                !Sha256(slot.PhysicalIdentitySha256) ||
                slot.InitialState != "created") ||
            value.AdmissionObservation.RequestStartedUnixMilliseconds < 0 ||
            value.AdmissionObservation.ResponseReceivedUnixMilliseconds <
                value.AdmissionObservation.RequestStartedUnixMilliseconds ||
            (hasPrevious
                ? value.OmittedSlots.Length != 0
                : value.OmittedSlots.Length != 1 ||
                    value.OmittedSlots[0].Name != "previous-state-key" ||
                    value.OmittedSlots[0].FinalState != "not-created"))
        {
            throw new InvalidDataException("credential_admission_invalid");
        }
    }

    private static void ValidateDisposition(
        CredentialDispositionDocument value,
        string destinationIdentitySha256,
        IReadOnlyList<string> operationIds,
        CredentialAdmissionMaterialization admission)
    {
        if (value.Kind != DispositionKind || !value.Finalized ||
            value.DestinationIdentitySha256 != destinationIdentitySha256 ||
            !value.OperationIds.SequenceEqual(operationIds, StringComparer.Ordinal) ||
            value.AdmissionReceiptSha256 != admission.Sha256 ||
            value.AdmissionReceiptPhysicalIdentitySha256 != admission.PhysicalIdentitySha256 ||
            value.CreatedSlots.Length != admission.Document.CreatedSlots.Length ||
            !value.CreatedSlots.Select(slot => slot.Name).SequenceEqual(
                admission.Document.CreatedSlots.Select(slot => slot.Name),
                StringComparer.Ordinal) ||
            value.CreatedSlots.Any(slot => !Sha256(slot.PhysicalIdentitySha256) ||
                slot.FinalState != "created-then-deleted") ||
            value.CreatedSlots.Where((slot, index) =>
                slot.PhysicalIdentitySha256 != admission.Document.CreatedSlots[index].PhysicalIdentitySha256)
                .Any() ||
            !value.OmittedSlots.SequenceEqual(admission.Document.OmittedSlots) ||
            value.AbsenceObservation.RequestStartedUnixMilliseconds < 0 ||
            value.AbsenceObservation.ResponseReceivedUnixMilliseconds <
                value.AbsenceObservation.RequestStartedUnixMilliseconds ||
            value.AbsenceSourceIds.Count() == 0 ||
            value.AbsenceSourceIds.Distinct(StringComparer.Ordinal).Count() !=
                value.AbsenceSourceIds.Length ||
            value.AbsenceSourceIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException("credential_disposition_invalid");
        }
    }

    private static bool Sha256(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
