using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Canonical;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime.ActionHost.Policy;

internal sealed partial class ActionHostTrustedPolicy
{
    internal const string Schema = "agentic-pr-review.config.v1";
    internal const string SecurityPolicy =
        "r4.trusted-workflow.reviewed-content-data-only.v1";
    internal const int InlineCommentCap = 5;
    internal const bool Thinking = true;
    internal const bool PromptCaching = true;
    internal static readonly TimeSpan MaterializationTimeout =
        TimeSpan.FromSeconds(60);
    private const int ConfigByteCap = 16 * 1024;
    private const int InstructionsByteCap = 64 * 1024;
    private const string IdentityDomain =
        "apr.action-host.trusted-policy.v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static async Task<ActionHostTrustedPolicyMaterialization>
        MaterializeAsync(
            ActionHostTrustedPolicyRequest request,
            IActionHostGitObjectTransport transport,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transport);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        deadline.CancelAfter(MaterializationTimeout);

        try
        {
            var reader = new ActionHostTrustedPolicySourceReader(
                request,
                transport);
            var configRead = await reader.ReadRegularBlobAsync(
                request.ConfigPath,
                ActionHostGitBlobReadBudget.TrustedConfig,
                deadline.Token);
            if (configRead.Blob is not { } configBlob)
            {
                return ActionHostTrustedPolicyMaterialization.Failed(
                    configRead.Failure);
            }

            if (!TryParseConfig(
                    configBlob.Bytes,
                    out var instructionsPath,
                    out var publicationMode,
                    out var inlineMinSeverity,
                    out var configFailure))
            {
                return ActionHostTrustedPolicyMaterialization.Failed(
                    configFailure);
            }

            var instructionsRead = await reader.ReadRegularBlobAsync(
                instructionsPath!,
                ActionHostGitBlobReadBudget.TrustedInstructions,
                deadline.Token);
            if (instructionsRead.Blob is not { } instructionsBlob)
            {
                return ActionHostTrustedPolicyMaterialization.Failed(
                    instructionsRead.Failure);
            }

            if (!IsStrictText(
                    instructionsBlob.Bytes,
                    InstructionsByteCap))
            {
                return ActionHostTrustedPolicyMaterialization.Failed(
                    instructionsBlob.Bytes.Length > InstructionsByteCap
                        ? ActionHostTrustedPolicyFailure.InstructionsTooLarge
                        : ActionHostTrustedPolicyFailure.MalformedInstructions);
            }

            if (AgentToolRegistry.Definitions.Length != 6)
            {
                return ActionHostTrustedPolicyMaterialization.Failed(
                    ActionHostTrustedPolicyFailure.InternalInvariant);
            }

            var configSha = AgentCanonical.HashRaw(configBlob.Bytes);
            var instructionsSha = AgentCanonical.HashRaw(
                instructionsBlob.Bytes);
            var toolsetSha = AgentCanonical.ToolsetSha256(
                AgentToolRegistry.Definitions);
            var limitsSha = AgentCanonical.LimitsSha256();
            var policySha = PolicyIdentity(
                request,
                configBlob,
                instructionsBlob,
                instructionsPath!,
                publicationMode,
                inlineMinSeverity,
                configSha,
                instructionsSha,
                toolsetSha,
                limitsSha);

            var policy = new ActionHostTrustedPolicy(new PolicyValues(
                request.RepositoryId,
                request.RepositoryName,
                request.WorkflowPath,
                request.WorkflowCommitSha,
                request.WorkflowBlobSha,
                request.ActionSourceSha,
                request.PayloadSha256,
                request.PayloadContinuitySha256,
                request.PayloadContinuityMode,
                request.BuildDiscriminator,
                request.ConfigPath.Value,
                instructionsPath!.Value,
                configBlob.BlobSha,
                instructionsBlob.BlobSha,
                configBlob.Bytes,
                instructionsBlob.Bytes,
                configSha,
                instructionsSha,
                policySha,
                publicationMode,
                inlineMinSeverity,
                DeepSeekAdapterContext.Provider,
                DeepSeekAdapterContext.Model,
                DeepSeekAdapterContext.Adapter,
                DeepSeekTransportPolicy.Endpoint,
                Thinking,
                PromptCaching,
                toolsetSha,
                limitsSha,
                RestrictedStateFormat.MaximumRetentionSeconds,
                InlineCommentCap,
                SecurityPolicy));
            return ActionHostTrustedPolicyMaterialization.Success(policy);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return ActionHostTrustedPolicyMaterialization.Failed(
                ActionHostTrustedPolicyFailure.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return ActionHostTrustedPolicyMaterialization.Failed(
                ActionHostTrustedPolicyFailure.Deadline);
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            return ActionHostTrustedPolicyMaterialization.Failed(
                ActionHostTrustedPolicyFailure.InternalInvariant);
        }
    }

    private static bool TryParseConfig(
        byte[] bytes,
        out ActionHostTrustedPolicyPath? instructionsPath,
        out ActionHostPublicationMode publicationMode,
        out ActionHostInlineSeverity inlineMinSeverity,
        out ActionHostTrustedPolicyFailure failure)
    {
        instructionsPath = null;
        publicationMode = default;
        inlineMinSeverity = default;
        failure = ActionHostTrustedPolicyFailure.MalformedConfig;
        if (bytes.Length > ConfigByteCap)
        {
            failure = ActionHostTrustedPolicyFailure.ConfigTooLarge;
            return false;
        }

        if (!IsStrictText(bytes, ConfigByteCap))
        {
            return false;
        }

        ActionHostTrustedPolicyDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(
                bytes,
                ActionHostTrustedPolicyJsonContext.Default
                    .ActionHostTrustedPolicyDocument);
        }
        catch (JsonException)
        {
            return false;
        }

        if (document is null ||
            !StringComparer.Ordinal.Equals(document.Schema, Schema) ||
            document.Publication is not { } publication)
        {
            return false;
        }

        if (!ActionHostTrustedPolicyPath.TryCreate(
                document.InstructionsPath,
                requireInstructionsSubtree: true,
                out instructionsPath))
        {
            failure = ActionHostTrustedPolicyFailure.InvalidInstructionsPath;
            return false;
        }

        publicationMode = publication.Mode switch
        {
            "sticky" => ActionHostPublicationMode.Sticky,
            "sticky_and_inline" =>
                ActionHostPublicationMode.StickyAndInline,
            _ => default,
        };
        if (publicationMode == default)
        {
            return false;
        }

        if (!publication.InlineMinSeverityPresent)
        {
            inlineMinSeverity = ActionHostInlineSeverity.High;
        }
        else
        {
            inlineMinSeverity = publication.InlineMinSeverity switch
            {
                "high" => ActionHostInlineSeverity.High,
                "critical" => ActionHostInlineSeverity.Critical,
                _ => default,
            };
        }

        return inlineMinSeverity != default;
    }

    private static bool IsStrictText(byte[] bytes, int maximumBytes)
    {
        if (bytes.Length is < 1 || bytes.Length > maximumBytes ||
            bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        {
            return false;
        }

        try
        {
            _ = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string PolicyIdentity(
        ActionHostTrustedPolicyRequest request,
        ActionHostTrustedPolicySourceReader.SourceBlob config,
        ActionHostTrustedPolicySourceReader.SourceBlob instructions,
        ActionHostTrustedPolicyPath instructionsPath,
        ActionHostPublicationMode publicationMode,
        ActionHostInlineSeverity inlineMinSeverity,
        string configSha,
        string instructionsSha,
        string toolsetSha,
        string limitsSha)
    {
        var writer = new Rfc8785Writer(4096);
        writer.WriteObjectStart();
        Write(ref writer, "action_source_sha", request.ActionSourceSha);
        Write(ref writer, "adapter_id", DeepSeekAdapterContext.Adapter);
        Write(ref writer, "build_discriminator", request.BuildDiscriminator);
        Write(ref writer, "config_blob_sha", config.BlobSha);
        Write(ref writer, "config_path", request.ConfigPath.Value);
        Write(ref writer, "config_sha256", configSha);
        Write(ref writer, "endpoint", DeepSeekTransportPolicy.Endpoint);
        Write(ref writer, "inline_min_severity", SeverityName(inlineMinSeverity));
        Write(ref writer, "instructions_blob_sha", instructions.BlobSha);
        Write(ref writer, "instructions_path", instructionsPath.Value);
        Write(ref writer, "instructions_sha256", instructionsSha);
        Write(ref writer, "limits_sha256", limitsSha);
        Write(ref writer, "maximum_inline_comments", InlineCommentCap);
        Write(ref writer, "model_id", DeepSeekAdapterContext.Model);
        Write(ref writer, "payload_sha256", request.PayloadContinuitySha256);
        Write(ref writer, "prompt_caching", PromptCaching);
        Write(ref writer, "provider_id", DeepSeekAdapterContext.Provider);
        Write(ref writer, "publication_mode", PublicationName(publicationMode));
        Write(ref writer, "repository_id", request.RepositoryId);
        Write(ref writer, "repository_name", request.RepositoryName);
        Write(ref writer, "security_policy_id", SecurityPolicy);
        Write(ref writer, "state_retention_seconds",
            RestrictedStateFormat.MaximumRetentionSeconds);
        Write(ref writer, "thinking", Thinking);
        Write(ref writer, "toolset_sha256", toolsetSha);
        Write(ref writer, "workflow_blob_sha", request.WorkflowBlobSha);
        Write(ref writer, "workflow_commit_sha", request.WorkflowCommitSha);
        Write(ref writer, "workflow_path", request.WorkflowPath);
        writer.WriteObjectEnd();
        return AgentCanonical.HashDomain(
            IdentityDomain,
            writer.ToImmutableArray().AsSpan());
    }

    private static string PublicationName(ActionHostPublicationMode mode) =>
        mode == ActionHostPublicationMode.Sticky
            ? "sticky"
            : "sticky_and_inline";

    private static string SeverityName(ActionHostInlineSeverity severity) =>
        severity == ActionHostInlineSeverity.High ? "high" : "critical";

    private static void Write(
        ref Rfc8785Writer writer,
        string name,
        string value)
    {
        writer.WriteProperty(name);
        writer.WriteString(value);
    }

    private static void Write(
        ref Rfc8785Writer writer,
        string name,
        long value)
    {
        writer.WriteProperty(name);
        writer.WriteNumber(value);
    }

    private static void Write(
        ref Rfc8785Writer writer,
        string name,
        bool value)
    {
        writer.WriteProperty(name);
        writer.WriteBoolean(value);
    }

    private static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;
}
