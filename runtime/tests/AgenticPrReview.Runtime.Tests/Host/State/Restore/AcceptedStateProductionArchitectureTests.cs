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

        var result = await new AuthorizedAcceptedStateComposer()
            .RestoreAsync(request, CancellationToken.None);

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
        var authorized = await scenario.CreateAuthorizer().AuthorizeAsync(
            scenario.Launch,
            CancellationToken.None);
        var invocation = Assert.IsType<
            ActionHostAuthorizer.AuthorizedInvocation>(authorized.Invocation);
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
