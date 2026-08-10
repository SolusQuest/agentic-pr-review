using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.ActionHost.Contracts;

namespace AgenticPrReview.Runtime.Host.State.GitHubArtifacts;

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
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    GenerationMode = JsonSourceGenerationMode.Default)]
internal sealed partial class ArtifactBridgeJsonContext : JsonSerializerContext;
