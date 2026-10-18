#if APR_SOLUS_MANAGED
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Application.Solus;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;
using AgenticPrReview.Runtime.Solus;

namespace AgenticPrReview.Runtime.ActionHost;

// The managed archive's fixed composition. The ordinary workflow admission,
// R4 recovery owner and S2 source-consumption authority retain their contracts.
// No launch field, environment variable or provider response selects this path.
internal sealed class SolusManagedActionComposition
{
    private readonly ActionHostComposition host;
    private readonly SolusFreshReviewComposition review;

    internal SolusManagedActionComposition() : this(new ActionHostComposition()) { }

    // A compiled test capability only; the zero-argument entrypoint always uses
    // the production constructor and the provider's fixed HTTPS transport.
    internal SolusManagedActionComposition(ActionHostComposition host, Func<HttpMessageHandler>? controlledTransport = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        review = new(host, controlledTransport);
    }

    internal async Task<ActionHostCompletion> RunAsync(ActionHostLaunchContract launch, CancellationToken cancellationToken)
    {
        var accounting = new ActionHostAccountingCapture();
        var preparedResult = await host.RunPreparedFreshAsync(launch, async prepared =>
        {
            var execution = review.PrepareStateExecution(prepared);
            var current = prepared with { RestoreRequest = prepared.RestoreRequest with {
                SolusTransaction = new(prepared.FreshContext!, execution.Compatibility, execution.ExecutionId,
                    SolusTransactionMode.FreshOrCompletedContextReview) } };
            SolusRestrictedStateCapture? capture = null;
            SolusRestrictedStateRecord? record = null;
            SolusContextSource? source = null;
            SolusConsumedContext? consumed = null;
            var captureValid = false;
            try
            {
                return await host.RunStateCommitPreparedAsync(current, async (state, scope, owner) =>
                {
                    accounting = owner;
                    // R4 has already classified pending/committed/recovery work.
                    var fresh = RestrictedStateService.CanPrepareFreshSolus(state);
                    var restored = RestrictedStateService.CanPrepareRestoredSolus(state);
                    if (fresh == restored) return new(ActionHostStatus.StateConflict);
                    if (prepared.Launch.Inputs.ProviderApiKey is null) return new(ActionHostStatus.CredentialsMissing);
                    if (SolusFreshReviewComposition.ValidateInput(prepared, [], out _, out _) != SolusFreshReviewFailure.None)
                        return new(ActionHostStatus.AgentResultInvalid);

                    if (restored)
                    {
                        if (!prepared.Journal.TryBeginBusinessOperation(ActionHostOperationKind.CandidatePreparation,
                                prepared.CancellationToken, out var claimOperation)) return new(prepared.Journal.CancellationStatus);
                        using (claimOperation!)
                        {
                            // Revalidation grants a consumption capability; it
                            // never makes another Fresh/Restored mode decision.
                            var selected = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(
                                prepared.RestoreRequest with {
                                    SolusSourceSelection = new(prepared.FreshContext!, execution.Compatibility) },
                                prepared.CancellationToken).ConfigureAwait(false);
                            source = selected.SolusSource;
                            if (selected.Code != AcceptedStateCodes.Ready || source is null)
                                return new(ClaimFailure(prepared));
                            consumed = await source.PrepareAsync(execution.ExecutionId, Guid.NewGuid(),
                                prepared.CancellationToken).ConfigureAwait(false);
                            if (consumed is null) return new(ClaimFailure(prepared));
                            claimOperation!.Resolve(ActionHostOperationResolution.ResolvedCommitted);
                        }
                    }

                    var work = await review.ExecuteAsync(prepared, null, [], execution,
                        compatibility => capture = new(execution.ExecutionId, compatibility),
                        actual => captureValid = capture?.TryComplete(actual, out record) == true,
                        owner.BeginProvider, consumed).ConfigureAwait(false);
                    if (work.Work is not null) owner.Complete(work.Work.Accounting, work.Work.Termination);
                    if (prepared.Journal.ObserveCancellation(prepared.CancellationToken))
                        return new(prepared.Journal.CancellationStatus);
                    if (work.Failure == SolusFreshReviewFailure.CredentialsMissing) return new(ActionHostStatus.CredentialsMissing);
                    if (!work.ProductReviewAccepted)
                        return new(work.Context?.Admission == SolusAgent.Api.Context.ContextAdmission.Rejected ? ActionHostStatus.StateConflict :
                            work.Work?.Termination == ActionHostTerminationReason.ProviderFailure ? ActionHostStatus.ProviderFailed :
                            work.Failure == SolusFreshReviewFailure.HostFailure ? ActionHostStatus.InternalFailure : ActionHostStatus.AgentResultInvalid);
                    if (work.Publication?.CandidateEligible != true || work.Publication.Review is null)
                        return new(work.Publication?.Head is { } head
                            ? ActionHostCoordinator.RevalidationStatus(head, prepared.Journal.CancellationStatus)
                            : ActionHostStatus.StateConflict);
                    if (!captureValid || record is null) return new(ActionHostStatus.StateConflict);
                    if (!prepared.Journal.TryBeginBusinessOperation(ActionHostOperationKind.CandidatePreparation,
                            prepared.CancellationToken, out var operation)) return new(prepared.Journal.CancellationStatus);
                    using (operation!)
                    {
                        var result = fresh
                            ? await RestrictedStateService.PrepareRetainedSolusCandidateAsync(state, record,
                                work.Publication.Review, prepared.CancellationToken).ConfigureAwait(false)
                            : await RestrictedStateService.PrepareRetainedRestoredSolusCandidateAsync(state, consumed!, record,
                                work.Publication.Review, prepared.CancellationToken).ConfigureAwait(false);
                        operation!.Resolve(ActionHostOperationResolution.ResolvedNoCommit);
                        return new(null, result);
                    }
                }).ConfigureAwait(false);
            }
            finally { consumed?.Dispose(); source?.Dispose(); capture?.Dispose(); }
        }, cancellationToken).ConfigureAwait(false);

        // A completed state coordinator already owns accurate accounting. Only
        // an outer failure needs its current capture applied; never replace a
        // dispatched provider's unknown accounting with a fresh zero capture.
        return preparedResult.Value ?? accounting.Apply(preparedResult.Failure ?? throw new InvalidOperationException());
    }

    private static ActionHostStatus ClaimFailure(ActionHostPreparedReview prepared) =>
        prepared.Journal.ObserveCancellation(prepared.CancellationToken) ? prepared.Journal.CancellationStatus :
        prepared.Journal.HasCurrentRunActivity && prepared.Journal.LatestResolution == ActionHostOperationResolution.Unresolved
            ? ActionHostStatus.OutcomeAmbiguous : ActionHostStatus.StateConflict;
}
#endif
