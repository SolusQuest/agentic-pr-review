using System.Collections.Immutable;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;

namespace AgenticPrReview.Runtime.Execution.DeepSeek;

internal enum DeepSeekResponseParseOutcome
{
    Invalid,
    MissingTool,
    Success,
}

internal sealed class DeepSeekResponseParseResult
{
    private DeepSeekResponseParseResult(
        DeepSeekResponseParseOutcome outcome,
        DeepSeekParsedToolResponse? response)
    {
        if (outcome == DeepSeekResponseParseOutcome.Success !=
            (response is not null))
        {
            throw new ArgumentException("The parse result state is invalid.");
        }

        Outcome = outcome;
        Response = response;
    }

    internal DeepSeekResponseParseOutcome Outcome { get; }
    internal DeepSeekParsedToolResponse? Response { get; }

    internal static DeepSeekResponseParseResult Invalid() => new(
        DeepSeekResponseParseOutcome.Invalid,
        null);

    internal static DeepSeekResponseParseResult MissingTool() => new(
        DeepSeekResponseParseOutcome.MissingTool,
        null);

    internal static DeepSeekResponseParseResult Success(
        DeepSeekParsedToolResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new DeepSeekResponseParseResult(
            DeepSeekResponseParseOutcome.Success,
            response);
    }

    public override string ToString() => Outcome switch
    {
        DeepSeekResponseParseOutcome.Invalid => "invalid",
        DeepSeekResponseParseOutcome.MissingTool => "missing_tool",
        DeepSeekResponseParseOutcome.Success => "success",
        _ => nameof(DeepSeekResponseParseResult),
    };
}

internal sealed class DeepSeekParsedToolResponse
{
    internal DeepSeekParsedToolResponse(
        string content,
        ImmutableArray<DeepSeekParsedToolCall> calls,
        string reasoning,
        DeepSeekParsedUsage usage,
        int capturedBytes)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(reasoning);
        ArgumentNullException.ThrowIfNull(usage);
        if (calls.IsDefaultOrEmpty ||
            calls.Length > AgentLimits.ToolCallsPerResponse ||
            calls.Any(call => call is null) ||
            !AgentValueDomains.IsUtf8(
                content,
                0,
                AgentLimits.ContentBytes) ||
            !AgentValueDomains.IsUtf8(
                reasoning,
                1,
                AgentLimits.ContentBytes) ||
            capturedBytes is < 0 or >
                DeepSeekTransportPolicy.SuccessBodyMaxBytes)
        {
            throw new ArgumentException(
                "The parsed DeepSeek response state is invalid.");
        }

        Content = content;
        Calls = calls;
        Reasoning = reasoning;
        Usage = usage;
        CapturedBytes = capturedBytes;
    }

    internal string Content { get; }
    internal ImmutableArray<DeepSeekParsedToolCall> Calls { get; }
    internal string Reasoning { get; }
    internal DeepSeekParsedUsage Usage { get; }
    internal int CapturedBytes { get; }

    public override string ToString() =>
        $"deepseek_tool_response(call_count={Calls.Length}," +
        $"captured_bytes={CapturedBytes})";
}

internal sealed class DeepSeekParsedToolCall
{
    internal DeepSeekParsedToolCall(
        string id,
        string name,
        string arguments)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!AgentValueDomains.IsIdentifier(id) ||
            !AgentValueDomains.IsIdentifier(name) ||
            !AgentValueDomains.IsUtf8(
                arguments,
                0,
                AgentLimits.ToolArgumentsBytes))
        {
            throw new ArgumentException(
                "The parsed DeepSeek tool call is invalid.");
        }

        Id = id;
        Name = name;
        Arguments = arguments;
    }

    internal string Id { get; }
    internal string Name { get; }
    internal string Arguments { get; }

    public override string ToString() => "deepseek_tool_call";
}

internal sealed class DeepSeekParsedUsage
{
    internal DeepSeekParsedUsage(long inputTokens, long outputTokens)
    {
        if (inputTokens < 0 || outputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inputTokens),
                "Token totals must be nonnegative.");
        }

        InputTokens = inputTokens;
        OutputTokens = outputTokens;
    }

    internal long InputTokens { get; }
    internal long OutputTokens { get; }

    public override string ToString() => "deepseek_usage";
}

internal static class DeepSeekResponseParser
{
    private const int IgnoredStringMaxBytes = 256;

    private static readonly string[] RootProperties =
    [
        "choices",
        "model",
        "usage",
        "id",
        "object",
        "created",
        "system_fingerprint",
    ];

    private static readonly string[] ChoiceProperties =
    [
        "index",
        "message",
        "finish_reason",
        "logprobs",
    ];

    private static readonly string[] MessageProperties =
    [
        "role",
        "content",
        "reasoning_content",
        "tool_calls",
    ];

    private static readonly string[] ToolCallProperties =
    [
        "id",
        "index",
        "type",
        "function",
    ];

    private static readonly string[] FunctionProperties =
    [
        "name",
        "arguments",
    ];

    private static readonly string[] UsageProperties =
    [
        "prompt_tokens",
        "completion_tokens",
        "total_tokens",
        "prompt_cache_hit_tokens",
        "prompt_cache_miss_tokens",
        "prompt_tokens_details",
        "completion_tokens_details",
    ];

    internal static DeepSeekResponseParseResult Parse(
        DeepSeekTransportResult? transportResult)
    {
        if (transportResult is null ||
            transportResult.Outcome != DeepSeekTransportOutcome.Success ||
            !transportResult.HasBody ||
            transportResult.CapturedCount != transportResult.Body.Length ||
            transportResult.Body.Length >
                DeepSeekTransportPolicy.SuccessBodyMaxBytes)
        {
            return DeepSeekResponseParseResult.Invalid();
        }

        try
        {
            var body = transportResult.Body.ToArray();
            if (!HasUniquePropertyNames(body))
            {
                return DeepSeekResponseParseResult.Invalid();
            }

            using var document = JsonDocument.Parse(
                body,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64,
                });
            return ParseRoot(
                document.RootElement,
                transportResult.CapturedCount.Value);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidOperationException or
            JsonException or
            OverflowException)
        {
            return DeepSeekResponseParseResult.Invalid();
        }
    }

    private static DeepSeekResponseParseResult ParseRoot(
        JsonElement root,
        int capturedBytes)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !HasOnlyProperties(root, RootProperties) ||
            !TryReadExactString(root, "model", DeepSeekRequestWriter.Model) ||
            !ValidateOptionalRootFields(root) ||
            !root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() != 1 ||
            !TryReadUsage(root, out var usage))
        {
            return DeepSeekResponseParseResult.Invalid();
        }

        var choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object ||
            !HasOnlyProperties(choice, ChoiceProperties) ||
            !TryReadNonnegativeInt64(choice, "index", out var index) ||
            index != 0 ||
            choice.TryGetProperty("logprobs", out var logprobs) &&
            logprobs.ValueKind != JsonValueKind.Null ||
            !choice.TryGetProperty("message", out var message))
        {
            return DeepSeekResponseParseResult.Invalid();
        }

        if (TryReadExactString(choice, "finish_reason", "tool_calls") &&
            TryReadToolMessage(
                message,
                out var content,
                out var reasoning,
                out var calls))
        {
            return DeepSeekResponseParseResult.Success(
                new DeepSeekParsedToolResponse(
                    content!,
                    calls,
                    reasoning!,
                    usage!,
                    capturedBytes));
        }

        return TryReadExactString(choice, "finish_reason", "stop") &&
            IsValidNoToolMessage(message)
            ? DeepSeekResponseParseResult.MissingTool()
            : DeepSeekResponseParseResult.Invalid();
    }

    private static bool ValidateOptionalRootFields(JsonElement root)
    {
        if (root.TryGetProperty("id", out var id) &&
            !TryReadUtf8String(
                id,
                1,
                IgnoredStringMaxBytes,
                out _))
        {
            return false;
        }

        if (root.TryGetProperty("object", out var @object) &&
            (@object.ValueKind != JsonValueKind.String ||
             !StringComparer.Ordinal.Equals(
                 @object.GetString(),
                 "chat.completion")))
        {
            return false;
        }

        if (root.TryGetProperty("created", out var created) &&
            !TryReadNonnegativeInt64(created, out _))
        {
            return false;
        }

        if (root.TryGetProperty(
                "system_fingerprint",
                out var fingerprint) &&
            fingerprint.ValueKind != JsonValueKind.Null &&
            !TryReadUtf8String(
                fingerprint,
                0,
                IgnoredStringMaxBytes,
                out _))
        {
            return false;
        }

        return true;
    }

    private static bool TryReadToolMessage(
        JsonElement message,
        out string? content,
        out string? reasoning,
        out ImmutableArray<DeepSeekParsedToolCall> calls)
    {
        content = null;
        reasoning = null;
        calls = default;
        if (message.ValueKind != JsonValueKind.Object ||
            !HasOnlyProperties(message, MessageProperties) ||
            !TryReadExactString(message, "role", "assistant") ||
            !message.TryGetProperty("content", out var contentElement) ||
            !TryReadNullableUtf8StringAsEmpty(
                contentElement,
                AgentLimits.ContentBytes,
                out content) ||
            !message.TryGetProperty(
                "reasoning_content",
                out var reasoningElement) ||
            !TryReadUtf8String(
                reasoningElement,
                1,
                AgentLimits.ContentBytes,
                out reasoning) ||
            !message.TryGetProperty("tool_calls", out var toolCalls) ||
            toolCalls.ValueKind != JsonValueKind.Array ||
            toolCalls.GetArrayLength() is < 1 or >
                AgentLimits.ToolCallsPerResponse)
        {
            return false;
        }

        var builder = ImmutableArray.CreateBuilder<DeepSeekParsedToolCall>(
            toolCalls.GetArrayLength());
        for (var index = 0; index < toolCalls.GetArrayLength(); index++)
        {
            if (!TryReadToolCall(toolCalls[index], index, out var parsed))
            {
                return false;
            }

            builder.Add(parsed!);
        }

        calls = builder.MoveToImmutable();
        return true;
    }

    private static bool IsValidNoToolMessage(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object ||
            !HasOnlyProperties(message, MessageProperties) ||
            !TryReadExactString(message, "role", "assistant") ||
            !message.TryGetProperty("content", out var content) ||
            !IsNullableUtf8String(content, AgentLimits.ContentBytes))
        {
            return false;
        }

        if (message.TryGetProperty("reasoning_content", out var reasoning) &&
            !IsNullableUtf8String(reasoning, AgentLimits.ContentBytes))
        {
            return false;
        }

        if (!message.TryGetProperty("tool_calls", out var calls))
        {
            return true;
        }

        return calls.ValueKind == JsonValueKind.Null ||
            calls.ValueKind == JsonValueKind.Array &&
            calls.GetArrayLength() == 0;
    }

    private static bool TryReadToolCall(
        JsonElement toolCall,
        int expectedIndex,
        out DeepSeekParsedToolCall? parsed)
    {
        parsed = null;
        if (toolCall.ValueKind != JsonValueKind.Object ||
            !HasOnlyProperties(toolCall, ToolCallProperties) ||
            (toolCall.TryGetProperty("index", out var index) &&
             (!TryReadNonnegativeInt64(index, out var providerIndex) ||
              providerIndex != expectedIndex)) ||
            !TryReadIdentifier(toolCall, "id", out var id) ||
            !TryReadExactString(toolCall, "type", "function") ||
            !toolCall.TryGetProperty("function", out var function) ||
            function.ValueKind != JsonValueKind.Object ||
            !HasOnlyProperties(function, FunctionProperties) ||
            !TryReadIdentifier(function, "name", out var name) ||
            !function.TryGetProperty("arguments", out var argumentsElement) ||
            !TryReadUtf8String(
                argumentsElement,
                0,
                AgentLimits.ToolArgumentsBytes,
                out var arguments))
        {
            return false;
        }

        parsed = new DeepSeekParsedToolCall(id!, name!, arguments!);
        return true;
    }

    private static bool TryReadUsage(
        JsonElement root,
        out DeepSeekParsedUsage? usage)
    {
        usage = null;
        if (!root.TryGetProperty("usage", out var usageElement) ||
            usageElement.ValueKind != JsonValueKind.Object ||
            !HasOnlyProperties(usageElement, UsageProperties) ||
            !TryReadNonnegativeInt64(
                usageElement,
                "prompt_tokens",
                out var promptTokens) ||
            !TryReadNonnegativeInt64(
                usageElement,
                "completion_tokens",
                out var completionTokens) ||
            !TryReadNonnegativeInt64(
                usageElement,
                "total_tokens",
                out var totalTokens) ||
            !TryReadNonnegativeInt64(
                usageElement,
                "prompt_cache_hit_tokens",
                out var cacheHitTokens) ||
            !TryReadNonnegativeInt64(
                usageElement,
                "prompt_cache_miss_tokens",
                out var cacheMissTokens) ||
            !ValidateOptionalDetail(
                usageElement,
                "prompt_tokens_details",
                "cached_tokens") ||
            !ValidateOptionalDetail(
                usageElement,
                "completion_tokens_details",
                "reasoning_tokens"))
        {
            return false;
        }

        if (checked(cacheHitTokens + cacheMissTokens) != promptTokens ||
            checked(promptTokens + completionTokens) != totalTokens)
        {
            return false;
        }

        usage = new DeepSeekParsedUsage(promptTokens, completionTokens);
        return true;
    }

    private static bool ValidateOptionalDetail(
        JsonElement usage,
        string objectName,
        string counterName)
    {
        if (!usage.TryGetProperty(objectName, out var detail))
        {
            return true;
        }

        if (detail.ValueKind != JsonValueKind.Object ||
            !HasOnlyProperties(detail, new[] { counterName }))
        {
            return false;
        }

        return !detail.TryGetProperty(counterName, out var counter) ||
            TryReadNonnegativeInt64(counter, out _);
    }

    private static bool TryReadExactString(
        JsonElement container,
        string propertyName,
        string expected)
    {
        return container.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            StringComparer.Ordinal.Equals(property.GetString(), expected);
    }

    private static bool TryReadIdentifier(
        JsonElement container,
        string propertyName,
        out string? value)
    {
        value = null;
        return container.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            AgentValueDomains.IsIdentifier(value = property.GetString());
    }

    private static bool TryReadUtf8String(
        JsonElement element,
        int minimumBytes,
        int maximumBytes,
        out string? value)
    {
        value = element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
        return AgentValueDomains.IsUtf8(
            value,
            minimumBytes,
            maximumBytes);
    }

    private static bool TryReadNullableUtf8StringAsEmpty(
        JsonElement element,
        int maximumBytes,
        out string? value)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            value = string.Empty;
            return true;
        }

        return TryReadUtf8String(element, 0, maximumBytes, out value);
    }

    private static bool IsNullableUtf8String(
        JsonElement element,
        int maximumBytes) =>
        element.ValueKind == JsonValueKind.Null ||
        TryReadUtf8String(element, 0, maximumBytes, out _);

    private static bool TryReadNonnegativeInt64(
        JsonElement container,
        string propertyName,
        out long value)
    {
        value = default;
        return container.TryGetProperty(propertyName, out var property) &&
            TryReadNonnegativeInt64(property, out value);
    }

    private static bool TryReadNonnegativeInt64(
        JsonElement element,
        out long value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt64(out value) &&
            value >= 0;
    }

    private static bool HasOnlyProperties(
        JsonElement element,
        IReadOnlyList<string> allowed)
    {
        foreach (var property in element.EnumerateObject())
        {
            var admitted = false;
            for (var index = 0; index < allowed.Count; index++)
            {
                if (StringComparer.Ordinal.Equals(
                        property.Name,
                        allowed[index]))
                {
                    admitted = true;
                    break;
                }
            }

            if (!admitted)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasUniquePropertyNames(ReadOnlySpan<byte> body)
    {
        var reader = new Utf8JsonReader(
            body,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
        var scopes = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                continue;
            }

            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (scopes.Count == 0)
                {
                    return false;
                }

                scopes.Pop();
                continue;
            }

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                if (name is null || scopes.Count == 0 ||
                    !scopes.Peek().Add(name))
                {
                    return false;
                }
            }
        }

        return scopes.Count == 0;
    }
}
