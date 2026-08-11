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
    [JsonPropertyName("id")] public long? Id { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("documentation_url")]
    public string? DocumentationUrl { get; init; }
    [JsonPropertyName("errors")]
    public BoundedGitHubErrorItemDocument[]? Errors { get; init; }
}

internal sealed record BoundedGitHubErrorItemDocument
{
    [JsonPropertyName("resource")] public string? Resource { get; init; }
    [JsonPropertyName("field")] public string? Field { get; init; }
    [JsonPropertyName("code")] public string? Code { get; init; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false,
    AllowDuplicateProperties = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    MaxDepth = 32)]
[JsonSerializable(typeof(BoundedGitHubIssueCommentDocument))]
[JsonSerializable(typeof(BoundedGitHubIssueCommentDocument[]))]
[JsonSerializable(typeof(BoundedGitHubErrorDocument))]
[JsonSerializable(typeof(BoundedGitHubErrorItemDocument))]
internal sealed partial class BoundedGitHubPublisherJsonContext :
    JsonSerializerContext;
