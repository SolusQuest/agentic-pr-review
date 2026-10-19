using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

public sealed class FreshQualityProjectionTests
{
    [Fact]
    public async Task FreshProjectionRejectsResumedInputsAndDetachedExecutionEvents()
    {
        var fixture = await EvaluationSelfTest.CreateAsync();
        Assert.NotNull(fixture.Admit());
        var input = fixture.Input;
        var events = input.Outcome.Events;
        var tool = events.OfType<AgentToolResultEvent>().Single();
        var call = events.OfType<AgentToolCallEvent>().First();
        var mutations = new[]
        {
            input with { CurrentReviewContextIndex = -1 },
            input with { Run = input.Run with { StablePlan = input.Run.StablePlan with { PriorSessionSha256 = new string('a', 64) } } },
            input with { Run = input.Run with { InitialMessages = [..input.Run.InitialMessages, new("user", [new AgenticPrReview.Runtime.Agent.Chat.ProjectTextContent("Historical context")])] } },
            input with { Outcome = input.Outcome with { ReviewedIdentity = input.Run.ReviewedIdentity with { HeadSha = new string('f', 40) } } },
            input with { Outcome = input.Outcome with { Events = events.Remove(tool) } },
            input with { Outcome = input.Outcome with { Events = events.Replace(call, call with { ArgumentsSha256 = new string('f', 64) }) } },
            input with { Outcome = input.Outcome with { Events = events.Replace(tool, tool with { ResultSha256 = new string('f', 64) }) } },
            input with { Outcome = input.Outcome with { Events = events.Add(new AgentTerminalEvent(input.Outcome.Review!.TerminalSha256)) } },
            input with { Outcome = input.Outcome with { Events = events.SetItem(0, new AgentPlanEvent(new string('f', 64))) } },
        };
        foreach (var invalid in mutations) Assert.Null(EvaluationSubject.Admit(invalid, fixture.Run));
    }

}
