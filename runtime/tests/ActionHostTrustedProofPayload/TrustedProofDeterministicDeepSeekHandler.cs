using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

internal sealed class TrustedProofDeterministicDeepSeekHandler(
    string expectedCredential,
    ITrustedProofStaleSignal? staleSignal = null) : HttpMessageHandler
{
    internal const string ContinuationMarker =
        "apr-r4-e2p-private-continuation-7f5d35b4";

    private readonly string expectedCredential = expectedCredential;
    private readonly ITrustedProofStaleSignal staleSignal =
        staleSignal ?? TrustedProofNoStaleSignal.Instance;
    private int sequence;
    private bool? continuation;
    private string? continuationCarrierDigest;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Method != HttpMethod.Post ||
            request.RequestUri?.AbsoluteUri !=
                "https://api.deepseek.com/chat/completions" ||
            request.Headers.Authorization?.Scheme != "Bearer" ||
            !StringComparer.Ordinal.Equals(
                request.Headers.Authorization.Parameter,
                expectedCredential) ||
            request.Content is null)
        {
            return Json(HttpStatusCode.BadRequest, "{\"error\":{}}");
        }

        var requestBytes = await request.Content.ReadAsByteArrayAsync(
            cancellationToken).ConfigureAwait(false);
        var call = Interlocked.Increment(ref sequence);
        if (!TryValidateMessages(requestBytes, call, out var hasContinuation))
        {
            return Json(HttpStatusCode.BadRequest, "{\"error\":{}}");
        }

        continuation ??= hasContinuation;
        if (continuation != hasContinuation && call == 1)
        {
            return Json(HttpStatusCode.BadRequest, "{\"error\":{}}");
        }

        if (call == 2)
        {
            await staleSignal.SignalReadyAndWaitForReleaseAsync(
                cancellationToken).ConfigureAwait(false);
        }

        var response = continuation == true
            ? call switch
            {
                1 => Tool(
                    "e2p-continuation-read-file",
                    "read_file",
                    "{\"path\":\"proof/apr178-path-canary.txt\",\"start_line\":1," +
                    "\"line_count\":20}",
                    call),
                2 => Tool(
                    "e2p-continuation-search",
                    "search_text",
                    "{\"query\":\"trusted-proof\"," +
                    "\"path\":\"proof/apr178-path-canary.txt\"}",
                    call),
                3 => Finish(requestBytes, call, continuation: true),
                _ => Encoding.UTF8.GetBytes("{\"choices\":[]}"),
            }
            : call switch
        {
            1 => Tool("e2p-list-changed", "list_changed_files", "{}", call),
            2 => Tool("e2p-list-files", "list_files", "{}", call),
            3 => Tool(
                "e2p-read-diff",
                "read_diff",
                "{\"path\":\"proof/apr178-path-canary.txt\",\"start_hunk\":1," +
                "\"hunk_count\":20}",
                call,
                ContinuationMarker),
            4 => Tool(
                "e2p-read-file",
                "read_file",
                "{\"path\":\"proof/apr178-path-canary.txt\",\"start_line\":1," +
                "\"line_count\":20}",
                call),
            5 => Tool(
                "e2p-search",
                "search_text",
                "{\"query\":\"trusted-proof\",\"path\":" +
                "\"proof/apr178-path-canary.txt\"}",
                call),
            6 => Finish(requestBytes, call, continuation: false),
            _ => Encoding.UTF8.GetBytes("{\"choices\":[]}"),
        };
        return Json(HttpStatusCode.OK, response);
    }

    private static byte[] Finish(
        byte[] request,
        int sequence,
        bool continuation)
    {
        using var document = JsonDocument.Parse(request);
        if (!document.RootElement.TryGetProperty("messages", out _))
        {
            return Encoding.UTF8.GetBytes("{\"choices\":[]}");
        }

        return Tool(
            continuation ? "e2p-continuation-finish" : "e2p-finish",
            "finish_review",
            continuation
                ? "{\"summary\":\"Trusted continuation complete.\"," +
                    "\"findings\":[]}"
                : "{\"summary\":\"Trusted proof complete.\"," +
                    "\"findings\":[]}",
            sequence);
    }

    private bool TryValidateMessages(
        byte[] request,
        int call,
        out bool hasContinuation)
    {
        hasContinuation = false;
        try
        {
            using var document = JsonDocument.Parse(request);
            if (!document.RootElement.TryGetProperty(
                    "messages",
                    out var messages) ||
                messages.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var markerCount = Count(
                Encoding.UTF8.GetString(request),
                ContinuationMarker);
            var carriers = messages.EnumerateArray()
                .Where(message =>
                    message.TryGetProperty("role", out var role) &&
                    role.GetString() == "assistant" &&
                    message.TryGetProperty(
                        "reasoning_content",
                        out var reasoning) &&
                    reasoning.ValueKind == JsonValueKind.String &&
                    reasoning.GetString()!.Contains(
                        ContinuationMarker,
                        StringComparison.Ordinal))
                .ToArray();
            if (markerCount == 0)
            {
                if (continuation is true || call > 3)
                {
                    return false;
                }
            }
            else if (markerCount != 1 || carriers.Length != 1 ||
                !HasExactToolCall(
                    carriers[0],
                    "e2p-read-diff",
                    "read_diff",
                    "{\"path\":\"proof/apr178-path-canary.txt\",\"start_hunk\":1," +
                        "\"hunk_count\":20}"))
            {
                return false;
            }
            else
            {
                var carrierDigest = Convert.ToHexString(SHA256.HashData(
                        Encoding.UTF8.GetBytes(carriers[0].GetRawText())))
                    .ToLowerInvariant();
                continuationCarrierDigest ??= carrierDigest;
                if (!StringComparer.Ordinal.Equals(
                        carrierDigest,
                        continuationCarrierDigest))
                {
                    return false;
                }
            }

            var requiredBootstrapExchanges = markerCount == 0
                ? call - 1
                : continuation is true || call == 1
                    ? 6
                    : Math.Min(5, call - 1);
            (string Id, string Name, string Arguments)[] bootstrap =
            [
                ("e2p-list-changed", "list_changed_files",
                    "{\"after\":null}"),
                ("e2p-list-files", "list_files",
                    "{\"prefix\":null,\"after\":null}"),
                ("e2p-read-diff", "read_diff",
                    "{\"path\":\"proof/apr178-path-canary.txt\",\"start_hunk\":1," +
                    "\"hunk_count\":20}"),
                ("e2p-read-file", "read_file",
                    "{\"path\":\"proof/apr178-path-canary.txt\",\"start_line\":1," +
                        "\"line_count\":20}"),
                ("e2p-search", "search_text",
                    "{\"query\":\"trusted-proof\"," +
                    "\"path\":\"proof/apr178-path-canary.txt\"}"),
                ("e2p-finish", "finish_review",
                    "{\"summary\":\"Trusted proof complete.\"," +
                    "\"findings\":[]}"),
            ];
            var expected = new List<(string Id, string Name, string Arguments)>(
                requiredBootstrapExchanges + 2);
            for (var index = 0; index < requiredBootstrapExchanges; index++)
            {
                expected.Add(bootstrap[index]);
            }

            if (continuation is true && call >= 2)
            {
                expected.Add((
                    "e2p-continuation-read-file",
                    "read_file",
                    "{\"path\":\"proof/apr178-path-canary.txt\",\"start_line\":1," +
                        "\"line_count\":20}"));
            }

            if (continuation is true && call >= 3)
            {
                expected.Add((
                    "e2p-continuation-search",
                    "search_text",
                    "{\"query\":\"trusted-proof\"," +
                        "\"path\":\"proof/apr178-path-canary.txt\"}"));
            }

            if (!HasExactToolExchangeSequence(messages, expected))
            {
                return false;
            }

            if (markerCount == 0)
            {
                return true;
            }

            hasContinuation = continuation is true || call == 1;
            return continuation is not false || call >= 4;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasExactToolExchangeSequence(
        JsonElement messages,
        IReadOnlyList<(string Id, string Name, string Arguments)> expected)
    {
        var protocol = messages.EnumerateArray().Where(message =>
            message.TryGetProperty("tool_calls", out _) ||
            message.TryGetProperty("role", out var role) &&
            role.GetString() == "tool").ToArray();
        if (protocol.Length != expected.Count * 2)
        {
            return false;
        }

        for (var index = 0; index < expected.Count; index++)
        {
            var exchange = expected[index];
            if (!HasExactToolCall(
                    protocol[index * 2],
                    exchange.Id,
                    exchange.Name,
                    exchange.Arguments) ||
                !HasExactToolResult(protocol[index * 2 + 1], exchange.Id))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasExactToolResult(JsonElement message, string callId) =>
        message.TryGetProperty("role", out var role) &&
        role.GetString() == "tool" &&
        message.TryGetProperty("tool_call_id", out var resultId) &&
        resultId.GetString() == callId &&
        message.TryGetProperty("content", out var content) &&
        content.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(content.GetString());

    private static bool HasExactToolCall(
        JsonElement message,
        string callId,
        string name,
        string arguments)
    {
        if (!message.TryGetProperty("role", out var role) ||
            role.GetString() != "assistant" ||
            !message.TryGetProperty("tool_calls", out var calls) ||
            calls.ValueKind != JsonValueKind.Array ||
            calls.GetArrayLength() != 1)
        {
            return false;
        }

        var matches = calls.EnumerateArray().Where(call =>
            call.TryGetProperty("id", out var id) &&
            id.GetString() == callId &&
            call.TryGetProperty("type", out var type) &&
            type.GetString() == "function" &&
            call.TryGetProperty("function", out var function) &&
            function.TryGetProperty("name", out var functionName) &&
            functionName.GetString() == name &&
            function.TryGetProperty("arguments", out var functionArguments) &&
            functionArguments.GetString() == arguments).Count();
        return matches == 1;
    }

    private static int Count(string value, string match)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(
                   match,
                   offset,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += match.Length;
        }

        return count;
    }

    private static byte[] Tool(
        string callId,
        string name,
        string arguments,
        int sequence,
        string? privateMarker = null)
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
            writer.WriteString(
                "reasoning_content",
                "trusted-proof-reasoning-" + sequence +
                (privateMarker is null ? string.Empty : " " + privateMarker));
            writer.WriteStartArray("tool_calls");
            writer.WriteStartObject();
            writer.WriteString("id", callId);
            writer.WriteString("type", "function");
            writer.WriteStartObject("function");
            writer.WriteString("name", name);
            writer.WriteString("arguments", arguments);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteString("finish_reason", "tool_calls");
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteString("model", "deepseek-v4-flash");
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

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        Json(status, Encoding.UTF8.GetBytes(body));

    private static HttpResponseMessage Json(HttpStatusCode status, byte[] body) =>
        new(status)
        {
            Content = new ByteArrayContent(body)
            {
                Headers =
                {
                    ContentType = new("application/json") { CharSet = "utf-8" },
                },
            },
        };
}
