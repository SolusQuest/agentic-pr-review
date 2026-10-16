using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Solus;
using SolusAgent.Api.Context;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;

try
{
    foreach (var key in new[] { "GITHUB_TOKEN", "GH_TOKEN", "NuGetPackageSourceCredentials_solus", "NUGET_AUTH_TOKEN" })
        Check(Environment.GetEnvironmentVariable(key) is null);
    var provider = new NoDispatchProvider();
    var hooks = new NoDispatchHooks();
    var config = Configuration(provider, hooks);
    var agent = SolusRuntimeConstruction.Create(config, Cr1DevelopmentProfile.Id);
    Check(agent is IContextCandidateAgent);
    var options = SolusRuntimeConstruction.Options();
    Check(options.MaximumAttempts == 64 && options.MaximumRecords == 64 &&
        options.MaximumRetainedBytes == 196_608 && options.RequireContinuation &&
        options.SettlementGrace == TimeSpan.FromSeconds(1));
    var negatives = 0;
    Rejected(() => SolusRuntimeConstruction.Create(config, "unknown"));
    Rejected(() => SolusRuntimeConstruction.Create(config, Cr1DevelopmentProfile.Id, 64));
    Rejected(() => SolusRuntimeConstruction.Create(Configuration(provider, null), Cr1DevelopmentProfile.Id));
    var unsupported = new NoDispatchProvider(false);
    Rejected(() => SolusRuntimeConstruction.Create(Configuration(unsupported, hooks), Cr1DevelopmentProfile.Id));
    Rejected(() => new RuntimeOptions(maximumAttempts: 65));
    Rejected(() => new RuntimeOptions(maximumRecords: 65));
    Rejected(() => new RuntimeOptions(maximumRetainedBytes: 196_609));
    Rejected(() => SolusRuntimeConstruction.Create(Configuration(provider, hooks, 65_537), Cr1DevelopmentProfile.Id));
    Check(provider.Dispatches == 0 && unsupported.Dispatches == 0 && hooks.Entries == 0 && negatives == 8);
    Check(typeof(SolusAgent.Api.Execution.IAgent).Assembly.GetName().Name == "SolusAgent.Api");
    Check(typeof(SolusAgent.Tools.Api.ToolSchema).Assembly.GetName().Name == "SolusAgent.Tools.Api");
    Check(typeof(RuntimeConfiguration).Assembly.GetName().Name == "SolusAgent.Runtime.Api");
    Check(typeof(RuntimeAgentFactory).Assembly.GetName().Name == "SolusAgent.Runtime");
    Check(typeof(SolusAgent.Providers.DeepSeek.DeepSeekProvider).Assembly.GetName().Name == "SolusAgent.Providers.DeepSeek");
    Console.WriteLine("APR_SOLUS_STARTUP_PROOF constructed=true dispatches=0 negative_checks=8 credential_free=true");
    return 0;

    void Rejected(Action operation)
    {
        try { operation(); }
        catch (ArgumentException) { negatives++; return; }
        throw new InvalidOperationException();
    }
}
catch
{
    Console.Error.WriteLine("APR_SOLUS_STARTUP_FAILED");
    return 1;
}

static void Check(bool value)
{
    if (!value) throw new InvalidOperationException();
}

static RuntimeConfiguration Configuration(NoDispatchProvider provider, NoDispatchHooks? hooks,
    int requestBytes = Cr1DevelopmentProfile.RequestPayloadBytes) => new(provider, [], hooks,
        new(maximumInputs: Cr1DevelopmentProfile.ProviderInputs,
            maximumTools: Cr1DevelopmentProfile.NonterminalDefinitions,
            maximumToolCalls: Cr1DevelopmentProfile.ToolCallsPerBatch,
            maximumRequestBytes: requestBytes,
            maximumResponseBytes: Cr1DevelopmentProfile.ResponsePayloadBytes,
            maximumContinuationBytes: Cr1DevelopmentProfile.ContinuationBytes));

sealed class NoDispatchProvider(bool continuation = true) : IModelProvider
{
    public int Dispatches { get; private set; }
    public ProviderScope Scope { get; } = new("apr-construction", "synthetic");
    public ProviderCapabilities Capabilities => ProviderCapabilities.ToolCalls | ProviderCapabilities.UsageReporting |
        (continuation ? ProviderCapabilities.Continuation : ProviderCapabilities.None);
    public ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken cancellationToken = default)
    {
        Dispatches++;
        throw new InvalidOperationException();
    }
}

sealed class NoDispatchHooks : IRuntimeExposureHooks
{
    public int Entries { get; private set; }
    public ValueTask<ExposureAcknowledgement?> BeforeDispatchAsync(RuntimeExposure exposure, CancellationToken cancellationToken)
    {
        Entries++;
        throw new InvalidOperationException();
    }
    public ValueTask<SettlementAcknowledgement?> AfterAttemptAsync(RuntimeSettlement settlement, CancellationToken cancellationToken)
    {
        Entries++;
        throw new InvalidOperationException();
    }
}
