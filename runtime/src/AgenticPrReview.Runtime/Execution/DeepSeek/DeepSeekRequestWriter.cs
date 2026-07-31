using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;

namespace AgenticPrReview.Runtime.Execution.DeepSeek;

internal enum DeepSeekRequestWriteOutcome
{
    Invalid,
    RequestTooLarge,
    Success,
}

internal sealed class DeepSeekRequestWriteResult
{
    private readonly ImmutableArray<byte> _body;

    private DeepSeekRequestWriteResult(
        DeepSeekRequestWriteOutcome outcome,
        int? actualCount,
        ImmutableArray<byte> body)
    {
        if (outcome == DeepSeekRequestWriteOutcome.Success)
        {
            if (actualCount is not null || body.IsDefault ||
                body.Length > DeepSeekTransportPolicy.RequestBodyMaxBytes)
            {
                throw new ArgumentException("The request result is invalid.");
            }
        }
        else if (!body.IsDefault ||
            outcome == DeepSeekRequestWriteOutcome.RequestTooLarge !=
            (actualCount == DeepSeekTransportPolicy.RequestRejectedCount))
        {
            throw new ArgumentException("The request result is invalid.");
        }

        Outcome = outcome;
        ActualCount = actualCount;
        _body = body;
    }

    internal DeepSeekRequestWriteOutcome Outcome { get; }
    internal int? ActualCount { get; }
    internal bool HasBody => !_body.IsDefault;
    internal ImmutableArray<byte> Body => _body;

    internal static DeepSeekRequestWriteResult Invalid() => new(
        DeepSeekRequestWriteOutcome.Invalid,
        null,
        default);

    internal static DeepSeekRequestWriteResult RequestTooLarge() => new(
        DeepSeekRequestWriteOutcome.RequestTooLarge,
        DeepSeekTransportPolicy.RequestRejectedCount,
        default);

    internal static DeepSeekRequestWriteResult Success(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new DeepSeekRequestWriteResult(
            DeepSeekRequestWriteOutcome.Success,
            null,
            ImmutableArray.CreateRange(body));
    }

    public override string ToString() => Outcome switch
    {
        DeepSeekRequestWriteOutcome.Invalid => "invalid",
        DeepSeekRequestWriteOutcome.RequestTooLarge =>
            $"request_too_large(actual_count={ActualCount})",
        DeepSeekRequestWriteOutcome.Success =>
            $"success(count={Body.Length})",
        _ => nameof(DeepSeekRequestWriteResult),
    };
}

internal static class DeepSeekRequestWriter
{
    internal const string Model = "deepseek-v4-flash";
    internal const int MaxTokens = 4096;
    internal const int ToolsMaximum = 128;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static DeepSeekRequestWriteResult Write(
        MinimalChatRequest? request)
    {
        if (!TryValidate(request, out var schemas))
        {
            return DeepSeekRequestWriteResult.Invalid();
        }

        try
        {
            using var output = new CappedWriteStream(
                DeepSeekTransportPolicy.RequestBodyMaxBytes);
            using (var writer = new Utf8JsonWriter(output))
            {
                WriteRequest(writer, request!, schemas!);
                writer.Flush();
            }

            return DeepSeekRequestWriteResult.Success(output.ToArray());
        }
        catch (RequestTooLargeException)
        {
            return DeepSeekRequestWriteResult.RequestTooLarge();
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            EncoderFallbackException or
            InvalidOperationException or
            JsonException or
            OverflowException)
        {
            return DeepSeekRequestWriteResult.Invalid();
        }
    }

    private static bool TryValidate(
        MinimalChatRequest? request,
        out byte[][]? schemas)
    {
        schemas = null;
        if (request is null ||
            !request.ThinkingRequired ||
            request.Messages is null ||
            request.Messages.Length is < 1 or > AgentLimits.Messages ||
            request.Tools is null ||
            request.Tools.Length is < 1 or > ToolsMaximum)
        {
            return false;
        }

        var usedCallIds = new HashSet<string>(StringComparer.Ordinal);
        var pendingResults = new Queue<string>();
        var parts = 0;
        for (var messageIndex = 0;
             messageIndex < request.Messages.Length;
             messageIndex++)
        {
            var message = request.Messages[messageIndex];
            if (message is null ||
                message.Role is null ||
                message.Contents is null ||
                message.Contents.Length is < 1 or >
                    AgentLimits.PartsPerMessage)
            {
                return false;
            }

            try
            {
                parts = checked(parts + message.Contents.Length);
            }
            catch (OverflowException)
            {
                return false;
            }

            if (parts > AgentLimits.PartsTotal ||
                pendingResults.Count > 0 &&
                !StringComparer.Ordinal.Equals(message.Role, "tool"))
            {
                return false;
            }

            if (StringComparer.Ordinal.Equals(message.Role, "system") ||
                StringComparer.Ordinal.Equals(message.Role, "user"))
            {
                if (!ValidateSingleText(message, messageIndex))
                {
                    return false;
                }

                continue;
            }

            if (StringComparer.Ordinal.Equals(message.Role, "assistant"))
            {
                if (!ValidateAssistant(
                        message,
                        messageIndex,
                        usedCallIds,
                        pendingResults))
                {
                    return false;
                }

                continue;
            }

            if (!StringComparer.Ordinal.Equals(message.Role, "tool") ||
                !ValidateToolResult(
                    message,
                    messageIndex,
                    pendingResults))
            {
                return false;
            }
        }

        if (pendingResults.Count != 0 || !TryValidateTools(request.Tools, out schemas))
        {
            return false;
        }

        return true;
    }

    private static bool ValidateSingleText(
        MinimalChatMessage message,
        int messageIndex)
    {
        if (message.Contents.Length != 1)
        {
            return false;
        }

        var content = message.Contents[0];
        return content is not null &&
            StringComparer.Ordinal.Equals(content.Kind, "text") &&
            EmptyMetadata(content) &&
            CorrectPosition(content, messageIndex, 0) &&
            ValidUtf8(content.Text, 1, AgentLimits.ContentBytes);
    }

    private static bool ValidateAssistant(
        MinimalChatMessage message,
        int messageIndex,
        HashSet<string> usedCallIds,
        Queue<string> pendingResults)
    {
        var textCount = 0;
        var reasoningCount = 0;
        var localCalls = new List<string>();
        var associatedCalls = new List<string>();
        for (var contentIndex = 0;
             contentIndex < message.Contents.Length;
             contentIndex++)
        {
            var content = message.Contents[contentIndex];
            if (content is null ||
                !CorrectPosition(content, messageIndex, contentIndex))
            {
                return false;
            }

            if (StringComparer.Ordinal.Equals(content.Kind, "text"))
            {
                textCount++;
                if (textCount > 1 ||
                    !EmptyMetadata(content) ||
                    !ValidUtf8(content.Text, 1, AgentLimits.ContentBytes))
                {
                    return false;
                }

                continue;
            }

            if (StringComparer.Ordinal.Equals(content.Kind, "reasoning"))
            {
                reasoningCount++;
                if (reasoningCount > 1 ||
                    content.CallId is not null ||
                    content.Name is not null ||
                    !ValidUtf8(content.Text, 0, AgentLimits.ContentBytes) ||
                    !ValidUtf8(content.Opaque, 0, AgentLimits.ContentBytes) ||
                    !ValidUtf8(content.Framing, 1, AgentLimits.ContentBytes) ||
                    content.AssociatedCallId is not null &&
                    !ValidIdentifier(content.AssociatedCallId))
                {
                    return false;
                }

                if (content.AssociatedCallId is not null)
                {
                    associatedCalls.Add(content.AssociatedCallId);
                }

                continue;
            }

            if (!StringComparer.Ordinal.Equals(content.Kind, "tool_call") ||
                !ValidIdentifier(content.CallId) ||
                !ValidIdentifier(content.Name) ||
                !ValidUtf8(
                    content.Text,
                    0,
                    AgentLimits.ToolArgumentsBytes) ||
                content.Opaque is not null ||
                content.Framing is not null ||
                content.AssociatedCallId is not null ||
                !usedCallIds.Add(content.CallId!))
            {
                return false;
            }

            localCalls.Add(content.CallId!);
        }

        if (localCalls.Count is < 1 or > AgentLimits.ToolCallsPerResponse)
        {
            return false;
        }

        if (associatedCalls.Any(callId => !localCalls.Contains(
                callId,
                StringComparer.Ordinal)))
        {
            return false;
        }

        foreach (var callId in localCalls)
        {
            pendingResults.Enqueue(callId);
        }

        return true;
    }

    private static bool ValidateToolResult(
        MinimalChatMessage message,
        int messageIndex,
        Queue<string> pendingResults)
    {
        if (message.Contents.Length != 1 || pendingResults.Count == 0)
        {
            return false;
        }

        var content = message.Contents[0];
        if (content is null ||
            !StringComparer.Ordinal.Equals(content.Kind, "tool_result") ||
            !ValidIdentifier(content.CallId) ||
            content.Name is not null ||
            content.Opaque is not null ||
            content.Framing is not null ||
            content.AssociatedCallId is not null ||
            !CorrectPosition(content, messageIndex, 0) ||
            !TryUtf8Length(
                content.Text,
                1,
                AgentLimits.ToolResultBytes,
                out _) ||
            !StringComparer.Ordinal.Equals(
                pendingResults.Dequeue(),
                content.CallId))
        {
            return false;
        }

        return true;
    }

    private static bool TryValidateTools(
        MinimalChatTool[] tools,
        out byte[][]? schemas)
    {
        schemas = new byte[tools.Length][];
        var names = new HashSet<string>(StringComparer.Ordinal);
        var schemaBytes = 0;
        for (var index = 0; index < tools.Length; index++)
        {
            var tool = tools[index];
            if (tool is null ||
                !ValidIdentifier(tool.Name) ||
                !names.Add(tool.Name) ||
                !ValidUtf8(tool.Description, 0, AgentLimits.ContentBytes) ||
                !TryUtf8Length(
                    tool.SchemaJson,
                    2,
                    DeepSeekTransportPolicy.RequestBodyMaxBytes,
                    out var length))
            {
                schemas = null;
                return false;
            }

            try
            {
                schemaBytes = checked(schemaBytes + length);
            }
            catch (OverflowException)
            {
                schemas = null;
                return false;
            }

            if (schemaBytes > DeepSeekTransportPolicy.RequestBodyMaxBytes)
            {
                schemas = null;
                return false;
            }

            var bytes = StrictUtf8.GetBytes(tool.SchemaJson);
            if (!ValidSchemaObject(bytes))
            {
                schemas = null;
                return false;
            }

            schemas[index] = bytes;
        }

        return true;
    }

    private static bool ValidSchemaObject(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var reader = new Utf8JsonReader(
                bytes,
                isFinalBlock: true,
                state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            var scopes = new Stack<HashSet<string>>();
            scopes.Push(new HashSet<string>(StringComparer.Ordinal));
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.EndObject:
                        if (scopes.Count == 0)
                        {
                            return false;
                        }

                        scopes.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        if (scopes.Count == 0 ||
                            !scopes.Peek().Add(reader.GetString()!))
                        {
                            return false;
                        }

                        break;
                }
            }

            return scopes.Count == 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void WriteRequest(
        Utf8JsonWriter writer,
        MinimalChatRequest request,
        byte[][] schemas)
    {
        writer.WriteStartObject();
        writer.WriteString("model", Model);
        writer.WritePropertyName("messages");
        writer.WriteStartArray();
        foreach (var message in request.Messages)
        {
            WriteMessage(writer, message);
        }

        writer.WriteEndArray();
        writer.WriteBoolean("stream", false);
        writer.WritePropertyName("thinking");
        writer.WriteStartObject();
        writer.WriteString("type", "enabled");
        writer.WriteEndObject();
        writer.WriteString("reasoning_effort", "high");
        writer.WriteNumber("max_tokens", MaxTokens);
        writer.WritePropertyName("tools");
        writer.WriteStartArray();
        for (var index = 0; index < request.Tools.Length; index++)
        {
            var tool = request.Tools[index];
            writer.WriteStartObject();
            writer.WriteString("type", "function");
            writer.WritePropertyName("function");
            writer.WriteStartObject();
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description);
            writer.WritePropertyName("parameters");
            writer.WriteRawValue(schemas[index], skipInputValidation: true);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteMessage(
        Utf8JsonWriter writer,
        MinimalChatMessage message)
    {
        writer.WriteStartObject();
        writer.WriteString("role", message.Role);
        if (StringComparer.Ordinal.Equals(message.Role, "assistant"))
        {
            var text = message.Contents.SingleOrDefault(content =>
                StringComparer.Ordinal.Equals(content.Kind, "text"));
            writer.WriteString("content", text?.Text ?? string.Empty);
            var reasoning = message.Contents.SingleOrDefault(content =>
                StringComparer.Ordinal.Equals(content.Kind, "reasoning"));
            if (reasoning is not null)
            {
                writer.WriteString("reasoning_content", reasoning.Text);
            }

            writer.WritePropertyName("tool_calls");
            writer.WriteStartArray();
            foreach (var call in message.Contents.Where(content =>
                StringComparer.Ordinal.Equals(content.Kind, "tool_call")))
            {
                writer.WriteStartObject();
                writer.WriteString("id", call.CallId);
                writer.WriteString("type", "function");
                writer.WritePropertyName("function");
                writer.WriteStartObject();
                writer.WriteString("name", call.Name);
                writer.WriteString("arguments", call.Text);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }
        else if (StringComparer.Ordinal.Equals(message.Role, "tool"))
        {
            var result = message.Contents[0];
            writer.WriteString("tool_call_id", result.CallId);
            writer.WriteString("content", result.Text);
        }
        else
        {
            writer.WriteString("content", message.Contents[0].Text);
        }

        writer.WriteEndObject();
    }

    private static bool CorrectPosition(
        MinimalChatContent content,
        int message,
        int position) =>
        content.MessagePosition == message && content.Position == position;

    private static bool EmptyMetadata(MinimalChatContent content) =>
        content.CallId is null &&
        content.Name is null &&
        content.Opaque is null &&
        content.Framing is null &&
        content.AssociatedCallId is null;

    private static bool ValidIdentifier(string? value)
    {
        if (value is null || value.Length is < 1 or > 64)
        {
            return false;
        }

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

        return true;
    }

    private static bool ValidUtf8(
        string? value,
        int minimum,
        int maximum) =>
        TryUtf8Length(value, minimum, maximum, out _);

    private static bool TryUtf8Length(
        string? value,
        int minimum,
        int maximum,
        out int length)
    {
        length = 0;
        if (value is null)
        {
            return false;
        }

        try
        {
            length = StrictUtf8.GetByteCount(value);
            return length >= minimum && length <= maximum;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private sealed class RequestTooLargeException : Exception;

    private sealed class CappedWriteStream(int maximum) : Stream
    {
        private readonly MemoryStream _stream = new(
            Math.Min(maximum, 16 * 1024));

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _stream.Length;
        public override long Position
        {
            get => _stream.Position;
            set => throw new NotSupportedException();
        }

        internal byte[] ToArray() => _stream.ToArray();

        public override void Flush() => _stream.Flush();

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > maximum - _stream.Length)
            {
                throw new RequestTooLargeException();
            }

            _stream.Write(buffer);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stream.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
