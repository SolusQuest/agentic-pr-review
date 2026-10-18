using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using SolusAgent.Runtime.Api.Providers;
using AgenticPrReview.Runtime.SolusFreshHostProof;

namespace AgenticPrReview.Runtime.SolusEconomicsFixture;

internal sealed class WireObservation
{
    private readonly object gate = new();
    private readonly List<WireFact> requests = [];
    private bool frozen;
    private FinalFact? returnedFinal;
    internal ProviderAttempt? Active;
    internal FinalFact? ReturnedFinal
    {
        get { lock (gate) return returnedFinal; }
        set { lock (gate) { if (!frozen) returnedFinal = value; } }
    }
    internal (WireFact[] Requests, FinalFact? Final) Freeze()
    { lock (gate) { frozen = true; return (requests.ToArray(), returnedFinal); } }
    internal static SegmentFact Segment(string domain, string raw)
    {
        var bytes = Encoding.UTF8.GetBytes(raw);
        return new(Evidence.Hash(domain, bytes), bytes.Length);
    }
    internal WireFact Capture(string body)
    {
        Proof.Require(Active is not null && Encoding.UTF8.GetByteCount(body) <= 1024 * 1024, "v2_wire_bounds");
        using var parsed = JsonDocument.Parse(body, new() { MaxDepth = 24 }); var root = parsed.RootElement;
        using var settings = new MemoryStream();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            Proof.Require(names.Add(property.Name) && property.Name is "model" or "stream" or "thinking" or
                "reasoning_effort" or "max_tokens" or "messages" or "tools", "v2_wire_fields");
            if (property.Name == "messages") continue;
            Frame(settings, property.Name); Frame(settings, property.Value.GetRawText());
        }
        Proof.Require(names.Count is 6 or 7 && names.Contains("messages"), "v2_wire_inventory");
        var messages = root.GetProperty("messages").EnumerateArray().Select(Message).ToArray();
        Proof.Require(messages.Length is > 0 and <= 32 && messages[0].Role == "system" &&
            messages.Skip(1).All(m => m.Role != "system"), "v2_wire_roles");
        var current = Active!;
        var fact = new WireFact(current.ExecutionId, current.LogicalCallId, current.PhysicalAttemptId, current.AttemptNumber,
            Segment("whole", body), new(Evidence.Hash("settings", settings.ToArray()), checked((int)settings.Length)), messages);
        lock (gate)
        {
            if (!frozen) { Proof.Require(requests.Count < 6, "v2_wire_inventory_bound"); requests.Add(fact); }
        }
        return fact;
    }
    private static MessageFact Message(JsonElement value)
    {
        var role = value.GetProperty("role").GetString()!;
        Proof.Require(role is "system" or "user" or "assistant" or "tool", "v2_wire_role");
        var names = value.EnumerateObject().Select(p => p.Name).ToArray();
        Proof.Require(names.Distinct().Count() == names.Length && names.All(n => n is "role" or "content" or
            "reasoning_content" or "tool_calls" or "tool_call_id"), "v2_message_fields");
        string? Text(string name, string domain) => value.TryGetProperty(name, out var property) &&
            property.ValueKind != JsonValueKind.Null ? Evidence.Hash(domain, property.GetString()!) : null;
        var association = value.TryGetProperty("tool_calls", out var calls)
            ? Evidence.Hash("calls", calls.GetRawText()) : Text("tool_call_id", "association");
        var content = Text("content", "content"); var reasoning = Text("reasoning_content", "reasoning");
        Proof.Require(role == "assistant" ? reasoning is not null : reasoning is null && content is not null,
            "v2_message_continuation");
        return new(role, Segment("message", value.GetRawText()), content, reasoning, association);
    }
    private static void Frame(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value); Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(size, bytes.Length); stream.Write(size); stream.Write(bytes);
    }
}

internal static class HistoryOracle
{
    // Genuine historical read_file data, absent current instructions/context,
    // diff and the current Caller-only tool result. No deleted-snapshot claim.
    internal const string Fact = "    public static string? Find(string key) => null;";
    internal static void Transition(EconomicsDocument seed, WireFact current, JsonElement wire)
    {
        var previous = seed.Wire[^1]; var final = seed.FinalResponse;
        Proof.Require(previous.Messages.Length == 11 && final is not null && current.Messages.Length >= 14 &&
            current.Settings == previous.Settings && current.Messages.Take(11).SequenceEqual(previous.Messages), "v2_exact_historical_tokens");
        var assistant = current.Messages[11];
        Proof.Require(assistant.Role == "assistant" && assistant.ContentSha256 == final!.ContentSha256 &&
            assistant.ReasoningSha256 == final.ReasoningSha256 && assistant.ToolAssociationSha256 is null && final.Calls == 0,
            "v2_final_assistant_association");
        var messages = wire.GetProperty("messages").EnumerateArray().ToArray();
        Proof.Require(messages[10].GetProperty("role").GetString() == "tool" && ContainsFact(messages[10]) &&
            messages.Where((_, index) => index != 10).All(m => !ContainsFact(m)), "v2_prior_only_tool_fact");
        Proof.Require(current.Messages[12].Role == "user" && current.Messages[13].Role == "user", "v2_current_suffix_positions");
    }
    internal static bool ContainsFact(JsonElement value, int depth = 0)
    {
        Proof.Require(depth <= 32, "v2_fact_scan_bound");
        if (value.ValueKind == JsonValueKind.Object) return value.EnumerateObject().Any(p => ContainsFact(p.Value, depth + 1));
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Any(v => ContainsFact(v, depth + 1));
        if (value.ValueKind != JsonValueKind.String) return false;
        var text = value.GetString()!;
        if (text.Contains(Fact, StringComparison.Ordinal)) return true;
        if (text.Length == 0 || text[0] is not ('{' or '[')) return false;
        try { using var nested = JsonDocument.Parse(text, new() { MaxDepth = 24 }); return ContainsFact(nested.RootElement, depth + 1); }
        catch (JsonException) { return false; }
    }
}
