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
    private readonly Func<DeepSeekCredential, DeepSeekTransport>?
        transportFactory;

    internal ActionHostDeepSeekProviderRunnerFactory()
    {
    }

    internal ActionHostDeepSeekProviderRunnerFactory(
        Func<DeepSeekCredential, DeepSeekTransport> transportFactory) =>
        this.transportFactory = transportFactory ??
            throw new ArgumentNullException(nameof(transportFactory));

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
        var transport = transportFactory is null
            ? DeepSeekTransport.Create(credential)
            : transportFactory(credential);
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
    private static readonly object PostAcceptanceIssuer = new();

    private readonly StickyCommentPublisher publisher;
    private readonly IActionHostReviewedSnapshotTransportFactory
        revalidationFactory;
    private readonly IActionHostProviderRunnerFactory providerFactory;
    private readonly ActionHostTransactionJournal journal;
    private readonly IActionHostPostAcceptanceInlineHook? inlineHook;
    private readonly TimeProvider timeProvider;

    internal ActionHostCoordinator(
        StickyCommentPublisher publisher,
        IActionHostReviewedSnapshotTransportFactory revalidationFactory,
        IActionHostProviderRunnerFactory providerFactory,
        ActionHostTransactionJournal journal,
        TimeProvider timeProvider,
        IActionHostPostAcceptanceInlineHook? inlineHook = null)
    {
        this.publisher = publisher ??
            throw new ArgumentNullException(nameof(publisher));
        this.revalidationFactory = revalidationFactory ??
            throw new ArgumentNullException(nameof(revalidationFactory));
        this.providerFactory = providerFactory ??
            throw new ArgumentNullException(nameof(providerFactory));
        this.journal = journal ??
            throw new ArgumentNullException(nameof(journal));
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
        var initialResultCommittedThisRun = false;
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
                    if (journal.ObserveCancellation(cancellationToken))
                    {
                        return Failure(launch, journal.CancellationStatus);
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
                    if (!journal.TryBeginBusinessOperation(
                            ActionHostOperationKind.Provider,
                            cancellationToken,
                            out var providerOperation))
                    {
                        return Failure(launch, journal.CancellationStatus);
                    }

                    try
                    {
                        using (providerOperation!)
                        using (var runner = providerFactory.Create(
                                   new ActionHostProviderPolicy(
                                       policy.ProviderId,
                                       policy.ModelId,
                                       policy.AdapterId),
                                   launch.Inputs.ProviderApiKey,
                                   snapshot.Snapshot,
                                   timeProvider))
                        {
                            outcome = await runner.RunAsync(
                                    run,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                    catch (Exception exception) when (IsNonFatal(exception))
                    {
                        return Failure(
                            launch,
                            ActionHostStatus.ProviderFailed);
                    }

                    if (journal.ObserveCancellation(cancellationToken) &&
                        outcome.CompletedSessionEligible)
                    {
                        return Failure(launch, journal.CancellationStatus);
                    }

                    if (!outcome.CompletedSessionEligible)
                    {
                        return Failure(
                            launch,
                            AgentFailureStatus(outcome));
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

                    if (!journal.TryBeginBusinessOperation(
                            ActionHostOperationKind.HeadRevalidation,
                            cancellationToken,
                            out var revalidationOperation))
                    {
                        return Failure(launch, journal.CancellationStatus);
                    }

                    ExactHeadRevalidationResult exact;
                    using (revalidationOperation!)
                    {
                        exact = await RevalidateAsync(
                                invocation,
                                launch,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    if (!exact.MayMutate)
                    {
                        return Failure(
                            launch,
                            RevalidationStatus(exact.Status));
                    }

                    if (!journal.TryBeginBusinessOperation(
                            ActionHostOperationKind.CandidatePreparation,
                            cancellationToken,
                            out var candidatePreparationOperation))
                    {
                        return Failure(launch, journal.CancellationStatus);
                    }

                    RetainedStateTransactionResult<RetainedStatePreparedCandidate>
                        preparedResult;
                    using (candidatePreparationOperation!)
                    {
                        preparedResult = await RestrictedStateService
                            .PrepareRetainedCandidateAsync(
                                state,
                                run,
                                publication,
                                cancellationToken)
                            .ConfigureAwait(false);
                        candidatePreparationOperation!.Resolve(
                            StateOwnerResolution(preparedResult.Code));
                    }
                    using var prepared = preparedResult.Value;
                    if (!preparedResult.Succeeded || prepared is null)
                    {
                        return Failure(
                            launch,
                            CandidatePreparationStatus(preparedResult.Code));
                    }

                    if (!journal.TryBeginBusinessOperation(
                            ActionHostOperationKind.CandidatePersistence,
                            cancellationToken,
                            out var candidatePersistenceOperation))
                    {
                        return Failure(launch, journal.CancellationStatus);
                    }

                    RetainedStateTransactionResult<RetainedStatePersistedCandidate>
                        persisted;
                    using (candidatePersistenceOperation!)
                    {
                        persisted = await RestrictedStateService
                            .PersistRetainedCandidateAsync(
                                state,
                                prepared,
                                cancellationToken)
                            .ConfigureAwait(false);
                        candidatePersistenceOperation!.Resolve(
                            StateOwnerResolution(persisted.Code));
                    }
                    if (!persisted.Succeeded || persisted.Value is null)
                    {
                        return Failure(
                            launch,
                            CandidatePersistenceStatus(persisted.Code));
                    }

                    candidateCommittedThisRun = true;
                    journal.RecordTransactionAdvance();
                    break;
                }

                case PublicationRecoveryAction.CleanupSupersededRecovery:
                {
                    var cancellingBeforeIntent =
                        journal.ObserveCancellation(cancellationToken);
                    if (cancellingBeforeIntent &&
                        !candidateCommittedThisRun)
                    {
                        return Failure(launch, journal.CancellationStatus);
                    }

                    if (!journal.TryBeginBusinessOperation(
                            ActionHostOperationKind.Cleanup,
                            cancellationToken,
                            out var cleanupOperation,
                            allowReconciliation: cancellingBeforeIntent &&
                                candidateCommittedThisRun))
                    {
                        return Failure(launch, journal.CancellationStatus);
                    }

                    using var admittedCleanup = cleanupOperation!;
                    var cleanup = await PublicationRecoveryService
                        .CleanupHistoricalRecoveryRecordsAsync(
                            invocation,
                            state,
                            evaluation,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    admittedCleanup.Resolve(StateOwnerResolution(cleanup.Code));
                    if (!cleanup.Completed)
                    {
                        return Failure(
                            launch,
                            CleanupStatus(cleanup.Code));
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

                    var cancellingBeforeIntent =
                        journal.ObserveCancellation(cancellationToken);
                    if (cancellingBeforeIntent &&
                        !candidateCommittedThisRun)
                    {
                        return Failure(launch, journal.CancellationStatus);
                    }

                    if (!journal.TryBeginBusinessOperation(
                            ActionHostOperationKind.Recovery,
                            cancellationToken,
                            out var intentOperation,
                            allowReconciliation: cancellingBeforeIntent &&
                                candidateCommittedThisRun))
                    {
                        return Failure(launch, journal.CancellationStatus);
                    }

                    using var admittedIntent = intentOperation!;
                    var intentResult = await PublicationRecoveryPersistence
                        .PersistIntentAndAuthorizeAsync(
                            state,
                            observation,
                            cancellingBeforeIntent
                                ? CancellationToken.None
                                : cancellationToken)
                        .ConfigureAwait(false);
                    admittedIntent.Resolve(
                        StateOwnerResolution(intentResult.Code));
                    using var intent = intentResult.Value;
                    if (!intentResult.Succeeded || intent is null)
                    {
                        return Failure(
                            launch,
                            P5WriteStatus(intentResult.Code));
                    }

                    journal.RecordTransactionAdvance();

                    if (journal.ObserveCancellation(cancellationToken))
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
                                P5WriteStatus(
                                    failureResult.Code));
                        }

                        journal.RecordTransactionAdvance();

                        break;
                    }

                    var sticky = await PublishAsync(
                            launch,
                            invocation,
                            scope,
                            state,
                            intent.Observation,
                            intent.StickyWriteAuthorization,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (sticky.StateCode is { } stateCode)
                    {
                        return Failure(
                            launch,
                            OwnershipStatus(stateCode));
                    }

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
                                    P5WriteStatus(
                                        failureResult.Code));
                            }

                            journal.RecordTransactionAdvance();

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
                            P5WriteStatus(persistence.Code));
                    }

                    journal.RecordTransactionAdvance();
                    initialResultCommittedThisRun = sticky.Result?.Outcome ==
                        BoundedGitHubPublisherOutcome.KnownNotWritten;

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

                    var cancellingBeforeRetry =
                        journal.ObserveCancellation(cancellationToken);
                    if (cancellingBeforeRetry &&
                        !candidateCommittedThisRun &&
                        !initialResultCommittedThisRun)
                    {
                        return Failure(launch, journal.CancellationStatus);
                    }

                    if (journal.ObserveCancellation(cancellationToken))
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

                    if (!journal.TryBeginBusinessOperation(
                            ActionHostOperationKind.Recovery,
                            cancellationToken,
                            out var retryIntentOperation))
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

                    using var admittedRetryIntent = retryIntentOperation!;
                    var retryResult = await PublicationRecoveryPersistence
                        .PersistRetryIntentAndAuthorizeAsync(
                            state,
                            observation,
                            evaluation.RetryTransitionAuthorization,
                            cancellingBeforeRetry
                                ? CancellationToken.None
                                : cancellationToken)
                        .ConfigureAwait(false);
                    admittedRetryIntent.Resolve(
                        StateOwnerResolution(retryResult.Code));
                    using var retry = retryResult.Value;
                    if (!retryResult.Succeeded || retry is null)
                    {
                        return Failure(
                            launch,
                            P5WriteStatus(retryResult.Code));
                    }

                    journal.RecordTransactionAdvance();

                    if (journal.ObserveCancellation(cancellationToken))
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
                                P5WriteStatus(
                                    failureResult.Code));
                        }

                        journal.RecordTransactionAdvance();

                        break;
                    }

                    var sticky = await PublishAsync(
                            launch,
                            invocation,
                            scope,
                            state,
                            retry.Observation,
                            retry.StickyWriteAuthorization,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (sticky.StateCode is { } stateCode)
                    {
                        return Failure(
                            launch,
                            OwnershipStatus(stateCode));
                    }

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
                                    P5WriteStatus(
                                        failureResult.Code));
                            }

                            journal.RecordTransactionAdvance();

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
                            P5WriteStatus(persistence.Code));
                    }

                    journal.RecordTransactionAdvance();

                    break;
                }

                case PublicationRecoveryAction.KnownNotWrittenTerminal:
                case PublicationRecoveryAction.CancelledBeforeSend:
                    return Failure(
                        launch,
                        ActionHostStatus.StickyPublicationFailed);

                case PublicationRecoveryAction.CompleteAcceptance:
                {
                    if (observation is null ||
                        evaluation.ExactReadbackReceipt is null)
                    {
                        return Failure(
                            launch,
                            ActionHostStatus.StateConflict);
                    }

                    if (observation.StickyReadback is null)
                    {
                        var activeAttempt = observation.RetryIntent?
                                .RecordIdentity ??
                            observation.Intent?.RecordIdentity;
                        if (activeAttempt is null)
                        {
                            return Failure(
                                launch,
                                ActionHostStatus.StateConflict);
                        }

                        var persistence = await PersistResultAsync(
                                state,
                                observation,
                                activeAttempt,
                                observation.RetryIntent,
                                evaluation.ExactReadbackReceipt,
                                BoundedGitHubPublisherOutcome
                                    .WrittenAndReadBack,
                                StickyPublicationReason.None)
                            .ConfigureAwait(false);
                        using var persisted = persistence.Record;
                        if (persisted is null)
                        {
                            return Failure(
                                launch,
                                P5WriteStatus(persistence.Code));
                        }

                        journal.RecordTransactionAdvance();

                        break;
                    }

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
                }

                case PublicationRecoveryAction.StickyOutcomeUnknown:
                    return Failure(
                        launch,
                        ActionHostStatus.OutcomeAmbiguous);

                case PublicationRecoveryAction.AbandonStaleCandidate:
                case PublicationRecoveryAction.ResumeStaleCleanup:
                {
                    if (evaluation.Decision.Action ==
                            PublicationRecoveryAction.AbandonStaleCandidate &&
                        journal.ObserveCancellation(cancellationToken) &&
                        !candidateCommittedThisRun)
                    {
                        return Failure(launch, journal.CancellationStatus);
                    }

                    if (!journal.TryBeginBusinessOperation(
                            ActionHostOperationKind.Cleanup,
                            cancellationToken,
                            out var staleCleanupOperation,
                            allowReconciliation:
                                evaluation.Decision.Action ==
                                    PublicationRecoveryAction
                                        .ResumeStaleCleanup ||
                                candidateCommittedThisRun))
                    {
                        return Failure(launch, journal.CancellationStatus);
                    }

                    using var admittedStaleCleanup = staleCleanupOperation!;
                    var cleanup = await recovery
                        .AbandonAndCleanupStaleCandidateAsync(
                            launch.Inputs.GitHubToken!,
                            invocation,
                            scope,
                            state,
                            evaluation,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    admittedStaleCleanup.Resolve(
                        StateOwnerResolution(cleanup.Code));
                    if (!cleanup.Completed)
                    {
                        return Failure(
                            launch,
                            CleanupStatus(cleanup.Code));
                    }

                    break;
                }

                case PublicationRecoveryAction.ResumeAnchoredWrite:
                {
                    _ = journal.TryBeginBusinessOperation(
                        ActionHostOperationKind.Recovery,
                        CancellationToken.None,
                        out var resumeWriteOperation,
                        allowReconciliation: true);
                    using var admittedResumeWrite = resumeWriteOperation!;
                    var resumedResult = await PublicationRecoveryService
                        .ResumeInterruptedWriteAsync(
                            state,
                            evaluation,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    admittedResumeWrite.Resolve(
                        StateOwnerResolution(resumedResult.Code));
                    using var resumed = resumedResult.Value;
                    if (resumed is null)
                    {
                        return Failure(
                            launch,
                            P5WriteStatus(
                                resumedResult.Code));
                    }

                    break;
                }

                case PublicationRecoveryAction.ResumeCleanup:
                {
                    _ = journal.TryBeginBusinessOperation(
                        ActionHostOperationKind.Cleanup,
                        CancellationToken.None,
                        out var resumeCleanupOperation,
                        allowReconciliation: true);
                    using var admittedResumeCleanup = resumeCleanupOperation!;
                    var cleanup = await PublicationRecoveryService
                        .ResumeInterruptedCleanupAsync(
                            state,
                            evaluation,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    admittedResumeCleanup.Resolve(
                        StateOwnerResolution(cleanup.Code));
                    if (!cleanup.Completed)
                    {
                        return Failure(
                            launch,
                            CleanupStatus(cleanup.Code));
                    }

                    break;
                }

                case PublicationRecoveryAction
                    .AuthorizationOrValidationFailure:
                    return Failure(
                        launch,
                        DurablePublicationFailureStatus(
                            observation?.RetryFailure?.Reason ??
                            observation?.Failure?.Reason));
            }
        }

        return Failure(launch, ActionHostStatus.StateConflict);
    }

    private sealed record StickyDispatchResult(
        StickyCommentPublisher.StickyPublicationResult? Result,
        ExactHeadRevalidationStatus? RevalidationStatus,
        string? StateCode);

    private async Task<StickyDispatchResult> PublishAsync(
        ActionHostLaunchContract launch,
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        R4PublicationScopeV1 scope,
        AuthorizedAcceptedStateRestoreContext state,
        PublicationRecoveryObservation observation,
        PublicationStickyWriteAuthorization authorization,
        CancellationToken cancellationToken)
    {
        if (!journal.TryBeginBusinessOperation(
                ActionHostOperationKind.StickyPublication,
                cancellationToken,
                out var stickyOperation))
        {
            return new(
                null,
                ExactHeadRevalidationStatus.Cancelled,
                null);
        }

        using var admittedSticky = stickyOperation!;
        var exact = await RevalidateAsync(
                invocation,
                launch,
                cancellationToken)
            .ConfigureAwait(false);
        if (!exact.MayMutate)
        {
            return new(null, exact.Status, null);
        }

        var ownershipCode = await RevalidateStickyOwnershipAsync(
                state,
                observation,
                authorization)
            .ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(
                ownershipCode,
                RetainedStateTransactionCodes.Owned))
        {
            admittedSticky.Resolve(StateOwnerResolution(ownershipCode));
            return new(null, null, ownershipCode);
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
            admittedSticky.Resolve(
                ActionHostOperationResolution.ResolvedCommitted);
            return new(
                null,
                ExactHeadRevalidationStatus.InvalidResponse,
                null);
        }

        var result = await publisher.PublishAsync(
                launch.Inputs.GitHubToken!,
                request,
                cancellationToken)
            .ConfigureAwait(false);
        admittedSticky.Resolve(StickyOwnerResolution(result.Outcome));
        return new(result, null, null);
    }

    private static async Task<string> RevalidateStickyOwnershipAsync(
        AuthorizedAcceptedStateRestoreContext state,
        PublicationRecoveryObservation observation,
        PublicationStickyWriteAuthorization authorization)
    {
        var observedCandidate = observation.Candidate;
        if (observedCandidate is null ||
            !StringComparer.Ordinal.Equals(
                authorization.CandidateObjectIdentity,
                observedCandidate.Header.ObjectIdentity) ||
            !StringComparer.Ordinal.Equals(
                authorization.InventoryDigest,
                observation.InventoryDigest))
        {
            return RetainedStateTransactionCodes.Invalid;
        }

        var recoveredResult = await RestrictedStateService
            .RecoverRetainedCandidateAsync(state, CancellationToken.None)
            .ConfigureAwait(false);
        var candidate = recoveredResult.Value;
        if (!recoveredResult.Succeeded || candidate is null)
        {
            candidate?.Prepared.Dispose();
            return recoveredResult.Code;
        }

        using var prepared = candidate.Prepared;
        if (candidate.Metadata != observedCandidate.Metadata ||
            candidate.Prepared.Header != observedCandidate.Header ||
            !StringComparer.Ordinal.Equals(
                candidate.Prepared.LogicalGenerationIdentity,
                observedCandidate.LogicalGenerationIdentity))
        {
            return RetainedStateTransactionCodes.Conflict;
        }

        var ownershipResult = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                state,
                candidate,
                prior: null,
                observation.Records,
                CancellationToken.None)
            .ConfigureAwait(false);
        using var ownership = ownershipResult.Value;
        return ownershipResult.Succeeded &&
            ownership is not null &&
            StringComparer.Ordinal.Equals(
                ownership.InventoryDigest,
                authorization.InventoryDigest)
            ? RetainedStateTransactionCodes.Owned
            : ownershipResult.Succeeded
                ? RetainedStateTransactionCodes.Conflict
                : ownershipResult.Code;
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
        if (!journal.TryBeginBusinessOperation(
                ActionHostOperationKind.Recovery,
                CancellationToken.None,
                out var recoveryOperation,
                allowReconciliation: true))
        {
            return new(null, RetainedStateTransactionCodes.Cancelled);
        }

        using var admittedRecovery = recoveryOperation!;
        var recoveredResult = await RestrictedStateService
            .RecoverRetainedCandidateAsync(state, CancellationToken.None)
            .ConfigureAwait(false);
        var candidate = recoveredResult.Value;
        if (!recoveredResult.Succeeded || candidate is null)
        {
            admittedRecovery.Resolve(
                StateOwnerResolution(recoveredResult.Code));
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
            admittedRecovery.Resolve(
                StateOwnerResolution(ownershipResult.Code));
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
                admittedRecovery.Resolve(
                    ActionHostOperationResolution.ResolvedNoCommit);
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
                admittedRecovery.Resolve(
                    ActionHostOperationResolution.ResolvedNoCommit);
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
            admittedRecovery.Resolve(
                ActionHostOperationResolution.ResolvedNoCommit);
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
            admittedRecovery.Resolve(
                StateOwnerResolution(attemptResult.Code));
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
            admittedRecovery.Resolve(StateOwnerResolution(persisted.Code));
            persisted.Value?.Dispose();
            return new(null, persisted.Code);
        }

        var cleanup = await PublicationRecoveryPersistence
            .CleanupCompletedWriteAnchorAsync(
                state,
                attempt,
                CancellationToken.None)
            .ConfigureAwait(false);
        admittedRecovery.Resolve(StateOwnerResolution(
            cleanup.Completed
                ? RetainedStateTransactionCodes.Persisted
                : cleanup.Code));
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
        var stickyReadback = observation?.StickyReadback;
        if (observation is null || receipt is null || stickyReadback is null)
        {
            return Failure(launch, ActionHostStatus.StateConflict);
        }

        _ = journal.TryBeginBusinessOperation(
            ActionHostOperationKind.Acceptance,
            CancellationToken.None,
            out var acceptanceOperation,
            allowReconciliation: true);
        using var admittedAcceptance = acceptanceOperation!;

        var recoveredResult = await RestrictedStateService
            .RecoverRetainedCandidateAsync(state, CancellationToken.None)
            .ConfigureAwait(false);
        var candidate = recoveredResult.Value;
        if (!recoveredResult.Succeeded || candidate is null)
        {
            admittedAcceptance.Resolve(
                StateOwnerResolution(recoveredResult.Code));
            candidate?.Prepared.Dispose();
            return Failure(
                launch,
                CandidatePersistenceStatus(recoveredResult.Code));
        }

        using var preparedCandidate = candidate.Prepared;
        if (!RestrictedStateService.TryGetCurrentReviewProjection(
                state,
                candidate,
                out var projection) ||
            projection is null)
        {
            admittedAcceptance.Resolve(
                ActionHostOperationResolution.ResolvedNoCommit);
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
            admittedAcceptance.Resolve(
                StateOwnerResolution(ownershipResult.Code));
            return Failure(
                launch,
                OwnershipStatus(ownershipResult.Code));
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
                stickyReadback,
                out _,
                out var recoveryWrite) ||
            recoveryWrite is null)
        {
            admittedAcceptance.Resolve(
                StateOwnerResolution(preparationResult.Code));
            return Failure(
                launch,
                AcceptancePreparationStatus(preparationResult.Code));
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
            admittedAcceptance.Resolve(
                StateOwnerResolution(attemptResult.Code));
            return Failure(
                launch,
                P5WriteStatus(attemptResult.Code));
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
            admittedAcceptance.Resolve(
                StateOwnerResolution(persistedResult.Code));
            return Failure(
                launch,
                P5WriteStatus(persistedResult.Code));
        }

        using var extraction = PublicationRecoveryPersistence
            .CreateAcceptanceRecoveryExtraction(state, recoveryRecord).Value;
        if (extraction is null)
        {
            admittedAcceptance.Resolve(
                ActionHostOperationResolution.ResolvedCommitted);
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
            admittedAcceptance.Resolve(
                StateOwnerResolution(durabilityResult.Code));
            return Failure(
                launch,
                AcceptancePreparationStatus(durabilityResult.Code));
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
            admittedAcceptance.Resolve(
                StateOwnerResolution(predecessorCode));
            return Failure(
                launch,
                AcceptancePersistenceStatus(predecessorCode));
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
            admittedAcceptance.Resolve(
                StateOwnerResolution(finalOwnershipResult.Code));
            return Failure(
                launch,
                OwnershipStatus(finalOwnershipResult.Code));
        }

        var exact = await RevalidateAsync(
                invocation,
                launch,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!exact.MayMutate)
        {
            admittedAcceptance.Resolve(
                ActionHostOperationResolution.ResolvedCommitted);
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
            admittedAcceptance.Resolve(
                StateOwnerResolution(evidenceResult.Code));
            return Failure(
                launch,
                AcceptancePreparationStatus(evidenceResult.Code));
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
            admittedAcceptance.Resolve(
                StateOwnerResolution(acceptedResult.Code));
            return Failure(
                launch,
                AcceptancePersistenceStatus(acceptedResult.Code));
        }

        admittedAcceptance.Resolve(
            StateOwnerResolution(acceptedResult.Code));
        journal.RecordTransactionAdvance();

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
        var inventory = observation?.Inventory;
        if (observation is null || inventory is null || receipt is null)
        {
            return Failure(launch, ActionHostStatus.StateConflict);
        }

        var matched = observation.MatchedAcceptance;
        var candidateIdentity = matched?.CandidateObjectIdentity ??
            inventory.CurrentAcceptanceCandidateObjectIdentity;
        var expectedAcceptance = inventory.CurrentAcceptance;
        var expectedReceipt = matched?.Receipt ??
            inventory.CurrentAcceptancePublicationReceipt;
        if (candidateIdentity is null ||
            expectedAcceptance is null ||
            expectedReceipt is null ||
            !LineageValidation.IsSha256(candidateIdentity) ||
            !PublicationReceiptMatcher.AreDurablyEqual(
                expectedReceipt,
                receipt) ||
            matched is not null &&
            (!StringComparer.Ordinal.Equals(
                    matched.CandidateObjectIdentity,
                    candidateIdentity) ||
                !StringComparer.Ordinal.Equals(
                    matched.LogicalGenerationIdentity,
                    expectedAcceptance.LogicalGenerationIdentity) ||
                !StringComparer.Ordinal.Equals(
                    matched.AcceptanceReceiptIdentity,
                    expectedAcceptance.AcceptanceReceiptIdentity)))
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
                expectedAcceptance.LogicalGenerationIdentity) ||
            !StringComparer.Ordinal.Equals(
                acceptance.AcceptanceReceiptIdentity,
                expectedAcceptance.AcceptanceReceiptIdentity) ||
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
                candidateIdentity,
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
            if (!journal.TryBeginBusinessOperation(
                    ActionHostOperationKind.InlinePublication,
                    callerCancellationToken,
                    out var inlineOperation))
            {
                inlineWarning = true;
            }
            else
            {
                using var admittedInline = inlineOperation!;
                var transaction = new PostAcceptanceInlineTransactionIdentity(
                    invocation.PullRequest.RepositoryId,
                    invocation.PullRequest.Number,
                    invocation.PullRequest.HeadSha,
                    receipt.Operation,
                    receipt.RepositoryId,
                    receipt.PullRequestNumber,
                    receipt.CommentId,
                    receipt.CommentUrl,
                    receipt.ScopeSha256,
                    receipt.BodySha256,
                    receipt.HeadSha,
                    candidateIdentity,
                    acceptance.LogicalGenerationIdentity,
                    acceptance.AcceptanceReceiptIdentity,
                    policy.PolicySha256,
                    policy.PayloadSha256,
                    policy.BuildDiscriminator,
                    snapshot.Identities.DiffSha256,
                    ComputeMapIdentity(map));
                var authorization = MintPostAcceptanceInlineAuthorization(
                    invocation,
                    launch.Inputs.GitHubToken!,
                    revalidationFactory,
                    policy,
                    snapshot,
                    scope,
                    acceptance,
                    candidateIdentity,
                    receipt,
                    map);
                var request = PostAcceptanceInlineRequest.Create(
                    PostAcceptanceIssuer,
                    authorization,
                    transaction,
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
                                callerCancellationToken)
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

        if (journal.TryBeginBusinessOperation(
                ActionHostOperationKind.Cleanup,
                callerCancellationToken,
                out var cleanupOperation))
        {
            using var admittedCleanup = cleanupOperation!;
            var cleanupResolution =
                ActionHostOperationResolution.ResolvedNoCommit;
            if (existingEvaluation is not null)
            {
                var historicalCleanup = await PublicationRecoveryService
                    .CleanupHistoricalRecoveryRecordsAsync(
                        invocation,
                        state,
                        existingEvaluation,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                cleanupResolution = MergeResolution(
                    cleanupResolution,
                    StateOwnerResolution(historicalCleanup.Code));
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
                    var historicalCleanup = await PublicationRecoveryService
                        .CleanupHistoricalRecoveryRecordsAsync(
                            invocation,
                            state,
                            terminal,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    cleanupResolution = MergeResolution(
                        cleanupResolution,
                        StateOwnerResolution(historicalCleanup.Code));
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
                var cleanup = await RestrictedStateService
                    .CleanupRetainedStateAsync(
                        state,
                        new RetainedStateCleanupRequest(
                            acceptance,
                            cleanupAuthorization,
                            semanticExpiry),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                cleanupResolution = MergeResolution(
                    cleanupResolution,
                    StateOwnerResolution(cleanup.Code));
            }

            admittedCleanup.Resolve(cleanupResolution);
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

    private ActionHostStatus RevalidationStatus(
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
                journal.CancellationStatus,
            _ => ActionHostStatus.InternalFailure,
        };

    private static ActionHostOperationResolution StateOwnerResolution(
        string code) => code switch
        {
            RetainedStateTransactionCodes.OutcomeUnknown =>
                ActionHostOperationResolution.Unresolved,
            RetainedStateTransactionCodes.Ready or
            RetainedStateTransactionCodes.Prepared or
            RetainedStateTransactionCodes.Persisted or
            RetainedStateTransactionCodes.Owned or
            RetainedStateTransactionCodes.Accepted =>
                ActionHostOperationResolution.ResolvedCommitted,
            _ => ActionHostOperationResolution.ResolvedNoCommit,
        };

    private static ActionHostOperationResolution StickyOwnerResolution(
        BoundedGitHubPublisherOutcome outcome) => outcome switch
        {
            BoundedGitHubPublisherOutcome.WrittenAndReadBack =>
                ActionHostOperationResolution.ResolvedCommitted,
            BoundedGitHubPublisherOutcome.KnownNotWritten or
            BoundedGitHubPublisherOutcome.CancelledBeforeSend or
            BoundedGitHubPublisherOutcome.AuthorizationOrValidationFailure =>
                ActionHostOperationResolution.ResolvedNoCommit,
            BoundedGitHubPublisherOutcome.OutcomeUnknown =>
                ActionHostOperationResolution.Unresolved,
            _ => ActionHostOperationResolution.Unresolved,
        };

    private static ActionHostOperationResolution MergeResolution(
        ActionHostOperationResolution current,
        ActionHostOperationResolution next) =>
        current == ActionHostOperationResolution.Unresolved ||
        next == ActionHostOperationResolution.Unresolved
            ? ActionHostOperationResolution.Unresolved
            : current == ActionHostOperationResolution.ResolvedCommitted ||
                next == ActionHostOperationResolution.ResolvedCommitted
                ? ActionHostOperationResolution.ResolvedCommitted
                : ActionHostOperationResolution.ResolvedNoCommit;

    private ActionHostStatus AgentFailureStatus(AgentRunOutcome outcome)
    {
        var code = outcome.Diagnostic?.Code;
        if (StringComparer.Ordinal.Equals(code, AgentFailureCodes.Cancelled))
        {
            return journal.CancellationStatus;
        }

        return code is AgentFailureCodes.ChatFailed or
            AgentFailureCodes.DeadlineExceeded
            ? ActionHostStatus.ProviderFailed
            : ActionHostStatus.AgentResultInvalid;
    }

    private ActionHostStatus CandidatePreparationStatus(string code) =>
        code switch
        {
            RetainedStateTransactionCodes.Conflict or
            RetainedStateTransactionCodes.Stale or
            RetainedStateTransactionCodes.RetentionFailed or
            RetainedStateTransactionCodes.CleanupDebt =>
                ActionHostStatus.StateConflict,
            RetainedStateTransactionCodes.OutcomeUnknown =>
                ActionHostStatus.OutcomeAmbiguous,
            RetainedStateTransactionCodes.Cancelled =>
                journal.CancellationStatus,
            RetainedStateTransactionCodes.AccessDenied or
            RetainedStateTransactionCodes.Invalid or
            RetainedStateTransactionCodes.KeyUnavailable or
            RetainedStateTransactionCodes.Ready or
            RetainedStateTransactionCodes.Prepared or
            RetainedStateTransactionCodes.Persisted or
            RetainedStateTransactionCodes.Owned or
            RetainedStateTransactionCodes.Accepted =>
                ActionHostStatus.InternalFailure,
            _ => ActionHostStatus.InternalFailure,
        };

    private ActionHostStatus CandidatePersistenceStatus(string code) =>
        code switch
        {
            RetainedStateTransactionCodes.Conflict or
            RetainedStateTransactionCodes.Stale or
            RetainedStateTransactionCodes.RetentionFailed or
            RetainedStateTransactionCodes.CleanupDebt =>
                ActionHostStatus.StateConflict,
            RetainedStateTransactionCodes.OutcomeUnknown =>
                ActionHostStatus.OutcomeAmbiguous,
            RetainedStateTransactionCodes.Cancelled =>
                journal.CancellationStatus,
            RetainedStateTransactionCodes.AccessDenied or
            RetainedStateTransactionCodes.Invalid or
            RetainedStateTransactionCodes.KeyUnavailable or
            RetainedStateTransactionCodes.Ready or
            RetainedStateTransactionCodes.Prepared or
            RetainedStateTransactionCodes.Persisted or
            RetainedStateTransactionCodes.Owned or
            RetainedStateTransactionCodes.Accepted =>
                ActionHostStatus.InternalFailure,
            _ => ActionHostStatus.InternalFailure,
        };

    private ActionHostStatus OwnershipStatus(string code) => code switch
    {
        RetainedStateTransactionCodes.Conflict or
        RetainedStateTransactionCodes.Stale or
        RetainedStateTransactionCodes.RetentionFailed or
        RetainedStateTransactionCodes.CleanupDebt =>
            ActionHostStatus.StateConflict,
        RetainedStateTransactionCodes.OutcomeUnknown =>
            ActionHostStatus.OutcomeAmbiguous,
        RetainedStateTransactionCodes.Cancelled =>
            journal.CancellationStatus,
        RetainedStateTransactionCodes.AccessDenied or
        RetainedStateTransactionCodes.Invalid or
        RetainedStateTransactionCodes.KeyUnavailable or
        RetainedStateTransactionCodes.Ready or
        RetainedStateTransactionCodes.Prepared or
        RetainedStateTransactionCodes.Persisted or
        RetainedStateTransactionCodes.Owned or
        RetainedStateTransactionCodes.Accepted =>
            ActionHostStatus.InternalFailure,
        _ => ActionHostStatus.InternalFailure,
    };

    private ActionHostStatus P5WriteStatus(string code) => code switch
    {
        RetainedStateTransactionCodes.Conflict or
        RetainedStateTransactionCodes.Stale or
        RetainedStateTransactionCodes.RetentionFailed or
        RetainedStateTransactionCodes.CleanupDebt =>
            ActionHostStatus.StateConflict,
        RetainedStateTransactionCodes.OutcomeUnknown =>
            ActionHostStatus.OutcomeAmbiguous,
        RetainedStateTransactionCodes.Cancelled =>
            journal.CancellationStatus,
        RetainedStateTransactionCodes.AccessDenied or
        RetainedStateTransactionCodes.Invalid or
        RetainedStateTransactionCodes.KeyUnavailable or
        RetainedStateTransactionCodes.Ready or
        RetainedStateTransactionCodes.Prepared or
        RetainedStateTransactionCodes.Persisted or
        RetainedStateTransactionCodes.Owned or
        RetainedStateTransactionCodes.Accepted =>
            ActionHostStatus.InternalFailure,
        _ => ActionHostStatus.InternalFailure,
    };

    private ActionHostStatus AcceptancePreparationStatus(string code) =>
        code switch
        {
            RetainedStateTransactionCodes.Conflict or
            RetainedStateTransactionCodes.Stale or
            RetainedStateTransactionCodes.RetentionFailed or
            RetainedStateTransactionCodes.CleanupDebt =>
                ActionHostStatus.StateConflict,
            RetainedStateTransactionCodes.OutcomeUnknown =>
                ActionHostStatus.OutcomeAmbiguous,
            RetainedStateTransactionCodes.Cancelled =>
                journal.CancellationStatus,
            RetainedStateTransactionCodes.AccessDenied or
            RetainedStateTransactionCodes.Invalid or
            RetainedStateTransactionCodes.KeyUnavailable or
            RetainedStateTransactionCodes.Ready or
            RetainedStateTransactionCodes.Prepared or
            RetainedStateTransactionCodes.Persisted or
            RetainedStateTransactionCodes.Owned or
            RetainedStateTransactionCodes.Accepted =>
                ActionHostStatus.InternalFailure,
            _ => ActionHostStatus.InternalFailure,
        };

    private ActionHostStatus AcceptancePersistenceStatus(string code) =>
        code switch
        {
            RetainedStateTransactionCodes.Conflict or
            RetainedStateTransactionCodes.Stale or
            RetainedStateTransactionCodes.RetentionFailed or
            RetainedStateTransactionCodes.CleanupDebt =>
                ActionHostStatus.StateConflict,
            RetainedStateTransactionCodes.OutcomeUnknown =>
                ActionHostStatus.OutcomeAmbiguous,
            RetainedStateTransactionCodes.Cancelled =>
                journal.CancellationStatus,
            RetainedStateTransactionCodes.AccessDenied or
            RetainedStateTransactionCodes.Invalid or
            RetainedStateTransactionCodes.KeyUnavailable or
            RetainedStateTransactionCodes.Ready or
            RetainedStateTransactionCodes.Prepared or
            RetainedStateTransactionCodes.Persisted or
            RetainedStateTransactionCodes.Owned or
            RetainedStateTransactionCodes.Accepted =>
                ActionHostStatus.InternalFailure,
            _ => ActionHostStatus.InternalFailure,
        };

    private ActionHostStatus CleanupStatus(string code) => code switch
    {
        RetainedStateTransactionCodes.Conflict or
        RetainedStateTransactionCodes.Stale or
        RetainedStateTransactionCodes.RetentionFailed or
        RetainedStateTransactionCodes.CleanupDebt =>
            ActionHostStatus.StateConflict,
        RetainedStateTransactionCodes.OutcomeUnknown =>
            ActionHostStatus.OutcomeAmbiguous,
        RetainedStateTransactionCodes.Cancelled =>
            journal.CancellationStatus,
        RetainedStateTransactionCodes.AccessDenied or
        RetainedStateTransactionCodes.Invalid or
        RetainedStateTransactionCodes.KeyUnavailable or
        RetainedStateTransactionCodes.Ready or
        RetainedStateTransactionCodes.Prepared or
        RetainedStateTransactionCodes.Persisted or
        RetainedStateTransactionCodes.Owned or
        RetainedStateTransactionCodes.Accepted =>
            ActionHostStatus.InternalFailure,
        _ => ActionHostStatus.InternalFailure,
    };

    private static ActionHostStatus DurablePublicationFailureStatus(
        StickyPublicationReason? reason) => reason switch
        {
            StickyPublicationReason.AuthorizationDenied =>
                ActionHostStatus.AuthorizationFailed,
            StickyPublicationReason.AdmissionInvalid or
            StickyPublicationReason.DiscoveryIncomplete or
            StickyPublicationReason.TargetConflict =>
                ActionHostStatus.StickyPublicationFailed,
            _ => ActionHostStatus.InternalFailure,
        };

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

    private sealed record PostAcceptanceInlineTransactionIdentity(
        long RepositoryId,
        long PullRequestNumber,
        string ReviewedHeadSha,
        StickyPublicationOperation StickyOperation,
        long StickyRepositoryId,
        long StickyPullRequestNumber,
        long CommentId,
        string CommentUrl,
        string ScopeSha256,
        string BodySha256,
        string StickyHeadSha,
        string CandidateIdentity,
        string LogicalGenerationIdentity,
        string AcceptanceIdentity,
        string PolicySha256,
        string PayloadSha256,
        string BuildDiscriminator,
        string DiffSha256,
        string MapSha256);

    internal sealed class PostAcceptanceInlineRequest
    {
        private readonly PostAcceptanceInlineAuthorization authorization;
        private readonly PostAcceptanceInlineTransactionIdentity transaction;

        private PostAcceptanceInlineRequest(
            PostAcceptanceInlineAuthorization authorization,
            PostAcceptanceInlineTransactionIdentity transaction,
            InlineCandidateMap candidateMap)
        {
            this.authorization = authorization;
            this.transaction = transaction;
            CandidateMap = candidateMap;
        }

        internal static PostAcceptanceInlineRequest Create(
            object issuer,
            object authorization,
            object transaction,
            InlineCandidateMap candidateMap)
        {
            if (!ReferenceEquals(issuer, PostAcceptanceIssuer) ||
                authorization is not PostAcceptanceInlineAuthorization issued ||
                transaction is not PostAcceptanceInlineTransactionIdentity bound)
            {
                throw new InvalidOperationException();
            }

            return new(issued, bound, candidateMap);
        }

        internal InlineCandidateMap CandidateMap { get; }

        internal bool TryConsume(
            out PostAcceptanceInlineOperation? operation) =>
            authorization.TryConsume(transaction, CandidateMap, out operation);
    }

    internal sealed class PostAcceptanceInlineOperation
    {
        private PostAcceptanceInlineOperation(
            ActionHostGitHubToken token,
            ActionHostAuthorizer.AuthorizedInvocation invocation,
            IActionHostReviewedSnapshotTransportFactory revalidationFactory,
            InlineCandidateMap candidateMap)
        {
            Token = token;
            Invocation = invocation;
            RevalidationFactory = revalidationFactory;
            CandidateMap = candidateMap;
        }

        internal ActionHostGitHubToken Token { get; }

        internal ActionHostAuthorizer.AuthorizedInvocation Invocation { get; }

        internal IActionHostReviewedSnapshotTransportFactory
            RevalidationFactory { get; }

        internal InlineCandidateMap CandidateMap { get; }

        internal static PostAcceptanceInlineOperation Create(
            object issuer,
            ActionHostGitHubToken token,
            ActionHostAuthorizer.AuthorizedInvocation invocation,
            IActionHostReviewedSnapshotTransportFactory revalidationFactory,
            InlineCandidateMap candidateMap)
        {
            if (!ReferenceEquals(issuer, PostAcceptanceIssuer))
            {
                throw new InvalidOperationException();
            }

            return new(token, invocation, revalidationFactory, candidateMap);
        }
    }

    private sealed class PostAcceptanceInlineAuthorization
    {
        private readonly PostAcceptanceInlineTransactionIdentity transaction;
        private readonly ActionHostGitHubToken token;
        private readonly ActionHostAuthorizer.AuthorizedInvocation invocation;
        private readonly IActionHostReviewedSnapshotTransportFactory
            revalidationFactory;
        private int usable = 1;
        private int consumed;

        internal PostAcceptanceInlineAuthorization(
            ActionHostAuthorizer.AuthorizedInvocation invocation,
            ActionHostGitHubToken token,
            IActionHostReviewedSnapshotTransportFactory revalidationFactory,
            ActionHostTrustedPolicy policy,
            BoundedReviewedSnapshotLease snapshot,
            R4PublicationScopeV1 scope,
            VerifiedRetainedStateAcceptance acceptance,
            string candidateIdentity,
            StickyCommentPublisher.StickyPublicationReceipt receipt,
            InlineCandidateMap map)
        {
            this.invocation = invocation ??
                throw new ArgumentNullException(nameof(invocation));
            this.token = token ?? throw new ArgumentNullException(nameof(token));
            this.revalidationFactory = revalidationFactory ??
                throw new ArgumentNullException(nameof(revalidationFactory));
            transaction = new(
                invocation.PullRequest.RepositoryId,
                invocation.PullRequest.Number,
                invocation.PullRequest.HeadSha,
                receipt.Operation,
                receipt.RepositoryId,
                receipt.PullRequestNumber,
                receipt.CommentId,
                receipt.CommentUrl,
                receipt.ScopeSha256,
                receipt.BodySha256,
                receipt.HeadSha,
                candidateIdentity,
                acceptance.LogicalGenerationIdentity,
                acceptance.AcceptanceReceiptIdentity,
                policy.PolicySha256,
                policy.PayloadSha256,
                policy.BuildDiscriminator,
                snapshot.Identities.DiffSha256,
                ComputeMapIdentity(map));

            if (!StringComparer.Ordinal.Equals(
                    transaction.ScopeSha256,
                    R4PublicationIdentityV1.ComputeScopeSha256(scope)) ||
                transaction.StickyRepositoryId != transaction.RepositoryId ||
                transaction.StickyPullRequestNumber !=
                    transaction.PullRequestNumber ||
                !StringComparer.Ordinal.Equals(
                    transaction.StickyHeadSha,
                    transaction.ReviewedHeadSha))
            {
                usable = 0;
            }
        }

        internal bool WasConsumed => Volatile.Read(ref consumed) == 1;

        internal bool TryConsume(
            PostAcceptanceInlineTransactionIdentity presented,
            InlineCandidateMap map,
            out PostAcceptanceInlineOperation? operation)
        {
            operation = null;
            var matches = presented is not null &&
                presented == transaction &&
                map is not null &&
                long.TryParse(
                    map.ReviewedIdentity.RepositoryId,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var mapRepositoryId) &&
                mapRepositoryId == transaction.RepositoryId &&
                map.ReviewedIdentity.ReviewTarget ==
                    transaction.PullRequestNumber &&
                StringComparer.Ordinal.Equals(
                    map.ReviewedIdentity.HeadSha,
                    transaction.ReviewedHeadSha) &&
                StringComparer.Ordinal.Equals(
                    map.PolicySha256,
                    transaction.PolicySha256) &&
                StringComparer.Ordinal.Equals(
                    map.DiffSha256,
                    transaction.DiffSha256) &&
                StringComparer.Ordinal.Equals(
                    ComputeMapIdentity(map),
                    transaction.MapSha256) &&
                transaction.StickyOperation is
                    StickyPublicationOperation.Create or
                    StickyPublicationOperation.Update or
                    StickyPublicationOperation.Observed &&
                transaction.CommentId > 0 &&
                Uri.TryCreate(
                    transaction.CommentUrl,
                    UriKind.Absolute,
                    out _) &&
                LineageValidation.IsSha256(transaction.ScopeSha256) &&
                LineageValidation.IsSha256(transaction.BodySha256) &&
                LineageValidation.IsSha256(transaction.CandidateIdentity) &&
                LineageValidation.IsSha256(
                    transaction.LogicalGenerationIdentity) &&
                LineageValidation.IsSha256(transaction.AcceptanceIdentity) &&
                LineageValidation.IsSha256(transaction.PayloadSha256) &&
                !string.IsNullOrWhiteSpace(transaction.BuildDiscriminator) &&
                Interlocked.CompareExchange(ref usable, 0, 1) == 1;
            if (matches)
            {
                Volatile.Write(ref consumed, 1);
                operation = PostAcceptanceInlineOperation.Create(
                    PostAcceptanceIssuer,
                    token,
                    invocation,
                    revalidationFactory,
                    map!);
            }

            return matches;
        }
    }

    private static PostAcceptanceInlineAuthorization
        MintPostAcceptanceInlineAuthorization(
            ActionHostAuthorizer.AuthorizedInvocation invocation,
            ActionHostGitHubToken token,
            IActionHostReviewedSnapshotTransportFactory revalidationFactory,
            ActionHostTrustedPolicy policy,
            BoundedReviewedSnapshotLease snapshot,
            R4PublicationScopeV1 scope,
            VerifiedRetainedStateAcceptance acceptance,
            string candidateIdentity,
            StickyCommentPublisher.StickyPublicationReceipt receipt,
            InlineCandidateMap map)
        => new(
                invocation,
                token,
                revalidationFactory,
                policy,
                snapshot,
                scope,
                acceptance,
                candidateIdentity,
                receipt,
                map);

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
