using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.ActionHost.Contracts;

namespace AgenticPrReview.Runtime.Host.State.GitHubArtifacts;

internal static class ArtifactBridgeLimits
{
    internal const int MaximumNameBytes = 256;
    internal const int MaximumCorrelationBytes = 256;
    internal const int MaximumRelativePathBytes = 1_024;
    internal const int MaximumEncryptedObjectBytes = 2 * 1024 * 1024;
    internal const int MaximumStagingFileBytes = 4 * 1024 * 1024;
    internal const int MaximumDocumentBytes = 256 * 1024;
    internal const int RecordsPerPage = 100;
    internal const int MaximumPages = 3;
    internal const int MaximumRecords = 256;
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan LogicalOperationTimeout =
        TimeSpan.FromSeconds(120);
}

internal interface IArtifactBridgeCommandDocument
    : IActionHostPrivateCommandDocument
{
    string Operation { get; }

    string CorrelationId { get; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ArtifactBridgeReferenceDocument(
    string Name,
    string ObjectId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ArtifactBridgeMetadataDocument(
    string Name,
    string ObjectId,
    string ProducingRunId,
    string ProducingRunAttempt,
    string ArchiveDigest,
    string EncryptedObjectDigest,
    string ExpiresAtUnixSeconds,
    string Size);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ArtifactBridgeListExactCommandDocument(
    string Operation,
    string CorrelationId,
    string Name,
    string MaximumObjects) : IArtifactBridgeCommandDocument
{
    public override string ToString() => "[PRIVATE]";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ArtifactBridgeMetadataCommandDocument(
    string Operation,
    string CorrelationId,
    string Name,
    string ObjectId) : IArtifactBridgeCommandDocument
{
    public override string ToString() => "[PRIVATE]";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ArtifactBridgeDownloadCommandDocument(
    string Operation,
    string CorrelationId,
    ArtifactBridgeMetadataDocument Expected,
    string DestinationRelativePath,
    string MaximumBytes) : IArtifactBridgeCommandDocument
{
    public override string ToString() => "[PRIVATE]";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ArtifactBridgeUploadCommandDocument(
    string Operation,
    string CorrelationId,
    string Name,
    string SourceRelativePath,
    string EncryptedObjectDigest,
    string MinimumExpiresAtUnixSeconds) : IArtifactBridgeCommandDocument
{
    public override string ToString() => "[PRIVATE]";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ArtifactBridgeReadBackCommandDocument(
    string Operation,
    string CorrelationId,
    ArtifactBridgeMetadataDocument Expected)
    : IArtifactBridgeCommandDocument
{
    public override string ToString() => "[PRIVATE]";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ArtifactBridgeDeleteCommandDocument(
    string Operation,
    string CorrelationId,
    ArtifactBridgeMetadataDocument Expected)
    : IArtifactBridgeCommandDocument
{
    public override string ToString() => "[PRIVATE]";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ArtifactBridgeResultDocument(
    string Operation,
    string CorrelationId,
    string Failure,
    string? MutationState,
    bool? Complete,
    ArtifactBridgeReferenceDocument[]? Objects,
    ArtifactBridgeMetadataDocument? Metadata)
    : IActionHostPrivateCommandResultDocument
{
    public override string ToString() => "[PRIVATE]";
}
