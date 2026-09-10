using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

internal sealed class ReplayTransport(ReplayScript script, ReplayFault fault) : IDeepSeekTransport
{
    internal List<byte[]> Requests { get; } = [];
    internal int Consumed { get; private set; }
    internal bool Failed { get; private set; }
    internal string? FailureCode { get; private set; }

    public Task<DeepSeekTransportResult> SendAsync(ReadOnlyMemory<byte> requestBody, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Requests.Sum(bytes => bytes.Length) + requestBody.Length > ReplayWire.EvidenceLimit)
            throw new IOException();
        Requests.Add(requestBody.ToArray());
        if (fault == ReplayFault.Provider || Consumed >= script.Turns.Length || fault == ReplayFault.Incomplete && Consumed > 0)
        { Failed = true; FailureCode = fault == ReplayFault.Provider ? "provider_failed" : "script_exhausted"; return Task.FromResult(DeepSeekTransportResult.TransportFailure()); }
        var turn = script.Turns[Consumed++];
        using var body = JsonDocument.Parse(requestBody);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", DeepSeekAdapterContext.Model);
            writer.WriteStartArray("choices"); writer.WriteStartObject(); writer.WriteNumber("index", 0);
            writer.WriteString("finish_reason", "tool_calls"); writer.WriteStartObject("message");
            writer.WriteString("role", "assistant"); writer.WriteString("content", "");
            writer.WriteString("reasoning_content", turn.ReasoningContent);
            writer.WriteStartArray("tool_calls");
            foreach (var call in turn.ToolCalls)
            {
                var arguments = call.ArgumentsJson;
                if (call.Name == "finish_review")
                {
                    if (fault == ReplayFault.MalformedTerminal) arguments = "{}";
                    else
                    {
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
                writer.WriteStartObject(); writer.WriteString("id", call.Id); writer.WriteString("type", "function");
                writer.WriteStartObject("function"); writer.WriteString("name", call.Name); writer.WriteString("arguments", arguments);
                writer.WriteEndObject(); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndArray();
            writer.WriteStartObject("usage"); writer.WriteNumber("prompt_tokens", 3); writer.WriteNumber("completion_tokens", 2);
            writer.WriteNumber("total_tokens", 5); writer.WriteNumber("prompt_cache_hit_tokens", 0); writer.WriteNumber("prompt_cache_miss_tokens", 3);
            writer.WriteEndObject(); writer.WriteEndObject();
        }
        return Task.FromResult(DeepSeekTransportResult.Success(stream.ToArray()));
    }

    internal static string? HistoricalLine(JsonElement request, string callId, int line)
    {
        var messages = request.GetProperty("messages").EnumerateArray().ToArray();
        var current = Array.FindLastIndex(messages, message => message.GetProperty("role").GetString() == "user");
        var matches = messages.Take(current).Where(message => message.GetProperty("role").GetString() == "tool" &&
            message.GetProperty("tool_call_id").GetString() == callId).ToArray();
        if (matches.Length != 1) return null;
        using var result = JsonDocument.Parse(matches[0].GetProperty("content").GetString()!);
        if (!result.RootElement.TryGetProperty("lines", out var lines)) return null;
        var found = lines.EnumerateArray().Where(item => item.GetProperty("line").GetInt32() == line).ToArray();
        return found.Length == 1 ? found[0].GetProperty("text").GetString() : null;
    }

    public void Dispose() { }
}
