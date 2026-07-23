using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Ledger;
using AgenticPrReview.Runtime.Prefix;

namespace AgenticPrReview.Runtime;

/// <summary>
/// Closed, single-request Anthropic Messages adapter for the opt-in M4 live gate.
/// It owns HTTP/request parsing only; result, trace, ledger, and publication remain
/// owned by <see cref="LiveRuntimeApplication"/>.
/// </summary>
internal sealed class AnthropicReferenceProviderExecutor : ILiveProviderExecutor
{
    internal const string ProviderId = "anthropic";
    internal const string ModelId = "claude-sonnet-4-6";
    internal const string Endpoint = "https://api.anthropic.com/v1/messages";
    internal const int MaxResponseBytes = 512 * 1024;
    internal const int MaxErrorBytes = 8 * 1024;
    internal const int MaxTokens = 4096;

    private readonly HttpClient _client;
    private readonly Func<string?> _secret;

    internal AnthropicReferenceProviderExecutor(HttpClient client, Func<string?>? secret = null)
    {
        _client = client;
        _secret = secret ?? (() => Environment.GetEnvironmentVariable("AGENTIC_REVIEW_ANTHROPIC_API_KEY"));
    }

    internal static AnthropicReferenceProviderExecutor CreateProduction() =>
        new(new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }) { Timeout = TimeSpan.FromSeconds(120) });

    public ProviderExecutionObservation Execute(ReviewInput input, string inputHash, ExpectedIdentities identities, PrefixMaterialization prefix, bool stateless, CancellationToken cancellationToken = default)
    {
        if (!StringComparer.Ordinal.Equals(identities.ProviderId, ProviderId) ||
            !StringComparer.Ordinal.Equals(identities.ModelId, ModelId))
            throw new RuntimeFailure(10, "APR_LIVE_PROVIDER_CONFIG_INVALID", "Live provider identity does not match the fixed reference adapter.");

        var secret = _secret();
        if (string.IsNullOrWhiteSpace(secret) || secret.Contains('\r') || secret.Contains('\n'))
            throw new RuntimeFailure(10, "APR_LIVE_SECRET_INVALID", "The live provider secret is unavailable.");

        var request = BuildRequest(input, prefix, stateless);
        var requestBytes = Encoding.UTF8.GetBytes(request.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        if (requestBytes.Length > 1024 * 1024)
            throw new RuntimeFailure(30, "APR_LIVE_PROVIDER_REQUEST_TOO_LARGE", "The live provider request exceeds its byte cap.", true);

        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new ByteArrayContent(requestBytes)
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        message.Headers.TryAddWithoutValidation("x-api-key", secret);
        message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        HttpResponseMessage response;
        try
        {
            response = _client.Send(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new RuntimeFailure(30, "APR_PROVIDER_TIMEOUT", "The live provider request timed out.", true);
        }
        catch (HttpRequestException)
        {
            throw new RuntimeFailure(30, "APR_PROVIDER_TRANSPORT_FAILED", "The live provider transport failed.", true);
        }

        using (response)
        {
            var body = ReadBounded(response, response.IsSuccessStatusCode ? MaxResponseBytes : MaxErrorBytes);
            if (!response.IsSuccessStatusCode)
                throw new RuntimeFailure(30, MapStatus(response.StatusCode), "The live provider returned a bounded non-success response.", true);
            return ParseSuccess(body, identities, stateless, requestBytes);
        }
    }

    private static JsonObject BuildRequest(ReviewInput input, PrefixMaterialization prefix, bool stateless)
    {
        var system = new JsonArray();
        var messages = new JsonArray();
        foreach (var block in ReadBlocks(prefix.StableProviderStream).Concat(ReadBlocks(prefix.DynamicProviderStream)))
        {
            var role = block.GetProperty("role").GetString();
            var text = block.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
            if (role == "system")
                system.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = text });
            else if (role is "user" or "assistant")
                messages.Add((JsonNode)new JsonObject { ["role"] = role, ["content"] = text });
        }

        // The actual patch and PR body live after the frozen prefix. They are never
        // added to prefixSha256 or any durable ledger projection.
        messages.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = SubjectText(input) });
        if (!stateless && system.Count > 0)
            ((JsonObject)system[^1]!)!["cache_control"] = new JsonObject { ["type"] = "ephemeral" };

        return new JsonObject
        {
            ["model"] = ModelId,
            ["max_tokens"] = MaxTokens,
            ["system"] = system,
            ["messages"] = messages,
            ["tools"] = new JsonArray((JsonNode)SubmitReviewTool()),
            ["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = "submit_review" }
        };
    }

    private static JsonObject SubmitReviewTool() => new()
    {
        ["name"] = "submit_review",
        ["description"] = "Submit the bounded review result without requesting another provider turn.",
        ["input_schema"] = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("summary", "findings"),
            ["properties"] = new JsonObject
            {
                ["summary"] = new JsonObject { ["type"] = "string", ["maxLength"] = 4000 },
                ["findings"] = new JsonObject
                {
                    ["type"] = "array",
                    ["maxItems"] = 16,
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["required"] = new JsonArray("severity", "confidence", "category", "title", "body", "path", "startLine", "endLine"),
                        ["properties"] = new JsonObject
                        {
                            ["severity"] = new JsonObject { ["enum"] = new JsonArray("low", "medium", "high") },
                            ["confidence"] = new JsonObject { ["enum"] = new JsonArray("medium", "high") },
                            ["category"] = new JsonObject { ["enum"] = new JsonArray("correctness", "security", "requirements", "test_coverage", "build", "performance", "maintainability", "documentation") },
                            ["title"] = new JsonObject { ["type"] = "string", ["maxLength"] = 240 },
                            ["body"] = new JsonObject { ["type"] = "string", ["maxLength"] = 4000 },
                            ["evidence"] = new JsonObject { ["type"] = "string", ["maxLength"] = 2000 },
                            ["path"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["maxLength"] = 500 },
                            ["startLine"] = new JsonObject { ["type"] = new JsonArray("integer", "null"), ["minimum"] = 1 },
                            ["endLine"] = new JsonObject { ["type"] = new JsonArray("integer", "null"), ["minimum"] = 1 },
                            ["suggestedAction"] = new JsonObject { ["type"] = "string", ["maxLength"] = 1600 },
                            ["inlinePreference"] = new JsonObject { ["enum"] = new JsonArray("allowed", "preferred", "avoid") }
                        }
                    }
                },
                ["limitations"] = new JsonObject { ["type"] = "array", ["maxItems"] = 16 }
            }
        }
    };

    private static string SubjectText(ReviewInput input)
    {
        var builder = new StringBuilder();
        builder.Append("PR title: ").AppendLine(input.Subject.PullRequest.Title);
        builder.Append("PR body:\n").AppendLine(input.Subject.PullRequest.Body);
        foreach (var file in input.Subject.ChangedFiles)
        {
            builder.Append("\nFILE ").Append(file.Path).Append(" [").Append(file.Status).AppendLine("]");
            if (file.Patch is not null) builder.AppendLine(file.Patch.Text);
        }
        return builder.ToString();
    }

    private static ProviderExecutionObservation ParseSuccess(byte[] body, ExpectedIdentities identities, bool stateless, byte[] requestBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.GetProperty("type").GetString() != "message" ||
                root.GetProperty("model").GetString() != ModelId ||
                root.GetProperty("stop_reason").GetString() != "tool_use")
                throw new FormatException();
            var content = root.GetProperty("content");
            if (content.GetArrayLength() != 1 || content[0].GetProperty("type").GetString() != "tool_use" || content[0].GetProperty("name").GetString() != "submit_review")
                throw new FormatException();
            var result = content[0].GetProperty("input");
            var summary = BoundedString(result.GetProperty("summary"), 4000);
            var limitations = result.TryGetProperty("limitations", out var limitationValue)
                ? limitationValue.EnumerateArray().Select(x => BoundedString(x, 1200)).ToArray()
                : [];
            var findings = result.GetProperty("findings").EnumerateArray().Select(ParseFinding).ToArray();
            var usage = root.GetProperty("usage");
            var inputTokens = NonNegative(usage.GetProperty("input_tokens"));
            var cacheRead = usage.TryGetProperty("cache_read_input_tokens", out var read) ? NonNegative(read) : 0;
            var cacheWrite = usage.TryGetProperty("cache_creation_input_tokens", out var write) ? NonNegative(write) : 0;
            var outputTokens = usage.TryGetProperty("output_tokens", out var output) ? NonNegative(output) : 0;
            if (stateless && Encoding.UTF8.GetString(requestBytes).Contains("cache_control", StringComparison.Ordinal))
                throw new RuntimeFailure(30, "APR_CACHE_MARKER_MISMATCH", "The stateless request contained a cache marker.", true);
            var proof = stateless && usage.TryGetProperty("cache_read_input_tokens", out _) && usage.TryGetProperty("cache_creation_input_tokens", out _) && cacheRead == 0 && cacheWrite == 0;
            if (stateless && !proof)
                throw new RuntimeFailure(30, "APR_STATELESS_PROOF_MISSING", "The provider did not advertise the required stateless proof.", true);
            var cacheStatus = stateless ? "unknown" : cacheRead > 0 ? "hit" : cacheWrite > 0 ? "miss" : "unknown";
            var requestSha256 = Convert.ToHexString(SHA256.HashData(requestBytes)).ToLowerInvariant();
            return new ProviderExecutionObservation(
                identities.ProviderId, identities.ProviderId, ModelId, identities.AdapterId,
                new JsonObject { ["mode"] = stateless ? "stateless" : "standard", ["aggregate"] = "unknown", ["statelessProof"] = stateless ? new JsonObject { ["kind"] = "providerAdvertised", ["verified"] = true, ["requestSha256"] = requestSha256 } : null },
                cacheStatus,
                new JsonObject { ["attempts"] = new JsonArray((JsonNode)new JsonObject { ["inputTokens"] = inputTokens, ["cacheReadInputTokens"] = cacheRead, ["cacheCreationInputTokens"] = cacheWrite, ["outputTokens"] = outputTokens }), ["requests"] = new JsonArray(), ["aggregate"] = new JsonObject { ["totalInputTokens"] = inputTokens + cacheRead + cacheWrite, ["uncachedInputTokens"] = inputTokens, ["cacheWriteInputTokens"] = cacheWrite, ["cacheReadInputTokens"] = cacheRead, ["outputTokens"] = outputTokens, ["requestCount"] = 1, ["attemptCount"] = 1 } },
                new JsonObject { ["requests"] = new JsonArray((JsonNode)new JsonObject { ["attempt"] = 1, ["status"] = "succeeded" }), ["aggregate"] = new JsonObject { ["requestCount"] = 1, ["attemptCount"] = 1, ["succeededCount"] = 1, ["failedCount"] = 0, ["cancelledCount"] = 0 } },
                [],
                new JsonObject { ["usage"] = "complete", ["cache"] = "complete", ["statelessProof"] = stateless ? "verified" : "notApplicable", ["aggregate"] = "complete" },
                summary, limitations, findings, "live-provider");
        }
        catch (RuntimeFailure) { throw; }
        catch (Exception) { throw new RuntimeFailure(30, "APR_PROVIDER_RESPONSE_INVALID", "The live provider response was malformed or outside the closed response contract.", true); }
    }

    private static RuntimeFinding ParseFinding(JsonElement value) => new(
        EnumProperty(value, "severity", "low", "medium", "high"), EnumProperty(value, "confidence", "medium", "high"),
        EnumProperty(value, "category", "correctness", "security", "requirements", "test_coverage", "build", "performance", "maintainability", "documentation"),
        BoundedString(value.GetProperty("title"), 240), BoundedString(value.GetProperty("body"), 4000), NullableString(value.GetProperty("path")), NullableInt(value.GetProperty("startLine")), NullableInt(value.GetProperty("endLine")),
        value.TryGetProperty("evidence", out var evidence) ? BoundedString(evidence, 2000) : null,
        value.TryGetProperty("suggestedAction", out var action) ? BoundedString(action, 1600) : null,
        value.TryGetProperty("inlinePreference", out var preference) ? EnumString(preference, "allowed", "preferred", "avoid") : null);

    private static string EnumProperty(JsonElement value, string name, params string[] allowed) => EnumString(value.GetProperty(name), allowed);
    private static string EnumString(JsonElement value, params string[] allowed) { var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null; if (text is null || !allowed.Contains(text, StringComparer.Ordinal)) throw new FormatException(); return text; }
    private static string BoundedString(JsonElement value, int max) { if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > max) throw new FormatException(); return value.GetString()!; }
    private static string? NullableString(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : BoundedString(value, 1000);
    private static int? NullableInt(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : checked((int)NonNegative(value));
    private static long NonNegative(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0 ? number : throw new FormatException();
    private static string MapStatus(HttpStatusCode status) => status == HttpStatusCode.RequestTimeout ? "APR_PROVIDER_TIMEOUT" : status == (HttpStatusCode)429 ? "APR_PROVIDER_RATE_LIMITED" : ((int)status >= 500 ? "APR_PROVIDER_5XX" : "APR_PROVIDER_4XX");

    private static byte[] ReadBounded(HttpResponseMessage response, int cap)
    {
        using var stream = response.Content.ReadAsStream();
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int read; while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) { if (output.Length + read > cap) throw new RuntimeFailure(30, "APR_PROVIDER_RESPONSE_TOO_LARGE", "The live provider response exceeds its byte cap.", true); output.Write(buffer, 0, read); }
        return output.ToArray();
    }

    private static IEnumerable<JsonElement> ReadBlocks(IEnumerable<byte> stream)
    {
        var bytes = stream.ToArray(); var offset = 0;
        while (offset < bytes.Length) { if (bytes.Length - offset < 4) throw new FormatException(); var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4)); offset += 4; if (length > bytes.Length - offset) throw new FormatException(); using var doc = JsonDocument.Parse(bytes.AsMemory(offset, (int)length)); yield return doc.RootElement.Clone(); offset += (int)length; }
    }
}
