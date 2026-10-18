using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.SolusFreshHostProof;

namespace AgenticPrReview.Runtime.SolusEconomicsFixture;

internal sealed class ControlledProvider(Corpus corpus, string mode, WireObservation observation, EconomicsDocument? seed) : HttpMessageHandler
{
    private readonly JsonDocument script = corpus.Script("cs-defect");
    internal int Sends;
    internal bool InvariantFailure;
    internal Action? BeforeFinal;
    internal const string CurrentOnly = "V2_CURRENT_INPUT_PRIVATE_CANARY";
    internal const string Reasoning = "V2_RESTRICTED_REASONING_CANARY";
    internal const string ProviderRequestId = "V2_RESTRICTED_PROVIDER_REQUEST_ID";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        try { return await Exchange(request, token); }
        catch (ProofFailure) { InvariantFailure = true; throw; }
    }
    private async Task<HttpResponseMessage> Exchange(HttpRequestMessage request, CancellationToken token)
    {
        Proof.Require(request.RequestUri?.AbsoluteUri == "https://api.deepseek.com/chat/completions" &&
            request.Method == HttpMethod.Post && request.Headers.Authorization?.Parameter == HostFixture.ProviderKey,
            "v2_controlled_endpoint");
        var body = await request.Content!.ReadAsStringAsync(token);
        foreach (var secret in new[] { HostFixture.ProviderKey, HostFixture.GitHubKey, HostFixture.StateKey })
            Proof.Require(!body.Contains(secret, StringComparison.Ordinal), "v2_wire_no_credentials");
        using var parsed = JsonDocument.Parse(body); var root = parsed.RootElement;
        Proof.Require(root.GetProperty("model").GetString() == "deepseek-flash" &&
            root.GetProperty("max_tokens").GetInt32() == 1024 && !root.GetProperty("stream").GetBoolean() &&
            root.GetProperty("reasoning_effort").GetString() == "high" &&
            root.GetProperty("thinking").GetProperty("type").GetString() == "enabled" &&
            root.GetProperty("tools").GetArrayLength() == 5, "v2_actual_provider_settings");
        var wire = observation.Capture(body);
        var fresh = mode == "seed";
        if (fresh && Sends == 0) Proof.Require(!HistoryOracle.ContainsFact(root), "v2_seed_has_no_answer");
        var historicalFact = false;
        if (!fresh)
        {
            Proof.Require(seed is not null, "v2_seed_witness_required");
            HistoryOracle.Transition(seed!, wire, root);
            historicalFact = HistoryOracle.ContainsFact(root.GetProperty("messages")[10]);
        }
        var index = Sends++;
        Proof.Sends++;
        Proof.Require(index < (fresh ? 5 : 2), "v2_authored_send_count");
        if (mode == "transport-failure") throw new HttpRequestException(HttpRequestError.ConnectionError, "V2_PRIVATE_EXCEPTION_CANARY");
        // The target's useful current-file inspection and terminal result depend
        // on the old Lookup result. It never rereads Lookup in this new round.
        var turn = script.RootElement.GetProperty("turns")[fresh ? index : index == 0 ? 2 : 4];
        var calls = turn.GetProperty("tool_calls").EnumerateArray().ToArray();
        var final = calls.Length == 1 && calls[0].GetProperty("name").GetString() == "finish_review";
        if (final) BeforeFinal?.Invoke();
        string? content = final ? calls[0].GetProperty("arguments_json").GetString() : null;
        if (!fresh && final)
        {
            var candidate = JsonNode.Parse(content!)!;
            candidate["summary"] = "Authored current caller review using the historical nullable lookup.";
            content = historicalFact ? candidate.ToJsonString() : "{}";
        }
        if (mode == "invalid-final" && final) content = "{}";
        var toolCalls = final ? null : calls.Select(c => new { id = c.GetProperty("id").GetString() + (fresh ? "" : "-v2"),
            type = "function", function = new { name = c.GetProperty("name").GetString(),
                arguments = c.GetProperty("arguments_json").GetString() } }).ToArray();
        var reason = Reasoning + (fresh ? "_seed_" : "_current_") + index;
        var usage = Usage(mode, fresh);
        var response = JsonSerializer.Serialize(new {
            id = ProviderRequestId, @object = "chat.completion", model = "deepseek-flash",
            choices = new[] { new { index = 0, finish_reason = final ? "stop" : "tool_calls",
                message = new { role = "assistant", content, reasoning_content = reason, tool_calls = toolCalls } } }, usage,
        });
        if (final) observation.ReturnedFinal = new(Evidence.Hash("content", content!), Evidence.Hash("reasoning", reason), 0);
        return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
    }
    private static Dictionary<string, long>? Usage(string mode, bool fresh)
    {
        if (mode == "missing-usage") return null;
        var zero = mode == "zero";
        var value = new Dictionary<string, long> { ["prompt_tokens"] = zero ? 0 : fresh ? 10 : 11,
            ["completion_tokens"] = zero ? 0 : fresh ? 5 : 7 };
        if (mode is not ("total-only" or "miss-only")) value["prompt_cache_hit_tokens"] = zero ? 0 : fresh ? 4 : mode == "invalid-usage" ? 12 : 3;
        if (mode is not ("total-only" or "hit-only")) value["prompt_cache_miss_tokens"] = zero ? 0 : fresh ? 6 : 8;
        return value;
    }
    protected override void Dispose(bool disposing) { if (disposing) script.Dispose(); base.Dispose(disposing); }
}
