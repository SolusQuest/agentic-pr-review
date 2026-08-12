using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Policy;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.Action.Policy;
using AgenticPrReview.Runtime.Tests.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Tests.Host.State.Lineage;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;

namespace AgenticPrReview.Runtime.Tests.Host.State.Restore;

public sealed class AcceptedStateProductionEndToEndTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ProductionEntryRestoresRealEncryptedAcceptedSession(
        bool rotateCurrentKey,
        bool expireAfterAdmission)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowRun);
        var launch = StateLaunch(scenario.Launch, currentKeyByte: 0x42);
        var authorized = await scenario.CreateAuthorizer().AuthorizeAsync(
            launch,
            CancellationToken.None);
        var invocation = Assert.IsType<
            ActionHostAuthorizer.AuthorizedInvocation>(authorized.Invocation);
        Assert.True(ActionHostTrustedPolicyRequest.TryBind(
            launch,
            invocation,
            out var policyRequest,
            out var bindFailure));
        Assert.Equal(ActionHostTrustedPolicyFailure.None, bindFailure);
        var materialized = await ActionHostTrustedPolicy.MaterializeAsync(
            policyRequest!,
            ActionHostTrustedPolicyTests.ScriptedObjectTransport.Valid(
                ActionHostTrustedPolicyTests.Config("sticky", null),
                Encoding.UTF8.GetBytes("trusted accepted-state policy")),
            CancellationToken.None);
        Assert.True(materialized.Succeeded);
        var policy = materialized.Policy!;
        var time = new MutableLineageTimeProvider(
            AcceptedStateTestData.AcceptedAtUnixSeconds);
        var store = new ScriptedLocatorStore
        {
            FilterListsByName = true,
            UseNumericObjectIds = true,
        };
        var dependencies = new EndToEndDependencies(store);
        var request = new ArtifactStateRestoreRequest(
            launch,
            invocation,
            policy,
            User("current review context"),
            DeepSeekReasoningContinuationCodec.Instance,
            dependencies,
            time);

        var bootstrap = await RestrictedStateService
            .RestoreAuthorizedArtifactStateAsync(
                request,
                CancellationToken.None);

        Assert.True(bootstrap.Succeeded, bootstrap.Code);
        Assert.True(bootstrap.IsBootstrap);
        using (var bootstrapContext = Assert.IsType<
            AuthorizedAcceptedStateRestoreContext>(bootstrap.Context))
        {
            Assert.True(bootstrapContext.TryGetLineageSnapshot(
                out var selected));
            Assert.NotNull(selected);
            _ = await SeedAcceptedGenerationAsync(
                store,
                request,
                selected!,
                time);
        }

        var restoreRequest = request;
        if (rotateCurrentKey)
        {
            var previousKey = Convert.ToBase64String(
                Enumerable.Repeat((byte)0x42, 32).ToArray());
            var rotatedLaunch = StateLaunch(
                scenario.Launch,
                currentKeyByte: 0x43,
                previousKey);
            var rotatedAuthorization = await scenario.CreateAuthorizer()
                .AuthorizeAsync(rotatedLaunch, CancellationToken.None);
            var rotatedInvocation = Assert.IsType<
                ActionHostAuthorizer.AuthorizedInvocation>(
                    rotatedAuthorization.Invocation);
            Assert.True(ActionHostTrustedPolicyRequest.TryBind(
                rotatedLaunch,
                rotatedInvocation,
                out var rotatedPolicyRequest,
                out var rotatedBindFailure));
            Assert.Equal(
                ActionHostTrustedPolicyFailure.None,
                rotatedBindFailure);
            var rotatedPolicy = await ActionHostTrustedPolicy.MaterializeAsync(
                rotatedPolicyRequest!,
                ActionHostTrustedPolicyTests.ScriptedObjectTransport.Valid(
                    ActionHostTrustedPolicyTests.Config("sticky", null),
                    Encoding.UTF8.GetBytes(
                        "trusted accepted-state policy")),
                CancellationToken.None);
            Assert.True(rotatedPolicy.Succeeded);
            Assert.Equal(
                policy.PolicySha256,
                rotatedPolicy.Policy!.PolicySha256);
            restoreRequest = new ArtifactStateRestoreRequest(
                rotatedLaunch,
                rotatedInvocation,
                rotatedPolicy.Policy,
                User("current review context"),
                DeepSeekReasoningContinuationCodec.Instance,
                dependencies,
                time);
        }

        var uploadsBeforeRestore = store.UploadCalls;
        var deletesBeforeRestore = store.DeleteCalls;
        if (expireAfterAdmission)
        {
            time.UnixSeconds += AcceptedStateFormat.LogicalWindowSeconds;
        }

        var restored = await RestrictedStateService
            .RestoreAuthorizedArtifactStateAsync(
                restoreRequest,
                CancellationToken.None);

        Assert.True(restored.Succeeded, restored.Code);
        if (expireAfterAdmission)
        {
            Assert.True(restored.IsBootstrap);
            using var expiredContext = Assert.IsType<
                AuthorizedAcceptedStateRestoreContext>(restored.Context);
            Assert.False(expiredContext.HasAcceptedSession);
            Assert.True(store.UploadCalls > uploadsBeforeRestore);
            Assert.True(store.DeleteCalls > deletesBeforeRestore);
            return;
        }

        Assert.False(restored.IsBootstrap);
        using var restoredContext = Assert.IsType<
            AuthorizedAcceptedStateRestoreContext>(restored.Context);
        Assert.True(restoredContext.HasAcceptedSession);
        Assert.True(restoredContext.TryGetAdmittedValue(out var admitted));
        Assert.NotNull(admitted);
        Assert.Equal(
            invocation.PullRequest.HeadSha,
            admitted!.RunRequest.ReviewedIdentity.HeadSha);
    }

    [Theory]
    [InlineData("publication")]
    [InlineData("policy")]
    [InlineData("aead")]
    [InlineData("unavailable-key")]
    [InlineData("session")]
    [InlineData("continuation")]
    [InlineData("ancestry")]
    public async Task ExpiredInvalidAcceptedStatePerformsNoS4Mutation(
        string mutation)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowRun);
        var launch = StateLaunch(scenario.Launch, currentKeyByte: 0x42);
        var authorized = await scenario.CreateAuthorizer().AuthorizeAsync(
            launch,
            CancellationToken.None);
        var invocation = Assert.IsType<
            ActionHostAuthorizer.AuthorizedInvocation>(authorized.Invocation);
        Assert.True(ActionHostTrustedPolicyRequest.TryBind(
            launch,
            invocation,
            out var policyRequest,
            out _));
        var materialized = await ActionHostTrustedPolicy.MaterializeAsync(
            policyRequest!,
            ActionHostTrustedPolicyTests.ScriptedObjectTransport.Valid(
                ActionHostTrustedPolicyTests.Config("sticky", null),
                Encoding.UTF8.GetBytes("trusted accepted-state policy")),
            CancellationToken.None);
        Assert.True(materialized.Succeeded);
        var time = new MutableLineageTimeProvider(
            AcceptedStateTestData.AcceptedAtUnixSeconds);
        var store = new ScriptedLocatorStore
        {
            FilterListsByName = true,
            UseNumericObjectIds = true,
        };
        var request = new ArtifactStateRestoreRequest(
            launch,
            invocation,
            materialized.Policy!,
            User("current review context"),
            DeepSeekReasoningContinuationCodec.Instance,
            new EndToEndDependencies(store),
            time);
        var bootstrap = await RestrictedStateService
            .RestoreAuthorizedArtifactStateAsync(
                request,
                CancellationToken.None);
        Assert.True(bootstrap.Succeeded, bootstrap.Code);
        using (var bootstrapContext = Assert.IsType<
            AuthorizedAcceptedStateRestoreContext>(bootstrap.Context))
        {
            Assert.True(bootstrapContext.TryGetLineageSnapshot(
                out var selected));
            _ = await SeedAcceptedGenerationAsync(
                store,
                request,
                selected!,
                time,
                mutation);
        }

        time.UnixSeconds += AcceptedStateFormat.LogicalWindowSeconds;
        var uploadsBeforeRestore = store.UploadCalls;
        var deletesBeforeRestore = store.DeleteCalls;

        var restored = await RestrictedStateService
            .RestoreAuthorizedArtifactStateAsync(
                request,
                CancellationToken.None);

        Assert.False(restored.Succeeded);
        Assert.Equal(uploadsBeforeRestore, store.UploadCalls);
        Assert.Equal(deletesBeforeRestore, store.DeleteCalls);
    }

    [Fact]
    public async Task ProductionEntryPreservesInitialPendingCandidateForP5()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowRun);
        var launch = StateLaunch(scenario.Launch, currentKeyByte: 0x42);
        var authorized = await scenario.CreateAuthorizer().AuthorizeAsync(
            launch,
            CancellationToken.None);
        var invocation = Assert.IsType<
            ActionHostAuthorizer.AuthorizedInvocation>(authorized.Invocation);
        Assert.True(ActionHostTrustedPolicyRequest.TryBind(
            launch,
            invocation,
            out var policyRequest,
            out _));
        var materialized = await ActionHostTrustedPolicy.MaterializeAsync(
            policyRequest!,
            ActionHostTrustedPolicyTests.ScriptedObjectTransport.Valid(
                ActionHostTrustedPolicyTests.Config("sticky", null),
                Encoding.UTF8.GetBytes("trusted accepted-state policy")),
            CancellationToken.None);
        Assert.True(materialized.Succeeded);
        var time = new MutableLineageTimeProvider(
            AcceptedStateTestData.AcceptedAtUnixSeconds);
        var store = new ScriptedLocatorStore
        {
            FilterListsByName = true,
            UseNumericObjectIds = true,
        };
        var request = new ArtifactStateRestoreRequest(
            launch,
            invocation,
            materialized.Policy!,
            User("current review context"),
            DeepSeekReasoningContinuationCodec.Instance,
            new EndToEndDependencies(store),
            time);
        var bootstrap = await RestrictedStateService
            .RestoreAuthorizedArtifactStateAsync(
                request,
                CancellationToken.None);
        Assert.True(bootstrap.Succeeded, bootstrap.Code);
        SelectedLineageSnapshot selected;
        using (var bootstrapContext = Assert.IsType<
            AuthorizedAcceptedStateRestoreContext>(bootstrap.Context))
        {
            Assert.True(bootstrapContext.TryGetLineageSnapshot(
                out var snapshot));
            selected = snapshot!;
            _ = await SeedAcceptedGenerationAsync(
                store,
                request,
                selected,
                time,
                includeReceipt: false);
        }

        var uploadsBeforeRetry = store.UploadCalls;
        var deletesBeforeRetry = store.DeleteCalls;
        var retried = await RestrictedStateService
            .RestoreAuthorizedArtifactStateAsync(
                request,
                CancellationToken.None);

        Assert.True(retried.Succeeded, retried.Code);
        Assert.True(retried.IsBootstrap);
        using var retriedContext = Assert.IsType<
            AuthorizedAcceptedStateRestoreContext>(retried.Context);
        Assert.False(retriedContext.HasAcceptedSession);
        Assert.True(retriedContext.TryGetLineageSnapshot(out var retriedLineage));
        Assert.Equal(selected, retriedLineage);
        Assert.Equal(uploadsBeforeRetry, store.UploadCalls);
        Assert.Equal(deletesBeforeRetry, store.DeleteCalls);
    }

    [Theory]
    [InlineData(ExpiryCrashCut.AfterIntentUpload)]
    [InlineData(ExpiryCrashCut.AfterTargetDelete)]
    [InlineData(ExpiryCrashCut.AfterSuccessorUpload)]
    [InlineData(ExpiryCrashCut.DuringSuccessorCleanup)]
    public async Task NextProcessConvergesInterruptedTypedExpiry(
        ExpiryCrashCut crashCut)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowRun);
        var launch = StateLaunch(scenario.Launch, currentKeyByte: 0x42);
        var authorized = await scenario.CreateAuthorizer().AuthorizeAsync(
            launch,
            CancellationToken.None);
        var invocation = Assert.IsType<
            ActionHostAuthorizer.AuthorizedInvocation>(authorized.Invocation);
        Assert.True(ActionHostTrustedPolicyRequest.TryBind(
            launch,
            invocation,
            out var policyRequest,
            out _));
        var materialized = await ActionHostTrustedPolicy.MaterializeAsync(
            policyRequest!,
            ActionHostTrustedPolicyTests.ScriptedObjectTransport.Valid(
                ActionHostTrustedPolicyTests.Config("sticky", null),
                Encoding.UTF8.GetBytes("trusted accepted-state policy")),
            CancellationToken.None);
        Assert.True(materialized.Succeeded);
        var time = new MutableLineageTimeProvider(
            AcceptedStateTestData.AcceptedAtUnixSeconds);
        var store = new ScriptedLocatorStore
        {
            FilterListsByName = true,
            UseNumericObjectIds = true,
        };
        var request = new ArtifactStateRestoreRequest(
            launch,
            invocation,
            materialized.Policy!,
            User("current review context"),
            DeepSeekReasoningContinuationCodec.Instance,
            new EndToEndDependencies(store),
            time);
        var bootstrap = await RestrictedStateService
            .RestoreAuthorizedArtifactStateAsync(
                request,
                CancellationToken.None);
        Assert.True(bootstrap.Succeeded, bootstrap.Code);
        AcceptedSeed seed;
        using (var bootstrapContext = Assert.IsType<
            AuthorizedAcceptedStateRestoreContext>(bootstrap.Context))
        {
            Assert.True(bootstrapContext.TryGetLineageSnapshot(
                out var selected));
            seed = await SeedAcceptedGenerationAsync(
                store,
                request,
                selected!,
                time);
        }

        time.UnixSeconds += AcceptedStateFormat.LogicalWindowSeconds;
        var interruptedStore = new InterruptedExpiryStore(
            store,
            seed,
            crashCut);
        var interruptedRequest = request with
        {
            Dependencies = new EndToEndDependencies(interruptedStore),
        };
        var interrupted = await RestrictedStateService
            .RestoreAuthorizedArtifactStateAsync(
                interruptedRequest,
                CancellationToken.None);
        Assert.False(interrupted.Succeeded);
        Assert.Equal(AcceptedStateCodes.OutcomeUnknown, interrupted.Code);

        var recovered = await RestrictedStateService
            .RestoreAuthorizedArtifactStateAsync(
                interruptedRequest,
                CancellationToken.None);

        Assert.True(recovered.Succeeded, recovered.Code);
        Assert.True(recovered.IsBootstrap);
        using var recoveredContext = Assert.IsType<
            AuthorizedAcceptedStateRestoreContext>(recovered.Context);
        Assert.False(recoveredContext.HasAcceptedSession);
        Assert.True(recoveredContext.TryGetLineageSnapshot(out var lineage));
        Assert.Equal(LineageTransitionKind.Expiry, lineage!.Transition);
        Assert.Equal(1, interruptedStore.ExpiryIntentUploads);
        Assert.Equal(1, interruptedStore.SuccessorUploads);
        Assert.Empty(store.Objects.Where(item =>
            item.Reference.Name == seed.CandidateName ||
            item.Reference.Name == seed.AcceptanceName ||
            item.Reference.Name == seed.ExpiryName));
        Assert.Equal(2, store.Objects.Count(item =>
            item.Reference.Name == seed.LineageName));
    }

    [Fact]
    public async Task ProductionEntryRestoresMaximumGenerationFifteenComposite()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowRun);
        var launch = StateLaunch(scenario.Launch, currentKeyByte: 0x42);
        var authorized = await scenario.CreateAuthorizer().AuthorizeAsync(
            launch,
            CancellationToken.None);
        var invocation = Assert.IsType<
            ActionHostAuthorizer.AuthorizedInvocation>(authorized.Invocation);
        Assert.True(ActionHostTrustedPolicyRequest.TryBind(
            launch,
            invocation,
            out var policyRequest,
            out _));
        var materialized = await ActionHostTrustedPolicy.MaterializeAsync(
            policyRequest!,
            ActionHostTrustedPolicyTests.ScriptedObjectTransport.Valid(
                ActionHostTrustedPolicyTests.Config("sticky", null),
                Encoding.UTF8.GetBytes("trusted accepted-state policy")),
            CancellationToken.None);
        Assert.True(materialized.Succeeded);
        var time = new MutableLineageTimeProvider(
            AcceptedStateTestData.AcceptedAtUnixSeconds);
        var store = new ScriptedLocatorStore
        {
            FilterListsByName = true,
            UseNumericObjectIds = true,
        };
        var request = new ArtifactStateRestoreRequest(
            launch,
            invocation,
            materialized.Policy!,
            User("current review context"),
            DeepSeekReasoningContinuationCodec.Instance,
            new EndToEndDependencies(store),
            time);
        var bootstrap = await RestrictedStateService
            .RestoreAuthorizedArtifactStateAsync(
                request,
                CancellationToken.None);
        Assert.True(bootstrap.Succeeded, bootstrap.Code);
        using (var bootstrapContext = Assert.IsType<
            AuthorizedAcceptedStateRestoreContext>(bootstrap.Context))
        {
            Assert.True(bootstrapContext.TryGetLineageSnapshot(
                out var selected));
            await SeedMaximumAcceptedChainAsync(
                store,
                request,
                selected!,
                time);
        }

        var restored = await RestrictedStateService
            .RestoreAuthorizedArtifactStateAsync(
                request,
                CancellationToken.None);

        Assert.True(restored.Succeeded, restored.Code);
        Assert.False(restored.IsBootstrap);
        using var restoredContext = Assert.IsType<
            AuthorizedAcceptedStateRestoreContext>(restored.Context);
        Assert.True(restoredContext.TryGetAdmittedValue(out var admitted));
        Assert.NotNull(admitted);
        Assert.Equal(15, admitted!.Artifact.Document.Generation);
        Assert.Equal(
            AgentLimits.SessionPlaintextBytes,
            admitted.Artifact.Plaintext.Length);
        Assert.NotNull(admitted.Artifact.Document.PredecessorStateSha256);
        Assert.NotNull(admitted.Artifact.Document.PriorSessionSha256);
    }

    private static async Task SeedMaximumAcceptedChainAsync(
        ScriptedLocatorStore store,
        ArtifactStateRestoreRequest request,
        SelectedLineageSnapshot selected,
        TimeProvider time)
    {
        Assert.True(AcceptedStateProductionAuthorization.TryAuthorize(
            request,
            out var authorization));
        Assert.NotNull(authorization);
        var launch = request.Launch;
        var invocation = request.Invocation;
        var policy = request.TrustedPolicy;
        var repositoryId = launch.RepositoryId.ToString(
            CultureInfo.InvariantCulture);
        using var locatorAccess = AuthorizedLocatorAccess.Issue(
            authorization!,
            repositoryId);
        Assert.NotNull(locatorAccess);
        var currentKey = launch.Inputs.StateKey!.ExportForPrivateLaunch();
        var previousKey = launch.Inputs.PreviousStateKey?
            .ExportForPrivateLaunch();
        Assert.True(LocatorStateKeyRing.TryCreate(
            locatorAccess!,
            repositoryId,
            currentKey,
            previousKey,
            out var keyRing,
            out var keyCode), keyCode);
        using (keyRing)
        {
            var now = time.GetUtcNow().ToUnixTimeSeconds();
            var logicalExpiry = checked(
                now + AcceptedStateFormat.LogicalWindowSeconds);
            var requiredPlatformExpiry = Math.Max(
                checked(now +
                    StateRetentionRequirements.ScopedPlatformRequestSeconds),
                checked(logicalExpiry +
                    StateRetentionRequirements.SentinelDependentMarginSeconds));
            var retainedLocatorDependency = checked(
                requiredPlatformExpiry +
                AcceptedStateFormat.LogicalWindowSeconds +
                StateRetentionRequirements.SentinelDependentMarginSeconds);
            var locatorResult = await new LocatorRootService(
                    store,
                    keyRing!,
                    time)
                .ResolveAsync(
                    locatorAccess!,
                    retainedLocatorDependency,
                    CancellationToken.None);
            Assert.True(locatorResult.Succeeded, locatorResult.Code);
            using var locator = Assert.IsType<LocatorContext>(
                locatorResult.Context);
            var baseScope = AuthorizedAcceptedStateComposer.BaseScope(
                authorization!);
            Assert.True(LineageBaseScopeCodec.TryEncode(
                baseScope,
                out var canonicalScope));
            try
            {
                Assert.True(locator.TryDeriveOpaqueName(
                    locatorAccess!,
                    StateObjectClasses.ToWireName(StateObjectClass.Candidate),
                    canonicalScope,
                    out var candidateName));
                Assert.True(locator.TryDeriveOpaqueName(
                    locatorAccess!,
                    StateObjectClasses.ToWireName(StateObjectClass.Acceptance),
                    canonicalScope,
                    out var acceptanceName));
                Assert.NotNull(candidateName);
                Assert.NotNull(acceptanceName);

                var trustedWorkflowIdentity =
                    AuthorizedAcceptedStateComposer.TrustedWorkflowIdentity(
                        invocation,
                        policy);
                var fixture = await AgentSessionStateBoundaryTests
                    .BuildSessionAsync(
                        repositoryId,
                        invocation.PullRequest.Number,
                        selected.SessionId,
                        trustedWorkflowIdentity,
                        policy.InstructionBytes.ToArray(),
                        policy.BuildDiscriminator,
                        invocation.PullRequest.BaseSha,
                        invocation.PullRequest.HeadSha);
                var predecessor = AcceptedStatePersistenceBoundaryTests
                    .BuildAdmittedSession(
                        fixture,
                        completedRuns: 15,
                        predecessorEnvelopeSha256: new string('d', 64),
                        priorSessionSha256: new string('e', 64),
                        maximize: false);
                var stateScope = new RestrictedStateScope(
                    repositoryId,
                    trustedWorkflowIdentity,
                    invocation.PullRequest.Number,
                    selected.SessionId,
                    policy.ProviderId,
                    policy.ModelId,
                    policy.AdapterId,
                    policy.InstructionsSha256,
                    policy.LimitsSha256,
                    policy.ToolsetSha256,
                    policy.BuildDiscriminator);
                var stateAccessResult = AuthorizedStateAccess.Authorize(
                    new RestrictedStateAccessRequest(
                        stateScope,
                        stateScope,
                        IsTrustedWorkflow: true,
                        IsSameRepository: true,
                        IsForkOrigin: false),
                    out var stateAccess);
                Assert.Equal(
                    RestrictedStateCodes.Authorized,
                    stateAccessResult.Code);
                Assert.NotNull(stateAccess);
                var stateResolver = new LocatorStateResolver(
                    stateAccess!,
                    locatorAccess!,
                    locator);
                var predecessorDocument = predecessor.Value.Artifact.Document;
                var predecessorBinding = new RestrictedStateBinding(
                    stateScope,
                    predecessorDocument.ProducerBaseSha,
                    predecessorDocument.ProducerHeadSha,
                    predecessorDocument.Generation,
                    predecessorDocument.PredecessorStateSha256,
                    now,
                    logicalExpiry);
                Assert.True(RestrictedStateEnvelope.TryEncrypt(
                    stateAccess!,
                    predecessorBinding,
                    predecessor.Plaintext,
                    stateResolver,
                    out var predecessorEnvelope,
                    out var predecessorCode), predecessorCode);
                Assert.NotNull(predecessorEnvelope);

                var current = AcceptedStatePersistenceBoundaryTests
                    .BuildAdmittedSession(
                        fixture,
                        completedRuns: 16,
                        predecessorEnvelopeSha256:
                            RestrictedStateEnvelope.EnvelopeSha256(
                                predecessorEnvelope!),
                        priorSessionSha256: predecessor.SessionSha256,
                        maximize: true);
                var currentDocument = current.Value.Artifact.Document;
                var currentBinding = new RestrictedStateBinding(
                    stateScope,
                    currentDocument.ProducerBaseSha,
                    currentDocument.ProducerHeadSha,
                    currentDocument.Generation,
                    currentDocument.PredecessorStateSha256,
                    now,
                    logicalExpiry);
                Assert.True(RestrictedStateEnvelope.TryEncrypt(
                    stateAccess!,
                    currentBinding,
                    current.Plaintext,
                    stateResolver,
                    out var currentEnvelope,
                    out var currentCode), currentCode);
                Assert.NotNull(currentEnvelope);

                var publicationScope = new R4PublicationScopeV1(
                    (ulong)launch.RepositoryId,
                    (ulong)launch.RepositoryId,
                    invocation.WorkflowPath,
                    launch.WorkflowRef,
                    (ulong)invocation.PullRequest.Number,
                    policy.PolicySha256,
                    AuthorizedAcceptedStateComposer.PayloadBuildIdentity(
                        policy));
                var identity = new ReviewedIdentity(
                    repositoryId,
                    invocation.PullRequest.Number,
                    currentDocument.ProducerBaseSha,
                    currentDocument.ProducerHeadSha);
                var vector = R4StickyPublicationByteVectors.All.MaxBy(item =>
                    Encoding.UTF8.GetByteCount(item.Rendered.Comment))!;
                var rendered = R4StickyPublicationByteVectors.RenderForScope(
                    vector.Name,
                    identity,
                    publicationScope);
                Assert.True(ValidatedPublicationPayloadV1.TryCreate(
                    rendered.Comment,
                    launch.RepositoryId,
                    launch.RepositoryName,
                    invocation.PullRequest.Number,
                    policy.PolicySha256,
                    policy.PayloadSha256,
                    policy.BuildDiscriminator,
                    AcceptedStateFormat.RenderingVersion,
                    out var publication));
                Assert.NotNull(publication);
                Assert.Equal(
                    R4PublicationIdentityV1.ComputeScopeSha256(
                        publicationScope),
                    publication!.ScopeSha256);
                Assert.True(AcceptedStatePublicationPayloadCodec.TryEncode(
                    publication,
                    out var publicationBytes));

                var retainedAncestorLogicalIdentity = new string('a', 64);
                var retainedAncestorReceiptIdentity = new string('b', 64);
                var predecessorGeneration = Generation(
                    predecessor,
                    predecessorEnvelope!,
                    retainedAncestorLogicalIdentity,
                    publicationBytes,
                    policy,
                    now,
                    logicalExpiry);
                var predecessorAccepted = await UploadAcceptedGenerationAsync(
                    store,
                    locator,
                    locatorAccess!,
                    selected,
                    candidateName!,
                    acceptanceName!,
                    predecessorGeneration,
                    retainedAncestorLogicalIdentity,
                    retainedAncestorReceiptIdentity,
                    publication,
                    now,
                    logicalExpiry,
                    requiredPlatformExpiry);
                var currentGeneration = Generation(
                    current,
                    currentEnvelope!,
                    predecessorAccepted.LogicalGenerationIdentity,
                    publicationBytes,
                    policy,
                    now,
                    logicalExpiry);
                _ = await UploadAcceptedGenerationAsync(
                    store,
                    locator,
                    locatorAccess!,
                    selected,
                    candidateName!,
                    acceptanceName!,
                    currentGeneration,
                    predecessorAccepted.LogicalGenerationIdentity,
                    predecessorAccepted.ReceiptIdentity,
                    publication,
                    now,
                    logicalExpiry,
                    requiredPlatformExpiry);

                CryptographicOperations.ZeroMemory(predecessorEnvelope!);
                CryptographicOperations.ZeroMemory(currentEnvelope!);
                CryptographicOperations.ZeroMemory(publicationBytes);
                Zero(predecessor);
                Zero(current);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonicalScope);
            }
        }
    }

    private static StateGenerationRecordV1 Generation(
        RestrictedStateAdmittedSession session,
        byte[] envelope,
        string? previousLogicalGenerationIdentity,
        byte[] publicationBytes,
        ActionHostTrustedPolicy policy,
        long now,
        long logicalExpiry)
    {
        var document = session.Value.Artifact.Document;
        return new StateGenerationRecordV1(
            ImmutableArray.CreateRange(envelope),
            RestrictedStateEnvelope.EnvelopeSha256(envelope),
            session.SessionSha256,
            document.ProducerBaseSha,
            document.ProducerHeadSha,
            document.Generation,
            document.PredecessorStateSha256,
            previousLogicalGenerationIdentity,
            now,
            logicalExpiry,
            ImmutableArray.CreateRange(publicationBytes),
            AcceptedStateRecordValidation.Sha256(publicationBytes),
            policy.PolicySha256,
            policy.ConfigSha256,
            policy.InstructionsSha256,
            policy.PayloadSha256,
            policy.BuildDiscriminator);
    }

    private static async Task<AcceptedGenerationSeed>
        UploadAcceptedGenerationAsync(
            ScriptedLocatorStore store,
            LocatorContext locator,
            AuthorizedLocatorAccess locatorAccess,
            SelectedLineageSnapshot selected,
            OpaqueStoreName candidateName,
            OpaqueStoreName acceptanceName,
            StateGenerationRecordV1 generation,
            string? previousLogicalGenerationIdentity,
            string? previousAcceptanceReceiptIdentity,
            ValidatedPublicationPayloadV1 publication,
            long now,
            long logicalExpiry,
            long requiredPlatformExpiry)
    {
        Assert.True(AcceptedStateGenerationRecordCodec.TryEncode(
            generation,
            out var generationBytes));
        Assert.True(AcceptedStateIdentity.TryComputeLogicalGeneration(
            generationBytes,
            selected.BaseScopeDigest,
            selected.Epoch,
            selected.SessionId,
            previousAcceptanceReceiptIdentity,
            out var logicalIdentity));
        var originalDraft = Draft(
            store,
            selected,
            StateObjectClass.Candidate,
            previousAcceptanceReceiptIdentity,
            now,
            logicalExpiry,
            requiredPlatformExpiry);
        Assert.True(StateControlEnvelopeV1Codec.TryEncrypt(
            locator,
            locatorAccess,
            candidateName,
            originalDraft,
            generationBytes,
            out var originalEnvelope,
            out var originalHeader,
            out var originalCode), originalCode);
        Assert.NotNull(originalHeader);
        var originalMetadata = await UploadWithMetadataAsync(
            store,
            candidateName,
            originalEnvelope,
            requiredPlatformExpiry);

        var copy = new AcceptedStatePhysicalCopyV1(
            ImmutableArray.CreateRange(generationBytes),
            logicalIdentity,
            originalHeader!.ObjectIdentity,
            originalMetadata.Reference.ObjectId.Value,
            originalMetadata.ArchiveDigest.Sha256,
            originalMetadata.EncryptedObjectDigest.Sha256);
        Assert.True(AcceptedStatePhysicalCopyCodec.TryEncode(
            copy,
            out var copyBytes));
        var copyDraft = Draft(
            store,
            selected,
            StateObjectClass.Candidate,
            previousAcceptanceReceiptIdentity,
            now,
            logicalExpiry,
            requiredPlatformExpiry);
        Assert.True(StateControlEnvelopeV1Codec.TryEncrypt(
            locator,
            locatorAccess,
            candidateName,
            copyDraft,
            copyBytes,
            out var copyEnvelope,
            out _,
            out var copyCode), copyCode);
        _ = await UploadWithMetadataAsync(
            store,
            candidateName,
            copyEnvelope,
            requiredPlatformExpiry);

        var receipt = new AcceptanceReceiptV1(
            logicalIdentity,
            originalHeader.ObjectIdentity,
            previousLogicalGenerationIdentity,
            previousAcceptanceReceiptIdentity,
            publication.ReviewedHeadSha,
            StickyPublicationOperation.Observed,
            publication.RepositoryId,
            publication.PullRequestNumber,
            CommentId: 99,
            $"https://github.com/{publication.RepositoryName}/pull/" +
                $"{publication.PullRequestNumber}#issuecomment-99",
            publication.ScopeSha256,
            publication.BodySha256,
            generation.PublicationPayloadSha256,
            "scripted",
            store.UploadCalls + 1,
            now,
            logicalExpiry);
        Assert.True(AcceptedStateAcceptanceReceiptCodec.TryEncode(
            receipt,
            out var receiptBytes));
        var receiptDraft = Draft(
            store,
            selected,
            StateObjectClass.Acceptance,
            previousAcceptanceReceiptIdentity,
            now,
            logicalExpiry,
            requiredPlatformExpiry);
        Assert.True(StateControlEnvelopeV1Codec.TryEncrypt(
            locator,
            locatorAccess,
            acceptanceName,
            receiptDraft,
            receiptBytes,
            out var receiptEnvelope,
            out var receiptHeader,
            out var receiptCode), receiptCode);
        Assert.NotNull(receiptHeader);
        _ = await UploadWithMetadataAsync(
            store,
            acceptanceName,
            receiptEnvelope,
            requiredPlatformExpiry);
        CryptographicOperations.ZeroMemory(generationBytes);
        CryptographicOperations.ZeroMemory(copyBytes);
        CryptographicOperations.ZeroMemory(receiptBytes);
        return new AcceptedGenerationSeed(
            logicalIdentity,
            receiptHeader!.ObjectIdentity);
    }

    private static async Task<OpaqueStoreObjectMetadata>
        UploadWithMetadataAsync(
            ScriptedLocatorStore store,
            OpaqueStoreName name,
            byte[] envelope,
            long requiredPlatformExpiry)
    {
        try
        {
            var uploaded = await new ScopedStateUploadProtocol(store)
                .UploadAndReadBackAsync(
                    name,
                    envelope,
                    requiredPlatformExpiry,
                    CancellationToken.None);
            Assert.True(uploaded.Succeeded, uploaded.Code);
            return Assert.IsType<OpaqueStoreObjectMetadata>(
                uploaded.Metadata);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    private static void Zero(RestrictedStateAdmittedSession session)
    {
        CryptographicOperations.ZeroMemory(session.Plaintext);
        CryptographicOperations.ZeroMemory(session.Value.Artifact.Plaintext);
    }

    private static async Task<AcceptedSeed> SeedAcceptedGenerationAsync(
        ScriptedLocatorStore store,
        ArtifactStateRestoreRequest request,
        SelectedLineageSnapshot selected,
        TimeProvider time,
        string? mutation = null,
        bool includeReceipt = true)
    {
        Assert.True(AcceptedStateProductionAuthorization.TryAuthorize(
            request,
            out var authorization));
        Assert.NotNull(authorization);
        var launch = request.Launch;
        var invocation = request.Invocation;
        var policy = request.TrustedPolicy;
        var repositoryId = launch.RepositoryId.ToString(
            CultureInfo.InvariantCulture);
        using var locatorAccess = AuthorizedLocatorAccess.Issue(
            authorization!,
            repositoryId);
        Assert.NotNull(locatorAccess);
        var currentKey = launch.Inputs.StateKey!.ExportForPrivateLaunch();
        var previousKey = launch.Inputs.PreviousStateKey?
            .ExportForPrivateLaunch();
        LocatorStateKeyRing? keyRing = null;
        Assert.True(LocatorStateKeyRing.TryCreate(
            locatorAccess!,
            repositoryId,
            currentKey,
            previousKey,
            out keyRing,
            out var keyCode), keyCode);

        using (keyRing)
        {
            var now = time.GetUtcNow().ToUnixTimeSeconds();
            var logicalExpiry = checked(
                now + AcceptedStateFormat.LogicalWindowSeconds);
            var requiredPlatformExpiry = Math.Max(
                checked(now +
                    StateRetentionRequirements.ScopedPlatformRequestSeconds),
                checked(logicalExpiry +
                    StateRetentionRequirements.SentinelDependentMarginSeconds));
            var retainedLocatorDependency = checked(
                requiredPlatformExpiry +
                AcceptedStateFormat.LogicalWindowSeconds +
                StateRetentionRequirements.SentinelDependentMarginSeconds);
            var locatorResult = await new LocatorRootService(
                    store,
                    keyRing!,
                    time)
                .ResolveAsync(
                    locatorAccess!,
                    retainedLocatorDependency,
                    CancellationToken.None);
            Assert.True(locatorResult.Succeeded, locatorResult.Code);
            using var locator = Assert.IsType<LocatorContext>(
                locatorResult.Context);

            var baseScope = AuthorizedAcceptedStateComposer.BaseScope(
                authorization!);
            Assert.True(LineageBaseScopeCodec.TryEncode(
                baseScope,
                out var canonicalScope));
            try
            {
                Assert.True(locator.TryDeriveOpaqueName(
                    locatorAccess!,
                    StateObjectClasses.ToWireName(
                        StateObjectClass.Candidate),
                    canonicalScope,
                    out var candidateName));
                Assert.True(locator.TryDeriveOpaqueName(
                    locatorAccess!,
                    StateObjectClasses.ToWireName(
                        StateObjectClass.Acceptance),
                    canonicalScope,
                    out var acceptanceName));
                Assert.True(locator.TryDeriveOpaqueName(
                    locatorAccess!,
                    StateObjectClasses.ToWireName(
                        StateObjectClass.ExpiryTransition),
                    canonicalScope,
                    out var expiryName));
                Assert.True(locator.TryDeriveOpaqueName(
                    locatorAccess!,
                    StateObjectClasses.ToWireName(
                        StateObjectClass.LineageHead),
                    canonicalScope,
                    out var lineageName));
                Assert.NotNull(candidateName);
                Assert.NotNull(acceptanceName);
                Assert.NotNull(expiryName);
                Assert.NotNull(lineageName);

                var trustedWorkflowIdentity =
                    AuthorizedAcceptedStateComposer.TrustedWorkflowIdentity(
                        invocation,
                        policy);
                var session = await AgentSessionStateBoundaryTests
                    .BuildSessionAsync(
                        repositoryId,
                        invocation.PullRequest.Number,
                        selected.SessionId,
                        trustedWorkflowIdentity,
                        policy.InstructionBytes.ToArray(),
                        policy.BuildDiscriminator,
                        invocation.PullRequest.BaseSha,
                        invocation.PullRequest.HeadSha);
                var sessionArtifact = session.Artifact;
                if (mutation == "session")
                {
                    Assert.True(AgentSessionCodec.TryWrite(
                        sessionArtifact.Document with
                        {
                            WorkflowIdentity = "wrong-workflow",
                        },
                        out var malformedSession,
                        out var sessionFailure), sessionFailure);
                    sessionArtifact = malformedSession!;
                }
                else if (mutation == "continuation")
                {
                    var run = Assert.Single(
                        sessionArtifact.Document.CompletedRuns);
                    var item = Assert.Single(run.Continuation.Items);
                    var malformedDocument = sessionArtifact.Document with
                    {
                        CompletedRuns =
                        [
                            run with
                            {
                                Continuation = run.Continuation with
                                {
                                    Items =
                                    [
                                        item with
                                        {
                                            AssociatedCallId = "finish0",
                                        },
                                    ],
                                },
                            },
                        ],
                    };
                    Assert.True(AgentSessionCodec.TryWrite(
                        malformedDocument,
                        out var malformedSession,
                        out var sessionFailure), sessionFailure);
                    sessionArtifact = malformedSession!;
                }

                var document = sessionArtifact.Document;
                var stateScope = new RestrictedStateScope(
                    repositoryId,
                    trustedWorkflowIdentity,
                    invocation.PullRequest.Number,
                    selected.SessionId,
                    policy.ProviderId,
                    policy.ModelId,
                    policy.AdapterId,
                    policy.InstructionsSha256,
                    policy.LimitsSha256,
                    policy.ToolsetSha256,
                    policy.BuildDiscriminator);
                var stateAccessResult = AuthorizedStateAccess.Authorize(
                    new RestrictedStateAccessRequest(
                        stateScope,
                        stateScope,
                        IsTrustedWorkflow: true,
                        IsSameRepository: true,
                        IsForkOrigin: false),
                    out var stateAccess);
                Assert.Equal(
                    RestrictedStateCodes.Authorized,
                    stateAccessResult.Code);
                Assert.NotNull(stateAccess);
                var stateBinding = new RestrictedStateBinding(
                    stateScope,
                    document.ProducerBaseSha,
                    document.ProducerHeadSha,
                    document.Generation,
                    document.PredecessorStateSha256,
                    now,
                    logicalExpiry);
                var stateResolver = new LocatorStateResolver(
                    stateAccess!,
                    locatorAccess!,
                    locator);
                Assert.True(RestrictedStateEnvelope.TryEncrypt(
                    stateAccess!,
                    stateBinding,
                    sessionArtifact.Plaintext,
                    stateResolver,
                    out var stateEnvelope,
                    out var stateCode), stateCode);
                Assert.NotNull(stateEnvelope);
                if (mutation == "aead")
                {
                    stateEnvelope![^1] ^= 0x01;
                }
                else if (mutation == "unavailable-key")
                {
                    Assert.True(RestrictedStateEnvelope.TryParse(
                        stateEnvelope!,
                        out var parsed));
                    var keyBytes = Encoding.ASCII.GetBytes(parsed!.KeyId);
                    var keyOffset = stateEnvelope!.AsSpan().IndexOf(keyBytes);
                    Assert.True(keyOffset >= 0);
                    Encoding.ASCII.GetBytes(
                            new string('e', keyBytes.Length))
                        .CopyTo(stateEnvelope, keyOffset);
                }

                var publicationScope = new R4PublicationScopeV1(
                    (ulong)launch.RepositoryId,
                    (ulong)launch.RepositoryId,
                    invocation.WorkflowPath,
                    launch.WorkflowRef,
                    (ulong)invocation.PullRequest.Number,
                    policy.PolicySha256,
                    AuthorizedAcceptedStateComposer.PayloadBuildIdentity(
                        policy));
                var producerHeadSha = mutation == "ancestry"
                    ? new string('a', 40)
                    : document.ProducerHeadSha;
                var publicationIdentity = mutation == "ancestry"
                    ? session.Identity with { HeadSha = producerHeadSha }
                    : session.Identity;
                _ = AcceptedStateTestData.Publication(
                    out var publicationBytes,
                    publicationIdentity,
                    policy.BuildDiscriminator,
                    publicationScope,
                    mutation == "publication"
                        ? launch.RepositoryId + 1
                        : launch.RepositoryId,
                    launch.RepositoryName,
                    invocation.PullRequest.Number,
                    policy.PolicySha256,
                    policy.PayloadSha256);
                var generation = new StateGenerationRecordV1(
                    ImmutableArray.CreateRange(stateEnvelope!),
                    RestrictedStateEnvelope.EnvelopeSha256(stateEnvelope!),
                    sessionArtifact.SessionSha256,
                    document.ProducerBaseSha,
                    producerHeadSha,
                    document.Generation,
                    document.PredecessorStateSha256,
                    PreviousLogicalGenerationIdentity: null,
                    now,
                    logicalExpiry,
                    ImmutableArray.CreateRange(publicationBytes),
                    AcceptedStateRecordValidation.Sha256(publicationBytes),
                    mutation == "policy"
                        ? new string('f', 64)
                        : policy.PolicySha256,
                    policy.ConfigSha256,
                    policy.InstructionsSha256,
                    policy.PayloadSha256,
                    policy.BuildDiscriminator);
                Assert.True(AcceptedStateGenerationRecordCodec.TryEncode(
                    generation,
                    out var generationBytes));
                Assert.True(AcceptedStateIdentity.TryComputeLogicalGeneration(
                    generationBytes,
                    selected.BaseScopeDigest,
                    selected.Epoch,
                    selected.SessionId,
                    previousAcceptanceReceiptIdentity: null,
                    out var logicalIdentity));

                var candidateDraft = Draft(
                    store,
                    selected,
                    StateObjectClass.Candidate,
                    predecessorIdentity: null,
                    now,
                    logicalExpiry,
                    requiredPlatformExpiry);
                Assert.True(StateControlEnvelopeV1Codec.TryEncrypt(
                    locator,
                    locatorAccess!,
                    candidateName!,
                    candidateDraft,
                    generationBytes,
                    out var candidateEnvelope,
                    out var candidateHeader,
                    out var candidateCode), candidateCode);
                Assert.NotNull(candidateHeader);
                await UploadAsync(
                    store,
                    candidateName!,
                    candidateEnvelope,
                    requiredPlatformExpiry);

                if (!includeReceipt)
                {
                    return new AcceptedSeed(
                        candidateName!,
                        acceptanceName!,
                        expiryName!,
                        lineageName!);
                }

                var receipt = AcceptedStateTestData.Receipt(
                    logicalIdentity,
                    candidateHeader!.ObjectIdentity,
                    out _,
                    acceptedAtUnixSeconds: now,
                    identity: publicationIdentity,
                    buildDiscriminator: policy.BuildDiscriminator,
                    scope: publicationScope,
                    repositoryId: mutation == "publication"
                        ? launch.RepositoryId + 1
                        : launch.RepositoryId,
                    repositoryName: launch.RepositoryName,
                    pullRequestNumber: invocation.PullRequest.Number,
                    policyIdentitySha256: policy.PolicySha256,
                    payloadSha256: policy.PayloadSha256) with
                {
                    ProducingRunIdentity = "scripted",
                    ProducingRunAttempt = store.UploadCalls + 1,
                };
                Assert.True(AcceptedStateAcceptanceReceiptCodec.TryEncode(
                    receipt,
                    out var receiptBytes));
                var receiptDraft = Draft(
                    store,
                    selected,
                    StateObjectClass.Acceptance,
                    predecessorIdentity: null,
                    now,
                    logicalExpiry,
                    requiredPlatformExpiry);
                Assert.True(StateControlEnvelopeV1Codec.TryEncrypt(
                    locator,
                    locatorAccess!,
                    acceptanceName!,
                    receiptDraft,
                    receiptBytes,
                    out var receiptEnvelope,
                    out _,
                    out var receiptCode), receiptCode);
                await UploadAsync(
                    store,
                    acceptanceName!,
                    receiptEnvelope,
                    requiredPlatformExpiry);
                return new AcceptedSeed(
                    candidateName!,
                    acceptanceName!,
                    expiryName!,
                    lineageName!);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonicalScope);
            }
        }
    }

    private static StateControlHeaderDraft Draft(
        ScriptedLocatorStore store,
        SelectedLineageSnapshot selected,
        StateObjectClass objectClass,
        string? predecessorIdentity,
        long now,
        long logicalExpiry,
        long requiredPlatformExpiry) =>
        new(
            selected.BaseScopeDigest,
            selected.Epoch,
            selected.SessionId,
            objectClass,
            predecessorIdentity,
            SuccessorIdentity: null,
            "scripted",
            ProducingRunAttempt: store.UploadCalls + 1,
            now,
            logicalExpiry,
            requiredPlatformExpiry);

    private static async Task UploadAsync(
        ScriptedLocatorStore store,
        OpaqueStoreName name,
        byte[] envelope,
        long requiredPlatformExpiry)
    {
        try
        {
            var uploaded = await new ScopedStateUploadProtocol(store)
                .UploadAndReadBackAsync(
                    name,
                    envelope,
                    requiredPlatformExpiry,
                    CancellationToken.None);
            Assert.True(uploaded.Succeeded, uploaded.Code);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    private static ActionHostLaunchContract StateLaunch(
        ActionHostLaunchContract launch,
        byte currentKeyByte,
        string? previousKey = null)
    {
        Assert.True(ActionHostStateKey.TryCreate(
            Convert.ToBase64String(
                Enumerable.Repeat(currentKeyByte, 32).ToArray()),
            out var stateKey));
        ActionHostPreviousStateKey? previousStateKey = null;
        if (previousKey is not null)
        {
            Assert.True(ActionHostPreviousStateKey.TryCreate(
                previousKey,
                out previousStateKey));
        }
        Assert.True(ActionHostInputs.TryCreate(
            launch.Inputs.GitHubToken,
            launch.Inputs.ProviderApiKey,
            stateKey,
            previousStateKey,
            launch.Inputs.ConfigPath,
            pullRequestNumber: null,
            ActionHostStateMode.Auto,
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
            out var result));
        return result!;
    }

    private static ProjectChatMessage User(string text) =>
        new("user", [new ProjectTextContent(text)]);

    public enum ExpiryCrashCut
    {
        AfterIntentUpload,
        AfterTargetDelete,
        AfterSuccessorUpload,
        DuringSuccessorCleanup,
    }

    private sealed record AcceptedSeed(
        OpaqueStoreName CandidateName,
        OpaqueStoreName AcceptanceName,
        OpaqueStoreName ExpiryName,
        OpaqueStoreName LineageName);

    private sealed record AcceptedGenerationSeed(
        string LogicalGenerationIdentity,
        string ReceiptIdentity);

    private sealed class EndToEndDependencies(IRestrictedStateStore store) :
        IAcceptedStateProductionDependencies
    {
        public IRestrictedStateStore CreateArtifactStore(
            ActionHostLaunchContract launch) => store;

        public IActionHostGitObjectTransport CreateAncestryTransport(
            ActionHostGitHubToken token) => new NoCallTransport();
    }

    private sealed class InterruptedExpiryStore(
        IRestrictedStateStore inner,
        AcceptedSeed seed,
        ExpiryCrashCut crashCut) : IRestrictedStateStore
    {
        private bool interrupted;
        private bool successorUploaded;

        internal int ExpiryIntentUploads { get; private set; }
        internal int SuccessorUploads { get; private set; }

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
            var result = await inner.UploadImmutableAsync(
                request,
                cancellationToken);
            if (request.Name == seed.ExpiryName)
            {
                ExpiryIntentUploads++;
                InterruptAfterCommit(ExpiryCrashCut.AfterIntentUpload);
            }
            else if (request.Name == seed.LineageName)
            {
                SuccessorUploads++;
                successorUploaded = true;
                InterruptAfterCommit(ExpiryCrashCut.AfterSuccessorUpload);
            }

            return result;
        }

        public Task<OpaqueStoreReadBackResult> ReadBackExactAsync(
            OpaqueStoreReadBackRequest request,
            CancellationToken cancellationToken) =>
            inner.ReadBackExactAsync(request, cancellationToken);

        public async Task<OpaqueStoreDeleteResult> DeleteExactAsync(
            OpaqueStoreDeleteRequest request,
            CancellationToken cancellationToken)
        {
            var result = await inner.DeleteExactAsync(
                request,
                cancellationToken);
            if (request.Expected.Reference.Name == seed.CandidateName ||
                request.Expected.Reference.Name == seed.AcceptanceName)
            {
                InterruptAfterCommit(ExpiryCrashCut.AfterTargetDelete);
            }
            else if (successorUploaded &&
                request.Expected.Reference.Name == seed.ExpiryName)
            {
                InterruptAfterCommit(
                    ExpiryCrashCut.DuringSuccessorCleanup);
            }

            return result;
        }

        private void InterruptAfterCommit(ExpiryCrashCut point)
        {
            if (!interrupted && crashCut == point)
            {
                interrupted = true;
                throw new IOException($"Simulated crash at {point}.");
            }
        }
    }

    private sealed class NoCallTransport : IActionHostGitObjectTransport
    {
        public Task<ActionHostGitObjectResult<ActionHostGitCommitObject>>
            GetCommitObjectAsync(
                string repositoryName,
                string commitSha,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Same-head E2E read Git.");

        public Task<ActionHostGitObjectResult<ActionHostGitTreeObject>>
            GetTreeObjectAsync(
                string repositoryName,
                string treeSha,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Same-head E2E read Git.");

        public Task<ActionHostGitObjectResult<ActionHostGitBlobObject>>
            GetBlobObjectAsync(
                string repositoryName,
                string blobSha,
                ActionHostGitBlobReadBudget budget,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Same-head E2E read Git.");

        public void Dispose() { }
    }

    private sealed class LocatorStateResolver(
        AuthorizedStateAccess stateAuthority,
        AuthorizedLocatorAccess locatorAuthority,
        LocatorContext locator) : IRestrictedStateKeyResolver
    {
        public bool TryGetCurrentWriteKey(
            AuthorizedStateAccess access,
            out RestrictedStateKey? key)
        {
            key = null;
            if (!ReferenceEquals(access, stateAuthority))
            {
                return false;
            }

            Span<byte> material = stackalloc byte[RestrictedStateFormat.KeyBytes];
            try
            {
                if (!locator.TryCopyCurrentStateKey(
                        locatorAuthority,
                        material,
                        out var keyId))
                {
                    return false;
                }

                key = new RestrictedStateKey(keyId, material);
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material);
            }
        }

        public bool TryGetApprovedReadKey(
            AuthorizedStateAccess access,
            string keyId,
            long expiresAtUnixSeconds,
            out RestrictedStateKey? key)
        {
            key = null;
            return false;
        }
    }
}
