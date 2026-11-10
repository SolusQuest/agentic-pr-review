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
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ActionHostCompletion, CompletedInvocation> completed = new();

    // In-process completed facts only. Neither the ordinary output nor state schema changes.
    // Keys are the actual returned completion and exact launch object; each result is read once.
    internal sealed record CompletedObservation(string Classification, long RunId, long RunAttempt,
        string ActionSourceSha, string WorkflowSha, string ReviewedHead, string FamilySha256,
        string LogicalGenerationIdentity, string EnvelopeSha256, string CheckpointSha256,
        string? PredecessorLogicalGenerationIdentity, string? PredecessorEnvelopeSha256,
        string? SourceCheckpointSha256);
    private sealed record CompletedInvocation(ActionHostLaunchContract Launch, CompletedObservation Facts);

    internal CompletedObservation? TakeCompletedObservation(ActionHostLaunchContract launch, ActionHostCompletion completion)
    {
        lock (completed)
        {
            if (!completed.TryGetValue(completion, out var value) || !ReferenceEquals(value.Launch, launch)) return null;
            completed.Remove(completion);
            return value.Facts;
        }
    }

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
        CompletedObservation? observation = null;
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
                    observation = null;
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
                    if (work.Work is not null) owner.Complete(work.Work.Accounting, work.Work.Termination, work.Work.FailureDiagnostic);
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
                        // Observation is subordinate to normal work/transaction acceptance. Never
                        // change a real completion or its accounting if observation is unavailable.
                        try
                        {
                            if (result.Succeeded && result.Value is { } candidate &&
                                record.Checkpoint.ExecutionId == execution.ExecutionId)
                            {
                                var generation = candidate.Generation;
                                var header = candidate.Header;
                                var restoredFacts = !fresh && source is not null && consumed?.WasClaimed == true &&
                                    source.Selection.Current.LogicalGenerationIdentity == consumed.Claim.LogicalGenerationIdentity &&
                                    source.Selection.Current.Generation.StateEnvelopeSha256 == consumed.Claim.EnvelopeSha256 &&
                                    source.Selection.Current.ReceiptPhysical.Header.ObjectIdentity == consumed.Claim.AcceptanceIdentity &&
                                    generation.PreviousLogicalGenerationIdentity == consumed.Claim.LogicalGenerationIdentity &&
                                    generation.PredecessorEnvelopeSha256 == consumed.Claim.EnvelopeSha256 &&
                                    header.BaseScopeDigest == consumed.Claim.BaseScopeDigest && header.Epoch == consumed.Claim.Epoch &&
                                    header.SessionId == consumed.Claim.SessionId &&
                                    record.Checkpoint.ExecutionId == consumed.Claim.ExecutionId && record.Checkpoint.RoundId == consumed.Claim.RoundId &&
                                    record.Checkpoint.LogicalWorkId != consumed.Claim.Source.LogicalWorkId &&
                                    record.Checkpoint.CheckpointId != consumed.Claim.Source.CheckpointId;
                                if (fresh || restoredFacts) observation = new(fresh ? "FreshAccepted" : "RestoredConsumedAccepted",
                                    launch.RunId, launch.RunAttempt, launch.ActionSourceSha, launch.WorkflowSha,
                                    generation.ProducerHeadSha,
                                    Digest(header.BaseScopeDigest, header.Epoch, header.SessionId),
                                    candidate.LogicalGenerationIdentity, generation.StateEnvelopeSha256,
                                    CheckpointDigest(record.Checkpoint), fresh ? null : consumed!.Claim.LogicalGenerationIdentity,
                                    fresh ? null : consumed!.Claim.EnvelopeSha256,
                                    fresh ? null : CheckpointDigest(consumed!.Claim.Source));
                            }
                        }
                        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
                        { observation = null; }
                        return new(null, result);
                    }
                }).ConfigureAwait(false);
            }
            finally { consumed?.Dispose(); source?.Dispose(); capture?.Dispose(); }
        }, cancellationToken).ConfigureAwait(false);

        // A completed state coordinator already owns accurate accounting. Only
        // an outer failure needs its current capture applied; never replace a
        // dispatched provider's unknown accounting with a fresh zero capture.
        var completion = preparedResult.Value ?? accounting.Apply(preparedResult.Failure ?? throw new InvalidOperationException());
        try
        {
            if (observation is not null && completion.ProcessExitCode == 0 &&
                completion.Status is ActionHostStatus.Reviewed or ActionHostStatus.ReviewedWithInlineWarnings &&
                completion.TerminationReason == ActionHostTerminationReason.ReviewCompleted &&
                completion.Summary.StateDisposition == ActionHostStateDisposition.Accepted &&
                completion.Summary.ReviewedSha == observation.ReviewedHead)
            {
                lock (completed) completed.Add(completion, new(launch, observation));
            }
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { }
        return completion;
    }

    private static string Digest(params string[] values) => Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            "apr.solus.completed-observation.v1\0" + string.Join("\0", values))));

    private static string CheckpointDigest(SolusRestrictedStateCheckpoint checkpoint) => Digest(
        checkpoint.CheckpointId.ToString("N"), checkpoint.LogicalWorkId.ToString("N"),
        checkpoint.RoundId.ToString("N"), checkpoint.ExecutionId.ToString("N"));

    private static ActionHostStatus ClaimFailure(ActionHostPreparedReview prepared) =>
        prepared.Journal.ObserveCancellation(prepared.CancellationToken) ? prepared.Journal.CancellationStatus :
        prepared.Journal.HasCurrentRunActivity && prepared.Journal.LatestResolution == ActionHostOperationResolution.Unresolved
            ? ActionHostStatus.OutcomeAmbiguous : ActionHostStatus.StateConflict;
}
#endif
