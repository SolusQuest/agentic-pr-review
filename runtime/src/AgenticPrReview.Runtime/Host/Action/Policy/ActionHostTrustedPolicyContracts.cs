using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.ActionHost.Policy;

internal enum ActionHostPublicationMode
{
    Sticky = 1,
    StickyAndInline,
}

internal enum ActionHostInlineSeverity
{
    High = 1,
    Critical,
}

internal enum ActionHostTrustedPolicyFailure
{
    None = 0,
    AuthorityMismatch,
    InvalidConfigPath,
    InvalidInstructionsPath,
    SourceMissing,
    SourceNonRegular,
    SourceIncomplete,
    SourceIdentityMismatch,
    ConfigTooLarge,
    InstructionsTooLarge,
    MalformedConfig,
    MalformedInstructions,
    CredentialDenied,
    TransportFailure,
    RequestLimit,
    AggregateLimit,
    Deadline,
    Cancelled,
    InternalInvariant,
}

internal sealed class ActionHostTrustedPolicyPath
{
    internal const int MaximumUtf8Bytes = 1024;
    internal const int MaximumSegmentsByUtf8Limit =
        (MaximumUtf8Bytes + 1) / 2;
    private const string InstructionsPrefix =
        ".github/agentic-pr-review/";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private ActionHostTrustedPolicyPath(string value)
    {
        Value = value;
        Segments = value.Split('/').ToImmutableArray();
    }

    internal string Value { get; }

    internal ImmutableArray<string> Segments { get; }

    internal static bool TryCreate(
        string? value,
        bool requireInstructionsSubtree,
        out ActionHostTrustedPolicyPath? path)
    {
        path = null;
        if (string.IsNullOrEmpty(value) ||
            value[0] == '/' ||
            value[^1] == '/' ||
            value.Contains('\\') ||
            value.Contains(':') ||
            value.Any(char.IsControl))
        {
            return false;
        }

        string[] segments = value.Split('/');
        if (segments.Length < 1 ||
            segments.Any(static segment =>
                segment.Length == 0 || segment is "." or ".."))
        {
            return false;
        }

        try
        {
            if (StrictUtf8.GetByteCount(value) > MaximumUtf8Bytes)
            {
                return false;
            }
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        if (requireInstructionsSubtree &&
            !value.StartsWith(
                InstructionsPrefix,
                StringComparison.Ordinal))
        {
            return false;
        }

        path = new(value);
        return true;
    }

    public override string ToString() => "trusted_policy_path";
}

internal sealed class ActionHostTrustedPolicyRequest
{
    internal const string DefaultConfigPath =
        ".github/agentic-pr-review.json";
    private const string PayloadContinuityDomain =
        "apr.action-host.payload-continuity.v1";

    private ActionHostTrustedPolicyRequest(
        long repositoryId,
        string repositoryName,
        string workflowPath,
        string workflowCommitSha,
        string workflowBlobSha,
        string actionSourceSha,
        string payloadSha256,
        string payloadContinuitySha256,
        ActionHostPayloadContinuityMode payloadContinuityMode,
        string buildDiscriminator,
        ActionHostTrustedPolicyPath configPath)
    {
        RepositoryId = repositoryId;
        RepositoryName = repositoryName;
        WorkflowPath = workflowPath;
        WorkflowCommitSha = workflowCommitSha;
        WorkflowBlobSha = workflowBlobSha;
        ActionSourceSha = actionSourceSha;
        PayloadSha256 = payloadSha256;
        PayloadContinuitySha256 = payloadContinuitySha256;
        PayloadContinuityMode = payloadContinuityMode;
        BuildDiscriminator = buildDiscriminator;
        ConfigPath = configPath;
    }

    internal long RepositoryId { get; }
    internal string RepositoryName { get; }
    internal string WorkflowPath { get; }
    internal string WorkflowCommitSha { get; }
    internal string WorkflowBlobSha { get; }
    internal string ActionSourceSha { get; }
    internal string PayloadSha256 { get; }
    internal string PayloadContinuitySha256 { get; }
    internal ActionHostPayloadContinuityMode PayloadContinuityMode { get; }
    internal string BuildDiscriminator { get; }
    internal ActionHostTrustedPolicyPath ConfigPath { get; }

    internal static bool TryBind(
        ActionHostLaunchContract? launch,
        ActionHostAuthorizer.AuthorizedInvocation? invocation,
        out ActionHostTrustedPolicyRequest? request,
        out ActionHostTrustedPolicyFailure failure)
    {
        request = null;
        failure = ActionHostTrustedPolicyFailure.AuthorityMismatch;
        if (launch is null ||
            invocation is null ||
            !invocation.IsBoundTo(launch) ||
            launch.RepositoryId != invocation.PullRequest.RepositoryId ||
            !StringComparer.Ordinal.Equals(
                launch.RepositoryName,
                invocation.PullRequest.BaseRepositoryName) ||
            !StringComparer.Ordinal.Equals(
                launch.WorkflowPath,
                invocation.WorkflowPath) ||
            !StringComparer.Ordinal.Equals(
                launch.WorkflowSha,
                invocation.WorkflowCommitSha) ||
            !StringComparer.Ordinal.Equals(
                launch.ActionSourceSha,
                invocation.ActionSourceSha))
        {
            return false;
        }

        var configValue = launch.Inputs.ConfigPath ?? DefaultConfigPath;
        if (!ActionHostTrustedPolicyPath.TryCreate(
                configValue,
                requireInstructionsSubtree: false,
                out var configPath))
        {
            failure = ActionHostTrustedPolicyFailure.InvalidConfigPath;
            return false;
        }

        if (!TryPayloadContinuityIdentity(
                launch,
                invocation,
                out var payloadContinuitySha256))
        {
            failure = ActionHostTrustedPolicyFailure.AuthorityMismatch;
            return false;
        }

        request = new(
            launch.RepositoryId,
            launch.RepositoryName,
            invocation.WorkflowPath,
            invocation.WorkflowCommitSha,
            invocation.WorkflowBlobSha,
            invocation.ActionSourceSha,
            launch.PayloadSha256,
            payloadContinuitySha256!,
            invocation.PayloadContinuityMode,
            launch.BuildDiscriminator,
            configPath!);
        failure = ActionHostTrustedPolicyFailure.None;
        return true;
    }

    private static bool TryPayloadContinuityIdentity(
        ActionHostLaunchContract launch,
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        out string? identity)
    {
        identity = null;
        if (invocation.PayloadContinuityMode ==
                ActionHostPayloadContinuityMode.ExactExecutable &&
            invocation.PayloadSourceCommit is null &&
            invocation.PayloadSourceTree is null)
        {
            identity = launch.PayloadSha256;
            return true;
        }

        if (invocation.PayloadContinuityMode !=
                ActionHostPayloadContinuityMode.ExactSource ||
            !IsLowerHex(invocation.PayloadSourceCommit, 40) ||
            !IsLowerHex(invocation.PayloadSourceTree, 40) ||
            !StringComparer.Ordinal.Equals(
                invocation.PayloadSourceCommit,
                invocation.WorkflowCommitSha))
        {
            return false;
        }

        identity = ComputeExactSourceContinuitySha256(
            invocation.ActionSourceSha,
            launch.BuildDiscriminator,
            invocation.PayloadSourceCommit!,
            invocation.PayloadSourceTree!,
            invocation.WorkflowBlobSha,
            invocation.WorkflowCommitSha,
            invocation.WorkflowPath);
        return true;
    }

    internal static string ComputeExactSourceContinuitySha256(
        string actionSourceSha,
        string buildDiscriminator,
        string payloadSourceCommit,
        string payloadSourceTree,
        string workflowBlobSha,
        string workflowCommitSha,
        string workflowPath)
    {
        var writer = new Rfc8785Writer(1024);
        writer.WriteObjectStart();
        Write(ref writer, "action_source_sha", actionSourceSha);
        Write(ref writer, "build_discriminator", buildDiscriminator);
        Write(ref writer, "payload_source_commit", payloadSourceCommit);
        Write(ref writer, "payload_source_tree", payloadSourceTree);
        Write(ref writer, "workflow_blob_sha", workflowBlobSha);
        Write(ref writer, "workflow_commit_sha", workflowCommitSha);
        Write(ref writer, "workflow_path", workflowPath);
        writer.WriteObjectEnd();
        return AgentCanonical.HashDomain(
            PayloadContinuityDomain,
            writer.ToImmutableArray().AsSpan());
    }

    private static void Write(
        ref Rfc8785Writer writer,
        string name,
        string value)
    {
        writer.WriteProperty(name);
        writer.WriteString(value);
    }

    private static bool IsLowerHex(string? value, int length) =>
        value?.Length == length && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public override string ToString() => "trusted_policy_request";
}

internal sealed class ActionHostTrustedPolicyMaterialization
{
    private ActionHostTrustedPolicyMaterialization(
        ActionHostTrustedPolicy? policy,
        ActionHostTrustedPolicyFailure failure)
    {
        Policy = policy;
        Failure = failure;
    }

    internal ActionHostTrustedPolicy? Policy { get; }
    internal ActionHostTrustedPolicyFailure Failure { get; }
    internal bool Succeeded =>
        Policy is not null && Failure == ActionHostTrustedPolicyFailure.None;

    internal static ActionHostTrustedPolicyMaterialization Success(
        ActionHostTrustedPolicy policy) => new(policy,
            ActionHostTrustedPolicyFailure.None);

    internal static ActionHostTrustedPolicyMaterialization Failed(
        ActionHostTrustedPolicyFailure failure) => new(null, failure);

    public override string ToString() => Succeeded
        ? "trusted_policy_materialized"
        : $"trusted_policy_failed({Failure})";
}

internal sealed partial class ActionHostTrustedPolicy
{
    private ActionHostTrustedPolicy(PolicyValues values)
    {
        RepositoryId = values.RepositoryId;
        RepositoryName = values.RepositoryName;
        WorkflowPath = values.WorkflowPath;
        WorkflowCommitSha = values.WorkflowCommitSha;
        WorkflowBlobSha = values.WorkflowBlobSha;
        ActionSourceSha = values.ActionSourceSha;
        PayloadSha256 = values.PayloadSha256;
        PayloadContinuitySha256 = values.PayloadContinuitySha256;
        PayloadContinuityMode = values.PayloadContinuityMode;
        BuildDiscriminator = values.BuildDiscriminator;
        ConfigPath = values.ConfigPath;
        InstructionsPath = values.InstructionsPath;
        ConfigBlobSha = values.ConfigBlobSha;
        InstructionsBlobSha = values.InstructionsBlobSha;
        ConfigBytes = ImmutableArray.CreateRange(values.ConfigBytes);
        InstructionBytes = ImmutableArray.CreateRange(values.InstructionBytes);
        ConfigSha256 = values.ConfigSha256;
        InstructionsSha256 = values.InstructionsSha256;
        PolicySha256 = values.PolicySha256;
        PublicationMode = values.PublicationMode;
        InlineMinSeverity = values.InlineMinSeverity;
        ProviderId = values.ProviderId;
        ModelId = values.ModelId;
        AdapterId = values.AdapterId;
        Endpoint = values.Endpoint;
        ThinkingEnabled = values.ThinkingEnabled;
        PromptCachingEnabled = values.PromptCachingEnabled;
        ToolsetSha256 = values.ToolsetSha256;
        LimitsSha256 = values.LimitsSha256;
        StateRetentionSeconds = values.StateRetentionSeconds;
        MaximumInlineComments = values.MaximumInlineComments;
        SecurityPolicyId = values.SecurityPolicyId;
    }

    internal long RepositoryId { get; }
    internal string RepositoryName { get; }
    internal string WorkflowPath { get; }
    internal string WorkflowCommitSha { get; }
    internal string WorkflowBlobSha { get; }
    internal string ActionSourceSha { get; }
    internal string PayloadSha256 { get; }
    internal string PayloadContinuitySha256 { get; }
    internal ActionHostPayloadContinuityMode PayloadContinuityMode { get; }
    internal string BuildDiscriminator { get; }
    internal string ConfigPath { get; }
    internal string InstructionsPath { get; }
    internal string ConfigBlobSha { get; }
    internal string InstructionsBlobSha { get; }
    internal ImmutableArray<byte> ConfigBytes { get; }
    internal ImmutableArray<byte> InstructionBytes { get; }
    internal string ConfigSha256 { get; }
    internal string InstructionsSha256 { get; }
    internal string PolicySha256 { get; }
    internal ActionHostPublicationMode PublicationMode { get; }
    internal ActionHostInlineSeverity InlineMinSeverity { get; }
    internal string ProviderId { get; }
    internal string ModelId { get; }
    internal string AdapterId { get; }
    internal string Endpoint { get; }
    internal bool ThinkingEnabled { get; }
    internal bool PromptCachingEnabled { get; }
    internal string ToolsetSha256 { get; }
    internal string LimitsSha256 { get; }
    internal long StateRetentionSeconds { get; }
    internal int MaximumInlineComments { get; }
    internal string SecurityPolicyId { get; }

    public override string ToString() => "trusted_policy";

    private sealed record PolicyValues(
        long RepositoryId,
        string RepositoryName,
        string WorkflowPath,
        string WorkflowCommitSha,
        string WorkflowBlobSha,
        string ActionSourceSha,
        string PayloadSha256,
        string PayloadContinuitySha256,
        ActionHostPayloadContinuityMode PayloadContinuityMode,
        string BuildDiscriminator,
        string ConfigPath,
        string InstructionsPath,
        string ConfigBlobSha,
        string InstructionsBlobSha,
        byte[] ConfigBytes,
        byte[] InstructionBytes,
        string ConfigSha256,
        string InstructionsSha256,
        string PolicySha256,
        ActionHostPublicationMode PublicationMode,
        ActionHostInlineSeverity InlineMinSeverity,
        string ProviderId,
        string ModelId,
        string AdapterId,
        string Endpoint,
        bool ThinkingEnabled,
        bool PromptCachingEnabled,
        string ToolsetSha256,
        string LimitsSha256,
        long StateRetentionSeconds,
        int MaximumInlineComments,
        string SecurityPolicyId);
}
