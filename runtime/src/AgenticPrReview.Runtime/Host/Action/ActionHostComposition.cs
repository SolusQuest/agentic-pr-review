using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Policy;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.ActionHost.Snapshot.ChangedFiles;
using AgenticPrReview.Runtime.ActionHost.Snapshot.GitObjects;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;

namespace AgenticPrReview.Runtime.ActionHost;

internal enum ActionHostOperationResolution
{
    ResolvedNoCommit = 1,
    ResolvedCommitted,
    Unresolved,
}

internal enum ActionHostExecutionMode
{
    Normal = 1,
    ReconciliationOnly,
}

internal sealed class ActionHostTransactionJournal
{
    private int cancellationObserved;
    private int currentRunMutationDispatched;
    private int currentRunTransactionAdvanced;
    private int executionMode = (int)ActionHostExecutionMode.Normal;
    private int latestResolution =
        (int)ActionHostOperationResolution.ResolvedNoCommit;

    internal ActionHostTransactionJournal(
        ActionHostCancellationState cancellation)
    {
        if (cancellation == ActionHostCancellationState.Requested)
        {
            cancellationObserved = 1;
            executionMode =
                (int)ActionHostExecutionMode.ReconciliationOnly;
        }
    }

    internal bool HasCurrentRunMutation =>
        Volatile.Read(ref currentRunMutationDispatched) != 0;

    internal bool HasCurrentRunActivity => HasCurrentRunMutation ||
        Volatile.Read(ref currentRunTransactionAdvanced) != 0;

    internal ActionHostOperationResolution LatestResolution =>
        (ActionHostOperationResolution)Volatile.Read(ref latestResolution);

    internal ActionHostExecutionMode ExecutionMode =>
        (ActionHostExecutionMode)Volatile.Read(ref executionMode);

    internal bool ObserveCancellation(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Exchange(ref cancellationObserved, 1);
            Interlocked.Exchange(
                ref executionMode,
                (int)ActionHostExecutionMode.ReconciliationOnly);
        }

        return Volatile.Read(ref cancellationObserved) != 0;
    }

    internal void BeforeMutationDispatch(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            _ = ObserveCancellation(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        Interlocked.Exchange(ref currentRunMutationDispatched, 1);
        Interlocked.Exchange(
            ref latestResolution,
            (int)ActionHostOperationResolution.Unresolved);
    }

    internal void Resolve(OpaqueStoreMutationState state) => Resolve(
        state switch
        {
            OpaqueStoreMutationState.NotCommitted =>
                ActionHostOperationResolution.ResolvedNoCommit,
            OpaqueStoreMutationState.Committed =>
                ActionHostOperationResolution.ResolvedCommitted,
            _ => ActionHostOperationResolution.Unresolved,
        });

    internal void Resolve(BoundedGitHubHttpOutcome outcome) => Resolve(
        outcome switch
        {
            BoundedGitHubHttpOutcome.Success =>
                ActionHostOperationResolution.ResolvedCommitted,
            BoundedGitHubHttpOutcome.CancelledBeforeSend or
            BoundedGitHubHttpOutcome.KnownNotSent or
            BoundedGitHubHttpOutcome.AuthorizationOrValidationFailure =>
                ActionHostOperationResolution.ResolvedNoCommit,
            _ => ActionHostOperationResolution.Unresolved,
        });

    internal void RecordTransactionAdvance() =>
        Interlocked.Exchange(ref currentRunTransactionAdvanced, 1);

    internal ActionHostStatus CancellationStatus => !HasCurrentRunActivity
        ? ActionHostStatus.Cancelled
        : LatestResolution == ActionHostOperationResolution.Unresolved
            ? ActionHostStatus.OutcomeAmbiguous
            : ActionHostStatus.ProviderFailed;

    private void Resolve(ActionHostOperationResolution resolution) =>
        Interlocked.Exchange(ref latestResolution, (int)resolution);
}

internal sealed class JournaledAcceptedStateProductionDependencies(
    IAcceptedStateProductionDependencies inner,
    ActionHostTransactionJournal journal) :
    IAcceptedStateProductionDependencies
{
    public IRestrictedStateStore CreateArtifactStore(
        ActionHostLaunchContract launch) => new JournaledRestrictedStateStore(
            inner.CreateArtifactStore(launch),
            journal);

    public IActionHostGitObjectTransport CreateAncestryTransport(
        ActionHostGitHubToken token) =>
        inner.CreateAncestryTransport(token);
}

internal sealed class JournaledRestrictedStateStore(
    IRestrictedStateStore inner,
    ActionHostTransactionJournal journal) : IRestrictedStateStore
{
    public Task<OpaqueStoreListResult> ListExactAsync(
        OpaqueStoreListRequest request,
        CancellationToken cancellationToken) =>
        inner.ListExactAsync(request, cancellationToken);

    public Task<OpaqueStoreMetadataResult> ReadMetadataAsync(
        OpaqueStoreMetadataRequest request,
        CancellationToken cancellationToken) =>
        inner.ReadMetadataAsync(request, cancellationToken);

    public Task<OpaqueStoreDownloadResult> DownloadAsync(
        OpaqueStoreDownloadRequest request,
        CancellationToken cancellationToken) =>
        inner.DownloadAsync(request, cancellationToken);

    public async Task<OpaqueStoreUploadResult> UploadImmutableAsync(
        OpaqueStoreUploadRequest request,
        CancellationToken cancellationToken)
    {
        journal.BeforeMutationDispatch(cancellationToken);
        try
        {
            var result = await inner.UploadImmutableAsync(
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
            journal.Resolve(result.MutationState);
            return result;
        }
        catch
        {
            journal.Resolve(OpaqueStoreMutationState.OutcomeUnknown);
            throw;
        }
    }

    public Task<OpaqueStoreReadBackResult> ReadBackExactAsync(
        OpaqueStoreReadBackRequest request,
        CancellationToken cancellationToken) =>
        inner.ReadBackExactAsync(request, cancellationToken);

    public async Task<OpaqueStoreDeleteResult> DeleteExactAsync(
        OpaqueStoreDeleteRequest request,
        CancellationToken cancellationToken)
    {
        journal.BeforeMutationDispatch(cancellationToken);
        try
        {
            var result = await inner.DeleteExactAsync(
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
            journal.Resolve(result.MutationState);
            return result;
        }
        catch
        {
            journal.Resolve(OpaqueStoreMutationState.OutcomeUnknown);
            throw;
        }
    }
}

internal sealed class JournaledStickyPublisherTransportFactory(
    IStickyGitHubPublisherTransportFactory inner,
    ActionHostTransactionJournal journal) :
    IStickyGitHubPublisherTransportFactory
{
    public IStickyGitHubPublisherTransport Create(
        ActionHostGitHubToken token,
        AuthorizedStickyPublicationRequest request) =>
        new JournaledStickyPublisherTransport(
            inner.Create(token, request),
            journal);

    public IStickyGitHubReadbackTransport CreateReadback(
        ActionHostGitHubToken token,
        AuthorizedStickyReadbackRequest request) =>
        inner.CreateReadback(token, request);
}

internal sealed class JournaledStickyPublisherTransport(
    IStickyGitHubPublisherTransport inner,
    ActionHostTransactionJournal journal) :
    IStickyGitHubPublisherTransport
{
    public bool IsWithinOverallDeadline => inner.IsWithinOverallDeadline;

    public Task<BoundedGitHubHttpResult<BoundedGitHubIssueCommentPage>>
        ListIssueCommentsAsync(
            int page,
            CancellationToken cancellationToken) =>
        inner.ListIssueCommentsAsync(page, cancellationToken);

    public Task<BoundedGitHubHttpResult<BoundedGitHubIssueComment>>
        GetIssueCommentAsync(
            long commentId,
            CancellationToken cancellationToken) =>
        inner.GetIssueCommentAsync(commentId, cancellationToken);

    public async Task<BoundedGitHubHttpResult<BoundedGitHubIssueComment>>
        MutateStickyCommentAsync(CancellationToken cancellationToken)
    {
        journal.BeforeMutationDispatch(cancellationToken);
        try
        {
            var result = await inner.MutateStickyCommentAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            journal.Resolve(result.Outcome);
            return result;
        }
        catch
        {
            journal.Resolve(BoundedGitHubHttpOutcome.OutcomeUnknown);
            throw;
        }
    }

    public void Dispose() => inner.Dispose();
}

internal sealed class ActionHostCompositionDependencies
{
    internal ActionHostCompositionDependencies(
        IActionHostEventReader eventReader,
        IActionHostGitHubAuthorizationTransportFactory authorizationFactory,
        IActionHostGitObjectTransportFactory gitObjectFactory,
        IActionHostReviewedSnapshotTransportFactory snapshotFactory,
        IAcceptedStateProductionDependencies stateDependencies,
        IStickyGitHubPublisherTransportFactory publisherFactory,
        IActionHostProviderRunnerFactory providerFactory,
        TimeProvider timeProvider,
        Func<string>? stagingParentFactory = null,
        IActionHostPostAcceptanceInlineHook? inlineHook = null)
    {
        EventReader = eventReader ??
            throw new ArgumentNullException(nameof(eventReader));
        AuthorizationFactory = authorizationFactory ??
            throw new ArgumentNullException(nameof(authorizationFactory));
        GitObjectFactory = gitObjectFactory ??
            throw new ArgumentNullException(nameof(gitObjectFactory));
        SnapshotFactory = snapshotFactory ??
            throw new ArgumentNullException(nameof(snapshotFactory));
        StateDependencies = stateDependencies ??
            throw new ArgumentNullException(nameof(stateDependencies));
        PublisherFactory = publisherFactory ??
            throw new ArgumentNullException(nameof(publisherFactory));
        ProviderFactory = providerFactory ??
            throw new ArgumentNullException(nameof(providerFactory));
        TimeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));
        StagingParentFactory = stagingParentFactory ?? CreateStagingParent;
        InlineHook = inlineHook;
    }

    internal IActionHostEventReader EventReader { get; }
    internal IActionHostGitHubAuthorizationTransportFactory
        AuthorizationFactory { get; }
    internal IActionHostGitObjectTransportFactory GitObjectFactory { get; }
    internal IActionHostReviewedSnapshotTransportFactory SnapshotFactory
    {
        get;
    }
    internal IAcceptedStateProductionDependencies StateDependencies { get; }
    internal IStickyGitHubPublisherTransportFactory PublisherFactory { get; }
    internal IActionHostProviderRunnerFactory ProviderFactory { get; }
    internal TimeProvider TimeProvider { get; }
    internal Func<string> StagingParentFactory { get; }
    internal IActionHostPostAcceptanceInlineHook? InlineHook { get; }

    internal static ActionHostCompositionDependencies Production()
    {
        var github = new ActionHostGitHubAuthorizationTransportFactory();
        return new ActionHostCompositionDependencies(
            new ActionHostExactPathEventReader(),
            github,
            github,
            github,
            new AcceptedStateProductionDependencies(),
            new BoundedGitHubPublisherTransportFactory(),
            new ActionHostDeepSeekProviderRunnerFactory(),
            TimeProvider.System);
    }

    private static string CreateStagingParent() => Path.Join(
        Path.GetTempPath(),
        "agentic-pr-review-r4",
        Guid.NewGuid().ToString("N"));
}

internal sealed class ActionHostComposition
{
    private readonly ActionHostCompositionDependencies dependencies;

    internal ActionHostComposition()
        : this(ActionHostCompositionDependencies.Production())
    {
    }

    internal ActionHostComposition(
        ActionHostCompositionDependencies dependencies) =>
        this.dependencies = dependencies ??
            throw new ArgumentNullException(nameof(dependencies));

    internal async Task<ActionHostCompletion> RunAsync(
        ActionHostLaunchContract launch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var journal = new ActionHostTransactionJournal(launch.Cancellation);
        if (journal.ObserveCancellation(cancellationToken))
        {
            return Completion(
                launch,
                journal.CancellationStatus,
                StateWasAccessed: false);
        }

        ActionHostAuthorizationResult authorization;
        try
        {
            authorization = await new ActionHostAuthorizer(
                    dependencies.EventReader,
                    dependencies.AuthorizationFactory,
                    ActionHostAuthorizationPolicy.TrustedProof)
                .AuthorizeAsync(launch, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ = journal.ObserveCancellation(cancellationToken);
            return Completion(
                launch,
                journal.CancellationStatus,
                StateWasAccessed: false);
        }

        if (authorization.Invocation is not { } invocation)
        {
            return Completion(
                launch,
                authorization.RejectionStatus ??
                    ActionHostStatus.InternalFailure,
                StateWasAccessed: false);
        }

        if (journal.ObserveCancellation(cancellationToken))
        {
            return Completion(
                launch,
                journal.CancellationStatus,
                StateWasAccessed: false);
        }

        if (!ActionHostTrustedPolicyRequest.TryBind(
                launch,
                invocation,
                out var policyRequest,
                out var bindingFailure) ||
            policyRequest is null)
        {
            return Completion(
                launch,
                PolicyStatus(bindingFailure),
                StateWasAccessed: false);
        }

        ActionHostTrustedPolicyMaterialization materialized;
        try
        {
            using var policyTransport = dependencies.GitObjectFactory
                .CreateExactObjectTransport(launch.Inputs.GitHubToken!);
            materialized = await ActionHostTrustedPolicy.MaterializeAsync(
                    policyRequest,
                    policyTransport,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ = journal.ObserveCancellation(cancellationToken);
            return Completion(
                launch,
                journal.CancellationStatus,
                StateWasAccessed: false);
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            return Completion(
                launch,
                ActionHostStatus.SnapshotIncomplete,
                StateWasAccessed: false);
        }

        if (!materialized.Succeeded ||
            materialized.Policy is not { } policy)
        {
            return Completion(
                launch,
                PolicyStatus(materialized.Failure),
                StateWasAccessed: false);
        }

        if (journal.ObserveCancellation(cancellationToken))
        {
            return Completion(
                launch,
                journal.CancellationStatus,
                StateWasAccessed: false);
        }

        string? stagingParent = null;
        ReviewedTreeSnapshot? tree = null;
        BoundedReviewedSnapshotLease? snapshot = null;
        try
        {
            stagingParent = dependencies.StagingParentFactory();
            Directory.CreateDirectory(stagingParent);
            var treeResult = await new ReviewedTreeReader(
                    new ReviewedGitObjectTransportFactory(
                        dependencies.GitObjectFactory),
                    dependencies.TimeProvider)
                .MaterializeAsync(
                    invocation,
                    launch.Inputs.GitHubToken!,
                    stagingParent,
                    cancellationToken)
                .ConfigureAwait(false);
            tree = treeResult.Snapshot;
            if (tree is null)
            {
                return Completion(
                    launch,
                    TreeStatus(treeResult.Failure),
                    StateWasAccessed: false);
            }

            if (journal.ObserveCancellation(cancellationToken))
            {
                return Completion(
                    launch,
                    journal.CancellationStatus,
                    StateWasAccessed: false);
            }

            var snapshotResult = await new BoundedReviewedSnapshotBuilder(
                    dependencies.SnapshotFactory)
                .BuildAsync(
                    invocation,
                    launch.Inputs.GitHubToken!,
                    tree,
                    stagingParent,
                    cancellationToken)
                .ConfigureAwait(false);
            snapshot = snapshotResult.Lease;
            if (snapshot is null)
            {
                return Completion(
                    launch,
                    SnapshotStatus(snapshotResult.Failure),
                    StateWasAccessed: false);
            }

            if (journal.ObserveCancellation(cancellationToken))
            {
                return Completion(
                    launch,
                    journal.CancellationStatus,
                    StateWasAccessed: false);
            }

            if (!ActionHostReviewContextFactory.TryCreate(
                    invocation,
                    snapshot.Identities,
                    out var reviewContext) ||
                reviewContext is null)
            {
                return Completion(
                    launch,
                    ActionHostStatus.InternalFailure,
                    StateWasAccessed: false);
            }

            if (journal.ObserveCancellation(cancellationToken))
            {
                return Completion(
                    launch,
                    journal.CancellationStatus,
                    StateWasAccessed: false);
            }

            if (launch.Inputs.StateKey is null)
            {
                return Completion(
                    launch,
                    ActionHostStatus.CredentialsMissing,
                    StateWasAccessed: false);
            }

            var restored = await RestrictedStateService
                .RestoreAuthorizedArtifactStateAsync(
                    new ArtifactStateRestoreRequest(
                        launch,
                        invocation,
                        policy,
                        reviewContext,
                        DeepSeekReasoningContinuationCodec.Instance,
                        new JournaledAcceptedStateProductionDependencies(
                            dependencies.StateDependencies,
                            journal),
                        dependencies.TimeProvider),
                    cancellationToken)
                .ConfigureAwait(false);
            using var state = restored.Context;
            if (!restored.Succeeded || state is null)
            {
                return Completion(
                    launch,
                    journal.ObserveCancellation(cancellationToken)
                        ? journal.CancellationStatus
                        : StateStatus(restored.Code),
                    StateWasAccessed: true);
            }

            var publicationScope = new R4PublicationScopeV1(
                (ulong)launch.RepositoryId,
                (ulong)launch.RepositoryId,
                invocation.WorkflowPath,
                launch.WorkflowRef,
                (ulong)invocation.PullRequest.Number,
                policy.PolicySha256,
                AuthorizedAcceptedStateComposer.PayloadBuildIdentity(policy));
            return await new ActionHostCoordinator(
                    new StickyCommentPublisher(
                        new JournaledStickyPublisherTransportFactory(
                            dependencies.PublisherFactory,
                            journal)),
                    dependencies.SnapshotFactory,
                    dependencies.ProviderFactory,
                    journal,
                    dependencies.TimeProvider,
                    dependencies.InlineHook)
                .RunAsync(
                    launch,
                    invocation,
                    policy,
                    snapshot,
                    state,
                    publicationScope,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Completion(
                launch,
                journal.CancellationStatus,
                StateWasAccessed: journal.HasCurrentRunActivity);
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            return Completion(
                launch,
                journal.HasCurrentRunActivity &&
                    journal.LatestResolution ==
                        ActionHostOperationResolution.Unresolved
                    ? ActionHostStatus.OutcomeAmbiguous
                    : ActionHostStatus.InternalFailure,
                StateWasAccessed: journal.HasCurrentRunActivity);
        }
        finally
        {
            if (snapshot is not null)
            {
                await snapshot.DisposeAsync().ConfigureAwait(false);
            }

            if (tree is not null)
            {
                await tree.DisposeAsync().ConfigureAwait(false);
            }

            try
            {
                if (stagingParent is not null &&
                    Directory.Exists(stagingParent))
                {
                    Directory.Delete(stagingParent, recursive: true);
                }
            }
            catch (Exception exception) when (IsNonFatal(exception))
            {
                // The bounded leases expose cleanup debt; H1 must remain closed.
            }
        }
    }

    private static ActionHostStatus PolicyStatus(
        ActionHostTrustedPolicyFailure failure) => failure switch
        {
            ActionHostTrustedPolicyFailure.None or
            ActionHostTrustedPolicyFailure.InternalInvariant =>
                ActionHostStatus.InternalFailure,
            ActionHostTrustedPolicyFailure.InvalidConfigPath or
            ActionHostTrustedPolicyFailure.InvalidInstructionsPath or
            ActionHostTrustedPolicyFailure.SourceMissing or
            ActionHostTrustedPolicyFailure.SourceNonRegular or
            ActionHostTrustedPolicyFailure.ConfigTooLarge or
            ActionHostTrustedPolicyFailure.InstructionsTooLarge or
            ActionHostTrustedPolicyFailure.MalformedConfig or
            ActionHostTrustedPolicyFailure.MalformedInstructions =>
                ActionHostStatus.ConfigurationInvalid,
            ActionHostTrustedPolicyFailure.AuthorityMismatch or
            ActionHostTrustedPolicyFailure.CredentialDenied =>
                ActionHostStatus.AuthorizationFailed,
            ActionHostTrustedPolicyFailure.SourceIncomplete or
            ActionHostTrustedPolicyFailure.SourceIdentityMismatch or
            ActionHostTrustedPolicyFailure.TransportFailure or
            ActionHostTrustedPolicyFailure.RequestLimit or
            ActionHostTrustedPolicyFailure.AggregateLimit or
            ActionHostTrustedPolicyFailure.Deadline =>
                ActionHostStatus.SnapshotIncomplete,
            ActionHostTrustedPolicyFailure.Cancelled =>
                ActionHostStatus.Cancelled,
            _ => ActionHostStatus.InternalFailure,
        };

    private static ActionHostStatus TreeStatus(ReviewedTreeFailure failure) =>
        failure switch
        {
            ReviewedTreeFailure.Cancelled => ActionHostStatus.Cancelled,
            ReviewedTreeFailure.InternalFailure =>
                ActionHostStatus.InternalFailure,
            ReviewedTreeFailure.None or
            ReviewedTreeFailure.UnsupportedSize or
            ReviewedTreeFailure.InvalidGraph or
            ReviewedTreeFailure.GitHubUnavailable or
            ReviewedTreeFailure.MissingObject or
            ReviewedTreeFailure.IdentityMismatch =>
                ActionHostStatus.SnapshotIncomplete,
            _ => ActionHostStatus.InternalFailure,
        };

    private static ActionHostStatus SnapshotStatus(
        ReviewedSnapshotReadFailure failure) => failure switch
        {
            ReviewedSnapshotReadFailure.Cancelled =>
                ActionHostStatus.Cancelled,
            ReviewedSnapshotReadFailure.Unauthorized or
            ReviewedSnapshotReadFailure.Forbidden =>
                ActionHostStatus.AuthorizationFailed,
            ReviewedSnapshotReadFailure.None or
            ReviewedSnapshotReadFailure.InvalidRequest or
            ReviewedSnapshotReadFailure.UnsupportedSize or
            ReviewedSnapshotReadFailure.NotFound or
            ReviewedSnapshotReadFailure.RateLimited or
            ReviewedSnapshotReadFailure.UpstreamUnavailable or
            ReviewedSnapshotReadFailure.InvalidResponse or
            ReviewedSnapshotReadFailure.IdentityMismatch or
            ReviewedSnapshotReadFailure.TransportFailure or
            ReviewedSnapshotReadFailure.StagingFailure =>
                ActionHostStatus.SnapshotIncomplete,
            _ => ActionHostStatus.InternalFailure,
        };

    private static ActionHostStatus StateStatus(string code) => code switch
    {
        AcceptedStateCodes.KeyUnavailable =>
            ActionHostStatus.CredentialsMissing,
        AcceptedStateCodes.AccessDenied =>
            ActionHostStatus.AuthorizationFailed,
        AcceptedStateCodes.OutcomeUnknown =>
            ActionHostStatus.OutcomeAmbiguous,
        AcceptedStateCodes.NonCurrent or
        AcceptedStateCodes.IncompatibleCurrent or
        AcceptedStateCodes.AuthenticationFailed or
        AcceptedStateCodes.ScopeMismatch or
        AcceptedStateCodes.AncestryFailed or
        AcceptedStateCodes.Overflow or
        AcceptedStateCodes.Conflict => ActionHostStatus.StateConflict,
        AcceptedStateCodes.Absent or
        AcceptedStateCodes.Expired => ActionHostStatus.InternalFailure,
        _ => ActionHostStatus.InternalFailure,
    };

    private static ActionHostCompletion Completion(
        ActionHostLaunchContract launch,
        ActionHostStatus status,
        bool StateWasAccessed)
    {
        var skipped = status is ActionHostStatus.SkippedUntrustedEvent or
            ActionHostStatus.SkippedFork or
            ActionHostStatus.SkippedDraft or
            ActionHostStatus.SkippedClosed;
        var disposition = skipped
            ? ActionHostStateDisposition.NotAccessed
            : status == ActionHostStatus.StateConflict
                ? ActionHostStateDisposition.Conflict
                : ActionHostStateDisposition.NotCommitted;
        _ = StateWasAccessed;
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
                out var expected) &&
            expected is { } code &&
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

    private static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;
}
