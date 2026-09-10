using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Quality.Incremental;

// The oracle is compiled independently of mutable script/expected files. Q1 Scored alone is not a closed finding set.
internal static class IncrementalCoverage
{
    internal static readonly string[] Cases = ["incremental-seed", "incremental-same", "incremental-ahead"];
    internal const string Policy = "Review current source using read_file. Historical evidence is context only.\n";
    internal const string Summary = "Incremental review complete.";
    internal static string Source(int phase) => phase == 2
        ? "repair: validates input\nreserved: safe location\nreserved: safe location\ncontinue: drops required validation\nnew: skips ownership check\n"
        : "repair: accepts unchecked input\ncontinue: drops required validation\ndelete: leaks obsolete value\n";
    internal static string Observation(int phase) => phase == 2
        ? "c6cd6b27cd56d51ad437649dcbe24aea51086214311d006250a6d662ae330afa"
        : "fbd7fb0f3ba2c9a71b6f3ace3b070151af9b41c3be1062338bebb31249547935";
    internal static ReviewedIdentity Identity(int phase) => new("42", 147, new('1', 40), new(phase == 2 ? 'c' : 'e', 40));
    internal static string Reasoning(int phase, bool finish) => finish ? $" incremental {phase} finish " : $" incremental {phase} read \nα ";
    internal static (string Id, int Line)[] Defects(int phase) => phase == 2
        ? [("continuing", 4), ("new", 5)] : [("repaired", 1), ("continuing", 2), ("deleted", 3)];
    internal static ImmutableArray<AgentFinding> Findings(int phase) => [.. Defects(phase).Select(defect =>
        new AgentFinding("high", defect.Id, $"Synthetic {defect.Id} defect.",
            [new(Observation(phase), "file.txt", defect.Line, defect.Line)]))];
    internal static bool IsSequence(AdmittedReplayFixture fixture) =>
        fixture.Runs.Select(run => run.Input.Id).SequenceEqual(Cases) &&
        fixture.Runs.Select(run => run.Input.CaseId).SequenceEqual(Cases);

    internal static bool VerifyFixture(AdmittedReplayFixture fixture)
    {
        if (!IsSequence(fixture)) return false;
        for (var phase = 0; phase < 3; phase++)
        {
            var run = fixture.Runs[phase];
            var trusted = run.CreateTrustedRequest(ReplayState.Build);
            var input = run.Input;
            if (input.ReviewedIdentity.Runtime != Identity(phase) ||
                input.Transition != (phase == 0 ? "initial" : phase == 1 ? "same_head" : "verified_ahead") ||
                input.PreviousRunId != (phase == 0 ? null : Cases[phase - 1]) ||
                trusted.WorkflowIdentity != "r5@incremental" ||
                trusted.ProviderId != DeepSeekAdapterContext.Provider || trusted.ModelId != DeepSeekAdapterContext.Model ||
                trusted.AdapterId != DeepSeekAdapterContext.Adapter || Encoding.UTF8.GetString(trusted.TrustedPolicyBytes) != Policy ||
                run.InitialContext != $"Review immutable revision {phase}; acquire current evidence before findings.\n" ||
                input.Repository.Length != 1 || input.Repository[0].Path != "file.txt" ||
                fixture.Files.Single(file => file.Path == input.Repository[0].File).Sha256 != AgentCanonical.HashRaw(Encoding.UTF8.GetBytes(Source(phase))) ||
                fixture.Files.Single(file => file.Path == input.Diff).Sha256 != (phase == 2
                    ? "c509c40fa9f99937117fd6d04ab7568e8c69151f9e537870efba42f851e64c9c"
                    : "78ec4b54cf56ed58236c3ff9aca824ec6964996805c3f0ebd6a649538b7361bc")) return false;
            var expected = run.Expected.Input;
            if (run.ExpectedCode != EvaluationCode.Scored ||
                !expected.Defects.SequenceEqual(Defects(phase).Select(defect => new ExpectedDefect(defect.Id, "high", Observation(phase), "file.txt", defect.Line, defect.Line))) ||
                !expected.RequiredObservations.SequenceEqual([new RequiredObservation("read_file", Observation(phase))]) ||
                !expected.ProhibitedFindings.SequenceEqual(phase == 2 ? [new ProhibitedFinding("file.txt", 1, 3)] : [])) return false;
        }
        return true;
    }

    internal static bool ClosedFindings(int phase, ImmutableArray<AgentFinding> findings) =>
        AgentToolArguments.WriteFinishReview(Summary, findings).AsSpan()
            .SequenceEqual(AgentToolArguments.WriteFinishReview(Summary, Findings(phase)));

    internal static bool Verify(AdmittedReplayFixture fixture, int phase, ReplayChildReply reply)
    {
        if (!VerifyFixture(fixture) || reply.Plaintext is null || reply.Requests.Length != 2 || reply.ModelCalls != 2 || reply.ToolCalls != 1 ||
            !AgentSessionCodec.TryParse(reply.Plaintext, out var artifact, out _) || artifact is null) return false;
        var document = artifact.Document;
        if (document.Generation != phase || document.CompletedRuns.Length != phase + 1) return false;
        for (var index = 0; index <= phase; index++)
        {
            var completed = document.CompletedRuns[index];
            var tools = completed.Records.OfType<AgentSessionToolResultRecord>().ToArray();
            if (completed.ReviewedIdentity != Identity(index) || tools.Length != 1 || tools[0].Name != "read_file" ||
                tools[0].CallId != "current_" + index || tools[0].ObservationId != Observation(index)) return false;
            using var result = JsonDocument.Parse(tools[0].ResultJson);
            if (!ReadMatches(index, result.RootElement)) return false;
            var continuation = completed.Continuation.Items;
            if (continuation.Length != 2 || continuation.Where((item, ordinal) =>
                item.ContentPosition != 0 || Encoding.UTF8.GetString(item.PayloadBytes) != Reasoning(index, ordinal == 1)).Any()) return false;
            var outcomes = completed.Records.OfType<AgentSessionReviewOutcomeRecord>().ToArray();
            if (outcomes.Length != 1 || outcomes[0].Summary != Summary ||
                !AgentToolArguments.TryFinishReview("{\"summary\":\"" + Summary + "\",\"findings\":" + outcomes[0].FindingsJson + "}", out var terminal) ||
                !ClosedFindings(index, terminal!.Findings)) return false;
        }
        using var first = JsonDocument.Parse(reply.Requests[0]);
        var messages = first.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        var assistant = messages.Where(message => message.GetProperty("role").GetString() == "assistant").ToArray();
        if (!assistant.Select(message => message.GetProperty("reasoning_content").GetString())
            .SequenceEqual(Enumerable.Range(0, phase).SelectMany(index => new[] { Reasoning(index, false), Reasoning(index, true) }))) return false;
        // Restored terminal acknowledgements are also tool messages; current acquisition is witnessed by read calls.
        var previousTools = messages.Where(message => message.GetProperty("role").GetString() == "tool" &&
            message.GetProperty("tool_call_id").GetString()!.StartsWith("current_", StringComparison.Ordinal)).ToArray();
        if (!previousTools.Select(message => message.GetProperty("tool_call_id").GetString())
            .SequenceEqual(Enumerable.Range(0, phase).Select(index => "current_" + index))) return false;
        using var final = JsonDocument.Parse(reply.Requests[1]);
        var current = IncrementalArguments.CurrentResult(final.RootElement, "current_" + phase);
        return current is not null && ReadMatches(phase, current.Value);
    }

    private static bool ReadMatches(int phase, JsonElement result) =>
        result.GetProperty("observation_id").GetString() == Observation(phase) && result.GetProperty("status").GetString() == "ok" &&
        result.GetProperty("path").GetString() == "file.txt" && result.GetProperty("requested_start_line").GetInt32() == 1 &&
        result.GetProperty("requested_line_count").GetInt32() == 20 &&
        result.GetProperty("reviewed_identity").GetProperty("head_sha").GetString() == Identity(phase).HeadSha &&
        result.GetProperty("lines").EnumerateArray().Select(line => line.GetProperty("text").GetString())
            .SequenceEqual(Source(phase).TrimEnd('\n').Split('\n'));
}
