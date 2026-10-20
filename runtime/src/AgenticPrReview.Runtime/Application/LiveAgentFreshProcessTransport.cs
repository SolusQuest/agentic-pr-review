using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime;

// Historical authored calls have their own proof. The shared transport proof
// describes actual Solus HTTP bytes and is not repurposed by this fixture seam.
internal sealed record LiveAgentFreshProcessScriptProof(bool Succeeded, int RequestCount,
    string? FirstTypedRequestSha256, string? PriorFactSha256, string? ExpectedTerminalSha256)
    : ILiveAgentFreshProcessProof
{
    public string? SecondProcessFirstRequestSha256 => null;
    public bool IsSatisfiedBy(string? terminalSha256) => Succeeded &&
        LiveAgentFreshProcessDomain.IsSha256(ExpectedTerminalSha256) &&
        StringComparer.Ordinal.Equals(ExpectedTerminalSha256, terminalSha256);
}

internal sealed class LiveAgentFreshProcessDeterministicChatFactory(string phase,
    IEnumerable<byte[]> currentPublicInputs) : IR3LiveAgentChatFactory, IDisposable
{
    private readonly byte[][] publicInputs = currentPublicInputs.Select(value => value.ToArray()).ToArray();
    private LiveAgentFreshProcessDeterministicChat? chat;
    internal LiveAgentFreshProcessScriptProof Proof => chat?.Proof ?? new(false, 0, null, null, null);
    public IProjectChatClient Create(DeepSeekAdapterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (chat is not null) throw new InvalidOperationException("Historical script is single-use.");
        chat = new(phase, publicInputs, context);
        return chat;
    }
    public void Dispose()
    {
        chat?.Dispose();
        foreach (var value in publicInputs) CryptographicOperations.ZeroMemory(value);
    }
}

// Two fixed read/finish calls exercise the real legacy loop/SESSION writer only.
// This class neither serializes DeepSeek requests nor parses provider responses.
internal sealed class LiveAgentFreshProcessDeterministicChat : IProjectChatClient, IDisposable
{
    private const string FactPrefix = "APR_PRIOR_ONLY_";
    private const string ReadArguments = "{\"path\":\"fact.txt\",\"start_line\":1,\"line_count\":800}";
    private readonly string phase;
    private readonly byte[][] publicInputs;
    private readonly DeepSeekAdapterContext context;
    private string? fact;
    private string? factHash;
    private string? firstTypedHash;
    private string? terminalHash;
    private int calls;
    private bool replayValidated;
    private bool terminalUsedFact;
    private bool disposed;

    internal LiveAgentFreshProcessDeterministicChat(string phase, IEnumerable<byte[]> publicInputs,
        DeepSeekAdapterContext context)
    {
        if (phase is not ("bootstrap" or "continue")) throw new ArgumentException("Historical script phase is invalid.");
        this.phase = phase;
        this.publicInputs = publicInputs.Select(value => value.ToArray()).ToArray();
        this.context = context;
    }
    internal LiveAgentFreshProcessScriptProof Proof => new(calls == 2 && terminalUsedFact &&
        (phase == "bootstrap" || replayValidated), calls, firstTypedHash,
        factHash ?? (fact is null ? null : Hash(fact)), terminalHash);

    public Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        token.ThrowIfCancellationRequested();
        if (++calls > 2) throw new InvalidOperationException("Historical script exhausted.");
        if (phase == "continue" && calls == 1)
        {
            var trace = AgentRequestWriter.Write(request);
            try { firstTypedHash = LiveAgentFreshProcessDomain.RawSha256(trace); }
            finally { CryptographicOperations.ZeroMemory(trace); }
        }
        var messages = ReadMessages(request);
        ProjectChatResponse response;
        if (phase == "bootstrap" && calls == 1)
        {
            Require(messages.Select(message => message.Role).SequenceEqual(["system", "user"]));
            response = Response(request, "Inspect the manifest-backed reviewed file.", "bootstrap_read",
                AgentToolRegistry.ReadFileName, "{\"path\":\"fact.txt\"}");
        }
        else if (phase == "bootstrap")
        {
            Require(messages.Select(message => message.Role).SequenceEqual(["system", "user", "assistant", "tool"]));
            Require(HasExactCall(messages[2], "Inspect the manifest-backed reviewed file.", "bootstrap_read",
                AgentToolRegistry.ReadFileName, ReadArguments));
            Require(TryReadTool(messages[^1], "bootstrap_read", out var observation, out var result));
            Require(TryFindFact(result, out fact));
            response = Finish(request, "Remember " + fact, "grounded " + fact, observation!, "bootstrap_finish");
        }
        else if (calls == 1)
        {
            Require(TryValidateRestoredMessages(messages, publicInputs, out fact));
            replayValidated = true;
            response = Response(request, "Ground the continued review in the current snapshot.", "continue_read",
                AgentToolRegistry.ReadFileName, "{\"path\":\"fact.txt\"}");
        }
        else
        {
            Require(replayValidated && fact is not null && messages.Length >= 2);
            Require(HasExactCall(messages[^2], "Ground the continued review in the current snapshot.",
                "continue_read", AgentToolRegistry.ReadFileName, ReadArguments));
            Require(TryReadTool(messages[^1], "continue_read", out var observation, out _));
            response = Finish(request, "Use restored fact " + fact, "continued " + fact, observation!, "continue_finish");
        }
        if (request.Accounting is { } accounting)
        {
            Require(accounting.TryBeginDispatch());
            accounting.RecordUsage(ProviderUsageObservation.Create(3, 2, 1, 2, 5));
        }
        return Task.FromResult(response);
    }

    private static ProjectChatMessage[] ReadMessages(ProjectChatRequest request) =>
        request.Messages.Select((message, position) =>
        {
            var items = request.Continuation?.Items.Where(item => item.MessagePosition == position).ToArray() ?? [];
            if (message.Role != "assistant") { Require(items.Length == 0); return message; }
            Require(items is [var _]);
            var item = items[0];
            Require(item.ContentPosition == 0 && item.AssociatedCallId is null && item.Opaque == "" &&
                item.Framing == DeepSeekReasoningContinuationCodec.FramingName);
            var inline = message.Contents.OfType<ProjectReasoningContent>().ToArray();
            Require(inline.Length <= 1 && (inline.Length == 0 || inline[0] == new ProjectReasoningContent(
                item.Readable, item.Opaque, item.Framing, item.AssociatedCallId, position, item.ContentPosition)));
            return message with { Contents = [new ProjectReasoningContent(item.Readable, item.Opaque, item.Framing,
                item.AssociatedCallId, position, item.ContentPosition), .. message.Contents.Where(part => part is not ProjectReasoningContent)] };
        }).ToArray();

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

    private ProjectChatResponse Finish(ProjectChatRequest request, string reasoning, string summary,
        string observation, string id)
    {
        var arguments = FinishArguments(summary, observation);
        var bytes = Encoding.UTF8.GetBytes(arguments);
        try { terminalHash = AgentCanonical.HashDomain(AgentCanonical.TerminalDomain, bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        terminalUsedFact = true;
        return Response(request, reasoning, id, AgentToolRegistry.FinishReviewName, arguments);
    }
    private ProjectChatResponse Response(ProjectChatRequest request, string reasoning, string id, string name, string arguments)
    {
        var position = request.Messages.Length;
        return new(new("assistant", [new ProjectReasoningContent(reasoning, "", DeepSeekReasoningContinuationCodec.FramingName,
            null, position, 0), new ProjectToolCallContent(id, name, arguments)]),
            new(3, 2, new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Model, 1, 2)),
            CapturedResponseBodyBytes: 0, Continuation: new(context.ProviderId, context.ModelId, context.AdapterId, context.SessionId,
                [new(reasoning, "", DeepSeekReasoningContinuationCodec.FramingName, null, position, 0)]));
    }
    private static bool HasExactCall(ProjectChatMessage message, string reasoning, string id, string name, string arguments) =>
        message.Role == "assistant" && message.Contents is [ProjectReasoningContent r, ProjectToolCallContent call] &&
        r.Text == reasoning && r.Opaque == "" && r.Framing == DeepSeekReasoningContinuationCodec.FramingName &&
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
    private static string Hash(string value) => LiveAgentFreshProcessDomain.RawSha256(Encoding.UTF8.GetBytes(value));
    private static void Require(bool valid) { if (!valid) throw new ProjectChatNormalizationException(); }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (fact is not null) factHash = Hash(fact);
        fact = null;
        foreach (var value in publicInputs) CryptographicOperations.ZeroMemory(value);
    }
    public override string ToString() => "historical_authored_chat";
}
