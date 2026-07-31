using System.Text.Json.Serialization;

namespace AgenticPrReview.Runtime.Agent.Tools;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ListFilesArgumentsDto))]
[JsonSerializable(typeof(ReadFileArgumentsDto))]
[JsonSerializable(typeof(SearchTextArgumentsDto))]
[JsonSerializable(typeof(FinishReviewArgumentsDto))]
internal sealed partial class AgentToolJsonContext : JsonSerializerContext;
