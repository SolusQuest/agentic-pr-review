using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.Tests.Execution.DeepSeek;

public sealed class DeepSeekProviderRetirementTests
{
    [Fact]
    public void ProductAssemblyContainsNoRetiredProviderImplementation()
    {
        var assembly = typeof(RuntimeApplication).Assembly;
        foreach (var name in new[] { "DeepSeekChatBackend", "DeepSeekRequestWriter", "DeepSeekResponseParser",
            "DeepSeekTransport", "IDeepSeekTransport", "DeepSeekContextAdmission", "DeepSeekUsageReader",
            "DeepSeekTransportResult", "DeepSeekTransportPolicy" })
            Assert.Null(assembly.GetType("AgenticPrReview.Runtime.Execution.DeepSeek." + name, false));
        Assert.Null(assembly.GetType("AgenticPrReview.Runtime.ActionHost.ActionHostDeepSeekProviderRunnerFactory", false));
        Assert.NotNull(assembly.GetType(typeof(DeepSeekCredential).FullName!, true));
        // This exact historical identity remains part of current direct admission.
        Assert.Equal("b7a3cc62596733a9b2c1747fda5fb862905c15f7946089a2537ccf6f5b7972bc", DeepSeekAdapterContext.Adapter);
    }
}
