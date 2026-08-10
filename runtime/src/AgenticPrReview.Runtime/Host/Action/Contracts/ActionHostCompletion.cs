using System.Collections.Immutable;

namespace AgenticPrReview.Runtime.ActionHost.Contracts;

internal enum ActionHostStatus
{
    Reviewed = 1,
    ReviewedWithInlineWarnings,
    SkippedUntrustedEvent,
    SkippedFork,
    SkippedDraft,
    SkippedClosed,
    ConfigurationInvalid,
    AuthorizationFailed,
    CredentialsMissing,
    SnapshotIncomplete,
    ProviderFailed,
    AgentResultInvalid,
    StaleHead,
    StateConflict,
    StickyPublicationFailed,
    OutcomeAmbiguous,
    Cancelled,
    InternalFailure,
}

internal enum ActionHostExitClass
{
    Success = 1,
    Contract,
    Authorization,
    Snapshot,
    Provider,
    Agent,
    State,
    Publication,
    Reconciliation,
    Cancellation,
    Internal,
}

/// <summary>
/// Describes only whether the final result accepted state. NotAccessed means no
/// state activation occurred; NotCommitted means no successor was accepted.
/// </summary>
internal enum ActionHostStateDisposition
{
    Accepted = 1,
    NotAccessed,
    NotCommitted,
    Conflict,
}

internal enum ActionHostAnnotationSeverity
{
    Warning = 1,
    Error,
}

internal enum ActionHostAnnotationCode
{
    InlinePublicationIncomplete = 1,
    ConfigurationInvalid,
    AuthorizationFailed,
    CredentialsMissing,
    SnapshotIncomplete,
    ProviderFailed,
    AgentResultInvalid,
    StaleHead,
    StateConflict,
    StickyPublicationFailed,
    OutcomeAmbiguous,
    Cancelled,
    InternalFailure,
}

internal sealed class ActionHostAnnotation
{
    private ActionHostAnnotation(
        ActionHostAnnotationCode code,
        ActionHostAnnotationSeverity severity,
        string message)
    {
        Code = code;
        Severity = severity;
        Message = message;
    }

    internal ActionHostAnnotationCode Code { get; }

    internal ActionHostAnnotationSeverity Severity { get; }

    internal string Message { get; }

    internal ActionHostPrivacyClass Privacy =>
        ActionHostPrivacyClass.WorkflowPresentation;

    internal static bool TryCreate(
        ActionHostAnnotationCode code,
        out ActionHostAnnotation? annotation)
    {
        annotation = code switch
        {
            ActionHostAnnotationCode.InlinePublicationIncomplete => new(
                code,
                ActionHostAnnotationSeverity.Warning,
                "Some inline annotations could not be published."),
            ActionHostAnnotationCode.ConfigurationInvalid => new(
                code,
                ActionHostAnnotationSeverity.Error,
                "Action configuration is invalid."),
            ActionHostAnnotationCode.AuthorizationFailed => new(
                code,
                ActionHostAnnotationSeverity.Error,
                "Action authorization failed."),
            ActionHostAnnotationCode.CredentialsMissing => new(
                code,
                ActionHostAnnotationSeverity.Error,
                "Required credentials are unavailable."),
            ActionHostAnnotationCode.SnapshotIncomplete => new(
                code,
                ActionHostAnnotationSeverity.Error,
                "The reviewed snapshot could not be completed."),
            ActionHostAnnotationCode.ProviderFailed => new(
                code,
                ActionHostAnnotationSeverity.Error,
                "The review provider failed."),
            ActionHostAnnotationCode.AgentResultInvalid => new(
                code,
                ActionHostAnnotationSeverity.Error,
                "The review result was invalid."),
            ActionHostAnnotationCode.StaleHead => new(
                code,
                ActionHostAnnotationSeverity.Error,
                "The pull request head changed before publication."),
            ActionHostAnnotationCode.StateConflict => new(
                code,
                ActionHostAnnotationSeverity.Error,
                "Review state is in conflict."),
            ActionHostAnnotationCode.StickyPublicationFailed => new(
                code,
                ActionHostAnnotationSeverity.Error,
                "The review summary could not be published."),
            ActionHostAnnotationCode.OutcomeAmbiguous => new(
                code,
                ActionHostAnnotationSeverity.Error,
                "A side effect could not be reconciled."),
            ActionHostAnnotationCode.Cancelled => new(
                code,
                ActionHostAnnotationSeverity.Error,
                "The review was cancelled before a side effect began."),
            ActionHostAnnotationCode.InternalFailure => new(
                code,
                ActionHostAnnotationSeverity.Error,
                "The review host failed."),
            _ => null,
        };

        return annotation is not null &&
            ActionHostContractValidation.IsWorkflowPresentationText(
                annotation.Message,
                256);
    }
}

internal sealed class ActionHostStepSummary
{
    private ActionHostStepSummary(
        string? reviewedSha,
        string? publicationUrl,
        int? findingCount,
        ActionHostStateDisposition stateDisposition)
    {
        ReviewedSha = reviewedSha;
        PublicationUrl = publicationUrl;
        FindingCount = findingCount;
        StateDisposition = stateDisposition;
    }

    internal string? ReviewedSha { get; }

    internal string? PublicationUrl { get; }

    internal int? FindingCount { get; }

    internal ActionHostStateDisposition StateDisposition { get; }

    internal ActionHostPrivacyClass Privacy =>
        ActionHostPrivacyClass.WorkflowPresentation;

    internal static bool TryCreate(
        string? reviewedSha,
        string? publicationUrl,
        int? findingCount,
        ActionHostStateDisposition stateDisposition,
        out ActionHostStepSummary? summary)
    {
        summary = null;
        if (reviewedSha is not null &&
                !ActionHostContractValidation.IsCommitSha(reviewedSha) ||
            publicationUrl is not null &&
                !ActionHostContractValidation.IsPublicationUrl(
                    publicationUrl) ||
            findingCount is < 0 or >
                ActionHostContractBounds.MaximumFindings ||
            stateDisposition is not (
                ActionHostStateDisposition.Accepted or
                ActionHostStateDisposition.NotAccessed or
                ActionHostStateDisposition.NotCommitted or
                ActionHostStateDisposition.Conflict))
        {
            return false;
        }

        summary = new ActionHostStepSummary(
            reviewedSha,
            publicationUrl,
            findingCount,
            stateDisposition);
        return true;
    }
}

internal sealed class ActionHostCompletion
{
    private ActionHostCompletion(
        string buildDiscriminator,
        ActionHostStatus status,
        ActionHostExitClass exitClass,
        int processExitCode,
        ActionHostStepSummary summary,
        ImmutableArray<ActionHostAnnotation> annotations)
    {
        BuildDiscriminator = buildDiscriminator;
        Status = status;
        ExitClass = exitClass;
        ProcessExitCode = processExitCode;
        Summary = summary;
        Annotations = annotations;
    }

    internal string BuildDiscriminator { get; }

    internal ActionHostStatus Status { get; }

    internal ActionHostExitClass ExitClass { get; }

    internal int ProcessExitCode { get; }

    internal ActionHostStepSummary Summary { get; }

    internal ImmutableArray<ActionHostAnnotation> Annotations { get; }

    internal ActionHostPrivacyClass Privacy =>
        ActionHostPrivacyClass.WorkflowPresentation;

    internal static bool TryCreate(
        string? buildDiscriminator,
        ActionHostStatus status,
        ActionHostStepSummary? summary,
        IReadOnlyList<ActionHostAnnotation>? annotations,
        out ActionHostCompletion? completion)
    {
        completion = null;
        if (!ActionHostContractValidation.IsBuildDiscriminator(
                buildDiscriminator) ||
            summary is null ||
            annotations is null ||
            annotations.Count > 1 ||
            !ActionHostStatusRules.TryClassify(
                status,
                out var exitClass,
                out var processExitCode,
                out var expectedAnnotation) ||
            !ActionHostStatusRules.IsSummaryValid(status, summary) ||
            !AnnotationsAreValid(annotations, expectedAnnotation))
        {
            return false;
        }

        completion = new ActionHostCompletion(
            buildDiscriminator!,
            status,
            exitClass,
            processExitCode,
            summary,
            annotations.ToImmutableArray());
        return true;
    }

    private static bool AnnotationsAreValid(
        IReadOnlyList<ActionHostAnnotation> annotations,
        ActionHostAnnotationCode? expected)
    {
        if (expected is null)
        {
            return annotations.Count == 0;
        }

        if (annotations.Count != 1 || annotations[0] is null)
        {
            return false;
        }

        var actual = annotations[0];
        return actual.Code == expected &&
            ActionHostAnnotation.TryCreate(expected.Value, out var canonical) &&
            actual.Severity == canonical!.Severity &&
            StringComparer.Ordinal.Equals(actual.Message, canonical.Message);
    }
}

internal static class ActionHostStatusRules
{
    internal static bool TryClassify(
        ActionHostStatus status,
        out ActionHostExitClass exitClass,
        out int processExitCode,
        out ActionHostAnnotationCode? annotation)
    {
        annotation = status switch
        {
            ActionHostStatus.ReviewedWithInlineWarnings =>
                ActionHostAnnotationCode.InlinePublicationIncomplete,
            ActionHostStatus.ConfigurationInvalid =>
                ActionHostAnnotationCode.ConfigurationInvalid,
            ActionHostStatus.AuthorizationFailed =>
                ActionHostAnnotationCode.AuthorizationFailed,
            ActionHostStatus.CredentialsMissing =>
                ActionHostAnnotationCode.CredentialsMissing,
            ActionHostStatus.SnapshotIncomplete =>
                ActionHostAnnotationCode.SnapshotIncomplete,
            ActionHostStatus.ProviderFailed =>
                ActionHostAnnotationCode.ProviderFailed,
            ActionHostStatus.AgentResultInvalid =>
                ActionHostAnnotationCode.AgentResultInvalid,
            ActionHostStatus.StaleHead =>
                ActionHostAnnotationCode.StaleHead,
            ActionHostStatus.StateConflict =>
                ActionHostAnnotationCode.StateConflict,
            ActionHostStatus.StickyPublicationFailed =>
                ActionHostAnnotationCode.StickyPublicationFailed,
            ActionHostStatus.OutcomeAmbiguous =>
                ActionHostAnnotationCode.OutcomeAmbiguous,
            ActionHostStatus.Cancelled =>
                ActionHostAnnotationCode.Cancelled,
            ActionHostStatus.InternalFailure =>
                ActionHostAnnotationCode.InternalFailure,
            _ => null,
        };

        exitClass = status switch
        {
            ActionHostStatus.Reviewed or
            ActionHostStatus.ReviewedWithInlineWarnings or
            ActionHostStatus.SkippedUntrustedEvent or
            ActionHostStatus.SkippedFork or
            ActionHostStatus.SkippedDraft or
            ActionHostStatus.SkippedClosed => ActionHostExitClass.Success,
            ActionHostStatus.ConfigurationInvalid =>
                ActionHostExitClass.Contract,
            ActionHostStatus.AuthorizationFailed or
            ActionHostStatus.CredentialsMissing =>
                ActionHostExitClass.Authorization,
            ActionHostStatus.SnapshotIncomplete =>
                ActionHostExitClass.Snapshot,
            ActionHostStatus.ProviderFailed => ActionHostExitClass.Provider,
            ActionHostStatus.AgentResultInvalid => ActionHostExitClass.Agent,
            ActionHostStatus.StateConflict => ActionHostExitClass.State,
            ActionHostStatus.StaleHead or
            ActionHostStatus.StickyPublicationFailed =>
                ActionHostExitClass.Publication,
            ActionHostStatus.OutcomeAmbiguous =>
                ActionHostExitClass.Reconciliation,
            ActionHostStatus.Cancelled =>
                ActionHostExitClass.Cancellation,
            ActionHostStatus.InternalFailure => ActionHostExitClass.Internal,
            _ => 0,
        };

        if (exitClass == 0)
        {
            processExitCode = 0;
            annotation = null;
            return false;
        }

        processExitCode = exitClass == ActionHostExitClass.Success ? 0 : 1;
        return true;
    }

    internal static bool IsSummaryValid(
        ActionHostStatus status,
        ActionHostStepSummary summary)
    {
        if (status is
            ActionHostStatus.Reviewed or
            ActionHostStatus.ReviewedWithInlineWarnings)
        {
            return summary.ReviewedSha is not null &&
                summary.PublicationUrl is not null &&
                summary.FindingCount is >= 0 and <=
                    ActionHostContractBounds.MaximumFindings &&
                summary.StateDisposition ==
                    ActionHostStateDisposition.Accepted;
        }

        if (status is
            ActionHostStatus.SkippedUntrustedEvent or
            ActionHostStatus.SkippedFork or
            ActionHostStatus.SkippedDraft or
            ActionHostStatus.SkippedClosed)
        {
            return HasNoReviewFields(summary) &&
                summary.StateDisposition ==
                    ActionHostStateDisposition.NotAccessed;
        }

        if (status == ActionHostStatus.StateConflict)
        {
            return HasNoReviewFields(summary) &&
                summary.StateDisposition ==
                    ActionHostStateDisposition.Conflict;
        }

        return (status is
                ActionHostStatus.ConfigurationInvalid or
                ActionHostStatus.AuthorizationFailed or
                ActionHostStatus.CredentialsMissing or
                ActionHostStatus.SnapshotIncomplete or
                ActionHostStatus.ProviderFailed or
                ActionHostStatus.AgentResultInvalid or
                ActionHostStatus.StaleHead or
                ActionHostStatus.StickyPublicationFailed or
                ActionHostStatus.OutcomeAmbiguous or
                ActionHostStatus.Cancelled or
                ActionHostStatus.InternalFailure) &&
            HasNoReviewFields(summary) &&
            summary.StateDisposition ==
                ActionHostStateDisposition.NotCommitted;
    }

    private static bool HasNoReviewFields(ActionHostStepSummary summary) =>
        summary.ReviewedSha is null &&
        summary.PublicationUrl is null &&
        summary.FindingCount is null;
}
