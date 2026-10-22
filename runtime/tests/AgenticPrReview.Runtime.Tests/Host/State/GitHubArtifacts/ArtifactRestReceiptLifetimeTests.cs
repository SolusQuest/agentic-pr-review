using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Host.State.GitHubArtifacts;
using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;

namespace AgenticPrReview.Runtime.Tests.Host.State.GitHubArtifacts;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ArtifactRestReceiptLifetimeTests
{
    [Theory]
    [InlineData("measurement", 2304, 1, true)]
    [InlineData("final-bootstrap", 4096, 80, false)]
    [InlineData("final-continuation", 4096, 80, false)]
    [InlineData("final-stale", 4096, 80, false)]
    public void ProofCompositionPassesSelectedPolicyToTheActualProductionArtifactFactory(
        string selection, int maximum, int reserve, bool measurement)
    {
        Assert.True(TrustedProofRequestBudgetProfile.TrySelectProduction(_ => selection, out var profile));
        using var github = new TrustedProofGitHubRequestBudget();
        var dependencies = TrustedProofPayloadComposition.CreateProductionLike(github,
            TrustedProofPayloadRuntimePorts.Production, requestBudgetProfile: profile);
        var original = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch).Launch;
        Assert.True(ActionHostLaunchContract.TryCreate(original.Inputs, original.EventJsonPath, original.EventJsonSha256,
            original.RepositoryName, original.RepositoryId, original.RunId, original.RunAttempt, original.WorkflowPath,
            original.WorkflowRef, original.WorkflowSha, original.ActionSourceSha, original.PayloadSha256, "r4-w2",
            original.Cancellation, original.ArtifactBridgeEndpoint, out var launch));
        using var store = Assert.IsType<GitHubArtifactRestrictedStateStore>(dependencies.StateDependencies.CreateArtifactStore(launch!));
        const string prefix = "APR_R4_E2P_ARTIFACT_REST_BUDGET ";
        using var receipt = JsonDocument.Parse(store.Rest.Budget.Receipt(launch!)![prefix.Length..]);
        Assert.Equal(maximum, receipt.RootElement.GetProperty("maximum_total_authenticated_api_requests").GetInt32());
        Assert.Equal(reserve, receipt.RootElement.GetProperty("remaining_tail_reserve").GetInt32());
        Assert.Equal(measurement, receipt.RootElement.GetProperty("measurement_only").GetBoolean());
        Assert.Equal(0, store.Rest.Budget.Requests);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task EarlyHostCancellationUsesAdmittedProfileAndReportsZeroWithoutCreatingACapability(bool protectedProof, bool measurement)
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
            // A contradictory ambient value cannot replace the admitted profile.
            Environment.SetEnvironmentVariable(variable, "invalid");
            Console.SetError(capture);
            var completion = await new ActionHostComposition(ActionHostCompositionDependencies.Production(
                protectedProof ? ArtifactRestBudgetPolicy.Protected(measurement) : null)).RunAsync(launch!, default);
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
        Assert.Equal(measurement, document.RootElement.GetProperty("measurement_only").GetBoolean());
        Assert.Equal(measurement ? 2304 : 4096, document.RootElement.GetProperty("maximum_total_authenticated_api_requests").GetInt32());
    }

    [Fact]
    public void ProtectedStoreCannotInventAnAmbientProfileAndOrdinaryPolicyStaysUncapped()
    {
        Assert.Throws<ArtifactTransportException>(() => ArtifactRestBudgetPolicy.ForBuild("r4-w2"));
        var ordinary = ArtifactRestBudgetPolicy.ForBuild("r7-d0");
        Assert.Null(ordinary.MaximumRequests);
        Assert.Null(ordinary.MaximumPrimary);
        Assert.Equal(0, ordinary.Reserve);
    }
}
