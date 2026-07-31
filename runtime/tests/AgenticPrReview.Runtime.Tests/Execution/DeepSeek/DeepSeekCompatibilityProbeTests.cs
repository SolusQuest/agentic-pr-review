using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.DeepSeekCompatibilityProbe;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.Tests.Execution.DeepSeek;

public sealed class DeepSeekCompatibilityProbeTests
{
    [Fact]
    public async Task RunsTheBoundedTwoTurnReplayThroughProductionProjection()
    {
        const string reasoningCanary = "probe-reasoning-canary";
        const string argumentsCanary = "{\"value\":\"probe\"}";
        var transport = new QueueTransport(
            DeepSeekTransportResult.Success(FirstResponse(
                reasoningCanary,
                argumentsCanary)),
            DeepSeekTransportResult.Success("{}"u8.ToArray()));

        var result = await DeepSeekCompatibilityProbeRunner.RunAsync(
            transport,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("APR_DEEPSEEK_PROBE_OK", result.Code);
        Assert.Equal(2, transport.Bodies.Count);
        Assert.All(
            transport.Bodies,
            body => Assert.DoesNotContain(
                "tool_choice",
                RootPropertyNames(body)));
        using var second = JsonDocument.Parse(transport.Bodies[1]);
        var messages = second.RootElement.GetProperty("messages");
        var assistant = messages[messages.GetArrayLength() - 2];
        Assert.Equal(JsonValueKind.String, assistant.GetProperty("content").ValueKind);
        Assert.Equal(string.Empty, assistant.GetProperty("content").GetString());
        Assert.Equal(
            reasoningCanary,
            assistant.GetProperty("reasoning_content").GetString());
        Assert.Equal(
            argumentsCanary,
            assistant.GetProperty("tool_calls")[0]
                .GetProperty("function")
                .GetProperty("arguments")
                .GetString());
        Assert.Equal(
            DeepSeekCompatibilityProbeRunner.ToolResult,
            messages[messages.GetArrayLength() - 1]
                .GetProperty("content")
                .GetString());
        Assert.DoesNotContain(
            "probe-opaque-not-on-wire",
            Encoding.UTF8.GetString(transport.Bodies[1]));
        Assert.DoesNotContain(reasoningCanary, result.ToString());
        Assert.DoesNotContain(argumentsCanary, result.ToString());
        Assert.DoesNotContain(
            DeepSeekCompatibilityProbeRunner.ToolResult,
            result.ToString());
    }

    [Theory]
    [InlineData("null", "reasoning", "compatibility_echo")]
    [InlineData("\"\"", "", "compatibility_echo")]
    [InlineData("\"\"", "reasoning", "other_tool")]
    public async Task RejectsAnInvalidFirstTurnWithoutRetry(
        string contentJson,
        string reasoning,
        string functionName)
    {
        var response = Encoding.UTF8.GetBytes(
            "{\"choices\":[{\"index\":0,\"message\":{" +
            "\"role\":\"assistant\",\"content\":" + contentJson + "," +
            "\"reasoning_content\":" + JsonSerializer.Serialize(reasoning) + "," +
            "\"tool_calls\":[{\"id\":\"call-1\",\"type\":\"function\"," +
            "\"function\":{\"name\":" + JsonSerializer.Serialize(functionName) + "," +
            "\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}" );
        var transport = new QueueTransport(
            DeepSeekTransportResult.Success(response),
            DeepSeekTransportResult.Success("{}"u8.ToArray()));

        var result = await DeepSeekCompatibilityProbeRunner.RunAsync(
            transport,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("APR_DEEPSEEK_PROBE_FIRST_RESPONSE_INVALID", result.Code);
        Assert.Single(transport.Bodies);
    }

    [Fact]
    public async Task RejectsDuplicateFirstTurnPropertiesWithoutRetry()
    {
        var response = Encoding.UTF8.GetBytes(
            "{\"choices\":[{\"index\":0,\"index\":0,\"message\":{" +
            "\"role\":\"assistant\",\"content\":\"\"," +
            "\"reasoning_content\":\"reasoning\",\"tool_calls\":[{" +
            "\"id\":\"call-1\",\"type\":\"function\",\"function\":{" +
            "\"name\":\"compatibility_echo\",\"arguments\":\"{}\"}}]}," +
            "\"finish_reason\":\"tool_calls\"}]}" );
        var transport = new QueueTransport(
            DeepSeekTransportResult.Success(response),
            DeepSeekTransportResult.Success("{}"u8.ToArray()));

        var result = await DeepSeekCompatibilityProbeRunner.RunAsync(
            transport,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Single(transport.Bodies);
    }

    [Fact]
    public async Task DoesNotRetryTransportFailures()
    {
        var transport = new QueueTransport(
            DeepSeekTransportResult.TransportFailure(),
            DeepSeekTransportResult.Success(FirstResponse("reasoning", "{}")));

        var result = await DeepSeekCompatibilityProbeRunner.RunAsync(
            transport,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("APR_DEEPSEEK_PROBE_FIRST_RESPONSE_INVALID", result.Code);
        Assert.Single(transport.Bodies);
    }

    private static byte[] FirstResponse(string reasoning, string arguments) =>
        Encoding.UTF8.GetBytes(
            "{\"choices\":[{\"index\":0,\"message\":{" +
            "\"role\":\"assistant\",\"content\":\"\"," +
            "\"reasoning_content\":" + JsonSerializer.Serialize(reasoning) + "," +
            "\"tool_calls\":[{\"id\":\"call-1\",\"type\":\"function\"," +
            "\"function\":{\"name\":\"compatibility_echo\"," +
            "\"arguments\":" + JsonSerializer.Serialize(arguments) + "}}]}," +
            "\"finish_reason\":\"tool_calls\"}]}" );

    private static string[] RootPropertyNames(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
    }

    private sealed class QueueTransport(
        params DeepSeekTransportResult[] results) : IDeepSeekTransport
    {
        private readonly Queue<DeepSeekTransportResult> _results = new(results);

        internal List<byte[]> Bodies { get; } = [];

        public Task<DeepSeekTransportResult> SendAsync(
            ReadOnlyMemory<byte> requestBody,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Bodies.Add(requestBody.ToArray());
            return Task.FromResult(_results.Dequeue());
        }

        public void Dispose()
        {
        }
    }
}
