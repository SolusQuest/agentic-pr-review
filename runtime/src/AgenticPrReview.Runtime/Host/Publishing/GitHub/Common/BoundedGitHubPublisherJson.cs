using System.Text.Json.Serialization;

namespace AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;

internal sealed record BoundedGitHubIssueCommentDocument
{
    [JsonPropertyName("id")] public long? Id { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }
    [JsonPropertyName("body")] public string? Body { get; init; }
}

internal sealed record BoundedGitHubReviewCommentDocument
{
    [JsonPropertyName("id")] public long? Id { get; init; }
    [JsonPropertyName("pull_request_review_id")]
    public long? PullRequestReviewId { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("pull_request_url")]
    public string? PullRequestUrl { get; init; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }
    [JsonPropertyName("body")] public string? Body { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("line")] public int? Line { get; init; }
    [JsonPropertyName("side")] public string? Side { get; init; }
    [JsonPropertyName("commit_id")] public string? CommitId { get; init; }
}

internal sealed record BoundedGitHubPullRequestReviewDocument
{
    [JsonPropertyName("id")] public long? Id { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("pull_request_url")]
    public string? PullRequestUrl { get; init; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }
    [JsonPropertyName("commit_id")] public string? CommitId { get; init; }
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
[JsonSerializable(typeof(BoundedGitHubReviewCommentDocument))]
[JsonSerializable(typeof(BoundedGitHubReviewCommentDocument[]))]
[JsonSerializable(typeof(BoundedGitHubPullRequestReviewDocument))]
[JsonSerializable(typeof(BoundedGitHubErrorDocument))]
[JsonSerializable(typeof(BoundedGitHubErrorItemDocument))]
internal sealed partial class BoundedGitHubPublisherJsonContext :
    JsonSerializerContext;
