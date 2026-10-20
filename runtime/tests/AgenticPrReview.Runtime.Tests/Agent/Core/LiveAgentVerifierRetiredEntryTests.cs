
namespace AgenticPrReview.Runtime.Tests.Agent.Core;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class LiveAgentVerifierRetiredEntryTests
{
    [Fact]
    public async Task EveryRetiredExecutionVerbRejectsBeforeMaterializationOrCredentialCapture()
    {
        string[] verbs =
        [
            "must-find",
            "must-not-find",
            "continuation-seed",
            "continuation-restore",
            "trusted-must-find",
            "trusted-must-not-find",
            "trusted-continuation-seed",
            "trusted-continuation-restore",
            "canary-routing",
            "negative-outer-authorization-denied",
            "negative-inner-authorization-denied",
            "negative-script-failure",
            "negative-script-invalid-response",
            "negative-tool-arguments-invalid",
            "negative-terminal-ungrounded",
            "negative-transition-from-head-invalid",
            "negative-lineage-authority-tampered",
            "negative-quality-failed-after-commit",
            "negative-public-result-canary",
            "historical-direct",
            "live-supervise",
            "negative-replacement-write-failed",
        ];
        const string providerName = "AGENTIC_REVIEW_DEEPSEEK_API_KEY";
        const string stateName = "AGENTIC_REVIEW_R3_STATE_KEY_B64";
        var providerBefore = Environment.GetEnvironmentVariable(providerName);
        var stateBefore = Environment.GetEnvironmentVariable(stateName);
        var outputBefore = Console.Out;
        var errorBefore = Console.Error;
        var root = Path.Join(Path.GetTempPath(), "apr-r3-retired-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(providerName, "retired-provider-synthetic");
            Environment.SetEnvironmentVariable(stateName, "retired-state-synthetic");
            foreach (var verb in verbs)
            {
                using var output = new StringWriter();
                using var error = new StringWriter();
                Console.SetOut(output);
                Console.SetError(error);
                Assert.Equal(2, await AgenticPrReview.Runtime.LiveAgentVerifierFixture.Program.Main([verb, "--root", root,
                    "--corpus", Path.Join(root, "missing-corpus.json")]));
                Assert.Equal(string.Empty, output.ToString());
                Assert.Equal("APR_R3_MANAGED_REQUIRED" + Environment.NewLine, error.ToString());
                Assert.Equal("retired-provider-synthetic", Environment.GetEnvironmentVariable(providerName));
                Assert.Equal("retired-state-synthetic", Environment.GetEnvironmentVariable(stateName));
                Assert.False(Directory.Exists(root));
            }
        }
        finally
        {
            Console.SetOut(outputBefore);
            Console.SetError(errorBefore);
            Environment.SetEnvironmentVariable(providerName, providerBefore);
            Environment.SetEnvironmentVariable(stateName, stateBefore);
        }
    }
}
