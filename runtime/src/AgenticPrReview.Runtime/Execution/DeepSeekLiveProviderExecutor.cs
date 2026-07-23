using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime;

internal static class DeepSeekProviderContract
{
    public const string ProviderId = "deepseek";
    public const string ModelId = "deepseek-v4-flash";
    public const string Endpoint = "https://api.deepseek.com/chat/completions";
    public const int RequestBodyMaxBytes = 1 * 1024 * 1024;
    public const int ResponseBodyMaxBytes = 1 * 1024 * 1024;
    public const int ModelContentMaxBytes = 256 * 1024;
    public const int RetainedErrorBodyMaxBytes = 8 * 1024;
    public const int ProviderTimeoutSeconds = 120;
    public const string AdapterBuildVersion = "deepseek-openai-chat-v1";

    public const string FixedInstruction =
        "Return exactly one JSON object with this shape; do not include Markdown fences or explanatory text:\n" +
        "{\"schemaVersion\":1,\"summary\":\"string\",\"findings\":[{\"severity\":\"low|medium|high\",\"confidence\":\"medium|high\",\"category\":\"correctness|security|requirements|test_coverage|build|performance|maintainability|documentation\",\"title\":\"string\",\"body\":\"string\",\"path\":\"string or null\",\"startLine\":\"positive integer or null\",\"endLine\":\"positive integer or null\",\"suggestedAction\":\"string or omitted\"}],\"limitations\":[\"string\"]}\n" +
        "The root keys schemaVersion, summary, findings, and limitations are required; every finding key shown except suggestedAction is required; no other keys are allowed. Use the exact enum and bound values from this closed schema: summary <=4000 Unicode characters, at most min(runtime policy maxFindings, 50) findings, title <=240, body <=4000, repo-relative path <=500 or null, evidence is not a model field, suggestedAction <=1600 when present, at most 16 limitations with each limitation <=1200. If both line values are present, endLine >= startLine. Return JSON, not a tool call.";
}

internal sealed class DeepSeekLiveProviderExecutor : ILiveProviderExecutor
{
    private readonly HttpClient client;
    private readonly string apiKey;

    private DeepSeekLiveProviderExecutor(string apiKey, HttpClient client)
    {
        this.apiKey = apiKey;
        this.client = client;
    }

    public static DeepSeekLiveProviderExecutor FromEnvironment()
    {
        var key = Environment.GetEnvironmentVariable("AGENTIC_REVIEW_DEEPSEEK_API_KEY");
        if (!IsValidKey(key))
            throw new ProviderFailureException("APR_PROVIDER_CONFIG", 20);

        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(DeepSeekProviderContract.ProviderTimeoutSeconds),
        };
        client.DefaultRequestHeaders.Clear();
        return new DeepSeekLiveProviderExecutor(key!, client);
    }

    internal DeepSeekLiveProviderExecutor(string apiKey, HttpMessageHandler handler, TimeSpan timeout)
        : this(apiKey, new HttpClient(handler, disposeHandler: true) { Timeout = timeout })
    {
        if (!IsValidKey(apiKey))
            throw new ArgumentException("The provider key is invalid.", nameof(apiKey));
        client.DefaultRequestHeaders.Clear();
    }

    public async Task<ProviderExecutionObservation> ExecuteAsync(
        ProviderRequestPlan plan,
        ExpectedIdentities identities,
        CancellationToken cancellationToken = default)
    {
        if (!StringComparer.Ordinal.Equals(identities.ProviderId, DeepSeekProviderContract.ProviderId) ||
            !StringComparer.Ordinal.Equals(identities.ModelId, DeepSeekProviderContract.ModelId) ||
            !StringComparer.Ordinal.Equals(identities.AdapterId, plan.AdapterId))
            throw new ProviderFailureException("APR_PROVIDER_CONFIG", 20);

        var messages = plan.Messages.ToList();
        if (messages.Count < 4 || messages.Take(3).Any(message => message.Role != "system") ||
            messages[^1].Role != "user")
            throw new ProviderFailureException("APR_PROVIDER_CONFIG", 20);
        messages.Insert(3, new ProviderRequestMessage("system", DeepSeekProviderContract.FixedInstruction));

        var requestBody = BuildRequestBody(messages);
        if (requestBody.Length > DeepSeekProviderContract.RequestBodyMaxBytes)
            throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);

        using var request = new HttpRequestMessage(HttpMethod.Post, DeepSeekProviderContract.Endpoint)
        {
            Content = new ByteArrayContent(requestBody),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new ProviderFailureException("APR_PROVIDER_CANCELLED", 30);
        }
        catch (TaskCanceledException)
        {
            throw new ProviderFailureException("APR_PROVIDER_TIMEOUT", 30);
        }
        catch (HttpRequestException)
        {
            throw new ProviderFailureException("APR_PROVIDER_TRANSPORT", 30);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                await DiscardBoundedAsync(response.Content, DeepSeekProviderContract.RetainedErrorBodyMaxBytes);
                throw new ProviderFailureException(StatusCode(response.StatusCode), 30);
            }

            var responseBytes = await ReadBoundedAsync(
                response.Content,
                DeepSeekProviderContract.ResponseBodyMaxBytes,
                cancellationToken);
            return ParseSuccess(responseBytes, plan, identities);
        }
    }

    private static bool IsValidKey(string? value) =>
        value is { Length: >= 1 and <= 256 } &&
        !value.Contains('\r') && !value.Contains('\n') && !value.Contains('\0');

    private static string StatusCode(HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests => "APR_PROVIDER_RATE_LIMITED",
        >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError => "APR_PROVIDER_4XX",
        >= HttpStatusCode.InternalServerError => "APR_PROVIDER_5XX",
        _ => "APR_PROVIDER_TRANSPORT",
    };

    private static byte[] BuildRequestBody(IReadOnlyList<ProviderRequestMessage> messages)
    {
        var messageArray = new JsonArray();
        foreach (var message in messages)
        {
            messageArray.Add((JsonNode)new JsonObject
            {
                ["content"] = message.Text,
                ["role"] = message.Role,
            });
        }

        var body = new JsonObject
        {
            ["max_tokens"] = 4096,
            ["messages"] = messageArray,
            ["model"] = DeepSeekProviderContract.ModelId,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["stream"] = false,
            ["temperature"] = 0,
            ["thinking"] = new JsonObject { ["type"] = "disabled" },
        };
        using var document = JsonDocument.Parse(body.ToJsonString());
        return AgenticPrReview.Runtime.Canonical.JsonElementCanonicalizer.Canonicalize(
            document.RootElement,
            DeepSeekProviderContract.RequestBodyMaxBytes,
            DeepSeekProviderContract.RequestBodyMaxBytes,
            DeepSeekProviderContract.RequestBodyMaxBytes,
            long.MaxValue,
            out _).ToArray();
    }

    private static ProviderExecutionObservation ParseSuccess(
        byte[] bytes,
        ProviderRequestPlan plan,
        ExpectedIdentities identities)
    {
        JsonDocument document;
        try
        {
            if (HasDuplicateJsonProperties(bytes))
                throw new JsonException();
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !HasOnlyKeys(root, "id", "object", "created", "model", "choices", "usage", "system_fingerprint") ||
                ReadRequiredString(root, "object") != "chat.completion" ||
                ReadRequiredString(root, "model") != DeepSeekProviderContract.ModelId)
                throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);

            if (root.TryGetProperty("system_fingerprint", out var fingerprint) &&
                (fingerprint.ValueKind != JsonValueKind.String || fingerprint.GetString()!.Length > 256))
                throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);

            var choices = Required(root, "choices", JsonValueKind.Array);
            if (choices.GetArrayLength() != 1)
                throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);
            var choice = choices[0];
            if (!HasOnlyKeys(choice, "index", "message", "finish_reason", "logprobs") ||
                ReadRequiredInt(choice, "index", 0) != 0 ||
                ReadRequiredString(choice, "finish_reason") != "stop")
                throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);
            if (choice.TryGetProperty("logprobs", out var logprobs) && logprobs.ValueKind != JsonValueKind.Null)
                throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);

            var message = Required(choice, "message", JsonValueKind.Object);
            if (!HasOnlyKeys(message, "role", "content", "reasoning_content", "tool_calls") ||
                ReadRequiredString(message, "role") != "assistant")
                throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);
            if (message.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind != JsonValueKind.Null)
                throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);
            if (message.TryGetProperty("tool_calls", out var toolCalls) &&
                (toolCalls.ValueKind != JsonValueKind.Array || toolCalls.GetArrayLength() != 0))
                throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);
            var content = ReadRequiredString(message, "content");
            if (content.Length == 0 || Encoding.UTF8.GetByteCount(content) > DeepSeekProviderContract.ModelContentMaxBytes)
                throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);

            var usage = ParseUsage(Required(root, "usage", JsonValueKind.Object));
            var model = ParseModelContent(content, plan.MaxFindings);
            return new ProviderExecutionObservation(
                identities.ProviderId,
                identities.ProviderId,
                identities.ModelId,
                identities.AdapterId,
                new JsonObject
                {
                    ["mode"] = "standard",
                    ["aggregate"] = "eligible",
                    ["statelessProof"] = null,
                },
                usage.CacheStatus,
                BuildUsage(usage),
                BuildRetryObservations(),
                [],
                new JsonObject
                {
                    ["usage"] = "partial",
                    ["cache"] = "complete",
                    ["statelessProof"] = "notApplicable",
                    ["aggregate"] = "partial",
                },
                model.Summary,
                model.Findings,
                model.Limitations,
                "live-provider");
        }
    }

    private static ParsedUsage ParseUsage(JsonElement usage)
    {
        if (!HasOnlyKeys(usage, "prompt_tokens", "completion_tokens", "total_tokens", "prompt_cache_hit_tokens", "prompt_cache_miss_tokens"))
            throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);
        var prompt = ReadToken(usage, "prompt_tokens");
        var completion = ReadToken(usage, "completion_tokens");
        var total = ReadToken(usage, "total_tokens");
        var hit = ReadToken(usage, "prompt_cache_hit_tokens");
        var miss = ReadToken(usage, "prompt_cache_miss_tokens");
        if (prompt <= 0 || total != checked(prompt + completion) || prompt != checked(hit + miss) || hit == 0 && miss == 0)
            throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);
        var status = hit > 0 && miss == 0 ? "hit" : hit == 0 ? "miss" : "partial";
        return new ParsedUsage(prompt, completion, hit, miss, status);
    }

    private static JsonObject BuildUsage(ParsedUsage usage)
    {
        var attempt = new JsonObject
        {
            ["requestOrdinal"] = 0,
            ["attemptOrdinal"] = 0,
            ["outcome"] = "succeeded",
            ["capability"] = "eligible",
            ["cacheStatus"] = usage.CacheStatus,
            ["usageCompleteness"] = "partial",
            ["totalInputTokens"] = usage.PromptTokens,
            ["uncachedInputTokens"] = usage.MissTokens,
            ["cacheWriteInputTokens"] = null,
            ["cacheReadInputTokens"] = usage.HitTokens,
            ["outputTokens"] = usage.CompletionTokens,
            ["attemptErrorCodes"] = new JsonArray(),
        };
        var request = new JsonObject
        {
            ["requestOrdinal"] = 0,
            ["capability"] = "eligible",
            ["cacheStatus"] = usage.CacheStatus,
            ["usageCompleteness"] = "partial",
            ["totalInputTokens"] = usage.PromptTokens,
            ["uncachedInputTokens"] = usage.MissTokens,
            ["cacheWriteInputTokens"] = null,
            ["cacheReadInputTokens"] = usage.HitTokens,
            ["outputTokens"] = usage.CompletionTokens,
        };
        var aggregate = new JsonObject
        {
            ["totalInputTokens"] = usage.PromptTokens,
            ["uncachedInputTokens"] = usage.MissTokens,
            ["cacheWriteInputTokens"] = null,
            ["cacheReadInputTokens"] = usage.HitTokens,
            ["outputTokens"] = usage.CompletionTokens,
            ["requestCount"] = 1,
            ["attemptCount"] = 1,
        };
        return new JsonObject
        {
            ["attempts"] = new JsonArray(attempt),
            ["requests"] = new JsonArray(request),
            ["aggregate"] = aggregate,
        };
    }

    private static JsonObject BuildRetryObservations() => new()
    {
        ["requests"] = new JsonArray(new JsonObject
        {
            ["requestOrdinal"] = 0,
            ["attemptCount"] = 1,
            ["succeededCount"] = 1,
            ["failedCount"] = 0,
            ["cancelledCount"] = 0,
        }),
        ["aggregate"] = new JsonObject
        {
            ["requestCount"] = 1,
            ["attemptCount"] = 1,
            ["succeededCount"] = 1,
            ["failedCount"] = 0,
            ["cancelledCount"] = 0,
        },
    };

    private static ParsedModel ParseModelContent(string content, int maxFindings)
    {
        JsonDocument document;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            if (HasDuplicateJsonProperties(bytes)) throw new JsonException();
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasOnlyKeys(root, "schemaVersion", "summary", "findings", "limitations") ||
                ReadRequiredInt(root, "schemaVersion", 1) != 1)
                throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);
            var summary = ReadBoundedNonEmptyString(root, "summary", 4_000);
            var findingsElement = Required(root, "findings", JsonValueKind.Array);
            if (findingsElement.GetArrayLength() > maxFindings)
                throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);
            var findings = ImmutableArray.CreateBuilder<LedgerFinding>();
            foreach (var item in findingsElement.EnumerateArray()) findings.Add(ParseFinding(item));
            var limitationsElement = Required(root, "limitations", JsonValueKind.Array);
            if (limitationsElement.GetArrayLength() > 16)
                throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);
            var limitations = limitationsElement.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 and <= 1_200 } value
                    ? value
                    : throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20))
                .ToArray();
            return new ParsedModel(summary, findings.ToImmutable(), limitations);
        }
    }

    private static LedgerFinding ParseFinding(JsonElement finding)
    {
        if (finding.ValueKind != JsonValueKind.Object || !HasOnlyKeys(finding, "severity", "confidence", "category", "title", "body", "path", "startLine", "endLine", "suggestedAction"))
            throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);
        var severity = ReadEnum(finding, "severity", "low", "medium", "high");
        var confidence = ReadEnum(finding, "confidence", "medium", "high");
        var category = ReadEnum(finding, "category", "correctness", "security", "requirements", "test_coverage", "build", "performance", "maintainability", "documentation");
        var title = ReadBoundedNonEmptyString(finding, "title", 240);
        var body = ReadBoundedNonEmptyString(finding, "body", 4_000);
        var path = ReadNullablePath(finding, "path");
        var start = ReadNullablePositiveInt(finding, "startLine");
        var end = ReadNullablePositiveInt(finding, "endLine");
        if ((start is null) != (end is null) || start is not null && (path is null || end < start))
            throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);
        var suggestedAction = finding.TryGetProperty("suggestedAction", out var action)
            ? action.ValueKind == JsonValueKind.String && action.GetString() is { Length: > 0 and <= 1_600 } value ? value : throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20)
            : null;
        return new LedgerFinding
        {
            Severity = severity,
            Confidence = confidence,
            Category = category,
            Title = title,
            Body = body,
            Path = path,
            StartLine = start,
            EndLine = end,
            SuggestedAction = suggestedAction,
        };
    }

    private static string? ReadNullablePath(JsonElement value, string property)
    {
        var element = Required(value, property);
        if (element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String || element.GetString() is not { Length: > 0 and <= 500 } path || !IsSafeRelativePath(path))
            throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);
        return path;
    }

    private static bool IsSafeRelativePath(string path) =>
        !path.StartsWith("/", StringComparison.Ordinal) && !path.Contains('\\') &&
        !System.Text.RegularExpressions.Regex.IsMatch(path, "^[A-Za-z][A-Za-z0-9+.-]*:") &&
        path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");

    private static int? ReadNullablePositiveInt(JsonElement value, string property)
    {
        var element = Required(value, property);
        if (element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var number) || number <= 0)
            throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);
        return number;
    }

    private static long ReadToken(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt64(out var number) || number < 0 || number > 1_000_000_000)
            throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);
        return number;
    }

    private static string ReadRequiredString(JsonElement value, string property)
    {
        var element = Required(value, property, JsonValueKind.String);
        return element.GetString()!;
    }

    private static string ReadBoundedNonEmptyString(JsonElement value, string property, int maximum)
    {
        var result = ReadRequiredString(value, property);
        if (result.Length == 0 || result.Length > maximum) throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);
        return result;
    }

    private static int ReadRequiredInt(JsonElement value, string property, int expected)
    {
        if (!value.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt32(out var number) || number != expected)
            throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);
        return number;
    }

    private static string ReadEnum(JsonElement value, string property, params string[] allowed)
    {
        var result = ReadRequiredString(value, property);
        if (!allowed.Contains(result, StringComparer.Ordinal)) throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);
        return result;
    }

    private static JsonElement Required(JsonElement value, string property, JsonValueKind? kind = null)
    {
        if (!value.TryGetProperty(property, out var element) || kind is not null && element.ValueKind != kind)
            throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);
        return element;
    }

    private static bool HasOnlyKeys(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var set = allowed.ToHashSet(StringComparer.Ordinal);
        return value.EnumerateObject().All(property => set.Contains(property.Name));
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        await using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length > maximum - read) throw new ProviderFailureException("APR_PROVIDER_RESPONSE", 20);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return output.ToArray();
    }

    private static async Task DiscardBoundedAsync(HttpContent content, int maximum)
    {
        try { _ = await ReadBoundedAsync(content, maximum, CancellationToken.None); }
        catch (ProviderFailureException) { }
        catch (Exception) { }
    }

    private static bool HasDuplicateJsonProperties(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, isFinalBlock: true, state: default);
            var scopes = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject) scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                else if (reader.TokenType == JsonTokenType.EndObject) scopes.Pop();
                else if (reader.TokenType == JsonTokenType.PropertyName && !scopes.Peek().Add(reader.GetString()!)) return true;
            }
            return false;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private sealed record ParsedUsage(long PromptTokens, long CompletionTokens, long HitTokens, long MissTokens, string CacheStatus);
    private sealed record ParsedModel(string Summary, ImmutableArray<LedgerFinding> Findings, string[] Limitations);
}
