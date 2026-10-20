namespace AgenticPrReview.Runtime.Tests.Application;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class LiveAgentSecretSourceTests
{
    private const string StateKey =
        "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private const string ProviderSecret =
        "r3-provider-secret-must-remain-header-only";

    [Theory]
    [InlineData(null, StateKey, true)]
    [InlineData(ProviderSecret, "not-canonical-base64", false)]
    public void OriginalNegativeInputsSurviveCaptureAndBothEnvironmentSlotsAreCleared(
        string? provider, string stateKey, bool validKey)
    {
        // These are the original CLI vectors. This component proof covers
        // capture/clearing and key admission, not SDK execution or acceptance.
        var previousProvider = Environment.GetEnvironmentVariable(
            R3LiveAgentEnvironmentSecretSource.ProviderVariable);
        var previousKey = Environment.GetEnvironmentVariable(
            R3LiveAgentEnvironmentSecretSource.StateKeyVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                R3LiveAgentEnvironmentSecretSource.ProviderVariable, provider);
            Environment.SetEnvironmentVariable(
                R3LiveAgentEnvironmentSecretSource.StateKeyVariable, stateKey);

            var captured = new R3LiveAgentEnvironmentSecretSource().TakeAndClear();

            Assert.Equal(provider, captured.ProviderCredential);
            Assert.Equal(stateKey, captured.StateKeyBase64);
            Assert.Null(Environment.GetEnvironmentVariable(
                R3LiveAgentEnvironmentSecretSource.ProviderVariable));
            Assert.Null(Environment.GetEnvironmentVariable(
                R3LiveAgentEnvironmentSecretSource.StateKeyVariable));
            var admitted = R3LiveAgentStateKeyResolver.TryCreate(captured.StateKeyBase64, out var keys);
            using (keys)
            {
                Assert.Equal(validKey, admitted);
                Assert.Equal(validKey, keys is not null);
            }
            var second = new R3LiveAgentEnvironmentSecretSource().TakeAndClear();
            Assert.Null(second.ProviderCredential);
            Assert.Null(second.StateKeyBase64);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                R3LiveAgentEnvironmentSecretSource.ProviderVariable, previousProvider);
            Environment.SetEnvironmentVariable(
                R3LiveAgentEnvironmentSecretSource.StateKeyVariable, previousKey);
        }
    }
}
