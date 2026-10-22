using System.Text;
using System.Globalization;
using AgenticPrReview.Runtime.Agent.Core;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AgenticPrReview.Runtime.ActionHost.Contracts;

namespace AgenticPrReview.Runtime.ActionHost.Serialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ActionHostRawInputDocument(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Value)
{
    public override string ToString() => "[PRIVATE]";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ActionHostFactsDocument(
    [property: JsonRequired] string EventJsonPath,
    [property: JsonRequired] string EventJsonSha256,
    [property: JsonRequired] string RepositoryName,
    [property: JsonRequired] string RepositoryId,
    [property: JsonRequired] string RunId,
    [property: JsonRequired] string RunAttempt,
    [property: JsonRequired] string WorkflowPath,
    [property: JsonRequired] string WorkflowRef,
    [property: JsonRequired] string WorkflowSha,
    [property: JsonRequired] string ActionSourceSha,
    [property: JsonRequired] string PayloadSha256)
{
    public override string ToString() => "[PRIVATE]";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ActionHostLaunchDocument(
    [property: JsonRequired] string Protocol,
    [property: JsonRequired] string BuildDiscriminator,
    [property: JsonRequired] ActionHostRawInputDocument[] Inputs,
    [property: JsonRequired] ActionHostFactsDocument Facts,
    [property: JsonRequired] string UploadEndpoint,
    [property: JsonRequired] string Cancellation)
{
    public override string ToString() => "[PRIVATE]";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ActionHostAnnotationDocument(string Code, string Severity, string Message);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ActionHostOutputDocument(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("termination-reason")] string TerminationReason,
    [property: JsonPropertyName("model-calls")] string? ModelCalls,
    [property: JsonPropertyName("provider-attempts")] string? ProviderAttempts,
    [property: JsonPropertyName("provider-retries")] string? ProviderRetries,
    [property: JsonPropertyName("provider-failed-attempts")] string? ProviderFailedAttempts,
    [property: JsonPropertyName("provider-unknown-usage-attempts")] string? ProviderUnknownUsageAttempts,
    [property: JsonPropertyName("provider-unknown-cache-partition-attempts")] string? ProviderUnknownCachePartitionAttempts,
    [property: JsonPropertyName("attempt-accounting-completeness")] string AttemptAccountingCompleteness,
    [property: JsonPropertyName("usage-completeness")] string UsageCompleteness,
    [property: JsonPropertyName("input-tokens")] string? InputTokens,
    [property: JsonPropertyName("input-cache-hit-tokens")] string? InputCacheHitTokens,
    [property: JsonPropertyName("input-cache-miss-tokens")] string? InputCacheMissTokens,
    [property: JsonPropertyName("output-tokens")] string? OutputTokens);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ActionHostCompletionDocument(
    string Protocol,
    string BuildDiscriminator,
    int ProcessExitCode,
    ActionHostOutputDocument Outputs,
    string Summary,
    ActionHostAnnotationDocument[] Annotations);

[JsonSerializable(typeof(ActionHostLaunchDocument))]
[JsonSerializable(typeof(ActionHostCompletionDocument))]
[JsonSerializable(typeof(
    ActionHostPrivateCommandEnvelope<ActionHostNoPrivateCommandDocument>))]
[JsonSerializable(typeof(
    ActionHostPrivateCommandResultEnvelope<
        ActionHostNoPrivateCommandResultDocument>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    GenerationMode = JsonSourceGenerationMode.Default)]
internal sealed partial class ActionHostJsonContext : JsonSerializerContext;

internal static partial class ActionHostJsonCodec
{
    internal const string RunProtocol = "apr.cli.run.cr2";

    internal static bool TryWriteLaunch(
        ActionHostLaunchContract? launch,
        out byte[] bytes)
    {
        bytes = [];
        if (launch is null) return false;
        var document = new ActionHostLaunchDocument(
            RunProtocol,
            launch.BuildDiscriminator,
            InputsDocument(launch.Inputs),
            new ActionHostFactsDocument(
                launch.EventJsonPath,
                launch.EventJsonSha256,
                launch.RepositoryName,
                ActionHostContractValidation.CanonicalDecimal(launch.RepositoryId),
                ActionHostContractValidation.CanonicalDecimal(launch.RunId),
                ActionHostContractValidation.CanonicalDecimal(launch.RunAttempt),
                launch.WorkflowPath,
                launch.WorkflowRef,
                launch.WorkflowSha,
                launch.ActionSourceSha,
                launch.PayloadSha256),
            launch.ArtifactBridgeEndpoint,
            CancellationString(launch.Cancellation));
        return TryWrite(document, ActionHostJsonContext.Default.ActionHostLaunchDocument,
            ActionHostContractBounds.MaximumLaunchDocumentBytes, out bytes);
    }

    internal static bool TryReadLaunch(
        byte[]? bytes,
        out ActionHostLaunchContract? launch,
        out ActionHostContractError error,
        string? expectedBuildDiscriminator = null)
    {
        launch = null;
        error = ActionHostContractError.InvalidLaunch;
        if (!TryRead(bytes, ActionHostContractBounds.MaximumLaunchDocumentBytes,
                ActionHostJsonContext.Default.ActionHostLaunchDocument,
                out ActionHostLaunchDocument? document) ||
            document?.Inputs is null || document.Facts is null ||
            !StringComparer.Ordinal.Equals(document.Protocol, RunProtocol) ||
            (expectedBuildDiscriminator is not null &&
                !StringComparer.Ordinal.Equals(document.BuildDiscriminator, expectedBuildDiscriminator)) ||
            document.Inputs.Any(entry => entry is null ||
                !ActionHostContractValidation.TryGetUtf8ByteCount(entry.Name, out _) ||
                !ActionHostContractValidation.TryGetUtf8ByteCount(entry.Value, out _)) ||
            !ActionHostContractValidation.TryParsePositiveInt64(document.Facts.RepositoryId, out var repositoryId) ||
            !ActionHostContractValidation.TryParsePositiveInt64(document.Facts.RunId, out var runId) ||
            !ActionHostContractValidation.TryParsePositiveInt32(document.Facts.RunAttempt, out var runAttempt) ||
            !TryParseCancellation(document.Cancellation, out var cancellation) ||
            !ActionHostLaunchContract.AreFactsValid(
                document.Facts.EventJsonPath, document.Facts.EventJsonSha256,
                document.Facts.RepositoryName, repositoryId, runId, runAttempt,
                document.Facts.WorkflowPath, document.Facts.WorkflowRef, document.Facts.WorkflowSha,
                document.Facts.ActionSourceSha, document.Facts.PayloadSha256,
                document.BuildDiscriminator, cancellation, document.UploadEndpoint))
        {
            return false;
        }

        // Only admitted wire/facts/build can yield a product-input failure.
        if (!ActionHostInputParser.TryParse(document.Inputs
                .Select(entry => new ActionHostRawInput(entry.Name, entry.Value)).ToArray(),
                out var inputs, out _))
        {
            error = ActionHostContractError.InvalidInputs;
            return false;
        }
        try
        {
            if (!ActionHostLaunchContract.TryCreate(inputs,
                    document.Facts.EventJsonPath, document.Facts.EventJsonSha256,
                    document.Facts.RepositoryName, repositoryId, runId, runAttempt,
                    document.Facts.WorkflowPath, document.Facts.WorkflowRef, document.Facts.WorkflowSha,
                    document.Facts.ActionSourceSha, document.Facts.PayloadSha256,
                    document.BuildDiscriminator, cancellation, document.UploadEndpoint, out launch))
            {
                return false;
            }
            error = ActionHostContractError.None;
            return true;
        }
        finally
        {
            if (launch is null) inputs?.Dispose();
        }
    }

    internal static bool TryWriteCompletion(
        ActionHostCompletion? completion,
        out byte[] bytes)
    {
        bytes = [];
        if (completion is null)
        {
            return false;
        }

        var document = ProjectCompletion(completion);
        return TryWrite(
            document,
            ActionHostJsonContext.Default.ActionHostCompletionDocument,
            ActionHostContractBounds.MaximumCompletionDocumentBytes,
            out bytes);
    }

    internal static bool TryWriteNoPrivateCommand(
        string buildDiscriminator,
        out byte[] bytes)
    {
        var typeInfo = RequireTypeInfo<
            ActionHostPrivateCommandEnvelope<
                ActionHostNoPrivateCommandDocument>>(
            ActionHostJsonContext.Default);
        return ActionHostPrivateCommandCodec.TryWriteCommand(
            new ActionHostPrivateCommandEnvelope<
                ActionHostNoPrivateCommandDocument>(
                buildDiscriminator,
                null),
            buildDiscriminator,
            typeInfo,
            out bytes);
    }

    internal static bool TryReadNoPrivateCommand(
        byte[]? bytes,
        string buildDiscriminator)
    {
        var typeInfo = RequireTypeInfo<
            ActionHostPrivateCommandEnvelope<
                ActionHostNoPrivateCommandDocument>>(
            ActionHostJsonContext.Default);
        return ActionHostPrivateCommandCodec.TryReadCommand(
                bytes,
                buildDiscriminator,
                typeInfo,
                out ActionHostPrivateCommandEnvelope<
                    ActionHostNoPrivateCommandDocument>? document) &&
            document is { Payload: null };
    }

    internal static bool TryWriteNoPrivateCommandResult(
        string buildDiscriminator,
        out byte[] bytes)
    {
        var typeInfo = RequireTypeInfo<
            ActionHostPrivateCommandResultEnvelope<
                ActionHostNoPrivateCommandResultDocument>>(
            ActionHostJsonContext.Default);
        return ActionHostPrivateCommandCodec.TryWriteResult(
            new ActionHostPrivateCommandResultEnvelope<
                ActionHostNoPrivateCommandResultDocument>(
                buildDiscriminator,
                null),
            buildDiscriminator,
            typeInfo,
            out bytes);
    }

    internal static bool TryReadNoPrivateCommandResult(
        byte[]? bytes,
        string buildDiscriminator)
    {
        var typeInfo = RequireTypeInfo<
            ActionHostPrivateCommandResultEnvelope<
                ActionHostNoPrivateCommandResultDocument>>(
            ActionHostJsonContext.Default);
        return ActionHostPrivateCommandCodec.TryReadResult(
                bytes,
                buildDiscriminator,
                typeInfo,
                out ActionHostPrivateCommandResultEnvelope<
                    ActionHostNoPrivateCommandResultDocument>? document) &&
            document is { Payload: null };
    }

    private static string? Decimal(long? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string CompletenessString(AccountingCompleteness value) => value switch
    {
        AccountingCompleteness.Complete => "complete",
        AccountingCompleteness.Partial => "partial",
        AccountingCompleteness.Unavailable => "unavailable",
        _ => throw new InvalidOperationException("Invalid accounting completeness."),
    };

    private static string TerminationString(ActionHostTerminationReason value) => value switch
    {
        ActionHostTerminationReason.ReviewCompleted => "review_completed",
        ActionHostTerminationReason.NotStarted => "not_started",
        ActionHostTerminationReason.ProviderFailure => "provider_failure",
        ActionHostTerminationReason.Cancelled => "cancelled",
        ActionHostTerminationReason.DeadlineExceeded => "deadline_exceeded",
        ActionHostTerminationReason.ModelLimit => "model_limit",
        ActionHostTerminationReason.ToolLimit => "tool_limit",
        ActionHostTerminationReason.TokenLimit => "token_limit",
        ActionHostTerminationReason.RequestLimit => "request_limit",
        ActionHostTerminationReason.ResponseLimit => "response_limit",
        ActionHostTerminationReason.ContextLimit => "context_limit",
        ActionHostTerminationReason.ResourceLimit => "resource_limit",
        ActionHostTerminationReason.InvalidResult => "invalid_result",
        ActionHostTerminationReason.HostFailure => "host_failure",
        _ => throw new InvalidOperationException("Invalid termination reason."),
    };

    private static ActionHostRawInputDocument[] InputsDocument(ActionHostInputs inputs) =>
    [
        new(ActionHostInputParser.GitHubTokenName, inputs.GitHubToken?.ExportForPrivateLaunch() ?? ""),
        new(ActionHostInputParser.ProviderApiKeyName, inputs.ProviderApiKey?.ExportForPrivateLaunch() ?? ""),
        new(ActionHostInputParser.StateKeyName, inputs.StateKey?.ExportForPrivateLaunch() ?? ""),
        new(ActionHostInputParser.PreviousStateKeyName, inputs.PreviousStateKey?.ExportForPrivateLaunch() ?? ""),
        new(ActionHostInputParser.ConfigPathName, inputs.ConfigPath),
        new(ActionHostInputParser.PullRequestNumberName, inputs.PullRequestNumber is { } number
            ? ActionHostContractValidation.CanonicalDecimal(number) : ""),
        new(ActionHostInputParser.StateModeName, StateModeString(inputs.StateMode)),
    ];

    private static bool TryRead<T>(
        byte[]? bytes,
        int maximumBytes,
        JsonTypeInfo<T> typeInfo,
        out T? value)
        where T : class
    {
        value = null;
        if (!ActionHostContractValidation.IsStrictUtf8Document(
                bytes,
                maximumBytes))
        {
            return false;
        }

        try
        {
            value = JsonSerializer.Deserialize(bytes!, typeInfo);
            return value is not null;
        }
        catch (Exception exception) when (exception is JsonException or
            NotSupportedException or
            DecoderFallbackException)
        {
            value = null;
            return false;
        }
    }

    private static bool TryWrite<T>(
        T value,
        JsonTypeInfo<T> typeInfo,
        int maximumBytes,
        out byte[] bytes)
    {
        bytes = [];
        try
        {
            var serialized = JsonSerializer.SerializeToUtf8Bytes(
                value,
                typeInfo);
            if (serialized.Length > maximumBytes)
            {
                return false;
            }

            bytes = serialized;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or
            NotSupportedException or
            EncoderFallbackException)
        {
            return false;
        }
    }

    private static JsonTypeInfo<T> RequireTypeInfo<T>(
        JsonSerializerContext context) =>
        context.GetTypeInfo(typeof(T)) as JsonTypeInfo<T> ??
        throw new InvalidOperationException(
            "The required generated ActionHost metadata is missing.");

    private static string StateModeString(ActionHostStateMode value) =>
        value switch
        {
            ActionHostStateMode.Auto => "auto",
            ActionHostStateMode.Reset => "reset",
            _ => throw new InvalidOperationException(
                "Invalid ActionHost state mode."),
        };

    private static string CancellationString(
        ActionHostCancellationState value) => value switch
        {
            ActionHostCancellationState.Active => "active",
            ActionHostCancellationState.Requested => "requested",
            _ => throw new InvalidOperationException(
                "Invalid ActionHost cancellation state."),
        };

    private static bool TryParseCancellation(
        string? value,
        out ActionHostCancellationState parsed)
    {
        parsed = value switch
        {
            "active" => ActionHostCancellationState.Active,
            "requested" => ActionHostCancellationState.Requested,
            _ => 0,
        };
        return parsed != 0;
    }

    private static string StatusString(ActionHostStatus value) => value switch
    {
        ActionHostStatus.Reviewed => "reviewed",
        ActionHostStatus.ReviewedWithInlineWarnings =>
            "reviewed_with_inline_warnings",
        ActionHostStatus.SkippedUntrustedEvent => "skipped_untrusted_event",
        ActionHostStatus.SkippedFork => "skipped_fork",
        ActionHostStatus.SkippedDraft => "skipped_draft",
        ActionHostStatus.SkippedClosed => "skipped_closed",
        ActionHostStatus.ConfigurationInvalid => "configuration_invalid",
        ActionHostStatus.AuthorizationFailed => "authorization_failed",
        ActionHostStatus.CredentialsMissing => "credentials_missing",
        ActionHostStatus.SnapshotIncomplete => "snapshot_incomplete",
        ActionHostStatus.ProviderFailed => "provider_failed",
        ActionHostStatus.AgentResultInvalid => "agent_result_invalid",
        ActionHostStatus.StaleHead => "stale_head",
        ActionHostStatus.StateConflict => "state_conflict",
        ActionHostStatus.StickyPublicationFailed =>
            "sticky_publication_failed",
        ActionHostStatus.OutcomeAmbiguous => "outcome_ambiguous",
        ActionHostStatus.Cancelled => "cancelled",
        ActionHostStatus.InternalFailure => "internal_failure",
        _ => throw new InvalidOperationException(
            "Invalid ActionHost status."),
    };

    private static string StateDispositionString(
        ActionHostStateDisposition value) => value switch
        {
            ActionHostStateDisposition.Accepted => "accepted",
            ActionHostStateDisposition.NotAccessed => "not_accessed",
            ActionHostStateDisposition.NotCommitted => "not_committed",
            ActionHostStateDisposition.Conflict => "conflict",
            _ => throw new InvalidOperationException(
                "Invalid ActionHost state disposition."),
        };

    private static string AnnotationCodeString(
        ActionHostAnnotationCode value) => value switch
        {
            ActionHostAnnotationCode.InlinePublicationIncomplete =>
                "inline_publication_incomplete",
            ActionHostAnnotationCode.ConfigurationInvalid =>
                "configuration_invalid",
            ActionHostAnnotationCode.AuthorizationFailed =>
                "authorization_failed",
            ActionHostAnnotationCode.CredentialsMissing =>
                "credentials_missing",
            ActionHostAnnotationCode.SnapshotIncomplete =>
                "snapshot_incomplete",
            ActionHostAnnotationCode.ProviderFailed => "provider_failed",
            ActionHostAnnotationCode.AgentResultInvalid =>
                "agent_result_invalid",
            ActionHostAnnotationCode.StaleHead => "stale_head",
            ActionHostAnnotationCode.StateConflict => "state_conflict",
            ActionHostAnnotationCode.StickyPublicationFailed =>
                "sticky_publication_failed",
            ActionHostAnnotationCode.OutcomeAmbiguous => "outcome_ambiguous",
            ActionHostAnnotationCode.Cancelled => "cancelled",
            ActionHostAnnotationCode.InternalFailure => "internal_failure",
            _ => throw new InvalidOperationException(
                "Invalid ActionHost annotation code."),
        };

    private static string AnnotationSeverityString(
        ActionHostAnnotationSeverity value) => value switch
        {
            ActionHostAnnotationSeverity.Warning => "warning",
            ActionHostAnnotationSeverity.Error => "error",
            _ => throw new InvalidOperationException(
                "Invalid ActionHost annotation severity."),
        };


}

internal static class ActionHostPrivateCommandCodec
{
    internal static bool TryWriteCommand<TCommand>(
        ActionHostPrivateCommandEnvelope<TCommand>? document,
        string expectedBuildDiscriminator,
        JsonTypeInfo<ActionHostPrivateCommandEnvelope<TCommand>> typeInfo,
        out byte[] bytes)
        where TCommand : class, IActionHostPrivateCommandDocument =>
        TryWrite(
            document,
            expectedBuildDiscriminator,
            typeInfo,
            out bytes);

    internal static bool TryReadCommand<TCommand>(
        byte[]? bytes,
        string expectedBuildDiscriminator,
        JsonTypeInfo<ActionHostPrivateCommandEnvelope<TCommand>> typeInfo,
        out ActionHostPrivateCommandEnvelope<TCommand>? document)
        where TCommand : class, IActionHostPrivateCommandDocument =>
        TryRead(
            bytes,
            expectedBuildDiscriminator,
            typeInfo,
            out document);

    internal static bool TryWriteResult<TResult>(
        ActionHostPrivateCommandResultEnvelope<TResult>? document,
        string expectedBuildDiscriminator,
        JsonTypeInfo<ActionHostPrivateCommandResultEnvelope<TResult>> typeInfo,
        out byte[] bytes)
        where TResult : class, IActionHostPrivateCommandResultDocument =>
        TryWrite(
            document,
            expectedBuildDiscriminator,
            typeInfo,
            out bytes);

    internal static bool TryReadResult<TResult>(
        byte[]? bytes,
        string expectedBuildDiscriminator,
        JsonTypeInfo<ActionHostPrivateCommandResultEnvelope<TResult>> typeInfo,
        out ActionHostPrivateCommandResultEnvelope<TResult>? document)
        where TResult : class, IActionHostPrivateCommandResultDocument =>
        TryRead(
            bytes,
            expectedBuildDiscriminator,
            typeInfo,
            out document);

    private static bool TryWrite<TDocument>(
        TDocument? document,
        string expectedBuildDiscriminator,
        JsonTypeInfo<TDocument> typeInfo,
        out byte[] bytes)
        where TDocument : class
    {
        bytes = [];
        if (document is null ||
            !HasStrictGeneratedOptions(typeInfo) ||
            !TryGetBuildDiscriminator(document, out var buildDiscriminator) ||
            !StringComparer.Ordinal.Equals(
                buildDiscriminator,
                expectedBuildDiscriminator) ||
            !ActionHostContractValidation.IsBuildDiscriminator(
                expectedBuildDiscriminator))
        {
            return false;
        }

        try
        {
            var serialized = JsonSerializer.SerializeToUtf8Bytes(
                document,
                typeInfo);
            if (serialized.Length >
                ActionHostContractBounds.MaximumPrivateCommandDocumentBytes)
            {
                return false;
            }

            bytes = serialized;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or
            NotSupportedException or
            EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryRead<TDocument>(
        byte[]? bytes,
        string expectedBuildDiscriminator,
        JsonTypeInfo<TDocument> typeInfo,
        out TDocument? document)
        where TDocument : class
    {
        document = null;
        if (!HasStrictGeneratedOptions(typeInfo) ||
            !ActionHostContractValidation.IsBuildDiscriminator(
                expectedBuildDiscriminator) ||
            !ActionHostContractValidation.IsStrictUtf8Document(
                bytes,
                ActionHostContractBounds.MaximumPrivateCommandDocumentBytes))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(bytes!, typeInfo);
            if (parsed is null ||
                !TryGetBuildDiscriminator(
                    parsed,
                    out var buildDiscriminator) ||
                !StringComparer.Ordinal.Equals(
                    buildDiscriminator,
                    expectedBuildDiscriminator))
            {
                return false;
            }

            document = parsed;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or
            NotSupportedException or
            DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool HasStrictGeneratedOptions<T>(JsonTypeInfo<T> typeInfo)
    {
        var options = typeInfo.Options;
        return typeInfo.Kind != JsonTypeInfoKind.None &&
            !options.PropertyNameCaseInsensitive &&
            options.UnmappedMemberHandling ==
                JsonUnmappedMemberHandling.Disallow &&
            !options.AllowDuplicateProperties &&
            options.DefaultIgnoreCondition == JsonIgnoreCondition.Never &&
            ReferenceEquals(
                options.PropertyNamingPolicy,
                JsonNamingPolicy.SnakeCaseLower);
    }

    private static bool TryGetBuildDiscriminator<TDocument>(
        TDocument document,
        out string? buildDiscriminator)
        where TDocument : class
    {
        buildDiscriminator = document switch
        {
            IActionHostPrivateEnvelope envelope =>
                envelope.BuildDiscriminator,
            _ => null,
        };
        return buildDiscriminator is not null;
    }
}
