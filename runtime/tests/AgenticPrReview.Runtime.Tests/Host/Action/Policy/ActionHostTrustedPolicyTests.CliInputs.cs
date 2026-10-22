using System.Text;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.ActionHost.Policy;
using AgenticPrReview.Runtime.ActionHost.Serialization;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Policy;

public sealed partial class ActionHostTrustedPolicyTests
{
    [Theory]
    [InlineData("omitted")]
    [InlineData("empty")]
    [InlineData("explicit")]
    public async Task RawCliDefaultsSelectTheAuthorizedConfigurationRequest(string mode)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
        Assert.True(ActionHostJsonCodec.TryWriteLaunch(scenario.Launch, out var original));
        var root = JsonNode.Parse(original)!;
        var entries = new JsonArray(new JsonObject
        {
            ["name"] = "github-token", ["value"] = scenario.Launch.Inputs.GitHubToken!.ExportForPrivateLaunch(),
        });
        entries.Add(new JsonObject { ["name"] = "pr-number", ["value"] = ActionHostContractValidation.CanonicalDecimal(scenario.Launch.Inputs.PullRequestNumber!.Value) });
        if (mode != "omitted")
        {
            entries.Add(new JsonObject { ["name"] = "config-path", ["value"] = mode == "empty" ? "" : ActionHostInputs.DefaultConfigPath });
            entries.Add(new JsonObject { ["name"] = "state-mode", ["value"] = mode == "empty" ? "" : "auto" });
        }
        root["inputs"] = entries;
        Assert.True(ActionHostJsonCodec.TryReadLaunch(Encoding.UTF8.GetBytes(root.ToJsonString()), out var launch, out _));
        Assert.Equal(ActionHostInputs.DefaultConfigPath, launch!.Inputs.ConfigPath);
        Assert.Equal(ActionHostStateMode.Auto, launch.Inputs.StateMode);
        var authorization = await scenario.CreateAuthorizer().AuthorizeAsync(launch, default);
        Assert.NotNull(authorization.Invocation);
        Assert.True(ActionHostTrustedPolicyRequest.TryBind(launch, authorization.Invocation, out var request, out _));
        Assert.Equal(ActionHostInputs.DefaultConfigPath, request!.ConfigPath.Value);
        var transport = ScriptedObjectTransport.Valid(Config("sticky", inlineSeverity: null), Encoding.UTF8.GetBytes("Review."));
        var materialized = await ActionHostTrustedPolicy.MaterializeAsync(request, transport, default);
        Assert.True(materialized.Succeeded);
        Assert.Equal(ActionHostInputs.DefaultConfigPath, materialized.Policy!.ConfigPath);
    }
}
