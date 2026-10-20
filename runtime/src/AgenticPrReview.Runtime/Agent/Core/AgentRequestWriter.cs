using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Agent.Loop;

internal static class AgentRequestWriter
{
    internal static byte[] Write(ProjectChatRequest request)
    {
        var writer = new Rfc8785Writer(4_096);
        writer.WriteObjectStart();
        writer.WriteProperty("messages");
        writer.WriteArrayStart();
        for (var messageIndex = 0; messageIndex < request.Messages.Length; messageIndex++)
        {
            if (messageIndex > 0)
            {
                writer.WriteComma();
            }

            WriteMessage(ref writer, request.Messages[messageIndex]);
        }

        writer.WriteArrayEnd();
        writer.WriteProperty("tools");
        writer.WriteArrayStart();
        for (var toolIndex = 0; toolIndex < request.Tools.Length; toolIndex++)
        {
            if (toolIndex > 0)
            {
                writer.WriteComma();
            }

            var tool = request.Tools[toolIndex];
            writer.WriteObjectStart();
            writer.WriteProperty("name");
            writer.WriteString(tool.Name);
            writer.WriteProperty("description");
            writer.WriteString(tool.Description);
            writer.WriteProperty("schema_json");
            writer.WriteString(tool.SchemaJson);
            writer.WriteObjectEnd();
        }

        writer.WriteArrayEnd();
        writer.WriteProperty("continuation");
        WriteContinuation(ref writer, request.Continuation);
        writer.WriteProperty("thinking_required");
        writer.WriteBoolean(request.ThinkingRequired);
        writer.WriteObjectEnd();
        return writer.ToImmutableArray().ToArray();
    }

    internal static byte[] WriteContinuationItem(ProjectContinuationItem item)
    {
        var writer = new Rfc8785Writer(256);
        WriteContinuationItem(ref writer, item);
        return writer.ToImmutableArray().ToArray();
    }

    private static void WriteMessage(
        ref Rfc8785Writer writer,
        ProjectChatMessage message)
    {
        writer.WriteObjectStart();
        writer.WriteProperty("role");
        writer.WriteString(message.Role);
        writer.WriteProperty("contents");
        writer.WriteArrayStart();
        for (var contentIndex = 0;
            contentIndex < message.Contents.Length;
            contentIndex++)
        {
            if (contentIndex > 0)
            {
                writer.WriteComma();
            }

            var content = message.Contents[contentIndex];
            writer.WriteObjectStart();
            writer.WriteProperty("kind");
            writer.WriteString(content.Kind);
            switch (content)
            {
                case ProjectTextContent text:
                    writer.WriteProperty("text");
                    writer.WriteString(text.Text);
                    break;
                case ProjectReasoningContent reasoning:
                    writer.WriteProperty("text");
                    writer.WriteString(reasoning.Text);
                    writer.WriteProperty("opaque");
                    writer.WriteString(reasoning.Opaque);
                    writer.WriteProperty("framing");
                    writer.WriteString(reasoning.Framing);
                    writer.WriteProperty("associated_call_id");
                    WriteNullableString(ref writer, reasoning.AssociatedCallId);
                    writer.WriteProperty("message_position");
                    writer.WriteNumber(reasoning.MessagePosition);
                    writer.WriteProperty("position");
                    writer.WriteNumber(reasoning.Position);
                    break;
                case ProjectToolCallContent call:
                    writer.WriteProperty("call_id");
                    writer.WriteString(call.CallId);
                    writer.WriteProperty("name");
                    writer.WriteString(call.Name);
                    writer.WriteProperty("arguments_json");
                    writer.WriteString(call.ArgumentsJson);
                    break;
                case ProjectRecoveryToolCallContent call:
                    writer.WriteProperty("call_id");
                    writer.WriteString(call.CallId);
                    writer.WriteProperty("name");
                    writer.WriteString(call.Name);
                    writer.WriteProperty("arguments_json");
                    writer.WriteString(call.ArgumentsJson);
                    writer.WriteProperty("rejected");
                    writer.WriteBoolean(call.Rejected);
                    break;
                case ProjectToolResultContent result:
                    writer.WriteProperty("call_id");
                    writer.WriteString(result.CallId);
                    writer.WriteProperty("result");
                    writer.WriteString(result.Result);
                    break;
                case ProjectToolErrorContent error:
                    writer.WriteProperty("call_id");
                    writer.WriteString(error.CallId);
                    writer.WriteProperty("result");
                    writer.WriteString(error.Result);
                    break;
            }

            writer.WriteObjectEnd();
        }

        writer.WriteArrayEnd();
        writer.WriteObjectEnd();
    }

    private static void WriteContinuation(
        ref Rfc8785Writer writer,
        ProjectContinuation? continuation)
    {
        if (continuation is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteObjectStart();
        writer.WriteProperty("provider_id");
        writer.WriteString(continuation.ProviderId);
        writer.WriteProperty("model_id");
        writer.WriteString(continuation.ModelId);
        writer.WriteProperty("adapter_id");
        writer.WriteString(continuation.AdapterId);
        writer.WriteProperty("session_id");
        writer.WriteString(continuation.SessionId);
        writer.WriteProperty("items");
        writer.WriteArrayStart();
        for (var index = 0; index < continuation.Items.Length; index++)
        {
            if (index > 0)
            {
                writer.WriteComma();
            }

            var item = continuation.Items[index];
            WriteContinuationItem(ref writer, item);
        }

        writer.WriteArrayEnd();
        writer.WriteObjectEnd();
    }

    private static void WriteContinuationItem(
        ref Rfc8785Writer writer,
        ProjectContinuationItem item)
    {
        writer.WriteObjectStart();
        writer.WriteProperty("readable");
        writer.WriteString(item.Readable);
        writer.WriteProperty("opaque");
        writer.WriteString(item.Opaque);
        writer.WriteProperty("framing");
        writer.WriteString(item.Framing);
        writer.WriteProperty("associated_call_id");
        WriteNullableString(ref writer, item.AssociatedCallId);
        writer.WriteProperty("message_position");
        writer.WriteNumber(item.MessagePosition);
        writer.WriteProperty("content_position");
        writer.WriteNumber(item.ContentPosition);
        writer.WriteObjectEnd();
    }

    private static void WriteNullableString(
        ref Rfc8785Writer writer,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull();
        }
        else
        {
            writer.WriteString(value);
        }
    }
}
