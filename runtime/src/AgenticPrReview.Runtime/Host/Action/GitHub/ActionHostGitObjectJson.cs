using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AgenticPrReview.Runtime.ActionHost.GitHub;

internal sealed class ActionHostGitCommitDocument
{
    [JsonRequired]
    [JsonPropertyName("sha")]
    public string? Sha { get; set; }

    [JsonRequired]
    [JsonPropertyName("tree")]
    public ActionHostGitObjectIdentityDocument? Tree { get; set; }
}

internal sealed class ActionHostGitObjectIdentityDocument
{
    [JsonRequired]
    [JsonPropertyName("sha")]
    public string? Sha { get; set; }
}

internal sealed class ActionHostGitTreeDocument
{
    [JsonRequired]
    [JsonPropertyName("sha")]
    public string? Sha { get; set; }

    [JsonRequired]
    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    [JsonRequired]
    [JsonPropertyName("tree")]
    public ActionHostGitTreeEntryDocument[]? Tree { get; set; }
}

internal sealed class ActionHostGitTreeEntryDocument
{
    [JsonRequired]
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonRequired]
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonRequired]
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonRequired]
    [JsonPropertyName("sha")]
    public string? Sha { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }
}

internal sealed class ActionHostGitBlobDocument
{
    [JsonRequired]
    [JsonPropertyName("sha")]
    public string? Sha { get; set; }

    [JsonRequired]
    [JsonPropertyName("size")]
    public int Size { get; set; }

    [JsonRequired]
    [JsonPropertyName("encoding")]
    public string? Encoding { get; set; }

    [JsonRequired]
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    AllowDuplicateProperties = false,
    MaxDepth = 32)]
[JsonSerializable(typeof(ActionHostGitCommitDocument))]
[JsonSerializable(typeof(ActionHostGitObjectIdentityDocument))]
[JsonSerializable(typeof(ActionHostGitTreeDocument))]
[JsonSerializable(typeof(ActionHostGitTreeEntryDocument))]
[JsonSerializable(typeof(ActionHostGitTreeEntryDocument[]))]
[JsonSerializable(typeof(ActionHostGitBlobDocument))]
internal sealed partial class ActionHostGitObjectJsonContext :
    JsonSerializerContext;

internal static class ActionHostGitObjectMapper
{
    private const int MaximumSharedTreeEntries = 20_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool TryMap(
        ActionHostGitCommitDocument? document,
        string expectedSha,
        out ActionHostGitCommitObject? value)
    {
        value = null;
        if (document is null ||
            !StringComparer.Ordinal.Equals(document.Sha, expectedSha) ||
            document.Tree is null ||
            !IsSha(document.Tree.Sha))
        {
            return false;
        }

        value = new(document.Sha!, document.Tree.Sha!);
        return true;
    }

    internal static bool TryMap(
        ActionHostGitTreeDocument? document,
        string expectedSha,
        out ActionHostGitTreeObject? value)
    {
        value = null;
        if (document is null ||
            !StringComparer.Ordinal.Equals(document.Sha, expectedSha) ||
            document.Truncated ||
            document.Tree is null ||
            document.Tree.Length > MaximumSharedTreeEntries)
        {
            return false;
        }

        var entries = new List<ActionHostGitTreeEntryObject>(
            document.Tree.Length);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document.Tree)
        {
            if (entry is null ||
                !IsTreeEntryPath(entry.Path) ||
                !IsModeAndType(entry.Mode, entry.Type) ||
                !IsSha(entry.Sha) ||
                !paths.Add(entry.Path!))
            {
                return false;
            }

            entries.Add(new(
                entry.Path!,
                entry.Mode!,
                entry.Type!,
                entry.Sha!,
                entry.Size));
        }

        value = new(document.Sha!, entries);
        return true;
    }

    internal static bool TryMap(
        ActionHostGitBlobDocument? document,
        string expectedSha,
        ActionHostGitBlobReadBudget budget,
        out ActionHostGitBlobObject? value)
    {
        value = null;
        if (document is null ||
            budget is null ||
            !StringComparer.Ordinal.Equals(document.Sha, expectedSha) ||
            document.Size < 0 ||
            document.Size > budget.MaximumDecodedBytes ||
            !StringComparer.Ordinal.Equals(document.Encoding, "base64") ||
            !ActionHostGitHubBase64.TryDecode(
                document.Content,
                budget.MaximumEncodedCharacters,
                budget.MaximumDecodedBytes,
                out var bytes) ||
            bytes.Length != document.Size ||
            !StringComparer.Ordinal.Equals(GitBlobSha(bytes), expectedSha))
        {
            return false;
        }

        value = new(expectedSha, bytes);
        return true;
    }

    internal static bool IsSha(string? value) =>
        value is { Length: 40 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsTreeEntryPath(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('/'))
        {
            return false;
        }

        try
        {
            return StrictUtf8.GetByteCount(value) <= 4096;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsModeAndType(string? mode, string? type) =>
        (StringComparer.Ordinal.Equals(mode, "040000") &&
            StringComparer.Ordinal.Equals(type, "tree")) ||
        (StringComparer.Ordinal.Equals(mode, "100644") &&
            StringComparer.Ordinal.Equals(type, "blob")) ||
        (StringComparer.Ordinal.Equals(mode, "100755") &&
            StringComparer.Ordinal.Equals(type, "blob")) ||
        (StringComparer.Ordinal.Equals(mode, "120000") &&
            StringComparer.Ordinal.Equals(type, "blob")) ||
        (StringComparer.Ordinal.Equals(mode, "160000") &&
            StringComparer.Ordinal.Equals(type, "commit"));

    private static string GitBlobSha(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes(
            "blob " + bytes.Length.ToString(CultureInfo.InvariantCulture) +
            "\0");
        return Convert.ToHexStringLower(
            SHA1.HashData([.. header, .. bytes]));
    }
}

internal static class ActionHostGitHubBase64
{
    internal static bool TryDecode(
        string? content,
        int maximumEncodedCharacters,
        int maximumDecodedBytes,
        out byte[] bytes)
    {
        bytes = [];
        if (content is null ||
            maximumEncodedCharacters < 0 ||
            maximumDecodedBytes < 0)
        {
            return false;
        }

        var canonical = new char[Math.Min(
            content.Length,
            maximumEncodedCharacters)];
        var count = 0;
        for (var index = 0; index < content.Length; index++)
        {
            var character = content[index];
            if (character == '\r')
            {
                if (index + 1 >= content.Length ||
                    content[index + 1] != '\n' ||
                    count == 0 ||
                    count % 4 != 0)
                {
                    return false;
                }

                index++;
                continue;
            }

            if (character == '\n')
            {
                if (count == 0 || count % 4 != 0)
                {
                    return false;
                }

                continue;
            }

            if (!(character is >= 'A' and <= 'Z' or
                    >= 'a' and <= 'z' or
                    >= '0' and <= '9' or '+' or '/' or '='))
            {
                return false;
            }

            if (count >= maximumEncodedCharacters)
            {
                return false;
            }

            canonical[count++] = character;
        }

        if (count % 4 != 0 ||
            checked((long)(count / 4) * 3) >
                (long)maximumDecodedBytes + 2)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64CharArray(canonical, 0, count);
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }

        return bytes.Length <= maximumDecodedBytes &&
            Convert.ToBase64String(bytes).AsSpan()
                .SequenceEqual(canonical.AsSpan(0, count));
    }
}
