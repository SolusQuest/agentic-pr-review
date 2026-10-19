using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed class CurrentHostScriptedChatClientTests
{
    [Theory]
    [InlineData(false, 6)]
    [InlineData(true, 3)]
    public async Task CurrentWorkPlanCompletesWithOnlyItsOwnToolTranscript(bool successor, int calls)
    {
        var client = Client(successor);
        var messages = new List<ProjectChatMessage>();
        for (var index = 0; index < calls; index++)
            Append(messages, await client.GetResponseAsync(new(messages.ToArray(), [], null), default));
        Assert.Equal(calls * 2, messages.Count);
        Assert.Equal("finish_review", messages[^2].Contents.OfType<ProjectToolCallContent>().Single().Name);
        Assert.All(messages.SelectMany(message => message.Contents.OfType<ProjectToolCallContent>()), call =>
            Assert.Equal(successor, call.CallId.StartsWith("e2p-successor-", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false, "reordered")]
    [InlineData(true, "reordered")]
    [InlineData(false, "malformed-relation")]
    [InlineData(true, "malformed-relation")]
    public async Task CurrentWorkRejectsIndependentTranscriptFaults(bool successor, string mutation)
    {
        var client = Client(successor);
        var messages = new List<ProjectChatMessage>();
        for (var index = 0; index < 2; index++)
            Append(messages, await client.GetResponseAsync(new(messages.ToArray(), [], null), default));
        if (mutation == "reordered")
        {
            (messages[0], messages[2]) = (messages[2], messages[0]);
            (messages[1], messages[3]) = (messages[3], messages[1]);
        }
        else messages[1] = new("tool", [new ProjectToolResultContent("unrelated-call", "{}")]);
        await Assert.ThrowsAsync<ProjectChatNormalizationException>(() =>
            client.GetResponseAsync(new(messages.ToArray(), [], null), default));
    }

    [Fact]
    public async Task SuccessorWorkCannotStartFromAnotherWorkTranscript()
    {
        var fresh = Client(false);
        var messages = new List<ProjectChatMessage>();
        Append(messages, await fresh.GetResponseAsync(new([], [], null), default));
        await Assert.ThrowsAsync<ProjectChatNormalizationException>(() =>
            Client(true).GetResponseAsync(new(messages.ToArray(), [], null), default));
    }

    private static CurrentHostScriptedChatClient Client(bool successor) => new(
        new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model,
            DeepSeekAdapterContext.Adapter, "current-host-unit-proof"), successor);

    private static void Append(List<ProjectChatMessage> messages, ProjectChatResponse response)
    {
        var call = response.Message.Contents.OfType<ProjectToolCallContent>().Single();
        var canonical = call with { ArgumentsJson = call.Name switch {
            "list_changed_files" => "{\"after\":null}",
            "list_files" => "{\"prefix\":null,\"after\":null}",
            _ => call.ArgumentsJson } };
        messages.Add(response.Message with { Contents = [response.Message.Contents[0], canonical] });
        messages.Add(new("tool", [new ProjectToolResultContent(call.CallId, "{\"test_observation\":true}")]));
    }
}
