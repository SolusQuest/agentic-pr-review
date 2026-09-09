using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

// R2 assertions are separate from the generic executor and from mutable fixture scripts/expected answers.
internal static class ReplayCoverage
{
    internal const string Fact = "R5_PRIOR_ONLY_ORCHID_246";
    internal const string Current = "Current snapshot contains no prior fact.";
    internal static readonly string[] Cases = ["replay-seed", "replay-same", "replay-ahead"];
    internal static readonly string[] Reasoning = [" seed reasoning \nα ", " seed finish ", " same reasoning ", " same finish ", " ahead reasoning ", " ahead finish "];

    internal static bool Verify(AdmittedReplayFixture fixture, int phase, ReplayChildReply reply)
    {
        if (fixture.Runs.Length != 3 || !fixture.Runs.Select(run => run.Input.Id).SequenceEqual(Cases) ||
            !fixture.Runs.Select(run => run.Input.CaseId).SequenceEqual(Cases) ||
            reply.Requests.Length != 2 || reply.ModelCalls != 2 || reply.ToolCalls != (phase == 0 ? 2 : 1) ||
            reply.Plaintext is null || !AgentSessionCodec.TryParse(reply.Plaintext, out var artifact, out _) || artifact is null) return false;
        for (var index = 0; index < fixture.Runs.Length; index++)
        {
            var input = fixture.Runs[index];
            var identity = input.Input.ReviewedIdentity;
            var trusted = input.CreateTrustedRequest(ReplayState.Build);
            if (identity.RepositoryId != "246" || identity.ReviewTarget != 246 || identity.BaseSha != new string('1', 40) ||
                identity.HeadSha != new string(index == 2 ? '3' : '2', 40) ||
                input.Input.Transition != (index == 0 ? "initial" : index == 1 ? "same_head" : "verified_ahead") ||
                trusted.ProviderId != DeepSeekAdapterContext.Provider || trusted.ModelId != DeepSeekAdapterContext.Model ||
                trusted.AdapterId != DeepSeekAdapterContext.Adapter ||
                input.InitialContext.Contains(Fact, StringComparison.Ordinal) ||
                Encoding.UTF8.GetString(trusted.TrustedPolicyBytes).Contains(Fact, StringComparison.Ordinal)) return false;
            var paths = index == 2 ? new[] { "src/current.txt" } : new[] { "src/current.txt", "src/fact.txt" };
            if (!input.Input.Repository.Select(entry => entry.Path).Order(StringComparer.Ordinal).SequenceEqual(paths)) return false;
            foreach (var entry in input.Input.Repository)
            {
                var content = entry.Path == "src/fact.txt" ? Fact : Current;
                if (fixture.Files.Single(file => file.Path == entry.File).Sha256 != AgentCanonical.HashRaw(Encoding.UTF8.GetBytes(content + "\n"))) return false;
            }
            // A diff is a model-visible input too, so withholding covers it as well as current files/context/policy.
            if (fixture.Files.Single(file => file.Path == input.Input.Diff).Sha256 !=
                AgentCanonical.HashRaw(Encoding.UTF8.GetBytes("{ \"changes\": [] }\n"))) return false;
        }
        var document = artifact.Document;
        if (document.Generation != phase || document.CompletedRuns.Length != phase + 1) return false;
        for (var index = 0; index <= phase; index++)
        {
            var completed = document.CompletedRuns[index];
            var results = completed.Records.OfType<AgentSessionToolResultRecord>().ToArray();
            var expectedCalls = index == 0 ? new[] { "seed_read", "current_0" } : new[] { "current_" + index };
            if (!results.Select(result => result.CallId).SequenceEqual(expectedCalls) || results.Any(result => result.Name != "read_file")) return false;
            foreach (var result in results)
            {
                using var parsed = JsonDocument.Parse(result.ResultJson);
                var body = parsed.RootElement;
                if (body.GetProperty("status").GetString() != "ok" || body.GetProperty("requested_start_line").GetInt32() != 1 ||
                    body.GetProperty("requested_line_count").GetInt32() != 1 || body.GetProperty("lines").GetArrayLength() != 1 ||
                    body.GetProperty("lines")[0].GetProperty("text").GetString() != (result.CallId == "seed_read" ? Fact : Current)) return false;
            }
            var continuation = completed.Continuation.Items;
            if (continuation.Length != 2 || continuation.Where((item, ordinal) =>
                item.ContentPosition != 0 || Encoding.UTF8.GetString(item.PayloadBytes) != Reasoning[index * 2 + ordinal]).Any()) return false;
            var outcome = completed.Records.OfType<AgentSessionReviewOutcomeRecord>().Single();
            if (outcome.Summary != (index == 0 ? "Seed complete." : "Restored fact: " + Fact) || outcome.FindingsJson != "[]") return false;
        }
        using var first = JsonDocument.Parse(reply.Requests[0]);
        var messages = first.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        var expectedReasoning = Reasoning.Take(phase * 2).ToArray();
        var assistant = messages.Where(message => message.GetProperty("role").GetString() == "assistant").ToArray();
        if (!assistant.Select(message => message.GetProperty("reasoning_content").GetString()).SequenceEqual(expectedReasoning)) return false;
        return phase == 0 ? assistant.Length == 0 : ReplayTransport.HistoricalLine(first.RootElement, "seed_read", 1) == Fact;
    }
}
