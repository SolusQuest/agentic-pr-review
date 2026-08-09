using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AgenticPrReview.Runtime.ActionHost.Contracts;

namespace AgenticPrReview.Runtime.ActionHost.Serialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ActionHostInputsDocument(
    string? GithubToken,
    string? ProviderApiKey,
    string? StateKey,
    string? PreviousStateKey,
    string? ConfigPath,
    string? PrNumber,
    string StateMode);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ActionHostLaunchDocument(
    ActionHostInputsDocument Inputs,
    string EventJsonPath,
    string EventJsonSha256,
    string RepositoryName,
    string RepositoryId,
    string RunId,
    string RunAttempt,
    string WorkflowPath,
    string WorkflowRef,
    string WorkflowSha,
    string ActionSourceSha,
    string PayloadSha256,
    string BuildDiscriminator,
    string Cancellation,
    string ArtifactBridgeEndpoint);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ActionHostStepSummaryDocument(
    string? ReviewedSha,
    string? PublicationUrl,
    int? FindingCount,
    string StateDisposition);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ActionHostAnnotationDocument(
    string Code,
    string Severity,
    string Message);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ActionHostCompletionDocument(
    string BuildDiscriminator,
    string Status,
    string ExitClass,
    int ProcessExitCode,
    ActionHostStepSummaryDocument Summary,
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

internal static class ActionHostJsonCodec
{
    internal static bool TryWriteLaunch(
        ActionHostLaunchContract? launch,
        out byte[] bytes)
    {
        bytes = [];
        if (launch is null)
        {
            return false;
        }

        var document = new ActionHostLaunchDocument(
            InputsDocument(launch.Inputs),
            launch.EventJsonPath,
            launch.EventJsonSha256,
            launch.RepositoryName,
            ActionHostContractValidation.CanonicalDecimal(
                launch.RepositoryId),
            ActionHostContractValidation.CanonicalDecimal(launch.RunId),
            ActionHostContractValidation.CanonicalDecimal(launch.RunAttempt),
            launch.WorkflowPath,
            launch.WorkflowRef,
            launch.WorkflowSha,
            launch.ActionSourceSha,
            launch.PayloadSha256,
            launch.BuildDiscriminator,
            CancellationString(launch.Cancellation),
            launch.ArtifactBridgeEndpoint);
        return TryWrite(
            document,
            ActionHostJsonContext.Default.ActionHostLaunchDocument,
            ActionHostContractBounds.MaximumLaunchDocumentBytes,
            out bytes);
    }

    internal static bool TryReadLaunch(
        byte[]? bytes,
        out ActionHostLaunchContract? launch,
        out ActionHostContractError error)
    {
        launch = null;
        error = ActionHostContractError.InvalidLaunch;
        if (!TryRead(
                bytes,
                ActionHostContractBounds.MaximumLaunchDocumentBytes,
                ActionHostJsonContext.Default.ActionHostLaunchDocument,
                out ActionHostLaunchDocument? document) ||
            document?.Inputs is null ||
            !ActionHostInputParser.TryCreateFromWire(
                document.Inputs.GithubToken,
                document.Inputs.ProviderApiKey,
                document.Inputs.StateKey,
                document.Inputs.PreviousStateKey,
                document.Inputs.ConfigPath,
                document.Inputs.PrNumber,
                document.Inputs.StateMode,
                out var inputs) ||
            !ActionHostContractValidation.TryParsePositiveInt64(
                document.RepositoryId,
                out var repositoryId) ||
            !ActionHostContractValidation.TryParsePositiveInt64(
                document.RunId,
                out var runId) ||
            !ActionHostContractValidation.TryParsePositiveInt32(
                document.RunAttempt,
                out var runAttempt) ||
            !TryParseCancellation(
                document.Cancellation,
                out var cancellation) ||
            !ActionHostLaunchContract.TryCreate(
                inputs,
                document.EventJsonPath,
                document.EventJsonSha256,
                document.RepositoryName,
                repositoryId,
                runId,
                runAttempt,
                document.WorkflowPath,
                document.WorkflowRef,
                document.WorkflowSha,
                document.ActionSourceSha,
                document.PayloadSha256,
                document.BuildDiscriminator,
                cancellation,
                document.ArtifactBridgeEndpoint,
                out launch))
        {
            return false;
        }

        error = ActionHostContractError.None;
        return true;
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

        var annotations = completion.Annotations
            .Select(annotation => new ActionHostAnnotationDocument(
                AnnotationCodeString(annotation.Code),
                AnnotationSeverityString(annotation.Severity),
                annotation.Message))
            .ToArray();
        var document = new ActionHostCompletionDocument(
            completion.BuildDiscriminator,
            StatusString(completion.Status),
            ExitClassString(completion.ExitClass),
            completion.ProcessExitCode,
            new ActionHostStepSummaryDocument(
                completion.Summary.ReviewedSha,
                completion.Summary.PublicationUrl,
                completion.Summary.FindingCount,
                StateDispositionString(
                    completion.Summary.StateDisposition)),
            annotations);
        return TryWrite(
            document,
            ActionHostJsonContext.Default.ActionHostCompletionDocument,
            ActionHostContractBounds.MaximumCompletionDocumentBytes,
            out bytes);
    }

    internal static bool TryReadCompletion(
        byte[]? bytes,
        out ActionHostCompletion? completion,
        out ActionHostContractError error)
    {
        completion = null;
        error = ActionHostContractError.InvalidCompletion;
        if (!TryRead(
                bytes,
                ActionHostContractBounds.MaximumCompletionDocumentBytes,
                ActionHostJsonContext.Default.ActionHostCompletionDocument,
                out ActionHostCompletionDocument? document) ||
            document?.Summary is null ||
            document.Annotations is null ||
            !TryParseStatus(document.Status, out var status) ||
            !TryParseExitClass(document.ExitClass, out var wireExitClass) ||
            !TryParseStateDisposition(
                document.Summary.StateDisposition,
                out var stateDisposition) ||
            !ActionHostStepSummary.TryCreate(
                document.Summary.ReviewedSha,
                document.Summary.PublicationUrl,
                document.Summary.FindingCount,
                stateDisposition,
                out var summary) ||
            !TryMapAnnotations(document.Annotations, out var annotations) ||
            !ActionHostCompletion.TryCreate(
                document.BuildDiscriminator,
                status,
                summary,
                annotations,
                out completion) ||
            completion!.ExitClass != wireExitClass ||
            completion.ProcessExitCode != document.ProcessExitCode)
        {
            completion = null;
            return false;
        }

        error = ActionHostContractError.None;
        return true;
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
            document.Payload is null;
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
            document.Payload is null;
    }

    private static ActionHostInputsDocument InputsDocument(
        ActionHostInputs inputs) => new(
        inputs.GitHubToken?.ExportForPrivateLaunch(),
        inputs.ProviderApiKey?.ExportForPrivateLaunch(),
        inputs.StateKey?.ExportForPrivateLaunch(),
        inputs.PreviousStateKey?.ExportForPrivateLaunch(),
        inputs.ConfigPath,
        inputs.PullRequestNumber is { } pullRequestNumber
            ? ActionHostContractValidation.CanonicalDecimal(
                pullRequestNumber)
            : null,
        StateModeString(inputs.StateMode));

    private static bool TryMapAnnotations(
        ActionHostAnnotationDocument[] documents,
        out ActionHostAnnotation[] annotations)
    {
        annotations = [];
        if (documents.Length > 1)
        {
            return false;
        }

        var mapped = new ActionHostAnnotation[documents.Length];
        for (var index = 0; index < documents.Length; index++)
        {
            var document = documents[index];
            if (document is null ||
                !TryParseAnnotationCode(document.Code, out var code) ||
                !TryParseAnnotationSeverity(
                    document.Severity,
                    out var severity) ||
                !ActionHostAnnotation.TryCreate(code, out var annotation) ||
                annotation!.Severity != severity ||
                !StringComparer.Ordinal.Equals(
                    annotation.Message,
                    document.Message))
            {
                return false;
            }

            mapped[index] = annotation;
        }

        annotations = mapped;
        return true;
    }

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

    private static bool TryParseStatus(
        string? value,
        out ActionHostStatus parsed)
    {
        parsed = value switch
        {
            "reviewed" => ActionHostStatus.Reviewed,
            "reviewed_with_inline_warnings" =>
                ActionHostStatus.ReviewedWithInlineWarnings,
            "skipped_untrusted_event" =>
                ActionHostStatus.SkippedUntrustedEvent,
            "skipped_fork" => ActionHostStatus.SkippedFork,
            "skipped_draft" => ActionHostStatus.SkippedDraft,
            "skipped_closed" => ActionHostStatus.SkippedClosed,
            "configuration_invalid" =>
                ActionHostStatus.ConfigurationInvalid,
            "authorization_failed" => ActionHostStatus.AuthorizationFailed,
            "credentials_missing" => ActionHostStatus.CredentialsMissing,
            "snapshot_incomplete" => ActionHostStatus.SnapshotIncomplete,
            "provider_failed" => ActionHostStatus.ProviderFailed,
            "agent_result_invalid" => ActionHostStatus.AgentResultInvalid,
            "stale_head" => ActionHostStatus.StaleHead,
            "state_conflict" => ActionHostStatus.StateConflict,
            "sticky_publication_failed" =>
                ActionHostStatus.StickyPublicationFailed,
            "outcome_ambiguous" => ActionHostStatus.OutcomeAmbiguous,
            "cancelled" => ActionHostStatus.Cancelled,
            "internal_failure" => ActionHostStatus.InternalFailure,
            _ => 0,
        };
        return parsed != 0;
    }

    private static string ExitClassString(
        ActionHostExitClass value) => value switch
        {
            ActionHostExitClass.Success => "success",
            ActionHostExitClass.Contract => "contract",
            ActionHostExitClass.Authorization => "authorization",
            ActionHostExitClass.Snapshot => "snapshot",
            ActionHostExitClass.Provider => "provider",
            ActionHostExitClass.Agent => "agent",
            ActionHostExitClass.State => "state",
            ActionHostExitClass.Publication => "publication",
            ActionHostExitClass.Reconciliation => "reconciliation",
            ActionHostExitClass.Cancellation => "cancellation",
            ActionHostExitClass.Internal => "internal",
            _ => throw new InvalidOperationException(
                "Invalid ActionHost exit class."),
        };

    private static bool TryParseExitClass(
        string? value,
        out ActionHostExitClass parsed)
    {
        parsed = value switch
        {
            "success" => ActionHostExitClass.Success,
            "contract" => ActionHostExitClass.Contract,
            "authorization" => ActionHostExitClass.Authorization,
            "snapshot" => ActionHostExitClass.Snapshot,
            "provider" => ActionHostExitClass.Provider,
            "agent" => ActionHostExitClass.Agent,
            "state" => ActionHostExitClass.State,
            "publication" => ActionHostExitClass.Publication,
            "reconciliation" => ActionHostExitClass.Reconciliation,
            "cancellation" => ActionHostExitClass.Cancellation,
            "internal" => ActionHostExitClass.Internal,
            _ => 0,
        };
        return parsed != 0;
    }

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

    private static bool TryParseStateDisposition(
        string? value,
        out ActionHostStateDisposition parsed)
    {
        parsed = value switch
        {
            "accepted" => ActionHostStateDisposition.Accepted,
            "not_accessed" => ActionHostStateDisposition.NotAccessed,
            "not_committed" => ActionHostStateDisposition.NotCommitted,
            "conflict" => ActionHostStateDisposition.Conflict,
            _ => 0,
        };
        return parsed != 0;
    }

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

    private static bool TryParseAnnotationCode(
        string? value,
        out ActionHostAnnotationCode parsed)
    {
        parsed = value switch
        {
            "inline_publication_incomplete" =>
                ActionHostAnnotationCode.InlinePublicationIncomplete,
            "configuration_invalid" =>
                ActionHostAnnotationCode.ConfigurationInvalid,
            "authorization_failed" =>
                ActionHostAnnotationCode.AuthorizationFailed,
            "credentials_missing" =>
                ActionHostAnnotationCode.CredentialsMissing,
            "snapshot_incomplete" =>
                ActionHostAnnotationCode.SnapshotIncomplete,
            "provider_failed" => ActionHostAnnotationCode.ProviderFailed,
            "agent_result_invalid" =>
                ActionHostAnnotationCode.AgentResultInvalid,
            "stale_head" => ActionHostAnnotationCode.StaleHead,
            "state_conflict" => ActionHostAnnotationCode.StateConflict,
            "sticky_publication_failed" =>
                ActionHostAnnotationCode.StickyPublicationFailed,
            "outcome_ambiguous" =>
                ActionHostAnnotationCode.OutcomeAmbiguous,
            "cancelled" => ActionHostAnnotationCode.Cancelled,
            "internal_failure" => ActionHostAnnotationCode.InternalFailure,
            _ => 0,
        };
        return parsed != 0;
    }

    private static string AnnotationSeverityString(
        ActionHostAnnotationSeverity value) => value switch
        {
            ActionHostAnnotationSeverity.Warning => "warning",
            ActionHostAnnotationSeverity.Error => "error",
            _ => throw new InvalidOperationException(
                "Invalid ActionHost annotation severity."),
        };

    private static bool TryParseAnnotationSeverity(
        string? value,
        out ActionHostAnnotationSeverity parsed)
    {
        parsed = value switch
        {
            "warning" => ActionHostAnnotationSeverity.Warning,
            "error" => ActionHostAnnotationSeverity.Error,
            _ => 0,
        };
        return parsed != 0;
    }
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
