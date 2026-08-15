using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;

internal sealed class FrameworkProviderHandler(string scenarioRoot) :
    HttpMessageHandler
{
    private readonly string scenarioRoot = scenarioRoot;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Method != HttpMethod.Post ||
            request.RequestUri?.AbsoluteUri !=
                "https://api.deepseek.com/chat/completions" ||
            request.Headers.Authorization?.Scheme != "Bearer" ||
            request.Headers.Authorization.Parameter !=
                FrameworkCanaries.ProviderKey ||
            request.Content is null)
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        var body = await request.Content.ReadAsByteArrayAsync(
            cancellationToken).ConfigureAwait(false);
        var mode = ReadMode();
        var requestOrdinal = Increment("provider-request-count");
        Record("provider-request-count", requestOrdinal);
        Record("provider-auth-count", Increment("provider-auth-count"));
        FrameworkCanaryCapture.CaptureAll(scenarioRoot,
            "provider.authorization", request.Headers.Authorization.ToString());
        FrameworkCanaryCapture.CaptureAll(scenarioRoot,
            "provider.request", body);
        if (Contains(body, FrameworkCanaries.Prompt))
        {
            Record("provider-prompt-observed", 1);
        }

        if (Contains(body, FrameworkCanaries.ToolData))
        {
            Record("provider-tool-data-observed", 1);
        }

        if (Contains(body, FrameworkCanaries.ContinuationMarker))
        {
            Record(
                "provider-session-plaintext-request-count",
                Increment("provider-session-plaintext-request-count"));
        }

        if (File.Exists(Path.Join(scenarioRoot, "expect-continuation")))
        {
            ValidateContinuationRequest(body, requestOrdinal);
        }

        if (mode == "provider-error")
        {
            return Json(HttpStatusCode.InternalServerError,
                Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"bounded\"}}"));
        }

        if (mode == "provider-stall")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);
        }

        if (mode == "provider-malformed")
        {
            return Json(HttpStatusCode.OK, Encoding.UTF8.GetBytes("{invalid"));
        }

        var call = Increment("provider-sequence");
        if (mode == "continuation-seed" && call == 4 && File.Exists(
                Path.Join(scenarioRoot, "crash-after-provider-checkpoint")))
        {
            Record("provider-checkpoint-ready", 1);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);
        }

        var prefix = mode == "continuation" ? "continuation-" : "framework-";
        var response = mode == "continuation"
            ? Continuation(body, call)
            : call switch
        {
            1 => Tool(
                prefix + "list-changed",
                "list_changed_files",
                "{}",
                call),
            2 => Tool(
                prefix + "list-files",
                "list_files",
                "{}",
                call),
            3 => Tool(
                prefix + "read-diff",
                "read_diff",
                "{\"path\":\"" + FrameworkCanaries.ReviewedPath +
                    "\",\"start_hunk\":1,\"hunk_count\":20}",
                call,
                mode == "continuation-seed"
                    ? FrameworkCanaries.ContinuationMarker
                    : null),
            4 => Tool(
                prefix + "read-file",
                "read_file",
                "{\"path\":\"" + FrameworkCanaries.ReviewedPath +
                    "\",\"start_line\":1,\"line_count\":20}",
                call),
            5 => Tool(
                prefix + "search-text",
                "search_text",
                "{\"query\":\"" + FrameworkCanaries.ToolData +
                    "\",\"path\":\"" +
                    FrameworkCanaries.ReviewedPath + "\"}",
                call),
            6 => Finish(body, mode, call, prefix + "finish"),
            _ => Encoding.UTF8.GetBytes("{\"choices\":[]}"),
        };
        return Json(HttpStatusCode.OK, response);
    }

    private byte[] Continuation(byte[] requestBody, int call) => call switch
    {
        1 => Tool(
            "continuation-read-file",
            "read_file",
            "{\"path\":\"" + FrameworkCanaries.ReviewedPath +
                "\",\"start_line\":1,\"line_count\":20}",
            call),
        2 => Tool(
            "continuation-search-text",
            "search_text",
            "{\"query\":\"" + FrameworkCanaries.ToolData +
                "\",\"path\":\"" + FrameworkCanaries.ReviewedPath + "\"}",
            call),
        3 => Finish(
            requestBody,
            "continuation",
            call,
            "continuation-finish"),
        _ => Encoding.UTF8.GetBytes("{\"choices\":[]}"),
    };

    private void ValidateContinuationRequest(byte[] body, int ordinal)
    {
        var markerCount = Count(body, FrameworkCanaries.ContinuationMarker);
        var carrierPath = Path.Join(
            scenarioRoot,
            "provider-continuation-marker-carrier.sha256");
        if (ordinal == 1 && markerCount == 1 &&
            TryMarkerCarrierDigest(body, out var carrierDigest))
        {
            File.WriteAllText(carrierPath, carrierDigest);
            Record("provider-continuation-observed", 1);
            Record("provider-continuation-first-request-exact", 1);
        }
        else if (ordinal == 1)
        {
            Record("provider-continuation-order-violation", 1);
        }
        else if (markerCount == 1 && File.Exists(carrierPath) &&
            TryMarkerCarrierDigest(body, out carrierDigest) &&
            StringComparer.Ordinal.Equals(
                File.ReadAllText(carrierPath),
                carrierDigest))
        {
            Record("provider-continuation-carried-history-" + ordinal, 1);
        }
        else
        {
            Record("provider-continuation-order-violation", 1);
        }

        var priorCall = ordinal switch
        {
            2 => "continuation-read-file",
            3 => "continuation-search-text",
            _ => null,
        };
        if (priorCall is null)
        {
            return;
        }

        if (HasExactToolExchange(body, priorCall))
        {
            Record("provider-continuation-relation-" + ordinal, 1);
        }
        else
        {
            Record("provider-continuation-order-violation", 1);
        }
    }

    private static bool TryMarkerCarrierDigest(
        byte[] body,
        out string digest)
    {
        digest = string.Empty;
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("messages", out var messages) ||
            messages.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var carriers = messages.EnumerateArray()
            .Select(message => message.GetRawText())
            .Where(message => message.Contains(
                FrameworkCanaries.ContinuationMarker,
                StringComparison.Ordinal))
            .ToArray();
        if (carriers.Length != 1 || Count(
                Encoding.UTF8.GetBytes(carriers[0]),
                FrameworkCanaries.ContinuationMarker) != 1)
        {
            return false;
        }

        digest = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(carriers[0])))
            .ToLowerInvariant();
        return true;
    }

    private static bool HasExactToolExchange(byte[] body, string callId)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("messages", out var messages) ||
            messages.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var calls = 0;
        var results = 0;
        foreach (var message in messages.EnumerateArray())
        {
            if (message.TryGetProperty("role", out var role) &&
                role.GetString() == "assistant" &&
                message.TryGetProperty("tool_calls", out var toolCalls) &&
                toolCalls.ValueKind == JsonValueKind.Array)
            {
                calls += toolCalls.EnumerateArray().Count(call =>
                    call.TryGetProperty("id", out var id) &&
                    id.GetString() == callId);
            }

            if (message.TryGetProperty("role", out role) &&
                role.GetString() == "tool" &&
                message.TryGetProperty("tool_call_id", out var resultId) &&
                resultId.GetString() == callId &&
                message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(content.GetString()))
            {
                results++;
            }
        }

        return calls == 1 && results == 1;
    }

    private byte[] Finish(
        byte[] requestBody,
        string mode,
        int call,
        string callId)
    {
        string arguments;
        if (mode == "public-result")
        {
            arguments = "{\"summary\":\"rejected\",\"findings\":[{" +
                "\"severity\":\"high\",\"title\":\"" +
                FrameworkCanaries.PublicResult +
                "\",\"message\":\"untrusted\",\"evidence\":[{" +
                "\"observation_id\":\"untrusted\",\"path\":\"" +
                FrameworkCanaries.ReviewedPath +
                "\",\"start_line\":1,\"end_line\":1}]}]}";
        }
        else if (mode is "inline" or "inline-warning")
        {
            var observation = FindObservationId(
                requestBody,
                "framework-read-diff");
            arguments = "{\"summary\":\"Bounded review complete.\"," +
                "\"findings\":[{\"severity\":\"high\"," +
                "\"title\":\"Grounded proof finding\"," +
                "\"message\":\"The exact changed line is reviewed.\"," +
                "\"evidence\":[{\"observation_id\":\"" + observation +
                "\",\"path\":\"" + FrameworkCanaries.ReviewedPath +
                "\",\"start_line\":1,\"end_line\":1}]}]}";
        }
        else
        {
            arguments = "{\"summary\":\"Bounded review complete.\"," +
                "\"findings\":[]}";
        }

        if (mode == "public-result")
        {
            FrameworkCanaryCapture.CaptureAll(scenarioRoot,
                "agent.validation", arguments);
        }

        return Tool(callId, "finish_review", arguments, call);
    }

    private static string FindObservationId(byte[] body, string callId)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("messages", out var messages))
        {
            return "missing";
        }

        foreach (var message in messages.EnumerateArray())
        {
            if (!message.TryGetProperty("role", out var role) ||
                role.GetString() != "tool" ||
                !message.TryGetProperty("tool_call_id", out var id) ||
                id.GetString() != callId ||
                !message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            using var result = JsonDocument.Parse(content.GetString()!);
            if (result.RootElement.TryGetProperty(
                    "observation_id",
                    out var observation) &&
                observation.ValueKind == JsonValueKind.String)
            {
                return observation.GetString()!;
            }
        }

        return "missing";
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
                "framework-reasoning-" + sequence.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) +
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

    private string ReadMode()
    {
        var path = Path.Join(scenarioRoot, "mode");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : "sticky";
    }

    private int Increment(string name)
    {
        var path = Path.Join(scenarioRoot, name);
        var current = File.Exists(path) &&
            int.TryParse(
                File.ReadAllText(path),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
            ? parsed
            : 0;
        current++;
        File.WriteAllText(
            path,
            current.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return current;
    }

    private void Record(string name, int value) => File.WriteAllText(
        Path.Join(scenarioRoot, name),
        value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static bool Contains(byte[] bytes, string value) =>
        Encoding.UTF8.GetString(bytes).Contains(value, StringComparison.Ordinal);

    private static int Count(byte[] bytes, string value)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static HttpResponseMessage Json(
        HttpStatusCode status,
        byte[] body) => new(status)
        {
            Content = new ByteArrayContent(body)
            {
                Headers =
                {
                    ContentType = new("application/json")
                    {
                        CharSet = "utf-8",
                    },
                },
            },
        };
}
