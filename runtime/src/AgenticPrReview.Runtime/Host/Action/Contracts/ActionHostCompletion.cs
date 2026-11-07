using System.Collections.Immutable;
using AgenticPrReview.Runtime.Agent.Core;

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

// Safe presentation facts only; the R2 aggregate remains the sole counter.
internal sealed class ActionHostAccounting
{
    private ActionHostAccounting(long?[] counts, long?[] tokens,
        AccountingCompleteness attempts, AccountingCompleteness usage)
    {
        ModelCalls = counts[0]; ProviderAttempts = counts[1]; ProviderRetries = counts[2];
        ProviderFailedAttempts = counts[3]; UnknownUsageAttempts = counts[4];
        UnknownCachePartitionAttempts = counts[5]; InputTokens = tokens[0];
        CacheHitTokens = tokens[1]; CacheMissTokens = tokens[2]; OutputTokens = tokens[3];
        AttemptCompleteness = attempts; UsageCompleteness = usage;
    }

    internal long? ModelCalls { get; }
    internal long? ProviderAttempts { get; }
    internal long? ProviderRetries { get; }
    internal long? ProviderFailedAttempts { get; }
    internal long? UnknownUsageAttempts { get; }
    internal long? UnknownCachePartitionAttempts { get; }
    internal long? InputTokens { get; }
    internal long? CacheHitTokens { get; }
    internal long? CacheMissTokens { get; }
    internal long? OutputTokens { get; }
    internal AccountingCompleteness AttemptCompleteness { get; }
    internal AccountingCompleteness UsageCompleteness { get; }
    internal bool IsCompleteZero => ModelCalls == 0 && ProviderAttempts == 0 &&
        ProviderRetries == 0 && ProviderFailedAttempts == 0 && UnknownUsageAttempts == 0 &&
        UnknownCachePartitionAttempts == 0 && InputTokens == 0 && CacheHitTokens == 0 &&
        CacheMissTokens == 0 && OutputTokens == 0 &&
        AttemptCompleteness == AccountingCompleteness.Complete &&
        UsageCompleteness == AccountingCompleteness.Complete;

    internal static ActionHostAccounting Unavailable { get; } = new(
        new long?[6], new long?[4], AccountingCompleteness.Unavailable, AccountingCompleteness.Unavailable);
    internal static ActionHostAccounting NoProvider { get; } = FromProvider(ProviderAccounting.Aggregate([]));

    internal static ActionHostAccounting FromProvider(ProviderAccounting? value)
    {
        if (value is null) return Unavailable;
        if (!TryCreate(value.ModelCalls, value.ProviderAttempts, value.ProviderRetries,
                value.ProviderFailedAttempts, value.UnknownUsageAttempts, value.UnknownCachePartitionAttempts,
                value.InputTokens, value.CacheHitTokens, value.CacheMissTokens, value.OutputTokens,
                value.AttemptCompleteness, value.UsageCompleteness, out var result))
            throw new InvalidOperationException("Invalid provider accounting projection.");
        return result!;
    }

    internal static bool TryCreate(long? calls, long? sends, long? retries, long? failed,
        long? unknownUsage, long? unknownCache, long? input, long? hit, long? miss, long? output,
        AccountingCompleteness attempts, AccountingCompleteness usage, out ActionHostAccounting? result)
    {
        result = null;
        long?[] counts = [calls, sends, retries, failed, unknownUsage, unknownCache];
        long?[] tokens = [input, hit, miss, output];
        if (!Enum.IsDefined(attempts) || !Enum.IsDefined(usage) ||
            counts.Any(value => value < 0) || tokens.Any(value => value < 0)) return false;
        if (counts.All(value => value is null))
        {
            if (tokens.Any(value => value is not null) || attempts != AccountingCompleteness.Unavailable ||
                usage != AccountingCompleteness.Unavailable) return false;
        }
        else
        {
            if (counts.Any(value => value is null) || calls > 128 || sends > 136 || retries > 8 ||
                retries > failed || failed > sends || unknownUsage > sends || unknownCache > sends ||
                sends - retries > calls || sends < retries || retries > 2 * (sends - retries)) return false;
            if (attempts == AccountingCompleteness.Unavailable && counts.Skip(1).Any(value => value != 0)) return false;
            if (usage == AccountingCompleteness.Unavailable &&
                (tokens.Any(value => value != 0) || unknownUsage != sends || unknownCache != 0)) return false;
            var completeUsage = attempts == AccountingCompleteness.Complete && unknownUsage == 0 &&
                unknownCache == 0 && tokens.All(value => value is not null);
            if ((usage == AccountingCompleteness.Complete) != completeUsage) return false;
            if (input is { } i && ((hit is { } h && h > i) || (miss is { } m && m > i) ||
                (hit.HasValue && miss.HasValue && miss > i - hit))) return false;
            if (usage == AccountingCompleteness.Complete && miss != input - hit) return false;
            if (attempts == AccountingCompleteness.Complete && sends == 0 &&
                (usage != AccountingCompleteness.Complete || tokens.Any(value => value != 0))) return false;
            if (calls == 0 && (counts.Any(value => value != 0) || tokens.Any(value => value != 0) ||
                attempts != AccountingCompleteness.Complete || usage != AccountingCompleteness.Complete)) return false;
        }
        result = new(counts, tokens, attempts, usage);
        return true;
    }
}

internal enum ActionHostTerminationReason
{
    ReviewCompleted = 1, NotStarted, ProviderFailure, Cancelled, DeadlineExceeded,
    ModelLimit, ToolLimit, TokenLimit, RequestLimit, ResponseLimit, ContextLimit, InvalidResult, HostFailure,
    ResourceLimit,
}

// Closed, role-validated labels shared with the credential-free Host contract.
// Managed producers map external enums explicitly; no enum/string/ID crosses here.
internal enum ActionHostRuntimeDiagnosticValue
{
    Unknown, None, InvalidInput, InvalidAssociation, UnsupportedCapability, InvalidEncoding, LimitExceeded, InvalidResponse, ContinuationMismatch, Cancelled, ProviderFailed, ObservationClosed, ObservationConflict, Succeeded, Rejected, Failed, NotDispatched, Dispatched, MissingHooks, ResourceLimit, ExposureDenied, ExposureMissing, ExposureFailed, ExposureUnknown, ExposureMismatch, DurableAcknowledgementRequired, SettlementMissing, SettlementFailed, SettlementUnknown, SettlementMismatch, HostStopped, DurationLimit, Completed, HostEnded, ProductionExhausted, SubmissionLimit, WorkUnitLimit, RepairLimit, ContinuationLimit, MissingAcknowledgement, FailedAcknowledgement, UnknownAcknowledgement, MismatchedFeedback, DuplicateFeedback, ProductionFailed, ProgressObserverFailed, RuntimeLimit, ProductionStopped, UsageAccountingUnavailable, Partial,
}

internal sealed class ActionHostAttemptDiagnostic
{
    private ActionHostAttemptDiagnostic(int ordinal, bool? invoked, ActionHostRuntimeDiagnosticValue outcome,
        ActionHostRuntimeDiagnosticValue error, ActionHostRuntimeDiagnosticValue exposure, ActionHostRuntimeDiagnosticValue stop, bool? usageKnown)
    {
        Ordinal = ordinal; ProviderInvoked = invoked; Outcome = outcome; Error = error;
        Exposure = exposure; Stop = stop; CoreUsageKnown = usageKnown;
    }
    internal int Ordinal { get; }
    internal bool? ProviderInvoked { get; }
    internal ActionHostRuntimeDiagnosticValue Outcome { get; }
    internal ActionHostRuntimeDiagnosticValue Error { get; }
    internal ActionHostRuntimeDiagnosticValue Exposure { get; }
    internal ActionHostRuntimeDiagnosticValue Stop { get; }
    internal bool? CoreUsageKnown { get; }

    internal static bool TryCreate(int ordinal, bool? invoked, ActionHostRuntimeDiagnosticValue outcome,
        ActionHostRuntimeDiagnosticValue error, ActionHostRuntimeDiagnosticValue exposure, ActionHostRuntimeDiagnosticValue stop, bool? usageKnown, out ActionHostAttemptDiagnostic? value)
    {
        value = null;
        if (ordinal is < 1 or > 6 ||
            outcome is not (ActionHostRuntimeDiagnosticValue.Unknown or ActionHostRuntimeDiagnosticValue.Succeeded or ActionHostRuntimeDiagnosticValue.Rejected or ActionHostRuntimeDiagnosticValue.Cancelled or ActionHostRuntimeDiagnosticValue.Failed) ||
            error is not (ActionHostRuntimeDiagnosticValue.Unknown or ActionHostRuntimeDiagnosticValue.None or ActionHostRuntimeDiagnosticValue.InvalidInput or ActionHostRuntimeDiagnosticValue.InvalidAssociation or ActionHostRuntimeDiagnosticValue.UnsupportedCapability or ActionHostRuntimeDiagnosticValue.InvalidEncoding or ActionHostRuntimeDiagnosticValue.LimitExceeded or ActionHostRuntimeDiagnosticValue.InvalidResponse or ActionHostRuntimeDiagnosticValue.ContinuationMismatch or ActionHostRuntimeDiagnosticValue.Cancelled or ActionHostRuntimeDiagnosticValue.ProviderFailed or ActionHostRuntimeDiagnosticValue.ObservationClosed or ActionHostRuntimeDiagnosticValue.ObservationConflict) ||
            exposure is not (ActionHostRuntimeDiagnosticValue.Unknown or ActionHostRuntimeDiagnosticValue.NotDispatched or ActionHostRuntimeDiagnosticValue.Dispatched) ||
            stop is not (ActionHostRuntimeDiagnosticValue.Unknown or ActionHostRuntimeDiagnosticValue.None or ActionHostRuntimeDiagnosticValue.UnsupportedCapability or ActionHostRuntimeDiagnosticValue.MissingHooks or ActionHostRuntimeDiagnosticValue.InvalidAssociation or ActionHostRuntimeDiagnosticValue.ResourceLimit or ActionHostRuntimeDiagnosticValue.Cancelled or ActionHostRuntimeDiagnosticValue.ExposureDenied or ActionHostRuntimeDiagnosticValue.ExposureMissing or ActionHostRuntimeDiagnosticValue.ExposureFailed or ActionHostRuntimeDiagnosticValue.ExposureUnknown or ActionHostRuntimeDiagnosticValue.ExposureMismatch or ActionHostRuntimeDiagnosticValue.DurableAcknowledgementRequired or ActionHostRuntimeDiagnosticValue.SettlementMissing or ActionHostRuntimeDiagnosticValue.SettlementFailed or ActionHostRuntimeDiagnosticValue.SettlementUnknown or ActionHostRuntimeDiagnosticValue.SettlementMismatch or ActionHostRuntimeDiagnosticValue.HostStopped or ActionHostRuntimeDiagnosticValue.DurationLimit) ||
            (exposure == ActionHostRuntimeDiagnosticValue.Dispatched && invoked == false)) return false;
        value = new(ordinal, invoked, outcome, error, exposure, stop, usageKnown);
        return true;
    }
    internal string Render() =>
        Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        "{invoked=" + Boolean(ProviderInvoked) + ",outcome=" + ActionHostRuntimeFailureDiagnostic.Label(Outcome) +
        ",error=" + ActionHostRuntimeFailureDiagnostic.Label(Error) + ",exposure=" + ActionHostRuntimeFailureDiagnostic.Label(Exposure) +
        ",stop=" + ActionHostRuntimeFailureDiagnostic.Label(Stop) + ",core=" + Boolean(CoreUsageKnown) + "}";
    private static string Boolean(bool? value) => value switch { true => "yes", false => "no", _ => "unknown" };
    public override string ToString() => "action_host_attempt_diagnostic";
}

internal sealed class ActionHostRuntimeFailureDiagnostic
{
    private ActionHostRuntimeFailureDiagnostic(ImmutableArray<ActionHostAttemptDiagnostic> attempts,
        ActionHostRuntimeDiagnosticValue candidateStop, ActionHostRuntimeDiagnosticValue runtimeReason)
    { Attempts = attempts; CandidateStop = candidateStop; RuntimeReason = runtimeReason; }
    internal ImmutableArray<ActionHostAttemptDiagnostic> Attempts { get; }
    internal ActionHostRuntimeDiagnosticValue CandidateStop { get; }
    internal ActionHostRuntimeDiagnosticValue RuntimeReason { get; }

    internal static bool TryCreate(IReadOnlyList<ActionHostAttemptDiagnostic>? attempts,
        ActionHostRuntimeDiagnosticValue candidateStop, ActionHostRuntimeDiagnosticValue runtimeReason, out ActionHostRuntimeFailureDiagnostic? value)
    {
        value = null;
        if (attempts is null || attempts.Count > 6 ||
            candidateStop is not (ActionHostRuntimeDiagnosticValue.Unknown or ActionHostRuntimeDiagnosticValue.Completed or ActionHostRuntimeDiagnosticValue.HostEnded or ActionHostRuntimeDiagnosticValue.ProductionExhausted or ActionHostRuntimeDiagnosticValue.SubmissionLimit or ActionHostRuntimeDiagnosticValue.WorkUnitLimit or ActionHostRuntimeDiagnosticValue.RepairLimit or ActionHostRuntimeDiagnosticValue.ContinuationLimit or ActionHostRuntimeDiagnosticValue.MissingAcknowledgement or ActionHostRuntimeDiagnosticValue.FailedAcknowledgement or ActionHostRuntimeDiagnosticValue.UnknownAcknowledgement or ActionHostRuntimeDiagnosticValue.MismatchedFeedback or ActionHostRuntimeDiagnosticValue.DuplicateFeedback or ActionHostRuntimeDiagnosticValue.Cancelled or ActionHostRuntimeDiagnosticValue.ProductionFailed or ActionHostRuntimeDiagnosticValue.ProgressObserverFailed or ActionHostRuntimeDiagnosticValue.UnsupportedCapability or ActionHostRuntimeDiagnosticValue.DurationLimit or ActionHostRuntimeDiagnosticValue.RuntimeLimit or ActionHostRuntimeDiagnosticValue.ProductionStopped or ActionHostRuntimeDiagnosticValue.UsageAccountingUnavailable) ||
            runtimeReason is not (ActionHostRuntimeDiagnosticValue.Unknown or ActionHostRuntimeDiagnosticValue.Completed or ActionHostRuntimeDiagnosticValue.Partial or ActionHostRuntimeDiagnosticValue.ResourceLimit or ActionHostRuntimeDiagnosticValue.Cancelled or ActionHostRuntimeDiagnosticValue.Failed or ActionHostRuntimeDiagnosticValue.UnsupportedCapability)) return false;
        var frozen = attempts.ToImmutableArray();
        if (frozen.Length > 6 || frozen.Where((attempt, i) => attempt is null || attempt.Ordinal != i + 1).Any()) return false;
        var result = new ActionHostRuntimeFailureDiagnostic(frozen, candidateStop, runtimeReason);
        if (result.Render().Length > 2048) return false;
        value = result;
        return true;
    }
    internal string Render() => "candidate=" + Label(CandidateStop) + ";runtime=" + Label(RuntimeReason) +
        ";attempts=" + (Attempts.IsEmpty ? "none" : string.Join("|", Attempts.Select(attempt => attempt.Render())));
    internal static string Label(ActionHostRuntimeDiagnosticValue value) => value switch
    {
        ActionHostRuntimeDiagnosticValue.Unknown => "unknown",
        ActionHostRuntimeDiagnosticValue.None => "none",
        ActionHostRuntimeDiagnosticValue.InvalidInput => "invalid_input",
        ActionHostRuntimeDiagnosticValue.InvalidAssociation => "invalid_association",
        ActionHostRuntimeDiagnosticValue.UnsupportedCapability => "unsupported_capability",
        ActionHostRuntimeDiagnosticValue.InvalidEncoding => "invalid_encoding",
        ActionHostRuntimeDiagnosticValue.LimitExceeded => "limit_exceeded",
        ActionHostRuntimeDiagnosticValue.InvalidResponse => "invalid_response",
        ActionHostRuntimeDiagnosticValue.ContinuationMismatch => "continuation_mismatch",
        ActionHostRuntimeDiagnosticValue.Cancelled => "cancelled",
        ActionHostRuntimeDiagnosticValue.ProviderFailed => "provider_failed",
        ActionHostRuntimeDiagnosticValue.ObservationClosed => "observation_closed",
        ActionHostRuntimeDiagnosticValue.ObservationConflict => "observation_conflict",
        ActionHostRuntimeDiagnosticValue.Succeeded => "succeeded",
        ActionHostRuntimeDiagnosticValue.Rejected => "rejected",
        ActionHostRuntimeDiagnosticValue.Failed => "failed",
        ActionHostRuntimeDiagnosticValue.NotDispatched => "not_dispatched",
        ActionHostRuntimeDiagnosticValue.Dispatched => "dispatched",
        ActionHostRuntimeDiagnosticValue.MissingHooks => "missing_hooks",
        ActionHostRuntimeDiagnosticValue.ResourceLimit => "resource_limit",
        ActionHostRuntimeDiagnosticValue.ExposureDenied => "exposure_denied",
        ActionHostRuntimeDiagnosticValue.ExposureMissing => "exposure_missing",
        ActionHostRuntimeDiagnosticValue.ExposureFailed => "exposure_failed",
        ActionHostRuntimeDiagnosticValue.ExposureUnknown => "exposure_unknown",
        ActionHostRuntimeDiagnosticValue.ExposureMismatch => "exposure_mismatch",
        ActionHostRuntimeDiagnosticValue.DurableAcknowledgementRequired => "durable_acknowledgement_required",
        ActionHostRuntimeDiagnosticValue.SettlementMissing => "settlement_missing",
        ActionHostRuntimeDiagnosticValue.SettlementFailed => "settlement_failed",
        ActionHostRuntimeDiagnosticValue.SettlementUnknown => "settlement_unknown",
        ActionHostRuntimeDiagnosticValue.SettlementMismatch => "settlement_mismatch",
        ActionHostRuntimeDiagnosticValue.HostStopped => "host_stopped",
        ActionHostRuntimeDiagnosticValue.DurationLimit => "duration_limit",
        ActionHostRuntimeDiagnosticValue.Completed => "completed",
        ActionHostRuntimeDiagnosticValue.HostEnded => "host_ended",
        ActionHostRuntimeDiagnosticValue.ProductionExhausted => "production_exhausted",
        ActionHostRuntimeDiagnosticValue.SubmissionLimit => "submission_limit",
        ActionHostRuntimeDiagnosticValue.WorkUnitLimit => "work_unit_limit",
        ActionHostRuntimeDiagnosticValue.RepairLimit => "repair_limit",
        ActionHostRuntimeDiagnosticValue.ContinuationLimit => "continuation_limit",
        ActionHostRuntimeDiagnosticValue.MissingAcknowledgement => "missing_acknowledgement",
        ActionHostRuntimeDiagnosticValue.FailedAcknowledgement => "failed_acknowledgement",
        ActionHostRuntimeDiagnosticValue.UnknownAcknowledgement => "unknown_acknowledgement",
        ActionHostRuntimeDiagnosticValue.MismatchedFeedback => "mismatched_feedback",
        ActionHostRuntimeDiagnosticValue.DuplicateFeedback => "duplicate_feedback",
        ActionHostRuntimeDiagnosticValue.ProductionFailed => "production_failed",
        ActionHostRuntimeDiagnosticValue.ProgressObserverFailed => "progress_observer_failed",
        ActionHostRuntimeDiagnosticValue.RuntimeLimit => "runtime_limit",
        ActionHostRuntimeDiagnosticValue.ProductionStopped => "production_stopped",
        ActionHostRuntimeDiagnosticValue.UsageAccountingUnavailable => "usage_accounting_unavailable",
        ActionHostRuntimeDiagnosticValue.Partial => "partial",
        _ => "unknown",
    };
    public override string ToString() => "action_host_runtime_failure_diagnostic";
}

internal sealed class ActionHostCompletion
{
    private ActionHostCompletion(
        string buildDiscriminator,
        ActionHostStatus status,
        ActionHostExitClass exitClass,
        int processExitCode,
        ActionHostStepSummary summary,
        ImmutableArray<ActionHostAnnotation> annotations,
        ActionHostAccounting accounting,
        ActionHostTerminationReason terminationReason,
        ActionHostRuntimeFailureDiagnostic? failureDiagnostic)
    {
        BuildDiscriminator = buildDiscriminator;
        Status = status;
        ExitClass = exitClass;
        ProcessExitCode = processExitCode;
        Summary = summary;
        Annotations = annotations;
        Accounting = accounting;
        TerminationReason = terminationReason;
        FailureDiagnostic = failureDiagnostic;
    }

    internal string BuildDiscriminator { get; }

    internal ActionHostStatus Status { get; }

    internal ActionHostExitClass ExitClass { get; }

    internal int ProcessExitCode { get; }

    internal ActionHostStepSummary Summary { get; }

    internal ImmutableArray<ActionHostAnnotation> Annotations { get; }

    internal ActionHostAccounting Accounting { get; }

    internal ActionHostTerminationReason TerminationReason { get; }

    internal ActionHostRuntimeFailureDiagnostic? FailureDiagnostic { get; }

    internal ActionHostPrivacyClass Privacy =>
        ActionHostPrivacyClass.WorkflowPresentation;

    internal static bool TryCreate(
        string? buildDiscriminator,
        ActionHostStatus status,
        ActionHostStepSummary? summary,
        IReadOnlyList<ActionHostAnnotation>? annotations,
        out ActionHostCompletion? completion,
        ActionHostAccounting? accounting = null,
        ActionHostTerminationReason? terminationReason = null,
        ActionHostRuntimeFailureDiagnostic? failureDiagnostic = null)
    {
        completion = null;
        var skipped = status is ActionHostStatus.SkippedUntrustedEvent or ActionHostStatus.SkippedFork or
            ActionHostStatus.SkippedDraft or ActionHostStatus.SkippedClosed;
        var reviewed = status is ActionHostStatus.Reviewed or ActionHostStatus.ReviewedWithInlineWarnings;
        accounting ??= skipped ? ActionHostAccounting.NoProvider : ActionHostAccounting.Unavailable;
        var reason = terminationReason ?? (skipped ? ActionHostTerminationReason.NotStarted :
            reviewed ? ActionHostTerminationReason.ReviewCompleted : ActionHostTerminationReason.HostFailure);
        if (!Enum.IsDefined(reason) ||
            (reason == ActionHostTerminationReason.NotStarted && !accounting.IsCompleteZero) ||
            (skipped && (reason != ActionHostTerminationReason.NotStarted || !accounting.IsCompleteZero)) ||
            (reviewed && reason is not (ActionHostTerminationReason.ReviewCompleted or ActionHostTerminationReason.NotStarted)) ||
            (reason == ActionHostTerminationReason.ReviewCompleted &&
                accounting.AttemptCompleteness == AccountingCompleteness.Complete &&
                accounting.ProviderAttempts <= accounting.ProviderFailedAttempts)) return false;
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
            annotations.ToImmutableArray(),
            accounting,
            reason,
            failureDiagnostic);
        return true;
    }

    internal ActionHostCompletion WithAccounting(ActionHostAccounting accounting, ActionHostTerminationReason reason,
        ActionHostRuntimeFailureDiagnostic? failureDiagnostic = null)
    {
        if (!TryCreate(BuildDiscriminator, Status, Summary, Annotations, out var result, accounting, reason,
                failureDiagnostic ?? FailureDiagnostic))
            throw new InvalidOperationException("Invalid completion accounting.");
        return result!;
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
