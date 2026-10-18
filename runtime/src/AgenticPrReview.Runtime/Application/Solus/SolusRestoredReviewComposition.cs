using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;
using AgenticPrReview.Runtime.Solus;
using SolusAgent.Api.Context;
using SolusAgent.Runtime.Api.Providers;

namespace AgenticPrReview.Runtime.Application.Solus;

internal sealed record SolusRestoredReviewResult(SolusFreshReviewResult? Work,
    ActionHostCompletion? Completion, bool? CurrentCaptureValid,
    SolusFreshReviewFailure PreparationFailure = SolusFreshReviewFailure.None)
{
    internal bool DurableStateAccepted => Completion?.Summary.StateDisposition == ActionHostStateDisposition.Accepted;
    internal bool RecoveredWithoutWork => Work is null && DurableStateAccepted;
    public override string ToString() => nameof(SolusRestoredReviewResult);
}

// Compiled internal composition only. Current workflow admission retains its
// same-head policy; no saved value or repository/model input selects new work.
internal sealed class SolusRestoredReviewComposition
{
    private readonly ActionHostComposition host;
    private readonly SolusFreshReviewComposition review;
    private readonly Func<IRestrictedContextSink, IRestrictedContextSink>? trustedCaptureDecorator;
    private readonly Action<SolusRestrictedStateRecord>? trustedCapturedRecord;

    internal SolusRestoredReviewComposition(ActionHostComposition host,
        Func<HttpMessageHandler>? controlledTransport = null,
        Func<IModelProvider, IModelProvider>? trustedProviderDecorator = null,
        Func<AgenticPrReview.Runtime.Agent.Tools.ReviewedSnapshot,
            AgenticPrReview.Runtime.Agent.Tools.IReviewedFileAccess>? trustedFileAccess = null,
        Func<IRestrictedContextSink, IRestrictedContextSink>? trustedCaptureDecorator = null,
        Action<SolusRestrictedStateRecord>? trustedCapturedRecord = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        review = new(host, controlledTransport, trustedProviderDecorator, trustedFileAccess);
        this.trustedCaptureDecorator = trustedCaptureDecorator;
        this.trustedCapturedRecord = trustedCapturedRecord;
    }

    internal Task<ActionHostPreparedResult<SolusRestoredReviewResult>> RunAsync(ActionHostLaunchContract launch,
        CancellationToken cancellationToken = default, IReadOnlyList<SolusAgent.Api.Execution.AgentInput>? reviewData = null)
    {
        var oversized = reviewData?.Count >= Cr1DevelopmentProfile.ProviderInputs - 1;
        var data = oversized ? [] : reviewData?.ToArray() ?? [];
        return host.RunPreparedFreshAsync(launch, prepared => oversized
            ? Task.FromResult(new SolusRestoredReviewResult(null, null, null, SolusFreshReviewFailure.InputProfileRejected))
            : ExecuteAsync(prepared, data), cancellationToken);
    }

    private async Task<SolusRestoredReviewResult> ExecuteAsync(ActionHostPreparedReview prepared,
        IReadOnlyList<SolusAgent.Api.Execution.AgentInput> data)
    {
        SolusPreparedFreshExecution execution;
        try { execution = review.PrepareStateExecution(prepared); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { return new(null, null, null, SolusFreshReviewFailure.AuthorityUnavailable); }
        var current = prepared with { RestoreRequest = prepared.RestoreRequest with {
            SolusTransaction = new(prepared.FreshContext!, execution.Compatibility, execution.ExecutionId,
                SolusTransactionMode.CompletedContextReview) } };
        SolusFreshReviewResult? work = null;
        SolusRestrictedStateCapture? capture = null;
        SolusRestrictedStateRecord? record = null;
        bool? captureValid = null;
        var inputFailure = SolusFreshReviewFailure.None;
        try
        {
            var completion = await host.RunStateCommitPreparedAsync(current, async (state, scope, accounting) =>
            {
                // R4 classified recovery first. Absence is never a fresh fallback.
                if (!RestrictedStateService.CanPrepareRestoredSolus(state)) return new(ActionHostStatus.StateConflict);
                if (prepared.Launch.Inputs.ProviderApiKey is null) return new(ActionHostStatus.CredentialsMissing);
                inputFailure = SolusFreshReviewComposition.ValidateInput(prepared, data, out _, out _);
                if (inputFailure != SolusFreshReviewFailure.None) return new(ActionHostStatus.AgentResultInvalid);
                SolusContextSource? source = null;
                SolusConsumedContext? consumed = null;
                try
                {
                    if (!prepared.Journal.TryBeginBusinessOperation(ActionHostOperationKind.CandidatePreparation,
                            prepared.CancellationToken, out var claimOperation)) return new(prepared.Journal.CancellationStatus);
                    using (claimOperation!)
                    {
                        var selected = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(
                            prepared.RestoreRequest with {
                                SolusSourceSelection = new(prepared.FreshContext!, execution.Compatibility) },
                            prepared.CancellationToken).ConfigureAwait(false);
                        source = selected.SolusSource;
                        // Source admission deliberately does not return transaction Context.
                        if (selected.Code != AcceptedStateCodes.Ready || source is null)
                            return new(ClaimFailure(prepared));
                        consumed = await source.PrepareAsync(execution.ExecutionId, Guid.NewGuid(),
                            prepared.CancellationToken).ConfigureAwait(false);
                        if (consumed is null) return new(ClaimFailure(prepared));
                        claimOperation!.Resolve(ActionHostOperationResolution.ResolvedCommitted);
                    }
                    work = await review.ExecuteAsync(prepared, null, data, execution,
                        compatibility => {
                            capture = new(execution.ExecutionId, compatibility);
                            return trustedCaptureDecorator?.Invoke(capture) ?? capture;
                        }, actual => captureValid = capture?.TryComplete(actual, out record) == true,
                        accounting.BeginProvider, consumed).ConfigureAwait(false);
                    if (work.Work is not null) accounting.Complete(work.Work.Accounting, work.Work.Termination);
                    if (prepared.Journal.ObserveCancellation(prepared.CancellationToken))
                        return new(prepared.Journal.CancellationStatus);
                    if (work.Failure == SolusFreshReviewFailure.CredentialsMissing) return new(ActionHostStatus.CredentialsMissing);
                    if (!work.ProductReviewAccepted)
                        return new(work.Context?.Admission == ContextAdmission.Rejected ? ActionHostStatus.StateConflict :
                            work.Work?.Termination == ActionHostTerminationReason.ProviderFailure ? ActionHostStatus.ProviderFailed :
                            work.Failure == SolusFreshReviewFailure.HostFailure ? ActionHostStatus.InternalFailure : ActionHostStatus.AgentResultInvalid);
                    if (work.Publication?.CandidateEligible != true || work.Publication.Review is null)
                        return new(work.Publication?.Head is { } head
                            ? ActionHostCoordinator.RevalidationStatus(head, prepared.Journal.CancellationStatus)
                            : ActionHostStatus.StateConflict);
                    if (captureValid != true || record is null) return new(ActionHostStatus.StateConflict);
                    try { trustedCapturedRecord?.Invoke(record); }
                    catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
                    { return new(ActionHostStatus.StateConflict); }
                    if (!prepared.Journal.TryBeginBusinessOperation(ActionHostOperationKind.CandidatePreparation,
                            prepared.CancellationToken, out var operation)) return new(prepared.Journal.CancellationStatus);
                    using (operation!)
                    {
                        var result = await RestrictedStateService.PrepareRetainedRestoredSolusCandidateAsync(
                            state, consumed, record, work.Publication.Review, prepared.CancellationToken).ConfigureAwait(false);
                        operation!.Resolve(ActionHostOperationResolution.ResolvedNoCommit);
                        return new(null, result);
                    }
                }
                finally { consumed?.Dispose(); source?.Dispose(); }
            }).ConfigureAwait(false);
            return new(work, completion, captureValid, inputFailure);
        }
        finally { capture?.Dispose(); }
    }

    private static ActionHostStatus ClaimFailure(ActionHostPreparedReview prepared) =>
        prepared.Journal.ObserveCancellation(prepared.CancellationToken) ? prepared.Journal.CancellationStatus :
        prepared.Journal.HasCurrentRunActivity && prepared.Journal.LatestResolution == ActionHostOperationResolution.Unresolved
            ? ActionHostStatus.OutcomeAmbiguous : ActionHostStatus.StateConflict;
}
