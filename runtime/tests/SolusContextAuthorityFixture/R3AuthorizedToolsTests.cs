using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Solus;
using SolusAgent.Runtime.Api.Providers;

static class R3AuthorizedToolsTests
{
    internal static async Task Run(string parent)
    {
        Proof.Stage = "authorized-r3-construction";
        var root = Path.Join(parent, "authorized-r3");
        Directory.CreateDirectory(root);
        await using var fixture = await Fixture.OpenAsync(root, default);
        using var targets = SolusFreshHostContext.TryCreate(fixture.Request, fixture.Snapshot, fixture.Exclusion);
        Proof.Require(targets is not null, "authorized_r3_targets");
        var provider = new AuthorizedProvider(targets!.Policy.ProviderId, targets.Policy.ModelId);
        var executor = new AuthorizedExecutor(new SnapshotToolExecutor(targets.Snapshot, new VerifiedReviewedFileAccess()));
        var tools = new SolusSnapshotTools(Guid.NewGuid(), targets.Snapshot, executor);
        try
        {
            using var runtime = SolusRuntimeConstruction.CreateForAuthorizedReview(targets,
                context => new(provider, tools.Registrations, new Hooks()));
        }
        catch (ProviderContractException error) when (error.Error == ProviderError.InvalidInput)
        {
            Proof.Require(provider.Calls == 0 && executor.Calls == 0 && tools.CurrentObservations.Length == 0,
                "authorized_r3_construction_zero_effects");
            throw new ProofFailure("authorized_r3_missing_policy");
        }
    }
}

sealed class AuthorizedProvider(string provider, string model) : ModelProvider(new(provider, model),
    ProviderCapabilities.Continuation | ProviderCapabilities.UsageReporting | ProviderCapabilities.ToolCalls |
    ProviderCapabilities.ToolArgumentCorrection)
{
    internal int Calls;
    protected override ValueTask<ProviderResponse> ExchangeCoreAsync(ProviderRequest request,
        ProviderObservation observation, CancellationToken token)
    { Calls++; throw new InvalidOperationException(); }
}

sealed class AuthorizedExecutor(IAgentToolExecutor inner) : IAgentToolExecutor
{
    internal int Calls;
    public string? Preflight(PreparedAgentToolCall call) => inner.Preflight(call);
    public async ValueTask<AgentToolExecution> ExecuteAsync(PreparedAgentToolCall call, CancellationToken token)
    { Calls++; return await inner.ExecuteAsync(call, token); }
}
