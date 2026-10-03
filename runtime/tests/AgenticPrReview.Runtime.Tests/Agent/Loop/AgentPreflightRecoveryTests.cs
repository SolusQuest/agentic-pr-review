using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Tests.Agent.Loop;

public sealed partial class AgentLoopTests
{
    [Theory]
    [InlineData(1, AgentFailureCodes.ModelLimit, 64)]
    [InlineData(16, AgentFailureCodes.ToolLimit, 32)]
    public async Task RepeatedDenialsConsumeExistingModelAndToolCaps(int batchSize, string code, int admittedBatches)
    {
        var responses = Enumerable.Range(0, AgentLimits.ModelCalls).Select(turn =>
            new ProjectChatResponse(new("assistant", Enumerable.Range(0, batchSize)
                .Select(index => (ProjectChatContent)new ProjectToolCallContent($"call-{turn}-{index}",
                    "read_file", "{\"path\":\"absent\"}")).ToArray()), new(1, 1), 1));
        var executor = new ScriptedToolExecutor(preflight: _ => AgentFailureCodes.ToolPathNotTracked);
        var outcome = await new AgentLoop(new ScriptedChatClient(responses), executor).RunAsync(Request(), default);
        AssertFailure(outcome, code);
        Assert.Equal(admittedBatches * batchSize, executor.PreflightOrder.Count);
        Assert.Equal(admittedBatches * batchSize, outcome.Events.OfType<AgentRecoveryToolCallEvent>().Count());
        Assert.Empty(executor.Order);
    }

    [Theory]
    [InlineData(AgentFailureCodes.ToolPathNotTracked)]
    [InlineData(AgentFailureCodes.ToolCursorInvalid)]
    public async Task SameCodeAfterExecutionDoesNotBecomePreflightRecovery(string code)
    {
        var executor = new ScriptedToolExecutor(_ => AgentToolExecution.Failure(code));
        var outcome = await new AgentLoop(new ScriptedChatClient([
            Response(new("read", "read_file", "{\"path\":\"a.txt\"}"), 1, 1),
        ]), executor).RunAsync(Request(), default);
        AssertFailure(outcome, code);
        Assert.Equal(1, outcome.Diagnostic!.ToolCalls);
        Assert.Single(executor.Order);
        Assert.Empty(outcome.Events.OfType<AgentRecoveryToolCallEvent>());
    }

    [Theory]
    [InlineData("read_file", "{\"path\":\"PRIVATE_CANARY\"}", "tool_path_not_tracked")]
    [InlineData("search_text", "{\"query\":\"x\",\"path\":\"PRIVATE_CANARY\"}", "tool_path_not_tracked")]
    [InlineData("read_diff", "{\"path\":\"PRIVATE_CANARY\"}", "tool_path_not_tracked")]
    [InlineData("list_files", "{\"after\":\"PRIVATE_CANARY\"}", "tool_cursor_invalid")]
    [InlineData("list_changed_files", "{\"after\":\"PRIVATE_CANARY\"}", "tool_cursor_invalid")]
    public async Task PreflightDenialRecoversWithOneClassificationAndNoSiblingDispatch(string tool, string arguments, string code)
    {
        var denied = new ProjectToolCallContent("denied", tool, arguments);
        var chat = new ScriptedChatClient([
            new(new("assistant", [new ProjectToolCallContent("sibling", "list_files", "{}"), denied]), new(1, 1), 1),
            Response(TerminalCall("finish", "done"), 1, 1),
        ]);
        var executor = new ScriptedToolExecutor(preflight: call => call.CallId == "denied" ? code : null);
        var outcome = await new AgentLoop(chat, executor).RunAsync(Request(), default);
        Assert.True(outcome.Succeeded);
        Assert.Equal(["sibling", "denied"], executor.PreflightOrder);
        Assert.Empty(executor.Order);
        var calls = chat.Requests[1].Messages.SelectMany(message => message.Contents)
            .OfType<ProjectRecoveryToolCallContent>().ToArray();
        Assert.False(calls[0].Rejected);
        Assert.True(calls[1].Rejected);
        Assert.Equal(AgentRecoveryFeedback.RejectedArguments, calls[1].ArgumentsJson);
        var errors = chat.Requests[1].Messages.SelectMany(message => message.Contents)
            .OfType<ProjectToolErrorContent>().ToArray();
        Assert.Equal(AgentRecoveryFeedback.BatchNotExecuted, errors[0].Result);
        Assert.True(AgentRecoveryFeedback.IsRejectedError(tool, errors[1].Result));
        Assert.DoesNotContain("PRIVATE_CANARY", Encoding.UTF8.GetString(AgentRequestWriter.Write(chat.Requests[1])));
        Assert.Empty(outcome.Events.OfType<AgentToolResultEvent>());
    }

    [Theory]
    [InlineData(false, "tool_path_invalid")]
    [InlineData(true, "tool_path_invalid")]
    [InlineData(false, "tool_path_unsafe")]
    [InlineData(true, "tool_path_unsafe")]
    [InlineData(false, "tool_io_failed")]
    [InlineData(true, "tool_io_failed")]
    [InlineData(false, "unregistered_failure")]
    [InlineData(true, "unregistered_failure")]
    [InlineData(false, "throw")]
    [InlineData(true, "throw")]
    public async Task FatalPreflightDominatesDenialInEitherOrder(bool fatalFirst, string fatal)
    {
        ProjectToolCallContent[] members = [new("denied", "read_file", "{\"path\":\"absent\"}"),
            new("fatal", "read_file", "{\"path\":\"other\"}")];
        if (fatalFirst) Array.Reverse(members);
        var executor = new ScriptedToolExecutor(preflight: call => call.CallId == "denied"
            ? AgentFailureCodes.ToolPathNotTracked : fatal == "throw" ? throw new IOException() : fatal);
        var outcome = await new AgentLoop(new ScriptedChatClient([new(new("assistant", members), new(1, 1), 1)]),
            executor).RunAsync(Request(), default);
        AssertFailure(outcome, fatal is "throw" or "unregistered_failure" ? AgentFailureCodes.ToolIoFailed : fatal);
        Assert.Equal(fatalFirst ? ["fatal"] : new[] { "denied", "fatal" }, executor.PreflightOrder);
        Assert.Empty(executor.Order);
        Assert.Empty(outcome.Events.OfType<AgentRecoveryToolCallEvent>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task PreflightRecoveryChargesUsageOnceAndHonorsTokenBound(int excess)
    {
        var chat = new ScriptedChatClient([
            Response(new("denied", "read_file", "{\"path\":\"absent\"}"), AgentLimits.InputTokens - 1, 0),
            Response(TerminalCall("finish", "done"), 1 + excess, 0),
        ]);
        var outcome = await new AgentLoop(chat, new ScriptedToolExecutor(preflight: _ => AgentFailureCodes.ToolPathNotTracked))
            .RunAsync(Request(), default);
        Assert.Equal(excess == 0, outcome.Succeeded);
        if (excess != 0) AssertFailure(outcome, AgentFailureCodes.TokenLimit);
        Assert.Single(outcome.Events.OfType<AgentRecoveryToolCallEvent>());
    }

    [Fact]
    public async Task ParserRejectionPrecedesAllPreflightAndMultipleDenialsRemainBounded()
    {
        var chat = new ScriptedChatClient([
            new(new("assistant", [new ProjectToolCallContent("one", "read_file", "{\"path\":\"absent\"}"),
                new ProjectToolCallContent("two", "search_text", "{bad")]), new(1, 1), 1),
            Response(TerminalCall("finish", "done"), 1, 1),
        ]);
        var executor = new ScriptedToolExecutor(preflight: _ => throw new InvalidOperationException());
        var outcome = await new AgentLoop(chat, executor).RunAsync(Request(), default);
        Assert.True(outcome.Succeeded);
        Assert.Empty(executor.PreflightOrder);
        Assert.Equal(AgentRecoveryFeedback.ArgumentsInvalid,
            Encoding.UTF8.GetString(outcome.Events.OfType<AgentToolErrorEvent>().Last().CanonicalResult.AsSpan()));
    }

    [Fact]
    public async Task MultiplePreflightDenialsRecoverTogetherAndCancellationCommitsNothing()
    {
        foreach (var cancel in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var chat = new ScriptedChatClient([
                new(new("assistant", [new ProjectToolCallContent("one", "read_file", "{\"path\":\"a\"}"),
                    new ProjectToolCallContent("two", "read_diff", "{\"path\":\"b\"}")]), new(1, 1), 1),
                Response(TerminalCall("finish", "done"), 1, 1),
            ]);
            var executor = new ScriptedToolExecutor(preflight: call =>
            {
                if (cancel && call.CallId == "two") cancellation.Cancel();
                return AgentFailureCodes.ToolPathNotTracked;
            });
            var outcome = await new AgentLoop(chat, executor).RunAsync(Request(), cancellation.Token);
            Assert.Equal(!cancel, outcome.Succeeded);
            Assert.Equal(cancel ? 0 : 2, outcome.Events.OfType<AgentRecoveryToolCallEvent>().Count());
            Assert.Empty(executor.Order);
            if (cancel) AssertFailure(outcome, AgentFailureCodes.Cancelled);
        }
    }
}
