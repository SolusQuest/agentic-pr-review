using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.Tests.Execution.DeepSeek;

public sealed class DeepSeekResponseParserTests
{
    [Fact]
    public void ParsesOrderedCallsWithoutStealingAgentAdmission()
    {
        const string noncanonical = "{ \"path\" : \"src/a.cs\" }";
        const string malformed = "{not-json";
        var calls = string.Join(
            ",",
            Call("call_same", "finish_review", noncanonical),
            Call("call_other", "unknown_tool", malformed),
            Call("call_same", "read_diff", ""));
        var json = Response(
            choice: Choice(Message(
                content: "answer 🧭",
                reasoning: "  complete reasoning 🧠  ",
                calls: calls)),
            usage: Usage(
                prompt: 7,
                completion: 5,
                total: 12,
                hit: 2,
                miss: 5,
                promptDetails: "{}",
                completionDetails:
                    "{\"reasoning_tokens\":9223372036854775807}"),
            rootSuffix:
                ",\"id\":\"provider-id\"" +
                ",\"object\":\"chat.completion\"" +
                ",\"created\":0" +
                ",\"system_fingerprint\":null");

        var result = Parse(json);

        Assert.Equal(DeepSeekResponseParseOutcome.Success, result.Outcome);
        var response = Assert.IsType<DeepSeekParsedToolResponse>(
            result.Response);
        Assert.Equal("answer 🧭", response.Content);
        Assert.Equal("  complete reasoning 🧠  ", response.Reasoning);
        Assert.Equal(Encoding.UTF8.GetByteCount(json), response.CapturedBytes);
        Assert.Equal(7, response.Usage.InputTokens);
        Assert.Equal(5, response.Usage.OutputTokens);
        Assert.Equal(
            ["call_same", "call_other", "call_same"],
            response.Calls.Select(call => call.Id));
        Assert.Equal(
            ["finish_review", "unknown_tool", "read_diff"],
            response.Calls.Select(call => call.Name));
        Assert.Equal(
            [noncanonical, malformed, ""],
            response.Calls.Select(call => call.Arguments));
    }

    [Fact]
    public void ValidatesOptionalToolCallIndicesAgainstPhysicalOrder()
    {
        var indexedCalls = string.Join(
            ",",
            Call("call_0", "read_file", "{}", indexLiteral: "0"),
            Call("call_1", "finish_review", "{}", indexLiteral: "1"));
        var indexed = Parse(Response(
            choice: Choice(Message(calls: indexedCalls))));

        Assert.Equal(DeepSeekResponseParseOutcome.Success, indexed.Outcome);
        var indexedResponse = Assert.IsType<DeepSeekParsedToolResponse>(
            indexed.Response);
        Assert.Equal(
            ["call_0", "call_1"],
            indexedResponse.Calls.Select(call => call.Id));

        AssertSuccess(Response(choice: Choice(Message(calls: string.Join(
            ",",
            Call("call_0", "read_file", "{}"),
            Call("call_1", "finish_review", "{}", indexLiteral: "1"))))));

        foreach (var indexLiteral in new[]
                 {
                     "-1",
                     "0.0",
                     "\"0\"",
                     "9223372036854775808",
                     "null",
                     "true",
                     "{}",
                     "[]",
                     "1",
                 })
        {
            AssertInvalid(Response(choice: Choice(Message(calls: Call(
                "call_0",
                "finish_review",
                "{}",
                indexLiteral: indexLiteral)))));
        }

        AssertInvalid(Response(choice: Choice(Message(calls: Call(
            "call_0",
            "finish_review",
            "{}",
            indexLiteral: "0,\"index\":0")))));
    }

    [Fact]
    public void AcceptsShuffledPropertiesAndNullableOptionalFields()
    {
        var json =
            "{\"usage\":{" +
            "\"completion_tokens_details\":{}," +
            "\"prompt_cache_miss_tokens\":0," +
            "\"total_tokens\":0," +
            "\"prompt_tokens_details\":{}," +
            "\"completion_tokens\":0," +
            "\"prompt_cache_hit_tokens\":0," +
            "\"prompt_tokens\":0}," +
            "\"system_fingerprint\":null," +
            "\"model\":\"deepseek-v4-flash\"," +
            "\"choices\":[{\"finish_reason\":\"tool_calls\"," +
            "\"logprobs\":null,\"message\":{" +
            "\"tool_calls\":[{\"function\":{" +
            "\"arguments\":\"{}\",\"name\":\"finish_review\"}," +
            "\"type\":\"function\",\"id\":\"call_1\"}]," +
            "\"reasoning_content\":\"r\",\"content\":\"\"," +
            "\"role\":\"assistant\"},\"index\":0}]," +
            "\"created\":9223372036854775807," +
            "\"object\":\"chat.completion\",\"id\":\"i\"}";

        AssertSuccess(json);
    }

    [Fact]
    public void NormalizesNullableToolCallContentToEmptyText()
    {
        var result = Parse(Response(choice: Choice(Message(
            contentLiteral: "null"))));

        Assert.Equal(DeepSeekResponseParseOutcome.Success, result.Outcome);
        var response = Assert.IsType<DeepSeekParsedToolResponse>(
            result.Response);
        Assert.Equal(string.Empty, response.Content);
        Assert.Single(response.Calls);
    }

    [Fact]
    public void ClassifiesAValidStandaloneResponseAsMissingTool()
    {
        var result = Parse(Response(choice: Choice(
            Message(
                contentLiteral: "null",
                reasoningLiteral: "null",
                callsLiteral: "null"),
            finishReason: "\"stop\"")));

        Assert.Equal(
            DeepSeekResponseParseOutcome.MissingTool,
            result.Outcome);
        Assert.Null(result.Response);
        Assert.Equal("missing_tool", result.ToString());
    }

    [Fact]
    public void RejectsEveryNonSuccessTransportOutcome()
    {
        var outcomes = new[]
        {
            DeepSeekTransportResult.RequestRejected(),
            DeepSeekTransportResult.ResponseTooLarge(),
            DeepSeekTransportResult.HttpFailure(
                DeepSeekHttpStatusClass.BadRequest,
                0),
            DeepSeekTransportResult.ConnectTimeout(),
            DeepSeekTransportResult.ProviderTimeout(),
            DeepSeekTransportResult.TransportFailure(),
        };

        AssertInvalid((DeepSeekTransportResult?)null);
        foreach (var outcome in outcomes)
        {
            AssertInvalid(outcome);
        }
    }

    [Fact]
    public void RejectsMalformedBodiesWithoutRetainingThem()
    {
        AssertInvalid(DeepSeekTransportResult.Success([]));
        AssertInvalid(DeepSeekTransportResult.Success([0xff]));
        AssertInvalid("null");
        AssertInvalid("[]");
        AssertInvalid(Response() + "{}");
        AssertInvalid(Response().Replace(
            "{\"choices\"",
            "{/*comment*/\"choices\"",
            StringComparison.Ordinal));
        AssertInvalid(Response()[..^1] + ",}");
        AssertSuccess(Response() + " ");
        AssertSuccess(Response());
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("length")]
    [InlineData("content_filter")]
    [InlineData("insufficient_system_resource")]
    [InlineData("unknown")]
    public void RejectsEveryAlternateFinishReason(string finishReason)
    {
        AssertInvalid(Response(choice: Choice(
            Message(),
            finishReason: Quote(finishReason))));
    }

    [Fact]
    public void RejectsMissingNullAndWrongKindFinishReasons()
    {
        var choice = Choice(Message());
        AssertInvalid(Response(choice: choice.Replace(
            ",\"finish_reason\":\"tool_calls\"",
            "",
            StringComparison.Ordinal)));
        AssertInvalid(Response(choice: Choice(
            Message(),
            finishReason: "null")));
        AssertInvalid(Response(choice: Choice(
            Message(),
            finishReason: "1")));
    }

    [Fact]
    public void EnforcesChoiceMessageAndCallShapes()
    {
        var validChoice = Choice(Message());
        var invalid = new[]
        {
            Response(choices: "null"),
            Response(choices: "[]"),
            Response(choices: "[0]"),
            Response(choices: $"[{validChoice},{validChoice}]"),
            Response().Replace(
                "\"deepseek-v4-flash\"",
                "\"wrong-model\"",
                StringComparison.Ordinal),
            Response(choice: Choice(Message(), index: "1")),
            Response(choice: Choice(Message(), index: "0.0")),
            Response(choice: Choice(Message(role: "user"))),
            Response(choice: Choice(Message(reasoningLiteral: "null"))),
            Response(choice: Choice(Message(reasoning: ""))),
            Response(choice: Choice(Message(calls: "", callsLiteral: "null"))),
            Response(choice: Choice(Message(calls: "", callsLiteral: "[]"))),
            Response(choice: Choice(Message(calls: "", callsLiteral: "[0]"))),
            Response(choice: Choice(Message(calls: Call(
                "call_1",
                "finish_review",
                "{}",
                type: "other")))),
            Response(choice: Choice(Message(calls:
                "{\"id\":\"call_1\",\"type\":\"function\"," +
                "\"function\":null}"))),
        };

        foreach (var json in invalid)
        {
            AssertInvalid(json);
        }

        AssertSuccess(Response(choice: Choice(
            Message(),
            extra: ",\"logprobs\":null")));
        AssertInvalid(Response(choice: Choice(
            Message(),
            extra: ",\"logprobs\":{}")));
    }

    [Fact]
    public void RejectsProhibitedMessagePayloads()
    {
        foreach (var property in new[] { "audio", "refusal", "annotation" })
        {
            AssertInvalid(Response(choice: Choice(Message(
                extra: $",\"{property}\":null"))));
        }
    }

    [Fact]
    public void EnforcesContentReasoningAndArgumentByteCaps()
    {
        AssertSuccess(Response(choice: Choice(Message(content: ""))));
        AssertSuccess(Response(choice: Choice(Message(
            content: new string('c', AgentLimits.ContentBytes)))));
        AssertInvalid(Response(choice: Choice(Message(
            content: new string('c', AgentLimits.ContentBytes + 1)))));

        AssertSuccess(Response(choice: Choice(Message(reasoning: "r"))));
        AssertSuccess(Response(choice: Choice(Message(
            reasoning: new string('r', AgentLimits.ContentBytes)))));
        AssertInvalid(Response(choice: Choice(Message(
            reasoning: new string('r', AgentLimits.ContentBytes + 1)))));

        AssertSuccess(Response(choice: Choice(Message(calls: Call(
            "call_1",
            "finish_review",
            new string('a', AgentLimits.ToolArgumentsBytes))))));
        AssertInvalid(Response(choice: Choice(Message(calls: Call(
            "call_1",
            "finish_review",
            new string('a', AgentLimits.ToolArgumentsBytes + 1))))));

        AssertSuccess(Response(choice: Choice(Message(
            content: new string('é', AgentLimits.ContentBytes / 2),
            reasoning: "🧠"))));
        AssertInvalid(Response().Replace(
            "\"reasoning_content\":\"reasoning\"",
            "\"reasoning_content\":\"\\uD800\"",
            StringComparison.Ordinal));
    }

    [Fact]
    public void EnforcesIdentifierDomainsAndCallCount()
    {
        AssertSuccess(Response(choice: Choice(Message(calls: Call(
            new string('i', 64),
            new string('n', 64),
            "{}")))));

        foreach (var (id, name) in new[]
                 {
                     ("", "name"),
                     (new string('i', 65), "name"),
                     ("invalid.id", "name"),
                     ("id", ""),
                     ("id", new string('n', 65)),
                     ("id", "invalid.name"),
                 })
        {
            AssertInvalid(Response(choice: Choice(Message(calls: Call(
                id,
                name,
                "{}")))));
        }

        var eight = string.Join(
            ",",
            Enumerable.Range(1, AgentLimits.ToolCallsPerResponse)
                .Select(index => Call($"call_{index}", "tool", "{}")));
        var nine = eight + "," + Call("call_9", "tool", "{}");
        AssertSuccess(Response(choice: Choice(Message(calls: eight))));
        AssertInvalid(Response(choice: Choice(Message(calls: nine))));
    }

    [Fact]
    public void EnforcesIgnoredStringByteCaps()
    {
        AssertSuccess(Response(rootSuffix:
            ",\"id\":\"i\",\"system_fingerprint\":\"\""));
        AssertSuccess(Response(rootSuffix:
            $",\"id\":{Quote(new string('é', 128))}" +
            $",\"system_fingerprint\":{Quote(new string('é', 128))}"));
        AssertInvalid(Response(rootSuffix: ",\"id\":\"\""));
        AssertInvalid(Response(rootSuffix:
            $",\"id\":{Quote(new string('é', 129))}"));
        AssertInvalid(Response(rootSuffix: ",\"id\":null"));
        AssertInvalid(Response(rootSuffix: ",\"id\":1"));
        AssertInvalid(Response(rootSuffix:
            $",\"system_fingerprint\":{Quote(new string('é', 129))}"));
        AssertInvalid(Response(rootSuffix:
            ",\"system_fingerprint\":1"));
        AssertInvalid(Response(rootSuffix:
            ",\"system_fingerprint\":{}"));
    }

    [Fact]
    public void EnforcesOptionalObjectLiteralMatrix()
    {
        AssertSuccess(Response());
        AssertSuccess(Response(rootSuffix:
            ",\"object\":\"chat.completion\""));

        foreach (var value in new[]
                 {
                     "\"\"",
                     "\"chat.completion.chunk\"",
                     "\"Chat.Completion\"",
                     "null",
                     "1",
                     "{}",
                     "[]",
                 })
        {
            AssertInvalid(Response(rootSuffix: $",\"object\":{value}"));
        }
    }

    [Fact]
    public void EnforcesOptionalCreatedInt64Matrix()
    {
        AssertSuccess(Response());
        AssertSuccess(Response(rootSuffix: ",\"created\":0"));
        AssertSuccess(Response(rootSuffix:
            ",\"created\":9223372036854775807"));

        foreach (var value in new[]
                 {
                     "-1",
                     "1.5",
                     "9223372036854775808",
                     "\"1\"",
                     "null",
                     "{}",
                     "[]",
                 })
        {
            AssertInvalid(Response(rootSuffix: $",\"created\":{value}"));
        }
    }

    [Fact]
    public void EnforcesUsageEquationsAndInt64Domain()
    {
        AssertSuccess(Response(usage: Usage(0, 0, 0, 0, 0)));
        AssertSuccess(Response(usage: Usage(
            long.MaxValue,
            0,
            long.MaxValue,
            long.MaxValue,
            0)));

        var invalid = new[]
        {
            Usage(3, 2, 5, 1, 1),
            Usage(3, 2, 4, 1, 2),
            Usage(long.MaxValue, 0, long.MaxValue, long.MaxValue, 1),
            Usage(long.MaxValue, 1, long.MaxValue, long.MaxValue, 0),
            DefaultUsage().Replace(
                "\"prompt_tokens\":3",
                "\"prompt_tokens\":-1",
                StringComparison.Ordinal),
            DefaultUsage().Replace(
                "\"completion_tokens\":2",
                "\"completion_tokens\":1.5",
                StringComparison.Ordinal),
            DefaultUsage().Replace(
                "\"total_tokens\":5",
                "\"total_tokens\":9223372036854775808",
                StringComparison.Ordinal),
            DefaultUsage().Replace(
                "\"prompt_cache_hit_tokens\":1",
                "\"prompt_cache_hit_tokens\":\"1\"",
                StringComparison.Ordinal),
            DefaultUsage().Replace(
                "\"prompt_cache_miss_tokens\":2",
                "\"prompt_cache_miss_tokens\":null",
                StringComparison.Ordinal),
        };

        foreach (var usage in invalid)
        {
            AssertInvalid(Response(usage: usage));
        }
    }

    [Theory]
    [InlineData("prompt_tokens_details", "cached_tokens")]
    [InlineData("completion_tokens_details", "reasoning_tokens")]
    public void EnforcesOptionalUsageDetailMatrix(
        string objectName,
        string counterName)
    {
        AssertSuccess(Response());
        AssertSuccess(Response(usage: WithDetail(objectName, "{}")));
        AssertSuccess(Response(usage: WithDetail(
            objectName,
            $"{{\"{counterName}\":0}}")));
        AssertSuccess(Response(usage: WithDetail(
            objectName,
            $"{{\"{counterName}\":9223372036854775807}}")));

        foreach (var detail in new[]
                 {
                     "null",
                     "[]",
                     "\"value\"",
                     $"{{\"{counterName}\":-1}}",
                     $"{{\"{counterName}\":1.5}}",
                     $"{{\"{counterName}\":\"1\"}}",
                     $"{{\"{counterName}\":null}}",
                     $"{{\"{counterName}\":9223372036854775808}}",
                     "{\"unknown\":0}",
                     $"{{\"{counterName}\":0,\"unknown\":0}}",
                     $"{{\"{counterName}\":0,\"{counterName}\":0}}",
                 })
        {
            AssertInvalid(Response(usage: WithDetail(objectName, detail)));
        }
    }

    [Fact]
    public void RejectsUnknownPropertiesAtEveryObjectDepth()
    {
        var baseJson = Response(usage: Usage(
            3,
            2,
            5,
            1,
            2,
            "{\"cached_tokens\":0}",
            "{\"reasoning_tokens\":0}"));
        var invalid = new[]
        {
            baseJson.Replace(
                "{\"choices\"",
                "{\"unknown\":0,\"choices\"",
                StringComparison.Ordinal),
            baseJson.Replace(
                "[{\"index\"",
                "[{\"unknown\":0,\"index\"",
                StringComparison.Ordinal),
            baseJson.Replace(
                "{\"role\":\"assistant\"",
                "{\"unknown\":0,\"role\":\"assistant\"",
                StringComparison.Ordinal),
            baseJson.Replace(
                "[{\"id\":\"call_1\"",
                "[{\"unknown\":0,\"id\":\"call_1\"",
                StringComparison.Ordinal),
            baseJson.Replace(
                "{\"name\":\"finish_review\"",
                "{\"unknown\":0,\"name\":\"finish_review\"",
                StringComparison.Ordinal),
            baseJson.Replace(
                "\"usage\":{\"prompt_tokens\"",
                "\"usage\":{\"unknown\":0,\"prompt_tokens\"",
                StringComparison.Ordinal),
            baseJson.Replace(
                "{\"cached_tokens\":0}",
                "{\"unknown\":0,\"cached_tokens\":0}",
                StringComparison.Ordinal),
            baseJson.Replace(
                "{\"reasoning_tokens\":0}",
                "{\"unknown\":0,\"reasoning_tokens\":0}",
                StringComparison.Ordinal),
        };

        foreach (var json in invalid)
        {
            AssertInvalid(json);
        }
    }

    [Fact]
    public void RejectsDuplicatePropertiesAtEveryObjectDepth()
    {
        var baseJson = Response(usage: Usage(
            3,
            2,
            5,
            1,
            2,
            "{\"cached_tokens\":0}",
            "{\"reasoning_tokens\":0}"));
        var invalid = new[]
        {
            baseJson.Replace(
                "\"model\":\"deepseek-v4-flash\"",
                "\"\\u006dodel\":\"deepseek-v4-flash\"," +
                "\"model\":\"deepseek-v4-flash\"",
                StringComparison.Ordinal),
            baseJson.Replace(
                "\"index\":0",
                "\"index\":0,\"index\":0",
                StringComparison.Ordinal),
            baseJson.Replace(
                "\"role\":\"assistant\"",
                "\"role\":\"assistant\",\"role\":\"assistant\"",
                StringComparison.Ordinal),
            baseJson.Replace(
                "\"id\":\"call_1\"",
                "\"id\":\"call_1\",\"id\":\"call_1\"",
                StringComparison.Ordinal),
            baseJson.Replace(
                "\"name\":\"finish_review\"",
                "\"name\":\"finish_review\"," +
                "\"name\":\"finish_review\"",
                StringComparison.Ordinal),
            baseJson.Replace(
                "\"prompt_tokens\":3",
                "\"prompt_tokens\":3,\"prompt_tokens\":3",
                StringComparison.Ordinal),
            baseJson.Replace(
                "\"cached_tokens\":0",
                "\"cached_tokens\":0,\"cached_tokens\":0",
                StringComparison.Ordinal),
            baseJson.Replace(
                "\"reasoning_tokens\":0",
                "\"reasoning_tokens\":0,\"reasoning_tokens\":0",
                StringComparison.Ordinal),
        };

        foreach (var json in invalid)
        {
            AssertInvalid(json);
        }
    }

    [Fact]
    public void RejectsMissingRequiredFieldsAtEveryObjectDepth()
    {
        var baseJson = Response();
        var invalid = new[]
        {
            baseJson.Replace(
                "\"choices\":[" + Choice(Message()) + "],",
                "",
                StringComparison.Ordinal),
            baseJson.Replace(
                ",\"model\":\"deepseek-v4-flash\"",
                "",
                StringComparison.Ordinal),
            baseJson[..baseJson.LastIndexOf(",\"usage\"", StringComparison.Ordinal)] + "}",
            baseJson.Replace("\"index\":0,", "", StringComparison.Ordinal),
            baseJson.Replace(
                "\"message\":" + Message() + ",",
                "",
                StringComparison.Ordinal),
            baseJson.Replace(
                ",\"finish_reason\":\"tool_calls\"",
                "",
                StringComparison.Ordinal),
            baseJson.Replace(
                "\"role\":\"assistant\",",
                "",
                StringComparison.Ordinal),
            baseJson.Replace("\"content\":\"\",", "", StringComparison.Ordinal),
            baseJson.Replace(
                "\"reasoning_content\":\"reasoning\",",
                "",
                StringComparison.Ordinal),
            baseJson.Replace(
                ",\"tool_calls\":[" +
                Call("call_1", "finish_review", "{}") +
                "]",
                "",
                StringComparison.Ordinal),
            baseJson.Replace("\"id\":\"call_1\",", "", StringComparison.Ordinal),
            baseJson.Replace("\"type\":\"function\",", "", StringComparison.Ordinal),
            baseJson.Replace(
                ",\"function\":{\"name\":\"finish_review\"," +
                "\"arguments\":\"{}\"}",
                "",
                StringComparison.Ordinal),
            baseJson.Replace(
                "\"name\":\"finish_review\",",
                "",
                StringComparison.Ordinal),
            baseJson.Replace(
                ",\"arguments\":\"{}\"",
                "",
                StringComparison.Ordinal),
            baseJson.Replace("\"prompt_tokens\":3,", "", StringComparison.Ordinal),
            baseJson.Replace("\"completion_tokens\":2,", "", StringComparison.Ordinal),
            baseJson.Replace("\"total_tokens\":5,", "", StringComparison.Ordinal),
            baseJson.Replace(
                "\"prompt_cache_hit_tokens\":1,",
                "",
                StringComparison.Ordinal),
            baseJson.Replace(
                ",\"prompt_cache_miss_tokens\":2",
                "",
                StringComparison.Ordinal),
        };

        foreach (var json in invalid)
        {
            AssertInvalid(json);
        }
    }

    [Fact]
    public void RejectsRepresentativeWrongKindsAtEveryObjectDepth()
    {
        var baseJson = Response();
        var invalid = new[]
        {
            Response(choices: "null"),
            Response(choices: "[null]"),
            baseJson.Replace(
                "\"model\":\"deepseek-v4-flash\"",
                "\"model\":1",
                StringComparison.Ordinal),
            Response(usage: "null"),
            baseJson.Replace("\"index\":0", "\"index\":\"0\"", StringComparison.Ordinal),
            Response(choice:
                "{\"index\":0,\"message\":null," +
                "\"finish_reason\":\"tool_calls\"}"),
            baseJson.Replace(
                "\"role\":\"assistant\"",
                "\"role\":null",
                StringComparison.Ordinal),
            baseJson.Replace("\"content\":\"\"", "\"content\":[]", StringComparison.Ordinal),
            baseJson.Replace(
                "\"reasoning_content\":\"reasoning\"",
                "\"reasoning_content\":{}",
                StringComparison.Ordinal),
            baseJson.Replace(
                "\"tool_calls\":[" +
                Call("call_1", "finish_review", "{}") +
                "]",
                "\"tool_calls\":null",
                StringComparison.Ordinal),
            baseJson.Replace("\"id\":\"call_1\"", "\"id\":1", StringComparison.Ordinal),
            baseJson.Replace(
                "\"type\":\"function\"",
                "\"type\":null",
                StringComparison.Ordinal),
            Response(choice: Choice(Message(calls:
                "{\"id\":\"call_1\",\"type\":\"function\"," +
                "\"function\":null}"))),
            baseJson.Replace(
                "\"name\":\"finish_review\"",
                "\"name\":[]",
                StringComparison.Ordinal),
            baseJson.Replace("\"arguments\":\"{}\"", "\"arguments\":{}", StringComparison.Ordinal),
        };

        foreach (var json in invalid)
        {
            AssertInvalid(json);
        }
    }

    [Fact]
    public void FormattingNeverPrintsProviderData()
    {
        const string providerId = "provider-id-canary";
        const string fingerprint = "fingerprint-canary";
        const string content = "content-canary";
        const string reasoning = "reasoning-canary";
        const string callId = "call_canary";
        const string toolName = "tool_canary";
        const string arguments = "authorization-canary";
        var result = Parse(Response(
            choice: Choice(Message(
                content: content,
                reasoning: reasoning,
                calls: Call(callId, toolName, arguments))),
            rootSuffix:
                $",\"id\":{Quote(providerId)}" +
                $",\"system_fingerprint\":{Quote(fingerprint)}"));
        var response = Assert.IsType<DeepSeekParsedToolResponse>(
            result.Response);
        var rendered = string.Join(
            "|",
            result,
            response,
            response.Usage,
            response.Calls[0]);

        foreach (var canary in new[]
                 {
                     providerId,
                     fingerprint,
                     content,
                     reasoning,
                     callId,
                     toolName,
                     arguments,
                 })
        {
            Assert.DoesNotContain(canary, rendered, StringComparison.Ordinal);
        }

        const string invalidCanary = "invalid-field-canary";
        var invalid = Parse(Response(rootSuffix:
            $",\"unknown\":{Quote(invalidCanary)}"));
        Assert.Equal("invalid", invalid.ToString());
        Assert.DoesNotContain(
            invalidCanary,
            invalid.ToString(),
            StringComparison.Ordinal);
    }

    private static DeepSeekResponseParseResult Parse(string json) =>
        DeepSeekResponseParser.Parse(DeepSeekTransportResult.Success(
            Encoding.UTF8.GetBytes(json)));

    private static void AssertSuccess(string json)
    {
        var result = Parse(json);
        Assert.Equal(DeepSeekResponseParseOutcome.Success, result.Outcome);
        Assert.NotNull(result.Response);
    }

    private static void AssertInvalid(string json) =>
        AssertInvalid(DeepSeekTransportResult.Success(
            Encoding.UTF8.GetBytes(json)));

    private static void AssertInvalid(DeepSeekTransportResult? result)
    {
        var parsed = DeepSeekResponseParser.Parse(result);
        Assert.Equal(DeepSeekResponseParseOutcome.Invalid, parsed.Outcome);
        Assert.Null(parsed.Response);
        Assert.Equal("invalid", parsed.ToString());
    }

    private static string Response(
        string? choice = null,
        string? choices = null,
        string? usage = null,
        string rootSuffix = "") =>
        "{\"choices\":" +
        (choices ?? $"[{choice ?? Choice(Message())}]") +
        ",\"model\":\"deepseek-v4-flash\",\"usage\":" +
        (usage ?? DefaultUsage()) +
        rootSuffix +
        "}";

    private static string Choice(
        string message,
        string index = "0",
        string finishReason = "\"tool_calls\"",
        string extra = "") =>
        "{\"index\":" + index +
        ",\"message\":" + message +
        ",\"finish_reason\":" + finishReason +
        extra +
        "}";

    private static string Message(
        string role = "assistant",
        string content = "",
        string reasoning = "reasoning",
        string? calls = null,
        string? contentLiteral = null,
        string? reasoningLiteral = null,
        string? callsLiteral = null,
        string extra = "") =>
        "{\"role\":" + Quote(role) +
        ",\"content\":" + (contentLiteral ?? Quote(content)) +
        ",\"reasoning_content\":" +
        (reasoningLiteral ?? Quote(reasoning)) +
        ",\"tool_calls\":" +
        (callsLiteral ?? $"[{calls ?? Call("call_1", "finish_review", "{}")}]") +
        extra +
        "}";

    private static string Call(
        string id,
        string name,
        string arguments,
        string type = "function",
        string? indexLiteral = null) =>
        "{\"id\":" + Quote(id) +
        (indexLiteral is null ? "" : ",\"index\":" + indexLiteral) +
        ",\"type\":" + Quote(type) +
        ",\"function\":{\"name\":" + Quote(name) +
        ",\"arguments\":" + Quote(arguments) +
        "}}";

    private static string DefaultUsage() => Usage(3, 2, 5, 1, 2);

    private static string Usage(
        long prompt,
        long completion,
        long total,
        long hit,
        long miss,
        string? promptDetails = null,
        string? completionDetails = null) =>
        "{\"prompt_tokens\":" + prompt +
        ",\"completion_tokens\":" + completion +
        ",\"total_tokens\":" + total +
        ",\"prompt_cache_hit_tokens\":" + hit +
        ",\"prompt_cache_miss_tokens\":" + miss +
        (promptDetails is null
            ? ""
            : ",\"prompt_tokens_details\":" + promptDetails) +
        (completionDetails is null
            ? ""
            : ",\"completion_tokens_details\":" + completionDetails) +
        "}";

    private static string WithDetail(string objectName, string detail) =>
        DefaultUsage()[..^1] + $",\"{objectName}\":{detail}}}";

    private static string Quote(string value) =>
        "\"" + JsonEncodedText.Encode(value, JavaScriptEncoder.Default) + "\"";
}
