using System.Text.Json.Serialization;

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
internal sealed partial class ActionHostTrustedPolicyJsonContext :
    JsonSerializerContext;
