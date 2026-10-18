using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Transactions;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed partial class ActionHostCompositionTests
{
    // Package-independent transaction selection. Actual installed-package work
    // and capture are separately required by the archive qualification harness.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedSelectionBootstrapsAndPreservesOrdinarySameHeadRecovery(bool explicitFresh)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var scenario = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = FullLaunch(scenario.Launch);
        var github = new FullPathGitHubFactory(scenario.Transport.PullRequest);
        var store = FullPathStore(launch);
        var publisher = SuccessfulPublisher(41401);
        var native = new FullPathProviderFactory();
        var compatibility = ManagedSelectionCompatibility();
        var mode = explicitFresh ? SolusTransactionMode.Fresh : SolusTransactionMode.FreshOrCompletedContextReview;
        ActionHostComposition Host() => new(new ActionHostCompositionDependencies(
            scenario.EventReader, scenario.Factory, github, github, new FullPathStateDependencies(store, github),
            publisher, native, new FrozenLocatorTimeProvider(LocatorTestData.Now), StagingPath));
        var host = Host();
        var seeded = await SeedManagedSelection(host, launch, compatibility, mode);
        Assert.Equal(ActionHostStateDisposition.Accepted, seeded.Summary.StateDisposition);
        var accepted = ReadAcceptedStateRecords(store, launch, new FrozenLocatorTimeProvider(LocatorTestData.Now));
        Assert.Single(accepted.Acceptances);

        var recovery = Host();
        var result = await recovery.RunPreparedFreshAsync(WithoutProviderKey(launch), prepared =>
            recovery.RunStateCommitPreparedAsync(prepared with { RestoreRequest = prepared.RestoreRequest with {
                SolusTransaction = new(prepared.FreshContext!, compatibility, Guid.NewGuid(), mode) } },
                (_, _, _) => throw new InvalidOperationException("Same-head recovery must precede new work")), default);
        Assert.Null(result.Failure);
        Assert.Equal(ActionHostStateDisposition.Accepted, result.Value!.Summary.StateDisposition);
        Assert.True(result.Value.Accounting.IsCompleteZero);
        Assert.Equal(ActionHostTerminationReason.NotStarted, result.Value.TerminationReason);
        Assert.Single(publisher.Transport.Bodies);
        Assert.Equal(0, native.Creates);
    }

    [Theory]
    [InlineData("ahead")]
    [InlineData("unverified")]
    [InlineData("incompatible")]
    public async Task ManagedSelectionRequiresAuthenticatedCompatibleSourceAndVerifiedAncestry(string condition)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var first = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
        var firstLaunch = FullLaunch(first.Launch);
        var firstGithub = new FullPathGitHubFactory(first.Transport.PullRequest);
        var store = FullPathStore(firstLaunch);
        var publisher = SuccessfulPublisher(41402);
        var native = new FullPathProviderFactory();
        var time = new FrozenLocatorTimeProvider(LocatorTestData.Now);
        var compatibility = ManagedSelectionCompatibility();
        var seedHost = new ActionHostComposition(new ActionHostCompositionDependencies(
            first.EventReader, first.Factory, firstGithub, firstGithub, new FullPathStateDependencies(store, firstGithub),
            publisher, native, time, StagingPath));
        var seeded = await SeedManagedSelection(seedHost, firstLaunch, compatibility,
            SolusTransactionMode.FreshOrCompletedContextReview);
        Assert.Equal(ActionHostStateDisposition.Accepted, seeded.Summary.StateDisposition);
        var sourceRecords = ReadAcceptedStateRecords(store, firstLaunch, time);

        var next = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
        next.Transport.PullRequest = next.Transport.PullRequest with { HeadSha = new string('f', 40) };
        next.Transport.CurrentRun = next.Transport.CurrentRun with { Id = 41403 };
        Assert.True(ActionHostLaunchContract.TryCreate(firstLaunch.Inputs, next.Launch.EventJsonPath,
            next.Launch.EventJsonSha256, firstLaunch.RepositoryName, firstLaunch.RepositoryId, 41403,
            firstLaunch.RunAttempt, firstLaunch.WorkflowPath, firstLaunch.WorkflowRef, firstLaunch.WorkflowSha,
            firstLaunch.ActionSourceSha, firstLaunch.PayloadSha256, firstLaunch.BuildDiscriminator,
            firstLaunch.Cancellation, firstLaunch.ArtifactBridgeEndpoint, out var nextLaunch));
        var github = new FullPathGitHubFactory(next.Transport.PullRequest,
            previousHead: condition == "unverified" ? null : first.Transport.PullRequest.HeadSha);
        store.ProducingRunIdentity = "41403";
        var uploadsBefore = store.UploadCalls;
        var calls = 0;
        var host = new ActionHostComposition(new ActionHostCompositionDependencies(
            next.EventReader, next.Factory, github, github, new FullPathStateDependencies(store, github),
            publisher, native, time, StagingPath));
        var selectedCompatibility = condition == "incompatible"
            ? compatibility with { RuntimeConfigurationSha256 = new string('3', 64) } : compatibility;
        var result = await host.RunPreparedFreshAsync(nextLaunch!, prepared =>
            host.RunStateCommitPreparedAsync(prepared with { RestoreRequest = prepared.RestoreRequest with {
                SolusTransaction = new(prepared.FreshContext!, selectedCompatibility, Guid.NewGuid(),
                    SolusTransactionMode.FreshOrCompletedContextReview) } }, (state, _, _) =>
                {
                    calls++;
                    Assert.False(RestrictedStateService.CanPrepareFreshSolus(state));
                    Assert.True(RestrictedStateService.CanPrepareRestoredSolus(state));
                    return Task.FromResult(new ActionHostCandidatePreparation(ActionHostStatus.StateConflict));
                }), default);
        Assert.Null(result.Failure);
        Assert.Equal(condition == "ahead" ? ActionHostStatus.StateConflict : ActionHostStatus.AuthorizationFailed,
            result.Value!.Status);
        Assert.Equal(condition == "ahead" ? 1 : 0, calls);
        // Verified ahead may reconcile historical R4 cleanup before calling
        // the preparer. Rejected source admission never reaches that owner.
        if (condition != "ahead") Assert.Equal(uploadsBefore, store.UploadCalls);
        var retained = ReadAcceptedStateRecords(store, firstLaunch, time);
        Assert.Equal(sourceRecords.Acceptances.Select(item => item.Receipt.LogicalGenerationIdentity),
            retained.Acceptances.Select(item => item.Receipt.LogicalGenerationIdentity));
        Assert.Equal(sourceRecords.Generations.Select(item => item.Generation.StateEnvelopeSha256),
            retained.Generations.Select(item => item.Generation.StateEnvelopeSha256));
        Assert.Single(publisher.Transport.Bodies);
        Assert.Equal(0, native.Creates);
    }

    private static SolusRestrictedStateCompatibility ManagedSelectionCompatibility() => new(
        SolusRestrictedStateFormat.PackageVersion, SolusRestrictedStateFormat.Profile,
        SolusRestrictedStateFormat.CompletedCandidateMode, SolusRestrictedStateFormat.Implementation,
        SolusRestrictedStateFormat.ContextFormat, SolusRestrictedStateFormat.ContextCompatibility,
        SolusRestrictedStateFormat.PackageClosureSha256, new string('1', 64), new string('2', 64));

    private static async Task<ActionHostCompletion> SeedManagedSelection(ActionHostComposition host,
        ActionHostLaunchContract launch, SolusRestrictedStateCompatibility compatibility, SolusTransactionMode mode)
    {
        var execution = Guid.NewGuid();
        Assert.True(SolusRestrictedStateRecord.TryCreate(compatibility,
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), execution), [0, 255, 128, 7], out var record));
        var result = await host.RunPreparedFreshAsync(launch, prepared =>
            host.RunStateCommitPreparedAsync(prepared with { RestoreRequest = prepared.RestoreRequest with {
                SolusTransaction = new(prepared.FreshContext!, compatibility, execution, mode) } }, async (state, scope, _) =>
                {
                    Assert.True(RestrictedStateService.CanPrepareFreshSolus(state));
                    Assert.False(RestrictedStateService.CanPrepareRestoredSolus(state));
                    var terminal = new AgentTerminalReview("Transaction selection fixture", [], new string('d', 64), [1]);
                    Assert.True(R4ValidatedPublicationReview.TryCreateProjection(terminal,
                        prepared.Snapshot.Snapshot.Identity, scope, out var projection));
                    return new(null, await RestrictedStateService.PrepareRetainedSolusCandidateAsync(
                        state, record!, projection!, default));
                }), default);
        Assert.Null(result.Failure);
        return result.Value!;
    }
}
