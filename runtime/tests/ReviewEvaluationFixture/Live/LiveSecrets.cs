namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

// Provider-only credential ingress: read once, clear immediately, hand to the
// existing DeepSeek transport. The state-key variable is never touched.
internal interface ILiveSecretSource
{
    string? TakeProviderCredential();
}

internal sealed class LiveEnvironmentSecretSource : ILiveSecretSource
{
    internal const string ProviderVariable =
        AgenticPrReview.Runtime.R3LiveAgentEnvironmentSecretSource.ProviderVariable;

    public string? TakeProviderCredential()
    {
        var value = Environment.GetEnvironmentVariable(ProviderVariable);
        if (value is not null) Environment.SetEnvironmentVariable(ProviderVariable, null);
        return value;
    }
}
