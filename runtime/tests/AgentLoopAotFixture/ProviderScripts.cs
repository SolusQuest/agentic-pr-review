using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.AgentLoopAotFixture;

internal static class ProviderScripts
{
    internal static ProviderServerResponse BootstrapRead(byte[] requestBody)
    {
        var messagePosition = MessageCount(requestBody);
        var response = new ProviderResponseDto(
            new ProviderMessageDto(
                "assistant",
                [
                    new ProviderContentDto(
                        "reasoning",
                        null,
                        null,
                        ProofScenario.ContinuationReadable,
                        ProofScenario.ContinuationOpaque,
                        ProofScenario.ContinuationFraming,
                        "read0",
                        messagePosition,
                        0),
                    new ProviderContentDto(
                        "tool_call",
                        "read0",
                        AgentToolRegistry.ReadFileName,
                        "{\"path\":\"reviewed/fact.txt\"}",
                        null,
                        null,
                        null,
                        messagePosition,
                        1),
                ]),
            new ProviderUsageDto(11, 7),
            new ProviderContinuationDto(
                ProofScenario.ProviderId,
                ProofScenario.ModelId,
                ProofScenario.AdapterId,
                ProofScenario.SessionId,
                [
                    new ProviderContinuationItemDto(
                        ProofScenario.ContinuationReadable,
                        ProofScenario.ContinuationOpaque,
                        ProofScenario.ContinuationFraming,
                        "read0",
                        messagePosition,
                        0),
                ]));
        return Json(response);
    }

    internal static ProviderServerResponse BootstrapFinish(byte[] requestBody)
    {
        using var document = JsonDocument.Parse(requestBody);
        var toolResult = document.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .Last()
            .GetProperty("contents")[0]
            .GetProperty("text")
            .GetString();
        if (toolResult is null ||
            !toolResult.Contains(
                ProofScenario.PriorOnlyFact,
                StringComparison.Ordinal))
        {
            throw new ProviderProtocolException();
        }

        using var resultDocument = JsonDocument.Parse(toolResult);
        var observation = resultDocument.RootElement
            .GetProperty("observation_id")
            .GetString();
        if (observation is null)
        {
            throw new ProviderProtocolException();
        }

        var finish = string.Concat(
            "{\"summary\":\"Synthetic grounded review completed.\",",
            "\"findings\":[{\"severity\":\"high\",",
            "\"title\":\"Synthetic prior fact\",",
            "\"message\":\"The reviewed snapshot contains the synthetic marker.\",",
            "\"evidence\":[{\"observation_id\":\"",
            observation,
            "\",\"path\":\"",
            ProofScenario.ReviewedPath,
            "\",\"start_line\":1,\"end_line\":1}]}]}");
        return Json(new ProviderResponseDto(
            new ProviderMessageDto(
                "assistant",
                [
                    new ProviderContentDto(
                        "tool_call",
                        "finish0",
                        AgentToolRegistry.FinishReviewName,
                        finish,
                        null,
                        null,
                        null,
                        MessageCount(requestBody),
                        0),
                ]),
            new ProviderUsageDto(9, 5),
            null));
    }

    internal static ProviderServerResponse ContinueFinish(byte[] requestBody)
    {
        using var document = JsonDocument.Parse(requestBody);
        var root = document.RootElement;
        var priorFactPresent = root.GetProperty("messages")
            .EnumerateArray()
            .SelectMany(message =>
                message.GetProperty("contents").EnumerateArray())
            .Any(content =>
                content.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String &&
                text.GetString()!.Contains(
                    ProofScenario.PriorOnlyFact,
                    StringComparison.Ordinal));
        if (!priorFactPresent ||
            !root.TryGetProperty("continuation", out var continuation) ||
            continuation.ValueKind != JsonValueKind.Object)
        {
            throw new ProviderProtocolException();
        }

        var item = continuation.GetProperty("items")[0];
        if (!StringComparer.Ordinal.Equals(
                item.GetProperty("opaque").GetString(),
                ProofScenario.ContinuationOpaque) ||
            !StringComparer.Ordinal.Equals(
                item.GetProperty("framing").GetString(),
                ProofScenario.ContinuationFraming) ||
            !StringComparer.Ordinal.Equals(
                item.GetProperty("associated_call_id").GetString(),
                "read0"))
        {
            throw new ProviderProtocolException();
        }

        var finish = string.Concat(
            "{\"summary\":\"Restored prior-only fact ",
            ProofScenario.PriorOnlyFact,
            " was used.\",\"findings\":[]}");
        return Json(new ProviderResponseDto(
            new ProviderMessageDto(
                "assistant",
                [
                    new ProviderContentDto(
                        "tool_call",
                        "finish1",
                        AgentToolRegistry.FinishReviewName,
                        finish,
                        null,
                        null,
                        null,
                        MessageCount(requestBody),
                        0),
                ]),
            new ProviderUsageDto(13, 4),
            null));
    }

    internal static ProviderServerResponse DirectFinish(
        byte[] requestBody,
        string callId = "finish_direct")
    {
        var finish =
            "{\"summary\":\"Synthetic direct terminal.\",\"findings\":[]}";
        return Json(new ProviderResponseDto(
            new ProviderMessageDto(
                "assistant",
                [
                    new ProviderContentDto(
                        "tool_call",
                        callId,
                        AgentToolRegistry.FinishReviewName,
                        finish,
                        null,
                        null,
                        null,
                        MessageCount(requestBody),
                        0),
                ]),
            new ProviderUsageDto(1, 1),
            null));
    }

    internal static ProviderServerResponse DirectFinishWithUsage(
        byte[] requestBody,
        long inputTokens,
        long outputTokens)
    {
        var finish =
            "{\"summary\":\"Synthetic direct terminal.\",\"findings\":[]}";
        return Json(new ProviderResponseDto(
            new ProviderMessageDto(
                "assistant",
                [
                    new ProviderContentDto(
                        "tool_call",
                        "finish_usage",
                        AgentToolRegistry.FinishReviewName,
                        finish,
                        null,
                        null,
                        null,
                        MessageCount(requestBody),
                        0),
                ]),
            new ProviderUsageDto(inputTokens, outputTokens),
            null));
    }

    internal static ProviderServerResponse DirectFinishAtBytes(
        byte[] requestBody,
        int targetBytes)
    {
        var finish =
            "{\"summary\":\"Synthetic direct terminal.\",\"findings\":[]}";
        var template = new ProviderResponseDto(
            new ProviderMessageDto(
                "assistant",
                [
                    new ProviderContentDto(
                        "tool_call",
                        "finish_sized",
                        AgentToolRegistry.FinishReviewName,
                        finish,
                        null,
                        null,
                        null,
                        MessageCount(requestBody),
                        0),
                ]),
            new ProviderUsageDto(1, 1),
            null,
            string.Empty);
        var baseBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            template,
            ProofJsonContext.Default.ProviderResponseDto);
        var paddingBytes = targetBytes - baseBytes.Length;
        if (paddingBytes < 0)
        {
            throw new ProviderProtocolException();
        }

        var sized = template with { Padding = new string('p', paddingBytes) };
        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            sized,
            ProofJsonContext.Default.ProviderResponseDto);
        if (body.Length != targetBytes)
        {
            throw new ProviderProtocolException();
        }

        return new ProviderServerResponse(body);
    }

    internal static ProviderServerResponse ReadCalls(
        byte[] requestBody,
        string path,
        int firstOrdinal,
        int count)
    {
        var messagePosition = MessageCount(requestBody);
        var contents = Enumerable.Range(firstOrdinal, count)
            .Select(ordinal => new ProviderContentDto(
                "tool_call",
                string.Concat("read", ordinal),
                AgentToolRegistry.ReadFileName,
                string.Concat("{\"path\":\"", path, "\"}"),
                null,
                null,
                null,
                messagePosition,
                ordinal - firstOrdinal))
            .ToArray();
        return Json(new ProviderResponseDto(
            new ProviderMessageDto("assistant", contents),
            new ProviderUsageDto(1, 1),
            null));
    }

    internal static ProviderServerResponse UnknownTool(byte[] requestBody) =>
        Json(new ProviderResponseDto(
            new ProviderMessageDto(
                "assistant",
                [
                    new ProviderContentDto(
                        "tool_call",
                        "unknown0",
                        "synthetic_unknown_tool",
                        "{}",
                        null,
                        null,
                        null,
                        MessageCount(requestBody),
                        0),
                ]),
            new ProviderUsageDto(1, 1),
            null));

    internal static ProviderServerResponse InvalidTerminal(
        byte[] requestBody)
    {
        const string finish =
            "{\"summary\":\"Synthetic invalid terminal.\",\"findings\":[{\"severity\":\"high\",\"title\":\"Invalid evidence\",\"message\":\"Synthetic.\",\"evidence\":[{\"observation_id\":\"missing\",\"path\":\"reviewed/fact.txt\",\"start_line\":1,\"end_line\":1}]}]}";
        return Json(new ProviderResponseDto(
            new ProviderMessageDto(
                "assistant",
                [
                    new ProviderContentDto(
                        "tool_call",
                        "finish_invalid",
                        AgentToolRegistry.FinishReviewName,
                        finish,
                        null,
                        null,
                        null,
                        MessageCount(requestBody),
                        0),
                ]),
            new ProviderUsageDto(1, 1),
            null));
    }

    internal static ProviderServerResponse Json(ProviderResponseDto response) =>
        new(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            response,
            ProofJsonContext.Default.ProviderResponseDto));

    private static int MessageCount(byte[] requestBody)
    {
        using var document = JsonDocument.Parse(requestBody);
        return document.RootElement
            .GetProperty("messages")
            .GetArrayLength();
    }
}
