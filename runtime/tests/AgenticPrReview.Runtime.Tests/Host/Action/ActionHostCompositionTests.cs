using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Policy;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Inline;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed class ActionHostCompositionTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PreObservedCancellationDoesNotStartAnyProducer(
        bool launchCancellation,
        bool callerCancellation)
    {
        var authorization = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = WithCancellation(
            FullLaunch(authorization.Launch),
            launchCancellation
                ? ActionHostCancellationState.Requested
                : ActionHostCancellationState.Active);
        var github = new FullPathGitHubFactory(
            authorization.Transport.PullRequest);
        var store = new ScriptedLocatorStore();
        var publisher = new FakePublisherTransportFactory();
        var provider = new FullPathProviderFactory();
        var stagingCalls = 0;
        var composition = new ActionHostComposition(
            new ActionHostCompositionDependencies(
                authorization.EventReader,
                authorization.Factory,
                github,
                github,
                new FullPathStateDependencies(store, github),
                publisher,
                provider,
                new FrozenLocatorTimeProvider(LocatorTestData.Now),
                () =>
                {
                    stagingCalls++;
                    throw new InvalidOperationException("must not stage");
                }));

        var completion = await composition.RunAsync(
            launch,
            new CancellationToken(callerCancellation));

        Assert.Equal(ActionHostStatus.Cancelled, completion.Status);
        Assert.Equal(ActionHostStateDisposition.NotCommitted,
            completion.Summary.StateDisposition);
        Assert.Equal(0, authorization.Factory.Calls);
        Assert.Equal(0, github.CurrentPullRequestCalls);
        Assert.Empty(store.Objects);
        Assert.Equal(0, provider.Creates);
        Assert.Equal(0, provider.Runs);
        Assert.Empty(publisher.Transport.Bodies);
        Assert.Equal(0, stagingCalls);
    }

    [Fact]
    public async Task ReconciledStateSetupCancellationClosesProviderAdmission()
    {
        var calibration = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var calibrationLaunch = FullLaunch(calibration.Launch);
        var calibrationGitHub = new FullPathGitHubFactory(
            calibration.Transport.PullRequest);
        var calibrationStore = FullPathStore(
            calibrationLaunch,
            rawUnknownFirstUpload: true);
        var listsBeforeProvider = 0;
        var calibrationProvider = new FullPathProviderFactory(
            onCreate: () =>
            {
                listsBeforeProvider = calibrationStore.ListCalls;
                throw new InvalidOperationException("calibration complete");
            });
        var calibrationStaging = StagingPath();
        var calibrationCompletion = await new ActionHostComposition(
                new ActionHostCompositionDependencies(
                    calibration.EventReader,
                    calibration.Factory,
                    calibrationGitHub,
                    calibrationGitHub,
                    new FullPathStateDependencies(
                        calibrationStore,
                        calibrationGitHub),
                    EmptyPublisher(),
                    calibrationProvider,
                    new FrozenLocatorTimeProvider(LocatorTestData.Now),
                    () => calibrationStaging))
            .RunAsync(calibrationLaunch, CancellationToken.None);

        Assert.Equal(
            ActionHostStatus.ProviderFailed,
            calibrationCompletion.Status);
        Assert.True(listsBeforeProvider > 0);
        Assert.True(calibrationStore.UploadCalls > 0);
        Assert.False(Directory.Exists(calibrationStaging));

        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = FullLaunch(scenario.Launch);
        var github = new FullPathGitHubFactory(
            scenario.Transport.PullRequest);
        var store = FullPathStore(
            launch,
            rawUnknownFirstUpload: true);
        using var cancellation = new CancellationTokenSource();
        store.BeforeList = (_, call) =>
        {
            if (call == listsBeforeProvider)
            {
                cancellation.Cancel();
            }
        };
        var provider = new FullPathProviderFactory();
        var publisher = EmptyPublisher();
        var staging = StagingPath();

        var completion = await new ActionHostComposition(
                new ActionHostCompositionDependencies(
                    scenario.EventReader,
                    scenario.Factory,
                    github,
                    github,
                    new FullPathStateDependencies(store, github),
                    publisher,
                    provider,
                    new FrozenLocatorTimeProvider(LocatorTestData.Now),
                    () => staging))
            .RunAsync(launch, cancellation.Token);

        Assert.Equal(ActionHostStatus.InternalFailure, completion.Status);
        Assert.Equal(
            ActionHostStateDisposition.NotCommitted,
            completion.Summary.StateDisposition);
        Assert.Equal(0, provider.Creates);
        Assert.Equal(0, provider.Runs);
        Assert.Empty(publisher.Transport.Bodies);
        Assert.Equal(listsBeforeProvider, store.ListCalls);
        Assert.True(store.UploadCalls > 0);
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public async Task AgentCancellationAfterResolvedStateSetupIsNotProviderFailure()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = FullLaunch(scenario.Launch);
        var github = new FullPathGitHubFactory(
            scenario.Transport.PullRequest);
        var store = FullPathStore(launch);
        var provider = new FullPathProviderFactory(cancelledOutcome: true);
        var publisher = EmptyPublisher();
        var staging = StagingPath();

        var completion = await new ActionHostComposition(
                new ActionHostCompositionDependencies(
                    scenario.EventReader,
                    scenario.Factory,
                    github,
                    github,
                    new FullPathStateDependencies(store, github),
                    publisher,
                    provider,
                    new FrozenLocatorTimeProvider(LocatorTestData.Now),
                    () => staging))
            .RunAsync(launch, CancellationToken.None);

        Assert.Equal(ActionHostStatus.InternalFailure, completion.Status);
        Assert.NotEqual(ActionHostStatus.ProviderFailed, completion.Status);
        Assert.Equal(1, provider.Creates);
        Assert.Equal(1, provider.Runs);
        Assert.Empty(publisher.Transport.Bodies);
        Assert.True(store.UploadCalls > 0);
        Assert.False(Directory.Exists(staging));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task CancellationAfterCandidateOrIntentTerminalizesThroughP5(
        int uploadOffsetAfterProvider)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = FullLaunch(scenario.Launch);
        var github = new FullPathGitHubFactory(
            scenario.Transport.PullRequest);
        var store = FullPathStore(launch);
        using var cancellation = new CancellationTokenSource();
        var setupUploads = 0;
        store.AfterUpload = (_, call) =>
        {
            if (setupUploads > 0 &&
                call == setupUploads + uploadOffsetAfterProvider)
            {
                cancellation.Cancel();
            }
        };
        var provider = new FullPathProviderFactory(
            onRunCompleted: () => setupUploads = store.UploadCalls);
        var publisher = EmptyPublisher();
        var staging = StagingPath();

        var completion = await new ActionHostComposition(
                new ActionHostCompositionDependencies(
                    scenario.EventReader,
                    scenario.Factory,
                    github,
                    github,
                    new FullPathStateDependencies(store, github),
                    publisher,
                    provider,
                    new FrozenLocatorTimeProvider(LocatorTestData.Now),
                    () => staging))
            .RunAsync(launch, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(
            ActionHostStatus.StickyPublicationFailed,
            completion.Status);
        Assert.Equal(
            ActionHostStateDisposition.NotCommitted,
            completion.Summary.StateDisposition);
        Assert.Equal(1, provider.Creates);
        Assert.Equal(1, provider.Runs);
        Assert.Empty(publisher.Transport.Bodies);
        Assert.True(store.UploadCalls > setupUploads + uploadOffsetAfterProvider);
        Assert.False(Directory.Exists(staging));
    }

    [Theory]
    [InlineData(
        (int)BoundedGitHubHttpOutcome.KnownNotSent,
        (int)BoundedGitHubPublisherReason.Deadline,
        (int)ActionHostStatus.StickyPublicationFailed,
        2)]
    [InlineData(
        (int)BoundedGitHubHttpOutcome.OutcomeUnknown,
        (int)BoundedGitHubPublisherReason.Deadline,
        (int)ActionHostStatus.OutcomeAmbiguous,
        1)]
    [InlineData(
        (int)BoundedGitHubHttpOutcome.AuthorizationOrValidationFailure,
        (int)BoundedGitHubPublisherReason.ValidationRejected,
        (int)ActionHostStatus.AuthorizationFailed,
        1)]
    public async Task P2FailureClassesConvergeThroughDurableRecovery(
        int transportOutcomeValue,
        int transportReasonValue,
        int expectedStatusValue,
        int expectedMutations)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = FullLaunch(scenario.Launch);
        var github = new FullPathGitHubFactory(
            scenario.Transport.PullRequest);
        var store = FullPathStore(launch);
        var publisher = new FakePublisherTransportFactory();
        for (var index = 0; index < 256; index++)
        {
            publisher.Transport.Enqueue();
        }
        var transportOutcome = (BoundedGitHubHttpOutcome)
            transportOutcomeValue;
        var transportReason = (BoundedGitHubPublisherReason)
            transportReasonValue;
        publisher.Transport.Mutation =
            BoundedGitHubHttpResult<BoundedGitHubIssueComment>.Failed(
                transportOutcome,
                transportReason,
                transportOutcome == BoundedGitHubHttpOutcome
                    .AuthorizationOrValidationFailure
                    ? new BoundedGitHubValidationEvidence(
                        422,
                        false,
                        "validation failed",
                        null,
                        [])
                    : null);
        var provider = new FullPathProviderFactory();
        var staging = StagingPath();

        var completion = await new ActionHostComposition(
                new ActionHostCompositionDependencies(
                    scenario.EventReader,
                    scenario.Factory,
                    github,
                    github,
                    new FullPathStateDependencies(store, github),
                    publisher,
                    provider,
                    new FrozenLocatorTimeProvider(LocatorTestData.Now),
                    () => staging))
            .RunAsync(launch, CancellationToken.None);

        Assert.Equal((ActionHostStatus)expectedStatusValue, completion.Status);
        Assert.Equal(1, provider.Creates);
        Assert.Equal(1, provider.Runs);
        Assert.Equal(expectedMutations, publisher.Transport.Bodies.Count);
        Assert.Equal(
            ActionHostStateDisposition.NotCommitted,
            completion.Summary.StateDisposition);
        Assert.False(Directory.Exists(staging));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    public async Task EachCoordinatorExactHeadBarrierStopsAChangedHead(
        int barrier,
        int expectedStickyMutations)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = FullLaunch(scenario.Launch);
        var github = new FullPathGitHubFactory(
            scenario.Transport.PullRequest);
        var store = FullPathStore(launch);
        var publisher = SuccessfulPublisher(780 + barrier);
        var setupUploads = 0;
        var provider = new FullPathProviderFactory(
            onRunCompleted: () => setupUploads = store.UploadCalls);
        github.CurrentPullRequestFact = _ =>
        {
            var shouldChange = barrier switch
            {
                1 => provider.Runs == 1 &&
                    store.UploadCalls == setupUploads &&
                    publisher.Transport.Bodies.Count == 0,
                2 => provider.Runs == 1 &&
                    store.UploadCalls > setupUploads &&
                    publisher.Transport.Bodies.Count == 0,
                3 => publisher.Transport.Bodies.Count > 0,
                _ => false,
            };
            return shouldChange
                ? scenario.Transport.PullRequest with
                {
                    HeadSha = new string('f', 40),
                }
                : scenario.Transport.PullRequest;
        };
        var staging = StagingPath();

        var completion = await new ActionHostComposition(
                new ActionHostCompositionDependencies(
                    scenario.EventReader,
                    scenario.Factory,
                    github,
                    github,
                    new FullPathStateDependencies(store, github),
                    publisher,
                    provider,
                    new FrozenLocatorTimeProvider(LocatorTestData.Now),
                    () => staging))
            .RunAsync(launch, CancellationToken.None);

        Assert.Equal(ActionHostStatus.StaleHead, completion.Status);
        Assert.Equal(1, provider.Creates);
        Assert.Equal(1, provider.Runs);
        Assert.Equal(
            expectedStickyMutations,
            publisher.Transport.Bodies.Count);
        Assert.Equal(
            ActionHostStateDisposition.NotCommitted,
            completion.Summary.StateDisposition);
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public async Task RecoversAcceptanceCrashWithoutProviderKeyOrDuplicateSticky()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = FullLaunch(scenario.Launch);
        var github = new FullPathGitHubFactory(
            scenario.Transport.PullRequest);
        var store = FullPathStore(launch);
        var publisher = SuccessfulPublisher(790);
        var provider = new FullPathProviderFactory();
        var acceptanceCrashArmed = false;
        store.AfterUpload = (_, call) =>
        {
            if (acceptanceCrashArmed || publisher.Transport.Bodies.Count == 0)
            {
                return;
            }

            acceptanceCrashArmed = true;
            store.FailUploadOnUploadCall = call + 1;
            store.ScheduledUploadFailure = OpaqueStoreFailure.OutcomeUnknown;
            store.ScheduledUploadMutationState =
                OpaqueStoreMutationState.OutcomeUnknown;
            store.HideFailedUploadForNextLists = 128;
        };
        var firstStaging = StagingPath();

        var first = await new ActionHostComposition(
                new ActionHostCompositionDependencies(
                    scenario.EventReader,
                    scenario.Factory,
                    github,
                    github,
                    new FullPathStateDependencies(store, github),
                    publisher,
                    provider,
                    new FrozenLocatorTimeProvider(LocatorTestData.Now),
                    () => firstStaging))
            .RunAsync(launch, CancellationToken.None);

        Assert.True(acceptanceCrashArmed);
        Assert.Equal(ActionHostStatus.OutcomeAmbiguous, first.Status);
        Assert.Equal(1, provider.Creates);
        Assert.Equal(1, provider.Runs);
        Assert.Single(publisher.Transport.Bodies);
        Assert.False(Directory.Exists(firstStaging));

        store.AfterUpload = null;
        store.HideNextUploadedObjectForNextLists = 0;
        var resumedStaging = StagingPath();
        var resumed = await new ActionHostComposition(
                new ActionHostCompositionDependencies(
                    scenario.EventReader,
                    scenario.Factory,
                    github,
                    github,
                    new FullPathStateDependencies(store, github),
                    publisher,
                    provider,
                    new FrozenLocatorTimeProvider(LocatorTestData.Now),
                    () => resumedStaging))
            .RunAsync(WithoutProviderKey(launch), CancellationToken.None);

        Assert.Equal(ActionHostStatus.Reviewed, resumed.Status);
        Assert.Equal(
            ActionHostStateDisposition.Accepted,
            resumed.Summary.StateDisposition);
        Assert.Equal(1, provider.Creates);
        Assert.Equal(1, provider.Runs);
        Assert.Single(publisher.Transport.Bodies);
        Assert.False(Directory.Exists(resumedStaging));
    }

    [Fact]
    public async Task ExecutesTheCompleteStickyTransactionThroughAcceptance()
    {
        var authorization = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = FullLaunch(authorization.Launch);
        var github = new FullPathGitHubFactory(
            authorization.Transport.PullRequest);
        var store = new ScriptedLocatorStore
        {
            FilterListsByName = true,
            UseNumericObjectIds = true,
            ProducingRunIdentity = launch.RunId.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ProducingRunAttempt = launch.RunAttempt,
        };
        var publisher = new FakePublisherTransportFactory();
        for (var index = 0; index < 32; index++)
        {
            publisher.Transport.Enqueue();
        }

        publisher.Transport.OnMutation = () =>
        {
            var request = Assert.IsType<AuthorizedStickyPublicationRequest>(
                publisher.Transport.Request);
            var comment = StickyPublicationTestData.Comment(
                701,
                request.Rendered.Comment);
            publisher.Transport.Mutation =
                BoundedGitHubHttpResult<BoundedGitHubIssueComment>.Success(
                    comment);
            publisher.Transport.Read =
                BoundedGitHubHttpResult<BoundedGitHubIssueComment>.Success(
                    comment);
            publisher.Transport.Pages.Clear();
            for (var index = 0; index < 32; index++)
            {
                publisher.Transport.Enqueue(comment);
            }
        };
        var provider = new FullPathProviderFactory();
        var time = new FrozenLocatorTimeProvider(LocatorTestData.Now);
        var staging = Path.Join(
            Path.GetTempPath(),
            "agentic-pr-review-r4-tests",
            Guid.NewGuid().ToString("N"));
        var composition = new ActionHostComposition(
            new ActionHostCompositionDependencies(
                authorization.EventReader,
                authorization.Factory,
                github,
                github,
                new FullPathStateDependencies(store, github),
                publisher,
                provider,
                time,
                () => staging));

        var completion = await composition.RunAsync(
            launch,
            CancellationToken.None);

        Assert.True(
            completion.Status == ActionHostStatus.Reviewed,
            $"status={completion.Status}; provider={provider.Runs}; " +
            $"creates={publisher.Transport.Creates}; " +
            $"updates={publisher.Transport.Updates}; " +
            $"reads={publisher.Transport.Reads}; " +
            $"lists={publisher.Transport.Lists}; " +
            $"publisherTransports={publisher.Creates}; " +
            $"uploads={store.UploadCalls}; readbacks={store.ReadBackCalls}; " +
            $"deletes={store.DeleteCalls}; objects={store.Objects.Length}; " +
            $"names={string.Join(',', store.Objects.Select(item =>
                item.Reference.Name.Value))}");
        Assert.Equal(
            ActionHostStateDisposition.Accepted,
            completion.Summary.StateDisposition);
        Assert.Equal(ActionHostAuthorizationScenario.HeadSha,
            completion.Summary.ReviewedSha);
        Assert.Equal(0, completion.Summary.FindingCount);
        Assert.Equal(1, provider.Creates);
        Assert.Equal(1, provider.Runs);
        Assert.Equal(1, publisher.Transport.Creates);
        Assert.True(publisher.Transport.Reads > 0);
        Assert.True(store.UploadCalls > 0);
        Assert.False(Directory.Exists(staging));

        var providerRuns = provider.Runs;
        var stickyMutations = publisher.Transport.Bodies.Count;
        var resumedStaging = Path.Join(
            Path.GetTempPath(),
            "agentic-pr-review-r4-tests",
            Guid.NewGuid().ToString("N"));
        var resumed = await new ActionHostComposition(
                new ActionHostCompositionDependencies(
                    authorization.EventReader,
                    authorization.Factory,
                    github,
                    github,
                    new FullPathStateDependencies(store, github),
                    publisher,
                    provider,
                    time,
                    () => resumedStaging))
            .RunAsync(WithoutProviderKey(launch), CancellationToken.None);

        Assert.Equal(ActionHostStatus.Reviewed, resumed.Status);
        Assert.Equal(ActionHostStateDisposition.Accepted,
            resumed.Summary.StateDisposition);
        Assert.Equal(providerRuns, provider.Runs);
        Assert.Equal(stickyMutations, publisher.Transport.Bodies.Count);
        Assert.False(Directory.Exists(resumedStaging));
    }

    [Fact]
    public async Task TrustedV2ContinuesAcrossRebuiltPayloadsAndRecoversHigherAttempt()
    {
        const long bootstrapRunId = 910;
        const long continuationRunId = 911;
        const int bootstrapAttempt = 1;
        const int continuationAttempt = 1;
        const int recoveryAttempt = 2;
        var payloadA = new string('a', 64);
        var payloadB = new string('b', 64);
        var payloadC = new string('c', 64);
        var bootstrap = TrustedV2Scenario(
            bootstrapRunId,
            bootstrapAttempt,
            payloadA);
        var github = new FullPathGitHubFactory(
            bootstrap.Scenario.Transport.PullRequest,
            workflowSha: TrustedProofPayloadBuildIdentity.SourceCommit);
        var store = FullPathStore(bootstrap.Launch);
        var publisher = SuccessfulPublisher(791);
        var provider = new FullPathProviderFactory();
        var time = new FrozenLocatorTimeProvider(LocatorTestData.Now);

        var first = await RunTrustedV2Async(
            bootstrap,
            github,
            store,
            publisher,
            provider,
            time);

        Assert.Equal(ActionHostStatus.Reviewed, first.Status);
        Assert.Equal(ActionHostStateDisposition.Accepted,
            first.Summary.StateDisposition);
        Assert.Equal(1, provider.Runs);
        Assert.Single(provider.Requests);
        Assert.Null(provider.Requests[0].Continuation);
        Assert.Single(publisher.Transport.Bodies);
        var bootstrapUploads = store.UploadCalls;
        var bootstrapLists = store.ListCalls;
        var bootstrapDeletes = store.DeleteCalls;
        var bootstrapObjects = store.Objects.Length;

        var continuation = TrustedV2Scenario(
            continuationRunId,
            continuationAttempt,
            payloadB);
        store.ProducingRunIdentity = continuationRunId.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        store.ProducingRunAttempt = continuationAttempt;
        var second = await RunTrustedV2Async(
            continuation,
            github,
            store,
            publisher,
            provider,
            time);

        Assert.True(second.Status == ActionHostStatus.Reviewed,
            $"status={second.Status}; provider={provider.Runs}; " +
            $"sticky={publisher.Transport.Bodies.Count}; " +
            $"stickyLists={publisher.Transport.Lists}; " +
            $"uploads={bootstrapUploads}->{store.UploadCalls}; " +
            $"lists={bootstrapLists}->{store.ListCalls}; " +
            $"deletes={bootstrapDeletes}->{store.DeleteCalls}; " +
            $"objects={bootstrapObjects}->{store.Objects.Length}; " +
            $"names={string.Join(',', store.Objects.Select(item =>
                item.Reference.Name.Value))}");
        Assert.Equal(ActionHostStateDisposition.Accepted,
            second.Summary.StateDisposition);
        Assert.Equal(2, provider.Runs);
        Assert.Equal(2, provider.Requests.Count);
        Assert.NotNull(provider.Requests[1].Continuation);
        Assert.Equal(provider.Requests[0].SessionId,
            provider.Requests[1].SessionId);
        Assert.Equal(2, publisher.Transport.Bodies.Count);

        var recovery = TrustedV2Scenario(
            continuationRunId,
            recoveryAttempt,
            payloadC);
        store.ProducingRunAttempt = recoveryAttempt;
        var recovered = await RunTrustedV2Async(
            recovery with { Launch = WithoutProviderKey(recovery.Launch) },
            github,
            store,
            publisher,
            provider,
            time);

        Assert.Equal(ActionHostStatus.Reviewed, recovered.Status);
        Assert.Equal(ActionHostStateDisposition.Accepted,
            recovered.Summary.StateDisposition);
        Assert.Equal(2, provider.Runs);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(2, publisher.Transport.Bodies.Count);
    }

    [Fact]
    public async Task OwnershipDriftDuringTheSecondH5BlocksStickyMutation()
    {
        var authorization = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = FullLaunch(authorization.Launch);
        var github = new FullPathGitHubFactory(
            authorization.Transport.PullRequest);
        var store = new ScriptedLocatorStore
        {
            FilterListsByName = true,
            UseNumericObjectIds = true,
            ProducingRunIdentity = launch.RunId.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ProducingRunAttempt = launch.RunAttempt,
        };
        var publisher = new FakePublisherTransportFactory();
        for (var index = 0; index < 32; index++)
        {
            publisher.Transport.Enqueue();
        }

        var provider = new FullPathProviderFactory();
        var injected = false;
        github.OnCurrentPullRequest = () =>
        {
            if (injected || provider.Runs != 1 ||
                publisher.Transport.Bodies.Count != 0 ||
                store.Objects.Length < 3)
            {
                return;
            }

            var latest = store.Objects
                .OrderBy(item => long.Parse(
                    item.Reference.ObjectId.Value,
                    System.Globalization.CultureInfo.InvariantCulture))
                .Last();
            _ = store.Add(
                store.Bytes(latest),
                latest.ExpiresAtUnixSeconds,
                name: latest.Reference.Name);
            injected = true;
        };
        var staging = Path.Join(
            Path.GetTempPath(),
            "agentic-pr-review-r4-tests",
            Guid.NewGuid().ToString("N"));
        var completion = await new ActionHostComposition(
                new ActionHostCompositionDependencies(
                    authorization.EventReader,
                    authorization.Factory,
                    github,
                    github,
                    new FullPathStateDependencies(store, github),
                    publisher,
                    provider,
                    new FrozenLocatorTimeProvider(LocatorTestData.Now),
                    () => staging))
            .RunAsync(launch, CancellationToken.None);

        Assert.True(injected);
        Assert.Equal(ActionHostStatus.StateConflict, completion.Status);
        Assert.Equal(1, provider.Runs);
        Assert.Empty(publisher.Transport.Bodies);
        Assert.Equal(0, publisher.Transport.Creates);
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public async Task InlineCapabilityIsMintedAfterAcceptanceAndConsumedOnce()
    {
        var authorization = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = FullLaunch(authorization.Launch);
        var github = new FullPathGitHubFactory(
            authorization.Transport.PullRequest,
            withInlineFile: true);
        var store = new ScriptedLocatorStore
        {
            FilterListsByName = true,
            UseNumericObjectIds = true,
            ProducingRunIdentity = launch.RunId.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ProducingRunAttempt = launch.RunAttempt,
        };
        var publisher = new FakePublisherTransportFactory();
        for (var index = 0; index < 32; index++)
        {
            publisher.Transport.Enqueue();
        }

        publisher.Transport.OnMutation = () =>
        {
            var request = Assert.IsType<AuthorizedStickyPublicationRequest>(
                publisher.Transport.Request);
            var comment = StickyPublicationTestData.Comment(
                771,
                request.Rendered.Comment);
            publisher.Transport.Mutation =
                BoundedGitHubHttpResult<BoundedGitHubIssueComment>.Success(
                    comment);
            publisher.Transport.Read =
                BoundedGitHubHttpResult<BoundedGitHubIssueComment>.Success(
                    comment);
            publisher.Transport.Pages.Clear();
            for (var index = 0; index < 32; index++)
            {
                publisher.Transport.Enqueue(comment);
            }
        };
        var provider = new FullPathProviderFactory(withFinding: true);
        var inline = new ConsumingInlineHook();
        var staging = Path.Join(
            Path.GetTempPath(),
            "agentic-pr-review-r4-tests",
            Guid.NewGuid().ToString("N"));
        var completion = await new ActionHostComposition(
                new ActionHostCompositionDependencies(
                    authorization.EventReader,
                    authorization.Factory,
                    github,
                    github,
                    new FullPathStateDependencies(store, github),
                    publisher,
                    provider,
                    new FrozenLocatorTimeProvider(LocatorTestData.Now),
                    () => staging,
                    inline))
            .RunAsync(launch, CancellationToken.None);

        Assert.True(
            completion.Status == ActionHostStatus.Reviewed,
            $"status={completion.Status}; github={github.CurrentPullRequestCalls}; " +
            $"git={github.GitCommitCalls}/{github.GitTreeCalls}/" +
            $"{github.GitBlobCalls}; " +
            $"provider={provider.Runs}/" +
            $"{provider.LastOutcome?.Succeeded}/" +
            $"{provider.LastOutcome?.Review?.Findings.Length}/" +
            $"{provider.LastOutcome?.Diagnostic?.Code}; " +
            $"store={store.Objects.Length}/u{store.UploadCalls}/" +
            $"l{store.ListCalls}/r{store.ReadBackCalls}; " +
            $"objects={string.Join(',', store.Objects.Select(item =>
                item.Reference.Name.Value))}; " +
            $"p2={publisher.Transport.Bodies.Count}; inline={inline.Calls}");
        Assert.Equal(1, completion.Summary.FindingCount);
        Assert.Equal(1, inline.Calls);
        Assert.Equal(1, inline.CandidateCount);
        Assert.True(inline.FirstConsumption);
        Assert.False(inline.ReplayConsumption);
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public async Task ProductionInlineWarningPreservesAcceptedStickyAndState()
    {
        var authorization = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = FullLaunch(authorization.Launch);
        var github = new FullPathGitHubFactory(
            authorization.Transport.PullRequest,
            withInlineFile: true);
        var store = new ScriptedLocatorStore
        {
            FilterListsByName = true,
            UseNumericObjectIds = true,
            ProducingRunIdentity = launch.RunId.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ProducingRunAttempt = launch.RunAttempt,
        };
        var publisher = new FakePublisherTransportFactory();
        for (var index = 0; index < 32; index++)
        {
            publisher.Transport.Enqueue();
        }

        publisher.Transport.OnMutation = () =>
        {
            var request = Assert.IsType<AuthorizedStickyPublicationRequest>(
                publisher.Transport.Request);
            var comment = StickyPublicationTestData.Comment(
                772,
                request.Rendered.Comment);
            publisher.Transport.Mutation =
                BoundedGitHubHttpResult<BoundedGitHubIssueComment>.Success(
                    comment);
            publisher.Transport.Read =
                BoundedGitHubHttpResult<BoundedGitHubIssueComment>.Success(
                    comment);
            publisher.Transport.Pages.Clear();
            for (var index = 0; index < 32; index++)
            {
                publisher.Transport.Enqueue(comment);
            }
        };
        var inline = new CompositionInlineTransportFactory();
        inline.Transport.EnqueuePage();
        inline.Transport.EnqueuePage();
        var staging = Path.Join(
            Path.GetTempPath(),
            "agentic-pr-review-r4-tests",
            Guid.NewGuid().ToString("N"));
        var completion = await new ActionHostComposition(
                new ActionHostCompositionDependencies(
                    authorization.EventReader,
                    authorization.Factory,
                    github,
                    github,
                    new FullPathStateDependencies(store, github),
                    publisher,
                    new FullPathProviderFactory(withFinding: true),
                    new FrozenLocatorTimeProvider(LocatorTestData.Now),
                    () => staging,
                    new PostAcceptanceInlinePublisherHook(inline)))
            .RunAsync(launch, CancellationToken.None);

        Assert.Equal(ActionHostStatus.ReviewedWithInlineWarnings,
            completion.Status);
        Assert.Equal(ActionHostStateDisposition.Accepted,
            completion.Summary.StateDisposition);
        Assert.Contains(completion.Annotations,
            static annotation => annotation.Code ==
                ActionHostAnnotationCode.InlinePublicationIncomplete);
        Assert.Equal(1, inline.Creates);
        Assert.Equal(1, inline.Transport.BatchCalls);
        Assert.Equal(0, inline.Transport.IndividualCalls);
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public async Task MissingGitHubCredentialClosesBeforeAnyLaterProducer()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch,
            includeToken: false);
        var stagingCalls = 0;
        var composition = new ActionHostComposition(Dependencies(
            scenario,
            () =>
            {
                stagingCalls++;
                throw new InvalidOperationException("must not stage");
            }));

        var completion = await composition.RunAsync(
            scenario.Launch,
            CancellationToken.None);

        Assert.Equal(ActionHostStatus.CredentialsMissing, completion.Status);
        Assert.Equal(
            ActionHostStateDisposition.NotCommitted,
            completion.Summary.StateDisposition);
        Assert.Equal(0, scenario.Factory.Calls);
        Assert.Equal(0, stagingCalls);
    }

    [Theory]
    [InlineData("fork")]
    [InlineData("draft")]
    [InlineData("closed")]
    public async Task PreservesExactH2SkipAndDoesNotMaterializeLaterLayers(
        string kind)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var pullRequest = scenario.Transport.PullRequest;
        scenario.Transport.PullRequest = kind switch
        {
            "fork" => pullRequest with
            {
                HeadRepository = new(
                    99,
                    "contributor/agentic-pr-review"),
            },
            "draft" => pullRequest with { Draft = true },
            _ => pullRequest with { State = "closed" },
        };
        var stagingCalls = 0;
        var composition = new ActionHostComposition(Dependencies(
            scenario,
            () =>
            {
                stagingCalls++;
                throw new InvalidOperationException("must not stage");
            }));

        var completion = await composition.RunAsync(
            scenario.Launch,
            CancellationToken.None);

        var expected = kind switch
        {
            "fork" => ActionHostStatus.SkippedFork,
            "draft" => ActionHostStatus.SkippedDraft,
            _ => ActionHostStatus.SkippedClosed,
        };
        Assert.Equal(expected, completion.Status);
        Assert.Equal(
            ActionHostStateDisposition.NotAccessed,
            completion.Summary.StateDisposition);
        Assert.Equal(0, stagingCalls);
    }

    private static ActionHostCompositionDependencies Dependencies(
        ActionHostAuthorizationScenario scenario,
        Func<string> stagingParentFactory)
    {
        var github = new ActionHostGitHubAuthorizationTransportFactory();
        return new ActionHostCompositionDependencies(
            scenario.EventReader,
            scenario.Factory,
            github,
            github,
            new AcceptedStateProductionDependencies(),
            new BoundedGitHubPublisherTransportFactory(),
            new ActionHostDeepSeekProviderRunnerFactory(),
            TimeProvider.System,
            stagingParentFactory);
    }

    private static async Task<ActionHostCompletion> RunTrustedV2Async(
        TrustedV2CompositionScenario value,
        FullPathGitHubFactory github,
        ScriptedLocatorStore store,
        FakePublisherTransportFactory publisher,
        FullPathProviderFactory provider,
        TimeProvider time)
    {
        var staging = StagingPath();
        var completion = await new ActionHostComposition(
                new ActionHostCompositionDependencies(
                    value.Scenario.EventReader,
                    value.Scenario.Factory,
                    github,
                    github,
                    new FullPathStateDependencies(store, github),
                    publisher,
                    provider,
                    time,
                    () => staging,
                    workflowAdmission: TrustedProofV2WorkflowAdmission.Instance))
            .RunAsync(value.Launch, CancellationToken.None);
        Assert.False(Directory.Exists(staging));
        return completion;
    }

    private static TrustedV2CompositionScenario TrustedV2Scenario(
        long runId,
        int runAttempt,
        string payloadSha256)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var source = TrustedProofPayloadBuildIdentity.SourceCommit;
        var workflow = Encoding.UTF8.GetBytes(
            TrustedProofV2WorkflowAdmission.Render(source));
        var header = Encoding.ASCII.GetBytes($"blob {workflow.Length}\0");
        scenario.Transport.Source = scenario.Transport.Source with
        {
            BlobSha = Convert.ToHexString(SHA1.HashData(
                header.Concat(workflow).ToArray())).ToLowerInvariant(),
            Bytes = workflow,
        };
        scenario.Transport.CurrentRun = scenario.Transport.CurrentRun with
        {
            Id = runId,
            Attempt = runAttempt,
            HeadSha = source,
        };
        var keyed = FullLaunch(scenario.Launch);
        Assert.True(ActionHostLaunchContract.TryCreate(
            keyed.Inputs,
            keyed.EventJsonPath,
            keyed.EventJsonSha256,
            keyed.RepositoryName,
            keyed.RepositoryId,
            runId,
            runAttempt,
            keyed.WorkflowPath,
            keyed.WorkflowRef,
            source,
            source,
            payloadSha256,
            keyed.BuildDiscriminator,
            keyed.Cancellation,
            keyed.ArtifactBridgeEndpoint,
            out var launch));
        return new(scenario, launch!);
    }

    private static ActionHostLaunchContract FullLaunch(
        ActionHostLaunchContract launch)
    {
        Assert.True(ActionHostProviderApiKey.TryCreate(
            "provider-key-canary",
            out var providerKey));
        Assert.True(ActionHostStateKey.TryCreate(
            Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()),
            out var stateKey));
        Assert.True(ActionHostInputs.TryCreate(
            launch.Inputs.GitHubToken,
            providerKey,
            stateKey,
            previousStateKey: null,
            launch.Inputs.ConfigPath,
            launch.Inputs.PullRequestNumber,
            launch.Inputs.StateMode,
            out var inputs));
        Assert.True(ActionHostLaunchContract.TryCreate(
            inputs,
            launch.EventJsonPath,
            launch.EventJsonSha256,
            launch.RepositoryName,
            launch.RepositoryId,
            launch.RunId,
            launch.RunAttempt,
            launch.WorkflowPath,
            launch.WorkflowRef,
            launch.WorkflowSha,
            launch.ActionSourceSha,
            launch.PayloadSha256,
            launch.BuildDiscriminator,
            launch.Cancellation,
            launch.ArtifactBridgeEndpoint,
            out var full));
        return full!;
    }

    private static ActionHostLaunchContract WithCancellation(
        ActionHostLaunchContract launch,
        ActionHostCancellationState cancellation)
    {
        Assert.True(ActionHostLaunchContract.TryCreate(
            launch.Inputs,
            launch.EventJsonPath,
            launch.EventJsonSha256,
            launch.RepositoryName,
            launch.RepositoryId,
            launch.RunId,
            launch.RunAttempt,
            launch.WorkflowPath,
            launch.WorkflowRef,
            launch.WorkflowSha,
            launch.ActionSourceSha,
            launch.PayloadSha256,
            launch.BuildDiscriminator,
            cancellation,
            launch.ArtifactBridgeEndpoint,
            out var cancelled));
        return cancelled!;
    }

    private static ActionHostLaunchContract WithoutProviderKey(
        ActionHostLaunchContract launch)
    {
        Assert.True(ActionHostInputs.TryCreate(
            launch.Inputs.GitHubToken,
            providerApiKey: null,
            launch.Inputs.StateKey,
            launch.Inputs.PreviousStateKey,
            launch.Inputs.ConfigPath,
            launch.Inputs.PullRequestNumber,
            launch.Inputs.StateMode,
            out var inputs));
        Assert.True(ActionHostLaunchContract.TryCreate(
            inputs,
            launch.EventJsonPath,
            launch.EventJsonSha256,
            launch.RepositoryName,
            launch.RepositoryId,
            launch.RunId,
            launch.RunAttempt,
            launch.WorkflowPath,
            launch.WorkflowRef,
            launch.WorkflowSha,
            launch.ActionSourceSha,
            launch.PayloadSha256,
            launch.BuildDiscriminator,
            launch.Cancellation,
            launch.ArtifactBridgeEndpoint,
            out var withoutProviderKey));
        return withoutProviderKey!;
    }

    private static ScriptedLocatorStore FullPathStore(
        ActionHostLaunchContract launch,
        bool rawUnknownFirstUpload = false) => new()
        {
            FilterListsByName = true,
            UseNumericObjectIds = true,
            ProducingRunIdentity = launch.RunId.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ProducingRunAttempt = launch.RunAttempt,
            NextUploadFailure = rawUnknownFirstUpload
                ? OpaqueStoreFailure.OutcomeUnknown
                : OpaqueStoreFailure.None,
            NextUploadMutationState = rawUnknownFirstUpload
                ? OpaqueStoreMutationState.OutcomeUnknown
                : OpaqueStoreMutationState.NotCommitted,
            PersistFailedUpload = rawUnknownFirstUpload,
        };

    private static FakePublisherTransportFactory EmptyPublisher()
    {
        var publisher = new FakePublisherTransportFactory();
        for (var index = 0; index < 32; index++)
        {
            publisher.Transport.Enqueue();
        }

        return publisher;
    }

    private static FakePublisherTransportFactory SuccessfulPublisher(
        long commentId)
    {
        var publisher = EmptyPublisher();
        publisher.Transport.OnMutation = () =>
        {
            var request = Assert.IsType<AuthorizedStickyPublicationRequest>(
                publisher.Transport.Request);
            var comment = StickyPublicationTestData.Comment(
                commentId,
                request.Rendered.Comment);
            publisher.Transport.Mutation =
                BoundedGitHubHttpResult<BoundedGitHubIssueComment>.Success(
                    comment);
            publisher.Transport.Read =
                BoundedGitHubHttpResult<BoundedGitHubIssueComment>.Success(
                    comment);
            publisher.Transport.Pages.Clear();
            for (var index = 0; index < 256; index++)
            {
                publisher.Transport.Enqueue(comment);
            }
        };
        return publisher;
    }

    private static string StagingPath() => Path.Join(
        Path.GetTempPath(),
        "agentic-pr-review-r4-tests",
        Guid.NewGuid().ToString("N"));

    private sealed class FullPathStateDependencies(
        IRestrictedStateStore store,
        IActionHostGitObjectTransportFactory github) :
        IAcceptedStateProductionDependencies
    {
        public IRestrictedStateStore CreateArtifactStore(
            ActionHostLaunchContract launch) => store;

        public IActionHostGitObjectTransport CreateAncestryTransport(
            ActionHostGitHubToken token) =>
            github.CreateExactObjectTransport(token);
    }

    private sealed class FullPathProviderFactory(
        bool withFinding = false,
        bool cancelledOutcome = false,
        System.Action? onCreate = null,
        System.Action? onRunCompleted = null) :
        IActionHostProviderRunnerFactory
    {
        private readonly bool withFinding = withFinding;
        private readonly bool cancelledOutcome = cancelledOutcome;
        private readonly System.Action? onCreate = onCreate;
        private readonly System.Action? onRunCompleted = onRunCompleted;
        internal int Creates { get; private set; }
        internal int Runs { get; private set; }
        internal AgentRunOutcome? LastOutcome { get; private set; }
        internal List<AgentRunRequest> Requests { get; } = [];

        public IActionHostProviderRunner Create(
            ActionHostProviderPolicy policy,
            ActionHostProviderApiKey key,
            ReviewedSnapshot snapshot,
            TimeProvider timeProvider)
        {
            onCreate?.Invoke();
            Creates++;
            return new Runner(this, snapshot, timeProvider);
        }

        private sealed class Runner(
            FullPathProviderFactory owner,
            ReviewedSnapshot snapshot,
            TimeProvider timeProvider) :
            IActionHostProviderRunner
        {
            public async Task<AgentRunOutcome> RunAsync(
                AgentRunRequest request,
                CancellationToken cancellationToken)
            {
                owner.Runs++;
                owner.Requests.Add(request);
                var callSuffix = owner.Runs.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                if (owner.cancelledOutcome)
                {
                    var cancelled = AgentRunOutcome.Failure(
                        AgentFailureCodes.Cancelled,
                        modelCalls: 0,
                        toolCalls: 0,
                        ImmutableArray<AgentLogicalEvent>.Empty);
                    owner.LastOutcome = cancelled;
                    owner.onRunCompleted?.Invoke();
                    return cancelled;
                }

                var executor = owner.withFinding
                    ? new SnapshotToolExecutor(
                        snapshot,
                        new VerifiedReviewedFileAccess())
                    : null;
                AgentFinding? finding = null;
                AgentToolExecution? scriptedExecution = null;
                if (executor is not null)
                {
                    Assert.True(AgentToolArguments.TryReadFile(
                        "{\"path\":\"file.txt\",\"start_line\":1," +
                        "\"line_count\":20}",
                        out var arguments));
                    scriptedExecution = await executor.ExecuteAsync(
                        new PreparedReadFileCall(
                            $"read-file-{callSuffix}",
                            arguments!),
                        cancellationToken);
                    var observation = Assert.IsType<AgentObservation>(
                        scriptedExecution.Observation);
                    finding = new AgentFinding(
                        "high",
                        "Synthetic inline finding",
                        "The changed line is covered by the integration proof.",
                        [new AgentEvidence(
                            observation.ObservationId,
                            "file.txt",
                            1,
                            1)]);
                }

                var terminal = AgentToolArguments.WriteFinishReview(
                    $"Synthetic full transaction completed {callSuffix}.",
                    finding is null
                        ? ImmutableArray<AgentFinding>.Empty
                        : [finding]);
                IAgentToolExecutor toolExecutor = executor is null
                    ? new NoToolExecutor()
                    : new SingleExecutionToolExecutor(scriptedExecution!);
                var outcome = await new AgentLoop(
                        new TerminalChatClient(
                            request,
                            terminal,
                            callSuffix,
                            readFileFirst: executor is not null),
                        toolExecutor,
                        timeProvider)
                    .RunAsync(request, cancellationToken);
                owner.LastOutcome = outcome;
                owner.onRunCompleted?.Invoke();
                return outcome;
            }

            public void Dispose() { }
        }
    }

    private sealed class TerminalChatClient(
        AgentRunRequest run,
        byte[] terminal,
        string callSuffix,
        bool readFileFirst = false) : IProjectChatClient
    {
        private int calls;

        public Task<ProjectChatResponse> GetResponseAsync(
            ProjectChatRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var messagePosition = request.Messages.Length;
            var reasoning = new ProjectReasoningContent(
                "synthetic terminal",
                string.Empty,
                DeepSeekReasoningContinuationCodec.FramingName,
                AssociatedCallId: null,
                messagePosition,
                Position: 0);
            var message = new ProjectChatMessage(
                "assistant",
                [
                    reasoning,
                    readFileFirst && Interlocked.Increment(ref calls) == 1
                        ? new ProjectToolCallContent(
                            $"read-file-{callSuffix}",
                            AgentToolRegistry.ReadFileName,
                            "{\"path\":\"file.txt\",\"start_line\":1," +
                            "\"line_count\":20}")
                        : new ProjectToolCallContent(
                            $"finish-review-{callSuffix}",
                            AgentToolRegistry.FinishReviewName,
                            Encoding.UTF8.GetString(terminal)),
                ]);
            var continuation = new ProjectContinuation(
                run.StablePlan.ProviderId,
                run.StablePlan.ModelId,
                run.StablePlan.AdapterId,
                run.SessionId,
                [
                    new ProjectContinuationItem(
                        reasoning.Text,
                        reasoning.Opaque,
                        reasoning.Framing,
                        reasoning.AssociatedCallId,
                        reasoning.MessagePosition,
                        reasoning.Position),
                ]);
            return Task.FromResult(new ProjectChatResponse(
                message,
                new ProjectChatUsage(1, 1),
                CapturedResponseBodyBytes: 1,
                continuation));
        }
    }

    private sealed class ConsumingInlineHook :
        IActionHostPostAcceptanceInlineHook
    {
        internal int Calls { get; private set; }
        internal int CandidateCount { get; private set; }
        internal bool FirstConsumption { get; private set; }
        internal bool ReplayConsumption { get; private set; }

        public Task<ActionHostInlineHookResult> PublishAsync(
            ActionHostCoordinator.PostAcceptanceInlineRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            FirstConsumption = request.TryConsume(out var operation);
            CandidateCount = operation?.CandidateMap.Candidates.Length ?? 0;
            ReplayConsumption = request.TryConsume(out _);
            return Task.FromResult(ActionHostInlineHookResult.Complete);
        }
    }

    private sealed class CompositionInlineTransportFactory :
        IInlineGitHubPublisherTransportFactory
    {
        internal CompositionInlineTransport Transport { get; } = new();
        internal int Creates { get; private set; }

        public IInlineGitHubPublisherTransport Create(
            AuthorizedInlinePublicationRequest request)
        {
            Creates++;
            return Transport;
        }
    }

    private sealed class CompositionInlineTransport :
        IInlineGitHubPublisherTransport
    {
        private readonly Queue<BoundedGitHubHttpResult<
            BoundedGitHubReviewCommentPage>> pages = new();

        internal int BatchCalls { get; private set; }
        internal int IndividualCalls { get; private set; }
        public bool IsWithinOverallDeadline => true;

        internal void EnqueuePage() => pages.Enqueue(
            BoundedGitHubHttpResult<BoundedGitHubReviewCommentPage>.Success(
                new([], null, null)));

        public Task<BoundedGitHubHttpResult<BoundedGitHubReviewCommentPage>>
            ListReviewCommentsAsync(int page,
                CancellationToken cancellationToken) =>
            Task.FromResult(pages.Dequeue());

        public Task<BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>>
            CreateBatchReviewAsync(ReadOnlyMemory<byte> body,
                CancellationToken cancellationToken)
        {
            BatchCalls++;
            return Task.FromResult(BoundedGitHubHttpResult<
                BoundedGitHubPullRequestReview>.Failed(
                    BoundedGitHubHttpOutcome.OutcomeUnknown,
                    BoundedGitHubPublisherReason.TransportFailure));
        }

        public Task<BoundedGitHubHttpResult<BoundedGitHubReviewComment>>
            CreateReviewCommentAsync(ReadOnlyMemory<byte> body,
                CancellationToken cancellationToken)
        {
            IndividualCalls++;
            throw new InvalidOperationException(
                "Ambiguous batch outcomes cannot fan out.");
        }

        public Task<BoundedGitHubHttpResult<BoundedGitHubReviewComment>>
            GetReviewCommentAsync(long commentId,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "No individual comment was created.");

        public void Dispose() { }
    }

    private sealed class NoToolExecutor : IAgentToolExecutor
    {
        public string? Preflight(PreparedAgentToolCall call) =>
            "unexpected_tool_call";

        public ValueTask<AgentToolExecution> ExecuteAsync(
            PreparedAgentToolCall call,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No tool call is expected.");
    }

    private sealed class SingleExecutionToolExecutor(
        AgentToolExecution execution) : IAgentToolExecutor
    {
        private int calls;

        public string? Preflight(PreparedAgentToolCall call) =>
            call is PreparedReadFileCall
                ? null
                : "unexpected_tool_call";

        public ValueTask<AgentToolExecution> ExecuteAsync(
            PreparedAgentToolCall call,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (call is not PreparedReadFileCall ||
                Interlocked.Increment(ref calls) != 1)
            {
                throw new InvalidOperationException(
                    "Exactly one scripted read_file call is expected.");
            }

            return ValueTask.FromResult(execution);
        }
    }

    private sealed class FullPathGitHubFactory :
        IActionHostGitObjectTransportFactory,
        IActionHostReviewedSnapshotTransportFactory
    {
        private static readonly string WorkflowRoot = new('1', 40);
        private static readonly string GitHubTree = new('2', 40);
        private static readonly string InstructionsTree = new('3', 40);
        private static readonly string ConfigBlob = new('4', 40);
        private static readonly string InstructionsBlob = new('5', 40);
        private static readonly string ReviewedRoot = new('6', 40);
        private const string FileBlob =
            "acff1efca38bb0f23c265ddb8aa37f337eb8e89d";
        private static readonly string BaseRoot = new('8', 40);
        private static readonly byte[] FileBytes =
            Encoding.UTF8.GetBytes("changed line\n");
        private static readonly byte[] Instructions =
            Encoding.UTF8.GetBytes("Review the exact snapshot.");
        private readonly ActionHostGitHubPullRequestFact pullRequest;
        private readonly bool withInlineFile;
        private readonly byte[] config;
        private readonly string workflowSha;

        internal int CurrentPullRequestCalls { get; private set; }
        internal int GitCommitCalls { get; private set; }
        internal int GitTreeCalls { get; private set; }
        internal int GitBlobCalls { get; private set; }
        internal System.Action? OnCurrentPullRequest { get; set; }
        internal Func<int, ActionHostGitHubPullRequestFact>?
            CurrentPullRequestFact { get; set; }

        internal FullPathGitHubFactory(
            ActionHostGitHubPullRequestFact pullRequest,
            bool withInlineFile = false,
            string? workflowSha = null)
        {
            this.pullRequest = pullRequest;
            this.withInlineFile = withInlineFile;
            this.workflowSha = workflowSha ??
                ActionHostAuthorizationScenario.WorkflowSha;
            config = Encoding.UTF8.GetBytes(
                "{\"schema\":\"agentic-pr-review.config.v1\"," +
                "\"instructionsPath\":\".github/agentic-pr-review/" +
                "instructions.md\",\"publication\":{\"mode\":\"" +
                (withInlineFile ? "sticky_and_inline" : "sticky") +
                "\"" + (withInlineFile
                    ? ",\"inlineMinSeverity\":\"high\""
                    : string.Empty) + "}}");
        }

        public IActionHostGitObjectTransport CreateExactObjectTransport(
            ActionHostGitHubToken token) => new GitObjectTransport(this);

        public IActionHostReviewedSnapshotTransport
            CreateReviewedSnapshotTransport(ActionHostGitHubToken token) =>
            new SnapshotTransport(this);

        private sealed class GitObjectTransport(FullPathGitHubFactory owner) :
            IActionHostGitObjectTransport
        {
            public Task<ActionHostGitObjectResult<ActionHostGitCommitObject>>
                GetCommitObjectAsync(
                    string repositoryName,
                    string commitSha,
                    CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.GitCommitCalls++;
                var value = commitSha == owner.workflowSha
                    ? new ActionHostGitCommitObject(commitSha, WorkflowRoot)
                    : new ActionHostGitCommitObject(
                        commitSha,
                        commitSha == owner.pullRequest.BaseSha
                            ? BaseRoot
                            : ReviewedRoot);
                return Task.FromResult(ActionHostGitObjectResult<
                    ActionHostGitCommitObject>.Success(value, 64));
            }

            public Task<ActionHostGitObjectResult<ActionHostGitTreeObject>>
                GetTreeObjectAsync(
                    string repositoryName,
                    string treeSha,
                    CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.GitTreeCalls++;
                var value = treeSha switch
                {
                    var sha when sha == WorkflowRoot => new(
                        sha,
                        [new(".github", "040000", "tree", GitHubTree)]),
                    var sha when sha == GitHubTree => new(
                        sha,
                        [
                            new(
                                "agentic-pr-review.json",
                                "100644",
                                "blob",
                                ConfigBlob),
                            new(
                                "agentic-pr-review",
                                "040000",
                                "tree",
                                InstructionsTree),
                        ]),
                    var sha when sha == InstructionsTree => new(
                        sha,
                        [new(
                            "instructions.md",
                            "100644",
                            "blob",
                            InstructionsBlob)]),
                    var sha when sha == BaseRoot => new(
                        sha,
                        []),
                    _ => new ActionHostGitTreeObject(
                        ReviewedRoot,
                        owner.withInlineFile
                            ? [new(
                                "file.txt",
                                "100644",
                                "blob",
                                FileBlob,
                                FileBytes.LongLength)]
                            : []),
                };
                return Task.FromResult(ActionHostGitObjectResult<
                    ActionHostGitTreeObject>.Success(value, 64));
            }

            public Task<ActionHostGitObjectResult<ActionHostGitBlobObject>>
                GetBlobObjectAsync(
                    string repositoryName,
                    string blobSha,
                    ActionHostGitBlobReadBudget budget,
                    CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.GitBlobCalls++;
                var value = new ActionHostGitBlobObject(
                    blobSha,
                    blobSha == ConfigBlob
                        ? (byte[])owner.config.Clone()
                        : blobSha == FileBlob
                            ? (byte[])FileBytes.Clone()
                            : (byte[])Instructions.Clone());
                return Task.FromResult(ActionHostGitObjectResult<
                    ActionHostGitBlobObject>.Success(value, value.Bytes.Length));
            }

            public Task<ActionHostGitObjectResult<ActionHostGitArchiveReader>>
                GetHeadArchiveAsync(
                    string repositoryName,
                    string headSha,
                    CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!owner.withInlineFile || headSha != owner.pullRequest.HeadSha)
                {
                    return Task.FromResult(ActionHostGitObjectResult<
                        ActionHostGitArchiveReader>.Failed(
                            ActionHostGitObjectFailure.NotFound));
                }

                return Task.FromResult(ActionHostGitObjectResult<
                    ActionHostGitArchiveReader>.Success(
                    new CompositionArchiveReader(
                        "agentic-pr-review-fixture/file.txt", FileBytes),
                    FileBytes.Length));
            }

            public void Dispose() { }
        }

        private sealed class CompositionArchiveReader(
            string name,
            byte[] bytes) : ActionHostGitArchiveReader
        {
            private readonly MemoryStream stream = new(bytes, writable: false);
            private int returned;
            private bool disposed;

            internal override int CapturedResponseBytes => bytes.Length;

            internal override Task<ActionHostGitArchiveEntry?> GetNextEntryAsync(
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (disposed)
                {
                    throw new ObjectDisposedException(nameof(CompositionArchiveReader));
                }

                if (returned == 2)
                {
                    return Task.FromResult<ActionHostGitArchiveEntry?>(null);
                }

                if (returned++ == 0)
                {
                    return Task.FromResult<ActionHostGitArchiveEntry?>(new(
                        name[..(name.LastIndexOf('/') + 1)],
                        ActionHostGitArchiveEntryType.Directory,
                        0,
                        0,
                        null,
                        null));
                }

                return Task.FromResult<ActionHostGitArchiveEntry?>(new(
                    name,
                    ActionHostGitArchiveEntryType.RegularFile,
                    0x1b4,
                    bytes.Length,
                    null,
                    stream));
            }

            public override void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                stream.Dispose();
            }
        }

        private sealed class SnapshotTransport(
            FullPathGitHubFactory owner) :
            IActionHostReviewedSnapshotTransport
        {
            public Task<ActionHostGitObjectResult<
                ActionHostGitHubPullRequestFact>> GetCurrentPullRequestAsync(
                    string repositoryName,
                    long pullRequestNumber,
                    CancellationToken cancellationToken)
            {
                owner.CurrentPullRequestCalls++;
                owner.OnCurrentPullRequest?.Invoke();
                return Task.FromResult(ActionHostGitObjectResult<
                    ActionHostGitHubPullRequestFact>.Success(
                        owner.CurrentPullRequestFact?.Invoke(
                            owner.CurrentPullRequestCalls) ??
                            owner.pullRequest,
                        64));
            }

            public Task<ActionHostGitObjectResult<
                ActionHostPullRequestFilePageObject>>
                GetPullRequestFilesAsync(
                    string repositoryName,
                    long pullRequestNumber,
                    int page,
                    int perPage,
                CancellationToken cancellationToken) =>
                Task.FromResult(ActionHostGitObjectResult<
                    ActionHostPullRequestFilePageObject>.Success(
                        new(
                            owner.withInlineFile
                                ? [new ActionHostPullRequestFileObject(
                                    FileBlob,
                                    "file.txt",
                                    null,
                                    "added",
                                    1,
                                    0,
                                    1,
                                    "@@ -0,0 +1 @@\n+changed line")]
                                : [],
                            IsComplete: true),
                        64));

            public Task<ActionHostGitObjectResult<ActionHostGitCommitObject>>
                GetCommitObjectAsync(
                    string repositoryName,
                    string commitSha,
                CancellationToken cancellationToken) =>
                Task.FromResult(ActionHostGitObjectResult<
                    ActionHostGitCommitObject>.Success(
                        new(
                            commitSha,
                            commitSha == owner.pullRequest.BaseSha
                                ? BaseRoot
                                : ReviewedRoot),
                        64));

            public Task<ActionHostGitObjectResult<ActionHostGitTreeObject>>
                GetTreeObjectAsync(
                    string repositoryName,
                    string treeSha,
                CancellationToken cancellationToken) =>
                Task.FromResult(ActionHostGitObjectResult<
                    ActionHostGitTreeObject>.Success(
                        new(
                            treeSha,
                            owner.withInlineFile && treeSha != BaseRoot
                                ? [new(
                                    "file.txt",
                                    "100644",
                                    "blob",
                                    FileBlob,
                                    FileBytes.LongLength)]
                                : []),
                        64));

            public async Task<ActionHostGitObjectResult<
                ActionHostStreamedBlobObject>> CopyBlobObjectAsync(
                    string repositoryName,
                    string blobSha,
                    long declaredSize,
                    Stream destination,
                    CancellationToken cancellationToken)
            {
                if (!owner.withInlineFile || blobSha != FileBlob ||
                    declaredSize != FileBytes.LongLength)
                {
                    return ActionHostGitObjectResult<
                        ActionHostStreamedBlobObject>.Failed(
                            ActionHostGitObjectFailure.InvalidRequest);
                }

                await destination.WriteAsync(FileBytes, cancellationToken);
                return ActionHostGitObjectResult<
                    ActionHostStreamedBlobObject>.Success(
                        new(FileBlob, FileBytes.LongLength),
                        checked((int)FileBytes.LongLength));
            }

            public void Dispose() { }
        }
    }

    private sealed record TrustedV2CompositionScenario(
        ActionHostAuthorizationScenario Scenario,
        ActionHostLaunchContract Launch);
}
