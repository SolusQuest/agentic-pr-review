using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

internal sealed record TrustedProofControlCoordinates(
    string Repository,
    long RepositoryId,
    long PullRequestNumber,
    string FixtureHeadSha,
    string OperationId,
    string WorkflowSha,
    string ActionSourceSha,
    string PayloadSha256,
    long RunId,
    int RunAttempt)
{
    internal static bool TryReadEnvironment(
        Func<string, string?> read,
        out TrustedProofControlCoordinates? coordinates,
        out string? token)
    {
        coordinates = null;
        token = read("GITHUB_TOKEN");
        var repository = read("REPOSITORY");
        var fixtureHeadSha = read("FIXTURE_HEAD_SHA");
        var operationId = read("OPERATION_ID");
        var workflowSha = read("WORKFLOW_SHA");
        var actionSourceSha = read("ACTION_SOURCE_SHA");
        var payloadSha256 = read("PAYLOAD_SHA256");
        if (string.IsNullOrWhiteSpace(token) ||
            repository != "SolusQuest/agentic-pr-review" ||
            !long.TryParse(read("REPOSITORY_ID"), out var repositoryId) ||
            repositoryId <= 0 ||
            !long.TryParse(read("PR_NUMBER"), out var pullRequestNumber) ||
            pullRequestNumber <= 0 ||
            !IsLowerHex(fixtureHeadSha, 40) ||
            !IsLowerHex(operationId, 64) ||
            !IsLowerHex(workflowSha, 40) ||
            !IsLowerHex(actionSourceSha, 40) ||
            !IsLowerHex(payloadSha256, 64) ||
            !long.TryParse(read("RUN_ID"), out var runId) ||
            runId <= 0 ||
            !int.TryParse(read("RUN_ATTEMPT"), out var runAttempt) ||
            runAttempt <= 0)
        {
            token = null;
            return false;
        }

        coordinates = new(
            repository,
            repositoryId,
            pullRequestNumber,
            fixtureHeadSha!,
            operationId!,
            workflowSha!,
            actionSourceSha!,
            payloadSha256!,
            runId,
            runAttempt);
        return true;
    }

    private static bool IsLowerHex(string? value, int length) =>
        value?.Length == length && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal sealed record TrustedProofControlMarker(
    [property: JsonPropertyName("contract")] string Contract,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("operation_id")] string OperationId,
    [property: JsonPropertyName("repository_id")] long RepositoryId,
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("pr_number")] long PullRequestNumber,
    [property: JsonPropertyName("fixture_head_sha")] string FixtureHeadSha,
    [property: JsonPropertyName("workflow_sha")] string WorkflowSha,
    [property: JsonPropertyName("action_source_sha")] string ActionSourceSha,
    [property: JsonPropertyName("payload_sha256")] string PayloadSha256,
    [property: JsonPropertyName("run_id")] long RunId,
    [property: JsonPropertyName("run_attempt")] int RunAttempt,
    [property: JsonPropertyName("predecessor_comment_id")] long? PredecessorCommentId,
    [property: JsonPropertyName("body_sha256")] string BodySha256)
{
    internal const string ContractKind = "apr-r4-e2p-proof-control-v1";
    internal const string Prefix = "<!-- apr-r4-e2p-control ";
    internal const string Suffix = " -->";
    internal const int MaximumBodyBytes = 4096;
    internal static readonly string[] Kinds =
        ["ready", "release", "stale-ready", "stale-release"];

    internal static string CreateBody(
        string kind,
        TrustedProofControlCoordinates coordinates,
        long? predecessorCommentId)
    {
        if (!Kinds.Contains(kind, StringComparer.Ordinal) ||
            (kind is "release" or "stale-release") !=
                predecessorCommentId.HasValue)
        {
            throw new ArgumentException("The proof-control marker is invalid.");
        }

        var marker = new TrustedProofControlMarker(
            ContractKind,
            kind,
            coordinates.OperationId,
            coordinates.RepositoryId,
            coordinates.Repository,
            coordinates.PullRequestNumber,
            coordinates.FixtureHeadSha,
            coordinates.WorkflowSha,
            coordinates.ActionSourceSha,
            coordinates.PayloadSha256,
            coordinates.RunId,
            coordinates.RunAttempt,
            predecessorCommentId,
            string.Empty);
        var preimage = JsonSerializer.Serialize(
            marker,
            TrustedProofControlJsonContext.Default.TrustedProofControlMarker);
        var digest = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(preimage)))
            .ToLowerInvariant();
        var body = Prefix + JsonSerializer.Serialize(
            marker with { BodySha256 = digest },
            TrustedProofControlJsonContext.Default.TrustedProofControlMarker) +
            Suffix;
        if (Encoding.UTF8.GetByteCount(body) > MaximumBodyBytes)
        {
            throw new InvalidOperationException("The marker is oversized.");
        }

        return body;
    }

    internal static bool TryParse(
        string? body,
        out TrustedProofControlMarker? marker)
    {
        marker = null;
        if (body is null ||
            Encoding.UTF8.GetByteCount(body) > MaximumBodyBytes ||
            !body.StartsWith(Prefix, StringComparison.Ordinal) ||
            !body.EndsWith(Suffix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var json = body[Prefix.Length..^Suffix.Length];
            marker = JsonSerializer.Deserialize(
                json,
                TrustedProofControlJsonContext.Default.TrustedProofControlMarker);
            if (marker is null ||
                marker.Contract != ContractKind ||
                !Kinds.Contains(marker.Kind, StringComparer.Ordinal) ||
                marker.BodySha256.Length != 64)
            {
                marker = null;
                return false;
            }

            var preimage = JsonSerializer.Serialize(
                marker with { BodySha256 = string.Empty },
                TrustedProofControlJsonContext.Default.TrustedProofControlMarker);
            var digest = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(preimage)))
                .ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(digest),
                    Encoding.ASCII.GetBytes(marker.BodySha256)) ||
                !StringComparer.Ordinal.Equals(
                    body,
                    Prefix + JsonSerializer.Serialize(
                        marker,
                        TrustedProofControlJsonContext.Default
                            .TrustedProofControlMarker) + Suffix))
            {
                marker = null;
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            marker = null;
            return false;
        }
    }

    internal bool Matches(TrustedProofControlCoordinates coordinates) =>
        OperationId == coordinates.OperationId &&
        RepositoryId == coordinates.RepositoryId &&
        Repository == coordinates.Repository &&
        PullRequestNumber == coordinates.PullRequestNumber &&
        FixtureHeadSha == coordinates.FixtureHeadSha &&
        WorkflowSha == coordinates.WorkflowSha &&
        ActionSourceSha == coordinates.ActionSourceSha &&
        PayloadSha256 == coordinates.PayloadSha256 &&
        RunId == coordinates.RunId &&
        RunAttempt == coordinates.RunAttempt;
}

internal sealed record TrustedProofIssueComment(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("user")] TrustedProofCommentUser User,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

internal sealed record TrustedProofCommentUser(
    [property: JsonPropertyName("login")] string Login);

internal sealed record TrustedProofPermission(
    [property: JsonPropertyName("permission")] string Permission);

internal sealed record TrustedProofCreateComment(
    [property: JsonPropertyName("body")] string Body);

internal sealed record TrustedProofCleanupOutcome(
    [property: JsonPropertyName("comment_id")] long CommentId,
    [property: JsonPropertyName("outcome")] string Outcome);

internal sealed record TrustedProofCleanupReceipt(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("operation_id")] string OperationId,
    [property: JsonPropertyName("comment_outcomes")]
        TrustedProofCleanupOutcome[] CommentOutcomes,
    [property: JsonPropertyName("result")] string Result);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    PropertyNameCaseInsensitive = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = false)]
[JsonSerializable(typeof(TrustedProofControlMarker))]
[JsonSerializable(typeof(TrustedProofIssueComment))]
[JsonSerializable(typeof(TrustedProofIssueComment[]))]
[JsonSerializable(typeof(TrustedProofPermission))]
[JsonSerializable(typeof(TrustedProofCreateComment))]
[JsonSerializable(typeof(TrustedProofCleanupOutcome))]
[JsonSerializable(typeof(TrustedProofCleanupReceipt))]
internal sealed partial class TrustedProofControlJsonContext : JsonSerializerContext;
