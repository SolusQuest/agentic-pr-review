using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.Tests.Agent.Loop;

public sealed partial class AgentLoopTests
{
    [Theory]
    [InlineData(2_000_000, 0, 0)]
    [InlineData(0, 38_000_000, 0)]
    [InlineData(0, 0, 524_288)]
    public async Task EveryExhaustedPartitionStopsAnEntireSixteenToolBatch(long miss, long hit, long output)
    {
        var calls = Enumerable.Range(0, 16).Select(i => (ProjectChatContent)new ProjectToolCallContent(
            "read" + i, "read_file", "{\"path\":\"a.txt\"}")).ToArray();
        var response = new ProjectChatResponse(new("assistant", calls),
            new(miss + hit, output, new("deepseek", "deepseek-flash", "deepseek-flash", hit, miss)), 1);
        var chat = new ScriptedChatClient([response, Response(TerminalCall("never", "done"), 0, 0)]);
        var executor = new ScriptedToolExecutor();
        var outcome = await new AgentLoop(chat, executor).RunAsync(Request(), default);
        AssertFailure(outcome, AgentFailureCodes.TokenLimit);
        Assert.Single(chat.Requests);
        Assert.Empty(executor.PreflightOrder);
        Assert.Empty(executor.Order);
    }

    [Theory]
    [InlineData(2_000_000, 0, 0, true)]
    [InlineData(2_000_001, 0, 0, false)]
    [InlineData(0, 38_000_000, 0, true)]
    [InlineData(0, 38_000_001, 0, false)]
    [InlineData(0, 0, 524_288, true)]
    [InlineData(0, 0, 524_289, false)]
    [InlineData(2_000_001, 37_999_999, 0, false)]
    public async Task CurrentIndependentBudgetTerminalBoundaries(long miss, long hit, long output, bool success)
    {
        var response = Response(TerminalCall("finish", "done"), miss + hit, output) with
        {
            Usage = new(miss + hit, output, new("deepseek", "deepseek-flash", "deepseek-flash", hit, miss)),
        };
        var chat = new ScriptedChatClient([response]);
        var outcome = await new AgentLoop(chat, new ScriptedToolExecutor()).RunAsync(Request(), default);
        Assert.Equal(success, outcome.Succeeded);
        if (!success) AssertFailure(outcome, AgentFailureCodes.TokenLimit);
        Assert.Single(chat.Requests);
        Assert.Equal(hit, outcome.Accounting!.CacheHitTokens);
        Assert.Equal(miss, outcome.Accounting.CacheMissTokens);
    }

    [Theory]
    [InlineData("read_file", "{\"path\":\"a.txt\"}")]
    [InlineData("read_file", "{bad")]
    [InlineData("finish_review", "{bad")]
    public async Task ExhaustedCurrentBudgetCannotPreflightRecoverOrAcceptInvalidTerminal(string name, string arguments)
    {
        var chat = new ScriptedChatClient([
            Response(new("boundary", name, arguments), 2_000_000, 0),
            Response(TerminalCall("never", "done"), 0, 0),
        ]);
        var executor = new ScriptedToolExecutor();
        var outcome = await new AgentLoop(chat, executor).RunAsync(Request(), default);
        AssertFailure(outcome, AgentFailureCodes.TokenLimit);
        Assert.Single(chat.Requests);
        Assert.Empty(executor.PreflightOrder);
        Assert.Empty(executor.Order);
        Assert.Empty(outcome.Events.OfType<AgentRecoveryToolCallEvent>());
    }

    [Fact]
    public async Task CurrentCanCrossAllThreeOldCapsInMultipleTurns()
    {
        var chat = new ScriptedChatClient([
            Response(new("read", "read_file", "{\"path\":\"a.txt\"}"), 300_000, 40_000),
            Response(TerminalCall("finish", "done"), 1, 1),
        ]);
        var outcome = await new AgentLoop(chat, new ScriptedToolExecutor()).RunAsync(Request(), default);
        Assert.True(outcome.Succeeded);
        Assert.Equal(300_001, outcome.Accounting!.InputTokens);
        Assert.Equal(40_001, outcome.Accounting.OutputTokens);
        Assert.Equal(2, chat.Requests.Count);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(100L, null)]
    [InlineData(null, 0L)]
    [InlineData(100L, 1L)]
    public async Task UnknownPartitionDebitsFullInputWithoutManufacturingMisses(long? hit, long? miss)
    {
        var budget = new ReviewTokenBudget(110, 1000, 100);
        var run = Request();
        var authority = new AgentLimitAuthority(run.StablePlan.AdapterId, AgentLimitProfile.Current, budget);
        run = run with { StablePlan = run.StablePlan with { LimitsSha256 = AgentCanonical.LimitsSha256(AgentLimitProfile.Current, budget) } };
        var calls = 0;
        var client = new CallbackChatClient((request, ordinal) =>
        {
            calls++;
            Assert.True(request.Accounting!.TryBeginDispatch());
            request.Accounting.RecordUsage(ordinal == 1
                ? ProviderUsageObservation.Create(100, 0, 90, 10)
                : ProviderUsageObservation.Create(100, 0, hit, miss));
            return Task.FromResult(Response(new("read" + ordinal, "read_file", "{\"path\":\"a.txt\"}"), 100, 0));
        });
        var executor = new ScriptedToolExecutor();
        var outcome = await new AgentLoop(client, executor, limitAuthority: authority).RunAsync(run, default);
        AssertFailure(outcome, AgentFailureCodes.TokenLimit);
        Assert.Equal(2, calls);
        Assert.Single(executor.Order);
        Assert.Single(executor.PreflightOrder);
        Assert.Equal(1, outcome.Accounting!.UnknownCachePartitionAttempts);
        Assert.Equal(10, outcome.Accounting.CacheMissTokens);
        Assert.Equal(AccountingCompleteness.Partial, outcome.Accounting.UsageCompleteness);
    }

    [Fact]
    public async Task VeryLargeValidatedInputCannotWrapStoppingDebit()
    {
        var response = Response(TerminalCall("finish", "done"), long.MaxValue, 0);
        var outcome = await new AgentLoop(new ScriptedChatClient([response]), new ScriptedToolExecutor())
            .RunAsync(Request(), default);
        AssertFailure(outcome, AgentFailureCodes.TokenLimit);
        Assert.Equal(long.MaxValue, outcome.Accounting!.InputTokens);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TypedCallUsesRemainingOutputAndNeverSendsAfterExactExhaustion(bool terminal)
    {
        var run = Request();
        var authority = DeepSeekAdapterContext.LimitAuthorityFor(DeepSeekRequestProfile.Current);
        run = run with { StablePlan = run.StablePlan with
        {
            ProviderId = DeepSeekAdapterContext.Provider, ModelId = DeepSeekAdapterContext.Model,
            AdapterId = authority.AdapterId,
        } };
        var transport = new BudgetScriptClient(terminal);
        var executor = new ScriptedToolExecutor();
        var outcome = await new AgentLoop(transport, executor, limitAuthority: authority).RunAsync(run, default);
        Assert.Equal(terminal, outcome.Succeeded);
        if (!terminal) AssertFailure(outcome, AgentFailureCodes.TokenLimit);
        Assert.Equal(Enumerable.Repeat(65_536, 8).Concat([7123, 1]), transport.Allowances);
        Assert.Equal(9, executor.Order.Count);
        Assert.Equal(524_288, outcome.Accounting!.OutputTokens);
    }

    private sealed class BudgetScriptClient(bool terminal) : IProjectChatClient
    {
        internal List<int> Allowances { get; } = [];
        public Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.True(request.Accounting!.TryBeginDispatch());
            Allowances.Add(request.MaxOutputTokens!.Value);
            var call = Allowances.Count;
            var output = call < 8 ? 65_536 : call == 8 ? 58_413 : call == 9 ? 7122 : 1;
            request.Accounting.RecordUsage(ProviderUsageObservation.Create(1, output, 0, 1, 1 + output));
            var finish = call == 10 && terminal;
            return Task.FromResult(Response(new("typed" + call, finish ? "finish_review" : "read_file",
                finish ? "{\"summary\":\"done\",\"findings\":[]}" : "{\"path\":\"a.txt\"}"), 1, output));
        }
        public void Dispose() { }
    }
}
