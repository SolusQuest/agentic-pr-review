using System.Text.Json.Serialization;

namespace AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;

internal sealed record BoundedGitHubIssueCommentDocument
{
    [JsonPropertyName("id")] public long? Id { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }
    [JsonPropertyName("body")] public string? Body { get; init; }
}

internal sealed record BoundedGitHubErrorDocument
{
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("documentation_url")]
    public string? DocumentationUrl { get; init; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false,
    AllowDuplicateProperties = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    MaxDepth = 32)]
[JsonSerializable(typeof(BoundedGitHubIssueCommentDocument))]
[JsonSerializable(typeof(BoundedGitHubIssueCommentDocument[]))]
[JsonSerializable(typeof(BoundedGitHubErrorDocument))]
internal sealed partial class BoundedGitHubPublisherJsonContext :
    JsonSerializerContext;
