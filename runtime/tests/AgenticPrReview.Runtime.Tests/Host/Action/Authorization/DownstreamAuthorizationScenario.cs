using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Authorization;

internal sealed record DownstreamAuthorizationScenario(ActionHostAuthorizationScenario Scenario, ActionHostLaunchContract Launch)
{
    internal static DownstreamAuthorizationScenario Create(bool automatic = true, bool workflowRun = false,
        bool reset = false, bool candidate = false, string? configPath = null, string? branch = null,
        string? eventStateMode = null, bool omitEventStateMode = false, string? workflowPath = null,
        byte[]? serializedEvent = null)
    {
        var route = workflowRun ? ActionHostAuthorizationRoute.WorkflowRun : ActionHostAuthorizationRoute.WorkflowDispatch;
        var scenario = ActionHostAuthorizationScenario.Valid(route,
            candidateSourcePullRequestNumber: candidate ? 999 : null,
            candidateExecutionPhase: candidate ? ActionHostCandidateExecutionPhase.Bootstrap : null,
            workflowBranch: branch);
        var original = scenario.Launch;
        var document = JsonNode.Parse(serializedEvent ?? scenario.EventBytes)!;
        if (!workflowRun && !omitEventStateMode) document["inputs"]!["state-mode"] = eventStateMode ?? (reset ? "reset" : "auto");
        var bytes = Encoding.UTF8.GetBytes(document.ToJsonString());
        scenario.EventReader.Bytes = bytes;
        Assert.True(ActionHostInputs.TryCreate(original.Inputs.GitHubToken, original.Inputs.ProviderApiKey,
            original.Inputs.StateKey, original.Inputs.PreviousStateKey, configPath ?? ActionHostInputs.DefaultConfigPath,
            original.Inputs.PullRequestNumber, reset ? ActionHostStateMode.Reset : ActionHostStateMode.Auto, out var inputs));
        var path = workflowPath ?? ActionHostDownstreamWorkflowContract.WorkflowPath;
        var source = Encoding.UTF8.GetBytes(ActionHostDownstreamWorkflowContract.Render(original.ActionSourceSha, automatic));
        scenario.Transport.CurrentRun = scenario.Transport.CurrentRun with { Path = path, Name = "Agentic PR Review" };
        scenario.Transport.Source = scenario.Transport.Source with { Path = path, Name = "agentic-pr-review.yml",
            Bytes = source, BlobSha = BlobSha(source) };
        Assert.True(ActionHostLaunchContract.TryCreate(inputs, original.EventJsonPath,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), original.RepositoryName, original.RepositoryId,
            original.RunId, original.RunAttempt, path,
            $"{original.RepositoryName}/{path}@refs/heads/{branch ?? "main"}", original.WorkflowSha,
            original.ActionSourceSha, original.PayloadSha256, original.BuildDiscriminator, original.Cancellation,
            original.ArtifactBridgeEndpoint, out var launch));
        return new(scenario, launch!);
    }

    internal static string BlobSha(byte[] bytes) => Convert.ToHexStringLower(SHA1.HashData(
        Encoding.ASCII.GetBytes($"blob {bytes.Length}\0").Concat(bytes).ToArray()));

    internal void SetSource(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Scenario.Transport.Source = Scenario.Transport.Source with { Bytes = bytes, BlobSha = BlobSha(bytes) };
    }

    internal Task<ActionHostAuthorizationResult> Authorize() => new ActionHostAuthorizer(
        Scenario.EventReader, Scenario.Factory, ActionHostAuthorizationPolicy.TrustedProof)
        .AuthorizeAsync(Launch, default);
}
