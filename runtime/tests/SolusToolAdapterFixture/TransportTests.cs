using System.Net;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Solus;
using SolusAgent.Api.Context;
using SolusAgent.Providers.DeepSeek;
using SolusAgent.Tools.Api;

namespace AgenticPrReview.Runtime.SolusToolAdapterFixture;

internal static class TransportTests
{
    internal const string Credential = "SYNTHETIC_R3_KEY_399";
    internal static async Task Run()
    {
        using var fixture = new SnapshotFixture();
        Proof.Stage = "installed-deepseek-real-serializer-integers-final";
        var execution = Guid.NewGuid(); var (tools, executor) = fixture.Tools(execution);
        var handler = new Handler((body, n) =>
        {
            using var document = JsonDocument.Parse(body);
            var definitions = document.RootElement.GetProperty("tools");
            Proof.Check(definitions.GetArrayLength() == 5 && !body.Contains("finish_review", StringComparison.Ordinal));
            Proof.Check(definitions.EnumerateArray().All(t => t.GetProperty("function").GetProperty("parameters")
                .GetProperty("additionalProperties").ValueKind == JsonValueKind.False));
            return n == 1 ? Wire("tool_calls", Proof.EmptyFinal,
                [new("wire_read", "read_file", "{\"path\":\"changed.cs\",\"start_line\":1.0,\"line_count\":1e0}")])
                : Wire("stop", Proof.Grounded(tools.CurrentObservations.Single().ObservationId));
        });
        using (var provider = Create(handler))
        {
            var host = new SolusReviewCandidateHost(execution, tools); var sink = new Sink();
            var result = await Proof.Agent(Proof.Configuration(tools, provider)).ExecuteCandidatesWithContextAsync(
                new(Proof.Request(execution), ContextExecutionIntent.Fresh), SolusReviewCandidateHost.Bounds(), host, sink);
            Proof.Check(host.TryComplete(result, out _) && handler.Sends == 2 && executor.Invocations == 1);
            Proof.Check(sink.Context is not null && !Encoding.UTF8.GetString(sink.Context.CopyRestrictedPayload()).Contains(Credential, StringComparison.Ordinal));
            Proof.Check(!provider.ToString()!.Contains(Credential, StringComparison.Ordinal));
        }

        Proof.Stage = "installed-deepseek-correction-sanitized-history";
        var correctedExecution = Guid.NewGuid(); var (correctedTools, correctedExecutor) = fixture.Tools(correctedExecution);
        var bad = "{\"path\":\"" + Proof.RejectedCanary + "\",\"unknown\":true}";
        var badHash = AgentCanonical.HashRaw(Encoding.UTF8.GetBytes(bad));
        var correctionHandler = new Handler((body, n) =>
        {
            if (n == 1) return Wire("tool_calls", null, [new("wire_bad", "read_file", bad), new("wire_sibling", "list_files", "{}")]);
            Proof.Check(!body.Contains(Proof.RejectedCanary, StringComparison.Ordinal) && !body.Contains(badHash, StringComparison.Ordinal));
            if (n == 2)
            {
                Proof.Check(correctedExecutor.Invocations == 0 && correctedTools.CurrentObservations.Length == 0);
                using var document = JsonDocument.Parse(body);
                var messages = document.RootElement.GetProperty("messages").EnumerateArray().ToArray();
                var feedback = messages.Where(m => m.GetProperty("role").GetString() == "tool").ToArray();
                Proof.Check(feedback.Length == 2 && feedback[0].GetProperty("content").GetString() == AgentRecoveryFeedback.ArgumentsInvalid &&
                    feedback[1].GetProperty("content").GetString() == AgentRecoveryFeedback.BatchNotExecuted);
                Proof.Check(messages.Where(m => m.GetProperty("role").GetString() == "assistant")
                    .SelectMany(m => m.GetProperty("tool_calls").EnumerateArray()).First().GetProperty("function")
                    .GetProperty("arguments").GetString() == AgentRecoveryFeedback.RejectedArguments);
                return Wire("tool_calls", null, [new("wire_new", "read_file", "{\"path\":\"changed.cs\",\"line_count\":1}")]);
            }
            return Wire("stop", Proof.Grounded(correctedTools.CurrentObservations.Single().ObservationId));
        });
        using (var provider = Create(correctionHandler))
        {
            var host = new SolusReviewCandidateHost(correctedExecution, correctedTools); var sink = new Sink();
            var result = await Proof.Agent(Proof.Configuration(correctedTools, provider)).ExecuteCandidatesWithContextAsync(
                new(Proof.Request(correctedExecution), ContextExecutionIntent.Fresh), SolusReviewCandidateHost.Bounds(), host, sink);
            Proof.Check(host.TryComplete(result, out _) && correctionHandler.Sends == 3 && correctedExecutor.Invocations == 1);
            var captured = Encoding.UTF8.GetString(sink.Context!.CopyRestrictedPayload());
            Proof.Check(!captured.Contains(Proof.RejectedCanary, StringComparison.Ordinal) && !captured.Contains(badHash, StringComparison.Ordinal) &&
                !captured.Contains(Credential, StringComparison.Ordinal));
        }

        Proof.Stage = "installed-deepseek-invalid-terminal-carriers";
        foreach (var mode in new[] { "stop_with_calls", "terminal_function_first", "terminal_function_last", "bad_nested_final" })
        {
            var id = Guid.NewGuid(); var (badTools, badExecutor) = fixture.Tools(id);
            var badHandler = new Handler((_, _) =>
            {
                if (mode == "bad_nested_final") return Wire("stop", "{\"summary\":\"bad\",\"findings\":[{\"severity\":\"high\"}]}");
                var calls = mode == "stop_with_calls" ? new[] { new ToolCall("read", "read_file", "{\"path\":\"changed.cs\"}") }
                    : new[] { new ToolCall("terminal", "finish_review", Proof.EmptyFinal), new ToolCall("read", "read_file", "{\"path\":\"changed.cs\"}") };
                if (mode == "terminal_function_last") Array.Reverse(calls);
                return Wire(mode == "stop_with_calls" ? "stop" : "tool_calls", Proof.EmptyFinal, calls);
            });
            using var provider = Create(badHandler); var host = new SolusReviewCandidateHost(id, badTools);
            var result = await Proof.Agent(Proof.Configuration(badTools, provider)).ExecuteCandidatesWithContextAsync(
                new(Proof.Request(id), ContextExecutionIntent.Fresh), SolusReviewCandidateHost.Bounds(), host);
            Proof.Check(!host.TryComplete(result, out _) && badHandler.Sends == 1 && badExecutor.Invocations == 0);
        }
    }

    private static DeepSeekProvider Create(Handler handler) => new(new(Credential, 1024,
        maximumRequestBodyBytes: 1048576, maximumResponseBodyBytes: 524288, timeout: TimeSpan.FromSeconds(30)), handler);
    private static string Wire(string finish, string? content, ToolCall[]? calls = null) => JsonSerializer.Serialize(new
    {
        id = "controlled_response", @object = "chat.completion", model = DeepSeekOptions.Model,
        choices = new[] { new { index = 0, finish_reason = finish, message = new {
            role = "assistant", content, reasoning_content = "SYNTHETIC_R3_REPLAY_399",
            tool_calls = calls?.Select(c => new { id = c.CallId, type = "function", function = new { name = c.ToolName, arguments = c.ArgumentsJson } }).ToArray(),
        } } },
        usage = new { prompt_tokens = 12, completion_tokens = 3, total_tokens = 15 },
    });

    private sealed class Handler(Func<string, int, string> reply) : HttpMessageHandler
    {
        internal int Sends;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Proof.Check(request.RequestUri!.OriginalString == DeepSeekOptions.OfficialEndpoint &&
                request.Headers.Authorization?.Parameter == Credential);
            var body = await request.Content!.ReadAsStringAsync(token);
            Proof.Check(!body.Contains(Credential, StringComparison.Ordinal));
            return new(HttpStatusCode.OK) { Content = new StringContent(reply(body, ++Sends), Encoding.UTF8, "application/json") };
        }
    }
}
