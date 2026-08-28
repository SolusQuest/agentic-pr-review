using System.Security.Cryptography;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

public sealed record PhaseFragmentDocument(
    string Kind,
    string DestinationIdentitySha256,
    string[] OperationIds,
    int Sequence,
    string Phase,
    string? PredecessorSha256,
    string SourceId,
    string Endpoint,
    int Page,
    long RequestStartedUnixMilliseconds,
    long ResponseReceivedUnixMilliseconds,
    string BodySha256,
    string SafeHeadersSha256,
    string BodyPhysicalIdentitySha256,
    bool Finalized);

public sealed record PhaseFragmentReference(
    int Sequence,
    string Path,
    string Sha256,
    string PhysicalIdentitySha256);

public sealed record PhaseFragmentJournalDocument(
    string Kind,
    string DestinationIdentitySha256,
    string[] OperationIds,
    PhaseFragmentReference[] Fragments,
    bool Finalized);

public static class PhaseFragmentJournal
{
    public const string FragmentKind = "apr-r4-e3-phase-fragment-v1";
    public const string JournalKind = "apr-r4-e3-phase-fragment-journal-v1";
    public const string JournalName = "phase-fragment-journal.json";

    public static PhaseFragmentReference AppendCreateNew(
        RestrictedEvidenceRoot root,
        string packageName,
        IReadOnlyList<string> operationIds,
        int sequence,
        string? predecessorSha256,
        CaptureManifestSource source)
    {
        var relativePath = $"{packageName}/phase-fragment-{sequence:D4}.json";
        var fragment = new PhaseFragmentDocument(
            FragmentKind,
            root.DestinationIdentitySha256,
            [.. operationIds],
            sequence,
            source.SourceId,
            predecessorSha256,
            source.SourceId,
            source.Route,
            source.Page,
            source.RequestStartedUnixMilliseconds,
            source.ResponseReceivedUnixMilliseconds,
            source.BodySha256,
            source.SafeHeadersSha256,
            source.BodyFileIdentity,
            Finalized: true);
        ValidateFragment(fragment, root.DestinationIdentitySha256, operationIds, source, sequence,
            predecessorSha256);
        var bytes = CanonicalEvidence.Encode(fragment, EvidenceJson.Options);
        try
        {
            var identity = root.WritePinnedFileCreateNew(relativePath, bytes);
            return new PhaseFragmentReference(
                sequence,
                System.IO.Path.GetFileName(relativePath),
                CanonicalEvidence.Sha256(bytes),
                identity);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static (string Path, string Sha256, string PhysicalIdentitySha256) FinalizeCreateNew(
        RestrictedEvidenceRoot root,
        string packageName,
        IReadOnlyList<string> operationIds,
        IReadOnlyList<PhaseFragmentReference> fragments,
        IReadOnlyList<CaptureManifestSource> sources)
    {
        ValidateReferences(root, packageName, operationIds, fragments, sources);
        var document = new PhaseFragmentJournalDocument(
            JournalKind,
            root.DestinationIdentitySha256,
            [.. operationIds],
            [.. fragments],
            Finalized: true);
        var bytes = CanonicalEvidence.Encode(document, EvidenceJson.Options);
        var relativePath = $"{packageName}/{JournalName}";
        try
        {
            var identity = root.WritePinnedFileCreateNew(relativePath, bytes);
            return (JournalName, CanonicalEvidence.Sha256(bytes), identity);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static void Validate(
        RestrictedEvidenceRoot root,
        string captureManifestRelativePath,
        CaptureManifestDocument capture)
    {
        var packageName = System.IO.Path.GetDirectoryName(captureManifestRelativePath)?.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(packageName) || packageName.Contains('/') ||
            capture.PhaseFragmentJournalPath != JournalName)
        {
            throw new InvalidDataException("phase_fragment_journal_invalid");
        }
        var journalPath = $"{packageName}/{capture.PhaseFragmentJournalPath}";
        using var lease = root.AcquirePinnedFile(journalPath, EvidenceLimits.MaximumDocumentBytes);
        if (CanonicalEvidence.Sha256(lease.Bytes) != capture.PhaseFragmentJournalSha256 ||
            lease.Identity != capture.PhaseFragmentJournalFileIdentity)
        {
            throw new InvalidDataException("phase_fragment_journal_invalid");
        }
        try
        {
            var value = System.Text.Json.JsonSerializer.Deserialize<PhaseFragmentJournalDocument>(
                lease.Bytes,
                EvidenceJson.Options) ?? throw new InvalidDataException("phase_fragment_journal_invalid");
            var canonical = CanonicalEvidence.Encode(value, EvidenceJson.Options);
            try
            {
                if (!lease.Bytes.AsSpan().SequenceEqual(canonical) || value.Kind != JournalKind ||
                    !value.Finalized || value.DestinationIdentitySha256 != root.DestinationIdentitySha256 ||
                    !value.OperationIds.SequenceEqual(capture.OperationIds, StringComparer.Ordinal))
                {
                    throw new InvalidDataException("phase_fragment_journal_invalid");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonical);
            }
            ValidateReferences(root, packageName, capture.OperationIds, value.Fragments, capture.Sources);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new InvalidDataException("phase_fragment_journal_invalid");
        }
    }

    private static void ValidateReferences(
        RestrictedEvidenceRoot root,
        string packageName,
        IReadOnlyList<string> operationIds,
        IReadOnlyList<PhaseFragmentReference> fragments,
        IReadOnlyList<CaptureManifestSource> sources)
    {
        if (fragments.Count != sources.Count || fragments.Count == 0 ||
            fragments.Select(item => item.Path).Distinct(StringComparer.Ordinal).Count() != fragments.Count)
        {
            throw new InvalidDataException("phase_fragment_journal_invalid");
        }
        string? predecessor = null;
        for (var index = 0; index < fragments.Count; index++)
        {
            var reference = fragments[index];
            var source = sources[index];
            if (reference.Sequence != index + 1 || reference.Path != $"phase-fragment-{index + 1:D4}.json" ||
                !Sha256(reference.Sha256) || !Sha256(reference.PhysicalIdentitySha256))
            {
                throw new InvalidDataException("phase_fragment_journal_invalid");
            }
            using var lease = root.AcquirePinnedFile(
                $"{packageName}/{reference.Path}",
                EvidenceLimits.MaximumDocumentBytes);
            if (CanonicalEvidence.Sha256(lease.Bytes) != reference.Sha256 ||
                lease.Identity != reference.PhysicalIdentitySha256)
            {
                throw new InvalidDataException("phase_fragment_journal_invalid");
            }
            try
            {
                var fragment = System.Text.Json.JsonSerializer.Deserialize<PhaseFragmentDocument>(
                    lease.Bytes,
                    EvidenceJson.Options) ?? throw new InvalidDataException("phase_fragment_journal_invalid");
                var canonical = CanonicalEvidence.Encode(fragment, EvidenceJson.Options);
                try
                {
                    if (!lease.Bytes.AsSpan().SequenceEqual(canonical))
                    {
                        throw new InvalidDataException("phase_fragment_journal_invalid");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(canonical);
                }
                ValidateFragment(fragment, root.DestinationIdentitySha256, operationIds, source,
                    index + 1, predecessor);
            }
            catch (System.Text.Json.JsonException)
            {
                throw new InvalidDataException("phase_fragment_journal_invalid");
            }
            predecessor = reference.Sha256;
        }
    }

    private static void ValidateFragment(
        PhaseFragmentDocument value,
        string destinationIdentitySha256,
        IReadOnlyList<string> operationIds,
        CaptureManifestSource source,
        int sequence,
        string? predecessorSha256)
    {
        if (value.Kind != FragmentKind || !value.Finalized ||
            value.DestinationIdentitySha256 != destinationIdentitySha256 ||
            !value.OperationIds.SequenceEqual(operationIds, StringComparer.Ordinal) ||
            value.Sequence != sequence || value.Phase != source.SourceId ||
            value.PredecessorSha256 != predecessorSha256 || value.SourceId != source.SourceId ||
            value.Endpoint != source.Route || value.Page != source.Page ||
            value.RequestStartedUnixMilliseconds != source.RequestStartedUnixMilliseconds ||
            value.ResponseReceivedUnixMilliseconds != source.ResponseReceivedUnixMilliseconds ||
            value.BodySha256 != source.BodySha256 || value.SafeHeadersSha256 != source.SafeHeadersSha256 ||
            value.BodyPhysicalIdentitySha256 != source.BodyFileIdentity ||
            (predecessorSha256 is not null && !Sha256(predecessorSha256)))
        {
            throw new InvalidDataException("phase_fragment_journal_invalid");
        }
    }

    private static bool Sha256(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
