using System.Collections.Immutable;
using System.Globalization;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Policy;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Inline;
using AgenticPrReview.Runtime.Host.Publishing.Recovery;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;

namespace AgenticPrReview.Runtime.ActionHost;

internal sealed record ActionHostProviderPolicy(
    string ProviderId,
    string ModelId,
    string AdapterId);

internal interface IActionHostProviderRunnerFactory
{
    IActionHostProviderRunner Create(
        ActionHostProviderPolicy policy,
        ActionHostProviderApiKey key,
        ReviewedSnapshot snapshot,
        TimeProvider timeProvider);
}

internal interface IActionHostProviderRunner : IDisposable
{
    Task<AgentRunOutcome> RunAsync(
        AgentRunRequest run,
        CancellationToken cancellationToken);
}

internal sealed class ActionHostDeepSeekProviderRunnerFactory :
    IActionHostProviderRunnerFactory
{
    public IActionHostProviderRunner Create(
        ActionHostProviderPolicy policy,
        ActionHostProviderApiKey key,
        ReviewedSnapshot snapshot,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var credential = DeepSeekCredential.Create(
            key.ExportForPrivateLaunch());
        var transport = DeepSeekTransport.Create(credential);
        try
        {
            return new Runner(policy, snapshot, timeProvider, transport);
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    private sealed class Runner : IActionHostProviderRunner
    {
        private readonly DeepSeekTransport transport;
        private readonly ReviewedSnapshot snapshot;
        private readonly TimeProvider timeProvider;
        private readonly ActionHostProviderPolicy policy;

        internal Runner(
            ActionHostProviderPolicy policy,
            ReviewedSnapshot snapshot,
            TimeProvider timeProvider,
            DeepSeekTransport transport)
        {
            this.policy = policy;
            this.snapshot = snapshot;
            this.timeProvider = timeProvider;
            this.transport = transport;
        }

        public Task<AgentRunOutcome> RunAsync(
            AgentRunRequest run,
            CancellationToken cancellationToken)
        {
            var adapter = new DeepSeekAdapterContext(
                policy.ProviderId,
                policy.ModelId,
                policy.AdapterId,
                run.SessionId);
            if (!adapter.IsValid)
            {
                return Task.FromResult(AgentRunOutcome.Failure(
                    AgentFailureCodes.ResponseInvalid,
                    0,
                    0,
                    []));
            }

            var client = DeepSeekChatBackend.CreateClient(adapter, transport);
            var tools = new SnapshotToolExecutor(
                snapshot,
                new VerifiedReviewedFileAccess());
            return new AgentLoop(client, tools, timeProvider)
                .RunAsync(run, cancellationToken);
        }

        public void Dispose() => transport.Dispose();
    }
}

internal enum ActionHostInlineHookResult
{
    Complete = 1,
    Incomplete,
}

internal interface IActionHostPostAcceptanceInlineHook
{
    Task<ActionHostInlineHookResult> PublishAsync(
        ActionHostCoordinator.PostAcceptanceInlineRequest request,
        CancellationToken cancellationToken);
}

internal sealed class ActionHostCoordinator
{
    private const int MaximumConvergenceIterations = 16;
    private const string InlineMapDomain =
        "agentic-pr-review/r4/post-acceptance-inline-map/v1";

    private readonly StickyCommentPublisher publisher;
    private readonly IActionHostReviewedSnapshotTransportFactory
        revalidationFactory;
    private readonly IActionHostProviderRunnerFactory providerFactory;
    private readonly IActionHostPostAcceptanceInlineHook? inlineHook;
    private readonly TimeProvider timeProvider;

    internal ActionHostCoordinator(
        StickyCommentPublisher publisher,
        IActionHostReviewedSnapshotTransportFactory revalidationFactory,
        IActionHostProviderRunnerFactory providerFactory,
        TimeProvider timeProvider,
        IActionHostPostAcceptanceInlineHook? inlineHook = null)
    {
        this.publisher = publisher ??
            throw new ArgumentNullException(nameof(publisher));
        this.revalidationFactory = revalidationFactory ??
            throw new ArgumentNullException(nameof(revalidationFactory));
        this.providerFactory = providerFactory ??
            throw new ArgumentNullException(nameof(providerFactory));
        this.timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));
        this.inlineHook = inlineHook;
    }

    internal async Task<ActionHostCompletion> RunAsync(
        ActionHostLaunchContract launch,
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        ActionHostTrustedPolicy policy,
        BoundedReviewedSnapshotLease snapshot,
        AuthorizedAcceptedStateRestoreContext state,
        R4PublicationScopeV1 scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(scope);

        var recovery = new PublicationRecoveryService(publisher);
        var progress = new HashSet<string>(StringComparer.Ordinal);
        var candidateCommittedThisRun = false;
        for (var iteration = 0;
            iteration < MaximumConvergenceIterations;
            iteration++)
        {
            using var evaluation = await recovery.ClassifyBeforeProviderAsync(
                    launch.Inputs.GitHubToken!,
                    invocation,
                    scope,
                    state,
                    CancellationToken.None)
                .ConfigureAwait(false);
            var observation = evaluation.Observation;
            var progressKey = string.Concat(
                ((int)evaluation.Decision.Action).ToString(
                    CultureInfo.InvariantCulture),
                ":",
                ((int)evaluation.Decision.Lifecycle).ToString(
                    CultureInfo.InvariantCulture),
                ":",
                observation?.InventoryDigest ?? "none",
                ":",
                ((int)evaluation.DiscoveryKind).ToString(
                    CultureInfo.InvariantCulture),
                ":",
                evaluation.ExactReadbackReceipt?.CommentId.ToString(
                    CultureInfo.InvariantCulture) ?? "none");
            if (!progress.Add(progressKey))
            {
                return Failure(launch, ActionHostStatus.StateConflict);
            }

            switch (evaluation.Decision.Action)
            {
                case PublicationRecoveryAction.Conflict:
                    return Failure(launch, ActionHostStatus.StateConflict);

                case PublicationRecoveryAction.NoPendingWork:
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return Failure(launch, ActionHostStatus.Cancelled);
                    }

                    if (launch.Inputs.ProviderApiKey is null)
                    {
                        return Failure(
                            launch,
                            ActionHostStatus.CredentialsMissing);
                    }

                    if (!RestrictedStateService
                            .TryGetCanonicalAgentRunRequest(
                                state,
                                out var run) ||
                        run is null)
                    {
                        return Failure(
                            launch,
                            ActionHostStatus.StateConflict);
                    }

                    AgentRunOutcome outcome;
                    try
                    {
                        using var runner = providerFactory.Create(
                            new ActionHostProviderPolicy(
                                policy.ProviderId,
                                policy.ModelId,
                                policy.AdapterId),
                            launch.Inputs.ProviderApiKey,
                            snapshot.Snapshot,
                            timeProvider);
                        outcome = await runner.RunAsync(run, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (IsNonFatal(exception))
                    {
                        return Failure(
                            launch,
                            ActionHostStatus.ProviderFailed);
                    }

                    if (!outcome.CompletedSessionEligible)
                    {
                        return Failure(
                            launch,
                            AgentFailureStatus(outcome, candidateCommittedThisRun));
                    }

                    if (!R4PreparedPublication.TryCreate(
                            outcome,
                            scope,
                            out var publication) ||
                        publication is null)
                    {
                        return Failure(
                            launch,
                            ActionHostStatus.AgentResultInvalid);
                    }

                    var exact = await RevalidateAsync(
                            invocation,
                            launch,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!exact.MayMutate)
                    {
                        return Failure(
                            launch,
                            RevalidationStatus(exact.Status));
                    }

                    var preparedResult = await RestrictedStateService
                        .PrepareRetainedCandidateAsync(
                            state,
                            run,
                            publication,
                            cancellationToken)
                        .ConfigureAwait(false);
                    using var prepared = preparedResult.Value;
                    if (!preparedResult.Succeeded || prepared is null)
                    {
                        return Failure(
                            launch,
                            TransactionStatus(preparedResult.Code));
                    }

                    var persisted = await RestrictedStateService
                        .PersistRetainedCandidateAsync(
                            state,
                            prepared,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!persisted.Succeeded || persisted.Value is null)
                    {
                        return Failure(
                            launch,
                            TransactionStatus(persisted.Code));
                    }

                    candidateCommittedThisRun = true;
                    break;
                }

                case PublicationRecoveryAction.CleanupSupersededRecovery:
                {
                    if (cancellationToken.IsCancellationRequested &&
                        !candidateCommittedThisRun)
                    {
                        return Failure(launch, ActionHostStatus.Cancelled);
                    }

                    var cleanup = await PublicationRecoveryService
                        .CleanupHistoricalRecoveryRecordsAsync(
                            invocation,
                            state,
                            evaluation,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    if (!cleanup.Completed)
                    {
                        return Failure(
                            launch,
                            TransactionStatus(cleanup.Code));
                    }

                    break;
                }

                case PublicationRecoveryAction.ReturnCommitted:
                    return await ReturnCommittedAsync(
                            launch,
                            invocation,
                            policy,
                            snapshot,
                            state,
                            scope,
                            evaluation,
                            cancellationToken)
                        .ConfigureAwait(false);

                case PublicationRecoveryAction.ResumeBeforeIntent:
                {
                    if (observation is null)
                    {
                        return Failure(
                            launch,
                            ActionHostStatus.StateConflict);
                    }

                    if (evaluation.Decision.Action ==
                            PublicationRecoveryAction.AbandonStaleCandidate &&
                        cancellationToken.IsCancellationRequested &&
                        !candidateCommittedThisRun)
                    {
                        return Failure(launch, ActionHostStatus.Cancelled);
                    }

                    var intentResult = await PublicationRecoveryPersistence
                        .PersistIntentAndAuthorizeAsync(
                            state,
                            observation,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    using var intent = intentResult.Value;
                    if (!intentResult.Succeeded || intent is null)
                    {
                        return Failure(
                            launch,
                            TransactionStatus(intentResult.Code));
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        var failureResult = await PersistResultAsync(
                                state,
                                intent.Observation,
                                intent.Intent.RecordIdentity,
                                retryIntent: null,
                                receipt: null,
                                BoundedGitHubPublisherOutcome
                                    .CancelledBeforeSend,
                                StickyPublicationReason.Cancelled)
                            .ConfigureAwait(false);
                        using var persistedFailure = failureResult.Record;
                        if (persistedFailure is null)
                        {
                            return Failure(
                                launch,
                                PublicationPersistenceStatus(
                                    failureResult.Code));
                        }

                        break;
                    }

                    var sticky = await PublishAsync(
                            launch,
                            invocation,
                            scope,
                            intent.Observation,
                            intent.StickyWriteAuthorization,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (sticky.RevalidationStatus is { } revalidationStatus)
                    {
                        if (revalidationStatus ==
                            ExactHeadRevalidationStatus.Cancelled)
                        {
                            var failureResult = await PersistResultAsync(
                                        state,
                                        intent.Observation,
                                        intent.Intent.RecordIdentity,
                                        retryIntent: null,
                                        receipt: null,
                                        BoundedGitHubPublisherOutcome
                                            .CancelledBeforeSend,
                                        StickyPublicationReason.Cancelled)
                                    .ConfigureAwait(false);
                            using var persistedFailure = failureResult.Record;
                            if (persistedFailure is null)
                            {
                                return Failure(
                                    launch,
                                    PublicationPersistenceStatus(
                                        failureResult.Code));
                            }

                            break;
                        }

                        return Failure(
                            launch,
                            RevalidationStatus(revalidationStatus));
                    }

                    var persistence = await PersistResultAsync(
                            state,
                            intent.Observation,
                            intent.Intent.RecordIdentity,
                            retryIntent: null,
                            sticky.Result?.Receipt,
                            sticky.Result?.Outcome ??
                                BoundedGitHubPublisherOutcome.OutcomeUnknown,
                            sticky.Result?.Reason ??
                                StickyPublicationReason
                                    .ReconciliationIncomplete)
                        .ConfigureAwait(false);
                    using var persisted = persistence.Record;
                    if (persisted is null)
                    {
                        return Failure(
                            launch,
                            PublicationPersistenceStatus(persistence.Code));
                    }

                    break;
                }

                case PublicationRecoveryAction.ResumeKnownNotWritten:
                {
                    if (observation is null ||
                        evaluation.RetryTransitionAuthorization is null)
                    {
                        return Failure(
                            launch,
                            ActionHostStatus.StateConflict);
                    }

                    if (cancellationToken.IsCancellationRequested &&
                        !candidateCommittedThisRun)
                    {
                        return Failure(launch, ActionHostStatus.Cancelled);
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        return PublicationRecoveryService
                                .TryTerminalizeFreshKnownNotWritten(
                                    evaluation,
                                    out var terminal) &&
                            terminal?.Action == PublicationRecoveryAction
                                .KnownNotWrittenTerminal
                            ? Failure(
                                launch,
                                ActionHostStatus.StickyPublicationFailed)
                            : Failure(
                                launch,
                                ActionHostStatus.StateConflict);
                    }

                    var retryResult = await PublicationRecoveryPersistence
                        .PersistRetryIntentAndAuthorizeAsync(
                            state,
                            observation,
                            evaluation.RetryTransitionAuthorization,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    using var retry = retryResult.Value;
                    if (!retryResult.Succeeded || retry is null)
                    {
                        return Failure(
                            launch,
                            TransactionStatus(retryResult.Code));
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        var failureResult = await PersistResultAsync(
                                state,
                                retry.Observation,
                                retry.RetryIntent.RecordIdentity,
                                retry.RetryIntent,
                                receipt: null,
                                BoundedGitHubPublisherOutcome
                                    .CancelledBeforeSend,
                                StickyPublicationReason.Cancelled)
                            .ConfigureAwait(false);
                        using var persistedFailure = failureResult.Record;
                        if (persistedFailure is null)
                        {
                            return Failure(
                                launch,
                                PublicationPersistenceStatus(
                                    failureResult.Code));
                        }

                        break;
                    }

                    var sticky = await PublishAsync(
                            launch,
                            invocation,
                            scope,
                            retry.Observation,
                            retry.StickyWriteAuthorization,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (sticky.RevalidationStatus is { } revalidationStatus)
                    {
                        if (revalidationStatus ==
                            ExactHeadRevalidationStatus.Cancelled)
                        {
                            var failureResult = await PersistResultAsync(
                                        state,
                                        retry.Observation,
                                        retry.RetryIntent.RecordIdentity,
                                        retry.RetryIntent,
                                        receipt: null,
                                        BoundedGitHubPublisherOutcome
                                            .CancelledBeforeSend,
                                        StickyPublicationReason.Cancelled)
                                    .ConfigureAwait(false);
                            using var persistedFailure = failureResult.Record;
                            if (persistedFailure is null)
                            {
                                return Failure(
                                    launch,
                                    PublicationPersistenceStatus(
                                        failureResult.Code));
                            }

                            break;
                        }

                        return Failure(
                            launch,
                            RevalidationStatus(revalidationStatus));
                    }

                    var persistence = await PersistResultAsync(
                            state,
                            retry.Observation,
                            retry.RetryIntent.RecordIdentity,
                            retry.RetryIntent,
                            sticky.Result?.Receipt,
                            sticky.Result?.Outcome ??
                                BoundedGitHubPublisherOutcome.OutcomeUnknown,
                            sticky.Result?.Reason ??
                                StickyPublicationReason
                                    .ReconciliationIncomplete)
                        .ConfigureAwait(false);
                    using var persisted = persistence.Record;
                    if (persisted is null)
                    {
                        return Failure(
                            launch,
                            PublicationPersistenceStatus(persistence.Code));
                    }

                    break;
                }

                case PublicationRecoveryAction.KnownNotWrittenTerminal:
                case PublicationRecoveryAction.CancelledBeforeSend:
                    return Failure(
                        launch,
                        ActionHostStatus.StickyPublicationFailed);

                case PublicationRecoveryAction.CompleteAcceptance:
                    return await CompleteAcceptanceAsync(
                            launch,
                            invocation,
                            policy,
                            snapshot,
                            state,
                            scope,
                            evaluation,
                            cancellationToken)
                        .ConfigureAwait(false);

                case PublicationRecoveryAction.StickyOutcomeUnknown:
                    return Failure(
                        launch,
                        ActionHostStatus.OutcomeAmbiguous);

                case PublicationRecoveryAction.AbandonStaleCandidate:
                case PublicationRecoveryAction.ResumeStaleCleanup:
                {
                    if (cancellationToken.IsCancellationRequested &&
                        !candidateCommittedThisRun)
                    {
                        return Failure(launch, ActionHostStatus.Cancelled);
                    }

                    var cleanup = await recovery
                        .AbandonAndCleanupStaleCandidateAsync(
                            launch.Inputs.GitHubToken!,
                            invocation,
                            scope,
                            state,
                            evaluation,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    if (!cleanup.Completed)
                    {
                        return Failure(
                            launch,
                            TransactionStatus(cleanup.Code));
                    }

                    break;
                }

                case PublicationRecoveryAction.ResumeAnchoredWrite:
                {
                    var resumedResult = await PublicationRecoveryService
                        .ResumeInterruptedWriteAsync(
                            state,
                            evaluation,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    using var resumed = resumedResult.Value;
                    if (resumed is null)
                    {
                        return Failure(
                            launch,
                            PublicationPersistenceStatus(
                                resumedResult.Code));
                    }

                    break;
                }

                case PublicationRecoveryAction.ResumeCleanup:
                {
                    var cleanup = await PublicationRecoveryService
                        .ResumeInterruptedCleanupAsync(
                            state,
                            evaluation,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    if (!cleanup.Completed)
                    {
                        return Failure(
                            launch,
                            TransactionStatus(cleanup.Code));
                    }

                    break;
                }

                case PublicationRecoveryAction
                    .AuthorizationOrValidationFailure:
                    return Failure(
                        launch,
                        evaluation.DiscoveryReason ==
                            StickyPublicationReason.AuthorizationDenied
                            ? ActionHostStatus.AuthorizationFailed
                            : ActionHostStatus.StickyPublicationFailed);
            }
        }

        return Failure(launch, ActionHostStatus.StateConflict);
    }

    private async Task<(StickyCommentPublisher.StickyPublicationResult? Result,
        ExactHeadRevalidationStatus? RevalidationStatus)> PublishAsync(
        ActionHostLaunchContract launch,
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        R4PublicationScopeV1 scope,
        PublicationRecoveryObservation observation,
        PublicationStickyWriteAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var exact = await RevalidateAsync(
                invocation,
                launch,
                cancellationToken)
            .ConfigureAwait(false);
        if (!exact.MayMutate)
        {
            return (null, exact.Status);
        }

        if (!PublicationRecoveryService.TryRestoreRendered(
                observation.StoredPublication,
                out var rendered) ||
            rendered is null ||
            !AuthorizedStickyPublicationRequest.TryCreateRecovery(
                invocation,
                scope,
                rendered,
                observation,
                authorization,
                out var request) ||
            request is null)
        {
            return (null, ExactHeadRevalidationStatus.InvalidResponse);
        }

        return (await publisher.PublishAsync(
                launch.Inputs.GitHubToken!,
                request,
                cancellationToken)
            .ConfigureAwait(false), null);
    }

    private sealed record PublicationResultPersistence(
        RetainedStateOpaqueRecord? Record,
        string Code);

    private async Task<PublicationResultPersistence> PersistResultAsync(
        AuthorizedAcceptedStateRestoreContext state,
        PublicationRecoveryObservation observation,
        string attemptIntentIdentity,
        PublicationRetryIntentV1? retryIntent,
        StickyCommentPublisher.StickyPublicationReceipt? receipt,
        BoundedGitHubPublisherOutcome outcome,
        StickyPublicationReason reason)
    {
        var recoveredResult = await RestrictedStateService
            .RecoverRetainedCandidateAsync(state, CancellationToken.None)
            .ConfigureAwait(false);
        var candidate = recoveredResult.Value;
        if (!recoveredResult.Succeeded || candidate is null)
        {
            candidate?.Prepared.Dispose();
            return new(null, recoveredResult.Code);
        }

        using var prepared = candidate.Prepared;
        var ownershipResult = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                state,
                candidate,
                prior: null,
                observation.Records,
                CancellationToken.None)
            .ConfigureAwait(false);
        using var ownership = ownershipResult.Value;
        if (!ownershipResult.Succeeded || ownership is null)
        {
            return new(null, ownershipResult.Code);
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        RetainedStateOpaqueWriteRequest? request;
        if (receipt is not null &&
            outcome == BoundedGitHubPublisherOutcome.WrittenAndReadBack)
        {
            if (!PublicationRecoveryPersistence.TryCreateStickyReadbackWrite(
                    candidate,
                    attemptIntentIdentity,
                    receipt,
                    now,
                    candidate.Prepared.Header.LogicalExpiresAtUnixSeconds,
                    out _,
                    out request))
            {
                return new(null, RetainedStateTransactionCodes.Invalid);
            }
        }
        else if (retryIntent is null)
        {
            if (!PublicationRecoveryPersistence.TryCreateFailureWrite(
                    candidate,
                    attemptIntentIdentity,
                    outcome,
                    reason,
                    now,
                    candidate.Prepared.Header.LogicalExpiresAtUnixSeconds,
                    out _,
                    out request))
            {
                return new(null, RetainedStateTransactionCodes.Invalid);
            }
        }
        else if (!PublicationRecoveryPersistence.TryCreateRetryFailureWrite(
                candidate,
                retryIntent,
                outcome,
                reason,
                now,
                candidate.Prepared.Header.LogicalExpiresAtUnixSeconds,
                out _,
                out request))
        {
            return new(null, RetainedStateTransactionCodes.Invalid);
        }

        var attemptResult = await RestrictedStateService
            .PrepareRetainedOpaqueWriteAsync(
                state,
                ownership,
                request!,
                CancellationToken.None)
            .ConfigureAwait(false);
        using var attempt = attemptResult.Value;
        if (!attemptResult.Succeeded || attempt is null)
        {
            return new(null, attemptResult.Code);
        }

        var persisted = await RestrictedStateService
            .PersistPreparedRetainedOpaqueWriteAsync(
                state,
                attempt,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!persisted.Succeeded || persisted.Value is null)
        {
            persisted.Value?.Dispose();
            return new(null, persisted.Code);
        }

        var cleanup = await PublicationRecoveryPersistence
            .CleanupCompletedWriteAnchorAsync(
                state,
                attempt,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!cleanup.Completed)
        {
            persisted.Value.Dispose();
            return new(null, cleanup.Code);
        }

        return new(persisted.Value, RetainedStateTransactionCodes.Persisted);
    }

    private async Task<ActionHostCompletion> CompleteAcceptanceAsync(
        ActionHostLaunchContract launch,
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        ActionHostTrustedPolicy policy,
        BoundedReviewedSnapshotLease snapshot,
        AuthorizedAcceptedStateRestoreContext state,
        R4PublicationScopeV1 scope,
        PublicationRecoveryEvaluation evaluation,
        CancellationToken callerCancellationToken)
    {
        var observation = evaluation.Observation;
        var receipt = evaluation.ExactReadbackReceipt;
        if (observation is null || receipt is null)
        {
            return Failure(launch, ActionHostStatus.StateConflict);
        }

        if (observation.StickyReadback is null)
        {
            var activeAttempt = observation.RetryIntent?.RecordIdentity ??
                observation.Intent?.RecordIdentity;
            if (activeAttempt is null)
            {
                return Failure(launch, ActionHostStatus.StateConflict);
            }

            var persistence = await PersistResultAsync(
                    state,
                    observation,
                    activeAttempt,
                    observation.RetryIntent,
                    receipt,
                    BoundedGitHubPublisherOutcome.WrittenAndReadBack,
                    StickyPublicationReason.None)
                .ConfigureAwait(false);
            using var persisted = persistence.Record;
            return persisted is null
                ? Failure(
                    launch,
                    PublicationPersistenceStatus(persistence.Code))
                : await RunAsync(
                        launch,
                        invocation,
                        policy,
                        snapshot,
                        state,
                        scope,
                        CancellationToken.None)
                    .ConfigureAwait(false);
        }

        var recoveredResult = await RestrictedStateService
            .RecoverRetainedCandidateAsync(state, CancellationToken.None)
            .ConfigureAwait(false);
        var candidate = recoveredResult.Value;
        if (!recoveredResult.Succeeded || candidate is null)
        {
            candidate?.Prepared.Dispose();
            return Failure(
                launch,
                TransactionStatus(recoveredResult.Code));
        }

        using var preparedCandidate = candidate.Prepared;
        if (!RestrictedStateService.TryGetCurrentReviewProjection(
                state,
                candidate,
                out var projection) ||
            projection is null)
        {
            return Failure(launch, ActionHostStatus.StateConflict);
        }
        var inlineProjectionUnavailable = !TryBuildInlineMap(
            policy,
            snapshot,
            projection,
            out var map);

        var ownershipResult = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                state,
                candidate,
                prior: null,
                observation.Records,
                CancellationToken.None)
            .ConfigureAwait(false);
        using var ownership = ownershipResult.Value;
        if (!ownershipResult.Succeeded || ownership is null)
        {
            return Failure(
                launch,
                TransactionStatus(ownershipResult.Code));
        }

        var preparationResult = await RestrictedStateService
            .PrepareRetainedStateAcceptanceAsync(
                state,
                ownership,
                receipt,
                CancellationToken.None)
            .ConfigureAwait(false);
        using var preparation = preparationResult.Value;
        if (!preparationResult.Succeeded || preparation is null ||
            !PublicationRecoveryPersistence.TryCreateAcceptanceRecoveryWrite(
                preparation,
                observation.StickyReadback,
                out _,
                out var recoveryWrite) ||
            recoveryWrite is null)
        {
            return Failure(
                launch,
                TransactionStatus(preparationResult.Code));
        }

        var attemptResult = await RestrictedStateService
            .PrepareRetainedOpaqueWriteAsync(
                state,
                preparation.Ownership,
                recoveryWrite,
                CancellationToken.None)
            .ConfigureAwait(false);
        using var attempt = attemptResult.Value;
        if (!attemptResult.Succeeded || attempt is null)
        {
            return Failure(
                launch,
                TransactionStatus(attemptResult.Code));
        }

        var persistedResult = await RestrictedStateService
            .PersistPreparedRetainedOpaqueWriteAsync(
                state,
                attempt,
                CancellationToken.None)
            .ConfigureAwait(false);
        using var recoveryRecord = persistedResult.Value;
        if (!persistedResult.Succeeded || recoveryRecord is null)
        {
            return Failure(
                launch,
                TransactionStatus(persistedResult.Code));
        }

        using var extraction = PublicationRecoveryPersistence
            .CreateAcceptanceRecoveryExtraction(state, recoveryRecord).Value;
        if (extraction is null)
        {
            return Failure(launch, ActionHostStatus.StateConflict);
        }

        var durabilityResult = await RestrictedStateService
            .BindRetainedStateAcceptanceRecoveryAsync(
                state,
                preparation,
                extraction,
                CancellationToken.None)
            .ConfigureAwait(false);
        using var durability = durabilityResult.Value;
        if (!durabilityResult.Succeeded || durability is null)
        {
            return Failure(
                launch,
                TransactionStatus(durabilityResult.Code));
        }

        var predecessorCode = await RestrictedStateService
            .ReconcileRetainedStateAcceptancePredecessorAsync(
                state,
                preparation,
                durability,
                CancellationToken.None)
            .ConfigureAwait(false);
        for (var attemptIndex = 0;
            attemptIndex < 4 && StringComparer.Ordinal.Equals(
                predecessorCode,
                RetainedStateTransactionCodes.OutcomeUnknown);
            attemptIndex++)
        {
            predecessorCode = await RestrictedStateService
                .ReconcileRetainedStateAcceptancePredecessorAsync(
                    state,
                    preparation,
                    durability,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        if (!StringComparer.Ordinal.Equals(
                predecessorCode,
                RetainedStateTransactionCodes.Ready))
        {
            return Failure(
                launch,
                TransactionStatus(predecessorCode));
        }

        var allP5 = observation.Records.Add(recoveryRecord);
        var finalOwnershipResult = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                state,
                candidate,
                prior: null,
                allP5,
                CancellationToken.None)
            .ConfigureAwait(false);
        using var finalOwnership = finalOwnershipResult.Value;
        if (!finalOwnershipResult.Succeeded || finalOwnership is null)
        {
            return Failure(
                launch,
                TransactionStatus(finalOwnershipResult.Code));
        }

        var exact = await RevalidateAsync(
                invocation,
                launch,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!exact.MayMutate)
        {
            return Failure(
                launch,
                exact.Status is ExactHeadRevalidationStatus.HeadChanged or
                    ExactHeadRevalidationStatus.PullRequestIneligible or
                    ExactHeadRevalidationStatus.PullRequestMissing
                    ? ActionHostStatus.StaleHead
                    : ActionHostStatus.OutcomeAmbiguous);
        }

        var evidenceResult = await RestrictedStateService
            .CreateRetainedStateAcceptanceEvidenceAsync(
                state,
                preparation,
                durability,
                finalOwnership,
                exact,
                CancellationToken.None)
            .ConfigureAwait(false);
        using var evidence = evidenceResult.Value;
        if (!evidenceResult.Succeeded || evidence is null)
        {
            return Failure(
                launch,
                TransactionStatus(evidenceResult.Code));
        }

        var acceptedResult = await RestrictedStateService
            .AcceptRetainedStateAsync(
                state,
                evidence,
                CancellationToken.None)
            .ConfigureAwait(false);
        var acceptance = acceptedResult.Value;
        if (!acceptedResult.Succeeded || acceptance is null)
        {
            return Failure(
                launch,
                TransactionStatus(acceptedResult.Code));
        }

        return await FinishAcceptedAsync(
                launch,
                invocation,
                policy,
                snapshot,
                state,
                scope,
                acceptance,
                candidate.Prepared.Header.ObjectIdentity,
                projection,
                map,
                receipt,
                inlineProjectionUnavailable: inlineProjectionUnavailable,
                callerCancellationToken: callerCancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ActionHostCompletion> ReturnCommittedAsync(
        ActionHostLaunchContract launch,
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        ActionHostTrustedPolicy policy,
        BoundedReviewedSnapshotLease snapshot,
        AuthorizedAcceptedStateRestoreContext state,
        R4PublicationScopeV1 scope,
        PublicationRecoveryEvaluation evaluation,
        CancellationToken callerCancellationToken)
    {
        var observation = evaluation.Observation;
        var receipt = evaluation.ExactReadbackReceipt;
        if (observation?.MatchedAcceptance is not { } matched ||
            receipt is null)
        {
            return Failure(launch, ActionHostStatus.StateConflict);
        }

        var acceptedResult = await RestrictedStateService
            .RecoverVerifiedRetainedStateAcceptanceAsync(
                state,
                CancellationToken.None)
            .ConfigureAwait(false);
        var acceptance = acceptedResult.Value;
        if (!acceptedResult.Succeeded || acceptance is null ||
            !StringComparer.Ordinal.Equals(
                acceptance.LogicalGenerationIdentity,
                matched.LogicalGenerationIdentity) ||
            !RestrictedStateService.TryGetCurrentReviewProjection(
                state,
                acceptance,
                out var projection) ||
            projection is null)
        {
            return Failure(launch, ActionHostStatus.StateConflict);
        }
        var inlineProjectionUnavailable = !TryBuildInlineMap(
            policy,
            snapshot,
            projection,
            out var map);

        return await FinishAcceptedAsync(
                launch,
                invocation,
                policy,
                snapshot,
                state,
                scope,
                acceptance,
                matched.CandidateObjectIdentity,
                projection,
                map,
                receipt,
                evaluation,
                inlineProjectionUnavailable,
                callerCancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ActionHostCompletion> FinishAcceptedAsync(
        ActionHostLaunchContract launch,
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        ActionHostTrustedPolicy policy,
        BoundedReviewedSnapshotLease snapshot,
        AuthorizedAcceptedStateRestoreContext state,
        R4PublicationScopeV1 scope,
        VerifiedRetainedStateAcceptance acceptance,
        string candidateIdentity,
        RetainedStateCurrentReviewProjection projection,
        InlineCandidateMap? map,
        StickyCommentPublisher.StickyPublicationReceipt receipt,
        PublicationRecoveryEvaluation? existingEvaluation = null,
        bool inlineProjectionUnavailable = false,
        CancellationToken callerCancellationToken = default)
    {
        var inlineWarning = inlineProjectionUnavailable;
        if (map is { Candidates.Length: > 0 })
        {
            if (callerCancellationToken.IsCancellationRequested)
            {
                inlineWarning = true;
            }
            else
            {
                var authorization = PostAcceptanceInlineAuthorization.Mint(
                    invocation,
                    policy,
                    snapshot,
                    scope,
                    acceptance,
                    candidateIdentity,
                    receipt,
                    map);
                var request = new PostAcceptanceInlineRequest(
                    authorization,
                    map);
                if (inlineHook is null)
                {
                    inlineWarning = true;
                }
                else
                {
                    try
                    {
                        var result = await inlineHook.PublishAsync(
                                request,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        inlineWarning = result !=
                                ActionHostInlineHookResult.Complete ||
                            !authorization.WasConsumed;
                    }
                    catch (Exception exception) when (IsNonFatal(exception))
                    {
                        inlineWarning = true;
                    }
                }
            }
        }

        if (!callerCancellationToken.IsCancellationRequested)
        {
            if (existingEvaluation is not null)
            {
                _ = await PublicationRecoveryService
                    .CleanupHistoricalRecoveryRecordsAsync(
                        invocation,
                        state,
                        existingEvaluation,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                using var terminal = await new PublicationRecoveryService(
                        publisher)
                    .ClassifyBeforeProviderAsync(
                        launch.Inputs.GitHubToken!,
                        invocation,
                        scope,
                        state,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (terminal.Decision.Action ==
                    PublicationRecoveryAction.ReturnCommitted)
                {
                    _ = await PublicationRecoveryService
                        .CleanupHistoricalRecoveryRecordsAsync(
                            invocation,
                            state,
                            terminal,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }

            var cleanupPlan = await RestrictedStateService
                .PlanRetainedStateCleanupAsync(
                    state,
                    acceptance,
                    CancellationToken.None)
                .ConfigureAwait(false);
            using var cleanupAuthorization = cleanupPlan.Value;
            if (cleanupPlan.Succeeded && cleanupAuthorization is not null)
            {
                var semanticExpiry = checked(
                    timeProvider.GetUtcNow().ToUnixTimeSeconds() +
                    StateRetentionRequirements.ScopedPlatformRequestSeconds);
                _ = await RestrictedStateService.CleanupRetainedStateAsync(
                        state,
                        new RetainedStateCleanupRequest(
                            acceptance,
                            cleanupAuthorization,
                            semanticExpiry),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        return Success(
            launch,
            inlineWarning
                ? ActionHostStatus.ReviewedWithInlineWarnings
                : ActionHostStatus.Reviewed,
            receipt,
            projection.OrderedFindings.Length);
    }

    private static bool TryBuildInlineMap(
        ActionHostTrustedPolicy policy,
        BoundedReviewedSnapshotLease snapshot,
        RetainedStateCurrentReviewProjection projection,
        out InlineCandidateMap? map)
    {
        map = null;
        if (policy.PublicationMode == ActionHostPublicationMode.Sticky)
        {
            return true;
        }

        if (projection.ReviewedIdentity != snapshot.Snapshot.Identity ||
            !InlineDiffCoordinates.TryCreate(
                snapshot.Snapshot,
                snapshot.Identities,
                out var coordinates) ||
            coordinates is null)
        {
            return false;
        }

        try
        {
            map = InlineCandidateMapper.Map(
                policy,
                projection.OrderedFindings,
                coordinates);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private Task<ExactHeadRevalidationResult> RevalidateAsync(
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        ActionHostLaunchContract launch,
        CancellationToken cancellationToken) =>
        ExactHeadRevalidator.RevalidateAsync(
            invocation.PullRequest,
            launch.Inputs.GitHubToken!,
            revalidationFactory,
            cancellationToken);

    private static ActionHostStatus RevalidationStatus(
        ExactHeadRevalidationStatus status) => status switch
        {
            ExactHeadRevalidationStatus.Exact =>
                ActionHostStatus.InternalFailure,
            ExactHeadRevalidationStatus.HeadChanged or
            ExactHeadRevalidationStatus.PullRequestIneligible or
            ExactHeadRevalidationStatus.PullRequestMissing =>
                ActionHostStatus.StaleHead,
            ExactHeadRevalidationStatus.Unauthorized or
            ExactHeadRevalidationStatus.Forbidden =>
                ActionHostStatus.AuthorizationFailed,
            ExactHeadRevalidationStatus.RateLimited or
            ExactHeadRevalidationStatus.UpstreamUnavailable or
            ExactHeadRevalidationStatus.InvalidResponse or
            ExactHeadRevalidationStatus.TransportFailure or
            ExactHeadRevalidationStatus.DeadlineExceeded =>
                ActionHostStatus.SnapshotIncomplete,
            ExactHeadRevalidationStatus.Cancelled =>
                ActionHostStatus.Cancelled,
        };

    private static ActionHostStatus AgentFailureStatus(
        AgentRunOutcome outcome,
        bool stateCommittedThisRun)
    {
        var code = outcome.Diagnostic?.Code;
        if (StringComparer.Ordinal.Equals(code, AgentFailureCodes.Cancelled))
        {
            return stateCommittedThisRun
                ? ActionHostStatus.ProviderFailed
                : ActionHostStatus.Cancelled;
        }

        return code is AgentFailureCodes.ChatFailed or
            AgentFailureCodes.DeadlineExceeded or
            AgentFailureCodes.ModelLimit or
            AgentFailureCodes.TokenLimit or
            AgentFailureCodes.RequestTooLarge or
            AgentFailureCodes.ResponseTooLarge
            ? ActionHostStatus.ProviderFailed
            : ActionHostStatus.AgentResultInvalid;
    }

    private static ActionHostStatus TransactionStatus(string code) =>
        code switch
        {
            AcceptedStateCodes.AccessDenied or
            RetainedStateTransactionCodes.AccessDenied =>
                ActionHostStatus.AuthorizationFailed,
            AcceptedStateCodes.KeyUnavailable or
            RetainedStateTransactionCodes.KeyUnavailable =>
                ActionHostStatus.CredentialsMissing,
            AcceptedStateCodes.OutcomeUnknown or
            RetainedStateTransactionCodes.OutcomeUnknown =>
                ActionHostStatus.OutcomeAmbiguous,
            RetainedStateTransactionCodes.Cancelled =>
                ActionHostStatus.Cancelled,
            _ => ActionHostStatus.StateConflict,
        };

    private static ActionHostStatus PublicationPersistenceStatus(
        string code) => StringComparer.Ordinal.Equals(
            code,
            RetainedStateTransactionCodes.Invalid)
            ? ActionHostStatus.InternalFailure
            : TransactionStatus(code);

    private static ActionHostCompletion Failure(
        ActionHostLaunchContract launch,
        ActionHostStatus status)
    {
        var disposition = status == ActionHostStatus.StateConflict
            ? ActionHostStateDisposition.Conflict
            : ActionHostStateDisposition.NotCommitted;
        if (!ActionHostStepSummary.TryCreate(
                reviewedSha: null,
                publicationUrl: null,
                findingCount: null,
                disposition,
                out var summary) ||
            summary is null)
        {
            throw new InvalidOperationException();
        }

        var annotations = new List<ActionHostAnnotation>();
        if (ActionHostStatusRules.TryClassify(
                status,
                out _,
                out _,
                out var annotationCode) &&
            annotationCode is { } code &&
            ActionHostAnnotation.TryCreate(code, out var annotation) &&
            annotation is not null)
        {
            annotations.Add(annotation);
        }

        if (!ActionHostCompletion.TryCreate(
                launch.BuildDiscriminator,
                status,
                summary,
                annotations,
                out var completion) ||
            completion is null)
        {
            throw new InvalidOperationException();
        }

        return completion;
    }

    private static ActionHostCompletion Success(
        ActionHostLaunchContract launch,
        ActionHostStatus status,
        StickyCommentPublisher.StickyPublicationReceipt receipt,
        int findingCount)
    {
        if (!ActionHostStepSummary.TryCreate(
                receipt.HeadSha,
                receipt.CommentUrl,
                findingCount,
                ActionHostStateDisposition.Accepted,
                out var summary) ||
            summary is null)
        {
            throw new InvalidOperationException();
        }

        var annotations = new List<ActionHostAnnotation>();
        if (status == ActionHostStatus.ReviewedWithInlineWarnings &&
            ActionHostAnnotation.TryCreate(
                ActionHostAnnotationCode.InlinePublicationIncomplete,
                out var annotation) &&
            annotation is not null)
        {
            annotations.Add(annotation);
        }

        if (!ActionHostCompletion.TryCreate(
                launch.BuildDiscriminator,
                status,
                summary,
                annotations,
                out var completion) ||
            completion is null)
        {
            throw new InvalidOperationException();
        }

        return completion;
    }

    private static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;

    internal sealed class PostAcceptanceInlineRequest
    {
        private readonly PostAcceptanceInlineAuthorization authorization;

        internal PostAcceptanceInlineRequest(
            PostAcceptanceInlineAuthorization authorization,
            InlineCandidateMap candidateMap)
        {
            this.authorization = authorization;
            CandidateMap = candidateMap;
        }

        internal InlineCandidateMap CandidateMap { get; }

        internal bool TryConsume() => authorization.TryConsume(CandidateMap);
    }

    internal sealed class PostAcceptanceInlineAuthorization
    {
        private readonly long repositoryId;
        private readonly long pullRequestNumber;
        private readonly string reviewedHeadSha;
        private readonly long commentId;
        private readonly string scopeSha256;
        private readonly string bodySha256;
        private readonly string candidateIdentity;
        private readonly string logicalGenerationIdentity;
        private readonly string acceptanceIdentity;
        private readonly string policySha256;
        private readonly string payloadSha256;
        private readonly string buildDiscriminator;
        private readonly string diffSha256;
        private readonly string mapSha256;
        private int usable = 1;
        private int consumed;

        private PostAcceptanceInlineAuthorization(
            ActionHostAuthorizer.AuthorizedInvocation invocation,
            ActionHostTrustedPolicy policy,
            BoundedReviewedSnapshotLease snapshot,
            R4PublicationScopeV1 scope,
            VerifiedRetainedStateAcceptance acceptance,
            string candidateIdentity,
            StickyCommentPublisher.StickyPublicationReceipt receipt,
            InlineCandidateMap map)
        {
            repositoryId = invocation.PullRequest.RepositoryId;
            pullRequestNumber = invocation.PullRequest.Number;
            reviewedHeadSha = invocation.PullRequest.HeadSha;
            commentId = receipt.CommentId;
            scopeSha256 = receipt.ScopeSha256;
            bodySha256 = receipt.BodySha256;
            this.candidateIdentity = candidateIdentity;
            logicalGenerationIdentity = acceptance.LogicalGenerationIdentity;
            acceptanceIdentity = acceptance.AcceptanceReceiptIdentity;
            policySha256 = policy.PolicySha256;
            payloadSha256 = policy.PayloadSha256;
            buildDiscriminator = policy.BuildDiscriminator;
            diffSha256 = snapshot.Identities.DiffSha256;
            mapSha256 = ComputeMapIdentity(map);

            if (!StringComparer.Ordinal.Equals(
                    scopeSha256,
                    R4PublicationIdentityV1.ComputeScopeSha256(scope)))
            {
                usable = 0;
            }
        }

        internal static PostAcceptanceInlineAuthorization Mint(
            ActionHostAuthorizer.AuthorizedInvocation invocation,
            ActionHostTrustedPolicy policy,
            BoundedReviewedSnapshotLease snapshot,
            R4PublicationScopeV1 scope,
            VerifiedRetainedStateAcceptance acceptance,
            string candidateIdentity,
            StickyCommentPublisher.StickyPublicationReceipt receipt,
            InlineCandidateMap map) => new(
                invocation,
                policy,
                snapshot,
                scope,
                acceptance,
                candidateIdentity,
                receipt,
                map);

        internal bool WasConsumed => Volatile.Read(ref consumed) == 1;

        internal bool TryConsume(InlineCandidateMap map)
        {
            var matches = map is not null &&
            long.TryParse(
                map.ReviewedIdentity.RepositoryId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var mapRepositoryId) &&
            mapRepositoryId == repositoryId &&
            map.ReviewedIdentity.ReviewTarget == pullRequestNumber &&
            StringComparer.Ordinal.Equals(
                map.ReviewedIdentity.HeadSha,
                reviewedHeadSha) &&
            StringComparer.Ordinal.Equals(map.PolicySha256, policySha256) &&
            StringComparer.Ordinal.Equals(map.DiffSha256, diffSha256) &&
            StringComparer.Ordinal.Equals(
                ComputeMapIdentity(map),
                mapSha256) &&
            commentId > 0 &&
            LineageValidation.IsSha256(scopeSha256) &&
            LineageValidation.IsSha256(bodySha256) &&
            LineageValidation.IsSha256(candidateIdentity) &&
            LineageValidation.IsSha256(logicalGenerationIdentity) &&
            LineageValidation.IsSha256(acceptanceIdentity) &&
            LineageValidation.IsSha256(payloadSha256) &&
            !string.IsNullOrWhiteSpace(buildDiscriminator) &&
            Interlocked.CompareExchange(ref usable, 0, 1) == 1;
            if (matches)
            {
                Volatile.Write(ref consumed, 1);
            }

            return matches;
        }
    }

    private static string ComputeMapIdentity(InlineCandidateMap map)
    {
        var fields = new List<string>
        {
            map.ReviewedIdentity.RepositoryId,
            map.ReviewedIdentity.ReviewTarget.ToString(
                CultureInfo.InvariantCulture),
            map.ReviewedIdentity.BaseSha,
            map.ReviewedIdentity.HeadSha,
            map.PolicySha256,
            map.DiffSha256,
        };
        foreach (var candidate in map.Candidates)
        {
            fields.Add(candidate.FindingIdentity.FingerprintSha256);
            fields.Add(candidate.Path);
            fields.Add(candidate.Line.ToString(CultureInfo.InvariantCulture));
            fields.Add(candidate.InlineKey);
        }

        foreach (var stickyOnly in map.StickyOnlyFindings)
        {
            fields.Add(stickyOnly.FindingIdentity.FingerprintSha256);
            fields.Add(stickyOnly.ReasonCode);
        }

        return R4CanonicalUtf8Framing.Hash(InlineMapDomain, [.. fields]);
    }
}
