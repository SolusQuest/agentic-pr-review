using System.Net;
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
        var requestText = Encoding.UTF8.GetString(requestBytes);
        var markerCount = Count(requestText, ContinuationMarker);
        continuation ??= markerCount != 0;
        if (continuation == true && markerCount != 1)
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
                    "{\"path\":\"src/reviewed.ts\",\"start_line\":1," +
                    "\"line_count\":20}",
                    call),
                2 => Tool(
                    "e2p-continuation-search",
                    "search_text",
                    "{\"query\":\"trusted-proof\"," +
                    "\"path\":\"src/reviewed.ts\"}",
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
                "{\"path\":\"src/reviewed.ts\",\"start_hunk\":1," +
                "\"hunk_count\":20}",
                call,
                ContinuationMarker),
            4 => Tool(
                "e2p-read-file",
                "read_file",
                "{\"path\":\"src/reviewed.ts\",\"start_line\":1," +
                "\"line_count\":20}",
                call),
            5 => Tool(
                "e2p-search",
                "search_text",
                "{\"query\":\"trusted-proof\",\"path\":\"src/reviewed.ts\"}",
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
            "e2p-finish",
            "finish_review",
            continuation
                ? "{\"summary\":\"Trusted continuation complete.\"," +
                    "\"findings\":[]}"
                : "{\"summary\":\"Trusted proof complete.\"," +
                    "\"findings\":[]}",
            sequence);
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
