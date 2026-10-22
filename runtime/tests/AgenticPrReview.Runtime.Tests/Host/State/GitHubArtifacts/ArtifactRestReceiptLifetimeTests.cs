using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;

namespace AgenticPrReview.Runtime.Tests.Host.State.GitHubArtifacts;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ArtifactRestReceiptLifetimeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EarlyHostCancellationReportsZeroArtifactUseWithoutCreatingACapability(bool protectedProof)
    {
        var original = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch).Launch;
        Assert.True(ActionHostLaunchContract.TryCreate(original.Inputs, original.EventJsonPath, original.EventJsonSha256,
            original.RepositoryName, original.RepositoryId, original.RunId, original.RunAttempt, original.WorkflowPath,
            original.WorkflowRef, original.WorkflowSha, original.ActionSourceSha, original.PayloadSha256,
            protectedProof ? "r4-w2" : original.BuildDiscriminator, ActionHostCancellationState.Requested,
            original.ArtifactBridgeEndpoint, out var launch));
        const string variable = "AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE";
        var previous = Environment.GetEnvironmentVariable(variable);
        var stderr = Console.Error;
        using var capture = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable(variable, "measurement");
            Console.SetError(capture);
            var completion = await new ActionHostComposition().RunAsync(launch!, default);
            Assert.Equal(ActionHostStatus.Cancelled, completion.Status);
            Assert.True(completion.Accounting.IsCompleteZero);
        }
        finally { Console.SetError(stderr); Environment.SetEnvironmentVariable(variable, previous); }
        var lines = capture.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!protectedProof) { Assert.Empty(lines); return; }
        const string prefix = "APR_R4_E2P_ARTIFACT_REST_BUDGET ";
        var line = Assert.Single(lines);
        Assert.StartsWith(prefix, line);
        using var document = JsonDocument.Parse(line[prefix.Length..]);
        Assert.Equal(0, document.RootElement.GetProperty("total_authenticated_api_requests").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("primary_rate_limit_requests").GetInt32());
        Assert.Equal(original.PayloadSha256, document.RootElement.GetProperty("payload_sha256").GetString());
    }
}
