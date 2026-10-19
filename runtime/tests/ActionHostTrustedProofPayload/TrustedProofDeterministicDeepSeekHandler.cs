using System.Security.Cryptography;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

// The retained historical script has no HTTP, endpoint or credential capability.
// Its old name is retained only as the protected fixture's assembly identity.
internal sealed class TrustedProofDeterministicDeepSeekHandler(DeepSeekAdapterContext context,
    ITrustedProofStaleSignal? staleSignal = null) : IProjectChatClient
{
    internal const string ContinuationMarker = "apr-r4-e2p-private-continuation-7f5d35b4";
    private readonly ITrustedProofStaleSignal staleSignal = staleSignal ?? TrustedProofNoStaleSignal.Instance;
    private int sequence;
    private bool? continuation;
    private string? carrierDigest;
    private static readonly (string Id, string Name, string Arguments)[] Bootstrap =
    [
        ("e2p-list-changed", "list_changed_files", "{}"),
        ("e2p-list-files", "list_files", "{}"),
        ("e2p-read-diff", "read_diff", "{\"path\":\"proof/apr178-path-canary.txt\",\"start_hunk\":1,\"hunk_count\":20}"),
        ("e2p-read-file", "read_file", "{\"path\":\"proof/apr178-path-canary.txt\",\"start_line\":1,\"line_count\":20}"),
        ("e2p-search", "search_text", "{\"query\":\"trusted-proof\",\"path\":\"proof/apr178-path-canary.txt\"}"),
        ("e2p-finish", "finish_review", "{\"summary\":\"Trusted proof complete.\",\"findings\":[]}"),
    ];
    private static readonly (string Id, string Name, string Arguments)[] Continued =
    [
        ("e2p-continuation-read-file", "read_file", "{\"path\":\"proof/apr178-path-canary.txt\",\"start_line\":1,\"line_count\":20}"),
        ("e2p-continuation-search", "search_text", "{\"query\":\"trusted-proof\",\"path\":\"proof/apr178-path-canary.txt\"}"),
        ("e2p-continuation-finish", "finish_review", "{\"summary\":\"Trusted continuation complete.\",\"findings\":[]}"),
    ];

    public async Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var ordinal = Interlocked.Increment(ref sequence);
        Validate(request, ordinal);
        if (request.Accounting is { } capture && !capture.TryBeginDispatch())
            throw new InvalidOperationException("historical_script_dispatch_refused");
        if (ordinal == 2) await staleSignal.SignalReadyAndWaitForReleaseAsync(token).ConfigureAwait(false);
        var script = continuation == true ? Continued : Bootstrap;
        if (ordinal > script.Length) throw new ProjectChatNormalizationException();
        var call = script[ordinal - 1];
        var reasoning = "trusted-proof-reasoning-" + ordinal +
            (continuation == false && ordinal == 3 ? " " + ContinuationMarker : "");
        var position = request.Messages.Length;
        request.Accounting?.RecordUsage(ProviderUsageObservation.Create(3, 2, 1, 2, 5));
        return new(new("assistant", [new ProjectReasoningContent(reasoning, "",
            DeepSeekReasoningContinuationCodec.FramingName, null, position, 0),
            new ProjectToolCallContent(call.Id, call.Name, call.Arguments)]),
            new(3, 2, new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Model, 1, 2)),
            CapturedResponseBodyBytes: 0, Continuation: new(context.ProviderId, context.ModelId, context.AdapterId, context.SessionId,
                [new(reasoning, "", DeepSeekReasoningContinuationCodec.FramingName, null, position, 0)]));
    }

    private void Validate(ProjectChatRequest request, int ordinal)
    {
        // The generic loop carries restored reasoning in Continuation, with inline reasoning optional.
        // Read the typed carrier without serializing a provider request.
        var messages = request.Messages.Select((message, position) =>
        {
            var items = request.Continuation?.Items.Where(item => item.MessagePosition == position).ToArray() ?? [];
            var inline = message.Contents.OfType<ProjectReasoningContent>().ToArray();
            if (items.Length == 0) return message;
            if (items is not [var item] || item.ContentPosition != 0 || item.AssociatedCallId is not null ||
                item.Opaque != "" || item.Framing != DeepSeekReasoningContinuationCodec.FramingName ||
                inline.Length > 1 || inline is [var reasoning] && (reasoning.Text != item.Readable ||
                    reasoning.MessagePosition != position || reasoning.Position != 0 || reasoning.Opaque != item.Opaque ||
                    reasoning.Framing != item.Framing || reasoning.AssociatedCallId != item.AssociatedCallId))
                throw new ProjectChatNormalizationException();
            return inline.Length == 1 ? message : message with { Contents =
                [new ProjectReasoningContent(item.Readable, item.Opaque, item.Framing, item.AssociatedCallId, position, 0), .. message.Contents] };
        }).ToArray();
        var carriers = messages.Select((message, index) => (message, index)).Where(pair =>
            pair.message.Contents.OfType<ProjectReasoningContent>().Any(reasoning => reasoning.Text.Contains(ContinuationMarker, StringComparison.Ordinal))).ToArray();
        var markerOccurrences = messages.Sum(message => message.Contents.Sum(part => part switch
        {
            ProjectReasoningContent r => Count(r.Text), ProjectTextContent text => Count(text.Text),
            ProjectToolCallContent call => Count(call.ArgumentsJson), ProjectToolResultContent result => Count(result.Result), _ => 0,
        }));
        continuation ??= markerOccurrences > 0;
        if (markerOccurrences != 0)
        {
            if (markerOccurrences != 1 || carriers.Length != 1 || !CallMatches(carriers[0].message, Bootstrap[2]))
                throw new ProjectChatNormalizationException();
            var bytes = AgentRequestWriter.Write(new([carriers[0].message], [], null));
            string digest;
            try { digest = Convert.ToHexStringLower(SHA256.HashData(bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            carrierDigest ??= digest;
            if (carrierDigest != digest) throw new ProjectChatNormalizationException();
        }
        else if (continuation == true || ordinal > 3) throw new ProjectChatNormalizationException();
        var expected = continuation == true
            ? Bootstrap.Concat(Continued.Take(ordinal - 1)).ToArray()
            : Bootstrap.Take(ordinal - 1).ToArray();
        var protocol = messages.Where(message => message.Contents.Any(part => part is ProjectToolCallContent or ProjectToolResultContent)).ToArray();
        if (protocol.Length != expected.Length * 2) throw new ProjectChatNormalizationException();
        for (var index = 0; index < expected.Length; index++)
        {
            if (!CallMatches(protocol[index * 2], expected[index]) || protocol[index * 2 + 1] is not
                { Role: "tool", Contents: [ProjectToolResultContent result] } || result.CallId != expected[index].Id ||
                string.IsNullOrWhiteSpace(result.Result)) throw new ProjectChatNormalizationException();
        }
    }
    private static bool CallMatches(ProjectChatMessage message, (string Id, string Name, string Arguments) expected) =>
        message.Role == "assistant" && message.Contents.OfType<ProjectToolCallContent>().ToArray() is [var call] &&
        call.CallId == expected.Id && call.Name == expected.Name && call.ArgumentsJson == (expected.Name switch
        {
            "list_changed_files" => "{\"after\":null}",
            "list_files" => "{\"prefix\":null,\"after\":null}",
            _ => expected.Arguments,
        });
    private static int Count(string value)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(ContinuationMarker, index, StringComparison.Ordinal)) >= 0; index += ContinuationMarker.Length) count++;
        return count;
    }
}
