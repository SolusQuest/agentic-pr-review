using System.Security.Cryptography;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

public sealed record PhaseFragmentDocument(
    string Kind,
    string DestinationIdentitySha256,
    string ExecutionAuthorizationSha256,
    string OperationId,
    string MaterializerSourceSha256,
    string MaterializerBuildSha256,
    string ProducerJournalSealSha256,
    int Sequence,
    string Phase,
    string? PredecessorSha256,
    string SourceId,
    string Endpoint,
    int Page,
    int Status,
    string BodyPath,
    long RequestStartedUnixMilliseconds,
    long ResponseReceivedUnixMilliseconds,
    string BodySha256,
    string BodySize,
    string SafeHeadersSha256,
    string BodyPhysicalIdentitySha256,
    string? NextRoute,
    bool Finalized);

public sealed record PhaseFragmentReference(
    int Sequence,
    string Path,
    string Sha256,
    string PhysicalIdentitySha256);

public sealed record PhaseFragmentPartialJournal(
    PhaseFragmentReference[] Fragments,
    CaptureManifestSource[] Sources);

public sealed record PhaseFragmentJournalDocument(
    string Kind,
    string DestinationIdentitySha256,
    string ExecutionAuthorizationSha256,
    string[] OperationIds,
    string MaterializerSourceSha256,
    string MaterializerBuildSha256,
    string ProducerJournalSealSha256,
    PhaseFragmentReference[] Fragments,
    bool Finalized);

public static class PhaseFragmentJournal
{
    public const string FragmentKind = "apr-r4-e3-phase-fragment-v1";
    public const string JournalKind = "apr-r4-e3-phase-fragment-journal-v1";
    public const string JournalName = "phase-fragment-journal.json";

    public static PhaseFragmentPartialJournal ReadPartial(
        RestrictedEvidenceRoot root,
        string packageName,
        IReadOnlyList<string> operationIds,
        string executionAuthorizationSha256,
        string materializerSourceSha256,
        string materializerBuildSha256,
        string producerJournalSealSha256)
    {
        var packagePath = RestrictedEvidenceRoot.ResolveChildPath(root.Path, packageName);
        var directory = new DirectoryInfo(packagePath);
        if (!directory.Exists || directory.LinkTarget is not null ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("phase_fragment_journal_invalid");
        }
        var names = Directory.EnumerateFiles(packagePath, "phase-fragment-*.json")
            .Select(System.IO.Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!names.SequenceEqual(
                Enumerable.Range(1, names.Length).Select(index => $"phase-fragment-{index:D4}.json"),
                StringComparer.Ordinal))
        {
            throw new InvalidDataException("phase_fragment_journal_invalid");
        }
        var fragments = new List<PhaseFragmentReference>();
        var sources = new List<CaptureManifestSource>();
        string? predecessor = null;
        foreach (var name in names)
        {
            using var lease = root.AcquirePinnedFile(
                $"{packageName}/{name}",
                EvidenceLimits.MaximumDocumentBytes);
            PhaseFragmentDocument fragment;
            try
            {
                fragment = System.Text.Json.JsonSerializer.Deserialize<PhaseFragmentDocument>(
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
            }
            catch (System.Text.Json.JsonException)
            {
                throw new InvalidDataException("phase_fragment_journal_invalid");
            }
            var source = new CaptureManifestSource(
                fragment.SourceId,
                fragment.OperationId,
                fragment.Phase,
                fragment.Endpoint,
                fragment.Page,
                fragment.Status,
                fragment.BodyPath,
                fragment.BodySha256,
                fragment.BodySize,
                fragment.BodyPhysicalIdentitySha256,
                fragment.SafeHeadersSha256,
                fragment.RequestStartedUnixMilliseconds,
                fragment.ResponseReceivedUnixMilliseconds,
                fragment.NextRoute);
            ValidateFragment(
                fragment,
                root.DestinationIdentitySha256,
                operationIds,
                executionAuthorizationSha256,
                materializerSourceSha256,
                materializerBuildSha256,
                producerJournalSealSha256,
                source,
                fragments.Count + 1,
                predecessor);
            using var bodyLease = root.AcquirePinnedFile(
                $"{packageName}/{source.BodyPath}",
                EvidenceLimits.MaximumDocumentBytes);
            if (CanonicalEvidence.Sha256(bodyLease.Bytes) != source.BodySha256 ||
                bodyLease.Bytes.Length.ToString() != source.BodySize ||
                bodyLease.Identity != source.BodyFileIdentity)
            {
                throw new InvalidDataException("phase_fragment_journal_invalid");
            }
            var digest = CanonicalEvidence.Sha256(lease.Bytes);
            fragments.Add(new PhaseFragmentReference(
                fragments.Count + 1,
                name!,
                digest,
                lease.Identity));
            sources.Add(source);
            predecessor = digest;
        }
        if (sources.Select(item => item.SourceId).Distinct(StringComparer.Ordinal).Count() != sources.Count)
        {
            throw new InvalidDataException("phase_fragment_journal_invalid");
        }
        return new PhaseFragmentPartialJournal([.. fragments], [.. sources]);
    }

    public static PhaseFragmentReference AppendCreateNew(
        RestrictedEvidenceRoot root,
        string packageName,
        IReadOnlyList<string> operationIds,
        string executionAuthorizationSha256,
        string materializerSourceSha256,
        string materializerBuildSha256,
        string producerJournalSealSha256,
        int sequence,
        string? predecessorSha256,
        CaptureManifestSource source)
    {
        var relativePath = $"{packageName}/phase-fragment-{sequence:D4}.json";
        var fragment = new PhaseFragmentDocument(
            FragmentKind,
            root.DestinationIdentitySha256,
            executionAuthorizationSha256,
            source.OperationId,
            materializerSourceSha256,
            materializerBuildSha256,
            producerJournalSealSha256,
            sequence,
            source.Phase,
            predecessorSha256,
            source.SourceId,
            source.Route,
            source.Page,
            source.Status,
            source.BodyPath,
            source.RequestStartedUnixMilliseconds,
            source.ResponseReceivedUnixMilliseconds,
            source.BodySha256,
            source.BodySize,
            source.SafeHeadersSha256,
            source.BodyFileIdentity,
            source.NextRoute,
            Finalized: true);
        ValidateFragment(
            fragment,
            root.DestinationIdentitySha256,
            operationIds,
            executionAuthorizationSha256,
            materializerSourceSha256,
            materializerBuildSha256,
            producerJournalSealSha256,
            source,
            sequence,
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
        string executionAuthorizationSha256,
        string materializerSourceSha256,
        string materializerBuildSha256,
        string producerJournalSealSha256,
        IReadOnlyList<PhaseFragmentReference> fragments,
        IReadOnlyList<CaptureManifestSource> sources)
    {
        ValidateReferences(
            root,
            packageName,
            operationIds,
            executionAuthorizationSha256,
            materializerSourceSha256,
            materializerBuildSha256,
            producerJournalSealSha256,
            fragments,
            sources);
        var document = new PhaseFragmentJournalDocument(
            JournalKind,
            root.DestinationIdentitySha256,
            executionAuthorizationSha256,
            [.. operationIds],
            materializerSourceSha256,
            materializerBuildSha256,
            producerJournalSealSha256,
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
                    value.ExecutionAuthorizationSha256 != capture.ExecutionAuthorizationSha256 ||
                    value.ProducerJournalSealSha256 != capture.ProducerJournalSealSha256 ||
                    !Sha256(value.MaterializerSourceSha256) ||
                    !Sha256(value.MaterializerBuildSha256) ||
                    !value.OperationIds.SequenceEqual(capture.OperationIds, StringComparer.Ordinal))
                {
                    throw new InvalidDataException("phase_fragment_journal_invalid");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonical);
            }
            ValidateReferences(
                root,
                packageName,
                capture.OperationIds,
                value.ExecutionAuthorizationSha256,
                value.MaterializerSourceSha256,
                value.MaterializerBuildSha256,
                value.ProducerJournalSealSha256,
                value.Fragments,
                capture.Sources);
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
        string executionAuthorizationSha256,
        string materializerSourceSha256,
        string materializerBuildSha256,
        string producerJournalSealSha256,
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
                ValidateFragment(
                    fragment,
                    root.DestinationIdentitySha256,
                    operationIds,
                    executionAuthorizationSha256,
                    materializerSourceSha256,
                    materializerBuildSha256,
                    producerJournalSealSha256,
                    source,
                    index + 1,
                    predecessor);
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
        string executionAuthorizationSha256,
        string materializerSourceSha256,
        string materializerBuildSha256,
        string producerJournalSealSha256,
        CaptureManifestSource source,
        int sequence,
        string? predecessorSha256)
    {
        if (value.Kind != FragmentKind || !value.Finalized ||
            value.DestinationIdentitySha256 != destinationIdentitySha256 ||
            value.ExecutionAuthorizationSha256 != executionAuthorizationSha256 ||
            value.OperationId != source.OperationId ||
            !operationIds.Contains(value.OperationId, StringComparer.Ordinal) ||
            value.MaterializerSourceSha256 != materializerSourceSha256 ||
            value.MaterializerBuildSha256 != materializerBuildSha256 ||
            value.ProducerJournalSealSha256 != producerJournalSealSha256 ||
            !Sha256(value.MaterializerSourceSha256) || !Sha256(value.MaterializerBuildSha256) ||
            value.Sequence != sequence || value.Phase != source.Phase ||
            value.PredecessorSha256 != predecessorSha256 || value.SourceId != source.SourceId ||
            value.Endpoint != source.Route || value.Page != source.Page || value.Status != source.Status ||
            value.BodyPath != source.BodyPath || value.BodySize != source.BodySize ||
            value.RequestStartedUnixMilliseconds != source.RequestStartedUnixMilliseconds ||
            value.ResponseReceivedUnixMilliseconds != source.ResponseReceivedUnixMilliseconds ||
            value.BodySha256 != source.BodySha256 || value.SafeHeadersSha256 != source.SafeHeadersSha256 ||
            value.BodyPhysicalIdentitySha256 != source.BodyFileIdentity ||
            value.NextRoute != source.NextRoute ||
            (predecessorSha256 is not null && !Sha256(predecessorSha256)))
        {
            throw new InvalidDataException("phase_fragment_journal_invalid");
        }
    }

    private static bool Sha256(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
