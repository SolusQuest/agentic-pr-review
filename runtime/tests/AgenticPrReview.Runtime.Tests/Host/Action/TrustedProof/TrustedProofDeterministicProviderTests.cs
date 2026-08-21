using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.TrustedProof;

public sealed class TrustedProofDeterministicProviderTests
{
    [Fact]
    public async Task BootstrapUsesRealDeepSeekMessagesAndWaitsAtValueFreeSignal()
    {
        const string providerCanary = "provider-canary-value";
        var signal = new TrustedProofStaleSignal();
        using var invoker = new HttpMessageInvoker(
            new TrustedProofDeterministicDeepSeekHandler(
                providerCanary,
                signal));
        var messages = new JsonArray();

        var first = await SendAsync(invoker, providerCanary, messages);
        Assert.Equal("list_changed_files", ToolName(first));
        AppendExchange(messages, first);
        var secondTask = SendAsync(invoker, providerCanary, messages);
        await signal.Ready;
        Assert.False(secondTask.IsCompleted);
        signal.Release();
        var second = await secondTask;
        Assert.Equal("list_files", ToolName(second));
        AppendExchange(messages, second);

        var names = new List<string> { "list_changed_files", "list_files" };
        for (var ordinal = 3; ordinal <= 6; ordinal++)
        {
            var response = await SendAsync(invoker, providerCanary, messages);
            names.Add(ToolName(response));
            Assert.DoesNotContain(providerCanary, response, StringComparison.Ordinal);
            AppendExchange(messages, response);
        }

        Assert.Equal(
            [
                "list_changed_files",
                "list_files",
                "read_diff",
                "read_file",
                "search_text",
                "finish_review",
            ],
            names);
    }

    [Fact]
    public async Task RestoredContinuationRequiresExactPriorExchangesAndIsDistinct()
    {
        const string credential = "provider-canary-value";
        var messages = await BootstrapHistoryAsync(credential);
        using var invoker = new HttpMessageInvoker(
            new TrustedProofDeterministicDeepSeekHandler(credential));

        var response = await SendAsync(invoker, credential, messages);
        Assert.Equal("read_file", ToolName(response));
        Assert.DoesNotContain(
            TrustedProofDeterministicDeepSeekHandler.ContinuationMarker,
            response,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContinuationRejectsCurrentContentInjectionAndMissingHistory()
    {
        const string credential = "provider-canary-value";
        var injected = await BootstrapHistoryAsync(credential);
        injected.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] =
                TrustedProofDeterministicDeepSeekHandler.ContinuationMarker,
        });
        using var injectedInvoker = new HttpMessageInvoker(
            new TrustedProofDeterministicDeepSeekHandler(credential));
        using var injectedResponse = await SendRawAsync(
            injectedInvoker,
            credential,
            injected);
        Assert.Equal(HttpStatusCode.BadRequest, injectedResponse.StatusCode);

        var missing = await BootstrapHistoryAsync(credential);
        missing.RemoveAt(1);
        using var missingInvoker = new HttpMessageInvoker(
            new TrustedProofDeterministicDeepSeekHandler(credential));
        using var missingResponse = await SendRawAsync(
            missingInvoker,
            credential,
            missing);
        Assert.Equal(HttpStatusCode.BadRequest, missingResponse.StatusCode);
    }

    [Fact]
    public async Task ContinuationCarriesPriorHistoryAcrossItsOwnToolExchanges()
    {
        const string credential = "provider-canary-value";
        var messages = await BootstrapHistoryAsync(credential);
        using var invoker = new HttpMessageInvoker(
            new TrustedProofDeterministicDeepSeekHandler(credential));

        var first = await SendAsync(invoker, credential, messages);
        Assert.Equal("read_file", ToolName(first));
        AppendExchange(messages, first);
        var second = await SendAsync(invoker, credential, messages);
        Assert.Equal(
            "search_text",
            ToolName(second));
        AppendExchange(messages, second);
        var terminal = await SendAsync(invoker, credential, messages);
        Assert.Equal("finish_review", ToolName(terminal));
        Assert.Contains("Trusted continuation complete.", terminal);
        Assert.DoesNotContain(
            TrustedProofDeterministicDeepSeekHandler.ContinuationMarker,
            terminal,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WrongCredentialIsRejectedWithoutARequestBodyEcho()
    {
        using var invoker = new HttpMessageInvoker(
            new TrustedProofDeterministicDeepSeekHandler("expected"));

        using var request = Request("wrong", "private-body-canary");
        using var response = await invoker.SendAsync(
            request,
            CancellationToken.None);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("wrong", body, StringComparison.Ordinal);
        Assert.DoesNotContain("private-body-canary", body, StringComparison.Ordinal);
    }

    private static async Task<string> SendAsync(
        HttpMessageInvoker invoker,
        string credential,
        JsonArray messages)
    {
        using var request = Request(
            credential,
            $"{{\"messages\":{messages.ToJsonString()}}}");
        using var response = await invoker.SendAsync(
            request,
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static Task<HttpResponseMessage> SendRawAsync(
        HttpMessageInvoker invoker,
        string credential,
        JsonArray messages) => invoker.SendAsync(
        Request(credential, $"{{\"messages\":{messages.ToJsonString()}}}"),
        CancellationToken.None);

    private static async Task<JsonArray> BootstrapHistoryAsync(
        string credential)
    {
        using var invoker = new HttpMessageInvoker(
            new TrustedProofDeterministicDeepSeekHandler(credential));
        var messages = new JsonArray();
        for (var ordinal = 0; ordinal < 5; ordinal++)
        {
            var response = await SendAsync(invoker, credential, messages);
            AppendExchange(messages, response);
        }

        return messages;
    }

    private static void AppendExchange(JsonArray messages, string response)
    {
        var root = JsonNode.Parse(response)!;
        var message = root["choices"]![0]!["message"]!.DeepClone();
        var callId = message["tool_calls"]![0]!["id"]!.GetValue<string>();
        messages.Add(message);
        messages.Add(new JsonObject
        {
            ["role"] = "tool",
            ["tool_call_id"] = callId,
            ["content"] = "{\"result\":\"accepted\"}",
        });
    }

    private static HttpRequestMessage Request(string credential, string body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.deepseek.com/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", credential);
        return request;
    }

    private static string ToolName(string response)
    {
        using var document = JsonDocument.Parse(response);
        return document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("tool_calls")[0]
            .GetProperty("function")
            .GetProperty("name")
            .GetString()!;
    }
}
