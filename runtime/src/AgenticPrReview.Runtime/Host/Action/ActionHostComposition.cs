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
using AgenticPrReview.Runtime.Host.State.Restore;

namespace AgenticPrReview.Runtime.ActionHost;

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

    private static string CreateStagingParent() => Path.Combine(
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
        var authorization = await new ActionHostAuthorizer(
                dependencies.EventReader,
                dependencies.AuthorizationFactory,
                ActionHostAuthorizationPolicy.TrustedProof)
            .AuthorizeAsync(launch, cancellationToken)
            .ConfigureAwait(false);
        if (authorization.Invocation is not { } invocation)
        {
            return Completion(
                launch,
                authorization.RejectionStatus ??
                    ActionHostStatus.InternalFailure,
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

        var stagingParent = dependencies.StagingParentFactory();
        ReviewedTreeSnapshot? tree = null;
        BoundedReviewedSnapshotLease? snapshot = null;
        try
        {
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
                        dependencies.StateDependencies,
                        dependencies.TimeProvider),
                    cancellationToken)
                .ConfigureAwait(false);
            using var state = restored.Context;
            if (!restored.Succeeded || state is null)
            {
                return Completion(
                    launch,
                    StateStatus(restored.Code),
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
                        dependencies.PublisherFactory),
                    dependencies.SnapshotFactory,
                    dependencies.ProviderFactory,
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
        catch (Exception exception) when (IsNonFatal(exception))
        {
            return Completion(
                launch,
                ActionHostStatus.InternalFailure,
                StateWasAccessed: false);
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
                if (Directory.Exists(stagingParent))
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
        AcceptedStateCodes.Conflict or
        AcceptedStateCodes.Absent or
        AcceptedStateCodes.Expired => ActionHostStatus.StateConflict,
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
