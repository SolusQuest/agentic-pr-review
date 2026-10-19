using System.Collections.Immutable;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed partial class ActionHostCompositionTests
{
    // Package-independent transaction contract only. The managed fixture separately
    // proves genuine runtime execution/capture through this same Host continuation.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task SolusFamilyUsesOfficialTransactionAndRecoversWithoutNativeSession(int findingCount)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var scenario = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = FullLaunch(scenario.Launch);
        var github = new FullPathGitHubFactory(scenario.Transport.PullRequest);
        var store = FullPathStore(launch);
        var publisher = SuccessfulPublisher(790);
        var nativeProvider = new FullPathProviderFactory();
        var compatibility = new SolusRestrictedStateCompatibility(SolusRestrictedStateFormat.PackageVersion,
            SolusRestrictedStateFormat.Profile, 1, SolusRestrictedStateFormat.Implementation,
            SolusRestrictedStateFormat.ContextFormat, SolusRestrictedStateFormat.ContextCompatibility,
            SolusRestrictedStateFormat.PackageClosureSha256, new string('1', 64), new string('2', 64));
        var executionId = Guid.NewGuid();
        var checkpoint = new SolusRestrictedStateCheckpoint(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), executionId);
        byte[] restricted = [0, 255, 128, 42, 7];
        Assert.True(SolusRestrictedStateRecord.TryCreate(compatibility, checkpoint, restricted, out var captured));
        var calls = 0;
        ActionHostComposition NewHost() => new(new ActionHostCompositionDependencies(
            scenario.EventReader, scenario.Factory, github, github, new FullPathStateDependencies(store, github),
            publisher, nativeProvider, new FrozenLocatorTimeProvider(LocatorTestData.Now), StagingPath));
        var host = NewHost();
        var first = await host.RunPreparedFreshAsync(launch, async prepared =>
        {
            Assert.NotNull(prepared.FreshContext);
            var selected = prepared with { RestoreRequest = prepared.RestoreRequest with {
                SolusTransaction = new(prepared.FreshContext!, compatibility, executionId) } };
            return await host.RunStateCommitPreparedAsync(selected, async (state, scope, _) =>
            {
                calls++;
                Assert.True(RestrictedStateService.CanPrepareFreshSolus(state));
                ImmutableArray<AgentFinding> findings = findingCount == 0 ? ImmutableArray<AgentFinding>.Empty : [
                    new AgentFinding("high", "Title", "Message", [new(new string('a', 64), "src/app.cs", 1, 1)])];
                var terminal = new AgentTerminalReview("Review complete", findings, new string('d', 64), [1]);
                Assert.True(R4ValidatedPublicationReview.TryCreateProjection(terminal,
                    prepared.Snapshot.Snapshot.Identity, scope, out var review));
                var result = await RestrictedStateService.PrepareRetainedSolusCandidateAsync(state, captured!, review!, default);
                Assert.True(result.Succeeded, result.Code);
                Assert.False(result.Value!.IsRecovered);
                Assert.Null(result.Value.Run);
                return new(null, result);
            });
        }, default);
        Assert.Null(first.Failure);
        Assert.Equal(ActionHostStateDisposition.Accepted, first.Value!.Summary.StateDisposition);
        Assert.Equal(findingCount, first.Value.Summary.FindingCount);
        Assert.Equal(1, calls);
        Assert.Equal(0, nativeProvider.Creates);
        Assert.Single(publisher.Transport.Bodies);

        var resumedHost = NewHost();
        var resumed = await resumedHost.RunPreparedFreshAsync(WithoutProviderKey(launch), async prepared =>
        {
            var selected = prepared with { RestoreRequest = prepared.RestoreRequest with {
                SolusTransaction = new(prepared.FreshContext!, compatibility, Guid.NewGuid()) } };
            return await resumedHost.RunStateCommitPreparedAsync(selected, (_, _, _) =>
                throw new InvalidOperationException("No fresh preparation during recovery"));
        }, default);
        Assert.Null(resumed.Failure);
        Assert.Equal(ActionHostStateDisposition.Accepted, resumed.Value!.Summary.StateDisposition);
        Assert.Equal(findingCount, resumed.Value.Summary.FindingCount);
        Assert.Equal(0, nativeProvider.Creates);
        Assert.Single(publisher.Transport.Bodies);

        var beforeReadback = store.UploadCalls;
        var readbackHost = NewHost();
        var readback = await readbackHost.RunPreparedFreshAsync(WithoutProviderKey(launch), async prepared =>
        {
            var restored = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(prepared.RestoreRequest with {
                SolusTransaction = new(prepared.FreshContext!, compatibility, Guid.NewGuid()) }, default);
            using var context = restored.Context;
            Assert.True(restored.Succeeded, restored.Code);
            Assert.True(RestrictedStateService.TryReadAcceptedSolusRecord(context!, out var record));
            Assert.Equal(restricted, record!.CopyRestrictedPayload());
            Assert.Equal(checkpoint, record.Checkpoint);
            return new SolusTransactionReadback(true);
        }, default);
        Assert.True(readback.Value!.Succeeded);
        Assert.Equal(beforeReadback, store.UploadCalls); // No S2 consumption claim.
    }
    [Fact]
    public async Task MissingCurrentPreparerFailsWithoutProviderPublicationOrStateMutation()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var scenario = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = FullLaunch(scenario.Launch);
        var github = new FullPathGitHubFactory(scenario.Transport.PullRequest);
        var store = FullPathStore(launch);
        var publisher = EmptyPublisher();
        var nativeProvider = new FullPathProviderFactory();
        var clock = new FrozenLocatorTimeProvider(LocatorTestData.Now);
        var host = new ActionHostComposition(new ActionHostCompositionDependencies(
            scenario.EventReader, scenario.Factory, github, github, new FullPathStateDependencies(store, github),
            publisher, nativeProvider, clock, StagingPath));
        Exception? callbackFailure = null;
        var result = await host.RunPreparedFreshAsync(launch, async prepared =>
        {
            try
            {
                var compatibility = new SolusRestrictedStateCompatibility(SolusRestrictedStateFormat.PackageVersion,
                    SolusRestrictedStateFormat.Profile, 1, SolusRestrictedStateFormat.Implementation,
                    SolusRestrictedStateFormat.ContextFormat, SolusRestrictedStateFormat.ContextCompatibility,
                    SolusRestrictedStateFormat.PackageClosureSha256, new string('1', 64), new string('2', 64));
                var restored = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(
                    prepared.RestoreRequest with { SolusTransaction = new(prepared.FreshContext!, compatibility, Guid.NewGuid()) }, default);
                using var state = restored.Context;
                Assert.True(restored.Succeeded, restored.Code);
                var uploads = store.UploadCalls;
                var deletes = store.DeleteCalls;
                var before = store.Objects.ToArray();
                var scope = new R4PublicationScopeV1((ulong)launch.RepositoryId, (ulong)launch.RepositoryId,
                    prepared.Invocation.WorkflowPath, launch.WorkflowRef, (ulong)prepared.Invocation.PullRequest.Number,
                    prepared.Policy.PolicySha256, AuthorizedAcceptedStateComposer.PayloadBuildIdentity(prepared.Policy));
                var completion = await new ActionHostCoordinator(new StickyCommentPublisher(publisher), github,
                    nativeProvider, prepared.Journal, prepared.TimeBudget).RunAsync(launch, prepared.Invocation,
                        prepared.Policy, prepared.Snapshot, state!, scope, default);
                Assert.Equal(ActionHostStatus.StateConflict, completion.Status);
                Assert.True(completion.Accounting.IsCompleteZero);
                Assert.Equal(ActionHostStateDisposition.NotCommitted, completion.Summary.StateDisposition);
                Assert.Equal(uploads, store.UploadCalls);
                Assert.Equal(deletes, store.DeleteCalls);
                Assert.Equal(before, store.Objects.ToArray());
                Assert.Equal(0, nativeProvider.Creates);
                Assert.Equal(0, nativeProvider.Runs);
                Assert.Empty(publisher.Transport.Bodies);
                return completion;
            }
            catch (Exception exception)
            {
                callbackFailure = exception;
                throw;
            }
        }, default);
        Assert.True(callbackFailure is null, callbackFailure?.ToString());
        Assert.Null(result.Failure);
        Assert.Equal(ActionHostStatus.StateConflict, result.Value!.Status);
    }

    private sealed record SolusTransactionReadback(bool Succeeded);
}
