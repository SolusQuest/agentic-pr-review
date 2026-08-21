using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

        var first = await SendAsync(invoker, providerCanary, "[]");
        Assert.Equal("list_changed_files", ToolName(first));
        var secondTask = SendAsync(invoker, providerCanary, "[]");
        await signal.Ready;
        Assert.False(secondTask.IsCompleted);
        signal.Release();
        var second = await secondTask;
        Assert.Equal("list_files", ToolName(second));

        var names = new List<string> { "list_changed_files", "list_files" };
        for (var ordinal = 3; ordinal <= 6; ordinal++)
        {
            var response = await SendAsync(invoker, providerCanary, "[]");
            names.Add(ToolName(response));
            Assert.DoesNotContain(providerCanary, response, StringComparison.Ordinal);
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
    public async Task RestoredContinuationRequiresOnePriorMarkerAndIsDistinct()
    {
        const string credential = "provider-canary-value";
        using var invoker = new HttpMessageInvoker(
            new TrustedProofDeterministicDeepSeekHandler(credential));
        var messages = JsonSerializer.Serialize(new[]
        {
            new
            {
                role = "assistant",
                reasoning_content =
                    TrustedProofDeterministicDeepSeekHandler.ContinuationMarker,
            },
        });

        Assert.Equal(
            "read_file",
            ToolName(await SendAsync(invoker, credential, messages)));
        Assert.Equal(
            "search_text",
            ToolName(await SendAsync(invoker, credential, messages)));
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
        string messages)
    {
        using var request = Request(
            credential,
            $"{{\"messages\":{messages}}}");
        using var response = await invoker.SendAsync(
            request,
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
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
