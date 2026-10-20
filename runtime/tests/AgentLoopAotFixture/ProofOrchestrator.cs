using System.Text;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.AgentLoopAotFixture;

// Framework/AOT smoke for APR's actual snapshot and terminal boundaries. The
// four operations are fixed before execution; no provider or generic loop runs.
internal static class ProofOrchestrator
{
    internal static async Task<int> RunAsync(ProofCommand command)
    {
        if (!AgentStableRequestMaterializer.TryMaterialize(ProofScenario.Trusted(), null, out var materialized) ||
            materialized!.ControlMessages.Length == 0) return 1;
        var identity = ProofScenario.BootstrapIdentity();
        var diff = ProofScenario.BootstrapDiffSource(identity);
        var snapshot = new ReviewedSnapshot(identity, ProofPaths.RepositoryRoot(command),
            [ProofScenario.ReviewedPath], [ProofScenario.BootstrapChangedFile(diff)], [diff]);
        if (!AgentToolArguments.TryReadFileProvider("{\"path\":\"reviewed/fact.txt\"}", out var read) ||
            !AgentToolArguments.TryListFilesProvider("{}", out var list) ||
            !AgentToolArguments.TryListChangedFilesProvider("{}", out var changed) ||
            !AgentToolArguments.TryReadDiffProvider("{\"path\":\"reviewed/fact.txt\"}", out var readDiff)) return 1;
        PreparedAgentToolCall[] work = [new PreparedReadFileCall("read0", read!),
            new PreparedListFilesCall("list0", list!), new PreparedListChangedFilesCall("changed0", changed!),
            new PreparedReadDiffCall("diff0", readDiff!)];
        var executor = new SnapshotToolExecutor(snapshot, new VerifiedReviewedFileAccess());
        var observations = new List<AgentObservation>();
        var channels = new List<byte[]>();
        foreach (var call in work)
        {
            if (executor.Preflight(call) is not null) return 1;
            var result = await executor.ExecuteAsync(call, CancellationToken.None);
            if (!AgentToolResultAdmission.TryAdmit(call, identity, result, out _, out var observation)) return 1;
            observations.Add(observation);
            channels.Add(result.CanonicalResult!);
        }
        var terminal = AgentToolArguments.WriteFinishReview("Synthetic grounded review completed.",
            [new AgentFinding("high", "Synthetic snapshot fact", "The current snapshot contains the marker.",
                [new AgentEvidence(observations[0].ObservationId, ProofScenario.ReviewedPath, 1, 1),
                 new AgentEvidence(observations[3].ObservationId, ProofScenario.ReviewedPath, 1, 1)])]);
        if (!AgentToolArguments.TryFinishReviewProvider(Encoding.UTF8.GetString(terminal), out var finish) ||
            !TerminalReviewValidator.TryValidate(finish!, identity, observations, out var review) ||
            review!.Findings.Length != 1 || Directory.Exists(ProofPaths.StateRoot(command))) return 1;
        channels.Add(terminal);
        var canaries = ProofCanaryValues.Create("apr-domain-proof");
        if (!CanaryScanner.VerifyDomainOutput(canaries, channels) ||
            !new[] { "model", "durable", "transport", "diagnostic", "environment" }
                .All(CanaryScanner.RejectsRepresentativeLeak)) return 1;
        ProofFiles.WriteNew(command.Output, "APR_AGENT_DOMAIN_EXECUTION_OK\n"u8);
        Console.WriteLine("APR_AGENT_DOMAIN_EXECUTION_OK");
        return 0;
    }
}
