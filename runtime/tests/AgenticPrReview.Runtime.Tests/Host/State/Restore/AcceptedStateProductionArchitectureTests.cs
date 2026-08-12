using System.Reflection;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Policy;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.Action.Policy;

namespace AgenticPrReview.Runtime.Tests.Host.State.Restore;

public sealed class AcceptedStateProductionArchitectureTests
{
    [Fact]
    public async Task AuthorizationFailurePrecedesEveryExternalDependency()
    {
        var dependencies = new ThrowingDependencies();
        var request = new ArtifactStateRestoreRequest(
            Launch: null!,
            Invocation: null!,
            TrustedPolicy: null!,
            new ProjectChatMessage(
                "user",
                [new ProjectTextContent("review")]),
            EmptyContinuationCodec.Instance,
            dependencies);

        var result = await RestrictedStateService
            .RestoreAuthorizedArtifactStateAsync(
                request,
                CancellationToken.None);

        Assert.Equal(AcceptedStateCodes.AccessDenied, result.Code);
        Assert.Equal(0, dependencies.StoreCreates);
        Assert.Equal(0, dependencies.TransportCreates);
    }

    [Fact]
    public void ProductionCapabilitiesHaveNoPublicIssuerOrSecretSurface()
    {
        Assert.Empty(typeof(AcceptedStateProductionAuthorization)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        Assert.DoesNotContain(
            typeof(AcceptedStateProductionAuthorization).GetProperties(
                BindingFlags.Instance | BindingFlags.Public),
            property => property.PropertyType ==
                    typeof(ActionHostGitHubToken) ||
                property.PropertyType == typeof(LocatorContext) ||
                property.Name.Contains("Key", StringComparison.Ordinal));
        Assert.Empty(typeof(AuthorizedAcceptedStateRestoreContext)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public));
    }

    [Fact]
    public async Task WorkflowRunAutoUsesAuthorizedPrWhenInputIsAbsent()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowRun);
        Assert.True(ActionHostStateKey.TryCreate(
            "state-key-material",
            out var stateKey));
        Assert.True(ActionHostInputs.TryCreate(
            scenario.Launch.Inputs.GitHubToken,
            scenario.Launch.Inputs.ProviderApiKey,
            stateKey,
            scenario.Launch.Inputs.PreviousStateKey,
            scenario.Launch.Inputs.ConfigPath,
            pullRequestNumber: null,
            ActionHostStateMode.Auto,
            out var inputs));
        Assert.True(ActionHostLaunchContract.TryCreate(
            inputs,
            scenario.Launch.EventJsonPath,
            scenario.Launch.EventJsonSha256,
            scenario.Launch.RepositoryName,
            scenario.Launch.RepositoryId,
            scenario.Launch.RunId,
            scenario.Launch.RunAttempt,
            scenario.Launch.WorkflowPath,
            scenario.Launch.WorkflowRef,
            scenario.Launch.WorkflowSha,
            scenario.Launch.ActionSourceSha,
            scenario.Launch.PayloadSha256,
            scenario.Launch.BuildDiscriminator,
            scenario.Launch.Cancellation,
            scenario.Launch.ArtifactBridgeEndpoint,
            out var launch));
        var authorized = await scenario.CreateAuthorizer().AuthorizeAsync(
            launch!,
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
                Encoding.UTF8.GetBytes("instructions")),
            CancellationToken.None);
        Assert.True(materialized.Succeeded);
        var request = new ArtifactStateRestoreRequest(
            launch!,
            invocation,
            materialized.Policy!,
            new ProjectChatMessage(
                "user",
                [new ProjectTextContent("review")]),
            EmptyContinuationCodec.Instance);

        Assert.True(AcceptedStateProductionAuthorization.TryAuthorize(
            request,
            out var production));
        Assert.NotNull(production);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("attempt")]
    [InlineData("workflow-ref")]
    [InlineData("config")]
    [InlineData("cancellation")]
    [InlineData("credential-input")]
    public async Task CrossPairedLaunchFailsBeforeEveryExternalDependency(
        string mutation)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowRun);
        var exactLaunch = CloneLaunch(scenario.Launch, "state-key");
        var authorized = await scenario.CreateAuthorizer().AuthorizeAsync(
            exactLaunch,
            CancellationToken.None);
        var invocation = Assert.IsType<
            ActionHostAuthorizer.AuthorizedInvocation>(authorized.Invocation);
        Assert.True(ActionHostTrustedPolicyRequest.TryBind(
            exactLaunch,
            invocation,
            out var policyRequest,
            out var bindFailure));
        Assert.Equal(ActionHostTrustedPolicyFailure.None, bindFailure);
        var materialized = await ActionHostTrustedPolicy.MaterializeAsync(
            policyRequest!,
            ActionHostTrustedPolicyTests.ScriptedObjectTransport.Valid(
                ActionHostTrustedPolicyTests.Config("sticky", null),
                Encoding.UTF8.GetBytes("instructions")),
            CancellationToken.None);
        Assert.True(materialized.Succeeded);
        var dependencies = new ThrowingDependencies();
        var request = new ArtifactStateRestoreRequest(
            CloneLaunch(exactLaunch, mutation),
            invocation,
            materialized.Policy!,
            new ProjectChatMessage(
                "user",
                [new ProjectTextContent("review")]),
            EmptyContinuationCodec.Instance,
            dependencies);

        var result = await RestrictedStateService
            .RestoreAuthorizedArtifactStateAsync(
                request,
                CancellationToken.None);

        Assert.Equal(AcceptedStateCodes.AccessDenied, result.Code);
        Assert.Equal(0, dependencies.StoreCreates);
        Assert.Equal(0, dependencies.TransportCreates);
    }

    [Fact]
    public async Task MalformedAuthorizedStateKeyFailsBeforeStoreCreation()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowRun);
        var launch = CloneLaunch(scenario.Launch, "state-key");
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
                Encoding.UTF8.GetBytes("instructions")),
            CancellationToken.None);
        var dependencies = new ThrowingDependencies();

        var result = await RestrictedStateService
            .RestoreAuthorizedArtifactStateAsync(
            new ArtifactStateRestoreRequest(
                launch,
                invocation,
                materialized.Policy!,
                new ProjectChatMessage(
                    "user",
                    [new ProjectTextContent("review")]),
                EmptyContinuationCodec.Instance,
                dependencies),
            CancellationToken.None);

        Assert.Equal(AcceptedStateCodes.KeyUnavailable, result.Code);
        Assert.Equal(0, dependencies.StoreCreates);
        Assert.Equal(0, dependencies.TransportCreates);
    }

    private static ActionHostLaunchContract CloneLaunch(
        ActionHostLaunchContract launch,
        string mutation)
    {
        var stateKey = launch.Inputs.StateKey;
        var configPath = launch.Inputs.ConfigPath;
        if (mutation is "state-key" or "credential-input")
        {
            Assert.True(ActionHostStateKey.TryCreate(
                mutation == "state-key"
                    ? "state-key-material"
                    : "different-state-key-material",
                out stateKey));
        }

        if (mutation == "config")
        {
            configPath = ".github/other-policy.json";
        }

        Assert.True(ActionHostInputs.TryCreate(
            launch.Inputs.GitHubToken,
            launch.Inputs.ProviderApiKey,
            stateKey,
            launch.Inputs.PreviousStateKey,
            configPath,
            launch.Inputs.PullRequestNumber,
            launch.Inputs.StateMode,
            out var inputs));
        Assert.True(ActionHostLaunchContract.TryCreate(
            inputs,
            launch.EventJsonPath,
            launch.EventJsonSha256,
            launch.RepositoryName,
            launch.RepositoryId,
            mutation == "run" ? launch.RunId + 1 : launch.RunId,
            mutation == "attempt" ? launch.RunAttempt + 1 : launch.RunAttempt,
            launch.WorkflowPath,
            mutation == "workflow-ref"
                ? "refs/heads/other"
                : launch.WorkflowRef,
            launch.WorkflowSha,
            launch.ActionSourceSha,
            launch.PayloadSha256,
            launch.BuildDiscriminator,
            mutation == "cancellation"
                ? ActionHostCancellationState.Requested
                : launch.Cancellation,
            launch.ArtifactBridgeEndpoint,
            out var clone));
        return clone!;
    }

    private sealed class ThrowingDependencies
        : IAcceptedStateProductionDependencies
    {
        internal int StoreCreates { get; private set; }
        internal int TransportCreates { get; private set; }

        public IRestrictedStateStore CreateArtifactStore(
            ActionHostLaunchContract launch)
        {
            StoreCreates++;
            throw new InvalidOperationException(
                "Artifact store creation was reached.");
        }

        public IActionHostGitObjectTransport CreateAncestryTransport(
            ActionHostGitHubToken token)
        {
            TransportCreates++;
            throw new InvalidOperationException(
                "Git transport creation was reached.");
        }
    }

    private sealed class EmptyContinuationCodec : IAgentContinuationCodec
    {
        internal static EmptyContinuationCodec Instance { get; } = new();

        public string CodecId => "s5-test";
        public string CodecDiscriminator => "v1";

        public bool TryEncode(
            AgentContinuationCodecValue value,
            out AgentContinuationEncodedPayload? payload)
        {
            payload = null;
            return false;
        }

        public bool TryDecode(
            string encoding,
            ReadOnlySpan<byte> payload,
            out AgentContinuationCodecValue? value)
        {
            value = null;
            return false;
        }
    }
}
