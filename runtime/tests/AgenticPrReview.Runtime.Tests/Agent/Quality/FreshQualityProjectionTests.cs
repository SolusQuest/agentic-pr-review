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

    [Fact]
    public async Task CurrentFailedExecutionRejectsNegativeAndOverBudgetDiagnosticCounts()
    {
        var fixture = await EvaluationSelfTest.CreateAsync(ungrounded: true);
        var actual = fixture.Input.Outcome;
        Assert.False(actual.Succeeded);
        Assert.NotNull(actual.Diagnostic);
        Assert.Equal(EvaluationFailureSource.Agent, EvaluationFailure.FromAgentOutcome(actual).Source);
        var diagnostics = new[]
        {
            actual.Diagnostic with { ToolCalls = -1 },
            actual.Diagnostic with { ToolCalls = 513 },
            actual.Diagnostic with { ModelCalls = -1 },
            actual.Diagnostic with { ModelCalls = 65 },
        };
        foreach (var diagnostic in diagnostics)
        {
            var failure = EvaluationFailure.FromAgentOutcome(actual with { Diagnostic = diagnostic });
            Assert.Same(EvaluationFailure.Invalid, failure);
            var report = EvaluationScorer.Failure(fixture.Case, failure, fixture.Attempt);
            Assert.Equal(EvaluationFailureSource.Evaluator, report.FailureSource);
            Assert.Equal(EvaluationFailureKind.InvalidInput, report.FailureKind);
            Assert.Equal(ModelObservationStatus.NotEvaluated, report.ModelStatus);
            Assert.Equal(AssertionStatus.NotEvaluated, report.ScenarioStatus);
            Assert.NotEqual(EvaluationStatus.Completed, report.ExecutionStatus);
        }
    }

}
