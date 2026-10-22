using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.GitHubArtifacts;

internal static class ArtifactBridgeLimits
{
    internal const int MaximumNameBytes = 256;
    internal const int MaximumCorrelationBytes = 256;
    internal const int MaximumRelativePathBytes = 1_024;
    internal const int MaximumEncryptedObjectBytes = OpaqueStoreLimits.MaximumObjectBytes;
    internal const int MaximumStagingFileBytes = OpaqueStoreCapacity.MaximumTransportEnvelopeBytes;
    internal const int MaximumArchiveBytes = OpaqueStoreCapacity.MaximumArchiveBytes;
    internal const int MaximumDocumentBytes = 256 * 1024;
    internal const int RecordsPerPage = 100;
    internal const int MaximumPages = 3;
    internal const int MaximumRecords = 256;
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan LogicalOperationTimeout =
        TimeSpan.FromSeconds(120);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ArtifactUploadReceiptDocument(
    [property: JsonRequired] string ArtifactId,
    [property: JsonRequired] string ArchiveSha256,
    [property: JsonRequired] string ArchiveSize);
