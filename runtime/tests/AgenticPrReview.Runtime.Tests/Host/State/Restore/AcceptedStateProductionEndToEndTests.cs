using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Policy;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.Action.Policy;
using AgenticPrReview.Runtime.Tests.Host.State.Lineage;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;

namespace AgenticPrReview.Runtime.Tests.Host.State.Restore;

public sealed class AcceptedStateProductionEndToEndTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionEntryRestoresRealEncryptedAcceptedSession(
        bool rotateCurrentKey)
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
            await SeedAcceptedGenerationAsync(
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

        var restored = await RestrictedStateService
            .RestoreAuthorizedArtifactStateAsync(
                restoreRequest,
                CancellationToken.None);

        Assert.True(restored.Succeeded, restored.Code);
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

    private static async Task SeedAcceptedGenerationAsync(
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
            var locatorResult = await new LocatorRootService(
                    store,
                    keyRing!,
                    time)
                .ResolveAsync(
                    locatorAccess!,
                    requiredPlatformExpiry,
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
                Assert.NotNull(candidateName);
                Assert.NotNull(acceptanceName);

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
                var document = session.Artifact.Document;
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
                    session.Artifact.Plaintext,
                    stateResolver,
                    out var stateEnvelope,
                    out var stateCode), stateCode);
                Assert.NotNull(stateEnvelope);

                var publicationScope = new R4PublicationScopeV1(
                    (ulong)launch.RepositoryId,
                    (ulong)launch.RepositoryId,
                    invocation.WorkflowPath,
                    launch.WorkflowRef,
                    (ulong)invocation.PullRequest.Number,
                    policy.PolicySha256,
                    AuthorizedAcceptedStateComposer.PayloadBuildIdentity(
                        policy));
                _ = AcceptedStateTestData.Publication(
                    out var publicationBytes,
                    session.Identity,
                    policy.BuildDiscriminator,
                    publicationScope,
                    launch.RepositoryId,
                    launch.RepositoryName,
                    invocation.PullRequest.Number,
                    policy.PolicySha256,
                    policy.PayloadSha256);
                var generation = new StateGenerationRecordV1(
                    ImmutableArray.CreateRange(stateEnvelope!),
                    RestrictedStateEnvelope.EnvelopeSha256(stateEnvelope!),
                    session.Artifact.SessionSha256,
                    document.ProducerBaseSha,
                    document.ProducerHeadSha,
                    document.Generation,
                    document.PredecessorStateSha256,
                    PreviousLogicalGenerationIdentity: null,
                    now,
                    logicalExpiry,
                    ImmutableArray.CreateRange(publicationBytes),
                    AcceptedStateRecordValidation.Sha256(publicationBytes),
                    policy.PolicySha256,
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

                var receipt = AcceptedStateTestData.Receipt(
                    logicalIdentity,
                    candidateHeader!.ObjectIdentity,
                    out _,
                    acceptedAtUnixSeconds: now,
                    identity: session.Identity,
                    buildDiscriminator: policy.BuildDiscriminator,
                    scope: publicationScope,
                    repositoryId: launch.RepositoryId,
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

    private sealed class EndToEndDependencies(ScriptedLocatorStore store) :
        IAcceptedStateProductionDependencies
    {
        public IRestrictedStateStore CreateArtifactStore(
            ActionHostLaunchContract launch) => store;

        public IActionHostGitObjectTransport CreateAncestryTransport(
            ActionHostGitHubToken token) => new NoCallTransport();
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
