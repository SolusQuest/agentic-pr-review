using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.Application.Solus;

// The retained direct command is a deterministic verification contract. This
// compiled handler observes the real package HTTP serialization/parser and
// emits Final data; it is never a native provider adapter or a live selector.
internal sealed class SolusDirectTransport(string phase, IReadOnlyList<byte[]> publicInputs) : HttpMessageHandler
{
    private readonly byte[][] inputs = publicInputs.Select(bytes => bytes.ToArray()).ToArray();
    private string? fact;
    private string? firstRequestSha256;
    private string? expectedTerminalSha256;
    private bool historyValidated;
    private bool terminalIssued;
    internal int Requests { get; private set; }
    internal LiveAgentFreshProcessTransportProof Proof => new(Requests == 2 && terminalIssued &&
        (phase == "bootstrap" || historyValidated), Requests, firstRequestSha256,
        fact is null ? null : AgentCanonical.HashRaw(Encoding.UTF8.GetBytes(fact)), expectedTerminalSha256);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.Method != HttpMethod.Post || request.RequestUri?.OriginalString !=
            "https://api.deepseek.com/chat/completions" || request.Content is null)
            throw new HttpRequestException("APR_SOLUS_DIRECT_TRANSPORT");
        var bytes = await request.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        try
        {
            Requests++;
            if (Requests > 2) throw new HttpRequestException("APR_SOLUS_DIRECT_TRANSPORT");
            if (phase == "continue" && Requests == 1) firstRequestSha256 = AgentCanonical.HashRaw(bytes);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.GetProperty("model").GetString() != DeepSeekAdapterContext.Model ||
                root.GetProperty("tools").GetArrayLength() != 5 ||
                root.GetProperty("tools").EnumerateArray().Any(tool =>
                    tool.GetProperty("function").GetProperty("name").GetString() == AgentToolRegistry.FinishReviewName))
                throw new HttpRequestException("APR_SOLUS_DIRECT_TRANSPORT");
            var messages = root.GetProperty("messages").EnumerateArray().ToArray();
            if (Requests == 1)
            {
                if (phase == "continue")
                {
                    var historical = messages.Where(message => message.GetProperty("role").GetString() == "tool" &&
                        message.GetProperty("tool_call_id").GetString() == "bootstrap_read").ToArray();
                    if (historical.Length != 1 || !TryFact(historical[0].GetProperty("content").GetString(), out fact) ||
                        inputs.Any(value => Encoding.UTF8.GetString(value).Contains(fact!, StringComparison.Ordinal)) ||
                        messages.Where(message => message.GetProperty("role").GetString() is "user" or "system")
                            .Any(message => message.GetRawText().Contains(fact!, StringComparison.Ordinal)) ||
                        !messages.Any(message => HasReadCall(message, "bootstrap_read")))
                        throw new HttpRequestException("APR_SOLUS_DIRECT_HISTORY");
                    historyValidated = true;
                }
                else if (phase != "bootstrap" ||
                    !messages.Select(message => message.GetProperty("role").GetString()).SequenceEqual(["system", "user"]))
                    throw new HttpRequestException("APR_SOLUS_DIRECT_TRANSPORT");
                return Response(null, phase == "bootstrap" ? "bootstrap_read" : "continue_read");
            }
            var callId = phase == "bootstrap" ? "bootstrap_read" : "continue_read";
            var results = messages.Where(message => message.GetProperty("role").GetString() == "tool" &&
                message.GetProperty("tool_call_id").GetString() == callId).ToArray();
            if (results.Length != 1 || !messages.Any(message => HasReadCall(message, callId)))
                throw new HttpRequestException("APR_SOLUS_DIRECT_TRANSPORT");
            var content = results[0].GetProperty("content").GetString();
            using var toolResult = JsonDocument.Parse(content!);
            var observation = toolResult.RootElement.GetProperty("observation_id").GetString();
            if (!LiveAgentFreshProcessDomain.IsSha256(observation) ||
                phase == "bootstrap" && !TryFact(content, out fact) || fact is null)
                throw new HttpRequestException("APR_SOLUS_DIRECT_TRANSPORT");
            var terminal = AgentToolArguments.WriteFinishReview((phase == "bootstrap" ? "grounded " : "continued ") + fact,
                [new AgentFinding("high", "grounded continuation", "The reviewed fact was grounded.",
                    [new(observation!, "fact.txt", 1, 1)])]);
            try
            {
                expectedTerminalSha256 = AgentCanonical.HashDomain(AgentCanonical.TerminalDomain, terminal);
                terminalIssued = true;
                return Response(Encoding.UTF8.GetString(terminal), null);
            }
            finally { CryptographicOperations.ZeroMemory(terminal); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool HasReadCall(JsonElement message, string id)
    {
        if (message.GetProperty("role").GetString() != "assistant" ||
            !message.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array ||
            calls.GetArrayLength() != 1) return false;
        var call = calls[0];
        if (call.GetProperty("id").GetString() != id ||
            call.GetProperty("function").GetProperty("name").GetString() != AgentToolRegistry.ReadFileName) return false;
        using var arguments = JsonDocument.Parse(call.GetProperty("function").GetProperty("arguments").GetString()!);
        return arguments.RootElement.GetProperty("path").GetString() == "fact.txt";
    }

    private static bool TryFact(string? value, out string? result)
    {
        result = null;
        if (value is null) return false;
        const string prefix = "APR_PRIOR_ONLY_";
        var index = value.IndexOf(prefix, StringComparison.Ordinal);
        if (index < 0 || value.Length < index + prefix.Length + 64) return false;
        var found = value.Substring(index, prefix.Length + 64);
        if (!found.AsSpan(prefix.Length).ToArray().All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            return false;
        result = found;
        return true;
    }

    private static HttpResponseMessage Response(string? final, string? callId)
    {
        var body = JsonSerializer.Serialize(new {
            id = "synthetic-direct", @object = "chat.completion", model = DeepSeekAdapterContext.Model,
            choices = new[] { new { index = 0, finish_reason = final is null ? "tool_calls" : "stop", message = new {
                role = "assistant", content = final, reasoning_content = "Synthetic restricted review reasoning.",
                tool_calls = final is null ? new[] { new { id = callId, type = "function",
                    function = new { name = AgentToolRegistry.ReadFileName, arguments = "{\"path\":\"fact.txt\"}" } } } : null } } },
            usage = new { prompt_tokens = 3, completion_tokens = 2, total_tokens = 5,
                prompt_cache_hit_tokens = 1, prompt_cache_miss_tokens = 2 }
        });
        return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var bytes in inputs) CryptographicOperations.ZeroMemory(bytes);
        fact = null;
        base.Dispose(disposing);
    }
}
