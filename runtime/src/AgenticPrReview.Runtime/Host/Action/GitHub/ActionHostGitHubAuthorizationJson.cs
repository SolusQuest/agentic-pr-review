using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AgenticPrReview.Runtime.ActionHost.GitHub;

internal sealed class ActionHostGitHubRepositoryDocument
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("full_name")]
    public string? FullName { get; set; }

    [JsonPropertyName("default_branch")]
    public string? DefaultBranch { get; set; }
}

internal sealed class ActionHostGitHubRepositoryIdentityDocument
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("full_name")]
    public string? FullName { get; set; }
}

internal sealed class ActionHostGitHubActorDocument
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("login")]
    public string? Login { get; set; }
}

internal sealed class ActionHostGitHubPullRequestSideDocument
{
    [JsonPropertyName("sha")]
    public string? Sha { get; set; }

    [JsonPropertyName("repo")]
    public ActionHostGitHubRepositoryIdentityDocument? Repository { get; set; }
}

internal sealed class ActionHostGitHubRepositoryReferenceDocument
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

internal sealed class ActionHostGitHubPullRequestReferenceSideDocument
{
    [JsonPropertyName("sha")]
    public string? Sha { get; set; }

    [JsonPropertyName("repo")]
    public ActionHostGitHubRepositoryReferenceDocument? Repository { get; set; }
}

internal sealed class ActionHostGitHubPullRequestDocument
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("number")]
    public long Number { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("draft")]
    [JsonRequired]
    public bool Draft { get; set; }

    [JsonPropertyName("merged_at")]
    [JsonRequired]
    public DateTimeOffset? MergedAt { get; set; }

    [JsonPropertyName("base")]
    public ActionHostGitHubPullRequestSideDocument? Base { get; set; }

    [JsonPropertyName("head")]
    public ActionHostGitHubPullRequestSideDocument? Head { get; set; }
}

internal sealed class ActionHostGitHubPullRequestReferenceDocument
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("number")]
    public long Number { get; set; }

    [JsonPropertyName("base")]
    public ActionHostGitHubPullRequestReferenceSideDocument? Base { get; set; }

    [JsonPropertyName("head")]
    public ActionHostGitHubPullRequestReferenceSideDocument? Head { get; set; }
}

internal sealed class ActionHostGitHubWorkflowRunDocument
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("run_attempt")]
    public int Attempt { get; set; }

    [JsonPropertyName("workflow_id")]
    public long WorkflowId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("head_branch")]
    public string? HeadBranch { get; set; }

    [JsonPropertyName("head_sha")]
    public string? HeadSha { get; set; }

    [JsonPropertyName("event")]
    public string? Event { get; set; }

    [JsonPropertyName("conclusion")]
    public string? Conclusion { get; set; }

    [JsonPropertyName("repository")]
    public ActionHostGitHubRepositoryIdentityDocument? Repository { get; set; }

    [JsonPropertyName("head_repository")]
    public ActionHostGitHubRepositoryIdentityDocument? HeadRepository { get; set; }

    [JsonPropertyName("actor")]
    public ActionHostGitHubActorDocument? Actor { get; set; }

    [JsonPropertyName("triggering_actor")]
    public ActionHostGitHubActorDocument? TriggeringActor { get; set; }

    [JsonPropertyName("pull_requests")]
    public ActionHostGitHubPullRequestReferenceDocument[]? PullRequests
    {
        get;
        set;
    }
}

internal sealed class ActionHostGitHubContentDocument
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("encoding")]
    public string? Encoding { get; set; }

    [JsonPropertyName("size")]
    public int Size { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("sha")]
    public string? Sha { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

internal sealed class ActionHostGitHubPermissionDocument
{
    [JsonPropertyName("permission")]
    public string? Permission { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    AllowDuplicateProperties = false,
    MaxDepth = 32)]
[JsonSerializable(typeof(ActionHostGitHubRepositoryDocument))]
[JsonSerializable(typeof(ActionHostGitHubWorkflowRunDocument))]
[JsonSerializable(typeof(ActionHostGitHubContentDocument))]
[JsonSerializable(typeof(ActionHostGitHubPullRequestDocument))]
[JsonSerializable(typeof(ActionHostGitHubPullRequestDocument[]))]
[JsonSerializable(typeof(ActionHostGitHubRepositoryReferenceDocument))]
[JsonSerializable(typeof(ActionHostGitHubPullRequestReferenceSideDocument))]
[JsonSerializable(typeof(ActionHostGitHubPullRequestReferenceDocument))]
[JsonSerializable(typeof(ActionHostGitHubPullRequestReferenceDocument[]))]
[JsonSerializable(typeof(ActionHostGitHubPermissionDocument))]
internal sealed partial class ActionHostGitHubJsonContext :
    JsonSerializerContext;

internal static class ActionHostGitHubDocumentMapper
{
    internal static bool TryMap(
        ActionHostGitHubRepositoryDocument? document,
        out ActionHostGitHubRepositoryFact? fact)
    {
        fact = null;
        if (document is null ||
            document.Id <= 0 ||
            !IsRepositoryName(document.FullName) ||
            !IsName(document.DefaultBranch, 255))
        {
            return false;
        }

        fact = new(
            document.Id,
            document.FullName!,
            document.DefaultBranch!);
        return true;
    }

    internal static bool TryMap(
        ActionHostGitHubWorkflowRunDocument? document,
        out ActionHostGitHubWorkflowRunFact? fact)
    {
        fact = null;
        if (document is null ||
            document.Id <= 0 ||
            document.Attempt <= 0 ||
            document.WorkflowId <= 0 ||
            !IsName(document.Name, 255) ||
            !IsName(document.Path, 1024) ||
            !IsName(document.HeadBranch, 255) ||
            !IsCommitSha(document.HeadSha) ||
            !IsName(document.Event, 64) ||
            document.Conclusion is not null &&
                !IsName(document.Conclusion, 64) ||
            !TryMap(document.Repository, out var repository) ||
            !TryMap(document.HeadRepository, out var headRepository) ||
            !TryMap(document.Actor, out var actor) ||
            !TryMap(document.TriggeringActor, out var triggeringActor) ||
            document.PullRequests is null ||
            document.PullRequests.Length > 100)
        {
            return false;
        }

        var pullRequests = new List<ActionHostGitHubPullRequestReferenceFact>(
            document.PullRequests.Length);
        foreach (var pullRequest in document.PullRequests)
        {
            if (!TryMapReference(pullRequest, out var mapped))
            {
                return false;
            }

            pullRequests.Add(mapped!);
        }

        fact = new(
            document.Id,
            document.Attempt,
            document.WorkflowId,
            document.Name!,
            document.Path!,
            document.HeadBranch!,
            document.HeadSha!,
            document.Event!,
            document.Conclusion,
            repository!,
            headRepository!,
            actor!,
            triggeringActor!,
            pullRequests);
        return true;
    }

    internal static bool TryMap(
        ActionHostGitHubPullRequestDocument? document,
        out ActionHostGitHubPullRequestFact? fact)
    {
        fact = null;
        if (document is null ||
            document.Id <= 0 ||
            document.Number <= 0 ||
            !IsName(document.State, 32) ||
            !TryMapSide(
                document.Base,
                out var baseSha,
                out var baseRepository) ||
            !TryMapSide(
                document.Head,
                out var headSha,
                out var headRepository))
        {
            return false;
        }

        fact = new(
            document.Id,
            document.Number,
            document.State!,
            document.Draft,
            document.MergedAt,
            baseSha!,
            baseRepository!,
            headSha!,
            headRepository!);
        return true;
    }

    internal static bool TryMap(
        ActionHostGitHubPermissionDocument? document,
        out ActionHostGitHubPermissionFact? fact)
    {
        fact = null;
        if (document is null || !IsName(document.Permission, 32))
        {
            return false;
        }

        fact = new(document.Permission!);
        return true;
    }

    internal static bool TryMapContent(
        ActionHostGitHubContentDocument? document,
        string expectedPath,
        out ActionHostGitHubWorkflowSourceFact? fact)
    {
        fact = null;
        var expectedName = expectedPath.Split('/')[^1];
        if (document is null ||
            !StringComparer.Ordinal.Equals(document.Type, "file") ||
            !StringComparer.Ordinal.Equals(document.Encoding, "base64") ||
            !StringComparer.Ordinal.Equals(document.Path, expectedPath) ||
            !StringComparer.Ordinal.Equals(document.Name, expectedName) ||
            document.Size <= 0 ||
            document.Size > ActionHostGitHubAuthorizationPolicy
                .MaximumWorkflowBytes ||
            !IsCommitSha(document.Sha) ||
            !TryDecodeGitHubBase64(document.Content, out var bytes) ||
            bytes.Length != document.Size ||
            !StringComparer.Ordinal.Equals(
                GitBlobSha(bytes),
                document.Sha))
        {
            return false;
        }

        fact = new(expectedPath, expectedName, document.Sha!, bytes);
        return true;
    }

    internal static bool IsCommitSha(string? value) =>
        value is { Length: 40 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool IsRepositoryName(string? value)
    {
        if (!IsName(value, 255))
        {
            return false;
        }

        var separator = value!.IndexOf('/');
        return separator > 0 &&
            separator == value.LastIndexOf('/') &&
            separator < value.Length - 1;
    }

    private static bool TryMapReference(
        ActionHostGitHubPullRequestReferenceDocument? document,
        out ActionHostGitHubPullRequestReferenceFact? fact)
    {
        fact = null;
        if (document is null ||
            document.Id <= 0 ||
            document.Number <= 0 ||
            !TryMapSide(
                document.Base,
                out var baseSha,
                out var baseRepository) ||
            !TryMapSide(
                document.Head,
                out var headSha,
                out var headRepository))
        {
            return false;
        }

        fact = new(
            document.Id,
            document.Number,
            baseSha!,
            baseRepository!,
            headSha!,
            headRepository!);
        return true;
    }

    private static bool TryMapSide(
        ActionHostGitHubPullRequestReferenceSideDocument? document,
        out string? sha,
        out ActionHostGitHubRepositoryReference? repository)
    {
        sha = null;
        repository = null;
        if (document is null ||
            !IsCommitSha(document.Sha) ||
            !TryMap(document.Repository, out repository))
        {
            return false;
        }

        sha = document.Sha;
        return true;
    }

    private static bool TryMapSide(
        ActionHostGitHubPullRequestSideDocument? document,
        out string? sha,
        out ActionHostGitHubRepositoryIdentity? repository)
    {
        sha = null;
        repository = null;
        if (document is null ||
            !IsCommitSha(document.Sha) ||
            !TryMap(document.Repository, out repository))
        {
            return false;
        }

        sha = document.Sha;
        return true;
    }

    private static bool TryMap(
        ActionHostGitHubRepositoryReferenceDocument? document,
        out ActionHostGitHubRepositoryReference? reference)
    {
        reference = null;
        if (document is null ||
            document.Id <= 0 ||
            !IsName(document.Name, 255) ||
            !IsName(document.Url, 2048))
        {
            return false;
        }

        reference = new(document.Id, document.Name!);
        return true;
    }

    private static bool TryMap(
        ActionHostGitHubRepositoryIdentityDocument? document,
        out ActionHostGitHubRepositoryIdentity? identity)
    {
        identity = null;
        if (document is null ||
            document.Id <= 0 ||
            !IsRepositoryName(document.FullName))
        {
            return false;
        }

        identity = new(document.Id, document.FullName!);
        return true;
    }

    private static bool TryMap(
        ActionHostGitHubActorDocument? document,
        out ActionHostGitHubActorFact? actor)
    {
        actor = null;
        if (document is null ||
            document.Id <= 0 ||
            !IsName(document.Login, 255))
        {
            return false;
        }

        actor = new(document.Id, document.Login!);
        return true;
    }

    private static bool TryDecodeGitHubBase64(
        string? content,
        out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        var maximumEncoded = checked(
            ((ActionHostGitHubAuthorizationPolicy.MaximumWorkflowBytes + 2) /
                3) * 4);
        var unwrapped = new StringBuilder(
            Math.Min(content.Length, maximumEncoded));
        for (var index = 0; index < content.Length; index++)
        {
            var character = content[index];
            if (character == '\r')
            {
                if (index + 1 >= content.Length ||
                    content[index + 1] != '\n' ||
                    unwrapped.Length == 0 ||
                    unwrapped.Length % 4 != 0)
                {
                    return false;
                }

                index++;
                continue;
            }

            if (character == '\n')
            {
                if (unwrapped.Length == 0 || unwrapped.Length % 4 != 0)
                {
                    return false;
                }

                continue;
            }

            if (character is not (
                    >= 'A' and <= 'Z' or
                    >= 'a' and <= 'z' or
                    >= '0' and <= '9' or
                    '+' or '/' or '=') ||
                unwrapped.Length >= maximumEncoded)
            {
                return false;
            }

            unwrapped.Append(character);
        }

        if (unwrapped.Length == 0 || unwrapped.Length % 4 != 0)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(unwrapped.ToString());
        }
        catch (FormatException)
        {
            return false;
        }

        return bytes.Length <=
                ActionHostGitHubAuthorizationPolicy.MaximumWorkflowBytes &&
            StringComparer.Ordinal.Equals(
                Convert.ToBase64String(bytes),
                unwrapped.ToString());
    }

    private static string GitBlobSha(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes(
            $"blob {bytes.Length.ToString(CultureInfo.InvariantCulture)}\0");
        var framed = new byte[checked(header.Length + bytes.Length)];
        header.CopyTo(framed, 0);
        bytes.CopyTo(framed, header.Length);
        return Convert.ToHexString(SHA1.HashData(framed)).ToLowerInvariant();
    }

    private static bool IsName(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        value.All(static character => !char.IsControl(character));
}
