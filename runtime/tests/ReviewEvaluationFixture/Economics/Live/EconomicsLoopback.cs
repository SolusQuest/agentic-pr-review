using System.Collections.Immutable;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Histories;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;

internal sealed class EconomicsLoopback : IProjectChatClient, IDisposable
{
    private readonly ReplayScriptClient inner;
    private readonly EconomicsFault fault;
    private readonly long inputBasis;
    private readonly long? expectedOutputCap;
    internal EconomicsLoopback(ReplayScript script, EconomicsFault fault, long inputBasis, DeepSeekAdapterContext context, long? expectedOutputCap = null)
    {
        this.fault = fault; this.inputBasis = inputBasis; this.expectedOutputCap = expectedOutputCap;
        if (fault == EconomicsFault.TerminalArguments)
            script = new([new([new("invalid-terminal", "finish_review", "{\"summary\":\"PRIVATE_CANARY\",\"findings\":[null]}")], "")]);
        if (fault is EconomicsFault.UntrackedRead or EconomicsFault.UntrackedSearch or EconomicsFault.UnchangedDiff)
        {
            var (tool, arguments) = fault switch
            {
                EconomicsFault.UntrackedRead => ("read_file", "{\"path\":\"PRIVATE_CANARY.txt\"}"),
                EconomicsFault.UntrackedSearch => ("search_text", "{\"query\":\"fact\",\"path\":\"PRIVATE_CANARY.txt\"}"),
                _ => ("read_diff", "{\"path\":\"PRIVATE_CANARY.txt\"}"),
            };
            script = new(script.Turns.Insert(0, new([new("rejected-path", tool, arguments)], "")));
        }
        if (fault is EconomicsFault.ThreeCalls or EconomicsFault.EightCalls)
        {
            var first = script.Turns[0];
            var turns = Enumerable.Range(0, fault == EconomicsFault.ThreeCalls ? 2 : 8)
                .Select(index => first with { ToolCalls = first.ToolCalls.Select(call => call with
                    { Id = index == 0 ? call.Id : "c2-extra-" + index + "-" + call.Id }).ToImmutableArray() }).ToImmutableArray();
            script = new(fault == EconomicsFault.ThreeCalls ? turns.Add(script.Turns[^1]) : turns);
        }
        inner = new(script, ReplayFault.None, context);
    }
    public async Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
    {
        if (expectedOutputCap is { } cap && request.MaxOutputTokens != cap)
            throw new HistoricalScriptFailure("request_rejected");
        if (fault == EconomicsFault.RateLimit) throw new HistoricalScriptFailure("http429");
        if (fault == EconomicsFault.ProviderFailure) throw new HistoricalScriptFailure("transport_failure");
        var result = await inner.GetResponseAsync(request, token);
        if (fault == EconomicsFault.UsageViolation)
            return result with { Usage = new(inputBasis + 1, 2,
                new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Model, 0, inputBasis + 1)) };
        return result;
    }
    public void Dispose() => inner.Dispose();
}
