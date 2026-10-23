// Historical bridge fixture only; not compiled into the production runtime.
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Host.State.GitHubArtifacts;
using AgenticPrReview.Runtime.ActionHost.Contracts;

#if APR_HISTORICAL_UNIT_BRIDGE
namespace AgenticPrReview.Runtime.HistoricalUnit;
#elif APR_HISTORICAL_FRAMEWORK_BRIDGE
namespace AgenticPrReview.Runtime.HistoricalFramework;
#else
namespace AgenticPrReview.Runtime.Host.State.GitHubArtifacts;
#endif

[JsonSerializable(typeof(
    ActionHostPrivateCommandEnvelope<
        ArtifactBridgeListExactCommandDocument>))]
[JsonSerializable(typeof(
    ActionHostPrivateCommandEnvelope<
        ArtifactBridgeMetadataCommandDocument>))]
[JsonSerializable(typeof(
    ActionHostPrivateCommandEnvelope<
        ArtifactBridgeDownloadCommandDocument>))]
[JsonSerializable(typeof(
    ActionHostPrivateCommandEnvelope<
        ArtifactBridgeUploadCommandDocument>))]
[JsonSerializable(typeof(
    ActionHostPrivateCommandEnvelope<
        ArtifactBridgeReadBackCommandDocument>))]
[JsonSerializable(typeof(
    ActionHostPrivateCommandEnvelope<
        ArtifactBridgeDeleteCommandDocument>))]
[JsonSerializable(typeof(
    ActionHostPrivateCommandResultEnvelope<ArtifactBridgeResultDocument>))]
[JsonSerializable(typeof(ActionHostPrivateCommandResultEnvelope<ArtifactUploadResultDocument>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    GenerationMode = JsonSourceGenerationMode.Default)]
internal sealed partial class ArtifactBridgeJsonContext : JsonSerializerContext;
