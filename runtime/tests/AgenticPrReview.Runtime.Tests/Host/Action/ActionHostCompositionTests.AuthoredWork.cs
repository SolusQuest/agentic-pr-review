using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed partial class ActionHostCompositionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthoredHostWorkGroundsCurrentEvidenceWithoutProviderDispatch(bool withFinding)
    {
        var root = Directory.CreateTempSubdirectory("apr-authored-host-").FullName;
        try
        {
            File.WriteAllText(Path.Join(root, "file.txt"), "current reviewed line\n");
            var identity = new ReviewedIdentity("1", 1, new('a', 40), new('b', 40));
            var snapshot = new ReviewedSnapshot(identity, root, ["file.txt"]);
            var trusted = new AgentTrustedRequest("1", 1, "trusted-workflow", "fixed policy"u8.ToArray(),
                "test-build", DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model,
                DeepSeekAdapterContext.Adapter);
            Assert.True(AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var materialized));
            var run = new AgentRunRequest(identity, materialized!.StablePlan, "authored-host-work",
                [.. materialized.ControlMessages]);
            Assert.True(ActionHostProviderApiKey.TryCreate("synthetic-unused-key", out var key));
            var factory = new FullPathProviderFactory(withFinding);
            using var runner = factory.Create(new(trusted.ProviderId, trusted.ModelId, trusted.AdapterId),
                key!, snapshot, TimeProvider.System);

            var outcome = await runner.RunAsync(run, CancellationToken.None);

            Assert.True(outcome.CompletedSessionEligible);
            Assert.Equal(identity, outcome.ReviewedIdentity);
            Assert.Null(outcome.Continuation);
            Assert.Equal(withFinding ? 1 : 0, outcome.Review!.Findings.Length);
            if (withFinding)
            {
                var finding = Assert.Single(outcome.Review.Findings);
                var evidence = Assert.Single(finding.Evidence);
                Assert.Equal("file.txt", evidence.Path);
                Assert.Equal(1, evidence.StartLine);
                Assert.Equal(1, evidence.EndLine);
                Assert.Equal(64, evidence.ObservationId.Length);
            }
            var accounting = Assert.IsType<ProviderAccounting>(outcome.Accounting);
            Assert.Equal(withFinding ? 2 : 1, accounting.ModelCalls);
            Assert.Equal(0, accounting.ProviderAttempts);
            Assert.Equal(AccountingCompleteness.Unavailable, accounting.AttemptCompleteness);
            Assert.Equal(AccountingCompleteness.Partial, accounting.UsageCompleteness);
            // Aggregate exposes known lower bounds; completeness retains the
            // unknown cache partition rather than qualifying these as totals.
            Assert.Equal(0, accounting.CacheHitTokens);
            Assert.Equal(0, accounting.CacheMissTokens);
        }
        finally { Directory.Delete(root, true); }
    }
}
