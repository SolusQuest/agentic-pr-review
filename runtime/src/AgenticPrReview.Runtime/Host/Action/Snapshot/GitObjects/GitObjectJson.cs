using System.Text.Json.Serialization;

namespace AgenticPrReview.Runtime.ActionHost.Snapshot.GitObjects;

internal sealed class ReviewedGitCommitDocument
{
    [JsonPropertyName("sha")]
    public string? Sha { get; set; }

    [JsonPropertyName("tree")]
    public ReviewedGitCommitTreeDocument? Tree { get; set; }
}

internal sealed class ReviewedGitCommitTreeDocument
{
    [JsonPropertyName("sha")]
    public string? Sha { get; set; }
}

internal sealed class ReviewedGitTreeDocument
{
    [JsonPropertyName("sha")]
    public string? Sha { get; set; }

    [JsonPropertyName("truncated")]
    [JsonRequired]
    public bool Truncated { get; set; }

    [JsonPropertyName("tree")]
    public ReviewedGitTreeEntryDocument[]? Entries { get; set; }
}

internal sealed class ReviewedGitTreeEntryDocument
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("sha")]
    public string? Sha { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    AllowDuplicateProperties = false,
    MaxDepth = 32)]
[JsonSerializable(typeof(ReviewedGitCommitDocument))]
[JsonSerializable(typeof(ReviewedGitTreeDocument))]
[JsonSerializable(typeof(ReviewedGitTreeEntryDocument))]
[JsonSerializable(typeof(ReviewedGitTreeEntryDocument[]))]
internal sealed partial class ReviewedGitObjectJsonContext :
    JsonSerializerContext;

internal static class ReviewedGitObjectDocumentMapper
{
    internal static bool TryMap(
        ReviewedGitCommitDocument? document,
        string expectedSha,
        out ReviewedGitCommitFact? fact)
    {
        fact = null;
        if (document?.Tree is null ||
            !StringComparer.Ordinal.Equals(document.Sha, expectedSha) ||
            !ReviewedGitObjectValidation.IsSha(document.Sha) ||
            !ReviewedGitObjectValidation.IsSha(document.Tree.Sha))
        {
            return false;
        }

        fact = new ReviewedGitCommitFact(
            document.Sha!,
            document.Tree.Sha!);
        return true;
    }

    internal static bool TryMap(
        ReviewedGitTreeDocument? document,
        string expectedSha,
        out ReviewedGitTreeFact? fact)
    {
        fact = null;
        if (document is null ||
            document.Truncated ||
            document.Entries is null ||
            !StringComparer.Ordinal.Equals(document.Sha, expectedSha) ||
            !ReviewedGitObjectValidation.IsSha(document.Sha))
        {
            return false;
        }

        var entries = new ReviewedGitTreeEntryFact[document.Entries.Length];
        for (var index = 0; index < document.Entries.Length; index++)
        {
            var entry = document.Entries[index];
            if (entry is null ||
                entry.Path is null ||
                entry.Mode is null ||
                entry.Type is null ||
                !ReviewedGitObjectValidation.IsSha(entry.Sha))
            {
                return false;
            }

            entries[index] = new ReviewedGitTreeEntryFact(
                entry.Path,
                entry.Mode,
                entry.Type,
                entry.Sha!,
                entry.Size);
        }

        fact = new ReviewedGitTreeFact(document.Sha!, entries);
        return true;
    }
}
