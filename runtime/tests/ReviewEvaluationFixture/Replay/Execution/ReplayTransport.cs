using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Quality.Incremental;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

// Execute an admitted, fixed fixture script at the typed chat seam. Captured bytes
// are the actual logical input, never simulated provider request/response bodies.
internal sealed class ReplayScriptClient(ReplayScript script, ReplayFault fault, DeepSeekAdapterContext context)
    : IProjectChatClient, IDisposable
{
    internal List<byte[]> Requests { get; } = [];
    internal List<int?> OutputCaps { get; } = [];
    internal int Consumed { get; private set; }
    internal bool Failed { get; private set; }
    internal string? FailureCode { get; private set; }

    public Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var logical = AgentRequestWriter.Write(request);
        if (Requests.Sum(bytes => bytes.Length) + logical.Length > ReplayWire.EvidenceLimit) throw new IOException();
        Requests.Add(logical); OutputCaps.Add(request.MaxOutputTokens);
        if (fault == ReplayFault.Provider || Consumed >= script.Turns.Length || fault == ReplayFault.Incomplete && Consumed > 0)
        {
            Failed = true; FailureCode = fault == ReplayFault.Provider ? "provider_failed" : "script_exhausted";
            throw new IOException("historical_script_failed");
        }
        if (request.Continuation is { } continuation &&
            (continuation.ProviderId != context.ProviderId || continuation.ModelId != context.ModelId ||
                continuation.AdapterId != context.AdapterId || continuation.SessionId != context.SessionId ||
                continuation.Items.Any(item => item.ContentPosition != 0 || item.Opaque != "" ||
                    item.Framing != DeepSeekReasoningContinuationCodec.FramingName || item.MessagePosition < 0 ||
                    item.MessagePosition >= request.Messages.Length || request.Messages[item.MessagePosition].Role != "assistant")))
            throw new ProjectChatNormalizationException();
        // Restored reasoning is carried by typed continuation items, not necessarily inline contents.
        foreach (var (message, priorPosition) in request.Messages.Select((message, index) => (message, index)))
            if (message.Role == "assistant" && request.Continuation?.Items.Count(item => item.MessagePosition == priorPosition) != 1)
                throw new ProjectChatNormalizationException();
        var turn = script.Turns[Consumed++];
        using var body = JsonDocument.Parse(logical);
        var calls = new List<ProjectChatContent>();
        var position = request.Messages.Length;
        calls.Add(new ProjectReasoningContent(turn.ReasoningContent, "", DeepSeekReasoningContinuationCodec.FramingName, null, position, 0));
        foreach (var call in turn.ToolCalls)
        {
            var arguments = call.ArgumentsJson;
            if (call.Name == "finish_review")
            {
                if (fault == ReplayFault.MalformedTerminal) arguments = "{}";
                else
                {
                    arguments = IncrementalArguments.Expand(arguments, body.RootElement);
                    using var terminal = JsonDocument.Parse(arguments);
                    var summary = terminal.RootElement.GetProperty("summary").GetString()!;
                    if (summary.StartsWith("$history:", StringComparison.Ordinal))
                    {
                        var parts = summary.Split(':');
                        var fact = parts.Length == 3 && int.TryParse(parts[2], out var line)
                            ? HistoricalLine(body.RootElement, parts[1], line) : null;
                        if (fact is null) { Failed = true; FailureCode = "history_failed"; throw new InvalidOperationException("replay_history_missing"); }
                        using var rewritten = new MemoryStream();
                        using (var output = new Utf8JsonWriter(rewritten))
                        {
                            output.WriteStartObject(); output.WriteString("summary", "Restored fact: " + fact);
                            output.WritePropertyName("findings"); terminal.RootElement.GetProperty("findings").WriteTo(output);
                            output.WriteEndObject();
                        }
                        arguments = Encoding.UTF8.GetString(rewritten.ToArray());
                    }
                }
            }
            calls.Add(new ProjectToolCallContent(call.Id, call.Name, arguments));
        }
        if (request.Accounting is { } capture && !capture.TryBeginDispatch()) throw new IOException("historical_script_refused");
        request.Accounting?.RecordUsage(ProviderUsageObservation.Create(3, 2, 0, 3, 5));
        // Typed fixture: no serialized provider response body; zero is not a wire measurement.
        return Task.FromResult(new ProjectChatResponse(new("assistant", calls.ToArray()),
            new(3, 2, new(context.ProviderId, context.ModelId, context.ModelId, 0, 3)),
            CapturedResponseBodyBytes: 0, Continuation: new(context.ProviderId, context.ModelId, context.AdapterId, context.SessionId,
                [new(turn.ReasoningContent, "", DeepSeekReasoningContinuationCodec.FramingName, null, position, 0)])));
    }

    internal static string? HistoricalLine(JsonElement request, string callId, int line)
    {
        var messages = request.GetProperty("messages").EnumerateArray().ToArray();
        var current = Array.FindLastIndex(messages, message => message.GetProperty("role").GetString() == "user");
        var matches = messages.Take(current).Where(message => message.GetProperty("role").GetString() == "tool")
            .SelectMany(message => message.GetProperty("contents").EnumerateArray())
            .Where(content => content.GetProperty("kind").GetString() == "tool_result" &&
                content.GetProperty("call_id").GetString() == callId).ToArray();
        if (matches.Length != 1) return null;
        using var result = JsonDocument.Parse(matches[0].GetProperty("result").GetString()!);
        if (!result.RootElement.TryGetProperty("lines", out var lines)) return null;
        var found = lines.EnumerateArray().Where(item => item.GetProperty("line").GetInt32() == line).ToArray();
        return found.Length == 1 ? found[0].GetProperty("text").GetString() : null;
    }

    public void Dispose() { }
}
