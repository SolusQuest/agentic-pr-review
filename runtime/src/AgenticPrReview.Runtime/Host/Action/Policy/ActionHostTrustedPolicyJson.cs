using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Agent;

namespace AgenticPrReview.Runtime.ActionHost.Policy;

internal sealed class ActionHostTrustedPolicyDocument
{
    [JsonRequired]
    [JsonPropertyName("schema")]
    public string? Schema { get; set; }

    [JsonRequired]
    [JsonPropertyName("instructionsPath")]
    public string? InstructionsPath { get; set; }

    [JsonRequired]
    [JsonPropertyName("publication")]
    public ActionHostPublicationDocument? Publication { get; set; }

    // An omitted object gets defaults; an explicit null remains invalid.
    [JsonPropertyName("review")]
    public ActionHostReviewDocument? Review { get; set; } = new();
}

internal sealed class ActionHostReviewDocument
{
    [JsonPropertyName("maxModelCalls")]
    public int MaxModelCalls { get; set; } = AgentLimits.ModelCalls;

    [JsonPropertyName("maxUncachedInputTokens")]
    public long MaxUncachedInputTokens { get; set; } = AgentLimits.InputTokens;

    [JsonPropertyName("maxCachedInputTokens")]
    public long MaxCachedInputTokens { get; set; } = AgentLimits.CachedInputTokens;

    [JsonPropertyName("maxOutputTokens")]
    public long MaxOutputTokens { get; set; } = AgentLimits.OutputTokens;

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = AgentLimits.DeadlineSeconds;
}

internal sealed class ActionHostPublicationDocument
{
    private string? _inlineMinSeverity;

    [JsonRequired]
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("inlineMinSeverity")]
    public string? InlineMinSeverity
    {
        get => _inlineMinSeverity;
        set
        {
            InlineMinSeverityPresent = true;
            _inlineMinSeverity = value;
        }
    }

    [JsonIgnore]
    public bool InlineMinSeverityPresent { get; private set; }
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    MaxDepth = 8)]
[JsonSerializable(typeof(ActionHostTrustedPolicyDocument))]
[JsonSerializable(typeof(ActionHostPublicationDocument))]
[JsonSerializable(typeof(ActionHostReviewDocument))]
internal sealed partial class ActionHostTrustedPolicyJsonContext :
    JsonSerializerContext;
