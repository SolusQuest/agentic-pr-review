using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.AgentLoopAotFixture;

internal static class ProofOrchestrator
{
    internal static async Task<int> RunAsync(ProofCommand command)
    {
        var trusted = ProofScenario.Trusted();
        if (!AgentStableRequestMaterializer.TryMaterialize(
                trusted,
                priorSessionSha256: null,
                out var materialized))
        {
            return 1;
        }

        var identity = ProofScenario.BootstrapIdentity();
        var context = ProofScenario.User(
            "Review the synthetic snapshot. Use read_file and each R3 observation tool before finishing.");
        var run = new AgentRunRequest(
            identity,
            materialized!.StablePlan,
            ProofScenario.SessionId,
            [.. materialized.ControlMessages, context]);
        var canaries = ProofCanaryValues.Create("issue88-proof");
        var providerCanary = canaries.Provider;
        await using var server = StrictLoopbackServer.Start(
            providerCanary,
            [
                ProviderScripts.BootstrapR3Observations,
                ProviderScripts.BootstrapFinish,
            ]);
        using var backend = new LoopbackProviderBackend(
            server.Endpoint,
            providerCanary);
        var chat = new MinimalChatClient(backend);
        var diffSource = ProofScenario.BootstrapDiffSource(identity);
        var snapshot = new ReviewedSnapshot(
            identity,
            ProofPaths.RepositoryRoot(command),
            [ProofScenario.ReviewedPath],
            [ProofScenario.BootstrapChangedFile(diffSource)],
            [diffSource]);
        var loop = new AgentLoop(
            chat,
            new SnapshotToolExecutor(
                snapshot,
                new VerifiedReviewedFileAccess()),
            new SyntheticTimeProvider(ProofScenario.Now));
        var outcome = await loop.RunAsync(run, CancellationToken.None);
        if (!outcome.CompletedSessionEligible ||
            outcome.Review is null ||
            server.Captures.Count != 2)
        {
            return 1;
        }

        if (outcome.Events.OfType<AgentToolResultEvent>().Count() != 4 ||
            !outcome.Events.OfType<AgentToolCallEvent>().Any(call => call.Name == AgentToolRegistry.FinishReviewName) ||
            Directory.Exists(ProofPaths.StateRoot(command))) return 1;
        ProofFiles.WriteNew(command.Output, "APR_AGENT_GENERIC_EXECUTION_OK\n"u8.ToArray());
        Console.WriteLine("APR_AGENT_GENERIC_EXECUTION_OK");
        return 0;
    }
}
