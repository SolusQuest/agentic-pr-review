using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofCapture;

public sealed class CapturePackageWriter
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly RestrictedEvidenceRoot root;
    private readonly string packagePath;
    private readonly List<CaptureManifestSource> sources = [];
    private readonly List<CaptureManifestArtifact> artifacts = [];
    private bool finalized;

    public CapturePackageWriter(
        RestrictedEvidenceRoot root,
        string packageName)
    {
        this.root = root;
        if (!BoundedText(packageName, EvidenceLimits.MaximumNameBytes) ||
            !RestrictedEvidenceRoot.IsSinglePathSegment(packageName))
        {
            throw new InvalidDataException("capture_package_name_invalid");
        }

        packagePath = RestrictedEvidenceRoot.ResolveChildPath(root.Path, packageName);
        if (!RestrictedEvidenceRoot.IsWithin(packagePath, root.Path) ||
            Directory.Exists(packagePath) || File.Exists(packagePath))
        {
            throw new InvalidDataException("capture_package_exists");
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(packagePath);
        }
        else
        {
            Directory.CreateDirectory(
                packagePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void AddSource(string sourceId, SafeResponseCapture capture, ReadOnlySpan<byte> body)
    {
        if (finalized ||
            sources.Count >= EvidenceLimits.MaximumRecords ||
            !BoundedText(sourceId, EvidenceLimits.MaximumNameBytes) ||
            sources.Any(item => StringComparer.Ordinal.Equals(item.SourceId, sourceId)) ||
            !BoundedText(capture.Route, EvidenceLimits.MaximumRelativePathBytes) ||
            capture.Page < 1 ||
            capture.Status != 200 ||
            body.Length is < 1 or > EvidenceLimits.MaximumDocumentBytes ||
            !StringComparer.Ordinal.Equals(CanonicalEvidence.Sha256(body), capture.BodySha256) ||
            body.Length != capture.BodySize ||
            !Sha256(capture.SafeHeadersSha256) ||
            capture.ResponseReceivedUnixMilliseconds < capture.RequestStartedUnixMilliseconds)
        {
            throw new InvalidDataException("capture_source_invalid");
        }

        var bodyName = $"source-{sources.Count + 1:D4}.json";
        var bodyPath = RestrictedEvidenceRoot.ResolveChildPath(packagePath, bodyName);
        CanonicalEvidence.WriteCreateNew(bodyPath, body);
        var pinned = ReadPinned(bodyPath, EvidenceLimits.MaximumDocumentBytes);
        var reopened = pinned.Bytes;
        try
        {
            if (!StringComparer.Ordinal.Equals(CanonicalEvidence.Sha256(reopened), capture.BodySha256))
            {
                throw new InvalidDataException("capture_source_reopen_invalid");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(reopened);
        }

        sources.Add(new CaptureManifestSource(
            sourceId,
            capture.Route,
            capture.Page,
            capture.Status,
            bodyName,
            capture.BodySha256,
            capture.BodySize.ToString(),
            pinned.Identity,
            capture.SafeHeadersSha256,
            capture.RequestStartedUnixMilliseconds,
            capture.ResponseReceivedUnixMilliseconds,
            capture.NextRoute));
    }

    public void AddArtifact(
        string artifactId,
        string artifactName,
        string metadataSourceId,
        string metadataBodySha256,
        ReadOnlySpan<byte> archive,
        string expectedArchiveSha256,
        string expectedRunId,
        string expectedRunAttempt,
        SafeResponseCapture downloadCapture)
    {
        if (finalized ||
            artifacts.Count >= EvidenceLimits.MaximumRecords ||
            !PositiveDecimal(artifactId) ||
            !BoundedText(artifactName, EvidenceLimits.MaximumNameBytes) ||
            !BoundedText(metadataSourceId, EvidenceLimits.MaximumNameBytes) ||
            !Sha256(metadataBodySha256) ||
            !BoundedText(downloadCapture.Route, EvidenceLimits.MaximumRelativePathBytes) ||
            downloadCapture.Status != 200 ||
            !StringComparer.Ordinal.Equals(downloadCapture.BodySha256, expectedArchiveSha256) ||
            downloadCapture.BodySize != archive.Length ||
            !Sha256(downloadCapture.SafeHeadersSha256) ||
            downloadCapture.ResponseReceivedUnixMilliseconds <
                downloadCapture.RequestStartedUnixMilliseconds ||
            artifacts.Any(item =>
                StringComparer.Ordinal.Equals(item.ArtifactId, artifactId) ||
                StringComparer.OrdinalIgnoreCase.Equals(item.ArtifactName, artifactName)))
        {
            throw new InvalidDataException("capture_artifact_invalid");
        }

        var admitted = ArtifactArchiveAdmission.Admit(
            archive,
            expectedArchiveSha256,
            expectedRunId,
            expectedRunAttempt);
        try
        {
            var archiveName = $"artifact-{artifactId}.zip";
            var objectName = $"artifact-{artifactId}.bin";
            var archivePath = RestrictedEvidenceRoot.ResolveChildPath(packagePath, archiveName);
            var objectPath = RestrictedEvidenceRoot.ResolveChildPath(packagePath, objectName);
            CanonicalEvidence.WriteCreateNew(archivePath, archive);
            CanonicalEvidence.WriteCreateNew(objectPath, admitted.EncryptedObject);
            var pinnedArchive = ReadPinned(archivePath, EvidenceLimits.MaximumArchiveBytes);
            var pinnedObject = ReadPinned(objectPath, EvidenceLimits.MaximumEncryptedObjectBytes);
            try
            {
                if (!StringComparer.Ordinal.Equals(CanonicalEvidence.Sha256(pinnedArchive.Bytes), admitted.ArchiveSha256) ||
                    !StringComparer.Ordinal.Equals(CanonicalEvidence.Sha256(pinnedObject.Bytes), admitted.EncryptedObjectSha256))
                {
                    throw new InvalidDataException("capture_artifact_reopen_invalid");
                }

                artifacts.Add(new CaptureManifestArtifact(
                    artifactId,
                    artifactName,
                    metadataSourceId,
                    metadataBodySha256,
                    admitted.ProducingRunId,
                    admitted.ProducingRunAttempt,
                    downloadCapture.Route,
                    downloadCapture.SafeHeadersSha256,
                    downloadCapture.RequestStartedUnixMilliseconds,
                    downloadCapture.ResponseReceivedUnixMilliseconds,
                    archiveName,
                    admitted.ArchiveSha256,
                    archive.Length.ToString(),
                    pinnedArchive.Identity,
                    objectName,
                    admitted.EncryptedObjectSha256,
                    admitted.EncryptedObject.Length.ToString(),
                    pinnedObject.Identity));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pinnedArchive.Bytes);
                CryptographicOperations.ZeroMemory(pinnedObject.Bytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(admitted.EncryptedObject);
        }
    }

    public (string Path, string Sha256) Finalize(
        string repositoryId,
        string repository,
        string[] operationIds,
        string sourceMapSha256)
    {
        if (finalized || sources.Count == 0 || artifacts.Count == 0 ||
            !PositiveDecimal(repositoryId) ||
            !BoundedText(repository, EvidenceLimits.MaximumRelativePathBytes) ||
            operationIds.Length != 2 ||
            operationIds.Distinct(StringComparer.Ordinal).Count() != 2 ||
            operationIds.Any(item => !Sha256(item)) ||
            !Sha256(sourceMapSha256))
        {
            throw new InvalidDataException("capture_manifest_invalid");
        }

        foreach (var source in sources)
        {
            var sourcePath = RestrictedEvidenceRoot.ResolveChildPath(packagePath, source.BodyPath);
            var pinned = ReadPinned(sourcePath, EvidenceLimits.MaximumDocumentBytes);
            try
            {
                if (!StringComparer.Ordinal.Equals(CanonicalEvidence.Sha256(pinned.Bytes), source.BodySha256) ||
                    !StringComparer.Ordinal.Equals(pinned.Identity, source.BodyFileIdentity))
                {
                    throw new InvalidDataException("capture_manifest_reopen_invalid");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pinned.Bytes);
            }
        }
        foreach (var artifact in artifacts)
        {
            var archivePath = RestrictedEvidenceRoot.ResolveChildPath(
                packagePath,
                artifact.ArchivePath);
            var objectPath = RestrictedEvidenceRoot.ResolveChildPath(
                packagePath,
                artifact.EncryptedObjectPath);
            var pinnedArchive = ReadPinned(archivePath, EvidenceLimits.MaximumArchiveBytes);
            var pinnedObject = ReadPinned(objectPath, EvidenceLimits.MaximumEncryptedObjectBytes);
            try
            {
                if (!StringComparer.Ordinal.Equals(CanonicalEvidence.Sha256(pinnedArchive.Bytes), artifact.ArchiveSha256) ||
                    !StringComparer.Ordinal.Equals(pinnedArchive.Identity, artifact.ArchiveFileIdentity) ||
                    !StringComparer.Ordinal.Equals(CanonicalEvidence.Sha256(pinnedObject.Bytes), artifact.EncryptedObjectSha256) ||
                    !StringComparer.Ordinal.Equals(pinnedObject.Identity, artifact.EncryptedObjectFileIdentity))
                {
                    throw new InvalidDataException("capture_manifest_reopen_invalid");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pinnedArchive.Bytes);
                CryptographicOperations.ZeroMemory(pinnedObject.Bytes);
            }
        }

        var document = new CaptureManifestDocument(
            "apr-r4-e3-capture-manifest-v1",
            repositoryId,
            repository,
            operationIds,
            sourceMapSha256,
            root.DestinationIdentitySha256,
            sources.ToArray(),
            artifacts.ToArray(),
            Finalized: true);
        var bytes = CanonicalEvidence.Encode(document, EvidenceJson.Options);
        try
        {
            var manifestPath = RestrictedEvidenceRoot.ResolveChildPath(
                packagePath,
                "capture-manifest.json");
            CanonicalEvidence.WriteCreateNew(manifestPath, bytes);
            var digest = CanonicalEvidence.Sha256(bytes);
            if (!ReopenedDigestEquals(manifestPath, digest))
            {
                throw new InvalidDataException("capture_manifest_reopen_invalid");
            }

            finalized = true;
            return (manifestPath, digest);
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

    private bool ReopenedDigestEquals(string path, string expected)
    {
        var pinned = ReadPinned(path, EvidenceLimits.MaximumDocumentBytes);
        var bytes = pinned.Bytes;
        try
        {
            return StringComparer.Ordinal.Equals(CanonicalEvidence.Sha256(bytes), expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private PinnedEvidenceFile ReadPinned(string path, int maximumBytes) =>
        root.ReadPinnedFile(System.IO.Path.GetRelativePath(root.Path, path), maximumBytes);

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
