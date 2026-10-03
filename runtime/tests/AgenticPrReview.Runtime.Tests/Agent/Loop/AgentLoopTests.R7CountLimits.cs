using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Tests.Agent.Loop;

public sealed partial class AgentLoopTests
{
    [Fact]
    public async Task LowTokenTerminalCanFinishOnLogicalCall64()
    {
        var responses = Enumerable.Range(0, 63)
            .Select(index => CountBatch(index, 1))
            .Append(Response(TerminalCall("finish", "done"), 1, 1));
        var chat = new ScriptedChatClient(responses);
        var executor = new ScriptedToolExecutor(yieldDuringExecution: true);
        var outcome = await new AgentLoop(chat, executor).RunAsync(Request(), default);

        Assert.True(outcome.Succeeded, outcome.Diagnostic?.Code);
        Assert.Equal(64, chat.Requests.Count);
        Assert.Equal(64, outcome.Accounting!.ModelCalls);
        Assert.Equal(63, executor.Order.Count);
        Assert.Equal(1, executor.MaximumConcurrency);
        Assert.Equal(64, outcome.Events.OfType<AgentToolCallEvent>().Count());
        Assert.Single(outcome.Events.OfType<AgentTerminalEvent>());
    }

    [Theory]
    [InlineData(511, true)]
    [InlineData(512, false)]
    public async Task OrdinaryExecutionAndTerminalShareThe512Budget(int members, bool completes)
    {
        var chat = new ScriptedChatClient(CountBatches(members)
            .Append(Response(TerminalCall("finish", "done"), 1, 1)));
        var executor = new ScriptedToolExecutor();
        var outcome = await new AgentLoop(chat, executor).RunAsync(Request(), default);

        Assert.Equal(completes, outcome.Succeeded);
        if (!completes) AssertFailure(outcome, AgentFailureCodes.ToolLimit);
        Assert.Equal(33, outcome.Accounting!.ModelCalls);
        Assert.Equal(members, executor.Order.Count);
        Assert.Equal(512, outcome.Events.OfType<AgentToolCallEvent>().Count());
        Assert.Equal(members, outcome.Events.OfType<AgentToolResultEvent>().Count());
        Assert.Equal(completes ? 1 : 0, outcome.Events.OfType<AgentTerminalEvent>().Count());
        Assert.Empty(outcome.Events.OfType<AgentRecoveryToolCallEvent>());
        if (!completes) Assert.Equal(512, outcome.Diagnostic!.ToolCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WholeBatchAboveRemainingCountCannotPreflightRecoverOrDispatch(
        bool recoveredPrefix, bool invalidLastArgument)
    {
        var denied = CountBatch(504, 9, invalidLastArgument);
        var chat = new ScriptedChatClient(CountBatches(504, recoveredPrefix).Append(denied));
        var executor = new ScriptedToolExecutor();
        var outcome = await new AgentLoop(chat, executor).RunAsync(Request(), default);

        AssertFailure(outcome, AgentFailureCodes.ToolLimit);
        Assert.Equal(33, chat.Requests.Count);
        Assert.Equal(33, outcome.Accounting!.ModelCalls);
        Assert.Equal(recoveredPrefix ? 0 : 504, outcome.Diagnostic!.ToolCalls);
        Assert.Equal(recoveredPrefix ? 0 : 504, executor.PreflightOrder.Count);
        Assert.Equal(recoveredPrefix ? 0 : 504, executor.Order.Count);
        Assert.Equal(recoveredPrefix ? 504 : 0,
            outcome.Events.OfType<AgentRecoveryToolCallEvent>().Count());
        Assert.DoesNotContain(outcome.Events.OfType<AgentToolCallEvent>(),
            call => call.CallId == "budget-504");
        Assert.DoesNotContain(outcome.Events.OfType<AgentRecoveryToolCallEvent>(),
            call => call.CallId == "budget-504");
    }

    [Theory]
    [InlineData("arguments", 511, true)]
    [InlineData("arguments", 512, false)]
    [InlineData("preflight", 511, true)]
    [InlineData("preflight", 512, false)]
    public async Task RecoveryChargesEveryMemberAndTerminalSharesThe512Budget(
        string recovery, int members, bool completes)
    {
        var responses = CountBatches(members, recovery == "arguments",
                arguments: recovery == "preflight" ? "{\"after\":\"absent\"}" : "{}")
            .Append(Response(TerminalCall("finish", "done"), 1, 1));
        var chat = new ScriptedChatClient(responses);
        var executor = new ScriptedToolExecutor(preflight: recovery == "preflight"
            ? _ => AgentFailureCodes.ToolCursorInvalid : null);
        var outcome = await new AgentLoop(chat, executor).RunAsync(Request(), default);

        Assert.Equal(completes, outcome.Succeeded);
        if (!completes) AssertFailure(outcome, AgentFailureCodes.ToolLimit);
        Assert.Equal(33, chat.Requests.Count);
        Assert.Equal(33, outcome.Accounting!.ModelCalls);
        Assert.Empty(executor.Order);
        Assert.Equal(recovery == "preflight" ? members : 0, executor.PreflightOrder.Count);
        var recovered = outcome.Events.OfType<AgentRecoveryToolCallEvent>().ToArray();
        Assert.Equal(members, recovered.Length);
        Assert.Equal(recovery == "arguments" ? 32 : members, recovered.Count(call => call.Rejected));
        Assert.Equal(members, outcome.Events.OfType<AgentToolErrorEvent>().Count());
        Assert.Equal(completes ? 1 : 0, outcome.Events.OfType<AgentToolCallEvent>().Count());
        Assert.Empty(outcome.Events.OfType<AgentToolResultEvent>());
        if (!completes) Assert.Equal(0, outcome.Diagnostic!.ToolCalls);
    }

    [Fact]
    public async Task OrdinaryRecoveryAndSkippedSiblingsConsumeOneSharedBudget()
    {
        var responses = new[] { CountBatch(0, 16) }
            .Concat(CountBatches(480, true, 16))
            .Append(CountBatch(496, 15))
            .Append(Response(TerminalCall("finish", "done"), 1, 1));
        var chat = new ScriptedChatClient(responses);
        var executor = new ScriptedToolExecutor();
        var outcome = await new AgentLoop(chat, executor).RunAsync(Request(), default);

        Assert.True(outcome.Succeeded, outcome.Diagnostic?.Code);
        Assert.Equal(33, outcome.Accounting!.ModelCalls);
        Assert.Equal(31, executor.Order.Count);
        Assert.Equal(32, outcome.Events.OfType<AgentToolCallEvent>().Count());
        var recovered = outcome.Events.OfType<AgentRecoveryToolCallEvent>().ToArray();
        Assert.Equal(480, recovered.Length);
        Assert.Equal(30, recovered.Count(call => call.Rejected));
        Assert.Equal(450, recovered.Count(call => !call.Rejected));
    }

    [Theory]
    [InlineData("{bad", false)]
    [InlineData("{\"summary\":\" \",\"findings\":[]}", true)]
    public async Task TerminalFailureChargeDependsOnPreparationAtSlot512(
        string arguments, bool prepared)
    {
        var chat = new ScriptedChatClient(CountBatches(511).Append(Response(
            new("finish", "finish_review", arguments), 1, 1)));
        var executor = new ScriptedToolExecutor();
        var outcome = await new AgentLoop(chat, executor).RunAsync(Request(), default);

        AssertFailure(outcome, AgentFailureCodes.TerminalInvalid);
        Assert.Equal(prepared ? 512 : 511, outcome.Diagnostic!.ToolCalls);
        Assert.Equal(prepared ? "terminal_bounds_invalid" : "arguments_invalid",
            outcome.Diagnostic.TerminalReason);
        Assert.Equal(511, executor.Order.Count);
        Assert.Equal(prepared ? 512 : 511, outcome.Events.OfType<AgentToolCallEvent>().Count());
        Assert.Equal(33, outcome.Accounting!.ModelCalls);
        Assert.Empty(outcome.Events.OfType<AgentTerminalEvent>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrThrowingExecutionStillChargesSlot512(bool throws)
    {
        var chat = new ScriptedChatClient(CountBatches(511).Append(CountBatch(511, 1)));
        var executor = new ScriptedToolExecutor(call => call.CallId == "budget-511"
            ? throws ? throw new IOException() : AgentToolExecution.Failure(AgentFailureCodes.ToolIoFailed)
            : Success(call, new string('a', 64), "a.txt", 1));
        var outcome = await new AgentLoop(chat, executor).RunAsync(Request(), default);

        AssertFailure(outcome, AgentFailureCodes.ToolIoFailed);
        Assert.Equal(512, outcome.Diagnostic!.ToolCalls);
        Assert.Equal(33, outcome.Accounting!.ModelCalls);
        Assert.Equal(512, executor.Order.Count);
        Assert.Equal(512, outcome.Events.OfType<AgentToolCallEvent>().Count());
        Assert.Equal(511, outcome.Events.OfType<AgentToolResultEvent>().Count());
    }

    [Fact]
    public async Task FormerExactLimitsIdentityFailsClosedBeforeAnyChatInvocation()
    {
        var run = Request();
        run = run with { StablePlan = run.StablePlan with {
            LimitsSha256 = "dbe36dba580fd8a3a5256b8c6710e462a58b477d84844cc33b2daf63c54614e6",
        } };
        var chat = new ScriptedChatClient([]);
        var outcome = await new AgentLoop(chat, new ScriptedToolExecutor()).RunAsync(run, default);

        AssertFailure(outcome, AgentFailureCodes.ResponseInvalid);
        Assert.Empty(chat.Requests);
        Assert.Equal(0, outcome.Accounting!.ModelCalls);
    }

    private static IEnumerable<ProjectChatResponse> CountBatches(
        int count, bool recover = false, int offset = 0, string arguments = "{}")
    {
        for (var consumed = 0; consumed < count; consumed += 16)
            yield return CountBatch(offset + consumed, Math.Min(16, count - consumed), recover, arguments);
    }

    private static ProjectChatResponse CountBatch(
        int offset, int count, bool invalidLast = false, string arguments = "{}") =>
        new(new("assistant", Enumerable.Range(0, count).Select(index =>
            (ProjectChatContent)new ProjectToolCallContent($"budget-{offset + index}", "list_files",
                invalidLast && index == count - 1 ? "{bad" : arguments)).ToArray()), new(1, 1), 1);
}
