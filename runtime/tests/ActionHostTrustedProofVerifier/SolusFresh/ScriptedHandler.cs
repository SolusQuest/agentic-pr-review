using System.Net;
using System.Text;
using System.Text.Json;

namespace AgenticPrReview.Runtime.SolusFreshHostProof;

internal sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly JsonDocument script;
    internal int Sends;
    internal string FailureCode = "none";
    internal long RequestBytes, ResponseBytes;
    internal readonly HashSet<string> ReadFacts = new(StringComparer.Ordinal);
    internal readonly List<string> FirstInstructions = [];
    internal Action? BeforeFinal;
    internal Action? BeforeResponse;
    internal bool Malformed, MissingUsage, MixedFinal, PrivateReasoning, WaitForCancellation;
    internal string Focus;
    internal ScriptedHandler(Corpus corpus, string id)
    {
        script = corpus.Script(id); Turns = script.RootElement.GetProperty("turns").GetArrayLength();
        Focus = Encoding.UTF8.GetString(corpus.Member($"cases/{id}/context.txt"));
    }
    internal int Turns { get; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        try { return await SendControlledAsync(request, token); }
        catch (Exception exception)
        {
            FailureCode = exception is ProofFailure failure ? failure.Code :
                exception is KeyNotFoundException ? "wire_missing_property" :
                exception is OperationCanceledException ? "cancelled" : "transport_contract";
            throw;
        }
    }
    private async Task<HttpResponseMessage> SendControlledAsync(HttpRequestMessage request, CancellationToken token)
    {
        Proof.Require(request.RequestUri?.AbsoluteUri == "https://api.deepseek.com/chat/completions" &&
            request.Method == HttpMethod.Post && request.Headers.Authorization?.Scheme == "Bearer" &&
            request.Headers.Authorization?.Parameter == HostFixture.ProviderKey,
            "official_controlled_transport");
        var body = await request.Content!.ReadAsStringAsync(token); RequestBytes += Encoding.UTF8.GetByteCount(body);
        Proof.Require(!body.Contains(HostFixture.ProviderKey, StringComparison.Ordinal) &&
            !body.Contains(HostFixture.GitHubKey, StringComparison.Ordinal) &&
            !body.Contains(HostFixture.StateKey, StringComparison.Ordinal), "wire_no_host_credentials");
        using var wire = JsonDocument.Parse(body);
        var value = wire.RootElement;
        Proof.Require(value.GetProperty("model").GetString() == "deepseek-flash" &&
            value.GetProperty("max_tokens").GetInt32() == 1024 &&
            value.GetProperty("thinking").GetProperty("type").GetString() == "enabled" &&
            value.GetProperty("reasoning_effort").GetString() == "high" &&
            !value.GetProperty("stream").GetBoolean() && value.GetProperty("tools").GetArrayLength() == 5,
            "actual_selected_provider_options");
        foreach (var definition in value.GetProperty("tools").EnumerateArray())
            Proof.Require(definition.GetProperty("function").GetProperty("name").GetString() != "finish_review",
                "five_nonterminal_definitions");
        foreach (var message in value.GetProperty("messages").EnumerateArray())
        {
            var role = message.GetProperty("role").GetString();
            if (Sends == 0 && role == "system") FirstInstructions.Add(message.GetProperty("content").GetString()!);
            if (role == "tool")
            {
                using var result = JsonDocument.Parse(message.GetProperty("content").GetString()!);
                if (result.RootElement.TryGetProperty("lines", out var lines))
                    foreach (var line in lines.EnumerateArray()) ReadFacts.Add(line.GetProperty("text").GetString()!);
            }
        }
        if (Sends == 0)
            foreach (var fact in Proof.DecisiveFacts)
                Proof.Require(!body.Contains(fact, StringComparison.Ordinal) &&
                    FirstInstructions.All(i => !i.Contains(fact, StringComparison.Ordinal)), "first_request_withholds_answers");
        Proof.Require(Sends < Turns, "no_unexpected_script_send");
        var turn = script.RootElement.GetProperty("turns")[Sends++]; Proof.Sends++;
        BeforeResponse?.Invoke(); token.ThrowIfCancellationRequested();
        if (WaitForCancellation) await Task.Delay(Timeout.Infinite, token);
        if (Malformed) return Response("{");
        var calls = turn.GetProperty("tool_calls").EnumerateArray().ToArray();
        var final = calls.Length == 1 && calls[0].GetProperty("name").GetString() == "finish_review";
        if (final) BeforeFinal?.Invoke();
        var tools = final && !MixedFinal ? null : calls.Select(call => new
        {
            id = call.GetProperty("id").GetString(), type = "function", function = new
            { name = call.GetProperty("name").GetString(), arguments = call.GetProperty("arguments_json").GetString() },
        }).ToArray();
        var json = JsonSerializer.Serialize(new
        {
            id = "r5-controlled", @object = "chat.completion", model = "deepseek-flash",
            choices = new[] { new { index = 0, finish_reason = final ? "stop" : "tool_calls", message = new
            {
                role = "assistant", content = final ? calls[0].GetProperty("arguments_json").GetString() : null,
                reasoning_content = PrivateReasoning ? "R5_PRIVATE_REASONING_CANARY" : turn.GetProperty("reasoning_content").GetString(),
                tool_calls = tools,
            } } },
            usage = MissingUsage ? null : new { prompt_tokens = 10, completion_tokens = 5,
                prompt_cache_hit_tokens = 4, prompt_cache_miss_tokens = 6 },
        });
        return Response(json);
    }
    private HttpResponseMessage Response(string json)
    {
        ResponseBytes += Encoding.UTF8.GetByteCount(json);
        return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
    protected override void Dispose(bool disposing) { if (disposing) script.Dispose(); base.Dispose(disposing); }
}
