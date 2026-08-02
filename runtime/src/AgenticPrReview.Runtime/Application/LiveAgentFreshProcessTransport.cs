using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime;

internal sealed record LiveAgentFreshProcessTransportProof(
    bool Succeeded,
    int RequestCount,
    string? SecondProcessFirstRequestSha256,
    string? PriorFactSha256,
    string? ExpectedTerminalSha256)
{
    internal bool IsSatisfiedBy(string? terminalSha256) =>
        Succeeded &&
        LiveAgentFreshProcessDomain.IsSha256(ExpectedTerminalSha256) &&
        StringComparer.Ordinal.Equals(
            ExpectedTerminalSha256,
            terminalSha256);
}

internal sealed class LiveAgentFreshProcessDeterministicTransportFactory(
    string phase,
    IEnumerable<byte[]> currentPublicInputs)
    : IR3LiveAgentTransportFactory,
    IDisposable
{
    private readonly byte[][] publicInputs = currentPublicInputs
        .Select(value => value.ToArray())
        .ToArray();
    private LiveAgentFreshProcessDeterministicTransport? transport;

    internal LiveAgentFreshProcessTransportProof Proof =>
        transport?.Proof ?? new LiveAgentFreshProcessTransportProof(
            false,
            0,
            null,
            null,
            null);

    public IDeepSeekTransport Create(DeepSeekCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (transport is not null)
        {
            throw new InvalidOperationException(
                "The deterministic transport is single-use.");
        }

        transport = new LiveAgentFreshProcessDeterministicTransport(
            phase,
            publicInputs);
        return transport;
    }

    public void Dispose()
    {
        transport?.Dispose();
        foreach (var value in publicInputs)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }
}

internal sealed class LiveAgentFreshProcessDeterministicTransport :
    IDeepSeekTransport
{
    private const string FactPrefix = "APR_PRIOR_ONLY_";
    private readonly string phase;
    private readonly byte[][] currentPublicInputs;
    private string? priorFact;
    private string? priorFactSha256;
    private string? firstRequestSha256;
    private string? expectedTerminalSha256;
    private int requestCount;
    private bool replayValidated;
    private bool terminalUsedFact;
    private bool disposed;

    internal LiveAgentFreshProcessDeterministicTransport(
        string phase,
        IEnumerable<byte[]> currentPublicInputs)
    {
        if (phase is not ("bootstrap" or "continue"))
        {
            throw new ArgumentException(
                "The deterministic transport phase is invalid.",
                nameof(phase));
        }

        this.phase = phase;
        this.currentPublicInputs = currentPublicInputs
            .Select(value => value.ToArray())
            .ToArray();
    }

    internal LiveAgentFreshProcessTransportProof Proof
    {
        get
        {
            var succeeded = requestCount == 2 &&
                terminalUsedFact &&
                (phase == "bootstrap" || replayValidated);
            return new LiveAgentFreshProcessTransportProof(
                succeeded,
                requestCount,
                phase == "continue" ? firstRequestSha256 : null,
                priorFactSha256 ?? (priorFact is null
                    ? null
                    : LiveAgentFreshProcessDomain.RawSha256(
                        Encoding.UTF8.GetBytes(priorFact))),
                expectedTerminalSha256);
        }
    }

    public Task<DeepSeekTransportResult> SendAsync(
        ReadOnlyMemory<byte> requestBody,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<DeepSeekTransportResult>(
                cancellationToken);
        }

        if (requestBody.Length is <= 0 or
            > DeepSeekTransportPolicy.RequestBodyMaxBytes)
        {
            return Task.FromResult(
                DeepSeekTransportResult.RequestRejected());
        }

        var owned = requestBody.ToArray();
        try
        {
            requestCount++;
            if (requestCount > 2)
            {
                return Task.FromResult(
                    DeepSeekTransportResult.TransportFailure());
            }

            if (phase == "continue" && requestCount == 1)
            {
                firstRequestSha256 =
                    LiveAgentFreshProcessDomain.RawSha256(owned);
            }

            return Task.FromResult(
                TryCreateResponse(owned, out var response)
                    ? DeepSeekTransportResult.Success(response!)
                    : DeepSeekTransportResult.TransportFailure());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(owned);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (priorFact is not null)
        {
            priorFactSha256 = LiveAgentFreshProcessDomain.RawSha256(
                Encoding.UTF8.GetBytes(priorFact));
        }
        priorFact = null;
        foreach (var value in currentPublicInputs)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    public override string ToString() =>
        "r3_fresh_process_deterministic_transport";

    private bool TryCreateResponse(byte[] request, out byte[]? response)
    {
        response = null;
        try
        {
            using var document = JsonDocument.Parse(request);
            var root = document.RootElement;
            if (!StringComparer.Ordinal.Equals(
                    root.GetProperty("model").GetString(),
                    DeepSeekAdapterContext.Model) ||
                root.GetProperty("tools").GetArrayLength() != 6)
            {
                return false;
            }

            var messages = root.GetProperty("messages");
            if (phase == "bootstrap")
            {
                return requestCount == 1
                    ? TryBootstrapRead(messages, out response)
                    : TryBootstrapFinish(messages, out response);
            }

            return requestCount == 1
                ? TryContinueRead(messages, out response)
                : TryContinueFinish(messages, out response);
        }
        catch (Exception exception) when (exception is JsonException or
            InvalidOperationException or
            KeyNotFoundException or
            ArgumentException)
        {
            return false;
        }
    }

    private static bool TryBootstrapRead(
        JsonElement messages,
        out byte[]? response)
    {
        response = null;
        if (!Roles(messages).SequenceEqual(
                ["system", "user"],
                StringComparer.Ordinal))
        {
            return false;
        }

        response = Response(
            "Inspect the manifest-backed reviewed file.",
            AgentToolRegistry.ReadFileName,
            "{\"path\":\"fact.txt\"}",
            "bootstrap_read");
        return true;
    }

    private bool TryBootstrapFinish(
        JsonElement messages,
        out byte[]? response)
    {
        response = null;
        if (!Roles(messages).SequenceEqual(
                ["system", "user", "assistant", "tool"],
                StringComparer.Ordinal) ||
            !HasExactToolCall(
                messages[2],
                "Inspect the manifest-backed reviewed file.",
                "bootstrap_read",
                AgentToolRegistry.ReadFileName,
                "{\"path\":\"fact.txt\",\"start_line\":1," +
                    "\"line_count\":400}") ||
            !TryReadLastTool(
                messages,
                "bootstrap_read",
                out var observation,
                out var content) ||
            !TryFindFact(content!, out priorFact))
        {
            return false;
        }

        response = FinishResponse(
            "Remember " + priorFact,
            "grounded " + priorFact,
            observation!,
            "bootstrap_finish");
        terminalUsedFact = true;
        return true;
    }

    private bool TryContinueRead(
        JsonElement messages,
        out byte[]? response)
    {
        response = null;
        if (!TryValidateRestoredMessages(
                messages,
                currentPublicInputs,
                out var restoredFact) ||
            restoredFact is null)
        {
            return false;
        }

        priorFact = restoredFact;
        replayValidated = true;
        response = Response(
            "Ground the continued review in the current snapshot.",
            AgentToolRegistry.ReadFileName,
            "{\"path\":\"fact.txt\"}",
            "continue_read");
        return true;
    }

    internal static bool TryValidateRestoredMessages(
        JsonElement messages,
        IReadOnlyList<byte[]> currentPublicInputs,
        out string? restoredFact)
    {
        restoredFact = null;
        if (!Roles(messages).SequenceEqual(
                [
                    "system",
                    "user",
                    "assistant",
                    "tool",
                    "assistant",
                    "tool",
                    "user",
                ],
                StringComparer.Ordinal) ||
            currentPublicInputs is null)
        {
            return false;
        }

        var values = messages.EnumerateArray().ToArray();
        if (!HasExactToolCall(
                values[2],
                "Inspect the manifest-backed reviewed file.",
                "bootstrap_read",
                AgentToolRegistry.ReadFileName,
                "{\"path\":\"fact.txt\",\"start_line\":1," +
                    "\"line_count\":400}") ||
            !TryReadTool(
                values[3],
                "bootstrap_read",
                out var observation,
                out var historicalTool) ||
            !TryFindFact(historicalTool, out restoredFact) ||
            restoredFact is null)
        {
            return false;
        }

        var fact = restoredFact;
        if (ContainsFact(values[0].GetRawText(), fact) ||
            ContainsFact(values[1].GetRawText(), fact) ||
            ContainsFact(values[^1].GetRawText(), fact) ||
            currentPublicInputs.Any(value => ContainsFact(value, fact)) ||
            !HasNoReasoning(values[0]) ||
            !HasNoReasoning(values[1]) ||
            !HasNoReasoning(values[3]) ||
            !HasNoReasoning(values[5]) ||
            !HasNoReasoning(values[6]) ||
            !HasExactToolCall(
                values[4],
                "Remember " + fact,
                "bootstrap_finish",
                AgentToolRegistry.FinishReviewName,
                FinishArguments("grounded " + fact, observation!)) ||
            !IsExactToolResult(values[5], "bootstrap_finish", "{}"))
        {
            return false;
        }

        return true;
    }

    private bool TryContinueFinish(
        JsonElement messages,
        out byte[]? response)
    {
        response = null;
        if (!replayValidated ||
            priorFact is null ||
            !HasExactToolCall(
                messages.EnumerateArray().ElementAt(
                    messages.GetArrayLength() - 2),
                "Ground the continued review in the current snapshot.",
                "continue_read",
                AgentToolRegistry.ReadFileName,
                "{\"path\":\"fact.txt\",\"start_line\":1," +
                    "\"line_count\":400}") ||
            !TryReadLastTool(
                messages,
                "continue_read",
                out var observation,
                out _))
        {
            return false;
        }

        response = FinishResponse(
            "Use restored fact " + priorFact,
            "continued " + priorFact,
            observation!,
            "continue_finish");
        terminalUsedFact = true;
        return true;
    }

    private static string[] Roles(JsonElement messages) =>
        messages.EnumerateArray()
            .Select(message => message.GetProperty("role").GetString()!)
            .ToArray();

    private static bool HasExactToolCall(
        JsonElement message,
        string reasoning,
        string callId,
        string name,
        string arguments)
    {
        if (!StringComparer.Ordinal.Equals(
                message.GetProperty("role").GetString(),
                "assistant") ||
            !StringComparer.Ordinal.Equals(
                message.GetProperty("content").GetString(),
                string.Empty) ||
            !StringComparer.Ordinal.Equals(
                message.GetProperty("reasoning_content").GetString(),
                reasoning) ||
            !message.TryGetProperty("tool_calls", out var calls) ||
            calls.ValueKind != JsonValueKind.Array ||
            calls.GetArrayLength() != 1)
        {
            return false;
        }

        var call = calls[0];
        var function = call.GetProperty("function");
        return StringComparer.Ordinal.Equals(
                call.GetProperty("id").GetString(),
                callId) &&
            StringComparer.Ordinal.Equals(
                call.GetProperty("type").GetString(),
                "function") &&
            StringComparer.Ordinal.Equals(
                function.GetProperty("name").GetString(),
                name) &&
            StringComparer.Ordinal.Equals(
                function.GetProperty("arguments").GetString(),
                arguments);
    }

    private static bool HasNoReasoning(JsonElement message) =>
        !message.TryGetProperty("reasoning_content", out _);

    private static bool IsExactToolResult(
        JsonElement message,
        string callId,
        string content) =>
        StringComparer.Ordinal.Equals(
            message.GetProperty("role").GetString(),
            "tool") &&
        StringComparer.Ordinal.Equals(
            message.GetProperty("tool_call_id").GetString(),
            callId) &&
        StringComparer.Ordinal.Equals(
            message.GetProperty("content").GetString(),
            content) &&
        HasNoReasoning(message);

    private static bool TryReadLastTool(
        JsonElement messages,
        string expectedCallId,
        out string? observation,
        out string? content)
    {
        observation = null;
        content = null;
        var tool = messages.EnumerateArray()
            .LastOrDefault(message => StringComparer.Ordinal.Equals(
                message.GetProperty("role").GetString(),
                "tool"));
        if (tool.ValueKind == JsonValueKind.Undefined)
        {
            return false;
        }

        return TryReadTool(
            tool,
            expectedCallId,
            out observation,
            out content);
    }

    private static bool TryReadTool(
        JsonElement tool,
        string expectedCallId,
        out string? observation,
        out string? content)
    {
        observation = null;
        content = null;
        if (!StringComparer.Ordinal.Equals(
                tool.GetProperty("role").GetString(),
                "tool") ||
            !StringComparer.Ordinal.Equals(
                tool.GetProperty("tool_call_id").GetString(),
                expectedCallId) ||
            !HasNoReasoning(tool))
        {
            return false;
        }

        content = tool.GetProperty("content").GetString();
        if (content is null)
        {
            return false;
        }

        using var result = JsonDocument.Parse(content);
        observation = result.RootElement
            .GetProperty("observation_id")
            .GetString();
        return LiveAgentFreshProcessDomain.IsSha256(observation);
    }

    private static bool TryFindFact(string? value, out string? fact)
    {
        fact = null;
        if (value is null)
        {
            return false;
        }

        var index = value.IndexOf(FactPrefix, StringComparison.Ordinal);
        if (index < 0 || value.Length < index + FactPrefix.Length + 64)
        {
            return false;
        }

        var candidate = value.Substring(index, FactPrefix.Length + 64);
        if (!candidate.AsSpan(FactPrefix.Length).ToArray().All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            return false;
        }

        fact = candidate;
        return true;
    }

    private static bool ContainsFact(string? value, string fact) =>
        value?.Contains(fact, StringComparison.Ordinal) == true;

    private static bool ContainsFact(byte[] value, string fact) =>
        Encoding.UTF8.GetString(value).Contains(fact, StringComparison.Ordinal);

    private byte[] FinishResponse(
        string reasoning,
        string summary,
        string observation,
        string callId)
    {
        var arguments = FinishArguments(summary, observation);
        var bytes = Encoding.UTF8.GetBytes(arguments);
        try
        {
            expectedTerminalSha256 = AgentCanonical.HashDomain(
                AgentCanonical.TerminalDomain,
                bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        return Response(
            reasoning,
            AgentToolRegistry.FinishReviewName,
            arguments,
            callId);
    }

    private static string FinishArguments(
        string summary,
        string observation)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("summary", summary);
            writer.WriteStartArray("findings");
            writer.WriteStartObject();
            writer.WriteString("severity", "high");
            writer.WriteString("title", "grounded continuation");
            writer.WriteString("message", "The reviewed fact was grounded.");
            writer.WriteStartArray("evidence");
            writer.WriteStartObject();
            writer.WriteString("observation_id", observation);
            writer.WriteString("path", "fact.txt");
            writer.WriteNumber("start_line", 1);
            writer.WriteNumber("end_line", 1);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static byte[] Response(
        string reasoning,
        string toolName,
        string arguments,
        string callId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("choices");
            writer.WriteStartObject();
            writer.WriteNumber("index", 0);
            writer.WriteStartObject("message");
            writer.WriteString("role", "assistant");
            writer.WriteString("content", string.Empty);
            writer.WriteString("reasoning_content", reasoning);
            writer.WriteStartArray("tool_calls");
            writer.WriteStartObject();
            writer.WriteString("id", callId);
            writer.WriteString("type", "function");
            writer.WriteStartObject("function");
            writer.WriteString("name", toolName);
            writer.WriteString("arguments", arguments);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteString("finish_reason", "tool_calls");
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteString("model", DeepSeekAdapterContext.Model);
            writer.WriteStartObject("usage");
            writer.WriteNumber("prompt_tokens", 3);
            writer.WriteNumber("completion_tokens", 2);
            writer.WriteNumber("total_tokens", 5);
            writer.WriteNumber("prompt_cache_hit_tokens", 1);
            writer.WriteNumber("prompt_cache_miss_tokens", 2);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}
