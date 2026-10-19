using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed partial class ActionHostCompositionTests
{
    // Package-independent S1/S2/R4 producer-consumer contract. The managed
    // installed-package fixture separately supplies genuine runtime outcomes.
    [Theory]
    [InlineData("valid")]
    [InlineData("unclaimed")]
    [InlineData("wrong-round")]
    [InlineData("wrong-execution")]
    [InlineData("old-work")]
    [InlineData("old-checkpoint")]
    [InlineData("fresh-preparer")]
    public async Task SolusSuccessorRequiresExactConsumedSourceAndNewCheckpoint(string variant)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var first = TrustedV2Scenario(81001, 1, new string('a', 64));
        var next = TrustedV2Scenario(81002, 1, new string('a', 64));
        var github = new FullPathGitHubFactory(first.Scenario.Transport.PullRequest,
            workflowSha: TrustedProofPayloadBuildIdentity.SourceCommit);
        var store = FullPathStore(first.Launch);
        var publisher = SuccessfulPublisher(819);
        var provider = new FullPathProviderFactory();
        var time = new FrozenLocatorTimeProvider(LocatorTestData.Now);
        var compatibility = new SolusRestrictedStateCompatibility(SolusRestrictedStateFormat.PackageVersion,
            SolusRestrictedStateFormat.Profile, SolusRestrictedStateFormat.CompletedCandidateMode,
            SolusRestrictedStateFormat.Implementation, SolusRestrictedStateFormat.ContextFormat,
            SolusRestrictedStateFormat.ContextCompatibility, SolusRestrictedStateFormat.PackageClosureSha256,
            new string('1', 64), new string('2', 64));
        var original = new SolusRestrictedStateCheckpoint(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        byte[] payload = [0, 128, 255, 7];
        Assert.True(SolusRestrictedStateRecord.TryCreate(compatibility, original, payload, out var seed));
        ActionHostComposition Host(TrustedV2CompositionScenario scenario) => new(new ActionHostCompositionDependencies(
            scenario.Scenario.EventReader, scenario.Scenario.Factory, github, github,
            new FullPathStateDependencies(store, github), publisher, provider, time, StagingPath,
            workflowAdmission: TrustedProofV2WorkflowAdmission.Instance));
        R4ValidatedPublicationReview Projection(ActionHostPreparedReview prepared, R4PublicationScopeV1 scope, string summary)
        {
            var terminal = new AgentTerminalReview(summary, [], new string('d', 64), [1]);
            Assert.True(R4ValidatedPublicationReview.TryCreateProjection(terminal,
                prepared.Snapshot.Snapshot.Identity, scope, out var review));
            return review!;
        }
        var firstHost = Host(first);
        var seeded = await firstHost.RunPreparedFreshAsync(first.Launch, prepared =>
            firstHost.RunStateCommitPreparedAsync(prepared with { RestoreRequest = prepared.RestoreRequest with {
                SolusTransaction = new(prepared.FreshContext!, compatibility, original.ExecutionId) } },
                async (state, scope, _) => new(null, await RestrictedStateService.PrepareRetainedSolusCandidateAsync(
                    state, seed!, Projection(prepared, scope, "Seed review"), default))), default);
        Assert.Equal(ActionHostStateDisposition.Accepted, seeded.Value!.Summary.StateDisposition);
        var sourceRecords = ReadAcceptedStateRecords(store, first.Launch, time);
        var originalGeneration = Assert.Single(sourceRecords.Generations).Generation;
        var originalAcceptance = Assert.Single(sourceRecords.Acceptances);
        store.ProducingRunIdentity = next.Launch.RunId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        store.ProducingRunAttempt = next.Launch.RunAttempt;
        var execution = Guid.NewGuid(); var round = Guid.NewGuid();
        var checkpoint = new SolusRestrictedStateCheckpoint(
            variant == "old-checkpoint" ? original.CheckpointId : Guid.NewGuid(),
            variant == "old-work" ? original.LogicalWorkId : Guid.NewGuid(),
            variant == "wrong-round" ? Guid.NewGuid() : round,
            variant == "wrong-execution" ? Guid.NewGuid() : execution);
        Assert.True(SolusRestrictedStateRecord.TryCreate(compatibility, checkpoint, [8, 9, 10], out var captured));
        var calls = 0;
        var nextHost = Host(next);
        var result = await nextHost.RunPreparedFreshAsync(next.Launch, prepared =>
            nextHost.RunStateCommitPreparedAsync(prepared with { RestoreRequest = prepared.RestoreRequest with {
                SolusTransaction = new(prepared.FreshContext!, compatibility, execution, SolusTransactionMode.CompletedContextReview) } },
                async (state, scope, _) =>
                {
                    calls++;
                    Assert.False(RestrictedStateService.CanPrepareFreshSolus(state));
                    Assert.True(RestrictedStateService.CanPrepareRestoredSolus(state));
                    Assert.True(prepared.Journal.TryBeginBusinessOperation(ActionHostOperationKind.CandidatePreparation,
                        default, out var claimOperation));
                    SolusContextSource? source = null;
                    SolusConsumedContext? consumed = null;
                    try
                    {
                        using (claimOperation!)
                        {
                            var selected = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(
                                prepared.RestoreRequest with { SolusSourceSelection = new(prepared.FreshContext!, compatibility) }, default);
                            Assert.False(selected.Succeeded); // Source is deliberately not transaction Context.
                            Assert.Equal(AcceptedStateCodes.Ready, selected.Code);
                            source = Assert.IsType<SolusContextSource>(selected.SolusSource);
                            consumed = await source.PrepareAsync(execution, round, default);
                            Assert.NotNull(consumed);
                            claimOperation!.Resolve(ActionHostOperationResolution.ResolvedCommitted);
                        }
                        if (variant != "unclaimed") Assert.True(consumed!.TryClaim(prepared.FreshContext!, compatibility,
                            original, consumed.Claim.GrantId, execution, round, payload));
                        Assert.True(prepared.Journal.TryBeginBusinessOperation(ActionHostOperationKind.CandidatePreparation,
                            default, out var operation));
                        using (operation!)
                        {
                            var uploads = store.UploadCalls;
                            var candidate = variant == "fresh-preparer"
                                ? await RestrictedStateService.PrepareRetainedSolusCandidateAsync(state,
                                    captured!, Projection(prepared, scope, "Subsequent review"), default)
                                : await RestrictedStateService.PrepareRetainedRestoredSolusCandidateAsync(state,
                                    consumed!, captured!, Projection(prepared, scope, "Subsequent review"), default);
                            Assert.Equal(uploads, store.UploadCalls);
                            Assert.Equal(variant == "valid", candidate.Succeeded);
                            operation!.Resolve(ActionHostOperationResolution.ResolvedNoCommit);
                            return new ActionHostCandidatePreparation(null, candidate);
                        }
                    }
                    finally { consumed?.Dispose(); source?.Dispose(); }
                }), default);
        Assert.Equal(1, calls);
        Assert.Equal(0, provider.Creates);
        Assert.Equal(variant == "valid", result.Value!.Summary.StateDisposition == ActionHostStateDisposition.Accepted);
        var accepted = ReadAcceptedStateRecords(store, next.Launch, time);
        Assert.Equal(variant == "valid" ? 2 : 1, accepted.Acceptances.Length);
        if (variant != "valid") Assert.Single(publisher.Transport.Bodies);
        if (variant == "valid")
        {
            var successor = Assert.Single(accepted.Generations.Where(item => item.Generation.Generation == 1));
            Assert.Equal(originalGeneration.StateEnvelopeSha256, successor.Generation.PredecessorEnvelopeSha256);
            Assert.Equal(originalAcceptance.Receipt.LogicalGenerationIdentity, successor.Generation.PreviousLogicalGenerationIdentity);
            var recoveryHost = Host(next);
            var recovered = await recoveryHost.RunPreparedFreshAsync(WithoutProviderKey(next.Launch), prepared =>
                recoveryHost.RunStateCommitPreparedAsync(prepared with { RestoreRequest = prepared.RestoreRequest with {
                    SolusTransaction = new(prepared.FreshContext!, compatibility, Guid.NewGuid(), SolusTransactionMode.CompletedContextReview) } },
                    (_, _, _) => throw new InvalidOperationException("No new preparation during accepted recovery")), default);
            Assert.Equal(ActionHostStateDisposition.Accepted, recovered.Value!.Summary.StateDisposition);
            Assert.Equal(2, publisher.Transport.Bodies.Count);
        }
    }
}
