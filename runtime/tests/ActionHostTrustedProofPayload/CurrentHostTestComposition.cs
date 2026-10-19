using System.Globalization;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;

#if S5_EVALUATION_HOST
namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Reset;
#else
namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
#endif

// Credential-free Host regression driver. Records are structural test inputs;
// this driver does not capture/import SDK context or qualify state continuity.
// Authorization, root leases, transaction admission, publication and recovery
// remain the production Host paths. Recovery never invokes the work callback.
#if S5_EVALUATION_HOST
internal sealed class CurrentEvaluationHostTestComposition(ActionHostCompositionDependencies dependencies)
#else
internal sealed class CurrentHostTestComposition(ActionHostCompositionDependencies dependencies)
#endif
{
    internal static SolusRestrictedStateCompatibility Compatibility() => new(
        SolusRestrictedStateFormat.PackageVersion, SolusRestrictedStateFormat.Profile,
        SolusRestrictedStateFormat.CompletedCandidateMode, SolusRestrictedStateFormat.Implementation,
        SolusRestrictedStateFormat.ContextFormat, SolusRestrictedStateFormat.ContextCompatibility,
        SolusRestrictedStateFormat.PackageClosureSha256, new string('1', 64), new string('2', 64));

    internal async Task<ActionHostCompletion> RunAsync(ActionHostLaunchContract launch, CancellationToken token)
    {
        var host = new ActionHostComposition(dependencies);
        var result = await host.RunPreparedFreshAsync(launch, prepared =>
        {
            var compatibility = Compatibility();
            var execution = Guid.NewGuid();
            var current = prepared with { RestoreRequest = prepared.RestoreRequest with {
                SolusTransaction = new(prepared.FreshContext!, compatibility, execution,
                    SolusTransactionMode.FreshOrCompletedContextReview) } };
            return host.RunStateCommitPreparedAsync(current, (state, scope, accounting) =>
                PrepareCurrentWorkAsync(current, state, scope, accounting, compatibility, execution));
        }, token);
        return result.Value ?? new ActionHostAccountingCapture().Apply(result.Failure!);
    }

    private async Task<ActionHostCandidatePreparation> PrepareCurrentWorkAsync(ActionHostPreparedReview prepared,
        AuthorizedAcceptedStateRestoreContext state, R4PublicationScopeV1 scope,
        ActionHostAccountingCapture accounting, SolusRestrictedStateCompatibility compatibility, Guid execution)
    {
        var journal = prepared.Journal;
        var token = prepared.CancellationToken;
        var launch = prepared.Launch;
        if (launch.Inputs.ProviderApiKey is null) return new(ActionHostStatus.CredentialsMissing);
        var fresh = RestrictedStateService.CanPrepareFreshSolus(state);
        if ((!fresh && !RestrictedStateService.CanPrepareRestoredSolus(state)) ||
            !state.TryGetLineageSnapshot(out var lineage) || lineage is null)
            return new(ActionHostStatus.StateConflict);
        SolusConsumedContext? consumed = null;
        if (!fresh)
        {
            var selected = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(
                prepared.RestoreRequest with { SolusTransaction = null,
                    SolusSourceSelection = new(prepared.FreshContext!, compatibility) }, token);
            using var source = selected.SolusSource;
            if (source is null) return new(ActionHostStatus.StateConflict);
            consumed = await source.PrepareAsync(execution, Guid.NewGuid(), token);
            if (consumed is null) return new(ActionHostStatus.StateConflict);
            var payload = consumed.CopyPayload();
            try
            {
                if (!consumed.TryClaim(prepared.FreshContext!, compatibility, consumed.Claim.Source,
                    consumed.Claim.GrantId, execution, consumed.Claim.RoundId, payload))
                {
                    consumed.Dispose();
                    return new(ActionHostStatus.StateConflict);
                }
            }
            finally { CryptographicOperations.ZeroMemory(payload); }
        }
        using var consumedLifetime = consumed;
        // This selects synthetic current work after real source admission; it
        // conveys no SDK import, retained chat history or provider continuation.
        if (dependencies.ProviderFactory is ICurrentHostTestWorkPlanConsumer plan)
            plan.SelectCurrentWorkPlan(successor: !fresh);
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
                new(Guid.NewGuid(), Guid.NewGuid(), consumed?.Claim.RoundId ?? Guid.NewGuid(), execution), [1, 2, 3], out var record))
                return new(ActionHostStatus.AgentResultInvalid);
            var candidate = fresh
                ? await RestrictedStateService.PrepareRetainedSolusCandidateAsync(state, record!, review!, token)
                : await RestrictedStateService.PrepareRetainedRestoredSolusCandidateAsync(state, consumed!, record!, review!, token);
            preparation!.Resolve(candidate.Code is RetainedStateTransactionCodes.OutcomeUnknown
                ? ActionHostOperationResolution.Unresolved : ActionHostOperationResolution.ResolvedNoCommit);
            return new(null, candidate);
        }
    }
}

internal interface ICurrentHostTestWorkPlanConsumer
{
    void SelectCurrentWorkPlan(bool successor);
}
