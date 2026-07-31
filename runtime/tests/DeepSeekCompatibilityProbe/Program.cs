using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.DeepSeekCompatibilityProbe;

internal static class Program
{
    private const string SecretName = "AGENTIC_REVIEW_DEEPSEEK_API_KEY";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0)
        {
            Console.WriteLine("APR_DEEPSEEK_PROBE_ARGUMENT_INVALID");
            return 2;
        }

        var secret = Environment.GetEnvironmentVariable(SecretName);
        DeepSeekCredential credential;
        try
        {
            credential = DeepSeekCredential.Create(secret!);
        }
        catch (Exception exception) when (
            exception is ArgumentException or ArgumentNullException)
        {
            Console.WriteLine("APR_DEEPSEEK_PROBE_CONFIG_INVALID");
            return 20;
        }

        using var transport = DeepSeekTransport.Create(credential);
        using var deadline = new CancellationTokenSource(
            TimeSpan.FromSeconds(270));
        var result = await DeepSeekCompatibilityProbeRunner.RunAsync(
            transport,
            deadline.Token);
        Console.WriteLine(result);
        return result.Succeeded ? 0 : 30;
    }
}

internal static class DeepSeekCompatibilityProbeRunner
{
    internal const string ToolName = "compatibility_echo";
    internal const string ToolResult = "{\"value\":\"probe\"}";

    internal static async Task<DeepSeekCompatibilityProbeResult> RunAsync(
        IDeepSeekTransport transport,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var firstRequest = BuildInitialRequest();
        var firstProjection = DeepSeekRequestWriter.Write(firstRequest);
        if (firstProjection.Outcome != DeepSeekRequestWriteOutcome.Success)
        {
            return DeepSeekCompatibilityProbeResult.Failure(
                "APR_DEEPSEEK_PROBE_FIRST_PROJECTION_FAILED");
        }

        var firstResponse = await transport.SendAsync(
            firstProjection.Body.ToArray(),
            cancellationToken);
        if (firstResponse.Outcome != DeepSeekTransportOutcome.Success ||
            !firstResponse.HasBody ||
            !TryInspectFirstResponse(firstResponse.Body, out var turn))
        {
            return DeepSeekCompatibilityProbeResult.Failure(
                "APR_DEEPSEEK_PROBE_FIRST_RESPONSE_INVALID");
        }

        var secondRequest = BuildContinuationRequest(firstRequest, turn!);
        var secondProjection = DeepSeekRequestWriter.Write(secondRequest);
        if (secondProjection.Outcome != DeepSeekRequestWriteOutcome.Success ||
            !HasExpectedReplay(secondProjection.Body, turn!))
        {
            return DeepSeekCompatibilityProbeResult.Failure(
                "APR_DEEPSEEK_PROBE_REPLAY_INVALID");
        }

        var secondResponse = await transport.SendAsync(
            secondProjection.Body.ToArray(),
            cancellationToken);
        if (secondResponse.Outcome != DeepSeekTransportOutcome.Success)
        {
            return DeepSeekCompatibilityProbeResult.Failure(
                "APR_DEEPSEEK_PROBE_SECOND_RESPONSE_INVALID");
        }

        return DeepSeekCompatibilityProbeResult.Success(
            firstProjection.Body,
            secondProjection.Body,
            turn!.Calls.Length);
    }

    private static MinimalChatRequest BuildInitialRequest() => BuildRequest(
        [
            new MinimalChatMessage(
                "system",
                [Text("Use the supplied compatibility tool exactly once.")]),
            new MinimalChatMessage(
                "user",
                [Text("Call compatibility_echo with value probe. Do not answer directly.")]),
        ],
        null);

    private static MinimalChatRequest BuildContinuationRequest(
        MinimalChatRequest initial,
        ProbeAssistantTurn turn)
    {
        var assistant = new List<MinimalChatContent>
        {
            Reasoning(turn.Reasoning, turn.Calls[0].Id),
        };
        if (turn.Content.Length > 0)
        {
            assistant.Add(Text(turn.Content));
        }

        assistant.AddRange(turn.Calls.Select(call =>
            Call(call.Id, call.Name, call.Arguments)));
        var messages = initial.Messages
            .Concat([
                new MinimalChatMessage("assistant", assistant.ToArray()),
                .. turn.Calls.Select(call => new MinimalChatMessage(
                    "tool",
                    [Result(call.Id, ToolResult)])),
            ])
            .ToArray();
        var assistantPosition = initial.Messages.Length;
        var continuation = new MinimalChatContinuation(
            "deepseek",
            DeepSeekRequestWriter.Model,
            "probe-only",
            "probe-session",
            [
                new MinimalChatContinuationItem(
                    turn.Reasoning,
                    "probe-opaque-not-on-wire",
                    "deepseek-v4",
                    turn.Calls[0].Id,
                    assistantPosition,
                    0),
            ]);
        return BuildRequest(messages, continuation);
    }

    private static MinimalChatRequest BuildRequest(
        MinimalChatMessage[] messages,
        MinimalChatContinuation? continuation) => new(
        messages.Select((message, messageIndex) => message with
        {
            Contents = message.Contents.Select((content, contentIndex) =>
                content with
                {
                    MessagePosition = messageIndex,
                    Position = contentIndex,
                }).ToArray(),
        }).ToArray(),
        [
            new MinimalChatTool(
                ToolName,
                "Return the supplied synthetic value.",
                "{\"type\":\"object\",\"properties\":{\"value\":{" +
                "\"type\":\"string\",\"enum\":[\"probe\"]}}," +
                "\"required\":[\"value\"],\"additionalProperties\":false}"),
        ],
        continuation,
        ThinkingRequired: true);

    private static bool TryInspectFirstResponse(
        ImmutableArray<byte> body,
        out ProbeAssistantTurn? turn)
    {
        turn = null;
        try
        {
            var bytes = body.ToArray();
            if (HasDuplicateProperties(bytes))
            {
                return false;
            }

            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() != 1)
            {
                return false;
            }

            var choice = choices[0];
            if (!RequiredInt(choice, "index", 0) ||
                !RequiredString(choice, "finish_reason", "tool_calls") ||
                !choice.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.Object ||
                !RequiredString(message, "role", "assistant") ||
                !TryReadUtf8String(
                    message,
                    "content",
                    0,
                    64 * 1024,
                    out var content) ||
                !TryReadUtf8String(
                    message,
                    "reasoning_content",
                    1,
                    64 * 1024,
                    out var reasoning) ||
                !message.TryGetProperty("tool_calls", out var callsElement) ||
                callsElement.ValueKind != JsonValueKind.Array ||
                callsElement.GetArrayLength() != 1 ||
                !TryReadCall(callsElement[0], out var call))
            {
                return false;
            }

            turn = new ProbeAssistantTurn(content!, reasoning!, [call!]);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadCall(
        JsonElement value,
        out ProbeToolCall? call)
    {
        call = null;
        if (value.ValueKind != JsonValueKind.Object ||
            !TryReadUtf8String(value, "id", 1, 64, out var id) ||
            !ValidIdentifier(id!) ||
            !RequiredString(value, "type", "function") ||
            !value.TryGetProperty("function", out var function) ||
            function.ValueKind != JsonValueKind.Object ||
            !TryReadUtf8String(function, "name", 1, 64, out var name) ||
            !StringComparer.Ordinal.Equals(name, ToolName) ||
            !TryReadUtf8String(
                function,
                "arguments",
                0,
                8 * 1024,
                out var arguments))
        {
            return false;
        }

        call = new ProbeToolCall(id!, name!, arguments!);
        return true;
    }

    private static bool HasExpectedReplay(
        ImmutableArray<byte> body,
        ProbeAssistantTurn turn)
    {
        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            var root = document.RootElement;
            if (root.TryGetProperty("tool_choice", out _) ||
                !root.TryGetProperty("messages", out var messages) ||
                messages.ValueKind != JsonValueKind.Array ||
                messages.GetArrayLength() < 4)
            {
                return false;
            }

            var assistant = messages[messages.GetArrayLength() - 2];
            return assistant.GetProperty("content").ValueKind ==
                    JsonValueKind.String &&
                StringComparer.Ordinal.Equals(
                    assistant.GetProperty("content").GetString(),
                    turn.Content) &&
                StringComparer.Ordinal.Equals(
                    assistant.GetProperty("reasoning_content").GetString(),
                    turn.Reasoning) &&
                !Encoding.UTF8.GetString(body.AsSpan())
                    .Contains("probe-opaque-not-on-wire", StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or JsonException)
        {
            return false;
        }
    }

    private static bool TryReadUtf8String(
        JsonElement value,
        string property,
        int minimumBytes,
        int maximumBytes,
        out string? result)
    {
        result = null;
        if (!value.TryGetProperty(property, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = element.GetString()!;
        try
        {
            var bytes = new UTF8Encoding(false, true).GetByteCount(candidate);
            if (bytes < minimumBytes || bytes > maximumBytes)
            {
                return false;
            }
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        result = candidate;
        return true;
    }

    private static bool RequiredString(
        JsonElement value,
        string property,
        string expected) =>
        value.TryGetProperty(property, out var element) &&
        element.ValueKind == JsonValueKind.String &&
        StringComparer.Ordinal.Equals(element.GetString(), expected);

    private static bool RequiredInt(
        JsonElement value,
        string property,
        int expected) =>
        value.TryGetProperty(property, out var element) &&
        element.ValueKind == JsonValueKind.Number &&
        element.TryGetInt32(out var actual) &&
        actual == expected;

    private static bool HasDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var reader = new Utf8JsonReader(
                bytes,
                isFinalBlock: true,
                state: default);
            var scopes = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                }
                else if (reader.TokenType == JsonTokenType.EndObject)
                {
                    if (scopes.Count == 0)
                    {
                        return true;
                    }

                    scopes.Pop();
                }
                else if (reader.TokenType == JsonTokenType.PropertyName &&
                    (scopes.Count == 0 ||
                    !scopes.Peek().Add(reader.GetString()!)))
                {
                    return true;
                }
            }

            return scopes.Count != 0;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static bool ValidIdentifier(string value)
    {
        foreach (var character in value)
        {
            if (!(character is >= 'A' and <= 'Z') &&
                !(character is >= 'a' and <= 'z') &&
                !(character is >= '0' and <= '9') &&
                character is not '_' and not '-')
            {
                return false;
            }
        }

        return value.Length is >= 1 and <= 64;
    }

    private static MinimalChatContent Text(string value) => new(
        "text", null, null, value, null, null, null, 0, 0);

    private static MinimalChatContent Reasoning(
        string value,
        string associatedCallId) => new(
        "reasoning",
        null,
        null,
        value,
        "probe-opaque-not-on-wire",
        "deepseek-v4",
        associatedCallId,
        0,
        0);

    private static MinimalChatContent Call(
        string id,
        string name,
        string arguments) => new(
        "tool_call", id, name, arguments, null, null, null, 0, 0);

    private static MinimalChatContent Result(string id, string value) => new(
        "tool_result", id, null, value, null, null, null, 0, 0);
}

internal sealed record ProbeAssistantTurn(
    string Content,
    string Reasoning,
    ProbeToolCall[] Calls);

internal sealed record ProbeToolCall(
    string Id,
    string Name,
    string Arguments);

internal sealed class DeepSeekCompatibilityProbeResult
{
    private DeepSeekCompatibilityProbeResult(
        bool succeeded,
        string code,
        int? firstRequestBytes,
        int? secondRequestBytes,
        string? firstRequestSha256,
        string? secondRequestSha256,
        int? toolCalls)
    {
        Succeeded = succeeded;
        Code = code;
        FirstRequestBytes = firstRequestBytes;
        SecondRequestBytes = secondRequestBytes;
        FirstRequestSha256 = firstRequestSha256;
        SecondRequestSha256 = secondRequestSha256;
        ToolCalls = toolCalls;
    }

    internal bool Succeeded { get; }
    internal string Code { get; }
    internal int? FirstRequestBytes { get; }
    internal int? SecondRequestBytes { get; }
    internal string? FirstRequestSha256 { get; }
    internal string? SecondRequestSha256 { get; }
    internal int? ToolCalls { get; }

    internal static DeepSeekCompatibilityProbeResult Failure(string code) =>
        new(false, code, null, null, null, null, null);

    internal static DeepSeekCompatibilityProbeResult Success(
        ImmutableArray<byte> firstRequest,
        ImmutableArray<byte> secondRequest,
        int toolCalls) => new(
        true,
        "APR_DEEPSEEK_PROBE_OK",
        firstRequest.Length,
        secondRequest.Length,
        Hash(firstRequest),
        Hash(secondRequest),
        toolCalls);

    public override string ToString() => Succeeded
        ? string.Concat(
            Code,
            " model=",
            DeepSeekRequestWriter.Model,
            " first_request_bytes=",
            FirstRequestBytes,
            " second_request_bytes=",
            SecondRequestBytes,
            " first_request_sha256=",
            FirstRequestSha256,
            " second_request_sha256=",
            SecondRequestSha256,
            " tool_calls=",
            ToolCalls,
            " assistant_content_non_null=true reasoning_replayed=true")
        : Code;

    private static string Hash(ImmutableArray<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value.AsSpan())).ToLowerInvariant();
}
