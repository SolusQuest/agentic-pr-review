// Historical bridge fixture only; not compiled into the production runtime.
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Host.State.GitHubArtifacts;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

#if APR_HISTORICAL_UNIT_BRIDGE
namespace AgenticPrReview.Runtime.HistoricalUnit;
#elif APR_HISTORICAL_FRAMEWORK_BRIDGE
namespace AgenticPrReview.Runtime.HistoricalFramework;
#else
namespace AgenticPrReview.Runtime.Host.State.GitHubArtifacts;
#endif

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
    string ObjectId,
    string MaximumBytes) : IArtifactBridgeCommandDocument
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


[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ArtifactUploadResultDocument(
    [property: JsonRequired] string Operation,
    [property: JsonRequired] string CorrelationId,
    [property: JsonRequired] string Dispatch,
    [property: JsonRequired] string Failure,
    [property: JsonRequired] ArtifactUploadReceiptDocument? Receipt) : IActionHostPrivateCommandResultDocument
{
    public override string ToString() => "[PRIVATE]";
}
