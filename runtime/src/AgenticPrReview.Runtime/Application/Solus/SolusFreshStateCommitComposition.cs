using AgenticPrReview.Runtime.Agent;
using SolusAgent.Api.Context;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Solus;
using SolusAgent.Api.Execution;
using SolusAgent.Runtime.Api.Providers;

namespace AgenticPrReview.Runtime.Application.Solus;

internal sealed record SolusFreshStateCommitResult(SolusFreshReviewResult? Work,
    ActionHostCompletion? Completion, bool? CurrentCaptureValid,
    SolusFreshReviewFailure PreparationFailure = SolusFreshReviewFailure.None)
{
    internal bool DurableStateAccepted => Completion?.Summary.StateDisposition == ActionHostStateDisposition.Accepted;
    internal bool RecoveredWithoutWork => Work is null && DurableStateAccepted;
    public override string ToString() => nameof(SolusFreshStateCommitResult);
}

// Explicit internal composition only. R5's default and the ordinary Action do
// not select it. All state/publication effects remain inside the R4 coordinator.
internal sealed class SolusFreshStateCommitComposition
{
    private readonly ActionHostComposition host;
    private readonly SolusFreshReviewComposition review;
    private readonly Func<IRestrictedContextSink, IRestrictedContextSink>? trustedCaptureDecorator;
    private readonly Action<SolusRestrictedStateRecord>? trustedCapturedRecord;

    internal SolusFreshStateCommitComposition(ActionHostComposition host,
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

    internal Task<ActionHostPreparedResult<SolusFreshStateCommitResult>> RunAsync(ActionHostLaunchContract launch,
        CancellationToken cancellationToken = default, IReadOnlyList<AgentInput>? reviewData = null)
    {
        var oversized = reviewData?.Count >= Cr1DevelopmentProfile.ProviderInputs - 1;
        var data = oversized ? [] : reviewData?.ToArray() ?? [];
        return host.RunPreparedFreshAsync(launch, prepared => oversized
            ? Task.FromResult(new SolusFreshStateCommitResult(null, null, null, SolusFreshReviewFailure.InputProfileRejected))
            : ExecuteAsync(prepared, data), cancellationToken);
    }

    private async Task<SolusFreshStateCommitResult> ExecuteAsync(ActionHostPreparedReview prepared,
        IReadOnlyList<AgentInput> data)
    {
        SolusPreparedFreshExecution execution;
        try { execution = review.PrepareStateExecution(prepared); }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        { return new(null, null, null, SolusFreshReviewFailure.AuthorityUnavailable); }
        var current = prepared with { RestoreRequest = prepared.RestoreRequest with {
            SolusTransaction = new(prepared.FreshContext!, execution.Compatibility, execution.ExecutionId) } };
        SolusFreshReviewResult? work = null;
        SolusRestrictedStateCapture? capture = null;
        SolusRestrictedStateRecord? record = null;
        bool? captureValid = null;
        try
        {
            var completion = await host.RunStateCommitPreparedAsync(current, async (state, scope, accounting) =>
            {
                // An accepted source cannot silently become a fresh review. S4
                // owns restored execution; R4 already classified pending work.
                if (!RestrictedStateService.CanPrepareFreshSolus(state)) return new(ActionHostStatus.StateConflict);
                if (prepared.Launch.Inputs.ProviderApiKey is null) return new(ActionHostStatus.CredentialsMissing);
                work = await review.ExecuteAsync(prepared, null, data, execution,
                    compatibility => {
                        capture = new(execution.ExecutionId, compatibility);
                        return trustedCaptureDecorator?.Invoke(capture) ?? capture;
                    },
                    actual => captureValid = capture?.TryComplete(actual, out record) == true,
                    accounting.BeginProvider).ConfigureAwait(false);
                if (work.Work is not null) accounting.Complete(work.Work.Accounting, work.Work.Termination);
                if (prepared.Journal.ObserveCancellation(prepared.CancellationToken))
                    return new(prepared.Journal.CancellationStatus);
                if (work.Failure == SolusFreshReviewFailure.CredentialsMissing) return new(ActionHostStatus.CredentialsMissing);
                if (!work.ProductReviewAccepted)
                    return new(work.Work?.Termination == ActionHostTerminationReason.ProviderFailure
                        ? ActionHostStatus.ProviderFailed : work.Failure == SolusFreshReviewFailure.HostFailure
                            ? ActionHostStatus.InternalFailure : ActionHostStatus.AgentResultInvalid);
                if (work.Publication?.CandidateEligible != true || work.Publication.Review is null)
                    return new(work.Publication?.Head is { } head
                        ? ActionHostCoordinator.RevalidationStatus(head, prepared.Journal.CancellationStatus)
                        : ActionHostStatus.StateConflict);
                if (captureValid != true || record is null) return new(ActionHostStatus.StateConflict);
                try { trustedCapturedRecord?.Invoke(record); }
                catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
                { return new(ActionHostStatus.StateConflict); }
                if (!prepared.Journal.TryBeginBusinessOperation(ActionHostOperationKind.CandidatePreparation,
                        prepared.CancellationToken, out var operation)) return new(prepared.Journal.CancellationStatus);
                using (operation!)
                {
                    var result = await RestrictedStateService.PrepareRetainedSolusCandidateAsync(state, record,
                        work.Publication.Review, prepared.CancellationToken).ConfigureAwait(false);
                    operation!.Resolve(ActionHostOperationResolution.ResolvedNoCommit);
                    return new(null, result);
                }
            }).ConfigureAwait(false);
            return new(work, completion, captureValid);
        }
        finally { capture?.Dispose(); }
    }
}
