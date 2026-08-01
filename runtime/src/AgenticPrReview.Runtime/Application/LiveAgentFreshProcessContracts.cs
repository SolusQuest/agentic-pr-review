using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime;

internal static class LiveAgentFreshProcessCodes
{
    internal const string UsageInvalid = "r3_fresh_process_usage_invalid";
    internal const string AuthorizationInvalid =
        "r3_fresh_process_authorization_invalid";
    internal const string RootInvalid = "r3_fresh_process_root_invalid";
    internal const string InputInvalid = "r3_fresh_process_input_invalid";
    internal const string LineageInvalid = "state_lineage_mismatch";
    internal const string TransitionRejected = "session_transition_rejected";
    internal const string ProcessIdentityReused =
        "r3_fresh_process_identity_reused";
    internal const string TransportProofFailed =
        "r3_fresh_process_transport_proof_failed";
    internal const string OutputFailed = "r3_fresh_process_output_failed";
}

internal sealed record LiveAgentFreshProcessCommandResult(
    int ExitCode,
    string? DiagnosticCode);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LiveAgentFreshProcessStableAuthority(
    string RepositoryId,
    long ReviewTarget,
    string WorkflowIdentity,
    string TrustedPolicy,
    string BuildId,
    string ProviderId,
    string ModelId,
    string AdapterId,
    string SessionId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LiveAgentFreshProcessScopeDocument(
    string RepositoryId,
    string WorkflowIdentity,
    long ReviewTarget,
    string SessionId,
    string ProviderId,
    string ModelId,
    string AdapterId,
    string PolicySha256,
    string LimitsSha256,
    string ToolsetSha256,
    string BuildId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LiveAgentFreshProcessTransitionDocument(
    string Classification,
    string FromHeadSha,
    string ToBaseSha,
    string ToHeadSha,
    string ReceiptSha256);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LiveAgentFreshProcessAuthorizationDocument(
    string Kind,
    LiveAgentFreshProcessStableAuthority Stable,
    LiveAgentFreshProcessScopeDocument AuthorizedScope,
    bool IsTrustedWorkflow,
    bool IsSameRepository,
    bool IsForkOrigin,
    string ExecutionProfile,
    string StateLocatorFamily,
    string RestoreIntent,
    LiveAgentFreshProcessTransitionDocument Transition,
    string InvocationIdentity,
    string? ExpectedLineageSha256);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LiveAgentFreshProcessReviewedIdentityDocument(
    string RepositoryId,
    long ReviewTarget,
    string BaseSha,
    string HeadSha);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LiveAgentFreshProcessReviewedInputDocument(
    string Kind,
    LiveAgentFreshProcessReviewedIdentityDocument ReviewedIdentity,
    string ReviewContext);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LiveAgentFreshProcessFileDocument(
    string Path,
    long Length,
    string Sha256,
    string ContentBase64);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LiveAgentFreshProcessChangedFileDocument(
    string Path,
    string? PreviousPath,
    string Status,
    int Additions,
    int Deletions,
    int Changes,
    string PatchStatus,
    string? PatchSha256,
    bool SourceTruncated);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LiveAgentFreshProcessDiffLineDocument(
    string Kind,
    int? OldLine,
    int? NewLine,
    string Text);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LiveAgentFreshProcessDiffHunkDocument(
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    LiveAgentFreshProcessDiffLineDocument[] Lines);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LiveAgentFreshProcessDiffSourceDocument(
    string Path,
    string? PreviousPath,
    string Status,
    bool SourceTruncated,
    LiveAgentFreshProcessDiffHunkDocument[] Hunks);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LiveAgentFreshProcessSnapshotManifestDocument(
    string Kind,
    LiveAgentFreshProcessReviewedIdentityDocument ReviewedIdentity,
    string[] TrackedFiles,
    LiveAgentFreshProcessFileDocument[] Files,
    LiveAgentFreshProcessChangedFileDocument[] ChangedFiles,
    LiveAgentFreshProcessDiffSourceDocument[] DiffSources);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LiveAgentFreshProcessLineageDocument(
    string Kind,
    LiveAgentFreshProcessScopeDocument Scope,
    string ProducerBaseSha,
    string ProducerHeadSha,
    long Generation,
    string SessionSha256,
    string EnvelopeSha256,
    string? ExpectedPredecessorEnvelopeSha256,
    long AcceptedAtUnixSeconds,
    long ExpiresAtUnixSeconds,
    bool TransitionAuthorized,
    string InvocationIdentitySha256);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LiveAgentFreshProcessResultDocument(
    string Kind,
    string Code,
    long? Generation,
    string TransitionClass,
    int ModelCalls,
    int ToolCalls,
    string? StablePlanSha256,
    string? TerminalSha256,
    string? SessionSha256,
    string? EnvelopeSha256,
    string? LineageSha256,
    string? SecondRequestSha256,
    string InvocationIdentitySha256,
    bool HandoffReady);

[JsonSerializable(typeof(LiveAgentFreshProcessAuthorizationDocument))]
[JsonSerializable(typeof(LiveAgentFreshProcessReviewedInputDocument))]
[JsonSerializable(typeof(LiveAgentFreshProcessSnapshotManifestDocument))]
[JsonSerializable(typeof(LiveAgentFreshProcessLineageDocument))]
[JsonSerializable(typeof(LiveAgentFreshProcessResultDocument))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false,
    GenerationMode = JsonSourceGenerationMode.Default)]
internal sealed partial class LiveAgentFreshProcessJsonContext :
    JsonSerializerContext;

internal static class LiveAgentFreshProcessCodec
{
    internal const int AuthorizationBytes = 16 * 1024;
    internal const int ReviewedInputBytes = 128 * 1024;
    internal const int SnapshotManifestBytes = 2 * 1024 * 1024;
    internal const int LineageBytes = 16 * 1024;
    internal const int ResultBytes = 16 * 1024;

    internal static LiveAgentFreshProcessAuthorizationDocument?
        ReadAuthorization(byte[] bytes) => ReadCanonical(
            bytes,
            AuthorizationBytes,
            LiveAgentFreshProcessJsonContext.Default
                .LiveAgentFreshProcessAuthorizationDocument);

    internal static LiveAgentFreshProcessReviewedInputDocument?
        ReadReviewedInput(byte[] bytes) => ReadCanonical(
            bytes,
            ReviewedInputBytes,
            LiveAgentFreshProcessJsonContext.Default
                .LiveAgentFreshProcessReviewedInputDocument);

    internal static LiveAgentFreshProcessSnapshotManifestDocument?
        ReadSnapshotManifest(byte[] bytes) => ReadCanonical(
            bytes,
            SnapshotManifestBytes,
            LiveAgentFreshProcessJsonContext.Default
                .LiveAgentFreshProcessSnapshotManifestDocument);

    internal static LiveAgentFreshProcessLineageDocument?
        ReadLineage(byte[] bytes) => ReadCanonical(
            bytes,
            LineageBytes,
            LiveAgentFreshProcessJsonContext.Default
                .LiveAgentFreshProcessLineageDocument);

    internal static LiveAgentFreshProcessResultDocument?
        ReadResult(byte[] bytes) => ReadCanonical(
            bytes,
            ResultBytes,
            LiveAgentFreshProcessJsonContext.Default
                .LiveAgentFreshProcessResultDocument);

    internal static byte[] Write(
        LiveAgentFreshProcessAuthorizationDocument value) => WriteCanonical(
            value,
            LiveAgentFreshProcessJsonContext.Default
                .LiveAgentFreshProcessAuthorizationDocument);

    internal static byte[] Write(
        LiveAgentFreshProcessReviewedInputDocument value) => WriteCanonical(
            value,
            LiveAgentFreshProcessJsonContext.Default
                .LiveAgentFreshProcessReviewedInputDocument);

    internal static byte[] Write(
        LiveAgentFreshProcessSnapshotManifestDocument value) => WriteCanonical(
            value,
            LiveAgentFreshProcessJsonContext.Default
                .LiveAgentFreshProcessSnapshotManifestDocument);

    internal static byte[] Write(
        LiveAgentFreshProcessLineageDocument value) => WriteCanonical(
            value,
            LiveAgentFreshProcessJsonContext.Default
                .LiveAgentFreshProcessLineageDocument);

    internal static byte[] Write(
        LiveAgentFreshProcessResultDocument value) => WriteCanonical(
            value,
            LiveAgentFreshProcessJsonContext.Default
                .LiveAgentFreshProcessResultDocument);

    private static T? ReadCanonical<T>(
        byte[] bytes,
        int maximumBytes,
        JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (bytes is null ||
            bytes.Length is 0 ||
            bytes.Length > maximumBytes ||
            bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            return null;
        }

        try
        {
            var value = JsonSerializer.Deserialize(bytes, typeInfo);
            if (value is null)
            {
                return null;
            }

            var canonical = JsonSerializer.SerializeToUtf8Bytes(
                value,
                typeInfo);
            return bytes.AsSpan().SequenceEqual(canonical)
                ? value
                : null;
        }
        catch (Exception exception) when (exception is JsonException or
            NotSupportedException or
            EncoderFallbackException)
        {
            return null;
        }
    }

    private static byte[] WriteCanonical<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
    }
}

internal static class LiveAgentFreshProcessDomain
{
    internal const string AuthorizationKind =
        "agentic-pr-review/r3-host-authorization";
    internal const string ReviewedInputKind =
        "agentic-pr-review/r3-reviewed-input";
    internal const string SnapshotManifestKind =
        "agentic-pr-review/r3-snapshot-manifest";
    internal const string LineageKind =
        "agentic-pr-review/r3-accepted-lineage";
    internal const string ResultKind =
        "agentic-pr-review/r3-fresh-process-result";
    internal const string DeterministicProfile = "deterministic";

    internal static bool TryMapScope(
        LiveAgentFreshProcessScopeDocument value,
        out RestrictedStateScope? scope)
    {
        scope = null;
        if (value is null)
        {
            return false;
        }

        var candidate = new RestrictedStateScope(
            value.RepositoryId,
            value.WorkflowIdentity,
            value.ReviewTarget,
            value.SessionId,
            value.ProviderId,
            value.ModelId,
            value.AdapterId,
            value.PolicySha256,
            value.LimitsSha256,
            value.ToolsetSha256,
            value.BuildId);
        if (!RestrictedStateValidation.IsValidScope(candidate))
        {
            return false;
        }

        scope = candidate;
        return true;
    }

    internal static LiveAgentFreshProcessScopeDocument ScopeDocument(
        RestrictedStateScope scope) => new(
            scope.RepositoryId,
            scope.WorkflowIdentity,
            scope.ReviewTarget,
            scope.SessionId,
            scope.ProviderId,
            scope.ModelId,
            scope.AdapterId,
            scope.PolicySha256,
            scope.LimitsSha256,
            scope.ToolsetSha256,
            scope.BuildId);

    internal static bool TryMapReviewedIdentity(
        LiveAgentFreshProcessReviewedIdentityDocument value,
        out ReviewedIdentity? identity)
    {
        identity = null;
        if (value is null)
        {
            return false;
        }

        var candidate = new ReviewedIdentity(
            value.RepositoryId,
            value.ReviewTarget,
            value.BaseSha,
            value.HeadSha);
        if (!candidate.IsValid())
        {
            return false;
        }

        identity = candidate;
        return true;
    }

    internal static LiveAgentFreshProcessReviewedIdentityDocument
        IdentityDocument(ReviewedIdentity identity) => new(
            identity.RepositoryId,
            identity.ReviewTarget,
            identity.BaseSha,
            identity.HeadSha);

    internal static bool IsIdentifier(string? value)
    {
        if (value is not { Length: >= 1 and <= 64 })
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (
                >= 'A' and <= 'Z' or
                >= 'a' and <= 'z' or
                >= '0' and <= '9' or
                '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool IsCommitSha(string? value) =>
        value is { Length: 40 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static string InvocationIdentitySha256(string value) =>
        AgentCanonical.HashDomain(
            "apr.r3.host-invocation",
            Encoding.ASCII.GetBytes(value));

    internal static string TransitionReceiptSha256(
        string? lineageSha256,
        string classification,
        string fromHeadSha,
        string toBaseSha,
        string toHeadSha,
        string invocationIdentity)
    {
        var text = string.Join(
            '\0',
            lineageSha256 ?? "absent",
            classification,
            fromHeadSha,
            toBaseSha,
            toHeadSha,
            invocationIdentity);
        return AgentCanonical.HashDomain(
            "apr.r3.host-transition",
            Encoding.UTF8.GetBytes(text));
    }

    internal static string RawSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
