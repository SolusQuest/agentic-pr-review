using System.Text.Json.Serialization;

namespace AgenticPrReview.Runtime.ActionHost.GitHub;

internal sealed class ActionHostPullRequestFileDocument
{
    [JsonRequired]
    [JsonPropertyName("sha")]
    public string? Sha { get; set; }

    [JsonRequired]
    [JsonPropertyName("filename")]
    public string? FileName { get; set; }

    [JsonPropertyName("previous_filename")]
    public string? PreviousFileName { get; set; }

    [JsonRequired]
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonRequired]
    [JsonPropertyName("additions")]
    public int Additions { get; set; }

    [JsonRequired]
    [JsonPropertyName("deletions")]
    public int Deletions { get; set; }

    [JsonRequired]
    [JsonPropertyName("changes")]
    public int Changes { get; set; }

    [JsonPropertyName("patch")]
    public string? Patch { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    AllowDuplicateProperties = false,
    MaxDepth = 32)]
[JsonSerializable(typeof(ActionHostPullRequestFileDocument))]
[JsonSerializable(typeof(ActionHostPullRequestFileDocument[]))]
internal sealed partial class ActionHostReviewedSnapshotJsonContext :
    JsonSerializerContext;

internal static class ActionHostReviewedSnapshotMapper
{
    internal static bool TryMap(
        ActionHostPullRequestFileDocument? document,
        out ActionHostPullRequestFileObject? value)
    {
        value = null;
        if (document is null ||
            !ActionHostGitObjectMapper.IsSha(document.Sha) ||
            string.IsNullOrEmpty(document.FileName) ||
            document.FileName.Length > 4_096 ||
            document.PreviousFileName is { Length: > 4_096 } ||
            string.IsNullOrEmpty(document.Status) ||
            document.Status.Length > 32 ||
            document.Additions < 0 ||
            document.Deletions < 0 ||
            document.Changes < 0 ||
            document.Patch is { Length: > 1024 * 1024 })
        {
            return false;
        }

        value = new(
            document.Sha!,
            document.FileName,
            document.PreviousFileName,
            document.Status,
            document.Additions,
            document.Deletions,
            document.Changes,
            document.Patch);
        return true;
    }
}
