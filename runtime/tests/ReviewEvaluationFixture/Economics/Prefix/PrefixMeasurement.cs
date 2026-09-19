using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Canonical;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Prefix;

internal static class PrefixMeasurement
{
    internal static PrefixObservation Observe(PrefixBoundary boundary, ProjectChatRequest request)
    {
        try
        {
            CheckBounds(request);
            if (request.Messages.Length <= boundary.DynamicStart) throw new PrefixObservationException();
            var logical = AgentRequestWriter.Write(request);
            if (logical.Length > AgentLimits.RequestBytes) throw new PrefixObservationException();
            var native = MinimalChatClient.Materialize(request);
            var provider = DeepSeekRequestWriter.Write(native);
            if (provider.Outcome != DeepSeekRequestWriteOutcome.Success) throw new PrefixObservationException();
            using var logicalJson = JsonDocument.Parse(logical);
            using var providerJson = JsonDocument.Parse(provider.Body.AsMemory());
            return new(boundary.Domain, boundary.ControlMessages, boundary.HistoricalMessages,
                Project(logicalJson.RootElement, boundary, logical: true, logical),
                Project(providerJson.RootElement, boundary, logical: false, provider.Body.AsSpan()));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
            JsonException or OverflowException or NullReferenceException or IndexOutOfRangeException or Rfc8785CanonicalizationException)
        {
            // Do not retain the original exception or content-bearing exception message.
            throw new PrefixObservationException();
        }
    }

    private static PrefixProjection Project(JsonElement root, PrefixBoundary boundary, bool logical, ReadOnlySpan<byte> whole)
    {
        var tag = logical ? "logical" : "provider";
        var messages = root.GetProperty("messages").EnumerateArray().ToArray();
        if (messages.Length <= boundary.DynamicStart || messages.Length > AgentLimits.Messages)
            throw new PrefixObservationException();
        var control = new List<ReadOnlyMemory<byte>>();
        var history = new List<ReadOnlyMemory<byte>>();
        var dynamic = new List<ReadOnlyMemory<byte>>();
        var settings = new List<ReadOnlyMemory<byte>>();
        for (var index = 0; index < messages.Length; index++) Segment(index).Add(Raw(messages[index]));
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name == "messages") continue;
            if (logical && property.Name == "continuation")
            {
                // Envelope identity is logical metadata, not a DeepSeek wire field.
                if (property.Value.ValueKind == JsonValueKind.Null) continue;
                foreach (var member in property.Value.EnumerateObject())
                {
                    if (member.Name == "items") continue;
                    // With no accepted continuation, the newly appearing envelope belongs to
                    // the current suffix. Do not turn the first live tool response into a
                    // false historical invalidation at bootstrap.
                    var destination = boundary.HistoricalMessages == 0 ? dynamic : settings;
                    destination.Add(Encoding.UTF8.GetBytes(member.Name)); destination.Add(Raw(member.Value));
                }
                foreach (var item in property.Value.GetProperty("items").EnumerateArray())
                {
                    var position = item.GetProperty("message_position").GetInt32();
                    if (position < boundary.ControlMessages || position >= messages.Length)
                        throw new PrefixObservationException();
                    // Keep original order, value and absolute placement. No ID/deadline normalization.
                    Segment(position).Add(Raw(item));
                }
                continue;
            }
            settings.Add(Encoding.UTF8.GetBytes(property.Name)); settings.Add(Raw(property.Value));
        }
        return new(Hash(tag + ".control", control), Hash(tag + ".history", history),
            Hash(tag + ".dynamic", dynamic), Hash(tag + ".settings", settings),
            Hash(tag + ".whole", [whole.ToArray()]));

        List<ReadOnlyMemory<byte>> Segment(int index) => index < boundary.ControlMessages ? control :
            index < boundary.DynamicStart ? history : dynamic;
    }

    // GetRawText returns the original serialized token, not reserialized JSON.
    private static ReadOnlyMemory<byte> Raw(JsonElement value) => Encoding.UTF8.GetBytes(value.GetRawText());

    private static PrefixSegment Hash(string tag, IEnumerable<ReadOnlyMemory<byte>> parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("apr.r6.prefix." + tag + "\0"));
        var bytes = 0; var count = 0;
        Span<byte> length = stackalloc byte[4];
        foreach (var part in parts)
        {
            BinaryPrimitives.WriteInt32BigEndian(length, part.Length);
            hash.AppendData(length); hash.AppendData(part.Span);
            bytes = checked(bytes + part.Length); count++;
        }
        return new(Convert.ToHexStringLower(hash.GetHashAndReset()), bytes, count);
    }

    // Preflight bounds keep the unbounded logical writer from allocating arbitrary inputs.
    // This counts inputs; production writers remain the sole request implementations.
    private static void CheckBounds(ProjectChatRequest request)
    {
        if (request.Messages.Length is < 1 or > AgentLimits.Messages ||
            request.Tools.Length is < 1 or > DeepSeekRequestWriter.ToolsMaximum ||
            request.Continuation?.Items.Length > AgentLimits.PartsTotal)
            throw new PrefixObservationException();
        long bytes = 0; var parts = 0;
        void Add(string? value)
        {
            if (value is null) return;
            bytes += Encoding.UTF8.GetByteCount(value);
            if (bytes > AgentLimits.RequestBytes) throw new PrefixObservationException();
        }
        foreach (var message in request.Messages)
        {
            if (message.Contents.Length is < 1 or > AgentLimits.PartsPerMessage) throw new PrefixObservationException();
            parts += message.Contents.Length;
            if (parts > AgentLimits.PartsTotal) throw new PrefixObservationException();
            Add(message.Role);
            foreach (var content in message.Contents)
                switch (content)
                {
                    case ProjectTextContent text: Add(text.Text); break;
                    case ProjectToolCallContent call: Add(call.CallId); Add(call.Name); Add(call.ArgumentsJson); break;
                    case ProjectToolResultContent result: Add(result.CallId); Add(result.Result); break;
                    case ProjectReasoningContent reasoning:
                        Add(reasoning.Text); Add(reasoning.Opaque); Add(reasoning.Framing); Add(reasoning.AssociatedCallId); break;
                    default: throw new PrefixObservationException();
                }
        }
        foreach (var tool in request.Tools) { Add(tool.Name); Add(tool.Description); Add(tool.SchemaJson); }
        if (request.Continuation is { } continuation)
        {
            Add(continuation.ProviderId); Add(continuation.ModelId); Add(continuation.AdapterId); Add(continuation.SessionId);
            foreach (var item in continuation.Items)
            { Add(item.Readable); Add(item.Opaque); Add(item.Framing); Add(item.AssociatedCallId); }
        }
    }
}

// Wrap the actual Agent chat seam. Retain at most one run's bounded hash/count observations.
internal sealed class PrefixObservingChatClient(PrefixBoundary boundary, IProjectChatClient inner) : IProjectChatClient
{
    private ImmutableArray<PrefixObservation> _observations = [];
    internal ImmutableArray<PrefixObservation> Observations => _observations;

    public Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_observations.Length >= AgentLimits.ModelCalls) throw new PrefixObservationException();
        _observations = _observations.Add(PrefixMeasurement.Observe(boundary, request));
        return inner.GetResponseAsync(request, token);
    }

    public override string ToString() => "prefix_observing_chat_client";
}
