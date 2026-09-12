using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles;

internal sealed record GrowthChatCounts(int Calls, int LastProjectRequestBytes, int LastMessages, int LastResponseMessages, long LastContinuationBeforeBytes,
    long LastContinuationAfterBytes);

// Observes the real project request and adapter response, without retaining either.
internal sealed class GrowthChatMeasurement(IProjectChatClient inner) : IProjectChatClient
{
    internal GrowthChatCounts Counts { get; private set; } = new(0, 0, 0, 0, 0, 0);
    public async Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
    {
        var before = request.Continuation?.Items.Sum(item => (long)AgentRequestWriter.WriteContinuationItem(item).Length) ?? 0;
        Counts = new(Counts.Calls + 1, AgentRequestWriter.Write(request).Length, request.Messages.Length, request.Messages.Length, before, before);
        var response = await inner.GetResponseAsync(request, token);
        var added = response.Continuation?.Items.Sum(item => (long)AgentRequestWriter.WriteContinuationItem(item).Length) ?? 0;
        Counts = Counts with { LastContinuationAfterBytes = before + added,
            LastResponseMessages = request.Messages.Length + 1 + response.Message.Contents.OfType<ProjectToolCallContent>().Count(c => c.Name != "finish_review") };
        return response;
    }
}
