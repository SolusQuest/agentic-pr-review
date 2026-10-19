using System.Globalization;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;
using AgenticPrReview.Runtime.Tests.Host.State.Transactions;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

// Credential-free Host regression driver. Records are structural test inputs;
// this driver does not capture/import SDK context or qualify state continuity.
// Authorization, root leases, transaction admission, publication and recovery
// remain the production Host paths. Recovery never invokes the work callback.
internal sealed class CurrentHostTestComposition(ActionHostCompositionDependencies dependencies)
{
    internal async Task<ActionHostCompletion> RunAsync(ActionHostLaunchContract launch, CancellationToken token)
    {
        var host = new ActionHostComposition(dependencies);
        var result = await host.RunPreparedFreshAsync(launch, prepared =>
        {
            var compatibility = SolusTransactionTestSupport.Compatibility();
            var execution = Guid.NewGuid();
            var current = prepared with { RestoreRequest = prepared.RestoreRequest with {
                SolusTransaction = new(prepared.FreshContext!, compatibility, execution,
                    SolusTransactionMode.FreshOrCompletedContextReview) } };
            return host.RunStateCommitPreparedAsync(current, (state, scope, accounting) =>
                PrepareFreshAsync(current, state, scope, accounting, compatibility, execution));
        }, token);
        return result.Value ?? new ActionHostAccountingCapture().Apply(result.Failure!);
    }

    private async Task<ActionHostCandidatePreparation> PrepareFreshAsync(ActionHostPreparedReview prepared,
        AuthorizedAcceptedStateRestoreContext state, R4PublicationScopeV1 scope,
        ActionHostAccountingCapture accounting, SolusRestrictedStateCompatibility compatibility, Guid execution)
    {
        var journal = prepared.Journal;
        var token = prepared.CancellationToken;
        var launch = prepared.Launch;
        if (launch.Inputs.ProviderApiKey is null) return new(ActionHostStatus.CredentialsMissing);
        // Successor/import scenarios have their own explicit current-context driver.
        if (!RestrictedStateService.CanPrepareFreshSolus(state) ||
            !state.TryGetLineageSnapshot(out var lineage) || lineage is null)
            return new(ActionHostStatus.StateConflict);
        var policy = prepared.Policy;
        var trusted = new AgentTrustedRequest(launch.RepositoryId.ToString(CultureInfo.InvariantCulture),
            prepared.Invocation.PullRequest.Number,
            AuthorizedAcceptedStateComposer.TrustedWorkflowIdentity(prepared.Invocation, policy),
            policy.InstructionBytes.ToArray(), policy.BuildDiscriminator, policy.ProviderId,
            policy.ModelId, policy.AdapterId, policy.LimitAuthority);
        if (!AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var materialized))
            return new(ActionHostStatus.AgentResultInvalid);
        var run = new AgentRunRequest(prepared.Snapshot.Snapshot.Identity,
            materialized!.StablePlan, lineage.SessionId,
            [.. materialized.ControlMessages, prepared.RestoreRequest.CurrentReviewContext]);
        if (!journal.TryBeginBusinessOperation(ActionHostOperationKind.Provider, token, out var operation))
            return new(journal.CancellationStatus);
        AgentRunOutcome outcome;
        try
        {
            using (operation!)
            using (var runner = dependencies.ProviderFactory.Create(
                new(policy.ProviderId, policy.ModelId, policy.AdapterId, policy.LimitAuthority),
                launch.Inputs.ProviderApiKey, prepared.Snapshot.Snapshot, prepared.TimeBudget))
            {
                accounting.BeginProvider();
                outcome = await runner.RunAsync(run with { RemainingHostTime = journal.RemainingReviewTime },
                    journal.ProviderCancellationToken);
                accounting.Complete(outcome);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return new(ActionHostStatus.ProviderFailed);
        }
        if (journal.ObserveCancellation(token)) return new(journal.CancellationStatus);
        if (!outcome.CompletedSessionEligible)
            return new(outcome.Diagnostic?.Code switch {
                AgentFailureCodes.Cancelled => journal.CancellationStatus,
                AgentFailureCodes.ChatFailed or AgentFailureCodes.DeadlineExceeded => ActionHostStatus.ProviderFailed,
                _ => ActionHostStatus.AgentResultInvalid });
        if (outcome.Review is null || outcome.ReviewedIdentity is null ||
            !R4ValidatedPublicationReview.TryCreate(outcome, scope, out var review)) return new(ActionHostStatus.AgentResultInvalid);
        if (!journal.TryBeginBusinessOperation(ActionHostOperationKind.HeadRevalidation, token, out var revalidation))
            return new(journal.CancellationStatus);
        ExactHeadRevalidationResult exact;
        using (revalidation!)
            exact = await ExactHeadRevalidator.RevalidateAsync(prepared.Invocation.PullRequest,
                launch.Inputs.GitHubToken!, prepared.RevalidationFactory, token);
        if (!exact.MayMutate) return new(ActionHostCoordinator.RevalidationStatus(exact.Status, journal.CancellationStatus));
        if (!journal.TryBeginBusinessOperation(ActionHostOperationKind.CandidatePreparation, token, out var preparation))
            return new(journal.CancellationStatus);
        using (preparation!)
        {
            if (!SolusRestrictedStateRecord.TryCreate(compatibility,
                new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), execution), [1, 2, 3], out var record))
                return new(ActionHostStatus.AgentResultInvalid);
            var candidate = await RestrictedStateService.PrepareRetainedSolusCandidateAsync(state, record!, review!, token);
            preparation!.Resolve(candidate.Code is RetainedStateTransactionCodes.OutcomeUnknown
                ? ActionHostOperationResolution.Unresolved : ActionHostOperationResolution.ResolvedNoCommit);
            return new(null, candidate);
        }
    }
}
