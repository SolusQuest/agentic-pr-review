using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

// Readonly seven-message authored fixture predicate; no SESSION bytes, SDK import or restore authority.
internal static class AuthoredReplayShapePredicate
{
    private const string FactPrefix = "APR_PRIOR_ONLY_";
    private const string ReadArguments = "{\"path\":\"fact.txt\",\"start_line\":1,\"line_count\":800}";

    internal static bool TryValidateRestoredMessages(ProjectChatMessage[] messages,
        IReadOnlyList<byte[]> publicInputs, out string? restoredFact)
    {
        restoredFact = null;
        if (messages is null || publicInputs is null || !messages.Select(message => message.Role).SequenceEqual(
                ["system", "user", "assistant", "tool", "assistant", "tool", "user"]) ||
            !HasExactCall(messages[2], "Inspect the manifest-backed reviewed file.", "bootstrap_read", AgentToolRegistry.ReadFileName, ReadArguments) ||
            !TryReadTool(messages[3], "bootstrap_read", out var observation, out var content) ||
            !TryFindFact(content, out restoredFact)) return false;
        var fact = restoredFact!;
        if (new[] { messages[0], messages[1], messages[^1] }.Any(message => message.Contents.Any(part =>
                part is ProjectTextContent text && text.Text.Contains(fact, StringComparison.Ordinal))) ||
            publicInputs.Any(value => Encoding.UTF8.GetString(value).Contains(fact, StringComparison.Ordinal)) ||
            !HasExactCall(messages[4], "Remember " + fact, "bootstrap_finish", AgentToolRegistry.FinishReviewName,
                FinishArguments("grounded " + fact, observation!)) ||
            !TryReadTool(messages[5], "bootstrap_finish", out _, out var finish, requireObservation: false) || finish != "{}") return false;
        return new[] { 0, 1, 3, 5, 6 }.All(index => messages[index].Contents.All(part => part is not ProjectReasoningContent));
    }
    private static bool HasExactCall(ProjectChatMessage message, string reasoning, string id, string name, string arguments) =>
        message.Role == "assistant" && message.Contents is [ProjectReasoningContent r, ProjectToolCallContent call] &&
        r.Text == reasoning && r.Opaque == "" && r.Framing == DeepSeekAdapterContext.ReasoningFraming &&
        r.AssociatedCallId is null && call.CallId == id && call.Name == name && call.ArgumentsJson == arguments;

    private static bool TryReadTool(ProjectChatMessage message, string id, out string? observation,
        out string? content, bool requireObservation = true)
    {
        observation = null; content = null;
        if (message.Role != "tool" || message.Contents is not [ProjectToolResultContent result] || result.CallId != id) return false;
        content = result.Result;
        if (!requireObservation) return true;
        try
        {
            using var json = JsonDocument.Parse(content);
            observation = json.RootElement.GetProperty("observation_id").GetString();
            return LiveAgentFreshProcessDomain.IsSha256(observation);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
    }
    private static bool TryFindFact(string? value, out string? found)
    {
        found = null;
        if (value is null) return false;
        var index = value.IndexOf(FactPrefix, StringComparison.Ordinal);
        if (index < 0 || value.Length < index + FactPrefix.Length + 64) return false;
        var candidate = value.Substring(index, FactPrefix.Length + 64);
        if (!candidate.AsSpan(FactPrefix.Length).ToArray().All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        found = candidate; return true;
    }
    private static string FinishArguments(string summary, string observation)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteString("summary", summary); writer.WriteStartArray("findings");
            writer.WriteStartObject(); writer.WriteString("severity", "high"); writer.WriteString("title", "grounded continuation");
            writer.WriteString("message", "The reviewed fact was grounded."); writer.WriteStartArray("evidence");
            writer.WriteStartObject(); writer.WriteString("observation_id", observation); writer.WriteString("path", "fact.txt");
            writer.WriteNumber("start_line", 1); writer.WriteNumber("end_line", 1); writer.WriteEndObject();
            writer.WriteEndArray(); writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
