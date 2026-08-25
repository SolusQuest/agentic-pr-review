using System.Text.Json;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

public sealed record CaptureManifestArtifact(
    string ArtifactId,
    string ArtifactName,
    string MetadataSourceId,
    string MetadataBodySha256,
    string ProducingRunId,
    string ProducingRunAttempt,
    string DownloadRoute,
    string DownloadSafeHeadersSha256,
    long DownloadRequestStartedUnixMilliseconds,
    long DownloadResponseReceivedUnixMilliseconds,
    string ArchivePath,
    string ArchiveSha256,
    string ArchiveSize,
    string ArchiveFileIdentity,
    string EncryptedObjectPath,
    string EncryptedObjectSha256,
    string EncryptedObjectSize,
    string EncryptedObjectFileIdentity);

public sealed record CaptureManifestSource(
    string SourceId,
    string Route,
    int Page,
    int Status,
    string BodyPath,
    string BodySha256,
    string BodySize,
    string BodyFileIdentity,
    string SafeHeadersSha256,
    long RequestStartedUnixMilliseconds,
    long ResponseReceivedUnixMilliseconds,
    string? NextRoute);

public sealed record CaptureManifestDocument(
    string Kind,
    string RepositoryId,
    string Repository,
    string[] OperationIds,
    string SourceMapSha256,
    string DestinationIdentitySha256,
    CaptureManifestSource[] Sources,
    CaptureManifestArtifact[] Artifacts,
    bool Finalized);

public static class EvidenceJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };
}
